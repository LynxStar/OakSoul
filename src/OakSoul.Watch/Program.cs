using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.Vision.Readers;

// OakSoul.Watch: run the HUD readers live on a captured window and print values as they change.
//   --list | --window-id N | --pid N --title "T" | --app "Name" --title "T"
//   --crop-top N (28 for a windowed client)   --fps N (default 30)   --seconds S (default 0 = until Ctrl-C)

uint? windowId = null; int? pid = null; string? app = null, title = null;
int cropTop = 0, fps = 30; double seconds = 0; var list = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--list": list = true; break;
        case "--window-id": windowId = uint.Parse(Next()); break;
        case "--pid": pid = int.Parse(Next()); break;
        case "--app": app = Next(); break;
        case "--title": title = Next(); break;
        case "--crop-top": cropTop = int.Parse(Next()); break;
        case "--fps": fps = int.Parse(Next()); break;
        case "--seconds": seconds = double.Parse(Next()); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

ScreenCapture.Initialize();
var windows = ScreenCapture.ListWindows();
if (list)
{
    foreach (var w in windows.OrderBy(w => w.App).ThenBy(w => w.Title))
        Console.WriteLine($"{w.Id,7}  pid={w.Pid,-6} {w.Width,5:0}x{w.Height,-5:0} {(w.OnScreen ? "on " : "off")}  {w.App}: {w.Title}");
    return 0;
}

var matches = windowId is { } id ? windows.Where(w => w.Id == id).ToList()
    : pid is { } p && title is not null ? windows.Where(w => w.Pid == p && w.Title == title).ToList()
    : app is not null && title is not null ? windows.Where(w => w.App == app && w.Title == title).ToList()
    : [];
if (matches.Count != 1)
{
    Console.Error.WriteLine(matches.Count == 0 ? "error: choose exactly one window with --window-id, or --pid/--app plus --title (see --list)." : "error: several windows match; use --window-id.");
    foreach (var m in matches) Console.Error.WriteLine($"  {m}");
    return 1;
}
var window = matches[0];
if (window.Width <= 0 || window.Height <= 0) { Console.Error.WriteLine("error: window reports a zero-size frame."); return 1; }
CaptureRect? crop = cropTop > 0 ? new CaptureRect(0, cropTop, (int)window.Width, (int)window.Height - cropTop) : null;
Console.WriteLine($"window   {window}{(crop is null ? "" : $"  crop {crop}")}");

var layout = HudLayout.Native1366x768;
var tracker = new MapNameTracker();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var stream = CaptureStream.Start(window.Id, 0, 0, fps, 8, crop);

FrameGeometry? geometry = null;
State? last = null;
long frames = 0;
double readMs = 0;
var consumer = Task.Run(async () =>
{
    await foreach (var frame in stream.Frames.ReadAllAsync())
    {
        using (frame)
        {
            var sw = Stopwatch.StartNew();
            if (geometry is null || geometry.FrameWidth != frame.Width || geometry.FrameHeight != frame.Height)
            {
                geometry = FrameGeometry.Detect(layout, frame.Pixels, frame.Width, frame.Height);
                Console.WriteLine($"geometry {geometry}");
                if (geometry.BarsConfirmed == false)
                    Console.WriteLine("warning: the frame does not look like the native layout; for a windowed client pass --crop-top 28");
            }
            var status = StatusReader.Read(geometry, frame.Pixels);
            var minimap = MinimapReader.Read(geometry, frame.Pixels);
            if (minimap.Valid) tracker.Update(geometry, frame.Pixels, minimap.Panel);
            var state = new State(status.HpValue, status.MpValue, status.ExpValue,
                tracker.Current is { } n ? $"{n.Region} / {n.Map}" : null,
                minimap.SelfInMap is { } s ? ((int)Math.Round(s.X), (int)Math.Round(s.Y)) : null,
                minimap.Valid ? minimap.Players : null);
            readMs += sw.Elapsed.TotalMilliseconds;
            frames++;
            if (state != last)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {state.Describe(last)}");
                last = state;
            }
        }
    }
});

var started = Stopwatch.StartNew();
var nextBeat = TimeSpan.FromSeconds(10);
while (!cancellation.IsCancellationRequested && !stream.Failed && !consumer.IsCompleted && (seconds <= 0 || started.Elapsed.TotalSeconds < seconds))
{
    ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(100));
    if (started.Elapsed >= nextBeat)
    {
        Console.WriteLine($"-- {started.Elapsed.TotalSeconds:0}s: {frames} frames, {frames / started.Elapsed.TotalSeconds:0.0} fps, read {readMs / Math.Max(frames, 1):0.0} ms/frame, dropped {stream.DroppedFrames}, idle {stream.IdleFrames}");
        nextBeat += TimeSpan.FromSeconds(10);
    }
}
stream.Stop();
await consumer;
Console.WriteLine($"done: {frames} frames in {started.Elapsed.TotalSeconds:0.0}s, read {readMs / Math.Max(frames, 1):0.0} ms/frame");
return 0;

/// <summary>Everything the watcher reports; a change in any field prints a line.</summary>
sealed record State((long Current, long Maximum)? Hp, (long Current, long Maximum)? Mp, (long Total, double Percent)? Exp, string? Map, (int X, int Y)? Position, int? Players)
{
    public string Describe(State? previous)
    {
        string Mark<T>(T value, T? before) => previous is not null && !Equals(value, before) ? "*" : " ";
        var hp = Hp is { } h ? $"{h.Current}/{h.Maximum} ({100.0 * h.Current / h.Maximum:0}%)" : "?";
        var mp = Mp is { } m ? $"{m.Current}/{m.Maximum}" : "?";
        var exp = Exp is { } e ? $"{e.Total:N0} {e.Percent:0.00}%" : "?";
        var pos = Position is { } p ? $"{p.X},{p.Y}" : "?";
        return $"HP{Mark(Hp, previous?.Hp)}{hp,-16} MP{Mark(Mp, previous?.Mp)}{mp,-10} EXP{Mark(Exp, previous?.Exp)}{exp,-16} map{Mark(Map, previous?.Map)}{Map ?? "?",-28} pos{Mark(Position, previous?.Position)}{pos,-8} players{Mark(Players, previous?.Players)}{Players?.ToString() ?? "?"}";
    }
}
