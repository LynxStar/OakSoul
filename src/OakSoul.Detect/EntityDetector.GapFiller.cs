using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The gap filler (on by default; <see cref="GapFiller"/>). While the camera moves, the pixels the mosaic cannot vouch for are
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
    /// <summary>
    /// Runs the gap filler on frames whose camera moved. On by default since 2026-10-30, after its measured run on fx-combat-exact
    /// (README, "Floor slices"); EntityWatch turns it off with OAKSOUL_GAP_FILLER=off or 0.
    /// </summary>
    public bool GapFiller { get; set; } = true;

    /// <summary>
    /// The gap filler's time per frame, its slicing included: the matcher's own budget (<see cref="SliceMatcher.Options.BudgetMs"/>)
    /// stops each search at a slice boundary once the time left is spent, so a frame overruns by at most the slice in hand (the
    /// first slice of each search always runs), and the walk resumes there on the next moving frame.
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
    /// <param name="Searched">Slices the mob search covered within the budget.</param>
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
    /// <param name="MesoSearched">Slices the coin search covered within the budget.</param>
    /// <param name="ResolveMs">Of <paramref name="Ms"/>: the one explain pass over both searches' finds.</param>
    public sealed record GapFrame(long Frame, long Sequence, double Ms, double DetectorMs, int Available, int Searched, int Finds, int Duplicates,
        int Attached, int Added, int NewTracks, int MatchedTracks, double PrepMs = 0, double MobMs = 0, double MesoMs = 0, int Absorbed = 0,
        int MesoSearched = 0, double ResolveMs = 0);

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

    /// <summary>
    /// Where the next moving frame's mob search and coin search start: a floor (its rank top to bottom) and a world column on it,
    /// kept as a world position because the runs change from frame to frame.
    /// </summary>
    private (int Floor, int Wx) _gapMobCursor, _gapMesoCursor;

    /// <summary>
    /// The share of the budget left after the masks and the slicing that the mob search may take; the coin search, which runs
    /// second, takes the rest of that share whatever the mobs overran (else it was left nothing and stopped before its first slice on
    /// most frames). On fx-combat-exact a 40-slice search took a median 1.43 ms for the mobs and 0.35 ms for the
    /// coins (the probes of the previous sizing), about four to one.
    /// </summary>
    public const double GapMobShare = 0.75;

    /// <summary>
    /// The most rows a search's runs' lines may spread over. A search prepares the screen for every row its runs can reach, and
    /// its budget counts that preparation: over all of a frame's runs (the whole frame's height) it took about 1.5 ms, the budget
    /// was spent before the first slice and the search stopped there (a median 0 slices per moving frame). Whole runs are passed
    /// either way; a window of runs close in height keeps the preparation small.
    /// </summary>
    public const int GapWindowRows = 64;

    /// <summary>
    /// Milliseconds held back from the two searches' budgets for what comes after a deadline: the slice in hand finishing, the
    /// resolve and the candidates. Without it every moving frame of fx-combat-exact overran the 2 ms budget, by a median 0.28 ms.
    /// </summary>
    public const double GapReserveMs = 0.3;

    /// <summary>Searches that stopped before their first slice (the budget spent on preparing the screen); the cursor then stays.</summary>
    public int GapStalls { get; private set; }

    /// <summary>This frame's gap candidates, and what each was, for counting the tracker's answer.</summary>
    private readonly List<(Candidate Candidate, SliceMatcher.Find Find, bool Attached)> _gapCandidates = [];
    private GapFrame? _gapPending;

    /// <summary>The candidate lists and their sets (the matcher's layout and seed index), laid out once: every frame but the die frames of the map's ground mobs (not flyers), named by species, and every meso coin frame.</summary>
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
                // No die frames: a dying mob is not a target (the tracker already holds it, or its kill is credited), and a die frame's
                // pale, ragged art fits mist under the matcher's lift: five of the eight false new tracks of the 40-slice run were
                // Fire Boar die1 frames over mist. Move, stand, hit and the rest stay.
                if (f.Anim.StartsWith("die", StringComparison.Ordinal)) continue;
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
        // Floors ranked top to bottom by their mean line, so the walk goes from a floor to the one nearest it in height and a budgeted
        // search over consecutive runs reaches few rows.
        var floors = FootholdSlices.Plan.Of(map.Footholds).Floors
            .Select(f => (f, Mean: f.Line.Where(l => l != int.MinValue).Average())).OrderBy(f => f.Mean).ToList();
        _gapFloorOf = [];
        for (var i = 0; i < floors.Count; i++)
            foreach (var id in floors[i].f.FootholdId) _gapFloorOf.TryAdd(id, i);
        _gapMobCursor = _gapMesoCursor = (0, int.MinValue);
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
        runs.Sort((a, b) => (_gapFloorOf.GetValueOrDefault(a.Slices[0].FootholdId, int.MaxValue), a.X0).CompareTo((_gapFloorOf.GetValueOrDefault(b.Slices[0].FootholdId, int.MaxValue), b.X0)));
        var available = runs.Sum(r => r.Width);
        var prepMs = Elapsed();
        double mobMs = 0, mesoMs = 0, resolveMs = 0;
        int searched = 0, mesoSearched = 0;
        var finds = new List<(SliceMatcher.Find Find, bool Meso)>();
        if (available > 0 && (_gapMobs!.Count > 0 || _gapMesos!.Count > 0))
        {
            // The runs whole, in the walk's order (floor by floor top to bottom, left to right); each search resumes where the last
            // moving frame's stopped, keeps every find unsuppressed, and one explain pass then settles both searches' finds together.
            int Floor(FootholdSlices.Slice sl) => _gapFloorOf.GetValueOrDefault(sl.FootholdId, int.MaxValue);
            (int, int) Key(FootholdSlices.Slice sl) => (Floor(sl), camX + sl.X);
            var all = new List<SliceMatcher.Find>();
            if (_gapMobs!.Count > 0)
            {
                var c0 = Elapsed();
                searched = SearchFrom(frame, frameWidth, camX, camY, runs, _gapMobSet!, new SliceMatcher.Options { KeepOverlapping = true }, GapMobStats,
                    ref _gapMobCursor, (GapFillerBudgetMs - GapReserveMs - c0) * GapMobShare, all);
                mobMs = Elapsed() - c0;
            }
            if (_gapMesos!.Count > 0)
            {
                var c1 = Elapsed();
                mesoSearched = SearchFrom(frame, frameWidth, camX, camY, runs, _gapMesoSet!, MesoSliceOptions with { KeepOverlapping = true }, GapMesoStats,
                    ref _gapMesoCursor, (GapFillerBudgetMs - GapReserveMs - prepMs) * (1 - GapMobShare), all);
                mesoMs = Elapsed() - c1;
            }
            var c2 = Elapsed();
            foreach (var f in SliceMatcher.Resolve(frame, frameWidth, _gapMatchMask, _mosaic, camX, camY, all)) finds.Add((f, _gapCoinOf.ContainsKey(f.Frame)));
            resolveMs = Elapsed() - c2;

            // One list's searches from its cursor under the budget given: each over a window of whole runs (from the cursor's run on,
            // as long as their lines stay within GapWindowRows), resumed at the cursor's slice; a window finished with time left
            // passes to the next (after the last run, the first). Returns the slices searched (at most the runs' slices) and moves
            // the cursor to where the last search stopped, or back to the top when the whole band was searched.
            int SearchFrom(ReadOnlySpan<byte> fr, int fw, int cx, int cy, List<FootholdSlices.Run> rs, SliceMatcher.CandidateSet set, SliceMatcher.Options o,
                SliceMatcher.Stats total, ref (int Floor, int Wx) cursor, double budget, List<SliceMatcher.Find> into)
            {
                var t0 = Stopwatch.GetTimestamp();
                int ri = 0, si = 0;
                var c = cursor;
                var at = rs.SelectMany((r, i) => r.Slices.Select((sl, j) => (i, j, sl))).FirstOrDefault(q => Key(q.sl).CompareTo(c) >= 0);
                if (at.sl != default) (ri, si) = (at.i, at.j);
                int firstRun = ri, firstSlice = si, runsDone = 0;
                long done = 0;
                var window = new List<FootholdSlices.Run>();
                while (true)
                {
                    window.Clear();
                    int lo = int.MaxValue, hi = int.MinValue;
                    for (var k = ri; window.Count < rs.Count - runsDone; k = (k + 1) % rs.Count)
                    {
                        // A wrapped window stops at the run this frame began in.
                        if (window.Count > 0 && k == firstRun) break;
                        int rlo = Math.Min(lo, rs[k].Slices.Min(q => q.LineY)), rhi = Math.Max(hi, rs[k].Slices.Max(q => q.LineY));
                        if (window.Count > 0 && rhi - rlo > GapWindowRows) break;
                        (lo, hi) = (rlo, rhi);
                        window.Add(rs[k]);
                        if (k == rs.Count - 1) break;
                    }
                    var stats = new SliceMatcher.Stats();
                    var left = budget - Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    into.AddRange(SliceMatcher.Search(fr, fw, _gapMatchMask!, _mosaic, cx, cy, window, set, o with { BudgetMs = Math.Max(left, 0), Start = (0, si) }, stats));
                    Add(total, stats);
                    done += stats.Slices;
                    if (stats.StoppedRun >= 0)
                    {
                        // Stopped in the window: resume there next time (a search that stopped before its first slice leaves the cursor as it was).
                        cursor = Key(window[stats.StoppedRun].Slices[stats.StoppedSlice]);
                        if (stats.Slices == 0) GapStalls++;
                        break;
                    }
                    // The window finished: on to the run after it, unless that is where this frame began (the whole band searched).
                    runsDone += window.Count;
                    ri = (ri + window.Count) % rs.Count;
                    si = 0;
                    if (runsDone >= rs.Count || ri == firstRun)
                    {
                        // Back at the start: the runs before the first slice of this frame's first run were all searched, but not
                        // the first run's own slices before firstSlice; those come first next time.
                        cursor = firstSlice > 0 ? Key(rs[firstRun].Slices[0]) : (0, int.MinValue);
                        break;
                    }
                    if (budget - Stopwatch.GetElapsedTime(t0).TotalMilliseconds <= 0)
                    {
                        cursor = Key(rs[ri].Slices[0]);
                        break;
                    }
                }
                return (int)Math.Min(done, rs.Sum(r => (long)r.Width));
            }
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
        _gapPending = new GapFrame(ctx.Frame, ctx.Sequence, ms, 0, available, searched, finds.Count, duplicates, attached, added, 0, 0, prepMs, mobMs, mesoMs,
            MesoSearched: mesoSearched, ResolveMs: resolveMs);
        return ms;
    }

    /// <summary>Adds one search's counters to the running totals.</summary>
    private static void Add(SliceMatcher.Stats total, SliceMatcher.Stats s)
    {
        total.Placements += s.Placements; total.SeedsPassed += s.SeedsPassed; total.Columns += s.Columns; total.Pixels += s.Pixels;
        total.Retrieved += s.Retrieved; total.IndexMs += s.IndexMs; total.QueryMs += s.QueryMs; total.Slices += s.Slices;
    }

    /// <summary>The shared area of two boxes over the smaller one's.</summary>
    private static double Overlap(CaptureRect a, CaptureRect b)
    {
        int w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X), h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0) return 0;
        return (double)w * h / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    /// <summary>After the tracker:
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
