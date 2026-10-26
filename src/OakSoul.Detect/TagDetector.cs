using System.Diagnostics;
using System.Text.RegularExpressions;
using OakSoul.Capture;
using OakSoul.Vision.Readers;

namespace OakSoul.Detect;

/// <summary>A name-tag box found in a frame, in viewport pixels (inclusive), with its OCR'd text when read.</summary>
public sealed record TagBox(int X0, int Y0, int X1, int Y1, double TextFraction, ulong Fingerprint)
{
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    public double CentreX => (X0 + X1) / 2.0;
    public string? Text { get; set; }
    public int? Level { get; set; }
    public string? Name { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>
/// Finds name tags: the client draws every tag (own, other players, pets, and mobs once they are hit) as near-white
/// text on a translucent black box 16 rows tall, mobs with a smaller level badge box just left of the name box.
/// Against the mosaic the box is unmistakable: the background darkened to 0.30 to 0.40 of its brightness, with the
/// same factor on all three channels. The detector stacks horizontal runs of box-or-text pixels into rectangles of
/// tag height, checks their text content, fingerprints the binarized text and OCRs each distinct fingerprint once.
/// </summary>
public sealed partial class TagDetector
{
    /// <summary>
    /// Box height range in rows. Every single tag measured on fx-combat (own, pet, Fire Boar, Dark Axe Stump) is 16
    /// rows; a box partly hidden by a sprite or the HUD edge, or merged with an overlapping one, ends up between.
    /// </summary>
    public const int MinHeight = 12, MaxHeight = 19;

    /// <summary>Shortest run that can be part of a box: the shortest name seen ("Mara") gives a box 32 wide.</summary>
    public const int MinWidth = 14;

    /// <summary>Background pixels a row run bridges: glyph edges that happen to match it, or the 1-2 column seam between badge and name.</summary>
    public const int MaxGap = 3;

    /// <summary>Gap of solid pixels bridged: glyph edges inside the box (up to five in a row inside "Boar").</summary>
    public const int MaxSolidGap = 7;

    /// <summary>
    /// Fraction of a box's pixels that must be text: glyphs covered 13% of the pet's box and 15% of a Fire Boar's on
    /// fx-combat; far fewer is a dark sprite part whose shading happens to match, far more a bright sprite or cloud.
    /// </summary>
    public const double MinText = 0.06, MaxText = 0.6;

    /// <summary>
    /// Fraction of a box's pixels that must be darkened background (a glyph's anti-aliased edge can match the
    /// background exactly, so the rest is text, unchanged pixels and a little sprite overlap).
    /// </summary>
    public const double MinShade = 0.3;

    /// <summary>Largest fraction of a box that may be solid foreground or unreliable background: a sprite overlapping it.</summary>
    public const double MaxSolid = 0.3;

    /// <summary>
    /// Two fingerprints within this fraction of differing text bits are the same tag: the anti-aliased glyph edges
    /// blend with whatever is behind the box, so the binarized text flips a few edge pixels from frame to frame
    /// (chosen, not tuned; fx-combat needed 227 OCR calls for 541 tag sightings).
    /// </summary>
    public const double FuzzyBits = 0.08;

    private readonly List<CacheEntry> _cache = [];

    /// <summary>Debug: <c>OAKSOUL_TAG_DUMP=dir</c> writes every crop sent to OCR, as captured, for trying OCR settings offline.</summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("OAKSOUL_TAG_DUMP");

    public int OcrCalls { get; private set; }
    public double OcrMilliseconds { get; private set; }
    public IReadOnlyCollection<string> DistinctTexts => _cache.Where(c => !string.IsNullOrWhiteSpace(c.Text)).Select(c => c.Text!).Distinct().ToList();

    private sealed record CacheEntry(int Width, int Height, ulong Hash, bool[] Bits, int Ones, string? Text, int? Level, string? Name);

    private static bool TagLike(byte c) => c is (byte)PixelClass.Shade or (byte)PixelClass.Bright or (byte)PixelClass.BrightFog or (byte)PixelClass.DarkConsistent;

    /// <summary>Finds tag boxes in the classified viewport of the last <see cref="ForegroundExtractor.Difference"/>.</summary>
    public List<TagBox> Find(ForegroundExtractor fg, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport)
    {
        int w = fg.Width, h = fg.Height;
        var cls = fg.Classes;
        var found = new List<TagBox>();
        // Open stacks: x range and first row; one per run of the previous row that continued.
        var open = new List<(int X0, int X1, int Y0, List<(int A, int B)> Runs)>();
        var runs = new List<(int A, int B)>();
        for (var y = 0; y <= h; y++)
        {
            runs.Clear();
            if (y < h)
            {
                var row = y * w;
                int start = -1, lastOn = -100;
                var gapOther = 0;
                for (var x = 0; x < w; x++)
                {
                    var c = cls[row + x];
                    if (!TagLike(c))
                    {
                        // Anti-aliased glyph edges blend white into the darkened box and read as solid or steam, several
                        // in a row, and now and then exactly as the background.
                        if (start >= 0 && c is not ((byte)PixelClass.Solid or (byte)PixelClass.Fog)) gapOther++;
                        if (start >= 0 && (x - lastOn > MaxSolidGap || gapOther > MaxGap))
                        {
                            if (lastOn - start + 1 >= MinWidth) runs.Add((start, lastOn));
                            start = -1;
                        }
                        continue;
                    }
                    if (start < 0) start = x;
                    lastOn = x;
                    gapOther = 0;
                }
                if (start >= 0 && lastOn - start + 1 >= MinWidth) runs.Add((start, lastOn));
            }
            var next = new List<(int X0, int X1, int Y0, List<(int A, int B)> Runs)>();
            var used = new bool[runs.Count];
            foreach (var s in open)
            {
                var continued = false;
                for (var r = 0; r < runs.Count; r++)
                {
                    var (a, b) = runs[r];
                    var overlap = Math.Min(b, s.X1) - Math.Max(a, s.X0) + 1;
                    // A box row covers mostly the same columns as the one above: give or take a rounded corner, or the level badge, which is two rows shorter than the name box.
                    if (overlap >= 0.6 * (s.X1 - s.X0 + 1) && overlap >= 0.6 * (b - a + 1))
                    {
                        s.Runs.Add((a, b));
                        next.Add((Math.Min(s.X0, a), Math.Max(s.X1, b), s.Y0, s.Runs));
                        used[r] = continued = true;
                        break;
                    }
                }
                if (!continued) Close(s.Y0, y - 1, s.Runs, cls, w, frame, frameWidth, viewport, found);
            }
            for (var r = 0; r < runs.Count; r++)
                if (!used[r]) next.Add((runs[r].A, runs[r].B, y, [runs[r]]));
            open = next;
        }
        return Dedupe(found);
    }

    private void Close(int y0, int y1, List<(int A, int B)> runs, byte[] cls, int w, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, List<TagBox> found)
    {
        var height = y1 - y0 + 1;
        if (height < MinHeight) return;
        // Tall stacks are a box merged with dark sprite rows above or below it: keep the best 16-row window.
        var x0s = runs.Select(r => r.A).Order().ToList();
        var x1s = runs.Select(r => r.B).Order().ToList();
        int bx0 = x0s[x0s.Count / 2], bx1 = x1s[x1s.Count / 2];
        if (bx1 - bx0 + 1 < MinWidth) return;
        var windows = height <= MaxHeight ? [(y0, y1)] : Enumerable.Range(y0, height - 16 + 1).Select(t => (t, t + 15)).ToList();
        TagBox? best = null;
        var bestScore = 0.0;
        foreach (var (wy0, wy1) in windows)
        {
            int text = 0, shade = 0, solid = 0, n = 0;
            for (var y = wy0; y <= wy1; y++)
                for (var x = bx0; x <= bx1; x++)
                {
                    var c = (PixelClass)cls[y * w + x];
                    n++;
                    if (c is PixelClass.Bright or PixelClass.BrightFog) text++;
                    else if (c is PixelClass.Shade or PixelClass.DarkConsistent) shade++;
                    else if (c is PixelClass.Solid or PixelClass.Ignored) solid++;
                }
            var tf = (double)text / n;
            var cf = (double)shade / n;
            if (tf < MinText || tf > MaxText || cf < MinShade || (double)solid / n > MaxSolid) continue;
            if (cf > bestScore) { bestScore = cf; best = new TagBox(bx0, wy0, bx1, wy1, tf, Fingerprint(frame, frameWidth, viewport, bx0, wy0, bx1, wy1)); }
        }
        if (best is not null) found.Add(best);
    }

    private static List<TagBox> Dedupe(List<TagBox> boxes)
    {
        var result = new List<TagBox>();
        foreach (var b in boxes.OrderByDescending(b => b.Width * b.Height))
            if (!result.Any(r => Math.Min(r.X1, b.X1) - Math.Max(r.X0, b.X0) > 0 && Math.Min(r.Y1, b.Y1) - Math.Max(r.Y0, b.Y0) > 4)) result.Add(b);
        return result;
    }

    /// <summary>Hash of the binarized text pixels (luma at or above <see cref="ForegroundExtractor.TextLuma"/>) inside the box.</summary>
    private static ulong Fingerprint(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, int x0, int y0, int x1, int y1)
    {
        var hash = 14695981039346656037UL;
        hash = (hash ^ (ulong)(x1 - x0)) * 1099511628211UL;
        hash = (hash ^ (ulong)(y1 - y0)) * 1099511628211UL;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var o = ((y + viewport.Y) * frameWidth + x + viewport.X) * 4;
                var bit = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2 >= ForegroundExtractor.TextLuma ? 1UL : 0UL;
                hash = (hash ^ bit) * 1099511628211UL;
            }
        return hash;
    }

    private static bool[] Bits(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, TagBox b)
    {
        var bits = new bool[b.Width * b.Height];
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var o = ((b.Y0 + y + viewport.Y) * frameWidth + b.X0 + x + viewport.X) * 4;
                bits[y * b.Width + x] = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2 >= ForegroundExtractor.TextLuma;
            }
        return bits;
    }

    /// <summary>
    /// Gives each box its text: from the cache when the fingerprint (or a near-identical one) was read before,
    /// otherwise by OCR of the box and a little margin, upscaled. The crop of the pixel-exact frame is binarized first,
    /// light text as black ink on white paper (<see cref="OcrInput.InkOnPaper"/>, as the level badge is read), not
    /// softened as the status bar and map name are: on fx-exact's five crops of the pet's tag Vision read Mara on 2 as
    /// captured (lara and a cut-off Ma on the others), on none softened (empty or "BJBI"), and on 3 binarized, and the
    /// mob tag "Lv. 17 Axe Stump" as captured "Lw, 17", softened "Lw, 17", binarized "Lv. 17"; on fx-anttunnel-open's
    /// player and pet tags (Slivan, Black Kitty) all three read alike.
    /// </summary>
    public void Read(List<TagBox> boxes, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, bool ocr = true)
    {
        foreach (var b in boxes)
        {
            var hit = _cache.FirstOrDefault(c => c.Hash == b.Fingerprint);
            bool[]? bits = null;
            if (hit is null)
            {
                bits = Bits(frame, frameWidth, viewport, b);
                var ones = bits.Count(v => v);
                foreach (var c in _cache)
                {
                    if (Math.Abs(c.Width - b.Width) > 2 || Math.Abs(c.Height - b.Height) > 1) continue;
                    if (Differing(c, bits, b.Width, b.Height) <= FuzzyBits * Math.Max(ones, c.Ones)) { hit = c; break; }
                }
            }
            if (hit is not null)
            {
                b.Text = hit.Text; b.Level = hit.Level; b.Name = hit.Name; b.FromCache = true;
                continue;
            }
            if (!ocr) continue;
            var text = Ocr(frame, frameWidth, viewport, b);
            var (level, name) = Parse(text);
            b.Text = text; b.Level = level; b.Name = name;
            bits ??= Bits(frame, frameWidth, viewport, b);
            _cache.Add(new CacheEntry(b.Width, b.Height, b.Fingerprint, bits, bits.Count(v => v), text, level, name));
        }
    }

    private static int Differing(CacheEntry c, bool[] bits, int w, int h)
    {
        // Compare at the same top-left; the box edges are found to a pixel or two, so also try one-column shifts.
        var best = int.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
        {
            var diff = 0;
            for (var y = 0; y < Math.Min(h, c.Height); y++)
                for (var x = 0; x < Math.Min(w, c.Width); x++)
                {
                    var cx = x + dx;
                    var a = (uint)cx < (uint)c.Width && c.Bits[y * c.Width + cx];
                    if (a != bits[y * w + x]) diff++;
                }
            best = Math.Min(best, diff);
        }
        return best;
    }

    private string? Ocr(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, TagBox b)
    {
        var sw = Stopwatch.StartNew();
        // Only the box and a small margin: a wider crop picked up the pet's tag and sprite parts as stray letters.
        int x0 = Math.Max(0, b.X0 + viewport.X - 3), y0 = Math.Max(0, b.Y0 + viewport.Y - 2);
        int x1 = Math.Min(frameWidth - 1, b.X1 + viewport.X + 3), y1 = Math.Min(viewport.Y + viewport.Height - 1, b.Y1 + viewport.Y + 2);
        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var crop = new byte[w * h * 4];
        for (var y = 0; y < h; y++) frame.Slice(((y0 + y) * frameWidth + x0) * 4, w * 4).CopyTo(crop.AsSpan(y * w * 4));
        if (DumpDir is { } dump) { Directory.CreateDirectory(dump); Png.WriteBgra(Path.Combine(dump, $"tag{OcrCalls:000}_{x0}_{y0}.png"), crop, w, h); }
        var lines = TextRecognizer.Recognize(OcrInput.InkOnPaper(crop, w, h), w, h, upscale: 4);
        OcrCalls++;
        OcrMilliseconds += sw.Elapsed.TotalMilliseconds;
        // Fragments of other tags and sprite edges come back as two- and three-letter junk ("eJe", "PJP", "M");
        // keep lines that can be a name (four characters or more) or part of a level badge.
        var kept = lines.Where(l => l.Confidence >= 0.5 && (l.Text.Trim().Length >= 4 || l.Text.Any(char.IsDigit) || l.Text.TrimStart().StartsWith('L'))).ToList();
        if (kept.Count == 0) return null;
        return string.Join(" ", kept.OrderBy(l => l.X).Select(l => l.Text.Trim())).Trim();
    }

    /// <summary>
    /// "Lv. 23 Dark Axe Stump" to level and name. Vision reads the tiny badge as "Lv. 23", "Lv, 23", "LV.23", "Lw, 11",
    /// "Lx23", ":23" or just "23", so the level is the last number of one to three digits followed by a capitalised
    /// word, and the name is the text after it (whatever precedes it is badge debris or a neighbouring tag). A badge
    /// whose number is lost still marks a mob, with level 0.
    /// </summary>
    public static (int? Level, string? Name) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var m = LevelPattern().Matches(text).LastOrDefault();
        if (m is not null && int.TryParse(m.Groups[1].Value, out var level))
        {
            var name = CleanName(text[(m.Index + m.Length)..]);
            return (level, name);
        }
        // A badge whose number did not read ("LV Fire"): a mob, level unknown (0).
        var badge = BadgePattern().Match(text);
        if (badge.Success) return (0, CleanName(text[(badge.Index + badge.Length)..]));
        return (null, CleanName(text));
    }

    private static string? CleanName(string s)
    {
        var name = CollapseSpaces(s.Trim(' ', '|', '-', '.', ',', ':', ';', '*', ']', '['));
        return name.Count(char.IsLetter) >= 3 ? name : null;
    }

    private static string CollapseSpaces(string s) => SpacePattern().Replace(s, " ");

    [GeneratedRegex(@"(\d{1,3})[^\dA-Za-z]*(?=[A-Z])")]
    private static partial Regex LevelPattern();

    [GeneratedRegex(@"^\W*L[vVwWxy][.,:;]?(?=\s|$)")]
    private static partial Regex BadgePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
