namespace OakSoul.Capture;

/// <summary>A region of a window to capture, in points relative to the window's top-left corner.</summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}+{X}+{Y}";
}
