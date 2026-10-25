using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.Vision.Readers;

// OakSoul.Watch: run the HUD readers on a live window or a recording and print values as they change.
//   --list | --window-id N | --pid N --title "T" | --app "Name" --title "T"      live capture
//   --recording DIR [--keyframes] [--speed X]                                      replay a recorder session
//   --crop-top N (28 for a windowed client)   --fps N (default 30)   --seconds S (default 0 = until the end or Ctrl-C)

using OakSoul.Capture.Replay;

uint? windowId = null; int? pid = null; string? app = null, title = null, recording = null;
int cropTop = 0, fps = 30; double seconds = 0, speed = 1; var list = false; var keyframes = false;
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
        case "--recording": recording = Next(); break;
        case "--keyframes": keyframes = true; break;
        case "--speed": speed = double.Parse(Next()); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

ScreenCapture.Initialize();
IFrameSource source;
if (recording is not null)
{
    source = new RecordingFrameSource(recording, keyframes, cropTop, speed);
}
else
{
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
source = new CaptureFrameSource(CaptureStream.Start(window.Id, 0, 0, fps, 8, crop), $"{window}{(crop is null ? "" : $"  crop {crop}")}");
}
Console.WriteLine($"source   {source.Description}");

var layout = HudLayout.Native1366x768;
var tracker = new MapNameTracker();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var frameSource = source;
var identity = new IdentityReader(layout);

FrameGeometry? geometry = null;
State? last = null;
long frames = 0;
double readMs = 0;
var consumer = Task.Run(async () =>
{
    await foreach (var frame in frameSource.Frames(cancellation.Token))
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
            identity.Update(geometry, frame.Pixels);
            var state = new State(identity.Current?.Level, identity.Current?.Job, identity.Current?.Name, status.HpValue, status.MpValue, status.ExpValue,
                tracker.Current is { } n ? $"{n.Region} / {n.Map}" : null,
                minimap.SelfInMap is { } s ? ((int)Math.Round(s.X), (int)Math.Round(s.Y)) : null,
                minimap.Valid ? minimap.Players : null);
            readMs += sw.Elapsed.TotalMilliseconds;
            frames++;
            if (state != last)
            {
                var stamp = source.NeedsMainLoop ? DateTime.Now.ToString("HH:mm:ss.fff") : $"t={TimeSpan.FromSeconds(frame.Pts):m\\:ss}  ";
                Console.WriteLine($"{stamp}  {state.Describe(last)}");
                last = state;
            }
        }
    }
});

var started = Stopwatch.StartNew();
var nextBeat = TimeSpan.FromSeconds(10);
var capture = (source as CaptureFrameSource)?.Stream;
while (!cancellation.IsCancellationRequested && !(capture?.Failed ?? false) && !consumer.IsCompleted && (seconds <= 0 || started.Elapsed.TotalSeconds < seconds))
{
    if (source.NeedsMainLoop) ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(100)); else await Task.Delay(100);
    if (started.Elapsed >= nextBeat)
    {
        Console.WriteLine($"-- {started.Elapsed.TotalSeconds:0}s: {frames} frames, {frames / started.Elapsed.TotalSeconds:0.0} fps, read {readMs / Math.Max(frames, 1):0.0} ms/frame{(capture is null ? "" : $", dropped {capture.DroppedFrames}, idle {capture.IdleFrames}")}");
        nextBeat += TimeSpan.FromSeconds(10);
    }
}
capture?.Stop();
cancellation.Cancel();
try { await consumer; } catch (OperationCanceledException) { }
Console.WriteLine($"done: {frames} frames in {started.Elapsed.TotalSeconds:0.0}s, read {readMs / Math.Max(frames, 1):0.0} ms/frame");
return 0;

/// <summary>Everything the watcher reports; a change in any field prints a line.</summary>
sealed record State(int? Level, string? Job, string? Name, (long Current, long Maximum)? Hp, (long Current, long Maximum)? Mp, (long Total, double Percent)? Exp, string? Map, (int X, int Y)? Position, int? Players)
{
    public string Describe(State? previous)
    {
        string Mark<T>(T value, T? before) => previous is not null && !Equals(value, before) ? "*" : " ";
        var hp = Hp is { } h ? $"{h.Current}/{h.Maximum} ({100.0 * h.Current / h.Maximum:0}%)" : "?";
        var mp = Mp is { } m ? $"{m.Current}/{m.Maximum}" : "?";
        var exp = Exp is { } e ? $"{e.Total:N0} {e.Percent:0.00}%" : "?";
        var pos = Position is { } p ? $"{p.X},{p.Y}" : "?";
        var who = $"Lv{Mark(Level, previous?.Level)}{Level?.ToString() ?? "?",-4}{Mark(Job, previous?.Job)}{Job ?? "?",-22}{Mark(Name, previous?.Name)}{Name ?? "?",-12}";
        return $"{who} HP{Mark(Hp, previous?.Hp)}{hp,-16} MP{Mark(Mp, previous?.Mp)}{mp,-10} EXP{Mark(Exp, previous?.Exp)}{exp,-16} map{Mark(Map, previous?.Map)}{Map ?? "?",-28} pos{Mark(Position, previous?.Position)}{pos,-8} players{Mark(Players, previous?.Players)}{Players?.ToString() ?? "?"}";
    }
}
