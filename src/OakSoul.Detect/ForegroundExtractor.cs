namespace OakSoul.Detect;

/// <summary>What a viewport pixel looks like against the background.</summary>
public enum PixelClass : byte
{
    /// <summary>Same as the background (or masked).</summary>
    Background = 0,
    /// <summary>Differs and is no translucent layer: sprite, effect or text.</summary>
    Solid = 1,
    /// <summary>The background darkened by a translucent black layer: name-tag boxes.</summary>
    Shade = 2,
    /// <summary>The background brightened by a translucent white layer: steam and clouds. Not foreground.</summary>
    Fog = 3,
    /// <summary>Differs, near white and grey: name-tag text (or a white sprite part).</summary>
    Bright = 4,
    /// <summary>Background too dark for a tag box to show, and the frame not brighter: consistent with a box.</summary>
    DarkConsistent = 5,
    /// <summary>The mosaic is unreliable or unseen here.</summary>
    Ignored = 6,
    /// <summary>
    /// Near-white grey that also fits a white layer over the background: steam at high alpha, or name-tag text (white
    /// glyphs over a saturated background fit a white layer too). Not foreground; text for the tag detector.
    /// </summary>
    BrightFog = 7,
}

/// <summary>One connected blob of foreground, in viewport pixels (inclusive box).</summary>
public sealed record Blob(int X0, int Y0, int X1, int Y1, int Pixels, double SumX, double SumY, int Solid = 0)
{
    /// <summary>Fraction of the blob's pixels that differ from the background as no translucent layer would (class Solid or Bright).</summary>
    public double SolidFraction => Pixels == 0 ? 0 : (double)Solid / Pixels;

    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    public double CentroidX => SumX / Pixels;
    public double CentroidY => SumY / Pixels;
    public double Fill => (double)Pixels / (Width * Height);
}

/// <summary>
/// Separates what is drawn over the background: the viewport minus the mosaic at the frame's camera, thresholded,
/// cleaned with a 3x3 morphological open, and split into 8-connected components. Pixels the mosaic marks unreliable
/// (or never saw) are not foreground, whatever they show. Buffers are reused across frames.
/// </summary>
public sealed class ForegroundExtractor
{
    /// <summary>
    /// Sum of absolute B, G, R differences above which a pixel differs from the background. Static background in a
    /// lossless keyframe differs by 0, sprites by hundreds. Swept on fx-combat keyframes: 40 gave 4.1 mobs per frame
    /// (85% on footholds) and 4.1 unknowns, 60 gave 3.7 (84%) and 3.8, 90 gave 3.3 (81%) and 3.7. 60 keeps most of
    /// what 40 finds and leaves room for the noise of the x264 video.
    /// </summary>
    public const int DiffThreshold = 60;

    /// <summary>
    /// Components smaller than this many pixels (after the open) are dropped: stray pixels, spell sparks, the ends of
    /// a weapon swing sticking out of the own-character box. A meso coin (about 18x18 on fx-combat) or a 32x32 item
    /// icon is several hundred pixels; a snail half hidden in unreliable background still keeps a hundred or so.
    /// </summary>
    public const int MinPixels = 40;

    /// <summary>Per-pixel classes of the last frame (see <see cref="PixelClass"/>), viewport row-major.</summary>
    public byte[] Classes => _class;

    private byte[] _raw = [], _open = [], _tmp = [], _class = [];
    private int[] _labels = [], _parent = [];

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The opened foreground mask of the last frame, 1 = foreground, viewport row-major.</summary>
    public byte[] Mask => _open;

    /// <summary>Foreground pixels before the open, and pixels skipped as unreliable, in the last frame.</summary>
    public int RawPixels { get; private set; }
    public int IgnoredPixels { get; private set; }

    /// <summary>Thresholds the difference to the mosaic at the integer camera; returns nothing, fills <see cref="Mask"/>.</summary>
    public unsafe void Difference(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int cameraX, int cameraY)
    {
        int w = mask.Width, h = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y;
        Ensure(w, h);
        Array.Clear(_raw);
        Array.Clear(_class);
        // The screen-space model of the unreliable pixels only holds while the camera stands still.
        var still = _lastCamera == (cameraX, cameraY);
        if (!still) Array.Clear(_screenAge);
        _lastCamera = (cameraX, cameraY);
        int raw = 0, ignored = 0, screen = 0;
        int mw = mosaic.Width, mh = mosaic.Height;
        fixed (byte* framePtr = frame, bits = mask.Bits, rawPtr = _raw, cls = _class, usable = mosaic.ForegroundUsable, age = _screenAge, miss = _screenMiss)
        fixed (uint* pixels = mosaic.Pixels, scr = _screen)
        {
            for (var y = 0; y < h; y++)
            {
                var wy = cameraY + vy + y - mosaic.OriginY;
                var rowInside = (uint)wy < (uint)mh;
                var mrow = wy * mw;
                var src = framePtr + ((y + vy) * frameWidth + vx) * 4;
                var dst = y * w;
                var wx0 = cameraX + vx - mosaic.OriginX;
                for (var x = 0; x < w; x++, src += 4)
                {
                    var p = dst + x;
                    if (bits[p] != 0) continue;
                    int fb = src[0], fg = src[1], fr = src[2];
                    var wx = wx0 + x;
                    int mb, mg, mr;
                    if (rowInside && (uint)wx < (uint)mw && usable[mrow + wx] != 0)
                    {
                        var m = pixels[mrow + wx];
                        mb = (int)(m & 0xFF); mg = (int)((m >> 8) & 0xFF); mr = (int)((m >> 16) & 0xFF);
                    }
                    else
                    {
                        // Unreliable in the mosaic (parallax, water, steam): compare with this pixel's value from the
                        // last frames instead, when it has held still long enough, and keep that model up to date.
                        var sv = scr[p];
                        int sb = (int)(sv & 0xFF), sg = (int)((sv >> 8) & 0xFF), sr = (int)((sv >> 16) & 0xFF);
                        var ds = Math.Abs(fb - sb) + Math.Abs(fg - sg) + Math.Abs(fr - sr);
                        var stable = age[p] >= ScreenStableFrames;
                        if (ds <= ScreenTolerance)
                        {
                            // Follow slow drift (clouds crawl across the sky even while the camera rests), so only an
                            // abrupt change stands out.
                            scr[p] = (uint)(fb | fg << 8 | fr << 16);
                            if (age[p] < byte.MaxValue) age[p]++;
                            miss[p] = 0;
                        }
                        else if (!stable || miss[p] >= ScreenMissFrames)
                        {
                            scr[p] = (uint)(fb | fg << 8 | fr << 16);
                            age[p] = 1;
                            miss[p] = 0;
                        }
                        else miss[p]++;
                        if (!stable) { ignored++; cls[p] = (byte)PixelClass.Ignored; continue; }
                        screen++;
                        mb = sb; mg = sg; mr = sr;
                    }
                    var d = Math.Abs(fb - mb) + Math.Abs(fg - mg) + Math.Abs(fr - mr);
                    if (d <= DiffThreshold)
                    {
                        // Over a dark background a name-tag box changes too little to pass the threshold; remember
                        // that the pixel would be consistent with one, for the tag detector.
                        if (mb + 2 * mg + mr < 4 * DarkBackgroundLuma && fb + 2 * fg + fr <= mb + 2 * mg + mr + 32) cls[p] = (byte)PixelClass.DarkConsistent;
                        continue;
                    }
                    var c = Classify(fb, fg, fr, mb, mg, mr);
                    cls[p] = (byte)c;
                    if (c is not (PixelClass.Fog or PixelClass.BrightFog)) { rawPtr[p] = 1; raw++; }
                }
            }
        }
        RawPixels = raw;
        IgnoredPixels = ignored;
        ScreenModelPixels = screen;
        Open();
    }

    /// <summary>
    /// The screen-space model stands in for the mosaic at an unreliable pixel once the pixel has repeated (within
    /// <see cref="ScreenTolerance"/>) for this many frames with the camera still: 3 keyframes, 1.5 s on fx-combat.
    /// Parallax layers, which make most of the unreliable area, hold perfectly still on screen while the camera does.
    /// </summary>
    public const int ScreenStableFrames = 3;

    /// <summary>
    /// A stable screen value survives this many differing frames before it is replaced, so a mob standing in front of
    /// parallax for a second is still foreground rather than absorbed; one that stays longer becomes background.
    /// </summary>
    public const int ScreenMissFrames = 4;

    /// <summary>Colour difference under which a pixel repeats its screen value (as <see cref="MosaicBuilder.SameTolerance"/>).</summary>
    public const int ScreenTolerance = 24;

    /// <summary>Pixels of the last frame compared with the screen-space model instead of the mosaic.</summary>
    public int ScreenModelPixels { get; private set; }

    private uint[] _screen = [];
    private byte[] _screenAge = [], _screenMiss = [];
    private (int X, int Y) _lastCamera = (int.MinValue, int.MinValue);

    /// <summary>
    /// Mosaic luma under which a translucent box cannot be seen against the background: at alpha 0.65 the darkening of
    /// a background this dark stays under <see cref="DiffThreshold"/>. Such pixels count as consistent with a tag box.
    /// </summary>
    public const int DarkBackgroundLuma = 45;

    /// <summary>
    /// Largest per-channel residual, in levels, for a pixel to count as the background seen through a translucent
    /// black (name-tag box) or white (steam) layer. The per-channel alpha estimates measured inside tag boxes and steam
    /// agreed to about 0.02, a few levels; 12 is a margin, not tuned.
    /// </summary>
    public const int OverlayResidual = 12;

    /// <summary>
    /// Range of the remaining brightness factor (1 - alpha) for a translucent black layer: a single name-tag box
    /// leaves 0.30 to 0.40 of the background (alpha 0.60 to 0.70, measured over the Fire Boar tag, the own tag and
    /// the Dark Axe Stump tags), two overlapping boxes about 0.1; the rounded top and bottom rows leave up to 0.5.
    /// </summary>
    public const double ShadeMin = 0.03, ShadeMax = 0.55;

    /// <summary>
    /// Alpha range of a translucent white layer. The drifting steam of Deep Valley I is white at alpha 0.3 to 0.7
    /// (per-channel estimates agreed to 0.02, e.g. background AB7D49 seen as C4A47F at 0.30 and E7DBCC at 0.72); an
    /// opaque white sprite pixel would read 1.0 and stays foreground.
    /// </summary>
    public const double FogMin = 0.05, FogMax = 0.9;

    /// <summary>
    /// Luma (B + 2G + R) / 4 at or above which a greyish pixel may be name-tag text; tag glyphs are near white. Close
    /// to the own-tag locator's 140; the chroma limit admits the glyphs' anti-aliased edges.
    /// </summary>
    public const int TextLuma = 150, TextChroma = 48;

    /// <summary>Classifies a pixel that differs from the background.</summary>
    public static PixelClass Classify(int fb, int fg, int fr, int mb, int mg, int mr)
    {
        // Near-white grey over near-white grey is cloud against cloud: the horizon band of Deep Valley I drifts and
        // shifts with parallax while staying under the relaxed reliability level. Tag text over a pale background
        // lands here too, which the tag detector counts as text.
        if (IsBright(fb, fg, fr) && IsBright(mb, mg, mr)) return PixelClass.BrightFog;
        var mm = mb * mb + mg * mg + mr * mr;
        if (mm > 900)
        {
            var k = (double)(fb * mb + fg * mg + fr * mr) / mm;
            if (k >= ShadeMin && k <= ShadeMax &&
                Math.Abs(fb - k * mb) <= OverlayResidual && Math.Abs(fg - k * mg) <= OverlayResidual && Math.Abs(fr - k * mr) <= OverlayResidual)
                return PixelClass.Shade;
        }
        int wb = 255 - mb, wg = 255 - mg, wr = 255 - mr;
        var ww = wb * wb + wg * wg + wr * wr;
        if (ww > 900)
        {
            var a = (double)((fb - mb) * wb + (fg - mg) * wg + (fr - mr) * wr) / ww;
            if (a >= FogMin && a <= FogMax &&
                Math.Abs(fb - mb - a * wb) <= OverlayResidual && Math.Abs(fg - mg - a * wg) <= OverlayResidual && Math.Abs(fr - mr - a * wr) <= OverlayResidual)
                return IsBright(fb, fg, fr) ? PixelClass.BrightFog : PixelClass.Fog;
        }
        if (IsBright(fb, fg, fr)) return PixelClass.Bright;
        return PixelClass.Solid;
    }

    public static bool IsBright(int b, int g, int r) =>
        (b + 2 * g + r) >> 2 >= TextLuma && Math.Max(b, Math.Max(g, r)) - Math.Min(b, Math.Min(g, r)) <= TextChroma;

    /// <summary>Clears a rectangle of the foreground mask (viewport coordinates, inclusive), e.g. a name tag already accounted for.</summary>
    public void Clear(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);
        for (var y = y0; y <= y1; y++) Array.Clear(_open, y * Width + x0, Math.Max(0, x1 - x0 + 1));
    }

    /// <summary>8-connected components of the opened mask with at least <paramref name="minPixels"/> pixels.</summary>
    public List<Blob> Components(int minPixels = MinPixels)
    {
        int w = Width, h = Height;
        var labels = _labels;
        var next = 1;
        _parent[0] = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var i = row + x;
                if (_open[i] == 0) { labels[i] = 0; continue; }
                int best = 0;
                // Neighbours already labelled: left, up-left, up, up-right.
                if (x > 0 && labels[i - 1] != 0) best = Union(best, labels[i - 1]);
                if (y > 0)
                {
                    if (x > 0 && labels[i - w - 1] != 0) best = Union(best, labels[i - w - 1]);
                    if (labels[i - w] != 0) best = Union(best, labels[i - w]);
                    if (x < w - 1 && labels[i - w + 1] != 0) best = Union(best, labels[i - w + 1]);
                }
                if (best == 0)
                {
                    if (next >= _parent.Length) Array.Resize(ref _parent, _parent.Length * 2);
                    _parent[next] = next;
                    best = next++;
                }
                labels[i] = best;
            }
        }
        // Per-root statistics in flat arrays indexed by the root label.
        var n = next;
        var x0s = new int[n]; var y0s = new int[n]; var x1s = new int[n]; var y1s = new int[n];
        var counts = new int[n]; var solids = new int[n]; var sx = new long[n]; var sy = new long[n];
        Array.Fill(x0s, int.MaxValue); Array.Fill(y0s, int.MaxValue); Array.Fill(x1s, -1); Array.Fill(y1s, -1);
        for (var l = 1; l < n; l++) _parent[l] = Find(l);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var l = labels[row + x];
                if (l == 0) continue;
                l = _parent[l];
                labels[row + x] = l;
                if (x < x0s[l]) x0s[l] = x;
                if (x > x1s[l]) x1s[l] = x;
                if (y < y0s[l]) y0s[l] = y;
                y1s[l] = y;
                counts[l]++; sx[l] += x; sy[l] += y;
                if (_class[row + x] is (byte)PixelClass.Solid or (byte)PixelClass.Bright) solids[l]++;
            }
        }
        var blobs = new List<Blob>();
        for (var l = 1; l < n; l++)
            if (counts[l] >= minPixels) blobs.Add(new Blob(x0s[l], y0s[l], x1s[l], y1s[l], counts[l], sx[l], sy[l], solids[l]));
        return blobs;
    }

    /// <summary>Foreground pixels of the opened mask inside a box (viewport coordinates, inclusive).</summary>
    public int CountIn(int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            for (var x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++) n += _open[y * Width + x];
        return n;
    }

    private int Union(int a, int b)
    {
        b = Find(b);
        if (a == 0) return b;
        a = Find(a);
        if (a == b) return a;
        if (a < b) { _parent[b] = a; return a; }
        _parent[a] = b;
        return b;
    }

    private int Find(int a)
    {
        while (_parent[a] != a) { _parent[a] = _parent[_parent[a]]; a = _parent[a]; }
        return a;
    }

    /// <summary>
    /// 3x3 open: erode then dilate, each separable into a horizontal and a vertical pass of byte-wise AND or OR over
    /// shifted rows, which the runtime vectorizes. Outside the viewport counts as background for the erosion.
    /// </summary>
    private void Open()
    {
        int w = Width, h = Height, n = w * h;
        // Horizontal erosion: raw shifted left and right (the row ends are background).
        Combine3(_raw, _tmp, n, 1, and: true);
        for (var y = 0; y < h; y++) { _tmp[y * w] = 0; _tmp[y * w + w - 1] = 0; }
        // Vertical erosion: rows above and below.
        Combine3(_tmp, _open, n, w, and: true);
        Array.Clear(_open, 0, w); Array.Clear(_open, n - w, w);
        // Horizontal dilation, then vertical.
        Combine3(_open, _tmp, n, 1, and: false);
        for (var y = 0; y < h; y++)
        {
            _tmp[y * w] = (byte)(_open[y * w] | _open[y * w + 1]);
            _tmp[y * w + w - 1] = (byte)(_open[y * w + w - 1] | _open[y * w + w - 2]);
        }
        Combine3(_tmp, _open, n, w, and: false);
        for (var x = 0; x < w; x++)
        {
            _open[x] = (byte)(_tmp[x] | _tmp[x + w]);
            _open[n - w + x] = (byte)(_tmp[n - w + x] | _tmp[n - 2 * w + x]);
        }
    }

    /// <summary>dst[i] = src[i - step] op src[i] op src[i + step] for step &lt;= i &lt; n - step (the ends are fixed up by the caller).</summary>
    private static void Combine3(byte[] src, byte[] dst, int n, int step, bool and)
    {
        var a = src.AsSpan(0, n - 2 * step);
        var b = src.AsSpan(step, n - 2 * step);
        var c = src.AsSpan(2 * step, n - 2 * step);
        var d = dst.AsSpan(step, n - 2 * step);
        var lanes = System.Numerics.Vector<byte>.Count;
        var i = 0;
        for (; i <= d.Length - lanes; i += lanes)
        {
            var va = new System.Numerics.Vector<byte>(a[i..]);
            var vb = new System.Numerics.Vector<byte>(b[i..]);
            var vc = new System.Numerics.Vector<byte>(c[i..]);
            (and ? va & vb & vc : va | vb | vc).CopyTo(d[i..]);
        }
        for (; i < d.Length; i++) d[i] = (byte)(and ? a[i] & b[i] & c[i] : a[i] | b[i] | c[i]);
    }

    private void Ensure(int w, int h)
    {
        if (Width == w && Height == h) return;
        Width = w; Height = h;
        _raw = new byte[w * h]; _open = new byte[w * h]; _tmp = new byte[w * h]; _class = new byte[w * h];
        _screen = new uint[w * h]; _screenAge = new byte[w * h]; _screenMiss = new byte[w * h];
        _labels = new int[w * h]; _parent = new int[4096];
    }
}
