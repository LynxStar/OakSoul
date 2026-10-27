using System.Diagnostics;
using System.Text.Json;
using OakSoul.Capture.Replay;
using OakSoul.Detect;
using OakSoul.Overlay;

namespace OakSoul.Bake;

/// <summary>
/// A recorder session and where its baked outputs live: <c>recordings/&lt;session&gt;</c> (read-only),
/// <c>data/overlays/&lt;session&gt;/</c> for streams and <c>data/bake/&lt;session&gt;/</c> for images.
/// </summary>
public sealed class Session
{
    public required string Name { get; init; }
    public required string Recording { get; init; }

    /// <summary>A session name (looked up under recordings/) or the path of a session folder.</summary>
    public static Session Resolve(string arg)
    {
        var dir = Directory.Exists(arg) && File.Exists(Path.Combine(arg, "session.json")) ? arg : Path.Combine("recordings", arg);
        if (!File.Exists(Path.Combine(dir, "session.json"))) throw new BakeException($"no recorder session at {dir} (expected session.json)");
        return new Session { Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), Recording = OverlayProvenance.RelativePath(dir) };
    }

    public string OverlayDir => Path.Combine("data", "overlays", Name);

    /// <summary>The default stream: <c>overlay.jsonl.gz</c> from the keyframes, <c>overlay-video.jsonl.gz</c> from the video.</summary>
    public string DefaultStream(bool video) => Path.Combine(OverlayDir, video ? "overlay-video.jsonl.gz" : "overlay.jsonl.gz");

    public string BakeDir(string name) => Path.Combine("data", "bake", Name, name);
}

public sealed class BakeException(string message) : Exception(message);

/// <summary>
/// The pixels of a captured frame of a recording, by capture sequence: the keyframe PNG when one was written for that
/// sequence, else the frame the lossless video holds for it (frames.jsonl's <c>videoFrame</c>; a filled video frame shows
/// the captured frame it repeats, so its sequence's pixels are the same). Frames are read-only copies of the recording.
/// </summary>
public sealed class RecordingPixels
{
    private readonly string _dir;
    private readonly RecordingIndex? _index;
    private readonly string? _video;
    private readonly int _fps;
    private readonly bool _lossless;

    public int Width { get; }
    public int Height { get; }

    public RecordingPixels(string sessionDir)
    {
        _dir = sessionDir;
        _index = RecordingIndex.Load(sessionDir);
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json")));
        var root = session.RootElement;
        Width = root.GetProperty("width").GetInt32();
        Height = root.GetProperty("height").GetInt32();
        _fps = root.GetProperty("fps").GetInt32();
        _video = Directory.GetFiles(sessionDir, "video.*").FirstOrDefault();
        _lossless = RecordingFrameSource.IsLosslessVideo(root);
    }

    public string KeyframePath(long sequence) => Path.Combine(_dir, "keyframes", $"{sequence:D7}.png");

    /// <summary>Where the pixels of a sequence come from: "keyframe", "video" or null when neither has it.</summary>
    public string? SourceOf(long sequence, bool videoOnly = false) =>
        !videoOnly && File.Exists(KeyframePath(sequence)) ? "keyframe" : _index?.BySequence(sequence) is { VideoFrame: >= 0 } && _video is not null ? "video" : null;

    /// <summary>
    /// The BGRA pixels of each requested sequence; video frames are decoded in runs, one ffmpeg call per run. With
    /// <paramref name="videoOnly"/> keyframes are ignored (to check the video against them).
    /// </summary>
    public Dictionary<long, byte[]> Load(IEnumerable<long> sequences, bool videoOnly = false)
    {
        var result = new Dictionary<long, byte[]>();
        var fromVideo = new List<(long Sequence, long VideoFrame)>();
        foreach (var seq in sequences.Distinct())
        {
            if (!videoOnly && File.Exists(KeyframePath(seq)))
            {
                var (bgra, w, h) = Png.ReadBgra(KeyframePath(seq));
                if (w != Width || h != Height) throw new BakeException($"keyframe {seq} is {w}x{h}, the session {Width}x{Height}");
                result[seq] = bgra;
            }
            else if (_index?.BySequence(seq) is { VideoFrame: >= 0 } r && _video is not null) fromVideo.Add((seq, r.VideoFrame));
            else throw new BakeException($"no pixels for sequence {seq}: no keyframe, and {(_index?.BySequence(seq) is null ? "no capture record" : "the recorder left it out of the video")}");
        }
        if (fromVideo.Count > 0 && !_lossless)
            Console.Error.WriteLine($"warning: {_video} is lossy; {fromVideo.Count} frame(s) baked from it are not the captured pixels");
        // Contiguous runs of video frames are decoded with one seek each.
        fromVideo.Sort((a, b) => a.VideoFrame.CompareTo(b.VideoFrame));
        for (var i = 0; i < fromVideo.Count;)
        {
            var j = i;
            while (j + 1 < fromVideo.Count && fromVideo[j + 1].VideoFrame - fromVideo[j].VideoFrame <= 30) j++;
            var first = fromVideo[i].VideoFrame;
            var frames = DecodeVideo(first, (int)(fromVideo[j].VideoFrame - first + 1));
            for (var k = i; k <= j; k++) result[fromVideo[k].Sequence] = frames[(int)(fromVideo[k].VideoFrame - first)];
            i = j + 1;
        }
        return result;
    }

    /// <summary>
    /// Decodes <paramref name="count"/> video frames from frame <paramref name="first"/>. The seek lands half a frame before
    /// the wanted one, so the container's millisecond timestamps cannot round it onto the previous frame.
    /// </summary>
    private List<byte[]> DecodeVideo(long first, int count)
    {
        var seek = Math.Max(0, (first - 0.5) / _fps).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -ss {seek} -i \"{_video}\" -frames:v {count} -f rawvideo -pix_fmt bgra -")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
        }) ?? throw new BakeException("could not start ffmpeg");
        var stdout = ffmpeg.StandardOutput.BaseStream;
        var frames = new List<byte[]>();
        var bytes = Width * Height * 4;
        for (var n = 0; n < count; n++)
        {
            var buffer = new byte[bytes];
            var read = 0;
            while (read < bytes && stdout.Read(buffer, read, bytes - read) is var got and > 0) read += got;
            if (read < bytes) throw new BakeException($"ffmpeg delivered {n} of {count} frames from video frame {first}");
            frames.Add(buffer);
        }
        ffmpeg.WaitForExit();
        return frames;
    }
}
