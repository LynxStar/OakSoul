namespace OakSoul.Capture;

/// <summary>
/// One shareable window as reported by ScreenCaptureKit. Sizes are in points (logical pixels); <see cref="Scale"/>
/// is the backing scale factor (pixels per point) of the display the window is on, 2 on Retina, 1 if unknown.
/// </summary>
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
    int Layer,
    double Scale = 1)
{
    public override string ToString() => $"{Id} pid={Pid} {Width:0}x{Height:0} @{Scale:0.##}x {(OnScreen ? "on-screen" : "off-screen")} {App}: {Title}";
}
