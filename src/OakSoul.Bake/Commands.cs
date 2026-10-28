using System.Diagnostics;
using System.Globalization;
using System.Text;
using OakSoul.Capture.Replay;
using OakSoul.Overlay;
using OakSoul.Watch;

namespace OakSoul.Bake;

public static class Commands
{
    /// <summary>
    /// Runs the watcher (the current pipeline, in this process) over the recording with <c>--overlay-out</c>, its log
    /// redirected to a file next to the stream.
    /// </summary>
    public static async Task<int> Regenerate(Session session, Options o)
    {
        var stream = o.Out ?? session.DefaultStream(o.Video);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stream))!);
        var log = Path.Combine(Path.GetDirectoryName(stream) ?? ".", Path.GetFileName(stream).Replace(".jsonl.gz", "").Replace(".jsonl", "") + ".watch.log");
        string[] watchArgs = ["--recording", session.Recording, .. o.Video ? Array.Empty<string>() : ["--keyframes"], "--speed", "0", "--overlay-out", stream,
            .. o.CharacterName is { } name ? new[] { "--name", name } : [], .. o.Pet is { } pet ? new[] { "--pet", pet } : []];
        Console.WriteLine($"regenerate: OakSoul.Watch {string.Join(' ', watchArgs)}");
        var sw = Stopwatch.StartNew();
        var stdout = Console.Out;
        int code;
        await using (var writer = new StreamWriter(log, false, new UTF8Encoding(false)) { AutoFlush = false })
        {
            Console.SetOut(writer);
            try { code = await WatchProgram.RunAsync(watchArgs); }
            finally { Console.Out.Flush(); Console.SetOut(stdout); }
        }
        foreach (var line in File.ReadLines(log).Where(l => l.StartsWith("overlay ", StringComparison.Ordinal) || l.StartsWith("done:", StringComparison.Ordinal) || l.StartsWith("source ", StringComparison.Ordinal) || l.StartsWith("entities: skipped", StringComparison.Ordinal) || l.StartsWith("replay:", StringComparison.Ordinal)))
            Console.WriteLine($"  {line}");
        Console.WriteLine($"regenerate: {(code == 0 ? "wrote" : "FAILED")} {stream} in {sw.Elapsed.TotalSeconds:0.0} s; watcher log {log}");
        return code;
    }

    private static OverlayStream LoadStream(Session session, Options o)
    {
        var path = o.Stream ?? session.DefaultStream(o.Video);
        if (!File.Exists(path)) throw new BakeException($"no stream at {path}; run: regenerate {session.Name}{(o.Video ? " --video" : "")}");
        var stream = OverlayStream.Load(path);
        if (stream.Header.Session is { } s && s != session.Name) Console.Error.WriteLine($"warning: {path} is a stream of {s}, not {session.Name}");
        return stream;
    }

    /// <summary>The captured frames (never fills) the selection names, in stream order, and a folder name for them.</summary>
    private static (List<OverlayFrame> Frames, string Name) Select(OverlayStream stream, Options o)
    {
        if (!o.HasSelection) throw new BakeException("select frames with --frame N, --frames LIST, --from M:SS --seconds S, or --all");
        var captured = stream.Frames.Where(f => !f.Filled).ToList();
        var chosen = new List<OverlayFrame>();
        string name;
        if (o.All) { chosen.AddRange(captured); name = "all"; }
        else if (o.From is { } from)
        {
            var t0 = Options.ParseTime(from);
            var seconds = o.Seconds ?? 15;
            chosen.AddRange(captured.Where(f => f.Time >= t0 - 1e-6 && f.Time < t0 + seconds - 1e-6));
            name = $"t{OverlayFrame.FormatTime(t0).Replace(':', '-')}-{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s";
            if (chosen.Count == 0) throw new BakeException($"no frame of the stream between {from} and {seconds} s later (the stream spans {captured.FirstOrDefault()?.Clock} to {captured.LastOrDefault()?.Clock})");
        }
        else
        {
            foreach (var (first, last) in o.Frames)
            {
                var range = captured.Where(f => f.Sequence >= first && f.Sequence <= last).ToList();
                if (range.Count == 0)
                {
                    var near = captured.OrderBy(f => Math.Abs(f.Sequence - first)).Take(2).Select(f => f.Sequence.ToString("D7", CultureInfo.InvariantCulture));
                    throw new BakeException(first == last
                        ? $"frame {first} is not in the stream ({stream.Header.Replay} replay); nearest: {string.Join(", ", near)}"
                        : $"no frame of the stream in {first}-{last}; nearest: {string.Join(", ", near)}");
                }
                chosen.AddRange(range);
            }
            chosen = chosen.Distinct().OrderBy(f => f.Sequence).ToList();
            name = o.Frames is [var (a, b)] && a == b ? $"frame-{a:D7}" : $"frames-{chosen[0].Sequence:D7}-{chosen[^1].Sequence:D7}";
        }
        return (chosen, o.Name ?? name);
    }

    public static string FileName(OverlayFrame f) => $"{f.Sequence:D7}_{f.Clock.Replace(':', '-')}.png";

    public static int Render(Session session, Options o)
    {
        var stream = LoadStream(session, o);
        var (frames, name) = Select(stream, o);
        var dir = o.Out ?? session.BakeDir(name);
        Directory.CreateDirectory(dir);
        var pixels = new RecordingPixels(session.Recording);
        var options = new RenderOptions(o.Scale, o.DrawnLayers);
        var sw = Stopwatch.StartNew();
        var images = pixels.Load(frames.Select(f => f.Sequence), o.VideoPixels);
        var tiles = new List<(OverlayFrame, OakSoul.Detect.Canvas)>();
        var list = new StringBuilder();
        foreach (var f in frames)
        {
            var bgra = images[f.Sequence];
            var canvas = FrameRenderer.Render(stream.Header, f, bgra, pixels.Width, pixels.Height, options);
            var path = Path.Combine(dir, FileName(f));
            canvas.Save(path);
            if (!o.NoSheet && frames.Count > 1) tiles.Add((f, Sheets.Tile(f, bgra, pixels.Width, pixels.Height)));
            foreach (var line in ListFrame(f, options.Layers, pixels.SourceOf(f.Sequence, o.VideoPixels))) list.AppendLine(line);
            list.AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "items.txt"), list.ToString());
        var sheet = tiles.Count > 1 ? Sheets.Write(dir, "contact-sheet", tiles, tiles.Count, Math.Min(5, tiles.Count)) : [];
        Console.WriteLine($"render: {frames.Count} frame(s) of {stream.Path} at {o.Scale}x to {dir} in {sw.Elapsed.TotalSeconds:0.0} s" +
                          $" ({frames.Count(f => pixels.SourceOf(f.Sequence, o.VideoPixels) == "keyframe")} from keyframes, {frames.Count(f => pixels.SourceOf(f.Sequence, o.VideoPixels) == "video")} from the video)");
        foreach (var f in frames.Take(3)) Console.WriteLine($"  {Path.Combine(dir, FileName(f))}");
        if (frames.Count > 3) Console.WriteLine($"  ... {frames.Count - 3} more");
        foreach (var p in sheet) Console.WriteLine($"  contact sheet {p}");
        Console.WriteLine($"  items {Path.Combine(dir, "items.txt")}");
        return 0;
    }

    /// <summary>The frame's heading and one line per item: layer, kind, label, rect, feet and the remaining fields.</summary>
    public static IEnumerable<string> ListFrame(OverlayFrame f, IReadOnlySet<string> layers, string? pixelSource = null)
    {
        yield return $"frame {f.Sequence:D7}  t {f.Clock} ({f.Time.ToString("0.000", CultureInfo.InvariantCulture)} s)  pts {f.Pts.ToString("R", CultureInfo.InvariantCulture)}" +
                     $"{(f.Filled ? "  filled" : "")}  map {f.MapId?.ToString(CultureInfo.InvariantCulture) ?? "?"}  camera {(f.Camera is { } c ? $"{c.X},{c.Y}" : "?")}" +
                     $"  detector {(f.Detect ?? $"skipped: {f.Skip}")}{(pixelSource is null ? "" : $"  pixels from {pixelSource}")}";
        foreach (var layer in OverlayLayers.Names.Reverse())
        {
            if (!layers.Contains(layer)) continue;
            foreach (var item in f.Layer(layer))
            {
                var where = (item.Rect is { } r ? $" rect {r.X},{r.Y},{r.W},{r.H}" : "") + (item.Point("feet") is { } p ? $" feet {p.X},{p.Y}" : "") +
                            (item.Point("point") is { } q ? $" point {q.X},{q.Y}" : "") + (item.Line() is { } l ? $" line {IOverlayFormat(l)}" : "");
                yield return $"  {item.Layer,-7} {item.Kind,-9} {(item.Label is { } lb ? $"\"{lb}\"" : "-")}{where}   {item.FieldText("rect", "feet", "point", "line", "label")}";
            }
        }
    }

    private static string IOverlayFormat(int[][] line) => string.Join(" ", line.Select(p => $"{p[0]},{p[1]}"));

    public static int List(Session session, Options o)
    {
        var stream = LoadStream(session, o);
        var (frames, _) = Select(stream, o);
        var pixels = new RecordingPixels(session.Recording);
        foreach (var f in frames)
        {
            foreach (var line in ListFrame(f, o.DrawnLayers, pixels.SourceOf(f.Sequence))) Console.WriteLine(line);
            Console.WriteLine();
        }
        return 0;
    }

    public static int EntitySheets(Session session, Options o)
    {
        var stream = LoadStream(session, o);
        var dir = o.Out ?? session.BakeDir("entity-sheets");
        var frames = Sheets.EntitySheetFrames(stream);
        var pixels = new RecordingPixels(session.Recording);
        var images = pixels.Load(frames.Select(f => f.Sequence));
        var tiles = frames.Select(f => (f, Sheets.Tile(f, images[f.Sequence], pixels.Width, pixels.Height))).ToList();
        var paths = Sheets.Write(dir, "entity-sheet", tiles, Sheets.FramesPerSheet, 2);
        Console.WriteLine($"entity sheets: {string.Join(", ", paths.Select(Path.GetFullPath))}");
        return 0;
    }

    /// <summary>Per captured frame: mob detections, matched ones, an EXP gain (a kill), drops, and whether the pet is near a mob.</summary>
    private sealed record Activity(OverlayFrame Frame, int Mobs, int Matched, int Kills, int Drops, int PetNearMob);

    /// <summary>Weights of <see cref="Windows"/>' activity score (chosen, not tuned): a kill or a drop is rarer than a mob sighting.</summary>
    public const int KillWeight = 10, DropWeight = 4, PetWeight = 3;

    /// <summary>The pet counts as near a mob within this many pixels (feet to feet).</summary>
    public const int PetNearPixels = 150;

    public static int Windows(Session session, Options o)
    {
        var stream = LoadStream(session, o);
        var seconds = o.Seconds ?? 15;
        var frames = stream.Frames.Where(f => !f.Filled).ToList();
        var activity = new List<Activity>();
        long? lastExp = null;
        int? lastMap = null;
        foreach (var f in frames)
        {
            var ents = f.Layer(OverlayLayers.Entity).ToList();
            var mobs = ents.Where(e => e.Kind == "mob").ToList();
            var exp = f.Layer(OverlayLayers.Hud).FirstOrDefault(i => i.Str("name") == "status.exp")?.Long("exp");
            if (f.MapId != lastMap) { lastExp = null; lastMap = f.MapId; }
            var kill = exp is { } e1 && lastExp is { } e0 && e1 > e0 ? 1 : 0;
            if (exp is not null) lastExp = exp;
            var pets = ents.Where(e => e.Kind == "pet").Select(e => e.Point("feet")).OfType<(int X, int Y)>().ToList();
            var near = pets.Any(p => mobs.Any(m => m.Point("feet") is { } q && Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) <= PetNearPixels)) ? 1 : 0;
            activity.Add(new Activity(f, mobs.Count, mobs.Count(m => m.Str("evidence") == "matched"), kill, ents.Count(e => e.Kind == "drop"), near));
        }
        var windows = new List<(int Start, int End, int Score, int Mobs, int Matched, int Kills, int Drops, int Pet)>();
        for (var i = 0; i < activity.Count; i++)
        {
            var end = i;
            while (end + 1 < activity.Count && activity[end + 1].Frame.Time < activity[i].Frame.Time + seconds - 1e-6) end++;
            var w = activity.GetRange(i, end - i + 1);
            int mobs = w.Sum(a => a.Mobs), matched = w.Sum(a => a.Matched), kills = w.Sum(a => a.Kills), drops = w.Sum(a => a.Drops), pet = w.Sum(a => a.PetNearMob);
            windows.Add((i, end, mobs + KillWeight * kills + DropWeight * drops + PetWeight * pet, mobs, matched, kills, drops, pet));
        }
        Console.WriteLine($"windows of {seconds:0.#} s in {stream.Path}: score = mob detections + {KillWeight} x kills (EXP gains) + {DropWeight} x drop detections + {PetWeight} x frames with the pet within {PetNearPixels} px of a mob");
        var taken = new List<(int Start, int End)>();
        foreach (var w in windows.OrderByDescending(w => w.Score).ThenBy(w => w.Start))
        {
            if (taken.Any(t => w.Start <= t.End && t.Start <= w.End)) continue;
            taken.Add((w.Start, w.End));
            var a = activity[w.Start].Frame;
            var b = activity[w.End].Frame;
            // The start rounded down to a tenth, so --from selects the window's first frame.
            Console.WriteLine($"  --from {OverlayFrame.FormatTime(Math.Floor(a.Time * 10) / 10)} --seconds {seconds:0.#}: frames {a.Sequence:D7}..{b.Sequence:D7} ({w.End - w.Start + 1}), score {w.Score}: mob detections {w.Mobs} (matched {w.Matched}), kills {w.Kills}, drop detections {w.Drops}, pet near a mob in {w.Pet}");
            if (taken.Count == 5) break;
        }
        return 0;
    }

    private static string Excerpt(string s, int at) => s.Substring(Math.Max(0, at - 30), Math.Min(s.Length - Math.Max(0, at - 30), 60));

    /// <summary>
    /// Validates a stream: every line parses and re-serializes to the same bytes, every item conforms to the layer registry,
    /// and the capture sequences and pts join frames.jsonl: a keyframe stream holds every keyframe once, in order; a video
    /// stream every video frame in order, the captured ones with their own sequence and pts, the filled ones with the
    /// sequence of the frame they repeat.
    /// </summary>
    public static int Check(Session session, Options o)
    {
        var stream = LoadStream(session, o);
        var problems = new List<string>();
        void Problem(string p) { if (problems.Count < 20) Console.WriteLine($"  problem: {p}"); problems.Add(p); }
        // Round trip.
        var headerAgain = Encoding.UTF8.GetString(OverlayJson.Header(stream.Header));
        if (headerAgain != stream.HeaderLine) Problem("the header does not re-serialize to the same bytes");
        for (var i = 0; i < stream.Lines.Count; i++)
            if (Encoding.UTF8.GetString(OverlayJson.Frame(stream.Frames[i])) is var again && again != stream.Lines[i])
            {
                var at = 0;
                while (at < again.Length && at < stream.Lines[i].Length && again[at] == stream.Lines[i][at]) at++;
                Problem($"line {i + 2} does not re-serialize to the same bytes: at {at}, '{Excerpt(stream.Lines[i], at)}' became '{Excerpt(again, at)}'");
            }
        // Registry.
        var items = 0;
        foreach (var f in stream.Frames)
            foreach (var item in f.Items)
            {
                items++;
                foreach (var p in OverlayLayers.Check(item)) Problem($"seq {f.Sequence}: {p}");
            }
        // Join with frames.jsonl.
        var index = RecordingIndex.Load(session.Recording) ?? throw new BakeException($"{session.Recording} has no frames.jsonl");
        var captured = stream.Frames.Where(f => !f.Filled).ToList();
        var fills = stream.Frames.Count - captured.Count;
        int ptsEqual = 0, unrecorded = 0;
        foreach (var f in captured)
        {
            if (index.BySequence(f.Sequence) is not { } r) { unrecorded++; continue; }
            if (r.Pts.Equals(f.Pts)) ptsEqual++; else Problem($"seq {f.Sequence}: pts {f.Pts:R} in the stream, {r.Pts:R} in frames.jsonl");
        }
        string coverage;
        if (stream.Header.Replay == "keyframes")
        {
            var files = RecordingIndex.KeyframeFiles(session.Recording).Select(p => RecordingIndex.KeyframeSequence(p)!.Value).ToList();
            if (!files.SequenceEqual(stream.Frames.Select(f => f.Sequence))) Problem("the stream's sequences are not the keyframe files', in order");
            if (fills > 0) Problem($"{fills} filled frames in a keyframe stream");
            coverage = $"{stream.Frames.Count} lines for {files.Count} keyframe files, sequences in file order {(files.SequenceEqual(stream.Frames.Select(f => f.Sequence)) ? "match" : "DIFFER")}";
        }
        else
        {
            var expected = new List<(long Seq, bool Filled)>();
            var video = index.VideoRecords;
            for (var k = 0; k < video.Count; k++)
            {
                expected.Add((video[k].Sequence, false));
                var gap = k + 1 < video.Count ? video[k + 1].VideoFrame - video[k].VideoFrame - 1 : 0;
                for (var g = 0; g < gap; g++) expected.Add((video[k].Sequence, true));
            }
            var actual = stream.Frames.Select(f => (f.Sequence, f.Filled)).ToList();
            // The video may end with fills after the last record; the stream then has more lines than the records predict.
            var prefix = actual.Take(expected.Count).SequenceEqual(expected);
            var trailing = actual.Skip(expected.Count).All(a => a.Filled && a.Sequence == video[^1].Sequence);
            if (!prefix || !trailing) Problem($"the stream's (sequence, filled) pairs do not follow frames.jsonl's video order ({actual.Count} lines, {expected.Count} expected before trailing fills)");
            coverage = $"{stream.Frames.Count} lines for {video.Count} recorded video frames and {fills} fills; video order {(prefix && trailing ? "matches" : "DIFFERS")}";
        }
        var keys = stream.Frames.Select(f => (f.Sequence, f.Pts)).ToList();
        if (keys.Distinct().Count() != keys.Count) Problem("(sequence, pts) is not unique");
        Console.WriteLine($"check {stream.Path}: {stream.Frames.Count} frames, {items} items; round trip and registry checked");
        Console.WriteLine($"  join: {coverage}; pts equal to frames.jsonl on {ptsEqual} of {captured.Count} captured frames{(unrecorded > 0 ? $", {unrecorded} without a capture record (pts extrapolated)" : "")}; (sequence, pts) unique");
        Console.WriteLine(problems.Count == 0 ? "  ok" : $"  {problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }
}
