using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// A colour modulation the client applies to a whole sprite. Frozen: the Ice wizard's freeze tints the mob blue, a
/// per-channel multiply by (255, 168, 10) / 255 in B, G, R, rounded: on fx-anttunnel-open's frozen Zombie Mushroom every
/// art level of every channel mapped to exactly one screen level (G 17 to 11, 85 to 56, 255 to 168; R 85 to 3, 255 to
/// 10; B unchanged).
/// </summary>
public enum SpriteTint : byte { None, Frozen }

/// <summary>How a frame's visible extent is placed on a candidate box before the window search.</summary>
public enum Align : byte { Centre, Left, Right }

/// <summary>What a match's species rests on.</summary>
public enum SpeciesBasis : byte
{
    /// <summary>No other species of the map draws the same frame: the shape names it.</summary>
    Shape,
    /// <summary>A recolour shares the frame, and the pixels where the two differ settled it (<see cref="SpriteMatcher.Distinguish"/>).</summary>
    Pixels,
    /// <summary>A recolour shares the frame and too few telling pixels were visible: the track's species was kept.</summary>
    Track,
    /// <summary>As <see cref="Track"/> without a track species: the species with more spawns on the map.</summary>
    Spawns,
}

/// <summary>A sprite frame placed on screen and how well the frame agrees with it.</summary>
/// <param name="Left">Screen x of the frame image's left column.</param>
/// <param name="Top">Screen y of its top row.</param>
/// <param name="Matches">Opaque pixels equal to the screen pixel (as drawn, frozen, or through steam).</param>
/// <param name="Compared">Opaque pixels compared (the rest fell on masked or off-screen pixels, or on background of their own colour).</param>
/// <param name="Tint">The colour modulation the pixels were compared under.</param>
/// <param name="Fog">The mean alpha (in 1/256) of the light layer (steam) over the pixels it explained (see <see cref="Fogged"/>), else 0.</param>
/// <param name="Rival">The best other species tried at the same placement (a recolour of the same shape), if any, and its score.</param>
/// <param name="Fogged">Agreeing pixels that matched only as seen through a light translucent layer (steam).</param>
/// <param name="Basis">What the species rests on, once the detector's contest settled it.</param>
/// <param name="Clouded">Agreeing pixels that matched as the blend of the map's clouds over the art (<see cref="CloudLayer"/>).</param>
public readonly record struct SpriteMatch(MobSpecies Species, SpriteFrame Frame, int Left, int Top, int Matches, int Compared, SpriteTint Tint, int Fog,
    MobSpecies? Rival = null, double RivalScore = 0, int Fogged = 0, SpeciesBasis Basis = SpeciesBasis.Shape, int Clouded = 0)
{
    public double Score => Compared == 0 ? 0 : (double)Matches / Compared;

    /// <summary>Screen point the frame's origin is drawn at: the mob's feet.</summary>
    public PointD Feet => new(Left + Frame.OriginX, Top + Frame.OriginY);

    public override string ToString() =>
        $"{Species.Name} {Frame.Key} {Score:0.00} ({Matches}/{Compared}{(Tint != SpriteTint.None ? $", {Tint.ToString().ToLowerInvariant()}" : "")}{(Fogged > 0 ? $", fog {Fog / 256.0:0.00} on {Fogged}" : "")}{(Clouded > 0 ? $", cloud on {Clouded}" : "")}) at {Left},{Top}";
}

/// <summary>
/// B, G and R over a screen region, which of its pixels may be compared (inside the world viewport and not masked), and
/// the background behind each. Built once per candidate and shared by every frame tried on it.
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

    /// <summary>The background behind each pixel, packed as <see cref="Packed"/>, or -1 where the mosaic cannot vouch for it.</summary>
    internal int[] Background = [];

    /// <summary>
    /// The map's clouds over each pixel (<see cref="CloudLayer"/>): how many (<see cref="FogN"/>), and per layer, in draw
    /// order, the cloud pixel's colour and weight (art alpha times cloud alpha), at k * MaxLayers + layer. Filled only when a
    /// cloud covers the patch (<see cref="HasFog"/>).
    /// </summary>
    internal byte[] FogN = [];
    internal int[] FogC = [], FogW = [];

    /// <summary>The background seen through the clouds over it (<see cref="Fogged"/>), where <see cref="FogN"/> is non-zero and the background known.</summary>
    internal int[] BackFog = [];
    internal bool HasFog;

    internal void ClearFog()
    {
        var n = Width * Height;
        if (FogN.Length < n) { FogN = new byte[n]; FogC = new int[n * CloudLayer.MaxLayers]; FogW = new int[n * CloudLayer.MaxLayers]; BackFog = new int[n]; }
        else Array.Clear(FogN, 0, n);
        HasFog = true;
    }

    internal void AddFog(int k, int colour, int weight)
    {
        var n = FogN[k];
        if (n >= CloudLayer.MaxLayers) { Ok[k] = 0; Packed[k] = -1; return; }
        FogC[k * CloudLayer.MaxLayers + n] = colour;
        FogW[k * CloudLayer.MaxLayers + n] = weight;
        FogN[k] = (byte)(n + 1);
    }

    /// <summary>The share of the scene the clouds over patch pixel k let through.</summary>
    internal double Transmittance(int k)
    {
        int n = FogN[k], o = k * CloudLayer.MaxLayers;
        var t = 1.0;
        for (var j = 0; j < n; j++) t *= 1 - FogW[o + j] / 65025.0;
        return t;
    }

    /// <summary>A colour (packed) as the screen shows it at patch pixel k under the clouds over it: blended with each in draw order.</summary>
    internal int Fogged(int k, int colour)
    {
        int n = FogN[k], o = k * CloudLayer.MaxLayers;
        for (var j = 0; j < n; j++) colour = CloudLayer.BlendPacked(colour, FogC[o + j], FogW[o + j]);
        return colour;
    }

    /// <summary>
    /// Fills the patch for the inclusive screen box (x0, y0)-(x1, y1), clamped to the frame; with a
    /// <paramref name="background"/> (the map's mosaic at the frame's registered camera) also the background behind each pixel.
    /// </summary>
    /// <param name="explained">Pixels already explained (viewport coordinates, non-zero where explained), compared as little as masked ones.</param>
    /// <param name="clouds">The map's clouds placed in this frame: the patch then knows, per pixel, the clouds drawn over it.</param>
    public void Fill(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int x0, int y0, int x1, int y1,
        (BackgroundMosaic Mosaic, int CameraX, int CameraY)? background = null, byte[]? explained = null, CloudLayer? clouds = null)
    {
        var frameHeight = frame.Length / 4 / frameWidth;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(frameWidth - 1, x1); y1 = Math.Min(frameHeight - 1, y1);
        X0 = x0; Y0 = y0; Width = Math.Max(0, x1 - x0 + 1); Height = Math.Max(0, y1 - y0 + 1);
        var n = Width * Height;
        if (B.Length < n) { B = new int[n]; G = new int[n]; R = new int[n]; Ok = new byte[n]; Packed = new int[n]; Background = new int[n]; }
        var vp = mask.Viewport;
        var bits = mask.Bits;
        int mw = mask.Width, mh = mask.Height, w = Width;
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(frame);
        int[] pb = B, pg = G, pr = R, packed = Packed;
        var ok = Ok;
        for (var y = 0; y < Height; y++)
        {
            var d = y * w;
            var src = (y0 + y) * frameWidth + x0;
            var vy = y0 + y - vp.Y;
            var rowOk = (uint)vy < (uint)mh;
            var mrow = vy * mw - vp.X;
            for (var x = 0; x < w; x++)
            {
                var c = px[src + x] & 0xFFFFFF;
                pb[d + x] = c & 0xFF; pg[d + x] = c >> 8 & 0xFF; pr[d + x] = c >> 16;
                var vx = x0 + x - vp.X;
                var good = rowOk && (uint)vx < (uint)mw && bits[mrow + x0 + x] == 0 && (explained is null || explained[mrow + x0 + x] == 0);
                ok[d + x] = good ? (byte)1 : (byte)0;
                packed[d + x] = good ? c : -1;
            }
        }
        if (background is { } bg)
        {
            var m = bg.Mosaic;
            int bw = m.Width, bh = m.Height;
            var usable = m.ForegroundUsable;
            var pixels = m.Pixels;
            var back = Background;
            for (var y = 0; y < Height; y++)
            {
                var wy = bg.CameraY + y0 + y - m.OriginY;
                var d = y * w;
                if ((uint)wy >= (uint)bh) { Array.Fill(back, -1, d, w); continue; }
                var row = wy * bw;
                var wx0 = bg.CameraX + x0 - m.OriginX;
                for (var x = 0; x < w; x++)
                {
                    var wx = wx0 + x;
                    back[d + x] = (uint)wx < (uint)bw && usable[row + wx] != 0 ? (int)(pixels[row + wx] & 0xFFFFFF) : -1;
                }
            }
        }
        else Array.Fill(Background, -1, 0, n);
        HasFog = false;
        if (clouds is not null)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (clouds.Cover(this))
            {
                var back = Background;
                for (var k = 0; k < n; k++)
                {
                    if (FogN[k] == 0) continue;
                    if (back[k] >= 0) BackFog[k] = Fogged(k, back[k]);
                    // Under thick cloud (less than half the scene shows through) a light art colour and a light background
                    // blend alike; where the background is unknown that cannot be ruled out, so the pixel is not compared.
                    else if (Transmittance(k) < CloudLayer.MinUnknownTransmittance) { Ok[k] = 0; Packed[k] = -1; }
                }
            }
            clouds.CoverMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            clouds.Covers++;
        }
    }
}

/// <summary>
/// Verifies a foreground candidate against sprite frames and identifies it, on pixel-exact frames (exact capture, live,
/// from keyframes or from a lossless video). The client draws a sprite 1:1 at an integer position, so every fully
/// opaque pixel of a frame (<see cref="SpriteFrame.PixelX"/>) must show on screen as stored, tinted when frozen, or seen
/// through a light translucent layer (steam), wherever nothing covers it. Each frame is placed so that its visible
/// extent sits on the candidate box (bottom rows aligned, centres aligned) and searched over a small integer window:
/// every offset is tried on a few spread pixels, the best few on more, and the two best of those are scored in full. The
/// score is the share of the visible opaque pixels that agree; pixels another sprite, a tag or an effect covers simply
/// disagree, so a partly hidden mob scores the share that shows, and pixels the background behind them shows anyway are
/// left out (<see cref="Background"/>). Integer arithmetic throughout.
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
    /// ... and the extent at most this fraction larger than the box. A box is often cut: the axe of a Dark Axe Stump, a
    /// boar's flames or a snail's eyes fall on background the mosaic cannot vouch for, and the box loses them while the
    /// screen still shows them; and a mob that stood still through most of a short recording is partly baked into its
    /// mosaic and shows only where it has moved since (Zombie Mushrooms on fx-anttunnel-open, 38x37 boxes of a 60x67
    /// frame). A strict score cannot be reached by chance, so the screen pixels, not the box, decide. 1.5 also caught a
    /// steamed Axe Stump's 50x30 box on fx-exact but raised the sprite stage there from about 4 to 6.5 ms per frame.
    /// </summary>
    public const double CutTolerance = 1.0;

    /// <summary>
    /// The freeze as per-channel multipliers on 255 for B, G, R (see <see cref="SpriteTint.Frozen"/>). Every other
    /// comparison is equality: every fully visible mob found on the keyframes of fx-exact and fx-anttunnel-open agrees on
    /// all its opaque pixels exactly (Red Snail 1026/1026, Axe Stump 3047/3047, Zombie Mushroom 2951/2951; the frozen
    /// Zombie Mushroom 3161/3161 through the tint).
    /// </summary>
    public const int FrozenB = 255, FrozenG = 168, FrozenR = 10;

    /// <summary>
    /// Steam: a pixel seen through a light translucent layer is s = t + a (c - t) per channel, with a common alpha a and
    /// a light colour c (the steam sprite's, 221 to 255 per channel). Measured on the steamed Axe Stump of fx-exact's
    /// keyframe 256: a ran from 0.04 to 0.59 across the mob, the same in all three channels to within rounding, so each
    /// pixel gets its own alpha. A pixel agrees through steam when it is no darker than the art in any channel, both art
    /// and screen pixel are coloured (<see cref="FogMinChroma"/>, <see cref="FogMinScreenChroma"/>), a fitted against
    /// white lies between <see cref="MinFog"/> and <see cref="MaxFog"/>, and every channel lies within
    /// <see cref="FogSlack"/> of t + a (255 - t).
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
    /// ... and the screen pixel itself must still show a hue of at least one art level (the art has 4 bits per channel,
    /// every level a multiple of 17): seen through steam of alpha a, an art pixel keeps only (1 - a) of its chroma, and
    /// once that is within the slack of grey, any light surface with a faint warm tint fits it. On fx-combat-exact that
    /// let Dark Stump's die1/2 frame, a large light beige disc whose pixels barely pass <see cref="FogMinChroma"/>, fit
    /// steam over brown rock (frame 342, 150 of 512 pixels, all through steam of alpha 0.64, score 0.29), two drops under
    /// a cloud (frame 47, 0.27) and the pet against a white wall (frame 102, 0.19), above the 0.2 threshold. With the
    /// floor no candidate the tracker made a pet, player or effect reached 0.2 (those at 0.17 to 0.19 are a Dark Axe
    /// Stump behind a rope and one in steam, both taken for effects, and the pet), while matched mob detections went
    /// from 1122 to 1106 and the kills found as their species stayed at 27 of 58. A floor of 12 left frame 342 at 0.19;
    /// leaving out fogged pixels the background explains through the same layer brought it only to 0.25 (the mosaic
    /// there is mostly unusable).
    /// </summary>
    public const int FogMinScreenChroma = 17;

    /// <summary>
    /// Coarse pass, on pixels equal as drawn or frozen or fitting through steam (<see cref="Fog"/>, without the
    /// neighbour test): <see cref="ProbeSamples"/> spread pixels at every offset of the window, then
    /// <see cref="CoarseSamples"/> at the <see cref="ProbeKeep"/> offsets that agreed most; the best must reach this
    /// fraction (<see cref="CoarseFactor"/>) of the score needed to go on to the full score. Wrong frames and offsets
    /// rarely agree on a whole pixel by chance; on blue alone, a sixteen-level channel, they did on about one in eight,
    /// which let most wrong frames through to the full score.
    /// </summary>
    public const int ProbeSamples = 8, ProbeKeep = 6, CoarseSamples = 32;
    public const double CoarseFactor = 0.5;

    /// <summary>At most this many opaque pixels are scored per placement: a spread subset, so the share is an unbiased estimate.</summary>
    public const int MaxPixels = 512;

    /// <summary>
    /// Share of a frame's visible opaque pixels that must agree. On the keyframes of fx-exact and fx-anttunnel-open
    /// (entity report, every candidate scored against every species, joined with an exhaustive exact search of the
    /// keyframes for every frame of every species) no candidate lying on no mob reached 0.10: effects 0.094, unknown
    /// blobs 0.092, the pet's and players' tagged candidates 0.078; on Ant Tunnel IV 0.052. Candidates on a mob the
    /// exhaustive search found scored 0.57 to 1.00 (21) and 0.25 to 1.00 (5), frozen ones 0.57 and 0.97, and a frozen
    /// Zombie Mushroom three quarters under a lightning bolt 0.17 to 0.25. 0.2 sits in that gap, twice the best non-mob;
    /// a whole pixel matching by chance is rare, so a mob needs only a fifth of itself in view.
    /// </summary>
    public const double Threshold = 0.2;

    /// <summary>At least this fraction of a frame's pixels must fall on comparable (unmasked, on-screen) pixels.</summary>
    public const double MinComparable = 0.4;

    /// <summary>Frames scored (after the size prefilter) and skipped by it since construction, for the cost report.</summary>
    public long FramesScored { get; private set; }
    public long FramesSkippedBySize { get; private set; }

    /// <summary>When false, the freeze is left out (drop icons are never tinted).</summary>
    public bool AllowFreeze { get; set; } = true;

    /// <summary>
    /// The map's mosaic and the frame's registered camera, set per frame by the detector. A frame pixel whose colour
    /// equals the background behind it says nothing about the mob, since the screen shows that colour with or without
    /// it, and is left out of the score: on Ant Tunnel IV's black cave floor the black outlines of a misplaced Horny
    /// Mushroom agreed on 41 of 512 pixels.
    /// </summary>
    public (BackgroundMosaic Mosaic, int CameraX, int CameraY)? Background { get; set; }

    private readonly ScreenPatch _patch = new();

    /// <summary>The prepared screen patch, for comparisons the detector makes on the same pixels (the species contest's telling pixels).</summary>
    internal ScreenPatch Patch => _patch;

    /// <summary>
    /// The map's clouds placed in this frame (set per frame by the detector), or null. A sprite pixel under clouds is expected
    /// as the blend of the clouds over its art (<see cref="CloudLayer.Blend"/>, rounded as the client rounds), as well as as
    /// drawn; and it is left out where the background behind it shows the same through the same clouds.
    /// </summary>
    public CloudLayer? Clouds { get; set; }

    /// <summary>
    /// Screen pixels the temporal pass's verified predictions explain (viewport coordinates, the screen mask's layout, non-zero
    /// where explained), or null. Patches prepared while it is set leave them out like masked pixels, so the full search that
    /// runs on the rest of the frame does not find the predicted mobs a second time.
    /// </summary>
    public byte[]? Explained { get; set; }

    /// <summary>Whether a frame's extent fits a candidate box (the size prefilter).</summary>
    public static bool Fits(SpriteFrame f, int boxWidth, int boxHeight) =>
        boxWidth <= (1 + SizeTolerance) * f.ExtentWidth && boxHeight <= (1 + SizeTolerance) * f.ExtentHeight
        && f.ExtentWidth <= (1 + CutTolerance) * boxWidth && f.ExtentHeight <= (1 + CutTolerance) * boxHeight;

    /// <summary>
    /// Prepares the screen patch for a candidate: its box plus the search window and the room a frame up to
    /// <see cref="CutTolerance"/> larger needs; or, for a tag with no blob, a generous region above its feet.
    /// </summary>
    public void Prepare(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect box, PointD? feet)
    {
        (int, int, int, int) r;
        if (feet is { } p) r = ((int)p.X - 90, (int)p.Y - 140, (int)p.X + 90, (int)p.Y + 20);
        else
        {
            var padX = (int)(CutTolerance * box.Width / 2) + SearchX + 2;
            var padY = (int)(CutTolerance * box.Height) + SearchY + 2;
            r = (box.X - padX, box.Y - padY, box.X + box.Width - 1 + padX, box.Y + box.Height - 1 + SearchY + 2);
        }
        _patch.Fill(frame, frameWidth, mask, r.Item1 - 1, r.Item2 - 1, r.Item3 + 1, r.Item4 + 1, Background, Explained, Clouds);
    }

    /// <summary>Whether the prepared patch covers the screen region (inclusive), as far as it lies on the frame.</summary>
    public bool PatchCovers(int x0, int y0, int x1, int y1) =>
        _patch.Width > 0 && Math.Max(0, x0) >= _patch.X0 && Math.Max(0, y0) >= _patch.Y0 && x1 < _patch.X0 + _patch.Width && y1 < _patch.Y0 + _patch.Height;

    /// <summary>Prepares the screen patch for an explicit screen region (inclusive), for searches placing frames by their origin (<see cref="FootholdSearch"/>).</summary>
    public void PrepareRegion(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, int x0, int y0, int x1, int y1) =>
        _patch.Fill(frame, frameWidth, mask, x0, y0, x1, y1, Background, Explained, Clouds);

    /// <summary>
    /// Tries the frames in order on the prepared candidate and returns the best of the first species to pass
    /// <see cref="Threshold"/>, or null: once a frame passes, the remaining frames of its species are scored too, since a
    /// neighbouring frame of the same animation shares most pixels and passes at a lower score. Frames are placed on
    /// <paramref name="box"/>, or by their origin on <paramref name="feet"/> when given.
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
    /// <param name="minComparable">Share of the frame's pixels that must be comparable (default <see cref="MinComparable"/>).</param>
    public SpriteMatch? Best(CaptureRect box, PointD? feet, MobSpecies species, SpriteFrame f, double needed, int searchX = SearchX, int searchY = SearchY,
        Align align = Align.Centre, double minComparable = MinComparable)
    {
        if (f.PixelX.Length == 0) return null;
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
        return Search(species, f, left0, top0, needed, searchX, searchY, minComparable);
    }

    /// <summary>
    /// Compares two recolours (frames of the same geometry with their own pixels, <see cref="MapMobSet.Recolours"/>) at
    /// one placement on the pixels that tell them apart: those opaque in both frames whose colours differ (under
    /// <paramref name="tint"/>). Pixels they share (Axe Stump and Dark Axe Stump share every grey: the outlines, the axe
    /// head) say nothing about which of the two it is. Of the distinguishing pixels that are visible (on the patch,
    /// unmasked, not showing a background of either colour), counts those the screen explains by one frame only, as
    /// drawn or through steam (<see cref="Fog"/> with the neighbour test); a pixel both explain, or neither, counts for
    /// neither.
    /// </summary>
    /// <param name="decided">When given, counting stops once one side has at least this many and at least twice what the other
    /// can still reach (every remaining pixel counted for it): the outcome of <c>EntityDetector.Settle</c> cannot change.</param>
    public (int OnlyA, int OnlyB, int Visible) Distinguish(SpriteFrame a, SpriteFrame b, int left, int top, SpriteTint tint, int decided = 0)
    {
        var patch = _patch;
        int lx = left - patch.X0, ty = top - patch.Y0, onlyA = 0, onlyB = 0, visible = 0;
        var frozen = tint == SpriteTint.Frozen;
        if (a.Width != b.Width || a.Height != b.Height) return (0, 0, 0);
        for (var i = 0; i < a.PixelX.Length; i++)
        {
            int fx = a.PixelX[i], fy = a.PixelY[i], x = lx + fx, y = ty + fy;
            var o = (fy * b.Width + fx) * 4;
            if (b.Bgra[o + 3] != SpriteFrame.SolidAlpha) continue;
            int bb = b.Bgra[o], bg = b.Bgra[o + 1], br = b.Bgra[o + 2];
            var plainB = bb | bg << 8 | br << 16;
            int ab = a.PixelB[i], ag = a.PixelG[i], ar = a.PixelR[i];
            if (frozen)
            {
                ab = (ab * FrozenB + 127) / 255; ag = (ag * FrozenG + 127) / 255; ar = (ar * FrozenR + 127) / 255;
                bb = (bb * FrozenB + 127) / 255; bg = (bg * FrozenG + 127) / 255; br = (br * FrozenR + 127) / 255;
            }
            if (ab == bb && ag == bg && ar == br) continue;
            if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
            var k = y * patch.Width + x;
            if (patch.Ok[k] == 0 || patch.Background[k] == a.PixelPacked[i] || patch.Background[k] == plainB) continue;
            var clouded = patch.HasFog && patch.FogN[k] != 0;
            int cloudA = 0, cloudB = 0;
            if (clouded)
            {
                cloudA = patch.Fogged(k, ab | ag << 8 | ar << 16); cloudB = patch.Fogged(k, bb | bg << 8 | br << 16);
                if (patch.Background[k] >= 0 && (patch.BackFog[k] == patch.Fogged(k, a.PixelPacked[i]) || patch.BackFog[k] == patch.Fogged(k, plainB))) continue;
            }
            visible++;
            int sb = patch.B[k], sg = patch.G[k], sr = patch.R[k];
            var fitA = (sb == ab && sg == ag && sr == ar) || clouded && patch.Packed[k] == cloudA || (!clouded && Fog(sb, sg, sr, ab, ag, ar) is var fa && fa > 0 && Coherent(a, fx, fy, x, y, fa, frozen));
            var fitB = (sb == bb && sg == bg && sr == br) || clouded && patch.Packed[k] == cloudB || (!clouded && Fog(sb, sg, sr, bb, bg, br) is var fb && fb > 0 && Coherent(b, fx, fy, x, y, fb, frozen));
            if (fitA && !fitB) onlyA++;
            else if (fitB && !fitA) onlyB++;
            else continue;
            if (decided > 0)
            {
                var remaining = a.PixelX.Length - 1 - i;
                if (onlyA >= decided && onlyA >= 2 * (onlyB + remaining) || onlyB >= decided && onlyB >= 2 * (onlyA + remaining)) break;
            }
        }
        return (onlyA, onlyB, visible);
    }

    /// <summary>
    /// The screen pixels a match explains, on the prepared patch (which must cover it): its opaque pixels that agree with
    /// the screen under the tint it was scored with (equal as drawn or frozen, or through steam with a coherent neighbour,
    /// as <see cref="Score"/> counts them), and its partly transparent edge pixels, which blend the sprite with whatever lies
    /// behind it. Opaque pixels that disagree (something in front of the mob: the pet, a snail, a damage number) are not
    /// explained. Those set in <paramref name="mask"/> (viewport coordinates, <paramref name="viewport"/> its place on
    /// screen) are counted and, when <paramref name="clear"/>, cleared; returns the count and their extent on screen.
    /// </summary>
    /// <param name="explained">When given, every explained pixel (in the foreground or not) is marked in it, in the mask's layout.</param>
    public (int Pixels, CaptureRect? Extent) Explain(SpriteMatch m, byte[] mask, int maskWidth, int maskHeight, CaptureRect viewport, bool clear, byte[]? explained = null)
    {
        var patch = _patch;
        var f = m.Frame;
        var frozen = m.Tint == SpriteTint.Frozen;
        int count = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (var fy = 0; fy < f.Height; fy++)
            for (var fx = 0; fx < f.Width; fx++)
            {
                var o = (fy * f.Width + fx) * 4;
                var a = f.Bgra[o + 3];
                if (a == 0) continue;
                int sx = m.Left + fx, sy = m.Top + fy, vx = sx - viewport.X, vy = sy - viewport.Y;
                if ((uint)vx >= (uint)maskWidth || (uint)vy >= (uint)maskHeight) continue;
                var i = vy * maskWidth + vx;
                if (mask[i] == 0 && explained is null) continue;
                if (a == SpriteFrame.SolidAlpha)
                {
                    int px = sx - patch.X0, py = sy - patch.Y0;
                    if ((uint)px >= (uint)patch.Width || (uint)py >= (uint)patch.Height) continue;
                    var k = py * patch.Width + px;
                    int tb = f.Bgra[o], tg = f.Bgra[o + 1], tr = f.Bgra[o + 2];
                    if (frozen) { tb = (tb * FrozenB + 127) / 255; tg = (tg * FrozenG + 127) / 255; tr = (tr * FrozenR + 127) / 255; }
                    int sb = patch.B[k], sg = patch.G[k], sr = patch.R[k];
                    var clouded = patch.HasFog && patch.FogN[k] != 0;
                    var agrees = (sb == tb && sg == tg && sr == tr) || clouded && (sb | sg << 8 | sr << 16) == patch.Fogged(k, tb | tg << 8 | tr << 16)
                        || (!clouded && Fog(sb, sg, sr, tb, tg, tr) is var fa && fa > 0 && Coherent(f, fx, fy, px, py, fa, frozen));
                    if (!agrees) continue;
                }
                if (explained is not null) explained[i] = 1;
                if (mask[i] == 0) continue;
                count++;
                if (clear) mask[i] = 0;
                x0 = Math.Min(x0, sx); y0 = Math.Min(y0, sy); x1 = Math.Max(x1, sx); y1 = Math.Max(y1, sy);
            }
        return (count, count == 0 ? null : new CaptureRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
    }

    /// <summary>The score of a frame at one exact placement on the prepared patch (no early stop).</summary>
    public SpriteMatch? At(MobSpecies species, SpriteFrame f, int left, int top)
    {
        var n = Math.Min(f.PixelX.Length, MaxPixels);
        if (n == 0) return null;
        var s = Score(f, n, left, top, 0);
        return s.Compared < MinComparable * n ? null : new SpriteMatch(species, f, left, top, s.Matches, s.Compared, s.Tint, s.Fog, Fogged: s.Fogged, Clouded: s.Clouded);
    }


    private SpriteMatch? Search(MobSpecies species, SpriteFrame f, int left0, int top0, double needed, int searchX, int searchY, double minComparable = MinComparable)
    {
        var n = Math.Min(f.PixelX.Length, MaxPixels);
        var patch = _patch;
        var freeze = AllowFreeze;
        (int Agree, int Compared) Whole(int dx, int dy, int samples)
        {
            int left = left0 + dx - patch.X0, top = top0 + dy - patch.Y0, compared = 0, agree = 0, w = patch.Width, h = patch.Height;
            var packed = patch.Packed;
            var back = patch.Background;
            var fogN = patch.HasFog ? patch.FogN : null;
            for (var i = 0; i < samples; i++)
            {
                int x = left + f.PixelX[i], y = top + f.PixelY[i];
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                var k = y * w + x;
                var s = packed[k];
                if (s < 0 || back[k] == f.PixelPacked[i]) continue;
                // Under clouds: the art as blended with them, and the background likewise.
                var clouded = fogN is not null && fogN[k] != 0;
                var cloudArt = clouded ? patch.Fogged(k, f.PixelPacked[i]) : 0;
                if (clouded && back[k] >= 0 && patch.BackFog[k] == cloudArt) continue;
                compared++;
                // Equality of the packed colours: no tolerance.
                if (s == f.PixelPacked[i] || (freeze && s == f.PixelFrozen[i])) { agree++; continue; }
                if (clouded && (s == cloudArt || freeze && s == patch.Fogged(k, f.PixelFrozen[i]))) { agree++; continue; }
                // Where a cloud of the map is modelled the light layer is known: the steam rule is for the skills' steam.
                if (!clouded && f.PixelFoggable[i] && (s & 0xFF) > f.PixelB[i] && Fog(s & 0xFF, (s >> 8) & 0xFF, s >> 16, f.PixelB[i], f.PixelG[i], f.PixelR[i]) > 0) agree++;
            }
            return (agree, compared);
        }
        // Every offset of the window, since an exact match has a one-pixel peak a grid could step over: a few pixels
        // first, then a larger prefix at the offsets that agreed most.
        var probe = Math.Min(ProbeSamples, n);
        Span<(int Key, int Dx, int Dy)> keep = stackalloc (int, int, int)[ProbeKeep];
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
        var coarse = Math.Min(CoarseSamples, n);
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
        if (needed > 0 && bestAgree < CoarseFactor * needed * bestN)
            return new SpriteMatch(species, f, bestX, bestY, bestAgree * n / Math.Max(1, bestN) / 2, n, SpriteTint.None, 0);
        var best = Score(f, n, bestX, bestY, needed);
        if (secondKey >= 0)
        {
            var other = Score(f, n, secondX, secondY, needed);
            if (Rank(other, n, minComparable) > Rank(best, n, minComparable)) { best = other; bestX = secondX; bestY = secondY; }
        }
        if (best.Compared < minComparable * n) return null;
        return new SpriteMatch(species, f, bestX, bestY, best.Matches, best.Compared, best.Tint, best.Fog, Fogged: best.Fogged, Clouded: best.Clouded);
    }

    private static double Rank((int Matches, int Compared, SpriteTint, int, int, int) r, int n, double minComparable) => r.Compared < minComparable * n ? -1 : (double)r.Matches / r.Compared;

    /// <summary>
    /// Scores the first <paramref name="n"/> opaque pixels at one placement under each tint: a pixel agrees when it equals
    /// the art (frozen: the tinted art) or fits it seen through steam (<see cref="Fog"/>). The best tint wins. Counting
    /// stops once <paramref name="needed"/> is out of reach for every tint.
    /// </summary>
    private (int Matches, int Compared, SpriteTint Tint, int Fog, int Fogged, int Clouded) Score(SpriteFrame f, int n, int left, int top, double needed)
    {
        var patch = _patch;
        int lx = left - patch.X0, ty = top - patch.Y0;
        var compared = 0;
        var back = patch.Background;
        var fogN = patch.HasFog ? patch.FogN : null;
        for (var i = 0; i < n; i++)
        {
            int x = lx + f.PixelX[i], y = ty + f.PixelY[i];
            if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
            var k = y * patch.Width + x;
            if (patch.Ok[k] == 0 || back[k] == f.PixelPacked[i]) continue;
            if (fogN is not null && fogN[k] != 0 && back[k] >= 0 && patch.BackFog[k] == patch.Fogged(k, f.PixelPacked[i])) continue;
            compared++;
        }
        if (compared == 0) return (0, 0, SpriteTint.None, 0, 0, 0);
        var allowed = needed > 0 ? (int)((1 - needed) * compared) + 1 : int.MaxValue;
        var freeze = AllowFreeze;
        int plain = 0, plainFog = 0, plainMiss = 0, plainCloud = 0, frozen = 0, frozenFog = 0, frozenMiss = 0, frozenCloud = 0;
        long plainAlpha = 0, frozenAlpha = 0;
        for (var i = 0; i < n; i++)
        {
            int x = lx + f.PixelX[i], y = ty + f.PixelY[i];
            if ((uint)x >= (uint)patch.Width || (uint)y >= (uint)patch.Height) continue;
            var k = y * patch.Width + x;
            if (patch.Ok[k] == 0 || back[k] == f.PixelPacked[i]) continue;
            var clouded = fogN is not null && fogN[k] != 0;
            if (clouded && back[k] >= 0 && patch.BackFog[k] == patch.Fogged(k, f.PixelPacked[i])) continue;
            int sb = patch.B[k], sg = patch.G[k], sr = patch.R[k];
            int tb = f.PixelB[i], tg = f.PixelG[i], tr = f.PixelR[i];
            if (sb == tb && sg == tg && sr == tr) plain++;
            else if (clouded && patch.Packed[k] == patch.Fogged(k, f.PixelPacked[i])) { plain++; plainCloud++; }
            else if (!clouded && Fog(sb, sg, sr, tb, tg, tr) is var a && a > 0 && Coherent(f, f.PixelX[i], f.PixelY[i], x, y, a, false)) { plain++; plainFog++; plainAlpha += a; }
            else plainMiss++;
            if (freeze)
            {
                int fb = (tb * FrozenB + 127) / 255, fg = (tg * FrozenG + 127) / 255, fr = (tr * FrozenR + 127) / 255;
                if (sb == fb && sg == fg && sr == fr) frozen++;
                else if (clouded && patch.Packed[k] == patch.Fogged(k, f.PixelFrozen[i])) { frozen++; frozenCloud++; }
                else if (!clouded && Fog(sb, sg, sr, fb, fg, fr) is var b && b > 0 && Coherent(f, f.PixelX[i], f.PixelY[i], x, y, b, true)) { frozen++; frozenFog++; frozenAlpha += b; }
                else frozenMiss++;
            }
            else frozenMiss++;
            if (plainMiss > allowed && frozenMiss > allowed) break;
        }
        return frozen > plain
            ? (frozen, compared, SpriteTint.Frozen, frozenFog == 0 ? 0 : (int)(frozenAlpha / frozenFog), frozenFog, frozenCloud)
            : (plain, compared, SpriteTint.None, plainFog == 0 ? 0 : (int)(plainAlpha / plainFog), plainFog, plainCloud);
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
            var neighbour = sb == tb && sg == tg && sr == tr ? 0
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

    /// <summary>Whether an art pixel can be tested through steam at all: coloured (<see cref="FogMinChroma"/>) and dark enough (<see cref="FogMinHeadroom"/>).</summary>
    public static bool Foggable(int tb, int tg, int tr) =>
        765 - tb - tg - tr >= FogMinHeadroom && Math.Max(tb, Math.Max(tg, tr)) - Math.Min(tb, Math.Min(tg, tr)) >= FogMinChroma;

    /// <summary>
    /// Whether screen pixel s fits art pixel t seen through a light translucent layer (see <see cref="MinFog"/>): the
    /// layer's alpha in 1/256 when it does, else 0.
    /// </summary>
    public static int Fog(int sb, int sg, int sr, int tb, int tg, int tr)
    {
        int db = sb - tb, dg = sg - tg, dr = sr - tr;
        if (db < -1 || dg < -1 || dr < -1) return 0;
        int hb = 255 - tb, hg = 255 - tg, hr = 255 - tr, head = hb + hg + hr;
        if (head < FogMinHeadroom || Math.Max(tb, Math.Max(tg, tr)) - Math.Min(tb, Math.Min(tg, tr)) < FogMinChroma) return 0;
        if (Math.Max(sb, Math.Max(sg, sr)) - Math.Min(sb, Math.Min(sg, sr)) < FogMinScreenChroma) return 0;
        var a = (db + dg + dr) * 256 / head;
        if (a < (int)(MinFog * 256) || a > (int)(MaxFog * 256)) return 0;
        if (Math.Abs(db - (hb * a >> 8)) > FogSlack || Math.Abs(dg - (hg * a >> 8)) > FogSlack || Math.Abs(dr - (hr * a >> 8)) > FogSlack) return 0;
        return a;
    }
}
