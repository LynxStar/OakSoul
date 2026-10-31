using System.Text;
using System.Text.Json;

namespace OakSoul.Vision.Readers;

/// <summary>Where a name's glyphs were found on a line: its first and last column, the baseline row, and how many bits differed.</summary>
public sealed record MapNameAlignment(int X0, int X1, int Baseline, int Differing, int Ones);

/// <summary>
/// One of the two fonts the minimap's name block is drawn in (see <see cref="MapNameFonts"/>): glyph bitmaps learned, one vote per
/// pixel per sample, from lines whose text the map catalog confirmed. Like the tag font it keeps <see cref="Rows"/> rows per glyph,
/// <see cref="Above"/> of them above the baseline, and a letter's width is one number; unlike it, it learns no gaps. Measured on the
/// recordings (README, "Map names without a read"): the gap between two letters depends on both of them (n to t one column, n to e two;
/// D to u two, T to u one), and on the expanded panel neighbouring letters touch, so a line is cut into letters where a second, stricter
/// threshold leaves a blank column, and a name is found on a line by aligning its glyphs left to right with the gap free within
/// <see cref="MinWordGap"/> to <see cref="MaxWordGap"/> inside a word and <see cref="MinSpaceGap"/> to <see cref="MaxSpaceGap"/> at a space.
/// </summary>
public sealed class MapFont
{
    /// <summary>Rows kept per glyph: <see cref="Above"/> above the baseline, the baseline, and the descender rows below.</summary>
    public const int Rows = 12, Above = 9;

    /// <summary>Widest glyph a sample may show (the panel's W is 12 columns).</summary>
    public const int MaxGlyphWidth = 14;

    /// <summary>
    /// Gaps tried between two glyphs' columns when aligning a name: 0 to 2 inside a word and 3 to 4 at a space on the panel, 1 to 2
    /// and 2 to 5 on the bar, each widened by one.
    /// </summary>
    public const int MinWordGap = 0, MaxWordGap = 3, MinSpaceGap = 2, MaxSpaceGap = 6;

    /// <summary>A name is found when its glyphs differ from the line by at most this share of their ink bits, and no glyph by more than a third.</summary>
    public const double FuzzyBits = 0.08;

    /// <summary>Stray ink bits allowed beside a found name, and the least differing bits a name may show before the share applies.</summary>
    public const int StrayBits = 8;

    /// <summary>Ways of cutting a line's merged letters that are tried before the line is given up as ambiguous.</summary>
    public const int MaxCutCombinations = 2000;

    private sealed class Glyph
    {
        public required char Letter;
        public required int Width;
        public required int[] InkVotes;
        public int Samples;
        private bool[]? _bits;
        private int _ones;
        /// <summary>The voted bitmap, row by row; a pixel is ink when more than half the samples showed it.</summary>
        public bool[] Bits { get { if (_bits is null) { _bits = new bool[InkVotes.Length]; _ones = 0; for (var i = 0; i < InkVotes.Length; i++) if (InkVotes[i] * 2 > Samples) { _bits[i] = true; _ones++; } } return _bits; } }
        public int Ones { get { _ = Bits; return _ones; } }
        public void Invalidate() => _bits = null;
    }

    private readonly Dictionary<char, Glyph> _glyphs = [];

    public MapFont(string name) { Name = name; }

    /// <summary>"panel" or "bar", for messages and the file.</summary>
    public string Name { get; }

    /// <summary>Lines offered, taken, and refused: for not cutting into one glyph per letter, for an ambiguous cut, or for disagreeing on a width.</summary>
    public int Offered { get; private set; }
    public int Samples { get; private set; }
    public int Rejected { get; private set; }
    public int Ambiguous { get; private set; }
    public int Conflicts { get; private set; }

    /// <summary>Bumps whenever a sample was learned.</summary>
    public int Version { get; private set; }

    public IReadOnlyCollection<char> Letters => _glyphs.Keys;

    /// <summary>Whether every letter of a text (spaces apart) has a glyph, so the text can be aligned to a line.</summary>
    public bool CanRender(string text)
    {
        var any = false;
        foreach (var c in text) { if (c == ' ') continue; if (!_glyphs.ContainsKey(c)) return false; any = true; }
        return any;
    }

    /// <summary>The voted bitmap of a letter, <see cref="Rows"/> strings of '#' and '.', for tests and the file; null for a letter unknown.</summary>
    public IReadOnlyList<string>? Bitmap(char letter)
    {
        if (!_glyphs.TryGetValue(letter, out var g)) return null;
        var rows = new List<string>(Rows);
        for (var y = 0; y < Rows; y++) { var sb = new StringBuilder(g.Width); for (var x = 0; x < g.Width; x++) sb.Append(g.Bits[y * g.Width + x] ? '#' : '.'); rows.Add(sb.ToString()); }
        return rows;
    }

    /// <summary>
    /// Learns from a line whose text is <paramref name="text"/> exactly: the ink bits of a crop (<paramref name="width"/> by
    /// <paramref name="height"/>), the rows <paramref name="top"/> to <paramref name="bottom"/> holding the line. The line's column groups
    /// are its letters; when they are fewer than the letters and <paramref name="split"/> is given (the same crop at a stricter
    /// threshold), merged groups are cut at the midpoint of an interior stretch blank in it, as many cuts as the letter count needs,
    /// and only when exactly one way of cutting gives every letter a width the font allows (the width it knows for that letter, or
    /// any up to <see cref="MaxGlyphWidth"/> for one it does not); two ways are ambiguous and teach nothing. A line with a letter at
    /// the crop's edge, ink above the glyph rows or a width the font knows otherwise teaches nothing either. Returns true when taken.
    /// </summary>
    public bool Learn(bool[] ink, bool[]? split, int width, int height, int top, int bottom, string text)
    {
        Offered++;
        var letters = text.Replace(" ", "");
        if (letters.Length == 0 || top < 0 || bottom >= height || top > bottom) { Rejected++; return false; }
        var groups = Groups(ink, width, top, bottom);
        if (groups.Count == 0 || groups.Count > letters.Length) { Rejected++; return false; }
        List<(int X0, int X1)>? cells;
        if (groups.Count == letters.Length) cells = groups;
        else
        {
            if (split is null) { Rejected++; return false; }
            cells = Cut(groups, split, width, top, bottom, letters);
            if (cells is null) { Rejected++; return false; }
        }
        if (cells[0].X0 == 0 || cells[^1].X1 == width - 1 || cells.Any(c => c.X1 - c.X0 + 1 > MaxGlyphWidth)) { Rejected++; return false; }
        if (!WidthsAgree(cells, letters)) { Conflicts++; Rejected++; return false; }
        // The baseline: the row most of the letters' ink ends on; a descender reaches at most two rows below it, and nothing may stand
        // above the glyph rows, or the letter would be learned cut.
        var bottoms = cells.Select(c => Bottom(ink, width, top, bottom, c)).ToList();
        var baseline = bottoms.GroupBy(b => b).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        if (bottoms.Any(b => b > baseline + (Rows - Above - 1)) || baseline - Above < 0 || baseline + (Rows - Above - 1) > height - 1) { Rejected++; return false; }
        if (cells.Any(c => Top(ink, width, top, bottom, c) < baseline - Above)) { Rejected++; return false; }
        for (var i = 0; i < letters.Length; i++)
        {
            var (x0, x1) = cells[i];
            var w = x1 - x0 + 1;
            if (!_glyphs.TryGetValue(letters[i], out var glyph)) _glyphs[letters[i]] = glyph = new Glyph { Letter = letters[i], Width = w, InkVotes = new int[Rows * w] };
            glyph.Samples++;
            for (var y = 0; y < Rows; y++)
            {
                var row = baseline - Above + y;
                for (var x = 0; x < w; x++) if (ink[row * width + x0 + x]) glyph.InkVotes[y * w + x]++;
            }
            glyph.Invalidate();
        }
        Samples++;
        Version++;
        return true;
    }

    /// <summary>Cuts merged groups into one cell per letter, or null when no way or more than one way of cutting fits (see <see cref="Learn"/>).</summary>
    private List<(int X0, int X1)>? Cut(List<(int X0, int X1)> groups, bool[] split, int width, int top, int bottom, string letters)
    {
        var cuts = new List<(int Group, int Mid)>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var (x0, x1) = groups[gi];
            var start = -1;
            for (var x = x0 + 1; x <= x1; x++)
            {
                var blank = x < x1;
                for (var y = top; y <= bottom && blank; y++) if (split[y * width + x]) blank = false;
                if (blank && start < 0) start = x;
                if (!blank && start >= 0) { cuts.Add((gi, (start + x - 1) / 2)); start = -1; }
            }
        }
        var need = letters.Length - groups.Count;
        if (cuts.Count < need || Combinations(cuts.Count, need) > MaxCutCombinations) return null;
        List<(int X0, int X1)>? found = null;
        foreach (var chosen in Choose(cuts.Count, need))
        {
            var cells = new List<(int X0, int X1)>();
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var from = groups[gi].X0;
                foreach (var k in chosen) if (cuts[k].Group == gi) { cells.Add((from, cuts[k].Mid)); from = cuts[k].Mid + 1; }
                cells.Add((from, groups[gi].X1));
            }
            if (cells.Any(c => c.X1 < c.X0 || c.X1 - c.X0 + 1 > MaxGlyphWidth) || !WidthsAgree(cells, letters)) continue;
            if (found is not null) { Ambiguous++; return null; }
            found = cells;
        }
        return found;
    }

    /// <summary>Whether every letter's cell has the width the font knows for it, and the sample agrees with itself.</summary>
    private bool WidthsAgree(List<(int X0, int X1)> cells, string letters)
    {
        var seen = new Dictionary<char, int>();
        for (var i = 0; i < letters.Length; i++)
        {
            var w = cells[i].X1 - cells[i].X0 + 1;
            if (_glyphs.TryGetValue(letters[i], out var g) && g.Width != w) return false;
            if (seen.TryGetValue(letters[i], out var s) && s != w) return false;
            seen[letters[i]] = w;
        }
        return true;
    }

    private static long Combinations(int n, int k)
    {
        long r = 1;
        for (var i = 1; i <= k; i++) { r = r * (n - k + i) / i; if (r > MaxCutCombinations * 4) return r; }
        return r;
    }

    private static IEnumerable<int[]> Choose(int n, int k)
    {
        var idx = Enumerable.Range(0, k).ToArray();
        if (k == 0) { yield return idx; yield break; }
        while (true)
        {
            yield return (int[])idx.Clone();
            var i = k - 1;
            while (i >= 0 && idx[i] == n - k + i) i--;
            if (i < 0) yield break;
            idx[i]++;
            for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    /// <summary>
    /// Looks for a text on a line: its glyphs placed left to right from a column within one of <paramref name="firstInk"/> (the line's
    /// first ink column), on a baseline that keeps the glyph rows inside the crop, the gap between two glyphs free within the word or
    /// space range; the fewest differing bits, where ink in a gap's columns differs too (nothing of the name stands there, so a glyph
    /// cannot hide in the gap of a shorter name) and no gap may hold more than <see cref="StrayBits"/> of it, with no glyph more than
    /// a third wrong and the whole within <see cref="FuzzyBits"/> of the glyphs' ink bits (at least <see cref="StrayBits"/>). Null
    /// when a letter is unknown or nothing fits. What lies left and right of the alignment is the caller's to judge.
    /// </summary>
    public MapNameAlignment? Align(string text, bool[] ink, int width, int height, int top, int bottom, int firstInk)
    {
        var letters = text.Replace(" ", "");
        if (letters.Length == 0) return null;
        var glyphs = new Glyph[letters.Length];
        for (var i = 0; i < letters.Length; i++) if (!_glyphs.TryGetValue(letters[i], out glyphs[i]!)) return null;
        var spaced = new bool[letters.Length];
        for (int i = 0, li = 0; i < text.Length; i++) if (text[i] != ' ') { spaced[li] = i > 0 && text[i - 1] == ' '; li++; }
        var ones = glyphs.Sum(g => g.Ones);
        if (ones == 0) return null;
        var limit = Math.Max(StrayBits, (int)Math.Floor(FuzzyBits * ones));
        MapNameAlignment? best = null;
        var frontier = new Dictionary<int, int>();
        var next = new Dictionary<int, int>();
        for (var b = Math.Max(Above, top); b <= Math.Min(bottom + (Rows - Above - 1), height - (Rows - Above)); b++)
            for (var x1 = Math.Max(0, firstInk - 1); x1 <= firstInk + 1 && x1 + glyphs[0].Width <= width; x1++)
            {
                var d0 = Diff(glyphs[0], ink, width, x1, b);
                if (d0 * 3 > glyphs[0].Ones || d0 > limit) continue;
                frontier.Clear();
                frontier[x1] = d0;
                for (var i = 1; i < glyphs.Length && frontier.Count > 0; i++)
                {
                    next.Clear();
                    var (lo, hi) = spaced[i] ? (MinSpaceGap, MaxSpaceGap) : (MinWordGap, MaxWordGap);
                    foreach (var (x, cost) in frontier)
                    {
                        var end = x + glyphs[i - 1].Width;
                        var gapInk = 0;
                        for (var gap = 0; gap <= hi; gap++)
                        {
                            var x2 = end + gap;
                            if (x2 + glyphs[i].Width > width) break;
                            if (gap > 0) gapInk += ColumnInk(ink, width, x2 - 1, b);
                            if (gapInk > StrayBits) break;
                            if (gap < lo) continue;
                            var d = Diff(glyphs[i], ink, width, x2, b);
                            if (d * 3 > glyphs[i].Ones || cost + gapInk + d > limit) continue;
                            if (!next.TryGetValue(x2, out var old) || cost + gapInk + d < old) next[x2] = cost + gapInk + d;
                        }
                    }
                    (frontier, next) = (next, frontier);
                }
                foreach (var (x, cost) in frontier)
                    if (best is null || cost < best.Differing) best = new MapNameAlignment(x1, x + glyphs[^1].Width - 1, b, cost, ones);
            }
        return best;
    }

    /// <summary>Ink bits in one column over the glyph rows of a baseline.</summary>
    private static int ColumnInk(bool[] ink, int width, int x, int baseline)
    {
        var n = 0;
        for (var y = 0; y < Rows; y++) if (ink[(baseline - Above + y) * width + x]) n++;
        return n;
    }

    private static int Diff(Glyph g, bool[] ink, int width, int x, int baseline)
    {
        var diff = 0;
        var bits = g.Bits;
        for (var y = 0; y < Rows; y++)
        {
            var row = (baseline - Above + y) * width + x;
            var gy = y * g.Width;
            for (var dx = 0; dx < g.Width; dx++) if (bits[gy + dx] != ink[row + dx]) diff++;
        }
        return diff;
    }

    /// <summary>Runs of columns holding ink in the rows <paramref name="top"/> to <paramref name="bottom"/>, inclusive.</summary>
    public static List<(int X0, int X1)> Groups(bool[] bits, int width, int top, int bottom)
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

    private static int Top(bool[] bits, int width, int top, int bottom, (int X0, int X1) g)
    {
        for (var y = top; y <= bottom; y++)
            for (var x = g.X0; x <= g.X1; x++)
                if (bits[y * width + x]) return y;
        return bottom + 1;
    }

    private static int Bottom(bool[] bits, int width, int top, int bottom, (int X0, int X1) g)
    {
        for (var y = bottom; y >= top; y--)
            for (var x = g.X0; x <= g.X1; x++)
                if (bits[y * width + x]) return y;
        return -1;
    }

    /// <summary>Writes the font as one JSON object (the file holds both fonts; see <see cref="MapNameFonts"/>).</summary>
    public void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("rows", Rows);
        w.WriteNumber("above", Above);
        w.WriteNumber("samples", Samples);
        w.WriteStartArray("glyphs");
        foreach (var g in _glyphs.Values.OrderBy(g => g.Letter))
        {
            w.WriteStartObject();
            w.WriteString("letter", g.Letter.ToString());
            w.WriteNumber("width", g.Width);
            w.WriteNumber("samples", g.Samples);
            w.WriteStartArray("rows");
            foreach (var row in Bitmap(g.Letter)!) w.WriteStringValue(row);
            w.WriteEndArray();
            w.WriteStartArray("votes");
            foreach (var v in g.InkVotes) w.WriteNumberValue(v);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static MapFont Read(JsonElement e, string name)
    {
        var font = new MapFont(name);
        if (e.GetProperty("rows").GetInt32() != Rows || e.GetProperty("above").GetInt32() != Above) throw new InvalidDataException($"map font {name}: another row layout");
        font.Samples = e.GetProperty("samples").GetInt32();
        if (font.Samples < 0) throw new InvalidDataException($"map font {name}: negative samples");
        foreach (var g in e.GetProperty("glyphs").EnumerateArray())
        {
            var text = g.GetProperty("letter").GetString();
            if (text is not { Length: 1 }) throw new InvalidDataException($"map font {name}: a glyph's letter is '{text}'");
            var width = g.GetProperty("width").GetInt32();
            if (width is < 1 or > MaxGlyphWidth) throw new InvalidDataException($"map font {name}: glyph '{text}' is {width} wide");
            if (font._glyphs.ContainsKey(text[0])) throw new InvalidDataException($"map font {name}: glyph '{text}' twice");
            var votes = g.GetProperty("votes");
            if (votes.GetArrayLength() != Rows * width) throw new InvalidDataException($"map font {name}: glyph '{text}' has {votes.GetArrayLength()} votes for {Rows}x{width}");
            var glyph = new Glyph { Letter = text[0], Width = width, InkVotes = votes.EnumerateArray().Select(v => v.GetInt32()).ToArray(), Samples = g.GetProperty("samples").GetInt32() };
            if (glyph.Samples < 1 || glyph.InkVotes.Any(v => v < 0 || v > glyph.Samples)) throw new InvalidDataException($"map font {name}: glyph '{text}' has a vote outside 0 to {glyph.Samples} samples");
            font._glyphs[text[0]] = glyph;
        }
        return font;
    }
}
