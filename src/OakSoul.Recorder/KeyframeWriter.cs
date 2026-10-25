using OakSoul.Capture;

namespace OakSoul.Recorder;

/// <summary>Saves a lossless PNG of the frame every N seconds of presentation time, encoding off the hot path.</summary>
public sealed class KeyframeWriter
{
    private readonly string _directory;
    private readonly string _relativeDirectory;
    private readonly double _interval;
    private readonly List<Task> _pending = [];
    private readonly SemaphoreSlim _concurrency = new(2);
    private double? _lastPts;

    public long Written { get; private set; }

    public KeyframeWriter(string sessionDirectory, string relativeDirectory, double intervalSeconds)
    {
        _directory = Path.Combine(sessionDirectory, relativeDirectory);
        _relativeDirectory = relativeDirectory;
        _interval = intervalSeconds;
        if (_interval > 0) Directory.CreateDirectory(_directory);
    }

    /// <summary>Returns the session-relative path of the PNG if this frame became a keyframe, otherwise null.</summary>
    public string? MaybeCapture(Frame frame)
    {
        if (_interval <= 0) return null;
        if (_lastPts is not null && frame.Pts - _lastPts.Value < _interval) return null;
        _lastPts = frame.Pts;

        var fileName = $"{frame.Sequence:D7}.png";
        var fullPath = Path.Combine(_directory, fileName);
        var pixels = frame.CopyPixels();
        int width = frame.Width, height = frame.Height;

        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(Task.Run(async () =>
        {
            await _concurrency.WaitAsync();
            try { await File.WriteAllBytesAsync(fullPath, PngWriter.EncodeBgra(pixels, width, height)); }
            finally { _concurrency.Release(); }
        }));
        Written++;
        return $"{_relativeDirectory}/{fileName}";
    }

    public Task DrainAsync() => Task.WhenAll(_pending);
}
