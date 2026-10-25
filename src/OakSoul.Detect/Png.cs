using System.Buffers.Binary;
using System.IO.Compression;

namespace OakSoul.Detect;

/// <summary>
/// Minimal dependency-free PNG codec for the detector's own cache files and contact sheets: writes 8-bit RGBA (from
/// BGRA) or 8-bit greyscale with the Sub filter, and reads back 8-bit greyscale, RGB or RGBA non-interlaced files
/// (all five filter types). The CRC code follows OakSoul.Recorder's PngWriter.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes a tightly packed BGRA buffer as an RGBA PNG, alpha kept.</summary>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height) => Encode(bgra, width, height, 4);

    /// <summary>Encodes one byte per pixel as a greyscale PNG.</summary>
    public static byte[] EncodeGrey(ReadOnlySpan<byte> grey, int width, int height) => Encode(grey, width, height, 1);

    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height) => File.WriteAllBytes(path, EncodeBgra(bgra, width, height));

    public static void WriteGrey(string path, ReadOnlySpan<byte> grey, int width, int height) => File.WriteAllBytes(path, EncodeGrey(grey, width, height));

    private static byte[] Encode(ReadOnlySpan<byte> src, int width, int height, int channels)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(channels == 4 ? 6 : 0);
        WriteChunk(output, "IHDR", ihdr);
        using (var idat = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stride = width * channels;
                var row = new byte[1 + stride];
                var raw = new byte[stride];
                for (var y = 0; y < height; y++)
                {
                    var s = src.Slice(y * stride, stride);
                    if (channels == 4)
                        for (var x = 0; x < stride; x += 4) { raw[x] = s[x + 2]; raw[x + 1] = s[x + 1]; raw[x + 2] = s[x]; raw[x + 3] = s[x + 3]; }
                    else s.CopyTo(raw);
                    row[0] = 1; // Sub filter: large flat areas compress far better than unfiltered
                    for (var i = 0; i < stride; i++) row[1 + i] = (byte)(raw[i] - (i >= channels ? raw[i - channels] : 0));
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>Decodes a PNG to tightly packed BGRA (greyscale and RGB get opaque alpha).</summary>
    public static (byte[] Bgra, int Width, int Height) ReadBgra(string path)
    {
        var (data, w, h, channels) = Decode(File.ReadAllBytes(path));
        var bgra = new byte[w * h * 4];
        for (int i = 0, o = 0; i < w * h; i++, o += 4)
        {
            var s = i * channels;
            if (channels == 1) { bgra[o] = bgra[o + 1] = bgra[o + 2] = data[s]; bgra[o + 3] = 255; }
            else { bgra[o] = data[s + 2]; bgra[o + 1] = data[s + 1]; bgra[o + 2] = data[s]; bgra[o + 3] = channels == 4 ? data[s + 3] : (byte)255; }
        }
        return (bgra, w, h);
    }

    /// <summary>Decodes a greyscale PNG to one byte per pixel.</summary>
    public static (byte[] Grey, int Width, int Height) ReadGrey(string path)
    {
        var (data, w, h, channels) = Decode(File.ReadAllBytes(path));
        if (channels != 1) throw new InvalidDataException($"{path}: expected a greyscale PNG");
        return (data, w, h);
    }

    private static (byte[] Data, int Width, int Height, int Channels) Decode(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        for (var p = 8; p + 8 <= file.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(file, p + 4, 4);
            var body = file.AsSpan(p + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0) throw new InvalidDataException("only 8-bit non-interlaced PNGs are supported");
                channels = body[9] switch { 0 => 1, 2 => 3, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {body[9]} not supported") };
            }
            else if (type == "IDAT") idat.Write(body);
            else if (type == "IEND") break;
            p += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = width * channels;
        var data = new byte[stride * height];
        var row = new byte[stride + 1];
        for (var y = 0; y < height; y++)
        {
            zlib.ReadExactly(row);
            var cur = data.AsSpan(y * stride, stride);
            var prev = y > 0 ? data.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (var i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0, b = y > 0 ? prev[i] : 0, c = y > 0 && i >= channels ? prev[i - channels] : 0;
                var x = row[i + 1];
                cur[i] = row[0] switch
                {
                    0 => x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + ((a + b) >> 1)),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"bad PNG filter {row[0]}"),
                };
            }
        }
        return (data, width, height, channels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        for (var i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
        output.Write(header);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in header[4..]) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        output.Write(crcBytes);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
