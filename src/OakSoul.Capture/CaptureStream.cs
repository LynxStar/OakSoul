using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OakSoul.Capture;

/// <summary>
/// A live ScreenCaptureKit stream of one window. Frames arrive on a native thread, are copied into pooled
/// buffers, and are queued on a bounded channel. If the consumer falls behind, the oldest queued frame is
/// dropped and counted, so memory stays flat and the consumer always sees recent frames.
/// </summary>
public sealed class CaptureStream : IDisposable
{
    private readonly Channel<Frame> _channel;
    private GCHandle _self;
    private long _handle;
    private long _sequence;
    private long _idle;
    private long _dropped;
    private int _stopped;
    private bool _stopConfirmed;
    private int _streamError;

    private CaptureStream(int queueCapacity)
    {
        var options = new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false, // TryComplete may run on another thread than the frame callback
        };
        _channel = Channel.CreateBounded<Frame>(options, dropped =>
        {
            dropped.Dispose();
            Interlocked.Increment(ref _dropped);
        });
    }

    /// <summary>Complete frames, in order. Completes when <see cref="Stop"/> is called or the stream fails.</summary>
    public ChannelReader<Frame> Frames => _channel.Reader;

    /// <summary>Frames ScreenCaptureKit reported without pixels (window unchanged, blank, or suspended).</summary>
    public long IdleFrames => Interlocked.Read(ref _idle);

    /// <summary>Complete frames discarded because the consumer was too slow.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    /// <summary>Complete frames delivered by the native side, whether or not they were consumed.</summary>
    public long DeliveredFrames => Interlocked.Read(ref _sequence);

    /// <summary>True once ScreenCaptureKit stopped the stream on its own, for example because the window closed.</summary>
    public bool Failed => Volatile.Read(ref _streamError) != 0;

    /// <summary>
    /// Starts capturing <paramref name="windowId"/>. Pass 0 for width and height to capture at the window's
    /// point size, which gives 1x game pixels on a Retina display.
    /// </summary>
    public static unsafe CaptureStream Start(uint windowId, int width = 0, int height = 0, int fps = 30, int queueCapacity = 8)
    {
        ScreenCapture.Initialize();
        var stream = new CaptureStream(queueCapacity);
        stream._self = GCHandle.Alloc(stream);
        var handle = NativeMethods.StartCapture(windowId, width, height, fps, &OnFrame, GCHandle.ToIntPtr(stream._self), out var error);
        if (handle == 0)
        {
            var message = NativeMethods.TakeString(error) ?? "unknown error";
            stream._self.Free();
            throw new InvalidOperationException($"could not start capture of window {windowId}: {message}");
        }
        stream._handle = handle;
        return stream;
    }

    /// <summary>Stops the native stream and completes <see cref="Frames"/>. Safe to call more than once.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _stopConfirmed = _handle == 0 || NativeMethods.StopCapture(_handle) == 1;
        _channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        // If native could not confirm the stream stopped, a late callback could still dereference the context,
        // so the handle is deliberately leaked instead of freed.
        if (_self.IsAllocated && _stopConfirmed) _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnFrame(nint context, byte* data, int width, int height, int bytesPerRow, double pts, int status)
    {
        // Exceptions must never escape into native code; they would take the process down.
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not CaptureStream self) return;
            if (status == -1)
            {
                Volatile.Write(ref self._streamError, 1);
                self._channel.Writer.TryComplete();
                return;
            }
            if (status != 0 || data is null)
            {
                Interlocked.Increment(ref self._idle);
                return;
            }
            if (Volatile.Read(ref self._stopped) != 0) return;

            var rowBytes = width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * height);
            if (bytesPerRow == rowBytes)
            {
                new ReadOnlySpan<byte>(data, rowBytes * height).CopyTo(buffer);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    new ReadOnlySpan<byte>(data + (long)y * bytesPerRow, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
            }

            var sequence = Interlocked.Increment(ref self._sequence);
            var frame = new Frame(buffer, width, height, sequence, pts, DateTime.UtcNow);
            if (!self._channel.Writer.TryWrite(frame))
            {
                frame.Dispose();
                Interlocked.Increment(ref self._dropped);
            }
        }
        catch
        {
            // Swallow: nothing useful can be done from a native callback.
        }
    }
}
