using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The gap filler (off by default; <see cref="GapFiller"/>). While the camera moves, the pixels the mosaic cannot vouch for are
/// classed Ignored (<see cref="ForegroundExtractor.Difference"/>) and the blob stage sees nothing there; the tracker's predictions
/// carry the known mobs through, but a new one is missed until the camera stops. On a frame whose camera moved since the last
/// one, after the temporal pass and the blob stage and before the tracker, the floor band's Ignored pixels that no prediction
/// explains are cut into slices (<see cref="FootholdSlices"/>) and the map's ground mob frames and the meso coins are searched on
/// them directly against the screen (<see cref="SliceMatcher"/>, no background needed), under a per-frame time budget, from a
/// cursor that moves on across frames so consecutive moving frames cover different stretches of the band. Each find becomes a
/// candidate found by <see cref="FoundBy.Slice"/>, unless a candidate already shows it. Off, nothing here runs and every result
/// is as before.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>Runs the gap filler on frames whose camera moved (EntityWatch: OAKSOUL_GAP_FILLER=on or 1). Off by default.</summary>
    public bool GapFiller { get; set; }

    /// <summary>
    /// The gap filler's time per frame, its slicing included. The first <see cref="GapFirstChunk"/> slices of a moving frame are
    /// always searched; further searches run while the cost model (<see cref="GapFixedMs"/>, <see cref="GapMarginalMs"/>) says the
    /// budget left buys <see cref="GapMinChunk"/> slices or more. A frame overruns by what its searches cost beyond the model: a
    /// slice's cost depends on what lies on it.
    /// </summary>
    public double GapFillerBudgetMs { get; set; } = 2.0;

    /// <summary>
    /// The narrowest blind run searched, in slices (columns). Smaller than the narrowest ground mob (35 on Deep Valley I, the
    /// snails' move/0) because the blind stretch may cover only part of a mob, the rest standing on classified pixels where the blob
    /// stage sees at most a fragment; the matcher compares the whole sprite, so a seed in the blind part is enough. A run narrower
    /// than 12 hides less than a third of the narrowest mob, and a coin face-on (about 25 wide) cannot lie wholly inside it; only
    /// the edge-on coin frames (5 wide) fit wholly in such a run, and those are left to the blob stage.
    /// </summary>
    public const int GapMinRun = 12;

    /// <summary>The gap filler's work on one moving frame.</summary>
    /// <param name="Available">Slices of the blind runs on the frame.</param>
    /// <param name="Searched">Slices searched within the budget.</param>
    /// <param name="Finds">Finds the searches kept (one per place, mobs and coins).</param>
    /// <param name="Duplicates">Finds a candidate already showed (the same mob's sprite, or a drop over the coin's box).</param>
    /// <param name="Attached">Finds that verified an unmatched blob (the blob became the mob or the coin).</param>
    /// <param name="Added">Finds added as candidates of their own.</param>
    /// <param name="NewTracks">Finds (attached or added) whose candidate started a new track.</param>
    /// <param name="MatchedTracks">Finds whose candidate the tracker gave to a track that already existed.</param>
    /// <param name="PrepMs">Of <paramref name="Ms"/>: the masks and the slicing.</param>
    /// <param name="MobMs">Of <paramref name="Ms"/>: the mob searches.</param>
    /// <param name="MesoMs">Of <paramref name="Ms"/>: the coin searches.</param>
    /// <param name="Absorbed">Finds whose candidate the tracker absorbed into another (a piece of a mob a track holds this frame).</param>
    public sealed record GapFrame(long Frame, long Sequence, double Ms, double DetectorMs, int Available, int Searched, int Finds, int Duplicates,
        int Attached, int Added, int NewTracks, int MatchedTracks, double PrepMs = 0, double MobMs = 0, double MesoMs = 0, int Absorbed = 0);

    /// <summary>A find whose candidate started a new track: the frame, what was found and where.</summary>
    public sealed record GapNewTrack(long Frame, long Sequence, string Name, string FrameKey, CaptureRect Box, double Distance, int TrackId, EntityKind Kind);

    /// <summary>Every moving frame the gap filler ran on, in order.</summary>
    public List<GapFrame> GapFrames { get; } = [];

    /// <summary>Every find that started a new track.</summary>
    public List<GapNewTrack> GapNewTracks { get; } = [];

    /// <summary>Cumulative totals since construction: milliseconds, slices available and searched, finds, and what became of them.</summary>
    public double GapFillerMs { get; private set; }
    public long GapSlicesAvailable => GapFrames.Sum(g => (long)g.Available);
    public long GapSlicesSearched => GapFrames.Sum(g => (long)g.Searched);
    public int GapFinds => GapFrames.Sum(g => g.Finds);
    public int GapFindsNewTracks => GapFrames.Sum(g => g.NewTracks);
    public int GapFindsMatchedTracks => GapFrames.Sum(g => g.MatchedTracks);

    /// <summary>Frames the gap filler saw with the camera still (it did nothing on them).</summary>
    public int GapStillFrames { get; private set; }

    /// <summary>The slice searches' own counters (placements seeded and passed, columns and pixels compared), mobs and coins.</summary>
    public SliceMatcher.Stats GapMobStats { get; } = new();
    public SliceMatcher.Stats GapMesoStats { get; } = new();

    /// <summary>When set, a PNG crop of the frame around every find that started a new track is written here (EntityWatch: OAKSOUL_GAP_FINDS_DIR).</summary>
    public string? GapFindsDir { get; set; }

    private HudLayout? _gapLayout;
    private ScreenMask? _gapMask, _gapMatchMask;
    private (int X, int Y)? _gapLastCamera;
    private MapData? _gapMap;
    private Dictionary<int, int> _gapFloorOf = [];
    private List<SliceMatcher.Candidate>? _gapMobs, _gapMesos;
    private SliceMatcher.CandidateSet? _gapMobSet, _gapMesoSet;
    private Dictionary<SpriteFrame, MobSpecies> _gapSpecies = new(ReferenceEqualityComparer.Instance);
    private Dictionary<SpriteFrame, string> _gapCoinOf = new(ReferenceEqualityComparer.Instance);
    private static readonly SliceMatcher.Options MesoSliceOptions = new() { FloorRows = 8 };

    /// <summary>Where the next frame's walk starts: a floor (its index in <see cref="FootholdSlices.Floors"/>) and a world column on it.</summary>
    private (int Floor, int Wx) _gapCursor;

    /// <summary>
    /// Slices every moving frame searches, whatever the budget left says, in one search where the runs allow (<see cref="GapChunkRows"/>):
    /// a search carries a fixed cost (the screen cells for the rows its slices reach, both for the mobs and the coins, about 0.5 ms),
    /// so searches of one to three slices had spent the budget on that overhead. 40 slices cost a typical 0.5 + 40 x 0.02 ms, which
    /// with the masks and the slicing (about 0.2 ms) fits the 2 ms budget; sizing the first search by the cost model instead
    /// overran the budget on most frames at the median rate and searched about 24 slices at its 75th percentile.
    /// </summary>
    public const int GapFirstChunk = 40;

    /// <summary>
    /// The most rows a search's slices' lines may spread over (runs further apart in height go to the next search, which pays the
    /// fixed cost again). At 48 the 40 slices every frame searches took 1.52 searches per moving frame over fx-combat-exact (a tenth
    /// of the searches 11 slices or fewer); the screen a search prepares spans the sprites' height besides, so a wider spread costs
    /// less than a second search.
    /// </summary>
    public const int GapChunkRows = 128;

    /// <summary>A later search of the frame runs only if the budget left buys at least this many slices beyond its fixed cost.</summary>
    public const int GapMinChunk = 8;

    /// <summary>
    /// The cost model a search is sized by: <see cref="GapFixedMs"/> per search (mobs and coins over the same slices) plus
    /// <see cref="GapMarginalMs"/> per slice. The fixed part is set, not fitted: a 1-slice search of both lists took a median
    /// 0.54 ms on the first 15 s of fx-combat-exact (the probes, <see cref="GapProbeEvery"/>), which is nearly all fixed cost.
    /// The marginal rate is the median, over the last <see cref="GapFitWindow"/> searches, of each one's time beyond the fixed
    /// part per slice: a slice's cost depends on what lies on it (over mist the lift index retrieves many placements), so a
    /// least-squares fit on searches of nearly equal sizes was dominated by that spread and drove the fixed part to 17 ms.
    /// </summary>
    public double GapFixedMs { get; } = 0.5;
    public double GapMarginalMs { get; private set; } = 0.03;
    public const int GapFitWindow = 64;
    private readonly Queue<double> _gapFit = new();

    /// <summary>Every search the gap filler ran: its slices and its milliseconds for the mobs and for the coins.</summary>
    public List<(int Slices, double MobMs, double MesoMs)> GapCalls { get; } = [];

    /// <summary>
    /// Diagnostics (EntityWatch: OAKSOUL_GAP_PROBE_EVERY=N): on every Nth moving frame, after the frame is done and outside its time,
    /// one search of 1 slice and one of 40 on the frame's longest run (mobs, then coins), timed; their finds are dropped. 0 is off.
    /// </summary>
    public int GapProbeEvery { get; set; }
    public const int GapProbeSlices = 40;

    /// <summary>The probes: milliseconds of the mob and the coin search over 1 slice and over <see cref="GapProbeSlices"/>.</summary>
    public List<(double Mob1, double Meso1, double Mob40, double Meso40)> GapProbes { get; } = [];
    private List<FootholdSlices.Run> _gapRuns = [];
    private int _gapMoving;

    /// <summary>This frame's gap candidates, and what each was, for counting the tracker's answer.</summary>
    private readonly List<(Candidate Candidate, SliceMatcher.Find Find, bool Attached)> _gapCandidates = [];
    private GapFrame? _gapPending;

    /// <summary>The candidate lists and their sets (the matcher's layout and seed index), laid out once: every frame of the map's ground mobs (not flyers), named by species, and every meso coin frame.</summary>
    private void PrepareGapSets()
    {
        if (_gapMobs is not null) return;
        _gapSpecies = new(ReferenceEqualityComparer.Instance);
        _gapCoinOf = new(ReferenceEqualityComparer.Instance);
        _gapMobs = [];
        foreach (var e in _mobs?.Entries ?? [])
        {
            if (e.Species.Mobility == MobMobility.Fly) continue;
            foreach (var f in e.Species.Frames)
            {
                if (!_gapSpecies.TryAdd(f, e.Species)) continue;
                _gapMobs.Add(new SliceMatcher.Candidate(e.Species.Name, f));
            }
        }
        _gapMesos = [];
        foreach (var (key, group) in Coins?.Groups ?? new Dictionary<string, MobSpecies>())
            foreach (var f in group.Frames)
            {
                if (!_gapCoinOf.TryAdd(f, key)) continue;
                _gapMesos.Add(new SliceMatcher.Candidate("meso", f));
            }
        _gapMobSet = new SliceMatcher.CandidateSet(_gapMobs, new SliceMatcher.Options().SeedPixels);
        _gapMesoSet = new SliceMatcher.CandidateSet(_gapMesos, MesoSliceOptions.SeedPixels);
        GapMobStats.IndexMs += _gapMobSet.BuildMs;
        GapMesoStats.IndexMs += _gapMesoSet.BuildMs;
    }

    /// <summary>The map's floors by foothold, for the cursor.</summary>
    private void PrepareGapMap(MapData map)
    {
        if (ReferenceEquals(_gapMap, map)) return;
        _gapMap = map;
        // Floors ranked top to bottom by their mean line, so the walk goes from a floor to the one nearest it in height and a search
        // over consecutive runs reaches few rows.
        var floors = FootholdSlices.Plan.Of(map.Footholds).Floors
            .Select(f => (f, Mean: f.Line.Where(l => l != int.MinValue).Average())).OrderBy(f => f.Mean).ToList();
        _gapFloorOf = [];
        for (var i = 0; i < floors.Count; i++)
            foreach (var id in floors[i].f.FootholdId) _gapFloorOf.TryAdd(id, i);
        _gapCursor = (0, int.MinValue);
    }

    /// <summary>
    /// The gap filler on one frame: returns the milliseconds it took (0 when the camera stood still). Adds its finds to
    /// <paramref name="candidates"/>, or attaches them to an unmatched blob they cover.
    /// </summary>
    private double FillGaps(List<Candidate> candidates, IReadOnlyList<Prediction> predictions, ReadOnlySpan<byte> frame, int frameWidth,
        DetectionContext ctx, int camX, int camY)
    {
        _gapCandidates.Clear();
        _gapPending = null;
        _gapRuns = [];
        var last = _gapLastCamera;
        _gapLastCamera = (camX, camY);
        if (last == (camX, camY)) { GapStillFrames++; return 0; }
        var t0 = Stopwatch.GetTimestamp();
        double Elapsed() => Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        PrepareGapSets();
        PrepareGapMap(ctx.Map);
        var vp = _mask.Viewport;
        int w = _mask.Width, h = _mask.Height, n = w * h;
        _gapMask ??= new ScreenMask(_gapLayout!, [LogRegion]);
        _gapMatchMask ??= new ScreenMask(_gapLayout!, [LogRegion]);
        // One pass over the pixels: the matcher compares every pixel but the masked and the predicted (the temporal pass's
        // explained buffer, read in place); the slices are built only over the Ignored pixels nothing explains.
        var bits = _mask.Bits;
        var cls = _fg.Classes;
        var gapBits = _gapMask.Bits;
        var matchBits = _gapMatchMask.Bits;
        var ex = _matcher.Explained is { } e0 && e0.Length == n ? e0 : null;
        {
            var vs = System.Numerics.Vector<byte>.Count;
            var ignored = new System.Numerics.Vector<byte>((byte)PixelClass.Ignored);
            var one = new System.Numerics.Vector<byte>(1);
            var p = 0;
            for (; p <= n - vs; p += vs)
            {
                var known = new System.Numerics.Vector<byte>(bits, p);
                if (ex is not null) known |= new System.Numerics.Vector<byte>(ex, p);
                known.CopyTo(matchBits, p);
                (known | ~System.Numerics.Vector.Equals(new System.Numerics.Vector<byte>(cls, p), ignored) & one).CopyTo(gapBits, p);
            }
            for (; p < n; p++)
            {
                var known = (byte)(bits[p] | (ex is null ? 0 : ex[p]));
                matchBits[p] = known;
                gapBits[p] = (byte)(known | (cls[p] != (byte)PixelClass.Ignored ? 1 : 0));
            }
        }
        // On a full frame the predictions explain nothing in the buffer: their opaque pixels are marked here.
        if (ex is null)
            foreach (var pr in predictions)
            {
                var m = pr.Match;
                for (var i = 0; i < m.Frame.PixelX.Length; i++)
                {
                    int x = m.Left + m.Frame.PixelX[i] - vp.X, y = m.Top + m.Frame.PixelY[i] - vp.Y;
                    if ((uint)x < (uint)w && (uint)y < (uint)h) matchBits[y * w + x] = gapBits[y * w + x] = 1;
                }
            }
        var runs = FootholdSlices.Build(frame, frameWidth, _gapMask, _mosaic, camX, camY, ctx.Map.Footholds, GapMinRun);
        _gapRuns = runs;
        runs.Sort((a, b) => (_gapFloorOf.GetValueOrDefault(a.Slices[0].FootholdId, int.MaxValue), a.X0).CompareTo((_gapFloorOf.GetValueOrDefault(b.Slices[0].FootholdId, int.MaxValue), b.X0)));
        var available = runs.Sum(r => r.Width);
        var prepMs = Elapsed();
        double mobMs = 0, mesoMs = 0;
        var finds = new List<(SliceMatcher.Find Find, bool Meso)>();
        var searched = 0;
        if (available > 0 && (_gapMobs!.Count > 0 || _gapMesos!.Count > 0))
        {
            // The walk starts at the first slice at or after the cursor, in the runs' order (floor by floor top to bottom, left to right).
            int Floor(FootholdSlices.Slice s) => _gapFloorOf.GetValueOrDefault(s.FootholdId, int.MaxValue);
            (int, int) Key(FootholdSlices.Slice s) => (Floor(s), camX + s.X);
            int ri = 0, si = 0;
            var found = false;
            for (var r = 0; r < runs.Count && !found; r++)
                for (var s = 0; s < runs[r].Slices.Count && !found; s++)
                    if (Key(runs[r].Slices[s]).CompareTo(_gapCursor) >= 0) { ri = r; si = s; found = true; }
            FootholdSlices.Slice? lastSearched = null;
            var chunk = new List<FootholdSlices.Run>();
            while (searched < available)
            {
                // The first GapFirstChunk slices always; then what the budget left buys beyond one search's fixed cost.
                var left = GapFillerBudgetMs - Elapsed();
                var buys = (int)Math.Min(int.MaxValue / 2, Math.Max(0, (left - GapFixedMs) / GapMarginalMs));
                int take;
                if (searched < GapFirstChunk) take = GapFirstChunk - searched;
                else if (buys < GapMinChunk) break;
                else take = buys;
                take = Math.Min(take, available - searched);
                // Whole runs, or a run's stretch, from the walk's place on (wrapping to the first run), as long as their lines stay
                // within GapChunkRows of each other: a search prepares the screen for every row its slices can reach.
                chunk.Clear();
                int got = 0, lineMin = int.MaxValue, lineMax = int.MinValue;
                while (got < take)
                {
                    var run = runs[ri];
                    var k = Math.Min(take - got, run.Slices.Count - si);
                    int lo = lineMin, hi = lineMax;
                    for (var j = si; j < si + k; j++) { lo = Math.Min(lo, run.Slices[j].LineY); hi = Math.Max(hi, run.Slices[j].LineY); }
                    if (got > 0 && hi - lo > GapChunkRows) break;
                    lineMin = lo; lineMax = hi;
                    chunk.Add(si == 0 && k == run.Slices.Count ? run : new FootholdSlices.Run(run.Slices.Skip(si).Take(k).ToArray()));
                    lastSearched = run.Slices[si + k - 1];
                    got += k;
                    si += k;
                    if (si >= run.Slices.Count) { si = 0; ri = (ri + 1) % runs.Count; }
                }
                take = got;
                var c0 = Elapsed();
                if (_gapMobs!.Count > 0)
                    foreach (var f in SliceMatcher.Search(frame, frameWidth, _gapMatchMask, _mosaic, camX, camY, chunk, _gapMobSet!, null, GapMobStats)) Keep(f, false);
                var c1 = Elapsed();
                if (_gapMesos!.Count > 0)
                    foreach (var f in SliceMatcher.Search(frame, frameWidth, _gapMatchMask, _mosaic, camX, camY, chunk, _gapMesoSet!, MesoSliceOptions, GapMesoStats)) Keep(f, true);
                var c2 = Elapsed();
                mobMs += c1 - c0; mesoMs += c2 - c1;
                GapCalls.Add((take, c1 - c0, c2 - c1));
                FitGapCost(take, c2 - c0);
                searched += take;
            }
            if (lastSearched is { } ls) _gapCursor = searched >= available ? _gapCursor : (Floor(ls), camX + ls.X + 1);
        }

        // Each find to a candidate the tracker understands, unless a candidate shows it already.
        int duplicates = 0, attached = 0, added = 0;
        foreach (var (f, meso) in finds)
        {
            var box = new CaptureRect(f.Left + f.Frame.X0, f.Top + f.Frame.Y0, f.Frame.ExtentWidth, f.Frame.ExtentHeight);
            var matches = (int)Math.Round((1 - f.Distance) * f.Pixels);
            if (!meso)
            {
                var sm = new SpriteMatch(_gapSpecies[f.Frame], f.Frame, f.Left, f.Top, matches, f.Pixels, SpriteTint.None, 0);
                if (candidates.Any(c => c.Sprite is { } cs && SameMob(cs, sm) || c.PetSprite is { } ps && SameMob(ps, sm))) { duplicates++; continue; }
                var holder = candidates.FirstOrDefault(c => c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Damage is null && c.Pixels > 0
                    && !_tracker.Names.IsCharacterTag(c.Tag?.Text) && Covers(sm, c.Box) >= 0.5);
                var cand = holder ?? MakeCandidate(ctx, camX, camY, box, sm.Feet, 0, null, false, false, null, _ownWorld);
                cand.BoxSurfaceDistance ??= cand.SurfaceDistance;
                cand.Sprite = sm;
                cand.Found = FoundBy.Slice;
                Place(cand, sm.Feet, ctx, camX, camY);
                cand.Floor = FloorFor(cand, ctx);
                if (holder is null) { candidates.Add(cand); added++; } else attached++;
                _gapCandidates.Add((cand, f, holder is not null));
            }
            else
            {
                if (candidates.Any(c => c.Drop is not null && Overlap(c.Box, box) >= 0.5)) { duplicates++; continue; }
                var holder = candidates.FirstOrDefault(c => c.Sprite is null && c.Drop is null && c.PetSprite is null && c.Skill is null && c.Damage is null && c.Pixels > 0
                    && !c.TouchesOwn && c.Tag is null && Overlap(c.Box, box) >= 0.5);
                var cand = holder ?? MakeCandidate(ctx, camX, camY, box, new PointD(f.FeetX, box.Y + box.Height), Math.Max(1, f.Pixels), null, false, false, null, _ownWorld);
                cand.Drop = WithAnchor(new DropMatch(_gapCoinOf[f.Frame], "meso", f.Frame.Index, f.Left, f.Top, matches, f.Pixels), camX, camY);
                cand.Found = FoundBy.Slice;
                if (holder is null) { candidates.Add(cand); added++; } else attached++;
                _gapCandidates.Add((cand, f, holder is not null));
            }
        }
        var ms = Elapsed();
        _gapPending = new GapFrame(ctx.Frame, ctx.Sequence, ms, 0, available, searched, finds.Count, duplicates, attached, added, 0, 0, prepMs, mobMs, mesoMs);
        return ms;

        // Across searches of one frame the best per place stays (two neighbouring chunks can find one mob).
        void Keep(SliceMatcher.Find f, bool meso)
        {
            var box = new CaptureRect(f.Left + f.Frame.X0, f.Top + f.Frame.Y0, f.Frame.ExtentWidth, f.Frame.ExtentHeight);
            var i = finds.FindIndex(k => k.Meso == meso && Overlap(new CaptureRect(k.Find.Left + k.Find.Frame.X0, k.Find.Top + k.Find.Frame.Y0, k.Find.Frame.ExtentWidth, k.Find.Frame.ExtentHeight), box) >= 0.5);
            if (i < 0) finds.Add((f, meso));
            else if (f.Distance < finds[i].Find.Distance) finds[i] = (f, meso);
        }
    }

    /// <summary>The shared area of two boxes over the smaller one's.</summary>
    private static double Overlap(CaptureRect a, CaptureRect b)
    {
        int w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X), h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0) return 0;
        return (double)w * h / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    /// <summary>Adds a search to the cost model: the marginal rate is the median of the recent searches' time beyond the fixed part, per slice.</summary>
    private void FitGapCost(int slices, double ms)
    {
        _gapFit.Enqueue(Math.Max(ms - GapFixedMs, 0) / slices);
        if (_gapFit.Count > GapFitWindow) _gapFit.Dequeue();
        var sorted = _gapFit.OrderBy(r => r).ToList();
        GapMarginalMs = Math.Max(sorted[sorted.Count / 2], 0.002);
    }

    /// <summary>The probe (<see cref="GapProbeEvery"/>): one search of 1 slice and one of <see cref="GapProbeSlices"/> on the frame's longest run, timed apart.</summary>
    private void ProbeGapCost(ReadOnlySpan<byte> frame, int frameWidth, int camX, int camY)
    {
        if (_gapRuns.Count == 0 || _gapMobSet is null || _gapMesoSet is null) return;
        var run = _gapRuns.MaxBy(r => r.Width)!;
        if (run.Width < GapProbeSlices) return;
        double Time(ReadOnlySpan<byte> fr, FootholdSlices.Run r, bool meso)
        {
            var t = Stopwatch.GetTimestamp();
            if (meso) SliceMatcher.Search(fr, frameWidth, _gapMatchMask!, _mosaic, camX, camY, [r], _gapMesoSet, MesoSliceOptions, new SliceMatcher.Stats());
            else SliceMatcher.Search(fr, frameWidth, _gapMatchMask!, _mosaic, camX, camY, [r], _gapMobSet, null, new SliceMatcher.Stats());
            return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        var mid = (run.Width - GapProbeSlices) / 2;
        var r1 = new FootholdSlices.Run(run.Slices.Skip(mid).Take(1).ToArray());
        var r40 = new FootholdSlices.Run(run.Slices.Skip(mid).Take(GapProbeSlices).ToArray());
        GapProbes.Add((Time(frame, r1, false), Time(frame, r1, true), Time(frame, r40, false), Time(frame, r40, true)));
    }

    /// <summary>After the tracker: which gap candidates started a track and which joined one; the frame's counters; crops of the new ones.</summary>
    private void CountGapTracks(List<Candidate> candidates, List<DetectedEntity> entities, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, double detectorMs)
    {
        if (_gapPending is not { } g) return;
        int fresh = 0, matched = 0, absorbed = 0;
        foreach (var (cand, find, _) in _gapCandidates)
        {
            var k = candidates.IndexOf(cand);
            if (k < 0 || k >= entities.Count) { absorbed++; continue; }
            var e = entities[k];
            if (e.FirstFrame == ctx.Frame && e.Age <= 1)
            {
                fresh++;
                var box = new CaptureRect(find.Left + find.Frame.X0, find.Top + find.Frame.Y0, find.Frame.ExtentWidth, find.Frame.ExtentHeight);
                var nt = new GapNewTrack(ctx.Frame, ctx.Sequence, find.Name, find.Frame.Key, box, find.Distance, e.TrackId, e.Kind);
                GapNewTracks.Add(nt);
                if (GapFindsDir is not null) SaveGapCrop(frame, frameWidth, nt);
            }
            else matched++;
        }
        GapFrames.Add(g with { DetectorMs = detectorMs, NewTracks = fresh, MatchedTracks = matched, Absorbed = absorbed });
        if (GapProbeEvery > 0 && ++_gapMoving % GapProbeEvery == 0 && ctx.Camera is not null && _gapLastCamera is { } cam) ProbeGapCost(frame, frameWidth, cam.X, cam.Y);
        _gapPending = null;
        _gapCandidates.Clear();
    }

    /// <summary>The frame around a new track's find (its box plus 24 pixels), three times, nearest neighbour; listed in finds.txt.</summary>
    private void SaveGapCrop(ReadOnlySpan<byte> frame, int frameWidth, GapNewTrack nt)
    {
        const int margin = 24, z = 3;
        Directory.CreateDirectory(GapFindsDir!);
        var fh = frame.Length / 4 / frameWidth;
        int x0 = nt.Box.X - margin, y0 = nt.Box.Y - margin, cw = nt.Box.Width + 2 * margin, ch = nt.Box.Height + 2 * margin;
        var canvas = new Canvas(cw * z, ch * z, 0xFF000000);
        for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                int sx = x0 + x, sy = y0 + y;
                if ((uint)sx >= (uint)frameWidth || (uint)sy >= (uint)fh) continue;
                var o = (sy * frameWidth + sx) * 4;
                var argb = 0xFF000000u | (uint)(frame[o + 2] << 16 | frame[o + 1] << 8 | frame[o]);
                for (var dy = 0; dy < z; dy++)
                    for (var dx = 0; dx < z; dx++) canvas.Set(x * z + dx, y * z + dy, argb);
            }
        var name = $"{nt.Sequence:000000}-{nt.Name.Replace(' ', '-')}-track{nt.TrackId}.png";
        canvas.Save(Path.Combine(GapFindsDir!, name));
        File.AppendAllText(Path.Combine(GapFindsDir!, "finds.txt"),
            FormattableString.Invariant($"sequence {nt.Sequence} frame {nt.Frame} {nt.Name} {nt.FrameKey} box {nt.Box.X},{nt.Box.Y} {nt.Box.Width}x{nt.Box.Height} distance {nt.Distance:0.000} track {nt.TrackId} {nt.Kind} -> {name}\n"));
    }
}
