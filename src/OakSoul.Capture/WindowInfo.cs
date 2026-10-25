namespace OakSoul.Capture;

/// <summary>One shareable window as reported by ScreenCaptureKit. Sizes are in points (logical pixels).</summary>
public sealed record WindowInfo(
    uint Id,
    int Pid,
    string App,
    string Title,
    double X,
    double Y,
    double Width,
    double Height,
    bool OnScreen,
    int Layer)
{
    public override string ToString() => $"{Id} pid={Pid} {Width:0}x{Height:0} {(OnScreen ? "on-screen" : "off-screen")} {App}: {Title}";
}
