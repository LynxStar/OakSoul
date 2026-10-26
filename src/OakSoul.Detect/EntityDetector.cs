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
/// <param name="Exact">The source delivers the game's own pixels (<c>OcrInput.IsPixelExact</c>), not the point-size
/// capture's blurred copy: tag crops are binarized for OCR (see <see cref="TagDetector.Read"/>), and unless <paramref name="Lossy"/>
/// sprites and drop icons are compared pixel for pixel (see <see cref="SpriteMatcher"/>).</param>
/// <param name="Lossy">The frame was decoded from a lossy video (a recording replayed without --keyframes): sprites are then
/// compared by 3x3 means with a tolerance even when the recording is exact.</param>
public sealed record DetectionContext(long Frame, double Time, MapData Map, FootholdIndex Footholds, PointD? Camera, bool CameraMeasured,
    CaptureRect? OwnTag, PointD? OwnWorld, CaptureRect? MinimapPanel, bool Exact = false, bool Lossy = false)
{
    /// <summary>Sprites are compared pixel for pixel: an exact frame not decoded from a lossy video.</summary>
    public bool Strict => Exact && !Lossy;
}

/// <summary>
/// Detects and tracks entities in native frames of one map against its cached <see cref="BackgroundMosaic"/>:
/// refines the camera by registration, extracts foreground, finds name tags (OCR'd once per distinct tag), turns
/// blobs and tags into candidates with a world position and foothold, then classifies and tracks them.
/// </summary>
public sealed class EntityDetector
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

    /// <summary>
    /// HP bar: a grey frame around a green and dark red fill, drawn above a mob that was hit, about 50x9 on the Dark
    /// Axe Stumps and the Fire Boar. It is often merged with the sprite or the background behind it is unreliable, so
    /// on fx-combat it was found over only 16% of the tagged mobs; it is a bonus signal, not a classifier.
    /// </summary>
    public const int HpBarMinWidth = 36, HpBarMaxWidth = 70, HpBarMinHeight = 5, HpBarMaxHeight = 13, HpBarGreenPixels = 4;

    /// <summary>Names of the timed stages, in <see cref="StageMs"/> order.</summary>
    public static readonly string[] StageNames = ["registration", "difference", "tags", "components", "sprites", "drops", "tracking"];

    /// <summary>Cumulative milliseconds per stage (see <see cref="StageNames"/>; OCR excluded) and frames timed.</summary>
    public double[] StageMs { get; } = new double[StageNames.Length];

    /// <summary>Per-frame milliseconds of the sprite stage, for its median.</summary>
    public List<double> SpriteStageMs { get; } = [];
    public int StageFrames { get; private set; }

    /// <summary>Frames that needed the wide search.</summary>
    public int WideSearches { get; private set; }

    private CoarseMosaic? _coarse;
    private (int X, int Y, double Time)? _lastCamera;

    private readonly BackgroundMosaic _mosaic;
    private readonly ScreenMask _mask;
    private readonly ForegroundExtractor _fg = new();
    private readonly TagDetector _tags = new();
    private readonly EntityTracker _tracker;
    private readonly MapMobSet? _mobs;
    private readonly DropMatcher? _drops;
    private readonly SpriteMatcher _matcher = new();
    private PointD? _ownWorld;

    /// <param name="tracker">A tracker shared across maps (it is flushed on a map change by the caller), or null for a new one.</param>
    /// <param name="mobs">The map's mob templates; without them candidates are not verified against sprites.</param>
    /// <param name="drops">Drop icons; without them drops are classified by appearance only.</param>
    public EntityDetector(BackgroundMosaic mosaic, HudLayout layout, EntityTracker? tracker = null, MapMobSet? mobs = null, DropMatcher? drops = null)
    {
        _mosaic = mosaic;
        _mask = new ScreenMask(layout);
        _tracker = tracker ?? new EntityTracker();
        _tracker.MapId = mosaic.MapId;
        _mobs = mobs;
        _drops = drops;
    }

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
        LastTags = [];
        LastOwnBox = null;
        if (ctx.Camera is not { } guess) return EntityFrame.Empty(ctx.Frame, ctx.Time, "no camera");
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

        var samples = CameraRegistration.Samples(frame, frameWidth, _mask, RegistrationSamples);
        int guessX = (int)Math.Round(guess.X), guessY = (int)Math.Round(guess.Y);
        // A measured camera is searched near the guess first; a predicted one, or a failed first try, goes through the
        // coarse search of block means over a wide window and is then refined to the pixel.
        Registration? reg = ctx.CameraMeasured ? Fine(frame, frameWidth, samples, guessX, guessY, MeasuredRangeX, MeasuredRangeY) : null;
        if (reg is not { Score: >= AcceptScore })
        {
            _coarse ??= new CoarseMosaic(_mosaic);
            if (_coarse.Search(frame, frameWidth, _mask, guessX, guessY, CoarseRange) is { } c)
            {
                var wide = Fine(frame, frameWidth, samples, c.X, c.Y, CoarseRefine, CoarseRefine);
                if (reg is null || wide.Score > reg.Value.Score) reg = wide with { GuessMatches = reg?.GuessMatches ?? wide.GuessMatches, GuessCompared = reg?.GuessCompared ?? wide.GuessCompared };
            }
            // Still nothing: the tracker's camera can be far off while the tag is hidden and the camera eases; the
            // last registered camera is usually closer.
            if (reg is not { Score: >= MinRegistrationScore } && _lastCamera is { } last && ctx.Time - last.Time <= 2
                && _coarse.Search(frame, frameWidth, _mask, last.X, last.Y, CoarseRange) is { } c2)
            {
                var wide = Fine(frame, frameWidth, samples, c2.X, c2.Y, CoarseRefine, CoarseRefine);
                if (reg is null || wide.Score > reg.Value.Score) reg = wide;
            }
            reg ??= Fine(frame, frameWidth, samples, guessX, guessY, 0, 0);
            WideSearches++;
        }
        if (reg.Value.Score < MinRegistrationScore)
        {
            _tracker.Update([], ctx.Frame, ctx.Time);
            return new EntityFrame(ctx.Frame, ctx.Time, [], null, reg, sw.Elapsed.TotalMilliseconds, 0, $"registration failed ({reg.Value.Score:0.00})");
        }
        int camX = reg.Value.CameraX, camY = reg.Value.CameraY;
        _lastCamera = (camX, camY, ctx.Time);
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
        _matcher.Exact = ctx.Strict;
        _matcher.Background = (_mosaic, camX, camY);
        if (_drops is not null) _drops.Background = (_mosaic, camX, camY);
        _fg.Difference(frame, frameWidth, _mask, _mosaic, camX, camY);
        var tDiff = sw.Elapsed.TotalMilliseconds;
        var vp = _mask.Viewport;
        var tags = _tags.Find(_fg, frame, frameWidth, vp);
        foreach (var t in tags) _fg.Clear(t.X0 - 1, t.Y0 - 1, t.X1 + 1, t.Y1 + 1);
        var tTags = sw.Elapsed.TotalMilliseconds;
        // Blobs made mostly of darkened or brightened background (a parallax layer or the waterfall seen against the
        // screen-space model, the edge of a steam cloud) are not sprites.
        var blobs = Merge(_fg.Components().Where(b => b.SolidFraction >= MinSolidFraction).ToList());
        var tComp = sw.Elapsed.TotalMilliseconds;

        // HP bars become a flag on the blob under them.
        var bars = new List<Blob>();
        foreach (var b in blobs) if (IsHpBar(b, frame, frameWidth, vp)) bars.Add(b);
        blobs.RemoveAll(bars.Contains);

        var ocrBefore = _tags.OcrMilliseconds;
        _tags.Read(tags, frame, frameWidth, vp, Ocr, exact: ctx.Exact);
        var ocrMs = _tags.OcrMilliseconds - ocrBefore;

        // With the camera registered to the pixel, the own tag gives the own position exactly (the dot only to +-8).
        var ownWorld = ctx.OwnTag is { } ownTag ? CameraModel.ScreenToWorld(new PointD(camX, camY), CameraModel.SolesFromTag(OwnCharacter.TagFeet(ownTag))) : ctx.OwnWorld;
        _ownWorld = ownWorld;
        var candidates = new List<Candidate>();
        var tagUsed = new bool[tags.Count];
        var tagOwner = new Blob?[tags.Count];
        foreach (var b in blobs)
        {
            var touchesOwn = own is { } ob && b.X0 + vp.X <= ob.X + ob.Width + 2 && b.X1 + vp.X >= ob.X - 2 && b.Y0 + vp.Y <= ob.Y + ob.Height + 2 && b.Y1 + vp.Y >= ob.Y - 2;
            if (touchesOwn && b.Pixels < OwnFragmentPixels) continue;
            // The tag under the blob, if any.
            var cx = (b.X0 + b.X1) / 2.0;
            var ti = -1;
            var bestCost = double.MaxValue;
            for (var i = 0; i < tags.Count; i++)
            {
                var t = tags[i];
                var gapRows = t.Y0 - b.Y1;
                if (gapRows < -4 || gapRows > TagAboveRows || Math.Abs(t.CentreX - cx) > t.Width / 2.0 + TagSlackX) continue;
                // A blob much wider than the tag is two sprites run together (the pet against a boar); the tag then
                // stands on its own rather than naming both.
                if (b.Width > Math.Max(t.Width * 1.6, t.Width + 40)) continue;
                var cost = Math.Abs(t.CentreX - cx) + Math.Abs(gapRows - 2);
                if (cost < bestCost) { bestCost = cost; ti = i; }
            }
            // The tag goes to the biggest blob standing on it, not to a stray fragment that happens to be closer.
            if (ti >= 0 && tagOwner[ti] is { } prev && prev.Pixels >= b.Pixels) ti = -1;
            if (ti >= 0) tagOwner[ti] = b;
        }
        foreach (var b in blobs)
        {
            var touchesOwn = own is { } ob && b.X0 + vp.X <= ob.X + ob.Width + 2 && b.X1 + vp.X >= ob.X - 2 && b.Y0 + vp.Y <= ob.Y + ob.Height + 2 && b.Y1 + vp.Y >= ob.Y - 2;
            if (touchesOwn && b.Pixels < OwnFragmentPixels) continue;
            var cx = (b.X0 + b.X1) / 2.0;
            var ti = Array.FindIndex(tagOwner, o => ReferenceEquals(o, b));
            TagBox? tb = null;
            if (ti >= 0 && !tagUsed[ti]) { tagUsed[ti] = true; tb = tags[ti]; }
            // With a tag the feet are exact: centred on the tag, a couple of rows above its box.
            var feet = tb is not null ? new PointD(tb.CentreX + vp.X, tb.Y0 - 2 + vp.Y) : new PointD(cx + vp.X, b.Y1 + 1 + vp.Y);
            var bar = bars.FirstOrDefault(r => r.Y1 <= b.Y0 + 4 && r.Y1 >= b.Y0 - 40 && Math.Abs((r.X0 + r.X1) / 2.0 - cx) <= 30);
            if (bar is not null) bars.Remove(bar);
            candidates.Add(MakeCandidate(ctx, camX, camY, new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height), feet, b.Pixels, tb,
                bar is not null, touchesOwn, Thumb(frame, frameWidth, vp, b), ownWorld));
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

        var tCand = sw.Elapsed.TotalMilliseconds - ocrMs;
        if (_mobs is not null)
            for (var i = 0; i < candidates.Count; i++)
                if (Identify(candidates[i], frame, frameWidth, ctx, camX, camY) is { } rest) candidates.Add(rest);
        if (_mobs is not null) CapBySpawns(candidates);
        var tSprites = sw.Elapsed.TotalMilliseconds - ocrMs;
        if (_drops is not null)
            foreach (var c in candidates)
                if (c.Sprite is null && c.Pixels > 0 && !c.TouchesOwn && c.SurfaceDistance is { } sd && Math.Abs(sd) <= DropSurfaceUnits)
                    c.Drop = _drops.Match(frame, frameWidth, _mask, c.Box, ctx.Strict);
        var tDrops = sw.Elapsed.TotalMilliseconds - ocrMs;
        // Diagnostics are timed apart and left out of the frame's cost.
        var diagMs = 0.0;
        if (SpriteDiagnostics && _mobs is not null)
        {
            var d0 = sw.Elapsed.TotalMilliseconds;
            foreach (var c in candidates) Diagnose(c, frame, frameWidth, ctx);
            if (_drops is not null)
                foreach (var c in candidates)
                    if (c.Sprite is null && c.Pixels > 0 && !c.TouchesOwn && c.SurfaceDistance is { } sd2 && Math.Abs(sd2) <= DropSurfaceUnits)
                        _drops.Diagnose(ctx.Frame, frame, frameWidth, _mask, c.Box, ctx.Strict);
            diagMs = sw.Elapsed.TotalMilliseconds - d0;
            DiagnosticMs += diagMs;
        }

        var entities = _tracker.Update(candidates, ctx.Frame, ctx.Time);
        if (SpriteDiagnostics)
            for (var i = Diagnostics.Count - 1; i >= 0 && Diagnostics[i].Frame == ctx.Frame; i--)
                if (Diagnostics[i].Source is { } src && candidates.IndexOf(src) is var k and >= 0) Diagnostics[i].Kind = entities[k].Kind;
        LastTags = tags;
        var ms = sw.Elapsed.TotalMilliseconds - ocrMs - diagMs;
        StageMs[0] += tReg; StageMs[1] += tDiff - tReg; StageMs[2] += tTags - tDiff; StageMs[3] += tComp - tTags;
        StageMs[4] += tSprites - tCand; StageMs[5] += tDrops - tSprites; StageMs[6] += ms - tDrops + (tCand - tComp);
        SpriteStageMs.Add(tSprites - tCand);
        StageFrames++;
        if (DebugDir is not null) Dump(frame, frameWidth, ctx, entities, camX, camY);
        return new EntityFrame(ctx.Frame, ctx.Time, entities, (camX, camY), reg.Value, ms, ocrMs);
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
        tagName ??= hint?.TagName;
        var named = _mobs!.Named(tagName);
        return (floor, named.Count, _mobs.Order(floor, c.World.Y, hint?.MobId, named), hint?.LastSprite);
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
        if (match is not { } m) return rest;
        m = Contest(c, m, order, last, feetOnly);
        c.Sprite = m;
        var feet = m.Feet;
        c.Feet = feet;
        c.World = new PointD(camX + feet.X, camY + feet.Y);
        var nearest = ctx.Footholds.Nearest(c.World, 40);
        c.SurfaceDistance = nearest?.Distance;
        c.Foothold = nearest is { } n && Math.Abs(n.Distance) <= EntityTracker.OnFootholdUnits ? nearest : null;
        return rest;
    }

    /// <summary>
    /// The first frame to pass names a shape, not yet a species: recolours (Stump and Dark Stump) share every frame's
    /// shape, and a Dark Stump passes as a Dark Axe Stump whose thin axe has few interior samples. So once a frame
    /// passes, every other species of the plan is scored too (recolours at the same placement, the rest over the
    /// window, each needing at least the first score less <see cref="ContestSlack"/>), and the winner is the best
    /// <see cref="SpriteMatch.Score"/> times the share of the blob's foreground the frame covers (a Dark Stump frame
    /// leaves the Dark Axe Stump's axe uncovered). The species the track already has gets <see cref="RecolourMargin"/>,
    /// and rarer species pay <see cref="SpawnPrior"/>.
    /// </summary>
    private SpriteMatch Contest(Candidate c, SpriteMatch first, List<MapMobSet.Entry> order, SpriteMatch? last, PointD? feetOnly)
    {
        var entries = new List<(SpriteMatch M, double Score)>();
        var maxSpawns = Math.Max(1, _mobs!.Entries.Max(e => e.Spawns));
        double Value(SpriteMatch x) => x.Score * Coverage(c, x) + (last?.Species.Id == x.Species.Id ? RecolourMargin : 0)
            - SpawnPrior * Math.Log((double)maxSpawns / Math.Max(1, _mobs.ById(x.Species.Id)?.Spawns ?? 1));
        entries.Add((first, Value(first)));
        var recolours = _mobs!.Recolours(first.Species);
        foreach (var other in recolours)
            if (other.Frames.FirstOrDefault(f => f.Key == first.Frame.Key) is { } of && _matcher.At(other, of, first.Left, first.Top) is { } om)
                entries.Add((om, Value(om)));
        var needed = Math.Max(_matcher.Threshold - ContestSlack, first.Score - ContestSlack);
        foreach (var e in order)
        {
            if (e.Species.Id == first.Species.Id || recolours.Contains(e.Species)) continue;
            SpriteMatch? best = null;
            foreach (var f in e.Species.Frames)
            {
                if (feetOnly is null && !_matcher.Fits(f, c.Box.Width, c.Box.Height)) continue;
                if (_matcher.Best(c.Box, feetOnly, e.Species, f, needed) is { } x && x.Score >= needed && (best is null || x.Score > best.Value.Score)) best = x;
            }
            if (best is { } b) entries.Add((b, Value(b)));
        }
        entries.Sort((a, b) => b.Score.CompareTo(a.Score));
        var winner = entries[0].M;
        return entries.Count > 1 ? winner with { Rival = entries[1].M.Species, RivalScore = entries[1].M.Score } : winner;
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
    /// The spawn list caps how many mobs of a species one frame can show: matches beyond a species' spawn count (the
    /// weakest first) lose their sprite, and stay mobs only by what their track knew.
    /// </summary>
    private void CapBySpawns(List<Candidate> candidates)
    {
        foreach (var g in candidates.Where(c => c.Sprite is not null).GroupBy(c => c.Sprite!.Value.Species.Id))
        {
            var spawns = _mobs!.ById(g.Key)?.Spawns ?? 0;
            foreach (var extra in g.OrderByDescending(c => c.Sprite!.Value.Score).Skip(spawns)) { extra.Sprite = null; SpawnCapped++; }
        }
    }

    /// <summary>Matches dropped by <see cref="CapBySpawns"/> since construction.</summary>
    public int SpawnCapped { get; private set; }

    /// <summary>Scores a candidate against every species of the map (best frame each), for the report.</summary>
    private void Diagnose(Candidate c, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx)
    {
        if (c.Pixels <= 0) return;
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
                if (!_matcher.Fits(f, c.Box.Width, c.Box.Height)) continue;
                if (_matcher.Best(c.Box, null, e.Species, f, 0) is { } m && (!best.TryGetValue(e.Species.Id, out var b) || m.Score > b.Score))
                    best[e.Species.Id] = m;
            }
        Diagnostics.Add(new SpriteDiagnostic(ctx.Frame, c.Box, c.Tag is not null, tagSpecies, c.Sprite, best, c.Floor, c.BoxSurfaceDistance) { Source = c });
        if (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_DUMP") is { } dumpDir && best.Count > 0
            && (tagSpecies is not null || c.Sprite is not null || (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_DUMP_UNMATCHED") is not null && c.Pixels >= 600 && c.SurfaceDistance is { } usd && Math.Abs(usd) <= 6)))
            DumpMatch(dumpDir, frame, frameWidth, ctx.Frame, c, tagSpecies is { } ts && best.TryGetValue(ts, out var tm) ? tm : c.Sprite ?? best.Values.MaxBy(b => b.Score));
    }

    /// <summary>
    /// Debug (<c>OAKSOUL_SPRITE_DUMP=dir</c>, report runs only): the candidate's surroundings, the best frame placed over
    /// it, and their raw per-pixel difference (green within the tolerance, red beyond, blue the frame's soft edge), side
    /// by side at 3x, for every tagged or matched candidate (and, with <c>OAKSOUL_SPRITE_DUMP_UNMATCHED</c>, every large
    /// unmatched one on a foothold).
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
                        dpx = a < (m.Strict ? SpriteFrame.SolidAlpha : SpriteFrame.OpaqueAlpha) ? 0xFF0000FFu : d <= (m.Strict ? 3 * SpriteMatcher.StrictTolerance : SpriteMatcher.PixelTolerance) ? 0xFF00C000u : 0xFF000000u | (uint)Math.Min(255, d) << 16;
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

    private Registration Fine(ReadOnlySpan<byte> frame, int frameWidth, int[] samples, int gx, int gy, int rx, int ry) =>
        CameraRegistration.Register(frame, frameWidth, _mask, samples, _mosaic.Pixels, _mosaic.Usable, _mosaic.OriginX, _mosaic.OriginY,
            _mosaic.Width, _mosaic.Height, gx, gy, rx, ry);

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

    private static bool IsHpBar(Blob b, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect vp)
    {
        if (b.Width < HpBarMinWidth || b.Width > HpBarMaxWidth || b.Height < HpBarMinHeight || b.Height > HpBarMaxHeight) return false;
        var green = 0;
        for (var y = b.Y0; y <= b.Y1; y++)
            for (var x = b.X0; x <= b.X1; x++)
            {
                var o = ((y + vp.Y) * frameWidth + x + vp.X) * 4;
                if (frame[o + 1] > 150 && frame[o + 2] < 120 && frame[o] < 120) green++;
            }
        return green >= HpBarGreenPixels;
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
        foreach (var e in result.Entities) canvas.Rect(e.Box.X, e.Box.Y, e.Box.Width, e.Box.Height, KindColour(e.Kind), t);
        if (half) canvas = canvas.Half();
        var s = half ? 2 : 1;
        foreach (var e in result.Entities)
        {
            var label = Label(e);
            canvas.Text(e.Box.X / s, Math.Max(0, e.Box.Y / s - 9), label, KindColour(e.Kind));
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
        var name = e.Name is null ? "" : " " + (e.Level is { } lv ? lv > 0 ? $"Lv{lv} " : "Lv? " : "") + e.Name;
        return $"#{e.TrackId} {e.Kind}{(e.Kind == EntityKind.Mob ? "?" : "")}{name}";
    }

    private void Dump(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, List<DetectedEntity> entities, int camX, int camY)
    {
        Directory.CreateDirectory(DebugDir!);
        var r = new EntityFrame(ctx.Frame, ctx.Time, entities, (camX, camY), null, 0, 0);
        Annotate(frame, frameWidth, r, foreground: true).Save(Path.Combine(DebugDir!, $"{ctx.Frame:0000}.png"));
    }
}
