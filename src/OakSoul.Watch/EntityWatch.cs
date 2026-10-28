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
    private readonly bool _diagnostics;
    private bool _petWarned;
    private readonly EntityTracker _tracker = new();
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
    public EntityWatch(HudLayout layout, string backgrounds, SessionTimes times, string? sheetDir, long? totalFrames,
        string? mobInfo = null, string? sprites = null, bool diagnostics = false, string? petName = null)
    {
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
                _detector = mosaic is null ? null : new EntityDetector(mosaic, _layout, _tracker, mobs, _drops, _damage) { SpriteDiagnostics = _diagnostics };
                _detectors[mapId] = _detector;
                Console.WriteLine(mosaic is null ? $"entities: no background for map {mapId} in {_backgrounds} (build it with --build-background)"
                    : $"entities: background {mapId} loaded from {_backgrounds} ({mosaic.Width}x{mosaic.Height}, {mosaic.Frames} frames)");
            }
            _tracker.MapId = mapId;
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
            frame.Sequence, status.ExpValue?.Total);
        _sequences[frame.Index] = frame.Sequence;
        _detector.DebugDir = _debugDir is not null && _debugFrames.Contains(frame.Index) ? _debugDir : null;
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
