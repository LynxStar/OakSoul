using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The direct search for mobs along footholds. Foreground differencing finds a mob only where the mosaic vouches for the
/// background behind it, and over parallax sky, clouds or a waterfall it does not (Deep Valley I's top platform: 60 to 99%
/// of the 60 rows above its footholds unusable). But on an exact frame a sprite's opaque pixels equal the screen whatever is
/// behind them, and a walking mob's feet are on its foothold (matched mobs stood a median 0.2 units off it), so its
/// placement is one integer x per foothold column: y follows from the line. For every column of a foothold stretch, every
/// frame (all poses, both facings) of every species that can stand there is probed on a few spread opaque pixels, and the
/// placements that agree go on to the matcher's full strict score.
/// <para>
/// Two uses: <see cref="Gaps"/>, the stretches of visible footholds whose band above the mosaic cannot vouch for (computed
/// once per map), searched every frame for mobs the differencing cannot see; and the stretch under an unmatched candidate's
/// blob, when no frame fits its box (a box cut by the own character's mask or by an effect drawn over the mob).
/// </para>
/// </summary>
public sealed class FootholdSearch
{
    /// <summary>Rows above a foothold whose usability decides a gap: the height of a stump's body (51 to 54 rows).</summary>
    public const int BandRows = 60;

    /// <summary>A foothold column is in a gap when at least this share of its band is not usable for foreground.</summary>
    public const double GapShare = 0.5;

    /// <summary>Gaps shorter than this many columns are left out; holes between gap columns up to <see cref="GapBridge"/> are bridged.</summary>
    public const int MinGap = 8, GapBridge = 6;

    /// <summary>
    /// The probe: <see cref="ProbePixels"/> spread opaque pixels, at least <see cref="ProbeAgree"/> of those on screen and
    /// unmasked equal to the screen as drawn or frozen (at least <see cref="ProbeCompared"/> compared). A whole pixel equal
    /// by chance is rare; a placement off by a pixel still agrees on flat colours and is refined by the full score's window.
    /// </summary>
    public const int ProbePixels = 16, ProbeCompared = 5;

    /// <summary>Probe pixels that may disagree: occlusion by a rope, a tag, an effect.</summary>
    public const int ProbeMisses = 2;

    /// <summary>
    /// Probe pixels that may disagree under a mob known to be hit (its tag's level badge or its HP bar): the skill that hit it is
    /// drawn over it. The frozen Wild Boar of fx-combat-exact's video frame 3 under Cold Beam's shards agrees on 0.53 of its
    /// pixels (hit1/0 R), but its probe missed 3 of 8; on frame 5 2 of 8. At least <see cref="ProbeCompared"/> -
    /// <see cref="ProbeMisses"/> probe pixels must still agree.
    /// </summary>
    public const int HitProbeMisses = 4;

    /// <summary>
    /// Share of a found frame's pixels that must be comparable: lower than the matcher's 0.4, since a mob the own character
    /// stands in front of is masked by the character's box (the dying Wild Boar on frame 781, 61% of it behind LynxHope).
    /// </summary>
    public const double MinComparable = 0.25;

    /// <summary>
    /// Full strict score a found placement needs. A search over thousands of placements meets more chance agreement than
    /// a blob's box does, so this sits above the matcher's 0.2: see the entity report's score distribution of the finds.
    /// </summary>
    public const double Threshold = 0.4;

    /// <summary>Full-score window around a probed placement: the probe placed the origin on the line, the real feet are within a pixel.</summary>
    public const int RefineX = 1, RefineY = 1;

    private readonly MapMobSet _mobs;
    private readonly FootholdIndex _index;
    private readonly List<(Foothold Foothold, int? Floor, List<(int X0, int X1)> Gaps)> _gaps = [];

    /// <summary>How often each colour occurs in the map's mosaic (every fourth pixel of every fourth row), to pick anchors the background rarely shows.</summary>
    private readonly Dictionary<int, int> _background = [];

    /// <summary>
    /// The map's clouds placed in this frame, or null (set per frame by the detector): under a cloud a screen pixel is looked
    /// up as each art colour the clouds blend into it, and a probe pixel agrees as the clouds' blend of the art.
    /// </summary>
    public CloudLayer? Clouds { get; set; }

    public FootholdSearch(BackgroundMosaic mosaic, MapData map, FootholdIndex index, MapMobSet mobs)
    {
        _mobs = mobs;
        _index = index;
        for (var y = 0; y < mosaic.Height; y += 4)
            for (var x = 0; x < mosaic.Width; x += 4)
            {
                var px = mosaic.Pixels[y * mosaic.Width + x];
                if (px >> 24 == 0) continue;
                var c = (int)(px & 0xFFFFFF);
                _background[c] = _background.GetValueOrDefault(c) + 1;
            }
        foreach (var fh in map.Footholds)
        {
            if (fh.IsWall) continue;
            int x0 = Math.Min(fh.X1, fh.X2), x1 = Math.Max(fh.X1, fh.X2);
            var runs = new List<(int X0, int X1)>();
            int start = int.MinValue, lastGap = int.MinValue;
            for (var x = x0; x <= x1; x++)
            {
                if (fh.YAt(x) is not { } yw) continue;
                int unusable = 0, n = 0;
                for (var dy = 1; dy <= BandRows; dy++)
                {
                    int mx = x - mosaic.OriginX, my = (int)Math.Round(yw) - dy - mosaic.OriginY;
                    n++;
                    if ((uint)mx >= (uint)mosaic.Width || (uint)my >= (uint)mosaic.Height || mosaic.ForegroundUsable[my * mosaic.Width + mx] == 0) unusable++;
                }
                if (unusable < GapShare * n) continue;
                if (start == int.MinValue || x - lastGap > GapBridge + 1)
                {
                    if (start != int.MinValue && lastGap - start + 1 >= MinGap) runs.Add((start, lastGap));
                    start = x;
                }
                lastGap = x;
            }
            if (start != int.MinValue && lastGap - start + 1 >= MinGap) runs.Add((start, lastGap));
            if (runs.Count > 0) _gaps.Add((fh, index.FloorOf(fh.Id), runs));
        }
    }

    /// <summary>World columns of gaps, per foothold, for the report.</summary>
    public int GapColumns => _gaps.Sum(g => g.Gaps.Sum(r => r.X1 - r.X0 + 1));

    /// <summary>The gap stretches visible in a frame, as screen columns on their footholds.</summary>
    public IEnumerable<(Foothold Foothold, int? Floor, int Sx0, int Sx1)> Gaps(int camX, int camY, CaptureRect viewport)
    {
        foreach (var (fh, floor, runs) in _gaps)
        {
            int ya = fh.Y1 - camY, yb = fh.Y2 - camY;
            if (Math.Max(ya, yb) < viewport.Y || Math.Min(ya, yb) - BandRows > viewport.Y + viewport.Height) continue;
            foreach (var (x0, x1) in runs)
            {
                int sx0 = Math.Max(viewport.X, x0 - camX), sx1 = Math.Min(viewport.X + viewport.Width - 1, x1 - camX);
                if (sx1 >= sx0) yield return (fh, floor, sx0, sx1);
            }
        }
    }

    /// <summary>The species that can stand on a floor: those with a spawn on it (any mobility), then the jumpers; never flyers.</summary>
    public List<MobSpecies> SpeciesFor(int? floor, double worldY) =>
        _mobs.Order(floor, worldY, null, null).Where(e => e.Species.Mobility != MobMobility.Fly && (floor is null || e.Floors.Contains(floor.Value) || e.Species.Mobility == MobMobility.Jump))
            .Select(e => e.Species).ToList();

    /// <summary>Placements probed since construction, and those that went on to the full score.</summary>
    public long Probed { get; private set; }
    public long Scored { get; private set; }

    /// <summary>
    /// Probes every column from <paramref name="sx0"/> to <paramref name="sx1"/> of a foothold, feet on the line, for every frame of the given species, skipping columns inside <paramref name="skip"/> (screen x
    /// ranges of mobs already matched and of the own character), and adds the passing placements.
    /// </summary>
    public void Probe(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, Foothold fh, int camX, int camY, int sx0, int sx1,
        IReadOnlyList<MobSpecies> species, IReadOnlyList<(int X0, int X1)> skip, List<(MobSpecies Species, SpriteFrame Frame, int FeetX, int FeetY)> hits,
        int misses = ProbeMisses)
    {
        var vp = mask.Viewport;
        var bits = mask.Bits;
        int mw = mask.Width, mh = mask.Height, frameHeight = frame.Length / 4 / frameWidth;
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(frame);
        // The columns to try and their feet rows: feet on the line (on a slope the rounding may be a row off, which the
        // full score's window covers).
        var n = sx1 - sx0 + 1;
        if (n <= 0) return;
        var feet = new int[n];
        for (var c = 0; c < n; c++)
        {
            var sx = sx0 + c;
            feet[c] = int.MinValue;
            var skipped = false;
            foreach (var (a, b) in skip) if (sx >= a && sx <= b) { skipped = true; break; }
            if (!skipped && fh.YAt(sx + camX) is { } yw) feet[c] = (int)Math.Round(yw - camY);
        }
        // Placements come from anchors: every column's pixels a few rows above its feet are looked up by colour among the
        // frames' pixels on those rows, each hit naming a frame and a column whose placement is then probed.
        var index = IndexOf(species);
        // Placements already probed in this call, by frame and column: a generation stamp, so the buffer is not cleared.
        if (_tried.Length < index.Frames.Count * n) _tried = new int[index.Frames.Count * n * 2];
        var generation = ++_generation;
        var tried = _tried;
        var clouds = Clouds;
        Span<int> unfogged = stackalloc int[1 + UnfoggedColours];
        // An anchor pixel at column qx belongs to a placement up to the frames' reach away, whose feet row is that placement's own:
        // on a flat foothold one row for every column, on a slope every row the feet take within the reach, each hit checked
        // against its placement's feet (before, the row of the anchor's own column was used, which missed mobs on slopes).
        var flat = fh.Y1 == fh.Y2;
        int anyFeet = int.MinValue;
        for (var c = 0; c < n && anyFeet == int.MinValue; c++) anyFeet = feet[c];
        if (anyFeet == int.MinValue) return;
        // The line's row at every column, skipped or not: along one foothold it changes monotonically, so the rows within the reach
        // of a column are those between the line's rows at the reach's two ends.
        var line = flat ? null : new int[n];
        if (line is not null)
            for (var c = 0; c < n; c++) line[c] = fh.YAt(sx0 + c + camX) is { } yl ? (int)Math.Round(yl - camY) : anyFeet;
        for (var qx = Math.Max(0, sx0 - index.Reach); qx <= Math.Min(frameWidth - 1, sx1 + index.Reach); qx++)
        {
            int rowLo = anyFeet, rowHi = anyFeet;
            if (line is not null)
            {
                int ca = Math.Clamp(qx - index.MaxDx - sx0, 0, n - 1), cb = Math.Clamp(qx - index.MinDx - sx0, 0, n - 1);
                rowLo = Math.Min(line[ca], line[cb]);
                rowHi = Math.Max(line[ca], line[cb]);
            }
            for (var fy = rowLo; fy <= rowHi; fy++)
            foreach (var k in AnchorRows)
            {
                var qy = fy + k;
                if ((uint)qy >= (uint)frameHeight) continue;
                var screenColour = pixels[qy * frameWidth + qx] & 0xFFFFFF;
                // Under clouds the anchor is each art colour they blend into this screen colour.
                var lookups = 1;
                unfogged[0] = screenColour;
                if (clouds is not null && clouds.MayCover(qx, qy)) lookups += clouds.Unfog(screenColour, qx, qy, unfogged[1..]);
                for (var u = 0; u < lookups; u++)
                {
                if (!index.ByColour.TryGetValue(unfogged[u], out var entries)) continue;
                foreach (var (fi, dx) in entries)
                {
                    var c = qx - dx - sx0;
                    if ((uint)c >= (uint)n || feet[c] != fy || tried[fi * n + c] == generation) continue;
                    tried[fi * n + c] = generation;
                    var (sp, f) = index.Frames[fi];
                    var p = ProbeOf(f);
                    if (p.Count < ProbeCompared) continue;
                    Probed++;
                    // Spread pixels until eight are compared (masked and off-screen ones skipped), at most two disagreeing.
                    int ox = sx0 + c, oy = feet[c], compared = 0, missed = 0;
                    for (var i = 0; i < p.Count && compared < 8; i++)
                    {
                        int x = ox + p.Dx[i], y = oy + p.Dy[i];
                        if ((uint)x >= (uint)frameWidth || (uint)y >= (uint)frameHeight) continue;
                        int vx = x - vp.X, vy = y - vp.Y;
                        if ((uint)vx >= (uint)mw || (uint)vy >= (uint)mh || bits[vy * mw + vx] != 0) continue;
                        compared++;
                        var s = pixels[y * frameWidth + x] & 0xFFFFFF;
                        if (s == p.Colour[i] || s == p.Frozen[i]) continue;
                        if (clouds is not null && clouds.MayCover(x, y) && s == clouds.CompositeAt(p.Colour[i], x, y)) continue;
                        if (++missed > misses || (compared == 2 && missed == 2 && misses == ProbeMisses)) break;
                    }
                    if (compared >= ProbeCompared && missed <= misses && compared - missed >= ProbeCompared - ProbeMisses) hits.Add((sp, f, ox, oy));
                }
                }
            }
        }
    }

    /// <summary>
    /// Rows above the feet whose pixels anchor the search: every frame of every mob seen is at least 27 rows tall, so each
    /// has opaque pixels on these rows, and a mob is found when any of them shows.
    /// </summary>
    public static readonly int[] AnchorRows = [-6, -14, -22];

    /// <summary>
    /// Art colours an anchor under clouds is looked up as, at most: under thick cloud several art levels per channel blend to
    /// the screen's (the Wild Boar of keyframe 496 under two clouds, a tenth to a third of it showing through).
    /// </summary>
    public const int UnfoggedColours = 16;

    /// <summary>
    /// Grey colours (channels within this many levels: outline black, the stumps' and axes' greys) anchor nothing: every
    /// frame has them and so does the background. A frame's coloured pixels on the anchor rows name it.
    /// </summary>
    public const int MinAnchorChroma = 17;

    /// <summary>Anchor pixels per frame: a mob is found when any of them shows (the rest of the probe then checks it).</summary>
    public const int AnchorsPerFrame = 12;

    /// <summary>Anchors of one frame are at least this many columns apart, so a rope or a tag in front of the mob hides only some.</summary>
    public const int AnchorSpread = 4;

    private int[] _tried = [];
    private int _generation;

    private sealed record AnchorIndex(List<(MobSpecies Species, SpriteFrame Frame)> Frames, Dictionary<int, List<(int Frame, int Dx)>> ByColour, int Reach)
    {
        /// <summary>The anchors' column offsets from their origins, least and most: a placement is this far from its anchor's column.</summary>
        public int MinDx { get; init; }
        public int MaxDx { get; init; }
    }

    private readonly Dictionary<string, AnchorIndex> _anchors = [];

    /// <summary>The anchor index of a set of species: per colour (as drawn and frozen), the frames and column offsets that show it on an anchor row.</summary>
    private AnchorIndex IndexOf(IReadOnlyList<MobSpecies> species)
    {
        var key = string.Join(",", species.Select(s => s.Id));
        if (_anchors.TryGetValue(key, out var index)) return index;
        var frames = species.SelectMany(sp => sp.Frames.Select(f => (sp, f))).ToList();
        var byColour = new Dictionary<int, List<(int, int)>>();
        var reach = 0;
        for (var fi = 0; fi < frames.Count; fi++)
        {
            var f = frames[fi].f;
            reach = Math.Max(reach, Math.Max(f.OriginX, f.Width - f.OriginX));
            // The frame's coloured pixels on the anchor rows, those whose colour the background shows least first, a few
            // columns apart.
            var pool = new List<(int Dx, int Colour, int Frozen, int Count)>();
            for (var i = 0; i < f.PixelX.Length; i++)
            {
                if (Array.IndexOf(AnchorRows, f.PixelY[i] - f.OriginY) < 0) continue;
                var colour = f.PixelPacked[i];
                int cb = colour & 0xFF, cg = colour >> 8 & 0xFF, cr = colour >> 16 & 0xFF;
                if (Math.Max(cb, Math.Max(cg, cr)) - Math.Min(cb, Math.Min(cg, cr)) < MinAnchorChroma) continue;
                pool.Add((f.PixelX[i] - f.OriginX, colour, f.PixelFrozen[i], _background.GetValueOrDefault(colour)));
            }
            var chosen = new List<(int Dx, int Colour, int Frozen, int Count)>();
            foreach (var a in pool.OrderBy(a => a.Count).ThenBy(a => a.Dx))
            {
                if (chosen.Count >= AnchorsPerFrame) break;
                if (chosen.Any(c => Math.Abs(c.Dx - a.Dx) < AnchorSpread)) continue;
                chosen.Add(a);
            }
            foreach (var a in chosen)
                foreach (var colour in new[] { a.Colour, a.Frozen })
                {
                    if (!byColour.TryGetValue(colour, out var list)) byColour[colour] = list = [];
                    if (!list.Contains((fi, a.Dx))) list.Add((fi, a.Dx));
                }
        }
        var dxs = byColour.Values.SelectMany(l => l.Select(e => e.Item2)).DefaultIfEmpty(0).ToList();
        index = new AnchorIndex(frames, byColour, reach) { MinDx = dxs.Min(), MaxDx = dxs.Max() };
        _anchors[key] = index;
        return index;
    }

    private sealed record ProbeData(int Count, int[] Dx, int[] Dy, int[] Colour, int[] Frozen);

    private readonly Dictionary<SpriteFrame, ProbeData> _probes = new(ReferenceEqualityComparer.Instance);

    /// <summary>A frame's first <see cref="ProbePixels"/> spread opaque pixels as offsets from its origin, with their colours as drawn and frozen.</summary>
    private ProbeData ProbeOf(SpriteFrame f)
    {
        if (_probes.TryGetValue(f, out var p)) return p;
        var n = Math.Min(ProbePixels, f.PixelX.Length);
        p = new ProbeData(n, Enumerable.Range(0, n).Select(i => f.PixelX[i] - f.OriginX).ToArray(), Enumerable.Range(0, n).Select(i => f.PixelY[i] - f.OriginY).ToArray(),
            f.PixelPacked[..n], f.PixelFrozen[..n]);
        _probes[f] = p;
        return p;
    }

    /// <summary>
    /// Scores the probed placements in full on the matcher's prepared patch (which must cover them), keeps those reaching
    /// <paramref name="threshold"/>, and of those standing in one place (feet within <see cref="EntityDetector.SameMobX"/>) the best.
    /// </summary>
    public List<SpriteMatch> Score(SpriteMatcher matcher, List<(MobSpecies Species, SpriteFrame Frame, int FeetX, int FeetY)> hits, double threshold,
        ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask)
    {
        var found = new List<SpriteMatch>();
        // Hits close together share one prepared patch around them.
        var sorted = hits.OrderBy(h => h.FeetX).ToList();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].FeetX - sorted[j].FeetX <= ClusterGap) j++;
            int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
            for (var k = i; k <= j; k++)
            {
                var (_, f, x, y) = sorted[k];
                x0 = Math.Min(x0, x - f.OriginX); x1 = Math.Max(x1, x - f.OriginX + f.Width);
                y0 = Math.Min(y0, y - f.OriginY); y1 = Math.Max(y1, y - f.OriginY + f.Height);
            }
            matcher.PrepareRegion(frame, frameWidth, mask, x0 - RefineX - 8, y0 - RefineY - 8, x1 + RefineX + 8, y1 + RefineY + 8);
            for (var k = i; k <= j; k++)
            {
                var (sp, f, x, y) = sorted[k];
                Scored++;
                if (matcher.Best(new CaptureRect(x, y, 1, 1), new PointD(x, y), sp, f, threshold, RefineX, RefineY, minComparable: MinComparable) is { } m && m.Score >= threshold) found.Add(m);
            }
            i = j + 1;
        }
        var kept = new List<SpriteMatch>();
        foreach (var m in found.OrderByDescending(m => m.Score).ThenByDescending(m => m.Compared))
            if (!kept.Any(k => EntityDetector.SameMob(k, m))) kept.Add(m);
        return kept;
    }

    /// <summary>Hits at most this many columns apart are scored on one prepared patch.</summary>
    public const int ClusterGap = 40;

    /// <summary>The overlap of two matches' visible extents as a share of the larger.</summary>
    public static double OverlapOfLarger(in SpriteMatch a, in SpriteMatch b)
    {
        int ax0 = a.Left + a.Frame.X0, ax1 = a.Left + a.Frame.X1, ay0 = a.Top + a.Frame.Y0, ay1 = a.Top + a.Frame.Y1;
        int bx0 = b.Left + b.Frame.X0, bx1 = b.Left + b.Frame.X1, by0 = b.Top + b.Frame.Y0, by1 = b.Top + b.Frame.Y1;
        int w = Math.Min(ax1, bx1) - Math.Max(ax0, bx0) + 1, h = Math.Min(ay1, by1) - Math.Max(ay0, by0) + 1;
        if (w <= 0 || h <= 0) return 0;
        return (double)w * h / Math.Max(a.Frame.ExtentWidth * a.Frame.ExtentHeight, b.Frame.ExtentWidth * b.Frame.ExtentHeight);
    }

    /// <summary>The non-wall footholds crossing screen columns <paramref name="sx0"/> to <paramref name="sx1"/> with their surface between screen rows <paramref name="syTop"/> and <paramref name="syBottom"/> somewhere in that span.</summary>
    public IEnumerable<Foothold> Under(MapData map, int camX, int camY, int sx0, int sx1, int syTop, int syBottom)
    {
        foreach (var fh in map.Footholds)
        {
            if (fh.IsWall) continue;
            int fx0 = Math.Min(fh.X1, fh.X2) - camX, fx1 = Math.Max(fh.X1, fh.X2) - camX;
            if (fx1 < sx0 || fx0 > sx1) continue;
            int ya = fh.Y1 - camY, yb = fh.Y2 - camY;
            if (Math.Max(ya, yb) < syTop || Math.Min(ya, yb) > syBottom) continue;
            yield return fh;
        }
    }

    public FootholdIndex Index => _index;
}
