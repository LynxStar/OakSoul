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
    /// <summary>
    /// The taught icons: pixels, mean luma, and the correlation's side of each (luma outside the minute corner, its mean and
    /// its summed squared deviation), built once when the icon is learned or loaded; icons never change after that.
    /// </summary>
    private readonly List<(string Name, byte[] Pixels, double MeanLuma, double[] Luma, double LumaMean, double LumaDev)> _known = [];

    /// <summary>The slot's side of the correlation, rewritten by each <see cref="Match"/> (one reader, one thread).</summary>
    private readonly double[] _slotLuma = new double[IconSize * IconSize];

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
        _known.Add(Known(name, px));
    }

    private static (string Name, byte[] Pixels, double MeanLuma, double[] Luma, double LumaMean, double LumaDev) Known(string name, byte[] px)
    {
        var view = new BgraView(px, IconSize, IconSize);
        var luma = new double[IconSize * IconSize];
        var (mean, dev) = LumaSide(view, luma);
        return (name, px, MeanLuma(view), luma, mean, dev);
    }

    /// <summary>The best known buff for a slot, its correlation, and whether the slot is drawn darker than the template.</summary>
    public (string? Name, double Correlation, bool Faded) Match(FrameGeometry geometry, ReadOnlySpan<byte> frame, int slot)
    {
        var rect = SlotRect(_rightEdge, _top, slot);
        if (rect.X < 0) return (null, 0, false);
        var px = geometry.ViewNative(frame, new HudRegion(0, "buff", RegionKind.Fixed, rect, ""));
        string? best = null;
        var bestCorrelation = -1.0;
        var bestMean = 1.0;
        var (slotMean, slotDev) = LumaSide(px, _slotLuma);
        foreach (var (name, _, mean, luma, lumaMean, lumaDev) in _known)
        {
            var c = Correlation(_slotLuma, slotMean, slotDev, luma, lumaMean, lumaDev);
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
        foreach (var k in list) _known.Add(Known(k.Name, Convert.FromBase64String(k.Pixels)));
        return list.Count;
    }

    private sealed record KnownBuff(string Name, string Pixels);

    private static double MeanLuma(BgraView view)
    {
        var px = view.Pixels;
        long sum = 0; var n = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var o = view.Offset(x, y);
                sum += (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
                n++;
            }
        return (double)sum / n;
    }

    /// <summary>
    /// One side of the correlation: the luma outside the minute corner written into <paramref name="luma"/> (by y * IconSize + x),
    /// its mean, and the sum of its squared deviations from the mean.
    /// </summary>
    private static (double Mean, double Dev) LumaSide(BgraView view, double[] luma)
    {
        var px = view.Pixels;
        double sum = 0; var n = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                int o = view.Offset(x, y), i = y * IconSize + x;
                luma[i] = (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
                sum += luma[i]; n++;
            }
        double mean = sum / n, dev = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var i = y * IconSize + x;
                dev += (luma[i] - mean) * (luma[i] - mean);
            }
        return (mean, dev);
    }

    /// <summary>Normalised cross-correlation of luma outside the minute corner, from both sides (<see cref="LumaSide"/>).</summary>
    private static double Correlation(double[] la, double ma, double da, double[] lb, double mb, double db)
    {
        double num = 0;
        for (var y = 0; y < IconSize; y++)
            for (var x = 0; x < IconSize; x++)
            {
                if (InMinuteCorner(x, y)) continue;
                var i = y * IconSize + x;
                num += (la[i] - ma) * (lb[i] - mb);
            }
        return da == 0 || db == 0 ? 0 : num / Math.Sqrt(da * db);
    }
}
