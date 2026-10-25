using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// Builds the background mosaic of every map a replayed session visited, offline, from the frames whose camera the
/// world tracker measured (dot and own tag). Four replays: vote with each frame registered against the votes so far,
/// count agreement, then once more registered against that first mosaic's reliable pixels, and count again. The
/// second round fixes frames the incremental registration got wrong early on, when the votes so far were thin.
/// </summary>
public static class BackgroundBuild
{
    /// <summary>One usable frame: its map, the tracker's camera, whether that camera is exact, and what to mask.</summary>
    public sealed record FrameInfo(MapData Map, PointD Camera, bool Exact, CaptureRect Panel, CaptureRect Tag);

    public static async Task Run(TextWriter o, Func<IFrameSource> replay, IReadOnlyDictionary<long, FrameInfo> frames, ScreenMask mask, string dir)
    {
        var builders = frames.Values.Select(f => f.Map).DistinctBy(m => m.MapId).ToDictionary(m => m.MapId, m => new MosaicBuilder(m.MapId, m.Vr));
        var refined = new Dictionary<long, (int X, int Y)>();
        Dictionary<int, BackgroundMosaic>? first = null;
        for (var round = 0; round < 2; round++)
        {
            refined.Clear();
            await Pass(replay, frames, mask, (seq, f, px, w) =>
            {
                var reference = first?.GetValueOrDefault(f.Map.MapId);
                if (builders[f.Map.MapId].Vote(px, w, f.Camera, mask, f.Exact, reference) is { } cam) refined[seq] = cam;
            });
            await Pass(replay, frames, mask, (seq, f, px, w) =>
            {
                if (refined.TryGetValue(seq, out var cam)) builders[f.Map.MapId].Count(px, w, cam.X, cam.Y, mask);
            });
            if (round == 0)
            {
                first = builders.ToDictionary(b => b.Key, b => b.Value.Build());
                foreach (var b in builders.Values) b.StartOver();
            }
        }
        foreach (var (mapId, builder) in builders)
        {
            var mosaic = builder.Build();
            mosaic.Save(dir);
            BackgroundReport.Print(o, builder, mosaic, dir);
        }
    }

    private delegate void FrameAction(long sequence, FrameInfo info, ReadOnlySpan<byte> pixels, int width);

    private static async Task Pass(Func<IFrameSource> replay, IReadOnlyDictionary<long, FrameInfo> frames, ScreenMask mask, FrameAction action)
    {
        using var source = replay();
        await foreach (var frame in source.Frames())
            using (frame)
                if (frames.TryGetValue(frame.Sequence, out var f))
                {
                    mask.Reset();
                    mask.Exclude(f.Panel, 4);
                    mask.Exclude(OwnCharacter.Box(f.Tag, OwnCharacter.MosaicPad));
                    action(frame.Sequence, f, frame.Pixels, frame.Width);
                }
    }
}
