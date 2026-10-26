using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace OakSoul.Capture;

/// <summary>
/// A live ScreenCaptureKit stream of one window. Frames arrive on a native thread, are copied into pooled
/// buffers, and are queued on a bounded channel. If the consumer falls behind, the oldest queued frame is
/// dropped and counted, so memory stays flat and the consumer always sees recent frames. With a scale above 1
/// the native frame is decimated while it is copied (see <see cref="FrameDecimator"/>), so frames come out at
/// 1/scale of the size ScreenCaptureKit delivers.
/// </summary>
public sealed class CaptureStream : IDisposable
{
    /// <summary>The colour space the shim asks ScreenCaptureKit to deliver.</summary>
    public const string ColorSpace = "sRGB";

    private readonly Channel<Frame> _channel;
    private readonly int _scale;
    private GCHandle _self;
    private long _handle;
    private long _sequence;
    private long _idle;
    private long _dropped;
    private int _stopped;
    private bool _stopConfirmed;
    private int _streamError;
    private long _copyTicks;

    private CaptureStream(int queueCapacity, int scale)
    {
        _scale = scale;
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

    /// <summary>The decimation factor: ScreenCaptureKit's output is this many times the size of a frame.</summary>
    public int Scale => _scale;

    /// <summary>Mean time the native callback spent copying (and decimating) one delivered frame.</summary>
    public double FrameCopyMilliseconds
    {
        get
        {
            var frames = DeliveredFrames;
            return frames == 0 ? 0 : Interlocked.Read(ref _copyTicks) * 1000.0 / Stopwatch.Frequency / frames;
        }
    }

    /// <summary>
    /// Starts capturing <paramref name="windowId"/>, with colours in sRGB. <paramref name="crop"/> selects a
    /// region of the window in points, for example to leave out a title bar.
    /// </summary>
    /// <param name="width">
    /// Output size requested from ScreenCaptureKit, in pixels. Pass 0 for both to get the crop's (or window's)
    /// point size times <paramref name="scale"/>.
    /// </param>
    /// <param name="scale">
    /// Keep one pixel of every scale x scale block of the output, so frames are 1/scale of it. Pass the window's
    /// <see cref="WindowInfo.Scale"/> with width and height 0 to get the game's own pixels at point size; with 1,
    /// ScreenCaptureKit itself scales a Retina window down to point size, which blurs.
    /// </param>
    public static unsafe CaptureStream Start(uint windowId, int width = 0, int height = 0, int fps = 30, int queueCapacity = 8, CaptureRect? crop = null, int scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        ScreenCapture.Initialize();
        var stream = new CaptureStream(queueCapacity, scale);
        stream._self = GCHandle.Alloc(stream);
        var c = crop ?? default;
        var handle = NativeMethods.StartCapture(windowId, width, height, scale, fps, c.X, c.Y, c.Width, c.Height, &OnFrame, GCHandle.ToIntPtr(stream._self), out var error);
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

            // Read only the pixels that are kept straight from the native buffer; a 2x frame is never copied whole.
            var scale = self._scale;
            int frameWidth = width / scale, frameHeight = height / scale;
            if (frameWidth == 0 || frameHeight == 0) return;
            var buffer = ArrayPool<byte>.Shared.Rent(frameWidth * frameHeight * 4);
            var started = Stopwatch.GetTimestamp();
            FrameDecimator.Decimate(data, width, height, bytesPerRow, scale, buffer);
            Interlocked.Add(ref self._copyTicks, Stopwatch.GetTimestamp() - started);

            var sequence = Interlocked.Increment(ref self._sequence);
            var frame = new Frame(buffer, frameWidth, frameHeight, sequence, pts, DateTime.UtcNow);
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
