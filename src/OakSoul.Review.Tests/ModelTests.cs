using System.Text.Json;
using System.Text.Json.Nodes;
using OakSoul.Bake;
using OakSoul.Detect;
using OakSoul.Overlay;
using OakSoul.Review.Model;
using Xunit;

namespace OakSoul.Review.Tests;

public class ModelTests
{
    private static OverlayHeader Header(string? commit, string replay = "keyframes") =>
        new(OverlayHeader.CurrentSchema, "OakSoul.Watch", commit, "fx", "recordings/fx", replay, "src", 1366, 768, "LynxHope",
            SpriteMatcher.Threshold, DropMatcher.Threshold, DropMatcher.Threshold, OverlayLayers.Names, "Mara");

    [Fact]
    public void ReaderKeepsFieldsThisBuildDoesNotKnow()
    {
        const string line = """{"seq":5,"pts":1.5,"t":0.25,"map":7,"lane":{"a":1},"items":[{"layer":"entity","kind":"mob","rect":[1,2,3,4],"track":9,"foundBy":"prediction","cost":{"ms":1.2},"hints":["x","y"],"score":0.5,"age":3}]}""";
        var parsed = TolerantJson.ParseFrame(line);
        var item = Assert.Single(parsed.Frame.Items);
        Assert.Equal("prediction", item.Str("foundBy"));
        Assert.Equal("""{"ms":1.2}""", item.Str("cost"));
        Assert.Equal("""["x","y"]""", item.Str("hints"));
        Assert.Equal(0.5, item.Double("score"));
        Assert.Equal(3L, item.Long("age"));
        Assert.Equal((1, 2, 3, 4), item.Rect);
        var extra = Assert.Single(parsed.Extras!);
        Assert.Equal("lane", extra.Key);
        // What the canonical reader reads, the tolerant one reads identically.
        var known = """{"seq":5,"pts":1.5,"t":0.25,"items":[{"layer":"entity","kind":"mob","rect":[1,2,3,4],"line":[[1,2],[3,4]],"score":1.0,"track":9}]}""";
        Assert.Equal(System.Text.Encoding.UTF8.GetString(OverlayJson.Frame(OverlayJson.ParseFrame(known))),
            System.Text.Encoding.UTF8.GetString(OverlayJson.Frame(TolerantJson.ParseFrame(known).Frame)));
    }

    [Fact]
    public void ProvenanceComparesCommitAndOptions()
    {
        Assert.Equal(Freshness.Current, Provenance.Check(Header("abc"), null, "abc", false, "fx", null, null).Level);
        Assert.Equal(Freshness.Unknown, Provenance.Check(Header("abc+dirty"), null, "abc", false, "fx", null, null).Level);
        Assert.Equal(Freshness.Unknown, Provenance.Check(Header("abc"), null, "abc+dirty", false, "fx", null, null).Level);
        Assert.Equal(Freshness.Stale, Provenance.Check(Header("abc"), null, "def", false, "fx", null, null).Level);
        Assert.Equal(Freshness.Stale, Provenance.Check(Header("abc", "video"), null, "abc", false, "fx", null, null).Level);
        Assert.Equal(Freshness.Stale, Provenance.Check(Header("abc"), null, "abc", false, "fx", "Other", null).Level);
        Assert.Equal(Freshness.Current, Provenance.Check(Header("abc"), null, "abc", false, "fx", "lynxhope", "Mara").Level);
        // Commits that differ only in what cannot affect a stream (the review tool, the docs) make the same streams.
        Assert.Equal(Freshness.Current, Provenance.Check(Header("abc"), null, "def", false, "fx", null, null, (_, _) => true).Level);
        Assert.Equal(Freshness.Stale, Provenance.Check(Header("abc"), null, "def", false, "fx", null, null, (_, _) => false).Level);
        Assert.Equal(Freshness.Stale, Provenance.Check(Header("abc"), null, "def", false, "fx", null, null, (_, _) => null).Level);
    }

    [Fact]
    public void NotesAppendAndPromoteInTruthsFormat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oaksoul-review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RepoPaths { Root = dir, Reviews = Path.Combine(dir, "reviews"), Truth = Path.Combine(dir, "truth"), ReviewData = Path.Combine(dir, "data", "reviews") };
            Directory.CreateDirectory(paths.Truth);
            File.WriteAllText(Path.Combine(paths.Truth, "fx.jsonl"), """{"id":"r1-1","session":"fx","seq":1,"point":[1,1],"expect":{"none":true},"source":"Arthur, review 1, 2026-10-27"}""" + "\n");
            var store = new PacketStore(paths, "fx");
            var (name, review, isNew) = store.NextPacket();
            Assert.True(isNew);
            Assert.Equal(2, review);
            var note = store.Append(new JsonObject
            {
                ["session"] = "fx", ["seq"] = 781L, ["t"] = 26.0, ["rect"] = new JsonArray(222, 667, 50, 35), ["category"] = "wrong-box",
                ["expect"] = new NoteExpect("mob", "Dark Axe Stump", null, null).ToJson(), ["note"] = "the box cuts off its lower part",
            });
            Assert.Equal($"{name}/1", note.Id);
            Assert.True(File.Exists(Path.Combine(paths.Reviews, "fx", name + ".jsonl")));
            var again = new PacketStore(paths, "fx");
            var read = Assert.Single(again.Notes);
            Assert.Equal("the box cuts off its lower part", read.Text);
            Assert.Equal(2, read.Packet.Review);

            var truth = new TruthBook(paths, "fx");
            var entry = truth.Promotion(read, "Arthur", truth.Ids())!;
            Assert.Equal($"r2-781-dark-axe-stump", entry["id"]!.GetValue<string>());
            Assert.Equal($"Arthur, review 2, {read.Packet.Date}", entry["source"]!.GetValue<string>());
            Assert.Equal("the box cuts off its lower part", entry["note"]!.GetValue<string>());
            Assert.Equal("""{"kind":"mob","nameEndsWith":"Dark Axe Stump","covers":[[224,669],[269,699]]}""", entry["expect"]!.ToJsonString());
            truth.Append([entry]);
            Assert.Equal(2, truth.Entries.Count);
            // Bake's check reads it: here against a frame with a stump whose box covers the drawn rect.
            var frame = new OverlayFrame { Sequence = 781, Pts = 0, Time = 0, Items = [new OverlayItem(OverlayLayers.Entity, "mob").Set("rect", new[] { 220, 660, 56, 70 }).Set("name", "Dark Axe Stump")] };
            var (ok, why, _) = Truth.CheckEntry(frame, truth.Entries[1].Json);
            Assert.True(ok, why);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TimelinesFollowTheRecording()
    {
        if (!Fixture.Has()) return;
        var dir = Path.Combine(Fixture.Root!, "recordings", Fixture.Session);
        var keyframes = Timeline.Build(dir, false);
        Assert.Equal(Directory.GetFiles(Path.Combine(dir, "keyframes"), "*.png").Length, keyframes.Count);
        Assert.True(keyframes.IndexOf(781) >= 0);
        var video = Timeline.Build(dir, true);
        Assert.True(video.Count > 5000);
        Assert.All(video.Entries, e => Assert.Equal(e.Index, e.VideoFrame));
    }
}
