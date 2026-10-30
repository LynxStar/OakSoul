using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The band along the floors cut into one-pixel-wide slices, an experiment exercised by OakSoul.Detect.Tests on
/// fx-combat-exact's frame 511 before anything uses it. A slice is one screen column of one floor foothold, from
/// <see cref="Above"/> rows above the foothold's line to <see cref="Below"/> below it, the line followed slope and all:
/// a slot a ground mob's or a drop's bottom could stand in. A pixel is known when it is masked or shows exactly the
/// background the mosaic vouches for; anything else (it differs, the background is not trusted, a cloud is over it) may
/// hold something. A slice of known pixels only can hold nothing and is cut. A slice whose known pixels lie between unknown
/// ones above and below is interrupted: nothing starts there, but a sprite may span it (an O with background in its
/// middle). The rest are live. A chain of floor footholds (each the next's prev, walls ending it) is one floor: its slices
/// that are not cut make runs across the joints, the line stepping with the slope, and a run narrower than the narrowest
/// thing searched for is cut too. Cutting each foothold on its own lost the Axe Stump of frame 511, which stands across
/// three footholds none of them 35 pixels long.
/// </summary>
public static class FootholdSlices
{
    public const int Above = 20, Below = 4;

    public enum Kind : byte { Cut, Interrupted, Live }

    /// <summary>One slice: its screen column, its foothold, the screen row of the foothold's line there, and what it holds.</summary>
    public readonly record struct Slice(int X, int FootholdId, int LineY, Kind Kind);

    /// <summary>A floor's contiguous slices that are not cut, left to right.</summary>
    public sealed record Run(IReadOnlyList<Slice> Slices)
    {
        public int X0 => Slices[0].X;
        public int X1 => Slices[^1].X;
        public int Width => Slices.Count;
        public IEnumerable<int> FootholdIds => Slices.Select(s => s.FootholdId).Distinct();
    }

    /// <summary>
    /// The runs of every floor (a chain of non-wall footholds) in the frame, at the frame's registered camera; runs narrower
    /// than <paramref name="minWidth"/> slices are dropped. The floors' columns are laid out once per footholds collection
    /// (<see cref="Plan"/>, kept while the collection lives; it must not change after its first use here).
    /// </summary>
    public static List<Run> Build(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IEnumerable<Foothold> footholds, int minWidth) =>
        Build(frame, frameWidth, mask, mosaic, cameraX, cameraY, Plan.Of(footholds), minWidth);

    /// <summary>The runs of every floor of <paramref name="plan"/> in the frame, as <see cref="Build(ReadOnlySpan{byte}, int, ScreenMask, BackgroundMosaic, int, int, IEnumerable{Foothold}, int)"/>.</summary>
    public static List<Run> Build(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        Plan plan, int minWidth)
    {
        var frameHeight = frame.Length / 4 / frameWidth;
        var runs = new List<Run>();
        var current = new List<Slice>();
        var view = new View(frameWidth, frameHeight, mask, mosaic, cameraX, cameraY);
        foreach (var floor in plan.Floors)
        {
            // Off the frame, every column is cut: nothing to classify.
            if (floor.MaxWx < cameraX || floor.MinWx >= cameraX + frameWidth
                || floor.MaxLine - cameraY + Below < 0 || floor.MinLine - cameraY - Above >= frameHeight) continue;
            // The columns on the frame's width only (those beside it are cut, so a run cannot cross the frame's edge).
            var wx = floor.Wx;
            int i0 = LowerBound(wx, cameraX), i1 = LowerBound(wx, cameraX + frameWidth);
            var last = int.MinValue;
            for (var i = i0; i < i1; i++)
            {
                if (wx[i] != last + 1) Close();
                last = wx[i];
                var x = wx[i] - cameraX;
                var line = floor.Line[i] == int.MinValue ? int.MinValue : floor.Line[i] - cameraY;
                var visible = line != int.MinValue && line + Below >= 0 && line - Above < frameHeight;
                var kind = visible ? view.Classify(frame, x, line) : Kind.Cut;
                if (kind != Kind.Cut) { current.Add(new Slice(x, floor.FootholdId[i], line, kind)); continue; }
                Close();
            }
            Close();
        }
        return runs;

        void Close()
        {
            if (current.Count >= minWidth) runs.Add(new Run(current.ToArray()));
            current.Clear();
        }

        static int LowerBound(int[] a, int v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { var m = (lo + hi) >>> 1; if (a[m] < v) lo = m + 1; else hi = m; }
            return lo;
        }
    }

    /// <summary>
    /// A map's floors laid out once: every column of every floor, left to right (a foothold's from its left end up to its right
    /// end exclusive, so two footholds meeting at a point do not share it), with its foothold and the world row of the line there.
    /// </summary>
    public sealed class Plan
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Plan> _cache = new();

        /// <summary>One floor's columns in world x order (a column two footholds share appears twice and breaks a run, as it always did).</summary>
        public sealed record Floor(int[] Wx, int[] FootholdId, int[] Line, int MinWx, int MaxWx, int MinLine, int MaxLine);

        public IReadOnlyList<Floor> Floors { get; }

        public Plan(IEnumerable<Foothold> footholds)
        {
            var floors = new List<Floor>();
            foreach (var floor in FootholdSlices.Floors(footholds))
            {
                var columns = floor.SelectMany(fh => Enumerable.Range(fh.MinX, fh.MaxX - fh.MinX).Select(wx => (Wx: wx, Fh: fh))).OrderBy(c => c.Wx).ToArray();
                if (columns.Length == 0) continue;
                var wxs = new int[columns.Length];
                var ids = new int[columns.Length];
                var lines = new int[columns.Length];
                int minLine = int.MaxValue, maxLine = int.MinValue;
                for (var i = 0; i < columns.Length; i++)
                {
                    var (wx, fh) = columns[i];
                    wxs[i] = wx; ids[i] = fh.Id;
                    lines[i] = fh.YAt(wx) is { } wy ? (int)Math.Round(wy) : int.MinValue;
                    if (lines[i] == int.MinValue) continue;
                    minLine = Math.Min(minLine, lines[i]); maxLine = Math.Max(maxLine, lines[i]);
                }
                if (maxLine == int.MinValue) continue;
                floors.Add(new Floor(wxs, ids, lines, wxs[0], wxs[^1], minLine, maxLine));
            }
            Floors = floors;
        }

        /// <summary>The plan of a footholds collection, laid out on its first use and kept while the collection lives.</summary>
        public static Plan Of(IEnumerable<Foothold> footholds) => _cache.GetValue(footholds, f => new Plan((IEnumerable<Foothold>)f));
    }

    /// <summary>The map's floors: each chain of non-wall footholds linked by prev and next, a wall or a missing link ending it.</summary>
    public static List<List<Foothold>> Floors(IEnumerable<Foothold> footholds)
    {
        var byId = footholds.Where(f => !f.IsWall).ToDictionary(f => f.Id);
        var seen = new HashSet<int>();
        var floors = new List<List<Foothold>>();
        foreach (var start in byId.Values)
        {
            if (seen.Contains(start.Id)) continue;
            // Back to the chain's first foothold, then forward through it.
            var head = start;
            var back = new HashSet<int> { head.Id };
            while (byId.TryGetValue(head.Prev, out var p) && p.Next == head.Id && back.Add(p.Id)) head = p;
            var floor = new List<Foothold>();
            for (var fh = head; fh is not null && seen.Add(fh.Id); fh = byId.TryGetValue(fh.Next, out var n) && n.Prev == fh.Id ? n : null)
                floor.Add(fh);
            floors.Add(floor);
        }
        return floors;
    }

    /// <summary>What the slice at screen column <paramref name="x"/> over the line at row <paramref name="line"/> holds (rows off the frame left out).</summary>
    public static Kind Classify(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        int x, int line)
    {
        bool unknownSeen = false, gapAfterUnknown = false;
        for (var y = Math.Max(0, line - Above); y <= Math.Min(frameHeight - 1, line + Below); y++)
        {
            if (Known(frame, frameWidth, mask, mosaic, cameraX, cameraY, x, y))
            {
                if (unknownSeen) gapAfterUnknown = true;
                continue;
            }
            if (gapAfterUnknown) return Kind.Interrupted;
            unknownSeen = true;
        }
        return unknownSeen ? Kind.Live : Kind.Cut;
    }

    /// <summary><see cref="Classify"/> and <see cref="Known"/> with the mask's and the mosaic's arrays and offsets read once per frame.</summary>
    private readonly struct View(int frameWidth, int frameHeight, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY)
    {
        private readonly byte[] _bits = mask.Bits, _usable = mosaic.ForegroundUsable;
        private readonly uint[] _pixels = mosaic.Pixels;
        private readonly int _maskX = mask.Viewport.X, _maskY = mask.Viewport.Y, _maskW = mask.Width, _maskH = mask.Height;
        private readonly int _dx = cameraX - mosaic.OriginX, _dy = cameraY - mosaic.OriginY, _mosaicW = mosaic.Width, _mosaicH = mosaic.Height;

        public Kind Classify(ReadOnlySpan<byte> frame, int x, int line)
        {
            bool unknownSeen = false, gapAfterUnknown = false;
            for (var y = Math.Max(0, line - Above); y <= Math.Min(frameHeight - 1, line + Below); y++)
            {
                if (Known(frame, x, y))
                {
                    if (unknownSeen) gapAfterUnknown = true;
                    continue;
                }
                if (gapAfterUnknown) return Kind.Interrupted;
                unknownSeen = true;
            }
            return unknownSeen ? Kind.Live : Kind.Cut;
        }

        private bool Known(ReadOnlySpan<byte> frame, int x, int y)
        {
            int mx = x - _maskX, my = y - _maskY;
            if ((uint)mx >= (uint)_maskW || (uint)my >= (uint)_maskH || _bits[my * _maskW + mx] != 0) return true;
            int wx = _dx + x, wy = _dy + y;
            if ((uint)wx >= (uint)_mosaicW || (uint)wy >= (uint)_mosaicH) return false;
            var i = wy * _mosaicW + wx;
            if (_usable[i] == 0) return false;
            var o = (y * frameWidth + x) * 4;
            return (uint)(frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16) == (_pixels[i] & 0xFFFFFF);
        }
    }

    /// <summary>Whether a frame pixel is known: masked, or exactly the background the mosaic vouches for (no tolerance).</summary>
    public static bool Known(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY, int x, int y)
    {
        if (mask.IsExcluded(x - mask.Viewport.X, y - mask.Viewport.Y)) return true;
        int wx = cameraX + x - mosaic.OriginX, wy = cameraY + y - mosaic.OriginY;
        if ((uint)wx >= (uint)mosaic.Width || (uint)wy >= (uint)mosaic.Height) return false;
        var i = wy * mosaic.Width + wx;
        if (mosaic.ForegroundUsable[i] == 0) return false;
        var o = (y * frameWidth + x) * 4;
        return (uint)(frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16) == (mosaic.Pixels[i] & 0xFFFFFF);
    }
}
