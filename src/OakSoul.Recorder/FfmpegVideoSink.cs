using System.Diagnostics;
using OakSoul.Capture;

namespace OakSoul.Recorder;

/// <summary>
/// Pipes raw BGRA frames into an ffmpeg process. ffmpeg is started on the first frame so the stream size always
/// matches what ScreenCaptureKit actually delivers. The output is constant frame rate: when ScreenCaptureKit
/// skips frames because the window did not change, the previous frame is repeated so video time stays true.
/// </summary>
public sealed class FfmpegVideoSink : IDisposable
{
    private readonly string _encoderArgs;
    private readonly StreamWriter _log;
    private readonly Queue<string> _recentStderr = new();
    private readonly int _fps;
    private Process? _process;
    private Stream? _stdin;
    private double? _firstPts;
    private int _videoIndex;
    private byte[]? _lastFrame;
    private bool _closed;

    public string OutputPath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Started => _process is not null;
    public long WrittenFrames { get; private set; }
    public long FilledFrames { get; private set; }

    public FfmpegVideoSink(string outputPath, int fps, string encoderArgs, string logPath)
    {
        OutputPath = outputPath;
        _fps = fps;
        _encoderArgs = encoderArgs;
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
    }

    private void Start(int width, int height)
    {
        Width = width;
        Height = height;
        var arguments = $"-hide_banner -loglevel warning -y -f rawvideo -pix_fmt bgra -s {width}x{height} -r {_fps} -i - {_encoderArgs} \"{OutputPath}\"";
        _log.WriteLine($"ffmpeg {arguments}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            },
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _log.WriteLine(e.Data);
            lock (_recentStderr)
            {
                _recentStderr.Enqueue(e.Data);
                while (_recentStderr.Count > 8) _recentStderr.Dequeue();
            }
        };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("could not start ffmpeg; is it installed and on PATH?", ex);
        }
        process.BeginErrorReadLine();
        _process = process;
        _stdin = process.StandardInput.BaseStream;
    }

    /// <summary>Writes one frame and returns its index in the output video.</summary>
    public int Write(Frame frame)
    {
        if (_process is null) Start(frame.Width, frame.Height);
        if (frame.Width != Width || frame.Height != Height)
            throw new InvalidOperationException($"frame size changed from {Width}x{Height} to {frame.Width}x{frame.Height}; the window was resized");
        if (_process!.HasExited)
            throw new InvalidOperationException($"ffmpeg exited with code {_process.ExitCode}: {RecentStderr()}");

        _firstPts ??= frame.Pts;
        var expectedIndex = (int)Math.Round((frame.Pts - _firstPts.Value) * _fps);
        var gap = Math.Clamp(expectedIndex - _videoIndex, 0, _fps * 5);
        try
        {
            if (_lastFrame is not null)
            {
                for (var i = 0; i < gap; i++)
                {
                    _stdin!.Write(_lastFrame);
                    _videoIndex++;
                    FilledFrames++;
                }
            }
            _stdin!.Write(frame.Pixels);
        }
        catch (IOException ex)
        {
            _process.WaitForExit(2000);
            throw new InvalidOperationException($"writing to ffmpeg failed ({ex.Message}); ffmpeg said: {RecentStderr()}", ex);
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
                try { _stdin?.Flush(); _stdin?.Close(); } catch (IOException) { }
                _process.WaitForExit();
                _log.WriteLine($"ffmpeg exited with code {_process.ExitCode}");
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
