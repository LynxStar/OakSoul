using Microsoft.Win32.SafeHandles;

namespace OakSoul.Capture;

/// <summary>
/// A child process spawned in its own process group, so a terminal Ctrl-C aimed at the host does not reach
/// it. Only stdin (for writing) and stderr (for reading) are piped. The child is expected to exit on its own
/// when stdin is closed, as ffmpeg does.
/// </summary>
public sealed class DetachedProcess : IDisposable
{
    private int? _exitCode;

    private DetachedProcess(int pid, Stream stdin, Stream stderr)
    {
        Pid = pid;
        Stdin = stdin;
        Stderr = stderr;
    }

    public int Pid { get; }
    public Stream Stdin { get; }
    public Stream Stderr { get; }
    public int? ExitCode => _exitCode;

    /// <summary>Starts <c>argv[0]</c>, searched on PATH, with the remaining arguments passed verbatim.</summary>
    public static DetachedProcess Start(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0])) throw new ArgumentException("argv must start with the program name", nameof(argv));
        if (argv.Any(a => a.Contains('\n'))) throw new ArgumentException("arguments cannot contain newlines", nameof(argv));

        var pid = NativeMethods.SpawnDetached(string.Join('\n', argv), out var stdinFd, out var stderrFd);
        if (pid <= 0)
            throw new InvalidOperationException($"could not start '{argv[0]}' (errno {-pid}); is it installed and on PATH?");

        var stdin = new FileStream(new SafeFileHandle(stdinFd, ownsHandle: true), FileAccess.Write, bufferSize: 0);
        var stderr = new FileStream(new SafeFileHandle(stderrFd, ownsHandle: true), FileAccess.Read, bufferSize: 4096);
        return new DetachedProcess(pid, stdin, stderr);
    }

    public bool HasExited
    {
        get
        {
            if (_exitCode is not null) return true;
            var result = NativeMethods.WaitProcess(Pid, noHang: 1);
            if (result == -2) return false;
            _exitCode = result;
            return true;
        }
    }

    /// <summary>Blocks until the child exits and returns its exit code (128 + signal number if it was killed).</summary>
    public int WaitForExit()
    {
        _exitCode ??= NativeMethods.WaitProcess(Pid, noHang: 0);
        return _exitCode.Value;
    }

    public void Dispose()
    {
        Stdin.Dispose();
        Stderr.Dispose();
    }
}
