using OakSoul.Vision.Readers;
using Xunit;

namespace OakSoul.Detect.Tests;

/// <summary>
/// The map-name fonts and the name-block cache on a font of the test's own: letters learned from clean lines align a new name with
/// other gaps, merged letters are cut where the stricter threshold is blank (and not when the cut is ambiguous), a prefix is not
/// taken for a longer name nor a longer name for a shorter line, a glyph cannot hide in a shorter name's gap, a line seen before is
/// named from the cache, the cache never contradicts the fonts, the bar's one line is "street : map", and the file round-trips.
/// </summary>
public sealed class MapFontTests
{
    // Letters 5 rows tall on the baseline (g reaches 2 rows below); a has a faint fourth column (ink at the panel's threshold, blank at the
    // split one). They are small next to the client's (a real I is 18 bits), so the stray and gap allowances of 8 bits weigh more here.
    private static readonly Dictionary<char, (string[] Rows, int Descent)> Letters = new()
    {
        ['a'] = (["###.", "..#.", "###.", "#.#.", "###f"], 0),
        ['b'] = (["##.", "#.#", "##.", "#.#", "##."], 0),
        ['c'] = (["###", "#..", "#..", "#..", "###"], 0),
        ['g'] = (["###", "#.#", "###", "..#", "###"], 2),
        [':'] = (["#", ".", ".", ".", "#"], 0),
        // d's middle column is faint throughout: blank at the split threshold, so a cut candidate inside a letter.
        ['d'] = (["#f#", "#f#", "#f#", "#f#", "#f#"], 0),
        // m is wide and heavy (14 bits) and not periodic, as the real letters are next to the 8 stray bits allowed.
        ['m'] = (["#####", "#...#", "#.#.#", "#...#", "#...#"], 0),
    };

    private const int W = 60, H = 44;

    /// <summary>A panel crop (luma 182 background, white ink, a faint pixel at 200) with lines of text, each letter followed by its gap; a bar crop (grey 221, black ink) when asked.</summary>
    private static byte[] Crop(MapNameForm form, params (string Text, int X, int Baseline, int[] Gaps)[] lines)
    {
        var px = new byte[W * H * 4];
        byte bg = form == MapNameForm.Panel ? (byte)182 : (byte)221, ink = form == MapNameForm.Panel ? (byte)255 : (byte)0, faint = form == MapNameForm.Panel ? (byte)200 : (byte)0;
        for (var p = 0; p < W * H; p++) { px[p * 4] = px[p * 4 + 1] = px[p * 4 + 2] = bg; px[p * 4 + 3] = 255; }
        void Set(int x, int y, byte v) { var o = (y * W + x) * 4; px[o] = px[o + 1] = px[o + 2] = v; }
        foreach (var (text, x0, baseline, gaps) in lines)
        {
            int cx = x0, li = 0;
            foreach (var ch in text)
            {
                if (ch == ' ') { cx += 3; continue; }
                var (rows, descent) = Letters[ch];
                for (var y = 0; y < rows.Length; y++)
                    for (var dx = 0; dx < rows[y].Length; dx++)
                        if (rows[y][dx] == '#') Set(cx + dx, baseline + descent - rows.Length + 1 + y, ink);
                        else if (rows[y][dx] == 'f') Set(cx + dx, baseline + descent - rows.Length + 1 + y, faint);
                cx += rows[0].Length + (li < gaps.Length ? gaps[li] : 1);
                li++;
            }
        }
        return px;
    }

    private static MapNameFonts Fonts(params (string Street, string Map)[] names)
    {
        var fonts = new MapNameFonts();
        fonts.SetNames(names);
        return fonts;
    }

    [Fact]
    public void LearnsFromCleanLines_AndAlignsANewNameWithOtherGaps()
    {
        var fonts = Fonts(("ab", "bca"), ("ab", "ca b"), ("cb", "ba"));
        // Two clean lines teach a, b and c (gaps of 1 and 2).
        Assert.True(fonts.Learn(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("bca", 5, 32, [2, 1])), W, H, MapNameForm.Panel, "ab", "bca"));
        Assert.Equal("abc", string.Concat(fonts.Panel.Letters.Order()));
        Assert.Equal(2, fonts.Panel.Samples);
        Assert.Equal((2, 2), (fonts.LinesCached, fonts.LinesTaught));
        // A block never seen: the region line is cached, the map line "ca b" is new and aligns with gaps of 2 and a space of 3 + 1.
        var lines = fonts.Recognise(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("ca b", 5, 32, [2, 1, 0])), W, H, MapNameForm.Panel);
        Assert.NotNull(lines);
        Assert.Equal(["ab", "ca b"], lines.Select(l => l.Text));
        Assert.Equal(MapNameSource.Font, fonts.LastSource);
        Assert.Equal((5, 28, 5), (lines[1].X, lines[1].Y, lines[1].Height));
        // The same block again: both lines cached now? No: recognition caches nothing; only confirmed reads do.
        Assert.Equal(2, fonts.Lines);
    }

    [Fact]
    public void MergedLettersAreCutWhereTheSplitThresholdIsBlank_NotWhenAmbiguous()
    {
        var fonts = Fonts(("ab", "ab"), ("ab", "bab"));
        // "ab" with a's faint column touching b: one group at the ink threshold, cut at the faint column; the only cut, so taken although nothing is known yet.
        Assert.True(fonts.Learn(Crop(MapNameForm.Panel, ("ab", 5, 18, [0])), W, H, MapNameForm.Panel, "", "ab"));
        Assert.Equal(["a", "b"], fonts.Panel.Letters.Order().Select(c => c.ToString()));
        Assert.Equal(["###.", "..#.", "###.", "#.#.", "####"], fonts.Panel.Bitmap('a')!.Skip(5).Take(5));
        // "dab" with a touching b: two groups (d, ab) for three letters, one cut needed, two candidates (d's faint middle column, a's faint
        // last column). A font that knows no widths cannot choose: refused as ambiguous, nothing learned.
        var naive = Fonts(("ab", "dab"));
        Assert.True(naive.Learn(Crop(MapNameForm.Panel, ("dab", 5, 18, [1, 0])), W, H, MapNameForm.Panel, "", "dab")); // the cache takes the line
        Assert.Equal((0, 1, 1, 1), (naive.Panel.Samples, naive.Panel.Rejected, naive.Panel.Ambiguous, naive.Lines));
        // Knowing d's width from a clean line, the cut inside d is out, and the line is taken.
        var knowing = Fonts(("ab", "db"), ("ab", "dab"));
        Assert.True(knowing.Learn(Crop(MapNameForm.Panel, ("db", 5, 18, [1])), W, H, MapNameForm.Panel, "", "db"));
        Assert.True(knowing.Learn(Crop(MapNameForm.Panel, ("dab", 5, 18, [1, 0])), W, H, MapNameForm.Panel, "", "dab"));
        Assert.Equal((2, 0), (knowing.Panel.Samples, knowing.Panel.Ambiguous));
        Assert.Equal(["###.", "..#.", "###.", "#.#.", "####"], knowing.Panel.Bitmap('a')!.Skip(5).Take(5));
    }

    [Fact]
    public void APrefixIsNotTakenForALongerName_NorALongerNameForAShorterLine()
    {
        var fonts = Fonts(("ab", "ca"), ("ab", "cag"));
        Assert.True(fonts.Learn(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cag", 5, 32, [1, 1])), W, H, MapNameForm.Panel, "ab", "cag"));
        // The line "ca": "cag" cannot fit (its g is wholly wrong), "ca" fits with nothing to its right.
        var shorter = fonts.Recognise(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("ca", 5, 32, [1])), W, H, MapNameForm.Panel);
        Assert.NotNull(shorter);
        Assert.Equal("ca", shorter[1].Text);
        // The line "cag" with "cag" out of the catalog: "ca" would leave the g (12 bits, more than the strays allowed) to its right, so
        // nothing is named and the block is read.
        var only = Fonts(("ab", "ca"));
        Assert.True(only.Learn(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("ca", 5, 32, [1])), W, H, MapNameForm.Panel, "ab", "ca"));
        Assert.False(only.Learn(Crop(MapNameForm.Panel, ("cag", 5, 32, [1, 1])), W, H, MapNameForm.Panel, "", "cag")); // not a catalog name
        Assert.Null(only.Recognise(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cag", 5, 32, [1, 1])), W, H, MapNameForm.Panel));
    }

    [Fact]
    public void AGlyphCannotHideInAShorterNamesGap()
    {
        // "bmb" drawn with gaps of 1: "b b" could put its second b over part of the m with the rest of the m in its gap, if gap columns
        // were free. The ink in the gap counts and more than the stray bits allowed ends the candidate; what the b covers must match it.
        var fonts = Fonts(("", "b b"), ("", "bmb"));
        Assert.True(fonts.Learn(Crop(MapNameForm.Panel, ("bmb", 5, 18, [2, 1])), W, H, MapNameForm.Panel, "", "bmb"));
        var lines = fonts.Recognise(Crop(MapNameForm.Panel, ("bmb", 5, 18, [1, 1])), W, H, MapNameForm.Panel);
        Assert.Equal("bmb", Assert.Single(lines!).Text);
        Assert.Equal(MapNameSource.Font, fonts.LastSource);
        var only = Fonts(("", "b b"), ("", "mb"));
        Assert.True(only.Learn(Crop(MapNameForm.Panel, ("mb", 5, 18, [1])), W, H, MapNameForm.Panel, "", "mb"));
        Assert.True(only.Learn(Crop(MapNameForm.Panel, ("b b", 5, 18, [1, 1])), W, H, MapNameForm.Panel, "", "b b"));
        Assert.Null(only.Recognise(Crop(MapNameForm.Panel, ("bmb", 5, 18, [1, 1])), W, H, MapNameForm.Panel));
    }

    [Fact]
    public void TheCacheNeverContradictsTheFonts()
    {
        // A read that was wrong yet a real name: the block shows "cag", the read says "ca". With the font unable to spell "ca" the line
        // is cached under the wrong text (the font itself refuses it: three groups for two letters).
        var fonts = Fonts(("ab", "ca"), ("ab", "cag"));
        var wrong = Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cag", 5, 32, [1, 1]));
        Assert.True(fonts.Learn(wrong, W, H, MapNameForm.Panel, "ab", "ca"));
        Assert.Equal((2, 1), (fonts.Lines, fonts.Panel.Samples));
        Assert.Equal("ca", fonts.Recognise(wrong, W, H, MapNameForm.Panel)![1].Text);
        // Once the font can spell "ca" (c and g learned from a right read of the same block), the hit is checked, refuted and dropped,
        // and the font names the block right.
        Assert.True(fonts.Learn(wrong, W, H, MapNameForm.Panel, "ab", "cag"));
        var lines = fonts.Recognise(wrong, W, H, MapNameForm.Panel);
        Assert.Equal("cag", lines![1].Text);
        Assert.Equal((1, 2, MapNameSource.Font), (fonts.CacheRefuted, fonts.Lines, fonts.LastSource));
        // And a wrong read of a block the font can already spell is refused outright for that line: nothing cached, nothing taught
        // from it (the region line, right as ever, still teaches).
        var (before, samples) = (fonts.Lines, fonts.Panel.Samples);
        Assert.True(fonts.Learn(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cag", 5, 32, [2, 2])), W, H, MapNameForm.Panel, "ab", "ca"));
        Assert.Equal((before, samples + 1, 1), (fonts.Lines, fonts.Panel.Samples, fonts.Contradicted));
    }

    [Fact]
    public void ALineSeenBeforeIsNamedFromTheCache_BeforeTheFont()
    {
        var fonts = Fonts(("ab", "cab"), ("ab", "bca"));
        var block = Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cab", 5, 32, [1, 1]));
        Assert.True(fonts.Learn(block, W, H, MapNameForm.Panel, "AB", "Cab")); // the read's case does not matter: the catalog's spelling is kept
        var same = fonts.Recognise(block, W, H, MapNameForm.Panel);
        Assert.NotNull(same);
        Assert.Equal(["ab", "cab"], same.Select(l => l.Text));
        Assert.Equal(MapNameSource.Cache, fonts.LastSource);
        var other = fonts.Recognise(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("bca", 5, 32, [1, 1])), W, H, MapNameForm.Panel);
        Assert.NotNull(other);
        Assert.Equal("bca", other[1].Text);
        Assert.Equal(MapNameSource.Font, fonts.LastSource);
        Assert.Equal((1, 1), (fonts.ByCache, fonts.ByFont));
    }

    [Fact]
    public void TheBarsLineIsStreetColonMap_AndEndsBeforeWhatFollows()
    {
        var fonts = Fonts(("ab", "ca"), ("ab", "cab"), ("cb", "ca"));
        // Learned from a bar crop with button captions far to the right of the name (more than BarEndBlank blank columns on).
        var crop = Crop(MapNameForm.Bar, ("ab : ca", 3, 10, [1, 1, 1, 1]), ("cb", 40, 10, [1]));
        Assert.True(fonts.Learn(crop, W, H, MapNameForm.Bar, "ab", "ca"));
        Assert.Equal("ab : ca", Assert.Single(fonts.Recognise(crop, W, H, MapNameForm.Bar)!).Text);
        Assert.Equal(MapNameSource.Cache, fonts.LastSource);
        // Another pair of the same letters, by the font; the captions after it do not count against it.
        var lines = fonts.Recognise(Crop(MapNameForm.Bar, ("cb : ca", 3, 10, [1, 1, 1, 1]), ("ab", 40, 10, [1])), W, H, MapNameForm.Bar);
        Assert.Equal("cb : ca", Assert.Single(lines!).Text);
        Assert.Equal(MapNameSource.Font, fonts.LastSource);
        // The bar font is apart from the panel's.
        Assert.Empty(fonts.Panel.Letters);
        Assert.Equal(":abc", string.Concat(fonts.Bar.Letters.Order()));
    }

    [Fact]
    public void JsonRoundTripKeepsFontsAndCache()
    {
        var fonts = Fonts(("ab", "cab"), ("ab", "bca"), ("cb", "ga"));
        var block = Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("cab", 5, 32, [1, 1]));
        Assert.True(fonts.Learn(block, W, H, MapNameForm.Panel, "ab", "cab"));
        Assert.True(fonts.Learn(Crop(MapNameForm.Bar, ("cb : ga", 3, 10, [1, 1, 1, 1])), W, H, MapNameForm.Bar, "cb", "ga"));
        var again = MapNameFonts.FromJson(fonts.ToJson());
        again.SetNames([("ab", "cab"), ("ab", "bca"), ("cb", "ga")]);
        Assert.Equal((fonts.Panel.Samples, fonts.Bar.Samples, fonts.Lines), (again.Panel.Samples, again.Bar.Samples, again.Lines));
        Assert.NotNull(again.Panel.Bitmap('a'));
        Assert.NotNull(again.Bar.Bitmap('g'));
        Assert.Equal(fonts.Panel.Bitmap('a'), again.Panel.Bitmap('a'));
        Assert.Equal(fonts.Bar.Bitmap('g'), again.Bar.Bitmap('g'));
        Assert.Equal(MapNameSource.Cache, (again.Recognise(block, W, H, MapNameForm.Panel), again.LastSource).LastSource);
        Assert.Equal("bca", again.Recognise(Crop(MapNameForm.Panel, ("ab", 5, 18, [1]), ("bca", 5, 32, [2, 2])), W, H, MapNameForm.Panel)![1].Text);
        Assert.False(again.Changed);
        // A catalog without "cab" drops that cached line when the names are set.
        var stale = MapNameFonts.FromJson(fonts.ToJson());
        stale.SetNames([("ab", "bca"), ("cb", "ga")]);
        Assert.Equal((2, 1, true), (stale.Lines, stale.LinesDropped, stale.Changed));
    }
}
