using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>
/// Runs Vision OCR on a fixed region only when its pixels change and then hold still for a few frames, so
/// text that changes rarely (names, titles, levels) costs a hash per frame and one OCR call per change.
/// The region is taken at the frame's own resolution, since downsampling a scaled frame only hurts OCR, and softened
/// when the frame is pixel-exact (see <see cref="OcrInput"/>).
/// </summary>
public sealed class TextRegionTracker
{
    private readonly Func<CaptureRect> _region;
    private readonly int _upscale;
    private ulong _lastHash, _candidateHash;
    private int _candidateFrames;

    /// <summary>Frames the region must hold a new appearance before it is read.</summary>
    public int StableFrames { get; init; } = 3;

    /// <summary>Lines from the last read, top to bottom, or null before the first read.</summary>
    public IReadOnlyList<TextLine>? Lines { get; private set; }

    /// <summary>
    /// Optional reader-specific preparation of the crop (BGRA, width, height) before OCR, applied after
    /// <see cref="OcrInput.ForOcr"/> has softened a pixel-exact frame, so it always sees the blurred look it was tuned on.
    /// </summary>
    public Func<byte[], int, int, byte[]>? Prepare { get; init; }

    public TextRegionTracker(CaptureRect region, int upscale = 3) : this(() => region, upscale) { }

    /// <summary>For regions whose position is found per frame, such as anything inside the minimap panel.</summary>
    public TextRegionTracker(Func<CaptureRect> region, int upscale = 3)
    {
        _region = region;
        _upscale = upscale;
    }

    /// <summary>Returns true when a fresh read happened this frame.</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var native = _region();
        var hashRegion = new HudRegion(0, "text.hash", RegionKind.Fixed, native, "");
        var hash = Hash(geometry.ExtractNative(frame, hashRegion));
        if (hash == _lastHash && Lines is not null) return false;
        if (hash == _candidateHash) _candidateFrames++;
        else { _candidateHash = hash; _candidateFrames = 1; }
        if (_candidateFrames < StableFrames) return false;

        _lastHash = hash;
        var block = geometry.ToFrame(native);
        var px = new HudRegion(0, "text.ocr", RegionKind.Fixed, block, "").Extract(frame, geometry.FrameWidth, geometry.FrameHeight);
        px = OcrInput.ForOcr(geometry, frame, px, block.Width, block.Height);
        if (Prepare is not null) px = Prepare(px, block.Width, block.Height);
        Lines = TextRecognizer.Recognize(px, block.Width, block.Height, _upscale);
        return true;
    }

    /// <summary>Coarse luma hash: a one-pixel wobble or resampling noise does not look like a change.</summary>
    public static ulong Hash(byte[] bgra)
    {
        var h = 14695981039346656037UL;
        for (var o = 0; o + 3 < bgra.Length; o += 4)
        {
            var luma = (bgra[o] + bgra[o + 1] * 2 + bgra[o + 2]) >> 6;
            h ^= (byte)luma;
            h *= 1099511628211UL;
        }
        return h;
    }
}
