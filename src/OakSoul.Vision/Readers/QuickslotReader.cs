using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>One quickslot cell: its key, whether it holds anything, and a fingerprint that changes when its contents or count change.</summary>
public sealed record QuickslotCell(int Index, char Key, CaptureRect Rect, bool Occupied, ulong Fingerprint, ulong CountFingerprint);

/// <summary>
/// Reads the eight quickslot cells. The item counts are tiny anti-aliased digits drawn over the icon art, too
/// low in contrast to read as text, but the rendering is deterministic: a fingerprint of the count corner
/// changes exactly when the count does. A bot that knows the starting count (from the inventory) can count
/// potions down from those change events; identity of the item comes from the icon fingerprint.
/// </summary>
public sealed class QuickslotReader
{
    private static readonly char[] Keys = ['Q', 'W', 'E', 'R', 'A', 'S', 'D', 'F'];
    private const int CellWidth = 31, CellHeight = 30, Pitch = 35;
    private readonly CaptureRect _origin;
    private readonly ulong[] _lastCount = new ulong[8];

    public QuickslotReader(HudLayout layout)
    {
        var r = layout["quickslot"].Rect;
        _origin = new CaptureRect(r.X + 4, r.Y + 5, r.Width, r.Height); // first cell's top-left inside the panel frame
    }

    public static CaptureRect CellRect(CaptureRect origin, int index) =>
        new(origin.X + Pitch * (index % 4), origin.Y + Pitch * (index / 4), CellWidth, CellHeight);

    /// <summary>The bottom-right corner where the count is drawn.</summary>
    public static CaptureRect CountRect(CaptureRect cell) => new(cell.X + 2, cell.Y + 17, cell.Width - 4, 12);

    public IReadOnlyList<QuickslotCell> Read(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var cells = new List<QuickslotCell>(8);
        for (var i = 0; i < 8; i++)
        {
            var rect = CellRect(_origin, i);
            var px = geometry.ViewNative(frame, new HudRegion(0, "cell", RegionKind.Fixed, rect, ""));
            // An empty cell is flat light grey in the middle (the key letter sits in the top-left corner); any item
            // draws there. Measured: empty centres vary by under 100, the faintest item (a white feather) by over 1000.
            var centre = geometry.ViewNative(frame, new HudRegion(0, "centre", RegionKind.Fixed, new CaptureRect(rect.X + 8, rect.Y + 8, rect.Width - 12, rect.Height - 12), ""));
            var occupied = Variance(centre) > 300;
            var count = CountRect(rect);
            var countPx = geometry.ViewNative(frame, new HudRegion(0, "count", RegionKind.Fixed, count, ""));
            cells.Add(new QuickslotCell(i, Keys[i], rect, occupied, TextRegionTracker.Hash(px), TextRegionTracker.Hash(countPx)));
        }
        return cells;
    }

    /// <summary>Indices of cells whose count corner changed since the previous call. Call once per frame.</summary>
    public List<int> CountChanges(IReadOnlyList<QuickslotCell> cells)
    {
        var changed = new List<int>();
        foreach (var c in cells)
        {
            if (_lastCount[c.Index] != 0 && c.Occupied && c.CountFingerprint != _lastCount[c.Index]) changed.Add(c.Index);
            _lastCount[c.Index] = c.CountFingerprint;
        }
        return changed;
    }

    private static double Variance(BgraView bgra)
    {
        long sum = 0, sq = 0; var n = bgra.Width * bgra.Height;
        for (var y = 0; y < bgra.Height; y++)
        {
            var row = bgra.Row(y);
            for (var o = 0; o < row.Length; o += 4) { var l = (row[o] + row[o + 1] * 2 + row[o + 2]) >> 2; sum += l; sq += (long)l * l; }
        }
        var mean = (double)sum / n;
        return (double)sq / n - mean * mean;
    }
}
