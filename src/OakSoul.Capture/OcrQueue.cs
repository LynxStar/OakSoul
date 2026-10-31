using System.Diagnostics;

namespace OakSoul.Capture;

/// <summary>Which queue a request waits in: short reads (a tag, a name block) go before long ones (a whole viewport).</summary>
public enum OcrPriority { Short, Long }

/// <summary>
/// One text recognition handed to the <see cref="OcrQueue"/>: the crop it owns, and once <see cref="Done"/>, the lines read.
/// The owner polls <see cref="Done"/> on its own thread and reads the result there; nothing here is called back.
/// </summary>
public sealed class OcrRequest
{
    private int _state;

    internal OcrRequest(long id, byte[] bgra, int width, int height, int upscale, OcrPriority priority)
    {
        Id = id; Bgra = bgra; Width = width; Height = height; Upscale = upscale; Priority = priority;
        Queued = Stopwatch.GetTimestamp();
    }

    public long Id { get; }
    /// <summary>The crop, tightly packed BGRA, owned by the request from the moment it is queued.</summary>
    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }
    public int Upscale { get; }
    public OcrPriority Priority { get; }
    internal long Queued { get; }

    /// <summary>True once the worker has written <see cref="Lines"/> (or <see cref="Error"/>).</summary>
    public bool Done => Volatile.Read(ref _state) == 2;

    /// <summary>The lines read, top to bottom; empty until <see cref="Done"/>, and on an error.</summary>
    public IReadOnlyList<TextLine> Lines { get; private set; } = [];
    public string? Error { get; private set; }

    /// <summary>Milliseconds the recognition itself took on the worker.</summary>
    public double Milliseconds { get; private set; }

    /// <summary>Milliseconds from queueing to completion: the wait behind other requests plus the recognition.</summary>
    public double Latency { get; private set; }

    internal void Complete(IReadOnlyList<TextLine> lines, string? error, double ms)
    {
        lock (this)
        {
            Lines = lines; Error = error; Milliseconds = ms;
            Latency = Stopwatch.GetElapsedTime(Queued).TotalMilliseconds;
            Volatile.Write(ref _state, 2);
            Monitor.PulseAll(this);
        }
    }

    /// <summary>Blocks the caller until the request is done (the synchronous mode; the worker still does the reading).</summary>
    public void Wait()
    {
        if (Done) return;
        lock (this) while (!Done) Monitor.Wait(this);
    }
}

/// <summary>
/// Text recognition on one worker thread of its own, so no frame waits for Vision. Measured on 60 real tag crops: the shim's
/// request gives the same lines from any thread, but two or more threads at once only lengthen each call (throughput stays at
/// about 11.5 ms per read however many run), so one thread is all there is to have; its first call pays a few hundred
/// milliseconds for Vision's loading, which <see cref="OcrQueue"/> pays at construction on that thread. Short requests (tags,
/// name blocks) go before long ones (a whole viewport for the own tag's learning). Requests complete in the order they run;
/// each owner polls its own.
/// </summary>
public sealed class OcrQueue : IDisposable
{
    private readonly Func<byte[], int, int, int, IReadOnlyList<TextLine>> _recognize;
    private readonly Queue<OcrRequest> _short = new(), _long = new();
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly List<double> _latencies = [];
    private bool _stop;
    private long _nextId = 1;
    private int _pending;

    /// <param name="recognize">The recogniser (bgra, width, height, upscale); the shim's when null. Tests pass their own.</param>
    /// <param name="warmUp">Run a tiny recognition first, so Vision loads before the first real request.</param>
    public OcrQueue(Func<byte[], int, int, int, IReadOnlyList<TextLine>>? recognize = null, bool warmUp = true)
    {
        _recognize = recognize ?? ((bgra, w, h, up) => TextRecognizer.Recognize(bgra, w, h, up));
        _thread = new Thread(Run) { IsBackground = true, Name = "oaksoul-ocr" };
        if (warmUp)
        {
            // A blank image the size of the largest read made (the own tag's whole viewport), so Vision loads everything it will
            // need: a small image left the first full-size read paying for the rest. Queued before the thread starts, so the
            // worker never sees the request before it is known as the warm-up.
            var blank = new byte[1366 * 702 * 4];
            Array.Fill(blank, (byte)255);
            WarmUp = Enqueue(blank, 1366, 702, 2);
        }
        _thread.Start();
    }

    /// <summary>The warm-up request, when one was made; its <see cref="OcrRequest.Milliseconds"/> is Vision's loading time.</summary>
    public OcrRequest? WarmUp { get; }

    /// <summary>Requests queued or running right now.</summary>
    public int Pending { get { lock (_gate) return _pending; } }

    /// <summary>Requests the recogniser failed on (they complete with <see cref="OcrRequest.Error"/> and no lines), and the first message.</summary>
    public int Errors { get; private set; }
    public string? FirstError { get; private set; }

    /// <summary>The most requests ever waiting at once.</summary>
    public int MaxPending { get; private set; }

    /// <summary>Requests completed (the warm-up left out), and the worker's time on them.</summary>
    public long Completed { get; private set; }
    public double WorkerMilliseconds { get; private set; }

    /// <summary>Queue-to-completion latency of every completed request, in order.</summary>
    public IReadOnlyList<double> Latencies { get { lock (_gate) return _latencies.ToArray(); } }

    /// <summary>Queues a crop. The request owns <paramref name="bgra"/> from here on.</summary>
    public OcrRequest Enqueue(byte[] bgra, int width, int height, int upscale, OcrPriority priority = OcrPriority.Short)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("buffer is smaller than width * height * 4");
        OcrRequest request;
        lock (_gate)
        {
            if (_stop) throw new ObjectDisposedException(nameof(OcrQueue));
            request = new OcrRequest(_nextId++, bgra, width, height, upscale, priority);
            (priority == OcrPriority.Short ? _short : _long).Enqueue(request);
            _pending++;
            if (_pending > MaxPending) MaxPending = _pending;
            Monitor.Pulse(_gate);
        }
        return request;
    }

    private void Run()
    {
        while (true)
        {
            OcrRequest request;
            lock (_gate)
            {
                while (!_stop && _short.Count == 0 && _long.Count == 0) Monitor.Wait(_gate);
                if (_stop) break;
                request = _short.Count > 0 ? _short.Dequeue() : _long.Dequeue();
            }
            var sw = Stopwatch.StartNew();
            IReadOnlyList<TextLine> lines = [];
            string? error = null;
            try { lines = _recognize(request.Bgra, request.Width, request.Height, request.Upscale); }
            catch (Exception e) { error = e.Message; }
            request.Complete(lines, error, sw.Elapsed.TotalMilliseconds);
            lock (_gate)
            {
                // Counted before the pending count drops, so a caller that saw nothing pending sees every completion counted.
                if (!ReferenceEquals(request, WarmUp)) { Completed++; WorkerMilliseconds += request.Milliseconds; _latencies.Add(request.Latency); }
                if (error is not null) { Errors++; FirstError ??= error; }
                _pending--;
            }
        }
        // Whatever was still queued completes empty, so no owner waits forever.
        lock (_gate)
        {
            foreach (var r in _short.Concat(_long)) r.Complete([], "queue disposed", 0);
            _short.Clear(); _long.Clear(); _pending = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stop = true; Monitor.PulseAll(_gate); }
        // A recognition in progress finishes in well under a second; the thread is a background one if it does not.
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
