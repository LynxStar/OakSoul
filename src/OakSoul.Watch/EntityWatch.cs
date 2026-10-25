using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.Vision;
using OakSoul.Vision.Readers;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// The watcher's entity stage: loads the cached background of the current map, runs the detector on every native
/// frame, prints a summary line whenever it changes, collects annotated frames for the contact sheets and keeps
/// what the entity report needs.
/// </summary>
public sealed class EntityWatch
{
    /// <summary>Frames on the contact sheets, evenly spaced over the session, six to a sheet.</summary>
    public const int SheetFrames = 12, FramesPerSheet = 6;

    private readonly HudLayout _layout;
    private readonly string _backgrounds;
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
    public EntityWatch(HudLayout layout, string backgrounds, SessionTimes times, string? sheetDir, long? totalFrames)
    {
        _layout = layout;
        _backgrounds = backgrounds;
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

    /// <summary>Per processed frame: the result plus what the report compares it with.</summary>
    public sealed record Row(EntityFrame Result, long? Exp, PointD? Own, bool OwnTag);

    public List<Row> Rows { get; } = [];
    public EntityDetector? Detector => _detector;
    public List<string> SheetPaths { get; } = [];

    public void Update(Frame frame, FrameGeometry geometry, WorldState? place, WorldTracker? world, NameTagHit? tag, CaptureRect? panel, StatusReading status, string? ownName, string stamp)
    {
        if (!geometry.IsNative || place is not { MapId: { } mapId, Map: { } map } || world?.Index is not { } index) return;
        if (_mapId != mapId)
        {
            _mapId = mapId;
            _detector?.Tracker.Flush();
            var mosaic = BackgroundMosaic.Load(_backgrounds, mapId);
            _detector = mosaic is null ? null : new EntityDetector(mosaic, _layout);
            Console.WriteLine(mosaic is null ? $"entities: no background for map {mapId} in {_backgrounds} (build it with --build-background)" : $"entities: background {mapId} loaded ({mosaic.Width}x{mosaic.Height}, {mosaic.Frames} frames)");
        }
        if (_detector is null) return;
        _detector.Tracker.Names.OwnName ??= ownName;
        var time = _times.TimeOf(frame);
        var ctx = new DetectionContext(frame.Sequence, time, map, index, place.Camera, place.Source == WorldSource.DotAndTag, tag?.Tag, place.Position, panel);
        _detector.DebugDir = _debugDir is not null && _debugFrames.Contains(frame.Sequence) ? _debugDir : null;
        var result = _detector.Detect(frame.Pixels, frame.Width, ctx);
        Rows.Add(new Row(result, status.ExpValue?.Total, place.Position, tag is not null));
        if (_detector.DebugDir is not null) Console.WriteLine($"{stamp}  debug frame {frame.Sequence}: {result.Note} reg {result.Registration?.Score:0.000} cam {result.Camera} {result.Milliseconds:0.0} ms");
        if (_sheetDir is not null && _sheetPicks.Contains(frame.Sequence) && _tiles.Count < SheetFrames)
            _tiles.Add((frame.Sequence, time, _detector.Annotate(frame.Pixels, frame.Width, result, foreground: false, half: true)));

        var summary = Summary(result, place.Position);
        if (summary != _lastSummary)
        {
            Console.WriteLine($"{stamp}  entities {summary}");
            _lastSummary = summary;
        }
    }

    /// <summary>Counts by kind and the nearest mob with its tag and distance from the own character.</summary>
    private static string Summary(EntityFrame r, PointD? own)
    {
        if (r.Note is { } note && r.Entities.Count == 0) return note;
        var counts = string.Join(" ", new[] { EntityKind.Mob, EntityKind.Player, EntityKind.Pet, EntityKind.Drop, EntityKind.Effect, EntityKind.Unknown }
            .Select(k => $"{k.ToString().ToLowerInvariant()} {r.Count(k)}"));
        var nearest = own is { } o ? r.Entities.Where(e => e.Kind == EntityKind.Mob).OrderBy(e => e.World.DistanceTo(o)).FirstOrDefault() : null;
        var mob = nearest is null ? "" : $"  nearest mob #{nearest.TrackId}{(nearest.Name is null ? "" : $" '{(nearest.Level is { } lv ? lv > 0 ? $"Lv. {lv} " : "Lv. ? " : "")}{nearest.Name}'")} at {nearest.World.DistanceTo(own!.Value):0} units";
        return counts + mob;
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
            canvas.Text(x, 5, "cyan: tag box  white: own character (masked)", 0xFFFFFFFF, 1, null);
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
