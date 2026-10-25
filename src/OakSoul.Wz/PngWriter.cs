using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OakSoul.Wz;

/// <summary>
/// Minimal dependency-free PNG encoder: 8-bit RGBA (colour type 6), non-interlaced, one IDAT chunk.
/// Input is tightly packed BGRA8888 with straight alpha, as returned by <see cref="WzCanvas.Decode"/>.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes a PNG file, creating the parent directory if needed. Overwrites an existing file.</summary>
    public static void Save(string path, int width, int height, ReadOnlySpan<byte> bgra)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Write(fs, width, height, bgra);
    }

    /// <summary>Encodes a PNG to a byte array.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> bgra)
    {
        using var ms = new MemoryStream();
        Write(ms, width, height, bgra);
        return ms.ToArray();
    }

    public static void Write(Stream output, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"PNG size must be positive (got {width}x{height}).");
        if (bgra.Length < (long)width * height * 4)
            throw new ArgumentException($"Pixel buffer has {bgra.Length} bytes; {width}x{height} BGRA needs {(long)width * height * 4}.");

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // no interlace
        WriteChunk(output, "IHDR", ihdr);

        // Scanlines: filter byte then RGBA. The Sub filter (difference from the pixel to the left) is cheap
        // and noticeably shrinks sprite data compared with no filter.
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride, dst = y * (stride + 1);
            raw[dst++] = 1; // Sub
            for (int x = 0; x < width; x++, src += 4, dst += 4)
            {
                byte r = bgra[src + 2], g = bgra[src + 1], b = bgra[src], a = bgra[src + 3];
                if (x == 0)
                {
                    raw[dst] = r; raw[dst + 1] = g; raw[dst + 2] = b; raw[dst + 3] = a;
                }
                else
                {
                    raw[dst] = (byte)(r - bgra[src - 2]);
                    raw[dst + 1] = (byte)(g - bgra[src - 3]);
                    raw[dst + 2] = (byte)(b - bgra[src - 4]);
                    raw[dst + 3] = (byte)(a - bgra[src - 1]);
                }
            }
        }

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(data);

        uint crc = Crc(0xFFFFFFFFu, header[4..]);
        crc = Crc(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        output.Write(tail);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
