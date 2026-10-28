using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OakSoul.Bake;

namespace OakSoul.Review.Model;

/// <summary>The verdict of a truth entry or a note against the stream: null while the stream has not reached its frame.</summary>
public sealed record Verdict(bool? Pass, string Why, string Place)
{
    public string Word => Pass switch { true => "PASS", false => "FAIL", null => "…" };
}

/// <summary>One line of <c>truth/&lt;session&gt;.jsonl</c>.</summary>
public sealed class TruthEntry(int line, JsonElement json)
{
    public int Line { get; } = line;
    public JsonElement Json { get; } = json;
    public string Id => Text("id") ?? $"line {Line}";
    public long Sequence => Json.TryGetProperty("seq", out var s) && s.TryGetInt64(out var v) ? v : -1;
    public string? Session => Text("session");
    public string Note => Text("note") ?? "";
    public string Source => Text("source") ?? "";
    public string ExpectText => Json.TryGetProperty("expect", out var e) ? e.GetRawText() : "";
    public int[]? Rect => Ints("rect");
    public int[]? Point => Ints("point");
    public Verdict? Verdict { get; set; }

    public (int X, int Y, int W, int H) Region => Rect is [var x, var y, var w, var h] ? (x, y, w, h) : Point is [var px, var py] ? (px, py, 1, 1) : (0, 0, 0, 0);
    public bool IsPoint => Rect is null && Point is not null;

    private string? Text(string name) => Json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private int[]? Ints(string name) =>
        Json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.TryGetInt32(out var i) ? i : 0).ToArray() : null;
}

/// <summary>
/// The ground truth of a session (<c>truth/&lt;session&gt;.jsonl</c>, the format Bake's <see cref="Truth"/> reads), each
/// entry checked against the open stream with <see cref="Truth.CheckEntry"/>, and the promotion of review notes into new
/// entries.
/// </summary>
public sealed class TruthBook
{
    private readonly RepoPaths _paths;

    public TruthBook(RepoPaths paths, string session)
    {
        _paths = paths;
        Session = session;
        Load();
    }

    public string Session { get; }
    public string Path => System.IO.Path.Combine(_paths.Truth, Session + ".jsonl");
    public List<TruthEntry> Entries { get; } = [];
    public int BadLines { get; private set; }

    public void Load()
    {
        Entries.Clear();
        BadLines = 0;
        if (!File.Exists(Path)) return;
        var n = 0;
        foreach (var line in File.ReadLines(Path))
        {
            n++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var entry = new TruthEntry(n, doc.RootElement.Clone());
                if (entry.Session is null || entry.Session == Session) Entries.Add(entry);
            }
            catch (JsonException) { BadLines++; }
        }
    }

    /// <summary>Checks every entry against the stream; entries whose frame the stream has not reached yet wait.</summary>
    public void Evaluate(ReviewStream? stream, Timeline timeline)
    {
        foreach (var e in Entries) e.Verdict = Check(stream, timeline, e.Sequence, e.Json);
    }

    /// <summary>
    /// The verdict of an entry: Bake's check on the stream's captured frame of its sequence, "not in the stream" when the
    /// replay never shows that frame (a keyframe stream holds every 15th), waiting while the stream has not reached it.
    /// </summary>
    public static Verdict Check(ReviewStream? stream, Timeline timeline, long sequence, JsonElement entry)
    {
        var index = timeline.IndexOf(sequence);
        var frame = stream?.At(index);
        if (frame is null && index >= 0 && stream is not { Complete: true })
            return new Verdict(null, stream is null ? "no stream" : "the stream has not reached this frame yet", "");
        try
        {
            var (ok, why, place) = Truth.CheckEntry(frame, entry);
            return new Verdict(ok, why, place);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        {
            return new Verdict(false, $"unreadable entry: {e.Message}", "");
        }
    }

    /// <summary>
    /// The truth entry a note becomes, or null when it says nothing checkable. The expectation maps onto Truth's terms:
    /// a kind (with nameEndsWith for the name and state), "nothing" as none, a kind it is not as notKind, and for a wrong
    /// box the drawn rect's inner corners as covers (the box must contain what was drawn).
    /// </summary>
    public static JsonObject? Expectation(ReviewNote note)
    {
        var x = note.Expect;
        var expect = new JsonObject();
        if (x.Kind == NoteExpect.Nothing) expect["none"] = true;
        else if (x.Kind is { } kind)
        {
            expect["kind"] = kind;
            if (x.Name is { } name) expect["nameEndsWith"] = name;
            if (x.State is { } state) expect["state"] = state;
            if (note.Category == "wrong-box" && note.Rect is [var rx, var ry, var rw, var rh] && rw >= 6 && rh >= 6)
                expect["covers"] = new JsonArray(new JsonArray(rx + 2, ry + 2), new JsonArray(rx + rw - 3, ry + rh - 3));
        }
        if (x.NotKind is { } not) expect["notKind"] = not;
        return expect.Count == 0 ? null : expect;
    }

    /// <summary>The note's verdict: its expectation checked like a truth entry, or null when it has none.</summary>
    public static Verdict? CheckNote(ReviewNote note, ReviewStream? stream, Timeline timeline)
    {
        if (Expectation(note) is not { } expect) return null;
        var entry = new JsonObject { ["seq"] = note.Sequence, ["expect"] = expect };
        if (note.Rect is { } r) entry["rect"] = new JsonArray(r.Select(v => (JsonNode?)v).ToArray());
        else if (note.Point is { } p) entry["point"] = new JsonArray(p.Select(v => (JsonNode?)v).ToArray());
        using var doc = JsonDocument.Parse(entry.ToJsonString());
        return Check(stream, timeline, note.Sequence, doc.RootElement);
    }

    /// <summary>
    /// The entry a note is promoted to, in the format Truth.cs reads: id r&lt;review&gt;-&lt;seq&gt;-&lt;what&gt;, the
    /// place, the expectation, the reviewer's words verbatim as note, the packet as basis, and source "Reviewer, review N, date".
    /// </summary>
    public JsonObject? Promotion(ReviewNote note, string reviewer, ISet<string> takenIds)
    {
        if (Expectation(note) is not { } expect) return null;
        var x = note.Expect;
        var what = Slug(x.Name ?? (x.Kind == NoteExpect.Nothing ? "nothing" : x.Kind) ?? (x.NotKind is { } nk ? $"not-{nk}" : note.Category));
        var id = $"r{note.Packet.Review}-{note.Sequence}-{what}";
        for (var k = 2; takenIds.Contains(id); k++) id = $"r{note.Packet.Review}-{note.Sequence}-{what}-{k}";
        takenIds.Add(id);
        var entry = new JsonObject { ["id"] = id, ["session"] = Session, ["seq"] = note.Sequence };
        if (note.Rect is { } r) entry["rect"] = new JsonArray(r.Select(v => (JsonNode?)v).ToArray());
        else if (note.Point is { } p) entry["point"] = new JsonArray(p.Select(v => (JsonNode?)v).ToArray());
        entry["expect"] = expect;
        entry["note"] = note.Text;
        entry["basis"] = $"review packet {_paths.Relative(note.Packet.Path)} note {note.Number} ({NoteCategory.Find(note.Category).Title.ToLowerInvariant()}), " +
                         $"{(note.IsPoint ? "point" : "rect")} drawn by the reviewer in OakSoul.Review";
        entry["source"] = $"{reviewer}, review {note.Packet.Review}, {note.Packet.Date}";
        return entry;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "note" : slug;
    }

    /// <summary>Appends entries to the truth file (making it when needed) and reads it again.</summary>
    public void Append(IEnumerable<JsonObject> entries)
    {
        Directory.CreateDirectory(_paths.Truth);
        var text = new StringBuilder();
        if (File.Exists(Path) && new FileInfo(Path).Length > 0)
        {
            using var f = File.OpenRead(Path);
            f.Seek(-1, SeekOrigin.End);
            if (f.ReadByte() != '\n') text.Append('\n');
        }
        foreach (var e in entries) text.Append(PacketStore.Serialize(e)).Append('\n');
        File.AppendAllText(Path, text.ToString(), new UTF8Encoding(false));
        Load();
    }

    public HashSet<string> Ids() => Entries.Select(e => e.Id).ToHashSet();
}
