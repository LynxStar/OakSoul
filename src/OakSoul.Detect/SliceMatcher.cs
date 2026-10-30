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

    /// <summary>A frame's opaque pixels by column (x relative to the frame), and its seed columns: the first with enough colour in the band from each side.</summary>
    private sealed class Columns
    {
        public readonly List<(int Y, int B, int G, int R)>[] ByX;
        public readonly int Seed, SeedRight;

        public Columns(SpriteFrame f, int seedPixels)
        {
            ByX = new List<(int, int, int, int)>[f.Width];
            for (var x = 0; x < f.Width; x++) ByX[x] = [];
            for (var y = 0; y < f.Height; y++)
                for (var x = 0; x < f.Width; x++)
                {
                    var o = (y * f.Width + x) * 4;
                    if (f.Bgra[o + 3] == SpriteFrame.SolidAlpha) ByX[x].Add((y, f.Bgra[o], f.Bgra[o + 1], f.Bgra[o + 2]));
                }
            bool Coloured(int x) =>
                ByX[x].Count(p => p.Y >= f.OriginY - FootholdSlices.Above && p.Y <= f.OriginY + FootholdSlices.Below && Math.Max(p.B, Math.Max(p.G, p.R)) > 51) >= seedPixels;
            Seed = SeedRight = -1;
            for (var x = 0; x < f.Width && Seed < 0; x++) if (Coloured(x)) Seed = x;
            for (var x = f.Width - 1; x >= 0 && SeedRight < 0; x--) if (Coloured(x)) SeedRight = x;
        }
    }

    /// <summary>Every placement that matches, the best per place: overlapping finds keep the one with the least distance.</summary>
    public static List<Find> Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        IReadOnlyList<FootholdSlices.Run> runs, IReadOnlyList<Candidate> candidates, Options? options = null, Stats? stats = null)
    {
        var o = options ?? new Options();
        stats ??= new Stats();
        var frameHeight = frame.Length / 4 / frameWidth;
        var columns = candidates.Select(c => new Columns(c.Frame, o.SeedPixels)).ToArray();
        var finds = new List<Find>();
        var tried = new HashSet<(int, int, int)>();
        foreach (var run in runs)
        {
            var lineAt = run.Slices.ToDictionary(s => s.X, s => s.LineY);
            // Every slice of a run seeds, interrupted ones too: one known pixel inside a sprite's body (a hole, or a colour the
            // background happens to share) interrupts the slice, and Dark Stump 23 of frame 511 had all three seed columns on such slices.
            foreach (var slice in run.Slices)
            {
            for (var k = 0; k < candidates.Count; k++)
            {
                var (name, f) = (candidates[k].Name, candidates[k].Frame);
                var cols = columns[k];
                // The frame's feet on the slice, or its first coloured column from either side on it.
                foreach (var seedX in new[] { f.OriginX, cols.Seed, cols.SeedRight }.Distinct())
                {
                if (seedX < 0 || seedX >= f.Width) continue;
                var left = slice.X - seedX;
                var line = lineAt.TryGetValue(left + f.OriginX, out var l) ? l : slice.LineY;
                for (var dy = -o.FloorRows; dy <= o.FloorRows; dy++)
                {
                    int feetY = line + dy, top = feetY - f.OriginY;
                    if (!tried.Add((k, left, top))) continue;
                    stats.Placements++;
                    // The lax check: the seed column, its band rows only.
                    var seed = Column(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, cols.ByX[seedX], left + seedX, top,
                        f.OriginY - FootholdSlices.Above, f.OriginY + FootholdSlices.Below, o.MaxLift, o.LiftGates, default);
                    if (seed.N == 0 || seed.Residual / seed.N > o.SeedDistance) continue;
                    stats.SeedsPassed++;
                    // Then every column, whole.
                    var find = Score(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, name, f, cols, left, top, o);
                    stats.Columns += find.Columns; stats.Pixels += find.Pixels;
                    // Passes on its screen distance for now; the art-units limit is judged after kept finds explain what covers it
                    // (the boar's columns behind the snail would fail it here).
                    if (find.Columns > 0 && find.Pixels >= o.MinCompared * f.PixelX.Length && find.Share >= o.MinShare && find.Distance <= o.MaxDistance) finds.Add(find);
                }
                }
            }
            }
        }
        if (o.KeepOverlapping) return finds;
        // The best first, explaining its pixels; a later find that overlaps a kept one is scored again on the pixels left and
        // stays only if it still passes on its own (the Wild Boar half behind the Blue Snail on frame 511 does; a copy of the
        // snail a pixel over, left with a sliver, does not).
        var colsOf = new Dictionary<SpriteFrame, Columns>(ReferenceEqualityComparer.Instance);
        for (var k = 0; k < candidates.Count; k++) colsOf.TryAdd(candidates[k].Frame, columns[k]);
        var kept = new List<Find>();
        var explained = new byte[frameWidth * frameHeight];
        foreach (var f in finds.OrderBy(f => f.Distance).ThenByDescending(f => f.Matched))
        {
            var find = kept.Any(k => Overlaps(k, f))
                ? Score(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, f.Name, f.Frame, colsOf[f.Frame], f.Left, f.Top, o, explained)
                : f;
            if (!Accepts(find, o)) continue;
            kept.Add(find);
            Explain(explained, frameWidth, frameHeight, find);
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

    /// <summary>Marks a kept find's opaque pixels as explained.</summary>
    private static void Explain(byte[] explained, int frameWidth, int frameHeight, Find find)
    {
        var f = find.Frame;
        for (var i = 0; i < f.PixelX.Length; i++)
        {
            int x = find.Left + f.PixelX[i], y = find.Top + f.PixelY[i];
            if ((uint)x < (uint)frameWidth && (uint)y < (uint)frameHeight) explained[y * frameWidth + x] = 1;
        }
    }

    /// <summary>A candidate placed with its frame's top-left at (<paramref name="left"/>, <paramref name="top"/>), compared whole: every column's opaque pixels after that column's best lift.</summary>
    public static Find Score(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        Candidate candidate, int left, int top, Options? options = null)
    {
        var o = options ?? new Options();
        return Score(frame, frameWidth, frame.Length / 4 / frameWidth, mask, mosaic, cameraX, cameraY, candidate.Name, candidate.Frame, new Columns(candidate.Frame, o.SeedPixels), left, top, o);
    }

    private static Find Score(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        string name, SpriteFrame f, Columns cols, int left, int top, Options o, ReadOnlySpan<byte> explained = default)
    {
        int compared = 0, matched = 0, pixels = 0;
        double residual = 0, lifted = 0;
        for (var x = f.X0; x <= f.X1; x++)
        {
            var c = Column(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, cols.ByX[x], left + x, top, int.MinValue, int.MaxValue, o.MaxLift, o.LiftGates, explained);
            if (c.N == 0) continue;
            compared++;
            if (c.Residual / c.N <= o.ColumnDistance) matched++;
            pixels += c.N; residual += c.Residual; lifted += c.Lifted;
        }
        return new Find(name, f, left, top, left + f.OriginX, top + f.OriginY, compared, matched, pixels, pixels == 0 ? 1 : residual / pixels, pixels == 0 ? 1 : lifted / pixels);
    }

    /// <summary>
    /// One sprite column (its opaque pixels between sprite rows <paramref name="y0"/> and <paramref name="y1"/>) against the
    /// screen column <paramref name="x"/>: the pixels compared, their summed distance after the best lift toward white, and the
    /// same distance in art units (each pixel's divided by 1 - lift).
    /// </summary>
    private static (int N, double Residual, double Lifted) Column(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, ScreenMask mask, BackgroundMosaic mosaic,
        int cameraX, int cameraY, List<(int Y, int B, int G, int R)> pixels, int x, int top, int y0, int y1, double maxLift, bool gates, ReadOnlySpan<byte> explained)
    {
        if ((uint)x >= (uint)frameWidth) return (0, 0, 0);
        Span<int> s = stackalloc int[3];
        // First pass: the pixels that count, and the lift that fits them best (least squares on s - t = a (255 - t)).
        double num = 0, den = 0;
        var n = 0;
        foreach (var p in pixels)
        {
            if (p.Y < y0 || p.Y > y1) continue;
            var y = top + p.Y;
            if (!Counts(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, x, y, p, explained)) continue;
            var o = (y * frameWidth + x) * 4;
            num += (frame[o] - p.B) * (255.0 - p.B) + (frame[o + 1] - p.G) * (255.0 - p.G) + (frame[o + 2] - p.R) * (255.0 - p.R);
            den += (255.0 - p.B) * (255.0 - p.B) + (255.0 - p.G) * (255.0 - p.G) + (255.0 - p.R) * (255.0 - p.R);
            n++;
        }
        if (n == 0) return (0, 0, 0);
        var a = den == 0 ? 0 : Math.Clamp(num / den, 0, maxLift);
        // Gated, only coloured art with room to lighten says anything under a lift (SpriteMatcher.FogMinChroma, FogMinHeadroom).
        var lifted = gates && a >= 0.02;
        double residual = 0, liftedResidual = 0;
        n = 0;
        foreach (var p in pixels)
        {
            if (p.Y < y0 || p.Y > y1) continue;
            if (lifted && !SpriteMatcher.Foggable(p.B, p.G, p.R)) continue;
            var y = top + p.Y;
            if (!Counts(frame, frameWidth, frameHeight, mask, mosaic, cameraX, cameraY, x, y, p, explained)) continue;
            var o = (y * frameWidth + x) * 4;
            // ... and the screen pixel must still show a hue (SpriteMatcher.FogMinScreenChroma): washed to grey, it fits anything light.
            if (lifted && Math.Max(frame[o], Math.Max(frame[o + 1], frame[o + 2])) - Math.Min(frame[o], Math.Min(frame[o + 1], frame[o + 2])) < SpriteMatcher.FogMinScreenChroma) continue;
            var d = (Math.Abs(frame[o] - (p.B + a * (255 - p.B))) + Math.Abs(frame[o + 1] - (p.G + a * (255 - p.G))) + Math.Abs(frame[o + 2] - (p.R + a * (255 - p.R)))) / 765.0;
            residual += d; liftedResidual += d / (1 - a);
            n++;
        }
        return (n, residual, liftedResidual);
    }

    /// <summary>Whether a screen pixel says anything about a sprite pixel drawn there: on the frame, not masked, not explained by a kept find, and not the trusted background in the sprite's own colour.</summary>
    private static bool Counts(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY,
        int x, int y, (int Y, int B, int G, int R) p, ReadOnlySpan<byte> explained)
    {
        if ((uint)y >= (uint)frameHeight || mask.IsExcluded(x - mask.Viewport.X, y - mask.Viewport.Y)) return false;
        if (explained.Length != 0 && explained[y * frameWidth + x] != 0) return false;
        int wx = cameraX + x - mosaic.OriginX, wy = cameraY + y - mosaic.OriginY;
        if ((uint)wx >= (uint)mosaic.Width || (uint)wy >= (uint)mosaic.Height) return true;
        var i = wy * mosaic.Width + wx;
        return mosaic.ForegroundUsable[i] == 0 || (mosaic.Pixels[i] & 0xFFFFFF) != (uint)(p.B | p.G << 8 | p.R << 16);
    }
}
