namespace OakSoul.Detect;

/// <summary>
/// A BGRA image to draw annotations on: rectangles, a built-in 5x7 bitmap font, half-size downscaling and pasting,
/// enough for contact sheets without an imaging library.
/// </summary>
public sealed class Canvas
{
    public Canvas(int width, int height, uint fill = 0xFF202020)
    {
        Width = width; Height = height;
        Pixels = new byte[width * height * 4];
        Fill(0, 0, width, height, fill);
    }

    public Canvas(ReadOnlySpan<byte> bgra, int width, int height)
    {
        Width = width; Height = height;
        Pixels = bgra[..(width * height * 4)].ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>Colours as 0xAARRGGBB.</summary>
    public void Set(int x, int y, uint argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        var o = (y * Width + x) * 4;
        Pixels[o] = (byte)argb; Pixels[o + 1] = (byte)(argb >> 8); Pixels[o + 2] = (byte)(argb >> 16); Pixels[o + 3] = 255;
    }

    /// <summary>Blends a colour over a pixel with the given opacity (0..1).</summary>
    public void Tint(int x, int y, uint argb, double alpha)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        var o = (y * Width + x) * 4;
        Pixels[o] = (byte)(Pixels[o] * (1 - alpha) + (byte)argb * alpha);
        Pixels[o + 1] = (byte)(Pixels[o + 1] * (1 - alpha) + (byte)(argb >> 8) * alpha);
        Pixels[o + 2] = (byte)(Pixels[o + 2] * (1 - alpha) + (byte)(argb >> 16) * alpha);
    }

    public void Fill(int x, int y, int w, int h, uint argb)
    {
        for (var yy = Math.Max(0, y); yy < Math.Min(Height, y + h); yy++)
            for (var xx = Math.Max(0, x); xx < Math.Min(Width, x + w); xx++) Set(xx, yy, argb);
    }

    public void Rect(int x, int y, int w, int h, uint argb, int thickness = 1)
    {
        for (var t = 0; t < thickness; t++)
        {
            for (var xx = x - t; xx < x + w + t; xx++) { Set(xx, y - t, argb); Set(xx, y + h - 1 + t, argb); }
            for (var yy = y - t; yy < y + h + t; yy++) { Set(x - t, yy, argb); Set(x + w - 1 + t, yy, argb); }
        }
    }

    /// <summary>Text in the 5x7 font, 6 pixels per character at scale 1, on an optional backing box.</summary>
    public void Text(int x, int y, string text, uint argb, int scale = 1, uint? backing = 0xFF000000)
    {
        if (backing is { } b) Fill(x - 1, y - 1, text.Length * 6 * scale + 1, 8 * scale + 1, b);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i] is >= ' ' and <= '~' ? text[i] : '?';
            var glyph = TinyFont.Glyph(c);
            for (var col = 0; col < 5; col++)
                for (var row = 0; row < 7; row++)
                    if ((glyph[col] >> row & 1) != 0)
                        for (var sy = 0; sy < scale; sy++)
                            for (var sx = 0; sx < scale; sx++) Set(x + (i * 6 + col) * scale + sx, y + row * scale + sy, argb);
        }
    }

    public static int TextWidth(string text, int scale = 1) => text.Length * 6 * scale;

    /// <summary>A half-size copy, each pixel the mean of a 2x2 block.</summary>
    public Canvas Half()
    {
        var half = new Canvas(Width / 2, Height / 2);
        for (var y = 0; y < half.Height; y++)
            for (var x = 0; x < half.Width; x++)
                for (var ch = 0; ch < 3; ch++)
                {
                    int o(int dx, int dy) => ((2 * y + dy) * Width + 2 * x + dx) * 4 + ch;
                    half.Pixels[(y * half.Width + x) * 4 + ch] = (byte)((Pixels[o(0, 0)] + Pixels[o(1, 0)] + Pixels[o(0, 1)] + Pixels[o(1, 1)] + 2) >> 2);
                }
        return half;
    }

    public void Paste(Canvas other, int x, int y)
    {
        for (var yy = 0; yy < other.Height; yy++)
        {
            var ty = y + yy;
            if ((uint)ty >= (uint)Height) continue;
            for (var xx = 0; xx < other.Width; xx++)
            {
                var tx = x + xx;
                if ((uint)tx >= (uint)Width) continue;
                Array.Copy(other.Pixels, (yy * other.Width + xx) * 4, Pixels, (ty * Width + tx) * 4, 4);
            }
        }
    }

    public void Save(string path) => Png.WriteBgra(path, Pixels, Width, Height);
}

/// <summary>The classic 5x7 ASCII font: five column bytes per character, bit 0 the top row.</summary>
internal static class TinyFont
{
    private static readonly byte[] Data = Convert.FromHexString(
        "0000000000" + "00005F0000" + "0007000700" + "147F147F14" + "242A7F2A12" + "2313086462" + "3649552250" + "0005030000" +
        "001C224100" + "0041221C00" + "082A1C2A08" + "08083E0808" + "0050300000" + "0808080808" + "0060600000" + "2010080402" +
        "3E5149453E" + "00427F4000" + "4261514946" + "2141454B31" + "1814127F10" + "2745454539" + "3C4A494930" + "0171090503" +
        "3649494936" + "064949291E" + "0036360000" + "0056360000" + "0008142241" + "1414141414" + "4122140800" + "0201510906" +
        "324979413E" + "7E1111117E" + "7F49494936" + "3E41414122" + "7F4141221C" + "7F49494941" + "7F09090101" + "3E41415132" +
        "7F0808087F" + "00417F4100" + "2040413F01" + "7F08142241" + "7F40404040" + "7F0204027F" + "7F0408107F" + "3E4141413E" +
        "7F09090906" + "3E4151215E" + "7F09192946" + "4649494931" + "01017F0101" + "3F4040403F" + "1F2040201F" + "7F2018207F" +
        "6314081463" + "0304780403" + "6151494543" + "00007F4141" + "0204081020" + "41417F0000" + "0402010204" + "4040404040" +
        "0001020400" + "2054545478" + "7F48444438" + "3844444420" + "384444487F" + "3854545418" + "087E090102" + "081454543C" +
        "7F08040478" + "00447D4000" + "2040443D00" + "007F102844" + "00417F4000" + "7C04180478" + "7C08040478" + "3844444438" +
        "7C14141408" + "081414187C" + "7C08040408" + "4854545420" + "043F444020" + "3C4040207C" + "1C2040201C" + "3C4030403C" +
        "4428102844" + "0C5050503C" + "4464544C44" + "0008364100" + "00007F0000" + "0041360800" + "0804081008");

    public static ReadOnlySpan<byte> Glyph(char c) => Data.AsSpan((c - ' ') * 5, 5);
}
