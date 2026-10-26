namespace OakSoul.Capture;

/// <summary>Whether a recorder session's video holds the captured pixels exactly.</summary>
public static class VideoEncoding
{
    /// <summary>
    /// True when ffmpeg encoder arguments store RGB frames losslessly: libx264rgb at <c>-qp 0</c> or <c>-crf 0</c> (the
    /// recorder's <c>lossless</c> preset), FFV1, PNG or raw video, with no YUV pixel format. Everything else is lossy for
    /// the detector's purpose: x264 at CRF 18 in 4:4:4 moves a sprite pixel by a median of 9 levels in its worst channel
    /// (docs/capture-notes.md), and libx264 even at <c>-qp 0</c> converts RGB to YUV, which rounds.
    /// </summary>
    public static bool IsLossless(string? encoderArgs)
    {
        if (string.IsNullOrWhiteSpace(encoderArgs)) return false;
        var tokens = encoderArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? Value(params string[] names)
        {
            for (var i = 0; i < tokens.Length - 1; i++)
                if (names.Contains(tokens[i])) return tokens[i + 1];
            return null;
        }
        if (Value("-pix_fmt") is { } pix && (pix.StartsWith("yuv", StringComparison.Ordinal) || pix.StartsWith("nv", StringComparison.Ordinal))) return false;
        return Value("-c:v", "-vcodec", "-codec:v") switch
        {
            "libx264rgb" => Value("-qp") == "0" || Value("-crf") == "0",
            "ffv1" or "png" or "rawvideo" => true,
            _ => false,
        };
    }
}
