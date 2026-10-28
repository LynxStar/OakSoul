using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OakSoul.Detect;
using OakSoul.Overlay;

namespace OakSoul.Review.Model;

/// <summary>Receives a stream as it is read; every call arrives on the UI thread.</summary>
public interface IStreamSink
{
    void Header(OverlayHeader header, IReadOnlyList<KeyValuePair<string, string>> fields, string? problem);
    void Frames(IReadOnlyList<ParsedFrame> frames, int badLines);
    void Done(bool ok, string message);
}

/// <summary>How far a stream can be trusted to be what the current code would produce.</summary>
public enum Freshness { Current, Unknown, Stale }

/// <summary>
/// Whether a stream's header provenance matches the current code and options: the commit it was made from against the
/// checkout's (a "+dirty" on either side makes it unknown), the replay mode, the session, the thresholds the code has
/// now, the layers, and the character and pet when the tool was given them.
/// </summary>
public sealed record Provenance(Freshness Level, IReadOnlyList<string> Reasons)
{
    public static Provenance Check(OverlayHeader header, string? problem, string? checkoutCommit, bool video, string session, string? character, string? pet)
    {
        var stale = new List<string>();
        var unknown = new List<string>();
        if (problem is not null) stale.Add(problem);
        var replay = video ? "video" : "keyframes";
        if (header.Replay != replay) stale.Add($"a {header.Replay ?? "?"} replay, not {replay}");
        if (header.Session is { } s && s != session) stale.Add($"a stream of {s}");
        if (!Same(header.SpriteThreshold, SpriteMatcher.Threshold)) stale.Add($"sprite threshold {header.SpriteThreshold}, the code has {SpriteMatcher.Threshold}");
        if (!Same(header.DropThreshold, DropMatcher.Threshold)) stale.Add($"drop threshold {header.DropThreshold}, the code has {DropMatcher.Threshold}");
        if (!header.Layers.SequenceEqual(OverlayLayers.Names)) stale.Add($"layers {string.Join(",", header.Layers)}, the code has {string.Join(",", OverlayLayers.Names)}");
        if (character is not null && !string.Equals(header.Name, character, StringComparison.OrdinalIgnoreCase)) stale.Add($"character {header.Name ?? "?"}, not {character}");
        if (pet is not null && !string.Equals(header.Pet ?? "none", pet, StringComparison.OrdinalIgnoreCase)) stale.Add($"pet {header.Pet ?? "none"}, not {pet}");
        var (made, madeDirty) = Split(header.Commit);
        var (now, nowDirty) = Split(checkoutCommit);
        if (made is null) unknown.Add("the stream names no commit");
        else if (now is null) unknown.Add("git cannot name the checkout's commit");
        else if (made != now) stale.Add($"made by {Short(made)}{(madeDirty ? "+dirty" : "")}, the checkout is at {Short(now)}{(nowDirty ? "+dirty" : "")}");
        else
        {
            if (madeDirty) unknown.Add($"made from uncommitted changes on {Short(made)}");
            if (nowDirty) unknown.Add("the checkout has uncommitted changes");
        }
        return stale.Count > 0 ? new Provenance(Freshness.Stale, [.. stale, .. unknown])
            : unknown.Count > 0 ? new Provenance(Freshness.Unknown, unknown)
            : new Provenance(Freshness.Current, [$"made by {Short(made!)}, the checkout's commit"]);
    }

    private static bool Same(double a, double b) => double.IsNaN(a) ? false : Math.Abs(a - b) < 1e-9;

    private static (string? Sha, bool Dirty) Split(string? commit) =>
        commit is null ? (null, false) : commit.EndsWith("+dirty", StringComparison.Ordinal) ? (commit[..^6], true) : (commit, false);

    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}

/// <summary>
/// Feeds a stream to a sink: <see cref="Load"/> reads a whole file in the background; <see cref="StreamGenerator"/>
/// regenerates one and reads it while it grows.
/// </summary>
public static class StreamFeed
{
    public const int Batch = 200;

    /// <summary>Reads <paramref name="path"/> (gzip or plain) on a worker, posting the header and batches of frames.</summary>
    public static Task Load(string path, Action<Action> post, IStreamSink sink, CancellationToken cancel) => Task.Run(() =>
    {
        var batch = new List<ParsedFrame>(Batch);
        var bad = 0;
        var first = true;
        try
        {
            foreach (var line in TolerantJson.ReadLines(path))
            {
                cancel.ThrowIfCancellationRequested();
                if (first)
                {
                    first = false;
                    var (h, fields, problem) = TolerantJson.ParseHeader(line);
                    post(() => sink.Header(h, fields, problem));
                    continue;
                }
                try { batch.Add(TolerantJson.ParseFrame(line)); }
                catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { bad++; }
                if (batch.Count >= Batch)
                {
                    var b = batch; var n = bad;
                    post(() => sink.Frames(b, n));
                    batch = new List<ParsedFrame>(Batch);
                    bad = 0;
                }
            }
            var last = batch; var lastBad = bad;
            post(() => sink.Frames(last, lastBad));
            post(() => sink.Done(!first, first ? $"{path} is empty" : "read"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            var b = batch;
            post(() => sink.Frames(b, bad));
            post(() => sink.Done(false, $"{path}: {e.Message}"));
        }
    }, cancel);
}

/// <summary>
/// Regenerates a stream in a child process, the way <c>OakSoul.Bake regenerate [--video]</c> does (this program's
/// <c>regenerate</c> runs Bake's command unchanged), writing plain JSON Lines next to the stream (overlay.jsonl, so the
/// watcher's log keeps its usual name), reading the file while it grows, and when the watcher succeeds compressing it
/// to the stream's own path (overlay.jsonl.gz), which keeps the previous stream until then.
/// </summary>
public sealed class StreamGenerator : IDisposable
{
    private readonly string _session, _finalPath, _partialPath;
    private readonly bool _video;
    private readonly string? _character, _pet;
    private readonly Action<Action> _post;
    private readonly IStreamSink _sink;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Queue<string> _output = new();
    private Process? _process;

    public StreamGenerator(string session, bool video, string finalPath, string? character, string? pet, Action<Action> post, IStreamSink sink)
    {
        _session = session;
        _video = video;
        _finalPath = finalPath;
        _partialPath = finalPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? finalPath[..^3] : finalPath + ".partial";
        _character = character;
        _pet = pet;
        _post = post;
        _sink = sink;
    }

    public string PartialPath => _partialPath;

    /// <summary>The child's last lines of output (its summary, or the error).</summary>
    public IReadOnlyList<string> Output { get { lock (_output) return _output.ToList(); } }

    /// <summary>The command line run, for display.</summary>
    public string Command { get; private set; } = "";

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_partialPath))!);
        if (File.Exists(_partialPath)) File.Delete(_partialPath);
        string[] args = ["regenerate", _session, .. _video ? new[] { "--video" } : [], "--out", _partialPath,
            .. _character is { } c ? new[] { "--character", c } : [], .. _pet is { } p ? new[] { "--pet", p } : []];
        var (exe, prefix) = SelfCommand();
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        foreach (var a in prefix.Concat(args)) info.ArgumentList.Add(a);
        Command = $"OakSoul.Review {string.Join(' ', args)}";
        _process = Process.Start(info) ?? throw new InvalidOperationException("could not start the regeneration");
        _process.OutputDataReceived += (_, e) => Keep(e.Data);
        _process.ErrorDataReceived += (_, e) => Keep(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _ = Task.Run(() => Tail(_cancel.Token));
    }

    /// <summary>This program, to run again as the child: its apphost, or dotnet with its assembly.</summary>
    private static (string Exe, string[] Prefix) SelfCommand()
    {
        var self = Environment.ProcessPath;
        var dll = typeof(StreamGenerator).Assembly.Location;
        return self is not null && Path.GetFileNameWithoutExtension(self) == Path.GetFileNameWithoutExtension(dll) ? (self, []) : ("dotnet", [dll]);
    }

    private void Keep(string? line)
    {
        if (line is null) return;
        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > 40) _output.Dequeue();
        }
    }

    private async Task Tail(CancellationToken cancel)
    {
        FileStream? file = null;
        var carry = new MemoryStream();
        var buffer = new byte[1 << 20];
        var batch = new List<ParsedFrame>(StreamFeed.Batch);
        var bad = 0;
        var header = false;
        try
        {
            while (true)
            {
                var exited = _process!.HasExited;
                if (file is null && File.Exists(_partialPath))
                    file = new FileStream(_partialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file is not null)
                {
                    int n;
                    while ((n = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var start = 0;
                        for (var i = 0; i < n; i++)
                        {
                            if (buffer[i] != (byte)'\n') continue;
                            carry.Write(buffer, start, i - start);
                            var line = Encoding.UTF8.GetString(carry.GetBuffer(), 0, (int)carry.Length);
                            carry.SetLength(0);
                            start = i + 1;
                            if (line.Length == 0) continue;
                            if (!header)
                            {
                                header = true;
                                var (h, fields, problem) = TolerantJson.ParseHeader(line);
                                _post(() => _sink.Header(h, fields, problem));
                                continue;
                            }
                            try { batch.Add(TolerantJson.ParseFrame(line)); }
                            catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { bad++; }
                        }
                        carry.Write(buffer, start, n - start);
                        if (batch.Count >= StreamFeed.Batch) Flush();
                    }
                }
                Flush();
                if (exited) break;
                await Task.Delay(150, cancel);
            }
            file?.Dispose();
            file = null;
            var code = _process.ExitCode;
            if (code != 0)
            {
                _post(() => _sink.Done(false, $"the watcher failed (exit {code}): {Output.LastOrDefault(l => l.Length > 0) ?? "no output"}"));
                return;
            }
            await Compress(cancel);
            _post(() => _sink.Done(true, $"regenerated {_finalPath}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _post(() => _sink.Done(false, $"regeneration: {e.Message}"));
        }
        finally
        {
            file?.Dispose();
        }

        void Flush()
        {
            if (batch.Count == 0 && bad == 0) return;
            var b = batch; var n = bad;
            _post(() => _sink.Frames(b, n));
            batch = new List<ParsedFrame>(StreamFeed.Batch);
            bad = 0;
        }
    }

    /// <summary>Gzips the finished JSON Lines into the stream's path (through a temporary file) and removes them.</summary>
    private async Task Compress(CancellationToken cancel)
    {
        var temp = _finalPath + ".tmp";
        await using (var input = File.OpenRead(_partialPath))
        await using (var output = File.Create(temp))
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            await input.CopyToAsync(gzip, cancel);
        File.Move(temp, _finalPath, overwrite: true);
        File.Delete(_partialPath);
    }

    /// <summary>Stops the child (and the ffmpeg it runs) and leaves the previous stream as it was.</summary>
    public void Cancel()
    {
        _cancel.Cancel();
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    public bool Running => _process is { HasExited: false };

    public void Dispose()
    {
        Cancel();
        _process?.Dispose();
        _cancel.Dispose();
    }
}
