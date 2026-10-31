using Xunit;
using OakSoul.Capture;

namespace OakSoul.Detect.Tests;

/// <summary>
/// The tag detector's reading path with the OCR worker, driven by a recogniser of the test's own (no Vision, no recordings):
/// a box whose read is out is handed back pending and named when the read is in; the blocking mode names it at once; two
/// renderings within the fuzzy distance share one read; and the cache file brings a session's readable entries back.
/// </summary>
public sealed class TagReaderTests
{
    private const int W = 160, H = 60;

    /// <summary>A dark frame with a white bar of text pixels inside each box given (x0, y0, x1, y1), one pixel missing where asked.</summary>
    private static byte[] Frame(params (int X0, int Y0, int X1, int Y1, int SkipX)[] boxes)
    {
        var frame = new byte[W * H * 4];
        for (var o = 0; o < frame.Length; o += 4) { frame[o] = frame[o + 1] = frame[o + 2] = 20; frame[o + 3] = 255; }
        foreach (var (x0, y0, x1, y1, skip) in boxes)
            for (var y = y0 + 5; y <= y0 + 6; y++)
                for (var x = x0 + 4; x <= x1 - 4; x++)
                {
                    if (x == skip) continue;
                    var o = (y * W + x) * 4;
                    frame[o] = frame[o + 1] = frame[o + 2] = 255;
                }
        return frame;
    }

    private static TagBox Box(int x0, int y0, int x1, int y1, ulong fingerprint) => new(x0, y0, x1, y1, 0.15, fingerprint);

    private static IReadOnlyList<TextLine> Boar(byte[] bgra, int w, int h, int upscale) => [new TextLine("Lv, 25", 1, 0, 0, 20, h), new TextLine("Wild Boar", 1, 24, 0, 40, h)];

    private static void Settle(OcrQueue queue)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (queue.Pending > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public void PendingUntilTheReadIsIn_ThenNamedWithTheFramesWaited()
    {
        using var gate = new ManualResetEventSlim(false);
        using var queue = new OcrQueue((bgra, w, h, up) => { gate.Wait(); return Boar(bgra, w, h, up); }, warmUp: false);
        var tags = new TagDetector { Queue = queue };
        var frame = Frame((10, 10, 90, 25, -1));
        var viewport = new CaptureRect(0, 0, W, H);

        var boxes = new List<TagBox> { Box(10, 10, 90, 25, 1001) };
        tags.Read(boxes, frame, W, viewport, frameIndex: 1);
        Assert.True(boxes[0].Pending);
        Assert.Null(boxes[0].Text);
        Assert.Equal(1, tags.OcrCalls);
        Assert.Equal(1, tags.PendingSightings);

        // The same box on the next frame: still pending, and not read again.
        boxes = [Box(10, 10, 90, 25, 1001)];
        tags.Read(boxes, frame, W, viewport, frameIndex: 2);
        Assert.True(boxes[0].Pending);
        Assert.Equal(1, tags.OcrCalls);
        Assert.Equal(1, tags.InFlight);

        gate.Set();
        Settle(queue);
        boxes = [Box(10, 10, 90, 25, 1001)];
        tags.Read(boxes, frame, W, viewport, frameIndex: 4);
        Assert.False(boxes[0].Pending);
        Assert.Equal("Lv, 25 Wild Boar", boxes[0].Text);
        Assert.Equal(25, boxes[0].Level);
        Assert.Equal("Wild Boar", boxes[0].Name);
        Assert.Equal(3, boxes[0].NamedAfter);
        Assert.Equal(0, tags.InFlight);
        Assert.Equal([3], tags.NamedAfterFrames);
        Assert.Equal(0.0, tags.WaitedMilliseconds);
    }

    [Fact]
    public void BlockingModeNamesTheBoxInItsOwnFrame()
    {
        using var queue = new OcrQueue(Boar, warmUp: false);
        var tags = new TagDetector { Queue = queue, Blocking = true };
        var frame = Frame((10, 10, 90, 25, -1));
        var boxes = new List<TagBox> { Box(10, 10, 90, 25, 2001) };
        tags.Read(boxes, frame, W, new CaptureRect(0, 0, W, H), frameIndex: 7);
        Assert.False(boxes[0].Pending);
        Assert.Equal("Wild Boar", boxes[0].Name);
        // Read in its own frame: nothing to tell the stream about a late name.
        Assert.Null(boxes[0].NamedAfter);
        Assert.Equal(0, tags.PendingSightings);
        Assert.Equal(0, tags.InFlight);
    }

    [Fact]
    public void TwoRenderingsWithinTheFuzzyDistanceShareOneRead()
    {
        using var queue = new OcrQueue(Boar, warmUp: false);
        var tags = new TagDetector { Queue = queue };
        // The same bar in two boxes, the second missing one of its 146 text pixels: within FuzzyBits of the first.
        var frame = Frame((10, 10, 90, 25, -1), (10, 35, 90, 50, 40));
        var boxes = new List<TagBox> { Box(10, 10, 90, 25, 3001), Box(10, 35, 90, 50, 3002) };
        tags.Read(boxes, frame, W, new CaptureRect(0, 0, W, H), frameIndex: 1);
        Assert.Equal(1, tags.OcrCalls);
        Assert.True(boxes[0].Pending);
        Assert.True(boxes[1].Pending);
        Assert.Equal(2, tags.PendingSightings);
        Assert.Equal(1, tags.Entries);
    }

    [Fact]
    public void CacheFileBringsReadableEntriesBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oaksoul-tags-{Guid.NewGuid():N}.json");
        var frame = Frame((10, 10, 90, 25, -1), (10, 35, 60, 50, -1));
        var viewport = new CaptureRect(0, 0, W, H);
        try
        {
            using (var queue = new OcrQueue((bgra, w, h, up) => w > 70 ? Boar(bgra, w, h, up) : [], warmUp: false))
            {
                var tags = new TagDetector { Queue = queue, Blocking = true };
                tags.Read([Box(10, 10, 90, 25, 4001), Box(10, 35, 60, 50, 4002)], frame, W, viewport, frameIndex: 1);
                Assert.Equal(2, tags.OcrCalls);
                Assert.Equal(2, tags.Entries);
                Assert.Equal(1, tags.ReadableEntries);
                // Only the entry with a text is written: the one that read nothing is left out.
                Assert.Equal(1, tags.Save(path));
            }
            using (var queue = new OcrQueue(Boar, warmUp: false))
            {
                var tags = new TagDetector { Queue = queue };
                Assert.Equal(1, tags.Load(path));
                Assert.Equal(1, tags.Loaded);
                // The same rendering under another fingerprint (another frame's hash) is known by its bits: no read, no pending.
                var boxes = new List<TagBox> { Box(10, 10, 90, 25, 4101), Box(10, 35, 60, 50, 4102) };
                tags.Read(boxes, frame, W, viewport, frameIndex: 1);
                Assert.Equal("Lv, 25 Wild Boar", boxes[0].Text);
                Assert.False(boxes[0].Pending);
                Assert.True(boxes[0].FromCache);
                Assert.Null(boxes[0].NamedAfter);
                // The second box was never readable, so it is read again.
                Assert.True(boxes[1].Pending);
                Assert.Equal(1, tags.OcrCalls);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextOfKeepsNamesAndBadgesLeftToRight()
    {
        Assert.Equal("Lv, 25 Wild Boar", TagDetector.TextOf([new TextLine("Wild Boar", 1, 24, 0, 40, 10), new TextLine("Lv, 25", 1, 0, 0, 20, 10)]));
        Assert.Null(TagDetector.TextOf([new TextLine("eJe", 1, 0, 0, 10, 10), new TextLine("Mara", 0.3, 0, 0, 10, 10)]));
        Assert.Equal((25, "Wild Boar"), TagDetector.Parse("Lv, 25 Wild Boar"));
    }

    [Fact]
    public void QueueRunsShortReadsBeforeLongOnesAndCompletesTheRestOnDispose()
    {
        using var gate = new ManualResetEventSlim(false);
        var order = new List<int>();
        var queue = new OcrQueue((bgra, w, h, up) => { gate.Wait(); lock (order) order.Add(w); return []; }, warmUp: false);
        var first = queue.Enqueue(new byte[4 * 1 * 4], 4, 1, 1);
        var longer = queue.Enqueue(new byte[5 * 1 * 4], 5, 1, 1, OcrPriority.Long);
        var shorter = queue.Enqueue(new byte[6 * 1 * 4], 6, 1, 1);
        Assert.Equal(3, queue.MaxPending);
        gate.Set();
        Settle(queue);
        Assert.Equal([4, 6, 5], order);
        Assert.True(first.Done && longer.Done && shorter.Done);
        Assert.Equal(3, queue.Completed);
        gate.Reset();
        var stuck = queue.Enqueue(new byte[7 * 1 * 4], 7, 1, 1);
        var waiting = queue.Enqueue(new byte[8 * 1 * 4], 8, 1, 1);
        Thread.Sleep(20);
        gate.Set();
        queue.Dispose();
        Assert.True(stuck.Done);
        Assert.True(waiting.Done);
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(new byte[16], 2, 2, 1));
    }
}
