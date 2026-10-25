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
public sealed record MinimapReading(CaptureRect Panel, CaptureRect MapArea, MinimapDot? Self, IReadOnlyList<MinimapDot> Dots, bool Valid)
{
    public (double X, double Y)? SelfInMap => Self is { } s ? (s.X - MapArea.X, s.Y - MapArea.Y) : null;
    public int Players => Dots.Count(d => d.Kind == MinimapDotKind.Player);
    public int Portals => Dots.Count(d => d.Kind == MinimapDotKind.Portal);

    public override string ToString() => Valid
        ? $"panel {Panel} map {MapArea} self {(SelfInMap is { } p ? $"{p.X:0.#},{p.Y:0.#}" : "none")} players {Players} portals {Portals}"
        : "minimap not found";
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
        if (right < PanelLeft + 60 || bottom < PanelTop + MapInsetTop + MapInsetBottom + 10) return Invalid();

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
/// Runs map-name OCR only when the name block's pixels change, which happens on map change and almost never
/// otherwise, so per-frame cost is a hash of a few kilobytes.
/// </summary>
public sealed class MapNameTracker
{
    private ulong _lastHash, _candidateHash;
    private int _candidateFrames;
    private (string Region, string Map, double Confidence)? _last;

    /// <summary>Frames the name block must hold a new appearance before it is read; rides out transitions and noise.</summary>
    public int StableFrames { get; init; } = 3;

    public (string Region, string Map, double Confidence)? Current => _last;

    /// <summary>Returns the current names, re-reading them if the name block changed. True when a new read happened.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel)
    {
        // A fixed-size block at a fixed offset, coarsely quantised, so a one-pixel wobble in the detected panel
        // width or resampling noise does not look like a change.
        var block = new CaptureRect(panel.X + 46, panel.Y + 18, 160, 44);
        var px = geometry.ExtractNative(frame, new HudRegion(0, "minimap.name_block", RegionKind.Fixed, block, ""));
        var hash = Hash(px);
        if (hash == _lastHash && _last is not null) return false;
        if (hash == _candidateHash) _candidateFrames++;
        else { _candidateHash = hash; _candidateFrames = 1; }
        if (_candidateFrames < StableFrames) return false;
        _lastHash = hash;
        _last = MinimapReader.ReadMapName(geometry, frame, panel);
        return true;
    }

    private static ulong Hash(byte[] bgra)
    {
        var h = 14695981039346656037UL;
        for (var o = 0; o + 3 < bgra.Length; o += 4)
        {
            var luma = (bgra[o] + bgra[o + 1] * 2 + bgra[o + 2]) >> 6; // 16 levels
            h ^= (byte)luma;
            h *= 1099511628211UL;
        }
        return h;
    }
}
