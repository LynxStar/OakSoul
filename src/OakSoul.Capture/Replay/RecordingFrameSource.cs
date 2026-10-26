using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace OakSoul.Capture.Replay;

/// <summary>
/// Plays a recorder session folder back as frames: either its video (every frame; exact only when it was encoded
/// losslessly, see <see cref="LosslessVideo"/>) or its PNG keyframes (sparse, always the captured pixels). Frames are
/// decoded by ffmpeg into BGRA and optionally cropped at the top, exactly as the recorder's --crop-top would have done
/// at capture time.
/// </summary>
public sealed class RecordingFrameSource : IFrameSource
{
    private readonly string _sessionDir;
    private readonly bool _keyframes;
    private readonly int _cropTop;
    private readonly double _speed;
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
        Fps = keyframes ? 1 : root.GetProperty("fps").GetInt32();
        LosslessVideo = root.TryGetProperty("lossless", out var lossless) && lossless.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? lossless.GetBoolean()
            : VideoEncoding.IsLossless(root.TryGetProperty("encoderArgs", out var args) && args.ValueKind == JsonValueKind.String ? args.GetString() : null);
        Description = $"{(keyframes ? "keyframes" : LosslessVideo ? "lossless video" : "lossy video")} of {sessionDir}{(cropTop > 0 ? $", top {cropTop} rows cropped" : "")}";
    }

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

            var pts = sequence / Fps;
            if (_speed > 0)
            {
                var due = TimeSpan.FromSeconds(pts / _speed) - started.Elapsed;
                if (due > TimeSpan.Zero) await Task.Delay(due, cancellation);
            }
            sequence++;
            yield return new Frame(buffer, Width, Height, sequence, pts, DateTime.UtcNow);
        }
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
