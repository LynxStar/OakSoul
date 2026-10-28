using System.Text.Json;
using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>Whose damage a number shows: dealt by the own character (NoRed, NoCri), taken by it (NoViolet), or another set.</summary>
public enum DamageDirection : byte { Dealt, Taken, Other }

/// <summary>One digit of a damage number as placed on screen.</summary>
/// <param name="First">Drawn from the set's larger first-digit glyphs (NoRed1, NoViolet1, ...).</param>
/// <param name="Alpha">The opacity it was drawn with (1 until the number fades out).</param>
public sealed record DamageDigit(int Digit, bool First, int Left, int Top, int Width, int Height, int OriginX, int OriginY, double Score, double Alpha);

/// <summary>
/// A damage number read from the client's digit sprites: the set family (NoRed, NoViolet, NoCri, ...), its direction,
/// the digits left to right and their value, the box around them, the mean digit score and opacity. <see cref="Complete"/>
/// is false when the leftmost digit found is not a first-digit glyph (the number's start is hidden, so the value may lack
/// leading digits); a digit between two read ones that was not read shows as "?" in <see cref="Digits"/>, and the number
/// then has no <see cref="Value"/>. Trailing digits that were not read leave no trace: a short number may be the start of a
/// longer one.
/// </summary>
public sealed record DamageMatch(string Family, DamageDirection Direction, bool Critical, string Digits, CaptureRect Box, double Score, double Alpha,
    IReadOnlyList<DamageDigit> Glyphs)
{
    public bool Complete => Glyphs.Count > 0 && Glyphs[0].First;
    /// <summary>The value, when every digit was read ("2?7", a digit between not read, has none).</summary>
    public long? Value => long.TryParse(Digits, out var v) ? v : null;
    public string DirectionText => Direction switch { DamageDirection.Dealt => "dealt", DamageDirection.Taken => "taken", _ => "other" };
    public override string ToString() => $"{Family} {Digits}{(Complete ? "" : "?")} {DirectionText}{(Critical ? " critical" : "")} {Score:0.00} a {Alpha:0.00}";
}

/// <summary>
/// Reads damage numbers. The client draws them from the digit sprites of Effect.wz BasicEff.img (exported by
/// <c>OakSoul.SpriteExport --effects</c> to <c>data/sprites/effect</c>): sets NoRed (the damage the own character deals),
/// NoCri (its critical hits, with a bubble), NoViolet (damage it takes), and NoBlue, NoGreen, NoRed2/3 and NoCri2/3 (not
/// seen on the fixtures); in each family the set ending in 1 (or 3) holds the larger first digit and the one ending in 0
/// (or 2) the rest. Every digit has a white outer and a black inner outline around a gradient fill.
/// <para>
/// Two things keep a plain pixel comparison from working. The client draws the digits with every level of 136 and up one
/// higher than the 4-bit art (art 136, 153, ..., 238 show as 137, 154, ..., 239 on fx-combat-exact's frame 781; lower levels
/// and 255 as stored): a multiply by 256/255, rounded, which <see cref="Drawn"/> applies to the art. And a number fades out
/// at the end of its rise, blended over whatever is behind it with one opacity for the whole number (the purple number on
/// frame 781 at 0.79). So a glyph is scored on pairs of neighbouring pixels whose art colours differ: the background behind
/// two neighbours is nearly the same, so the screen difference of the pair is the art difference times the opacity, per
/// channel. The opacity is the median ratio over the pairs, and a pair agrees within <see cref="PairSlack"/> levels. A drawn
/// digit agrees on every pair it shows; digits overlap their left neighbour, so a covered one scores the share that shows.
/// </para>
/// </summary>
public sealed class DamageReader
{
    /// <summary>Largest per-channel error of a pair: (s_i - s_j) - a (t_i - t_j), with the background a few levels apart under a fade.</summary>
    public const int PairSlack = 10;

    /// <summary>Share of a glyph's pairs that must agree. On frame 781 the six digits scored 0.58 to 1.00 (the covered ones lowest); chance placements stay under 0.4.</summary>
    public const double GlyphThreshold = 0.5;

    /// <summary>Per-channel slack of the probe pairs, whose opacity is only estimated from the outlines' luma.</summary>
    public const int ProbeSlack = 18;

    /// <summary>Share of a glyph's probe pairs that must agree before all its pairs are scored.</summary>
    public const double ProbeThreshold = 0.35;

    /// <summary>Opacity range accepted: a number fades from 1 to 0; under 0.25 its pairs are within the slack of anything.</summary>
    public const double MinAlpha = 0.25, MaxAlpha = 1.06;

    /// <summary>Digits of one number: origins on one baseline within this many pixels, successive origins this far apart (the client's advances are 20 to 24).</summary>
    public const int BaselineSlack = 4, MinAdvance = 12, MaxAdvance = 34;

    /// <summary>A digit up to this much further than <see cref="MaxAdvance"/> still belongs to the number, with an unread one between ("2?7").</summary>
    public const int MaxGapAdvance = 26;

    /// <summary>
    /// Outline pixels (light grey touching dark) a seed needs within <see cref="OutlineReach"/>: a digit's white and black
    /// rings give dozens, a sprite's eye highlight or the edge of a name tag a few.
    /// </summary>
    public const int MinOutlineNear = 10;

    /// <summary>Fill-coloured samples next to a dark pixel a region needs in the quick look.</summary>
    public const int MinFillPairs = 8;

    /// <summary>Light-next-to-dark samples a region needs in the quick look before it is searched (a digit's outlines give dozens).</summary>
    public const int MinOutlinePairs = 6;

    /// <summary>Seeds lie within this many pixels of a white outline pixel touching a dark one (a fill pixel is at most 7 from its outline).</summary>
    public const int OutlineReach = 8;

    /// <summary>A family is searched in a region only when it holds at least this many pixels of the family's fill hues near outlines.</summary>
    public const int MinHuePixels = 80;

    private sealed class Glyph
    {
        public required int Digit;
        public required bool First;
        public required int W, H, OriginX, OriginY;
        /// <summary>As drawn (<see cref="Drawn"/>), b, g, r per pixel.</summary>
        public required int[] Art;
        public required int[] PairA, PairB;
        /// <summary>Fill pixels (x, y) used to seed and probe placements, and their hues (degrees) as drawn.</summary>
        public required int[] FillX, FillY;
        public required double[] FillHue;
        /// <summary>A spread subset of the pairs for the first test.</summary>
        public required int[] ProbePairs;
        /// <summary>Spread pixels of the white outer outline and of the black inner one: the cheapest test of a placement.</summary>
        public required int[] WhiteX, WhiteY, BlackX, BlackY;
    }

    private sealed class Family
    {
        public required string Name;
        public required DamageDirection Direction;
        public required bool Critical;
        public readonly List<Glyph> Glyphs = [];
        /// <summary>Per quantized colour (4 bits a channel, r g b): whether it can be one of the family's fills, faded or not.</summary>
        public readonly bool[] Hue = new bool[4096];
    }

    private readonly List<Family> _families = [];

    /// <summary>The colours any family's fill can be (the union of the families' hue tables), for the quick look.</summary>
    private readonly bool[] _anyHue = new bool[4096];

    public int GlyphCount => _families.Sum(f => f.Glyphs.Count);
    public IEnumerable<string> FamilyNames => _families.Select(f => f.Name);

    /// <summary>Timing and counts since construction, for the report.</summary>
    public double Milliseconds { get; private set; }
    public long PlacementsScored { get; private set; }
    public long RegionsTried { get; private set; }
    public long RegionsSearched { get; private set; }
    public long RegionPixels { get; private set; }

    /// <summary>The digit families: name, first-digit set, other-digit set, direction, critical.</summary>
    private static readonly (string Name, string First, string Others, DamageDirection Direction, bool Critical)[] Families =
    [
        ("NoRed", "NoRed1", "NoRed0", DamageDirection.Dealt, false),
        ("NoCri", "NoCri1", "NoCri0", DamageDirection.Dealt, true),
        ("NoViolet", "NoViolet1", "NoViolet0", DamageDirection.Taken, false),
    ];

    /// <summary>
    /// The other digit sets of BasicEff.img (NoBlue, NoGreen, NoRed2/3, NoCri2/3, NoKite, NoProduction) are exported but not
    /// searched: none showed on the fixtures, what they mean in this client is not known, and NoBlue's cyan fill matches the
    /// Ice skill's shards. Add them here with a direction once a recording shows one.
    /// </summary>
    public static readonly string[] Unsearched = ["NoBlue", "NoGreen", "NoRed2", "NoCri2", "NoKite", "NoProduction"];

    /// <summary>A 4-bit art level as the client draws a damage digit: times 256/255, rounded (136 shows as 137, 255 stays).</summary>
    public static int Drawn(int v) => Math.Min(255, (v * 256 + 127) / 255);

    /// <summary>Loads the digit sets from <c>effect/damage-digits.json</c> under <paramref name="spritesDir"/>, or null when they were not exported.</summary>
    public static DamageReader? Load(string spritesDir)
    {
        var index = Path.Combine(spritesDir, "effect", "damage-digits.json");
        if (!File.Exists(index)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(index));
        var sets = doc.RootElement.GetProperty("sets");
        var reader = new DamageReader();
        foreach (var (name, firstSet, restSet, direction, critical) in Families)
        {
            if (!sets.TryGetProperty(firstSet, out var first) || !sets.TryGetProperty(restSet, out var rest)) continue;
            var family = new Family { Name = name, Direction = direction, Critical = critical };
            foreach (var (set, isFirst) in new[] { (first, true), (rest, false) })
                for (var d = 0; d <= 9; d++)
                {
                    if (!set.TryGetProperty(d.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry)) continue;
                    var file = Path.Combine(spritesDir, entry.GetProperty("file").GetString() ?? "");
                    if (!File.Exists(file)) continue;
                    var (bgra, w, h) = Png.ReadBgra(file);
                    family.Glyphs.Add(MakeGlyph(d, isFirst, bgra, w, h, entry.GetProperty("originX").GetInt32(), entry.GetProperty("originY").GetInt32()));
                }
            if (family.Glyphs.Count == 0) continue;
            BuildHue(family);
            for (var q = 0; q < 4096; q++) reader._anyHue[q] |= family.Hue[q];
            reader._families.Add(family);
        }
        return reader._families.Count == 0 ? null : reader;
    }

    private static Glyph MakeGlyph(int digit, bool first, byte[] bgra, int w, int h, int ox, int oy)
    {
        var art = new int[w * h * 3];
        var opaque = new bool[w * h];
        for (var i = 0; i < w * h; i++)
        {
            opaque[i] = bgra[i * 4 + 3] == 255;
            for (var c = 0; c < 3; c++) art[i * 3 + c] = Drawn(bgra[i * 4 + c]);
        }
        var pa = new List<int>();
        var pb = new List<int>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!opaque[i]) continue;
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx >= w || yy >= h) continue;
                    var j = yy * w + xx;
                    if (!opaque[j]) continue;
                    var dt = Math.Abs(art[i * 3] - art[j * 3]) + Math.Abs(art[i * 3 + 1] - art[j * 3 + 1]) + Math.Abs(art[i * 3 + 2] - art[j * 3 + 2]);
                    if (dt < 30) continue;
                    pa.Add(i);
                    pb.Add(j);
                }
            }
        // Fill pixels: coloured (chroma at least 68) and opaque, spread by a stride.
        var fills = new List<(int X, int Y)>();
        for (var i = 0; i < w * h; i++)
            if (opaque[i] && Chroma(art[i * 3 + 2], art[i * 3 + 1], art[i * 3]) >= 68) fills.Add((i % w, i / w));
        var stride = Math.Max(1, fills.Count / 7);
        var spread = fills.Where((_, k) => k % stride == stride / 2).Take(6).ToList();
        var probe = new List<int>();
        var pstride = Math.Max(1, pa.Count / 24);
        for (var k = pstride / 2; k < pa.Count && probe.Count < 24; k += pstride) probe.Add(k);
        List<(int X, int Y)> Spread(Func<int, bool> pick)
        {
            var all = new List<(int X, int Y)>();
            for (var i = 0; i < w * h; i++) if (opaque[i] && pick(i)) all.Add((i % w, i / w));
            var st = Math.Max(1, all.Count / 7);
            return all.Where((_, k) => k % st == st / 2).Take(6).ToList();
        }
        var white = Spread(i => art[i * 3] == 255 && art[i * 3 + 1] == 255 && art[i * 3 + 2] == 255);
        var black = Spread(i => art[i * 3] + art[i * 3 + 1] + art[i * 3 + 2] <= 51);
        return new Glyph
        {
            Digit = digit, First = first, W = w, H = h, OriginX = ox, OriginY = oy, Art = art, PairA = pa.ToArray(), PairB = pb.ToArray(),
            WhiteX = white.Select(p => p.X).ToArray(), WhiteY = white.Select(p => p.Y).ToArray(), BlackX = black.Select(p => p.X).ToArray(), BlackY = black.Select(p => p.Y).ToArray(),
            FillX = spread.Select(p => p.X).ToArray(), FillY = spread.Select(p => p.Y).ToArray(), ProbePairs = probe.ToArray(),
            FillHue = spread.Select(p => { var i = p.Y * w + p.X; return HueOf(art[i * 3 + 2], art[i * 3 + 1], art[i * 3]); }).ToArray(),
        };
    }

    private static int Chroma(int r, int g, int b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static double HueOf(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max == min) return 0;
        double d = max - min, h;
        if (max == r) h = (g - b) / d % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>
    /// The quantized colours that can be one of the family's fills as drawn or faded over a background: within 22 degrees
    /// of a fill's hue and keeping at least 30% of its chroma (and 40 levels).
    /// </summary>
    private static void BuildHue(Family f)
    {
        var fills = new HashSet<(int R, int G, int B)>();
        foreach (var g in f.Glyphs)
            for (var i = 0; i < g.W * g.H; i++)
            {
                int b = g.Art[i * 3], gr = g.Art[i * 3 + 1], r = g.Art[i * 3 + 2];
                if (Chroma(r, gr, b) >= 68) fills.Add((r, gr, b));
            }
        var hues = fills.Select(c => (Hue: HueOf(c.R, c.G, c.B), Chroma: Chroma(c.R, c.G, c.B))).ToList();
        for (var q = 0; q < 4096; q++)
        {
            int r = (q >> 8) * 17, g = (q >> 4 & 15) * 17, b = (q & 15) * 17;
            var ch = Chroma(r, g, b);
            if (ch < 60) continue;
            var h = HueOf(r, g, b);
            foreach (var (fh, fc) in hues)
            {
                var dh = Math.Abs(h - fh);
                if (Math.Min(dh, 360 - dh) <= 22 && ch >= 0.45 * fc) { f.Hue[q] = true; break; }
            }
        }
    }

    /// <summary>Hue in degrees of each quantized colour (see <see cref="Quant"/>).</summary>
    private static readonly double[] QuantHue = Enumerable.Range(0, 4096).Select(q => HueOf((q >> 8) * 17, (q >> 4 & 15) * 17, (q & 15) * 17)).ToArray();

    /// <summary>A seed tries an anchor only when its hue is within this many degrees of the anchor's: the fills are vertical gradients (red at the top of a NoRed digit, yellow at the bottom), so the hue says which row a seed is on.</summary>
    public const double SeedHueSlack = 12;

    private static int Quant(int r, int g, int b) => ((r + 8) / 17) << 8 | ((g + 8) / 17) << 4 | (b + 8) / 17;

    /// <summary>
    /// The damage numbers inside a screen region (inclusive pixels, clamped to the viewport): per family whose fill hues
    /// show there, every glyph placement seeded on those pixels is probed and the passing ones scored on all their pairs;
    /// the accepted digits (<see cref="GlyphThreshold"/>), the best of any overlapping ones, are chained into numbers by
    /// baseline and advance.
    /// </summary>
    public List<DamageMatch> Read(ReadOnlySpan<byte> frame, int frameWidth, ScreenMask mask, CaptureRect region)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vp = mask.Viewport;
        int rx0 = Math.Max(vp.X, region.X), ry0 = Math.Max(vp.Y, region.Y);
        int rx1 = Math.Min(vp.X + vp.Width - 1, region.X + region.Width - 1), ry1 = Math.Min(vp.Y + vp.Height - 1, region.Y + region.Height - 1);
        var result = new List<DamageMatch>();
        if (rx1 - rx0 < 8 || ry1 - ry0 < 8) return result;
        int rw = rx1 - rx0 + 1, rh = ry1 - ry0 + 1;
        // A quick look first, every other pixel of every other row: digits need light grey pixels with a dark one two
        // columns away (their white and black outlines), which most regions do not have.
        var pairs = 0;
        for (var y = ry0; y <= ry1 && pairs < MinOutlinePairs; y += 2)
            for (var x = rx0; x + 2 <= rx1 && pairs < MinOutlinePairs; x += 2)
            {
                int o = (y * frameWidth + x) * 4, o2 = o + 8;
                int l1 = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2, l2 = (frame[o2] + 2 * frame[o2 + 1] + frame[o2 + 2]) >> 2;
                if ((l1 >= 190 && l2 <= 70) || (l2 >= 190 && l1 <= 70)) pairs++;
            }
        RegionsTried++;
        // ... and fill colours of some family right next to the black inner outline.
        var fills = 0;
        if (pairs >= MinOutlinePairs)
            for (var y = ry0 + 2; y + 2 <= ry1 && fills < MinFillPairs; y += 2)
                for (var x = rx0 + 2; x + 2 <= rx1 && fills < MinFillPairs; x += 2)
                {
                    var o = (y * frameWidth + x) * 4;
                    if (!_anyHue[Quant(frame[o + 2], frame[o + 1], frame[o])]) continue;
                    int ol = o - 8, or = o + 8, ou = o - frameWidth * 8, od = o + frameWidth * 8;
                    if ((frame[ol] + 2 * frame[ol + 1] + frame[ol + 2]) >> 2 <= 70 || (frame[or] + 2 * frame[or + 1] + frame[or + 2]) >> 2 <= 70
                        || (frame[ou] + 2 * frame[ou + 1] + frame[ou + 2]) >> 2 <= 70 || (frame[od] + 2 * frame[od + 1] + frame[od + 2]) >> 2 <= 70) fills++;
                }
        if (pairs < MinOutlinePairs || fills < MinFillPairs) { Milliseconds += sw.Elapsed.TotalMilliseconds; return result; }
        RegionsSearched++;
        RegionPixels += rw * rh;
        // The region's pixels, packed, and whether each may be compared.
        var px = new int[rw * rh * 3];
        var ok = new bool[rw * rh];
        var quant = new int[rw * rh];
        var luma = new int[rw * rh];
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                int sx = rx0 + x, sy = ry0 + y, o = (sy * frameWidth + sx) * 4, i = y * rw + x;
                px[i * 3] = frame[o]; px[i * 3 + 1] = frame[o + 1]; px[i * 3 + 2] = frame[o + 2];
                int vx = sx - vp.X, vy = sy - vp.Y;
                ok[i] = (uint)vx < (uint)mask.Width && (uint)vy < (uint)mask.Height && mask.Bits[vy * mask.Width + vx] == 0;

                quant[i] = Quant(frame[o + 2], frame[o + 1], frame[o]);
                luma[i] = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2;
            }
        // Digits are outlined white over black: seeds must lie near a light grey pixel touching a dark one. Rock, sky and
        // most sprites have no such pair, which keeps the seeds to the numbers themselves.
        var outline = new int[(rw + 1) * (rh + 1)];
        for (var y = 0; y < rh; y++)
        {
            var run = 0;
            for (var x = 0; x < rw; x++)
            {
                var i = y * rw + x;
                var hit = false;
                if (luma[i] >= 190 && Chroma(px[i * 3 + 2], px[i * 3 + 1], px[i * 3]) <= 40)
                    for (var d = 1; d <= 2 && !hit; d++)
                        hit = (x + d < rw && luma[i + d] <= 70) || (x - d >= 0 && luma[i - d] <= 70) || (y + d < rh && luma[i + d * rw] <= 70) || (y - d >= 0 && luma[i - d * rw] <= 70);
                if (hit) run++;
                outline[(y + 1) * (rw + 1) + x + 1] = outline[y * (rw + 1) + x + 1] + run;
            }
        }
        bool NearOutline(int x, int y)
        {
            int x0 = Math.Max(0, x - OutlineReach), y0 = Math.Max(0, y - OutlineReach), x1 = Math.Min(rw, x + OutlineReach + 1), y1 = Math.Min(rh, y + OutlineReach + 1);
            return outline[y1 * (rw + 1) + x1] - outline[y0 * (rw + 1) + x1] - outline[y1 * (rw + 1) + x0] + outline[y0 * (rw + 1) + x0] >= MinOutlineNear;
        }
        var digits = new List<(Family Family, Glyph Glyph, int Left, int Top, double Score, double Alpha)>();
        var hueMap = new bool[rw * rh];
        // Placements tried per glyph, by top-left, offset by SeenPad so tops and lefts up to half a glyph outside fit.
        const int SeenPad = 32;
        int sw2 = rw + 2 * SeenPad;
        var seen = new bool[sw2 * (rh + 2 * SeenPad)];
        var ratios = new List<double>(64);
        // Seeds per family; families sharing hues (NoRed, NoCri, NoRed2) are searched only when they hold at least half as
        // many seeds as the richest one.
        var familySeeds = new List<(Family Family, List<int> Seeds)>();
        var near = new List<int>();
        for (var i = 0; i < rw * rh; i++)
            if (ok[i] && NearOutline(i % rw, i / rw)) near.Add(i);
        foreach (var family in _families)
        {
            var list = new List<int>();
            foreach (var i in near)
                if (family.Hue[quant[i]]) list.Add(i);
            if (list.Count >= MinHuePixels) familySeeds.Add((family, list));
        }
        var richest = familySeeds.Count == 0 ? 0 : familySeeds.Max(f => f.Seeds.Count);
        // The tests of one placement, cheapest first: 1, the outlines (white stays light and black stays dark down to an
        // opacity of one half); 2, the spread fills on fill-hue pixels; 3, a dozen pairs agree under their own opacity; 4,
        // all pairs scored. Returns the last stage passed, with the score and opacity of stage 4.
        (int Stage, double Score, double Alpha) Evaluate(Glyph g, int left, int top)
        {
            // Two white and two black outline pixels first: most placements fail here.
            var quick = 0;
            for (var k = 0; k < 2 && k < g.WhiteX.Length && k < g.BlackX.Length; k++)
            {
                int x = left + g.WhiteX[k], y = top + g.WhiteY[k];
                if ((uint)x < (uint)rw && (uint)y < (uint)rh && luma[y * rw + x] < 120) quick++;
                x = left + g.BlackX[k]; y = top + g.BlackY[k];
                if ((uint)x < (uint)rw && (uint)y < (uint)rh && luma[y * rw + x] > 130) quick++;
                if (quick > 1) return (0, 0, 0);
            }
            int misses = 0, nw = 0, nb = 0, sumW = 0, sumB = 0;
            for (var k = 0; k < g.WhiteX.Length && misses <= 4; k++)
            {
                int x = left + g.WhiteX[k], y = top + g.WhiteY[k];
                if ((uint)x >= (uint)rw || (uint)y >= (uint)rh) continue;
                var l = luma[y * rw + x];
                if (l < 120) misses++; else { nw++; sumW += l; }
            }
            for (var k = 0; k < g.BlackX.Length && misses <= 4; k++)
            {
                int x = left + g.BlackX[k], y = top + g.BlackY[k];
                if ((uint)x >= (uint)rw || (uint)y >= (uint)rh) continue;
                var l = luma[y * rw + x];
                if (l > 130) misses++; else { nb++; sumB += l; }
            }
            if (misses > 4 || nw == 0 || nb == 0) return (0, 0, 0);
            var hits = 0;
            for (var k = 0; k < g.FillX.Length; k++)
            {
                int x = left + g.FillX[k], y = top + g.FillY[k];
                if ((uint)x < (uint)rw && (uint)y < (uint)rh && hueMap[y * rw + x]) hits++;
            }
            if (hits < g.FillX.Length - 2) return (1, 0, 0);
            // The opacity from the outlines (white over black is 255 levels at full opacity), then the probe pairs under it.
            var a0 = ((double)sumW / nw - (double)sumB / nb) / 255;
            if (a0 < MinAlpha - 0.1 || a0 > MaxAlpha + 0.05) return (2, 0, a0);
            int agree = 0, compared = 0;
            foreach (var p in g.ProbePairs)
            {
                if (!Screen(g, g.PairA[p], left, top, rw, rh, ok, out var si) || !Screen(g, g.PairB[p], left, top, rw, rh, ok, out var sj)) continue;
                compared++;
                var good = true;
                for (var c = 0; c < 3 && good; c++)
                {
                    var dt = g.Art[g.PairA[p] * 3 + c] - g.Art[g.PairB[p] * 3 + c];
                    if (Math.Abs(px[si * 3 + c] - px[sj * 3 + c] - a0 * dt) > ProbeSlack) good = false;
                }
                if (good) agree++;
            }
            if (compared == 0 || agree < ProbeThreshold * compared) return (2, 0, a0);
            // The probe pairs again under their own median opacity, with the full score's slack.
            if (Pairs(g, g.ProbePairs, left, top, px, ok, rw, rh, ratios, out var pa) < 0.45 || pa is < MinAlpha or > MaxAlpha) return (2, 0, pa);
            PlacementsScored++;
            var score = Pairs(g, null, left, top, px, ok, rw, rh, ratios, out var alpha);
            return (score >= GlyphThreshold ? 4 : 3, score, alpha);
        }
        if (Environment.GetEnvironmentVariable("OAKSOUL_DAMAGE_TRACE") is { } trace && trace.Split(',') is [var tf, var tfirst, var td, var tx, var ty])
            foreach (var family in _families.Where(f => f.Name == tf))
                foreach (var g in family.Glyphs.Where(g => g.First == (tfirst == "1") && g.Digit == int.Parse(td)))
                {
                    for (var i = 0; i < rw * rh; i++) hueMap[i] = ok[i] && family.Hue[quant[i]];
                    var r = Evaluate(g, int.Parse(tx) - rx0, int.Parse(ty) - ry0);
                    Console.WriteLine($"damage trace {trace}: stage {r.Stage} score {r.Score:0.00} alpha {r.Alpha:0.00}; anchors {string.Join(" ", Enumerable.Range(0, g.FillX.Length).Select(k => $"{g.FillX[k]},{g.FillY[k]}"))}");
                }
        foreach (var (family, seeds) in familySeeds)
        {
            if (seeds.Count < richest / 2) continue;
            for (var i = 0; i < rw * rh; i++) hueMap[i] = ok[i] && family.Hue[quant[i]];
            foreach (var g in family.Glyphs)
            {
                if (g.FillX.Length == 0 || g.PairA.Length < 20) continue;
                Array.Clear(seen);
                // Seeds: the region's fill-hue pixels, each tried as the glyph's first two probe fills.
                for (var anchor = 0; anchor < Math.Min(2, g.FillX.Length); anchor++)
                    foreach (var i in seeds)
                    {
                        var dh = Math.Abs(QuantHue[quant[i]] - g.FillHue[anchor]);
                        if (Math.Min(dh, 360 - dh) > SeedHueSlack) continue;
                        int left = i % rw - g.FillX[anchor], top = i / rw - g.FillY[anchor];
                        if (left < -g.W / 2 || top < -g.H / 2 || left > rw - g.W / 2 || top > rh - g.H / 2 || g.W / 2 > SeenPad || g.H / 2 > SeenPad) continue;
                        var key = (top + SeenPad) * sw2 + left + SeenPad;
                        if (seen[key]) continue;
                        seen[key] = true;
                        var (stage, score, alpha) = Evaluate(g, left, top);
                        if (stage == 4 && score >= GlyphThreshold && alpha is >= MinAlpha and <= MaxAlpha) digits.Add((family, g, left, top, score, alpha));
                    }
            }
        }
        // The best of overlapping digits (of any family) wins.
        var kept = new List<(Family Family, Glyph Glyph, int Left, int Top, double Score, double Alpha)>();
        foreach (var d in digits.OrderByDescending(d => d.Score).ThenByDescending(d => d.Glyph.PairA.Length))
        {
            var clash = kept.Any(k =>
            {
                int ox = Math.Min(k.Left + k.Glyph.W, d.Left + d.Glyph.W) - Math.Max(k.Left, d.Left);
                int oy = Math.Min(k.Top + k.Glyph.H, d.Top + d.Glyph.H) - Math.Max(k.Top, d.Top);
                return ox > 0.5 * Math.Min(k.Glyph.W, d.Glyph.W) && oy > 0.5 * Math.Min(k.Glyph.H, d.Glyph.H);
            });
            if (!clash) kept.Add(d);
        }
        // Chains: same family, one baseline, successive origins an advance apart, similar opacity; a first digit starts a new number.
        foreach (var group in kept.GroupBy(k => k.Family))
        {
            var list = group.OrderBy(k => k.Left + k.Glyph.OriginX).ToList();
            var used = new bool[list.Count];
            for (var a = 0; a < list.Count; a++)
            {
                if (used[a]) continue;
                var chain = new List<int> { a };
                var gaps = new List<bool> { false };
                used[a] = true;
                var last = a;
                for (var b = a + 1; b < list.Count; b++)
                {
                    if (used[b] || list[b].Glyph.First) continue;
                    var p = list[last];
                    var q = list[b];
                    int adv = q.Left + q.Glyph.OriginX - (p.Left + p.Glyph.OriginX);
                    var dy = Math.Abs(q.Top + q.Glyph.OriginY - (p.Top + p.Glyph.OriginY));
                    if (dy > BaselineSlack || Math.Abs(q.Alpha - p.Alpha) > 0.15) continue;
                    // A digit two advances on: the one between was not read (covered by its neighbours, or faded more).
                    var gap = adv > MaxAdvance && adv <= MaxAdvance + MaxGapAdvance;
                    if (adv < MinAdvance || (adv > MaxAdvance && !gap)) continue;
                    chain.Add(b);
                    gaps.Add(gap);
                    used[b] = true;
                    last = b;
                }
                var glyphs = chain.Select(c => list[c]).Select(d => new DamageDigit(d.Glyph.Digit, d.Glyph.First, rx0 + d.Left, ry0 + d.Top, d.Glyph.W, d.Glyph.H,
                    d.Glyph.OriginX, d.Glyph.OriginY, Math.Round(d.Score, 3), Math.Round(d.Alpha, 3))).ToList();
                int bx0 = glyphs.Min(d => d.Left), by0 = glyphs.Min(d => d.Top), bx1 = glyphs.Max(d => d.Left + d.Width), by1 = glyphs.Max(d => d.Top + d.Height);
                var fam = group.Key;
                var digitsText = string.Concat(glyphs.Select((d, k) => (gaps[k] ? "?" : "") + (char)('0' + d.Digit)));
                result.Add(new DamageMatch(fam.Name, fam.Direction, fam.Critical, digitsText,
                    new CaptureRect(bx0, by0, bx1 - bx0, by1 - by0), Math.Round(glyphs.Average(d => d.Score), 3), Math.Round(glyphs.Average(d => d.Alpha), 3), glyphs));
            }
        }
        Milliseconds += sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// The share of a glyph's pairs (or of <paramref name="subset"/>) that agree at a placement: the opacity is the median
    /// ratio of screen to art difference over the pairs' strongly differing channels, and a pair agrees when every channel's
    /// screen difference is the art difference times it, within <see cref="PairSlack"/>. Pairs on masked or outside pixels
    /// are left out.
    /// </summary>
    private static double Pairs(Glyph g, int[]? subset, int left, int top, int[] px, bool[] ok, int rw, int rh, List<double> ratios, out double alpha)
    {
        ratios.Clear();
        var n = subset?.Length ?? g.PairA.Length;
        var step = subset is null ? Math.Max(1, n / 60) : 1;
        for (var k = 0; k < n; k += step)
        {
            var p = subset is null ? k : subset[k];
            if (!Screen(g, g.PairA[p], left, top, rw, rh, ok, out var si) || !Screen(g, g.PairB[p], left, top, rw, rh, ok, out var sj)) continue;
            for (var c = 0; c < 3; c++)
            {
                double dt = g.Art[g.PairA[p] * 3 + c] - g.Art[g.PairB[p] * 3 + c];
                if (Math.Abs(dt) >= 60) ratios.Add((px[si * 3 + c] - px[sj * 3 + c]) / dt);
            }
        }
        alpha = 0;
        if (ratios.Count < 4) return 0;
        ratios.Sort();
        alpha = ratios[ratios.Count / 2];
        int agree = 0, compared = 0;
        for (var k = 0; k < n; k++)
        {
            var p = subset is null ? k : subset[k];
            if (!Screen(g, g.PairA[p], left, top, rw, rh, ok, out var si) || !Screen(g, g.PairB[p], left, top, rw, rh, ok, out var sj)) continue;
            compared++;
            var good = true;
            for (var c = 0; c < 3 && good; c++)
            {
                var dt = g.Art[g.PairA[p] * 3 + c] - g.Art[g.PairB[p] * 3 + c];
                var ds = px[si * 3 + c] - px[sj * 3 + c];
                if (Math.Abs(ds - alpha * dt) > PairSlack) good = false;
            }
            if (good) agree++;
        }
        return compared < 0.4 * n ? 0 : (double)agree / compared;
    }

    private static bool Screen(Glyph g, int artIndex, int left, int top, int rw, int rh, bool[] ok, out int index)
    {
        int x = left + artIndex % g.W, y = top + artIndex / g.W;
        index = y * rw + x;
        return (uint)x < (uint)rw && (uint)y < (uint)rh && ok[index];
    }
}
