using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace OakSoul.Capture.Replay;

/// <summary>
/// Plays a recorder session folder back as frames: either its video (every frame; exact only when it was encoded
/// losslessly, see <see cref="LosslessVideo"/>) or its PNG keyframes (sparse, always the captured pixels). Frames are
/// decoded by ffmpeg into BGRA and optionally cropped at the top, exactly as the recorder's --crop-top would have done
/// at capture time.
/// <para>
/// Each frame keeps the capture sequence and pts it was recorded under (<see cref="Frame.Sequence"/>,
/// <see cref="Frame.Pts"/>): a keyframe from its file name (the pts from <c>frames.jsonl</c>), a video frame through the
/// <c>videoFrame</c> field of <c>frames.jsonl</c>. A video frame with no record of its own was written by the recorder to
/// fill an idle gap: it is delivered with <see cref="Frame.Filled"/> set, the sequence of the frame it repeats and that
/// frame's pts plus its offset on the video timeline. <see cref="Frame.Index"/> counts the frames delivered and
/// <see cref="Frame.Clock"/> is the playback position, (Index - 1) / <see cref="Fps"/>. A session without
/// <c>frames.jsonl</c> is numbered by position instead (sequence = index, pts = clock).
/// </para>
/// </summary>
public sealed class RecordingFrameSource : IFrameSource
{
    private readonly string _sessionDir;
    private readonly bool _keyframes;
    private readonly int _cropTop;
    private readonly double _speed;
    private readonly int _videoFps;
    private readonly RecordingIndex? _index;
    private Process? _ffmpeg;

    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    public string Description { get; }
    public bool NeedsMainLoop => false;

    /// <summary>
    /// Whether the session's video holds the captured pixels: its <c>session.json</c> says <c>"lossless": true</c>
    /// (written by the recorder since this field exists), or, for older sessions, its <c>encoderArgs</c> are a lossless
    /// encoder (<see cref="VideoEncoding.IsLossless"/>, e.g. the lossless preset <c>-c:v libx264rgb -preset veryfast -qp 0</c>).
    /// </summary>
    public bool LosslessVideo { get; }

    /// <summary>Keyframes are lossless PNGs; the video is lossless when <see cref="LosslessVideo"/>.</summary>
    public bool Lossless => _keyframes || LosslessVideo;

    /// <summary>Whether this replays the keyframes rather than the video.</summary>
    public bool Keyframes => _keyframes;

    public string SessionDir => _sessionDir;

    /// <summary>The session's capture records (<c>frames.jsonl</c>), or null for a session without them.</summary>
    public RecordingIndex? Index => _index;

    /// <summary>The pts of the session's first capture record: <see cref="Frame.Pts"/> minus this is the time into the session.</summary>
    public double OriginPts => _index?.OriginPts ?? 0;

    /// <summary>Keyframes whose capture record was missing from <c>frames.jsonl</c> (numbered by file name, the pts extrapolated).</summary>
    public int UnrecordedKeyframes { get; private set; }

    /// <param name="speed">1 plays at the recorded rate, 2 twice as fast, 0 as fast as frames decode.</param>
    public RecordingFrameSource(string sessionDir, bool keyframes = false, int cropTop = 0, double speed = 1)
    {
        _sessionDir = sessionDir;
        _keyframes = keyframes;
        _cropTop = cropTop;
        _speed = speed;
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json")));
        var root = session.RootElement;
        Width = root.GetProperty("width").GetInt32();
        Height = root.GetProperty("height").GetInt32() - cropTop;
        _videoFps = root.GetProperty("fps").GetInt32();
        Fps = keyframes ? 1 : _videoFps;
        _index = RecordingIndex.Load(sessionDir);
        LosslessVideo = IsLosslessVideo(root);
        Description = $"{(keyframes ? "keyframes" : LosslessVideo ? "lossless video" : "lossy video")} of {sessionDir}{(cropTop > 0 ? $", top {cropTop} rows cropped" : "")}";
    }

    /// <summary>Whether a session's video holds the captured pixels, from its <c>session.json</c> (see <see cref="LosslessVideo"/>).</summary>
    public static bool IsLosslessVideo(JsonElement session) =>
        session.TryGetProperty("lossless", out var lossless) && lossless.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? lossless.GetBoolean()
            : VideoEncoding.IsLossless(session.TryGetProperty("encoderArgs", out var args) && args.ValueKind == JsonValueKind.String ? args.GetString() : null);

    public async IAsyncEnumerable<Frame> Frames([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var input = _keyframes
            ? $"-framerate 1 -pattern_type glob -i \"{Path.Combine(_sessionDir, "keyframes", "*.png")}\""
            : $"-i \"{Directory.GetFiles(_sessionDir, "video.*").Single()}\"";
        var crop = _cropTop > 0 ? $"-vf crop={Width}:{Height}:0:{_cropTop}" : "";
        _ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error {input} {crop} -f rawvideo -pix_fmt bgra -")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("could not start ffmpeg");
        var stdout = _ffmpeg.StandardOutput.BaseStream;

        var frameBytes = Width * Height * 4;
        var started = Stopwatch.StartNew();
        // ffmpeg's glob delivers the keyframes in file-name order, which is capture order (the names are the zero-padded sequence).
        var keyframeFiles = _keyframes ? RecordingIndex.KeyframeFiles(_sessionDir) : [];
        long sequence = 0;
        while (!cancellation.IsCancellationRequested)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
            var read = 0;
            while (read < frameBytes)
            {
                var n = await stdout.ReadAsync(buffer.AsMemory(read, frameBytes - read), cancellation);
                if (n == 0) break;
                read += n;
            }
            if (read < frameBytes) { ArrayPool<byte>.Shared.Return(buffer); yield break; }

            var clock = sequence / Fps;
            if (_speed > 0)
            {
                var due = TimeSpan.FromSeconds(clock / _speed) - started.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due, cancellation);
            }
            sequence++;
            var (captureSequence, pts, filled) = Identify(sequence, clock, keyframeFiles);
            yield return new Frame(buffer, Width, Height, captureSequence, pts, DateTime.UtcNow, sequence, clock, filled);
        }
    }

    /// <summary>The capture sequence and pts of the <paramref name="index"/>-th frame delivered (1-based), and whether it is a fill.</summary>
    private (long Sequence, double Pts, bool Filled) Identify(long index, double clock, string[] keyframeFiles)
    {
        if (_keyframes)
        {
            if (index > keyframeFiles.Length || RecordingIndex.KeyframeSequence(keyframeFiles[index - 1]) is not { } seq) return (index, clock, false);
            if (_index?.BySequence(seq) is { } record) return (seq, record.Pts, false);
            // A keyframe written after the last metadata flush (a recorder stopped without its clean stop): its pts is
            // extrapolated from the last record before it at the session's frame rate.
            UnrecordedKeyframes++;
            var before = _index?.Records.LastOrDefault(r => r.Sequence < seq);
            return (seq, before is null ? clock : before.Pts + (double)(seq - before.Sequence) / _videoFps, false);
        }
        if (_index?.ResolveVideoFrame(index - 1) is not { } shown) return (index, clock, false);
        return shown.Fill == 0 ? (shown.Record.Sequence, shown.Record.Pts, false)
            : (shown.Record.Sequence, shown.Record.Pts + (double)shown.Fill / _videoFps, true);
    }

    public void Dispose()
    {
        if (_ffmpeg is { HasExited: false })
        {
            try { _ffmpeg.Kill(); } catch (InvalidOperationException) { }
        }
        _ffmpeg?.Dispose();
    }
}
