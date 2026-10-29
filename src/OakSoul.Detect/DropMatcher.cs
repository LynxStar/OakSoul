using System.Text.Json;
using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>A drop identified by its icon: the item id ("09000000" to "09000003" are meso bags), the icon frame, and the match.</summary>
public sealed record DropMatch(string ItemId, string Category, int IconFrame, int Left, int Top, int Matches, int Compared)
{
    public double Score => Compared == 0 ? 0 : (double)Matches / Compared;
    public bool IsMeso => ItemId.StartsWith("0900000", StringComparison.Ordinal);

    /// <summary>A meso coin's anchor on screen (the client's origin of its spinning frames, the same for every frame), when known.</summary>
    public World.PointD? Anchor { get; init; }

    /// <summary>The anchor in world coordinates (the camera moves between frames; the coin lies still).</summary>
    public World.PointD? AnchorWorld { get; init; }

    /// <summary>An item's icon's top-left in world coordinates (the item lies still), for following it (<see cref="EntityDetector"/>'s drops).</summary>
    public World.PointD? PlaceWorld { get; init; }
    public override string ToString() => $"{(IsMeso ? "meso" : Category)} {ItemId}#{IconFrame} {Score:0.00}";
}

/// <summary>
/// Identifies drops lying on the ground by their <c>iconRaw</c> sprites, which the client draws 1:1: the meso bags'
/// spinning frames (<c>item/special/0900.img/0900000N/iconRaw/K</c>) and every item icon exported by
/// <c>OakSoul.SpriteExport --items</c> (equipment is not exported, so equipment drops stay unidentified). Because the
/// icon is drawn unscaled, the box must match the icon's extent to a few pixels, and the icon's opaque pixels are
/// compared with the screen pixel for pixel, as for mobs (<see cref="SpriteMatcher"/>, without the freeze).
/// </summary>
public sealed class DropMatcher
{
    /// <summary>Largest icon extent considered a ground drop (the meso bags are at most 33x31, item icons 32x32 with a few larger).</summary>
    public const int MaxIcon = 40;

    /// <summary>Extent difference allowed between the candidate box and the icon (the open and the foreground threshold eat an anti-aliased edge).</summary>
    public const int SizeSlack = 3;

    /// <summary>Search window: the box aligns the icon to a pixel or two, and drops bob up and down a little.</summary>
    public const int SearchX = 2, SearchY = 3;

    /// <summary>
    /// Fraction of an icon's visible opaque pixels that must equal the screen, for any icon. Stricter than for mobs: an
    /// icon is small, static and never tinted, and many icons share a size, so only a near-complete agreement names one.
    /// On fx-exact the one drop lying on the ground (item 04000004) matched at 1.00 on all four keyframes it was tried
    /// on, while no other small candidate on a surface of either short exact fixture reached 0.20 against any of the
    /// 1022 icons.
    /// </summary>
    public const double Threshold = 0.8;

    /// <summary>Icons with fewer opaque pixels than this are not loaded: too little to name an item (three of the exported icons).</summary>
    public const int MinIconPixels = 100;

    private readonly List<(string Id, string Category, MobSpecies Holder, SpriteFrame Frame)> _icons = [];

    /// <summary>Per icon (same order as the icons), the colours of its opaque pixels, for the cut-box pass.</summary>
    private readonly List<HashSet<int>> _colours = [];

    /// <summary>
    /// A drop's box may be smaller than its icon by up to this much: lying on background the mosaic cannot vouch for, or
    /// beside another drop, it loses columns (on fx-combat-exact's frames 721 to 751 the Leaf, 04000011, showed as a 23x21
    /// blob of its 27x23 icon). When no icon fits the box, a second pass tries the icons up to this much larger that hold
    /// most of the box's commonest colours, over a window that covers the shift a cut causes.
    /// </summary>
    public const int CutSlack = 8, CutSearchX = 5, CutSearchY = 4, CutColours = 6, CutColoursHeld = 4;
    private readonly SpriteMatcher _matcher = new() { AllowFreeze = false };

    /// <summary>The map's mosaic and the frame's camera (see <see cref="SpriteMatcher.Background"/>), set per frame by the detector.</summary>
    public (BackgroundMosaic Mosaic, int CameraX, int CameraY)? Background { get => _matcher.Background; set => _matcher.Background = value; }

    public int Count => _icons.Count;

    public int MesoIcons => _icons.Count(i => i.Id.StartsWith("0900000", StringComparison.Ordinal));

    /// <summary>Loads the icons listed in <c>manifest.json</c> under <paramref name="spritesDir"/> (data/sprites), or returns null when there is none.</summary>
    public static DropMatcher? Load(string spritesDir)
    {
        var manifest = Path.Combine(spritesDir, "manifest.json");
        if (!File.Exists(manifest)) return null;
        var matcher = new DropMatcher();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var category = e.GetProperty("category").GetString() ?? "";
            var node = e.GetProperty("node").GetString() ?? "";
            // The coins' frames with their origins (item/meso) are the coin pass's; their icons are under item/special already.
            if (!category.StartsWith("item/", StringComparison.Ordinal) || category == "item/meso" || !node.Contains("iconRaw", StringComparison.Ordinal)) continue;
            if (e.GetProperty("width").GetInt32() > MaxIcon || e.GetProperty("height").GetInt32() > MaxIcon) continue;
            // "0400.img/04000000/info/iconRaw", "0900.img/09000000/iconRaw/2", "5000000.img/info/iconRaw".
            var parts = node.Split('/');
            var id = parts.Length > 1 && parts[1] is not ("info" or "iconRaw") ? parts[1] : parts[0].Replace(".img", "");
            if (!id.All(char.IsDigit)) continue;
            var frameIndex = parts[^2] == "iconRaw" && int.TryParse(parts[^1], out var k) ? k : 0;
            var file = Path.Combine(spritesDir, e.GetProperty("file").GetString() ?? "");
            if (!File.Exists(file)) continue;
            var (bgra, w, h) = Png.ReadBgra(file);
            var frame = SpriteFrame.Create("iconRaw", frameIndex, -1, bgra, w, h, w / 2, h, 0);
            if (frame.PixelX.Length < MinIconPixels) continue;
            var holder = new MobSpecies { Id = 0, Name = id };
            matcher._icons.Add((id, category, holder, frame));
        }
        var ordered = matcher._icons.OrderBy(i => i.Id.StartsWith("0900000", StringComparison.Ordinal) ? 0 : 1).ToList();
        matcher._icons.Clear();
        matcher._icons.AddRange(ordered);
        foreach (var icon in matcher._icons) matcher._colours.Add(icon.Frame.PixelPacked.ToHashSet());
        return matcher;
    }

    /// <summary>When true, <see cref="Diagnose"/> records every tried candidate's best icon score (for the report).</summary>
    public List<(long Frame, CaptureRect Box, double Score, string ItemId)> Scores { get; } = [];

    /// <summary>The best score of any icon on a candidate, ignoring the threshold (report only).</summary>
    public void Diagnose(long sequence, ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box)
    {
        if (box.Width > MaxIcon + SizeSlack || box.Height > MaxIcon + SizeSlack) return;
        var best = (Score: 0.0, Id: "");
        var prepared = false;
        foreach (var (id, _, holder, f) in _icons)
        {
            if (Math.Abs(f.ExtentWidth - box.Width) > SizeSlack || Math.Abs(f.ExtentHeight - box.Height) > SizeSlack) continue;
            if (!prepared) { _matcher.Prepare(frame, frameWidth, mask, box, null); prepared = true; }
            if (_matcher.Best(box, null, holder, f, 0, SearchX, SearchY) is { } m && m.Score > best.Score) best = (m.Score, id);
        }
        if (prepared) Scores.Add((sequence, box, best.Score, best.Id));
    }

    /// <summary>The icon a small candidate box shows, or null. Meso bags are tried first (the common drop).</summary>
    public DropMatch? Match(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box)
    {
        if (box.Width > MaxIcon + SizeSlack || box.Height > MaxIcon + SizeSlack) return null;
        DropMatch? best = null;
        var prepared = false;
        foreach (var (id, category, holder, f) in _icons)
        {
            if (Math.Abs(f.ExtentWidth - box.Width) > SizeSlack || Math.Abs(f.ExtentHeight - box.Height) > SizeSlack) continue;
            if (!prepared) { _matcher.Prepare(frame, frameWidth, mask, box, null); prepared = true; }
            if (_matcher.Best(box, null, holder, f, Threshold, SearchX, SearchY) is not { } m || m.Score < Threshold) continue;
            if (best is null || m.Score > best.Score) best = new DropMatch(id, category, f.Index, m.Left, m.Top, m.Matches, m.Compared);
            if (best.Score >= 0.99) break;
        }
        return best ?? MatchCut(frame, frameWidth, mask, box, prepared);
    }

    /// <summary>The cut-box pass (see <see cref="CutSlack"/>).</summary>
    private DropMatch? MatchCut(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box, bool prepared)
    {
        // The box's commonest colours (its background included; an icon holding most of them is worth scoring).
        var counts = new Dictionary<int, int>();
        for (var y = box.Y; y < box.Y + box.Height; y++)
            for (var x = box.X; x < box.X + box.Width; x++)
            {
                if ((uint)x >= (uint)frameWidth || y < 0 || (y + 1) * frameWidth * 4 > frame.Length) continue;
                var o = (y * frameWidth + x) * 4;
                var c = frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16;
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        var common = counts.OrderByDescending(c => c.Value).Take(CutColours).Select(c => c.Key).ToList();
        DropMatch? best = null;
        for (var i = 0; i < _icons.Count; i++)
        {
            var (id, category, holder, f) = _icons[i];
            int dw = f.ExtentWidth - box.Width, dh = f.ExtentHeight - box.Height;
            if (dw > CutSlack || dh > CutSlack || dw < -SizeSlack || dh < -SizeSlack || (dw <= SizeSlack && dh <= SizeSlack && dw >= -SizeSlack && dh >= -SizeSlack)) continue;
            var held = 0;
            foreach (var c in common) if (_colours[i].Contains(c)) held++;
            if (held < Math.Min(CutColoursHeld, common.Count)) continue;
            if (!prepared) { _matcher.Prepare(frame, frameWidth, mask, box, null); prepared = true; }
            CutTried++;
            if (_matcher.Best(box, null, holder, f, Threshold, CutSearchX, CutSearchY) is not { } m || m.Score < Threshold) continue;
            if (best is null || m.Score > best.Score) best = new DropMatch(id, category, f.Index, m.Left, m.Top, m.Matches, m.Compared);
        }
        return best;
    }

    /// <summary>Icons scored by the cut-box pass since construction.</summary>
    public long CutTried { get; private set; }

    /// <summary>An item's icon frame (and the holder it is scored as), by item id and icon frame, or null.</summary>
    public (MobSpecies Holder, SpriteFrame Frame)? Icon(string itemId, int iconFrame)
    {
        _byId ??= _icons.GroupBy(i => (i.Id, i.Frame.Index)).ToDictionary(g => g.Key, g => (g.First().Holder, g.First().Frame));
        return _byId.TryGetValue((itemId, iconFrame), out var icon) ? icon : null;
    }

    private Dictionary<(string, int), (MobSpecies, SpriteFrame)>? _byId;

    /// <summary>Rows a drop's icon may sit above or below the floor row (it settles after its bounce).</summary>
    public const int FloorBob = 2;

    /// <summary>Share of an icon's colours a window must show for the icon to be placed on its floor (the icon is drawn exactly; another drop may cover some of it).</summary>
    public const double FloorColourShare = 0.6;

    /// <summary>Icons and placements tried by <see cref="FindOnFloor"/> since construction.</summary>
    public long FloorIconsTried { get; private set; }

    private Dictionary<int, List<int>>? _postings;

    /// <summary>
    /// Drops lying where the mosaic cannot vouch for the background leave no blob of their size, only a sliver where the floor's
    /// rows are known (the two drops of the Wild Boar killed on fx-combat-exact's first video frames, item 04010003 and 04000049,
    /// lay over parallax rock at frame 58 and showed as a 13x5 blob). Every icon showing <see cref="FloorColourShare"/> of its
    /// colours in the window is placed with its bottom row on the floor row (<see cref="FloorBob"/> either way) at every column of
    /// the window and scored exactly (<see cref="Threshold"/>); the passing finds, best first, that do not overlap.
    /// </summary>
    public List<DropMatch> FindOnFloor(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int x0, int x1, int floorRow)
    {
        var found = new List<DropMatch>();
        int frameHeight = frame.Length / 4 / frameWidth;
        x0 = Math.Max(0, x0); x1 = Math.Min(frameWidth - 1, x1);
        int y0 = Math.Max(0, floorRow - MaxIcon - FloorBob), y1 = Math.Min(frameHeight - 1, floorRow + FloorBob);
        if (x1 - x0 < 8 || y1 <= y0) return found;
        var present = new HashSet<int>();
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var o = (y * frameWidth + x) * 4;
                present.Add(frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16);
            }
        if (_postings is null)
        {
            _postings = [];
            for (var i = 0; i < _colours.Count; i++)
                foreach (var c in _colours[i])
                {
                    if (!_postings.TryGetValue(c, out var list)) _postings[c] = list = [];
                    list.Add(i);
                }
        }
        var counts = new int[_icons.Count];
        foreach (var c in present)
            if (_postings.TryGetValue(c, out var list))
                foreach (var i in list) counts[i]++;
        var prepared = false;
        var hits = new List<DropMatch>();
        for (var i = 0; i < _icons.Count; i++)
        {
            if (counts[i] < FloorColourShare * _colours[i].Count) continue;
            var (id, category, holder, f) = _icons[i];
            if (f.ExtentWidth > x1 - x0 + 1) continue;
            if (!prepared) { _matcher.PrepareRegion(frame, frameWidth, mask, x0 - 2, y0 - 2, x1 + 2 * SpriteMatcher.SearchX + 2, y1 + 2); prepared = true; }
            FloorIconsTried++;
            // The icon's visible extent with its bottom on the floor row at every column of the window, a window of the matcher's
            // width at a time (its few best coarse placements per window, so a flat icon's near misses cannot crowd out the exact one).
            SpriteMatch? best = null;
            for (var left = x0; left <= x1 - f.ExtentWidth + 1 + SpriteMatcher.SearchX; left += 2 * SpriteMatcher.SearchX + 1)
            {
                var box = new CaptureRect(left + SpriteMatcher.SearchX, floorRow - f.ExtentHeight + 1, f.ExtentWidth, f.ExtentHeight);
                if (_matcher.Best(box, null, holder, f, Threshold, SpriteMatcher.SearchX, FloorBob) is { } m && m.Score >= Threshold && (best is null || m.Score > best.Value.Score)) best = m;
            }
            if (best is { } bm) hits.Add(new DropMatch(id, category, f.Index, bm.Left, bm.Top, bm.Matches, bm.Compared));
        }
        foreach (var h in hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Compared))
        {
            var hf = Icon(h.ItemId, h.IconFrame)!.Value.Frame;
            var mine = new CaptureRect(h.Left + hf.X0, h.Top + hf.Y0, hf.ExtentWidth, hf.ExtentHeight);
            if (found.Any(k => Overlap(k, mine) > 1 / 3.0)) continue;
            found.Add(h);
        }
        return found;

        double Overlap(DropMatch k, CaptureRect r)
        {
            var kf = Icon(k.ItemId, k.IconFrame)!.Value.Frame;
            int ax0 = k.Left + kf.X0, ay0 = k.Top + kf.Y0, ax1 = ax0 + kf.ExtentWidth, ay1 = ay0 + kf.ExtentHeight;
            int w = Math.Min(ax1, r.X + r.Width) - Math.Max(ax0, r.X), h = Math.Min(ay1, r.Y + r.Height) - Math.Max(ay0, r.Y);
            return w <= 0 || h <= 0 ? 0 : (double)w * h / Math.Min(kf.ExtentWidth * kf.ExtentHeight, r.Width * r.Height);
        }
    }
}
