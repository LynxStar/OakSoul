using System.Text;

namespace OakSoul.Wz;

/// <summary>
/// Little-endian reader with the WZ primitives: compressed integers and masked strings.
/// Used both for the archive file (directories) and for an in-memory copy of one image.
/// </summary>
/// <remarks>
/// String encryption: every WZ string is XORed with a running mask (0xAA + i for 8-bit strings,
/// 0xAAAA + i for UTF-16 strings). Older regional clients additionally XOR with an AES-derived key
/// stream; this client uses the zero-IV variant whose key stream is all zeros, so the mask alone
/// decodes the text. If names ever decode to garbage, that assumption is the first thing to check.
/// </remarks>
internal sealed class WzBinaryReader(Stream stream) : BinaryReader(stream, Encoding.ASCII, leaveOpen: true)
{
    public long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    public long Length => BaseStream.Length;

    /// <summary>Compressed int: an sbyte, or the marker -128 followed by a full int32.</summary>
    public int ReadCompressedInt()
    {
        sbyte b = ReadSByte();
        return b == sbyte.MinValue ? ReadInt32() : b;
    }

    /// <summary>Compressed long: an sbyte, or the marker -128 followed by a full int64.</summary>
    public long ReadCompressedLong()
    {
        sbyte b = ReadSByte();
        return b == sbyte.MinValue ? ReadInt64() : b;
    }

    /// <summary>
    /// A length-prefixed, masked string. Positive length: UTF-16 (127 means an int32 length follows).
    /// Negative length: 8-bit, length is -len (-128 means an int32 length follows). Zero: empty.
    /// </summary>
    public string ReadWzString()
    {
        long start = Position;
        sbyte len = ReadSByte();
        if (len == 0)
            return string.Empty;

        if (len > 0)
        {
            int count = len == sbyte.MaxValue ? ReadInt32() : len;
            CheckLength(count, 2, start);
            var chars = new char[count];
            ushort mask = 0xAAAA;
            for (int i = 0; i < count; i++)
            {
                chars[i] = (char)(ReadUInt16() ^ mask);
                mask++;
            }
            return new string(chars);
        }
        else
        {
            int count = len == sbyte.MinValue ? ReadInt32() : -len;
            CheckLength(count, 1, start);
            var chars = new char[count];
            byte mask = 0xAA;
            for (int i = 0; i < count; i++)
            {
                chars[i] = (char)(byte)(ReadByte() ^ mask);
                mask++;
            }
            return new string(chars);
        }
    }

    /// <summary>Reads a WZ string at an absolute position and returns to where we were.</summary>
    public string ReadWzStringAt(long position)
    {
        long saved = Position;
        try
        {
            if (position < 0 || position >= Length)
                throw new WzFormatException($"String offset {position} is outside the stream (length {Length}).");
            Position = position;
            return ReadWzString();
        }
        finally
        {
            Position = saved;
        }
    }

    private void CheckLength(int count, int unitSize, long start)
    {
        if (count < 0 || (long)count * unitSize > Length - Position)
            throw new WzFormatException($"Implausible string length {count} at position {start}.");
    }
}
