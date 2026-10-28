using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OakSoul.Overlay;

namespace OakSoul.Review.Model;

/// <summary>A category of review note, with the key that picks it in the editor.</summary>
public sealed record NoteCategory(string Id, string Title, char Key)
{
    public static readonly IReadOnlyList<NoteCategory> All =
    [
        new("missed", "Missed", '1'),
        new("false-detection", "False detection", '2'),
        new("wrong-kind", "Wrong kind or species", '3'),
        new("wrong-box", "Wrong box", '4'),
        new("wrong-track", "Wrong track or identity", '5'),
        new("wrong-value", "Wrong value", '6'),
        new("other", "Other", '7'),
    ];

    public static NoteCategory Find(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[^1];
}

/// <summary>
/// What the reviewer says is really there: a kind (or "nothing"), a species or name, a state such as dying, and a kind
/// it is not. Empty when the note only describes.
/// </summary>
public sealed record NoteExpect(string? Kind, string? Name, string? State, string? NotKind)
{
    public const string Nothing = "nothing";

    public bool IsEmpty => Kind is null && Name is null && State is null && NotKind is null;

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Kind is not null) o["kind"] = Kind;
        if (Name is not null) o["name"] = Name;
        if (State is not null) o["state"] = State;
        if (NotKind is not null) o["notKind"] = NotKind;
        return o;
    }

    public static NoteExpect FromJson(JsonNode? node) =>
        node is JsonObject o ? new NoteExpect(Str(o, "kind"), Str(o, "name"), Str(o, "state"), Str(o, "notKind")) : new NoteExpect(null, null, null, null);

    internal static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// One note of a review packet: where in which frame, the category, what is really there, the reviewer's own words, and
/// what the stream showed there when the note was made. Backed by its JSON line, so fields this build does not write
/// survive a rewrite of the packet.
/// </summary>
public sealed class ReviewNote(ReviewPacket packet, JsonObject json)
{
    public ReviewPacket Packet { get; } = packet;
    public JsonObject Json { get; } = json;

    public int Number => Json["n"]?.GetValue<int>() ?? 0;
    public string Id => NoteExpect.Str(Json, "id") ?? $"{Packet.Name}/{Number}";
    public long Sequence => Json["seq"]?.GetValue<long>() ?? 0;
    public double Time => Json["t"]?.GetValue<double>() ?? 0;
    public bool Filled => Json["filled"]?.GetValue<bool>() ?? false;
    public string Category => NoteExpect.Str(Json, "category") ?? "other";
    public string Text => NoteExpect.Str(Json, "note") ?? "";
    public NoteExpect Expect => NoteExpect.FromJson(Json["expect"]);
    public int[]? Rect => Ints(Json["rect"]);
    public int[]? Point => Ints(Json["point"]);
    public string? Promoted => NoteExpect.Str(Json, "promoted");
    public string? RawCrop => Json["crops"] is JsonObject c ? NoteExpect.Str(c, "raw") : null;
    public string? OverlayCrop => Json["crops"] is JsonObject c ? NoteExpect.Str(c, "overlay") : null;

    /// <summary>The region as (x, y, w, h); a point is a 1x1 rect.</summary>
    public (int X, int Y, int W, int H) Region => Rect is [var x, var y, var w, var h] ? (x, y, w, h) : Point is [var px, var py] ? (px, py, 1, 1) : (0, 0, 0, 0);

    public bool IsPoint => Rect is null && Point is not null;

    private static int[]? Ints(JsonNode? node) => node is JsonArray a ? a.Select(x => x?.GetValue<int>() ?? 0).ToArray() : null;
}

/// <summary>A review packet: <c>reviews/&lt;session&gt;/&lt;yyyy-mm-dd&gt;-&lt;n&gt;.jsonl</c>, one note per line, all of one review round.</summary>
public sealed class ReviewPacket
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Session { get; init; }
    public required int Review { get; set; }
    public List<ReviewNote> Notes { get; } = [];

    public string Date => Name.Length >= 10 ? Name[..10] : Name;
    public int Ordinal => int.TryParse(Name.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

/// <summary>
/// The review packets of a session, read from <c>reviews/&lt;session&gt;/</c>, and the one new notes go to: today's
/// latest packet, or a new one made when the first note of the day is saved (or the reviewer starts one). A new packet
/// is the next review round: one more than any review number in the truth files' sources and the packets.
/// </summary>
public sealed partial class PacketStore
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    private readonly RepoPaths _paths;
    private bool _startNew;

    public PacketStore(RepoPaths paths, string session)
    {
        _paths = paths;
        Session = session;
        Load();
    }

    public string Session { get; }
    public string Dir => Path.Combine(_paths.Reviews, Session);
    public List<ReviewPacket> Packets { get; } = [];

    /// <summary>Where the next note goes: null until the first note when there is no packet of today.</summary>
    public ReviewPacket? Active { get; private set; }

    public IEnumerable<ReviewNote> Notes => Packets.SelectMany(p => p.Notes);

    /// <summary>Lines of the packets that could not be read.</summary>
    public int BadLines { get; private set; }

    public void Load()
    {
        Packets.Clear();
        BadLines = 0;
        if (Directory.Exists(Dir))
            foreach (var file in Directory.GetFiles(Dir, "*.jsonl"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (!PacketName().IsMatch(name)) continue;
                var packet = new ReviewPacket { Name = name, Path = file, Session = Session, Review = 0 };
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonNode.Parse(line) is JsonObject o) packet.Notes.Add(new ReviewNote(packet, o));
                    }
                    catch (JsonException) { BadLines++; }
                }
                packet.Review = packet.Notes.Select(n => n.Json["review"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max();
                Packets.Add(packet);
            }
        Packets.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date) is var c and not 0 ? c : a.Ordinal.CompareTo(b.Ordinal));
        var today = Today();
        Active = Packets.LastOrDefault(p => p.Date == today);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-\d+$")]
    private static partial Regex PacketName();

    [GeneratedRegex(@"review (\d+)")]
    private static partial Regex ReviewSource();

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The next note starts a new packet (the next review round).</summary>
    public void StartNewPacket() => _startNew = true;

    /// <summary>The packet the next note will go to, named but not yet written when it is new.</summary>
    public (string Name, int Review, bool New) NextPacket()
    {
        var today = Today();
        if (Active is { } a && a.Date == today && !_startNew) return (a.Name, a.Review, false);
        var n = Packets.Where(p => p.Date == today).Select(p => p.Ordinal).DefaultIfEmpty(0).Max() + 1;
        return ($"{today}-{n}", NextReview(), true);
    }

    /// <summary>One more than the highest review number in truth/*.jsonl sources and every packet.</summary>
    public int NextReview()
    {
        var max = 0;
        if (Directory.Exists(_paths.Truth))
            foreach (var file in Directory.GetFiles(_paths.Truth, "*.jsonl"))
                foreach (var line in File.ReadLines(file))
                    foreach (Match m in ReviewSource().Matches(line))
                        max = Math.Max(max, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        if (Directory.Exists(_paths.Reviews))
            foreach (var file in Directory.GetFiles(_paths.Reviews, "*.jsonl", SearchOption.AllDirectories))
                foreach (var line in File.ReadLines(file))
                    if (line.Contains("\"review\"", StringComparison.Ordinal))
                        try { if (JsonNode.Parse(line)?["review"]?.GetValue<int>() is { } r) max = Math.Max(max, r); }
                        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        return max + 1;
    }

    /// <summary>
    /// Numbers the note, adds it to the active packet (making it first when needed) and appends its line;
    /// <paramref name="decorate"/> adds fields that depend on the packet and number (the crops' paths) before it is written.
    /// </summary>
    public ReviewNote Append(JsonObject note, Action<ReviewPacket, int, JsonObject>? decorate = null)
    {
        var (name, review, isNew) = NextPacket();
        if (isNew)
        {
            Active = new ReviewPacket { Name = name, Path = System.IO.Path.Combine(Dir, name + ".jsonl"), Session = Session, Review = review };
            Packets.Add(Active);
            _startNew = false;
        }
        var packet = Active!;
        var n = packet.Notes.Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
        var ordered = new JsonObject
        {
            ["id"] = $"{packet.Name}/{n}",
            ["packet"] = packet.Name,
            ["n"] = n,
            ["review"] = packet.Review,
        };
        foreach (var (key, value) in note.ToList())
        {
            note.Remove(key);
            ordered[key] = value;
        }
        decorate?.Invoke(packet, n, ordered);
        var result = new ReviewNote(packet, ordered);
        packet.Notes.Add(result);
        Directory.CreateDirectory(Dir);
        File.AppendAllText(packet.Path, ordered.ToJsonString(Json) + "\n", new UTF8Encoding(false));
        return result;
    }

    /// <summary>Writes a packet's notes again (after a note was changed or removed).</summary>
    public void Rewrite(ReviewPacket packet)
    {
        var text = new StringBuilder();
        foreach (var note in packet.Notes) text.Append(note.Json.ToJsonString(Json)).Append('\n');
        File.WriteAllText(packet.Path, text.ToString(), new UTF8Encoding(false));
    }

    public void Remove(ReviewNote note)
    {
        note.Packet.Notes.Remove(note);
        Rewrite(note.Packet);
    }

    public static string Serialize(JsonNode node) => node.ToJsonString(Json);

    /// <summary>The folder of a packet's crops: <c>data/reviews/&lt;session&gt;/&lt;packet&gt;/</c>.</summary>
    public string CropDir(ReviewPacket packet) => System.IO.Path.Combine(_paths.ReviewData, Session, packet.Name);
}

/// <summary>Builds the JSON of a new note from what the tool shows.</summary>
public static class NoteBuilder
{
    /// <summary>Entities within this many pixels of the region are in the note's snapshot.</summary>
    public const int SnapshotSlack = 4;

    public static JsonObject Build(string reviewer, string session, bool video, TimelineEntry entry, OverlayFrame? frame, (int X, int Y, int W, int H) region, bool point,
        string category, NoteExpect expect, string text, ReviewStream? stream, Provenance? provenance, IEnumerable<string> layers, IEnumerable<string> kinds, RepoPaths paths)
    {
        var o = new JsonObject
        {
            ["reviewer"] = reviewer,
            ["created"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["session"] = session,
            ["mode"] = video ? "video" : "keyframes",
            ["seq"] = entry.Sequence,
            ["pts"] = frame?.Pts ?? entry.Pts,
            ["t"] = Math.Round(frame?.Time ?? entry.Time, 3),
        };
        if (entry.Filled) o["filled"] = true;
        if (point) o["point"] = new JsonArray(region.X, region.Y);
        else o["rect"] = new JsonArray(region.X, region.Y, region.W, region.H);
        o["category"] = category;
        o["expect"] = expect.ToJson();
        o["note"] = text;
        o["entities"] = Snapshot(frame, region, point);
        o["stream"] = StreamRef(stream, provenance, paths);
        o["layers"] = new JsonArray(layers.Select(l => (JsonNode?)l).ToArray());
        o["kinds"] = new JsonArray(kinds.Select(k => (JsonNode?)k).ToArray());
        return o;
    }

    /// <summary>The stream's entities at the region (rects within <see cref="SnapshotSlack"/> pixels): id, kind, label, rect, score, evidence.</summary>
    public static JsonArray Snapshot(OverlayFrame? frame, (int X, int Y, int W, int H) region, bool point)
    {
        var list = new JsonArray();
        if (frame is null) return list;
        foreach (var e in Under(frame, region))
        {
            var o = new JsonObject();
            if (e.Long("track") is { } track) o["id"] = track;
            o["kind"] = e.Kind;
            if (e.Label is { } label) o["label"] = label;
            if (e.Rect is { } r) o["rect"] = new JsonArray(r.X, r.Y, r.W, r.H);
            if ((e.Double("score") ?? e.Double("lastScore")) is { } score) o["score"] = score;
            if (e.Str("evidence") is { } ev) o["evidence"] = ev;
            if (e.Str("name") is { } name) o["name"] = name;
            list.Add(o);
        }
        return list;
    }

    public static IEnumerable<OverlayItem> Under(OverlayFrame frame, (int X, int Y, int W, int H) region) =>
        frame.Layer(OverlayLayers.Entity).Where(e => e.Rect is { } r &&
            r.X <= region.X + region.W + SnapshotSlack && region.X - SnapshotSlack <= r.X + r.W &&
            r.Y <= region.Y + region.H + SnapshotSlack && region.Y - SnapshotSlack <= r.Y + r.H);

    private static JsonObject StreamRef(ReviewStream? stream, Provenance? provenance, RepoPaths paths)
    {
        var o = new JsonObject();
        if (stream is null) return o;
        o["path"] = paths.Relative(stream.Path);
        if (stream.Header is { } h)
        {
            o["schema"] = h.Schema;
            o["producer"] = h.Producer;
            if (h.Commit is not null) o["commit"] = h.Commit;
            if (h.Replay is not null) o["replay"] = h.Replay;
            o["source"] = h.Source;
            if (h.Name is not null) o["name"] = h.Name;
            if (h.Pet is not null) o["pet"] = h.Pet;
            o["thresholds"] = new JsonObject { ["sprite"] = Num(h.SpriteThreshold), ["drop"] = Num(h.DropThreshold), ["coin"] = Num(h.CoinThreshold) };
        }
        o["complete"] = stream.Complete;
        if (provenance is not null) o["freshness"] = provenance.Level.ToString().ToLowerInvariant();
        return o;
    }

    private static JsonNode? Num(double v) => double.IsNaN(v) ? null : JsonValue.Create(v);
}
