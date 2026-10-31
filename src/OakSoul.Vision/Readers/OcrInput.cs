namespace OakSoul.Vision.Readers;

/// <summary>
/// Prepares crops for Vision OCR. Every OCR reader was tuned on point-size captures, where ScreenCaptureKit blurred the
/// Retina 2x composite down; exact capture delivers the game's own aliased pixel fonts instead, and Vision misreads them:
/// on fx-exact and the decimated fx-2x keyframes the status bar's LynxHope as LunxHope at every upscale from 4 to 10 and
/// the map name mostly as "Deep Valley !", on fx-2x the level badge as "3D". Softening the crop with the kernel the old
/// capture applied gives Vision back the input it reads well: LynxHope at upscales 6 to 10, and "Deep Valley 1", which
/// the map-name numeral rule turns into I, on all 40 of those keyframes. So <see cref="ForOcr"/> softens crops of
/// pixel-exact frames and passes blurred ones through unchanged, which keeps the old recordings' readings as they were.
/// </summary>
public static class OcrInput
{
    /// <summary>
    /// A one-pixel stroke at least this much brighter or darker than both horizontal neighbours exists only on an
    /// unfiltered frame. The status bar's job and name text is 177 luma levels above its background on exact frames;
    /// the point-size capture's blur leaves at most 129 (measured on every keyframe of fx-combat and fx-maps).
    /// </summary>
    public const int SharpStrokeContrast = 160;

    /// <summary>Strokes the job and name text must show for the frame to count as exact; LynxHope's two lines show 337.</summary>
    public const int MinSharpStrokes = 20;

    /// <summary>
    /// True when the frame holds the game's own pixels rather than a filtered copy: native geometry (a resampled frame never
    /// is) and sharp one-pixel strokes in the status bar's job and name text, which every in-game frame shows. Loading
    /// screens and other frames without the status bar count as not exact, which only means their crops are not softened.
    /// About 3,400 pixels, so it is cheap next to the OCR call it guards.
    /// </summary>
    public static bool IsPixelExact(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        if (!geometry.IsNative || !geometry.Layout.TryGet("status.class_name", out var region)) return false;
        var px = geometry.ViewNative(frame, region);
        int w = region.Width, h = region.Height, strokes = 0;
        for (var y = 0; y < h; y++)
            for (var x = 1; x < w - 1; x++)
            {
                int left = Luma(px, x - 1, y), here = Luma(px, x, y), right = Luma(px, x + 1, y);
                if (Math.Min(here - left, here - right) >= SharpStrokeContrast || Math.Min(left - here, right - here) >= SharpStrokeContrast)
                    if (++strokes >= MinSharpStrokes) return true;
            }
        return false;
    }

    /// <summary>The crop as OCR should see it: softened when the frame is pixel-exact, unchanged otherwise.</summary>
    public static byte[] ForOcr(FrameGeometry geometry, ReadOnlySpan<byte> frame, byte[] crop, int width, int height) =>
        IsPixelExact(geometry, frame) ? Soften(crop, width, height) : crop;

    /// <summary>
    /// The point-size capture's filter, separable [1/4, 1/2, 1/4] across and [1/8, 3/4, 1/8] down (see docs/capture-notes.md,
    /// Exactness), with edges clamped. BGRA in and out; alpha is set opaque.
    /// </summary>
    public static byte[] Soften(byte[] bgra, int width, int height)
    {
        var across = new int[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                int l = (y * width + Math.Max(x - 1, 0)) * 4, c = (y * width + x) * 4, r = (y * width + Math.Min(x + 1, width - 1)) * 4;
                for (var k = 0; k < 3; k++) across[(y * width + x) * 3 + k] = bgra[l + k] + 2 * bgra[c + k] + bgra[r + k];
            }
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            int up = Math.Max(y - 1, 0) * width, row = y * width, down = Math.Min(y + 1, height - 1) * width;
            for (var x = 0; x < width; x++)
            {
                for (var k = 0; k < 3; k++)
                    result[(row + x) * 4 + k] = (byte)((across[(up + x) * 3 + k] + 6 * across[(row + x) * 3 + k] + across[(down + x) * 3 + k] + 16) >> 5);
                result[(row + x) * 4 + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>
    /// Black text on white paper: pixels whose every channel exceeds <paramref name="minChannel"/> (white and light grey
    /// text) become black, everything else white. Used for the level badge, whose white digits sit on orange tiles.
    /// </summary>
    public static byte[] InkOnPaper(byte[] bgra, int width, int height, int minChannel = 160)
    {
        var result = new byte[width * height * 4];
        for (var o = 0; o < result.Length; o += 4)
        {
            var ink = Math.Min(bgra[o], Math.Min(bgra[o + 1], bgra[o + 2])) > minChannel;
            result[o] = result[o + 1] = result[o + 2] = ink ? (byte)0 : (byte)255;
            result[o + 3] = 255;
        }
        return result;
    }

    private static int Luma(BgraView px, int x, int y)
    {
        var o = px.Offset(x, y);
        return (px.Pixels[o] + px.Pixels[o + 1] * 2 + px.Pixels[o + 2]) >> 2;
    }
}
