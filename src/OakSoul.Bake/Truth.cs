using System.Globalization;
using System.Text.Json;
using OakSoul.Overlay;

namespace OakSoul.Bake;

/// <summary>
/// The reviewed ground truth of a recording, <c>truth/&lt;session&gt;.jsonl</c> (tracked in the repository): one JSON object
/// per line, each a statement the reviewer made about one place in one frame, and <c>bake truth</c>, which checks a stream
/// against it. Entries are keyed by session, capture sequence and an image position (a rect, or a point for something the
/// pipeline missed), never by track id, which changes between regenerations.
/// <code>
/// {"id":"r1-781-boar","session":"fx-combat-exact","seq":781,"rect":[774,462,48,34],"expect":{"kind":"mob","nameEndsWith":"Boar","state":"dying"},
///  "note":"his words","source":"Arthur, review 1, 2026-10-27"}
/// </code>
/// An entity is at the entry's place when its rect contains the point (a pixel of slack), or for a rect when the two
/// overlap by at least <see cref="MinOverlap"/> of the smaller or either contains the other's centre; <c>near</c> (pixels)
/// grows the place first. <c>expect</c> holds what must be true there: <c>kind</c> (some entity of that kind, which must
/// also satisfy every other condition given: <c>nameEndsWith</c>, <c>nameContains</c>, <c>state</c>, <c>direction</c>,
/// <c>covers</c> points its rect contains, <c>excludes</c> points it does not), <c>notKind</c> (no entity of these kinds
/// there), <c>none</c> (no entity at all there) and <c>self</c> (the own character's box contains the place's centre).
/// Since review 2 also: <c>name</c> (the entity's name, exactly), <c>fields</c> (an object of field values the entity must
/// have, exactly: <c>{"skillName":"Cold Beam"}</c>), <c>fieldsNot</c> (values it must not have: <c>{"state":"dying"}</c>),
/// <c>range</c> (numeric fields within [min, max]: <c>{"fill":[0.5,0.65]}</c>), <c>count</c> (exactly this many entities
/// of the kind, satisfying the conditions, at the place: one mob, one track), <c>minCount</c> (at least this many) and
/// <c>hudKind</c> (a HUD item of that kind, the cursor, at the place). Since review 3, <c>layer</c> names the layer the other
/// conditions look at instead of the entity layer: <c>{"layer":"tags","kind":"tag","fields":{"owner":"mob"}}</c>, a tag
/// whose owner is a mob. An entry whose frame is not in the stream because the
/// stream cannot hold it (a video-only sequence against the keyframe stream, a keyframe the video left out) reports n/a.
/// </summary>
public static class Truth
{
    public const double MinOverlap = 0.3;

    public static int Run(Session session, Options o)
    {
        var path = o.Truth ?? Path.Combine("truth", session.Name + ".jsonl");
        if (!File.Exists(path)) throw new BakeException($"no ground truth at {path}");
        var streamPath = o.Stream ?? session.DefaultStream(o.Video);
        if (!File.Exists(streamPath)) throw new BakeException($"no stream at {streamPath}; run: regenerate {session.Name}{(o.Video ? " --video" : "")}");
        var stream = OverlayStream.Load(streamPath);
        var frames = stream.Frames.Where(f => !f.Filled).GroupBy(f => f.Sequence).ToDictionary(g => g.Key, g => g.First());
        int pass = 0, fail = 0, missing = 0, other = 0, lineNo = 0, notApplicable = 0;
        var replay = stream.Header.Replay;
        var recording = Path.Combine(session.Recording, "keyframes");
        Console.WriteLine($"truth {path} against {streamPath}");
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var id = e.TryGetProperty("id", out var idv) ? idv.GetString() ?? $"line {lineNo}" : $"line {lineNo}";
            if (e.TryGetProperty("session", out var sv) && sv.GetString() != session.Name) { other++; continue; }
            var seq = e.GetProperty("seq").GetInt64();
            var place = Place(e);
            if (!frames.TryGetValue(seq, out var frame))
            {
                // A keyframe stream holds only the keyframes: an entry about a frame between them does not apply to it.
                if (replay == "keyframes" && !File.Exists(Path.Combine(recording, $"{seq:D7}.png")))
                {
                    notApplicable++;
                    Console.WriteLine($"  n/a  {id}  seq {seq:D7}: not a keyframe (video only)");
                    continue;
                }
                missing++;
                fail++;
                Console.WriteLine($"  FAIL {id}  seq {seq:D7}: the frame is not in the stream");
                continue;
            }
            var (ok, why) = Check(frame, place, e.GetProperty("expect"));
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {id}  seq {seq:D7} {place.Text}: {why}");
        }
        Console.WriteLine($"truth: {pass + fail + notApplicable} entries, {pass} pass, {fail} fail{(notApplicable > 0 ? $", {notApplicable} n/a (not in a {replay} stream)" : "")}{(missing > 0 ? $" ({missing} frames not in the stream)" : "")}{(other > 0 ? $"; {other} entries of other sessions skipped" : "")}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Checks one entry (a parsed line) against the frame of the stream it names, or null when the stream lacks that frame:
    /// pass or fail, why, and the entry's place as <c>bake truth</c> prints them. The review tool's check.
    /// </summary>
    public static (bool Ok, string Why, string Place) CheckEntry(OverlayFrame? frame, JsonElement entry)
    {
        var place = Place(entry);
        if (frame is null) return (false, "the frame is not in the stream", place.Text);
        var (ok, why) = Check(frame, place, entry.GetProperty("expect"));
        return (ok, why, place.Text);
    }

    private sealed record Location(int X, int Y, int W, int H, bool IsPoint, int Near)
    {
        public string Text => IsPoint ? $"point {X},{Y}" : $"rect {X},{Y},{W},{H}";
        public (double X, double Y) Centre => IsPoint ? (X, Y) : (X + W / 2.0, Y + H / 2.0);
    }

    private static Location Place(JsonElement e)
    {
        var near = e.TryGetProperty("near", out var n) ? n.GetInt32() : 0;
        if (e.TryGetProperty("point", out var p)) return new Location(p[0].GetInt32(), p[1].GetInt32(), 1, 1, true, near);
        var r = e.GetProperty("rect");
        return new Location(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32(), false, near);
    }

    private static bool At(OverlayItem item, Location l)
    {
        if (item.Rect is not { } r) return false;
        int x = l.X - l.Near, y = l.Y - l.Near, w = l.W + 2 * l.Near, h = l.H + 2 * l.Near;
        if (l.IsPoint && l.Near == 0) return l.X >= r.X - 1 && l.X <= r.X + r.W && l.Y >= r.Y - 1 && l.Y <= r.Y + r.H;
        int ix = Math.Min(x + w, r.X + r.W) - Math.Max(x, r.X), iy = Math.Min(y + h, r.Y + r.H) - Math.Max(y, r.Y);
        if (l.Near > 0) return ix > 0 && iy > 0;
        var overlap = ix > 0 && iy > 0 ? (double)ix * iy / Math.Max(1, Math.Min(w * h, r.W * r.H)) : 0;
        var (cx, cy) = l.Centre;
        double ex = r.X + r.W / 2.0, ey = r.Y + r.H / 2.0;
        return overlap >= MinOverlap || (cx >= r.X && cx <= r.X + r.W && cy >= r.Y && cy <= r.Y + r.H) || (ex >= x && ex <= x + w && ey >= y && ey <= y + h);
    }

    private static bool Contains(OverlayItem item, JsonElement point) =>
        item.Rect is { } r && point[0].GetInt32() >= r.X && point[0].GetInt32() < r.X + r.W && point[1].GetInt32() >= r.Y && point[1].GetInt32() < r.Y + r.H;

    private static string Describe(OverlayItem i) => $"{i.Kind} '{i.Label}' {(i.Rect is { } r ? $"{r.X},{r.Y},{r.W},{r.H}" : "")}";

    private static (bool Ok, string Why) Check(OverlayFrame frame, Location place, JsonElement expect)
    {
        string? S(string name) => expect.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        // The entity layer unless the entry names another (since review 3: the tags layer, for a tag's owner).
        var at = frame.Layer(S("layer") ?? OverlayLayers.Entity).Where(i => At(i, place)).ToList();
        var problems = new List<string>();
        string? matched = null;
        if (S("kind") is { } kind)
        {
            var good = at.Where(i => i.Kind == kind).Where(i =>
                (S("nameEndsWith") is not { } end || (i.Str("name") ?? "").EndsWith(end, StringComparison.OrdinalIgnoreCase)) &&
                (S("nameContains") is not { } part || (i.Str("name") ?? "").Contains(part, StringComparison.OrdinalIgnoreCase)) &&
                (S("name") is not { } exact || string.Equals(i.Str("name"), exact, StringComparison.OrdinalIgnoreCase)) &&
                (S("state") is not { } state || i.Str("state") == state) &&
                (S("direction") is not { } dir || i.Str("direction") == dir) &&
                (!expect.TryGetProperty("fields", out var fields) || fields.EnumerateObject().All(f => FieldIs(i, f))) &&
                (!expect.TryGetProperty("fieldsNot", out var fieldsNot) || fieldsNot.EnumerateObject().All(f => !FieldIs(i, f))) &&
                (!expect.TryGetProperty("range", out var range) || range.EnumerateObject().All(f => i.Double(f.Name) is { } v && v >= f.Value[0].GetDouble() && v <= f.Value[1].GetDouble())) &&
                (!expect.TryGetProperty("covers", out var cov) || cov.EnumerateArray().All(p => Contains(i, p))) &&
                (!expect.TryGetProperty("excludes", out var exc) || exc.EnumerateArray().All(p => !Contains(i, p)))).ToList();
            if (good.Count == 0) problems.Add($"no {kind}{Conditions(expect)} here");
            else matched = good.Count == 1 ? Describe(good[0]) : string.Join(", ", good.Select(Describe));
            if (expect.TryGetProperty("count", out var count) && good.Count != count.GetInt32()) problems.Add($"{good.Count} {kind}{Conditions(expect)} here, expected {count.GetInt32()}");
            if (expect.TryGetProperty("minCount", out var minCount) && good.Count < minCount.GetInt32()) problems.Add($"{good.Count} {kind}{Conditions(expect)} here, expected at least {minCount.GetInt32()}");
        }
        if (S("hudKind") is { } hudKind)
        {
            var hud = frame.Layer(OverlayLayers.Hud).Where(i => i.Kind == hudKind && At(i, place)).ToList();
            if (hud.Count == 0) problems.Add($"no {hudKind} in the hud layer here");
            else matched = (matched is null ? "" : matched + "; ") + $"hud {hudKind} '{hud[0].Label}' {(hud[0].Rect is { } hr ? $"{hr.X},{hr.Y},{hr.W},{hr.H}" : "")}";
        }
        if (expect.TryGetProperty("notKind", out var nk))
        {
            var kinds = nk.ValueKind == JsonValueKind.Array ? nk.EnumerateArray().Select(k => k.GetString()).ToHashSet() : [nk.GetString()];
            foreach (var i in at.Where(i => kinds.Contains(i.Kind))) problems.Add($"{Describe(i)} is here");
        }
        if (expect.TryGetProperty("none", out var none) && none.ValueKind == JsonValueKind.True)
            foreach (var i in at) problems.Add($"{Describe(i)} is here");
        if (expect.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True)
        {
            var own = frame.Layer(OverlayLayers.Self).FirstOrDefault(i => i.Kind == "character")?.Rect;
            var (cx, cy) = place.Centre;
            if (own is not { } r || cx < r.X || cx > r.X + r.W || cy < r.Y || cy > r.Y + r.H) problems.Add($"the own character's box {(own is { } o2 ? $"{o2.X},{o2.Y},{o2.W},{o2.H}" : "(none)")} does not cover it");
        }
        if (problems.Count > 0)
            return (false, string.Join("; ", problems) + (at.Count > 0 ? $" (here: {string.Join(", ", at.Select(Describe))})" : " (nothing here)"));
        return (true, matched ?? (at.Count == 0 ? "nothing here, as expected" : $"here: {string.Join(", ", at.Select(Describe))}"));
    }

    /// <summary>Whether an item's field has the value a truth entry gives (a string, number or bool).</summary>
    private static bool FieldIs(OverlayItem i, JsonProperty f) => f.Value.ValueKind switch
    {
        JsonValueKind.String => i.Str(f.Name) == f.Value.GetString(),
        JsonValueKind.Number => i.Double(f.Name) is { } v && Math.Abs(v - f.Value.GetDouble()) < 1e-9,
        JsonValueKind.True => i.Bool(f.Name),
        JsonValueKind.False => !i.Bool(f.Name),
        _ => false,
    };

    private static string Conditions(JsonElement expect)
    {
        var parts = new List<string>();
        foreach (var name in new[] { "name", "nameEndsWith", "nameContains", "state", "direction" })
            if (expect.TryGetProperty(name, out var v)) parts.Add($"{name} {v.GetString()}");
        foreach (var name in new[] { "fields", "fieldsNot", "range" })
            if (expect.TryGetProperty(name, out var v)) parts.Add($"{name} {v.GetRawText()}");
        if (expect.TryGetProperty("covers", out var c)) parts.Add($"covering {string.Join(" ", c.EnumerateArray().Select(p => $"{p[0]},{p[1]}"))}");
        if (expect.TryGetProperty("excludes", out var x)) parts.Add($"not covering {string.Join(" ", x.EnumerateArray().Select(p => $"{p[0]},{p[1]}"))}");
        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }
}
