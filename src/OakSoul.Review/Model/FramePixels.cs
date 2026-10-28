using System.Diagnostics;
using System.Globalization;
using OakSoul.Bake;

namespace OakSoul.Review.Model;

/// <summary>
/// The pixels of the timeline's frames (BGRA, the recording's size), decoded on a worker around the frame the tool
/// wants and kept in a cache, so stepping a frame either way finds it decoded; during playback the worker reads ahead.
/// </summary>
public abstract class FramePixels : IDisposable
{
    private readonly Dictionary<int, byte[]> _cache = [];
    private readonly Stack<byte[]> _free = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Action<Action> _post;
    private readonly Thread _worker;
    private volatile bool _disposed;
    private int _target, _direction = 1;
    private bool _playing;

    protected FramePixels(int width, int height, int count, int budget, Action<Action> post)
    {
        Width = width;
        Height = height;
        Count = count;
        Budget = budget;
        _post = post;
        _worker = new Thread(Run) { IsBackground = true, Name = GetType().Name };
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Width * Height * 4;

    /// <summary>Frames on the timeline (grows when the stream adds trailing fills).</summary>
    public int Count { get; set; }

    /// <summary>Frames from here on cannot be decoded (the video ended before the timeline does).</summary>
    protected int Limit { get; set; } = int.MaxValue;

    /// <summary>Frames kept decoded at most.</summary>
    protected int Budget { get; }

    /// <summary>Raised on the UI thread when a frame has been decoded.</summary>
    public event Action<int>? Ready;

    /// <summary>A message when decoding failed (raised on the UI thread).</summary>
    public event Action<string>? Failed;

    protected void Begin() => _worker.Start();

    /// <summary>Asks for a frame (and its neighbours) to be decoded; <paramref name="direction"/> is +1 or -1 for which side to read ahead.</summary>
    public void Want(int index, bool playing = false, int direction = 1)
    {
        lock (_cache)
        {
            _target = index;
            _playing = playing;
            _direction = direction >= 0 ? 1 : -1;
        }
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public bool Has(int index)
    {
        lock (_cache) return _cache.ContainsKey(index);
    }

    /// <summary>Hands the decoded pixels of a frame to <paramref name="use"/> under the cache's lock (the buffer is reused once evicted).</summary>
    public bool Use(int index, Action<byte[]> use)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(index, out var pixels)) return false;
            use(pixels);
            return true;
        }
    }

    /// <summary>A copy of a frame's pixels, or null when it is not decoded.</summary>
    public byte[]? Copy(int index)
    {
        byte[]? copy = null;
        Use(index, p => copy = (byte[])p.Clone());
        return copy;
    }

    protected (int Target, bool Playing, int Direction) Wanted()
    {
        lock (_cache) return (_target, _playing, _direction);
    }

    /// <summary>The frames to keep decoded around the target: [first, last].</summary>
    protected (int First, int Last) Window(int target, bool playing, int direction)
    {
        var ahead = playing ? AheadPlaying : Ahead;
        var behind = playing ? 4 : Behind;
        var first = direction > 0 ? target - behind : target - ahead;
        var last = direction > 0 ? target + ahead : target + behind;
        return (Math.Max(0, first), Math.Min(Math.Min(Count, Limit) - 1, last));
    }

    protected abstract int Ahead { get; }
    protected abstract int AheadPlaying { get; }
    protected abstract int Behind { get; }

    protected byte[] Rent()
    {
        lock (_cache) return _free.Count > 0 ? _free.Pop() : new byte[FrameBytes];
    }

    /// <summary>Stores a decoded frame, evicting the ones farthest outside the window, and tells the UI.</summary>
    protected void Put(int index, byte[] pixels)
    {
        lock (_cache)
        {
            if (_cache.Remove(index, out var old)) Recycle(old);
            _cache[index] = pixels;
            if (_cache.Count > Budget)
            {
                var (first, last) = Window(_target, _playing, _direction);
                foreach (var far in _cache.Keys.Where(k => k < first || k > last).OrderByDescending(k => Math.Max(first - k, k - last)).Take(_cache.Count - Budget).ToList())
                {
                    _cache.Remove(far, out var evicted);
                    Recycle(evicted!);
                }
            }
        }
        _post(() => Ready?.Invoke(index));
    }

    private void Recycle(byte[] buffer)
    {
        if (_free.Count < 4) _free.Push(buffer);
    }

    protected void Fail(string message) => _post(() => Failed?.Invoke(message));

    /// <summary>The first frame of the window not decoded yet: ahead of the target first, then behind it; -1 when all are.</summary>
    protected int Missing(int target, bool playing, int direction)
    {
        var (first, last) = Window(target, playing, direction);
        lock (_cache)
        {
            if (target >= 0 && target < Math.Min(Count, Limit) && !_cache.ContainsKey(target)) return target;
            if (direction > 0)
            {
                for (var i = target + 1; i <= last; i++) if (!_cache.ContainsKey(i)) return i;
                for (var i = first; i < target; i++) if (!_cache.ContainsKey(i)) return i;
            }
            else
            {
                for (var i = target - 1; i >= first; i--) if (!_cache.ContainsKey(i)) return i;
                for (var i = target + 1; i <= last; i++) if (!_cache.ContainsKey(i)) return i;
            }
            return -1;
        }
    }

    private void Run()
    {
        while (!_disposed)
        {
            var (target, playing, direction) = Wanted();
            bool worked;
            try { worked = Step(target, playing, direction); }
            catch (Exception e) when (e is IOException or BakeException or InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                Fail(e.Message);
                worked = false;
            }
            if (!worked) _signal.Wait(250);
        }
    }

    /// <summary>Decodes one frame toward the window; false when there is nothing to do.</summary>
    protected abstract bool Step(int target, bool playing, int direction);

    protected bool Disposed => _disposed;

    public virtual void Dispose()
    {
        _disposed = true;
        _signal.Release();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Keyframe mode: each frame is a keyframe PNG, read with Bake's <see cref="RecordingPixels"/>.</summary>
public sealed class KeyframePixels : FramePixels
{
    private readonly RecordingPixels _pixels;
    private readonly Timeline _timeline;

    public KeyframePixels(string sessionDir, Timeline timeline, Action<Action> post) : this(new RecordingPixels(sessionDir), timeline, post) { }

    private KeyframePixels(RecordingPixels pixels, Timeline timeline, Action<Action> post) : base(pixels.Width, pixels.Height, timeline.Count, 40, post)
    {
        _pixels = pixels;
        _timeline = timeline;
        Begin();
    }

    protected override int Ahead => 3;
    protected override int AheadPlaying => 8;
    protected override int Behind => 2;

    protected override bool Step(int target, bool playing, int direction)
    {
        var index = Missing(target, playing, direction);
        if (index < 0) return false;
        var seq = _timeline[index].Sequence;
        var pixels = _pixels.Load([seq])[seq];
        var buffer = Rent();
        Buffer.BlockCopy(pixels, 0, buffer, 0, Math.Min(buffer.Length, pixels.Length));
        Put(index, buffer);
        return true;
    }
}

/// <summary>
/// Video mode: frames come from the recording's lossless video through one ffmpeg process that decodes forward from a
/// seek, kept running while the frames wanted lie ahead of it (stepping forward and playback read on) and restarted
/// half a frame before the first missing one otherwise (as Bake's <see cref="RecordingPixels"/> seeks).
/// </summary>
public sealed class VideoPixels : FramePixels
{
    /// <summary>Read on through this many unwanted frames rather than seek again (a seek decodes from the previous key frame, up to 250 frames back).</summary>
    private const int ReadOn = 150;

    private readonly string _video;
    private readonly int _fps;
    private Process? _ffmpeg;
    private Stream? _stdout;
    private int _next;

    public VideoPixels(string sessionDir, Timeline timeline, Action<Action> post) : this(new RecordingPixels(sessionDir), sessionDir, timeline, post) { }

    private VideoPixels(RecordingPixels pixels, string sessionDir, Timeline timeline, Action<Action> post) : base(pixels.Width, pixels.Height, timeline.Count, 120, post)
    {
        _video = Directory.GetFiles(sessionDir, "video.*").FirstOrDefault() ?? throw new BakeException($"{sessionDir} has no video");
        _fps = timeline.Fps;
        Begin();
    }

    protected override int Ahead => 30;
    protected override int AheadPlaying => 90;
    protected override int Behind => 45;

    protected override bool Step(int target, bool playing, int direction)
    {
        var index = Missing(target, playing, direction);
        if (index < 0 || Disposed) return false;
        if (_ffmpeg is null || index < _next || index - _next > ReadOn)
        {
            // Backwards, start at the first missing frame of the window behind, so one pass fills it.
            var (first, _) = Window(target, playing, direction);
            var start = index;
            if (index < target)
                while (start > first && !Has(start - 1)) start--;
            Restart(start);
        }
        var buffer = Rent();
        var read = 0;
        while (read < buffer.Length && _stdout!.Read(buffer, read, buffer.Length - read) is var n and > 0) read += n;
        if (read < buffer.Length)
        {
            // The end of the video: frames from here on have no pixels.
            Stop();
            Limit = Math.Min(Limit, _next);
            Fail($"the video ends at frame {_next}; later frames of the timeline have no pixels");
            return false;
        }
        Put(_next, buffer);
        _next++;
        return true;
    }

    private void Restart(int start)
    {
        Stop();
        var seek = Math.Max(0, (start - 0.5) / _fps).ToString("0.000000", CultureInfo.InvariantCulture);
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false };
        foreach (var a in new[] { "-v", "error", "-ss", seek, "-i", _video, "-f", "rawvideo", "-pix_fmt", "bgra", "-" }) info.ArgumentList.Add(a);
        _ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("could not start ffmpeg");
        _stdout = _ffmpeg.StandardOutput.BaseStream;
        _next = start;
    }

    private void Stop()
    {
        if (_ffmpeg is null) return;
        try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(); }
        catch (InvalidOperationException) { }
        _ffmpeg.Dispose();
        _ffmpeg = null;
        _stdout = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        // The worker may be blocked reading; killing the process ends the read.
        try { if (_ffmpeg is { HasExited: false } p) p.Kill(); }
        catch (InvalidOperationException) { }
    }
}
