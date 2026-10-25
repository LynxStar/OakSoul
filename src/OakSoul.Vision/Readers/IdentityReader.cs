using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>Who the character is, as shown on the status bar: level badge, job line, name line.</summary>
public sealed record IdentityReading(int? Level, string Job, string Name, double Confidence)
{
    public override string ToString() => $"Lv {Level?.ToString() ?? "?"} {Job} '{Name}' ({Confidence:0.00})";
}

/// <summary>
/// Reads the level badge and the job and name lines through hash-gated OCR. The level badge is small, so it
/// is upscaled more. Values change at level-up or on a different character, so the cost is one OCR call each
/// per change and a hash per frame otherwise.
/// </summary>
public sealed class IdentityReader
{
    private readonly TextRegionTracker _level;
    private readonly TextRegionTracker _jobName;

    public IdentityReader(HudLayout layout, int stableFrames = 3)
    {
        // Vision needs the "LV." label as context and a large upscale to read the small badge digits; measured on
        // the real client, 8x reads both lines at full confidence where 3x to 6x read nothing or one digit.
        _level = new TextRegionTracker(layout["status.level"].Rect, upscale: 8) { StableFrames = stableFrames };
        _jobName = new TextRegionTracker(layout["status.class_name"].Rect, upscale: 8) { StableFrames = stableFrames };
    }

    public IdentityReading? Current { get; private set; }

    /// <summary>Returns true when either part was re-read this frame.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var changed = _level.Update(geometry, frame) | _jobName.Update(geometry, frame);
        if (!changed) return false;

        int? level = null;
        var confidence = 1.0;
        if (_level.Lines is { Count: > 0 } l)
        {
            // Lines come back as "LV." and the number, or on a scaled frame as one line "LV. 31"; prefer a line
            // that is all digits, else the digits after the label.
            var numberLine = l.FirstOrDefault(x => x.Text.Trim().Length > 0 && x.Text.Trim().All(char.IsDigit))
                ?? l.FirstOrDefault(x => x.Text.Contains("LV", StringComparison.OrdinalIgnoreCase) && x.Text.Any(char.IsDigit));
            var digits = numberLine is null ? "" : new string(numberLine.Text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var v) && v is > 0 and < 300) level = v;
            confidence = Math.Min(confidence, l.Min(x => x.Confidence));
        }
        string job = "", name = "";
        if (_jobName.Lines is { Count: > 0 } jn)
        {
            job = JobNames.Snap(jn[0].Text.Trim());
            name = jn.Count > 1 ? jn[1].Text.Trim() : "";
            confidence = Math.Min(confidence, jn.Min(x => x.Confidence));
        }
        Current = new IdentityReading(level, job, name, confidence);
        return true;
    }
}
