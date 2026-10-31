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
/// <remarks>
/// <see cref="NameRight"/>, on a collapsed bar whose buttons were found, is the column where the buttons start (the minus
/// button's left outline), so the "Region : Map" text lies left of it.
/// </remarks>
public sealed record MinimapReading(CaptureRect Panel, CaptureRect MapArea, MinimapDot? Self, IReadOnlyList<MinimapDot> Dots, bool Valid, bool Collapsed = false,
    int? NameRight = null)
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
    private const int OutlineToEdge = 5;        // from the WORLD button's dark right outline to the panel's right edge
    private const int MapInsetLeft = 6, MapInsetRight = 6, MapInsetTop = 68, MapInsetBottom = 11;
    private const int MinDotPixels = 6, MaxDotPixels = 160;
    private const int CollapsedBarHeight = 20;

    /// <summary>
    /// Rows of the collapsed bar where the WORLD button is plain orange: its caption crosses <see cref="TitleBarRow"/>
    /// there, which splits the orange run (fx-anttunnel: caption rows 7 to 12 of the button's 5 to 14).
    /// </summary>
    private static readonly int[] CollapsedButtonRows = [6, 13, 7];

    /// <summary>
    /// From the minus button's left outline to the WORLD button's right outline: 62 columns on the collapsed bar of
    /// fx-anttunnel (149 to 211) and of fx-maps (218 to 280) and in the expanded title bar of fx-anttunnel-open (162 to 224).
    /// </summary>
    private const int ButtonsWidth = 62;

    /// <summary>The collapsed bar's last column is four past the WORLD button's outline (215 for 211 on fx-anttunnel, 284 for 280 on fx-maps).</summary>
    private const int CollapsedOutlineToEdge = 4;

    public static MinimapReading Read(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var search = new HudRegion(0, "minimap.search", RegionKind.Fixed, SearchArea, "");
        var px = geometry.ViewNative(frame, search);
        var w = SearchArea.Width;

        // Corner must look like the panel: light border pixel just inside the top-left corner.
        if (Luma(px, PanelLeft + 1, PanelTop + 1) < 150) return Invalid();

        // Right edge: anchored on the orange WORLD button at the end of the title bar, which no background can
        // imitate; fall back to the first dark pixel on the top border line.
        // The button is a run of roughly 30 orange columns, then a dark edge pixel, then the light border. Cave
        // walls can be the same orange, but not with that structure. On an exact frame the run ends in the outline
        // itself (x 152..185 orange, 186 black, 187 white on Deep Valley I); the point-size capture's blur leaves the
        // first column after the run still three quarters orange and puts the darkest column one further. Both give
        // the outline, and the edge (x 191 there, the last panel column before the world) is five columns on.
        var worldOutline = FindWorldOutline(px, TitleBarRow);
        var right = worldOutline < 0 ? -1 : worldOutline + OutlineToEdge;
        if (right < 0)
            for (var x = PanelLeft + 4; x < w; x++)
                if (Luma(px, x, TopBorderRow) < 70) { right = x - 1; break; }

        // Bottom edge: walk down the left border while it still looks like the border (its blue-grey, then the
        // light bottom line); whatever lies under the panel, sky or cave, fails both tests.
        var bottom = -1;
        var misses = 0;
        for (var y = PanelTop + 3; y < SearchArea.Height; y++)
        {
            if (IsBorderBlue(px, LeftBorderColumn, y) || IsLightGrey(px, LeftBorderColumn, y)) { bottom = y; misses = 0; }
            else if (++misses > 2) break; // a resampled frame can blur one or two rows at the bottom corner
        }
        if (right < PanelLeft + 60) return Invalid();

        // Collapsed: only the title bar remains, so where the name block's blue background should be there is world.
        // On the collapsed bar the WORLD caption crosses the title bar row, so the scan above mostly fell back to the top
        // border, whose first dark pixel is the minus button's outline: the bar then read as ending where its buttons
        // start (149 of 215 columns on fx-anttunnel). The button is found again on rows above and below its caption,
        // which gives the bar's true end and where the name text must stop.
        if (!IsNameBlockBlue(px, PanelLeft + 100, PanelTop + 38) && !IsNameBlockBlue(px, PanelLeft + 60, PanelTop + 44))
        {
            // Only for the bar itself: with the name block hidden but the map shown (fx-maps around 1:02) the title bar holds
            // "MINI MAP" over the map, and the left border runs on down past the bar.
            var outline = -1;
            if (bottom <= PanelTop + CollapsedBarHeight + 2)
                foreach (var row in CollapsedButtonRows)
                    if (FindWorldOutline(px, row) is var found and >= 0) { outline = found; break; }
            if (outline - ButtonsWidth > PanelLeft + 20)
                return new MinimapReading(new CaptureRect(PanelLeft, PanelTop, outline + CollapsedOutlineToEdge - PanelLeft + 1, CollapsedBarHeight), default, null, [], true,
                    Collapsed: true, NameRight: outline - ButtonsWidth);
            return new MinimapReading(new CaptureRect(PanelLeft, PanelTop, right - PanelLeft + 1, CollapsedBarHeight), default, null, [], true, Collapsed: true);
        }

        // Bottom edge: the panel ends with a blue-grey bar, a light line and a dark shadow line, all across its
        // width. Map art under the panel, sky or cliff, never has that three-row signature.
        bottom = FindBottom(px, right, bottom);
        if (bottom < PanelTop + MapInsetTop + MapInsetBottom + 10) return Invalid();

        var panel = new CaptureRect(PanelLeft, PanelTop, right - PanelLeft + 1, bottom - PanelTop + 1);
        var map = new CaptureRect(PanelLeft + MapInsetLeft, PanelTop + MapInsetTop,
            right - MapInsetRight - (PanelLeft + MapInsetLeft) + 1, bottom - MapInsetBottom - (PanelTop + MapInsetTop) + 1);

        var dots = FindDots(px, map);
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
        var px = OcrInput.ForOcr(geometry, frame, region.Extract(frame, geometry.FrameWidth, geometry.FrameHeight), block.Width, block.Height);
        var lines = TextRecognizer.Recognize(px, block.Width, block.Height, upscale: 3);
        if (lines.Count == 0) return null;
        if (lines.Count == 1) return ("", lines[0].Text.Trim(), lines[0].Confidence);
        return (lines[0].Text.Trim(), lines[1].Text.Trim(), Math.Min(lines[0].Confidence, lines[1].Confidence));
    }

    private static MinimapReading Invalid() => new(default, default, null, [], false);

    /// <summary>
    /// The WORLD button's dark right outline on this row, or -1: a run of 24 to 44 orange columns, then the outline, then
    /// light border (see <see cref="Read"/>).
    /// </summary>
    private static int FindWorldOutline(BgraView px, int row)
    {
        var runStart = -1;
        for (var x = PanelLeft + 40; x < px.Width - 6; x++)
        {
            var orange = IsOrange(px, x, row);
            if (orange && runStart < 0) runStart = x;
            if (!orange && runStart >= 0)
            {
                var length = x - runStart;
                var outline = Luma(px, x + 1, row) < 130 && Luma(px, x + 3, row) > 140 ? x + 1
                    : Luma(px, x, row) < 130 && Luma(px, x + 2, row) > 140 ? x : -1;
                if (length is >= 24 and <= 44 && outline >= 0) return outline;
                runStart = -1;
            }
        }
        return -1;
    }

    /// <summary>The lowest row that is a light line over a dark shadow line, under a blue-grey bar, across the panel.</summary>
    private static int FindBottom(BgraView px, int right, int fallback)
    {
        int x0 = PanelLeft + 6, x1 = right - 6, span = x1 - x0 + 1;
        for (var y = PanelTop + MapInsetTop + 20; y < SearchArea.Height - 1; y++)
        {
            int light = 0, dark = 0, bar = 0;
            for (var x = x0; x <= x1; x++)
            {
                var here = Luma(px, x, y);
                // The light line is white. On an exact frame the bar's lowest light-blue row sits directly on the dark
                // blue line under it and would pass for it three rows too high; blur softened that drop too much to pass.
                if (here > 180 && !IsNameBlockBlue(px, x, y)) light++;
                // The shadow line is alpha-blended, so over a bright sky it is grey, not black: test the drop, not the level.
                if (here - Luma(px, x, y + 1) > 60) dark++;
                if (IsBorderBlue(px, x, y - 3) || IsNameBlockBlue(px, x, y - 3)) bar++;
            }
            if (light > span * 0.7 && dark > span * 0.7 && bar > span * 0.5) return y;
        }
        return fallback;
    }

    /// <summary>The name block and bottom bar, light blue-grey around (157,171,196) to (207,220,223).</summary>
    private static bool IsNameBlockBlue(BgraView px, int x, int y)
    {
        var (r, g, b) = Rgb(px, x, y);
        return b - r is >= 10 and <= 60 && g >= r && g <= b + 4 && r is >= 110 and <= 215;
    }

    private static List<MinimapDot> FindDots(BgraView px, CaptureRect map)
    {
        var kinds = new MinimapDotKind?[map.Height, map.Width];
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var o = px.Offset(map.X + x, map.Y + y);
                kinds[y, x] = Classify(px.Pixels[o + 2], px.Pixels[o + 1], px.Pixels[o]);
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

    private static int Luma(BgraView px, int x, int y)
    {
        var o = px.Offset(x, y);
        return (px.Pixels[o] + px.Pixels[o + 1] * 2 + px.Pixels[o + 2]) >> 2;
    }

    private static (int R, int G, int B) Rgb(BgraView px, int x, int y)
    {
        var o = px.Offset(x, y);
        return (px.Pixels[o + 2], px.Pixels[o + 1], px.Pixels[o]);
    }

    /// <summary>The WORLD button, about (210,155,60).</summary>
    private static bool IsOrange(BgraView px, int x, int y)
    {
        var (r, g, b) = Rgb(px, x, y);
        return r > 185 && g is >= 130 and <= 185 && b < 110 && r > g + 30;
    }

    /// <summary>The panel border, about (121,144,171) to (134,155,179).</summary>
    private static bool IsBorderBlue(BgraView px, int x, int y)
    {
        var (r, g, b) = Rgb(px, x, y);
        return b - r is >= 20 and <= 75 && g >= r && g <= b && r is >= 85 and <= 165;
    }

    /// <summary>The light lines that frame the panel, about (200..240) grey.</summary>
    private static bool IsLightGrey(BgraView px, int x, int y)
    {
        var (r, g, b) = Rgb(px, x, y);
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
    private int? _nameRight;

    public MapNameTracker(int stableFrames = 3)
    {
        // Expanded: a fixed-size block at a fixed offset inside the panel, so the hash ignores width wobble, but never
        // wider than the panel: on a narrow panel (141 wide on Rocky Road I and II) a 160-wide block reached past the
        // right edge into the world, whose scrolling kept the hash from settling, so Rocky Road II was never read.
        // Collapsed: the bar text "Region : Map" left of the buttons. Where the reader found the buttons, the text is read
        // up to them; otherwise from a generous fixed span, which also takes in the button captions and the world beyond
        // the bar (whose scrolling delays the read), and the WORLD caption is stripped afterwards.
        _tracker = new TextRegionTracker(Block, upscale: 3) { StableFrames = stableFrames };
    }

    private CaptureRect Block() => _collapsed
        ? new CaptureRect(_panel.X + 4, _panel.Y + 2, _nameRight is { } r && r - 2 > _panel.X + 24 ? r - 2 - (_panel.X + 4) : Math.Min(330, MinimapReader.SearchArea.Width - _panel.X - 4), 18)
        : new CaptureRect(_panel.X + 46, _panel.Y + 18, Math.Min(160, _panel.Width - 50), 44);

    /// <summary>The name block the last <see cref="Update"/> looked at (native frame pixels), or null before the first.</summary>
    public CaptureRect? NameBlock => _updated ? Block() : null;
    private bool _updated;

    public (string Region, string Map, double Confidence)? Current { get; private set; }

    /// <summary>
    /// Whether the catalog knows a street and map name exactly. When set, a pixel-exact frame whose softened name block
    /// (see <see cref="OcrInput"/>) reads as an unknown name is read again unsoftened, and that reading is kept if it is
    /// known: softened, Ant Tunnel IV's name block on fx-anttunnel-open reads "Ant TunnelM" at the 3x upscale used here
    /// on every keyframe, unsoftened "Ant Tunnel IV"; Deep Valley I on fx-exact reads right softened and is never re-read.
    /// The softened reading stays when neither is known, for the fuzzy lookup to work on.
    /// </summary>
    public Func<string, string, bool>? IsKnownName { get; set; }

    /// <summary>True when the last read was taken from the unsoftened crop (see <see cref="IsKnownName"/>).</summary>
    public bool LastReadUnsoftened { get; private set; }

    /// <summary>
    /// The panel widens for a long name, so a map image narrower than the panel is centred in it. From the OCR boxes of
    /// the expanded name block, the right end of the name text lies 5 or 6 pixels inside the panel's right edge on every
    /// panel widened for its name on fx-maps (The Tree That Grew I, East Domain of Perion, East Rocky Mountain I, The
    /// Forest North of Ellinia, The Field Up North of Ellinia), 8 to 11 on Rocky Road (image and name about as wide), and
    /// 53 or more where the image sets the width (Deep Valley I, Perion, Ellinia, Perion Street Corner, Ant Tunnel IV).
    /// </summary>
    public const int NameWidensPanelMargin = 24;

    /// <summary>
    /// After a read of the expanded panel, whether the name text reaches within <see cref="NameWidensPanelMargin"/> of
    /// the panel's right edge, so that the panel may be as wide as it is for its name rather than its map; null when the
    /// minimap was collapsed or nothing was read. Used to tell apart the maps a misread name may be.
    /// </summary>
    public bool? NameWidensPanel { get; private set; }

    /// <summary>Returns true when the names were re-read this frame.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel) => Update(geometry, frame, panel, collapsed: false);

    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, MinimapReading minimap) => Update(geometry, frame, minimap.Panel, minimap.Collapsed, minimap.NameRight);

    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame, CaptureRect panel, bool collapsed, int? nameRight = null)
    {
        _panel = panel;
        _collapsed = collapsed;
        _nameRight = collapsed ? nameRight : null;
        _updated = true;
        if (!_tracker.Update(geometry, frame)) return false;
        var lines = _tracker.Lines!;
        Current = Parse(lines, collapsed);
        LastReadUnsoftened = false;
        if (IsKnownName is { } known && _tracker.LastReadExact && !(Current is { } read && known(read.Region, read.Map))
            && _tracker.ReadUnsoftened(geometry, frame) is var rawLines && Parse(rawLines, collapsed) is { } raw && known(raw.Region, raw.Map))
        {
            Current = raw;
            lines = rawLines;
            LastReadUnsoftened = true;
        }
        var plausible = lines.Where(Plausible).ToList();
        // The block starts 46 columns into the panel; OCR boxes are in frame pixels of the block.
        NameWidensPanel = collapsed || plausible.Count == 0 ? null
            : panel.Width - (46 + plausible.Max(l => l.X + l.Width) / geometry.Scale) <= NameWidensPanelMargin;
        return true;
    }

    private static (string Region, string Map, double Confidence)? Parse(IReadOnlyList<TextLine> all, bool collapsed)
    {
        var lines = all.Where(Plausible).ToList();
        if (lines.Count == 0) return null;
        if (collapsed)
        {
            var text = StripWorldCaption(string.Join(" ", lines.Select(l => l.Text.Trim())));
            var colon = text.IndexOf(':');
            return colon < 0 ? ("", Normalize(text), lines.Min(l => l.Confidence))
                : (Normalize(text[..colon]), Normalize(text[(colon + 1)..]), lines.Min(l => l.Confidence));
        }
        return lines.Count == 1 ? ("", Normalize(lines[0].Text), lines[0].Confidence)
            : (Normalize(lines[0].Text), Normalize(lines[1].Text), Math.Min(lines[0].Confidence, lines[1].Confidence));
    }

    /// <summary>
    /// Removes the WORLD button's caption from the end of collapsed-bar text read past the buttons. OCR does not always
    /// read it whole: on fx-anttunnel's exact frames, softened, it came back as "WORLD", "WORLO", "WORLE", "WORLI" and
    /// "WORL". So the text is cut at the first word, after the first, that is a prefix of WORLD of three or more letters
    /// or one letter off it, with everything after it; and such a caption glued to the map's numeral ("IVWORL") is cut
    /// back to the numeral.
    /// </summary>
    public static string StripWorldCaption(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < words.Length; i++)
        {
            if (LooksLikeWorld(words[i])) return string.Join(' ', words[..i]);
            var glued = System.Text.RegularExpressions.Regex.Match(words[i], @"^([IVX]+)(W\w{2,4})$");
            if (glued.Success && LooksLikeWorld(glued.Groups[2].Value)) return string.Join(' ', words[..i].Append(glued.Groups[1].Value));
        }
        return text.Trim();
    }

    private static bool LooksLikeWorld(string word)
    {
        var w = word.ToUpperInvariant();
        if (w.Length < 3 || w.Length > 6 || !w.All(char.IsLetterOrDigit)) return false;
        if (w.Length < 5 && "WORLD".StartsWith(w, StringComparison.Ordinal)) return true;
        // One substitution, insertion or deletion away from WORLD.
        int i = 0, j = 0, edits = 0;
        const string target = "WORLD";
        while (i < w.Length && j < target.Length)
        {
            if (w[i] == target[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (w.Length > target.Length) i++;
            else if (w.Length < target.Length) j++;
            else { i++; j++; }
        }
        return edits + (w.Length - i) + (target.Length - j) <= 1 && w[0] == 'W';
    }

    /// <summary>A line worth keeping: confident, at least two letters, and not the panel's own "mini map" caption.</summary>
    private static bool Plausible(TextLine line) =>
        line.Confidence >= 0.5 && line.Text.Count(char.IsLetter) >= 2 && !line.Text.Contains("mini map", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Map names end in roman numerals, which OCR confuses with lower-case L, the digit one, the exclamation mark and T
    /// ("Rocky Road IIl", "Rocky Road IT", "Deep Valley !" on exact frames). A trailing token made only of I, V, X, l, 1,
    /// |, ! and T is rewritten as a numeral.
    /// </summary>
    public static string Normalize(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.LastIndexOf(' ');
        if (space < 0) return trimmed;
        var last = trimmed[(space + 1)..];
        if (last.Length == 0 || !last.All(c => "IVXl1|!T".Contains(c)) || !last.Any(c => "IVXl1|!".Contains(c))) return trimmed;
        return trimmed[..(space + 1)] + last.Replace('l', 'I').Replace('1', 'I').Replace('|', 'I').Replace('!', 'I').Replace('T', 'I');
    }
}
