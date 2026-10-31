using OakSoul.Capture;

namespace OakSoul.Vision;

/// <summary>
/// Maps between a HUD layout's native coordinate space and an actual captured frame. The client aspect-fits its
/// native render into the window, so any frame is the native image scaled uniformly and centered, with black
/// bars on the axis that has room to spare. Regions are authored once in native space; this type places them
/// in the frame and can resample a region back to native size so readers never deal with the frame's scale.
/// </summary>
public sealed class FrameGeometry
{
    public HudLayout Layout { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }

    /// <summary>Frame pixels per native pixel.</summary>
    public double Scale { get; }

    /// <summary>Frame position of the native origin.</summary>
    public double OffsetX { get; }
    public double OffsetY { get; }

    /// <summary>The part of the frame that holds the native render, in frame pixels.</summary>
    public CaptureRect ContentRect { get; }

    /// <summary>True when every bar predicted by the fit was measured as black. Null when no frame was inspected.</summary>
    public bool? BarsConfirmed { get; }

    /// <summary>True when the frame is the native layout as-is and no resampling is needed.</summary>
    public bool IsNative => Scale == 1.0 && OffsetX == 0 && OffsetY == 0;

    private FrameGeometry(HudLayout layout, int frameWidth, int frameHeight, bool? barsConfirmed)
    {
        Layout = layout;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Scale = Math.Min((double)frameWidth / layout.FrameWidth, (double)frameHeight / layout.FrameHeight);
        var contentWidth = layout.FrameWidth * Scale;
        var contentHeight = layout.FrameHeight * Scale;
        OffsetX = (frameWidth - contentWidth) / 2.0;
        OffsetY = (frameHeight - contentHeight) / 2.0;
        ContentRect = new CaptureRect((int)Math.Round(OffsetX), (int)Math.Round(OffsetY),
            (int)Math.Round(contentWidth), (int)Math.Round(contentHeight));
        BarsConfirmed = barsConfirmed;
    }

    /// <summary>Geometry from the frame size alone, assuming the client's aspect-fit and centering.</summary>
    public static FrameGeometry Fit(HudLayout layout, int frameWidth, int frameHeight) => new(layout, frameWidth, frameHeight, null);

    /// <summary>
    /// Geometry from the frame size, checked against the pixels: the bars the fit predicts must be black. A
    /// frame with an uncropped title bar fails this check, which is the signal to record with --crop-top.
    /// </summary>
    public static FrameGeometry Detect(HudLayout layout, ReadOnlySpan<byte> bgra, int frameWidth, int frameHeight, int blackThreshold = 20)
    {
        var fit = Fit(layout, frameWidth, frameHeight);
        var c = fit.ContentRect;
        var confirmed = true;
        if (c.Y > 0)
            confirmed &= MeanLuma(bgra, frameWidth, 0, 0, frameWidth, c.Y) < blackThreshold
                      && MeanLuma(bgra, frameWidth, 0, c.Y + c.Height, frameWidth, frameHeight - c.Y - c.Height) < blackThreshold;
        if (c.X > 0)
            confirmed &= MeanLuma(bgra, frameWidth, 0, 0, c.X, frameHeight) < blackThreshold
                      && MeanLuma(bgra, frameWidth, c.X + c.Width, 0, frameWidth - c.X - c.Width, frameHeight) < blackThreshold;
        return new FrameGeometry(layout, frameWidth, frameHeight, confirmed);
    }

    /// <summary>Places a native rectangle in the frame, rounded outward to whole pixels.</summary>
    public CaptureRect ToFrame(CaptureRect native)
    {
        var x0 = OffsetX + native.X * Scale;
        var y0 = OffsetY + native.Y * Scale;
        var x1 = OffsetX + (native.X + native.Width) * Scale;
        var y1 = OffsetY + (native.Y + native.Height) * Scale;
        int fx = (int)Math.Floor(x0), fy = (int)Math.Floor(y0);
        return new CaptureRect(fx, fy, (int)Math.Ceiling(x1) - fx, (int)Math.Ceiling(y1) - fy);
    }

    public CaptureRect ToFrame(HudRegion region) => ToFrame(region.Rect);

    /// <summary>Converts a frame position to native coordinates, for detections made on the raw frame.</summary>
    public (double X, double Y) ToNative(double frameX, double frameY) => ((frameX - OffsetX) / Scale, (frameY - OffsetY) / Scale);

    /// <summary>Converts a native position to frame coordinates.</summary>
    public (double X, double Y) ToFramePoint(double nativeX, double nativeY) => (OffsetX + nativeX * Scale, OffsetY + nativeY * Scale);

    /// <summary>
    /// Returns the region's pixels at its native size as tightly packed BGRA, resampling from the frame with
    /// bilinear interpolation when the frame is not native. Readers can therefore be written once, for native pixels.
    /// </summary>
    public byte[] ExtractNative(ReadOnlySpan<byte> bgra, HudRegion region)
    {
        if (IsNative) return region.Extract(bgra, FrameWidth, FrameHeight);

        int w = region.Width, h = region.Height;
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = OffsetY + (region.Y + y + 0.5) * Scale - 0.5;
            var y0 = (int)Math.Floor(sy);
            var fy = sy - y0;
            var ya = Math.Clamp(y0, 0, FrameHeight - 1);
            var yb = Math.Clamp(y0 + 1, 0, FrameHeight - 1);
            for (var x = 0; x < w; x++)
            {
                var sx = OffsetX + (region.X + x + 0.5) * Scale - 0.5;
                var x0 = (int)Math.Floor(sx);
                var fx = sx - x0;
                var xa = Math.Clamp(x0, 0, FrameWidth - 1);
                var xb = Math.Clamp(x0 + 1, 0, FrameWidth - 1);
                int pa = (ya * FrameWidth + xa) * 4, pb = (ya * FrameWidth + xb) * 4;
                int pc = (yb * FrameWidth + xa) * 4, pd = (yb * FrameWidth + xb) * 4;
                var o = (y * w + x) * 4;
                for (var ch = 0; ch < 4; ch++)
                {
                    var top = bgra[pa + ch] + (bgra[pb + ch] - bgra[pa + ch]) * fx;
                    var bottom = bgra[pc + ch] + (bgra[pd + ch] - bgra[pc + ch]) * fx;
                    output[o + ch] = (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy), 0, 255);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The region's pixels at native size, as <see cref="ExtractNative"/> gives them but without the copy where none is
    /// needed: on a native frame a view straight into the frame (stride the frame's row), otherwise the resampled copy
    /// wrapped in a view. The view lives no longer than the frame; use <see cref="ExtractNative"/> to keep the pixels.
    /// </summary>
    public BgraView ViewNative(ReadOnlySpan<byte> bgra, HudRegion region) =>
        IsNative ? region.View(bgra, FrameWidth, FrameHeight) : new BgraView(ExtractNative(bgra, region), region.Width, region.Height);

    private static double MeanLuma(ReadOnlySpan<byte> bgra, int frameWidth, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        long sum = 0;
        for (var row = y; row < y + h; row++)
        {
            var o = (row * frameWidth + x) * 4;
            for (var col = 0; col < w; col++, o += 4)
                sum += (bgra[o] + bgra[o + 1] * 2 + bgra[o + 2]) >> 2; // cheap luma from B, G, R
        }
        return (double)sum / ((long)w * h);
    }

    public override string ToString() =>
        $"{FrameWidth}x{FrameHeight} frame: content {ContentRect}, scale {Scale:0.####}" +
        (BarsConfirmed is null ? "" : BarsConfirmed.Value ? ", bars confirmed black" : ", bars NOT black (title bar or non-standard layout?)");
}
