using System.Text.Json;

namespace OakSoul.Capture;

/// <summary>One recognised line of text and where it sits in the image given, origin top-left.</summary>
public sealed record TextLine(string Text, double Confidence, double X, double Y, double Width, double Height)
{
    public override string ToString() => $"'{Text}' ({Confidence:0.00}) at {X:0},{Y:0} {Width:0}x{Height:0}";
}

/// <summary>
/// Text recognition through Apple's Vision framework, via the shim. Accurate on crisp UI text, needs no
/// training, and runs in a few tens of milliseconds on a small region; the image is upscaled first because
/// Vision prefers text taller than about a dozen pixels.
/// </summary>
public static class TextRecognizer
{
    public static unsafe IReadOnlyList<TextLine> Recognize(ReadOnlySpan<byte> bgra, int width, int height, int upscale = 3)
    {
        if (bgra.Length < width * height * 4) throw new ArgumentException("buffer is smaller than width * height * 4");
        int count;
        nint json, error;
        fixed (byte* p = bgra)
            count = NativeMethods.RecognizeText(p, width, height, width * 4, upscale, out json, out error);
        if (count < 0) throw new InvalidOperationException(NativeMethods.TakeString(error) ?? "text recognition failed");
        var text = NativeMethods.TakeString(json) ?? "[]";
        var lines = new List<TextLine>(count);
        using var doc = JsonDocument.Parse(text);
        foreach (var e in doc.RootElement.EnumerateArray())
            lines.Add(new TextLine(e.GetProperty("text").GetString() ?? "", e.GetProperty("confidence").GetDouble(),
                e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("width").GetDouble(), e.GetProperty("height").GetDouble()));
        return lines;
    }
}
