using OakSoul.Overlay;
using Xunit;

namespace OakSoul.Review.Tests;

/// <summary>The stream comparer on frames of the test's own: each verdict once, and the frames only one stream has.</summary>
public sealed class OverlayDiffTests
{
    private static OverlayFrame Frame(long seq, string? detect, string? skip, params OverlayItem[] items) =>
        new() { Sequence = seq, Pts = seq / 30.0, Time = seq / 30.0, Detect = detect, Skip = skip, Items = items.ToList() };

    private static OverlayItem Mob(int x, int y, string? name = null, string kind = "mob") =>
        new OverlayItem(OverlayLayers.Entity, kind).Set("feet", new[] { x, y }).Set("track", x).Set("name", name);

    private static OverlayItem Tag(int x, int y, string? text, bool pending = false) =>
        new OverlayItem(OverlayLayers.Tags, "tag").Set("rect", new[] { x, y, 60, 16 }).Set("text", text).Set("pending", pending ? true : null);

    [Fact]
    public void ClassifiesEachKindOfDifference()
    {
        var a = new List<OverlayFrame>
        {
            Frame(1, "ok", null, Mob(100, 200, "Wild Boar"), Tag(70, 210, "Lv. 25 Wild Boar")),              // identical
            Frame(2, "ok", null, Mob(100, 200, "Wild Boar"), Tag(70, 210, "Lv. 25 Wild Boar")),              // pending only in B
            Frame(3, "ok", null, Mob(100, 200, "Wild Boar"), Tag(70, 210, "Lv. 25 Wild Boar")),              // named only in A
            Frame(4, "ok", null, Mob(100, 200, "Wild Boar")),                                                 // B: another kind
            Frame(5, "ok", null, Mob(100, 200, "Wild Boar"), Mob(300, 200, "Red Snail")),                    // B: one moved a pixel
            Frame(6, "ok", null, Mob(100, 200, "Wild Boar")),                                                 // B: detector skipped
            Frame(7, "ok", null, Mob(100, 200, "Wild Boar")),                                                 // only in A
            Frame(8, "ok", null, Mob(100, 200)),                                                              // a fill in B pairs with nothing
        };
        var b = new List<OverlayFrame>
        {
            Frame(1, "ok", null, Mob(100, 200, "Wild Boar"), Tag(70, 210, "Lv. 25 Wild Boar")),
            Frame(2, "ok", null, Mob(100, 200, "Wild Boar"), Tag(70, 210, null, pending: true)),
            Frame(3, "ok", null, Mob(100, 200), Tag(70, 210, "Lv. 25 Wild Boar")),
            Frame(4, "ok", null, Mob(100, 200, null, kind: "unknown")),
            Frame(5, "ok", null, Mob(100, 200, "Wild Boar"), Mob(301, 200, "Red Snail")),
            Frame(6, null, "no map"),
            new() { Sequence = 8, Pts = 0, Time = 0, Filled = true, Detect = "ok", Items = [Mob(100, 200)] },
            Frame(9, "ok", null, Mob(100, 200)),                                                              // only in B
        };
        var r = OverlayDiff.Compare(a, b, bucket: 4);
        Assert.Equal((8, 7, 6), (r.FramesInA, r.FramesInB, r.FramesInBoth));
        Assert.Equal(1, r[OverlayDiff.Verdict.Identical]);
        Assert.Equal(1, r[OverlayDiff.Verdict.PendingOnly]);
        Assert.Equal(1, r[OverlayDiff.Verdict.NamesOnly]);
        Assert.Equal(1, r[OverlayDiff.Verdict.DetectorDiffers]);
        Assert.Equal(2, r[OverlayDiff.Verdict.Other]);
        Assert.Equal((1, 1), (r.OtherSameCounts, r.OtherKindCounts));
        Assert.Equal((0, 1, 0, 0), (r.Moved0, r.Moved1, r.Moved2To3, r.Unmatched));
        Assert.Equal(1, r.DetectorReasons["A ran / B no map"]);
        Assert.Equal(1, r.LateNames["Wild Boar (A)"]);
        Assert.Equal((1, 8, 8), r.RanA!.Value);
        Assert.Equal((1, 9, 6), r.RanB!.Value);
        // Buckets of four sequences: 0..3 and 4..7.
        Assert.Equal([1, 1, 1, 0, 0], r.Buckets[0]);
        Assert.Equal([0, 0, 0, 1, 2], r.Buckets[1]);
        var text = new StringWriter();
        r.Print(text);
        Assert.Contains("identical 1, a tag's text pending on one side only 1, an entity named on one side only 1, the detector ran on one side only 1, other 2", text.ToString());
    }
}
