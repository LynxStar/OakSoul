using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

public enum MinimapDotKind { Self, Player, Portal, Npc, Other }

/// <summary>A marker on the minimap. Coordinates are the blob's centre in native frame pixels.</summary>
public sealed record MinimapDot(double X, double Y, int Pixels, MinimapDotKind Kind)
{
    public override string ToString() => $"{Kind} at {X:0.#},{Y:0.#} ({Pixels}px)";
}

/// <summary>
/// What the minimap shows in one frame: the panel as drawn (it resizes per map), the map image inside it, the
/// player's own dot, and every other marker. <see cref="SelfInMap"/> is the own dot relative to the map image,
/// which is what later converts to world coordinates through the server's minimap metadata.
/// </summary>
public sealed record MinimapReading(CaptureRect Panel, CaptureRect MapArea, MinimapDot? Self, IReadOnlyList<MinimapDot> Dots, bool Valid, bool Collapsed = false)
{
    public (double X, double Y)? SelfInMap => Self is { } s ? (s.X - MapArea.X, s.Y - MapArea.Y) : null;
    public int Players => Dots.Count(d => d.Kind == MinimapDotKind.Player);
    public int Portals => Dots.Count(d => d.Kind == MinimapDotKind.Portal);

    public override string ToString() => !Valid ? "minimap not found" : Collapsed ? $"collapsed bar {Panel}"
        : $"panel {Panel} map {MapArea} self {(SelfInMap is { } p ? $"{p.X:0.#},{p.Y:0.#}" : "none")} players {Players} portals {Portals}"
;
}

/// <summary>
/// Reads the minimap panel. The panel's top-left corner is fixed but its size follows the map, so the right
/// and bottom edges are found by scanning: along the title bar for the right edge, down the left border for
/// the bottom. The map image sits at fixed insets inside. Dots are colour blobs inside the map image.
/// </summary>
public static class MinimapReader
{
    /// <summary>Native-space area searched for the panel; large enough for the biggest minimap seen so far.</summary>
    public static readonly CaptureRect SearchArea = new(0, 0, 480, 360);

    private const int PanelLeft = 1, PanelTop = 2;
    private const int TopBorderRow = 4;         // the light border line above the title bar: no buttons cross it
    private const int LeftBorderColumn = 2;     // x of the panel's left border, light or blue all the way down
    private const int TitleBarRow = 9;          // a row through the title bar buttons; the WORLD button is orange
    private const int WorldButtonToEdge = 7;    // from the WORLD button's last orange column to the panel's right edge
    private const int MapInsetLeft = 6, MapInsetRight = 6, MapInsetTop = 68, MapInsetBottom = 11;
    private const int MinDotPixels = 6, MaxDotPixels = 160;
    private const int CollapsedBarHeight = 20;

    public static MinimapReading Read(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var search = new HudRegion(0, "minimap.search", RegionKind.Fixed, SearchArea, "");
        var px = geometry.ExtractNative(frame, search);
        var w = SearchArea.Width;

        // Corner must look like the panel: light border pixel just inside the top-left corner.
        if (Luma(px, w, PanelLeft + 1, PanelTop + 1) < 150) return Invalid();

        // Right edge: anchored on the orange WORLD button at the end of the title bar, which no background can
        // imitate; fall back to the first dark pixel on the top border line.
        // The button is a run of roughly 30 orange columns, then a dark edge pixel, then the light border. Cave
        // walls can be the same orange, but not with that structure.
        var right = -1;
        var runStart = -1;
        for (var x = PanelLeft + 40; x < w - 6 && right < 0; x++)
        {
            var orange = IsOrange(px, w, x, TitleBarRow);
            if (orange && runStart < 0) runStart = x;
            if (!orange && runStart >= 0)
            {
                var length = x - runStart;
                if (length is >= 24 and <= 44 && Luma(px, w, x + 1, TitleBarRow) < 130 && Luma(px, w, x + 3, TitleBarRow) > 140)
                    right = x - 1 + WorldButtonToEdge;
                runStart = -1;
            }
        }
        if (right < 0)
            for (var x = PanelLeft + 4; x < w; x++)
                if (Luma(px, w, x, TopBorderRow) < 70) { right = x - 1; break; }

        // Bottom edge: walk down the left border while it still looks like the border (its blue-grey, then the
        // light bottom line); whatever lies under the panel, sky or cave, fails both tests.
        var bottom = -1;
        var misses = 0;
        for (var y = PanelTop + 3; y < SearchArea.Height; y++)
        {
            if (IsBorderBlue(px, w, LeftBorderColumn, y) || IsLightGrey(px, w, LeftBorderColumn, y)) { bottom = y; misses = 0; }
            else if (++misses > 2) break; // a resampled frame can blur one or two rows at the bottom corner
        }
        if (right < PanelLeft + 60) return Invalid();

        // Collapsed: only the title bar remains, so where the name block's blue background should be there is world.
        if (!IsNameBlockBlue(px, w, PanelLeft + 100, PanelTop + 38) && !IsNameBlockBlue(px, w, PanelLeft + 60, PanelTop + 44))
            return new MinimapReading(new CaptureRect(PanelLeft, PanelTop, right - PanelLeft + 1, CollapsedBarHeight), default, null, [], true, Collapsed: true);

        // Bottom edge: the panel ends with a blue-grey bar, a light line and a dark shadow line, all across its
        // width. Map art under the panel, sky or cliff, never has that three-row signature.
        bottom = FindBottom(px, w, right, bottom);
        if (bottom < PanelTop + MapInsetTop + MapInsetBottom + 10) return Invalid();

        var panel = new CaptureRect(PanelLeft, PanelTop, right - PanelLeft + 1, bottom - PanelTop + 1);
        var map = new CaptureRect(PanelLeft + MapInsetLeft, PanelTop + MapInsetTop,
            right - MapInsetRight - (PanelLeft + MapInsetLeft) + 1, bottom - MapInsetBottom - (PanelTop + MapInsetTop) + 1);

        var dots = FindDots(px, w, map);
        var self = dots.Where(d => d.Kind == MinimapDotKind.Self).OrderByDescending(d => d.Pixels).FirstOrDefault();
        return new MinimapReading(panel, map, self, dots, true);
    }

    /// <summary>The name block to the right of the map icon: two lines, region name over map name.</summary>
    public static CaptureRect NameBlock(CaptureRect panel) => new(panel.X + 46, panel.Y + 18, panel.Width - 50, 48);

    /// <summary>
    /// Reads the region and map names with Vision OCR. The block is taken at the frame's own resolution, since a
    /// scaled frame has more pixels than native and OCR only gets worse by resampling down. Call only when the
    /// name block changed; see <see cref="MapNameTracker"/>.
    /// </summary>
    public static (string Region, string Map, double Confidence)? ReadMapName(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel)
    {
        var block = geometry.ToFrame(NameBlock(panel));
        var region = new HudRegion(0, "minimap.name_block", RegionKind.Fixed, block, "");
        var px = region.Extract(frame, geometry.FrameWidth, geometry.FrameHeight);
        var lines = TextRecognizer.Recognize(px, block.Width, block.Height, upscale: 3);
        if (lines.Count == 0) return null;
        if (lines.Count == 1) return ("", lines[0].Text.Trim(), lines[0].Confidence);
        return (lines[0].Text.Trim(), lines[1].Text.Trim(), Math.Min(lines[0].Confidence, lines[1].Confidence));
    }

    private static MinimapReading Invalid() => new(default, default, null, [], false);

    /// <summary>The lowest row that is a light line over a dark shadow line, under a blue-grey bar, across the panel.</summary>
    private static int FindBottom(byte[] px, int w, int right, int fallback)
    {
        int x0 = PanelLeft + 6, x1 = right - 6, span = x1 - x0 + 1;
        for (var y = PanelTop + MapInsetTop + 20; y < SearchArea.Height - 1; y++)
        {
            int light = 0, dark = 0, bar = 0;
            for (var x = x0; x <= x1; x++)
            {
                var here = Luma(px, w, x, y);
                if (here > 180) light++;
                // The shadow line is alpha-blended, so over a bright sky it is grey, not black: test the drop, not the level.
                if (here - Luma(px, w, x, y + 1) > 60) dark++;
                if (IsBorderBlue(px, w, x, y - 3) || IsNameBlockBlue(px, w, x, y - 3)) bar++;
            }
            if (light > span * 0.7 && dark > span * 0.7 && bar > span * 0.5) return y;
        }
        return fallback;
    }

    /// <summary>The name block and bottom bar, light blue-grey around (157,171,196) to (207,220,223).</summary>
    private static bool IsNameBlockBlue(byte[] px, int w, int x, int y)
    {
        var (r, g, b) = Rgb(px, w, x, y);
        return b - r is >= 10 and <= 60 && g >= r && g <= b + 4 && r is >= 110 and <= 215;
    }

    private static List<MinimapDot> FindDots(byte[] px, int w, CaptureRect map)
    {
        var kinds = new MinimapDotKind?[map.Height, map.Width];
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var o = ((map.Y + y) * w + map.X + x) * 4;
                kinds[y, x] = Classify(px[o + 2], px[o + 1], px[o]);
            }

        var seen = new bool[map.Height, map.Width];
        var dots = new List<MinimapDot>();
        var stack = new Stack<(int X, int Y)>();
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                if (seen[y, x] || kinds[y, x] is not { } kind) continue;
                int count = 0; double sx = 0, sy = 0;
                stack.Push((x, y));
                seen[y, x] = true;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    count++; sx += cx; sy += cy;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height || seen[ny, nx] || kinds[ny, nx] != kind) continue;
                            seen[ny, nx] = true;
                            stack.Push((nx, ny));
                        }
                }
                if (count >= MinDotPixels && count <= MaxDotPixels)
                    dots.Add(new MinimapDot(map.X + sx / count + 0.5, map.Y + sy / count + 0.5, count, kind));
            }
        return dots;
    }

    private static MinimapDotKind? Classify(int r, int g, int b)
    {
        if (r > 200 && g > 190 && b < 110) return MinimapDotKind.Self;      // yellow, about (218,207,77)
        if (r > 170 && g < 90 && b < 90) return MinimapDotKind.Player;      // red, about (230,69,55)
        if (b > 150 && b > r + 40 && g > 120) return MinimapDotKind.Portal; // teal, about (99,152,174)
        if (g > 150 && g > r + 40 && g > b + 40) return MinimapDotKind.Npc; // green
        return null;
    }

    private static int Luma(byte[] px, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
    }

    private static (int R, int G, int B) Rgb(byte[] px, int w, int x, int y)
    {
        var o = (y * w + x) * 4;
        return (px[o + 2], px[o + 1], px[o]);
    }

    /// <summary>The WORLD button, about (210,155,60).</summary>
    private static bool IsOrange(byte[] px, int w, int x, int y)
    {
        var (r, g, b) = Rgb(px, w, x, y);
        return r > 185 && g is >= 130 and <= 185 && b < 110 && r > g + 30;
    }

    /// <summary>The panel border, about (121,144,171) to (134,155,179).</summary>
    private static bool IsBorderBlue(byte[] px, int w, int x, int y)
    {
        var (r, g, b) = Rgb(px, w, x, y);
        return b - r is >= 20 and <= 75 && g >= r && g <= b && r is >= 85 and <= 165;
    }

    /// <summary>The light lines that frame the panel, about (200..240) grey.</summary>
    private static bool IsLightGrey(byte[] px, int w, int x, int y)
    {
        var (r, g, b) = Rgb(px, w, x, y);
        return Math.Min(r, Math.Min(g, b)) > 165 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40;
    }
}

/// <summary>
/// Region and map names from the minimap panel, read through hash-gated OCR so the cost is paid on map change.
/// </summary>
public sealed class MapNameTracker
{
    private readonly TextRegionTracker _tracker;
    private CaptureRect _panel;

    private bool _collapsed;

    public MapNameTracker(int stableFrames = 3)
    {
        // Expanded: a fixed-size block at a fixed offset inside the panel, so the hash ignores width wobble.
        // Collapsed: the bar text "Region : Map" left of the buttons.
        // The collapsed bar's width is not reliably known, so its text is read from a generous fixed span and the
        // button captions are stripped afterwards.
        _tracker = new TextRegionTracker(() => _collapsed
            ? new CaptureRect(_panel.X + 4, _panel.Y + 2, Math.Min(330, MinimapReader.SearchArea.Width - _panel.X - 4), 18)
            : new CaptureRect(_panel.X + 46, _panel.Y + 18, 160, 44), upscale: 3) { StableFrames = stableFrames };
    }

    public (string Region, string Map, double Confidence)? Current { get; private set; }

    /// <summary>Returns true when the names were re-read this frame.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel) => Update(geometry, frame, panel, collapsed: false);

    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, MinimapReading minimap) => Update(geometry, frame, minimap.Panel, minimap.Collapsed);

    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel, bool collapsed)
    {
        _panel = panel;
        _collapsed = collapsed;
        if (!_tracker.Update(geometry, frame)) return false;
        var lines = _tracker.Lines!.Where(Plausible).ToList();
        if (collapsed)
        {
            var text = string.Join(" ", lines.Select(l => l.Text.Trim()));
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\bWORLD\b.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var colon = text.IndexOf(':');
            Current = lines.Count == 0 ? null
                : colon < 0 ? ("", Normalize(text), lines.Min(l => l.Confidence))
                : (Normalize(text[..colon]), Normalize(text[(colon + 1)..]), lines.Min(l => l.Confidence));
            return true;
        }
        Current = lines.Count switch
        {
            0 => null,
            1 => ("", Normalize(lines[0].Text), lines[0].Confidence),
            _ => (Normalize(lines[0].Text), Normalize(lines[1].Text), Math.Min(lines[0].Confidence, lines[1].Confidence)),
        };
        return true;
    }

    /// <summary>A line worth keeping: confident, at least two letters, and not the panel's own "mini map" caption.</summary>
    private static bool Plausible(TextLine line) =>
        line.Confidence >= 0.5 && line.Text.Count(char.IsLetter) >= 2 && !line.Text.Contains("mini map", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Map names end in roman numerals, which OCR confuses with lower-case L, the digit one and T ("Rocky Road IIl",
    /// "Rocky Road IT"). A trailing token made only of I, V, X, l, 1, | and T is rewritten as a numeral.
    /// </summary>
    public static string Normalize(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space < 0) return trimmed;
        var last = trimmed[(space + 1)..];
        if (last.Length == 0 || !last.All(c => "IVXl1|T".Contains(c)) || !last.Any(c => "IVXl1|".Contains(c))) return trimmed;
        return trimmed[..(space + 1)] + last.Replace('l', 'I').Replace('1', 'I').Replace('|', 'I').Replace('T', 'I');
    }
}
