using System.Buffers;
using System.IO.Compression;
using System.Threading.Channels;

namespace OakSoul.Overlay;

/// <summary>
/// Writes the stream off the hot path, the way the recorder's KeyframeWriter keeps PNG encoding off it: the watcher posts
/// each frame's <see cref="OverlayFrame"/> (built from what it already holds) to an unbounded channel and returns at once;
/// a writer task serializes the frames in order and gzips them into the file (<c>overlay.jsonl.gz</c>; a path not
/// ending in .gz is written uncompressed). <see cref="CompleteAsync"/> drains the channel and closes the file.
/// </summary>
public sealed class OverlayWriter : IAsyncDisposable
{
    private readonly Channel<OverlayFrame> _channel = Channel.CreateUnbounded<OverlayFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Task _writer;

    public string Path { get; }
    public long Frames { get; private set; }
    public long Bytes { get; private set; }

    public OverlayWriter(string path, OverlayHeader header)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        _writer = Task.Run(() => WriteAll(header));
    }

    /// <summary>Queues a frame for writing; never blocks.</summary>
    public void Post(OverlayFrame frame) => _channel.Writer.TryWrite(frame);

    private async Task WriteAll(OverlayHeader header)
    {
        await using var file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        Stream output = Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true) : file;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(1 << 16);
            buffer.Write(OverlayJson.Header(header));
            buffer.Write("\n"u8);
            await foreach (var frame in _channel.Reader.ReadAllAsync())
            {
                OverlayJson.Frame(buffer, frame);
                buffer.Write("\n"u8);
                Frames++;
                if (buffer.WrittenCount >= 1 << 15)
                {
                    await output.WriteAsync(buffer.WrittenMemory);
                    Bytes += buffer.WrittenCount;
                    buffer.Clear();
                }
            }
            await output.WriteAsync(buffer.WrittenMemory);
            Bytes += buffer.WrittenCount;
        }
        finally
        {
            if (!ReferenceEquals(output, file)) await output.DisposeAsync();
        }
    }

    /// <summary>Writes what is queued and closes the file.</summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _writer;
    }

    public async ValueTask DisposeAsync() => await CompleteAsync();
}

/// <summary>Reads a stream written by <see cref="OverlayWriter"/> (gzip or plain JSON Lines).</summary>
public sealed class OverlayStream
{
    public required string Path { get; init; }
    public required OverlayHeader Header { get; init; }
    public required string HeaderLine { get; init; }
    public required IReadOnlyList<OverlayFrame> Frames { get; init; }

    /// <summary>The raw frame lines, in order, as written (for byte-level comparisons).</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>The frame of a captured sequence (never a fill), or null.</summary>
    public OverlayFrame? Captured(long sequence)
    {
        if (_bySequence is null)
        {
            _bySequence = [];
            foreach (var f in Frames) if (!f.Filled) _bySequence.TryAdd(f.Sequence, f);
        }
        return _bySequence.GetValueOrDefault(sequence);
    }

    private Dictionary<long, OverlayFrame>? _bySequence;

    public static IEnumerable<string> ReadLines(string path)
    {
        using var file = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(input);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) yield return line;
    }

    public static OverlayStream Load(string path)
    {
        string? header = null;
        var lines = new List<string>();
        foreach (var line in ReadLines(path))
        {
            if (header is null) header = line;
            else lines.Add(line);
        }
        if (header is null) throw new InvalidDataException($"{path}: empty overlay stream");
        return new OverlayStream
        {
            Path = path, Header = OverlayJson.ParseHeader(header), HeaderLine = header, Lines = lines,
            Frames = lines.Select(OverlayJson.ParseFrame).ToList(),
        };
    }
}
