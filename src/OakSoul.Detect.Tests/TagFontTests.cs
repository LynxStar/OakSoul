using Xunit;

namespace OakSoul.Detect.Tests;

/// <summary>
/// The tag font on a font of the test's own: learned from two names, it renders a third from their letters, finds it in a box
/// behind a level badge and alone, leaves a box with other text before the name alone, and survives its JSON.
/// </summary>
public sealed class TagFontTests
{
    // Four letters, 5 rows tall on the baseline (g reaches 2 rows below it), with gaps of 1 (a), 2 (b), 1 (c) and 1 (g) after them.
    private static readonly Dictionary<char, (string[] Rows, int Gap, int Descent)> Letters = new()
    {
        ['a'] = (["###", "..#", "###", "#.#", "###"], 1, 0),
        ['b'] = (["#.", "#.", "##", "#.", "##"], 2, 0),
        ['c'] = (["###", "#..", "#..", "#..", "###"], 1, 0),
        ['g'] = (["###", "#.#", "###", "..#", "###"], 1, 2),
    };
    private const int Space = 3;

    /// <summary>
    /// A box of the given size with a name drawn on a baseline row, starting at a column; a badge or other text left of it when asked;
    /// letters drawn as other letters' bitmaps or with other gaps when asked (a sample that disagrees with the font).
    /// </summary>
    private static bool[] Box(int width, int height, string text, int x, int baseline, bool badge = false, bool other = false, bool border = false,
        Dictionary<char, char>? drawAs = null, Dictionary<char, int>? gaps = null)
    {
        var bits = new bool[width * height];
        void Draw(string[] rows, int gx, int bottom)
        {
            for (var y = 0; y < rows.Length; y++)
                for (var dx = 0; dx < rows[y].Length; dx++)
                    if (rows[y][dx] == '#') bits[(bottom - rows.Length + 1 + y) * width + gx + dx] = true;
        }
        var cx = x;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ') { cx += Space; continue; }
            var letter = drawAs is not null && drawAs.TryGetValue(text[i], out var instead) ? instead : text[i];
            var (rows, gap, descent) = Letters[letter];
            Draw(rows, cx, baseline + descent);
            cx += rows[0].Length + (gaps is not null && gaps.TryGetValue(text[i], out var g) ? g : gap);
        }
        // A badge: two small digits three rows above the baseline, ending two columns before the name, and its frame along the box's edges;
        // with a border, the frame's right edge runs down to the baseline two columns before the name, as the client's does.
        if (badge)
        {
            Draw(["##", "#.", "##"], x - 8, baseline - 3);
            Draw(["##", ".#", "##"], x - 5, baseline - 3);
            for (var c = 0; c < x - 2; c++) { bits[c] = true; bits[(height - 1) * width + c] = true; }
            if (border) for (var y = 1; y <= baseline; y++) bits[y * width + x - 2] = true;
        }
        // Other text: a letter standing on the same baseline right before the name, as a neighbouring tag's would.
        if (other) Draw(Letters['c'].Rows, x - 5, baseline);
        return bits;
    }

    private static TagFont Learned()
    {
        // Every letter is followed by another inside a word once, so every gap is seen; c stands before a space once, so the space is.
        var font = new TagFont();
        Assert.True(font.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "ab"));
        Assert.True(font.Learn(Box(40, 16, "bca", 4, 13), 40, 16, "bca"));
        Assert.True(font.Learn(Box(40, 16, "c ab", 6, 12), 40, 16, "c ab"));
        return font;
    }

    [Fact]
    public void LearnsLettersGapsAndTheSpace_AndRendersANewName()
    {
        var font = Learned();
        Assert.Equal(3, font.Samples);
        Assert.Equal("abc", string.Concat(font.Letters.Order()));
        Assert.Equal(3, font.LettersWithGap);
        Assert.Equal(Space, font.SpaceColumns);
        var r = font.Render("ba cb");
        Assert.NotNull(r);
        // b(2)+gap 2, a(3)+gap 1+space 3, c(3)+gap 1, b(2): 2+2+3+1+3+3+1+2 = 17 columns.
        Assert.Equal(17, r.Width);
        var expected = Box(17, TagFont.Rows, "ba cb", 0, TagFont.Above);
        Assert.Equal(expected, r.Bits);
    }

    [Fact]
    public void ALetterSeenOnlyLastRendersOnlyLast()
    {
        var font = new TagFont();
        Assert.True(font.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "ab"));
        Assert.Null(font.Render("ba"));
        Assert.NotNull(font.Render("ab"));
        Assert.Null(font.Render("ac"));
    }

    [Fact]
    public void FindsAKnownNameBehindItsBadgeAloneAndNotBesideOtherText()
    {
        var font = Learned();
        var renderings = new List<(string, int?, TagRendering)> { ("cab", 7, font.Render("cab")!), ("ba", null, font.Render("ba")!) };
        var species = TagFont.Match(renderings, Box(60, 16, "cab", 20, 13, badge: true), 60, 16);
        Assert.NotNull(species);
        Assert.Equal(("Lv. 7 cab", 7, "cab", 0), (species.Text, species.Level, species.Name, species.Differing));
        var cut = TagFont.Match(renderings, Box(60, 16, "cab", 20, 13), 60, 16);
        Assert.NotNull(cut);
        Assert.Equal(("cab", (int?)null), (cut.Text, cut.Level));
        Assert.Null(TagFont.Match(renderings, Box(60, 16, "cab", 20, 13, other: true), 60, 16));
        var pet = TagFont.Match(renderings, Box(40, 15, "ba", 10, 11), 40, 15);
        Assert.NotNull(pet);
        Assert.Equal("ba", pet.Text);
        // The pet's name never stands behind a badge.
        Assert.Null(TagFont.Match(renderings, Box(60, 16, "ba", 20, 13, badge: true), 60, 16));
    }

    [Fact]
    public void LearnsBehindABadge_ARepeatedLetterIncluded()
    {
        var font = new TagFont();
        Assert.True(font.Learn(Box(60, 16, "aba", 20, 13, badge: true, border: true), 60, 16, "aba"));
        Assert.Equal(2, font.Letters.Count);
        var r = font.Render("aba");
        Assert.NotNull(r);
        Assert.Equal(Box(r.Width, TagFont.Rows, "aba", 0, TagFont.Above), r.Bits);
    }

    [Fact]
    public void ASampleWhoseLetterWidthsDisagreeIsRefused()
    {
        // a then b, three and two wide, labelled as two a's: refused, and nothing learned.
        var bad = new TagFont();
        Assert.False(bad.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "aa"));
        Assert.Equal((0, 1, 1), (bad.Samples, bad.Rejected, bad.Conflicts));
        Assert.Empty(bad.Letters);
        // A width the font already knows for a letter is binding on later samples.
        var known = new TagFont();
        Assert.True(known.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "ab"));
        Assert.False(known.Learn(Box(40, 16, "cb", 4, 13, drawAs: new() { ['c'] = 'b' }), 40, 16, "ab"));
        Assert.Equal((1, 1, 1), (known.Samples, known.Rejected, known.Conflicts));
    }

    [Fact]
    public void DescendersKeepTheBaseline()
    {
        var font = new TagFont();
        Assert.True(font.Learn(Box(40, 16, "gag", 4, 12), 40, 16, "gag"));
        Assert.True(font.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "ab"));
        var r = font.Render("gab");
        Assert.NotNull(r);
        Assert.Equal(Box(r.Width, TagFont.Rows, "gab", 0, TagFont.Above), r.Bits);
        // Found in a box too, at a baseline that keeps the descender inside it.
        var m = TagFont.Match([("gab", 3, r)], Box(50, 16, "gab", 12, 12, badge: true, border: true), 50, 16);
        Assert.NotNull(m);
        Assert.Equal("Lv. 3 gab", m.Text);
    }

    [Fact]
    public void TiedVotesResolveTheSameLiveAndAfterALoad()
    {
        var font = new TagFont();
        Assert.True(font.Learn(Box(40, 16, "ab", 4, 13, gaps: new() { ['a'] = 2 }), 40, 16, "ab"));
        Assert.True(font.Learn(Box(40, 16, "ab", 4, 13), 40, 16, "ab"));
        var live = font.Render("ab")!;
        var loaded = TagFont.FromJson(font.ToJson()).Render("ab")!;
        // One vote each for a gap of 2 and of 1: the smaller wins, before and after (a 3 wide, the gap, b 2 wide).
        Assert.Equal(6, live.Width);
        Assert.Equal(live.Bits, loaded.Bits);
    }

    [Fact]
    public void JsonRoundTripKeepsTheFont()
    {
        var font = Learned();
        var again = TagFont.FromJson(font.ToJson());
        Assert.Equal(font.Samples, again.Samples);
        Assert.Equal(font.Render("ba cb")!.Bits, again.Render("ba cb")!.Bits);
        Assert.Equal(font.SpaceColumns, again.SpaceColumns);
    }
}
