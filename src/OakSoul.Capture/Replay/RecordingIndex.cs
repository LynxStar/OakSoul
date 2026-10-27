using System.Text.Json;

namespace OakSoul.Capture.Replay;

/// <summary>One line of a session's <c>frames.jsonl</c>: a captured frame.</summary>
/// <param name="VideoFrame">Index of the frame in the session's video, or -1 when the recorder left it out (it arrived
/// ahead of the constant-rate timeline).</param>
/// <param name="Keyframe">Session-relative path of its PNG keyframe, when one was written.</param>
public sealed record CaptureRecord(long Sequence, double Pts, long VideoFrame, string? Keyframe);

/// <summary>
/// The capture records of a recorder session (<c>frames.jsonl</c>), looked up by capture sequence and by video frame.
/// The video is a constant-rate timeline: the recorder repeats the previous frame to fill an idle gap (those video frames
/// have no record of their own, see <see cref="ResolveVideoFrame"/>) and leaves out a frame that arrives ahead of the
/// timeline (its record has <see cref="CaptureRecord.VideoFrame"/> -1).
/// </summary>
public sealed class RecordingIndex
{
    private readonly Dictionary<long, CaptureRecord> _bySequence = [];
    private readonly List<CaptureRecord> _byVideo = [];

    /// <summary>Every record in file order (capture order).</summary>
    public IReadOnlyList<CaptureRecord> Records { get; }

    /// <summary>The pts of the first capture record, the session's time origin (0 when there is none).</summary>
    public double OriginPts { get; }

    /// <summary>Records that are in the video, in video order.</summary>
    public IReadOnlyList<CaptureRecord> VideoRecords => _byVideo;

    private RecordingIndex(List<CaptureRecord> records)
    {
        Records = records;
        OriginPts = records.Count > 0 ? records[0].Pts : 0;
        foreach (var r in records)
        {
            _bySequence[r.Sequence] = r;
            if (r.VideoFrame >= 0) _byVideo.Add(r);
        }
        _byVideo.Sort((a, b) => a.VideoFrame.CompareTo(b.VideoFrame));
    }

    /// <summary>Loads <c>frames.jsonl</c> of a session folder, or returns null when the session has none.</summary>
    public static RecordingIndex? Load(string sessionDir)
    {
        var path = Path.Combine(sessionDir, "frames.jsonl");
        if (!File.Exists(path)) return null;
        var records = new List<CaptureRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            records.Add(new CaptureRecord(r.GetProperty("sequence").GetInt64(), r.GetProperty("pts").GetDouble(),
                r.TryGetProperty("videoFrame", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1,
                r.TryGetProperty("keyframe", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null));
        }
        return new RecordingIndex(records);
    }

    public CaptureRecord? BySequence(long sequence) => _bySequence.GetValueOrDefault(sequence);

    /// <summary>
    /// The capture record shown by a video frame: the frame's own record, or for a frame the recorder filled, the last
    /// record before it in the video (the frame it repeats) and how many frames after it the fill is (0 for an own record).
    /// Null before the first record.
    /// </summary>
    public (CaptureRecord Record, int Fill)? ResolveVideoFrame(long videoFrame)
    {
        // Binary search for the last record at or before the video frame.
        int lo = 0, hi = _byVideo.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_byVideo[mid].VideoFrame <= videoFrame) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return null;
        var r = _byVideo[found];
        return (r, (int)(videoFrame - r.VideoFrame));
    }

    /// <summary>The sequence a keyframe PNG was written for: its file name (<c>0001411.png</c>), or null.</summary>
    public static long? KeyframeSequence(string path) =>
        long.TryParse(Path.GetFileNameWithoutExtension(path), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;

    /// <summary>The session's keyframe PNGs in the order a replay delivers them (by file name, which is the sequence zero-padded).</summary>
    public static string[] KeyframeFiles(string sessionDir)
    {
        var dir = Path.Combine(sessionDir, "keyframes");
        if (!Directory.Exists(dir)) return [];
        var files = Directory.GetFiles(dir, "*.png");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }
}
