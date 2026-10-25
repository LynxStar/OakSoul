using System.Text;
using System.Text.Json;

namespace OakSoul.Vision.Readers;

/// <summary>A binarized glyph: <see cref="Height"/> rows of <see cref="Width"/> bits, stored one row per string of '#' and '.'.</summary>
public sealed record GlyphBitmap(int Width, int Height, string[] Rows)
{
    public bool this[int x, int y] => Rows[y][x] == '#';

    public int Ink => Rows.Sum(r => r.Count(c => c == '#'));

    /// <summary>Number of differing pixels, with width mismatch counted as fully different columns.</summary>
    public int Distance(GlyphBitmap other)
    {
        if (other.Height != Height) return int.MaxValue;
        var distance = Math.Abs(Width - other.Width) * Height;
        var w = Math.Min(Width, other.Width);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < w; x++)
                if (Rows[y][x] != other.Rows[y][x]) distance++;
        return distance;
    }

    public override string ToString() => string.Join("\n", Rows);
}

/// <summary>One glyph cut out of a text row: where it sits and what it looks like.</summary>
public sealed record GlyphSegment(int X0, int X1, GlyphBitmap Bitmap, bool IsGreen)
{
    public int Width => X1 - X0 + 1;
}

/// <summary>
/// Splits a text row into glyphs. Bright pixels (digits, label) and saturated green pixels (the brackets around
/// the numbers) both count as ink; columns with no ink separate glyphs. A glyph is tagged green when most of
/// its ink is green, which lets the number readers use the brackets as anchors.
/// </summary>
public static class GlyphSegmenter
{
    public const int DefaultLumaThreshold = 150;

    public static List<GlyphSegment> Segment(ReadOnlySpan<byte> bgra, int width, int rowTop, int rowBottom, int lumaThreshold = DefaultLumaThreshold)
    {
        var height = rowBottom - rowTop + 1;
        var bright = new bool[height, width];
        var green = new bool[height, width];
        var inkColumns = new bool[width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = ((rowTop + y) * width + x) * 4;
                int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
                var luma = (b + g * 2 + r) >> 2;
                var isGreen = g > 140 && g >= r + 20 && g > b + 60; // the brackets are a dull yellow-green, about (140,170,80)
                bright[y, x] = luma > lumaThreshold && !isGreen;
                green[y, x] = isGreen;
                inkColumns[x] |= bright[y, x] || green[y, x];
            }
        }

        var segments = new List<GlyphSegment>();
        var x0 = -1;
        for (var x = 0; x <= width; x++)
        {
            var ink = x < width && inkColumns[x];
            if (ink && x0 < 0) x0 = x;
            if (!ink && x0 >= 0)
            {
                segments.Add(Cut(bright, green, height, x0, x - 1));
                x0 = -1;
            }
        }
        return segments;
    }

    /// <summary>
    /// Splits a segment wider than any glyph of the font at its thinnest interior column, recursively. Two
    /// digits can touch through a stray anti-aliased pixel; the valley column is dropped when it holds at most
    /// two pixels of ink, otherwise it stays with the right-hand glyph.
    /// </summary>
    public static IEnumerable<GlyphSegment> SplitWide(GlyphSegment segment, int maxWidth)
    {
        if (segment.IsGreen || segment.Width <= maxWidth) { yield return segment; yield break; }
        var bitmap = segment.Bitmap;
        int bestColumn = -1, bestInk = int.MaxValue;
        for (var x = 2; x <= segment.Width - 3; x++)
        {
            var ink = 0;
            for (var y = 0; y < bitmap.Height; y++) if (bitmap[x, y]) ink++;
            if (ink < bestInk) { bestInk = ink; bestColumn = x; }
        }
        if (bestColumn < 0) { yield return segment; yield break; }
        var dropValley = bestInk <= 2;
        var left = Slice(segment, 0, bestColumn - 1);
        var right = Slice(segment, dropValley ? bestColumn + 1 : bestColumn, segment.Width - 1);
        foreach (var s in SplitWide(left, maxWidth)) yield return s;
        foreach (var s in SplitWide(right, maxWidth)) yield return s;
    }

    public static GlyphSegment Slice(GlyphSegment segment, int from, int to)
    {
        var rows = segment.Bitmap.Rows.Select(r => r.Substring(from, to - from + 1)).ToArray();
        return new GlyphSegment(segment.X0 + from, segment.X0 + to, new GlyphBitmap(to - from + 1, segment.Bitmap.Height, rows), segment.IsGreen);
    }

    /// <summary>Splits a precomputed ink mask into glyphs at empty columns; for fonts binarized by other rules.</summary>
    public static List<GlyphSegment> SegmentInk(bool[,] ink)
    {
        int height = ink.GetLength(0), width = ink.GetLength(1);
        var none = new bool[height, width];
        var segments = new List<GlyphSegment>();
        var x0 = -1;
        for (var x = 0; x <= width; x++)
        {
            var any = false;
            if (x < width) for (var y = 0; y < height && !any; y++) any = ink[y, x];
            if (any && x0 < 0) x0 = x;
            if (!any && x0 >= 0) { segments.Add(Cut(ink, none, height, x0, x - 1)); x0 = -1; }
        }
        return segments;
    }

    private static GlyphSegment Cut(bool[,] bright, bool[,] green, int height, int x0, int x1)
    {
        var rows = new string[height];
        int brightInk = 0, greenInk = 0;
        for (var y = 0; y < height; y++)
        {
            var sb = new StringBuilder(x1 - x0 + 1);
            for (var x = x0; x <= x1; x++)
            {
                var on = bright[y, x] || green[y, x];
                sb.Append(on ? '#' : '.');
                if (bright[y, x]) brightInk++;
                if (green[y, x]) greenInk++;
            }
            rows[y] = sb.ToString();
        }
        return new GlyphSegment(x0, x1, new GlyphBitmap(x1 - x0 + 1, height, rows), greenInk > brightInk);
    }
}

/// <summary>A set of labelled glyph templates for one on-screen font, matched by nearest bitmap.</summary>
public sealed class GlyphFont
{
    private readonly List<(char Label, GlyphBitmap Bitmap)> _templates = [];

    public string Name { get; }
    public int Height { get; }
    public IReadOnlyList<(char Label, GlyphBitmap Bitmap)> Templates => _templates;

    public GlyphFont(string name, int height)
    {
        Name = name;
        Height = height;
    }

    /// <summary>
    /// Learns templates from a text row whose content is known: one glyph per character of <paramref name="text"/>,
    /// in order. Throws when the segment count does not match, which usually means a glyph split or merged.
    /// </summary>
    public void Learn(IReadOnlyList<GlyphSegment> glyphs, string text)
    {
        if (glyphs.Count != text.Length)
            throw new InvalidOperationException($"expected {text.Length} glyphs for '{text}' but found {glyphs.Count}: widths {string.Join(",", glyphs.Select(g => g.Width))}");
        for (var i = 0; i < text.Length; i++) Add(text[i], glyphs[i].Bitmap);
    }

    public void Add(char label, GlyphBitmap bitmap)
    {
        if (bitmap.Height != Height) throw new ArgumentException($"glyph height {bitmap.Height} does not match font height {Height}");
        if (!_templates.Any(t => t.Label == label && t.Bitmap.Distance(bitmap) == 0)) _templates.Add((label, bitmap));
    }

    /// <summary>Nearest template and its pixel distance; the caller decides what distance is acceptable.</summary>
    public (char Label, int Distance) Match(GlyphBitmap glyph)
    {
        char best = '?';
        var bestDistance = int.MaxValue;
        foreach (var (label, template) in _templates)
        {
            var d = template.Distance(glyph);
            if (d < bestDistance) { bestDistance = d; best = label; }
        }
        return (best, bestDistance);
    }

    public string ToJson() => JsonSerializer.Serialize(new FontFile(Name, Height,
        _templates.Select(t => new TemplateEntry(t.Label.ToString(), t.Bitmap.Width, t.Bitmap.Rows)).ToList()),
        new JsonSerializerOptions { WriteIndented = true });

    public static GlyphFont FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<FontFile>(json) ?? throw new InvalidOperationException("empty font file");
        var font = new GlyphFont(file.Name, file.Height);
        foreach (var t in file.Templates) font.Add(t.Label[0], new GlyphBitmap(t.Width, file.Height, t.Rows));
        return font;
    }

    public static GlyphFont LoadEmbedded(string resourceName)
    {
        using var stream = typeof(GlyphFont).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded font '{resourceName}' not found");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record FontFile(string Name, int Height, List<TemplateEntry> Templates);
    private sealed record TemplateEntry(string Label, int Width, string[] Rows);
}
