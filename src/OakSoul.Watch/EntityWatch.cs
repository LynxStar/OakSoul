using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.Vision;
using OakSoul.Vision.Readers;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// The watcher's entity stage: loads the cached background of the current map, runs the detector on every pixel-exact
/// native frame (and counts the frames it skips, by reason), prints a summary line whenever it changes, collects
/// annotated frames for the contact sheets and keeps what the entity report needs.
/// </summary>
public sealed class EntityWatch
{
    /// <summary>Frames on the contact sheets, evenly spaced over the session, six to a sheet.</summary>
    public const int SheetFrames = 12, FramesPerSheet = 6;

    private readonly HudLayout _layout;
    private readonly string _backgrounds;
    private readonly MobLibrary? _mobLibrary;
    private readonly DropMatcher? _drops;
    private readonly DamageReader? _damage;
    private readonly CloudArt? _clouds;
    private readonly bool _diagnostics;
    private bool _petWarned;
    private readonly EntityTracker _tracker = new();
    private readonly string? _tagCache, _tagFont;
    private readonly Dictionary<int, EntityDetector?> _detectors = [];
    private readonly Dictionary<int, MapMobSet> _mobSets = [];
    private readonly SessionTimes _times;
    private readonly string? _sheetDir;
    private readonly HashSet<long> _sheetPicks = [];
    private readonly List<(long Frame, double Time, Canvas Tile)> _tiles = [];
    private EntityDetector? _detector;
    private int? _mapId;
    private readonly HashSet<long> _debugFrames;
    private readonly string? _debugDir;
    private string? _lastSummary;

    /// <param name="totalFrames">Frames the source will deliver, when known (a recording), to space the sheet frames.</param>
    /// <param name="mobInfo">The mob data exported by OakSoul.MobExport --sprites (data/mobinfo), for sprite verification.</param>
    /// <param name="sprites">The sprites exported by OakSoul.SpriteExport --items (data/sprites), for drop icons.</param>
    /// <param name="diagnostics">Score every candidate against every species too, for the report's score distributions.</param>
    /// <param name="character">The own character's name (the watcher's --name): the pet's identified species is kept per character.</param>
    /// <param name="ocr">The OCR worker the tag reads go to; null reads them on the frame thread.</param>
    /// <param name="blockingOcr">Wait for each tag read (the worker still reads), so a replay is byte-identical to the frame-thread detector's.</param>
    /// <param name="tagCache">The file the tag renderings read so far are kept in between sessions, loaded now and written by <see cref="SaveTagCache"/>; null keeps none.</param>
    /// <param name="tagFont">The file the tag font learned from confirmed reads is kept in, loaded now and written by <see cref="SaveTagCache"/>; null keeps none.</param>
    /// <param name="useFont">Whether the tag font learns and renders at all; off, every tag is read, as before there was a font.</param>
    public EntityWatch(HudLayout layout, string backgrounds, SessionTimes times, string? sheetDir, long? totalFrames,
        string? mobInfo = null, string? sprites = null, bool diagnostics = false, string? petName = null, string? character = null, string? session = null,
        OcrQueue? ocr = null, bool blockingOcr = false, string? tagCache = null, string? tagFont = null, bool useFont = true)
    {
        _session = session;
        Tags = new TagDetector { Queue = ocr, Blocking = blockingOcr, UseFont = useFont };
        _tagCache = tagCache;
        _tagFont = tagFont;
        if (tagFont is not null)
        {
            try
            {
                if (TagFont.Load(tagFont) is { } font) { Tags.Font = font; Console.WriteLine($"tags     font of {font.Letters.Count} letters from {font.Samples} confirmed reads, from {tagFont}"); }
                else Console.WriteLine($"tags     no font file at {tagFont}; the font is learned from confirmed reads and written there at the end");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"tags     the font file {tagFont} could not be read ({e.Message}); learning anew");
            }
        }
        if (tagCache is not null)
        {
            try
            {
                var loaded = Tags.Load(tagCache);
                Console.WriteLine(loaded > 0 ? $"tags     {loaded} tag renderings with their text from {tagCache}" : $"tags     no cache file at {tagCache}; one is written at the end");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"tags     the cache file {tagCache} could not be read ({e.Message}); starting without it, and it is written anew at the end");
            }
        }
        _tracker.Names.PetName = petName;
        _layout = layout;
        _backgrounds = backgrounds;
        _diagnostics = diagnostics;
        _mobLibrary = mobInfo is null ? null : MobLibrary.Open(mobInfo);
        Console.WriteLine(_mobLibrary is null ? $"entities: no mob data at {mobInfo} (run OakSoul.MobExport --sprites); sprites not verified" : $"entities: mob data from {mobInfo}");
        _drops = sprites is null ? null : DropMatcher.Load(sprites);
        Console.WriteLine(_drops is null ? $"entities: no item sprites at {sprites}; drops not identified" : $"entities: {_drops.Count} drop icons ({_drops.MesoIcons} meso) from {sprites}");
        _damage = sprites is null ? null : DamageReader.Load(sprites);
        Console.WriteLine(_damage is null ? $"entities: no damage digits at {sprites}/effect (run OakSoul.SpriteExport --effects); damage numbers stay effects"
            : $"entities: damage digits {string.Join(", ", _damage.FamilyNames)} ({_damage.GlyphCount} glyphs) from {sprites}/effect");
        // OAKSOUL_CLOUDS=off leaves the clouds out of the comparison (for measuring what they change).
        var cloudsOff = Environment.GetEnvironmentVariable("OAKSOUL_CLOUDS") is "off" or "0";
        _clouds = sprites is null || cloudsOff ? null : CloudArt.Load(sprites);
        Console.WriteLine(_clouds is null ? $"entities: no cloud art at {sprites}/weather (run OakSoul.SpriteExport --clouds){(cloudsOff ? " or OAKSOUL_CLOUDS=off" : "")}; clouds not modelled"
            : $"entities: {_clouds.Sprites.Count} cloud canvases from {sprites}/weather");
        if (sprites is not null)
        {
            _pets = petName is null ? null : SpriteSet.Pets(sprites);
            _cursors = SpriteSet.Cursor(sprites);
            _coins = SpriteSet.Mesos(sprites);
            _skills = SpriteSet.SkillEffects(sprites);
            Console.WriteLine($"entities: {(_pets is null ? "no pet frames" : $"{_pets.Groups.Count} pets' frames")}, {(_cursors is null ? "no cursor" : $"{_cursors.Groups.Count} cursor states")}, {(_coins is null ? "no coin frames" : $"{_coins.Groups.Count} meso coins")}," +
                $" {(_skills is null ? "no skill effects" : $"{_skills.Groups.Count} skill effects ({string.Join(", ", _skills.Groups.Values.Select(g => g.Name).Distinct())})")} from {sprites}" +
                (_pets is null && petName is not null ? " (run OakSoul.SpriteExport --pets --cursor --mesos --skill-effects JOBS)" : ""));
            if (petName is not null && character is not null)
            {
                _petFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sprites)) ?? ".", "pets", $"{character}.json");
                if (LoadPet(_petFile, petName) is { } known && _pets?.Groups.TryGetValue(known.Id, out var species) == true)
                {
                    PetSpecies = species;
                    Console.WriteLine($"pet      {petName} is a {species.Name} ({species.Id}), identified {known.When} (from {_petFile})");
                }
                else Console.WriteLine($"pet      {petName}: species not identified yet; it will be, by an exact match of the pets' frames under its tag");
            }
        }
        _times = times;
        _sheetDir = sheetDir;
        if (sheetDir is not null)
        {
            // Without a known length, take one frame every 30 until the sheets are full.
            var n = totalFrames ?? SheetFrames * 30;
            for (var i = 0; i < SheetFrames; i++) _sheetPicks.Add(1 + (long)Math.Round(i * (n - 1) / (double)(SheetFrames - 1)));
        }
        _debugDir = Environment.GetEnvironmentVariable("OAKSOUL_DEBUG_DUMP");
        _debugFrames = (Environment.GetEnvironmentVariable("OAKSOUL_DEBUG_FRAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
    }

    private readonly SpriteSet? _pets, _cursors, _coins, _skills;
    private readonly string? _petFile;

    /// <summary>The pet's species: identified in an earlier session (kept per character next to the sprites) or in this one.</summary>
    public MobSpecies? PetSpecies { get; private set; }

    /// <summary>The capture sequence and score of this session's identification of the pet, when it happened in this session.</summary>
    public (long Sequence, double Score)? PetIdentifiedNow { get; private set; }

    private static (string Id, string When)? LoadPet(string path, string petName)
    {
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        if (r.GetProperty("pet").GetString() != petName) return null;
        return (r.GetProperty("species").GetString() ?? "", r.TryGetProperty("identified", out var w) ? w.GetString() ?? "" : "");
    }

    /// <summary>Keeps the pet's identification for the character (data/pets/&lt;character&gt;.json), so later sessions know the species from their first frame.</summary>
    private void PetIdentified(MobSpecies species, long sequence, SpriteMatch match, string stamp)
    {
        PetSpecies = species;
        PetIdentifiedNow = (sequence, match.Score);
        foreach (var d in Detectors) d.PetSpecies = species;
        var when = $"on {_session ?? "a live capture"} frame {sequence} ({match.Frame.Key}, {match.Matches}/{match.Compared} pixels)";
        Console.WriteLine($"{stamp}  pet      {_tracker.Names.PetName} is a {species.Name} ({species.Id}), identified {when}");
        if (_petFile is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_petFile)!);
        File.WriteAllText(_petFile, System.Text.Json.JsonSerializer.Serialize(new { pet = _tracker.Names.PetName, species = species.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), speciesName = species.Name, identified = when },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private string _stamp = "";
    private readonly string? _session;

    /// <summary>
    /// Per processed frame: the result plus what the report compares it with. <see cref="Own"/> is the own position
    /// after the camera feedback; <see cref="OwnRaw"/> and <see cref="OwnSnapped"/> are the world tracker's before it
    /// (dot and tag, before and after snapping onto a foothold), <see cref="OwnRefined"/> the registered camera plus
    /// the tag's soles (null without a registration or a tag).
    /// </summary>
    public sealed record Row(int MapId, EntityFrame Result, long? Exp, PointD? Own, bool OwnTag, PointD? OwnRaw = null, PointD? OwnSnapped = null,
        PointD? OwnRefined = null, WorldSource Source = WorldSource.None)
    {
        /// <summary>The frame's capture sequence and the HP the status bar read, for the report's damage cross-check.</summary>
        public long Sequence { get; init; }
        public long? Hp { get; init; }
    }

    public List<Row> Rows { get; } = [];
    public EntityDetector? Detector => _detector;
    public EntityTracker Tracker => _tracker;

    /// <summary>The tag detector every map's detector shares: its cache of tag renderings and their text, and its OCR counters.</summary>
    public TagDetector Tags { get; }

    /// <summary>Writes the tag renderings read so far with their text to the cache file given at construction, if any.</summary>
    public void SaveTagCache()
    {
        if (_tagCache is not null)
        {
            var n = Tags.Save(_tagCache);
            Console.WriteLine($"tags     {n} tag renderings with their text written to {_tagCache} ({Math.Max(0, n - Tags.Loaded)} new in this session)");
        }
        if (_tagFont is not null && Tags.Font.Samples > 0)
        {
            Tags.Font.Save(_tagFont);
            Console.WriteLine($"tags     font of {Tags.Font.Letters.Count} letters from {Tags.Font.Samples} confirmed reads written to {_tagFont} ({Tags.FontLearned} learned in this session)");
        }
    }
    /// <summary>Every detector that ran, one per map with a background.</summary>
    public IEnumerable<EntityDetector> Detectors => _detectors.Values.OfType<EntityDetector>();
    public IReadOnlyDictionary<int, MapMobSet> MobSets => _mobSets;
    /// <summary>The foothold index of every map a detector ran on.</summary>
    public Dictionary<int, FootholdIndex> Footholds { get; } = [];
    public MobLibrary? MobLibrary => _mobLibrary;
    public DropMatcher? Drops => _drops;
    public DamageReader? Damage => _damage;
    public List<string> SheetPaths { get; } = [];

    /// <summary>Frames not given to the detector because they are not the game's own pixels, by reason.</summary>
    public Dictionary<string, int> Skipped { get; } = [];

    /// <summary>
    /// The capture sequence of a frame the detector ran on, by the frame id the detector was given (its position in the
    /// replay, <see cref="Frame.Index"/>), for printing; the id itself when unknown.
    /// </summary>
    public long SequenceOf(long index) => _sequences.TryGetValue(index, out var seq) ? seq : index;
    private readonly Dictionary<long, long> _sequences = [];

    /// <summary>The detector's result for the last frame given to <see cref="Update"/>, or null when it did not run on it.</summary>
    public EntityFrame? LastResult { get; private set; }

    /// <summary>Why the detector did not run on the last frame (null when it ran).</summary>
    public string? LastSkip { get; private set; }

    /// <summary>The tag boxes (viewport coordinates) and the own-character box of the last frame the detector ran on.</summary>
    public IReadOnlyList<TagBox> LastTags => LastResult is null ? [] : _detector?.LastTags ?? [];
    public CaptureRect? LastOwnBox => LastResult is null ? null : _detector?.LastOwnBox;

    /// <summary>
    /// Runs the detector on a frame; returns the world state refined by the registered camera (or the one given). The
    /// detector compares sprites pixel for pixel, so a frame that is not the game's own pixels is skipped, with
    /// <paramref name="notExact"/> the reason (a scaled client, a lossy video, a point-size capture): counted in
    /// <see cref="Skipped"/> and printed the first time.
    /// </summary>
    public WorldState? Update(Frame frame, FrameGeometry geometry, WorldState? place, WorldTracker? world, NameTagHit? tag, CaptureRect? panel, StatusReading status, string? ownName, string stamp,
        string? notExact = null)
    {
        LastResult = null;
        LastSkip = null;
        if (notExact is not null)
        {
            var n = Skipped[notExact] = Skipped.GetValueOrDefault(notExact) + 1;
            if (n == 1) Console.WriteLine($"{stamp}  entities skip frames: {notExact}");
            LastSkip = notExact;
            return place;
        }
        if (!geometry.IsNative || place is not { MapId: { } mapId, Map: { } map } || world?.Index is not { } index)
        {
            LastSkip = !geometry.IsNative ? "not native" : "no map";
            return place;
        }
        if (_mapId != mapId)
        {
            _tracker.Flush();
            _mapId = mapId;
            if (!_detectors.TryGetValue(mapId, out _detector))
            {
                var mosaic = BackgroundMosaic.Load(_backgrounds, mapId);
                MapMobSet? mobs = null;
                if (_mobLibrary is not null && !_mobSets.TryGetValue(mapId, out mobs))
                {
                    mobs = _mobSets[mapId] = MapMobSet.Build(map, index, _mobLibrary);
                    Console.WriteLine($"entities: map {mapId} mob templates: {(mobs.Entries.Count == 0 ? "none" : string.Join(", ", mobs.Entries.Select(e => $"{e.Species.Name} x{e.Spawns} ({e.Species.Mobility.ToString().ToLowerInvariant()}, {e.Species.Frames.Count} frames, floors {string.Join("/", e.Floors)})")))}{(mobs.Missing.Count > 0 ? $"; no data for {string.Join(", ", mobs.Missing)}" : "")}");
                }
                _detector = mosaic is null ? null : new EntityDetector(mosaic, _layout, _tracker, mobs, _drops, _damage, Tags)
                {
                    SpriteDiagnostics = _diagnostics,
                    Pets = _pets, Cursors = _cursors, Coins = _coins, SkillEffects = _skills, PetSpecies = PetSpecies,
                    PetIdentified = (species, seq, m) => PetIdentified(species, seq, m, _stamp),
                    // OAKSOUL_TEMPORAL=off runs the full search alone on every frame; OAKSOUL_TEMPORAL_FULL_EVERY=N sets how often it runs.
                    Temporal = Environment.GetEnvironmentVariable("OAKSOUL_TEMPORAL") is not ("off" or "0"),
                    FullEvery = int.TryParse(Environment.GetEnvironmentVariable("OAKSOUL_TEMPORAL_FULL_EVERY"), out var every) && every > 0 ? every : EntityDetector.DefaultFullEvery,
                    // The gap filler searches the floor band a moving camera leaves Ignored with the slice matcher (on by default;
                    // OAKSOUL_GAP_FILLER=off turns it off); OAKSOUL_GAP_FILLER_BUDGET_MS sets its time per frame, OAKSOUL_GAP_FINDS_DIR
                    // collects crops of the finds that start tracks.
                    GapFiller = Environment.GetEnvironmentVariable("OAKSOUL_GAP_FILLER") is not ("off" or "0"),
                    GapFillerBudgetMs = double.TryParse(Environment.GetEnvironmentVariable("OAKSOUL_GAP_FILLER_BUDGET_MS"), System.Globalization.CultureInfo.InvariantCulture, out var budget) && budget > 0 ? budget : 2.0,
                    GapFindsDir = Environment.GetEnvironmentVariable("OAKSOUL_GAP_FINDS_DIR") is { Length: > 0 } gapDir ? gapDir : null,
                    // A map with info/cloud draws drifting clouds over its mobs; the matcher sees through them.
                    Clouds = map.Cloud && _clouds is not null ? new CloudLayer(_clouds, map.Vr.Left, map.Vr.Right) : null,
                };
                _detector?.Prepare();
                if (_detector?.Clouds is not null) Console.WriteLine($"entities: map {mapId} has clouds (info/cloud); following them");
                _detectors[mapId] = _detector;
                Console.WriteLine(mosaic is null ? $"entities: no background for map {mapId} in {_backgrounds} (build it with --build-background)"
                    : $"entities: background {mapId} loaded from {_backgrounds} ({mosaic.Width}x{mosaic.Height}, {mosaic.Frames} frames)");
            }
            _tracker.MapId = mapId;
            // Clouds seen on an earlier visit have drifted on.
            _detector?.Clouds?.Clear();
        }
        if (_detector is null)
        {
            LastSkip = $"no background for map {mapId}";
            return place;
        }
        Footholds[mapId] = index;
        _tracker.Names.OwnName ??= ownName;
        var time = _times.TimeOf(frame);
        // The detector counts frames by their position in the replay (Frame.Index), which is unique even where the recorder
        // filled an idle gap by repeating a captured frame (a filled video frame carries the sequence of the frame it repeats).
        var ctx = new DetectionContext(frame.Index, time, map, index, place.Camera, place.Source == WorldSource.DotAndTag, tag?.Tag, place.Position, panel,
            frame.Sequence, status.ExpValue?.Total, frame.Pts);
        _sequences[frame.Index] = frame.Sequence;
        _detector.DebugDir = _debugDir is not null && _debugFrames.Contains(frame.Index) ? _debugDir : null;
        _stamp = stamp;
        var result = _detector.Detect(frame.Pixels, frame.Width, ctx);
        LastResult = result;
        // Camera feedback: the registered camera is exact, so the own position follows from it and the tag.
        var refined = place;
        PointD? ownRefined = null;
        if (result.Camera is { } cam)
        {
            var tagFeet = tag is { } t ? new PointD(t.Feet.X, t.Feet.Y) : (PointD?)null;
            refined = world.Refine(new PointD(cam.X, cam.Y), tagFeet);
            if (tagFeet is not null) ownRefined = refined.Position;
        }
        Rows.Add(new Row(mapId, result, status.ExpValue?.Total, refined.Position, tag is not null, place.RawPosition, place.Position, ownRefined, place.Source)
        {
            Sequence = frame.Sequence, Hp = status.HpValue?.Current,
        });
        if (!_petWarned && _tracker.Names.PetMismatch is { } mismatch)
        {
            _petWarned = true;
            Console.WriteLine($"{stamp}  warning: {mismatch}");
        }
        if (_detector.DebugDir is not null) Console.WriteLine($"{stamp}  debug frame {frame.Index} (sequence {frame.Sequence}): {result.Note} reg {result.Registration?.Score:0.000} cam {result.Camera} {result.Milliseconds:0.0} ms");
        // Sheet frames are picked by position in the replay and captioned with their capture sequence.
        if (_sheetDir is not null && _sheetPicks.Contains(frame.Index) && _tiles.Count < SheetFrames)
            _tiles.Add((frame.Sequence, time, _detector.Annotate(frame.Pixels, frame.Width, result, foreground: false, half: true)));

        var summary = Summary(result, refined.Position);
        if (summary != _lastSummary)
        {
            Console.WriteLine($"{stamp}  entities {summary}");
            _lastSummary = summary;
        }
        return refined;
    }

    /// <summary>
    /// The gap filler's report (OAKSOUL_GAP_FILLER): per moving frame its time against the budget, the share of the blind band's
    /// slices it searched, its finds and what the tracker made of them; nothing when it was off.
    /// </summary>
    public void PrintGapFiller(TextWriter o)
    {
        var detectors = Detectors.Where(d => d.GapFiller).ToList();
        if (detectors.Count == 0) return;
        static double Pct(List<double> v, double p)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            return s[(int)Math.Clamp(Math.Round(p / 100 * (s.Count - 1)), 0, s.Count - 1)];
        }
        var frames = detectors.SelectMany(d => d.GapFrames).ToList();
        var budget = detectors[0].GapFillerBudgetMs;
        var ms = frames.Select(g => g.Ms).ToList();
        var withBand = frames.Where(g => g.Available > 0).ToList();
        var coverage = withBand.Select(g => (double)g.Searched / g.Available).ToList();
        o.WriteLine($"== gap filler (budget {budget:0.0} ms per moving frame) ==");
        o.WriteLine($"  moving frames {frames.Count}, still frames {detectors.Sum(d => d.GapStillFrames)}; time per moving frame: median {Pct(ms, 50):0.00} ms, p90 {Pct(ms, 90):0.00}, max {(ms.Count == 0 ? 0 : ms.Max()):0.00}," +
                    $" over budget on {ms.Count(m => m > budget)} of {ms.Count}; total {detectors.Sum(d => d.GapFillerMs):0} ms");
        o.WriteLine($"    of which (median per moving frame): masks and slicing {Pct(frames.Select(g => g.PrepMs).ToList(), 50):0.00} ms (max {frames.Select(g => g.PrepMs).DefaultIfEmpty(0).Max():0.00})," +
                    $" mob searches {Pct(frames.Select(g => g.MobMs).ToList(), 50):0.00}, coin searches {Pct(frames.Select(g => g.MesoMs).ToList(), 50):0.00}");
        o.WriteLine($"  blind slices: on {withBand.Count} of {frames.Count} moving frames; available per such frame median {Pct(withBand.Select(g => (double)g.Available).ToList(), 50):0}, mob search median {Pct(withBand.Select(g => (double)g.Searched).ToList(), 50):0} (p10 {Pct(withBand.Select(g => (double)g.Searched).ToList(), 10):0});" +
                    $" mob coverage (searched / available) median {Pct(coverage, 50):0.0000}, p90 {Pct(coverage, 90):0.0000}, max {(coverage.Count == 0 ? 0 : coverage.Max()):0.0000}; total {frames.Sum(g => (long)g.Searched):N0} of {frames.Sum(g => (long)g.Available):N0}");
        o.WriteLine($"  finds {frames.Sum(g => g.Finds)} on {frames.Count(g => g.Finds > 0)} frames: {frames.Sum(g => g.Duplicates)} already shown by a candidate, {frames.Sum(g => g.Attached)} verified an unmatched blob, {frames.Sum(g => g.Added)} added;" +
                    $" of the {frames.Sum(g => g.Attached + g.Added)} passed on, {frames.Sum(g => g.NewTracks)} started a new track, {frames.Sum(g => g.MatchedTracks)} joined a track that existed, {frames.Sum(g => g.Absorbed)} absorbed by the tracker into another candidate");
        o.WriteLine($"  slice searches: mobs {string.Join("; ", detectors.Select(d => d.GapMobStats))}; coins {string.Join("; ", detectors.Select(d => d.GapMesoStats))}");
        var mesoCoverage = withBand.Select(g => (double)g.MesoSearched / g.Available).ToList();
        o.WriteLine($"  coin search: slices per such frame median {Pct(withBand.Select(g => (double)g.MesoSearched).ToList(), 50):0}, coverage median {Pct(mesoCoverage, 50):0.0000}, p90 {Pct(mesoCoverage, 90):0.0000};" +
                    $" total {frames.Sum(g => (long)g.MesoSearched):N0}; one resolve over both searches' finds per frame: median {Pct(frames.Select(g => g.ResolveMs).ToList(), 50):0.00} ms, max {frames.Select(g => g.ResolveMs).DefaultIfEmpty(0).Max():0.00};" +
                    $" searches stopped before their first slice: {detectors.Sum(d => d.GapStalls)}");
        var moving = frames.Select(g => g.DetectorMs).ToList();
        var movingSet = frames.Select(g => g.Frame).ToHashSet();
        var still = Rows.Where(r => r.Result.Camera is not null && !movingSet.Contains(r.Result.Frame)).Select(r => r.Result.Milliseconds).ToList();
        o.WriteLine($"  detector ms per frame (gap filler included): moving frames median {Pct(moving, 50):0.0}, p90 {Pct(moving, 90):0.0}; other detected frames median {Pct(still, 50):0.0}, p90 {Pct(still, 90):0.0}");
        foreach (var nt in detectors.SelectMany(d => d.GapNewTracks))
            o.WriteLine($"    new track #{nt.TrackId} ({nt.Kind.ToString().ToLowerInvariant()}) seq {nt.Sequence}: {nt.Name} {nt.FrameKey} box {nt.Box.Width}x{nt.Box.Height}+{nt.Box.X}+{nt.Box.Y}, distance {nt.Distance:0.000}");
    }

    /// <summary>The skipped frames by reason, as one line.</summary>
    public string SkipSummary() => Skipped.Count == 0 ? "entities: no frame skipped (every frame was the game's own pixels)"
        : $"entities: skipped {Skipped.Values.Sum()} frames that are not the game's own pixels: {string.Join("; ", Skipped.Select(s => $"{s.Value} {s.Key}"))}";

    /// <summary>Counts by kind and the nearest mob with its tag and distance from the own character.</summary>
    private static string Summary(EntityFrame r, PointD? own)
    {
        if (r.Note is { } note && r.Entities.Count == 0) return note;
        var counts = string.Join(" ", new[] { EntityKind.Mob, EntityKind.Player, EntityKind.Pet, EntityKind.Drop, EntityKind.Effect, EntityKind.Unknown }
            .Select(k => $"{k.ToString().ToLowerInvariant()} {r.Count(k)}"));
        var nearest = own is { } o ? r.Entities.Where(e => e.Kind == EntityKind.Mob).OrderBy(e => e.World.DistanceTo(o)).FirstOrDefault() : null;
        var mob = nearest is null ? "" : $"  nearest mob #{nearest.TrackId}{(nearest.Name is null ? "" : $" '{(nearest.Level is { } lv ? lv > 0 ? $"Lv. {lv} " : "Lv. ? " : "")}{nearest.Name}'")}{(nearest.Identity is { } id ? $" [{id}]" : "")} at {nearest.World.DistanceTo(own!.Value):0} units";
        var species = r.Entities.Where(e => e.Kind == EntityKind.Mob && e.Identity is not null).GroupBy(e => e.Identity!.Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        var ids = string.Join(", ", species);
        return counts + (ids.Length > 0 ? $"  ({ids})" : "") + mob;
    }

    /// <summary>Composes the collected tiles into contact sheets, six frames each, with a legend.</summary>
    public void WriteSheets()
    {
        if (_sheetDir is null || _tiles.Count == 0) return;
        Directory.CreateDirectory(_sheetDir);
        var tileW = _tiles[0].Tile.Width;
        var tileH = _tiles[0].Tile.Height;
        const int caption = 14, legend = 18, cols = 2;
        for (var sheet = 0; sheet * FramesPerSheet < _tiles.Count; sheet++)
        {
            var tiles = _tiles.Skip(sheet * FramesPerSheet).Take(FramesPerSheet).ToList();
            var rows = (tiles.Count + cols - 1) / cols;
            var canvas = new Canvas(cols * tileW, legend + rows * (tileH + caption));
            var x = 4;
            foreach (var kind in Enum.GetValues<EntityKind>())
            {
                var name = kind.ToString();
                canvas.Fill(x, 5, 10, 8, EntityDetector.KindColour(kind));
                canvas.Text(x + 14, 5, name, 0xFFFFFFFF, 1, null);
                x += 14 + Canvas.TextWidth(name) + 12;
            }
            canvas.Text(x, 5, "cyan: tag box  white: own character (masked)  label: #track name anim/frame L|R (~ carried by the track, ? unverified)", 0xFFFFFFFF, 1, null);
            for (var i = 0; i < tiles.Count; i++)
            {
                var (frame, time, tile) = tiles[i];
                int tx = i % cols * tileW, ty = legend + i / cols * (tileH + caption);
                canvas.Text(tx + 4, ty + 3, $"frame {frame}  t={time:0.0}s", 0xFFFFFFFF, 1, null);
                canvas.Paste(tile, tx, ty + caption);
            }
            var path = Path.Combine(_sheetDir, $"entity-sheet-{sheet + 1}.png");
            canvas.Save(path);
            SheetPaths.Add(Path.GetFullPath(path));
        }
        Console.WriteLine($"entity sheets: {string.Join(", ", SheetPaths)}");
    }
}
