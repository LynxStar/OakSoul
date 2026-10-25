using System.Buffers.Binary;
using System.IO.Compression;

namespace OakSoul.Wz;

/// <summary>
/// A canvas bitmap: its size and pixel format, plus where its pixel block lives in the archive. The pixels are
/// read and decoded only when <see cref="Decode"/> is called, straight from the (read-only, shared) archive file.
/// </summary>
/// <remarks>
/// The pixel block is <see cref="DataLength"/> bytes: a single 0 byte, then a zlib stream (header 0x78 0x01,
/// 0x78 0x5E, 0x78 0x9C or 0x78 0xDA). Older clients with an AES key instead store a list of XOR-keyed chunks;
/// that layout is detected and rejected with a <see cref="WzFormatException"/>, since this client has no key.
/// The inflated pixels are interpreted by <see cref="PixelFormat"/> (format + format2):
/// 1 BGRA4444, 2 BGRA8888, 3 BGRA4444 at 1/4 scale (one pixel per 4x4 block), 257 BGRA5551, 513 BGR565,
/// 517 BGR565 at 1/16 scale (one pixel per 16x16 block), 1026 DXT3, 2050 DXT5.
/// Format 4098 (BC7) occurs in a few large skill screen effects and is rejected as unsupported.
/// </remarks>
public sealed class WzCanvas
{
    internal WzCanvas(WzImage image, WzNode node, int width, int height, int format, int format2, long dataOffset, int dataLength)
    {
        Image = image;
        Node = node;
        Width = width;
        Height = height;
        Format = format;
        Format2 = format2;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }

    public WzImage Image { get; }
    public WzNode Node { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The stored format code (compressed int).</summary>
    public int Format { get; }

    /// <summary>The stored format2 byte (added to <see cref="Format"/> to get the pixel format; usually 0).</summary>
    public int Format2 { get; }

    /// <summary>The effective pixel format, <see cref="Format"/> + <see cref="Format2"/>.</summary>
    public int PixelFormat => Format + Format2;

    /// <summary>Absolute file offset (archive or pack) of the pixel block (the 0 byte that precedes the compressed stream).</summary>
    public long DataOffset { get; }

    /// <summary>Length of the pixel block in bytes, including the leading 0 byte.</summary>
    public int DataLength { get; }

    /// <summary>"archive-relative image path/node path", for messages.</summary>
    public string FullPath => Image.Path + "/" + Node.Path;

    /// <summary>The raw pixel block as stored in the archive.</summary>
    public byte[] ReadRaw() => Image.ReadBytes(DataOffset, DataLength);

    /// <summary>True when the stored stream is plain zlib (the only layout <see cref="Decode"/> accepts).</summary>
    public bool IsZlib()
    {
        if (DataLength < 3)
            return false;
        var head = Image.ReadBytes(DataOffset, 3);
        return IsZlibHeader(head.AsSpan(1));
    }

    /// <summary>
    /// Reads, inflates and converts the bitmap to tightly packed BGRA8888 (Width * Height * 4 bytes, rows top to
    /// bottom) with straight alpha. Thread-safe.
    /// </summary>
    /// <exception cref="WzFormatException">The data is not zlib, is truncated, or the pixel format is unsupported.</exception>
    public byte[] Decode()
    {
        if (Width == 0 || Height == 0)
            return [];
        int pf = PixelFormat;
        int expected = RawSize(pf, Width, Height)
            ?? throw Fail(pf == 4098
                ? "unsupported pixel format 4098 (BC7 block compression, 16 bytes per 4x4 block; no decoder implemented)"
                : $"unsupported pixel format {pf} (format {Format} + format2 {Format2})");

        byte[] block = ReadRaw();
        if (block.Length < 3 || !IsZlibHeader(block.AsSpan(1)))
            throw Fail(block.Length < 3
                ? $"pixel block is only {block.Length} bytes"
                : $"pixel data is not a zlib stream (bytes {block[0]:X2} {block[1]:X2} {block[2]:X2}); the keyed chunk layout is not supported");

        byte[] raw = Inflate(block, expected);
        if (pf == 517 && raw.Length < expected)
        {
            // Width/height not multiples of 16: some encoders round the block grid down instead of up.
            int floor = (Width / 16) * (Height / 16) * 2;
            if (raw.Length >= floor && floor > 0)
                return DecodeScaled565(raw, Width / 16, Height / 16, 16);
        }
        if (raw.Length < expected)
            throw Fail($"inflated to {raw.Length} bytes, expected {expected} for {Width}x{Height} format {pf}");

        return pf switch
        {
            1 => DecodeBgra4444(raw, Width * Height),
            2 => raw.Length == Width * Height * 4 ? raw : raw[..(Width * Height * 4)],
            3 => DecodeScaled4444(raw),
            257 => DecodeBgra5551(raw, Width * Height),
            513 => DecodeBgr565(raw, Width * Height),
            517 => DecodeScaled565(raw, (Width + 15) / 16, (Height + 15) / 16, 16),
            1026 => Dxt.Decode(raw, Width, Height, Dxt.Kind.Dxt3),
            2050 => Dxt.Decode(raw, Width, Height, Dxt.Kind.Dxt5),
            _ => throw Fail($"unsupported pixel format {pf}"),
        };
    }

    /// <summary>Inflated byte count for a pixel format, or null if the format is not supported.</summary>
    public static int? RawSize(int pixelFormat, int width, int height) => pixelFormat switch
    {
        1 or 257 or 513 => width * height * 2,
        2 => width * height * 4,
        3 => ((width + 3) / 4) * ((height + 3) / 4) * 2,
        517 => ((width + 15) / 16) * ((height + 15) / 16) * 2,
        1026 or 2050 => ((width + 3) / 4) * ((height + 3) / 4) * 16,
        _ => null,
    };

    public override string ToString() => $"{FullPath}: {Width}x{Height} format {PixelFormat}, {DataLength} bytes at 0x{DataOffset:X}";

    private static bool IsZlibHeader(ReadOnlySpan<byte> s) =>
        s.Length >= 2 && s[0] == 0x78 && s[1] is 0x01 or 0x5E or 0x9C or 0xDA;

    private byte[] Inflate(byte[] block, int expected)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(block, 1, block.Length - 1, writable: false), CompressionMode.Decompress);
            var buffer = new byte[expected];
            int total = 0, n;
            while (total < expected && (n = z.Read(buffer, total, expected - total)) > 0)
                total += n;
            return total == expected ? buffer : buffer[..total];
        }
        catch (InvalidDataException ex)
        {
            throw Fail($"zlib stream is corrupt: {ex.Message}", ex);
        }
    }

    private WzFormatException Fail(string what, Exception? inner = null) =>
        new($"{Image.SourcePath}: canvas '{FullPath}' ({Width}x{Height}): {what}.", inner);

    // ---- pixel converters; output is BGRA8888 ----

    private static byte[] DecodeBgra4444(byte[] raw, int pixels)
    {
        var o = new byte[pixels * 4];
        for (int i = 0, j = 0; i < pixels; i++, j += 4)
        {
            byte lo = raw[i * 2], hi = raw[i * 2 + 1];
            o[j] = (byte)((lo & 0x0F) * 17);
            o[j + 1] = (byte)((lo >> 4) * 17);
            o[j + 2] = (byte)((hi & 0x0F) * 17);
            o[j + 3] = (byte)((hi >> 4) * 17);
        }
        return o;
    }

    private static byte[] DecodeBgra5551(byte[] raw, int pixels)
    {
        var o = new byte[pixels * 4];
        for (int i = 0, j = 0; i < pixels; i++, j += 4)
        {
            int v = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2));
            o[j] = Expand5(v & 0x1F);
            o[j + 1] = Expand5((v >> 5) & 0x1F);
            o[j + 2] = Expand5((v >> 10) & 0x1F);
            o[j + 3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
        }
        return o;
    }

    private static byte[] DecodeBgr565(byte[] raw, int pixels)
    {
        var o = new byte[pixels * 4];
        for (int i = 0, j = 0; i < pixels; i++, j += 4)
            Write565(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2)), o, j);
        return o;
    }

    /// <summary>BGR565 values, one per <paramref name="scale"/>x<paramref name="scale"/> block of the output.</summary>
    private byte[] DecodeScaled565(byte[] raw, int blocksW, int blocksH, int scale)
    {
        var o = new byte[Width * Height * 4];
        var tmp = new byte[4];
        for (int by = 0; by < blocksH; by++)
            for (int bx = 0; bx < blocksW; bx++)
            {
                Write565(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((by * blocksW + bx) * 2)), tmp, 0);
                FillBlock(o, bx * scale, by * scale, scale, tmp);
            }
        return o;
    }

    /// <summary>BGRA4444 values, one per 4x4 block of the output (format 3).</summary>
    private byte[] DecodeScaled4444(byte[] raw)
    {
        var o = new byte[Width * Height * 4];
        int bw = (Width + 3) / 4, bh = (Height + 3) / 4;
        var tmp = new byte[4];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int i = (by * bw + bx) * 2;
                byte lo = raw[i], hi = raw[i + 1];
                tmp[0] = (byte)((lo & 0x0F) * 17);
                tmp[1] = (byte)((lo >> 4) * 17);
                tmp[2] = (byte)((hi & 0x0F) * 17);
                tmp[3] = (byte)((hi >> 4) * 17);
                FillBlock(o, bx * 4, by * 4, 4, tmp);
            }
        return o;
    }

    private void FillBlock(byte[] o, int x0, int y0, int size, byte[] bgra)
    {
        int x1 = Math.Min(x0 + size, Width), y1 = Math.Min(y0 + size, Height);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                bgra.CopyTo(o, (y * Width + x) * 4);
    }

    internal static void Write565(int v, byte[] o, int j)
    {
        o[j] = Expand5(v & 0x1F);
        o[j + 1] = (byte)((((v >> 5) & 0x3F) << 2) | (((v >> 5) & 0x3F) >> 4));
        o[j + 2] = Expand5((v >> 11) & 0x1F);
        o[j + 3] = 255;
    }

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
