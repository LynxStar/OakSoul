using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OakSoul.Detect;

/// <summary>
/// A name rendered in the tag font: its text bits, <see cref="Rows"/> high with the baseline at row <see cref="TagFont.Above"/>.
/// </summary>
public sealed record TagRendering(string Text, bool[] Bits, int Width, int Ones)
{
    public int Rows => TagFont.Rows;

    /// <summary>The first and last rows holding ink: what a box is compared on (its rows above the capitals may hold the sprite standing on the tag).</summary>
    public int InkTop { get; init; }
    public int InkBottom { get; init; }

    /// <summary>Each glyph's columns and ink bits, left to right, for judging a match glyph by glyph.</summary>
    public IReadOnlyList<(int X, int Width, int Ones)> Glyphs { get; init; } = [];
}

/// <summary>
/// The font the client draws name tags in, learned from reads whose text is a name the assets give (a species of the map, the
/// pet's name), so that such names can be rendered into the text bits a tag box shows and known without OCR. The font is a
/// 1-bit pixel font: on fx-combat-exact's crops every glyph's bits are the same at luma thresholds 100 to 150, letters stand
/// apart by blank columns, a letter's ink starts one or two columns after the previous letter's ink ends, which depends on that
/// previous letter alone, and a space adds <see cref="SpaceColumns"/> more (checked on the 20 letter pairs of four names over
/// 39 clean samples: every pair's distance agreed in every sample but one). So a glyph is its ink bitmap and its gap, and a
/// name renders by concatenation; a letter whose gap was never seen (only ever last in a name) renders only last.
/// </summary>
public sealed class TagFont
{
    /// <summary>Rows kept per glyph: <see cref="Above"/> above the baseline, the baseline, and the descender rows below.</summary>
    public const int Rows = 12, Above = 9;

    /// <summary>Widest glyph and widest gaps a sample may show (an 'm' is 9 columns; gaps are 1 or 2 inside a word, 4 or 5 at a space).</summary>
    public const int MaxGlyphWidth = 12, MaxLetterGap = 4, MaxWordGap = 9;

    /// <summary>
    /// Columns a space adds to a letter's gap, as learned from letters whose own gap is known; 3 on fx-combat-exact. A letter seen
    /// only before a space keeps the whole distance it showed (<see cref="Glyph.SpacedGap"/>), so it renders there without this.
    /// </summary>
    public int SpaceColumns => Mode(_spaceVotes) ?? 3;

    /// <summary>The most voted value; of a tie the smallest, so a font votes the same live and after a load.</summary>
    private static int? Mode(Dictionary<int, int> votes) => votes.Count == 0 ? null : votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key).First().Key;

    private sealed class Glyph
    {
        public required char Letter;
        public required int Width;
        public required int[] InkVotes;
        public int Samples;
        public readonly Dictionary<int, int> GapVotes = [];
        /// <summary>Columns from the letter's ink to the next word's when a space follows: its own gap plus the space's, as seen.</summary>
        public readonly Dictionary<int, int> SpacedGapVotes = [];
        public int? Gap => Mode(GapVotes);
        public int? SpacedGap => Mode(SpacedGapVotes);
        public bool Ink(int x, int y) => InkVotes[y * Width + x] * 2 > Samples;
    }

    private readonly Dictionary<char, Glyph> _glyphs = [];
    private readonly Dictionary<int, int> _spaceVotes = [];

    /// <summary>Samples offered, taken, and refused: for not splitting into one group per letter, or disagreeing with the glyphs learned.</summary>
    public int Offered { get; private set; }
    public int Samples { get; private set; }
    public int Rejected { get; private set; }
    public int Conflicts { get; private set; }

    /// <summary>Bumps whenever a sample was learned, so renderings made from an older font can be redone.</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<char> Letters => _glyphs.Keys;

    /// <summary>The letters whose gap is known, so they render inside a name, not only last.</summary>
    public int LettersWithGap => _glyphs.Values.Count(g => g.Gap is not null);

    /// <summary>
    /// Learns from a box whose text is <paramref name="text"/> (a known name, exactly): the rightmost one group of text columns per
    /// letter are the name's glyphs (a badge, if any, lies left of them), set apart by blank columns, none cut at the box's edge,
    /// their widths agreeing with the font's. Returns true when the sample was taken.
    /// </summary>
    public bool Learn(bool[] bits, int width, int height, string text)
    {
        Offered++;
        var letters = text.Replace(" ", "");
        if (letters.Length == 0 || height < Rows - 2) { Rejected++; return false; }
        var textIndex = LetterIndices(text);
        // First over the rows between the box's edge rows, to find the baseline; then over the glyph rows alone, so a sprite standing
        // on the tag (its pixels in the rows above the capitals) cannot join two letters or add a group.
        var baseline = -1;
        List<(int X0, int X1)>? name = null;
        for (var pass = 0; pass < 2; pass++)
        {
            int top = pass == 0 ? 1 : baseline - 8, bottom = pass == 0 ? height - 2 : baseline + 2;
            var groups = Groups(bits, width, Math.Max(1, top), Math.Min(height - 2, bottom));
            if (groups.Count < letters.Length) { Rejected++; return false; }
            name = groups.GetRange(groups.Count - letters.Length, letters.Length);
            var bottoms = name.Select(g => Bottom(bits, width, Math.Max(1, top), Math.Min(height - 2, bottom), g)).ToList();
            // The baseline: the topmost of the letters' bottoms, with every other within two rows below it (the descenders).
            var b = bottoms.Min();
            if (bottoms.Any(v => v > b + 2)) { Rejected++; return false; }
            baseline = b;
        }
        if (name![0].X0 == 0 || name[^1].X1 == width - 1 || name.Any(g => g.X1 - g.X0 + 1 > MaxGlyphWidth)) { Rejected++; return false; }
        for (var i = 1; i < name.Count; i++)
        {
            var gap = name[i].X0 - name[i - 1].X1 - 1;
            if (gap > (text[textIndex[i] - 1] == ' ' ? MaxWordGap : MaxLetterGap)) { Rejected++; return false; }
        }
        if (baseline - Above < 0 || baseline + (Rows - Above - 1) > height - 1) { Rejected++; return false; }
        // Agreement on widths, with the font and within the sample: a letter's width is one number, else the sample is no clean one.
        var widths = new Dictionary<char, int>();
        for (var i = 0; i < letters.Length; i++)
        {
            var w = name[i].X1 - name[i].X0 + 1;
            if (_glyphs.TryGetValue(letters[i], out var known) && known.Width != w) { Conflicts++; Rejected++; return false; }
            if (widths.TryGetValue(letters[i], out var seen) && seen != w) { Conflicts++; Rejected++; return false; }
            widths[letters[i]] = w;
        }
        for (var i = 0; i < letters.Length; i++)
        {
            var g = name[i];
            var w = g.X1 - g.X0 + 1;
            if (!_glyphs.TryGetValue(letters[i], out var glyph)) _glyphs[letters[i]] = glyph = new Glyph { Letter = letters[i], Width = w, InkVotes = new int[Rows * w] };
            glyph.Samples++;
            for (var y = 0; y < Rows; y++)
            {
                var row = baseline - Above + y;
                for (var x = 0; x < glyph.Width; x++) if (bits[row * width + g.X0 + x]) glyph.InkVotes[y * glyph.Width + x]++;
            }
            if (i + 1 < letters.Length)
            {
                var gap = name[i + 1].X0 - g.X1 - 1;
                if (text[textIndex[i + 1] - 1] == ' ')
                {
                    // A space: the whole distance is kept for this letter, and the space's own columns are learned from letters whose gap is known.
                    glyph.SpacedGapVotes[gap] = glyph.SpacedGapVotes.GetValueOrDefault(gap) + 1;
                    if (glyph.Gap is { } own && gap - own >= 1) _spaceVotes[gap - own] = _spaceVotes.GetValueOrDefault(gap - own) + 1;
                }
                else glyph.GapVotes[gap] = glyph.GapVotes.GetValueOrDefault(gap) + 1;
            }
        }
        Samples++;
        Version++;
        return true;
    }

    /// <summary>Renders a name, or null when a letter is unknown or a letter inside the name has no gap yet.</summary>
    public TagRendering? Render(string text)
    {
        var letters = text.Replace(" ", "");
        if (letters.Length == 0) return null;
        var textIndex = LetterIndices(text);
        var x = 0;
        var places = new List<(Glyph Glyph, int X)>();
        for (var i = 0; i < letters.Length; i++)
        {
            if (!_glyphs.TryGetValue(letters[i], out var g) || g.Samples == 0) return null;
            places.Add((g, x));
            if (i + 1 == letters.Length) break;
            var spaced = text[textIndex[i + 1] - 1] == ' ';
            int? step = spaced ? g.SpacedGap ?? (g.Gap is { } own ? own + SpaceColumns : null) : g.Gap;
            if (step is not { } gap) return null;
            x += g.Width + gap;
        }
        var width = places[^1].X + places[^1].Glyph.Width;
        var bits = new bool[Rows * width];
        var ones = 0;
        int top = Rows, bottom = -1;
        foreach (var (g, gx) in places)
            for (var y = 0; y < Rows; y++)
                for (var dx = 0; dx < g.Width; dx++)
                    if (g.Ink(dx, y)) { bits[y * width + gx + dx] = true; ones++; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (ones == 0) return null;
        var glyphs = places.Select(p => (p.X, p.Glyph.Width, Ones: Enumerable.Range(0, Rows * p.Glyph.Width).Count(i => p.Glyph.Ink(i % p.Glyph.Width, i / p.Glyph.Width)))).ToList();
        return new TagRendering(text, bits, width, ones) { InkTop = top, InkBottom = bottom, Glyphs = glyphs };
    }

    /// <summary>
    /// Looks for a rendering in a box's bits, at every column and on every baseline row the glyphs fit: the fewest differing bits
    /// over the rendering's rectangle, with where it lay and how many text bits the box shows in those rows left and right of it
    /// (a badge to the left; nothing, for a name standing alone). Null when the box is narrower than the rendering.
    /// </summary>
    public static (int Differing, int X, int Baseline, int LeftOutside, int RightOutside)? Find(TagRendering r, bool[] bits, int width, int height, int limit = int.MaxValue)
    {
        if (r.Width > width) return null;
        (int Differing, int X, int Baseline)? best = null;
        // The baseline may sit on any row that keeps the rendering's ink rows inside the box.
        for (var baseline = Above - r.InkTop; baseline - Above + r.InkBottom <= height - 1; baseline++)
            for (var x0 = 0; x0 + r.Width <= width; x0++)
            {
                var diff = 0;
                var stop = Math.Min(limit, best?.Differing ?? int.MaxValue);
                for (var y = r.InkTop; y <= r.InkBottom && diff <= stop; y++)
                {
                    var row = baseline - Above + y;
                    for (var dx = 0; dx < r.Width; dx++)
                        if (r.Bits[y * r.Width + dx] != bits[row * width + x0 + dx]) diff++;
                }
                if (diff <= stop && (best is null || diff < best.Value.Differing)) best = (diff, x0, baseline);
            }
        if (best is not { } b) return null;
        int left = 0, right = 0;
        for (var y = r.InkTop; y <= r.InkBottom; y++)
        {
            var row = b.Baseline - Above + y;
            for (var x = 0; x < b.X; x++) if (bits[row * width + x]) left++;
            for (var x = b.X + r.Width; x < width; x++) if (bits[row * width + x]) right++;
        }
        return (b.Differing, b.X, b.Baseline, left, right);
    }

    /// <summary>
    /// The worst glyph of a match: the largest share of a glyph's ink bits differing between the rendering at (x, baseline) and the
    /// box. A merged pair of tags can match a long name within <see cref="TagDetector.FuzzyBits"/> of its bits with one letter
    /// wholly wrong (a cut D read as J); no letter of a true match differs by much.
    /// </summary>
    public static double WorstGlyph(TagRendering r, bool[] bits, int width, int x, int baseline)
    {
        var worst = 0.0;
        foreach (var (gx, gw, ones) in r.Glyphs)
        {
            var diff = 0;
            for (var y = r.InkTop; y <= r.InkBottom; y++)
            {
                var row = baseline - Above + y;
                for (var dx = 0; dx < gw; dx++)
                    if (r.Bits[y * r.Width + gx + dx] != bits[row * width + x + gx + dx]) diff++;
            }
            worst = Math.Max(worst, (double)diff / Math.Max(1, ones));
        }
        return worst;
    }

    /// <summary>What stands left of a name found in a box.</summary>
    public enum LeftOf { Nothing, Badge, Other }

    /// <summary>
    /// Stray text bits allowed beside a name: the sprite standing on the tag reaches into the box's corners (the pet's tail over its
    /// own tag), a merged pair of tags shows a whole word there.
    /// </summary>
    public const int StrayBits = 8;

    /// <summary>Columns from the badge's last text column to the name's first; 2 or 3 on fx-combat-exact's boxes.</summary>
    public const int BadgeGap = 8;

    /// <summary>
    /// What stands left of a rendering found at (x, baseline), in the rows from the capitals' top to the baseline (the badge's frame
    /// runs along the box's top and bottom rows, which lie outside those; the sprite standing on the tag reaches into the rows above):
    /// nothing (a few stray bits at most), a level badge, or other text. The badge is drawn in a smaller font two rows shorter than
    /// the name's box: its text ends within <see cref="BadgeGap"/> columns of the name and its glyphs stand two or more rows above
    /// the name's baseline (the median of its column groups' bottoms, since its frame's right border reaches the baseline), where a
    /// neighbouring tag's letters stand on it.
    /// </summary>
    public static LeftOf WhatIsLeft(TagRendering r, int baseline, bool[] bits, int width, int height, int x)
    {
        int top = Math.Max(0, baseline - 8), bottom = Math.Min(height - 1, baseline);
        int Count(int column) { var n = 0; for (var y = top; y <= bottom; y++) if (bits[y * width + column]) n++; return n; }
        var end = -1;
        for (var c = x - 1; c >= 0 && c >= x - BadgeGap; c--) if (Count(c) > 0) { end = c; break; }
        var stray = 0;
        for (var c = 0; c < x; c++) stray += Count(c);
        if (stray <= StrayBits) return LeftOf.Nothing;
        if (end < 0) return LeftOf.Other;
        // The column groups of the text up to 36 columns left of the name, each with its bottom row.
        var bottoms = new List<int>();
        var groupBottom = -1;
        for (var c = end; c >= Math.Max(0, end - 36); c--)
        {
            var any = false;
            for (var y = top; y <= bottom; y++) if (bits[y * width + c]) { any = true; groupBottom = Math.Max(groupBottom, y); }
            if (!any && groupBottom >= 0) { bottoms.Add(groupBottom); groupBottom = -1; }
        }
        if (groupBottom >= 0) bottoms.Add(groupBottom);
        if (bottoms.Count == 0) return LeftOf.Nothing;
        bottoms.Sort();
        var median = bottoms[bottoms.Count / 2];
        return median <= baseline - 2 ? LeftOf.Badge : LeftOf.Other;
    }

    /// <summary>A known name found in a box: the text as a read would give it, its level (the species', when its badge stands before it) and the name.</summary>
    public sealed record KnownMatch(string Text, int? Level, string Name, int Differing, int X, int Baseline);

    /// <summary>
    /// The known name a box shows, or null: the rendering within <see cref="TagDetector.FuzzyBits"/> of its bits (at least
    /// <see cref="StrayBits"/>), no glyph differing by more than a third, no more than <see cref="StrayBits"/> stray bits right of
    /// it, and to its left a badge (a species: the text reads "Lv. N Name"), nothing (the name alone) or, for a species, nothing
    /// too (a box cut before its badge reads as the name alone). Of several, the fewest differing bits, then the longest name.
    /// </summary>
    public static KnownMatch? Match(IReadOnlyList<(string Name, int? Level, TagRendering Rendering)> renderings, bool[] bits, int width, int height)
    {
        KnownMatch? best = null;
        foreach (var (name, level, r) in renderings)
        {
            var limit = Math.Max(StrayBits, (int)Math.Floor(TagDetector.FuzzyBits * r.Ones));
            if (Find(r, bits, width, height, limit) is not { } f) continue;
            if (f.RightOutside > StrayBits || WorstGlyph(r, bits, width, f.X, f.Baseline) > 1 / 3.0) continue;
            var left = WhatIsLeft(r, f.Baseline, bits, width, height, f.X);
            if (left == LeftOf.Other || (left == LeftOf.Badge && level is null)) continue;
            var match = left == LeftOf.Badge ? new KnownMatch($"Lv. {level} {name}", level, name, f.Differing, f.X, f.Baseline) : new KnownMatch(name, null, name, f.Differing, f.X, f.Baseline);
            if (best is null || match.Differing < best.Differing || (match.Differing == best.Differing && name.Length > best.Name.Length)) best = match;
        }
        return best;
    }

    /// <summary>Runs of columns holding text bits in the rows from <paramref name="top"/> to <paramref name="bottom"/>, inclusive.</summary>
    private static List<(int X0, int X1)> Groups(bool[] bits, int width, int top, int bottom)
    {
        var groups = new List<(int X0, int X1)>();
        var start = -1;
        for (var x = 0; x <= width; x++)
        {
            var any = false;
            if (x < width) for (var y = top; y <= bottom && !any; y++) any = bits[y * width + x];
            if (any && start < 0) start = x;
            if (!any && start >= 0) { groups.Add((start, x - 1)); start = -1; }
        }
        return groups;
    }

    private static int Bottom(bool[] bits, int width, int top, int bottom, (int X0, int X1) g)
    {
        for (var y = bottom; y >= top; y--)
            for (var x = g.X0; x <= g.X1; x++)
                if (bits[y * width + x]) return y;
        return -1;
    }

    /// <summary>For each letter of a text (spaces left out), its index in the text.</summary>
    private static int[] LetterIndices(string text)
    {
        var indices = new List<int>();
        for (var i = 0; i < text.Length; i++) if (text[i] != ' ') indices.Add(i);
        return indices.ToArray();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteNumber("rows", Rows);
            w.WriteNumber("above", Above);
            w.WriteNumber("samples", Samples);
            w.WriteStartObject("space");
            foreach (var (k, v) in _spaceVotes.OrderBy(v => v.Key)) w.WriteNumber(k.ToString(), v);
            w.WriteEndObject();
            w.WriteStartArray("glyphs");
            foreach (var g in _glyphs.Values.OrderBy(g => g.Letter))
            {
                w.WriteStartObject();
                w.WriteString("letter", g.Letter.ToString());
                w.WriteNumber("width", g.Width);
                w.WriteNumber("samples", g.Samples);
                w.WriteStartArray("rows");
                for (var y = 0; y < Rows; y++)
                {
                    var sb = new StringBuilder(g.Width);
                    for (var x = 0; x < g.Width; x++) sb.Append(g.Ink(x, y) ? '#' : '.');
                    w.WriteStringValue(sb.ToString());
                }
                w.WriteEndArray();
                w.WriteStartArray("votes");
                foreach (var v in g.InkVotes) w.WriteNumberValue(v);
                w.WriteEndArray();
                w.WriteStartObject("gap");
                foreach (var (k, v) in g.GapVotes.OrderBy(v => v.Key)) w.WriteNumber(k.ToString(), v);
                w.WriteEndObject();
                w.WriteStartObject("spacedGap");
                foreach (var (k, v) in g.SpacedGapVotes.OrderBy(v => v.Key)) w.WriteNumber(k.ToString(), v);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static TagFont FromJson(string json)
    {
        var font = new TagFont();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("tag font: another file version");
        if (root.GetProperty("rows").GetInt32() != Rows || root.GetProperty("above").GetInt32() != Above) throw new InvalidDataException("tag font: another row layout");
        font.Samples = root.GetProperty("samples").GetInt32();
        if (font.Samples < 0) throw new InvalidDataException("tag font: negative samples");
        static Dictionary<int, int> Votes(JsonElement e)
        {
            var votes = new Dictionary<int, int>();
            foreach (var p in e.EnumerateObject())
            {
                if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) || key < 0 || key > 64 || p.Value.GetInt32() < 1) throw new InvalidDataException($"tag font: a vote of {p.Name}: {p.Value}");
                votes[key] = p.Value.GetInt32();
            }
            return votes;
        }
        foreach (var (k, v) in Votes(root.GetProperty("space"))) font._spaceVotes[k] = v;
        foreach (var e in root.GetProperty("glyphs").EnumerateArray())
        {
            var text = e.GetProperty("letter").GetString();
            if (text is not { Length: 1 }) throw new InvalidDataException($"tag font: a glyph's letter is '{text}'");
            var letter = text[0];
            var width = e.GetProperty("width").GetInt32();
            if (width is < 1 or > MaxGlyphWidth) throw new InvalidDataException($"tag font: glyph '{letter}' is {width} wide");
            var glyph = new Glyph { Letter = letter, Width = width, InkVotes = e.GetProperty("votes").EnumerateArray().Select(v => v.GetInt32()).ToArray(), Samples = e.GetProperty("samples").GetInt32() };
            if (glyph.Samples < 1) throw new InvalidDataException($"tag font: glyph '{letter}' has {glyph.Samples} samples");
            if (glyph.InkVotes.Length != Rows * width || glyph.InkVotes.Any(v => v < 0 || v > glyph.Samples)) throw new InvalidDataException($"tag font: glyph '{letter}' has {glyph.InkVotes.Length} votes for {Rows}x{width}");
            foreach (var (k, v) in Votes(e.GetProperty("gap"))) glyph.GapVotes[k] = v;
            if (e.TryGetProperty("spacedGap", out var sg)) foreach (var (k, v) in Votes(sg)) glyph.SpacedGapVotes[k] = v;
            font._glyphs[letter] = glyph;
        }
        return font;
    }

    /// <summary>Writes the font beside the path and moves it into place, so a reader never sees a half-written file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
    }

    public static TagFont? Load(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
}
