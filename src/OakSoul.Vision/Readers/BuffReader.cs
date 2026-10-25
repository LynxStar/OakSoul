using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>An active buff icon: its slot from the right, where it is, which known buff it matched and how well, whether it is drawn faded, and a fingerprint of its minute digit.</summary>
public sealed record BuffIcon(int Slot, CaptureRect Rect, string Name, double Correlation, bool Faded);

/// <summary>
/// Reads the buff row along the top-right edge. Icons are 32 px squares anchored at the right edge and growing
/// leftward. A slot counts as occupied when its pixels correlate with a buff icon this reader has been taught,
/// compared outside the bottom-left corner where the remaining minutes are drawn; correlation rather than
/// difference because the client periodically draws the icon faded, which the fixture showed on about a quarter
/// of frames. The remaining-minutes digit in the corner is not read yet: it does not survive the fade. The slot pitch has only been seen with one buff active, so multi-buff spacing is unverified.
/// </summary>
public sealed class BuffReader
{
    public const int IconSize = 32, Pitch = 34, MaxSlots = 8;
    public const double MinCorrelation = 0.8;

    private readonly int _rightEdge, _top;
    private readonly List<(string Name, byte[] Pixels, double MeanLuma)> _known = [];

    public BuffReader(HudLayout layout)
    {
        var r = layout["buffs.row1"].Rect;
        _rightEdge = r.X + r.Width;
        _top = r.Y;
    }

    public IReadOnlyList<string> KnownBuffs => _known.Select(k => k.Name).ToList();

    public static CaptureRect SlotRect(int rightEdge, int top, int slot) => new(rightEdge - IconSize - Pitch * slot, top, IconSize, IconSize);
    /// <summary>The minute digit's own box inside the icon.</summary>
    public static CaptureRect MinuteRect(CaptureRect icon) => new(icon.X + 6, icon.Y + 19, 9, 11);
    private static bool InMinuteCorner(int x, int y) => x < 15 && y >= 18;

    /// <summary>Teaches the reader the icon currently shown in <paramref name="slot"/>, under <paramref name="name"/>.</summary>
    public void Learn(string name, FrameGeometry geometry, ReadOnlySpan<byte> frame, int slot = 0)
    {
        var px = geometry.ExtractNative(frame, new HudRegion(0, "buff", RegionKind.Fixed, SlotRect(_rightEdge, _top, slot), ""));
        _known.Add((name, px, MeanLuma(px)));
    }

    /// <summary>The best known buff for a slot, its correlation, and whether the slot is drawn darker than the template.</summary>
    public (string? Name, double Correlation, bool Faded) Match(FrameGeometry geometry, ReadOnlySpan<byte> frame, int slot)
    {
        var rect = SlotRect(_rightEdge, _top, slot);
        if (rect.X < 0) return (null, 0, false);
        var px = geometry.ExtractNative(frame, new HudRegion(0, "buff", RegionKind.Fixed, rect, ""));
        string? best = null;
        var bestCorrelation = -1.0;
        var bestMean = 1.0;
        foreach (var (name, known, mean) in _known)
        {
            var c = Correlation(px, known);
            if (c > bestCorrelation) { bestCorrelation = c; best = name; bestMean = mean; }
        }
        return (best, bestCorrelation, best is not null && MeanLuma(px) < bestMean * 0.9);
    }

    public IReadOnlyList<BuffIcon> Read(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var icons = new List<BuffIcon>();
        for (var slot = 0; slot < MaxSlots; slot++)
        {
            var (name, correlation, faded) = Match(geometry, frame, slot);
            if (name is null || correlation < MinCorrelation) break; // buffs pack from the right: first empty slot ends the row
            icons.Add(new BuffIcon(slot, SlotRect(_rightEdge, _top, slot), name, correlation, faded));
        }
        return icons;
    }

    /// <summary>Writes the learned icons to a JSON file so a later session can recognise them.</summary>
    public void Save(string path) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_known.Select(k => new KnownBuff(k.Name, Convert.ToBase64String(k.Pixels))).ToList()));

    /// <summary>Loads icons written by <see cref="Save"/>. Returns how many were loaded.</summary>
    public int Load(string path)
    {
        var list = System.Text.Json.JsonSerializer.Deserialize<List<KnownBuff>>(File.ReadAllText(path)) ?? [];
        foreach (var k in list) { var px = Convert.FromBase64String(k.Pixels); _known.Add((k.Name, px, MeanLuma(px))); }
        return list.Count;
    }

    private sealed record KnownBuff(string Name, string Pixels);

    private static double MeanLuma(byte[] px)
    {
        long sum = 0; var n = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var o = (y * IconSize + x) * 4;
                sum += (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
                n++;
            }
        return (double)sum / n;
    }

    /// <summary>Normalised cross-correlation of luma outside the minute corner.</summary>
    private static double Correlation(byte[] a, byte[] b)
    {
        double sa = 0, sb = 0; var n = 0;
        var la = new double[IconSize * IconSize]; var lb = new double[IconSize * IconSize];
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var o = (y * IconSize + x) * 4; var i = y * IconSize + x;
                la[i] = (a[o] + a[o + 1] * 2 + a[o + 2]) >> 2; lb[i] = (b[o] + b[o + 1] * 2 + b[o + 2]) >> 2;
                sa += la[i]; sb += lb[i]; n++;
            }
        double ma = sa / n, mb = sb / n, num = 0, da = 0, db = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var i = y * IconSize + x;
                num += (la[i] - ma) * (lb[i] - mb); da += (la[i] - ma) * (la[i] - ma); db += (lb[i] - mb) * (lb[i] - mb);
            }
        return da == 0 || db == 0 ? 0 : num / Math.Sqrt(da * db);
    }
}
