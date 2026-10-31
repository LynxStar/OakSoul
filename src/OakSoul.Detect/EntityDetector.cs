using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>What the rest of the pipeline knows about a frame, for the detector.</summary>
/// <param name="Camera">The world tracker's camera (world position of the screen's top-left), or null.</param>
/// <param name="CameraMeasured">True when the camera came from the dot and the own tag (good to the dot's +-8 on x).</param>
/// <param name="OwnTag">The own name tag, when the locator found it.</param>
/// <param name="OwnWorld">The own character's world position (soles), for masking it when the tag is hidden.</param>
/// <param name="MinimapPanel">The minimap panel as read, masked out of the world.</param>
/// <param name="Sequence">The frame's capture sequence (the detector counts frames by <paramref name="Frame"/>, its position in the replay).</param>
/// <param name="Exp">The EXP total the status bar shows, when read: a gain equal to one species' client exp credits that kill to a track.</param>
/// <param name="Pts">The frame's capture pts in seconds, when known: poses advance in the game's time, which the delay table counts.</param>
public sealed record DetectionContext(long Frame, double Time, MapData Map, FootholdIndex Footholds, PointD? Camera, bool CameraMeasured,
    CaptureRect? OwnTag, PointD? OwnWorld, CaptureRect? MinimapPanel, long Sequence = 0, long? Exp = null, double? Pts = null);

/// <summary>
/// Detects and tracks entities in pixel-exact native frames of one map (the game's own pixels: exact capture, live, from
/// keyframes or from a lossless video; the caller skips any other frame) against its cached <see cref="BackgroundMosaic"/>:
/// refines the camera by registration, extracts foreground, finds name tags (OCR'd once per distinct tag), turns
/// blobs and tags into candidates with a world position and foothold, then classifies and tracks them.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>
    /// First, narrow registration window around a measured camera (dot and own tag; x good to the dot's +-8). The
    /// mosaic build's refinements put most frames within +-9 x and +-8 y of the tracker; the rest (up to 19 x, 22 y)
    /// fail this search and go to the wide one.
    /// </summary>
    public const int MeasuredRangeX = 12, MeasuredRangeY = 10;

    /// <summary>
    /// A narrow search scoring at least this is accepted without the wide one. On fx-combat correct registrations
    /// scored 0.7 to 0.95 (mobs, effects and steam cover the rest of the samples) and failed ones 0.05 to 0.3.
    /// </summary>
    public const double AcceptScore = 0.6;

    /// <summary>
    /// Wide search: +-64 pixels in 4-pixel steps over block means, then +-4 at full resolution, around the tracker's
    /// camera and, failing that, around the last registered one. With the tag hidden the tracker predicts the camera
    /// and was 30 pixels off (frames 12 to 14) and up to about 70 while the player dropped down a rope (frames 37 to
    /// 40); 15 of 402 keyframes still fail.
    /// </summary>
    public const int CoarseRange = 64, CoarseRefine = 4;

    /// <summary>Below this match fraction the registration is not trusted and the frame is skipped (see <see cref="AcceptScore"/>).</summary>
    public const double MinRegistrationScore = 0.5;

    /// <summary>Samples per registration: half the mosaic builder's, which still gives a sharp exact-match peak.</summary>
    public const int RegistrationSamples = 1500;

    /// <summary>Padding around the own-character box when its position comes from the dot (+-8 units) instead of the tag.</summary>
    public const int HiddenOwnPad = 12;

    /// <summary>
    /// Blobs touching the own-character box with fewer pixels than this are the parts of the own sprite (weapon,
    /// cape, spell sparks at the hands) or the pet that stick out of the box, and are dropped; larger ones are kept,
    /// since a mob in melee range touches the box too. A judgement call, not tuned.
    /// </summary>
    public const int OwnFragmentPixels = 250;

    /// <summary>
    /// Blobs whose boxes come within this many pixels of each other and whose union stays sprite-sized are merged:
    /// a sprite split by an unreliable stripe of the mosaic or by a colour that happens to match the background. The
    /// largest tagged mob box seen was 94x49 (a Fire Boar with its tag box).
    /// </summary>
    public const int MergeGap = 2, MergeMaxWidth = 160, MergeMaxHeight = 150;

    /// <summary>
    /// Smallest fraction of a blob's pixels that must be solid (not explainable as the background under a black or
    /// white translucent layer). Measured on fx-combat: the blobs of tagged mobs have a median of 0.79 and a p10 of
    /// 0.35, while all blobs are bimodal with a fifth of them under 0.2 (darkened parallax rock and waterfall, steam
    /// edges). 0.3 keeps 69 of 75 tagged-mob blobs and drops 26% of all blobs; 0.4 would lose 8 of 75.
    /// </summary>
    public const double MinSolidFraction = 0.3;

    /// <summary>
    /// A tag belongs to the blob whose bottom ends within this many rows above the tag's top (the sprite stands one
    /// to three rows above its tag) and whose centre is within half the tag's width plus <see cref="TagSlackX"/>.
    /// </summary>
    public const int TagAboveRows = 12, TagSlackX = 10;

    /// <summary>Names of the timed stages, in <see cref="StageMs"/> order.</summary>
    public static readonly string[] StageNames = ["registration", "difference", "tags", "temporal", "components", "sprites", "drops", "tracking"];

    /// <summary>Cumulative milliseconds per stage (see <see cref="StageNames"/>; OCR excluded) and frames timed.</summary>
    public double[] StageMs { get; } = new double[StageNames.Length];

    /// <summary>Per-frame milliseconds of the sprite stage, for its median.</summary>
    public List<double> SpriteStageMs { get; } = [];
    public int StageFrames { get; private set; }

    /// <summary>
    /// Candidates the species search (and the foothold search under an unmatched blob) ran on, and the milliseconds it took, by
    /// the kind the tracker then gave them: what effect blobs and the like cost in searches that cannot find a mob.
    /// </summary>
    public Dictionary<EntityKind, (int Candidates, double Ms)> SearchByKind { get; } = [];

    /// <summary>Frames that needed the wide search.</summary>
    public int WideSearches { get; private set; }

    private CoarseMosaic? _coarse;
    /// <summary>The registration samples' buffer, reused frame to frame (<see cref="CameraRegistration.Samples(ReadOnlySpan{byte}, int, ScreenMask, int, List{int})"/>).</summary>
    private readonly List<int> _sampleScratch = new(8192);
    /// <summary>The samples' colours and coordinates, gathered once per frame and shared by that frame's registrations (<see cref="Fine"/>).</summary>
    private readonly GatheredSamples _gathered = new();
    private (int X, int Y, double Time)? _lastCamera;
    private (int X, int Y)? _lastGuess;

    /// <summary>
    /// The camera prior's window (pixels either way) and how old the previous registration may be. On fx-combat-exact's video
    /// the registered camera moved 4 pixels or fewer between consecutive frames on 89% of them (12 or fewer on 99%); on its
    /// keyframes the registration's offset from the world tracker's camera stayed within 4 of the previous keyframe's on half.
    /// </summary>
    public const int PriorRange = 4;
    public const double PriorSeconds = 1.0;

    /// <summary>Frames registered within the camera prior's window, without the narrow or wide search.</summary>
    public int PriorRegistrations { get; private set; }

    private readonly BackgroundMosaic _mosaic;
    private readonly ScreenMask _mask;
    private readonly ForegroundExtractor _fg = new();
    private readonly TagDetector _tags;
    private readonly EntityTracker _tracker;
    private readonly MapMobSet? _mobs;
    private readonly DropMatcher? _drops;
    private readonly SpriteMatcher _matcher = new();
    private readonly DamageReader? _damage;
    private FootholdSearch? _search;
    private PointD? _ownWorld;
    private long? _lastExp;

    /// <param name="tracker">A tracker shared across maps (it is flushed on a map change by the caller), or null for a new one.</param>
    /// <param name="mobs">The map's mob templates; without them candidates are not verified against sprites.</param>
    /// <param name="drops">Drop icons; without them drops are classified by appearance only.</param>
    /// <param name="damage">The damage number digits; without them damage numbers stay effects.</param>
    /// <param name="tags">The tag detector shared across maps (its cache and its OCR worker), or null for one of this detector's own.</param>
    public EntityDetector(BackgroundMosaic mosaic, HudLayout layout, EntityTracker? tracker = null, MapMobSet? mobs = null, DropMatcher? drops = null, DamageReader? damage = null,
        TagDetector? tags = null)
    {
        _mosaic = mosaic;
        _tags = tags ?? new TagDetector();
        // The EXP and loot log stays in the world: only its text leaves the foreground (ClearLogText).
        _mask = new ScreenMask(layout, [LogRegion]);
        _logBox = layout.TryGet(LogRegion, out var log) ? log.Rect : null;
        _tracker = tracker ?? new EntityTracker();
        _tracker.MapId = mosaic.MapId;
        _mobs = mobs;
        _drops = drops;
        _damage = damage;
        _gapLayout = layout;
    }

    /// <summary>Builds what the detector's sprite sets need before the first frame (the effects' anchor index), so no frame pays for it.</summary>
    public void Prepare()
    {
        BuildEffectIndex();
        // The gap filler's candidate sets (a dozen milliseconds), when it is on.
        if (GapFiller) PrepareGapSets();
    }

    /// <summary>Mobs found by the foothold search: new ones where no blob was (over parallax background), and blobs no box alignment matched.</summary>
    public int SweepFinds { get; private set; }
    public int SweepAttached { get; private set; }
    public double SweepMs { get; private set; }
    public double SweepUnderMs { get; private set; }
    public List<double> SweepScores { get; } = [];
    public FootholdSearch? Search => _search;

    /// <summary>Foreground blobs of the own character that stuck out of its fixed box, dropped (see <see cref="OwnReach"/>).</summary>
    public int OwnSlivers { get; private set; }

    /// <summary>Damage numbers read, and the time spent reading them.</summary>
    public int DamageNumbers { get; private set; }
    public double DamageMs { get; private set; }
    public DamageReader? Damage => _damage;

    /// <summary>EXP gains seen, and those equal to one species' client exp (credited by the tracker when a track of that species qualifies).</summary>
    public int ExpGains { get; private set; }
    public int ExpGainsLabelled { get; private set; }

    public MapMobSet? Mobs => _mobs;
    public SpriteMatcher Matcher => _matcher;

    /// <summary>
    /// When true, every candidate is also scored against every species of the map (the best frame per species, size
    /// prefilter applied, no early stop), for the report's score distributions. Timed apart from the stages.
    /// </summary>
    public bool SpriteDiagnostics { get; set; }

    /// <summary>One candidate's diagnostic scores: the accepted match (if any), the tag's species (if the tag named one) and every species' best.</summary>
    public sealed record SpriteDiagnostic(long Frame, CaptureRect Box, bool Tagged, int? TagSpecies, SpriteMatch? Accepted,
        IReadOnlyDictionary<int, SpriteMatch> BestBySpecies, int? Floor, double? SurfaceDistance)
    {
        /// <summary>The kind the tracker gave the candidate's entity in this frame (filled in after tracking).</summary>
        public EntityKind Kind { get; set; }
        internal Candidate? Source { get; init; }

        /// <summary>The tag text read on the candidate, if any.</summary>
        public string? TagText => Source?.Tag?.Text;
    }

    public List<SpriteDiagnostic> Diagnostics { get; } = [];
    public double DiagnosticMs { get; private set; }

    public BackgroundMosaic Mosaic => _mosaic;
    public ForegroundExtractor Foreground => _fg;
    public TagDetector Tags => _tags;
    public EntityTracker Tracker => _tracker;

    /// <summary>When false, tags are matched against the cache only and never OCR'd (for timing the per-frame path).</summary>
    public bool Ocr { get; set; } = true;

    /// <summary>Directory for per-frame debug overlays (foreground, tags, blobs), or null.</summary>
    public string? DebugDir { get; set; }

    /// <summary>The tag boxes and own box of the last frame, for annotation.</summary>
    public IReadOnlyList<TagBox> LastTags { get; private set; } = [];
    public CaptureRect? LastOwnBox { get; private set; }

    public EntityFrame Detect(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx)
    {
        var sw = Stopwatch.StartNew();
        _matcher.Explained = null;
        LastTags = [];
        LastOwnBox = null;
        if (ctx.Camera is not { } guess)
        {
            _lastDetected = false;
            return EntityFrame.Empty(ctx.Frame, ctx.Time, "no camera");
        }
        var panel = ctx.MinimapPanel ?? ScreenMask.DefaultMinimapPanel;
        _mask.Reset();
        _mask.Exclude(panel, 4);
        CaptureRect? own = ctx.OwnTag is { } tag ? OwnCharacter.Box(tag) : null;
        if (own is null && ctx.OwnWorld is { } ow)
        {
            var soles = CameraModel.WorldToScreen(guess, ow);
            own = OwnCharacter.BoxFromSoles(soles.X, soles.Y, HiddenOwnPad);
        }
        if (own is { } box) _mask.Exclude(box);

        var samples = CameraRegistration.Samples(frame, frameWidth, _mask, RegistrationSamples, _sampleScratch);
        // Every registration below reads this frame through this mask at these samples: their colours are gathered once.
        _gathered.Gather(frame, frameWidth, _mask, samples);
        int guessX = (int)Math.Round(guess.X), guessY = (int)Math.Round(guess.Y);
        // The temporal pass predicts the camera too: within a small window of the previous frame's registered camera (a
        // video), or of the guess moved by the previous frame's registration offset (keyframes). Accepted when it scores as a
        // narrow search must and its peak lies inside the window; otherwise the searches below run as before.
        Registration? reg = null;
        if (Temporal && _lastCamera is { } lc && _lastGuess is { } lg && ctx.Time - lc.Time <= PriorSeconds)
        {
            var recent = ctx.Time - lc.Time <= PreciseSeconds;
            int px = recent ? lc.X : guessX + lc.X - lg.X, py = recent ? lc.Y : guessY + lc.Y - lg.Y;
            var prior = Fine(px, py, PriorRange, PriorRange);
            if (prior.Score >= AcceptScore && Math.Abs(prior.CameraX - px) < PriorRange && Math.Abs(prior.CameraY - py) < PriorRange) { reg = prior; PriorRegistrations++; }
        }
        // A measured camera is searched near the guess first; a predicted one, or a failed first try, goes through the
        // coarse search of block means over a wide window and is then refined to the pixel.
        reg ??= ctx.CameraMeasured ? Fine(guessX, guessY, MeasuredRangeX, MeasuredRangeY) : null;
        if (reg is not { Score: >= AcceptScore })
        {
            _coarse ??= new CoarseMosaic(_mosaic);
            if (_coarse.Search(frame, frameWidth, _mask, guessX, guessY, CoarseRange) is { } c)
            {
                var wide = Fine(c.X, c.Y, CoarseRefine, CoarseRefine);
                if (reg is null || wide.Score > reg.Value.Score) reg = wide with { GuessMatches = reg?.GuessMatches ?? wide.GuessMatches, GuessCompared = reg?.GuessCompared ?? wide.GuessCompared };
            }
            // Still nothing: the tracker's camera can be far off while the tag is hidden and the camera eases; the
            // last registered camera is usually closer.
            if (reg is not { Score: >= MinRegistrationScore } && _lastCamera is { } last && ctx.Time - last.Time <= 2
                && _coarse.Search(frame, frameWidth, _mask, last.X, last.Y, CoarseRange) is { } c2)
            {
                var wide = Fine(c2.X, c2.Y, CoarseRefine, CoarseRefine);
                if (reg is null || wide.Score > reg.Value.Score) reg = wide;
            }
            reg ??= Fine(guessX, guessY, 0, 0);
            WideSearches++;
        }
        if (reg.Value.Score < MinRegistrationScore)
        {
            _lastDetected = false;
            _tracker.Update([], ctx.Frame, ctx.Time);
            return new EntityFrame(ctx.Frame, ctx.Time, [], null, reg, sw.Elapsed.TotalMilliseconds, 0, $"registration failed ({reg.Value.Score:0.00})");
        }
        int camX = reg.Value.CameraX, camY = reg.Value.CameraY;
        _lastCamera = (camX, camY, ctx.Time);
        _lastGuess = (guessX, guessY);
        // When the own box came from the dot it moves with the refined camera.
        if (ctx.OwnTag is null && ctx.OwnWorld is { } ow2)
        {
            _mask.Reset();
            _mask.Exclude(panel, 4);
            own = OwnCharacter.BoxFromSoles(ow2.X - camX, ow2.Y - camY, HiddenOwnPad);
            _mask.Exclude(own.Value);
        }
        LastOwnBox = own;

        var tReg = sw.Elapsed.TotalMilliseconds;
        _matcher.Background = (_mosaic, camX, camY);
        if (_drops is not null) _drops.Background = (_mosaic, camX, camY);
        _fg.Difference(frame, frameWidth, _mask, _mosaic, camX, camY);
        UpdateClouds(frame, frameWidth, ctx, camX, camY);
        if (_bandBlind is not null) CountBandBlind(ctx, camX, camY); // diagnostics only (OAKSOUL_BAND_BLIND): the classes are final here
        var tDiff = sw.Elapsed.TotalMilliseconds;
        var vp = _mask.Viewport;
        // What the client draws over the world that is no entity: the log's text, the mobs' HP bars (read, then out of the foreground).
        var th = Stopwatch.GetTimestamp();
        ClearLogText(frame, frameWidth);
        LogMs += Stopwatch.GetElapsedTime(th).TotalMilliseconds;
        var tbar = Stopwatch.GetTimestamp();
        var hpBars = FindHpBars(frame, frameWidth);
        BarMs += Stopwatch.GetElapsedTime(tbar).TotalMilliseconds;
        HudMs += Stopwatch.GetElapsedTime(th).TotalMilliseconds;
        var tags = _tags.Find(_fg, frame, frameWidth, vp);
        _tags.IsPetText ??= _tracker.Names.IsPetTag;
        _tags.SplitPet(tags, frame, frameWidth, vp);
        foreach (var t in tags) _fg.Clear(t.X0 - 1, t.Y0 - 1, t.X1 + 1, t.Y1 + 1);
        // The names the assets give for this map (its species with their levels, the pet) are known to the tag detector before the
        // reads, which renders them in the learned font and names a box showing one without a read.
        if (_mobs is not null) _speciesNames ??= _mobs.Entries.Select(e => (e.Species.Name, e.Species.Level)).DistinctBy(s => s.Name).ToList();
        _tags.SetKnownNames(_speciesNames, _tracker.Names.PetName);
        // The reads go to the OCR worker; what the frame thread waited for one (nothing, unless the detector is set to wait) stays out of the stage times.
        var ocrBefore = _tags.WaitedMilliseconds;
        _tags.Read(tags, frame, frameWidth, vp, Ocr, ctx.Frame);
        // A name read close to a species of the map is that species' name ("Vvild Boar" is Wild Boar).
        if (_mobs is not null && !ReferenceEquals(_tracker.Names.Species, _speciesNames))
            _tracker.Names.Species = _speciesNames!;
        foreach (var t in tags)
            if (t.Name is { } tn && _tracker.Names.MatchSpecies(tn, t.Level) is { } species) t.Name = species;
        var ocrMs = _tags.WaitedMilliseconds - ocrBefore;
        var tTags = sw.Elapsed.TotalMilliseconds - ocrMs;
        // With the camera registered to the pixel, the own tag gives the own position exactly (the dot only to +-8).
        var ownWorld = ctx.OwnTag is { } ownTag ? CameraModel.ScreenToWorld(new PointD(camX, camY), CameraModel.SolesFromTag(OwnCharacter.TagFeet(ownTag))) : ctx.OwnWorld;
        _ownWorld = ownWorld;
        // The temporal pass: the cursor, the pet, the live tracks' predictions and the meso coins verified first. On a temporal
        // frame the pixels they explain leave the foreground and the full search below sees the rest; every FullEvery frames the
        // full search sees everything (the pet, the cursor and the coins leave the foreground on every frame: they are never mobs).
        if (_mobs is not null) _search ??= new FootholdSearch(_mosaic, ctx.Map, ctx.Footholds, _mobs);
        var full = NextIsFull();
        if (full) Temporals.FullFrames++; else Temporals.TemporalFrames++;
        BeginExplained(clear: !full);
        th = Stopwatch.GetTimestamp();
        var cursor = CursorAtLastPlace(frame, frameWidth);
        HudMs += Stopwatch.GetElapsedTime(th).TotalMilliseconds;
        var pet = FindPet(frame, frameWidth, ctx, camX, camY, tags, own, clear: !full);
        var predictions = _mobs is not null && Temporal ? Predict(frame, frameWidth, ctx, camX, camY, clear: !full) : [];
        th = Stopwatch.GetTimestamp();
        var coins = FollowCoins(frame, frameWidth, ctx, camX, camY, clear: !full);
        CoinMs += Stopwatch.GetElapsedTime(th).TotalMilliseconds;
        var tTemporal = sw.Elapsed.TotalMilliseconds - ocrMs;
        // Blobs made mostly of darkened or brightened background (a parallax layer or the waterfall seen against the
        // screen-space model, the edge of a steam cloud) are not sprites.
        var components = _fg.Components();
        var blobs = Merge(components.Where(b => b.SolidFraction >= MinSolidFraction).ToList());
        // A drop in steam is mostly whitened background, but its icon still shows: a drop-sized blob of that kind on a surface
        // whose icon matches is kept (a meso coin in the steam under the ladder on fx-combat-exact's keyframe 496).
        if (_drops is not null)
            foreach (var b in components)
                if (b.SolidFraction < MinSolidFraction && b.Width <= EntityTracker.DropMaxSize && b.Height <= EntityTracker.DropMaxSize && b.Width >= 4 && b.Pixels >= 60
                    && ctx.Footholds.Nearest(new PointD(camX + (b.X0 + b.X1) / 2.0 + vp.X, camY + b.Y1 + 1 + vp.Y), DropSurfaceUnits) is not null
                    && _drops.Match(frame, frameWidth, _mask, new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height)) is not null)
                {
                    blobs.Add(b);
                    DropsInSteam++;
                }
        th = Stopwatch.GetTimestamp();
        cursor ??= CursorInBlobs(blobs, frame, frameWidth);
        HudMs += Stopwatch.GetElapsedTime(th).TotalMilliseconds;
        pet ??= FindPetAfterBlobs(blobs, frame, frameWidth, ctx, camX, camY, tags, own, clear: !full);
        var tComp = sw.Elapsed.TotalMilliseconds - ocrMs;

        // A temporal frame's verified predictions join the residual blobs, so tags and HP bars attach to them as to any blob.
        var predicted = new Dictionary<Blob, Prediction>(ReferenceEqualityComparer.Instance);
        if (!full)
            foreach (var p in predictions)
            {
                var pb = BlobOf(p, vp);
                blobs.Add(pb);
                predicted[pb] = p;
            }
        if (DebugDir is not null) SnapshotBlobs(blobs);

        var candidates = new List<Candidate>();
        var tagUsed = new bool[tags.Count];
        var tagOwner = new Blob?[tags.Count];
        // The tag at the pet's place is the pet's.
        if (pet is { Tag: >= 0 } pf) tagUsed[pf.Tag] = true;
        // Small blobs touching the own character are parts of it, unless a drop's icon matches them (a coin the character stands by).
        var besideOwn = new Dictionary<Blob, DropMatch>(ReferenceEqualityComparer.Instance);
        foreach (var b in blobs)
        {
            var touchesOwn = own is { } ob && b.X0 + vp.X <= ob.X + ob.Width + 2 && b.X1 + vp.X >= ob.X - 2 && b.Y0 + vp.Y <= ob.Y + ob.Height + 2 && b.Y1 + vp.Y >= ob.Y - 2;
            if (!touchesOwn || b.Pixels >= OwnFragmentPixels || predicted.ContainsKey(b) || _drops is null || b.Width > EntityTracker.DropMaxSize + 7 || b.Height > EntityTracker.DropMaxSize + 7) continue;
            if (_drops.Match(frame, frameWidth, _mask, new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height)) is { } dm) { besideOwn[b] = WithAnchor(dm, camX, camY); DropsBesideOwn++; }
        }
        foreach (var b in blobs)
        {
            var touchesOwn = own is { } ob && b.X0 + vp.X <= ob.X + ob.Width + 2 && b.X1 + vp.X >= ob.X - 2 && b.Y0 + vp.Y <= ob.Y + ob.Height + 2 && b.Y1 + vp.Y >= ob.Y - 2;
            var isPredicted = predicted.TryGetValue(b, out var pr);
            if (touchesOwn && b.Pixels < OwnFragmentPixels && !isPredicted && !besideOwn.ContainsKey(b)) continue;
            // The tag under the blob, if any. A predicted mob stands where its frame is, whatever part of it the foreground showed.
            int bx0 = b.X0, bx1 = b.X1, by1 = b.Y1;
            if (isPredicted && pr!.Match is var pm) { bx0 = pm.Left + pm.Frame.X0 - vp.X; bx1 = pm.Left + pm.Frame.X1 - vp.X; by1 = pm.Top + pm.Frame.Y1 - vp.Y; }
            var cx = (bx0 + bx1) / 2.0;
            var ti = -1;
            var bestCost = double.MaxValue;
            for (var i = 0; i < tags.Count; i++)
            {
                if (tagUsed[i]) continue;
                var t = tags[i];
                var gapRows = t.Y0 - by1;
                // Under a predicted mob a tag anywhere below its frame is its own (its start can hide behind the own character).
                var slack = isPredicted ? (bx1 - bx0 + 1) / 2.0 : TagSlackX;
                if (gapRows < -4 || gapRows > TagAboveRows || Math.Abs(t.CentreX - cx) > t.Width / 2.0 + slack) continue;
                // A blob much wider than the tag is two sprites run together (the pet against a boar); the tag then
                // stands on its own rather than naming both. A predicted mob is one sprite.
                if (!isPredicted && bx1 - bx0 + 1 > Math.Max(t.Width * 1.6, t.Width + 40)) continue;
                var cost = Math.Abs(t.CentreX - cx) + Math.Abs(gapRows - 2);
                if (cost < bestCost) { bestCost = cost; ti = i; }
            }
            // The tag goes to the biggest blob standing on it, not to a stray fragment that happens to be closer; a mob a
            // prediction verified before any blob of the residual (what its frame left unexplained is no other sprite's).
            if (ti >= 0 && tagOwner[ti] is { } prev && (predicted.ContainsKey(prev) && !isPredicted || predicted.ContainsKey(prev) == isPredicted && prev.Pixels >= b.Pixels)) ti = -1;
            if (ti >= 0) tagOwner[ti] = b;
        }
        foreach (var b in blobs)
        {
            var touchesOwn = own is { } ob && b.X0 + vp.X <= ob.X + ob.Width + 2 && b.X1 + vp.X >= ob.X - 2 && b.Y0 + vp.Y <= ob.Y + ob.Height + 2 && b.Y1 + vp.Y >= ob.Y - 2;
            if (touchesOwn && b.Pixels < OwnFragmentPixels && !predicted.ContainsKey(b) && !besideOwn.ContainsKey(b)) continue;
            var cx = (b.X0 + b.X1) / 2.0;
            var ti = Array.FindIndex(tagOwner, o => ReferenceEquals(o, b));
            TagBox? tb = null;
            if (ti >= 0 && !tagUsed[ti]) { tagUsed[ti] = true; tb = tags[ti]; }
            // With a tag the feet are exact: centred on the tag, a couple of rows above its box.
            var feet = tb is not null ? new PointD(tb.CentreX + vp.X, tb.Y0 - 2 + vp.Y) : new PointD(cx + vp.X, b.Y1 + 1 + vp.Y);
            // An HP bar over the blob marks a mob that was hit.
            var bar = hpBars.Any(r => r.Rect.Y + r.Rect.Height - 1 - vp.Y <= b.Y0 + 4 && r.Rect.Y + r.Rect.Height - 1 - vp.Y >= b.Y0 - 40 && Math.Abs(r.Rect.X + r.Rect.Width / 2.0 - vp.X - cx) <= 30);
            var isPredicted = predicted.TryGetValue(b, out var prediction);
            var cand = MakeCandidate(ctx, camX, camY, new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height), feet, b.Pixels, tb,
                bar, touchesOwn, isPredicted ? null : Thumb(frame, frameWidth, vp, b), ownWorld);
            if (isPredicted) Predicted(cand, prediction!, ctx, camX, camY);
            if (besideOwn.TryGetValue(b, out var nearOwnDrop)) cand.Drop = nearOwnDrop;
            candidates.Add(cand);
        }
        // Tags with no blob over them still mark an entity: its sprite stands over unreliable background, or is hidden.
        for (var i = 0; i < tags.Count; i++)
        {
            if (tagUsed[i]) continue;
            var t = tags[i];
            // Skip the own tag (inside the own box) and fragments of it.
            if (own is { } ob && t.CentreX + vp.X >= ob.X && t.CentreX + vp.X <= ob.X + ob.Width && t.Y0 + vp.Y >= ob.Y && t.Y0 + vp.Y <= ob.Y + ob.Height + 4) continue;
            var feet = new PointD(t.CentreX + vp.X, t.Y0 - 2 + vp.Y);
            var guessBox = new CaptureRect((int)feet.X - 20, (int)feet.Y - 45, 40, 45);
            candidates.Add(MakeCandidate(ctx, camX, camY, guessBox, feet, 0, t, false, false, null, ownWorld));
        }
        if (pet is not null) candidates.Add(PetCandidate(pet, tags, ctx, camX, camY));
        candidates.AddRange(coins);
        MarkPetFragments(candidates, camX, camY, ctx.Time);

        var tCand = sw.Elapsed.TotalMilliseconds - ocrMs;
        foreach (var c in candidates) c.Floor ??= FloorFor(c, ctx);
        DetachPetTags(candidates, ctx, camX, camY, ownWorld);
        // The own skill effects are recognised before the species search, which then leaves them (and their pixels) alone.
        RecogniseEffects(candidates, frame, frameWidth);
        _sweepsThisFrame = 0;
        // Larger blobs first: the per-frame budget of searches under unmatched blobs goes to the likeliest mobs.
        candidates.Sort((a, b) => b.Pixels.CompareTo(a.Pixels));
        // A tag alone that is the pet's marks the pet (hidden, or over background the mosaic cannot vouch for): no sprite is tried.
        // A verified prediction is a match already.
        if (_mobs is not null)
            for (var i = 0; i < candidates.Count; i++)
                if (candidates[i].Found == FoundBy.Search && candidates[i].Skill is null && candidates[i].PetSprite is null && candidates[i].Drop is null
                    && !(candidates[i].Pixels == 0 && _tracker.Names.IsCharacterTag(candidates[i].Tag?.Text)))
                {
                    var ts = Stopwatch.GetTimestamp();
                    var rest = Identify(candidates[i], frame, frameWidth, ctx, camX, camY);
                    candidates[i].SearchMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
                    if (rest is not null) candidates.Add(rest);
                }
        if (_mobs is not null) SweepFragments(candidates, frame, frameWidth, ctx, camX, camY);
        // On a temporal frame the foothold search leaves out the columns of the mobs the predictions found.
        if (_mobs is not null) SearchGaps(candidates, frame, frameWidth, ctx, camX, camY, own,
            full ? [] : predictions.Select(p => new CaptureRect(p.Match.Left + p.Match.Frame.X0, p.Match.Top + p.Match.Frame.Y0, p.Match.Frame.ExtentWidth, p.Match.Frame.ExtentHeight)).ToList());
        if (_mobs is not null && full) Reconcile(candidates, predictions, ctx, camX, camY);
        if (_mobs is not null) OneSpritePerMob(candidates, ctx, camX, camY);
        if (_mobs is not null) DropExplainedBlobs(candidates);
        if (_mobs is not null) CapBySpawns(candidates, ctx.Frame);
        var tSprites = sw.Elapsed.TotalMilliseconds - ocrMs;
        if (_drops is not null)
        {
            var onFloor = new List<Candidate>();
            foreach (var c in candidates)
            {
                if (c.Sprite is not null || c.Drop is not null || c.PetSprite is not null || c.Skill is not null || c.Pixels <= 0 || c.TouchesOwn) continue;
                var onSurface = c.SurfaceDistance is { } sd && Math.Abs(sd) <= DropSurfaceUnits;
                // A kill's drops fly in an arc before they land: near a recent death a small candidate is a drop wherever it is.
                var flying = !onSurface && c.Box.Width <= EntityTracker.DropMaxSize + 7 && c.Box.Height <= EntityTracker.DropMaxSize + 7 && NearDeath(c, ctx.Time);
                if (!onSurface && !flying) continue;
                // A piece of a coin (in steam, behind something) is placed by the coin's anchor on the foothold under it.
                var dm = _drops.Match(frame, frameWidth, _mask, c.Box);
                if (dm is null && onSurface)
                {
                    var tc = Stopwatch.GetTimestamp();
                    dm = CoinOnFoothold(c, frame, frameWidth, ctx, camX, camY);
                    CoinMs += Stopwatch.GetElapsedTime(tc).TotalMilliseconds;
                }
                // A sliver at the floor near a kill, under background the mosaic cannot vouch for or beside a drop identified there, is
                // (the rest of) the kill's drops.
                if (dm is null && onSurface && c.Box.Width <= EntityTracker.DropMaxSize && c.Box.Height <= EntityTracker.DropMaxSize && NearDeath(c, ctx.Time)
                    && (HiddenAbove(c.Box, camX, camY) || BesideDrop(c, candidates)))
                {
                    var tf = Stopwatch.GetTimestamp();
                    dm = DropsOnFloor(c, candidates, onFloor, frame, frameWidth, ctx, camX, camY);
                    FloorDropMs += Stopwatch.GetElapsedTime(tf).TotalMilliseconds;
                }
                if (dm is null) continue;
                c.Drop = WithAnchor(dm, camX, camY);
                if (flying) { c.InFlight = true; DropsInFlight++; }
            }
            candidates.AddRange(onFloor);
        }
        MoveCharacterTags(candidates, ctx, camX, camY, ownWorld);
        if (own is { } ownBox) LastOwnBox = own = DropOwnSlivers(candidates, ownBox);
        if (_damage is not null) ReadDamage(candidates, frame, frameWidth, ctx, camX, camY, own);
        if (!full) DropLeftovers(candidates);
        DropPetLeftovers(candidates);
        var tDrops = sw.Elapsed.TotalMilliseconds - ocrMs;
        // Diagnostics are timed apart and left out of the frame's cost.
        var diagMs = 0.0;
        if (SpriteDiagnostics && _mobs is not null)
        {
            var d0 = sw.Elapsed.TotalMilliseconds;
            // A prediction's candidate is scored on every pixel; the rest on the pixels the predictions left (as the search saw them).
            var explained = _matcher.Explained;
            foreach (var c in candidates)
            {
                _matcher.Explained = c.Found == FoundBy.Search ? explained : null;
                Diagnose(c, frame, frameWidth, ctx);
            }
            _matcher.Explained = explained;
            if (_drops is not null)
                foreach (var c in candidates)
                    if (c.Sprite is null && c.Pixels > 0 && !c.TouchesOwn && c.SurfaceDistance is { } sd2 && Math.Abs(sd2) <= DropSurfaceUnits)
                        _drops.Diagnose(ctx.Frame, frame, frameWidth, _mask, c.Box);
            diagMs = sw.Elapsed.TotalMilliseconds - d0;
            DiagnosticMs += diagMs;
        }

        // The gap filler (off by default): finds on the floor band a moving camera left Ignored join the candidates; timed apart.
        var gapMs = GapFiller ? FillGaps(candidates, predictions, frame, frameWidth, ctx, camX, camY) : 0;

        _tracker.SpeciesCap = _mobs is null ? null : CapOf;
        _tracker.FloorBound = _mobs is not null && !_mobs.HasFlyers;
        if (_mobs is not null)
        {
            _tracker.Species ??= id => _mobs.ById(id)?.Species;
            _tracker.TagSpecies ??= text => _mobs.NamedLoose(TagDetector.Parse(text).Name ?? text).Select(e => e.Species.Id).ToList();
        }
        var entities = _tracker.Update(candidates, ctx.Frame, ctx.Time, ctx.Sequence);
        for (var k = 0; k < entities.Count && k < candidates.Count; k++)
            if (candidates[k].SearchMs > 0)
            {
                var kind = entities[k].Kind;
                SearchByKind[kind] = (SearchByKind.GetValueOrDefault(kind).Candidates + 1, SearchByKind.GetValueOrDefault(kind).Ms + candidates[k].SearchMs);
            }
        CreditKill(entities, ctx, ownWorld);
        AssignTagOwners(tags, candidates, entities, own);
        if (_mobs is not null) PlaceHitboxes(entities, ctx, camX, camY);
        var bars = AttachHpBars(hpBars, entities);
        RememberPoses(entities, ctx, full);
        RememberDeaths(entities, ctx.Time);
        CloudInterest(entities, ctx, camX, camY);
        _lastDetected = true;
        if (SpriteDiagnostics)
            for (var i = Diagnostics.Count - 1; i >= 0 && Diagnostics[i].Frame == ctx.Frame; i--)
                if (Diagnostics[i].Source is { } src && candidates.IndexOf(src) is var k and >= 0) Diagnostics[i].Kind = entities[k].Kind;
        LastTags = tags;
        var ms = sw.Elapsed.TotalMilliseconds - ocrMs - diagMs;
        StageMs[0] += tReg; StageMs[1] += tDiff - tReg; StageMs[2] += tTags - tDiff; StageMs[3] += tTemporal - tTags; StageMs[4] += tComp - tTemporal;
        StageMs[5] += tSprites - tCand; StageMs[6] += tDrops - tSprites; StageMs[7] += ms - tDrops - gapMs + (tCand - tComp);
        if (GapFiller) { GapFillerMs += gapMs; CountGapTracks(candidates, entities, frame, frameWidth, ctx, ms); }
        SpriteStageMs.Add(tSprites - tCand);
        StageFrames++;
        if (DebugDir is not null) Dump(frame, frameWidth, ctx, entities, camX, camY);
        return new EntityFrame(ctx.Frame, ctx.Time, entities, (camX, camY), reg.Value, ms, ocrMs) { HpBars = bars, Cursor = cursor };
    }

    /// <summary>
    /// Feet within this many units of a surface (above or below, at the box's x) let a candidate count as standing on
    /// that floor for the species order; the box's bottom edge can sit a few pixels above the sprite's real feet.
    /// </summary>
    public const double FloorUnits = 16;

    /// <summary>A small candidate is tried as a drop when its box bottom is within this many units of a surface (drops bob above the ground a little).</summary>
    public const double DropSurfaceUnits = 10;

    /// <summary>Score margin by which a recolour must beat the species the track already has to take over (temporal coherence).</summary>
    public const double RecolourMargin = 0.05;

    /// <summary>The frames of one species in the order to try them: the track's last frame and its animation first.</summary>
    private static IEnumerable<SpriteFrame> FrameOrder(MobSpecies species, SpriteMatch? last)
    {
        if (last is not { } l || l.Species.Id != species.Id) return species.Frames;
        return species.Frames.OrderBy(f => f.Facing == l.Frame.Facing ? 0 : 1).ThenBy(f => f.Anim == l.Frame.Anim ? 0 : 1)
            .ThenBy(f => f.Anim == l.Frame.Anim ? (f.Index - l.Frame.Index + 64) % 64 : 0);
    }

    private (int? Floor, int? TagSpeciesCount, List<MapMobSet.Entry> Order, SpriteMatch? Last) PlanFor(Candidate c, DetectionContext ctx)
    {
        var hit = c.Foothold ?? ctx.Footholds.Nearest(c.World, FloorUnits);
        var floor = hit is { } h ? ctx.Footholds.FloorOf(h.Foothold.Id) : null;
        c.Floor = floor;
        var hint = _tracker.Hint(c, ctx.Time);
        // A tag read now or earlier on the track names the species.
        string? tagName = null;
        if (c.Tag?.Text is { } text)
        {
            var (level, name) = TagDetector.Parse(_tracker.Names.Strip(text));
            if (level is not null && name is not null) tagName = _tracker.Names.MatchMob(name) ?? name;
            else if (name is not null && _tracker.Names.MatchMob(name) is { } known) tagName = known;
        }
        var named = _mobs!.Named(tagName);
        var order = _mobs.Order(floor, c.World.Y, hint?.MobId, named);
        // A name only the likely track's tags showed puts its species first but rules out none: the likely track is a
        // guess (the nearest within the gate), and next to a tagged mob it can be that mob's while the candidate is another.
        if (named.Count == 0 && hint?.TagName is { } hinted)
            foreach (var e in _mobs.Named(hinted).Reverse())
                if (order.Remove(e)) order.Insert(hint.MobId is { } id && order.Count > 0 && order[0].Species.Id == id ? 1 : 0, e);
        return (floor, named.Count, order, hint?.LastSprite);
    }

    /// <summary>
    /// Blobs at least this wide that match no frame as a whole are tried as two sprites side by side (a crowd of
    /// stumps walking together merges into one blob): frames aligned on the left and right edges.
    /// </summary>
    public const int SplitMinWidth = 60;

    /// <summary>Highest a non-flying mob's feet are seen above a surface: a boar's jump, plus slack. Chosen, not tuned.</summary>
    public const double MaxAirUnits = 90;

    /// <summary>Candidates not tried against sprites because they float with no surface below (see <see cref="MaxAirUnits"/>).</summary>
    public int SkippedInAir { get; private set; }

    /// <summary>The part of a split blob left over after one sprite was taken out must be at least this wide to be tried as another.</summary>
    public const int SplitRestWidth = 20;

    /// <summary>
    /// Verifies a candidate against the map's sprites and, when one matches, moves its feet to the sprite's origin. A
    /// wide blob matched on one edge is cut to that sprite, and the rest is returned as a new candidate.
    /// </summary>
    private Candidate? Identify(Candidate c, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY)
    {
        c.BoxSurfaceDistance = c.SurfaceDistance;
        var blob = c.Pixels > 0;
        // Nothing but a flyer floats: without one on the map, a candidate with no surface within reach below its feet
        // (a jumper's leap included) is an effect, a cloud or a parallax layer.
        if (!_mobs!.HasFlyers && c.Foothold is null && ctx.Footholds.Below(c.World, MaxAirUnits, FloorUnits) is null) { SkippedInAir++; return null; }
        var (_, _, order, last) = PlanFor(c, ctx);
        var tries = order.SelectMany(e => FrameOrder(e.Species, last).Select(f => (e.Species, f)));
        // A tag with no blob over it: place the frames by their origin on the tag's feet, without a box to size them by.
        PointD? feetOnly = blob ? null : c.Feet;
        _matcher.Prepare(frame, frameWidth, _mask, c.Box, feetOnly);
        var match = _matcher.First(c.Box, feetOnly, tries, sizeFilter: blob);
        // A box cut on one side (by the screen's edge, unreliable background, something in front) holds the frame's other edge: the
        // frames wider than the box are aligned on its left and on its right edge too (a Red Snail on fx-combat-exact's video frame
        // 207 agreed on 0.41 of its pixels seven columns off the box's centre and was taken for an effect).
        // A box beside hidden background (the mosaic cannot vouch for it, or a translucent layer whitens it) is cut there even when
        // some frame fits it weakly: the differencing sees no mob there, so a mob standing half in it leaves a blob of its other
        // half (the Wild Boar of fx-combat-exact's keyframe 496 under two clouds: its head, 34 of its 71 columns, which Dark Stump
        // die1/2 fitted on 0.23 of its pixels while move/0 aligned on the box's right edge shows exactly).
        var weakCut = (match is null || match.Value.Score < CutRetryScore) && blob && CutByUnknown(c.Box, camX, camY);
        if ((match is null || weakCut) && blob && c.Pixels >= SweepMinPixels && c.SurfaceDistance is { } csd && Math.Abs(csd) <= FloorUnits && order.Count > 0)
        {
            var tc = Stopwatch.GetTimestamp();
            // Beside hidden foreground the box may hold less than half the mob: frames up to WideCut times its width are aligned
            // too, on a patch wide enough for them.
            if (weakCut)
            {
                var reach = (int)(WideCut * c.Box.Width);
                _matcher.PrepareRegion(frame, frameWidth, _mask, c.Box.X - reach, c.Box.Y - c.Box.Height - SpriteMatcher.SearchY - 2,
                    c.Box.X + c.Box.Width - 1 + reach, c.Box.Y + c.Box.Height - 1 + SpriteMatcher.SearchY + 2);
            }
            // ... and of the likeliest few species, the best: the track there may have taken the mob for another.
            var cut = FirstCut(c.Box, order.Take(weakCut ? CutSpecies : 1).SelectMany(e => FrameOrder(e.Species, last).Select(f => (e.Species, f))),
                weakCut ? WideCut : SpriteMatcher.CutTolerance + 1, firstSpecies: !weakCut);
            if (cut is { } cm && (match is not { } fitted || cm.Score > fitted.Score)) { match = cm; if (weakCut) CutBesideUnknown++; }
            CutMs += Stopwatch.GetElapsedTime(tc).TotalMilliseconds;
        }
        Candidate? rest = null;
        if (match is null && blob && c.Box.Width >= SplitMinWidth && _matcher.FirstAtEdges(c.Box, tries) is { } edge)
        {
            match = edge;
            // Cut the blob at the matched sprite's extent; what is left is another candidate.
            int sx0 = edge.Left + edge.Frame.X0, sx1 = edge.Left + edge.Frame.X1, bx1 = c.Box.X + c.Box.Width - 1;
            var vp = _mask.Viewport;
            var restBox = sx0 - c.Box.X > bx1 - sx1 ? new CaptureRect(c.Box.X, c.Box.Y, sx0 - c.Box.X, c.Box.Height) : new CaptureRect(sx1 + 1, c.Box.Y, bx1 - sx1, c.Box.Height);
            var mine = new CaptureRect(Math.Max(c.Box.X, sx0), c.Box.Y, Math.Min(bx1, sx1) - Math.Max(c.Box.X, sx0) + 1, c.Box.Height);
            if (restBox.Width >= SplitRestWidth)
            {
                var restPixels = _fg.CountIn(restBox.X - vp.X, restBox.Y - vp.Y, restBox.X + restBox.Width - 1 - vp.X, restBox.Y + restBox.Height - 1 - vp.Y);
                if (restPixels >= ForegroundExtractor.MinPixels)
                    rest = MakeCandidate(ctx, camX, camY, restBox, new PointD(restBox.X + restBox.Width / 2.0, restBox.Y + restBox.Height), restPixels, null,
                        false, false, null, _ownWorld);
            }
            c.Pixels = Math.Max(1, _fg.CountIn(mine.X - vp.X, mine.Y - vp.Y, mine.X + mine.Width - 1 - vp.X, mine.Y + mine.Height - 1 - vp.Y));
            c.Box = mine;
        }
        // No frame fits the box: the box may be cut (by the own character's mask, an effect drawn over the mob, unreliable
        // background), so the mob is searched along the foothold under it, feet on the line. A mob known to be hit (its tag's level
        // badge, its HP bar) is searched for under any blob of it, its probe allowed more pixels under the skill that hit it: the
        // frozen Wild Boar of fx-combat-exact's video frame 3 under Cold Beam's shards was a 21x5 'Mob?' at its feet.
        var hitMob = c.HpBar || TagNames.IsMobTag(c.Tag?.Text);
        if (match is null && blob && rest is null && (c.Pixels >= SweepMinPixels || hitMob) && _sweepsThisFrame++ < MaxSweepsPerFrame
            && SweepUnder(c, frame, frameWidth, ctx, camX, camY, order, hitMob ? FootholdSearch.HitProbeMisses : FootholdSearch.ProbeMisses) is { } swept)
        {
            match = swept;
            feetOnly = swept.Feet;
            c.Swept = true;
            SweepAttached++;
        }
        if (match is not { } m) return rest;
        m = Contest(c, m, order, last, feetOnly, c.Swept ? (FootholdSearch.RefineX, FootholdSearch.RefineY) : null);
        c.Sprite = m;
        var feet = m.Feet;
        c.Feet = feet;
        c.World = new PointD(camX + feet.X, camY + feet.Y);
        var nearest = ctx.Footholds.Nearest(c.World, 40);
        c.SurfaceDistance = nearest?.Distance;
        c.Foothold = nearest is { } n && Math.Abs(n.Distance) <= EntityTracker.OnFootholdUnits ? nearest : null;
        c.Floor = FloorFor(c, ctx);
        return rest;
    }

    /// <summary>Boxes matched by a frame aligned on one of their edges because the box was cut (see <see cref="Identify"/>), since construction.</summary>
    public int CutAligned { get; private set; }

    /// <summary>Boxes beside hidden background (see <see cref="CutByUnknown"/>), fitted weakly or not at all, that a wide edge-aligned frame fitted better.</summary>
    public int CutBesideUnknown { get; private set; }

    /// <summary>A fit below this score on a box beside hidden background is retried with frames aligned on the box's edges.</summary>
    public const double CutRetryScore = 0.5;

    /// <summary>Frames up to this many times as wide as a box beside hidden foreground are aligned on its edges (twice otherwise).</summary>
    public const double WideCut = 3;

    /// <summary>Species (likeliest first) whose frames are aligned on the edges of a box beside hidden foreground.</summary>
    public const int CutSpecies = 2;

    /// <summary>Columns beside a box looked at, and the share of their pixels (box rows) the mosaic must not vouch for, for the box to be cut there.</summary>
    public const int CutColumns = 3;
    public const double CutUnknownShare = 0.5;

    /// <summary>
    /// Whether what lies just beside a box's left or right edge hides foreground: background the mosaic cannot vouch for, or
    /// background whitened by a translucent layer (steam, fog, clouds), where a mob's pixels under the layer are taken for the
    /// layer (the box may be cut there).
    /// </summary>
    private bool CutByUnknown(CaptureRect box, int camX, int camY)
    {
        var m = _mosaic;
        var vp = _mask.Viewport;
        var classes = _fg.Classes;
        int fw = _fg.Width, fh = _fg.Height;
        bool Side(int x0)
        {
            int unknown = 0, n = 0;
            for (var y = box.Y; y < box.Y + box.Height; y++)
                for (var x = x0; x < x0 + CutColumns; x++)
                {
                    n++;
                    int mx = camX + x - m.OriginX, my = camY + y - m.OriginY;
                    if ((uint)mx >= (uint)m.Width || (uint)my >= (uint)m.Height || m.ForegroundUsable[my * m.Width + mx] == 0) { unknown++; continue; }
                    int vx = x - vp.X, vy = y - vp.Y;
                    if ((uint)vx < (uint)fw && (uint)vy < (uint)fh && classes[vy * fw + vx] is (byte)PixelClass.Fog or (byte)PixelClass.BrightFog) unknown++;
                }
            return n > 0 && unknown >= CutUnknownShare * n;
        }
        return Side(box.X - CutColumns) || Side(box.X + box.Width);
    }
    public double CutMs { get; private set; }

    /// <summary>The best frame, wider than the box, that passes aligned on the box's left or right edge, or null (the likeliest species only, for cost).</summary>
    private SpriteMatch? FirstCut(CaptureRect box, IEnumerable<(MobSpecies Species, SpriteFrame Frame)> tries, double widest, bool firstSpecies = true)
    {
        SpriteMatch? found = null;
        foreach (var (species, f) in tries)
        {
            if (firstSpecies && found is { } m0 && species.Id != m0.Species.Id) break;
            if (f.ExtentWidth <= box.Width + SpriteMatcher.SearchX || f.ExtentWidth > widest * box.Width || !SpriteMatcher.Fits(f, f.ExtentWidth, box.Height)) continue;
            foreach (var align in new[] { Align.Left, Align.Right })
                if (_matcher.Best(box, null, species, f, found?.Score ?? SpriteMatcher.Threshold, align: align) is { } m && m.Score >= SpriteMatcher.Threshold
                    && (found is null || m.Score > found.Value.Score)) found = m;
        }
        if (found is not null) CutAligned++;
        return found;
    }

    /// <summary>The floor (foothold chain component) a candidate stands on or just above (<see cref="FloorUnits"/>), if any.</summary>
    private static int? FloorFor(Candidate c, DetectionContext ctx) =>
        (c.Foothold ?? ctx.Footholds.Nearest(c.World, FloorUnits)) is { } h ? ctx.Footholds.FloorOf(h.Foothold.Id) : null;

    /// <summary>
    /// Two matches whose feet are this close and whose extents cover half the larger one are one mob (two mobs side by side
    /// are a body width apart). A small mob in front of a large one (a Red Snail before a Dark Axe Stump on the top platform,
    /// feet 11 apart on frame 751) covers too little of it and both are kept.
    /// </summary>
    public const int SameMobX = 12, SameMobY = 8;

    public static bool SameMob(in SpriteMatch a, in SpriteMatch b) =>
        Math.Abs(a.Feet.X - b.Feet.X) <= SameMobX && Math.Abs(a.Feet.Y - b.Feet.Y) <= SameMobY && FootholdSearch.OverlapOfLarger(a, b) >= 0.5;

    /// <summary>
    /// Two candidates whose matched sprites stand in one place (<see cref="SameMob"/>) show one mob: a blob with the
    /// mob and the pet run together matched a weak pose while the search under a piece of it found the exact one. The
    /// better match keeps the sprite; the other candidate is left to be whatever else it is.
    /// </summary>
    private void OneSpritePerMob(List<Candidate> candidates, DetectionContext ctx, int camX, int camY)
    {
        var matched = candidates.Where(c => c.Sprite is not null).OrderByDescending(c => c.Sprite!.Value.Score).ThenByDescending(c => c.Sprite!.Value.Compared).ToList();
        for (var i = 0; i < matched.Count; i++)
        {
            if (matched[i].Sprite is not { } keep) continue;
            for (var j = i + 1; j < matched.Count; j++)
                if (matched[j].Sprite is { } other && SameMob(keep, other))
                {
                    // A predicted mob the search found in a better pose of the same species (the search under a piece of its
                    // blob): the better pose goes to the prediction's candidate, which holds the mob's blob and its track, and
                    // the piece is left to be whatever else it is.
                    if (matched[j].Found != FoundBy.Search && matched[i].Found == FoundBy.Search && other.Species.Id == keep.Species.Id)
                    {
                        matched[j].Sprite = keep;
                        matched[j].Found = FoundBy.Search;
                        matched[j].Swept = matched[i].Swept;
                        Place(matched[j], keep.Feet, ctx, camX, camY);
                        matched[j].Floor = FloorFor(matched[j], ctx);
                        (matched[i], matched[j]) = (matched[j], matched[i]);
                    }
                    matched[j].Sprite = null;
                    matched[j].Swept = false;
                    matched[j].Found = FoundBy.Search;
                    matched[j].PredictedTrack = null;
                    if (matched[j].Pixels == 0) candidates.Remove(matched[j]);
                    DuplicateSprites++;
                }
        }
    }

    /// <summary>
    /// An unverified blob whose foreground lies mostly (<see cref="ExplainedShare"/>) inside the silhouettes of mobs matched
    /// elsewhere and the pet's box is those run together (the pet beside a Wild Boar, 106x47 on fx-combat-exact's frame 751,
    /// once the boar was found exactly under a piece of it): it is dropped rather than shown as a box spanning both.
    /// </summary>
    private void DropExplainedBlobs(List<Candidate> candidates)
    {
        var sprites = candidates.Where(c => c.Sprite is not null).Select(c => c.Sprite!.Value).Concat(candidates.Where(c => c.PetSprite is not null).Select(c => c.PetSprite!.Value)).ToList();
        var pets = candidates.Where(c => c.Pixels == 0 && _tracker.Names.IsPetTag(c.Tag?.Text)).Select(c => c.Box).ToList();
        if (sprites.Count == 0) return;
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        int w = _fg.Width, h = _fg.Height;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (c.Sprite is not null || c.Drop is not null || c.Damage is not null || c.PetSprite is not null || c.Skill is not null || c.Pixels <= 0) continue;
            // A tagged blob is dropped only as the leftover of a predicted mob (below), whose tag it then hands on.
            var heir = c.Tag is { } tag && !_tracker.Names.IsCharacterTag(tag.Text)
                ? candidates.FirstOrDefault(p => p.Found != FoundBy.Search && p.Tag is null && p.Sprite is { } ps && Covers(ps, c.Box) >= ExplainedShare) : null;
            if (c.Tag is not null && heir is null) continue;
            int total = 0, inside = 0;
            for (var y = c.Box.Y; y < c.Box.Y + c.Box.Height; y++)
            {
                var vy = y - vp.Y;
                if ((uint)vy >= (uint)h) continue;
                for (var x = c.Box.X; x < c.Box.X + c.Box.Width; x++)
                {
                    var vx = x - vp.X;
                    if ((uint)vx >= (uint)w || mask[vy * w + vx] == 0) continue;
                    total++;
                    var covered = pets.Any(p => x >= p.X && x < p.X + p.Width && y >= p.Y && y < p.Y + p.Height);
                    foreach (var m in sprites)
                    {
                        if (covered) break;
                        int tx = x - m.Left, ty = y - m.Top;
                        covered = (uint)tx < (uint)m.Frame.Width && (uint)ty < (uint)m.Frame.Height && m.Frame.Bgra[(ty * m.Frame.Width + tx) * 4 + 3] >= SpriteFrame.VisibleAlpha;
                    }
                    if (covered) inside++;
                }
            }
            if (total > 0 && inside >= ExplainedShare * total && sprites.Any(m => Covers(m, c.Box) > 0))
            {
                // On a temporal frame the pixels of a predicted mob that disagree with its frame (steam, a fading die pose) can be
                // a blob of their own, and the mob's tag, below them all, went to that blob: the tag goes to the mob.
                if (heir is not null) heir.Tag = c.Tag;
                candidates.RemoveAt(i);
                ExplainedBlobs++;
            }
        }
    }

    public const double ExplainedShare = 0.6;

    /// <summary>
    /// On a temporal frame, the pixels of a predicted mob that disagree with its frame (steam, a fading die pose) can be a blob
    /// of their own: an untagged blob larger than a drop that nothing verified (no sprite, icon or digits) and whose box a
    /// predicted mob's frame extent covers by <see cref="LeftoverShare"/> is that mob's and is dropped (the dying Wild Boar on
    /// fx-combat-exact's frames 780 and 781, its die pose in steam, was shown twice).
    /// </summary>
    private void DropLeftovers(List<Candidate> candidates)
    {
        var predicted = candidates.Where(p => p.Found != FoundBy.Search && p.Sprite is not null).Select(p => p.Sprite!.Value).ToList();
        if (predicted.Count == 0) return;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (c.Sprite is not null || c.Drop is not null || c.Damage is not null || c.Tag is not null || c.PetSprite is not null || c.Skill is not null || c.Pixels <= 0) continue;
            // A drop-sized piece no icon matched is the mob's when its frame holds most of it (a Red Snail's unexplained pixels on the
            // video's frame 207, 24x30, three quarters inside its frame, were an effect scoring 0.41 as the snail).
            var small = c.Box.Width <= EntityTracker.DropMaxSize && c.Box.Height <= EntityTracker.DropMaxSize;
            if (!predicted.Any(m => Covers(m, c.Box) >= (small ? SmallLeftoverShare : LeftoverShare))) continue;
            candidates.RemoveAt(i);
            ExplainedBlobs++;
        }
    }

    /// <summary>Share of a blob's box a predicted mob's frame extent must cover for the blob to be that mob's leftover (<see cref="DropLeftovers"/>).</summary>
    public const double LeftoverShare = 0.5, SmallLeftoverShare = 0.7;

    /// <summary>Blobs dropped by <see cref="DropExplainedBlobs"/> since construction.</summary>
    public int ExplainedBlobs { get; private set; }

    /// <summary>Sprites dropped by <see cref="OneSpritePerMob"/> since construction.</summary>
    public int DuplicateSprites { get; private set; }

    /// <summary>
    /// Largest blob the pet's tag may name as the pet (Mara is about 40x45). A larger blob under the pet's tag is the pet run
    /// into a mob beside it (the pet and a Wild Boar, 106x47, on fx-combat-exact's frame 751; the pet and a frozen Dark Axe
    /// Stump, 63x81, on frame 2626): the tag then marks the pet alone, at the tag, and the blob is tried as a mob without it.
    /// </summary>
    public const int PetMaxWidth = 60, PetMaxHeight = 64;

    /// <summary>Pet tags taken off blobs too large for the pet since construction.</summary>
    public int PetTagsDetached { get; private set; }

    /// <summary>The map's species names and levels, handed to the tracker's names (see <see cref="TagNames.Species"/>).</summary>
    private IReadOnlyList<(string Name, int Level)>? _speciesNames;

    /// <summary>
    /// Every tag's owner, for the overlay (<see cref="TagBox.Owner"/>): the kind of the entity that carries it (a mob, the pet, a
    /// player), the pet for a tag of the pet's on no pet (its tag alone), self for the own tag, none for the rest. A tag reading a
    /// level badge is a mob's whatever carries it (fx-combat-exact's video frames 3 and 5: the Wild Boar's own "Lv. 25 Wild Boar",
    /// drawn cyan like the pet, read as the pet's).
    /// </summary>
    private void AssignTagOwners(List<TagBox> tags, List<Candidate> candidates, List<DetectedEntity> entities, CaptureRect? own)
    {
        if (tags.Count == 0) return;
        for (var k = 0; k < candidates.Count && k < entities.Count; k++)
        {
            if (candidates[k].Tag is not { } t) continue;
            var e = entities[k];
            t.OwnerTrack = e.TrackId;
            t.Owner = e.Kind switch
            {
                EntityKind.Mob => "mob",
                EntityKind.Pet => "pet",
                EntityKind.Player => "player",
                _ => candidates[k].PetTag || _tracker.Names.IsPetTag(t.Text) ? "pet" : null,
            };
        }
        var vp = _mask.Viewport;
        foreach (var t in tags)
        {
            if (TagNames.IsMobTag(t.Text)) t.Owner = "mob";
            else if (t.Owner is null && own is { } ob && t.CentreX + vp.X >= ob.X && t.CentreX + vp.X <= ob.X + ob.Width && t.Y0 + vp.Y >= ob.Y && t.Y0 + vp.Y <= ob.Y + ob.Height + 4) t.Owner = "self";
            else if (t.Owner is null && _tracker.Names.IsPetTag(t.Text)) t.Owner = "pet";
            t.Owner ??= "none";
        }
    }

    private void DetachPetTags(List<Candidate> candidates, DetectionContext ctx, int camX, int camY, PointD? ownWorld)
    {
        var vp = _mask.Viewport;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (c.Pixels <= 0 || c.Tag is not { } t || !_tracker.Names.IsPetTag(t.Text) || (c.Box.Width <= PetMaxWidth && c.Box.Height <= PetMaxHeight)) continue;
            c.Tag = null;
            PetTagsDetached++;
            Place(c, new PointD(c.Box.X + (c.Box.Width - 1) / 2.0, c.Box.Y + c.Box.Height), ctx, camX, camY);
            var feet = new PointD(t.CentreX + vp.X, t.Y0 - 2 + vp.Y);
            candidates.Add(MakeCandidate(ctx, camX, camY, new CaptureRect((int)feet.X - 20, (int)feet.Y - 45, 40, 45), feet, 0, t, false, false, null, ownWorld));
        }
    }

    /// <summary>Milliseconds spent on the HUD's overlays (log text, HP bars, cursor) and on the coins (following them, matching their pieces), since construction.</summary>
    public double HudMs { get; private set; }
    public double LogMs { get; private set; }
    public double BarMs { get; private set; }
    public double CoinMs { get; private set; }

    /// <summary>Drop-sized blobs of mostly whitened background (steam) kept because a drop's icon matched them, since construction.</summary>
    public int DropsInSteam { get; private set; }

    /// <summary>Rows between a predicted mob's frame bottom and a foothold line for the mob to stand on it (see <see cref="SearchGaps"/>).</summary>
    public const int CoveredRows = 8;

    /// <summary>Unmatched blobs with fewer foreground pixels than this are not searched for along their foothold.</summary>
    public const int SweepMinPixels = 150;

    /// <summary>Unmatched blobs searched along their foothold per frame at most, largest first: Ant Tunnel IV's cave makes dozens of effect blobs.</summary>
    public const int MaxSweepsPerFrame = 6;

    private int _sweepsThisFrame;

    /// <summary>Columns searched beyond an unmatched blob's box on either side, and rows below its bottom a foothold may lie.</summary>
    public const int SweepMargin = 40, SweepBelow = 30, SweepAbove = 16;

    /// <summary>
    /// Searches the footholds under an unmatched blob (surfaces from <see cref="SweepAbove"/> rows above its bottom to
    /// <see cref="SweepBelow"/> below) column by column for the plan's species (<see cref="FootholdSearch"/>); a found sprite
    /// must cover a third of the box. On fx-combat-exact's frame 781 the dying Wild Boar's blob was cut by the own
    /// character's box on its left and joined an ice shard on its right, so no frame fitted the box; die1/0 at its feet
    /// agrees on 60% of its pixels (the player and the shards cover the rest).
    /// </summary>
    private SpriteMatch? SweepUnder(Candidate c, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, List<MapMobSet.Entry> order,
        int misses = FootholdSearch.ProbeMisses)
    {
        if (_search is null) return null;
        var t0 = Stopwatch.GetTimestamp();
        var species = order.Select(e => e.Species).Where(sp => sp.Mobility != MobMobility.Fly).ToList();
        int bottom = c.Box.Y + c.Box.Height - 1, sx0 = c.Box.X - SweepMargin, sx1 = c.Box.X + c.Box.Width - 1 + SweepMargin;
        var hits = new List<(MobSpecies, SpriteFrame, int, int)>();
        foreach (var fh in _search.Under(ctx.Map, camX, camY, sx0, sx1, bottom - SweepAbove, bottom + SweepBelow))
            _search.Probe(frame, frameWidth, _mask, fh, camX, camY, sx0, sx1, species, [], hits, misses);
        SpriteMatch? best = null;
        if (hits.Count > 0)
            foreach (var m in _search.Score(_matcher, hits, FootholdSearch.Threshold, frame, frameWidth, _mask))
                if (Covers(m, c.Box) >= 1 / 3.0 && (best is null || m.Score > best.Value.Score)) best = m;
        // The contest that follows places other species around the find: their patch.
        if (best is { } found) _matcher.PrepareRegion(frame, frameWidth, _mask, (int)found.Feet.X - 100, (int)found.Feet.Y - 150, (int)found.Feet.X + 100, (int)found.Feet.Y + 12);
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        SweepMs += ms;
        SweepUnderMs += ms;
        if (best is { } b) SweepScores.Add(b.Score);
        return best;
    }

    /// <summary>Unmatched blobs at most this far apart (pixels, on either axis) whose union stays mob-sized are searched under as one mob's fragments.</summary>
    public const int FragmentGap = 24, FragmentMaxWidth = 100, FragmentMaxHeight = 110;

    /// <summary>Fragments joined into one mob by <see cref="SweepFragments"/> since construction (groups found, blobs taken in).</summary>
    public int FragmentGroups { get; private set; }
    public int FragmentsJoined { get; private set; }

    /// <summary>
    /// A mob drawn in colours close to the background's falls apart into fragments: the Dark Axe Stump on the ledge at world
    /// -400..-340, 1545 of fx-combat-exact's video frames 168 to 214, whose dark browns lie within the foreground threshold of the rock
    /// wall behind it and whose lighter wood fits the steam test, was three blobs of 101 to 243 pixels after the open (seq 193), none
    /// fitting a frame (the stump's is 66x77), the two at its feet too small to be searched under and the largest, its axe, 46 rows
    /// above the ledge, out of the search's reach. Unmatched blobs within <see cref="FragmentGap"/> of each other whose union is
    /// mob-sized and holds <see cref="SweepMinPixels"/> are searched under as one (out of the frame's sweeps); a find makes the largest
    /// of them the mob, its blob the union, and the others leave the frame.
    /// </summary>
    private void SweepFragments(List<Candidate> candidates, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY)
    {
        if (_sweepsThisFrame >= MaxSweepsPerFrame) return;
        var loose = candidates.Where(c => c.Found == FoundBy.Search && c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Damage is null
            && c.Tag is null && c.Pixels > 0 && !c.TouchesOwn).ToList();
        if (loose.Count < 2) return;
        // Single linkage, each group kept mob-sized.
        var root = Enumerable.Range(0, loose.Count).ToArray();
        var union = loose.Select(c => c.Box).ToArray();
        int Root(int i) { while (root[i] != i) i = root[i] = root[root[i]]; return i; }
        static int Apart(int a0, int a1, int b0, int b1) => Math.Max(0, Math.Max(b0 - a1, a0 - b1));
        for (var i = 0; i < loose.Count; i++)
            for (var j = i + 1; j < loose.Count; j++)
            {
                CaptureRect p = loose[i].Box, q = loose[j].Box;
                if (Math.Max(Apart(p.X, p.X + p.Width, q.X, q.X + q.Width), Apart(p.Y, p.Y + p.Height, q.Y, q.Y + q.Height)) > FragmentGap) continue;
                int a = Root(i), b = Root(j);
                if (a == b) continue;
                CaptureRect u = union[a], v = union[b];
                int x0 = Math.Min(u.X, v.X), y0 = Math.Min(u.Y, v.Y), x1 = Math.Max(u.X + u.Width, v.X + v.Width), y1 = Math.Max(u.Y + u.Height, v.Y + v.Height);
                if (x1 - x0 > FragmentMaxWidth || y1 - y0 > FragmentMaxHeight) continue;
                root[b] = a;
                union[a] = new CaptureRect(x0, y0, x1 - x0, y1 - y0);
            }
        var groups = Enumerable.Range(0, loose.Count).GroupBy(Root).Where(g => g.Count() >= 2).Select(g => (Box: union[g.Key], Parts: g.Select(i => loose[i]).ToList()))
            .Where(g => g.Parts.Sum(c => c.Pixels) >= SweepMinPixels).OrderByDescending(g => g.Parts.Sum(c => c.Pixels)).ToList();
        foreach (var (box, parts) in groups)
        {
            if (_sweepsThisFrame >= MaxSweepsPerFrame) break;
            var pixels = parts.Sum(c => c.Pixels);
            var probe = MakeCandidate(ctx, camX, camY, box, new PointD(box.X + (box.Width - 1) / 2.0, box.Y + box.Height), pixels, null, false, false, null, _ownWorld);
            if (!_mobs!.HasFlyers && probe.Foothold is null && ctx.Footholds.Below(probe.World, MaxAirUnits, FloorUnits) is null) continue;
            var (_, _, order, last) = PlanFor(probe, ctx);
            if (order.Count == 0) continue;
            _sweepsThisFrame++;
            var holder = parts.MaxBy(c => c.Pixels)!;
            var ts = Stopwatch.GetTimestamp();
            var swept = SweepUnder(probe, frame, frameWidth, ctx, camX, camY, order);
            holder.SearchMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
            if (swept is not { } m) continue;
            holder.Box = box;
            holder.Pixels = pixels;
            m = Contest(holder, m, order, last, m.Feet, (FootholdSearch.RefineX, FootholdSearch.RefineY));
            holder.Sprite = m;
            holder.Swept = true;
            Place(holder, m.Feet, ctx, camX, camY);
            holder.Floor = FloorFor(holder, ctx);
            foreach (var c in parts) if (!ReferenceEquals(c, holder)) candidates.Remove(c);
            SweepAttached++;
            FragmentGroups++;
            FragmentsJoined += parts.Count;
        }
    }

    /// <summary>The share of a box that a match's visible extent covers.</summary>
    private static double Covers(in SpriteMatch m, CaptureRect box)
    {
        int x0 = Math.Max(box.X, m.Left + m.Frame.X0), x1 = Math.Min(box.X + box.Width - 1, m.Left + m.Frame.X1);
        int y0 = Math.Max(box.Y, m.Top + m.Frame.Y0), y1 = Math.Min(box.Y + box.Height - 1, m.Top + m.Frame.Y1);
        return x1 < x0 || y1 < y0 ? 0 : (double)(x1 - x0 + 1) * (y1 - y0 + 1) / Math.Max(1, box.Width * box.Height);
    }

    /// <summary>
    /// The direct search over parallax background (<see cref="FootholdSearch.Gaps"/>): every visible stretch of a foothold
    /// whose band above the mosaic cannot vouch for is searched column by column for the species that can stand on its
    /// floor, skipping the columns of mobs already matched and of the own character. A find over an unmatched blob verifies
    /// that blob; elsewhere it is a new candidate with no blob (the differencing could not see it). The species contest
    /// settles recolours as for any match.
    /// </summary>
    /// <param name="covered">The boxes of mobs already found (the temporal pass's verified predictions): their columns are left out of the
    /// search on the footholds they stand on, not on footholds above or below them.</param>
    private void SearchGaps(List<Candidate> candidates, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, CaptureRect? own,
        IReadOnlyList<CaptureRect> covered)
    {
        if (_search is null) return;
        var t0 = Stopwatch.GetTimestamp();
        var vp = _mask.Viewport;
        var finds = new List<SpriteMatch>();
        foreach (var (fh, floor, sx0, sx1) in _search.Gaps(camX, camY, vp))
        {
            int lineTop = Math.Min(fh.Y1, fh.Y2) - camY, lineBottom = Math.Max(fh.Y1, fh.Y2) - camY;
            // Columns of the own character (mobs already matched are found again and dropped as the same mob).
            var skip = new List<(int, int)>();
            if (own is { } ob && ob.Y + ob.Height >= lineTop - FootholdSearch.BandRows && ob.Y <= lineBottom) skip.Add((ob.X, ob.X + ob.Width - 1));
            // A predicted mob's columns only on the foothold it stands on (its frame's bottom within a few rows of the line): before, the
            // columns were skipped at every height, which hid the stump on the slope at the top of keyframe 2161 behind a boar below it.
            foreach (var b in covered)
                if (b.Y + b.Height >= lineTop - CoveredRows && b.Y + b.Height <= lineBottom + CoveredRows) skip.Add((b.X, b.X + b.Width - 1));
            var species = _search.SpeciesFor(floor, (fh.Y1 + fh.Y2) / 2.0);
            if (species.Count == 0) continue;
            var hits = new List<(MobSpecies, SpriteFrame, int, int)>();
            _search.Probe(frame, frameWidth, _mask, fh, camX, camY, sx0, sx1, species, skip, hits);
            if (hits.Count == 0) continue;
            foreach (var m in _search.Score(_matcher, hits, FootholdSearch.Threshold, frame, frameWidth, _mask))
            {
                if (finds.Any(f => SameMob(f, m))) continue;
                if (candidates.Any(c => c.Sprite is { } cs && SameMob(cs, m))) continue;
                finds.Add(m);
                // An unmatched blob it covers becomes this mob; otherwise a new candidate without a blob.
                var holder = candidates.FirstOrDefault(c => c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Pixels > 0 && !_tracker.Names.IsCharacterTag(c.Tag?.Text) && Covers(m, c.Box) >= 0.5);
                var cand = holder ?? MakeCandidate(ctx, camX, camY, new CaptureRect(m.Left + m.Frame.X0, m.Top + m.Frame.Y0, m.Frame.ExtentWidth, m.Frame.ExtentHeight),
                    m.Feet, 0, null, false, false, null, _ownWorld);
                var hint = _tracker.Hint(cand, ctx.Time);
                var order = _mobs!.Order(floor, camY + m.Feet.Y, hint?.MobId, null);
                _matcher.PrepareRegion(frame, frameWidth, _mask, (int)m.Feet.X - 100, (int)m.Feet.Y - 150, (int)m.Feet.X + 100, (int)m.Feet.Y + 12);
                var settled = Contest(cand, m, order, hint?.LastSprite, m.Feet, (FootholdSearch.RefineX, FootholdSearch.RefineY));
                cand.Sprite = settled;
                cand.Swept = true;
                Place(cand, settled.Feet, ctx, camX, camY);
                cand.Floor = FloorFor(cand, ctx);
                if (holder is null) { candidates.Add(cand); SweepFinds++; } else SweepAttached++;
                SweepScores.Add(settled.Score);
            }
        }
        SweepMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// How far beyond the own character's fixed box (80x96 around its tag) a blob touching it may reach and still be part
    /// of the character: the hat, the staff or a sleeve sticking out (on fx-combat-exact's frame 751 the right side of the
    /// face and hat, 16x38 beyond the box's right edge, became a separate blob that took up the pet's track). A mob beside
    /// the character reaches further (the smallest, a snail, is 35 wide) or is verified by its sprite.
    /// </summary>
    public const int OwnReach = 24;

    /// <summary>
    /// Drops the blobs of the own character that stick out of its box (touching it, within <see cref="OwnReach"/>, verified
    /// as nothing, without a tag) and returns the box grown over them: the character's actual extent.
    /// </summary>
    private CaptureRect DropOwnSlivers(List<Candidate> candidates, CaptureRect own)
    {
        int x0 = own.X, y0 = own.Y, x1 = own.X + own.Width, y1 = own.Y + own.Height;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (!c.TouchesOwn || c.Sprite is not null || c.Drop is not null || c.Tag is not null || c.PetSprite is not null || c.Skill is not null || c.Pixels <= 0) continue;
            var b = c.Box;
            if (b.X < own.X - OwnReach || b.X + b.Width > own.X + own.Width + OwnReach || b.Y < own.Y - OwnReach || b.Y + b.Height > own.Y + own.Height + OwnReach) continue;
            candidates.RemoveAt(i);
            OwnSlivers++;
            x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y); x1 = Math.Max(x1, b.X + b.Width); y1 = Math.Max(y1, b.Y + b.Height);
        }
        return new CaptureRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Damage numbers rise from where a hit landed, over the mob hit or over the own character, so they are read in regions
    /// around the candidates nothing verified (effects, unknown blobs) and above the own character (<see cref="DamageReader"/>).
    /// A number read becomes a candidate of its own, and the blobs inside its box are taken into it.
    /// </summary>
    private void ReadDamage(List<Candidate> candidates, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, CaptureRect? own)
    {
        var t0 = Stopwatch.GetTimestamp();
        var regions = new List<CaptureRect>();
        foreach (var c in candidates)
            if (c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Pixels > 0 && c.Tag is null)
                regions.Add(new CaptureRect(c.Box.X - DamageMarginX, c.Box.Y - DamageMarginUp, c.Box.Width + 2 * DamageMarginX, c.Box.Height + DamageMarginUp + DamageMarginDown));
        if (own is { } ob) regions.Add(new CaptureRect(ob.X - 32, ob.Y - 96, ob.Width + 64, 120));
        // Overlapping regions are merged while the union stays small enough to read in one go.
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < regions.Count && !merged; i++)
                for (var j = i + 1; j < regions.Count && !merged; j++)
                {
                    CaptureRect a = regions[i], b = regions[j];
                    if (a.X > b.X + b.Width || b.X > a.X + a.Width || a.Y > b.Y + b.Height || b.Y > a.Y + a.Height) continue;
                    int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
                    if (x1 - x0 > DamageRegionMax || y1 - y0 > DamageRegionMax) continue;
                    regions[i] = new CaptureRect(x0, y0, x1 - x0, y1 - y0);
                    regions.RemoveAt(j);
                    merged = true;
                }
        }
        // Bounded: the regions nearest the own character first (its hits and the hits it takes are where numbers rise).
        if (own is { } oc)
            regions = regions.OrderBy(r => Math.Abs(r.X + r.Width / 2 - (oc.X + oc.Width / 2)) + Math.Abs(r.Y + r.Height / 2 - (oc.Y + oc.Height / 2))).ToList();
        if (regions.Count > MaxDamageRegions) regions.RemoveRange(MaxDamageRegions, regions.Count - MaxDamageRegions);
        var numbers = new List<DamageMatch>();
        foreach (var r in regions)
            foreach (var d in _damage!.Read(frame, frameWidth, _mask, r))
                if (!numbers.Any(n => n.Family == d.Family && n.Digits == d.Digits && Math.Abs(n.Box.X - d.Box.X) <= 2 && Math.Abs(n.Box.Y - d.Box.Y) <= 2)) numbers.Add(d);
        foreach (var d in numbers)
        {
            var box = d.Box;
            var pixels = 0;
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var c = candidates[i];
                if (c.Sprite is not null || c.Drop is not null || c.Tag is not null || c.Pixels <= 0 || c.Damage is not null || c.PetSprite is not null || c.Skill is not null) continue;
                double cx = c.Box.X + c.Box.Width / 2.0, cy = c.Box.Y + c.Box.Height / 2.0;
                if (cx < box.X - 4 || cx > box.X + box.Width + 4 || cy < box.Y - 4 || cy > box.Y + box.Height + 4) continue;
                pixels += c.Pixels;
                candidates.RemoveAt(i);
            }
            var feet = new PointD(box.X + box.Width / 2.0, box.Y + box.Height);
            var cand = MakeCandidate(ctx, camX, camY, box, feet, pixels, null, false, false, null, _ownWorld);
            cand.Damage = d;
            candidates.Add(cand);
            DamageNumbers++;
        }
        DamageMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>Damage reading regions around a candidate: this far to either side, up (numbers rise) and down.</summary>
    public const int DamageMarginX = 40, DamageMarginUp = 56, DamageMarginDown = 12, DamageRegionMax = 220;

    /// <summary>Damage regions read per frame at most (after merging): the cost stays bounded where effects swarm (Ant Tunnel IV's cave).</summary>
    public const int MaxDamageRegions = 8;

    /// <summary>
    /// An EXP gain equal to one species' client exp (every gain on fx-combat-exact but three was) credits that kill to a
    /// live track of the species (<see cref="EntityTracker.Kill"/>); the track's entity in this frame is dying.
    /// </summary>
    private void CreditKill(List<DetectedEntity> entities, DetectionContext ctx, PointD? ownWorld)
    {
        if (ctx.Exp is not { } exp) return;
        var previous = _lastExp;
        _lastExp = exp;
        if (previous is not { } prev || exp <= prev || _mobs is null) return;
        ExpGains++;
        var gain = exp - prev;
        if (_mobs.Entries.Where(e => e.Species.Exp == gain).ToList() is not [var species]) return;
        ExpGainsLabelled++;
        if (_tracker.Kill(species.Species.Id, ownWorld, ctx.Time) is not { } trackId) return;
        for (var i = 0; i < entities.Count; i++)
            if (entities[i].TrackId == trackId && entities[i].Identity is not null) entities[i] = entities[i] with { State = MobState.Dying };
    }

    /// <summary>
    /// Gives every mob with an identity its hitbox: the client's body rectangle (lt/rb) of the matched frame, or for a carried
    /// identity of the track's last frame, with its origin on the feet. Matched feet are the sprite's origin; a carried
    /// identity's feet come from its blob (the bottom of what shows), so a floor-bound species is put down on the surface
    /// below within the body's height (a stump half hidden by unreliable background showed only its top). Feet within
    /// <see cref="EntityTracker.OnFootholdUnits"/> of a surface are snapped onto it. A frame without lt/rb (the die frames)
    /// falls back to its visible extent.
    /// </summary>
    private void PlaceHitboxes(List<DetectedEntity> entities, DetectionContext ctx, int camX, int camY)
    {
        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.Identity is not { } id || e.Kind != EntityKind.Mob || _mobs!.ById(id.MobId)?.Species is not { } species) continue;
            var frameOf = id.Verified && e.Sprite is { } sm && sm.Species.Id == id.MobId ? sm.Frame
                : species.Frames.FirstOrDefault(f => f.Anim == id.Anim && f.Index == id.FrameIndex && f.Facing == id.Facing);
            if (frameOf is null) continue;
            var (l, t, r, b) = frameOf.Body;
            var feet = e.Feet;
            var world = new PointD(camX + feet.X, camY + feet.Y);
            FootholdHit? surface;
            if (!id.Verified && species.Mobility is MobMobility.Floor or MobMobility.Static)
                surface = ctx.Footholds.Below(world, b - t + 4, EntityTracker.OnFootholdUnits);
            else surface = ctx.Footholds.Nearest(world, EntityTracker.OnFootholdUnits);
            if (surface is { } h)
            {
                feet = new PointD(feet.X, h.SurfaceY - camY);
                world = new PointD(world.X, h.SurfaceY);
            }
            int fx = (int)Math.Round(feet.X), fy = (int)Math.Round(feet.Y);
            var hitbox = new CaptureRect(fx + l, fy + t, r - l, b - t);
            entities[i] = e with
            {
                Hitbox = hitbox, HitboxFromClient = frameOf.ClientBody is not null, Feet = feet, World = world,
                FootholdId = surface is { } s2 ? s2.Foothold.Id : e.FootholdId, FootholdDistance = surface is not null ? 0 : e.FootholdDistance,
            };
        }
    }

    /// <summary>
    /// The first frame to pass names a shape, not yet a species: recolours (Axe Stump and Dark Axe Stump, the snails)
    /// share every frame's geometry, and a Dark Stump's body is a Dark Axe Stump's without the axe. So once a frame
    /// passes, every other species of the plan is scored too (recolours at the same placement, the rest over the window,
    /// each needing at least the first score less <see cref="ContestSlack"/>). Recolours are settled among themselves
    /// on the pixels that tell them apart (<see cref="Settle"/>); the remaining species compete on the best
    /// <see cref="SpriteMatch.Score"/> times the share of the blob's foreground the frame covers (a Dark Stump frame
    /// leaves the Dark Axe Stump's axe uncovered), the species the track already has getting <see cref="RecolourMargin"/>
    /// and rarer species paying <see cref="SpawnPrior"/>.
    /// </summary>
    /// <param name="window">Search window for the other species (default the matcher's): a find of the foothold search knows its feet to a pixel.</param>
    private SpriteMatch Contest(Candidate c, SpriteMatch first, List<MapMobSet.Entry> order, SpriteMatch? last, PointD? feetOnly, (int X, int Y)? window = null)
    {
        var found = new List<SpriteMatch> { first };
        var recolours = _mobs!.Recolours(first.Species);
        foreach (var other in recolours)
            if (other.Frames.FirstOrDefault(f => f.Key == first.Frame.Key) is { } of && _matcher.At(other, of, first.Left, first.Top) is { } om)
                found.Add(om);
        var needed = Math.Max(SpriteMatcher.Threshold - ContestSlack, first.Score - ContestSlack);
        foreach (var e in order)
        {
            if (e.Species.Id == first.Species.Id || recolours.Contains(e.Species)) continue;
            SpriteMatch? best = null;
            foreach (var f in e.Species.Frames)
            {
                if (feetOnly is null && !SpriteMatcher.Fits(f, c.Box.Width, c.Box.Height)) continue;
                if (_matcher.Best(c.Box, feetOnly, e.Species, f, needed, window?.X ?? SpriteMatcher.SearchX, window?.Y ?? SpriteMatcher.SearchY) is { } x && x.Score >= needed && (best is null || x.Score > best.Value.Score)) best = x;
            }
            if (best is { } b) found.Add(b);
        }
        var maxSpawns = Math.Max(1, _mobs.Entries.Max(e => e.Spawns));
        double Value(SpriteMatch x) => x.Score * Coverage(c, x) + (last?.Species.Id == x.Species.Id ? RecolourMargin : 0)
            - SpawnPrior * Math.Log((double)maxSpawns / Math.Max(1, _mobs.ById(x.Species.Id)?.Spawns ?? 1));
        // One entry per recolour group, settled on its telling pixels; then the groups by value.
        var groups = new List<List<SpriteMatch>>();
        foreach (var m in found)
        {
            var g = groups.FirstOrDefault(g => g.Any(x => _mobs.Recolours(x.Species).Contains(m.Species)));
            if (g is null) groups.Add([m]); else g.Add(m);
        }
        var winner = SettleShapes(groups.Select(g => g.Count == 1 ? g[0] : Settle(g, last)).MaxBy(Value), found);
        var rival = found.Where(x => x.Species.Id != winner.Species.Id).Select(x => (SpriteMatch?)x).MaxBy(x => Value(x!.Value));
        return rival is { } r ? winner with { Rival = r.Species, RivalScore = r.Score } : winner;
    }

    /// <summary>
    /// Telling pixels a recolour needs to win: visible pixels where the two frames differ that only it explains, at
    /// least this many and at least <see cref="SettleRatio"/> times as many as the other. On fx-combat-exact the settled
    /// Axe Stump and Dark Axe Stump contests were decided by hundreds (0 against 1894 on a whole Axe Stump); the few
    /// close ones (714 against 429) were two stumps of either kind run into one blob.
    /// </summary>
    public const int MinTellingPixels = 8;
    public const int SettleRatio = 2;

    /// <summary>
    /// Settles a group of recolours (two or more species scored on one candidate whose frames share their geometry) on
    /// the pixels where their frames differ, at the placement of the best-scoring member (<see cref="SpriteMatcher.Distinguish"/>):
    /// pixels they share, such as the greys Axe Stump and Dark Axe Stump have in common, cannot tell them apart. When
    /// too few telling pixels are visible (steam, a tag, another sprite over the colours), the track's species is kept,
    /// else the one with more spawns.
    /// </summary>
    private SpriteMatch Settle(List<SpriteMatch> group, SpriteMatch? last)
    {
        var champion = group.MaxBy(m => m.Score);
        var basis = SpeciesBasis.Pixels;
        foreach (var m in group)
        {
            if (m.Species.Id == champion.Species.Id) continue;
            var frame = m.Species.Frames.FirstOrDefault(f => f.Key == champion.Frame.Key);
            var (onlyChampion, onlyOther, _) = frame is null ? (0, 0, 0) : _matcher.Distinguish(champion.Frame, frame, champion.Left, champion.Top, champion.Tint, MinTellingPixels);
            bool Wins(int a, int b) => a >= MinTellingPixels && a >= SettleRatio * b;
            if (Wins(onlyChampion, onlyOther)) continue;
            SpriteMatch? challenger = frame is null ? m : _matcher.At(m.Species, frame, champion.Left, champion.Top);
            if (Wins(onlyOther, onlyChampion)) { if (challenger is { } ch) champion = ch; continue; }
            // Undecided: the track's species, else the commoner one.
            var byTrack = last?.Species.Id == champion.Species.Id || last?.Species.Id == m.Species.Id;
            var keep = byTrack ? last?.Species.Id == champion.Species.Id
                : (_mobs!.ById(champion.Species.Id)?.Spawns ?? 0) >= (_mobs.ById(m.Species.Id)?.Spawns ?? 0);
            if (!keep && challenger is { } c2) champion = c2;
            basis = basis == SpeciesBasis.Spawns || !byTrack ? SpeciesBasis.Spawns : SpeciesBasis.Track;
        }
        return champion with { Basis = basis };
    }

    /// <summary>
    /// Weight of the spawn counts in the contest: a species with fewer spawns needs this much more score per factor e
    /// of spawns (an Axe Stump, 1 spawn, gives 0.06 to a Dark Axe Stump, 8): a Dark Axe Stump seen through steam or
    /// frozen loses most of the colour that tells it from an Axe Stump. Chosen, not tuned.
    /// </summary>
    public const double SpawnPrior = 0.03;

    /// <summary>Score slack of the contest: another species is scored only if it reaches the first match's score less this.</summary>
    public const double ContestSlack = 0.1;

    /// <summary>The share of the candidate box's foreground pixels that fall inside the frame's silhouette as placed (1 without a blob).</summary>
    private double Coverage(Candidate c, SpriteMatch m)
    {
        if (c.Pixels <= 0) return 1;
        var vp = _mask.Viewport;
        var mask = _fg.Mask;
        int w = _fg.Width, h = _fg.Height, total = 0, inside = 0;
        var f = m.Frame;
        for (var y = c.Box.Y; y < c.Box.Y + c.Box.Height; y++)
        {
            var vy = y - vp.Y;
            if ((uint)vy >= (uint)h) continue;
            var ty = y - m.Top;
            for (var x = c.Box.X; x < c.Box.X + c.Box.Width; x++)
            {
                var vx = x - vp.X;
                if ((uint)vx >= (uint)w || mask[vy * w + vx] == 0) continue;
                total++;
                var tx = x - m.Left;
                if ((uint)tx < (uint)f.Width && (uint)ty < (uint)f.Height && f.Bgra[(ty * f.Width + tx) * 4 + 3] >= SpriteFrame.VisibleAlpha) inside++;
            }
        }
        return total == 0 ? 1 : (double)inside / total;
    }

    /// <summary>
    /// A match proves its species is on screen when its species rests on its own pixels (<see cref="SpeciesBasis.Shape"/>
    /// or <see cref="SpeciesBasis.Pixels"/>) and at least this share of its visible pixels agree.
    /// </summary>
    public const double ProofScore = 0.5;

    /// <summary>
    /// How many mobs of a species one frame may show: its spawn count in the client's life list, or more once a single
    /// frame proved more (<see cref="ProofScore"/>). The server need not spawn what the client's data lists: on
    /// fx-combat-exact Deep Valley I showed two Axe Stumps (one spawn listed) on 22 keyframes, each settled on hundreds
    /// of telling pixels, both whole and exact on keyframe 49 (503 of 503 and 505 of 505 pixels; Dark Axe Stump 0.35 and
    /// 0.39 at the same placements), and three stumps at once on the walkway where the client lists one Axe Stump and
    /// one Dark Axe Stump.
    /// </summary>
    public int CapOf(int speciesId) => Math.Max(_mobs?.ById(speciesId)?.Spawns ?? 0, _seenAtOnce.GetValueOrDefault(speciesId).Count);

    /// <summary>The most mobs of a species one frame proved, and the first frame that did, per species id.</summary>
    public IReadOnlyDictionary<int, (int Count, long Frame)> SeenAtOnce => _seenAtOnce;
    private readonly Dictionary<int, (int Count, long Frame)> _seenAtOnce = [];

    private static bool Proves(SpriteMatch m) => m.Basis is SpeciesBasis.Shape or SpeciesBasis.Pixels && m.Score >= ProofScore;

    /// <summary>
    /// The cap on a species per frame (<see cref="CapOf"/>): matches beyond it lose their sprite (unproven ones first, then
    /// the weakest) and stay mobs only by what their track knew; the tracker holds carried identities to the same cap.
    /// </summary>
    private void CapBySpawns(List<Candidate> candidates, long frame)
    {
        foreach (var g in candidates.Where(c => c.Sprite is not null).GroupBy(c => c.Sprite!.Value.Species.Id))
        {
            var proven = g.Count(c => Proves(c.Sprite!.Value));
            if (proven > _seenAtOnce.GetValueOrDefault(g.Key).Count) _seenAtOnce[g.Key] = (proven, frame);
            foreach (var extra in g.OrderByDescending(c => Proves(c.Sprite!.Value)).ThenByDescending(c => c.Sprite!.Value.Score).Skip(CapOf(g.Key)))
            {
                extra.Sprite = null;
                extra.Found = FoundBy.Search;
                extra.PredictedTrack = null;
                SpawnCapped++;
            }
        }
    }

    /// <summary>Matches dropped by <see cref="CapBySpawns"/> since construction.</summary>
    public int SpawnCapped { get; private set; }

    /// <summary>Scores a candidate against every species of the map (best frame each), for the report.</summary>
    private void Diagnose(Candidate c, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx)
    {
        // The pet and the own skill effects are verified by their own frames; scored against the mobs they would only measure the
        // mob standing behind them (their box is their own pixels' extent, not a blob of foreground).
        if (c.Pixels <= 0 || c.PetSprite is not null || c.Skill is not null) return;
        var tagSpecies = (int?)null;
        string? tagName = null;
        if (c.Tag?.Text is { } text)
        {
            var (level, name) = TagDetector.Parse(_tracker.Names.Strip(text));
            if (level is not null && name is not null) tagName = _tracker.Names.MatchMob(name) ?? name;
            else if (name is not null) tagName = _tracker.Names.MatchMob(name);
            if (_mobs!.Named(tagName) is [var only]) tagSpecies = only.Species.Id;
        }
        var best = new Dictionary<int, SpriteMatch>();
        _matcher.Prepare(frame, frameWidth, _mask, c.Box, null);
        foreach (var e in _mobs!.Entries)
            foreach (var f in e.Species.Frames)
            {
                if (!SpriteMatcher.Fits(f, c.Box.Width, c.Box.Height)) continue;
                if (_matcher.Best(c.Box, null, e.Species, f, 0) is { } m && (!best.TryGetValue(e.Species.Id, out var b) || m.Score > b.Score))
                    best[e.Species.Id] = m;
            }
        Diagnostics.Add(new SpriteDiagnostic(ctx.Frame, c.Box, c.Tag is not null, tagSpecies, c.Sprite, best, c.Floor, c.BoxSurfaceDistance) { Source = c });
        if (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_DUMP") is { } dumpDir && best.Count > 0
            && (tagSpecies is not null || c.Sprite is not null || (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_DUMP_UNMATCHED") is not null && c.Pixels >= 600 && c.SurfaceDistance is { } usd && Math.Abs(usd) <= 6)
                || DumpFrames.Contains(ctx.Frame)))
            DumpMatch(dumpDir, frame, frameWidth, ctx.Frame, c, tagSpecies is { } ts && best.TryGetValue(ts, out var tm) ? tm : c.Sprite ?? best.Values.MaxBy(b => b.Score));
    }

    /// <summary>Debug: <c>OAKSOUL_SPRITE_DUMP_FRAMES=342,47</c> (with <c>OAKSOUL_SPRITE_DUMP</c>) dumps every candidate of those frames.</summary>
    private static readonly HashSet<long> DumpFrames = (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_DUMP_FRAMES") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();

    /// <summary>
    /// Debug (<c>OAKSOUL_SPRITE_DUMP=dir</c>, report runs only): the candidate's surroundings, the best frame placed over
    /// it, and their raw per-pixel difference (green where equal, as drawn or frozen; yellow where it fits through steam;
    /// red where not, the brighter the larger; blue the frame's soft edge), side by side at 3x, for every tagged or
    /// matched candidate (and, with <c>OAKSOUL_SPRITE_DUMP_UNMATCHED</c>, every large unmatched one on a foothold; with
    /// <c>OAKSOUL_SPRITE_DUMP_FRAMES</c>, every candidate of those frames).
    /// </summary>
    private static void DumpMatch(string dir, ReadOnlySpan<byte> frame, int frameWidth, long seq, Candidate c, SpriteMatch m)
    {
        Directory.CreateDirectory(dir);
        const int pad = 12, z = 3;
        int x0 = Math.Min(c.Box.X, m.Left) - pad, y0 = Math.Min(c.Box.Y, m.Top) - pad;
        int x1 = Math.Max(c.Box.X + c.Box.Width, m.Left + m.Frame.Width) + pad, y1 = Math.Max(c.Box.Y + c.Box.Height, m.Top + m.Frame.Height) + pad;
        int w = x1 - x0, h = y1 - y0;
        var canvas = new Canvas(3 * w * z + 8, h * z + 12);
        var fh = frame.Length / 4 / frameWidth;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int sx = x0 + x, sy = y0 + y;
                if ((uint)sx >= (uint)frameWidth || (uint)sy >= (uint)fh) continue;
                var o = (sy * frameWidth + sx) * 4;
                uint fpx = 0xFF000000u | (uint)(frame[o + 2] << 16 | frame[o + 1] << 8 | frame[o]);
                var tx = sx - m.Left; var ty = sy - m.Top;
                uint tpx = 0xFF000000 | 0x404040; uint dpx = fpx;
                if ((uint)tx < (uint)m.Frame.Width && (uint)ty < (uint)m.Frame.Height)
                {
                    var to = (ty * m.Frame.Width + tx) * 4;
                    var a = m.Frame.Bgra[to + 3];
                    if (a > 0)
                    {
                        tpx = 0xFF000000u | (uint)(m.Frame.Bgra[to + 2] << 16 | m.Frame.Bgra[to + 1] << 8 | m.Frame.Bgra[to]);
                        var d = Math.Abs(frame[o] - m.Frame.Bgra[to]) + Math.Abs(frame[o + 1] - m.Frame.Bgra[to + 1]) + Math.Abs(frame[o + 2] - m.Frame.Bgra[to + 2]);
                        int tb = m.Frame.Bgra[to], tg = m.Frame.Bgra[to + 1], tr = m.Frame.Bgra[to + 2];
                        if (m.Tint == SpriteTint.Frozen) { tb = (tb * SpriteMatcher.FrozenB + 127) / 255; tg = (tg * SpriteMatcher.FrozenG + 127) / 255; tr = (tr * SpriteMatcher.FrozenR + 127) / 255; }
                        var steam = SpriteMatcher.Fog(frame[o], frame[o + 1], frame[o + 2], tb, tg, tr) > 0;
                        dpx = a < SpriteFrame.SolidAlpha ? 0xFF0000FFu : frame[o] == tb && frame[o + 1] == tg && frame[o + 2] == tr ? 0xFF00C000u
                            : steam ? 0xFFE0E000u : 0xFF000000u | (uint)Math.Min(255, d) << 16;
                    }
                }
                for (var yy = 0; yy < z; yy++)
                    for (var xx = 0; xx < z; xx++)
                    {
                        canvas.Set(x * z + xx, 12 + y * z + yy, fpx);
                        canvas.Set(w * z + 4 + x * z + xx, 12 + y * z + yy, tpx);
                        canvas.Set(2 * w * z + 8 + x * z + xx, 12 + y * z + yy, dpx);
                    }
            }
        canvas.Text(1, 1, $"{seq} {m} floor {c.Floor} world {c.World} fh {c.Foothold?.Foothold.Id}", 0xFFFFFFFF);
        canvas.Save(Path.Combine(dir, $"{seq:0000}_{c.Box.X}_{c.Box.Y}_{c.Box.Width}x{c.Box.Height}_{(c.Sprite is null ? "U" : "M")}{m.Score:0.00}_{m.Species.Name.Replace(' ', '-')}.png"));
    }

    /// <summary>Registers the current frame's gathered samples (<see cref="_gathered"/>) against the mosaic around (gx, gy).</summary>
    private Registration Fine(int gx, int gy, int rx, int ry) =>
        CameraRegistration.Register(_gathered, _mosaic.Pixels, _mosaic.Usable, _mosaic.OriginX, _mosaic.OriginY,
            _mosaic.Width, _mosaic.Height, gx, gy, rx, ry);

    /// <summary>
    /// The pet's tag (or the own) is drawn under the pet, so it never labels a mob a sprite verified or a drop an icon
    /// identified, though the geometric association gives it to whatever blob stands closest above it: on fx-combat-exact
    /// a mob beside the pet on 5 detections and a meso coin beside it on 8. Such a tag is taken off and given to another
    /// blob standing on it that nothing verified (that blob is the pet), or else stands alone as the pet; the mob or drop
    /// keeps its own feet (the sprite's origin, or its box). The tracker does the rest: a candidate under the pet's tag
    /// is the pet and never continues a mob's track (44 more detections had taken a mob's identity that way).
    /// </summary>
    private void MoveCharacterTags(List<Candidate> candidates, DetectionContext ctx, int camX, int camY, PointD? ownWorld)
    {
        var vp = _mask.Viewport;
        var moved = new List<(Candidate From, TagBox Tag)>();
        foreach (var c in candidates)
            if (c.Tag is { } t && (c.Sprite is not null || c.Drop is not null) && _tracker.Names.IsCharacterTag(t.Text)) moved.Add((c, t));
        foreach (var (from, t) in moved)
        {
            from.Tag = null;
            if (from.Sprite is null) Place(from, new PointD(from.Box.X + (from.Box.Width - 1) / 2.0, from.Box.Y + from.Box.Height), ctx, camX, camY);
            Candidate? holder = null;
            var bestCost = double.MaxValue;
            foreach (var o in candidates)
            {
                if (o.Tag is not null || o.Pixels == 0 || o.Sprite is not null || o.Drop is not null) continue;
                var gapRows = t.Y0 + vp.Y - (o.Box.Y + o.Box.Height - 1);
                var cx = o.Box.X + (o.Box.Width - 1) / 2.0 - vp.X;
                if (gapRows < -4 || gapRows > TagAboveRows || Math.Abs(t.CentreX - cx) > t.Width / 2.0 + TagSlackX) continue;
                var cost = Math.Abs(t.CentreX - cx) + Math.Abs(gapRows - 2);
                if (cost < bestCost) { bestCost = cost; holder = o; }
            }
            var feet = new PointD(t.CentreX + vp.X, t.Y0 - 2 + vp.Y);
            if (holder is not null) { holder.Tag = t; Place(holder, feet, ctx, camX, camY); }
            else candidates.Add(MakeCandidate(ctx, camX, camY, new CaptureRect((int)feet.X - 20, (int)feet.Y - 45, 40, 45), feet, 0, t, false, false, null, ownWorld));
            CharacterTagsMoved++;
        }
    }

    /// <summary>Character tags taken off a verified mob or an identified drop since construction (see <see cref="MoveCharacterTags"/>).</summary>
    public int CharacterTagsMoved { get; private set; }

    /// <summary>Moves a candidate's feet to a screen point and updates its world position and foothold.</summary>
    private static void Place(Candidate c, PointD feet, DetectionContext ctx, int camX, int camY)
    {
        c.Feet = feet;
        c.World = new PointD(camX + feet.X, camY + feet.Y);
        var nearest = ctx.Footholds.Nearest(c.World, 40);
        c.SurfaceDistance = nearest?.Distance;
        c.Foothold = nearest is { } n && Math.Abs(n.Distance) <= EntityTracker.OnFootholdUnits ? nearest : null;
    }

    private static Candidate MakeCandidate(DetectionContext ctx, int camX, int camY, CaptureRect box, PointD feet, int pixels, TagBox? tag,
        bool hpBar, bool touchesOwn, byte[]? thumb, PointD? ownWorld)
    {
        var world = new PointD(camX + feet.X, camY + feet.Y);
        var nearest = ctx.Footholds.Nearest(world, 40);
        var on = nearest is { } n && Math.Abs(n.Distance) <= EntityTracker.OnFootholdUnits ? nearest : null;
        return new Candidate
        {
            Box = box, Feet = feet, World = world, Foothold = on, SurfaceDistance = nearest?.Distance, Pixels = pixels, Tag = tag,
            HpBar = hpBar, TouchesOwn = touchesOwn, Thumb = thumb, OwnDistance = ownWorld is { } o ? o.DistanceTo(world) : null,
        };
    }

    /// <summary>Merges blobs that nearly touch while the union stays sprite-sized.</summary>
    private static List<Blob> Merge(List<Blob> blobs)
    {
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < blobs.Count && !merged; i++)
                for (var j = i + 1; j < blobs.Count && !merged; j++)
                {
                    Blob a = blobs[i], b = blobs[j];
                    if (a.X0 > b.X1 + MergeGap || b.X0 > a.X1 + MergeGap || a.Y0 > b.Y1 + MergeGap || b.Y0 > a.Y1 + MergeGap) continue;
                    int x0 = Math.Min(a.X0, b.X0), y0 = Math.Min(a.Y0, b.Y0), x1 = Math.Max(a.X1, b.X1), y1 = Math.Max(a.Y1, b.Y1);
                    if (x1 - x0 + 1 > MergeMaxWidth || y1 - y0 + 1 > MergeMaxHeight) continue;
                    blobs[i] = new Blob(x0, y0, x1, y1, a.Pixels + b.Pixels, a.SumX + b.SumX, a.SumY + b.SumY, a.Solid + b.Solid);
                    blobs.RemoveAt(j);
                    merged = true;
                }
        }
        return blobs;
    }

    /// <summary>8x8 mean luma of the blob's foreground pixels (background cells 0), for the appearance check.</summary>
    private byte[] Thumb(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect vp, Blob b)
    {
        var sum = new int[64];
        var n = new int[64];
        var mask = _fg.Mask;
        int w = _fg.Width;
        for (var y = b.Y0; y <= b.Y1; y++)
        {
            var cy = (y - b.Y0) * 8 / b.Height;
            for (var x = b.X0; x <= b.X1; x++)
            {
                if (mask[y * w + x] == 0) continue;
                var cx = (x - b.X0) * 8 / b.Width;
                var o = ((y + vp.Y) * frameWidth + x + vp.X) * 4;
                sum[cy * 8 + cx] += (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2;
                n[cy * 8 + cx]++;
            }
        }
        var thumb = new byte[64];
        for (var i = 0; i < 64; i++) thumb[i] = (byte)(n[i] == 0 ? 0 : sum[i] / n[i]);
        return thumb;
    }

    public static uint KindColour(EntityKind kind) => kind switch
    {
        EntityKind.Mob => 0xFFFF3030,
        EntityKind.Player => 0xFF30A0FF,
        EntityKind.Pet => 0xFF30FFFF,
        EntityKind.Drop => 0xFFFFE030,
        EntityKind.Effect => 0xFFC060FF,
        EntityKind.Damage => 0xFFFF9020,
        _ => 0xFFB0B0B0,
    };

    /// <summary>
    /// Draws the detections of the last frame onto a copy of it: boxes by kind, tag boxes (cyan), the own box (white),
    /// optionally the foreground classes; at half size when <paramref name="half"/>, with the labels drawn after
    /// scaling so they stay legible.
    /// </summary>
    public Canvas Annotate(ReadOnlySpan<byte> frame, int frameWidth, EntityFrame result, bool foreground, bool half = false)
    {
        var canvas = new Canvas(frame, frameWidth, frame.Length / 4 / frameWidth);
        var vp = _mask.Viewport;
        if (foreground && result.Camera is not null)
        {
            int w = _mask.Width, h = _mask.Height;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var c = (PixelClass)_fg.Classes[y * w + x];
                    if (_mask.Bits[y * w + x] != 0) canvas.Tint(x + vp.X, y + vp.Y, 0xFF0000FF, 0.3);
                    else if (c == PixelClass.Ignored) canvas.Tint(x + vp.X, y + vp.Y, 0xFF808080, 0.45);
                    else if (DebugDir is not null && c == PixelClass.Shade) canvas.Tint(x + vp.X, y + vp.Y, 0xFF00FFFF, 0.6);
                    else if (DebugDir is not null && c == PixelClass.Bright) canvas.Tint(x + vp.X, y + vp.Y, 0xFFFFFF00, 0.6);
                    else if (_fg.Mask[y * w + x] != 0) canvas.Tint(x + vp.X, y + vp.Y, 0xFFFF0000, 0.4);
                    else if (c == PixelClass.Fog) canvas.Tint(x + vp.X, y + vp.Y, 0xFF00FF00, 0.25);
                }
        }
        var t = half ? 3 : 2;
        if (LastOwnBox is { } own) canvas.Rect(own.X, own.Y, own.Width, own.Height, 0xFFFFFFFF, half ? 2 : 1);
        foreach (var tag in LastTags) canvas.Rect(tag.X0 + vp.X, tag.Y0 + vp.Y, tag.Width, tag.Height, 0xFF00FFFF, half ? 2 : 1);
        foreach (var e in result.Entities) canvas.Rect(e.DisplayBox.X, e.DisplayBox.Y, e.DisplayBox.Width, e.DisplayBox.Height, KindColour(e.Kind), t);
        if (half) canvas = canvas.Half();
        var s = half ? 2 : 1;
        foreach (var e in result.Entities)
        {
            var label = Label(e);
            canvas.Text(e.DisplayBox.X / s, Math.Max(0, e.DisplayBox.Y / s - 9), label, KindColour(e.Kind));
        }
        return canvas;
    }

    /// <summary>
    /// A box's label: mobs by species, animation and frame and facing ("#12 Red Snail move/2R"; "~" when the identity is
    /// carried by the track rather than matched in this frame; "?" for a mob no sprite verified), drops by item, the
    /// rest by kind and name.
    /// </summary>
    public static string Label(DetectedEntity e)
    {
        if (e.Kind == EntityKind.Mob && e.Identity is { } id) return $"#{e.TrackId} {id.Name} {id.Anim}/{id.FrameIndex}{(id.Facing > 0 ? "R" : "L")}{(id.Verified ? "" : "~")}";
        if (e.Kind == EntityKind.Drop && e.Drop is { } d) return $"#{e.TrackId} {(d.IsMeso ? "meso" : "item")} {d.ItemId}";
        if (e.Kind == EntityKind.Damage && e.Damage is { } dm) return $"#{e.TrackId} dmg {(dm.Complete ? "" : "?")}{dm.Digits} {dm.DirectionText}";
        var name = e.Name is null ? "" : " " + (e.Level is { } lv ? lv > 0 ? $"Lv{lv} " : "Lv? " : "") + e.Name;
        return $"#{e.TrackId} {e.Kind}{(e.Kind == EntityKind.Mob ? "?" : "")}{name}";
    }

    private void Dump(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, List<DetectedEntity> entities, int camX, int camY)
    {
        Directory.CreateDirectory(DebugDir!);
        var r = new EntityFrame(ctx.Frame, ctx.Time, entities, (camX, camY), null, 0, 0);
        Annotate(frame, frameWidth, r, foreground: true).Save(Path.Combine(DebugDir!, $"{ctx.Frame:0000}.png"));
        BlobView(frame, frameWidth).Save(Path.Combine(DebugDir!, $"{ctx.Frame:0000}-blobs.png"));
    }
}
