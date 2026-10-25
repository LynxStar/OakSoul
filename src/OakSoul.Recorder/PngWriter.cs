using System.Buffers.Binary;
using System.IO.Compression;

namespace OakSoul.Recorder;

/// <summary>Minimal dependency-free PNG encoder: 8-bit RGB, no filtering, zlib via System.IO.Compression.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes a tightly packed BGRA buffer as an opaque RGB PNG.</summary>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        using var output = new MemoryStream(width * height / 2);
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: truecolour
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter method
        ihdr[12] = 0;  // interlace
        WriteChunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream(width * height / 2))
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                for (var y = 0; y < height; y++)
                {
                    row[0] = 0; // filter: none
                    var src = bgra.Slice(y * width * 4, width * 4);
                    for (int x = 0, s = 0, d = 1; x < width; x++, s += 4, d += 3)
                    {
                        row[d] = src[s + 2];     // R
                        row[d + 1] = src[s + 1]; // G
                        row[d + 2] = src[s];     // B
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        header[4] = (byte)type[0]; header[5] = (byte)type[1]; header[6] = (byte)type[2]; header[7] = (byte)type[3];
        output.Write(header);
        output.Write(data);

        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, header[4..]);
        crc = UpdateCrc(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
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
