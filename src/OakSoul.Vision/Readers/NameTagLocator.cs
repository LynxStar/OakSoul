using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>Where a name tag was found: the tag box, and the point just above it where the character's feet are.</summary>
public readonly record struct NameTagHit(CaptureRect Tag, int Matched, int Expected)
{
    public (double X, double Y) Feet => (Tag.X + Tag.Width / 2.0, Tag.Y - 1);
}

/// <summary>
/// Finds one character's name tag in the viewport by exact pixel signature. The client renders a tag as light
/// text on a translucent dark box, identically every frame, so a binarized template taken once is enough: a
/// position matches when nearly all text pixels are bright and nearly all gap pixels inside the box are dark.
/// The search runs near the last hit first and falls back to the whole viewport. The template is learned at
/// run time from a frame where OCR sees the name on its own, so no per-character asset is needed. On a pixel-exact
/// frame a tag partly hidden by a spell, the pet or a rope is still found near where it was last seen, by the share of
/// its text columns that match exactly (<see cref="MinExactColumns"/>).
/// </summary>
public sealed class NameTagLocator
{
    public const int TextLuma = 140, GapLuma = 130;

    /// <summary>
    /// Share of the template's text columns that must match pixel for pixel (every text pixel bright, every gap pixel
    /// dark) for a partly hidden tag. On fx-exact an unhidden tag matches all its columns; tags under a lightning bolt,
    /// a rope with the pet's tag, or the pet itself kept 0.53 to 0.88 of them, while the best place anywhere else in the
    /// viewport, the tag itself one pixel off included, reached 0.33.
    /// </summary>
    public const double MinExactColumns = 0.45;

    /// <summary>How far from where the tag was last seen a partly hidden tag is looked for: a second's walk.</summary>
    public const int OccludedSearchRadius = 128;

    private readonly string _name;
    private (int X, int Y)[] _text = [];
    private (int X, int Y)[] _gaps = [];
    private ((int X, int Y) Pos, bool Text)[] _probes = [];
    private (int X, (int Y, bool Text)[] Pixels)[] _columns = [];
    private int _width, _height;
    private CaptureRect? _last, _lastSeen;

    public NameTagLocator(string name) => _name = name;

    public bool Ready => _text.Length > 0;
    public string Name => _name;
    public int TemplateWidth => _width;
    public int TemplateHeight => _height;

    /// <summary>
    /// Tries to learn the template from this frame: OCR of the viewport must return the name as a line of its own,
    /// exactly or with one character misread (<see cref="SameName"/>), and only once. Returns true when the template was
    /// learned. The OCR sees the crop softened on a pixel-exact frame (<see cref="OcrInput"/>); the template is cut from
    /// the frame's own pixels.
    /// </summary>
    public bool TryLearn(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var viewport = geometry.Layout["world.viewport"];
        var native = geometry.ExtractNative(frame, viewport);
        var lines = TextRecognizer.Recognize(OcrInput.ForOcr(geometry, frame, native, viewport.Width, viewport.Height), viewport.Width, viewport.Height, upscale: 2);
        var own = lines.Where(l => string.Equals(l.Text.Trim(), _name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count == 0) own = lines.Where(l => SameName(l.Text, _name)).ToList();
        if (own.Count != 1) return false;
        var l = own[0];
        // Rows come from the OCR box with one pixel of slack, since the sprite's feet sit just above the tag and
        // would otherwise join the template; columns get a little more room.
        int x0 = Math.Max(0, (int)l.X - 1), y0 = Math.Max(0, (int)l.Y - 1);
        int x1 = Math.Min(viewport.Width - 1, (int)(l.X + l.Width) + 1), y1 = Math.Min(viewport.Height - 1, (int)(l.Y + l.Height) + 1);
        int tx0 = int.MaxValue, ty0 = int.MaxValue, tx1 = -1, ty1 = -1;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (Luma(native, viewport.Width, x, y) > TextLuma) { tx0 = Math.Min(tx0, x); ty0 = Math.Min(ty0, y); tx1 = Math.Max(tx1, x); ty1 = Math.Max(ty1, y); }
        if (tx1 < 0 || tx1 - tx0 < 10) return false;
        // Trim edge columns holding a single bright pixel: stray sprite or outline pixels, not glyph strokes.
        int ColumnInk(int x) { var n = 0; for (var y = ty0; y <= ty1; y++) if (Luma(native, viewport.Width, x, y) > TextLuma) n++; return n; }
        while (tx0 < tx1 && ColumnInk(tx0) <= 1) tx0++;
        while (tx1 > tx0 && ColumnInk(tx1) <= 1) tx1--;
        // Keep only the longest contiguous band of rows that carry text: the sprite's feet above the box are
        // separated from the glyphs by the box's dark top margin.
        int RowInk(int y) { var n = 0; for (var x = tx0; x <= tx1; x++) if (Luma(native, viewport.Width, x, y) > TextLuma) n++; return n; }
        int bestStart = ty0, bestLength = 0, runStart = -1;
        for (var y = ty0; y <= ty1 + 1; y++)
        {
            var ink = y <= ty1 && RowInk(y) >= 2;
            if (ink && runStart < 0) runStart = y;
            if (!ink && runStart >= 0) { if (y - runStart > bestLength) { bestLength = y - runStart; bestStart = runStart; } runStart = -1; }
        }
        if (bestLength < 6) return false;
        ty0 = bestStart; ty1 = bestStart + bestLength - 1;
        // World art beside the box can reach into the OCR box and survive the trim above (on fx-exact's first keyframe
        // a lit column one pixel left of the box made the template 58 wide instead of 54). The rows just above and below
        // the band are the box's own dark margin across the whole name, while art beside the box crosses them, so an
        // edge column bright there is cut, with the empty columns it leaves.
        bool CrossesMargin(int x) => (ty0 > 0 && Luma(native, viewport.Width, x, ty0 - 1) > TextLuma)
            || (ty1 + 1 < viewport.Height && Luma(native, viewport.Width, x, ty1 + 1) > TextLuma);
        while (tx0 < tx1 && CrossesMargin(tx0)) { tx0++; while (tx0 < tx1 && ColumnInk(tx0) <= 1) tx0++; }
        while (tx1 > tx0 && CrossesMargin(tx1)) { tx1--; while (tx1 > tx0 && ColumnInk(tx1) <= 1) tx1--; }
        if (tx1 - tx0 < 10) return false;
        var text = new List<(int, int)>();
        var gaps = new List<(int, int)>();
        for (var y = ty0; y <= ty1; y++)
            for (var x = tx0; x <= tx1; x++)
            {
                var luma = Luma(native, viewport.Width, x, y);
                if (luma > TextLuma) text.Add((x - tx0, y - ty0));
                else if (luma < GapLuma) gaps.Add((x - tx0, y - ty0));
            }
        _text = text.ToArray();
        _gaps = gaps.ToArray();
        // Probes spread through the template, text and gap pixels alternating, so both bright and dark areas of the
        // viewport fail within a few reads before the full check runs.
        _probes = Enumerable.Range(0, 12).Select(i => (Pos: _text[i * (_text.Length - 1) / 11], Text: true))
            .Concat(Enumerable.Range(0, 8).Select(i => (Pos: _gaps[i * (_gaps.Length - 1) / 7], Text: false)))
            .OrderBy(p => p.Pos.X).ToArray();
        // The text-bearing columns for the partial match, in an order that spreads them over the name, so a position
        // that does not hold the tag runs out of allowed misses after a few columns.
        var columns = _text.Select(p => (p.X, p.Y, Text: true)).Concat(_gaps.Select(p => (p.X, p.Y, Text: false)))
            .GroupBy(p => p.X).Where(c => c.Any(p => p.Text))
            .Select(c => (X: c.Key, Pixels: c.Select(p => (p.Y, p.Text)).ToArray())).OrderBy(c => c.X).ToArray();
        _columns = Enumerable.Range(0, 5).SelectMany(phase => columns.Where((_, i) => i % 5 == phase)).ToArray();
        _width = tx1 - tx0 + 1;
        _height = ty1 - ty0 + 1;
        _last = _lastSeen = new CaptureRect(viewport.X + tx0, viewport.Y + ty0, _width, _height);
        return true;
    }

    /// <summary>
    /// Whether an OCR line is the name: equal ignoring case, or the same length with one character different. Vision's
    /// slips on this pixel font are substitutions (y read as u, p as n), and the template is cut from the line's box, so
    /// a line with a glyph more or less (a stray "l" from whatever stands beside the tag) is not taken.
    /// </summary>
    public static bool SameName(string read, string name)
    {
        read = read.Trim();
        if (read.Length != name.Length || name.Length < 3) return false;
        var misses = 0;
        for (var i = 0; i < read.Length; i++)
            if (char.ToLowerInvariant(read[i]) != char.ToLowerInvariant(name[i]) && ++misses > 1) return false;
        return true;
    }

    /// <summary>Finds the tag in the frame, in native coordinates. Null when not present or not yet learned.</summary>
    public NameTagHit? Locate(FrameGeometry geometry, ReadOnlySpan<byte> frame, double minTextFraction = 0.8, double minCombinedFraction = 0.9)
    {
        if (!Ready) return null;
        var viewport = geometry.Layout["world.viewport"];
        var native = geometry.ExtractNative(frame, viewport);
        int w = viewport.Width, h = viewport.Height;

        if (_last is { } last)
        {
            var hit = Search(native, w, h, Math.Max(0, last.X - viewport.X - 80), Math.Max(0, last.Y - viewport.Y - 80),
                Math.Min(w - _width, last.X - viewport.X + 80), Math.Min(h - _height, last.Y - viewport.Y + 80), minTextFraction, minCombinedFraction);
            if (hit is not null) return Remember(hit.Value, viewport);
        }
        // Partly hidden: only on a pixel-exact frame, where an unhidden tag matches every column exactly. On the blurred
        // point-size captures unhidden tags already lose up to 30% of their columns to soft edges, so the share would not
        // separate a hidden tag from no tag there, and those recordings keep the strict match alone. Tried before the
        // whole viewport, since a tag missed near where it was is most likely hidden there, not gone.
        if (_lastSeen is { } seen && OcrInput.IsPixelExact(geometry, frame))
        {
            int sx = seen.X - viewport.X, sy = seen.Y - viewport.Y;
            var partial = SearchOccluded(native, w, Math.Max(0, sx - OccludedSearchRadius), Math.Max(0, sy - OccludedSearchRadius),
                Math.Min(w - _width, sx + OccludedSearchRadius), Math.Min(h - _height, sy + OccludedSearchRadius));
            if (partial is not null) return Remember(partial.Value, viewport);
        }
        var full = Search(native, w, h, 0, 0, w - _width, h - _height, minTextFraction, minCombinedFraction);
        if (full is null) { _last = null; return null; }
        return Remember(full.Value, viewport);
    }

    private NameTagHit Remember(NameTagHit hit, HudRegion viewport)
    {
        var tag = new CaptureRect(hit.Tag.X + viewport.X, hit.Tag.Y + viewport.Y, hit.Tag.Width, hit.Tag.Height);
        _last = _lastSeen = tag;
        return new NameTagHit(tag, hit.Matched, hit.Expected);
    }

    /// <summary>The position with the most exactly matching text columns, if at least <see cref="MinExactColumns"/> of them match.</summary>
    private NameTagHit? SearchOccluded(byte[] px, int w, int x0, int y0, int x1, int y1)
    {
        var maxFailures = _columns.Length - (int)Math.Ceiling(_columns.Length * MinExactColumns);
        // The first fifth of the columns spans the whole name, and a tag that keeps 45% or more of its columns keeps a
        // similar share of those, so a position matching under a quarter of them is skipped early.
        var firstPhase = (_columns.Length + 4) / 5;
        int bestX = -1, bestY = -1, bestExact = -1;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                int exact = 0, failures = 0;
                for (var i = 0; i < _columns.Length; i++)
                {
                    if (i == firstPhase && exact * 4 < firstPhase) { failures = int.MaxValue; break; }
                    var (cx, pixels) = _columns[i];
                    var ok = true;
                    foreach (var (cy, isText) in pixels)
                    {
                        var l = Luma(px, w, x + cx, y + cy);
                        if (isText ? l <= TextLuma : l >= GapLuma) { ok = false; break; }
                    }
                    if (ok) exact++;
                    else if (++failures > maxFailures) break;
                }
                if (failures <= maxFailures && exact > bestExact) { bestExact = exact; bestX = x; bestY = y; }
            }
        if (bestX < 0) return null;
        var matched = _text.Count(p => Luma(px, w, bestX + p.X, bestY + p.Y) > TextLuma) + _gaps.Count(p => Luma(px, w, bestX + p.X, bestY + p.Y) < GapLuma);
        return new NameTagHit(new CaptureRect(bestX, bestY, _width, _height), matched, _text.Length + _gaps.Length);
    }

    private NameTagHit? Search(byte[] px, int w, int h, int x0, int y0, int x1, int y1, double minText, double minCombined)
    {
        // Text pixels are opaque and must nearly all be bright; the gaps show the world through a translucent box,
        // so they are judged together with the text as a combined fraction.
        var maxTextMisses = _text.Length - (int)Math.Ceiling(_text.Length * minText);
        var maxTotalMisses = _text.Length + _gaps.Length - (int)Math.Ceiling((_text.Length + _gaps.Length) * minCombined);
        NameTagHit? best = null;
        var bestMatched = -1;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var probeMisses = 0;
                foreach (var (pos, isText) in _probes)
                {
                    var l = Luma(px, w, x + pos.X, y + pos.Y);
                    if ((isText ? l <= TextLuma : l >= GapLuma) && ++probeMisses > 2) break;
                }
                if (probeMisses > 2) continue;
                var misses = 0;
                var ok = true;
                foreach (var (tx, ty) in _text)
                {
                    if (Luma(px, w, x + tx, y + ty) <= TextLuma && ++misses > maxTextMisses) { ok = false; break; }
                }
                if (!ok) continue;
                var gapMisses = 0;
                var maxGapMisses = maxTotalMisses - misses;
                if (maxGapMisses < 0) continue;
                foreach (var (gx, gy) in _gaps)
                {
                    if (Luma(px, w, x + gx, y + gy) >= GapLuma && ++gapMisses > maxGapMisses) { ok = false; break; }
                }
                if (!ok) continue;
                var matched = _text.Length - misses + _gaps.Length - gapMisses;
                if (matched > bestMatched)
                {
                    bestMatched = matched;
                    best = new NameTagHit(new CaptureRect(x, y, _width, _height), matched, _text.Length + _gaps.Length);
                }
            }
        return best;
    }

    private static int Luma(ReadOnlySpan<byte> px, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
    }
}
