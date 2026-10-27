using OakSoul.Capture;

namespace OakSoul.Watch;

/// <summary>
/// Frame times in seconds from the start of the session, as the entity detector is timed. A keyframe replay's playback
/// clock (<see cref="Frame.Clock"/>) counts frame n as n seconds whatever the keyframe interval was (fx-combat-exact has
/// one every half second), so for keyframes the recorded times are taken from frames.jsonl, by position in the replay;
/// otherwise the playback clock is used.
/// </summary>
public sealed class SessionTimes
{
    private readonly List<double>? _keyframes;
    private double? _origin;

    public SessionTimes(string? keyframeSession)
    {
        var path = keyframeSession is null ? null : Path.Combine(keyframeSession, "frames.jsonl");
        if (path is null || !File.Exists(path)) return;
        var times = new List<double>();
        foreach (var line in File.ReadLines(path))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("keyframe", out _)) times.Add(doc.RootElement.GetProperty("pts").GetDouble());
        }
        if (times.Count > 0) _keyframes = times.Select(t => t - times[0]).ToList();
    }

    public double TimeOf(Frame frame)
    {
        if (_keyframes is { } k && frame.Index >= 1 && frame.Index <= k.Count) return k[(int)frame.Index - 1];
        _origin ??= frame.Clock;
        return frame.Clock - _origin.Value;
    }
}
