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
        // Vision needs the "LV." label as context and a large upscale to read the small badge digits. As captured, only 8x
        // read them (6x and 10x read "E", "1 3" or nothing), and on the decimated fx-2x frames 8x read "3D" too; the white
        // digits on their orange tiles were the trouble. Drawn as black ink on white paper, the badge reads "31" at 6x, 8x
        // and 10x on every keyframe of fx-combat, fx-maps (bar its 9 black transition frames), fx-exact and fx-2x, so the
        // badge is binarized first. An exact frame's badge is binarized as it is, not softened first: softened, LV. 32 on
        // fx-anttunnel and fx-anttunnel-open read "3 2" at 8x on every keyframe (and "32" at 6x and 10x); as it is, "32" at
        // 6x, 8x and 10x, and 31 still reads at all three on fx-exact and fx-2x.
        _level = new TextRegionTracker(layout["status.level"].Rect, upscale: 8)
            { StableFrames = stableFrames, SoftenExactFrames = false, Prepare = (px, w, h) => OcrInput.InkOnPaper(px, w, h) };
        _jobName = new TextRegionTracker(layout["status.class_name"].Rect, upscale: 8) { StableFrames = stableFrames };
    }

    public IdentityReading? Current { get; private set; }

    /// <summary>
    /// The name the operator says the character has. A name line that reads it, ignoring case or with one character
    /// misread (<see cref="NameTagLocator.SameName"/>), is reported as this name; any other name is reported as read,
    /// so a different character logged in shows. Null reports every name as read.
    /// </summary>
    public string? ExpectedName { get; init; }

    /// <summary>Returns true when either part was re-read this frame.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var changed = _level.Update(geometry, frame) | _jobName.Update(geometry, frame);
        if (!changed) return false;

        int? level = null;
        var confidence = 1.0;
        if (_level.Lines is { Count: > 0 } l)
        {
            level = ParseLevel(l);
            confidence = Math.Min(confidence, l.Min(x => x.Confidence));
        }
        string job = "", name = "";
        if (_jobName.Lines is { Count: > 0 } jn)
        {
            job = JobNames.Snap(jn[0].Text.Trim());
            name = jn.Count > 1 ? jn[1].Text.Trim() : "";
            if (ExpectedName is { } expected && (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) || NameTagLocator.SameName(name, expected))) name = expected;
            confidence = Math.Min(confidence, jn.Min(x => x.Confidence));
        }
        Current = new IdentityReading(level, job, name, confidence);
        return true;
    }

    /// <summary>
    /// The level from the badge's OCR lines. They come back as "LV." and the number, on a scaled frame as one line
    /// "LV. 31", and with one digit per tile also as "3 2" or as two lines "3" and "2"; so the digits of the lines right of
    /// the label that hold only digits and spaces are joined left to right, else (or when that is no level) the first such
    /// line alone, else the digits after the label. A level is one to three digits from 1 to 299.
    /// </summary>
    public static int? ParseLevel(IReadOnlyList<TextLine> lines)
    {
        static int? Level(string digits) => digits.Length is >= 1 and <= 3 && int.TryParse(digits, out var v) && v is > 0 and < 300 ? v : null;
        static string Digits(string text) => new(text.Where(char.IsDigit).ToArray());
        var label = lines.FirstOrDefault(x => x.Text.Contains("LV", StringComparison.OrdinalIgnoreCase));
        var numberLines = lines.Where(x => x.Text.Any(char.IsDigit) && x.Text.All(c => char.IsDigit(c) || c == ' ')
            && (label is null || x.X > label.X + label.Width / 2)).OrderBy(x => x.X).ToList();
        if (numberLines.Count > 0)
            return Level(string.Concat(numberLines.Select(x => Digits(x.Text)))) ?? Level(Digits(numberLines[0].Text));
        if (label is null || !label.Text.Any(char.IsDigit)) return null;
        var after = label.Text[(label.Text.IndexOf("LV", StringComparison.OrdinalIgnoreCase) + 2)..];
        return Level(Digits(new string(after.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == ' ').ToArray())));
    }
}
