using System.Runtime.InteropServices;
using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// What the client draws over the world that is neither a mob nor a drop: the mouse cursor, the HP bars over hit mobs and the
/// text of the EXP and loot log. Each is recognised exactly and kept out of the entities.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>The HUD region whose text, not whole box, is taken out of the foreground (see <see cref="ClearLogText"/>).</summary>
    public const string LogRegion = "exp_loot_log";

    /// <summary>
    /// The log's text is light grey to white with a black shadow, older lines fading, and a party bonus line yellow: every pixel of
    /// it is grey (channels within this many levels) or that yellow (fx-anttunnel-open's "Bonus EXP for PARTY" over a Horny Mushroom).
    /// A pixel of the log box that is in the foreground and of those colours is taken out of the foreground and masked for the frame,
    /// as the HUD is; a Red Snail walking behind the log keeps its coloured pixels.
    /// </summary>
    public const int LogTextChroma = 24;

    /// <summary>Text pixels this light (largest channel) are masked; darker grey is its shadow or the background.</summary>
    public const int LogTextLight = 0x60;

    private CaptureRect? _logBox;

    /// <summary>Foreground pixels of the log box taken out as its text, since construction.</summary>
    public long LogTextPixels { get; private set; }

    /// <summary>Takes the log's text pixels out of the foreground (the box itself stays in the world).</summary>
    private void ClearLogText(ReadOnlySpan<byte> frame, int frameWidth)
    {
        if (_logBox is not { } box) return;
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        var bits = _mask.Bits;
        int w = _fg.Width, h = _fg.Height;
        int ya = Math.Max(box.Y, vp.Y), yb = Math.Min(box.Y + box.Height, vp.Y + h), xa = Math.Max(box.X, vp.X), xb = Math.Min(box.X + box.Width, vp.X + w);
        for (var y = ya; y < yb; y++)
        {
            var row = (y - vp.Y) * w - vp.X;
            var o = (y * frameWidth + xa) * 4;
            for (var x = xa; x < xb; x++, o += 4)
            {
                int b = frame[o], g = frame[o + 1], r = frame[o + 2];
                int hi = b > g ? (b > r ? b : r) : (g > r ? g : r), lo = b < g ? (b < r ? b : r) : (g < r ? g : r);
                // Grey to white, or the yellow of a party bonus line (CCCC77, BBBB44: red and green alike, blue well under them).
                var grey = hi - lo <= LogTextChroma;
                var yellow = !grey && Math.Abs(r - g) <= LogTextChroma && b <= Math.Min(r, g) - 48 && r >= 0x60;
                if (!grey && !yellow) continue;
                var i = row + x;
                if (mask[i] != 0) { mask[i] = 0; LogTextPixels++; }
                // The light text and its black shadow (two pixels down and right, one up and left) are masked for the comparisons too,
                // as the HUD is, foreground or not: the foothold search's probes counted them against a Horny Mushroom under the log on
                // fx-anttunnel-open.
                if (!yellow && hi < LogTextLight) continue;
                for (var dy = -1; dy <= 2; dy++)
                {
                    var yy = y - vp.Y + dy;
                    if ((uint)yy >= (uint)h) continue;
                    for (var dx = -1; dx <= 2; dx++)
                    {
                        var xx = x - vp.X + dx;
                        if ((uint)xx < (uint)w) bits[yy * w + xx] = 1;
                    }
                }
            }
        }
    }


    /// <summary>The HP bar's outer width and height (black, white, black frame around four rows of fill), and the fill's width.</summary>
    public const int HpBarWidth = 52, HpBarHeight = 10, HpBarFill = 46;

    /// <summary>
    /// Finds the mob HP bars by their frame: a row of white the bar's width minus two between black ends, with the black outer
    /// row above it (the top) or below it (the bottom), every other row scanned so one of the two white rows is always seen.
    /// The fill's four rows read the current HP column by column (<see cref="HpBar"/>). The bars leave the foreground.
    /// </summary>
    private List<HpBar> FindHpBars(ReadOnlySpan<byte> frame, int frameWidth)
    {
        var bars = new List<HpBar>();
        var px = MemoryMarshal.Cast<byte, int>(frame);
        var vp = _mask.Viewport;
        int frameHeight = frame.Length / 4 / frameWidth, x1 = vp.X + vp.Width - 1;
        for (var y = vp.Y + 1; y < vp.Y + vp.Height - 1; y += 2)
        {
            var row = y * frameWidth;
            // A run of 50 white pixels holds a column every 16: those columns are looked at, and a white one's run measured both ways.
            for (var x = vp.X; x <= x1; x += 16)
            {
                if (px[row + x] != -1) continue;
                int a = x, e = x;
                while (a > vp.X && px[row + a - 1] == -1) a--;
                while (e < x1 && px[row + e + 1] == -1) e++;
                // The next column looked at is 15 past the run's end: a run starting after it still holds one.
                x = e - 1;
                if (e - a + 1 != HpBarWidth - 2) continue;
                foreach (var y0 in new[] { y - 1, y - HpBarHeight + 2 })
                    if (ReadHpBar(px, frameWidth, frameHeight, a - 1, y0) is { } bar && !bars.Any(b => b.Rect == bar.Rect)) bars.Add(bar);
            }
        }
        foreach (var b in bars)
            _fg.Clear(b.Rect.X - vp.X - 1, b.Rect.Y - vp.Y - 1, b.Rect.X + b.Rect.Width - vp.X, b.Rect.Y + b.Rect.Height - vp.Y);
        HpBarsRead += bars.Count;
        return bars;
    }


    /// <summary>HP bars read since construction.</summary>
    public int HpBarsRead { get; private set; }

    private static HpBar? ReadHpBar(ReadOnlySpan<int> px, int frameWidth, int frameHeight, int x0, int y0)
    {
        int x1 = x0 + HpBarWidth - 1, y1 = y0 + HpBarHeight - 1;
        if (x0 < 0 || y0 < 0 || x1 >= frameWidth || y1 >= frameHeight) return null;
        var pixels = new int[HpBarHeight * HpBarWidth];
        for (var y = 0; y < HpBarHeight; y++) px.Slice((y0 + y) * frameWidth + x0, HpBarWidth).CopyTo(pixels.AsSpan(y * HpBarWidth));
        int P(int x, int y) => pixels[(y - y0) * HpBarWidth + x - x0] & 0xFFFFFF;
        // The frame: black outside, white, black inside, on the rows of the fill (left and right) and on one full outer row.
        for (var y = y0 + 3; y <= y0 + 6; y++)
            if (P(x0, y) != 0 || P(x0 + 1, y) != 0xFFFFFF || P(x0 + 2, y) != 0 || P(x1 - 2, y) != 0 || P(x1 - 1, y) != 0xFFFFFF || P(x1, y) != 0) return null;
        bool Black(int y) { for (var x = x0; x <= x1; x++) if (P(x, y) != 0) return false; return true; }
        if (!Black(y0) && !Black(y1)) return null;
        // The fill, per column, from its first three rows (the fourth is a darker shade): green, red, black or covered.
        var cls = new char[HpBarFill];
        for (var i = 0; i < HpBarFill; i++)
        {
            var c = '?';
            for (var y = y0 + 3; y <= y0 + 5 && c == '?'; y++)
            {
                var v = P(x0 + 3 + i, y);
                int b = v & 0xFF, g = v >> 8 & 0xFF, r = v >> 16;
                c = g >= 0xC0 && r < 0x40 && b < 0x40 ? 'g' : r >= 0x90 && g < 0x30 && b < 0x30 ? 'r' : v == 0 ? 'k' : '?';
            }
            cls[i] = c;
        }
        var first = cls[0];
        var run = 0;
        while (run < HpBarFill && cls[run] == first) run++;
        var fill = first is 'g' or 'r' ? run : 0;
        return new HpBar(new CaptureRect(x0, y0, HpBarWidth, HpBarHeight), Math.Round((double)fill / HpBarFill, 3), fill, first switch { 'g' => "green", 'r' => "red", 'k' => "empty", _ => "covered" });
    }

    /// <summary>The HP bar's track: the mob below it, its hitbox (or blob) top within this many rows under the bar and its centre within this many columns.</summary>
    public const int HpBarBelow = 60, HpBarSideways = 40;

    /// <summary>Gives each HP bar the mob below it, and that mob its fill.</summary>
    private static List<HpBar> AttachHpBars(List<HpBar> bars, List<DetectedEntity> entities)
    {
        // The flag is a mob's: a candidate under a bar that became something else (a Cold Beam effect over the boar) does not keep it.
        for (var i = 0; i < entities.Count; i++)
            if (entities[i].HpBar && entities[i].Kind != EntityKind.Mob) entities[i] = entities[i] with { HpBar = false };
        var result = new List<HpBar>(bars.Count);
        foreach (var bar in bars)
        {
            double bx = bar.Rect.X + bar.Rect.Width / 2.0, by = bar.Rect.Y + bar.Rect.Height;
            var best = -1;
            var bestCost = double.MaxValue;
            for (var i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e.Kind != EntityKind.Mob) continue;
                var box = e.DisplayBox;
                double dx = Math.Abs(box.X + box.Width / 2.0 - bx), dy = box.Y - by;
                if (dx > HpBarSideways || dy < -12 || dy > HpBarBelow) continue;
                var cost = dx + Math.Abs(dy) + (e.Identity is null ? 30 : 0);
                if (cost < bestCost) { bestCost = cost; best = i; }
            }
            if (best >= 0)
            {
                entities[best] = entities[best] with { HpFill = bar.Fill, HpBar = true };
                result.Add(bar with { Track = entities[best].TrackId });
            }
            else result.Add(bar);
        }
        return result;
    }

    // The cursor.

    /// <summary>The client's cursor frames (data/sprites/ui/cursor), or null.</summary>
    public SpriteSet? Cursors { get; init; }

    private HashSet<int>? _cursorColours;
    private (MobSpecies State, SpriteFrame Frame, PointD Hotspot)? _cursor;

    /// <summary>A small blob is tried as the cursor when this share of its pixels show the glove's colours.</summary>
    public const double CursorPaletteShare = 0.25;

    /// <summary>A cursor match needs this share of its visible pixels exactly (it is drawn last, over everything).</summary>
    public const double CursorThreshold = 0.9;

    /// <summary>Frames the cursor was found on, at its last place and in a blob.</summary>
    public int CursorAtLast { get; private set; }
    public int CursorInBlob { get; private set; }

    /// <summary>The cursor where it was last seen (any frame of its state, then any state), its pixels out of the foreground.</summary>
    private CursorMatch? CursorAtLastPlace(ReadOnlySpan<byte> frame, int frameWidth)
    {
        if (Cursors is null || _cursor is not { } last) return null;
        int hx = (int)last.Hotspot.X, hy = (int)last.Hotspot.Y;
        _matcher.PrepareRegion(frame, frameWidth, _mask, hx - 40, hy - 40, hx + 40, hy + 40);
        foreach (var (state, f) in new[] { (last.State, last.Frame) }.Concat(last.State.Frames.Select(f => (last.State, f))).Concat(Cursors.Groups.Values.SelectMany(s => s.Frames.Select(f => (s, f)))))
            if (_matcher.At(state, f, hx - f.OriginX, hy - f.OriginY) is { } m && m.Score >= CursorThreshold)
            {
                CursorAtLast++;
                return TakeCursor(m);
            }
        _cursor = null;
        return null;
    }

    /// <summary>The cursor in a small blob holding its colours: every frame of every state placed on the blob's box.</summary>
    private CursorMatch? CursorInBlobs(List<Blob> blobs, ReadOnlySpan<byte> frame, int frameWidth)
    {
        if (Cursors is null) return null;
        // The glove's light blue, not its black outline and white, which every blob has.
        _cursorColours ??= Cursors.Groups.Values.SelectMany(s => s.Frames).SelectMany(f => f.PixelPacked).Where(c => OwnEffectsChroma(c) >= 17).ToHashSet();
        var vp = _mask.Viewport;
        for (var i = 0; i < blobs.Count; i++)
        {
            var b = blobs[i];
            if (b.Width > 40 || b.Height > 40 || b.Pixels < 20) continue;
            if (PaletteShare(frame, frameWidth, b, _cursorColours) < CursorPaletteShare) continue;
            var box = new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height);
            _matcher.Prepare(frame, frameWidth, _mask, box, null);
            foreach (var state in Cursors.Groups.Values)
                foreach (var f in state.Frames)
                    if (_matcher.Best(box, null, state, f, CursorThreshold, 3, 3) is { } m && m.Score >= CursorThreshold)
                    {
                        blobs.RemoveAt(i);
                        CursorInBlob++;
                        return TakeCursor(m);
                    }
        }
        return null;
    }

    /// <summary>Records the cursor and takes its pixels out of the foreground.</summary>
    private CursorMatch TakeCursor(SpriteMatch m)
    {
        var hotspot = m.Feet;
        _cursor = (m.Species, m.Frame, hotspot);
        _matcher.Explain(m, _fg.Mask, _fg.Width, _fg.Height, _mask.Viewport, clear: true);
        return new CursorMatch(m.Species.Name, m.Frame.Index, hotspot, new CaptureRect(m.Left + m.Frame.X0, m.Top + m.Frame.Y0, m.Frame.ExtentWidth, m.Frame.ExtentHeight), m.Score);
    }

    /// <summary>The share of a blob's foreground pixels whose colour is in a palette.</summary>
    private double PaletteShare(ReadOnlySpan<byte> frame, int frameWidth, Blob b, HashSet<int> palette)
    {
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        int w = _fg.Width, n = 0, hit = 0;
        var px = MemoryMarshal.Cast<byte, int>(frame);
        for (var y = b.Y0; y <= b.Y1; y++)
            for (var x = b.X0; x <= b.X1; x++)
            {
                if (mask[y * w + x] == 0) continue;
                n++;
                if (palette.Contains(px[(y + vp.Y) * frameWidth + x + vp.X] & 0xFFFFFF)) hit++;
            }
        return n == 0 ? 0 : (double)hit / n;
    }
}
