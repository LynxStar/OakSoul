using System.Globalization;

namespace OakSoul.Overlay;

/// <summary>
/// One thing drawn or read in a frame: its <see cref="Layer"/>, its <see cref="Kind"/> (a word: mob, tag, foothold, ...)
/// and named fields in a fixed order. Geometry is integer game pixels of the native frame: <c>rect</c> [x, y, w, h],
/// points [x, y] (<c>feet</c>, <c>point</c>), <c>line</c> a list of points, <c>label</c> the text to draw. World
/// coordinates are map units. Field values are long, double, string, bool, int[] or int[][]; null values are left out.
/// The fields each layer and kind may carry are listed in <see cref="OverlayLayers"/>.
/// </summary>
public sealed class OverlayItem
{
    private readonly List<KeyValuePair<string, object>> _fields = [];

    public OverlayItem(string layer, string kind)
    {
        Layer = layer;
        Kind = kind;
    }

    public string Layer { get; }
    public string Kind { get; }
    public IReadOnlyList<KeyValuePair<string, object>> Fields => _fields;

    /// <summary>Adds a field (in order); a null value is skipped. Integers are stored as long, rects and points as int[].</summary>
    public OverlayItem Set(string name, object? value)
    {
        switch (value)
        {
            case null: return this;
            case int i: _fields.Add(new(name, (long)i)); break;
            case long or double or string or bool or int[] or int[][]: _fields.Add(new(name, value)); break;
            default: throw new ArgumentException($"overlay field {name}: unsupported type {value.GetType().Name}");
        }
        return this;
    }

    public object? Get(string name)
    {
        foreach (var f in _fields) if (f.Key == name) return f.Value;
        return null;
    }

    public bool Has(string name) => Get(name) is not null;
    public string? Str(string name) => Get(name) as string;
    public long? Long(string name) => Get(name) switch { long l => l, double d when d == Math.Floor(d) => (long)d, _ => null };
    public int? Int(string name) => Long(name) is { } l ? (int)l : null;
    public double? Double(string name) => Get(name) switch { long l => l, double d => d, _ => null };
    public bool Bool(string name) => Get(name) is true;
    public int[]? Ints(string name) => Get(name) as int[];
    public int[][]? Line(string name = "line") => Get(name) as int[][];

    /// <summary>The <c>rect</c> field as (x, y, w, h), when present.</summary>
    public (int X, int Y, int W, int H)? Rect => Ints("rect") is [var x, var y, var w, var h] ? (x, y, w, h) : null;

    /// <summary>A point field (<c>feet</c>, <c>point</c>, ...) as (x, y), when present.</summary>
    public (int X, int Y)? Point(string name) => Ints(name) is [var x, var y] ? (x, y) : null;

    public string? Label => Str("label");

    /// <summary>The fields as "name=value" text, for listings.</summary>
    public string FieldText(params string[] skip) => string.Join(" ", _fields.Where(f => !skip.Contains(f.Key)).Select(f => $"{f.Key}={Format(f.Value)}"));

    public static string Format(object value) => value switch
    {
        int[] a => string.Join(",", a),
        int[][] l => string.Join(" ", l.Select(p => string.Join(",", p))),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        string s => s.Contains(' ') ? $"'{s}'" : s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

/// <summary>
/// Everything the watcher concluded about one processed frame. <see cref="Sequence"/> and <see cref="Pts"/> are the
/// capture's own (the join key with <c>frames.jsonl</c> and the keyframe file names); <see cref="Time"/> is seconds into
/// the session (pts minus the session's first capture pts). <see cref="Filled"/> marks a video frame the recorder wrote to
/// fill an idle gap, which repeats the captured frame <see cref="Sequence"/>. <see cref="Detect"/> is "ok" or the
/// detector's note when the entity detector ran on the frame; <see cref="Skip"/> says why it did not.
/// </summary>
public sealed class OverlayFrame
{
    public required long Sequence { get; init; }
    public required double Pts { get; init; }
    public bool Filled { get; init; }
    public required double Time { get; init; }
    public int? MapId { get; init; }
    public (int X, int Y)? Camera { get; init; }
    public string? Detect { get; init; }
    public string? Skip { get; init; }
    public List<OverlayItem> Items { get; init; } = [];

    /// <summary>Whether the entity detector ran on this frame (with or without a registered camera).</summary>
    public bool DetectorRan => Detect is not null;

    public IEnumerable<OverlayItem> Layer(string layer) => Items.Where(i => i.Layer == layer);

    /// <summary>"m:ss.s" of <see cref="Time"/>.</summary>
    public string Clock => FormatTime(Time);

    public static string FormatTime(double seconds)
    {
        var t = Math.Max(0, seconds);
        var m = (int)(t / 60);
        return $"{m}:{(t - 60 * m).ToString("00.0", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>The stream's first line: what produced it, from which recording, and with which thresholds and layers.</summary>
/// <param name="Name">The own character's name (the watcher's --name).</param>
/// <param name="Pet">The own pet's name (the watcher's --pet), or null for a character without a pet.</param>
public sealed record OverlayHeader(string Schema, string Producer, string? Commit, string? Session, string? Recording, string? Replay, string Source,
    int FrameWidth, int FrameHeight, string? Name, double SpriteThreshold, double DropThreshold, double CoinThreshold, IReadOnlyList<string> Layers, string? Pet = null)
{
    /// <summary>The pet's species (client pet id, 7 digits) and name, when known when the stream started (identified in an earlier session).</summary>
    public string? PetSpecies { get; init; }
    public string? PetSpeciesName { get; init; }

    /// <summary>The schema this code writes and reads. Bump the number on any incompatible change.</summary>
    public const string CurrentSchema = "oaksoul.overlay/1";
}
