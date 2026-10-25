using System.Buffers.Binary;
using System.Numerics;

namespace OakSoul.Wz;

/// <summary>
/// The ChaCha20 block function (RFC 8439 layout: 4 constant words, 8 key words, a 32-bit block counter,
/// 3 nonce words; 20 rounds). Only what the ".ms" pack reader needs: one 64-byte key-stream block at a time,
/// and an XOR over a buffer with consecutive counters.
/// </summary>
public static class ChaCha20
{
    public const int KeyLength = 32, NonceLength = 12, BlockLength = 64;

    /// <summary>Writes the 64-byte key-stream block for <paramref name="counter"/> into <paramref name="output"/>.</summary>
    public static void Block(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
        for (int i = 0; i < 8; i++)
            s[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        s[12] = counter;
        for (int i = 0; i < 3; i++)
            s[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);

        Span<uint> w = stackalloc uint[16];
        s.CopyTo(w);
        for (int round = 0; round < 10; round++)
        {
            Quarter(w, 0, 4, 8, 12); Quarter(w, 1, 5, 9, 13); Quarter(w, 2, 6, 10, 14); Quarter(w, 3, 7, 11, 15);
            Quarter(w, 0, 5, 10, 15); Quarter(w, 1, 6, 11, 12); Quarter(w, 2, 7, 8, 13); Quarter(w, 3, 4, 9, 14);
        }
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], unchecked(w[i] + s[i]));
    }

    /// <summary>XORs <paramref name="data"/> in place with the key stream starting at block <paramref name="counter"/>.</summary>
    public static void Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> data)
    {
        Span<byte> ks = stackalloc byte[BlockLength];
        for (int off = 0; off < data.Length; off += BlockLength, counter = unchecked(counter + 1))
        {
            Block(key, nonce, counter, ks);
            var chunk = data[off..Math.Min(off + BlockLength, data.Length)];
            for (int i = 0; i < chunk.Length; i++)
                chunk[i] ^= ks[i];
        }
    }

    private static void Quarter(Span<uint> w, int a, int b, int c, int d)
    {
        unchecked
        {
            w[a] += w[b]; w[d] = BitOperations.RotateLeft(w[d] ^ w[a], 16);
            w[c] += w[d]; w[b] = BitOperations.RotateLeft(w[b] ^ w[c], 12);
            w[a] += w[b]; w[d] = BitOperations.RotateLeft(w[d] ^ w[a], 8);
            w[c] += w[d]; w[b] = BitOperations.RotateLeft(w[b] ^ w[c], 7);
        }
    }
}
