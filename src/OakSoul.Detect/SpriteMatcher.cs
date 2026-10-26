using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// A colour modulation the client applies to a whole sprite. Frozen: the Ice wizard's freeze tints the mob blue. On
/// pixel-exact frames it is a per-channel multiply by (255, 168, 10) / 255 in B, G, R, rounded: on fx-anttunnel-open's
/// frozen Zombie Mushroom every art level of every channel mapped to exactly one screen level (G 17 to 11, 85 to 56,
/// 255 to 168; R 85 to 3, 255 to 10; B unchanged). On the blurred, Display P3 recordings the same tint fitted as
/// (1.0, 0.67, 0.30).
/// </summary>
public enum SpriteTint : byte { None, Frozen }

/// <summary>How a frame's visible extent is placed on a candidate box before the window search.</summary>
public enum Align : byte { Centre, Left, Right }

/// <summary>A sprite frame placed on screen and how well the frame agrees with it.</summary>
/// <param name="Left">Screen x of the frame image's left column.</param>
/// <param name="Top">Screen y of its top row.</param>
/// <param name="Matches">Samples that agreed (strict: opaque pixels equal to the screen; tolerant: 3x3 means within <see cref="SpriteMatcher.PixelTolerance"/>).</param>
/// <param name="Compared">Samples compared (the rest fell on masked or off-screen pixels).</param>
/// <param name="Tint">The colour modulation the samples were compared under.</param>
/// <param name="Offset">Tolerant path: brightness added to every channel (levels), 0 as stored, up to 80 while the mob
/// flashes after a hit; negative: the alpha (in 1/256) of a white layer (steam) the mob was seen through. Strict path:
/// negative, the mean alpha (in 1/256) of the light layer over the pixels it explained (see <see cref="Fogged"/>), else 0.</param>
/// <param name="Rival">The best other species tried at the same placement (a recolour of the same shape), if any, and its score.</param>
/// <param name="Fogged">Strict path: agreeing pixels that matched only as seen through a light translucent layer (steam).</param>
/// <param name="Strict">Scored pixel for pixel on an exact frame (else by 3x3 means).</param>
public readonly record struct SpriteMatch(MobSpecies Species, SpriteFrame Frame, int Left, int Top, int Matches, int Compared, SpriteTint Tint, int Offset,
    MobSpecies? Rival = null, double RivalScore = 0, int Fogged = 0, bool Strict = false)
{
    public double Score => Compared == 0 ? 0 : (double)Matches / Compared;

    /// <summary>Screen point the frame's origin is drawn at: the mob's feet.</summary>
    public PointD Feet => new(Left + Frame.OriginX, Top + Frame.OriginY);

    public override string ToString() =>
        $"{Species.Name} {Frame.Key} {Score:0.00} ({Matches}/{Compared}{(Tint != SpriteTint.None ? $", {Tint.ToString().ToLowerInvariant()}" : "")}{(Offset > 0 ? $", +{Offset}" : Offset < 0 ? $", fog {-Offset / 256.0:0.00}{(Strict ? $" on {Fogged}" : "")}" : "")}) at {Left},{Top}";
}

/// <summary>
/// B, G and R over a screen region, and which of its pixels may be compared (inside the world viewport and not
/// masked); for blurred frames also the 3x3 means of the screen around each pixel. Built once per candidate and
/// shared by every frame tried on it.
/// </summary>
public sealed class ScreenPatch
{
    public int X0 { get; private set; }
    public int Y0 { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    internal int[] B = [], G = [], R = [];
    internal byte[] Ok = [];

    /// <summary>b | g &lt;&lt; 8 | r &lt;&lt; 16 per pixel, or -1 where it may not be compared.</summary>
    internal int[] Packed = [];

    /// <summary>The background behind each pixel, packed as <see cref="Packed"/>, or -1 where the mosaic cannot vouch for it (strict path only).</summary>
    internal int[] Background = [];

    /// <summary>3x3 means of the screen and whether all nine pixels were comparable (filled only with <c>means</c>).</summary>
    internal int[] MeanB = [], MeanG = [], MeanR = [];
    internal byte[] MeanOk = [];

    /// <summary>
    /// Fills the patch for the inclusive screen box (x0, y0)-(x1, y1), clamped to the frame; with <paramref name="means"/>
    /// also the 3x3 means; with a <paramref name="background"/> (the map's mosaic at the frame's registered camera) also
    /// the background behind each pixel.
    /// </summary>
    public void Fill(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int x0, int y0, int x1, int y1, bool means,
        (BackgroundMosaic Mosaic, int CameraX, int CameraY)? background = null)
    {
        var frameHeight = frame.Length / 4 / frameWidth;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(frameWidth - 1, x1); y1 = Math.Min(frameHeight - 1, y1);
        X0 = x0; Y0 = y0; Width = Math.Max(0, x1 - x0 + 1); Height = Math.Max(0, y1 - y0 + 1);
        var n = Width * Height;
        if (B.Length < n) { B = new int[n]; G = new int[n]; R = new int[n]; Ok = new byte[n]; Packed = new int[n]; Background = new int[n]; }
        var vp = mask.Viewport;
        var bits = mask.Bits;
        for (var y = 0; y < Height; y++)
        {
            var d = y * Width;
            var o = ((y0 + y) * frameWidth + x0) * 4;
            var vy = y0 + y - vp.Y;
            for (var x = 0; x < Width; x++, o += 4)
            {
                B[d + x] = frame[o]; G[d + x] = frame[o + 1]; R[d + x] = frame[o + 2];
                var vx = x0 + x - vp.X;
                Ok[d + x] = (uint)vx < (uint)mask.Width && (uint)vy < (uint)mask.Height && bits[vy * mask.Width + vx] == 0 ? (byte)1 : (byte)0;
                Packed[d + x] = Ok[d + x] != 0 ? frame[o] | frame[o + 1] << 8 | frame[o + 2] << 16 : -1;
            }
        }
        if (background is { } bg)
        {
            var m = bg.Mosaic;
            for (var y = 0; y < Height; y++)
            {
                var wy = bg.CameraY + y0 + y - m.OriginY;
                for (var x = 0; x < Width; x++)
                {
                    var wx = bg.CameraX + x0 + x - m.OriginX;
                    var i = wy * m.Width + wx;
                    Background[y * Width + x] = (uint)wx < (uint)m.Width && (uint)wy < (uint)m.Height && m.ForegroundUsable[i] != 0 ? (int)(m.Pixels[i] & 0xFFFFFF) : -1;
                }
            }
        }
        else Array.Fill(Background, -1, 0, n);
        if (!means) return;
        if (MeanB.Length < n) { MeanB = new int[n]; MeanG = new int[n]; MeanR = new int[n]; MeanOk = new byte[n]; }
        Array.Clear(MeanOk, 0, n);
        for (var y = 1; y < Height - 1; y++)
            for (var x = 1; x < Width - 1; x++)
            {
                int b = 0, g = 0, r = 0, ok = 1;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var k = (y + dy) * Width + x - 1;
                    b += B[k] + B[k + 1] + B[k + 2]; g += G[k] + G[k + 1] + G[k + 2]; r += R[k] + R[k + 1] + R[k + 2];
                    ok &= Ok[k] & Ok[k + 1] & Ok[k + 2];
                }
                var i = y * Width + x;
                MeanB[i] = (b + 4) / 9; MeanG[i] = (g + 4) / 9; MeanR[i] = (r + 4) / 9; MeanOk[i] = (byte)ok;
            }
    }
}

/// <summary>
/// Verifies a foreground candidate against sprite frames and identifies it. Each frame is placed so that its visible
/// extent sits on the candidate box (bottom rows aligned, centres aligned) and searched over a small integer window.
/// Two ways of scoring a placement, chosen per frame by <see cref="Exact"/>:
/// <list type="bullet">
/// <item><b>Strict</b>, on pixel-exact frames (exact capture, live or keyframes): the client draws a sprite 1:1 at an
/// integer position, so every fully opaque pixel of the frame (<see cref="SpriteFrame.PixelX"/>) must show on screen as
/// stored, tinted when frozen, or seen through a light translucent layer (steam), wherever nothing covers it. The score
/// is the share of the visible opaque pixels that agree; pixels another sprite, a tag or an effect covers simply
/// disagree, so a partly hidden mob scores the share that shows, and pixels the background behind them shows anyway
/// are left out (<see cref="Background"/>). Every offset of the window is tried on a few spread pixels, the best few on
/// more, and the two best of those are scored in full.</item>
/// <item><b>Tolerant</b>, on the blurred point-size recordings (fx-combat, fx-maps): 3x3 means of the frame's interior
/// against 3x3 means of the screen (see <see cref="SpriteFrame"/>): a coarse pass on the blue channel over a spread
/// prefix of the samples on a grid of every second offset picks the offset, then the full sample set scores the two
/// best offsets under each tint, as drawn, with the hit flash's brightness fitted and seen through fitted steam,
/// stopping each count once the threshold is out of reach.</item>
/// </list>
/// Integer arithmetic throughout.
/// </summary>
public sealed class SpriteMatcher
{
    /// <summary>
    /// Search window around the aligned placement. The candidate's feet come from its box's bottom edge and the
    /// frame's from its lowest visible row; the open and the foreground threshold shave or add a pixel or two, and a
    /// box cut by an unreliable stretch of the mosaic or by a tag box loses a few more.
    /// </summary>
    public const int SearchX = 6, SearchY = 4;

    /// <summary>
    /// A frame is tried only when the candidate box is at most this fraction larger than its visible extent in both
    /// dimensions (more is another sprite or an effect merged in) ...
    /// </summary>
    public const double SizeTolerance = 0.3;

    /// <summary>
    /// ... and the extent at most this fraction larger than the box. A box is often cut: the axe of a Dark Axe Stump,
    /// a boar's flames or a snail's eyes fall on background the mosaic cannot vouch for, and the box loses them while
    /// the screen still shows them (on fx-combat Dark Axe Stumps were matched by a 74-row frame on 46-row boxes). The
    /// screen pixels, not the box, decide the score, so a larger frame costs nothing in false matches.
    /// </summary>
    public const double CutTolerance = 0.7;

    /// <summary>
    /// True for a pixel-exact frame: score strictly (see the class remarks). Set per frame by the detector from
    /// <see cref="DetectionContext.Strict"/>.
    /// </summary>
    public bool Exact { get; set; }

    // ---- strict path ----

    /// <summary>
    /// Largest difference per channel between a screen pixel and the frame's pixel (tinted when frozen) that still
    /// agrees: 0. Measured on the exact fixtures: every fully visible mob found on the keyframes of fx-exact and
    /// fx-anttunnel-open agrees on all its opaque pixels exactly (Red Snail 1026/1026, Axe Stump 3047/3047, Zombie
    /// Mushroom 2951/2951; the frozen Zombie Mushroom 3161/3161 through the tint). The x264 video of the same keyframes
    /// moves a sprite pixel by a median of 9 levels in its worst channel (p90 21, p99 38; 2.7% exact), so no tolerance
    /// that is still strict would admit it: the video is matched by the tolerant path instead.
    /// </summary>
    public const int StrictTolerance = 0;

    /// <summary>The freeze as per-channel multipliers on 255 for B, G, R (see <see cref="SpriteTint.Frozen"/>).</summary>
    public const int FrozenB = 255, FrozenG = 168, FrozenR = 10;

    /// <summary>
    /// Steam: a pixel seen through a light translucent layer is s = t + a (c - t) per channel, with a common alpha a and
    /// a light colour c (the steam sprite's, 221 to 255 per channel). Measured on the steamed Axe Stump of fx-exact's
    /// keyframe 256: a ran from 0.04 to 0.59 across the mob, the same in all three channels to within rounding, so one
    /// alpha per sprite (the old fit) does not hold; each pixel gets its own. A pixel agrees through steam when it is
    /// no darker than the art in any channel, a fitted against white lies between <see cref="MinFog"/> and
    /// <see cref="MaxFog"/>, and every channel lies within <see cref="FogSlack"/> of t + a (255 - t).
    /// </summary>
    public const double MinFog = 0.03, MaxFog = 0.75;

    /// <summary>Per-channel slack of the steam test (levels): the layer's colour is a light grey-blue, not pure white.</summary>
    public const int FogSlack = 6;

    /// <summary>
    /// Art pixels this light (sum of 255 - t over the channels under it) carry no steam information: any light screen
    /// pixel fits them.
    /// </summary>
    public const int FogMinHeadroom = 60;

    /// <summary>
    /// Nor do grey art pixels (largest minus smallest channel under this): black outlines and grey shading fit any
    /// lighter grey, which white spell effects, damage numbers and clouds supply. Only coloured pixels are tested
    /// through steam, where the lightened colour must keep the hue.
    /// </summary>
    public const int FogMinChroma = 34;

    /// <summary>
    /// Coarse pass, on pixels equal as drawn or frozen or fitting through steam (<see cref="Fog"/>, without the
    /// neighbour test): <see cref="StrictProbeSamples"/> spread pixels at every offset of the window, then
    /// <see cref="StrictCoarseSamples"/> at the <see cref="StrictProbeKeep"/> offsets that agreed most; the best must
    /// reach this fraction (<see cref="StrictCoarseFactor"/>) of the score needed to go on to the full score. Wrong
    /// frames and offsets rarely agree on a whole pixel by chance; on blue alone, a sixteen-level channel, they did on
    /// about one in eight, which let most wrong frames through to the full score.
    /// </summary>
    public const int StrictProbeSamples = 8, StrictProbeKeep = 6, StrictCoarseSamples = 32;
    public const double StrictCoarseFactor = 0.5;

    /// <summary>At most this many opaque pixels are scored per placement: a spread subset, so the share is an unbiased estimate.</summary>
    public const int MaxStrictPixels = 512;

    /// <summary>
    /// Share of a frame's visible opaque pixels that must agree, on an exact frame. On the keyframes of fx-exact and
    /// fx-anttunnel-open (entity report, every candidate scored against every species, joined with an exhaustive exact
    /// search of the keyframes for every frame of every species) no candidate lying on no mob reached 0.10: effects
    /// 0.094, unknown blobs 0.092, the pet's and players' tagged candidates 0.078; on Ant Tunnel IV 0.052. Candidates on
    /// a mob the exhaustive search found scored 0.57 to 1.00 (21) and 0.25 to 1.00 (5), frozen ones 0.57 and 0.97, and
    /// a frozen Zombie Mushroom three quarters under a lightning bolt 0.17 to 0.25. 0.2 sits in that gap, twice the best
    /// non-mob; a whole pixel matching by chance is rare, so a mob needs only a fifth of itself in view.
    /// </summary>
    public const double ExactThreshold = 0.2;

    // ---- tolerant path ----

    /// <summary>
    /// Largest sum over B, G and R of the absolute differences between the 3x3 means of screen and frame for a sample
    /// to agree, on a blurred frame. Swept on fx-combat: 30 matched 21% of the mob detections in their frame, called the
    /// one Axe Stump's spawn two Axe Stumps on 10 frames (frozen or steamed Dark Axe Stumps) and found the species of 32%
    /// of the kills (see the entity report); 45 matched 34%, never exceeded a spawn count, found 34%; 60 matched 37% but
    /// let the best wrong species of the tag-labelled candidates reach 0.7 on 41% of them instead of 25%. The point-size
    /// capture blurred every edge and pulled colours toward grey (Display P3), so a tight tolerance mostly measured that.
    /// </summary>
    public const int PixelTolerance = 45;

    /// <summary>The tolerance in use on blurred frames.</summary>
    public int Tolerance { get; set; } = PixelTolerance;

    /// <summary>Strongest white layer (alpha) the tolerant path's fog model fits.</summary>
    public const double TolerantMaxFog = 0.7;

    /// <summary>Samples in the coarse pass at each offset of the window, and the fraction of them that must agree on blue to go on to the full score.</summary>
    public const int CoarseSamples = 24;
    public const double CoarseMin = 0.4;

    /// <summary>Largest brightness added by the hit flash (levels per channel), and the most a fit may take off (tolerant path only).</summary>
    public const int MaxFlash = 80, MinFlash = -8;

    /// <summary>The freeze tint as multipliers on 256 as fitted on the blurred recordings (see <see cref="SpriteTint.Frozen"/>).</summary>
    private static readonly (int B, int G, int R)[] Tints = [(256, 256, 256), (256, 172, 77)];

    /// <summary>
    /// Fraction of the samples that must agree for a match on a blurred frame. On fx-combat (entity report, every
    /// candidate scored against every species): of the candidates the tracker made a pet, a player or an effect, 0.2%
    /// reach 0.6 and 0.1% reach 0.7, while of the tag-labelled mobs 69% reach 0.6 and 58% reach 0.7 for their own
    /// species (the rest are mobs half hidden by another sprite, a tag box or steam). 0.65 sits in that gap; the species
    /// is then settled by the contest in the detector, not by the threshold.
    /// </summary>
    public const double DefaultThreshold = 0.65;

    /// <summary>The acceptance threshold for the current kind of frame.</summary>
    public double Threshold => Exact ? ExactThreshold : DefaultThreshold;

    /// <summary>At least this fraction of a frame's samples must fall on comparable (unmasked, on-screen) pixels.</summary>
    public const double MinComparable = 0.4;

    /// <summary>Frames scored (after the size prefilter) and skipped by it since construction, for the cost report.</summary>
    public long FramesScored { get; private set; }
    public long FramesSkippedBySize { get; private set; }

    /// <summary>When false, the strict path leaves out the freeze (drop icons are never tinted).</summary>
    public bool AllowFreeze { get; set; } = true;

    /// <summary>
    /// The map's mosaic and the frame's registered camera, set per frame by the detector. On the strict path a frame pixel
    /// whose colour equals the background behind it says nothing about the mob, since the screen shows that colour with
    /// or without it, and is left out of the score: on Ant Tunnel IV's black cave floor the black outlines of a
    /// misplaced Horny Mushroom agreed on 41 of 512 pixels.
    /// </summary>
    public (BackgroundMosaic Mosaic, int CameraX, int CameraY)? Background { get; set; }

    private readonly ScreenPatch _patch = new();

    /// <summary>
    /// <see cref="CutTolerance"/> on an exact frame. A strict score cannot be reached by chance (no pet, player or
    /// effect candidate on the exact fixtures came near the threshold), so a box may be a smaller fragment of the
    /// sprite: a mob that stood still through most of a short recording is partly baked into its mosaic and shows only
    /// where it has moved since (Zombie Mushrooms on fx-anttunnel-open, 38x37 boxes of a 60x67 frame). 1.5 also caught
    /// a steamed Axe Stump's 50x30 box on fx-exact but raised the sprite stage there from about 4 to 6.5 ms per frame.
    /// </summary>
    public const double StrictCutTolerance = 1.0;

    /// <summary>The cut tolerance for the current kind of frame.</summary>
    public double Cut => Exact ? StrictCutTolerance : CutTolerance;

    /// <summary>Whether a frame's extent fits a candidate box (the size prefilter), with the current kind of frame's cut tolerance.</summary>
    public bool Fits(SpriteFrame f, int boxWidth, int boxHeight) => SizeFits(f, boxWidth, boxHeight, Cut);

    /// <summary>Whether a frame's extent fits a candidate box (the size prefilter).</summary>
    public static bool SizeFits(SpriteFrame f, int boxWidth, int boxHeight, double cut = CutTolerance) =>
        boxWidth <= (1 + SizeTolerance) * f.ExtentWidth && boxHeight <= (1 + SizeTolerance) * f.ExtentHeight
        && f.ExtentWidth <= (1 + cut) * boxWidth && f.ExtentHeight <= (1 + cut) * boxHeight;

    /// <summary>
    /// Prepares the screen patch for a candidate: its box plus the search window and the room a frame up to
    /// <see cref="SizeTolerance"/> larger needs; or, for a tag with no blob, a generous region above its feet.
    /// </summary>
    public void Prepare(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box, PointD? feet)
    {
        (int, int, int, int) r;
        if (feet is { } p) r = ((int)p.X - 90, (int)p.Y - 140, (int)p.X + 90, (int)p.Y + 20);
        else
        {
            var padX = (int)(Cut * box.Width / 2) + SearchX + 2;
            var padY = (int)(Cut * box.Height) + SearchY + 2;
            r = (box.X - padX, box.Y - padY, box.X + box.Width - 1 + padX, box.Y + box.Height - 1 + SearchY + 2);
        }
        _patch.Fill(frame, frameWidth, mask, r.Item1 - 1, r.Item2 - 1, r.Item3 + 1, r.Item4 + 1, means: !Exact, Exact ? Background : null);
    }

    /// <summary>
    /// Tries the frames in order on the prepared candidate and returns the first that passes <see cref="Threshold"/>,
    /// or null. Frames are placed on <paramref name="box"/>, or by their origin on <paramref name="feet"/> when given.
    /// On an exact frame the remaining frames of the species that passed are scored too and the best of them wins: a
    /// neighbouring frame of the same animation shares most pixels and passes at a lower score.
    /// </summary>
    public SpriteMatch? First(CaptureRect box, PointD? feet, IEnumerable<(MobSpecies Species, SpriteFrame Frame)> order, bool sizeFilter = true)
    {
        SpriteMatch? found = null;
        foreach (var (species, f) in order)
        {
            if (found is { } m0 && (species.Id != m0.Species.Id || m0.Matches == m0.Compared)) break;
            if (sizeFilter && !Fits(f, box.Width, box.Height)) { FramesSkippedBySize++; continue; }
            var needed = found is { } m1 ? m1.Score : Threshold;
            if (Best(box, feet, species, f, needed) is not { } m || m.Score < Threshold) continue;
            if (!Exact) return m;
            if (found is null || m.Score > found.Value.Score) found = m;
        }
        return found;
    }

    /// <summary>
    /// For a box too wide for any single frame (two mobs side by side merged into one blob): tries frames whose
    /// height fits and which are narrower than the box, aligned on its left and on its right edge, and returns the
    /// first that passes.
    /// </summary>
    public SpriteMatch? FirstAtEdges(CaptureRect box, IEnumerable<(MobSpecies Species, SpriteFrame Frame)> order)
    {
        foreach (var (species, f) in order)
        {
            if (Math.Abs(f.ExtentHeight - box.Height) > SizeTolerance * f.ExtentHeight || f.ExtentWidth * (1 + SizeTolerance) > box.Width) { FramesSkippedBySize++; continue; }
            foreach (var align in new[] { Align.Left, Align.Right })
                if (Best(box, null, species, f, Threshold, align: align) is { } m && m.Score >= Threshold) return m;
        }
        return null;
    }

    /// <summary>
    /// The best placement of one frame within the window (default <see cref="SearchX"/> x <see cref="SearchY"/>) on the
    /// prepared patch. With <paramref name="needed"/> &gt; 0 the coarse pass may reject the frame outright and the full
    /// counts stop once that fraction is out of reach (the score is then only a bound below it).
    /// </summary>
    public SpriteMatch? Best(CaptureRect box, PointD? feet, MobSpecies species, SpriteFrame f, double needed, int searchX = SearchX, int searchY = SearchY,
        Align align = Align.Centre)
    {
        if ((Exact ? f.PixelX.Length : f.SampleX.Length) == 0) return null;
        FramesScored++;
        int left0, top0;
        if (feet is { } p) { left0 = (int)Math.Round(p.X) - f.OriginX; top0 = (int)Math.Round(p.Y) - f.OriginY; }
        else
        {
            left0 = align switch
            {
                Align.Left => box.X - f.X0,
                Align.Right => box.X + box.Width - 1 - f.X1,
                _ => (int)Math.Round(box.X + (box.Width - 1) / 2.0 - (f.X0 + f.X1) / 2.0),
            };
            top0 = box.Y + box.Height - 1 - f.Y1;
        }
        return Exact ? BestStrict(species, f, left0, top0, needed, searchX, searchY) : BestTolerant(species, f, left0, top0, needed, searchX, searchY);
    }

    /// <summary>The score of a frame at one exact placement on the prepared patch (no early stop).</summary>
    public SpriteMatch? At(MobSpecies species, SpriteFrame f, int left, int top)
    {
        if (Exact)
        {
            var n = Math.Min(f.PixelX.Length, MaxStrictPixels);
            if (n == 0) return null;
            var s = Strict(f, n, left, top, 0);
            return s.Compared < MinComparable * n ? null : new SpriteMatch(species, f, left, top, s.Matches, s.Compared, s.Tint, s.Offset, Fogged: s.Fogged, Strict: true);
        }
        var m = f.SampleX.Length;
        if (m == 0) return null;
        var r = Full(f, m, left, top, 0);
        return r.Compared < MinComparable * m ? null : new SpriteMatch(species, f, left, top, r.Matches, r.Compared, r.Tint, r.Offset);
    }

    // ---- strict ----

    private SpriteMatch? BestStrict(MobSpecies species, SpriteFrame f, int left0, int top0, double needed, int searchX, int searchY)
    {
        var n = Math.Min(f.PixelX.Length, MaxStrictPixels);
        var patch = _patch;
        var freeze = AllowFreeze;
        (int Agree, int Compared) Whole(int dx, int dy, int samples)
        {
            int left = left0 + dx - patch.X0, top = top0 + dy - patch.Y0, compared = 0, agree = 0, w = patch.Width, h = patch.Height;
            var packed = patch.Packed;
            var back = patch.Background;
            for (var i = 0; i < samples; i++)
            {
                int x = left + f.PixelX[i], y = top + f.PixelY[i];
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                var s = packed[y * w + x];
                if (s < 0 || back[y * w + x] == f.PixelPacked[i]) continue;
                compared++;
                // Equality of the packed colours is the strict tolerance of 0.
                if (s == f.PixelPacked[i] || (freeze && s == f.PixelFrozen[i])) { agree++; continue; }
                if (f.PixelFoggable[i] && (s & 0xFF) > f.PixelB[i] && Fog(s & 0xFF, (s >> 8) & 0xFF, s >> 16, f.PixelB[i], f.PixelG[i], f.PixelR[i]) > 0) agree++;
            }
            return (agree, compared);
        }
        // Every offset of the window, since an exact match has a one-pixel peak a grid could step over: a few pixels
        // first, then a larger prefix at the offsets that agreed most.
        var probe = Math.Min(StrictProbeSamples, n);
        Span<(int Key, int Dx, int Dy)> keep = stackalloc (int, int, int)[StrictProbeKeep];
        var kept = 0;
        for (var dy = -searchY; dy <= searchY; dy++)
            for (var dx = -searchX; dx <= searchX; dx++)
            {
                var (agree, compared) = Whole(dx, dy, probe);
                if (compared == 0) continue;
                var key = agree * 64 * 64 / compared - (Math.Abs(dx) + Math.Abs(dy));
                if (kept < keep.Length) keep[kept++] = (key, dx, dy);
                else
                {
                    var worst = 0;
                    for (var j = 1; j < kept; j++) if (keep[j].Key < keep[worst].Key) worst = j;
                    if (key > keep[worst].Key) keep[worst] = (key, dx, dy);
                }
            }
        var coarse = Math.Min(StrictCoarseSamples, n);
        int bestKey = -1, bestX = 0, bestY = 0, secondKey = -1, secondX = 0, secondY = 0, bestAgree = 0, bestN = 0;
        for (var j = 0; j < kept; j++)
        {
            var (_, dx, dy) = keep[j];
            var (agree, compared) = Whole(dx, dy, coarse);
            if (compared == 0) continue;
            var key = agree * 64 * 64 / compared - (Math.Abs(dx) + Math.Abs(dy));
            if (key > bestKey)
            {
                secondKey = bestKey; secondX = bestX; secondY = bestY;
                bestKey = key; bestX = left0 + dx; bestY = top0 + dy; bestAgree = agree; bestN = compared;
            }
            else if (key > secondKey) { secondKey = key; secondX = left0 + dx; secondY = top0 + dy; }
        }
        if (bestKey < 0) return null;
        if (needed > 0 && bestAgree < StrictCoarseFactor * needed * bestN)
            return new SpriteMatch(species, f, bestX, bestY, bestAgree * n / Math.Max(1, bestN) / 2, n, SpriteTint.None, 0, Strict: true);
        var best = Strict(f, n, bestX, bestY, needed);
        if (secondKey >= 0)
        {
            var other = Strict(f, n, secondX, secondY, needed);
            if (RankStrict(other, n) > RankStrict(best, n)) { best = other; bestX = secondX; bestY = secondY; }
        }
        if (best.Compared < MinComparable * n) return null;
        return new SpriteMatch(species, f, bestX, bestY, best.Matches, best.Compared, best.Tint, best.Offset, Fogged: best.Fogged, Strict: true);
    }

    private static double RankStrict((int Matches, int Compared, SpriteTint, int, int) r, int n) => r.Compared < MinComparable * n ? -1 : (double)r.Matches / r.Compared;

    /// <summary>
    /// Scores the first <paramref name="n"/> opaque pixels at one placement under each tint: a pixel agrees when it equals
    /// the art (frozen: the tinted art) or fits it seen through steam (<see cref="Fog"/>). The best tint wins. Counting
    /// stops once <paramref name="needed"/> is out of reach for every tint.
    /// </summary>
    private (int Matches, int Compared, SpriteTint Tint, int Offset, int Fogged) Strict(SpriteFrame f, int n, int left, int top, double needed)
    {
        var patch = _patch;
        int lx = left - patch.X0, ty = top - patch.Y0;
        var compared = 0;
        var back = patch.Background;
        for (var i = 0; i < n; i++)
        {
            int x = lx + f.PixelX[i], y = ty + f.PixelY[i];
            if ((uint)x < (uint)patch.Width && (uint)y < (uint)patch.Height && patch.Ok[y * patch.Width + x] != 0 && back[y * patch.Width + x] != f.PixelPacked[i]) compared++;
        }
        if (compared == 0) return (0, 0, SpriteTint.None, 0, 0);
        var allowed = needed > 0 ? (int)((1 - needed) * compared) + 1 : int.MaxValue;
        var freeze = AllowFreeze;
        int plain = 0, plainFog = 0, plainMiss = 0, frozen = 0, frozenFog = 0, frozenMiss = 0;
        long plainAlpha = 0, frozenAlpha = 0;
        for (var i = 0; i < n; i++)
        {
            int x = lx + f.PixelX[i], y = ty + f.PixelY[i];
            if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
            var k = y * patch.Width + x;
            if (patch.Ok[k] == 0 || back[k] == f.PixelPacked[i]) continue;
            int sb = patch.B[k], sg = patch.G[k], sr = patch.R[k];
            int tb = f.PixelB[i], tg = f.PixelG[i], tr = f.PixelR[i];
            if (Math.Abs(sb - tb) <= StrictTolerance && Math.Abs(sg - tg) <= StrictTolerance && Math.Abs(sr - tr) <= StrictTolerance) plain++;
            else if (Fog(sb, sg, sr, tb, tg, tr) is var a && a > 0 && Coherent(f, f.PixelX[i], f.PixelY[i], x, y, a, false)) { plain++; plainFog++; plainAlpha += a; }
            else plainMiss++;
            if (freeze)
            {
                int fb = (tb * FrozenB + 127) / 255, fg = (tg * FrozenG + 127) / 255, fr = (tr * FrozenR + 127) / 255;
                if (Math.Abs(sb - fb) <= StrictTolerance && Math.Abs(sg - fg) <= StrictTolerance && Math.Abs(sr - fr) <= StrictTolerance) frozen++;
                else if (Fog(sb, sg, sr, fb, fg, fr) is var b && b > 0 && Coherent(f, f.PixelX[i], f.PixelY[i], x, y, b, true)) { frozen++; frozenFog++; frozenAlpha += b; }
                else frozenMiss++;
            }
            else frozenMiss++;
            if (plainMiss > allowed && frozenMiss > allowed) break;
        }
        return frozen > plain
            ? (frozen, compared, SpriteTint.Frozen, frozenFog == 0 ? 0 : -(int)(frozenAlpha / frozenFog), frozenFog)
            : (plain, compared, SpriteTint.None, plainFog == 0 ? 0 : -(int)(plainAlpha / plainFog), plainFog);
    }

    /// <summary>
    /// Steam is a smooth layer: on the steamed Axe Stump the alpha of neighbouring pixels differed by a few hundredths.
    /// A pixel that fits through steam counts only when its right or lower neighbour (opaque in the frame) fits too,
    /// with an alpha within this much (in 1/256), or matches as drawn while this pixel's alpha is that small. Chance
    /// fits (white spell effects over a sprite's colours) have no such neighbour.
    /// </summary>
    public const int FogCoherence = 26;

    private bool Coherent(SpriteFrame f, int fx, int fy, int px, int py, int alpha, bool frozen)
    {
        var patch = _patch;
        for (var d = 0; d < 2; d++)
        {
            int nx = fx + (d == 0 ? 1 : 0), ny = fy + (d == 0 ? 0 : 1), sx = px + nx - fx, sy = py + ny - fy;
            if (nx >= f.Width || ny >= f.Height || (uint)sx >= (uint)patch.Width || (uint)sy >= (uint)patch.Height) continue;
            var o = (ny * f.Width + nx) * 4;
            if (f.Bgra[o + 3] != SpriteFrame.SolidAlpha) continue;
            var k = sy * patch.Width + sx;
            if (patch.Ok[k] == 0) continue;
            int tb = f.Bgra[o], tg = f.Bgra[o + 1], tr = f.Bgra[o + 2];
            if (frozen) { tb = (tb * FrozenB + 127) / 255; tg = (tg * FrozenG + 127) / 255; tr = (tr * FrozenR + 127) / 255; }
            int sb = patch.B[k], sg = patch.G[k], sr = patch.R[k];
            var neighbour = Math.Abs(sb - tb) <= StrictTolerance && Math.Abs(sg - tg) <= StrictTolerance && Math.Abs(sr - tr) <= StrictTolerance ? 0
                : FogAlpha(sb, sg, sr, tb, tg, tr);
            if (neighbour >= 0 && Math.Abs(neighbour - alpha) <= FogCoherence) return true;
        }
        return false;
    }

    /// <summary>The alpha (1/256) of a light layer that turns t into s on every channel within <see cref="FogSlack"/>, ignoring the art's chroma; -1 when none does.</summary>
    private static int FogAlpha(int sb, int sg, int sr, int tb, int tg, int tr)
    {
        int db = sb - tb, dg = sg - tg, dr = sr - tr;
        if (db < -1 || dg < -1 || dr < -1) return -1;
        int hb = 255 - tb, hg = 255 - tg, hr = 255 - tr, head = hb + hg + hr;
        if (head < FogMinHeadroom) return -1;
        var a = (db + dg + dr) * 256 / head;
        if (a > (int)(MaxFog * 256)) return -1;
        if (Math.Abs(db - (hb * a >> 8)) > FogSlack || Math.Abs(dg - (hg * a >> 8)) > FogSlack || Math.Abs(dr - (hr * a >> 8)) > FogSlack) return -1;
        return a;
    }

    /// <summary>
    /// Whether screen pixel s fits art pixel t seen through a light translucent layer (see <see cref="MinFog"/>): the
    /// layer's alpha in 1/256 when it does, else 0.
    /// </summary>
    /// <summary>Whether an art pixel can be tested through steam at all: coloured (<see cref="FogMinChroma"/>) and dark enough (<see cref="FogMinHeadroom"/>).</summary>
    public static bool Foggable(int tb, int tg, int tr) =>
        765 - tb - tg - tr >= FogMinHeadroom && Math.Max(tb, Math.Max(tg, tr)) - Math.Min(tb, Math.Min(tg, tr)) >= FogMinChroma;

    public static int Fog(int sb, int sg, int sr, int tb, int tg, int tr)
    {
        int db = sb - tb, dg = sg - tg, dr = sr - tr;
        if (db < -1 || dg < -1 || dr < -1) return 0;
        int hb = 255 - tb, hg = 255 - tg, hr = 255 - tr, head = hb + hg + hr;
        if (head < FogMinHeadroom || Math.Max(tb, Math.Max(tg, tr)) - Math.Min(tb, Math.Min(tg, tr)) < FogMinChroma) return 0;
        var a = (db + dg + dr) * 256 / head;
        if (a < (int)(MinFog * 256) || a > (int)(MaxFog * 256)) return 0;
        if (Math.Abs(db - (hb * a >> 8)) > FogSlack || Math.Abs(dg - (hg * a >> 8)) > FogSlack || Math.Abs(dr - (hr * a >> 8)) > FogSlack) return 0;
        return a;
    }

    // ---- tolerant ----

    private SpriteMatch? BestTolerant(MobSpecies species, SpriteFrame f, int left0, int top0, double needed, int searchX, int searchY)
    {
        var n = f.SampleX.Length;
        var patch = _patch;
        var coarse = Math.Min(CoarseSamples, n);
        int bestKey = -1, bestX = 0, bestY = 0, secondKey = -1, secondX = 0, secondY = 0, bestCoarse = 0, bestCoarseN = 0, bestDx = 0, bestDy = 0;
        Span<int> diffs = stackalloc int[CoarseSamples];
        var coarseTol = Tolerance / 2;
        void Coarse(int dx, int dy, Span<int> diffs)
        {
            int left = left0 + dx - patch.X0, top = top0 + dy - patch.Y0;
            int compared = 0, sum = 0;
            for (var i = 0; i < coarse; i++)
            {
                int x = left + f.SampleX[i], y = top + f.SampleY[i];
                if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
                var k = y * patch.Width + x;
                if (patch.MeanOk[k] == 0) continue;
                var d = patch.MeanB[k] - f.MeanB[i];
                diffs[compared++] = d;
                sum += d;
            }
            if (compared == 0) return;
            // Blue as stored, and blue brightened by the fitted flash (or steam).
            var offset = Math.Clamp(sum / compared, MinFlash, MaxFlash);
            int plain = 0, lifted = 0;
            for (var i = 0; i < compared; i++)
            {
                if (Math.Abs(diffs[i]) <= coarseTol) plain++;
                if (Math.Abs(diffs[i] - offset) <= coarseTol) lifted++;
            }
            var matches = Math.Max(plain, lifted);
            // Rank by agreeing fraction (in 1/64ths), then by closeness to the aligned placement.
            var key = matches * 64 * 64 / compared - (Math.Abs(dx) + Math.Abs(dy));
            if (key > bestKey)
            {
                if (bestX != left0 + dx || bestY != top0 + dy) { secondKey = bestKey; secondX = bestX; secondY = bestY; }
                bestKey = key; bestX = left0 + dx; bestY = top0 + dy; bestCoarse = matches; bestCoarseN = compared; bestDx = dx; bestDy = dy;
            }
            else if (key > secondKey && (bestX != left0 + dx || bestY != top0 + dy)) { secondKey = key; secondX = left0 + dx; secondY = top0 + dy; }
        }
        // A grid of every second offset over the window, then the eight neighbours of its best: 43 offsets instead of
        // 117 for the default window. Blurred sprites are a few pixels wide in every part, so the peak spans the grid step.
        for (var dy = -searchY; dy <= searchY; dy += 2)
            for (var dx = -searchX; dx <= searchX; dx += 2) Coarse(dx, dy, diffs);
        if (bestKey >= 0)
        {
            int cx = bestDx, cy = bestDy;
            for (var dy = cy - 1; dy <= cy + 1; dy++)
                for (var dx = cx - 1; dx <= cx + 1; dx++)
                    if ((dx != cx || dy != cy) && Math.Abs(dx) <= searchX + 1 && Math.Abs(dy) <= searchY + 1) Coarse(dx, dy, diffs);
        }
        if (bestKey < 0) return null;
        if (needed > 0 && bestCoarse < CoarseMin * bestCoarseN) return new SpriteMatch(species, f, bestX, bestY, bestCoarse * n / Math.Max(1, bestCoarseN) / 2, n, SpriteTint.None, 0);
        var best = Full(f, n, bestX, bestY, needed);
        if (secondKey >= 0)
        {
            var other = Full(f, n, secondX, secondY, needed);
            if (Rank(other, n) > Rank(best, n)) { best = other; bestX = secondX; bestY = secondY; }
        }
        if (best.Compared < MinComparable * n) return null;
        return new SpriteMatch(species, f, bestX, bestY, best.Matches, best.Compared, best.Tint, best.Offset);
    }

    private static double Rank((int Matches, int Compared, SpriteTint, int) r, int n) => r.Compared < MinComparable * n ? -1 : (double)r.Matches / r.Compared;

    /// <summary>
    /// Scores all samples at one placement under each tint in three ways, and returns the best: as drawn; brightened
    /// by a constant (the hit flash; the mean difference, clamped to <see cref="MinFlash"/>..<see cref="MaxFlash"/>);
    /// and seen through a white translucent layer (steam drifting over the mob: s = t + a (255 - t), a fitted by least
    /// squares, at most <see cref="TolerantMaxFog"/>). Each count stops once <paramref name="needed"/> is out of reach.
    /// </summary>
    private (int Matches, int Compared, SpriteTint Tint, int Offset) Full(SpriteFrame f, int n, int left, int top, double needed)
    {
        var patch = _patch;
        int lx = left - patch.X0, ty = top - patch.Y0;
        const int white = 255;
        // Pass 1: comparable samples and, per tint, the sums for the flash and fog fits.
        Span<long> sumDiff = stackalloc long[Tints.Length];
        Span<long> fogNum = stackalloc long[Tints.Length];
        Span<long> fogDen = stackalloc long[Tints.Length];
        var compared = 0;
        for (var i = 0; i < n; i++)
        {
            int x = lx + f.SampleX[i], y = ty + f.SampleY[i];
            if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
            var k = y * patch.Width + x;
            if (patch.MeanOk[k] == 0) continue;
            compared++;
            for (var t = 0; t < Tints.Length; t++)
            {
                int tb = f.MeanB[i] * Tints[t].B >> 8, tg = f.MeanG[i] * Tints[t].G >> 8, tr = f.MeanR[i] * Tints[t].R >> 8;
                int db = patch.MeanB[k] - tb, dg = patch.MeanG[k] - tg, dr = patch.MeanR[k] - tr;
                sumDiff[t] += db + dg + dr;
                fogNum[t] += (long)db * (white - tb) + (long)dg * (white - tg) + (long)dr * (white - tr);
                fogDen[t] += (long)(white - tb) * (white - tb) + (long)(white - tg) * (white - tg) + (long)(white - tr) * (white - tr);
            }
        }
        if (compared == 0) return (0, 0, SpriteTint.None, 0);
        var tol = Tolerance;
        var allowed = needed > 0 ? (int)((1 - needed) * compared) + 1 : int.MaxValue;
        (int Matches, int Compared, SpriteTint Tint, int Offset) best = (-1, compared, SpriteTint.None, 0);
        Span<(int Offset, int Fog)> variants = stackalloc (int, int)[3];
        for (var t = 0; t < Tints.Length; t++)
        {
            var flash = (int)Math.Clamp(sumDiff[t] / (3 * compared), MinFlash, MaxFlash);
            // Fog level a in 1/256: s = t + a (white - t).
            var fog = fogDen[t] == 0 ? 0 : (int)Math.Clamp(fogNum[t] * 256 / fogDen[t], 0, (long)(TolerantMaxFog * 256));
            var nv = 0;
            variants[nv++] = (0, 0);
            if (flash != 0) variants[nv++] = (flash, 0);
            if (fog >= 13) variants[nv++] = (0, fog);
            var (mb, mg, mr) = Tints[t];
            for (var v = 0; v < nv; v++)
            {
                var (offset, a) = variants[v];
                int matches = 0, misses = 0;
                for (var i = 0; i < n; i++)
                {
                    int x = lx + f.SampleX[i], y = ty + f.SampleY[i];
                    if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
                    var k = y * patch.Width + x;
                    if (patch.MeanOk[k] == 0) continue;
                    int tb = f.MeanB[i] * mb >> 8, tg = f.MeanG[i] * mg >> 8, tr = f.MeanR[i] * mr >> 8;
                    if (a != 0) { tb += (white - tb) * a >> 8; tg += (white - tg) * a >> 8; tr += (white - tr) * a >> 8; }
                    var d = Math.Abs(patch.MeanB[k] - tb - offset) + Math.Abs(patch.MeanG[k] - tg - offset) + Math.Abs(patch.MeanR[k] - tr - offset);
                    if (d <= tol) matches++;
                    else if (++misses > allowed) break;
                }
                if (matches > best.Matches) best = (matches, compared, (SpriteTint)t, a != 0 ? -a : offset);
            }
        }
        return best;
    }
}
