using System.Buffers.Binary;

namespace OakSoul.Wz;

/// <summary>DXT3 / DXT5 (BC2 / BC3) block decompression to BGRA8888 with straight alpha.</summary>
internal static class Dxt
{
    public enum Kind { Dxt3, Dxt5 }

    public static byte[] Decode(byte[] data, int width, int height, Kind kind)
    {
        var o = new byte[width * height * 4];
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        Span<byte> colors = stackalloc byte[16];   // 4 palette entries, BGRA
        Span<byte> alphas = stackalloc byte[16];   // per-pixel alpha for the block
        Span<byte> alphaPalette = stackalloc byte[8];

        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var block = data.AsSpan((by * bw + bx) * 16, 16);

                if (kind == Kind.Dxt3)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        alphas[i * 2] = (byte)((block[i] & 0x0F) * 17);
                        alphas[i * 2 + 1] = (byte)((block[i] >> 4) * 17);
                    }
                }
                else
                {
                    byte a0 = block[0], a1 = block[1];
                    alphaPalette[0] = a0;
                    alphaPalette[1] = a1;
                    if (a0 > a1)
                    {
                        for (int i = 1; i < 7; i++)
                            alphaPalette[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
                    }
                    else
                    {
                        for (int i = 1; i < 5; i++)
                            alphaPalette[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
                        alphaPalette[6] = 0;
                        alphaPalette[7] = 255;
                    }
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++)
                        bits |= (ulong)block[2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++)
                        alphas[i] = alphaPalette[(int)((bits >> (3 * i)) & 7)];
                }

                // Colour half: always four-colour mode in DXT3/DXT5.
                var colorBlock = block[8..];
                int c0 = BinaryPrimitives.ReadUInt16LittleEndian(colorBlock);
                int c1 = BinaryPrimitives.ReadUInt16LittleEndian(colorBlock[2..]);
                Unpack565(c0, colors[..4]);
                Unpack565(c1, colors.Slice(4, 4));
                for (int ch = 0; ch < 3; ch++)
                {
                    colors[8 + ch] = (byte)((2 * colors[ch] + colors[4 + ch] + 1) / 3);
                    colors[12 + ch] = (byte)((colors[ch] + 2 * colors[4 + ch] + 1) / 3);
                }
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(colorBlock[4..]);

                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height)
                        break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width)
                            break;
                        int p = py * 4 + px;
                        int ci = (int)((indices >> (2 * p)) & 3) * 4;
                        int j = (y * width + x) * 4;
                        o[j] = colors[ci];
                        o[j + 1] = colors[ci + 1];
                        o[j + 2] = colors[ci + 2];
                        o[j + 3] = alphas[p];
                    }
                }
            }
        return o;
    }

    private static void Unpack565(int v, Span<byte> bgr)
    {
        int b = v & 0x1F, g = (v >> 5) & 0x3F, r = (v >> 11) & 0x1F;
        bgr[0] = (byte)((b << 3) | (b >> 2));
        bgr[1] = (byte)((g << 2) | (g >> 4));
        bgr[2] = (byte)((r << 3) | (r >> 2));
        bgr[3] = 255;
    }
}
