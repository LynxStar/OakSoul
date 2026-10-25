using System.Buffers;

namespace OakSoul.Capture;

/// <summary>
/// One captured frame in tightly packed BGRA (stride = width * 4). The pixel buffer is rented from the shared
/// array pool, so dispose the frame when you are done with it.
/// </summary>
public sealed class Frame : IDisposable
{
    private byte[]? _buffer;

    internal Frame(byte[] buffer, int width, int height, long sequence, double pts, DateTime wallClockUtc)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Sequence = sequence;
        Pts = pts;
        WallClockUtc = wallClockUtc;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int ByteLength => Stride * Height;

    /// <summary>1-based count of complete frames delivered by this stream, including ones later dropped.</summary>
    public long Sequence { get; }

    /// <summary>Presentation timestamp in seconds on the host clock (mach absolute time scaled).</summary>
    public double Pts { get; }

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
