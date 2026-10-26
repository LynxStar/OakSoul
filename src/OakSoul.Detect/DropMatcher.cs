using System.Text.Json;
using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>A drop identified by its icon: the item id ("09000000" to "09000003" are meso bags), the icon frame, and the match.</summary>
public sealed record DropMatch(string ItemId, string Category, int IconFrame, int Left, int Top, int Matches, int Compared)
{
    public double Score => Compared == 0 ? 0 : (double)Matches / Compared;
    public bool IsMeso => ItemId.StartsWith("0900000", StringComparison.Ordinal);
    public override string ToString() => $"{(IsMeso ? "meso" : Category)} {ItemId}#{IconFrame} {Score:0.00}";
}

/// <summary>
/// Identifies drops lying on the ground by their <c>iconRaw</c> sprites, which the client draws 1:1: the meso bags'
/// spinning frames (<c>item/special/0900.img/0900000N/iconRaw/K</c>) and every item icon exported by
/// <c>OakSoul.SpriteExport --items</c> (equipment is not exported, so equipment drops stay unidentified). Because the
/// icon is drawn unscaled, the box must match the icon's extent to a few pixels and the score is strict. On an exact
/// frame the icon's opaque pixels are compared with the screen pixel for pixel, as for mobs (<see cref="SpriteMatcher"/>,
/// without the freeze); on a blurred one by 3x3 means.
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
    /// Fraction of the icon's interior samples that must agree on a blurred frame (as for mobs, 3x3 means within
    /// <see cref="SpriteMatcher.PixelTolerance"/>). Stricter than for mobs: an icon is small, static and never tinted,
    /// and many icons share a size, so only a near-complete agreement names one.
    /// </summary>
    public const double Threshold = 0.9;

    /// <summary>
    /// The meso coins' threshold on a blurred frame. Small flat coins survive the point-size capture's blur better than
    /// mobs (bronze coins matched at 1.00 on fx-combat), but gold coins scored 0.83 to 0.88 and a bronze coin beside a
    /// snail 0.83, all five confirmed by eye, while the best non-coin small candidate on a surface reached 0.78 (against
    /// an item icon). There are only 15 coin frames, so a lower bar costs little in chance matches.
    /// </summary>
    public const double MesoThreshold = 0.8;

    /// <summary>
    /// Fraction of an icon's visible opaque pixels that must equal the screen on an exact frame, for any icon. On
    /// fx-exact the one drop lying on the ground (item 04000004) matched at 1.00 on all four keyframes it was tried on,
    /// while no other small candidate on a surface of either exact fixture reached 0.20 against any of the 1022 icons.
    /// </summary>
    public const double ExactThreshold = 0.8;

    private static double ThresholdFor(string id, bool exact) => exact ? ExactThreshold : id.StartsWith("0900000", StringComparison.Ordinal) ? MesoThreshold : Threshold;

    private readonly List<(string Id, string Category, MobSpecies Holder, SpriteFrame Frame)> _icons = [];
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
            if (!category.StartsWith("item/", StringComparison.Ordinal) || !node.Contains("iconRaw", StringComparison.Ordinal)) continue;
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
            if (frame.SampleX.Length < 20) continue;
            var holder = new MobSpecies { Id = 0, Name = id };
            matcher._icons.Add((id, category, holder, frame));
        }
        var ordered = matcher._icons.OrderBy(i => i.Id.StartsWith("0900000", StringComparison.Ordinal) ? 0 : 1).ToList();
        matcher._icons.Clear();
        matcher._icons.AddRange(ordered);
        return matcher;
    }

    /// <summary>When true, <see cref="Diagnose"/> records every tried candidate's best icon score (for the report).</summary>
    public List<(long Frame, CaptureRect Box, double Score, string ItemId)> Scores { get; } = [];

    /// <summary>The best score of any icon on a candidate, ignoring the threshold (report only).</summary>
    public void Diagnose(long sequence, ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box, bool exact = false)
    {
        _matcher.Exact = exact;
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
    public DropMatch? Match(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box, bool exact = false)
    {
        _matcher.Exact = exact;
        if (box.Width > MaxIcon + SizeSlack || box.Height > MaxIcon + SizeSlack) return null;
        DropMatch? best = null;
        var prepared = false;
        foreach (var (id, category, holder, f) in _icons)
        {
            if (Math.Abs(f.ExtentWidth - box.Width) > SizeSlack || Math.Abs(f.ExtentHeight - box.Height) > SizeSlack) continue;
            if (!prepared) { _matcher.Prepare(frame, frameWidth, mask, box, null); prepared = true; }
            var needed = ThresholdFor(id, exact);
            if (_matcher.Best(box, null, holder, f, needed, SearchX, SearchY) is not { } m || m.Score < needed) continue;
            if (best is null || m.Score > best.Score) best = new DropMatch(id, category, f.Index, m.Left, m.Top, m.Matches, m.Compared);
            if (best.Score >= 0.99) break;
        }
        return best;
    }
}
