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
/// <para>
/// Trying every candidate on every slice made 682,274 mob placements on frame 511, of which 207,013 passed the lax check and were
/// compared whole. Instead each slice queries a seed index (<see cref="CandidateSet"/>): its screen pixels that may hold something
/// vote for the placements whose seed column has that exact colour on that row, the row taken from the line at the placement's
/// feet column (five of frame 511's nine mobs stand on slopes), and, for the pixels under a cloud or steam where no colour is
/// exact, for those whose seed column has a colour the pixel is a lift of. A placement with <see cref="Options.SeedPixels"/> votes
/// of either kind is retrieved, then checked and scored as before, on the seed column the enumeration would have tried it on
/// first; the index only drops placements, and the finds keep the enumeration's order.
/// </para>
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
        /// <summary>Placements the seed index retrieved (each then seeded unless tried already).</summary>
        public long Retrieved;
        /// <summary>Milliseconds laying out the candidate set and its seed index (zero when the set was laid out before), and querying the index.</summary>
        public double IndexMs, QueryMs;
        public override string ToString() => $"{Placements:N0} placements seeded, {SeedsPassed:N0} passed, {Columns:N0} columns and {Pixels:N0} pixels compared whole" +
            $"; {Retrieved:N0} retrieved by the index (built in {IndexMs:0.0} ms, queried in {QueryMs:0.0} ms)";
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Candidates = [.. candidates];
            SeedPixels = seedPixels;
            // One layout per frame object (a frame listed twice is laid out once).
            var byFrame = new Dictionary<SpriteFrame, Layout>(ReferenceEqualityComparer.Instance);
            Layouts = [.. Candidates.Select(c => byFrame.TryGetValue(c.Frame, out var l) && l.Name == c.Name ? l : byFrame[c.Frame] = new Layout(c.Name, c.Frame, seedPixels))];
            MaxColumn = Layouts.Select(l => l.MaxColumn).DefaultIfEmpty(0).Max();
            MaxWidth = Layouts.Select(l => l.Order.Length).DefaultIfEmpty(0).Max();
            BuildIndex();
            BuildMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>Milliseconds the layout and the seed index took.</summary>
        public double BuildMs { get; }

        /// <summary>
        /// The exact seed index: for every candidate's seed column, its coloured band pixels (max channel above 51) posted under their
        /// colour (<see cref="ColourIds"/>; the meso icons' colours are not all multiples of 17, so the id is a lookup, not the
        /// channels over 17), each posting the code (candidate * 3 + which of its seed columns) and the pixel's row relative to the
        /// origin row.
        /// </summary>
        internal int[] PostStart = [], PostCode = [], PostRow = [];
        /// <summary>Every posted colour (packed) and its id.</summary>
        internal readonly Dictionary<int, int> ColourIds = [];
        /// <summary>
        /// The lift index: the seed columns' coloured band pixels that can be tested through steam (<see cref="SpriteMatcher.Foggable"/>),
        /// posted under the direction of their headroom toward white (255 - t), which a lift s = t + a (255 - t) keeps: 255 - s =
        /// (1 - a) (255 - t). The direction is the headroom's blue and green shares, <see cref="LiftQ"/> steps each.
        /// </summary>
        internal int[] LiftStart = [], LiftCodes = [];
        internal LiftEntry[] LiftEntries = [];
        /// <summary>A colour at a row of the lift index: its channels and headroom, 1 / the headroom's squared length, and the seed columns posting it (LiftCodes[CodeStart..CodeEnd)).</summary>
        internal readonly record struct LiftEntry(int B, int G, int R, int Hb, int Hg, int Hr, double InvDen, int Row, int CodeStart, int CodeEnd);
        internal const int LiftQ = 32, LiftBins = (LiftQ + 1) * (LiftQ + 1), LiftRows = FootholdSlices.Above + FootholdSlices.Below + 1;
        internal static int LiftBin(int hb, int hg, int hr)
        {
            var sum = hb + hg + hr;
            return hb * LiftQ / sum * (LiftQ + 1) + hg * LiftQ / sum;
        }

        /// <summary>The largest distance between a candidate's feet column and one of its seed columns.</summary>
        internal int MaxSeedSpan;

        private void BuildIndex()
        {
            var postings = new List<(int Colour, int Code, int Row)>();
            var lifted = new List<(int Bin, int Code, int Row, int Art)>();
            for (var k = 0; k < Layouts.Length; k++)
            {
                var c = Layouts[k];
                for (var si = 0; si < c.SeedXs.Length; si++)
                {
                    var x = c.SeedXs[si];
                    MaxSeedSpan = Math.Max(MaxSeedSpan, Math.Abs(x - c.Frame.OriginX));
                    for (var i = c.BandStart[x]; i < c.BandEnd[x]; i++)
                    {
                        ref readonly var p = ref c.Pixels[i];
                        if (Math.Max(p.B, Math.Max(p.G, p.R)) <= 51) continue;
                        if (!ColourIds.TryGetValue(p.Packed, out var id)) ColourIds[p.Packed] = id = ColourIds.Count;
                        postings.Add((id, k * 3 + si, p.Y - c.Frame.OriginY));
                    }
                    for (var i = c.BandStart[x]; i < c.BandEnd[x]; i++)
                    {
                        ref readonly var p = ref c.Pixels[i];
                        if (Math.Max(p.B, Math.Max(p.G, p.R)) <= 51 || !p.Foggable) continue;
                        lifted.Add((LiftBin(255 - p.B, 255 - p.G, 255 - p.R) * LiftRows + p.Y - c.Frame.OriginY + FootholdSlices.Above, k * 3 + si, p.Y - c.Frame.OriginY, p.Packed));
                    }
                }
            }
            // One entry per (bin, row, colour), holding every seed column that posts it: the lift is checked once per entry.
            lifted.Sort((a, b) => a.Bin != b.Bin ? a.Bin.CompareTo(b.Bin) : a.Art != b.Art ? a.Art.CompareTo(b.Art) : a.Code.CompareTo(b.Code));
            LiftStart = new int[LiftBins * LiftRows + 1];
            var entries = new List<LiftEntry>();
            LiftCodes = new int[lifted.Count];
            for (var i = 0; i < lifted.Count; i++)
            {
                LiftCodes[i] = lifted[i].Code;
                if (i > 0 && lifted[i].Bin == lifted[i - 1].Bin && lifted[i].Art == lifted[i - 1].Art) { entries[^1] = entries[^1] with { CodeEnd = i + 1 }; continue; }
                var t = lifted[i].Art;
                int hb = 255 - (t & 0xFF), hg = 255 - (t >> 8 & 0xFF), hr = 255 - (t >> 16);
                entries.Add(new LiftEntry(t & 0xFF, t >> 8 & 0xFF, t >> 16, hb, hg, hr, 1.0 / (hb * hb + hg * hg + hr * hr), lifted[i].Row, i, i + 1));
                LiftStart[lifted[i].Bin + 1]++;
            }
            LiftEntries = [.. entries];
            for (var i = 0; i < LiftBins * LiftRows; i++) LiftStart[i + 1] += LiftStart[i];
            postings.Sort((a, b) => a.Colour != b.Colour ? a.Colour.CompareTo(b.Colour) : a.Code != b.Code ? a.Code.CompareTo(b.Code) : a.Row.CompareTo(b.Row));
            PostStart = new int[ColourIds.Count + 1];
            PostCode = new int[postings.Count];
            PostRow = new int[postings.Count];
            for (var i = 0; i < postings.Count; i++) { PostStart[postings[i].Colour + 1]++; PostCode[i] = postings[i].Code; PostRow[i] = postings[i].Row; }
            for (var i = 0; i < ColourIds.Count; i++) PostStart[i + 1] += PostStart[i];
        }

        /// <summary>The set of a candidate list, laid out on its first use and kept while the list lives (the list must not change after).</summary>
        public static CandidateSet Of(IReadOnlyList<Candidate> candidates, int seedPixels) => Of(candidates, seedPixels, out _);

        internal static CandidateSet Of(IReadOnlyList<Candidate> candidates, int seedPixels, out bool built)
        {
            built = false;
            if (_cache.TryGetValue(candidates, out var set) && set.SeedPixels == seedPixels && set.Candidates.Count == candidates.Count) return set;
            set = new CandidateSet(candidates, seedPixels);
            _cache.AddOrUpdate(candidates, set);
            built = true;
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

    /// <summary>
    /// The lift index's vote: a screen pixel votes for a posting when the one lift that fits it best lies between LiftMin and
    /// LiftMax and leaves no channel more than LiftTolerance off; pixels with less headroom than LiftMinHeadroom (near white),
    /// or whose headroom direction is too uncertain to search fewer than LiftMaxRadius bins around, do not vote. These retrieve,
    /// they decide nothing: the lax seed test and the whole score still judge every placement retrieved. Measured on frame 511
    /// (votes needed = SeedPixels, 3): at tolerance 3 meso 141 is retrieved up to 6 votes needed and the Blue Snail and the Wild
    /// Boar beyond 7; at tolerance 2 the meso up to 4, the snail and the boar up to 8.
    /// </summary>
    private const int LiftTolerance = 3, LiftMinHeadroom = 48, LiftMaxRadius = 8;
    private const double LiftMin = 0.1, LiftMax = 0.95;

    /// <summary>k / 765 for every summed channel difference k: a clean pixel's distance, as the general formula gives it with no lift.</summary>
    private static readonly double[] Inv765 = [.. Enumerable.Range(0, 766).Select(k => k / 765.0)];

    /// <summary>Every placement that matches, the best per place: overlapping finds keep the one with the least distance.</summary>
    public static List<Find> Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IReadOnlyList<FootholdSlices.Run> runs, IReadOnlyList<Candidate> candidates, Options? options = null, Stats? stats = null)
    {
        stats ??= new Stats();
        var set = CandidateSet.Of(candidates, (options ?? new Options()).SeedPixels, out var built);
        if (built) stats.IndexMs += set.BuildMs;
        return Search(frame, frameWidth, mask, mosaic, cameraX, cameraY, runs, set, options, stats);
    }

    /// <summary>Every placement that matches, as the overload over a candidate list, with the candidates laid out already.</summary>
    public static List<Find> Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IReadOnlyList<FootholdSlices.Run> runs, CandidateSet set, Options? options = null, Stats? stats = null)
    {
        var o = options ?? new Options();
        stats ??= new Stats();
        if (set.SeedPixels != o.SeedPixels) { set = new CandidateSet(set.Candidates, o.SeedPixels); stats.IndexMs += set.BuildMs; }
        var layouts = set.Layouts;
        if (runs.Count == 0 || layouts.Length == 0) return [];
        // Only the rows a placement can reach: every line the runs hold, the floor rows either side, and the tallest frame.
        int minLine = int.MaxValue, maxLine = int.MinValue, above = 0, below = 0;
        foreach (var run in runs)
            foreach (var sl in run.Slices) { minLine = Math.Min(minLine, sl.LineY); maxLine = Math.Max(maxLine, sl.LineY); }
        foreach (var l in layouts) { above = Math.Max(above, l.Frame.OriginY); below = Math.Max(below, l.Frame.Height - 1 - l.Frame.OriginY); }
        var screen = new Screen(frame, frameWidth, mask, mosaic, cameraX, cameraY, minLine - o.FloorRows - above, maxLine + o.FloorRows + below);
        var work = new Work(set.MaxColumn, set.MaxWidth);
        var tried = new HashSet<(int, int, int)>();
        int floorRows = o.FloorRows, nd = 2 * floorRows + 1;
        var votes = new int[layouts.Length * 3 * nd];
        var liftVotes = new int[layouts.Length * 3 * nd];
        var touched = new List<int>();
        var hits = new List<int>();
        var keyed = new List<(long Key, Find Find)>();
        var queryWatch = new System.Diagnostics.Stopwatch();
        for (var ri = 0; ri < runs.Count; ri++)
        {
            var run = runs[ri];
            // The line under every column of the run (a run's slices are contiguous; a gap, if any, holds int.MinValue).
            int x0 = run.X0, x1 = run.X1;
            var lineAt = new int[x1 - x0 + 1];
            Array.Fill(lineAt, int.MinValue);
            foreach (var sl in run.Slices) lineAt[sl.X - x0] = sl.LineY;
            int LineAt(int x) => (uint)(x - x0) < (uint)lineAt.Length ? lineAt[x - x0] : int.MinValue;
            // Every slice of a run seeds, interrupted ones too: one known pixel inside a sprite's body (a hole, or a colour the
            // background happens to share) interrupts the slice, and Dark Stump 23 of frame 511 had all three seed columns on such slices.
            foreach (var slice in run.Slices)
            {
                hits.Clear();
                queryWatch.Start();
                Vote(slice);
                queryWatch.Stop();
                stats.Retrieved += hits.Count;
                // In the enumeration's order: candidate, seed column, row.
                hits.Sort();
                foreach (var hit in hits)
                {
                    int k = hit / nd / 3, si = hit / nd % 3, dy = hit % nd - floorRows;
                    var c = layouts[k];
                    var f = c.Frame;
                    var left = slice.X - c.SeedXs[si];
                    var feetLine = LineAt(left + f.OriginX);
                    var line = feetLine != int.MinValue ? feetLine : slice.LineY;
                    int feetY = line + dy, top = feetY - f.OriginY;
                    if (!tried.Add((k, left, top))) continue;
                    // The seed column the enumeration would have tried this placement on: its first route through the run (the
                    // leftmost slice from which one of its seed columns reaches it), so the index only ever drops placements.
                    int firstX = slice.X, firstSi = si, firstDy = dy;
                    for (var sj = 0; sj < c.SeedXs.Length; sj++)
                    {
                        var xj = left + c.SeedXs[sj];
                        if (xj >= firstX || LineAt(xj) == int.MinValue) continue;
                        var lj = feetLine != int.MinValue ? feetLine : LineAt(xj);
                        if (Math.Abs(feetY - lj) > floorRows) continue;
                        firstX = xj; firstSi = sj; firstDy = feetY - lj;
                    }
                    var seedX = c.SeedXs[firstSi];
                    stats.Placements++;
                    // The lax check: the seed column, its band rows only.
                    var seed = Column(screen, c.Pixels, c.BandStart[seedX], c.BandEnd[seedX], left + seedX, top, o.MaxLift, o.LiftGates, work);
                    if (seed.N == 0 || seed.Residual / seed.N > o.SeedDistance) continue;
                    stats.SeedsPassed++;
                    // Then every column, whole. Passes on its screen distance for now; the art-units limit is judged after kept finds
                    // explain what covers it (the boar's columns behind the snail would fail it here), so only the screen distance,
                    // the share and the pixels compared may stop a placement early.
                    var find = Score(screen, c, left, top, o, work, stats);
                    if (find is not null && find.Columns > 0 && find.Pixels >= o.MinCompared * f.PixelX.Length && find.Share >= o.MinShare && find.Distance <= o.MaxDistance)
                        keyed.Add(((((long)ri * 65536 + (firstX - x0 + 1024)) * layouts.Length + k) * 3 * nd + firstSi * nd + firstDy + floorRows, find));
                }
            }

            // The exact votes of one slice: every screen pixel that may hold something, in the rows a seed column's band can reach,
            // against the postings of its colour; a vote goes to the placement it implies (the line at that placement's feet column,
            // the posting's row and the pixel's), and a placement with SeedPixels exact votes or SeedPixels lift votes is retrieved.
            void Vote(FootholdSlices.Slice sl)
            {
                int lo = sl.LineY, hi = sl.LineY;
                for (var x = Math.Max(x0, sl.X - set.MaxSeedSpan); x <= Math.Min(x1, sl.X + set.MaxSeedSpan); x++)
                    if (lineAt[x - x0] != int.MinValue) { lo = Math.Min(lo, lineAt[x - x0]); hi = Math.Max(hi, lineAt[x - x0]); }
                int y0 = Math.Max(screen.Y0, lo - floorRows - FootholdSlices.Above), y1 = Math.Min(screen.Y0 + screen.Rows - 1, hi + floorRows + FootholdSlices.Below);
                if ((uint)sl.X >= (uint)screen.Width) return;
                var colBase = sl.X * screen.Rows - screen.Y0;
                for (var y = y0; y <= y1; y++)
                {
                    var v = screen.Cells[colBase + y];
                    var cell = (int)(v >> 32);
                    var s = (uint)v;
                    if (cell == Screen.Never || cell == (int)s) continue;
                    LiftVote(sl, y, s, lo, hi);
                    if (!set.ColourIds.TryGetValue((int)s, out var id)) continue;
                    for (var i = set.PostStart[id]; i < set.PostStart[id + 1]; i++)
                    {
                        var code = set.PostCode[i];
                        var c = layouts[code / 3];
                        var feetX = sl.X - c.SeedXs[code % 3] + c.Frame.OriginX;
                        var l = LineAt(feetX);
                        var dy = y - set.PostRow[i] - (l != int.MinValue ? l : sl.LineY);
                        if (dy < -floorRows || dy > floorRows) continue;
                        var slot = code * nd + dy + floorRows;
                        if (votes[slot] == 0 && liftVotes[slot] == 0) touched.Add(slot);
                        votes[slot]++;
                    }
                }
                foreach (var slot in touched)
                {
                    if (votes[slot] >= o.SeedPixels || liftVotes[slot] >= o.SeedPixels) hits.Add(slot);
                    votes[slot] = 0; liftVotes[slot] = 0;
                }
                touched.Clear();
            }

            // The lift votes of one screen pixel: the postings whose headroom points the way the pixel's does (its bin and the bins
            // its rounding could move it to), each checked whole: the one lift that fits the pixel best must lie between LiftMin and
            // LiftMax and leave no channel more than LiftTolerance off.
            void LiftVote(FootholdSlices.Slice sl, int y, uint s, int lo, int hi)
            {
                // Only postings on the rows that can land here: the posting's row is y less the line at its feet (lo to hi) less dy.
                int r0 = Math.Max(0, y - hi - floorRows + FootholdSlices.Above), r1 = Math.Min(CandidateSet.LiftRows - 1, y - lo + floorRows + FootholdSlices.Above);
                if (r0 > r1) return;
                int sb = (int)(s & 0xFF), sg = (int)(s >> 8 & 0xFF), sr = (int)(s >> 16);
                int hb = 255 - sb, hg = 255 - sg, hr = 255 - sr, sum = hb + hg + hr;
                if (sum < LiftMinHeadroom || sum <= 3 * LiftTolerance) return;
                // How far the headroom's shares can move when every channel is LiftTolerance off: |du| <= tol (1 + 3u) / (sum - 3 tol).
                const int q = CandidateSet.LiftQ;
                double slack = sum - 3.0 * LiftTolerance, us = (double)hb / sum, vs = (double)hg / sum;
                int ru = (int)Math.Ceiling(q * LiftTolerance * (1 + 3 * us) / slack), rv = (int)Math.Ceiling(q * LiftTolerance * (1 + 3 * vs) / slack);
                if (ru > LiftMaxRadius || rv > LiftMaxRadius) return;
                int ub = hb * q / sum, vb = hg * q / sum;
                var entries = set.LiftEntries;
                for (var u = Math.Max(0, ub - ru); u <= Math.Min(q, ub + ru); u++)
                for (var v = Math.Max(0, vb - rv); v <= Math.Min(q, vb + rv); v++)
                {
                    var bin = (u * (q + 1) + v) * CandidateSet.LiftRows;
                    int i0 = set.LiftStart[bin + r0], i1 = set.LiftStart[bin + r1 + 1];
                    for (var i = i0; i < i1; i++)
                    {
                        ref readonly var e = ref entries[i];
                        double a = ((sb - e.B) * e.Hb + (sg - e.G) * e.Hg + (sr - e.R) * e.Hr) * e.InvDen;
                        if (a < LiftMin || a > LiftMax) continue;
                        if (Math.Abs(sb - e.B - a * e.Hb) > LiftTolerance || Math.Abs(sg - e.G - a * e.Hg) > LiftTolerance || Math.Abs(sr - e.R - a * e.Hr) > LiftTolerance) continue;
                        for (var j = e.CodeStart; j < e.CodeEnd; j++)
                        {
                            var code = set.LiftCodes[j];
                            var c = layouts[code / 3];
                            var l = LineAt(sl.X - c.SeedXs[code % 3] + c.Frame.OriginX);
                            var dy = y - e.Row - (l != int.MinValue ? l : sl.LineY);
                            if (dy < -floorRows || dy > floorRows) continue;
                            var slot = code * nd + dy + floorRows;
                            if (votes[slot] == 0 && liftVotes[slot] == 0) touched.Add(slot);
                            liftVotes[slot]++;
                        }
                    }
                }
            }
        }
        stats.QueryMs += queryWatch.Elapsed.TotalMilliseconds;
        // The finds in the enumeration's order (run, slice of the first route, candidate, seed column, row), which breaks ties below as it always did.
        keyed.Sort((a, b) => a.Key.CompareTo(b.Key));
        var finds = keyed.Select(e => e.Find).ToList();
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
