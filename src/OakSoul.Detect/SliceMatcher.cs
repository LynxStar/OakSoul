using System.Runtime.CompilerServices;

namespace OakSoul.Detect;

/// <summary>
/// Finds sprites standing on the floor slices (<see cref="FootholdSlices"/>), an experiment exercised by OakSoul.Detect.Tests.
/// Every slice of a run may hold a sprite's edge: each candidate frame is placed with its feet, its first coloured column
/// from the left or its first from the right on the slice, its origin on the floor's line at the feet column and
/// <see cref="Options.FloorRows"/> rows either side, and checked first on the seed column's band rows (lax), then column by
/// column across the whole sprite. A column is scored by distance, not equality: the screen against the frame's opaque pixels
/// in that column, after the one lift toward white that fits them best (a cloud or steam over a sprite lightens it by about
/// the same share in every channel; clean pixels fit with no lift and must then be exact). A lift of weight w shrinks any
/// mismatch by (1 - w), so a find also carries its distance in art units, each lifted pixel's divided by (1 - lift), and must
/// pass both <see cref="Options.MaxDistance"/> and <see cref="Options.MaxLiftedDistance"/>. Pixels that are masked, off the
/// frame, explained by a find already kept, or exactly the trusted background where the sprite pixel has that colour too carry
/// no information. The best find is kept first and explains its pixels; every later find that overlaps a kept one is scored
/// again on the pixels left, so a sprite half behind another stays while a copy of the first a pixel over does not.
/// </summary>
public static class SliceMatcher
{
    /// <summary>A frame that may stand on a floor: a ground mob's in either facing, or a meso icon's.</summary>
    public sealed record Candidate(string Name, SpriteFrame Frame);

    /// <summary>
    /// A placed frame: its screen top-left and feet, how many of its columns were compared and matched, the pixels compared, its
    /// distance in screen units, and its distance in art units (each lifted pixel's distance divided by 1 - lift; equal to the
    /// first where nothing is lifted).
    /// </summary>
    public sealed record Find(string Name, SpriteFrame Frame, int Left, int Top, int FeetX, int FeetY, int Columns, int Matched, int Pixels, double Distance, double LiftedDistance)
    {
        public double Share => Columns == 0 ? 0 : (double)Matched / Columns;
        public int Right => Left + Frame.X1;
        public override string ToString() => $"{Name} {Frame.Key} feet {FeetX},{FeetY}: {Matched}/{Columns} columns, {Pixels}/{Frame.PixelX.Length} px, distance {Distance:0.000}" +
            (LiftedDistance > Distance + 0.0005 ? $" ({LiftedDistance:0.000} in art units)" : "");
    }

    /// <summary>The thresholds, all distances on 0 (exact) to 1 (white against black).</summary>
    public sealed record Options
    {
        /// <summary>Coloured opaque pixels (not outline) the seed column needs within the band.</summary>
        public int SeedPixels { get; init; } = 3;
        public double SeedDistance { get; init; } = 0.12;
        public double ColumnDistance { get; init; } = 0.12;
        /// <summary>The largest lift toward white a column may take (steam measured 0.04 to 0.59, see SpriteMatcher.MaxFog).</summary>
        public double MaxLift { get; init; } = 0.75;
        public double MinShare { get; init; } = 0.5;
        /// <summary>The share of a frame's opaque pixels a find must have compared (the rest masked or off the frame).</summary>
        public double MinCompared { get; init; } = 0.5;
        public double MaxDistance { get; init; } = 0.08;
        /// <summary>
        /// The most a find may be off in art units (<see cref="Find.LiftedDistance"/>): a cloud of weight w shrinks any mismatch by
        /// (1 - w), so in screen units a wrong frame under it looks close. Provisional, from frame 511: the true Blue Snail under the
        /// cloud pair reads 0.102 and the coin fitted over it 0.312 (at MaxLift 0.9; to re-measure as frames come in).
        /// </summary>
        public double MaxLiftedDistance { get; init; } = 0.15;
        /// <summary>Rows either side of the line a frame's origin is tried at (a drop does not rest on the line the way a mob does; use more for drops).</summary>
        public int FloorRows { get; init; } = 1;
        /// <summary>
        /// Under a lift, count only coloured art pixels whose screen pixel still shows a hue (the steam rules). Off by default: on
        /// frame 511 the gates dropped 59% of the true snail's pixels and 88% of the boar's, and <see cref="MinCompared"/> then
        /// rejected both while a coin kept enough pixels to pass.
        /// </summary>
        public bool LiftGates { get; init; } = false;
        /// <summary>Return every find that passes, overlapping ones included (diagnostics); off, the best per place is kept.</summary>
        public bool KeepOverlapping { get; init; } = false;
    }

    /// <summary>What a search did: placements seeded, seeds that passed (each then compared whole), columns and pixels compared.</summary>
    public sealed class Stats
    {
        public long Placements, SeedsPassed, Columns, Pixels;
        public override string ToString() => $"{Placements:N0} placements seeded, {SeedsPassed:N0} passed, {Columns:N0} columns and {Pixels:N0} pixels compared whole";
    }

    /// <summary>One opaque sprite pixel as the comparison reads it: its row, its colour packed (b | g &lt;&lt; 8 | r &lt;&lt; 16) and by
    /// channel, the squared headroom toward white the lift's fit sums, and whether it can be tested through steam.</summary>
    internal struct Px
    {
        public int Y, Packed, Den;
        public byte B, G, R;
        public bool Foggable;
    }

    /// <summary>
    /// A candidate laid out for the search: its opaque pixels by column (x relative to the frame, rows ascending) in one array,
    /// each column's range in it and the part of it on the band rows, its seed columns (the first with enough colour in the band
    /// from each side, and the feet), and its columns in a spread order (a stride coprime with the width) so a wrong placement
    /// fails its bound early.
    /// </summary>
    internal sealed class Layout
    {
        public readonly string Name;
        public readonly SpriteFrame Frame;
        public readonly Px[] Pixels;
        /// <summary>Column x's pixels are Pixels[Start[x]..Start[x + 1]); its band rows' are Pixels[BandStart[x]..BandEnd[x]).</summary>
        public readonly int[] Start, BandStart, BandEnd;
        public readonly int Seed, SeedRight;
        /// <summary>The seed columns in the order tried (the feet, then the first coloured from the left and from the right), distinct and on the frame.</summary>
        public readonly int[] SeedXs;
        public readonly int[] Order;
        public readonly int MaxColumn;

        public Layout(string name, SpriteFrame f, int seedPixels)
        {
            Name = name; Frame = f;
            var counts = new int[f.Width];
            for (var y = 0; y < f.Height; y++)
                for (var x = 0; x < f.Width; x++)
                    if (f.Bgra[(y * f.Width + x) * 4 + 3] == SpriteFrame.SolidAlpha) counts[x]++;
            Start = new int[f.Width + 1];
            for (var x = 0; x < f.Width; x++) { Start[x + 1] = Start[x] + counts[x]; MaxColumn = Math.Max(MaxColumn, counts[x]); }
            Pixels = new Px[Start[f.Width]];
            var fill = (int[])Start.Clone();
            for (var y = 0; y < f.Height; y++)
                for (var x = 0; x < f.Width; x++)
                {
                    var o = (y * f.Width + x) * 4;
                    if (f.Bgra[o + 3] != SpriteFrame.SolidAlpha) continue;
                    int b = f.Bgra[o], g = f.Bgra[o + 1], r = f.Bgra[o + 2];
                    Pixels[fill[x]++] = new Px
                    {
                        Y = y, Packed = b | g << 8 | r << 16, B = (byte)b, G = (byte)g, R = (byte)r,
                        Den = (255 - b) * (255 - b) + (255 - g) * (255 - g) + (255 - r) * (255 - r),
                        Foggable = SpriteMatcher.Foggable(b, g, r),
                    };
                }
            BandStart = new int[f.Width];
            BandEnd = new int[f.Width];
            int y0 = f.OriginY - FootholdSlices.Above, y1 = f.OriginY + FootholdSlices.Below;
            for (var x = 0; x < f.Width; x++)
            {
                int i = Start[x], e = Start[x + 1];
                while (i < e && Pixels[i].Y < y0) i++;
                BandStart[x] = i;
                while (i < e && Pixels[i].Y <= y1) i++;
                BandEnd[x] = i;
            }
            bool Coloured(int x)
            {
                var n = 0;
                for (var i = BandStart[x]; i < BandEnd[x]; i++) if (Math.Max(Pixels[i].B, Math.Max(Pixels[i].G, Pixels[i].R)) > 51) n++;
                return n >= seedPixels;
            }
            Seed = SeedRight = -1;
            for (var x = 0; x < f.Width && Seed < 0; x++) if (Coloured(x)) Seed = x;
            for (var x = f.Width - 1; x >= 0 && SeedRight < 0; x--) if (Coloured(x)) SeedRight = x;
            SeedXs = [.. new[] { f.OriginX, Seed, SeedRight }.Distinct().Where(x => x >= 0 && x < f.Width)];
            var w = Math.Max(0, f.X1 - f.X0 + 1);
            var stride = w <= 2 ? 1 : Math.Max(1, (int)(w * 0.618));
            while (w > 1 && Gcd(stride, w) != 1) stride++;
            Order = new int[w];
            for (var i = 0; i < w; i++) Order[i] = f.X0 + (int)((long)i * stride % w);
        }

        public int Count(int x) => Start[x + 1] - Start[x];

        private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    /// <summary>
    /// Candidates laid out once for every search over them (each frame's pixels by column, its seed columns); a caller that
    /// searches the same candidates every frame keeps one. <see cref="Of"/> keeps one per candidate list while the list lives.
    /// </summary>
    public sealed class CandidateSet
    {
        private static readonly ConditionalWeakTable<IReadOnlyList<Candidate>, CandidateSet> _cache = new();

        public IReadOnlyList<Candidate> Candidates { get; }
        /// <summary>The <see cref="Options.SeedPixels"/> the seed columns were chosen with.</summary>
        public int SeedPixels { get; }
        internal Layout[] Layouts { get; }
        internal int MaxColumn { get; }
        internal int MaxWidth { get; }

        public CandidateSet(IReadOnlyList<Candidate> candidates, int seedPixels = 3)
        {
            Candidates = [.. candidates];
            SeedPixels = seedPixels;
            // One layout per frame object (a frame listed twice is laid out once).
            var byFrame = new Dictionary<SpriteFrame, Layout>(ReferenceEqualityComparer.Instance);
            Layouts = [.. Candidates.Select(c => byFrame.TryGetValue(c.Frame, out var l) && l.Name == c.Name ? l : byFrame[c.Frame] = new Layout(c.Name, c.Frame, seedPixels))];
            MaxColumn = Layouts.Select(l => l.MaxColumn).DefaultIfEmpty(0).Max();
            MaxWidth = Layouts.Select(l => l.Order.Length).DefaultIfEmpty(0).Max();
        }

        /// <summary>The set of a candidate list, laid out on its first use and kept while the list lives (the list must not change after).</summary>
        public static CandidateSet Of(IReadOnlyList<Candidate> candidates, int seedPixels)
        {
            if (_cache.TryGetValue(candidates, out var set) && set.SeedPixels == seedPixels && set.Candidates.Count == candidates.Count) return set;
            set = new CandidateSet(candidates, seedPixels);
            _cache.AddOrUpdate(candidates, set);
            return set;
        }
    }

    /// <summary>
    /// What every screen pixel of rows <see cref="Y0"/> on (stored by column, as a placement reads them) says about a sprite pixel drawn there, prepared once per search: the
    /// screen colour (low 32 bits) and a cell (high 32 bits): <see cref="Never"/> where the pixel is masked (or explained by a
    /// kept find), <see cref="Always"/> where the background is not trusted or the mosaic does not reach, else the trusted
    /// background colour, which says nothing about a sprite pixel of that same colour.
    /// </summary>
    private sealed class Screen
    {
        public const int Never = -1, Always = -2;
        public readonly ulong[] Cells;
        public readonly int Width, Y0, Rows;

        public Screen(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY, int y0, int y1)
        {
            var frameHeight = frame.Length / 4 / frameWidth;
            Width = frameWidth;
            Y0 = Math.Max(0, y0);
            Rows = Math.Max(0, Math.Min(frameHeight - 1, y1) - Y0 + 1);
            Cells = new ulong[Rows * Width];
            var bits = mask.Bits;
            int mw = mask.Width, mh = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y;
            var usable = mosaic.ForegroundUsable;
            var pixels = mosaic.Pixels;
            for (var r = 0; r < Rows; r++)
            {
                var y = Y0 + r;
                int my = y - vy, wy = cameraY + y - mosaic.OriginY;
                var rowMasked = (uint)my >= (uint)mh;
                var rowInMosaic = (uint)wy < (uint)mosaic.Height;
                for (var x = 0; x < Width; x++)
                {
                    var o = (y * frameWidth + x) * 4;
                    var colour = (uint)(frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16);
                    int mx = x - vx, wx = cameraX + x - mosaic.OriginX;
                    int cell;
                    if (rowMasked || (uint)mx >= (uint)mw || bits[my * mw + mx] != 0) cell = Never;
                    else if (!rowInMosaic || (uint)wx >= (uint)mosaic.Width) cell = Always;
                    else
                    {
                        var i = wy * mosaic.Width + wx;
                        cell = usable[i] == 0 ? Always : (int)(pixels[i] & 0xFFFFFF);
                    }
                    Cells[x * Rows + r] = (ulong)(uint)cell << 32 | colour;
                }
            }
        }

        /// <summary>Marks a kept find's opaque pixels as explained: they say nothing about any later find.</summary>
        public void Explain(Find find)
        {
            var f = find.Frame;
            for (var i = 0; i < f.PixelX.Length; i++)
            {
                int x = find.Left + f.PixelX[i], r = find.Top + f.PixelY[i] - Y0;
                if ((uint)x < (uint)Width && (uint)r < (uint)Rows) Cells[x * Rows + r] = (ulong)unchecked((uint)Never) << 32 | (uint)Cells[x * Rows + r];
            }
        }
    }

    /// <summary>Buffers one search reuses: a column's counted pixels, and a placement's per-column sums.</summary>
    private sealed class Work(int maxColumn, int maxWidth)
    {
        public readonly int[] Index = new int[maxColumn];
        public readonly uint[] Colour = new uint[maxColumn];
        public readonly double[] Residual = new double[maxWidth], Lifted = new double[maxWidth];
        public readonly int[] N = new int[maxWidth];
    }

    /// <summary>k / 765 for every summed channel difference k: a clean pixel's distance, as the general formula gives it with no lift.</summary>
    private static readonly double[] Inv765 = [.. Enumerable.Range(0, 766).Select(k => k / 765.0)];

    /// <summary>Every placement that matches, the best per place: overlapping finds keep the one with the least distance.</summary>
    public static List<Find> Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IReadOnlyList<FootholdSlices.Run> runs, IReadOnlyList<Candidate> candidates, Options? options = null, Stats? stats = null) =>
        Search(frame, frameWidth, mask, mosaic, cameraX, cameraY, runs, CandidateSet.Of(candidates, (options ?? new Options()).SeedPixels), options, stats);

    /// <summary>Every placement that matches, as the overload over a candidate list, with the candidates laid out already.</summary>
    public static List<Find> Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IReadOnlyList<FootholdSlices.Run> runs, CandidateSet set, Options? options = null, Stats? stats = null)
    {
        var o = options ?? new Options();
        if (set.SeedPixels != o.SeedPixels) set = new CandidateSet(set.Candidates, o.SeedPixels);
        stats ??= new Stats();
        var layouts = set.Layouts;
        var finds = new List<Find>();
        if (runs.Count == 0 || layouts.Length == 0) return finds;
        // Only the rows a placement can reach: every line the runs hold, the floor rows either side, and the tallest frame.
        int minLine = int.MaxValue, maxLine = int.MinValue, above = 0, below = 0;
        foreach (var run in runs)
            foreach (var sl in run.Slices) { minLine = Math.Min(minLine, sl.LineY); maxLine = Math.Max(maxLine, sl.LineY); }
        foreach (var l in layouts) { above = Math.Max(above, l.Frame.OriginY); below = Math.Max(below, l.Frame.Height - 1 - l.Frame.OriginY); }
        var screen = new Screen(frame, frameWidth, mask, mosaic, cameraX, cameraY, minLine - o.FloorRows - above, maxLine + o.FloorRows + below);
        var work = new Work(set.MaxColumn, set.MaxWidth);
        var tried = new HashSet<(int, int, int)>();
        foreach (var run in runs)
        {
            var lineAt = run.Slices.ToDictionary(s => s.X, s => s.LineY);
            // Every slice of a run seeds, interrupted ones too: one known pixel inside a sprite's body (a hole, or a colour the
            // background happens to share) interrupts the slice, and Dark Stump 23 of frame 511 had all three seed columns on such slices.
            foreach (var slice in run.Slices)
            {
            for (var k = 0; k < layouts.Length; k++)
            {
                var c = layouts[k];
                var f = c.Frame;
                // The frame's feet on the slice, or its first coloured column from either side on it.
                foreach (var seedX in c.SeedXs)
                {
                var left = slice.X - seedX;
                var line = lineAt.TryGetValue(left + f.OriginX, out var l) ? l : slice.LineY;
                for (var dy = -o.FloorRows; dy <= o.FloorRows; dy++)
                {
                    int feetY = line + dy, top = feetY - f.OriginY;
                    if (!tried.Add((k, left, top))) continue;
                    stats.Placements++;
                    // The lax check: the seed column, its band rows only.
                    var seed = Column(screen, c.Pixels, c.BandStart[seedX], c.BandEnd[seedX], left + seedX, top, o.MaxLift, o.LiftGates, work);
                    if (seed.N == 0 || seed.Residual / seed.N > o.SeedDistance) continue;
                    stats.SeedsPassed++;
                    // Then every column, whole. Passes on its screen distance for now; the art-units limit is judged after kept finds
                    // explain what covers it (the boar's columns behind the snail would fail it here), so only the screen distance,
                    // the share and the pixels compared may stop a placement early.
                    var find = Score(screen, c, left, top, o, work, stats);
                    if (find is not null && find.Columns > 0 && find.Pixels >= o.MinCompared * f.PixelX.Length && find.Share >= o.MinShare && find.Distance <= o.MaxDistance) finds.Add(find);
                }
                }
            }
            }
        }
        if (o.KeepOverlapping) return finds;
        // The best first, explaining its pixels; a later find that overlaps a kept one is scored again on the pixels left and
        // stays only if it still passes on its own (the Wild Boar half behind the Blue Snail on frame 511 does; a copy of the
        // snail a pixel over, left with a sliver, does not).
        var layoutOf = new Dictionary<SpriteFrame, Layout>(ReferenceEqualityComparer.Instance);
        foreach (var c in layouts) layoutOf.TryAdd(c.Frame, c);
        var kept = new List<Find>();
        foreach (var f in finds.OrderBy(f => f.Distance).ThenByDescending(f => f.Matched))
        {
            var find = kept.Any(k => Overlaps(k, f)) ? Score(screen, layoutOf[f.Frame], f.Left, f.Top, o, work, null, f.Name)! : f;
            if (!Accepts(find, o)) continue;
            kept.Add(find);
            screen.Explain(find);
        }
        return kept;
    }

    /// <summary>Whether a scored placement passes: enough of its pixels compared, enough columns matched, and both distances under their limits.</summary>
    private static bool Accepts(Find find, Options o) =>
        find.Columns > 0 && find.Pixels >= o.MinCompared * find.Frame.PixelX.Length && find.Share >= o.MinShare
        && find.Distance <= o.MaxDistance && find.LiftedDistance <= o.MaxLiftedDistance;

    /// <summary>Whether the two finds' visible extents overlap.</summary>
    private static bool Overlaps(Find a, Find b) =>
        a.Left + a.Frame.X0 <= b.Left + b.Frame.X1 && b.Left + b.Frame.X0 <= a.Left + a.Frame.X1
        && a.Top + a.Frame.Y0 <= b.Top + b.Frame.Y1 && b.Top + b.Frame.Y0 <= a.Top + a.Frame.Y1;

    /// <summary>A candidate placed with its frame's top-left at (<paramref name="left"/>, <paramref name="top"/>), compared whole: every column's opaque pixels after that column's best lift.</summary>
    public static Find Score(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        Candidate candidate, int left, int top, Options? options = null)
    {
        var o = options ?? new Options();
        var c = new Layout(candidate.Name, candidate.Frame, o.SeedPixels);
        var screen = new Screen(frame, frameWidth, mask, mosaic, cameraX, cameraY, top, top + candidate.Frame.Height - 1);
        return Score(screen, c, left, top, o, new Work(c.MaxColumn, c.Order.Length), null)!;
    }

    /// <summary>
    /// The placement compared whole. With <paramref name="stats"/> (the search's first pass), columns go in the spread order and
    /// the placement stops, returning null, once it cannot pass: the residual only grows, so the distance can end no lower than
    /// the residual so far over every pixel that could still be compared; the share can end no higher than if every column still
    /// to come matched; the pixels compared no more than every opaque pixel still to come. The sums are taken in column order
    /// whatever order the columns are compared in, so a placement that is not stopped scores exactly as before.
    /// </summary>
    private static Find? Score(Screen screen, Layout c, int left, int top, Options o, Work w, Stats? stats, string? name = null)
    {
        var f = c.Frame;
        var width = c.Order.Length;
        var frameWidth = screen.Width;
        var ns = w.N;
        var res = w.Residual;
        var lif = w.Lifted;
        // What could still be compared: the opaque pixels of the columns on the frame, and those columns.
        int restPixels = 0, restColumns = 0;
        for (var x = f.X0; x <= f.X1; x++)
            if ((uint)(left + x) < (uint)frameWidth && c.Count(x) > 0) { restPixels += c.Count(x); restColumns++; }
        var early = stats is not null;
        var needPixels = o.MinCompared * f.PixelX.Length;
        int compared = 0, matched = 0, pixels = 0;
        double residualSoFar = 0;
        var stopped = early && restPixels < needPixels;
        for (var i = 0; i < width && !stopped; i++)
        {
            var x = early ? c.Order[i] : f.X0 + i;
            ns[x - f.X0] = 0;
            if ((uint)(left + x) >= (uint)frameWidth || c.Count(x) == 0) continue;
            restPixels -= c.Count(x); restColumns--;
            var col = Column(screen, c.Pixels, c.Start[x], c.Start[x + 1], left + x, top, o.MaxLift, o.LiftGates, w);
            ns[x - f.X0] = col.N; res[x - f.X0] = col.Residual; lif[x - f.X0] = col.Lifted;
            if (col.N == 0) continue;
            compared++;
            if (col.Residual / col.N <= o.ColumnDistance) matched++;
            pixels += col.N; residualSoFar += col.Residual;
            if (!early) continue;
            // The bounds, a hair lenient so a rounding in the partial sums never stops a placement that passes.
            if (pixels + restPixels < needPixels
                || residualSoFar / (pixels + restPixels) > o.MaxDistance + 1e-9
                || (double)(matched + restColumns) / (compared + restColumns) < o.MinShare - 1e-9)
                stopped = true;
        }
        if (stats is not null) { stats.Columns += compared; stats.Pixels += pixels; }
        if (stopped) return null;
        double residual = 0, lifted = 0;
        for (var i = 0; i < width; i++)
        {
            if (ns[i] == 0) continue;
            residual += res[i]; lifted += lif[i];
        }
        return new Find(name ?? c.Name, f, left, top, left + f.OriginX, top + f.OriginY, compared, matched, pixels, pixels == 0 ? 1 : residual / pixels, pixels == 0 ? 1 : lifted / pixels);
    }

    /// <summary>
    /// One sprite column (its opaque pixels <paramref name="start"/> to <paramref name="end"/> exclusive, rows ascending) against
    /// the screen column <paramref name="x"/>: the pixels compared, their summed distance after the best lift toward white, and
    /// the same distance in art units (each pixel's divided by 1 - lift). The pixels that count (on the frame, not masked, not
    /// explained by a kept find, and not the trusted background in the sprite's own colour) are found once; the lift's sums are
    /// integers, exact as the doubles they were, and with no lift a pixel's distance is its summed channel difference over 765.
    /// </summary>
    private static (int N, double Residual, double Lifted) Column(Screen screen, Px[] px, int start, int end, int x, int top, double maxLift, bool gates, Work w)
    {
        if ((uint)x >= (uint)screen.Width) return (0, 0, 0);
        var cells = screen.Cells;
        int rows = screen.Rows, row0 = top - screen.Y0, colBase = x * rows;
        var index = w.Index;
        var colour = w.Colour;
        // First pass: the pixels that count, and the lift that fits them best (least squares on s - t = a (255 - t)).
        long num = 0, den = 0;
        int n = 0, diff = 0;
        for (var j = start; j < end; j++)
        {
            ref readonly var p = ref px[j];
            var r = row0 + p.Y;
            if ((uint)r >= (uint)rows) continue;
            var v = cells[colBase + r];
            var cell = (int)(v >> 32);
            if (cell == Screen.Never || cell == p.Packed) continue;
            var s = (uint)v;
            int db = (int)(s & 0xFF) - p.B, dg = (int)(s >> 8 & 0xFF) - p.G, dr = (int)(s >> 16) - p.R;
            num += db * (255 - p.B) + dg * (255 - p.G) + dr * (255 - p.R);
            den += p.Den;
            diff |= db | dg | dr;
            index[n] = j; colour[n] = s;
            n++;
        }
        if (n == 0) return (0, 0, 0);
        // Every pixel exact: no lift, and nothing to sum.
        if (diff == 0) return (n, 0, 0);
        var a = den == 0 ? 0 : Math.Clamp((double)num / den, 0, maxLift);
        double residual = 0, liftedResidual = 0;
        if (a == 0)
        {
            // No lift: the distance is the summed channel difference over 765, in art units the same.
            for (var i = 0; i < n; i++)
            {
                ref readonly var p = ref px[index[i]];
                var s = colour[i];
                residual += Inv765[Math.Abs((int)(s & 0xFF) - p.B) + Math.Abs((int)(s >> 8 & 0xFF) - p.G) + Math.Abs((int)(s >> 16) - p.R)];
            }
            return (n, residual, residual);
        }
        // Gated, only coloured art with room to lighten says anything under a lift (SpriteMatcher.FogMinChroma, FogMinHeadroom).
        var lifted = gates && a >= 0.02;
        var m = 0;
        for (var i = 0; i < n; i++)
        {
            ref readonly var p = ref px[index[i]];
            if (lifted && !p.Foggable) continue;
            var s = colour[i];
            int sb = (int)(s & 0xFF), sg = (int)(s >> 8 & 0xFF), sr = (int)(s >> 16);
            // ... and the screen pixel must still show a hue (SpriteMatcher.FogMinScreenChroma): washed to grey, it fits anything light.
            if (lifted && Math.Max(sb, Math.Max(sg, sr)) - Math.Min(sb, Math.Min(sg, sr)) < SpriteMatcher.FogMinScreenChroma) continue;
            var d = (Math.Abs(sb - (p.B + a * (255 - p.B))) + Math.Abs(sg - (p.G + a * (255 - p.G))) + Math.Abs(sr - (p.R + a * (255 - p.R)))) / 765.0;
            residual += d; liftedResidual += d / (1 - a);
            m++;
        }
        return (m, residual, liftedResidual);
    }
}
