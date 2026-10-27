using System.Buffers;

namespace OakSoul.Capture;

/// <summary>
/// One captured frame in tightly packed BGRA (stride = width * 4). The pixel buffer is rented from the shared
/// array pool, so dispose the frame when you are done with it.
/// </summary>
public sealed class Frame : IDisposable
{
    private byte[]? _buffer;

    internal Frame(byte[] buffer, int width, int height, long sequence, double pts, DateTime wallClockUtc, long? index = null, double? clock = null, bool filled = false)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Sequence = sequence;
        Pts = pts;
        WallClockUtc = wallClockUtc;
        Index = index ?? sequence;
        Clock = clock ?? pts;
        Filled = filled;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int ByteLength => Stride * Height;

    /// <summary>
    /// The capture sequence: live, the 1-based count of complete frames delivered by the stream, including ones later
    /// dropped; replayed, the recording's own sequence of the captured frame (<c>frames.jsonl</c>, the keyframe's file
    /// name), so a replayed frame keeps the number it was recorded under. A <see cref="Filled"/> video frame carries the
    /// sequence of the captured frame it repeats.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// Presentation timestamp in seconds on the host clock (mach absolute time scaled) at capture; replayed, the recorded
    /// <c>pts</c>. A <see cref="Filled"/> video frame has the repeated frame's pts plus its offset on the video's
    /// constant-rate timeline, so sequence and pts together stay unique.
    /// </summary>
    public double Pts { get; }

    /// <summary>
    /// 1-based position of the frame in what its source delivered (live: <see cref="Sequence"/>). Unique and gapless
    /// over a replay, so it is what per-replay bookkeeping (detector frame ids, sheet picks) counts with.
    /// </summary>
    public long Index { get; }

    /// <summary>
    /// The clock the watcher's trackers run on, in seconds: live, <see cref="Pts"/>; replayed, the playback position
    /// (<see cref="Index"/> - 1) / the replay's nominal rate (the video's fps; keyframes count as one per second whatever
    /// their spacing), as replays have always been timed.
    /// </summary>
    public double Clock { get; }

    /// <summary>
    /// True for a video frame the recorder wrote to fill an idle gap (ScreenCaptureKit delivers nothing while the window
    /// is unchanged): it repeats the previous captured frame and has no capture record of its own.
    /// </summary>
    public bool Filled { get; }

    public DateTime WallClockUtc { get; }

    public ReadOnlySpan<byte> Pixels => (_buffer ?? throw new ObjectDisposedException(nameof(Frame))).AsSpan(0, ByteLength);

    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(Frame))).AsMemory(0, ByteLength);

    /// <summary>Copies the pixels into a fresh, exactly sized array that outlives this frame.</summary>
    public byte[] CopyPixels() => Pixels.ToArray();

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}
