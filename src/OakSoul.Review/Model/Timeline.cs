using System.Text.Json;
using OakSoul.Capture.Replay;

namespace OakSoul.Review.Model;

/// <summary>
/// One frame the tool can show: a keyframe, or in video mode a video frame (a fill repeats the captured frame
/// <see cref="Sequence"/>). <see cref="Time"/> is seconds into the session, as the stream's <c>t</c>.
/// </summary>
public sealed record TimelineEntry(int Index, long Sequence, double Pts, double Time, bool Filled, long VideoFrame, bool HasKeyframe);

/// <summary>
/// The frames of a recording in the order a stream of the chosen mode lists them, built from the recording alone so they
/// can be browsed before the stream has reached them: every keyframe (keyframe mode), or every video frame with the
/// recorder's fills (video mode), joined to frames.jsonl as the watcher's replay joins them.
/// </summary>
public sealed class Timeline
{
    private readonly List<TimelineEntry> _entries = [];
    private readonly Dictionary<long, int> _bySequence = [];

    public bool Video { get; }
    public int Fps { get; }
    public IReadOnlyList<TimelineEntry> Entries => _entries;
    public int Count => _entries.Count;
    public TimelineEntry this[int index] => _entries[index];

    /// <summary>Frames one keyframe step covers (the recorder writes a keyframe every 15th captured frame).</summary>
    public int KeyframeStep => Video ? 15 : 1;

    private Timeline(bool video, int fps)
    {
        Video = video;
        Fps = fps;
    }

    public static Timeline Build(string sessionDir, bool video)
    {
        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDir, "session.json")));
        var fps = session.RootElement.TryGetProperty("fps", out var f) ? f.GetInt32() : 30;
        var index = RecordingIndex.Load(sessionDir);
        var origin = index?.OriginPts ?? 0;
        var t = new Timeline(video, fps);
        if (!video)
        {
            foreach (var file in RecordingIndex.KeyframeFiles(sessionDir))
            {
                if (RecordingIndex.KeyframeSequence(file) is not { } seq) continue;
                // A keyframe without a record (written after the last metadata flush) gets the pts the replay extrapolates.
                var record = index?.BySequence(seq);
                var before = record is null ? index?.Records.LastOrDefault(r => r.Sequence < seq) : null;
                var pts = record?.Pts ?? (before is null ? 0 : before.Pts + (double)(seq - before.Sequence) / fps);
                t.Add(seq, pts, pts - origin, false, record?.VideoFrame ?? -1, true);
            }
        }
        else if (index is not null)
        {
            var records = index.VideoRecords;
            for (var k = 0; k < records.Count; k++)
            {
                var r = records[k];
                t.Add(r.Sequence, r.Pts, r.Pts - origin, false, r.VideoFrame, r.Keyframe is not null);
                var gap = k + 1 < records.Count ? records[k + 1].VideoFrame - r.VideoFrame - 1 : 0;
                for (var g = 1; g <= gap; g++) t.Add(r.Sequence, r.Pts + (double)g / fps, r.Pts + (double)g / fps - origin, true, r.VideoFrame + g, false);
            }
        }
        return t;
    }

    private void Add(long seq, double pts, double time, bool filled, long videoFrame, bool keyframe)
    {
        var e = new TimelineEntry(_entries.Count, seq, pts, Math.Max(0, time), filled, videoFrame, keyframe);
        _entries.Add(e);
        if (!filled) _bySequence.TryAdd(seq, e.Index);
    }

    /// <summary>
    /// Adds a trailing fill the stream holds beyond the recorded video frames (the recorder may end the video with fills
    /// after its last record).
    /// </summary>
    public void ExtendWithFill(long seq, double pts, double time)
    {
        var last = _entries.Count > 0 ? _entries[^1].VideoFrame : -1;
        Add(seq, pts, time, true, last + 1, false);
    }

    /// <summary>The entry of a captured sequence (never a fill), or -1.</summary>
    public int IndexOf(long sequence) => _bySequence.GetValueOrDefault(sequence, -1);

    /// <summary>The entry of a captured sequence, else the one whose sequence is nearest.</summary>
    public int Nearest(long sequence)
    {
        if (IndexOf(sequence) is var exact and >= 0) return exact;
        var best = 0;
        for (var i = 0; i < _entries.Count; i++)
            if (!_entries[i].Filled && Math.Abs(_entries[i].Sequence - sequence) < Math.Abs(_entries[best].Sequence - sequence)) best = i;
        return best;
    }

    /// <summary>The last entry at or before <paramref name="time"/> (the first entry before the start).</summary>
    public int AtTime(double time)
    {
        int lo = 0, hi = _entries.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_entries[mid].Time <= time + 1e-6) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The entry whose time is nearest <paramref name="time"/>.</summary>
    public int NearestTime(double time)
    {
        var at = AtTime(time);
        return at + 1 < _entries.Count && _entries[at + 1].Time - time < time - _entries[at].Time ? at + 1 : at;
    }

    public double Duration => _entries.Count == 0 ? 0 : _entries[^1].Time;
}
