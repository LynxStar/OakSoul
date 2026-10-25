namespace OakSoul.Capture;

/// <summary>
/// Anything that produces frames: the live capture of a window, or a recording played back. Readers and the
/// watcher consume this, so everything downstream of capture can be exercised on recorded game samples
/// without the game running.
/// </summary>
public interface IFrameSource : IDisposable
{
    string Description { get; }
    IAsyncEnumerable<Frame> Frames(CancellationToken cancellation = default);
    /// <summary>True when the host must service the main run loop while frames flow (live capture only).</summary>
    bool NeedsMainLoop { get; }
}

/// <summary>Live frames from a <see cref="CaptureStream"/>.</summary>
public sealed class CaptureFrameSource : IFrameSource
{
    private readonly CaptureStream _stream;

    public CaptureFrameSource(CaptureStream stream, string description)
    {
        _stream = stream;
        Description = description;
    }

    public string Description { get; }
    public bool NeedsMainLoop => true;
    public CaptureStream Stream => _stream;

    public IAsyncEnumerable<Frame> Frames(CancellationToken cancellation = default) => _stream.Frames.ReadAllAsync(cancellation);

    public void Dispose() => _stream.Dispose();
}
