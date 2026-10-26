using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// Builds a map's <see cref="BackgroundMosaic"/> offline from replayed frames whose camera is known, in two passes
/// over the same frames.
/// <list type="number">
/// <item><see cref="Vote"/>: registers the frame against the mosaic so far (a few pixels around the tracker's camera,
/// whose x comes from the minimap dot and is only good to +-8 units), then casts a streaming majority vote
/// (Boyer-Moore) per world pixel. The vote needs one candidate and a counter per pixel instead of every sample, and
/// finds the per-pixel mode whenever one value holds a majority, which is exactly the static-background case.</item>
/// <item><see cref="Count"/>: with the refined cameras, counts per pixel how many camera positions saw it and how many
/// agreed with the voted value (the background seen through steam or a name-tag box agrees too); the fraction that
/// disagreed becomes the unreliability mask.</item>
/// </list>
/// The watcher runs both passes twice (<c>BackgroundBuild</c>): the second round registers every frame against the
/// first round's reliable pixels, which repairs frames the incremental registration got wrong while the votes were thin.
/// </summary>
public sealed class MosaicBuilder
{
    /// <summary>
    /// Sum of absolute B, G, R differences under which two samples count as the same value, in the vote and in the
    /// agreement count. Static tiles in lossless keyframes repeat exactly (difference 0); the slack only absorbs video
    /// compression when a mosaic is built from video, and is well under the foreground threshold.
    /// </summary>
    public const int SameTolerance = 24;

    /// <summary>A pixel needs this many frames before its agreement means anything; fewer is reported as too few.</summary>
    public const int MinSamples = 3;

    /// <summary>
    /// A pixel's value is only trusted once frames whose cameras differ by at least this many pixels (on either axis)
    /// agreed on it. A parallax layer is as steady as a tile while the camera rests, and fx-combat rests for many
    /// seconds at a time, so without this the distant cliffs and the waterfall seen from one resting place passed for
    /// background and came back as large false foreground as soon as the camera moved. Eight pixels shifts even a
    /// slow layer by a few pixels, which the agreement test then catches. With agreement also counted once per camera
    /// position (see <see cref="Count"/>) this leaves out 4% of the pixels seen by three or more frames.
    /// </summary>
    public const int MinViewpointShift = 8;

    /// <summary>
    /// Search range of the first round's refinement around the tracker's camera. The dot gives x to +-8; y is usually
    /// exact (snapped to a foothold) but 32 frames of fx-combat were 7 rows off and a few up to 22 (in the air, on
    /// ropes). A first try at +-12 x +-6 y pinned 46 frames on the window's edge.
    /// </summary>
    public const int RefineRangeX = 16, RefineRangeY = 16;

    /// <summary>
    /// Search range of the second round against the first round's reliable pixels; the refined offsets of fx-combat
    /// span x -19..17 and y -22..22.
    /// </summary>
    public const int ReRangeX = 20, ReRangeY = 20;

    /// <summary>
    /// Second-round frames whose match fraction against the first mosaic is below this are not voted. On fx-combat the
    /// refined match fraction has median 0.88, p5 0.73, p10 0.77; the 7 frames below 0.6 (down to 0.10) had their best
    /// offset at the edge of the window, i.e. a failed registration rather than a crowded frame.
    /// </summary>
    public const double MinVoteScore = 0.6;

    /// <summary>Second-round frames skipped for a poor registration.</summary>
    public int Rejected { get; private set; }

    /// <summary>Votes needed before a pixel is used as a reference for registering later frames (chosen, not tuned).</summary>
    public const int ReferenceVotes = 3;

    private readonly int _mapId, _originX, _originY, _width, _height;
    private readonly uint[] _candidate;
    private readonly ushort[] _votes;
    private readonly byte[] _reference;
    private ushort[]? _seen, _agree;
    private short[]? _firstCamX, _firstCamY, _lastCamX, _lastCamY;
    private byte[]? _shifted;
    private int _voted, _counted;

    public MosaicBuilder(int mapId, ViewBounds vr)
    {
        _mapId = mapId;
        _originX = vr.Left; _originY = vr.Top;
        _width = vr.Width; _height = vr.Height;
        _candidate = new uint[_width * _height];
        _votes = new ushort[_width * _height];
        _reference = new byte[_width * _height];
    }

    public int Voted => _voted;
    public int Counted => _counted;

    /// <summary>Per voted frame: the tracker's camera and the refined one, for the build summary.</summary>
    public List<(int GuessX, int GuessY, Registration Result)> Refinements { get; } = [];

    /// <summary>
    /// Pass 1: refines the camera against the mosaic so far and votes the frame's unmasked viewport pixels. Returns the
    /// refined integer camera, which pass 2 must be given for the same frame.
    /// </summary>
    /// <param name="exact">The camera is trusted as is (clamped on x, snapped on y). Without a reference the first frame
    /// voted anchors the mosaic, so frames are skipped (null returned) until an exact one arrives.</param>
    /// <param name="reference">A mosaic from an earlier round: when given, the frame is registered against its reliable
    /// pixels over the wider <see cref="ReRangeX"/> x <see cref="ReRangeY"/> window instead of against the votes so far.</param>
    public (int X, int Y)? Vote(ReadOnlySpan<byte> frame, int frameWidth, PointD camera, ScreenMask mask, bool exact, BackgroundMosaic? reference = null)
    {
        if (_voted == 0 && !exact && reference is null) return null;
        int gx = (int)Math.Round(camera.X), gy = (int)Math.Round(camera.Y);
        var camX = gx; var camY = gy;
        if (_voted > 0 || reference is not null)
        {
            var samples = CameraRegistration.Samples(frame, frameWidth, mask);
            var r = reference is null
                ? CameraRegistration.Register(frame, frameWidth, mask, samples, _candidate, _reference, _originX, _originY, _width, _height, gx, gy, RefineRangeX, RefineRangeY)
                : CameraRegistration.Register(frame, frameWidth, mask, samples, reference.Pixels, reference.Usable, reference.OriginX, reference.OriginY, reference.Width, reference.Height, gx, gy, ReRangeX, ReRangeY);
            Refinements.Add((gx, gy, r));
            if (r.Compared >= 150) { camX = r.CameraX; camY = r.CameraY; }
            // A frame that matches the reference this badly is misregistered (or mostly covered by effects) and would
            // smear the mosaic; skip its vote.
            if (reference is not null && r.Score < MinVoteScore) { Rejected++; return null; }
        }
        _voted++;
        int w = mask.Width, h = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y;
        for (var y = 0; y < h; y++)
        {
            var wy = camY + vy + y - _originY;
            if ((uint)wy >= (uint)_height) continue;
            var row = wy * _width;
            var src = ((y + vy) * frameWidth + vx) * 4;
            for (var x = 0; x < w; x++, src += 4)
            {
                if (mask.Bits[y * w + x] != 0) continue;
                var wx = camX + vx + x - _originX;
                if ((uint)wx >= (uint)_width) continue;
                var i = row + wx;
                var v = (uint)(frame[src] | frame[src + 1] << 8 | frame[src + 2] << 16) | 0xFF000000u;
                if (_votes[i] == 0) { _candidate[i] = v; _votes[i] = 1; }
                else if (CameraRegistration.Distance(_candidate[i], v) <= SameTolerance) { if (_votes[i] < ushort.MaxValue) _votes[i]++; }
                else _votes[i]--;
                _reference[i] = _votes[i] >= ReferenceVotes ? (byte)1 : (byte)0;
            }
        }
        return (camX, camY);
    }

    /// <summary>
    /// Mosaic chroma (max - min channel) needed before a darker or brighter sample counts as the background under a
    /// translucent layer: on grey rock and cloud any lighter or darker grey fits a white or black overlay, so there the
    /// test says nothing, and letting it vote made the greyish parallax cliffs of Deep Valley I look reliable.
    /// </summary>
    public const int OverlayMinChroma = 40;

    private static int Chroma(uint v)
    {
        int b = (int)(v & 0xFF), g = (int)((v >> 8) & 0xFF), r = (int)((v >> 16) & 0xFF);
        return Math.Max(b, Math.Max(g, r)) - Math.Min(b, Math.Min(g, r));
    }

    /// <summary>Clears votes and counts for another round over the same frames (keeping nothing but the map box).</summary>
    public void StartOver()
    {
        Array.Clear(_candidate); Array.Clear(_votes); Array.Clear(_reference);
        _seen = null; _agree = null; _firstCamX = null; _firstCamY = null; _lastCamX = null; _lastCamY = null; _shifted = null; _voted = 0; _counted = 0; Rejected = 0;
        Refinements.Clear();
    }

    /// <summary>
    /// Pass 2: counts how many camera positions saw each pixel and how many of them agreed with the voted value
    /// (consecutive frames from the same camera count once).
    /// </summary>
    public void Count(ReadOnlySpan<byte> frame, int frameWidth, int cameraX, int cameraY, ScreenMask mask)
    {
        _seen ??= new ushort[_width * _height];
        _agree ??= new ushort[_width * _height];
        _firstCamX ??= new short[_width * _height];
        _firstCamY ??= new short[_width * _height];
        _shifted ??= new byte[_width * _height];
        if (_lastCamX is null) { _lastCamX = new short[_width * _height]; Array.Fill(_lastCamX, short.MinValue); }
        _lastCamY ??= new short[_width * _height];
        _counted++;
        int w = mask.Width, h = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y;
        for (var y = 0; y < h; y++)
        {
            var wy = cameraY + vy + y - _originY;
            if ((uint)wy >= (uint)_height) continue;
            var row = wy * _width;
            var src = ((y + vy) * frameWidth + vx) * 4;
            for (var x = 0; x < w; x++, src += 4)
            {
                if (mask.Bits[y * w + x] != 0) continue;
                var wx = cameraX + vx + x - _originX;
                if ((uint)wx >= (uint)_width) continue;
                var i = row + wx;
                var v = (uint)(frame[src] | frame[src + 1] << 8 | frame[src + 2] << 16);
                // One sample per camera position: a long rest would otherwise outvote every other viewpoint, and a
                // parallax layer is steady for as long as the camera rests.
                if (_lastCamX[i] == cameraX && _lastCamY[i] == cameraY) continue;
                _lastCamX[i] = (short)cameraX; _lastCamY[i] = (short)cameraY;
                if (_seen[i] < ushort.MaxValue) _seen[i]++;
                var cand = _candidate[i];
                // The background seen through steam or a name-tag box still agrees with the voted value.
                if (CameraRegistration.Distance(cand, v) <= SameTolerance ||
                    Chroma(cand) >= OverlayMinChroma && ForegroundExtractor.Classify(frame[src], frame[src + 1], frame[src + 2], (int)(cand & 0xFF), (int)((cand >> 8) & 0xFF), (int)((cand >> 16) & 0xFF)) is PixelClass.Fog or PixelClass.Shade)
                {
                    if (_agree[i] < ushort.MaxValue) _agree[i]++;
                    if (_agree[i] == 1) { _firstCamX[i] = (short)cameraX; _firstCamY[i] = (short)cameraY; }
                    else if (Math.Abs(cameraX - _firstCamX[i]) >= MinViewpointShift || Math.Abs(cameraY - _firstCamY[i]) >= MinViewpointShift) _shifted[i] = 1;
                }
            }
        }
    }

    /// <summary>
    /// After the counting pass: overwrites the voted value of every pixel a lossless keyframe shows within
    /// <see cref="SameTolerance"/> of it with the keyframe's own value. A mosaic voted from an exact session's video
    /// (which has the coverage: thirty frames a second keep the incremental registration overlapping) carries the
    /// video's compression error, a median of 7 levels per channel and 30 or more at sharp edges, which the foreground
    /// threshold then sees as sprites all over high-contrast art; keyframes give the game's own pixels wherever they
    /// saw the background. Returns the pixels replaced.
    /// </summary>
    public int ExactRefine(ReadOnlySpan<byte> frame, int frameWidth, int cameraX, int cameraY, ScreenMask mask)
    {
        int w = mask.Width, h = mask.Height, vx = mask.Viewport.X, vy = mask.Viewport.Y, replaced = 0;
        for (var y = 0; y < h; y++)
        {
            var wy = cameraY + vy + y - _originY;
            if ((uint)wy >= (uint)_height) continue;
            var row = wy * _width;
            var src = ((y + vy) * frameWidth + vx) * 4;
            for (var x = 0; x < w; x++, src += 4)
            {
                if (mask.Bits[y * w + x] != 0) continue;
                var wx = cameraX + vx + x - _originX;
                if ((uint)wx >= (uint)_width) continue;
                var i = row + wx;
                if (_votes[i] == 0) continue;
                var v = (uint)(frame[src] | frame[src + 1] << 8 | frame[src + 2] << 16) | 0xFF000000u;
                if (v == (_candidate[i] | 0xFF000000u) || CameraRegistration.Distance(_candidate[i], v) > SameTolerance) continue;
                _candidate[i] = v;
                replaced++;
            }
        }
        return replaced;
    }

    public BackgroundMosaic Build()
    {
        if (_seen is null || _agree is null) throw new InvalidOperationException("run the counting pass first");
        var dis = new byte[_width * _height];
        var pixels = new uint[_width * _height];
        for (var i = 0; i < dis.Length; i++)
        {
            if (_seen[i] == 0) { dis[i] = BackgroundMosaic.Unseen; continue; }
            pixels[i] = _candidate[i] | 0xFF000000u;
            dis[i] = _seen[i] < MinSamples || _shifted![i] == 0 ? BackgroundMosaic.TooFewSamples : (byte)Math.Round(253.0 * (_seen[i] - _agree[i]) / _seen[i]);
        }
        return new BackgroundMosaic(_mapId, _originX, _originY, _width, _height, pixels, dis, _counted);
    }

    /// <summary>Pixels seen by at least <see cref="MinSamples"/> frames, and of those the ones agreed on from shifted viewpoints.</summary>
    public (long Sampled, long Shifted) ViewpointCoverage()
    {
        if (_seen is null || _shifted is null) return (0, 0);
        long n = 0, k = 0;
        for (var i = 0; i < _seen.Length; i++)
        {
            if (_seen[i] < MinSamples) continue;
            n++;
            if (_shifted[i] != 0) k++;
        }
        return (n, k);
    }

    /// <summary>Histogram of the disagreement fraction over pixels with enough samples, in tenths; for tuning.</summary>
    public int[] DisagreementHistogram(BackgroundMosaic mosaic)
    {
        var hist = new int[11];
        foreach (var d in mosaic.Disagreement) if (d < BackgroundMosaic.TooFewSamples) hist[(int)Math.Round(d / 25.3)]++;
        return hist;
    }
}
