using System.Diagnostics;
using System.Text.Json;
using OakSoul.Capture;
using OakSoul.Recorder;

var options = Options.Parse(args);
if (options is null)
{
    Options.PrintUsage();
    return 2;
}

ScreenCapture.Initialize();

if (options.List)
{
    foreach (var w in ScreenCapture.ListWindows().OrderBy(w => w.App).ThenBy(w => w.Title))
        Console.WriteLine($"{w.Id,7}  pid={w.Pid,-6} {w.Width,5:0}x{w.Height,-5:0} {(w.OnScreen ? "on " : "off")}  {w.App}: {w.Title}");
    return 0;
}

var window = SelectWindow(options, quiet: false);
if (window is null) return 1;

// A window that was created a moment ago can be listed with a zero-size frame; give it a few seconds to settle.
for (var attempt = 0; attempt < 15 && (window.Width <= 0 || window.Height <= 0); attempt++)
{
    ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(200));
    window = SelectWindow(options, quiet: true) ?? window;
}
if (window.Width <= 0 || window.Height <= 0)
{
    Console.Error.WriteLine($"error: window {window.Id} reports a zero-size frame; refusing to capture it.");
    return 1;
}
// A window that is still being laid out can also report a transient size; wait for two consecutive listings to agree.
for (var attempt = 0; attempt < 10; attempt++)
{
    ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(300));
    var again = SelectWindow(options, quiet: true) ?? window;
    var stable = again.Width == window.Width && again.Height == window.Height;
    window = again;
    if (stable) break;
}

var requestedWidth = options.Width ?? (int)window.Width;
var requestedHeight = options.Height ?? (int)window.Height;
var (encoderArgs, extension) = options.ResolveEncoder();

var sessionName = options.SessionName ?? $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(window.Title)}";
var sessionDir = Path.GetFullPath(Path.Combine(options.OutputRoot, sessionName));
Directory.CreateDirectory(sessionDir);
var videoPath = Path.Combine(sessionDir, $"video.{extension}");

Console.WriteLine($"window   {window}");
Console.WriteLine($"capture  {requestedWidth}x{requestedHeight} @ {options.Fps} fps, encoder: {encoderArgs}");
Console.WriteLine($"session  {sessionDir}");
Console.WriteLine(options.Seconds > 0 ? $"stopping after {options.Seconds:0.#} s" : "press Ctrl-C to stop");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var startedUtc = DateTime.UtcNow;
var video = new FfmpegVideoSink(videoPath, options.Fps, encoderArgs, Path.Combine(sessionDir, "ffmpeg.log"));
var keyframes = new KeyframeWriter(sessionDir, "keyframes", options.KeyframeEvery);
using var metadata = new MetadataWriter(Path.Combine(sessionDir, "frames.jsonl"));
CaptureStream stream;
try
{
    stream = CaptureStream.Start(window.Id, requestedWidth, requestedHeight, options.Fps, options.Queue);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    video.Dispose();
    return 1;
}
using var streamScope = stream;

Exception? consumerError = null;
var consumer = Task.Run(async () =>
{
    long consumed = 0;
    try
    {
        await foreach (var frame in stream.Frames.ReadAllAsync())
        {
            using (frame)
            {
                if (consumed == 0) Console.WriteLine($"frames    {frame.Width}x{frame.Height}, {frame.ByteLength / 1e6:0.00} MB each");
                var videoIndex = video.Write(frame);
                var keyframe = keyframes.MaybeCapture(frame);
                metadata.Write(new FrameRecord(frame.Sequence, frame.Pts, frame.WallClockUtc, videoIndex, keyframe));
                consumed++;
            }
        }
    }
    catch (Exception ex)
    {
        consumerError = ex;
    }
    return consumed;
});

var stopwatch = Stopwatch.StartNew();
var nextReport = TimeSpan.FromSeconds(5);
while (!cancellation.IsCancellationRequested && !stream.Failed && !consumer.IsCompleted
       && (options.Seconds <= 0 || stopwatch.Elapsed.TotalSeconds < options.Seconds))
{
    ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(100));
    if (stopwatch.Elapsed >= nextReport)
    {
        Console.WriteLine($"t={stopwatch.Elapsed.TotalSeconds,6:0.0}s  delivered={stream.DeliveredFrames}  " +
                          $"fps={stream.DeliveredFrames / stopwatch.Elapsed.TotalSeconds:0.0}  idle={stream.IdleFrames}  " +
                          $"dropped={stream.DroppedFrames}  keyframes={keyframes.Written}");
        nextReport += TimeSpan.FromSeconds(5);
    }
}

stream.Stop();
var consumedFrames = await consumer;
await keyframes.DrainAsync();
var videoBytes = video.Close();
video.Dispose();
var elapsed = stopwatch.Elapsed.TotalSeconds;

var summary = new
{
    window,
    width = video.Width,
    height = video.Height,
    fps = options.Fps,
    encoderArgs,
    video = Path.GetFileName(videoPath),
    startedUtc,
    endedUtc = DateTime.UtcNow,
    durationSeconds = elapsed,
    deliveredFrames = stream.DeliveredFrames,
    consumedFrames,
    droppedFrames = stream.DroppedFrames,
    idleFrames = stream.IdleFrames,
    filledVideoFrames = video.FilledFrames,
    skippedVideoFrames = video.SkippedFrames,
    cutSeconds = video.CutSeconds,
    ffmpegExitCode = video.ExitCode,
    keyframes = keyframes.Written,
    videoBytes,
    streamFailed = stream.Failed,
    error = consumerError?.Message,
};
File.WriteAllText(Path.Combine(sessionDir, "session.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(MetadataWriter.JsonOptions) { WriteIndented = true }));

Console.WriteLine($"done: {consumedFrames} frames in {elapsed:0.0}s ({consumedFrames / Math.Max(elapsed, 0.001):0.0} fps), " +
                  $"dropped={stream.DroppedFrames}, idle={stream.IdleFrames}, filled={video.FilledFrames}, skipped={video.SkippedFrames}, cut={video.CutSeconds:0.0}s, keyframes={keyframes.Written}, " +
                  $"video={videoBytes / 1e6:0.0} MB -> {videoBytes / Math.Max(elapsed, 0.001) * 3600 / 1e9:0.00} GB/hour");
if (stream.Failed) Console.Error.WriteLine("warning: ScreenCaptureKit stopped the stream on its own (window closed?)");
if (consumerError is not null)
{
    Console.Error.WriteLine($"error: recording stopped early: {consumerError.Message}");
    Console.Error.WriteLine($"see {Path.Combine(sessionDir, "ffmpeg.log")}");
    return 1;
}
return 0;

static WindowInfo? SelectWindow(Options options, bool quiet)
{
    var windows = ScreenCapture.ListWindows();
    List<WindowInfo> matches;
    if (options.WindowId is { } id)
        matches = windows.Where(w => w.Id == id).ToList();
    else if (options.Pid is { } pid)
        matches = windows.Where(w => w.Pid == pid && w.Title == options.Title).ToList();
    else if (options.App is { } app)
        matches = windows.Where(w => w.App == app && w.Title == options.Title).ToList();
    else
    {
        if (!quiet) Console.Error.WriteLine("error: --title needs --pid or --app so the match is unambiguous.");
        return null;
    }

    if (matches.Count == 1) return matches[0];
    if (quiet) return null;
    Console.Error.WriteLine(matches.Count == 0
        ? "error: no window matched; run with --list to see shareable windows."
        : $"error: {matches.Count} windows matched; pick one with --window-id:");
    foreach (var m in matches) Console.Error.WriteLine($"  {m}");
    return null;
}

static string Sanitize(string title)
{
    var cleaned = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
    return string.IsNullOrEmpty(cleaned) ? "window" : cleaned[..Math.Min(cleaned.Length, 40)];
}
