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
/// run time from a frame where OCR sees the name on its own, so no per-character asset is needed.
/// </summary>
public sealed class NameTagLocator
{
    public const int TextLuma = 140, GapLuma = 130;

    private readonly string _name;
    private (int X, int Y)[] _text = [];
    private (int X, int Y)[] _gaps = [];
    private ((int X, int Y) Pos, bool Text)[] _probes = [];
    private int _width, _height;
    private CaptureRect? _last;

    public NameTagLocator(string name) => _name = name;

    public bool Ready => _text.Length > 0;
    public string Name => _name;
    public int TemplateWidth => _width;
    public int TemplateHeight => _height;

    /// <summary>
    /// Tries to learn the template from this frame: OCR of the viewport must return the name as a line of its own.
    /// Returns true when the template was learned.
    /// </summary>
    public bool TryLearn(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var viewport = geometry.Layout["world.viewport"];
        var native = geometry.ExtractNative(frame, viewport);
        var lines = TextRecognizer.Recognize(native, viewport.Width, viewport.Height, upscale: 2);
        var own = lines.Where(l => string.Equals(l.Text.Trim(), _name, StringComparison.OrdinalIgnoreCase)).ToList();
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
        _width = tx1 - tx0 + 1;
        _height = ty1 - ty0 + 1;
        _last = new CaptureRect(viewport.X + tx0, viewport.Y + ty0, _width, _height);
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
        var full = Search(native, w, h, 0, 0, w - _width, h - _height, minTextFraction, minCombinedFraction);
        if (full is null) { _last = null; return null; }
        return Remember(full.Value, viewport);
    }

    private NameTagHit Remember(NameTagHit hit, HudRegion viewport)
    {
        var tag = new CaptureRect(hit.Tag.X + viewport.X, hit.Tag.Y + viewport.Y, hit.Tag.Width, hit.Tag.Height);
        _last = tag;
        return new NameTagHit(tag, hit.Matched, hit.Expected);
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
