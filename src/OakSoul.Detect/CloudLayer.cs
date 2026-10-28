using System.Text.Json;

namespace OakSoul.Detect;

/// <summary>
/// One of the client's drifting clouds (Map.wz MapHelper.img weather/cloud/&lt;n&gt;, exported by
/// <c>OakSoul.SpriteExport --clouds</c>): BGRA4444 art, so every channel and the alpha are multiples of 17, mostly grey
/// from 170 to 255.
/// </summary>
public sealed class CloudSprite
{
    public required int Index { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }

    /// <summary>Alpha per pixel, row-major.</summary>
    public required byte[] Alpha { get; init; }

    /// <summary>b | g &lt;&lt; 8 | r &lt;&lt; 16 per pixel.</summary>
    public required int[] Colour { get; init; }

    /// <summary>Spread pixels (x | y &lt;&lt; 16) of alpha at least 51, the ones a placement is verified on.</summary>
    public required int[] Samples { get; init; }

    /// <summary>
    /// Visibly whitening pixels spread over the whole canvas (<see cref="CloudArt.DenseCount"/> of a 3-pixel grid, packed x | y
    /// &lt;&lt; 16): a young cloud, perhaps a fringe at the edge of sight where few of <see cref="Samples"/> show, is verified on these.
    /// </summary>
    public required int[] Dense { get; init; }

    /// <summary>The first <see cref="CloudArt.SparseCount"/> of <see cref="Dense"/>: a young cloud's window is searched on these first.</summary>
    public required int[] Sparse { get; init; }

    /// <summary>
    /// Per pixel, for a grey pixel of alpha at least 17 on the art's 4-bit levels: grey level * 16 + alpha level (each
    /// value / 17); -1 for any other (transparent, or tinted). Discovery looks the implied cloud alpha up by it.
    /// </summary>
    public required short[] Code { get; init; }

    public override string ToString() => $"cloud {Index} {Width}x{Height}";
}

/// <summary>The cloud art of the client, loaded from <c>data/sprites/weather</c>.</summary>
public sealed class CloudArt
{
    public CloudArt(IReadOnlyList<CloudSprite> sprites) => Sprites = sprites;

    public IReadOnlyList<CloudSprite> Sprites { get; }

    /// <summary>Verification samples per cloud.</summary>
    public const int SampleCount = 48;

    /// <summary>The grid <see cref="CloudSprite.Dense"/> is drawn from.</summary>
    public const int DenseStep = 3;

    /// <summary>Pixels in <see cref="CloudSprite.Sparse"/> and <see cref="CloudSprite.Dense"/>: a fringe a fifth of the canvas wide still shows a dozen and fifty.</summary>
    public const int SparseCount = 64, DenseCount = 256;

    /// <summary>Loads <c>weather/cloud.json</c> and the PNGs it names under <paramref name="spritesDir"/>, or null when they were not exported.</summary>
    public static CloudArt? Load(string spritesDir)
    {
        var json = Path.Combine(spritesDir, "weather", "cloud.json");
        if (!File.Exists(json)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var sprites = new List<CloudSprite>();
        foreach (var c in doc.RootElement.GetProperty("clouds").EnumerateArray())
        {
            var file = c.GetProperty("file").GetString()!;
            var (bgra, w, h) = Png.ReadBgra(Path.Combine(spritesDir, file));
            var alpha = new byte[w * h];
            var colour = new int[w * h];
            var code = new short[w * h];
            var solid = new List<int>();
            for (var i = 0; i < w * h; i++)
            {
                alpha[i] = bgra[i * 4 + 3];
                colour[i] = bgra[i * 4] | bgra[i * 4 + 1] << 8 | bgra[i * 4 + 2] << 16;
                if (alpha[i] >= 51) solid.Add(i % w | i / w << 16);
                int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2], a = alpha[i];
                code[i] = (short)(a >= 17 && a % 17 == 0 && b == g && g == r && b % 17 == 0 ? b / 17 * 16 + a / 17 : -1);
            }
            // A spread subset: every k-th in a fixed shuffle, so the samples cover the whole cloud.
            var rng = new Random(sprites.Count * 7919 + 17);
            var shuffled = solid.OrderBy(_ => rng.Next()).Take(SampleCount).ToArray();
            var dense = new List<int>();
            for (var y = 1; y < h; y += DenseStep)
                for (var x = 1; x < w; x += DenseStep)
                    if (alpha[y * w + x] >= 17) dense.Add(x | y << 16);
            var spread = dense.OrderBy(_ => rng.Next()).ToList();
            var name = Path.GetFileNameWithoutExtension(file);
            sprites.Add(new CloudSprite
            {
                Index = int.TryParse(name, out var n) ? n : sprites.Count,
                Width = w, Height = h,
                OriginX = c.GetProperty("originX").GetInt32(), OriginY = c.GetProperty("originY").GetInt32(),
                Alpha = alpha, Colour = colour, Samples = shuffled, Code = code, Dense = [.. spread.Take(DenseCount)], Sparse = [.. spread.Take(SparseCount)],
            });
        }
        return sprites.Count == 0 ? null : new CloudArt(sprites);
    }
}

/// <summary>One cloud seen on the map: its art, its alpha, where it was and how it moves.</summary>
public sealed class Cloud
{
    internal Cloud(int id, CloudSprite sprite, int alpha, double z)
    {
        Id = id; Sprite = sprite; Alpha = alpha; Z = z;
    }

    public int Id { get; }
    public CloudSprite Sprite { get; }

    /// <summary>The cloud's own alpha, 1 to 255: a pixel of art alpha a covers the scene with a * Alpha / 255^2.</summary>
    public int Alpha { get; internal set; }

    /// <summary>Draw order among the clouds: higher is drawn later, over the lower ones.</summary>
    public double Z { get; internal set; }

    /// <summary>Screen position of the canvas's top-left in the current frame, when <see cref="Placed"/>.</summary>
    public int Left { get; internal set; }
    public int Top { get; internal set; }
    public bool Placed { get; internal set; }

    /// <summary>Whether this frame's placement was verified on the pixels (rather than predicted from the motion).</summary>
    public bool Verified { get; internal set; }

    /// <summary>The verification of this frame: exact pixels of those the cloud changes (where the mosaic vouches for the background).</summary>
    public int Exact { get; internal set; }
    public int Informative { get; internal set; }

    /// <summary>World velocity (units per second) fitted to the observations, once two of them span enough time.</summary>
    public double VelocityX { get; internal set; }
    public double VelocityY { get; internal set; }
    public bool HasVelocity { get; internal set; }

    public double FirstSeen { get; internal set; }
    public double LastSeen { get; internal set; }
    public int Observations { get; internal set; }
    internal int Misses;

    /// <summary>Before its own motion is known, the drift assumed (the median of the known clouds' when it was found, else 0).</summary>
    public double PriorVelocityX { get; internal set; }
    internal readonly List<(double T, int X, int Y)> History = [];

    public override string ToString() =>
        $"cloud #{Id} sprite {Sprite.Index} alpha {Alpha} at {Left},{Top}{(Verified ? $" verified {Exact}/{Informative}" : " predicted")}{(HasVelocity ? $" v {VelocityX:0.0},{VelocityY:0.00}" : "")}";
}

/// <summary>
/// The clouds the client draws over a map whose info/cloud is 1 (Deep Valley I and the other Perion maps), in front of the
/// map's layers and its mobs. Each is one of the seven <see cref="CloudArt"/> canvases at an integer screen position with
/// its own alpha, drifting at a constant world velocity (measured on fx-combat-exact: 26 to 49 units per second to the
/// right, under 0.7 units per second up or down), so a cloud found once is predicted from frame to frame and verified on
/// the background pixels it covers.
/// <para>
/// The blend, measured on the background the mosaic vouches for: a scene pixel p under a cloud pixel of colour c and art
/// alpha a, the cloud's alpha G, shows round(p + (c - p) a G / 255^2), rounded once per cloud and channel (clouds over
/// clouds blend in turn, in draw order): <see cref="Blend"/>. On fx-combat-exact's keyframe 496 that is every pixel of a
/// cloud (G 205) the mosaic shows correctly, 17,245 of them, and every pixel of a Wild Boar under two clouds (1,980 of 1,980).
/// </para>
/// <para>
/// New clouds are found where the foreground classes show whitened background no known cloud explains (see
/// <see cref="Discover"/>); a cloud is dropped after repeated failed verifications or once it has gone unseen for
/// <see cref="ForgetSeconds"/>.
/// </para>
/// </summary>
public sealed class CloudLayer
{
    private readonly CloudArt _art;
    private readonly List<Cloud> _clouds = [];
    private int _nextId = 1;
    private double _lastTime = double.NaN;

    /// <param name="vrLeft">The map's view range (info/VRLeft, VRRight): clouds wrap around it (see <see cref="WrapMargin"/>).</param>
    public CloudLayer(CloudArt art, int vrLeft = 0, int vrRight = 0)
    {
        _art = art;
        if (vrRight > vrLeft) { _wrapLo = vrLeft - WrapMargin; _period = vrRight - vrLeft + 2 * WrapMargin; }
    }

    /// <summary>
    /// The client keeps a fixed set of clouds per map visit and moves one that leaves on the right back by a fixed distance:
    /// its origin's x runs over [VRLeft - margin, VRRight + margin) and wraps. Measured on fx-combat-exact (Deep Valley I, VR
    /// -823..2083): eight clouds seen before and after a wrap moved back by 3,259 to 3,266 units (two laps 6,523 and 6,524),
    /// the view range's 2,906 plus 356; canvas 2 at alpha 205 left at origin x 2,260 and appeared at -1,002 between two video
    /// frames (none of the 4,837 background pixels its canvas then covered changed in the frame before, 5,056 of 5,315 exact in
    /// the next), canvas 2 at alpha 194 came back where the same bound puts it, to a unit.
    /// </summary>
    public const int WrapMargin = 178;

    private readonly double _wrapLo, _period;

    /// <summary>A canvas's top-left x with its origin wrapped into the clouds' range.</summary>
    private double Wrap(double x, CloudSprite s)
    {
        if (_period <= 0) return x;
        var o = x + s.OriginX - _wrapLo;
        o -= Math.Floor(o / _period) * _period;
        return o + _wrapLo - s.OriginX;
    }

    public CloudArt Art => _art;

    /// <summary>The clouds tracked, in draw order (lowest first).</summary>
    public IReadOnlyList<Cloud> Clouds => _clouds;

    /// <summary>
    /// Where the clouds seen at least <paramref name="minObservations"/> times stand at <paramref name="time"/>, from their
    /// fitted lines (wrapped), as canvas, alpha and screen top-left for a camera: also before a cloud was first found, since
    /// the set is fixed for the visit. In draw order.
    /// </summary>
    public IEnumerable<(CloudSprite Sprite, int Alpha, int Left, int Top)> PlacementsAt(double time, int camX, int camY, int width, int height, int minObservations = Established)
    {
        foreach (var c in _clouds)
        {
            if (!c.HasVelocity || c.Observations < minObservations) continue;
            var (x, y) = WorldAt(c, time);
            int l = (int)Math.Round(Wrap(x, c.Sprite)) - camX, t = (int)Math.Round(y) - camY;
            if (l < width && t < height && l + c.Sprite.Width > 0 && t + c.Sprite.Height > 0) yield return (c.Sprite, c.Alpha, l, t);
        }
    }

    /// <summary>Where one cloud's wrapped line puts its canvas at <paramref name="time"/> for a camera, when on screen and the cloud seen often enough.</summary>
    public (int Left, int Top)? LinePlacement(Cloud c, double time, int camX, int camY, int width, int height, int minObservations = Established)
    {
        if (!c.HasVelocity || c.Observations < minObservations) return null;
        var (x, y) = WorldAt(c, time);
        int l = (int)Math.Round(Wrap(x, c.Sprite)) - camX, t = (int)Math.Round(y) - camY;
        return l < width && t < height && l + c.Sprite.Width > 0 && t + c.Sprite.Height > 0 ? (l, t) : null;
    }

    /// <summary>Clouds placed in the current frame, in draw order.</summary>
    public IEnumerable<Cloud> Placed => _clouds.Where(c => c.Placed);

    // ---- statistics ----
    public int Frames { get; private set; }
    public int Discovered { get; private set; }
    public int DiscoveryAttempts { get; private set; }
    public long Verifications { get; private set; }
    public long VerifiedPlacements { get; private set; }
    public int Dropped { get; private set; }
    public double Milliseconds { get; private set; }
    public double DiscoveryMilliseconds { get; private set; }

    /// <summary>Time spent predicting and verifying the known clouds (the rest of <see cref="Milliseconds"/> is discovery and the tiles).</summary>
    public double FollowMilliseconds { get; private set; }

    /// <summary>Time spent laying the clouds over the matcher's patches, and how many patches.</summary>
    public double CoverMilliseconds { get; internal set; }
    public long Covers { get; internal set; }

    /// <summary>Discovery time by phase (ms): whitened samples, coarse placements, refinement, exact count.</summary>
    public double[] PhaseMs { get; } = new double[4];

    /// <summary>The fastest drift a new cloud is searched for between two frames, in units per second either way of rightwards (measured 26 to 49).</summary>
    public const double MaxSpeed = 70, MinSpeed = -10;

    /// <summary>
    /// A placement is verified when at least this share of the background pixels it changes shows exactly the blend, with at
    /// least <see cref="MinInformative"/> of them compared. A wrong placement or alpha agrees on a handful by chance; the
    /// mosaic itself is wrong under some of a cloud (fog voted into it where clouds pass most often).
    /// </summary>
    public const double VerifyShare = 0.5;

    public const int MinInformative = 12;

    /// <summary>Observations a cloud keeps (the first eighth and the latest).</summary>
    public const int HistoryLength = 64;

    /// <summary>
    /// A cloud seen this often is part of the map's set and is never dropped (it wraps and comes back); a newer one whose
    /// placements fail (<see cref="MaxMisses"/>) or that is not seen for <see cref="ForgetSeconds"/> is.
    /// </summary>
    public const int Established = 5;

    /// <summary>
    /// A cloud not verified for this long is still searched for around its predicted place (the window grows with the time,
    /// up to <see cref="MaxFollowRadius"/>), but no longer laid over the scene until it is verified again.
    /// </summary>
    public const double TrustSeconds = 2.0;

    /// <summary>
    /// A cloud seen fewer than <see cref="Established"/> times has no motion to trust: unverified for longer than this it is no
    /// longer laid over the scene (a fringe found at the edge of sight, overlapped by one not yet found, would otherwise be
    /// composited at a guessed place into what the next discovery looks at).
    /// </summary>
    public const double YoungTrustSeconds = 0.25;

    private static double Trust(Cloud c) => Young(c) ? YoungTrustSeconds : TrustSeconds;

    /// <summary>Seen too few times, or over too short a span, for its line to be trusted (its motion is then the assumed drift).</summary>
    private static bool Young(Cloud c) => c.Observations < Established || !c.HasVelocity || c.History[^1].T - c.History[0].T < EstablishedSeconds;

    /// <summary>The span of sightings a cloud's line needs before it is trusted.</summary>
    public const double EstablishedSeconds = 1.0;

    /// <summary>Columns a young cloud is looked for around its assumed drift, at most (rows: one).</summary>
    public const int YoungRadius = 24;

    public const int MaxFollowRadius = 40;

    /// <summary>A cloud with a line of seconds verified this recently is placed by its line without a check (on video: every third frame is checked).</summary>
    public const double SkipVerifySeconds = 0.09;

    /// <summary>A cloud whose placements fail this many verifiable frames in a row, over at least <see cref="MissSeconds"/>, is dropped.</summary>
    public const int MaxMisses = 3;

    public const double MissSeconds = 0.5;

    /// <summary>A cloud not verified for this long is forgotten (it left the view, or the map).</summary>
    public const double ForgetSeconds = 30;

    /// <summary>A failed discovery is not retried around the same world place for this long.</summary>
    public const double DiscoveryCooldown = 1.0;

    /// <summary>Discoveries per frame at most (each a few milliseconds).</summary>
    public const int DiscoveriesPerFrame = 1;

    /// <summary>
    /// The discovery budget: every frame adds this many milliseconds of credit (up to <see cref="CreditCap"/>), a discovery
    /// runs while the credit is positive and its time is taken off, so over a replay discovery costs at most this much per frame
    /// on average. The first frame of a map (its entry) starts with <see cref="EntryCredit"/> and may try
    /// <see cref="EntryDiscoveries"/> regions: the clouds then in view are the set's first members.
    /// </summary>
    public const double CreditPerFrame = 0.2, CreditCap = 12, EntryCredit = 30;

    public const int EntryDiscoveries = 4;

    /// <summary>Offline (the mosaic build): every whitened region is worth a search, without a budget.</summary>
    public bool DiscoverEverywhere { get; set; }

    private double _credit;

    /// <summary>Discovery work done (samples looked at), what the budget charges; and its measured cost per unit.</summary>
    public long Work { get; private set; }

    public const double MsPerWork = 2.8e-5;
    private bool _entry = true;

    /// <summary>Receives a line per discovery and drop, when set (OAKSOUL_CLOUD_TRACE).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Forgets every cloud (a map change).</summary>
    public void Clear()
    {
        _clouds.Clear();
        _attempts.Clear();
        _interest.Clear();
        _lastTime = double.NaN;
        _entry = true;
        Array.Fill(_panAt, double.NegativeInfinity);
    }

    /// <summary>
    /// Blends one channel: p seen under a cloud pixel of colour c with weight w = art alpha * cloud alpha (0 to 255^2),
    /// rounded to the nearest level (the numerator is never a half: 255^2 is odd).
    /// </summary>
    public static int Blend(int p, int c, int w) => (p * 65025 + (c - p) * w + 32512) / 65025;

    /// <summary><see cref="Blend"/> on packed b | g &lt;&lt; 8 | r &lt;&lt; 16.</summary>
    public static int BlendPacked(int p, int c, int w) =>
        Blend(p & 0xFF, c & 0xFF, w) | Blend(p >> 8 & 0xFF, c >> 8 & 0xFF, w) << 8 | Blend(p >> 16 & 0xFF, c >> 16 & 0xFF, w) << 16;

    // The frame being processed.
    private int _camX, _camY, _frameWidth, _frameHeight;
    private BackgroundMosaic? _mosaic;
    private ScreenMask? _mask;

    /// <summary>
    /// Follows the known clouds into this frame and looks for new ones. <paramref name="classes"/> are the foreground
    /// extractor's pixel classes of this frame (viewport layout), whose whitened pixels no known cloud covers trigger a
    /// discovery.
    /// </summary>
    public void Update(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, BackgroundMosaic mosaic, int camX, int camY, double time, byte[]? classes)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        if (!double.IsNaN(_lastTime) && Frames > 1)
        {
            // The edges the camera moved toward: what enters there is world the view had not shown.
            if (camX > _camX) _panAt[(int)EntryEdge.Right] = time; else if (camX < _camX) _panAt[(int)EntryEdge.Left] = time;
            if (camY > _camY) _panAt[(int)EntryEdge.Bottom] = time; else if (camY < _camY) _panAt[(int)EntryEdge.Top] = time;
        }
        _camX = camX; _camY = camY; _frameWidth = frameWidth; _frameHeight = frame.Length / 4 / frameWidth;
        _mosaic = mosaic; _mask = mask;
        var dt = double.IsNaN(_lastTime) ? 0 : time - _lastTime;
        if (dt < 0) { Clear(); dt = 0; }
        _lastTime = time;
        var screen = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(frame);

        // Predict every cloud, then verify each around its prediction with the others at theirs.
        foreach (var c in _clouds) Predict(c, time);
        // Clouds over clouds are followed twice: first each to its best place with the others at their predictions, then
        // verified there (a pixel or so around) with the others at those places.
        var overlapping = _clouds.Where(c => c.Placed && _clouds.Any(o => !ReferenceEquals(o, c) && o.Placed && Overlap(c, o))).ToList();
        foreach (var c in overlapping) Follow(c, screen, time, tentative: true);
        foreach (var c in _clouds)
        {
            if (!c.Placed) continue;
            Follow(c, screen, time, narrow: overlapping.Contains(c));
        }
        for (var i = _clouds.Count - 1; i >= 0; i--)
        {
            var c = _clouds[i];
            if (Young(c) && (c.Misses >= MaxMisses && time - c.LastSeen >= MissSeconds || time - c.LastSeen > ForgetSeconds)) { _clouds.RemoveAt(i); Dropped++; Trace?.Invoke($"drop {c} misses {c.Misses} unseen {time - c.LastSeen:0.0} s"); }
        }
        var tD = System.Diagnostics.Stopwatch.GetTimestamp();
        FollowMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(t0, tD).TotalMilliseconds;
        _credit = _entry ? EntryCredit : Math.Min(CreditCap, _credit + CreditPerFrame);
        if (classes is not null) DiscoverFromClasses(screen, classes, time);
        // The entry is an allowance of its own: what it overran is not carried into the following frames.
        if (_entry) _credit = Math.Max(_credit, 0);
        _entry = false;
        DiscoveryMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(tD).TotalMilliseconds;
        BuildTiles();
        Milliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Places a cloud where its motion says it is now (its last place when the motion is not known yet).</summary>
    private void Predict(Cloud c, double time)
    {
        var (x, y) = WorldAt(c, time);
        x = Wrap(x, c.Sprite);
        c.Left = (int)Math.Round(x) - _camX;
        c.Top = (int)Math.Round(y) - _camY;
        c.Verified = false;
        c.Exact = c.Informative = 0;
        c.Placed = c.Left < _frameWidth && c.Top < _frameHeight && c.Left + c.Sprite.Width > 0 && c.Top + c.Sprite.Height > 0;
    }

    private static (double X, double Y) WorldAt(Cloud c, double time)
    {
        var last = c.History[^1];
        if (!c.HasVelocity) return (last.X + c.PriorVelocityX * (time - last.T), last.Y);
        // The line through the observations, evaluated now.
        var (t0, x0, y0) = Anchor(c);
        return (x0 + c.VelocityX * (time - t0), y0 + c.VelocityY * (time - t0));
    }

    /// <summary>The fitted position at the mean observation time.</summary>
    private static (double T, double X, double Y) Anchor(Cloud c)
    {
        double st = 0, sx = 0, sy = 0;
        foreach (var (t, x, y) in c.History) { st += t; sx += x; sy += y; }
        var n = c.History.Count;
        return (st / n, sx / n, sy / n);
    }

    private void Observe(Cloud c, double time)
    {
        // The history keeps the unwrapped x: the lap that puts this sighting nearest the line so far.
        var wx = c.Left + _camX;
        if (_period > 0 && c.History.Count > 0) wx += (int)(Math.Round((WorldAt(c, time).X - wx) / _period) * _period);
        c.History.Add((time, wx, c.Top + _camY));
        // The first sightings and the latest: the line rests on the whole span.
        if (c.History.Count > HistoryLength) c.History.RemoveAt(HistoryLength / 8);
        c.Observations++;
        c.LastSeen = time;
        c.Misses = 0;
        var span = c.History[^1].T - c.History[0].T;
        if (c.History.Count >= 2 && span >= 0.2)
        {
            var (tm, xm, ym) = Anchor(c);
            double stt = 0, stx = 0, sty = 0;
            foreach (var (t, x, y) in c.History) { stt += (t - tm) * (t - tm); stx += (t - tm) * (x - xm); sty += (t - tm) * (y - ym); }
            c.VelocityX = stx / stt;
            c.VelocityY = sty / stt;
            c.HasVelocity = true;
        }
    }

    /// <summary>Searches the window the cloud's motion allows around its prediction and keeps the best verified placement.</summary>
    private void Follow(Cloud c, ReadOnlySpan<int> screen, double time, bool tentative = false, bool narrow = false)
    {
        var since = Math.Max(0, time - c.LastSeen);
        int x0, x1, y0, y1;
        // A cloud whose line rests on seconds of sightings and that was seen a moment ago is where the line says, or a pixel
        // off: the prediction is verified first and the window searched only when it fails.
        // On video, a cloud whose line rests on several seconds and that was verified within the last few frames is placed by the
        // line alone (verified again every SkipVerifySeconds).
        if (!narrow && !tentative && c.HasVelocity && since < SkipVerifySeconds && c.History[^1].T - c.History[0].T >= 4) return;
        if (!narrow && !tentative && c.HasVelocity && since <= 0.25 && c.History[^1].T - c.History[0].T >= 2)
        {
            var (exact, inf) = Verify(c, c.Left, c.Top, screen, c.Sprite.Samples);
            Verifications++;
            if (inf >= MinInformative && exact >= VerifyShare * inf && exact * 10 >= inf * 9)
            {
                c.Verified = true; c.Exact = exact; c.Informative = inf;
                VerifiedPlacements++;
                Observe(c, time);
                return;
            }
            narrow = true;
        }
        if (narrow) { x0 = c.Left - 1; x1 = c.Left + 1; y0 = c.Top - 1; y1 = c.Top + 1; }
        else if (c.HasVelocity)
        {
            // The fit's error grows with the time since the last observation.
            var rx = Math.Min(MaxFollowRadius, 2 + (int)Math.Ceiling(since * 3));
            var ry = Math.Min(4, 1 + (int)(since * 0.5));
            x0 = c.Left - rx; x1 = c.Left + rx; y0 = c.Top - ry; y1 = c.Top + ry;
        }
        else
        {
            // Around the assumed drift, as far as the known clouds' speeds differ from it (any speed while none is known).
            var (lo, hi) = SpeedRange();
            x0 = c.Left + Math.Max(-YoungRadius, (int)Math.Floor((lo - c.PriorVelocityX) * since) - 2); x1 = c.Left + Math.Min(YoungRadius, (int)Math.Ceiling((hi - c.PriorVelocityX) * since) + 2);
            y0 = c.Top - 1; y1 = c.Top + 1;
        }
        // A cloud that keeps failing (hidden behind the HUD, or its line off) is searched for on every eighth frame only.
        if (!narrow && c.Misses >= MaxMisses && Frames % 8 != 0)
        {
            if (time - c.LastSeen > Trust(c)) c.Placed = false;
            return;
        }
        // A wide window over background the mosaic cannot vouch for verifies nothing: try the prediction first.
        if (!narrow && (x1 - x0) * (y1 - y0) > 50 && Verify(c, c.Left, c.Top, screen, Young(c) ? c.Sprite.Dense : c.Sprite.Samples).Informative < MinInformative)
        {
            if (!c.Verified && time - c.LastSeen > Trust(c)) c.Placed = false;
            return;
        }
        (int Exact, int Inf, int L, int T) best = (-1, 0, c.Left, c.Top);
        var verifiable = false;
        // A young cloud may be a fringe at the edge of sight, where few of the spread samples show: its pixels on a grid, the
        // window on a coarser grid of them first, then a pixel around the best on all.
        var young = Young(c);
        var samples = young ? c.Sprite.Sparse : c.Sprite.Samples;
        Window(c, screen, x0, y0, x1, y1, samples, ref best, ref verifiable);
        if (young && best.Exact >= 0)
        {
            var (bl, bt) = (best.L, best.T);
            best = (-1, 0, c.Left, c.Top);
            Window(c, screen, bl - 1, bt - 1, bl + 1, bt + 1, c.Sprite.Dense, ref best, ref verifiable);
        }
        if (tentative)
        {
            if (best.Exact >= 0) { c.Left = best.L; c.Top = best.T; }
            return;
        }
        if (best.Exact >= 0 && best.Exact >= VerifyShare * best.Inf)
        {
            c.Left = best.L; c.Top = best.T;
            c.Verified = true; c.Exact = best.Exact; c.Informative = best.Inf;
            VerifiedPlacements++;
            Observe(c, time);
        }
        else if (verifiable) c.Misses++;
        // Long unverified: searched for, but not laid over the scene.
        if (!c.Verified && time - c.LastSeen > Trust(c)) c.Placed = false;
        // Unverifiable (over background the mosaic cannot vouch for, or masked): the prediction stands.
    }

    /// <summary>Verifies a cloud at every placement of a window and keeps the best exact share (ties: more exact pixels).</summary>
    private void Window(Cloud c, ReadOnlySpan<int> screen, int x0, int y0, int x1, int y1, int[] samples, ref (int Exact, int Inf, int L, int T) best, ref bool verifiable, int step = 1)
    {
        for (var t = y0; t <= y1; t += step)
            for (var l = x0; l <= x1; l += step)
            {
                var (exact, inf) = Verify(c, l, t, screen, samples);
                Verifications++;
                if (inf >= MinInformative) verifiable = true;
                if (inf >= MinInformative && (best.Exact < 0 || exact * best.Inf > best.Exact * inf || exact * best.Inf == best.Exact * inf && exact > best.Exact)) best = (exact, inf, l, t);
            }
    }

    /// <summary>
    /// Counts, over the given sprite pixels of cloud <paramref name="c"/> placed at (left, top), those whose background the
    /// mosaic vouches for and the cloud changes (informative), and of those the ones the screen shows exactly as the blend of
    /// every placed cloud over the background, in draw order.
    /// </summary>
    private (int Exact, int Informative) Verify(Cloud c, int left, int top, ReadOnlySpan<int> screen, int[] samples)
    {
        var s = c.Sprite;
        var mosaic = _mosaic!;
        var mask = _mask!;
        var vp = mask.Viewport;
        int exact = 0, inf = 0;
        // A young cloud may be overlapped by one not found yet (a pair coming into sight one after the other): a pixel the
        // screen shows whiter than expected, in every channel, may be under that one and counts for nothing.
        var young = Young(c);
        foreach (var packedXY in samples)
        {
            int sx = packedXY & 0xFFFF, sy = packedXY >> 16;
            int x = left + sx, y = top + sy;
            var b = Background(x, y, mosaic, mask, vp);
            if (b < 0) continue;
            var expected = Composite(b, x, y, c, left, top);
            if (expected == b) continue;
            var shown = screen[y * _frameWidth + x] & 0xFFFFFF;
            if (expected == shown) { inf++; exact++; continue; }
            if (young && Whiter(shown, expected)) continue;
            inf++;
        }
        return (exact, inf);
    }

    /// <summary>Whether <paramref name="shown"/> is <paramref name="expected"/> whitened further: no channel lower, together a few levels higher.</summary>
    private static bool Whiter(int shown, int expected)
    {
        int db = (shown & 0xFF) - (expected & 0xFF), dg = (shown >> 8 & 0xFF) - (expected >> 8 & 0xFF), dr = (shown >> 16) - (expected >> 16);
        return db >= 0 && dg >= 0 && dr >= 0 && db + dg + dr >= 3;
    }

    /// <summary>The background the mosaic vouches for at a screen pixel (inside the viewport, unmasked), or -1.</summary>
    private int Background(int x, int y, BackgroundMosaic mosaic, ScreenMask mask, OakSoul.Capture.CaptureRect vp)
    {
        if ((uint)x >= (uint)_frameWidth || (uint)y >= (uint)_frameHeight) return -1;
        int vx = x - vp.X, vy = y - vp.Y;
        if ((uint)vx >= (uint)mask.Width || (uint)vy >= (uint)mask.Height || mask.Bits[vy * mask.Width + vx] != 0) return -1;
        int wx = _camX + x - mosaic.OriginX, wy = _camY + y - mosaic.OriginY;
        if ((uint)wx >= (uint)mosaic.Width || (uint)wy >= (uint)mosaic.Height) return -1;
        var i = wy * mosaic.Width + wx;
        return mosaic.Usable[i] == 0 ? -1 : (int)(mosaic.Pixels[i] & 0xFFFFFF);
    }

    /// <summary>The scene pixel p blended under every placed cloud covering screen (x, y), in draw order; <paramref name="moved"/> placed at (left, top) instead of its own place.</summary>
    private int Composite(int p, int x, int y, Cloud? moved = null, int left = 0, int top = 0)
    {
        foreach (var o in _clouds)
        {
            int l, t;
            if (ReferenceEquals(o, moved)) { l = left; t = top; }
            else if (o.Placed) { l = o.Left; t = o.Top; }
            else continue;
            int qx = x - l, qy = y - t;
            var s = o.Sprite;
            if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
            var k = qy * s.Width + qx;
            var a = s.Alpha[k];
            if (a == 0) continue;
            p = BlendPacked(p, s.Colour[k], a * o.Alpha);
        }
        return p;
    }

    // ---- per-pixel queries for searches that look at the screen directly (the foothold search's anchors and probes) ----

    /// <summary>Tiles of this many pixels square: a tile is marked when a placed cloud's canvas reaches into it.</summary>
    public const int Tile = 8;

    /// <summary>Per tile, how many placed clouds' canvases reach into it (up to <see cref="MaxLayers"/>; more is marked MaxLayers + 1), and which, in draw order.</summary>
    private byte[] _tiles = [];
    private Cloud[] _tileClouds = [];
    private int _tilesWide, _tilesHigh;
    private bool _anyPlaced;

    private void BuildTiles()
    {
        _tilesWide = (_frameWidth + Tile - 1) / Tile; _tilesHigh = (_frameHeight + Tile - 1) / Tile;
        var n = _tilesWide * _tilesHigh;
        if (_tiles.Length < n) { _tiles = new byte[n]; _tileClouds = new Cloud[n * MaxLayers]; }
        else Array.Clear(_tiles, 0, n);
        _anyPlaced = false;
        foreach (var c in _clouds)
        {
            if (!c.Placed) continue;
            _anyPlaced = true;
            int tx0 = Math.Max(0, c.Left / Tile), ty0 = Math.Max(0, c.Top / Tile);
            int tx1 = Math.Min(_tilesWide - 1, (c.Left + c.Sprite.Width - 1) / Tile), ty1 = Math.Min(_tilesHigh - 1, (c.Top + c.Sprite.Height - 1) / Tile);
            for (var ty = ty0; ty <= ty1; ty++)
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var t = ty * _tilesWide + tx;
                    var k = _tiles[t];
                    if (k < MaxLayers) _tileClouds[t * MaxLayers + k] = c;
                    if (k <= MaxLayers) _tiles[t] = (byte)(k + 1);
                }
        }
    }

    /// <summary>Experiment switch (OAKSOUL_CLOUD_PROBES=off): the probes that look at the screen directly ignore the clouds.</summary>
    public static readonly bool ProbesSeeClouds = Environment.GetEnvironmentVariable("OAKSOUL_CLOUD_PROBES") is not ("off" or "0");

    /// <summary>Whether a placed cloud's canvas may cover screen (x, y) (a quick test before <see cref="CompositeAt"/>).</summary>
    public bool MayCover(int x, int y) =>
        _anyPlaced && ProbesSeeClouds && (uint)x < (uint)_frameWidth && (uint)y < (uint)_frameHeight && _tiles[y / Tile * _tilesWide + x / Tile] != 0;

    /// <summary>A colour (packed) as the screen shows it at (x, y) under the placed clouds (the screen's own pixel, (x, y) on it).</summary>
    public int CompositeAt(int colour, int x, int y)
    {
        var t = y / Tile * _tilesWide + x / Tile;
        int n = _tiles[t];
        if (n > MaxLayers) return Composite(colour, x, y);
        for (var j = 0; j < n; j++)
        {
            var c = _tileClouds[t * MaxLayers + j];
            int qx = x - c.Left, qy = y - c.Top;
            var s = c.Sprite;
            if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
            var k = qy * s.Width + qx;
            var a = s.Alpha[k];
            if (a != 0) colour = BlendPacked(colour, s.Colour[k], a * c.Alpha);
        }
        return colour;
    }

    /// <summary>
    /// The 4-bit art colours (every channel a multiple of 17, as the mob art is) that the placed clouds over screen (x, y)
    /// turn into <paramref name="screen"/>; at most <paramref name="found"/>.Length of them. Returns how many.
    /// </summary>
    public int Unfog(int screen, int x, int y, Span<int> found)
    {
        Span<int> colour = stackalloc int[MaxLayers];
        Span<int> weight = stackalloc int[MaxLayers];
        var layers = 0;
        foreach (var c in _clouds)
        {
            if (!c.Placed) continue;
            int qx = x - c.Left, qy = y - c.Top;
            var s = c.Sprite;
            if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
            var k = qy * s.Width + qx;
            if (s.Alpha[k] == 0) continue;
            if (layers == MaxLayers) return 0;
            colour[layers] = s.Colour[k]; weight[layers] = s.Alpha[k] * c.Alpha; layers++;
        }
        if (layers == 0) return 0;
        // Per channel, the art levels that blend to the screen's.
        Span<int> levels = stackalloc int[48];
        Span<int> counts = stackalloc int[3];
        for (var ch = 0; ch < 3; ch++)
        {
            var target = screen >> (8 * ch) & 0xFF;
            for (var v = 0; v <= 255; v += 17)
            {
                var p = v;
                for (var j = 0; j < layers; j++) p = Blend(p, colour[j] >> (8 * ch) & 0xFF, weight[j]);
                if (p == target && counts[ch] < 16) levels[ch * 16 + counts[ch]++] = v;
            }
            if (counts[ch] == 0) return 0;
        }
        var n = 0;
        for (var b = 0; b < counts[0]; b++)
            for (var g = 0; g < counts[1]; g++)
                for (var r = 0; r < counts[2]; r++)
                {
                    if (n == found.Length) return n;
                    found[n++] = levels[b] | levels[16 + g] << 8 | levels[32 + r] << 16;
                }
        return n;
    }

    /// <summary>
    /// The clouds over a screen rectangle, for <see cref="ScreenPatch"/>: per pixel up to <see cref="MaxLayers"/> layers
    /// (colour, weight) in draw order. Returns false when no placed cloud covers any of it.
    /// </summary>
    internal bool Cover(ScreenPatch patch)
    {
        int px0 = patch.X0, py0 = patch.Y0, w = patch.Width, h = patch.Height;
        var any = false;
        foreach (var c in _clouds)
        {
            if (!c.Placed) continue;
            var s = c.Sprite;
            int ix0 = Math.Max(px0, c.Left), iy0 = Math.Max(py0, c.Top), ix1 = Math.Min(px0 + w, c.Left + s.Width), iy1 = Math.Min(py0 + h, c.Top + s.Height);
            if (ix0 >= ix1 || iy0 >= iy1) continue;
            if (!any) { patch.ClearFog(); any = true; }
            for (var y = iy0; y < iy1; y++)
            {
                var srow = (y - c.Top) * s.Width - c.Left;
                var prow = (y - py0) * w - px0;
                for (var x = ix0; x < ix1; x++)
                {
                    var a = s.Alpha[srow + x];
                    if (a == 0) continue;
                    patch.AddFog(prow + x, s.Colour[srow + x], a * c.Alpha);
                }
            }
        }
        return any;
    }

    /// <summary>Layers per patch pixel; a pixel under more clouds is not compared.</summary>
    public const int MaxLayers = 3;

    /// <summary>
    /// A clouded sprite pixel over background the mosaic cannot vouch for is compared only where at least this share of the
    /// scene shows through: on fx-combat-exact's keyframe 496 Dark Stump's die1/2 frame (a light beige disc) reached 0.23 on the
    /// boar's head under two clouds, 114 of its agreeing pixels over the unknown sky under thicker cloud than this.
    /// </summary>
    public const double MinUnknownTransmittance = 0.5;

    // ---- discovery ----

    /// <summary>Grid cells of this many pixels square, sampled on every fourth pixel, map the unexplained whitening.</summary>
    public const int Cell = 8;

    /// <summary>A cell is whitened when at least this many of its four samples are (Fog or BrightFog).</summary>
    public const int CellFog = 2;

    /// <summary>Unexplained whitened cells a region needs before a discovery is tried there (24 cells: 1536 pixels).</summary>
    public const int MinCells = 24;

    /// <summary>
    /// Only clouds that may matter are looked for: whitened regions within this many pixels of a mob no sprite verified or
    /// an unknown blob of the last <see cref="InterestSeconds"/> (a cloud elsewhere hides nothing the detector misses).
    /// </summary>
    public const int InterestReach = 24;

    public const double InterestSeconds = 1.0;

    private readonly List<(double Time, int X0, int Y0, int X1, int Y1)> _interest = [];
    private readonly List<(double Time, int X0, int Y0, int X1, int Y1, int Cells)> _attempts = [];
    private byte[] _cells = [];
    private int[] _labels = [];

    /// <summary>World boxes of the entities whose surroundings are worth a discovery (mobs and unknowns of the last frame).</summary>
    public void Interest(IEnumerable<(int X0, int Y0, int X1, int Y1)> worldBoxes, double time)
    {
        _interest.RemoveAll(i => time - i.Time > InterestSeconds || time < i.Time);
        foreach (var (x0, y0, x1, y1) in worldBoxes) _interest.Add((time, x0, y0, x1, y1));
    }

    private void DiscoverFromClasses(ReadOnlySpan<int> screen, byte[] classes, double time)
    {
        // The budget: a discovery runs while there is credit, its time is debited (the first frame of a map has the entry credit).
        // Below zero only an entry region may still be looked at (see EntryOverdraft).
        if (!DiscoverEverywhere && _credit <= -EntryOverdraft) return;
        var mask = _mask!;
        var vp = mask.Viewport;
        int cw = mask.Width / Cell, ch = mask.Height / Cell;
        if (_cells.Length < cw * ch) { _cells = new byte[cw * ch]; _labels = new int[cw * ch]; }
        var cells = _cells;
        Array.Clear(cells, 0, cw * ch);
        var fogCells = 0;
        for (var cy = 0; cy < ch; cy++)
            for (var cx = 0; cx < cw; cx++)
            {
                var n = 0;
                for (var dy = 2; dy < Cell; dy += 4)
                    for (var dx = 2; dx < Cell; dx += 4)
                    {
                        var k = classes[(cy * Cell + dy) * mask.Width + cx * Cell + dx];
                        if (k is (byte)PixelClass.Fog or (byte)PixelClass.BrightFog) n++;
                    }
                if (n < CellFog) continue;
                // Under a placed cloud the whitening is unexplained only where the screen is whiter still than the placed
                // clouds make the background (a second cloud over or under a known one).
                if (Covered(vp.X + cx * Cell + Cell / 2, vp.Y + cy * Cell + Cell / 2) && !WhiterThanPlaced(screen, vp.X + cx * Cell, vp.Y + cy * Cell)) continue;
                cells[cy * cw + cx] = 2;
                fogCells++;
            }
        if (fogCells < MinEntryCells) return;
        // 4-connected regions of unexplained whitened cells, largest first.
        var labels = _labels;
        Array.Clear(labels, 0, cw * ch);
        var regions = new List<(int Cells, int X0, int Y0, int X1, int Y1)>();
        var stack = new Stack<int>();
        for (var i = 0; i < cw * ch; i++)
        {
            if (cells[i] != 2 || labels[i] != 0) continue;
            var label = regions.Count + 1;
            int count = 0, rx0 = int.MaxValue, ry0 = int.MaxValue, rx1 = -1, ry1 = -1;
            stack.Push(i); labels[i] = label;
            while (stack.Count > 0)
            {
                var j = stack.Pop();
                int jx = j % cw, jy = j / cw;
                count++;
                rx0 = Math.Min(rx0, jx); ry0 = Math.Min(ry0, jy); rx1 = Math.Max(rx1, jx); ry1 = Math.Max(ry1, jy);
                if (jx > 0 && cells[j - 1] == 2 && labels[j - 1] == 0) { labels[j - 1] = label; stack.Push(j - 1); }
                if (jx < cw - 1 && cells[j + 1] == 2 && labels[j + 1] == 0) { labels[j + 1] = label; stack.Push(j + 1); }
                if (jy > 0 && cells[j - cw] == 2 && labels[j - cw] == 0) { labels[j - cw] = label; stack.Push(j - cw); }
                if (jy < ch - 1 && cells[j + cw] == 2 && labels[j + cw] == 0) { labels[j + cw] = label; stack.Push(j + cw); }
            }
            regions.Add((count, rx0, ry0, rx1, ry1));
        }
        _attempts.RemoveAll(a => time - a.Time > DiscoveryCooldown || time < a.Time);
        if (Trace is not null && regions.Count > 0)
            Trace($"regions credit {_credit:0.00}: {string.Join(", ", regions.Where(r => r.Cells >= 4).OrderByDescending(r => r.Cells).Select(r => $"{vp.X + r.X0 * Cell},{vp.Y + r.Y0 * Cell}-{vp.X + (r.X1 + 1) * Cell - 1},{vp.Y + (r.Y1 + 1) * Cell - 1}:{r.Cells}"))}");
        var tried = 0;
        var perFrame = DiscoverEverywhere ? DiscoveriesPerFrame : _entry ? EntryDiscoveries : DiscoveriesPerFrame;
        bool Interesting((int Cells, int X0, int Y0, int X1, int Y1) r)
        {
            int x0 = vp.X + r.X0 * Cell, y0 = vp.Y + r.Y0 * Cell, x1 = vp.X + (r.X1 + 1) * Cell - 1, y1 = vp.Y + (r.Y1 + 1) * Cell - 1;
            return _interest.Any(i => i.X0 - _camX - InterestReach <= x1 && i.X1 - _camX + InterestReach >= x0 && i.Y0 - _camY - InterestReach <= y1 && i.Y1 - _camY + InterestReach >= y0);
        }
        // Where an unknown cloud comes into view first, then regions next to a mob the fog may hide, then the largest: a cloud
        // never seen yet is found wherever it is.
        var entries = new Dictionary<(int, int, int, int), Entry>();
        foreach (var r in regions)
            if (r.Cells >= MinEntryCells && EntryOf(r.X0, r.Y0, r.X1, r.Y1, time) is { } e) entries[(r.X0, r.Y0, r.X1, r.Y1)] = e;
        Entry? EntryAt((int Cells, int X0, int Y0, int X1, int Y1) r) => entries.TryGetValue((r.X0, r.Y0, r.X1, r.Y1), out var e) ? e : null;
        foreach (var r in regions.Where(r => r.Cells >= MinCells || r.Cells >= MinEntryCells && EntryAt(r) is not null)
            .OrderByDescending(r => EntryAt(r) is not null).ThenByDescending(Interesting).ThenByDescending(r => r.Cells))
        {
            var entry = EntryAt(r);
            if (tried >= perFrame || !DiscoverEverywhere && _credit <= (entry is null ? 0 : -EntryOverdraft)) break;
            int x0 = vp.X + r.X0 * Cell, y0 = vp.Y + r.Y0 * Cell, x1 = vp.X + (r.X1 + 1) * Cell - 1, y1 = vp.Y + (r.Y1 + 1) * Cell - 1;
            // A region an attempt covered lately (in world space: the camera moves) is not tried again until the cooldown ends.
            int wcx = _camX + (x0 + x1) / 2, wcy = _camY + (y0 + y1) / 2;
            // ... unless its whitening has grown by half since (a cloud drifting into view shows more of itself).
            if (_attempts.Any(a => wcx >= a.X0 && wcx <= a.X1 && wcy >= a.Y0 && wcy <= a.Y1 && 2 * r.Cells < 3 * a.Cells)) continue;
            tried++;
            var w0 = Work;
            if (entry is not null) EntryAttempts++;
            if (!Discover(screen, x0, y0, x1, y1, time, entry)) _attempts.Add((time, _camX + x0, _camY + y0, _camX + x1, _camY + y1, r.Cells));
            // Charged by the work done, not the clock, so a replay finds the same clouds every time.
            _credit -= (Work - w0) * MsPerWork;
        }
    }

    /// <summary>
    /// The routes by which a cloud of the visit's fixed set comes into sight, besides being there when the map is entered
    /// (measured on fx-combat-exact: the pair over the snail and the boar wrapped at <c>VRLeft - 179</c> 1.2 s apart, 11 and
    /// 12 columns left of the view with the camera at the view range's left bound, drifted in over the parallax sky, and
    /// came into sight where the left wall's background starts). A cloud can be seen only over background the mosaic vouches
    /// for, so it comes into sight across a boundary of that: the view's edge or unknown background beside it. It crosses
    /// one by drifting (every cloud drifts right, so from the left), by a pan of the camera (toward the edge it moved to in
    /// the last <see cref="PanSeconds"/>), or by wrapping (its origin reappears at the wrap column, so its canvas's left
    /// column is known). A whitened region on such a boundary is looked at before any other, among the placements that
    /// reach past that boundary; a region at a wrap column is looked at first too.
    /// </summary>
    internal enum EntryEdge { Left = 0, Right = 1, Top = 2, Bottom = 3 }

    /// <summary>An entry region: the boundaries it lies on (bit per <see cref="EntryEdge"/>) and their screen coordinates (the region's own side).</summary>
    internal sealed record Entry(int Edges, int Left, int Right, int Top, int Bottom, bool Wrap);

    /// <summary>An edge the camera moved toward counts for this long.</summary>
    public const double PanSeconds = 0.5;

    /// <summary>Whitened cells an entry region needs (a fringe of a cloud: 6 cells, 384 pixels).</summary>
    public const int MinEntryCells = 6;

    /// <summary>An entry may be looked at while the credit is no lower than this (ms): the fringe is small and found early only.</summary>
    public const double EntryOverdraft = 2;

    /// <summary>Columns a wrapped cloud may have drifted from its entry column when its region is first looked at.</summary>
    public const int WrapDrift = 48;

    /// <summary>
    /// Pixels beside a region (just outside it, over its rows or columns) of which this share are unknown background or off the
    /// view make that side a boundary of sight.
    /// </summary>
    public const double BoundaryShare = 0.5;

    /// <summary>
    /// The leading fringe of an entry region (columns or rows on the side away from the boundary it came through): over a pair
    /// that entered one after the other, the first cloud shows alone there (about 30 columns on fx-combat-exact).
    /// </summary>
    public const int Fringe = 32;

    /// <summary>Discoveries tried on an entry region.</summary>
    public int EntryAttempts { get; private set; }

    private readonly double[] _panAt = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

    /// <summary>The entry routes a region of whitened cells (cell coordinates in the viewport) lies on, or null.</summary>
    private Entry? EntryOf(int cx0, int cy0, int cx1, int cy1, double time)
    {
        var vp = _mask!.Viewport;
        int x0 = vp.X + cx0 * Cell, y0 = vp.Y + cy0 * Cell, x1 = vp.X + (cx1 + 1) * Cell - 1, y1 = vp.Y + (cy1 + 1) * Cell - 1;
        // The drift: the side the known clouds come from (both while none is known).
        bool right = false, left = false;
        foreach (var c in _clouds) if (c.HasVelocity) { if (c.VelocityX > 0) left = true; else if (c.VelocityX < 0) right = true; }
        if (!left && !right) left = right = true;
        bool Pan(EntryEdge e) => time - _panAt[(int)e] <= PanSeconds;
        var edges = 0;
        if ((left || Pan(EntryEdge.Left)) && Hidden(x0 - 4, y0, x0 - 1, y1)) edges |= 1 << (int)EntryEdge.Left;
        if ((right || Pan(EntryEdge.Right)) && Hidden(x1 + 1, y0, x1 + 4, y1)) edges |= 1 << (int)EntryEdge.Right;
        if (Pan(EntryEdge.Top) && Hidden(x0, y0 - 4, x1, y0 - 1)) edges |= 1 << (int)EntryEdge.Top;
        if (Pan(EntryEdge.Bottom) && Hidden(x0, y1 + 1, x1, y1 + 4)) edges |= 1 << (int)EntryEdge.Bottom;
        // The wrap: a canvas's entry column in view, the region's left at that canvas's first opaque column (or drifted on).
        var wrap = false;
        if (_period > 0)
            foreach (var s in _art.Sprites)
            {
                var l = (int)Math.Round(_wrapLo - s.OriginX) - _camX;
                if (l + s.Width <= vp.X || l >= vp.X + vp.Width) continue;
                var first = Math.Max(vp.X, l + FirstColumn(s));
                if (x0 >= first - Cell && x0 <= first + WrapDrift) wrap = true;
            }
        return edges == 0 && !wrap ? null : new Entry(edges, x0, x1, y0, y1, wrap);
    }

    /// <summary>Whether most of a screen rectangle is out of sight: off the view, masked, or over background the mosaic cannot vouch for.</summary>
    private bool Hidden(int x0, int y0, int x1, int y1)
    {
        var mosaic = _mosaic!; var mask = _mask!; var vp = mask.Viewport;
        int hidden = 0, n = 0;
        for (var y = y0; y <= y1; y += 2)
            for (var x = x0; x <= x1; x++)
            {
                n++;
                if ((uint)(x - vp.X) >= (uint)vp.Width || (uint)(y - vp.Y) >= (uint)vp.Height || Background(x, y, mosaic, mask, vp) < 0) hidden++;
            }
        Work += n;
        return n > 0 && hidden >= BoundaryShare * n;
    }

    private readonly Dictionary<CloudSprite, (int X0, int Y0, int X1, int Y1)> _extent = [];

    /// <summary>A canvas's columns and rows that whiten visibly, first and last.</summary>
    private (int X0, int Y0, int X1, int Y1) Extent(CloudSprite s)
    {
        if (_extent.TryGetValue(s, out var e)) return e;
        int x0 = s.Width, y0 = s.Height, x1 = -1, y1 = -1;
        for (var y = 0; y < s.Height; y++)
            for (var x = 0; x < s.Width; x++)
                if (s.Alpha[y * s.Width + x] >= 17) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
        return _extent[s] = (x0, y0, x1, y1);
    }

    private int FirstColumn(CloudSprite s) => Extent(s).X0;

    /// <summary>
    /// How far a canvas's visible edge may lie from the side of its whitened region facing away from the boundary it came
    /// through: inside by <see cref="EdgeInside"/> (its faint rim whitens no cell), outside by <see cref="EdgeOutside"/>.
    /// </summary>
    public const int EdgeInside = 4, EdgeOutside = 24;

    /// <summary>
    /// Whether a canvas at (l, t) is one of the placements an entry allows: reaching past the boundary it came through, with
    /// its leading edge (the side it came in with) at the region's far side. A cloud coming in from the left shows its right
    /// part, so its last column is the region's last (a pair coming in one after the other: the first one's), and only a few
    /// placements per row remain.
    /// </summary>
    private bool Allows(Entry e, CloudSprite s, int l, int t)
    {
        if (e.Edges == 0) return true;
        var x = Extent(s);
        bool Near(int edge, int side, int outward) => outward > 0 ? edge >= side - EdgeInside && edge <= side + EdgeOutside : edge <= side + EdgeInside && edge >= side - EdgeOutside;
        if ((e.Edges & 1 << (int)EntryEdge.Left) != 0 && l + x.X0 < e.Left && Near(l + x.X1, e.Right, 1)) return true;
        if ((e.Edges & 1 << (int)EntryEdge.Right) != 0 && l + x.X1 > e.Right && Near(l + x.X0, e.Left, -1)) return true;
        if ((e.Edges & 1 << (int)EntryEdge.Top) != 0 && t + x.Y0 < e.Top && Near(t + x.Y1, e.Bottom, 1)) return true;
        if ((e.Edges & 1 << (int)EntryEdge.Bottom) != 0 && t + x.Y1 > e.Bottom && Near(t + x.Y0, e.Top, -1)) return true;
        return false;
    }

    /// <summary>The leading fringe of an entry region (the side away from its boundary), or null when it has one boundary side only or is narrow.</summary>
    private static (int X0, int Y0, int X1, int Y1)? LeadingFringe(Entry e, int x0, int y0, int x1, int y1) => e.Edges switch
    {
        1 << (int)EntryEdge.Left when x1 - x0 > 2 * Fringe => (x1 - Fringe + 1, y0, x1, y1),
        1 << (int)EntryEdge.Right when x1 - x0 > 2 * Fringe => (x0, y0, x0 + Fringe - 1, y1),
        1 << (int)EntryEdge.Top when y1 - y0 > 2 * Fringe => (x0, y1 - Fringe + 1, x1, y1),
        1 << (int)EntryEdge.Bottom when y1 - y0 > 2 * Fringe => (x0, y0, x1, y0 + Fringe - 1),
        _ => null,
    };

    /// <summary>Whether most of a cell's four samples show the screen whiter than the placed clouds make the background there.</summary>
    private bool WhiterThanPlaced(ReadOnlySpan<int> screen, int x0, int y0)
    {
        var mosaic = _mosaic!; var mask = _mask!; var vp = mask.Viewport;
        var n = 0;
        for (var dy = 2; dy < Cell; dy += 4)
            for (var dx = 2; dx < Cell; dx += 4)
            {
                int x = x0 + dx, y = y0 + dy;
                var b = Background(x, y, mosaic, mask, vp);
                if (b >= 0 && Whiter(screen[y * _frameWidth + x] & 0xFFFFFF, Composite(b, x, y))) n++;
            }
        Work += 4;
        return n >= CellFog;
    }

    /// <summary>Whether a placed cloud covers screen (x, y) with a weight that whitens visibly.</summary>
    private bool Covered(int x, int y)
    {
        foreach (var c in _clouds)
        {
            if (!c.Placed) continue;
            int qx = x - c.Left, qy = y - c.Top;
            var s = c.Sprite;
            if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
            if (s.Alpha[qy * s.Width + qx] * c.Alpha >= 17 * 64) return true;
        }
        return false;
    }

    /// <summary>
    /// Fog samples per discovery for the coarse search, for the refinement (where a cloud another overlaps leaves only a few
    /// samples explained by it alone), and the least that make one worth trying.
    /// </summary>
    public const int DiscoverySamples = 40, RefineSamples = 256, MinDiscoverySamples = 24;

    /// <summary>
    /// A cloud found alone is accepted when this share of the background pixels it changes in the region show exactly its
    /// blend (the mosaic is wrong under part of some clouds, where fog was voted into it); one that explains less, but at
    /// least <see cref="PartialShare"/>, is kept provisionally while a second cloud overlapping it is looked for, and the two
    /// are accepted together when they reach this share on the pixels either changes.
    /// </summary>
    public const double DiscoverShare = 0.5, PartialShare = 0.12;

    /// <summary>Exact pixels (every second row and column) a discovered cloud needs at least.</summary>
    public const int MinDiscoverExact = 30;

    /// <summary>Whitened samples a placement must explain exactly at one alpha to be tried at all (of <see cref="RefineSamples"/>).</summary>
    public const int MinVotes = 24;

    /// <summary>Sample data of one discovery round.</summary>
    private sealed class FogSamples(int capacity)
    {
        public int N;
        public readonly int[] X = new int[capacity], Y = new int[capacity], S = new int[capacity], B = new int[capacity];
        public int CentreX, CentreY;

        /// <summary>Per sample, the implied cloud alpha under each grey art code (see <see cref="CloudSprite.Code"/>), 0 where none.</summary>
        public readonly byte[] Implied = new byte[capacity * 256];

        public void Imply()
        {
            for (var i = 0; i < N; i++)
                for (var level = 0; level < 16; level++)
                {
                    var c = level * 17;
                    var grey = c | c << 8 | c << 16;
                    Implied[i * 256 + level * 16] = 0;
                    for (var al = 1; al < 16; al++) Implied[i * 256 + level * 16 + al] = (byte)ImpliedAlpha(S[i], B[i], grey, al * 17);
                }
        }

        public void Take(List<(int X, int Y, int S, int B)> fog)
        {
            var stride = Math.Max(1, fog.Count / X.Length);
            N = Math.Min(X.Length, fog.Count / stride);
            long cxSum = 0, cySum = 0;
            for (var i = 0; i < N; i++)
            {
                var (x, y, s, b) = fog[i * stride];
                X[i] = x; Y[i] = y; S[i] = s; B[i] = b;
                cxSum += x; cySum += y;
            }
            CentreX = N == 0 ? 0 : (int)(cxSum / N); CentreY = N == 0 ? 0 : (int)(cySum / N);
        }
    }

    private readonly FogSamples _samples = new(DiscoverySamples), _refine = new(RefineSamples);

    /// <summary>
    /// Looks for the cloud, or the pair of overlapping clouds, that explains the whitened background of a screen region
    /// (see <see cref="Search"/>); returns whether one was accepted.
    /// </summary>
    public bool Discover(ReadOnlySpan<int> screen, int x0, int y0, int x1, int y1, double time) => Discover(screen, x0, y0, x1, y1, time, null);

    private bool Discover(ReadOnlySpan<int> screen, int x0, int y0, int x1, int y1, double time, Entry? entry)
    {
        DiscoveryAttempts++;
        var vp = _mask!.Viewport;
        const int Margin = 16;
        x0 = Math.Max(vp.X, x0 - Margin); y0 = Math.Max(vp.Y, y0 - Margin);
        x1 = Math.Min(vp.X + vp.Width - 1, x1 + Margin); y1 = Math.Min(vp.Y + vp.Height - 1, y1 + Margin);
        if (x1 < x0 || y1 < y0) return false;
        var a = Search(screen, x0, y0, x1, y1, entry);
        Trace?.Invoke($"discover{(entry is null ? "" : $" entry {entry.Edges}{(entry.Wrap ? " wrap" : "")}")} {x0},{y0}-{x1},{y1}: {(a is { } fa ? $"sprite {fa.Sprite.Index} at {fa.L},{fa.T} G {fa.G} exact {fa.Exact}/{fa.Inf}" : "none")}");
        if (a is not { } first || first.Votes < MinVotes) return false;
        var cloudA = Add(first.Sprite, first.G, first.L, first.T, time);
        if (first.Exact >= DiscoverShare * first.Inf && first.Exact >= MinDiscoverExact)
        {
            Accept(cloudA, screen, time);
            // A second cloud over the same region, if what is left is still whitened enough.
            if (Search(screen, x0, y0, x1, y1, entry) is { } more && more.Votes >= MinVotes && more.Exact >= MinDiscoverExact && more.Exact >= DiscoverShare * more.Inf)
                Accept(Add(more.Sprite, more.G, more.L, more.T, time), screen, time);
            return true;
        }
        // An entry region: the cloud that came in first shows alone on the leading fringe; found there, the second is what is
        // left of the whole region.
        if (entry is not null && LeadingFringe(entry, x0, y0, x1, y1) is var (fx0, fy0, fx1, fy1)
            && Search(screen, fx0, fy0, fx1, fy1, entry) is { } lead && lead.Votes >= MinVotes / 2 && lead.Exact >= MinDiscoverExact && lead.Exact >= DiscoverShare * lead.Inf)
        {
            Trace?.Invoke($"discover fringe {fx0},{fy0}-{fx1},{fy1}: sprite {lead.Sprite.Index} at {lead.L},{lead.T} G {lead.G} exact {lead.Exact}/{lead.Inf}");
            _clouds.Remove(cloudA);
            cloudA = Add(lead.Sprite, lead.G, lead.L, lead.T, time);
            if (RegionExact(lead.Sprite, lead.L, lead.T, lead.G, screen, x0, y0, x1, y1) is var (le, li) && le >= DiscoverShare * li)
            {
                Accept(cloudA, screen, time);
                return true;
            }
        }
        // Provisional: the rest of the whitening may be a second cloud over this one.
        var b = Search(screen, x0, y0, x1, y1, entry);
        Trace?.Invoke($"discover pair: {(b is { } fb ? $"sprite {fb.Sprite.Index} at {fb.L},{fb.T} G {fb.G} exact {fb.Exact}/{fb.Inf}" : "none")}");
        if (b is { } second && second.Votes >= MinVotes)
        {
            var cloudB = Add(second.Sprite, second.G, second.L, second.T, time);
            Order(cloudB, screen);
            var (exact, inf) = PairExact(cloudA, cloudB, screen);
            // Each was found against the other's error: the two are moved together a few pixels to where they explain most.
            if (exact < DiscoverShare * inf) (exact, inf) = JointRefine(cloudA, cloudB, screen);
            Trace?.Invoke($"discover pair together: exact {exact}/{inf}");
            if (exact >= DiscoverShare * inf && exact >= 2 * MinDiscoverExact)
            {
                Accept(cloudA, screen, time);
                Accept(cloudB, screen, time);
                return true;
            }
            _clouds.Remove(cloudB);
        }
        _clouds.Remove(cloudA);
        return false;
    }

    /// <summary>The median drift of the clouds whose motion is known, or 0.</summary>
    private double MedianSpeed()
    {
        var v = _clouds.Where(c => !Young(c)).Select(c => c.VelocityX).OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    /// <summary>The known clouds' speeds, least and most, widened by <see cref="SpeedSlack"/> (the whole allowed range while fewer than three are known).</summary>
    private (double Lo, double Hi) SpeedRange()
    {
        double lo = double.MaxValue, hi = double.MinValue; var n = 0;
        foreach (var c in _clouds) if (!Young(c)) { lo = Math.Min(lo, c.VelocityX); hi = Math.Max(hi, c.VelocityX); n++; }
        return n < 3 ? (MinSpeed, MaxSpeed) : (Math.Max(MinSpeed, lo - SpeedSlack), Math.Min(MaxSpeed, hi + SpeedSlack));
    }

    /// <summary>Units per second beyond the known clouds' speeds a new cloud's may be.</summary>
    public const double SpeedSlack = 8;

    private Cloud Add(CloudSprite sprite, int g, int left, int top, double time)
    {
        var cloud = new Cloud(_nextId++, sprite, g, _clouds.Count == 0 ? 0 : _clouds.Max(c => c.Z) + 1)
        {
            Left = left, Top = top, Placed = true, Verified = true, FirstSeen = time, PriorVelocityX = MedianSpeed(),
        };
        _clouds.Add(cloud);
        return cloud;
    }

    private void Accept(Cloud cloud, ReadOnlySpan<int> screen, double time)
    {
        // A cloud of the set found again (its canvas and alpha, near its row): the known one takes the place, so the set
        // keeps one entry per cloud and the line its whole history. Offline (the mosaic build) a cloud found again is followed as
        // its own stretch: a line fitted to it places the cloud there better than one line over minutes.
        var known = DiscoverEverywhere ? null : _clouds.FirstOrDefault(c => !ReferenceEquals(c, cloud) && !c.Verified && ReferenceEquals(c.Sprite, cloud.Sprite)
            && Math.Abs(c.Alpha - cloud.Alpha) <= 2 && Math.Abs(WorldAt(c, time).Y - (cloud.Top + _camY)) <= ReacquireRows
            // A known cloud laid over this frame elsewhere is another one (the pair over the snail and the boar: one canvas,
            // alphas 164 and 166, rows 5 apart, 30 columns apart).
            && (!c.Placed || Math.Abs(c.Left - cloud.Left) <= ReacquireSlack && Math.Abs(c.Top - cloud.Top) <= ReacquireSlack));
        if (known is not null)
        {
            _clouds.Remove(cloud);
            known.Left = cloud.Left; known.Top = cloud.Top; known.Placed = true; known.Verified = true;
            Observe(known, time);
            Reacquired++;
            return;
        }
        Order(cloud, screen);
        Observe(cloud, time);
        Discovered++;
    }

    /// <summary>Rows a cloud of the set may be from its line's y and still be the one found again.</summary>
    public const int ReacquireRows = 24;

    /// <summary>Pixels a discovery may be from a known cloud laid over the frame and still be that one.</summary>
    public const int ReacquireSlack = 3;

    /// <summary>Clouds of the set a discovery found again (counted apart from <see cref="Discovered"/>).</summary>
    public int Reacquired { get; private set; }

    /// <summary>Exact and informative pixels (every second row and column) where either of two clouds covers background the mosaic vouches for.</summary>
    /// <summary>Pixels a discovered pair is moved by, at most, to where the two together explain the most (columns, rows).</summary>
    public const int JointX = 4, JointY = 2;

    /// <summary>
    /// Moves a provisional pair together: the second over <see cref="JointX"/> by <see cref="JointY"/> around its place, then
    /// the first a little less, each kept where the pair's exact share (every fourth pixel) is highest; returns the pair's
    /// exact and informative pixels there (every second pixel).
    /// </summary>
    private (int Exact, int Inf) JointRefine(Cloud a, Cloud b, ReadOnlySpan<int> screen)
    {
        foreach (var (c, rx, ry) in new[] { (b, JointX, JointY), (a, JointX / 2, JointY / 2), (b, 1, 1) })
        {
            int l0 = c.Left, t0 = c.Top, bl = l0, bt = t0;
            var (be, bi) = PairExact(a, b, screen, 4);
            for (var dy = -ry; dy <= ry; dy++)
                for (var dx = -rx; dx <= rx; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    c.Left = l0 + dx; c.Top = t0 + dy;
                    var (e, i) = PairExact(a, b, screen, 4);
                    if (i > 0 && (long)e * Math.Max(1, bi) > (long)be * i) { be = e; bi = i; bl = c.Left; bt = c.Top; }
                }
            c.Left = bl; c.Top = bt;
        }
        var r = PairExact(a, b, screen);
        Trace?.Invoke($"discover pair moved: sprite {a.Sprite.Index} at {a.Left},{a.Top}, sprite {b.Sprite.Index} at {b.Left},{b.Top}: exact {r.Exact}/{r.Inf}");
        return r;
    }

    private (int Exact, int Inf) PairExact(Cloud a, Cloud b, ReadOnlySpan<int> screen, int step = 2)
    {
        var mosaic = _mosaic!; var mask = _mask!; var vp = mask.Viewport;
        int ux0 = Math.Min(a.Left, b.Left), uy0 = Math.Min(a.Top, b.Top);
        int ux1 = Math.Max(a.Left + a.Sprite.Width, b.Left + b.Sprite.Width), uy1 = Math.Max(a.Top + a.Sprite.Height, b.Top + b.Sprite.Height);
        int exact = 0, inf = 0;
        Work += (long)(ux1 - ux0) * (uy1 - uy0) / (step * step);
        for (var y = uy0; y < uy1; y += step)
            for (var x = ux0; x < ux1; x += step)
            {
                if (!CoversAt(a, x, y) && !CoversAt(b, x, y)) continue;
                var bg = Background(x, y, mosaic, mask, vp);
                if (bg < 0) continue;
                var e = Composite(bg, x, y);
                if (e == bg) continue;
                inf++;
                if (e == (screen[y * _frameWidth + x] & 0xFFFFFF)) exact++;
            }
        return (exact, inf);
    }

    private static bool Overlap(Cloud a, Cloud b) =>
        a.Left < b.Left + b.Sprite.Width && b.Left < a.Left + a.Sprite.Width && a.Top < b.Top + b.Sprite.Height && b.Top < a.Top + a.Sprite.Height;

    private static bool CoversAt(Cloud c, int x, int y)
    {
        int qx = x - c.Left, qy = y - c.Top;
        return (uint)qx < (uint)c.Sprite.Width && (uint)qy < (uint)c.Sprite.Height && c.Sprite.Alpha[qy * c.Sprite.Width + qx] != 0;
    }

    /// <summary>
    /// The single cloud that best explains the whitened background of a screen region over what the placed clouds explain.
    /// Each canvas is placed at every third column and row that puts the whitened samples' centre under it and scored on
    /// spread whitened pixels: from the art's colour and alpha under each, the cloud alpha that would explain it (the other
    /// channels within two levels), and the best-supported alpha wins. The best few placements are refined at every pixel
    /// around them with the exact blend over the region; the one with the most exact pixels is returned.
    /// </summary>
    private (CloudSprite Sprite, int L, int T, int G, int Exact, int Inf, int Votes)? Search(ReadOnlySpan<int> screen, int x0, int y0, int x1, int y1, Entry? entry = null)
    {
        var mosaic = _mosaic!; var mask = _mask!; var vp = mask.Viewport;
        // Whitened pixels (every second row and column, staggered): every channel at least the background's (under the
        // placed clouds), together brighter by a few levels.
        var tp = System.Diagnostics.Stopwatch.GetTimestamp();
        Work += (long)(x1 - x0 + 1) * (y1 - y0 + 1) / 4;
        var fog = new List<(int X, int Y, int S, int B)>();
        for (var y = y0; y <= y1; y += 2)
            for (var x = x0 + ((y - y0) >> 1 & 1); x <= x1; x += 2)
            {
                var b = Background(x, y, mosaic, mask, vp);
                if (b < 0) continue;
                b = Composite(b, x, y);
                var s = screen[y * _frameWidth + x] & 0xFFFFFF;
                int db = (s & 0xFF) - (b & 0xFF), dg = (s >> 8 & 0xFF) - (b >> 8 & 0xFF), dr = (s >> 16) - (b >> 16);
                if (db < -1 || dg < -1 || dr < -1 || db + dg + dr < 9) continue;
                fog.Add((x, y, s, b));
            }
        if (fog.Count < MinDiscoverySamples) return null;
        var smp = _samples;
        smp.Take(fog);
        smp.Imply();
        _refine.Take(fog);
        PhaseMs[0] += System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds; tp = System.Diagnostics.Stopwatch.GetTimestamp();
        var top = new List<(int Score, CloudSprite Sprite, int L, int T)>();
        Span<int> bins = stackalloc int[36];
        int n = smp.N, ccx = smp.CentreX, ccy = smp.CentreY;
        // The canvas must cover the middle half of the whitened samples (their quartiles; two overlapping clouds each cover
        // most of their union), with an opaque pixel over their centre.
        var xs = smp.X.AsSpan(0, n).ToArray(); var ys = smp.Y.AsSpan(0, n).ToArray();
        Array.Sort(xs); Array.Sort(ys);
        int qx0 = xs[n / 4], qx1 = xs[3 * n / 4], qy0 = ys[n / 4], qy1 = ys[3 * n / 4];
        foreach (var s in _art.Sprites)
            for (var t = qy1 - s.Height + 1; t <= qy0; t += CoarseStep)
                for (var l = qx1 - s.Width + 1; l <= qx0; l += CoarseStep)
                {
                    if ((uint)(ccx - l) >= (uint)s.Width || (uint)(ccy - t) >= (uint)s.Height || s.Alpha[(ccy - t) * s.Width + ccx - l] < 17) continue;
                    if (entry is not null && !Allows(entry, s, l, t)) continue;
                    Work += 4;
                    bins.Clear();
                    int covered = 0, best = 0;
                    for (var i = 0; i < n; i++)
                    {
                        int qx = smp.X[i] - l, qy = smp.Y[i] - t;
                        if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
                        var k = qy * s.Width + qx;
                        int a = s.Alpha[k];
                        if (a < 17) continue;
                        covered++;
                        Work++;
                        var code = s.Code[k];
                        var g = code >= 0 ? smp.Implied[i * 256 + code] : ImpliedAlpha(smp.S[i], smp.B[i], s.Colour[k], a);
                        if (g > 0)
                        {
                            var bin = ++bins[g >> 3];
                            best = Math.Max(best, bin + Math.Max(bins[(g >> 3) + 1], g >= 8 ? bins[(g >> 3) - 1] : 0));
                        }
                        if (covered == 10 && best < 3) break;
                    }
                    if (best < 5) continue;
                    // The best two placements of every canvas go on: one overlapped by another cloud scores low against the rest.
                    var mine = 0; var worst = -1;
                    for (var j = 0; j < top.Count; j++)
                        if (ReferenceEquals(top[j].Sprite, s)) { mine++; if (worst < 0 || top[j].Score < top[worst].Score) worst = j; }
                    if (mine < 2) top.Add((best, s, l, t));
                    else if (best > top[worst].Score) top[worst] = (best, s, l, t);
                }
        PhaseMs[1] += System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds; tp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (top.Count == 0) return null;
        Trace?.Invoke($"  coarse ({n} samples, centre {ccx},{ccy}): {string.Join(", ", top.OrderByDescending(c => c.Score).Select(c => $"s{c.Sprite.Index}@{c.L},{c.T}:{c.Score}"))}");
        // The placement most samples agree on exactly (at one alpha) wins: where two clouds overlap no single one explains the
        // screen, so the region's exact share is no measure yet; the samples one cloud covers alone are.
        // First every pixel around the coarse picks on the coarse samples, then a pixel around the three best on more.
        var near = new List<(int Votes, CloudSprite Sprite, int L, int T)>();
        var r = CoarseStep - 1;
        foreach (var (_, s, l0, t0) in top)
            for (var t = t0 - r; t <= t0 + r; t++)
                for (var l = l0 - r; l <= l0 + r; l++)
                {
                    if (entry is not null && !Allows(entry, s, l, t)) continue;
                    var (g, votes) = BestAlpha(s, l, t, smp);
                    Work += smp.N;
                    if (g > 0) near.Add((votes, s, l, t));
                }
        (int Votes, CloudSprite? Sprite, int L, int T, int G) found = (0, null, 0, 0, 0);
        var refined = new HashSet<(CloudSprite, int, int)>();
        foreach (var (_, s, l0, t0) in near.OrderByDescending(c => c.Votes).Take(3))
            for (var t = t0 - 1; t <= t0 + 1; t++)
                for (var l = l0 - 1; l <= l0 + 1; l++)
                {
                    if (!refined.Add((s, l, t)) || entry is not null && !Allows(entry, s, l, t)) continue;
                    var (g, votes) = BestAlpha(s, l, t, _refine);
                    Work += _refine.N;
                    if (g > 0 && votes > found.Votes) found = (votes, s, l, t, g);
                }
        PhaseMs[2] += System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds; tp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (found.Sprite is null) return null;
        var (exact, inf) = RegionExact(found.Sprite, found.L, found.T, found.G, screen, x0, y0, x1, y1);
        PhaseMs[3] += System.Diagnostics.Stopwatch.GetElapsedTime(tp).TotalMilliseconds;
        return (found.Sprite, found.L, found.T, found.G, exact, inf, found.Votes);
    }

    /// <summary>Columns and rows between the placements the coarse search tries.</summary>
    public const int CoarseStep = 4;

    /// <summary>
    /// The cloud alpha (1 to 255) that turns background b into screen s under a cloud pixel of colour c and art alpha a,
    /// from the channel the cloud changes most, when the other channels agree within two levels; else 0.
    /// </summary>
    private static int ImpliedAlpha(int s, int b, int c, int a)
    {
        int db = (c & 0xFF) - (b & 0xFF), dg = (c >> 8 & 0xFF) - (b >> 8 & 0xFF), dr = (c >> 16) - (b >> 16);
        int ab = Math.Abs(db), ag = Math.Abs(dg), ar = Math.Abs(dr);
        int d, ds;
        if (ab >= ag && ab >= ar) { d = db; ds = (s & 0xFF) - (b & 0xFF); }
        else if (ag >= ar) { d = dg; ds = (s >> 8 & 0xFF) - (b >> 8 & 0xFF); }
        else { d = dr; ds = (s >> 16) - (b >> 16); }
        if (Math.Abs(d) < 24) return 0;
        var e = (double)ds / d;
        if (e <= 0) return 0;
        if (Math.Abs((s & 0xFF) - (b & 0xFF) - db * e) > 2 || Math.Abs((s >> 8 & 0xFF) - (b >> 8 & 0xFF) - dg * e) > 2 || Math.Abs((s >> 16) - (b >> 16) - dr * e) > 2) return 0;
        var g = (int)Math.Round(e * 65025 / a);
        return g is >= 1 and <= 270 ? Math.Min(g, 255) : 0;
    }

    /// <summary>The cloud alpha most samples agree on exactly at one placement (each sample admits an interval of alphas), or 0.</summary>
    private static (int G, int Votes) BestAlpha(CloudSprite s, int l, int t, FogSamples smp)
    {
        Span<int> votes = stackalloc int[257];
        for (var i = 0; i < smp.N; i++)
        {
            int qx = smp.X[i] - l, qy = smp.Y[i] - t;
            if ((uint)qx >= (uint)s.Width || (uint)qy >= (uint)s.Height) continue;
            var k = qy * s.Width + qx;
            int a = s.Alpha[k];
            if (a == 0) continue;
            long lo = 1, hi = 255;
            for (var ch = 0; ch < 24 && lo <= hi; ch += 8)
            {
                int p = smp.B[i] >> ch & 0xFF, c = s.Colour[k] >> ch & 0xFF, v = smp.S[i] >> ch & 0xFF;
                // Blend(p, c, a G) == v  <=>  (v - p) 65025 - 32512 <= (c - p) a G <= (v - p) 65025 + 32512
                if (c == p) { if (v != p) hi = -1; continue; }
                var den = (long)(c - p) * a;
                long lo2 = (long)(v - p) * 65025 - 32512, hi2 = (long)(v - p) * 65025 + 32512;
                long gl, gh;
                if (den > 0) { gl = CeilDiv(lo2, den); gh = FloorDiv(hi2, den); }
                else { gl = CeilDiv(hi2, den); gh = FloorDiv(lo2, den); }
                lo = Math.Max(lo, gl); hi = Math.Min(hi, gh);
            }
            for (var g = lo; g <= hi; g++) votes[(int)g]++;
        }
        var best = 0;
        for (var g = 1; g < 256; g++) if (votes[g] > votes[best]) best = g;
        return votes[best] == 0 ? (0, 0) : (best, votes[best]);
    }

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0);
    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    /// <summary>
    /// Exact and informative pixels (every second row and column) of one cloud placement over a region's background under
    /// the placed clouds.
    /// </summary>
    private (int Exact, int Informative) RegionExact(CloudSprite s, int l, int t, int g, ReadOnlySpan<int> screen, int x0, int y0, int x1, int y1, int step = 2)
    {
        var mosaic = _mosaic!; var mask = _mask!; var vp = mask.Viewport;
        int exact = 0, inf = 0;
        int ix0 = Math.Max(x0, l), iy0 = Math.Max(y0, t), ix1 = Math.Min(x1 + 1, l + s.Width), iy1 = Math.Min(y1 + 1, t + s.Height);
        for (var y = iy0; y < iy1; y += step)
            for (var x = ix0; x < ix1; x += step)
            {
                var k = (y - t) * s.Width + x - l;
                var a = s.Alpha[k];
                if (a == 0) continue;
                var b = Background(x, y, mosaic, mask, vp);
                if (b < 0) continue;
                b = Composite(b, x, y);
                var e = BlendPacked(b, s.Colour[k], a * g);
                if (e == b) continue;
                inf++;
                if (e == (screen[y * _frameWidth + x] & 0xFFFFFF)) exact++;
            }
        return (exact, inf);
    }

    /// <summary>
    /// Settles a cloud's draw order against the placed clouds it overlaps: on the pixels both cover, over background the
    /// mosaic vouches for, it goes below or above whichever of them explains more exactly.
    /// </summary>
    private void Order(Cloud added, ReadOnlySpan<int> screen)
    {
        var mosaic = _mosaic!;
        var mask = _mask!;
        var vp = mask.Viewport;
        foreach (var other in _clouds.ToList())
        {
            if (ReferenceEquals(other, added) || !other.Placed) continue;
            int ix0 = Math.Max(added.Left, other.Left), iy0 = Math.Max(added.Top, other.Top);
            int ix1 = Math.Min(added.Left + added.Sprite.Width, other.Left + other.Sprite.Width), iy1 = Math.Min(added.Top + added.Sprite.Height, other.Top + other.Sprite.Height);
            if (ix0 >= ix1 || iy0 >= iy1) continue;
            var original = added.Z;
            int above = 0, below = 0;
            foreach (var z in new[] { other.Z + 0.5, other.Z - 0.5 })
            {
                added.Z = z;
                _clouds.Sort((a, b) => a.Z.CompareTo(b.Z));
                var exact = 0;
                for (var y = iy0; y < iy1; y += 2)
                    for (var x = ix0; x < ix1; x += 2)
                    {
                        if (!CoversAt(added, x, y) || !CoversAt(other, x, y)) continue;
                        var b = Background(x, y, mosaic, mask, vp);
                        if (b < 0) continue;
                        if (Composite(b, x, y) == (screen[y * _frameWidth + x] & 0xFFFFFF)) exact++;
                    }
                if (z > other.Z) above = exact; else below = exact;
            }
            added.Z = above >= below ? Math.Max(original, other.Z + 0.5) : other.Z - 0.5;
            _clouds.Sort((a, b) => a.Z.CompareTo(b.Z));
        }
    }
}
