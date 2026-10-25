namespace OakSoul.SpriteExport;

/// <summary>A BGRA8888 image in memory, used to build contact sheets.</summary>
internal sealed class Raster(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = new byte[width * height * 4];

    public void Fill(int x0, int y0, int w, int h, uint argb)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
                Set(x, y, argb);
    }

    /// <summary>Two-tone checkerboard so transparent and semi-transparent pixels are visible.</summary>
    public void Checker(int x0, int y0, int w, int h, uint a, uint b, int size = 8)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++)
                Set(x, y, (((x - x0) / size + (y - y0) / size) & 1) == 0 ? a : b);
    }

    /// <summary>Alpha-composites a straight-alpha BGRA bitmap at (x0, y0), scaled by <paramref name="scale"/> (nearest neighbour).</summary>
    public void Blit(byte[] src, int sw, int sh, int x0, int y0, double scale = 1)
    {
        int dw = Math.Max(1, (int)Math.Round(sw * scale)), dh = Math.Max(1, (int)Math.Round(sh * scale));
        for (int dy = 0; dy < dh; dy++)
        {
            int y = y0 + dy;
            if (y < 0 || y >= Height)
                continue;
            int sy = Math.Min(sh - 1, (int)(dy / scale));
            for (int dx = 0; dx < dw; dx++)
            {
                int x = x0 + dx;
                if (x < 0 || x >= Width)
                    continue;
                int sx = Math.Min(sw - 1, (int)(dx / scale));
                int s = (sy * sw + sx) * 4, d = (y * Width + x) * 4;
                int a = src[s + 3];
                for (int c = 0; c < 3; c++)
                    Pixels[d + c] = (byte)((src[s + c] * a + Pixels[d + c] * (255 - a) + 127) / 255);
                Pixels[d + 3] = 255;
            }
        }
    }

    /// <summary>Draws digits, letters A-Z and '_' with a 3x5 pixel font scaled by <paramref name="scale"/>.</summary>
    public void Text(string text, int x0, int y0, uint argb, int scale = 2)
    {
        int x = x0;
        foreach (char ch in text.ToUpperInvariant())
        {
            if (Font.TryGetValue(ch, out var rows))
            {
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if ((rows[r] >> (2 - c) & 1) != 0)
                            Fill(x + c * scale, y0 + r * scale, scale, scale, argb);
            }
            x += 4 * scale;
        }
    }

    public static int TextWidth(string text, int scale = 2) => Math.Max(0, text.Length * 4 * scale - scale);

    private void Set(int x, int y, uint argb)
    {
        int d = (y * Width + x) * 4;
        Pixels[d] = (byte)argb;
        Pixels[d + 1] = (byte)(argb >> 8);
        Pixels[d + 2] = (byte)(argb >> 16);
        Pixels[d + 3] = (byte)(argb >> 24);
    }

    // Each glyph is five rows of three bits (most significant bit = left column).
    private static readonly Dictionary<char, byte[]> Font = new()
    {
        ['0'] = [7, 5, 5, 5, 7], ['1'] = [2, 6, 2, 2, 7], ['2'] = [7, 1, 7, 4, 7], ['3'] = [7, 1, 7, 1, 7],
        ['4'] = [5, 5, 7, 1, 1], ['5'] = [7, 4, 7, 1, 7], ['6'] = [7, 4, 7, 5, 7], ['7'] = [7, 1, 1, 1, 1],
        ['8'] = [7, 5, 7, 5, 7], ['9'] = [7, 5, 7, 1, 7], ['_'] = [0, 0, 0, 0, 7], ['-'] = [0, 0, 7, 0, 0],
        ['A'] = [2, 5, 7, 5, 5], ['B'] = [6, 5, 6, 5, 6], ['C'] = [3, 4, 4, 4, 3], ['D'] = [6, 5, 5, 5, 6],
        ['E'] = [7, 4, 6, 4, 7], ['F'] = [7, 4, 6, 4, 4], ['G'] = [3, 4, 5, 5, 3], ['H'] = [5, 5, 7, 5, 5],
        ['I'] = [7, 2, 2, 2, 7], ['J'] = [1, 1, 1, 5, 2], ['K'] = [5, 5, 6, 5, 5], ['L'] = [4, 4, 4, 4, 7],
        ['M'] = [5, 7, 7, 5, 5], ['N'] = [6, 5, 5, 5, 5], ['O'] = [2, 5, 5, 5, 2], ['P'] = [6, 5, 6, 4, 4],
        ['Q'] = [2, 5, 5, 6, 3], ['R'] = [6, 5, 6, 5, 5], ['S'] = [3, 4, 2, 1, 6], ['T'] = [7, 2, 2, 2, 2],
        ['U'] = [5, 5, 5, 5, 7], ['V'] = [5, 5, 5, 5, 2], ['W'] = [5, 5, 7, 7, 5], ['X'] = [5, 5, 2, 5, 5],
        ['Y'] = [5, 5, 2, 2, 2], ['Z'] = [7, 1, 2, 4, 7],
    };
}
