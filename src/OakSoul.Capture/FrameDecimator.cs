using System.Runtime.Intrinsics.Arm;

namespace OakSoul.Capture;

/// <summary>
/// Brings a frame captured at N times the point size back to point size by keeping one pixel of every N x N
/// block, the bottom-right one: source pixel (N*x + N-1, N*y + N-1). On a Retina display the compositor shows a
/// 1x game render as nearest-neighbour 2x2 blocks, so any pixel of a block is the game pixel, except that a
/// window's title-bar border line can cover the first row of the top blocks; the last row and column of a block
/// are clean. Asking ScreenCaptureKit for point-size output instead makes it filter the 2x image down, which blurs.
/// </summary>
public static class FrameDecimator
{
    /// <summary>
    /// Copies the BGRA image at <paramref name="source"/> (<paramref name="sourceStride"/> bytes per row) into
    /// <paramref name="destination"/> as a tightly packed (sourceWidth / scale) x (sourceHeight / scale) image.
    /// Only the kept pixels are read. Scale 1 is a plain copy.
    /// </summary>
    public static unsafe void Decimate(byte* source, int sourceWidth, int sourceHeight, int sourceStride, int scale, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        int width = sourceWidth / scale, height = sourceHeight / scale;
        var rowBytes = width * 4;
        if (destination.Length < rowBytes * height)
            throw new ArgumentException("destination is smaller than the decimated frame", nameof(destination));

        if (scale == 1)
        {
            if (sourceStride == rowBytes)
            {
                new ReadOnlySpan<byte>(source, rowBytes * height).CopyTo(destination);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    new ReadOnlySpan<byte>(source + (long)y * sourceStride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
            }
            return;
        }

        fixed (byte* target = destination)
        {
            if (scale == 2 && AdvSimd.Arm64.IsSupported)
            {
                // Four output pixels per step, the odd lanes of eight source pixels: 0.9 ms per 1366x768 frame on
                // the capture callback's thread, against 2.1 ms for the scalar loop below.
                for (var y = 0; y < height; y++)
                {
                    var src = (uint*)(source + (long)(2 * y + 1) * sourceStride);
                    var dst = (uint*)(target + (long)y * rowBytes);
                    var x = 0;
                    for (; x <= width - 4; x += 4)
                        AdvSimd.Store(dst + x, AdvSimd.Arm64.UnzipOdd(AdvSimd.LoadVector128(src + 2 * x), AdvSimd.LoadVector128(src + 2 * x + 4)));
                    for (; x < width; x++) dst[x] = src[2 * x + 1];
                }
                return;
            }
            for (var y = 0; y < height; y++)
            {
                var src = (uint*)(source + (long)(y * scale + scale - 1) * sourceStride) + (scale - 1);
                var dst = (uint*)(target + (long)y * rowBytes);
                for (var x = 0; x < width; x++) dst[x] = src[x * scale];
            }
        }
    }

    /// <inheritdoc cref="Decimate(byte*, int, int, int, int, Span{byte})"/>
    public static unsafe void Decimate(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride, int scale, Span<byte> destination)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceStride < sourceWidth * 4
            || source.Length < (long)(sourceHeight - 1) * sourceStride + sourceWidth * 4)
            throw new ArgumentException("source is smaller than its stated size", nameof(source));
        fixed (byte* s = source) Decimate(s, sourceWidth, sourceHeight, sourceStride, scale, destination);
    }
}
