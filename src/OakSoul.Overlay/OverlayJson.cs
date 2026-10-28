using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OakSoul.Overlay;

/// <summary>
/// The stream's JSON Lines encoding: the header and each frame as one compact line, fields in a fixed order so the same
/// frame always serializes to the same bytes, and parsing that gives back exactly what was written (a parsed line
/// re-serializes to identical bytes).
/// </summary>
public static class OverlayJson
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static byte[] Header(OverlayHeader h)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteString("schema", h.Schema);
            w.WriteString("producer", h.Producer);
            if (h.Commit is not null) w.WriteString("commit", h.Commit);
            if (h.Session is not null) w.WriteString("session", h.Session);
            if (h.Recording is not null) w.WriteString("recording", h.Recording);
            if (h.Replay is not null) w.WriteString("replay", h.Replay);
            w.WriteString("source", h.Source);
            w.WriteStartArray("frame"); w.WriteNumberValue(h.FrameWidth); w.WriteNumberValue(h.FrameHeight); w.WriteEndArray();
            if (h.Name is not null) w.WriteString("name", h.Name);
            if (h.Pet is not null) w.WriteString("pet", h.Pet);
            w.WriteStartObject("thresholds");
            w.WriteNumber("sprite", h.SpriteThreshold);
            w.WriteNumber("drop", h.DropThreshold);
            w.WriteNumber("coin", h.CoinThreshold);
            w.WriteEndObject();
            w.WriteStartArray("layers");
            foreach (var l in h.Layers) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static void Frame(IBufferWriter<byte> buffer, OverlayFrame f)
    {
        using var w = new Utf8JsonWriter(buffer, Options);
        w.WriteStartObject();
        w.WriteNumber("seq", f.Sequence);
        w.WriteNumber("pts", f.Pts);
        if (f.Filled) w.WriteBoolean("filled", true);
        w.WriteNumber("t", f.Time);
        if (f.MapId is { } map) w.WriteNumber("map", map);
        if (f.Camera is { } cam) { w.WriteStartArray("camera"); w.WriteNumberValue(cam.X); w.WriteNumberValue(cam.Y); w.WriteEndArray(); }
        if (f.Detect is not null) w.WriteString("detect", f.Detect);
        if (f.Skip is not null) w.WriteString("skip", f.Skip);
        w.WriteStartArray("items");
        foreach (var item in f.Items)
        {
            w.WriteStartObject();
            w.WriteString("layer", item.Layer);
            w.WriteString("kind", item.Kind);
            foreach (var (name, value) in item.Fields)
            {
                w.WritePropertyName(name);
                WriteValue(w, value);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static byte[] Frame(OverlayFrame f)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        Frame(buffer, f);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int[] a:
                w.WriteStartArray();
                foreach (var v in a) w.WriteNumberValue(v);
                w.WriteEndArray();
                break;
            case int[][] line:
                w.WriteStartArray();
                foreach (var p in line)
                {
                    w.WriteStartArray();
                    foreach (var v in p) w.WriteNumberValue(v);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            default: throw new InvalidOperationException($"unsupported overlay value {value.GetType().Name}");
        }
    }

    public static OverlayHeader ParseHeader(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var schema = S("schema") ?? throw new InvalidDataException("overlay stream: the first line is not a header (no schema)");
        if (schema != OverlayHeader.CurrentSchema) throw new InvalidDataException($"overlay stream: schema {schema}, this build reads {OverlayHeader.CurrentSchema}");
        var frame = r.GetProperty("frame");
        var th = r.TryGetProperty("thresholds", out var t) ? t : default;
        double T(string name) => th.ValueKind == JsonValueKind.Object && th.TryGetProperty(name, out var v) ? v.GetDouble() : double.NaN;
        return new OverlayHeader(schema, S("producer") ?? "", S("commit"), S("session"), S("recording"), S("replay"), S("source") ?? "",
            frame[0].GetInt32(), frame[1].GetInt32(), S("name"), T("sprite"), T("drop"), T("coin"),
            r.TryGetProperty("layers", out var layers) ? layers.EnumerateArray().Select(l => l.GetString() ?? "").ToList() : [], S("pet"));
    }

    public static OverlayFrame ParseFrame(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        var items = new List<OverlayItem>();
        foreach (var e in r.GetProperty("items").EnumerateArray())
        {
            var item = new OverlayItem(e.GetProperty("layer").GetString()!, e.GetProperty("kind").GetString()!);
            foreach (var p in e.EnumerateObject())
                if (p.Name is not ("layer" or "kind")) item.Set(p.Name, ReadValue(p.Value));
            items.Add(item);
        }
        (int, int)? camera = r.TryGetProperty("camera", out var c) ? (c[0].GetInt32(), c[1].GetInt32()) : null;
        return new OverlayFrame
        {
            Sequence = r.GetProperty("seq").GetInt64(),
            Pts = r.GetProperty("pts").GetDouble(),
            Filled = r.TryGetProperty("filled", out var f) && f.ValueKind == JsonValueKind.True,
            Time = r.GetProperty("t").GetDouble(),
            MapId = r.TryGetProperty("map", out var m) ? m.GetInt32() : null,
            Camera = camera,
            Detect = r.TryGetProperty("detect", out var d) ? d.GetString() : null,
            Skip = r.TryGetProperty("skip", out var s) ? s.GetString() : null,
            Items = items,
        };
    }

    private static object ReadValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var l) && !v.GetRawText().Contains('.') && !v.GetRawText().Contains('e') && !v.GetRawText().Contains('E') ? l : v.GetDouble(),
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array when v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.Array =>
            v.EnumerateArray().Select(p => p.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray(),
        JsonValueKind.Array => v.EnumerateArray().Select(x => x.GetInt32()).ToArray(),
        _ => throw new InvalidDataException($"overlay stream: unsupported value {v.GetRawText()}"),
    };
}
