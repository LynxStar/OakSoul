namespace OakSoul.Detect;

/// <summary>The result of registering a frame against a background: the integer camera and how well it matched.</summary>
public readonly record struct Registration(int CameraX, int CameraY, int Matches, int Compared, int GuessMatches, int GuessCompared, int Samples)
{
    public double Score => Compared == 0 ? 0 : (double)Matches / Compared;
    public double GuessScore => GuessCompared == 0 ? 0 : (double)GuessMatches / GuessCompared;
}

/// <summary>
/// Finds the camera (world position of the screen's top-left, integer) that best aligns a frame's viewport with a
/// world-space background, searching a small window around a guess. Sprites and tiles are drawn 1:1 at integer
/// positions, so at the right offset textured background pixels match exactly; a match is a colour difference of at
/// most <see cref="MatchTolerance"/>. Only textured frame pixels are sampled (flat sky matches at every offset and
/// says nothing), excluded screen pixels are skipped, and only reference pixels marked usable are compared.
/// </summary>
public static class CameraRegistration
{
    /// <summary>
    /// Sum of absolute B, G, R differences that still counts as the same pixel. Keyframes are lossless and static
    /// tiles match at 0; the slack is for replaying the x264 video, untested here.
    /// </summary>
    public const int MatchTolerance = 30;

    /// <summary>Sampling grid step in pixels, and the luma step to a neighbour that makes a pixel "textured" (chosen, not tuned).</summary>
    public const int GridStep = 5, TextureLuma = 24;

    /// <summary>
    /// At most this many samples per frame (the mosaic build; the per-frame detector uses half). Searches of more than
    /// 50 offsets first score every third sample over the window, then all samples within two pixels of the best.
    /// </summary>
    public const int MaxSamples = 3000;

    /// <summary>Picks textured, non-excluded sample points of the viewport (viewport coordinates, as y * width + x).</summary>
    public static int[] Samples(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int maxSamples = MaxSamples)
    {
        var list = new List<int>(8192);
        Textured(frame, frameWidth, mask, list);
        if (list.Count <= maxSamples) return list.ToArray();
        var result = new int[maxSamples];
        for (var i = 0; i < maxSamples; i++) result[i] = list[(int)((long)i * list.Count / maxSamples)];
        return result;
    }

    /// <summary>
    /// The same samples in the same order, picked into the caller's <paramref name="scratch"/> (cleared first, grown as needed and
    /// kept for the next frame); the span returned is valid until the scratch is next used.
    /// </summary>
    public static ReadOnlySpan<int> Samples(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int maxSamples, List<int> scratch)
    {
        scratch.Clear();
        Textured(frame, frameWidth, mask, scratch);
        var all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(scratch);
        if (all.Length <= maxSamples) return all;
        // Picked in place: the i-th pick reads entry i * count / max >= i, which no earlier pick (written below i) has overwritten.
        var count = all.Length;
        for (var i = 0; i < maxSamples; i++) all[i] = all[(int)((long)i * count / maxSamples)];
        return all[..maxSamples];
    }

    /// <summary>Every textured, non-excluded grid point of the viewport, appended to <paramref name="list"/> row by row.</summary>
    private static void Textured(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, List<int> list)
    {
        int w = mask.Width, h = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y;
        for (var y = GridStep / 2; y < h - 1; y += GridStep)
            for (var x = GridStep / 2; x < w - 1; x += GridStep)
            {
                if (mask.Bits[y * w + x] != 0) continue;
                var o = ((y + vy) * frameWidth + x + vx) * 4;
                var l = Luma(frame, o);
                if (Math.Abs(l - Luma(frame, o + 4)) + Math.Abs(l - Luma(frame, o + frameWidth * 4)) < TextureLuma) continue;
                list.Add(y * w + x);
            }
    }

    /// <summary>
    /// Searches cameras within (<paramref name="rangeX"/>, <paramref name="rangeY"/>) of the guess. The best offset has
    /// the highest fraction of matching samples (at least <paramref name="minCompared"/> compared); ties go to the
    /// offset nearest the guess. Wide searches run coarse to fine: a sparse sample set over the whole window, then the
    /// full set within two pixels of the coarse best.
    /// </summary>
    public static Registration Register(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, ReadOnlySpan<int> samples,
        uint[] reference, byte[] usable, int originX, int originY, int refWidth, int refHeight,
        int guessX, int guessY, int rangeX, int rangeY, int minCompared = 150)
    {
        var gathered = new GatheredSamples();
        gathered.Gather(frame, frameWidth, mask, samples);
        return Register(gathered, reference, usable, originX, originY, refWidth, refHeight, guessX, guessY, rangeX, rangeY, minCompared);
    }

    /// <summary>
    /// The same search over samples already gathered from the frame (<see cref="GatheredSamples.Gather"/>), so several
    /// searches of one frame read its sample colours once.
    /// </summary>
    public static Registration Register(GatheredSamples gathered,
        uint[] reference, byte[] usable, int originX, int originY, int refWidth, int refHeight,
        int guessX, int guessY, int rangeX, int rangeY, int minCompared = 150)
    {
        var values = gathered.Values;
        var sx = gathered.X;
        var sy = gathered.Y;
        var count = gathered.Count;

        (int Matches, int Compared) Score(int camX, int camY, int stride)
        {
            int m = 0, c = 0;
            for (var i = 0; i < count; i += stride)
            {
                int wx = camX + sx[i] - originX, wy = camY + sy[i] - originY;
                if ((uint)wx >= (uint)refWidth || (uint)wy >= (uint)refHeight) continue;
                var idx = wy * refWidth + wx;
                if (usable[idx] == 0) continue;
                c++;
                if (Distance(values[i], reference[idx]) <= MatchTolerance) m++;
            }
            return (m, c);
        }

        var guess = Score(guessX, guessY, 1);
        var best = (X: guessX, Y: guessY, M: guess.Matches, C: guess.Compared);
        bool Better(int m, int c, int x, int y, (int X, int Y, int M, int C) b)
        {
            if (c < minCompared) return false;
            if (b.C < minCompared) return true;
            var lhs = (long)m * b.C; var rhs = (long)b.M * c;
            if (lhs != rhs) return lhs > rhs;
            return Math.Abs(x - guessX) + Math.Abs(y - guessY) < Math.Abs(b.X - guessX) + Math.Abs(b.Y - guessY);
        }

        // Any real search runs coarse to fine: every third sample over the window (the exact-match peak is just as
        // sharp on a third of the samples), then all of them within two pixels of the best.
        var wide = (2 * rangeX + 1) * (2 * rangeY + 1) > 50;
        var stride = wide ? 3 : 1;
        var coarse = (X: guessX, Y: guessY, M: -1, C: 0);
        for (var dy = -rangeY; dy <= rangeY; dy++)
            for (var dx = -rangeX; dx <= rangeX; dx++)
            {
                var (m, c) = Score(guessX + dx, guessY + dy, stride);
                if (Better(m, c, guessX + dx, guessY + dy, coarse)) coarse = (guessX + dx, guessY + dy, m, c);
            }
        if (!wide) best = coarse.C >= minCompared ? coarse : best;
        else if (coarse.C > 0)
        {
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    var (m, c) = Score(coarse.X + dx, coarse.Y + dy, 1);
                    if (Better(m, c, coarse.X + dx, coarse.Y + dy, best)) best = (coarse.X + dx, coarse.Y + dy, m, c);
                }
        }
        return new Registration(best.X, best.Y, Math.Max(0, best.M), best.C, guess.Matches, guess.Compared, count);
    }

    public static int Distance(uint a, uint b) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));

    private static int Luma(ReadOnlySpan<byte> px, int o) => (px[o] + px[o + 1] * 2 + px[o + 2]) >> 2;
}

/// <summary>
/// The colours (b | g &lt;&lt; 8 | r &lt;&lt; 16) and frame coordinates of a frame's registration samples, for
/// <see cref="CameraRegistration.Register(GatheredSamples, uint[], byte[], int, int, int, int, int, int, int, int, int)"/>.
/// The arrays grow as needed and are kept; the first <see cref="Count"/> entries are the last gather's.
/// </summary>
public sealed class GatheredSamples
{
    public uint[] Values { get; private set; } = [];
    public int[] X { get; private set; } = [];
    public int[] Y { get; private set; } = [];
    public int Count { get; private set; }

    /// <summary>Reads each sample's colour from the frame (samples are viewport coordinates, y * mask width + x).</summary>
    public void Gather(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, ReadOnlySpan<int> samples)
    {
        if (Values.Length < samples.Length) { Values = new uint[samples.Length]; X = new int[samples.Length]; Y = new int[samples.Length]; }
        var w = mask.Width;
        for (var i = 0; i < samples.Length; i++)
        {
            int x = samples[i] % w, y = samples[i] / w;
            X[i] = x + mask.Viewport.X; Y[i] = y + mask.Viewport.Y;
            var o = (Y[i] * frameWidth + X[i]) * 4;
            Values[i] = (uint)(frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16);
        }
        Count = samples.Length;
    }
}

/// <summary>
/// A quarter-resolution copy of a mosaic for wide camera searches: each 4x4 block of world pixels (aligned on world
/// coordinates that are multiples of 4) holds the mean colour of its reliable pixels, or nothing when fewer than
/// 12 of 16 are reliable. Matching block means tolerates the up to 2-pixel misalignment of a camera that is not a
/// multiple of 4, so the coarse search can step 4 pixels at a time.
/// </summary>
public sealed class CoarseMosaic
{
    public const int Block = 4, MinPixels = 12;

    public CoarseMosaic(BackgroundMosaic m)
    {
        BlockX0 = FloorDiv(m.OriginX, Block);
        BlockY0 = FloorDiv(m.OriginY, Block);
        Width = FloorDiv(m.OriginX + m.Width - 1, Block) - BlockX0 + 1;
        Height = FloorDiv(m.OriginY + m.Height - 1, Block) - BlockY0 + 1;
        Means = new int[Width * Height];
        Valid = new byte[Width * Height];
        var sum = new int[Width * Height * 3];
        var n = new int[Width * Height];
        for (var y = 0; y < m.Height; y++)
        {
            var by = FloorDiv(m.OriginY + y, Block) - BlockY0;
            for (var x = 0; x < m.Width; x++)
            {
                var i = y * m.Width + x;
                if (m.Usable[i] == 0) continue;
                var b = by * Width + FloorDiv(m.OriginX + x, Block) - BlockX0;
                var v = m.Pixels[i];
                sum[b * 3] += (int)(v & 0xFF); sum[b * 3 + 1] += (int)((v >> 8) & 0xFF); sum[b * 3 + 2] += (int)((v >> 16) & 0xFF);
                n[b]++;
            }
        }
        for (var b = 0; b < n.Length; b++)
        {
            if (n[b] < MinPixels) continue;
            Valid[b] = 1;
            Means[b] = sum[b * 3] / n[b] | sum[b * 3 + 1] / n[b] << 8 | sum[b * 3 + 2] / n[b] << 16;
        }
    }

    /// <summary>World block coordinates of block (0, 0).</summary>
    public int BlockX0 { get; }
    public int BlockY0 { get; }
    public int Width { get; }
    public int Height { get; }
    public int[] Means { get; }
    public byte[] Valid { get; }

    public static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    /// <summary>The frame's block means, which blocks have enough unmasked pixels, and the four-row column sums: allocated on the first search, again only if the mask's size changes.</summary>
    private int[] _means = [], _colB = [], _colG = [], _colR = [], _colN = [];
    private bool[] _ok = [];

    /// <summary>
    /// Mean-absolute-difference search of block means over cameras that are multiples of 4 within <paramref name="range"/>
    /// of the guess; returns the best camera (a multiple of 4), or null when too few blocks overlap.
    /// </summary>
    /// <remarks>
    /// Halving the textured blocks from 1200 to 600 left the number of failed registrations on fx-combat unchanged.
    /// Not reentrant: the block means and column sums are this instance's fields, reused across calls; one instance belongs to one detector, called from one thread.
    /// </remarks>
    public (int X, int Y, double Sad)? Search(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int guessX, int guessY, int range, int maxSamples = 600)
    {
        // Frame block means over unmasked pixels: column sums of four rows at a time, then four columns per block.
        int bw = mask.Width / Block, bh = mask.Height / Block, mw = mask.Width;
        if (_ok.Length != bw * bh) { _means = new int[bw * bh]; _ok = new bool[bw * bh]; }
        if (_colN.Length != mw) { _colB = new int[mw]; _colG = new int[mw]; _colR = new int[mw]; _colN = new int[mw]; }
        // A block below MinPixels must read not ok; a mean is only read where ok, and every ok block's mean is written below.
        Array.Clear(_ok);
        var means = _means; var ok = _ok;
        var colB = _colB; var colG = _colG; var colR = _colR; var colN = _colN;
        var bits = mask.Bits;
        for (var by = 0; by < bh; by++)
        {
            Array.Clear(colB); Array.Clear(colG); Array.Clear(colR); Array.Clear(colN);
            for (var y = by * Block; y < by * Block + Block; y++)
            {
                var o = ((y + mask.Viewport.Y) * frameWidth + mask.Viewport.X) * 4;
                var row = y * mw;
                for (var x = 0; x < bw * Block; x++, o += 4)
                {
                    if (bits[row + x] != 0) continue;
                    colB[x] += frame[o]; colG[x] += frame[o + 1]; colR[x] += frame[o + 2]; colN[x]++;
                }
            }
            for (var bx = 0; bx < bw; bx++)
            {
                int sb = 0, sg = 0, sr = 0, cnt = 0;
                for (var x = bx * Block; x < bx * Block + Block; x++) { sb += colB[x]; sg += colG[x]; sr += colR[x]; cnt += colN[x]; }
                if (cnt < MinPixels) continue;
                ok[by * bw + bx] = true;
                means[by * bw + bx] = sb / cnt | sg / cnt << 8 | sr / cnt << 16;
            }
        }
        var samples = new List<int>();
        for (var by = 0; by < bh - 1; by++)
            for (var bx = 0; bx < bw - 1; bx++)
            {
                var i = by * bw + bx;
                if (!ok[i] || !ok[i + 1] || !ok[i + bw]) continue;
                if (CameraRegistration.Distance((uint)means[i], (uint)means[i + 1]) + CameraRegistration.Distance((uint)means[i], (uint)means[i + bw]) < 36) continue;
                samples.Add(i);
            }
        if (samples.Count > maxSamples) samples = Enumerable.Range(0, maxSamples).Select(k => samples[(int)((long)k * samples.Count / maxSamples)]).ToList();
        if (samples.Count < 50) return null;

        int gx = FloorDiv(guessX, Block), gy = FloorDiv(guessY, Block), r = range / Block;
        int vbx = mask.Viewport.X / Block, vby = mask.Viewport.Y / Block;
        (int X, int Y, double Sad)? best = null;
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                long sad = 0; var c = 0;
                foreach (var i in samples)
                {
                    int wbx = gx + dx + vbx + i % bw - BlockX0, wby = gy + dy + vby + i / bw - BlockY0;
                    if ((uint)wbx >= (uint)Width || (uint)wby >= (uint)Height) continue;
                    var j = wby * Width + wbx;
                    if (Valid[j] == 0) continue;
                    sad += CameraRegistration.Distance((uint)means[i], (uint)Means[j]);
                    c++;
                }
                if (c < Math.Max(50, samples.Count / 4)) continue;
                var mean = (double)sad / c;
                if (best is null || mean < best.Value.Sad) best = ((gx + dx) * Block, (gy + dy) * Block, mean);
            }
        return best;
    }
}
