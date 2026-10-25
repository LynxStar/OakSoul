namespace OakSoul.Vision.Readers;

/// <summary>Result of reading one status bar: how full it is, and whether the reader trusts it.</summary>
public readonly record struct BarReading(double Ratio, int FilledPixels, int BarPixels, bool Valid)
{
    public override string ToString() => Valid ? $"{Ratio:P1} ({FilledPixels}/{BarPixels}px)" : "invalid";
}

/// <summary>
/// Reads the fill ratio of an HP, MP or EXP bar from its native-size region pixels. The bar sits under the
/// text row: a bright highlight line on top, then a body whose filled part is strongly coloured (red, blue or
/// yellow-green) and whose empty part is a flat light grey. The bar's horizontal extent is measured from the
/// highlight line each time, so the reader survives small layout shifts; the fill is the run of saturated
/// columns from the left edge.
/// </summary>
public static class BarReader
{
    /// <summary>Row of the bright highlight line, relative to the region.</summary>
    public const int HighlightRow = 14;
    /// <summary>Body rows sampled for the fill, below the highlight stripe and the gradient's light top.</summary>
    public const int BodyTop = 18, BodyBottom = 21;
    public const int HighlightLuma = 150;
    public const int FilledSaturation = 50;

    public static BarReading Read(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (height <= BodyBottom) return default;

        // Horizontal extent: the longest bright run on the highlight row.
        int bestStart = -1, bestLength = 0, runStart = -1;
        for (var x = 0; x <= width; x++)
        {
            var bright = x < width && Luma(bgra, (HighlightRow * width + x) * 4) > HighlightLuma;
            if (bright && runStart < 0) runStart = x;
            if (!bright && runStart >= 0)
            {
                if (x - runStart > bestLength) { bestLength = x - runStart; bestStart = runStart; }
                runStart = -1;
            }
        }
        if (bestLength < 16) return default;

        // Fill: saturated columns averaged over the body rows, counted as one run from the left edge.
        var filled = 0;
        var gaps = 0;
        for (var x = bestStart; x < bestStart + bestLength; x++)
        {
            var saturation = 0;
            for (var y = BodyTop; y <= BodyBottom; y++)
            {
                var o = (y * width + x) * 4;
                int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
                saturation += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            }
            saturation /= BodyBottom - BodyTop + 1;
            if (saturation > FilledSaturation)
            {
                if (gaps > 0) return new BarReading(0, filled, bestLength, false); // colour after a grey gap: not a bar
                filled++;
            }
            else if (filled > 0)
            {
                gaps++;
            }
        }
        return new BarReading((double)filled / bestLength, filled, bestLength, true);
    }

    private static int Luma(ReadOnlySpan<byte> bgra, int offset) => (bgra[offset] + bgra[offset + 1] * 2 + bgra[offset + 2]) >> 2;
}
