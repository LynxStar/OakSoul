namespace OakSoul.Vision;

/// <summary>
/// A rectangle of BGRA pixels read in place: <see cref="Width"/> by <see cref="Height"/> pixels whose rows lie
/// <see cref="Stride"/> bytes apart in <see cref="Pixels"/>. On a native frame a region is a view straight into the
/// frame (stride the frame's row), so readers need not copy it; a packed buffer is a view with stride Width * 4.
/// </summary>
public readonly ref struct BgraView
{
    /// <summary>From the region's top-left pixel to the last byte of its bottom row.</summary>
    public ReadOnlySpan<byte> Pixels { get; }
    /// <summary>Bytes from one row to the next.</summary>
    public int Stride { get; }
    public int Width { get; }
    public int Height { get; }

    public BgraView(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width < 0 || height < 0 || stride < width * 4 || (width > 0 && height > 0 && pixels.Length < (height - 1) * stride + width * 4))
            throw new ArgumentException($"a {width}x{height} view with stride {stride} does not fit {pixels.Length} bytes");
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    /// <summary>A tightly packed buffer: rows Width * 4 bytes apart.</summary>
    public BgraView(ReadOnlySpan<byte> packed, int width, int height) : this(packed, width, height, width * 4) { }

    /// <summary>Byte offset of pixel (x, y) in <see cref="Pixels"/>; its channels are B, G, R, A from there.</summary>
    public int Offset(int x, int y) => y * Stride + x * 4;

    /// <summary>Row y's Width * 4 bytes.</summary>
    public ReadOnlySpan<byte> Row(int y) => Pixels.Slice(y * Stride, Width * 4);
}
