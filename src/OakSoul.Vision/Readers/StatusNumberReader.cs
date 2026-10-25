using System.Globalization;

namespace OakSoul.Vision.Readers;

/// <summary>Numbers read from a status region's text row, with the raw recognised text for diagnostics.</summary>
public sealed record NumberReading(string Text, int MaxGlyphDistance, int Threshold)
{
    /// <summary>"cur/max" as in HP and MP.</summary>
    public (long Current, long Maximum)? Fraction
    {
        get
        {
            var parts = Text.Split('/');
            return parts.Length == 2 && TryNumber(parts[0], out var a) && TryNumber(parts[1], out var b) ? (a, b) : null;
        }
    }

    /// <summary>"total" and "pct%" as in EXP; the two parts are separated by a wide gap or a bracket.</summary>
    public (long Total, double Percent)? Exp
    {
        get
        {
            var parts = Text.Split('|');
            if (parts.Length != 2) return null;
            // Context beats the tiny glyphs: the total only ever has thousands separators, the percent only a point.
            if (!TryNumber(parts[0].Replace('.', ','), out var total)) return null;
            var pct = parts[1].Replace(',', '.').TrimEnd('%');
            return double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? (total, p) : null;
        }
    }

    private static bool TryNumber(string s, out long value) =>
        long.TryParse(s.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int Unknown => Text.Count(c => c == '?');

    public override string ToString() => $"'{Text}' (max glyph distance {MaxGlyphDistance}, threshold {Threshold})";
}

/// <summary>
/// Reads the numbers in the text row of an HP, MP or EXP region. Label letters are skipped because they fill
/// the top of the 9-row band where digits leave two empty rows. Groups of glyphs separated by a wide gap or a
/// green bracket are joined with '|', so EXP reads as "total|pct%".
/// </summary>
public static class StatusNumberReader
{
    public const int TextTop = 2, TextBottom = 10;
    /// <summary>Glyphs further apart than this many empty columns belong to different groups.</summary>
    public const int GroupGap = 5;
    /// <summary>Largest template distance accepted as a recognised glyph.</summary>
    public const int MaxDistance = 4;
    /// <summary>Widest glyph in this font (the percent sign); anything wider is two glyphs touching.</summary>
    public const int MaxGlyphWidth = 7;

    /// <summary>
    /// Ink thresholds tried in order. Native frames read perfectly at the first; resampled frames lose thin
    /// strokes there and do better lower, so the reading with the fewest unknown glyphs and the smallest
    /// total distance wins.
    /// </summary>
    public static readonly int[] Thresholds = [150, 140, 130];

    private static readonly Lazy<GlyphFont> _font = new(() => GlyphFont.LoadEmbedded("status-font.json"));
    public static GlyphFont Font => _font.Value;

    /// <summary>Glyphs of the text row that are neither label letters nor brackets, in reading order.</summary>
    public static List<GlyphSegment> NumberGlyphs(ReadOnlySpan<byte> native, int width, int threshold = GlyphSegmenter.DefaultLumaThreshold) =>
        GlyphSegmenter.Segment(native, width, TextTop, TextBottom, threshold)
            .SelectMany(s => GlyphSegmenter.SplitWide(s, MaxGlyphWidth))
            .Where(s => !s.IsGreen && !s.Bitmap.Rows[0].Contains('#') && !s.Bitmap.Rows[1].Contains('#'))
            .ToList();

    public static NumberReading Read(ReadOnlySpan<byte> native, int width, GlyphFont? font = null)
    {
        font ??= Font;
        NumberReading? best = null;
        var bestSum = int.MaxValue;
        foreach (var threshold in Thresholds)
        {
            var (reading, sum) = ReadAt(native, width, font, threshold);
            if (best is null || reading.Unknown < best.Unknown || (reading.Unknown == best.Unknown && sum < bestSum))
            {
                best = reading;
                bestSum = sum;
            }
            if (best.Unknown == 0 && bestSum == 0) break;
        }
        return best!;
    }

    /// <summary>
    /// Splits a segment too wide for one glyph where the font recognises the pieces best. Two digits can touch
    /// through a bridge of anti-aliased pixels that is thicker than a hollow digit's own interior columns, so the
    /// thinnest-column rule cuts in the wrong place; trying every cut and scoring both halves does not.
    /// </summary>
    private static IEnumerable<GlyphSegment> SplitByFont(GlyphSegment segment, GlyphFont font)
    {
        if (segment.IsGreen || segment.Width <= MaxGlyphWidth) return [segment];
        return BestSplit(segment, font, 0).Parts;
    }

    private static (List<GlyphSegment> Parts, int Score) BestSplit(GlyphSegment segment, GlyphFont font, int depth)
    {
        if (segment.Width <= MaxGlyphWidth || depth > 5) return ([segment], Score(segment, font));
        List<GlyphSegment>? bestParts = null;
        var bestScore = int.MaxValue;
        for (var cut = 1; cut <= segment.Width - 2; cut++)
        {
            var left = GlyphSegmenter.Slice(segment, 0, cut - 1);
            var leftScore = Score(left, font);
            if (leftScore >= bestScore) continue;
            foreach (var dropBridge in new[] { true, false })
            {
                var start = dropBridge ? cut + 1 : cut;
                if (start > segment.Width - 1) continue;
                var (rightParts, rightScore) = BestSplit(GlyphSegmenter.Slice(segment, start, segment.Width - 1), font, depth + 1);
                if (leftScore + rightScore < bestScore)
                {
                    bestScore = leftScore + rightScore;
                    bestParts = [left, .. rightParts];
                }
            }
        }
        return bestParts is null ? ([segment], 100) : (bestParts, bestScore);
    }

    private static int Score(GlyphSegment segment, GlyphFont font) =>
        segment.Bitmap.Ink == 0 || segment.Width > MaxGlyphWidth ? 100 : Math.Min(font.Match(segment.Bitmap).Distance, 100);

    private static (NumberReading Reading, int DistanceSum) ReadAt(ReadOnlySpan<byte> native, int width, GlyphFont font, int threshold)
    {
        var text = new System.Text.StringBuilder();
        var maxDistance = 0;
        var distanceSum = 0;
        var separatorPending = false;
        GlyphSegment? previous = null;
        foreach (var g in GlyphSegmenter.Segment(native, width, TextTop, TextBottom, threshold).SelectMany(s => SplitByFont(s, font)))
        {
            if (g.IsGreen) { separatorPending = text.Length > 0; continue; }          // bracket: splits groups
            if (g.Bitmap.Rows[0].Contains('#') || g.Bitmap.Rows[1].Contains('#')) continue; // label letter
            if (separatorPending || (previous is not null && g.X0 - previous.X1 - 1 > GroupGap)) text.Append('|');
            separatorPending = false;
            var (label, distance) = font.Match(g.Bitmap);
            text.Append(distance <= MaxDistance ? label : '?');
            maxDistance = Math.Max(maxDistance, distance);
            distanceSum += Math.Min(distance, 100);
            previous = g;
        }
        return (new NumberReading(text.ToString(), maxDistance, threshold), distanceSum);
    }
}
