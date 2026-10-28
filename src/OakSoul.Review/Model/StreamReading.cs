using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OakSoul.Overlay;

namespace OakSoul.Review.Model;

/// <summary>A frame line as read, with the top-level fields this build does not know (raw JSON), for the inspector.</summary>
public sealed record ParsedFrame(OverlayFrame Frame, IReadOnlyList<KeyValuePair<string, string>>? Extras);

/// <summary>
/// Reads overlay stream lines tolerantly: what <see cref="OverlayJson"/> reads is read the same way (a number with no
/// fraction or exponent is a long, arrays of integers are int[] and int[][]), and what it would reject is kept instead
/// of failing the stream: an item field of another JSON shape is kept as its raw JSON text, an unknown top-level frame
/// field goes to <see cref="ParsedFrame.Extras"/>, and a header of another schema is read with a warning. So a stream
/// written by a newer pipeline (a field such as how an entity was found) still opens, and the new fields show.
/// </summary>
public static class TolerantJson
{
    private static readonly HashSet<string> FrameKeys = ["seq", "pts", "filled", "t", "map", "camera", "detect", "skip", "items"];

    public static (OverlayHeader Header, IReadOnlyList<KeyValuePair<string, string>> Fields, string? Problem) ParseHeader(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var schema = S("schema");
        string? problem = schema is null ? "the first line is not a header (no schema)"
            : schema != OverlayHeader.CurrentSchema ? $"schema {schema}; this build reads {OverlayHeader.CurrentSchema}" : null;
        int width = 0, height = 0;
        if (r.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Array && frame.GetArrayLength() == 2)
        {
            frame[0].TryGetInt32(out width);
            frame[1].TryGetInt32(out height);
        }
        var th = r.TryGetProperty("thresholds", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
        double T(string name) => th.ValueKind == JsonValueKind.Object && th.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
        var layers = r.TryGetProperty("layers", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).ToList()
            : new List<string>();
        var header = new OverlayHeader(schema ?? "", S("producer") ?? "", S("commit"), S("session"), S("recording"), S("replay"), S("source") ?? "",
            width, height, S("name"), T("sprite"), T("drop"), T("coin"), layers, S("pet"));
        var fields = r.EnumerateObject().Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText())).ToList();
        return (header, fields, problem);
    }

    public static ParsedFrame ParseFrame(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        var items = new List<OverlayItem>();
        if (r.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var item = new OverlayItem(Text(e, "layer") ?? "?", Text(e, "kind") ?? "?");
                foreach (var p in e.EnumerateObject())
                    if (p.Name is not ("layer" or "kind")) item.Set(p.Name, ReadValue(p.Value));
                items.Add(item);
            }
        List<KeyValuePair<string, string>>? extras = null;
        foreach (var p in r.EnumerateObject())
            if (!FrameKeys.Contains(p.Name)) (extras ??= []).Add(new(p.Name, p.Value.GetRawText()));
        (int, int)? camera = r.TryGetProperty("camera", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() == 2 && c[0].TryGetInt32(out var cx) && c[1].TryGetInt32(out var cy) ? (cx, cy) : null;
        var frame = new OverlayFrame
        {
            Sequence = r.GetProperty("seq").GetInt64(),
            Pts = r.GetProperty("pts").GetDouble(),
            Filled = r.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.True,
            Time = r.GetProperty("t").GetDouble(),
            MapId = r.TryGetProperty("map", out var m) && m.TryGetInt32(out var map) ? map : null,
            Camera = camera,
            Detect = Text(r, "detect"),
            Skip = Text(r, "skip"),
            Items = items,
        };
        return new ParsedFrame(frame, extras);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsInteger(JsonElement v, out long value)
    {
        value = 0;
        var raw = v.GetRawText();
        return v.TryGetInt64(out value) && !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E');
    }

    private static object? ReadValue(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number: return IsInteger(v, out var l) ? l : v.GetDouble();
            case JsonValueKind.String: return v.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Array:
                if (Ints(v) is { } ints) return ints;
                if (v.EnumerateArray().All(p => p.ValueKind == JsonValueKind.Array) && v.EnumerateArray().Select(Ints).ToArray() is var rows && rows.All(x => x is not null))
                    return rows!;
                return v.GetRawText();
            default: return v.GetRawText();
        }
    }

    private static int[]? Ints(JsonElement array)
    {
        var result = new int[array.GetArrayLength()];
        var i = 0;
        foreach (var x in array.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !IsInteger(x, out var l) || l is < int.MinValue or > int.MaxValue) return null;
            result[i++] = (int)l;
        }
        return result;
    }

    /// <summary>The lines of a whole stream file (gzip or plain).</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(input, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }
}

/// <summary>Evidence of a track in one frame, as the Tracks tab and the inspector's strip colour it.</summary>
public enum Evidence : byte { None, Matched, Carried, Unverified, Present }

/// <summary>A track's presence in one frame (timeline index).</summary>
public readonly record struct TrackCell(int Index, Evidence Evidence, bool Dying);

/// <summary>A track across the stream: its kind and name (the most frequent), first and last frame, and every frame it is in.</summary>
public sealed class TrackInfo(long id)
{
    private readonly Dictionary<string, int> _kinds = [], _names = [];

    public long Id { get; } = id;
    public string Kind { get; private set; } = "unknown";
    public string? Name { get; private set; }
    public int First { get; private set; } = int.MaxValue;
    public int Last { get; private set; } = -1;
    public List<TrackCell> Cells { get; } = [];
    public int Matched { get; private set; }

    public int Span => Last - First + 1;

    internal void Add(int index, OverlayItem e)
    {
        var evidence = e.Str("evidence") switch
        {
            "matched" => Evidence.Matched,
            "carried" => Evidence.Carried,
            "unverified" => Evidence.Unverified,
            _ => Evidence.Present,
        };
        if (evidence == Evidence.Matched) Matched++;
        Cells.Add(new TrackCell(index, evidence, e.Str("state") == "dying"));
        First = Math.Min(First, index);
        Last = Math.Max(Last, index);
        Kind = Count(_kinds, e.Kind) ?? Kind;
        if (e.Str("name") is { } name) Name = Count(_names, name);
    }

    private static string? Count(Dictionary<string, int> counts, string value)
    {
        counts[value] = counts.GetValueOrDefault(value) + 1;
        return counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>The cell of a frame, by binary search (cells are added in frame order).</summary>
    public TrackCell? At(int index)
    {
        int lo = 0, hi = Cells.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Cells[mid].Index == index) return Cells[mid];
            if (Cells[mid].Index < index) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    public string Title => $"#{Id} {(Name is null ? OverlayLabels.Title(Kind) : $"{Name}")}";
}

/// <summary>A map change: the frame where the stream's map id became another.</summary>
public sealed record MapChange(int Index, int? From, int To);

/// <summary>
/// A stream of one recording as it is read (all at once, or while the watcher writes it), its frames placed on the
/// recording's <see cref="Timeline"/>, and what the tool derives from them: kills (EXP gains, as <c>bake windows</c>
/// counts them), map changes and tracks. Changed only on the UI thread.
/// </summary>
public sealed class ReviewStream
{
    private readonly Timeline _timeline;
    private OverlayFrame?[] _byEntry;
    private readonly Dictionary<int, IReadOnlyList<KeyValuePair<string, string>>> _extras = [];
    private int _next;
    private long? _lastExp;
    private int? _lastExpMap, _lastMap;

    public ReviewStream(string path, Timeline timeline)
    {
        Path = path;
        _timeline = timeline;
        _byEntry = new OverlayFrame?[timeline.Count];
    }

    public string Path { get; }
    public OverlayHeader? Header { get; private set; }
    public IReadOnlyList<KeyValuePair<string, string>> HeaderFields { get; private set; } = [];
    public string? HeaderProblem { get; private set; }

    /// <summary>Frames read so far, in stream order.</summary>
    public int Count { get; private set; }

    /// <summary>Lines that could not be read (counted, skipped).</summary>
    public int BadLines { get; private set; }

    /// <summary>The first timeline index not yet covered by the stream's contiguous prefix: everything before it is processed.</summary>
    public int ProcessedUpTo { get; private set; }

    public bool Complete { get; set; }

    /// <summary>Frames whose (sequence, filled) did not follow the timeline and were placed by sequence instead.</summary>
    public int Misplaced { get; private set; }

    /// <summary>Frames that fit nowhere on the timeline (skipped).</summary>
    public int Unplaced { get; private set; }

    public List<int> Kills { get; } = [];
    public List<MapChange> MapChanges { get; } = [];
    public Dictionary<long, TrackInfo> Tracks { get; } = [];

    public void SetHeader(OverlayHeader header, IReadOnlyList<KeyValuePair<string, string>> fields, string? problem)
    {
        Header = header;
        HeaderFields = fields;
        HeaderProblem = problem;
    }

    public void CountBadLine() => BadLines++;

    public OverlayFrame? At(int index) => index >= 0 && index < _byEntry.Length ? _byEntry[index] : null;

    public IReadOnlyList<KeyValuePair<string, string>>? ExtrasAt(int index) => _extras.GetValueOrDefault(index);

    /// <summary>Places a frame on the timeline and updates kills, map changes and tracks.</summary>
    public void Add(ParsedFrame parsed)
    {
        var f = parsed.Frame;
        var index = Place(f);
        if (index < 0) { Unplaced++; return; }
        if (index >= _byEntry.Length) Array.Resize(ref _byEntry, Math.Max(_timeline.Count, index + 1));
        _byEntry[index] = f;
        if (parsed.Extras is { Count: > 0 } extras) _extras[index] = extras;
        Count++;
        while (ProcessedUpTo < _byEntry.Length && _byEntry[ProcessedUpTo] is not null) ProcessedUpTo++;

        if (f.MapId is { } map)
        {
            if (_lastMap is { } last && last != map) MapChanges.Add(new MapChange(index, last, map));
            _lastMap = map;
        }
        if (!f.Filled)
        {
            // An EXP gain on the same map is a kill (bake windows' rule); the total resets on a map change.
            var exp = f.Layer(OverlayLayers.Hud).FirstOrDefault(i => i.Str("name") == "status.exp")?.Long("exp");
            if (f.MapId != _lastExpMap) { _lastExp = null; _lastExpMap = f.MapId; }
            if (exp is { } e1 && _lastExp is { } e0 && e1 > e0) Kills.Add(index);
            if (exp is not null) _lastExp = exp;
        }
        foreach (var e in f.Layer(OverlayLayers.Entity))
        {
            if (e.Long("track") is not { } id) continue;
            if (!Tracks.TryGetValue(id, out var track)) Tracks[id] = track = new TrackInfo(id);
            track.Add(index, e);
        }
    }

    private int Place(OverlayFrame f)
    {
        var at = _next;
        if (at < _timeline.Count && _timeline[at] is var t && t.Sequence == f.Sequence && t.Filled == f.Filled) { _next = at + 1; return at; }
        if (!f.Filled && _timeline.IndexOf(f.Sequence) is var bySeq and >= 0) { Misplaced++; _next = bySeq + 1; return bySeq; }
        if (f.Filled && _timeline.Video && at >= _timeline.Count)
        {
            // A trailing fill after the last recorded video frame.
            _timeline.ExtendWithFill(f.Sequence, f.Pts, f.Time);
            _next = _timeline.Count;
            return _timeline.Count - 1;
        }
        return -1;
    }
}
