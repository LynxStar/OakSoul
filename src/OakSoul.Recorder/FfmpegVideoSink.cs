using OakSoul.Capture;

namespace OakSoul.Recorder;

/// <summary>
/// Pipes raw BGRA frames into an ffmpeg child. ffmpeg starts on the first frame so the stream size always
/// matches what ScreenCaptureKit delivers, and it runs in its own process group so a terminal Ctrl-C aimed at
/// the recorder does not reach it.
///
/// Output is constant frame rate on a timeline anchored at the first frame. ScreenCaptureKit only delivers a
/// frame when the window changed, so short gaps are filled by repeating the previous frame. A gap longer than
/// <see cref="MaxFillSeconds"/> is cut out of the video instead (counted in <see cref="CutSeconds"/>), and a
/// frame that arrives ahead of the timeline is left out (counted in <see cref="SkippedFrames"/>). The metadata
/// keeps true timestamps for every frame either way.
/// </summary>
public sealed class FfmpegVideoSink : IDisposable
{
    public const double MaxFillSeconds = 2.0;

    private readonly string _encoderArgs;
    private readonly StreamWriter _log;
    private readonly Queue<string> _recentStderr = new();
    private readonly int _fps;
    private readonly int _maxFill;
    private DetachedProcess? _process;
    private Task? _stderrPump;
    private double? _firstPts;
    private int _videoIndex;
    private byte[]? _lastFrame;
    private bool _closed;

    public string OutputPath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long WrittenFrames { get; private set; }
    public long FilledFrames { get; private set; }
    public long SkippedFrames { get; private set; }
    public double CutSeconds { get; private set; }
    public int? ExitCode => _process?.ExitCode;

    public FfmpegVideoSink(string outputPath, int fps, string encoderArgs, string logPath)
    {
        OutputPath = outputPath;
        _fps = fps;
        _maxFill = (int)Math.Round(MaxFillSeconds * fps);
        _encoderArgs = encoderArgs;
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
    }

    private void Start(int width, int height)
    {
        Width = width;
        Height = height;
        var argv = new List<string>
        {
            "ffmpeg", "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-r", _fps.ToString(), "-i", "-",
        };
        argv.AddRange(_encoderArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        argv.Add(OutputPath);
        _log.WriteLine(string.Join(' ', argv));

        _process = DetachedProcess.Start(argv);
        var stderr = _process.Stderr;
        _stderrPump = Task.Run(async () =>
        {
            using var reader = new StreamReader(stderr);
            while (await reader.ReadLineAsync() is { } line)
            {
                _log.WriteLine(line);
                lock (_recentStderr)
                {
                    _recentStderr.Enqueue(line);
                    while (_recentStderr.Count > 8) _recentStderr.Dequeue();
                }
            }
        });
    }

    /// <summary>Writes one frame and returns its index in the output video, or -1 if it was left out.</summary>
    public int Write(Frame frame)
    {
        if (_process is null) Start(frame.Width, frame.Height);
        if (frame.Width != Width || frame.Height != Height)
            throw new InvalidOperationException($"frame size changed from {Width}x{Height} to {frame.Width}x{frame.Height}; the window was resized");
        if (_process!.HasExited)
            throw new InvalidOperationException($"ffmpeg exited with code {_process.ExitCode}: {RecentStderr()}");

        _firstPts ??= frame.Pts;
        var expectedIndex = (int)Math.Round((frame.Pts - _firstPts.Value) * _fps);
        var gap = expectedIndex - _videoIndex;
        if (gap < 0)
        {
            SkippedFrames++;
            return -1;
        }
        if (gap > _maxFill)
        {
            var cut = gap - _maxFill;
            _firstPts += (double)cut / _fps;
            CutSeconds += (double)cut / _fps;
            gap = _maxFill;
        }

        try
        {
            if (_lastFrame is not null)
            {
                for (var i = 0; i < gap; i++)
                {
                    _process.Stdin.Write(_lastFrame);
                    _videoIndex++;
                    FilledFrames++;
                }
            }
            _process.Stdin.Write(frame.Pixels);
        }
        catch (IOException ex)
        {
            _process.WaitForExit();
            throw new InvalidOperationException($"writing to ffmpeg failed ({ex.Message}); ffmpeg exited with code {_process.ExitCode}: {RecentStderr()}", ex);
        }
        var index = _videoIndex++;
        WrittenFrames++;

        _lastFrame ??= new byte[frame.ByteLength];
        frame.Pixels.CopyTo(_lastFrame);
        return index;
    }

    private string RecentStderr()
    {
        lock (_recentStderr) return _recentStderr.Count == 0 ? "(no output)" : string.Join(" | ", _recentStderr);
    }

    /// <summary>Closes the pipe, waits for ffmpeg to finish, and returns the output file size in bytes.</summary>
    public long Close()
    {
        if (!_closed)
        {
            _closed = true;
            if (_process is not null)
            {
                try { _process.Stdin.Flush(); _process.Stdin.Close(); } catch (IOException) { }
                var exitCode = _process.WaitForExit();
                _stderrPump?.Wait();
                _log.WriteLine($"ffmpeg exited with code {exitCode}");
            }
            else
            {
                _log.WriteLine("ffmpeg was never started: no frames arrived");
            }
            _log.Dispose();
        }
        return File.Exists(OutputPath) ? new FileInfo(OutputPath).Length : 0;
    }

    public void Dispose()
    {
        Close();
        _process?.Dispose();
    }
}
