using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>
/// Runs Vision OCR on a fixed region only when its pixels change and then hold still for a few frames, so
/// text that changes rarely (names, titles, levels) costs a hash per frame and one OCR call per change.
/// The region is taken at the frame's own resolution, since downsampling a scaled frame only hurts OCR, and softened
/// when the frame is pixel-exact (see <see cref="OcrInput"/>). With a <see cref="Queue"/> the read runs on the OCR worker
/// and lands on a later <see cref="Update"/>, which then returns true; no frame waits for it.
/// </summary>
public sealed class TextRegionTracker
{
    private readonly Func<CaptureRect> _region;
    private readonly int _upscale;
    private ulong _lastHash, _candidateHash;
    private int _candidateFrames;
    /// <summary>The read on the worker, with the crop as captured for an unsoftened re-read once it is in.</summary>
    private (OcrRequest Request, bool Exact, byte[] Raw, int Width, int Height)? _pending;
    private (byte[] Raw, int Width, int Height)? _lastRaw;

    /// <summary>Frames the region must hold a new appearance before it is read.</summary>
    public int StableFrames { get; init; } = 3;

    /// <summary>Lines from the last read, top to bottom, or null before the first read.</summary>
    public IReadOnlyList<TextLine>? Lines { get; private set; }

    /// <summary>
    /// Optional reader-specific preparation of the crop (BGRA, width, height) before OCR, applied after a pixel-exact
    /// frame's crop was softened (unless <see cref="SoftenExactFrames"/> is off).
    /// </summary>
    public Func<byte[], int, int, byte[]>? Prepare { get; init; }

    /// <summary>
    /// Whether crops of pixel-exact frames are softened before <see cref="Prepare"/> and OCR (the default). Off for a
    /// region that <see cref="Prepare"/> binarizes anyway, where the softening only blurs the glyphs it then thresholds.
    /// </summary>
    public bool SoftenExactFrames { get; init; } = true;

    /// <summary>Whether the frame of the last read was pixel-exact, so <see cref="ReadUnsoftened"/> would see other pixels.</summary>
    public bool LastReadExact { get; private set; }

    /// <summary>The OCR worker the reads go to; null reads on the calling thread, as before there was a worker.</summary>
    public OcrQueue? Queue { get; init; }

    /// <summary>A read is out on the worker and not yet back.</summary>
    public bool Reading => _pending is not null;

    /// <summary>Reads the worker failed on (the region is read again once it settles), and the last message.</summary>
    public int Errors { get; private set; }
    public string? LastError { get; private set; }

    public TextRegionTracker(CaptureRect region, int upscale = 3) : this(() => region, upscale) { }

    /// <summary>For regions whose position is found per frame, such as anything inside the minimap panel.</summary>
    public TextRegionTracker(Func<CaptureRect> region, int upscale = 3)
    {
        _region = region;
        _upscale = upscale;
    }

    /// <summary>Returns true when a fresh read happened this frame (with a worker: when its lines came in this frame).</summary>
    public bool Update(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        if (_pending is { } p)
        {
            // One read out at a time; the region is not watched again until it is in.
            if (!p.Request.Done) return false;
            _pending = null;
            if (p.Request.Error is not null)
            {
                // The recogniser failed: the lines stay as they were, and the region is watched again from scratch.
                Errors++;
                LastError = p.Request.Error;
                _lastHash = 0;
                _candidateFrames = 0;
                return false;
            }
            LastReadExact = p.Exact;
            _lastRaw = (p.Raw, p.Width, p.Height);
            Lines = p.Request.Lines;
            return true;
        }
        var native = _region();
        var hashRegion = new HudRegion(0, "text.hash", RegionKind.Fixed, native, "");
        var hash = Hash(geometry.ViewNative(frame, hashRegion));
        if (hash == _lastHash && Lines is not null) return false;
        if (hash == _candidateHash) _candidateFrames++;
        else { _candidateHash = hash; _candidateFrames = 1; }
        if (_candidateFrames < StableFrames) return false;

        _lastHash = hash;
        var exact = OcrInput.IsPixelExact(geometry, frame);
        var block = geometry.ToFrame(_region());
        var raw = new HudRegion(0, "text.ocr", RegionKind.Fixed, block, "").Extract(frame, geometry.FrameWidth, geometry.FrameHeight);
        var px = PrepareCrop(raw, block.Width, block.Height, SoftenExactFrames && exact);
        if (Queue is null)
        {
            LastReadExact = exact;
            _lastRaw = (raw, block.Width, block.Height);
            Lines = TextRecognizer.Recognize(px, block.Width, block.Height, _upscale);
            return true;
        }
        _pending = (Queue.Enqueue(px, block.Width, block.Height, _upscale), exact, raw, block.Width, block.Height);
        return false;
    }

    /// <summary>
    /// Reads the region of this frame again without softening it, for a reader that can tell a wrong reading of the
    /// softened crop (see <see cref="OcrInput"/>) from a right one. Call right after <see cref="Update"/> returned true
    /// with <see cref="LastReadExact"/> set; <see cref="Lines"/> is left as it was. On the calling thread; with a worker
    /// use <see cref="BeginUnsoftened"/>.
    /// </summary>
    public IReadOnlyList<TextLine> ReadUnsoftened(FrameGeometry geometry, ReadOnlySpan<byte> frame)
    {
        var block = geometry.ToFrame(_region());
        var raw = new HudRegion(0, "text.ocr", RegionKind.Fixed, block, "").Extract(frame, geometry.FrameWidth, geometry.FrameHeight);
        return TextRecognizer.Recognize(PrepareCrop(raw, block.Width, block.Height, soften: false), block.Width, block.Height, _upscale);
    }

    /// <summary>
    /// Queues an unsoftened read of the crop the last read was taken from (the same frame's pixels), on the worker. Call after
    /// <see cref="Update"/> returned true; null without a worker or before any read.
    /// </summary>
    public OcrRequest? BeginUnsoftened() =>
        Queue is { } q && _lastRaw is { } r ? q.Enqueue(PrepareCrop(r.Raw, r.Width, r.Height, soften: false), r.Width, r.Height, _upscale) : null;

    private byte[] PrepareCrop(byte[] raw, int width, int height, bool soften)
    {
        var px = soften ? OcrInput.Soften(raw, width, height) : raw;
        if (Prepare is not null) px = Prepare(px, width, height);
        return px;
    }

    /// <summary>Coarse luma hash: a one-pixel wobble or resampling noise does not look like a change.</summary>
    public static ulong Hash(BgraView bgra)
    {
        var h = 14695981039346656037UL;
        for (var y = 0; y < bgra.Height; y++)
        {
            var row = bgra.Row(y);
            for (var o = 0; o + 3 < row.Length; o += 4)
            {
                var luma = (row[o] + row[o + 1] * 2 + row[o + 2]) >> 6;
                h ^= (byte)luma;
                h *= 1099511628211UL;
            }
        }
        return h;
    }
}
