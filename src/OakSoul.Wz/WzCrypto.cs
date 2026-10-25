using System.Globalization;
using System.Numerics;

namespace OakSoul.Wz;

/// <summary>
/// The arithmetic behind WZ "encryption": the version hash, the version check byte and the
/// directory offset scrambling. All of it is deliberately unchecked 32-bit arithmetic.
/// </summary>
public static class WzCrypto
{
    /// <summary>Constant subtracted during offset decoding (same in every client).</summary>
    private const uint OffsetConstant = 0x581C3F6D;

    /// <summary>
    /// Hash of a game version written as a decimal string: for each character c,
    /// hash = hash * 32 + c + 1. Used to decode directory offsets.
    /// </summary>
    public static uint VersionHash(int version)
    {
        uint hash = 0;
        foreach (char c in version.ToString(CultureInfo.InvariantCulture))
            hash = unchecked(hash * 32 + c + 1);
        return hash;
    }

    /// <summary>
    /// The value stored as a ushort right after the header: 0xFF xor each of the four hash bytes.
    /// Only 8 bits of information, so several versions map to the same value.
    /// </summary>
    public static ushort EncryptedVersion(uint hash) =>
        (ushort)(0xFF ^ ((hash >> 24) & 0xFF) ^ ((hash >> 16) & 0xFF) ^ ((hash >> 8) & 0xFF) ^ (hash & 0xFF));

    /// <summary>
    /// Decodes a directory entry offset. <paramref name="fieldPosition"/> is the file position of the
    /// 4-byte encrypted field itself (not of the entry), <paramref name="fileStart"/> the header's fstart.
    /// </summary>
    public static uint DecodeOffset(long fieldPosition, uint fileStart, uint versionHash, uint encrypted)
    {
        unchecked
        {
            uint o = (uint)(fieldPosition - fileStart) ^ 0xFFFFFFFF;
            o *= versionHash;
            o -= OffsetConstant;
            o = BitOperations.RotateLeft(o, (int)(o & 0x1F));
            o ^= encrypted;
            o += fileStart * 2;
            return o;
        }
    }
}
