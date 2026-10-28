using OakSoul.Capture;
using OakSoul.Vision;

namespace OakSoul.Detect;

/// <summary>
/// Which pixels of the world viewport belong to the world, per frame: the HUD regions drawn over it and the own
/// character are excluded from both the background mosaic and detection. One byte per viewport pixel, 1 = excluded.
/// The fixed HUD part is computed once; <see cref="Reset"/> restores it before the per-frame rectangles are added.
/// </summary>
public sealed class ScreenMask
{
    /// <summary>
    /// The HUD regions inside the viewport that are always drawn over the world: the buff row (its right end also holds
    /// the event icon), the quickslot and the EXP and loot log. The minimap panel is added per frame from the reader,
    /// because its size follows the map; <see cref="DefaultMinimapPanel"/> stands in when the reader has none.
    /// </summary>
    public static readonly string[] FixedHudRegions = ["buffs.row1", "quickslot", "exp_loot_log"];

    /// <summary>
    /// The chat bar's frame starts two rows above the viewport's bottom edge (row 700 from x 284, measured on the
    /// fx-combat keyframes), so its top rows, which the layout counts as world, are masked too.
    /// </summary>
    public static readonly CaptureRect ChatBarTop = new(282, 696, 804, 6);

    /// <summary>The largest minimap panel seen on the fixtures (about 260x230 at the top-left), used when the reader found none.</summary>
    public static readonly CaptureRect DefaultMinimapPanel = new(0, 0, 262, 232);

    private readonly byte[] _fixed;

    /// <param name="unmasked">Fixed regions left in the world: the detector leaves the EXP and loot log in, and takes only its text
    /// out of the foreground (a mob walking behind the log is drawn over by the text, not hidden: see EntityDetector.ClearLogText).</param>
    public ScreenMask(HudLayout layout, IReadOnlyCollection<string>? unmasked = null)
    {
        var viewport = layout["world.viewport"].Rect;
        Viewport = viewport;
        _fixed = new byte[viewport.Width * viewport.Height];
        Bits = new byte[_fixed.Length];
        foreach (var name in FixedHudRegions)
            if (unmasked?.Contains(name) != true && layout.TryGet(name, out var region)) Fill(_fixed, region.Rect, 2);
        Fill(_fixed, ChatBarTop, 0);
        Reset();
    }

    public CaptureRect Viewport { get; }
    public int Width => Viewport.Width;
    public int Height => Viewport.Height;

    /// <summary>1 = excluded, per viewport pixel, row-major.</summary>
    public byte[] Bits { get; }

    public void Reset() => _fixed.CopyTo(Bits, 0);

    /// <summary>Excludes a rectangle given in native frame pixels, padded by <paramref name="pad"/> on every side.</summary>
    public void Exclude(CaptureRect rect, int pad = 0) => Fill(Bits, rect, pad);

    public bool IsExcluded(int x, int y) => (uint)x >= (uint)Width || (uint)y >= (uint)Height || Bits[y * Width + x] != 0;

    /// <summary>Fraction of the pixels of a rectangle (viewport coordinates) that are excluded.</summary>
    public double ExcludedFraction(int x0, int y0, int x1, int y1)
    {
        int n = 0, ex = 0;
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) { n++; ex += Bits[y * Width + x]; }
        return n == 0 ? 1 : (double)ex / n;
    }

    private void Fill(byte[] bits, CaptureRect rect, int pad)
    {
        int x0 = Math.Max(0, rect.X - pad - Viewport.X), y0 = Math.Max(0, rect.Y - pad - Viewport.Y);
        int x1 = Math.Min(Width, rect.X + rect.Width + pad - Viewport.X), y1 = Math.Min(Height, rect.Y + rect.Height + pad - Viewport.Y);
        for (var y = y0; y < y1; y++) bits.AsSpan(y * Width + x0, Math.Max(0, x1 - x0)).Fill(1);
    }
}
