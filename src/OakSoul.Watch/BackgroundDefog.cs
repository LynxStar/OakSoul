using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// The third round of a mosaic build for a map whose info/cloud is 1. Clouds drift across such a map all the time, and where
/// they pass most (near the left bound of Deep Valley I, where every cloud comes back in) a fogged value can outvote the
/// background: 16% of the wall at frames 1171 to 1261 was brighter in the mosaic than in those clear frames. So the replay
/// is run once more with the clouds followed against the mosaic of the first two rounds (<see cref="CloudLayer"/>, finding
/// every whitened region it can, without the detector's budget); each cloud seen often enough gets its line over the whole
/// replay (the set is fixed for a visit, so the line also holds before it was first found), and then the votes, the
/// agreement count and the keyframes' exact values are taken again with every pixel a cloud's canvas covers left out.
/// </summary>
public static class BackgroundDefog
{
    /// <summary>
    /// Sightings a cloud needs before its line over the whole replay is used: two. In the frames where the clouds were followed
    /// their own places are left out (the line of a cloud seen for a minute can be a pixel off at its ends), the lines only before
    /// and after; and every canvas is widened by a column either side, since a pixel off leaves its faint edge voting. With the
    /// lines alone, 3.9% of the wall at frames 1171 to 1261 stayed brighter than those clear frames; with both, 2.0%; with a
    /// cloud found again followed as a stretch of its own (<see cref="CloudLayer.DiscoverEverywhere"/>), none.
    /// </summary>
    public const int MinObservations = 2;

    public static async Task<MosaicBuilder> Run(TextWriter o, Func<IFrameSource> replay, IReadOnlyDictionary<long, BackgroundBuild.FrameInfo> frames, ScreenMask mask,
        IReadOnlyDictionary<long, (int X, int Y)> refined, BackgroundMosaic mosaic, MapData map, CloudArt art, MosaicBuilder builder,
        IReadOnlyList<(long VideoSequence, string Path)>? keyframes)
    {
        var layer = new CloudLayer(art, map.Vr.Left, map.Vr.Right) { DiscoverEverywhere = true };
        var fg = new ForegroundExtractor();
        var times = new Dictionary<long, double>();
        var followed = new Dictionary<long, List<(Cloud Cloud, int X, int Y)>>();
        // 1. Follow the clouds over the replay.
        await Pass(replay, frames, mask, map.MapId, (seq, f, frame) =>
        {
            if (!refined.TryGetValue(seq, out var cam)) return;
            times[seq] = frame.Pts;
            fg.Difference(frame.Pixels, frame.Width, mask, mosaic, cam.X, cam.Y);
            layer.Update(frame.Pixels, frame.Width, mask, mosaic, cam.X, cam.Y, frame.Pts, fg.Classes);
            followed[seq] = layer.Placed.Select(c => (c, c.Left + cam.X, c.Top + cam.Y)).ToList();
        });
        var set = layer.Clouds.Where(c => c.HasVelocity && c.Observations >= MinObservations).ToList();
        o.WriteLine($"background {map.MapId}: clouds followed: {set.Count} seen at least {MinObservations} times ({layer.Discovered} found in {layer.DiscoveryAttempts} discoveries): " +
                    string.Join(", ", set.Select(c => $"canvas {c.Sprite.Index} alpha {c.Alpha} v {c.VelocityX:0.0},{c.VelocityY:0.00}")));
        // 2. Vote and count again without the pixels under them.
        builder.StartOver();
        var defogged = new Dictionary<long, (int X, int Y)>();
        long excluded = 0;
        await Pass(replay, frames, mask, map.MapId, (seq, f, frame) =>
        {
            if (!refined.TryGetValue(seq, out var cam) || !times.TryGetValue(seq, out var t)) return;
            excluded += MaskClouds(mask, layer, followed.GetValueOrDefault(seq), t, cam);
            if (builder.Vote(frame.Pixels, frame.Width, f.Camera, mask, f.Exact, mosaic) is { } c) defogged[seq] = c;
        });
        await Pass(replay, frames, mask, map.MapId, (seq, f, frame) =>
        {
            if (!defogged.TryGetValue(seq, out var cam) || !times.TryGetValue(seq, out var t)) return;
            MaskClouds(mask, layer, followed.GetValueOrDefault(seq), t, cam);
            builder.Count(frame.Pixels, frame.Width, cam.X, cam.Y, mask);
        });
        if (keyframes is { Count: > 0 })
        {
            var k = 0; long p = 0;
            foreach (var (seq, path) in keyframes)
            {
                if (!frames.TryGetValue(seq, out var f) || f.Map.MapId != map.MapId || !defogged.TryGetValue(seq, out var cam) || !times.TryGetValue(seq, out var t)) continue;
                var (px, w, _) = Png.ReadBgra(path);
                mask.Reset();
                mask.Exclude(f.Panel, 4);
                mask.Exclude(OwnCharacter.Box(f.Tag, OwnCharacter.MosaicPad));
                MaskClouds(mask, layer, followed.GetValueOrDefault(seq), t, cam);
                p += builder.ExactRefine(px, w, cam.X, cam.Y, mask);
                k++;
            }
            o.WriteLine($"background {map.MapId}: {k} lossless keyframes replaced {p:N0} voted video pixels with their exact values (clouds left out)");
        }
        o.WriteLine($"background {map.MapId}: clouds left out of {excluded:N0} pixel votes over {defogged.Count} frames ({builder.Rejected} frames rejected)");
        return builder;
    }

    /// <summary>Marks every pixel a cloud's canvas covers at <paramref name="time"/> as excluded; returns how many it added.</summary>
    private static long MaskClouds(ScreenMask mask, CloudLayer layer, List<(Cloud Cloud, int X, int Y)>? followed, double time, (int X, int Y) cam)
    {
        var vp = mask.Viewport;
        long n = 0;
        var placed = new List<(CloudSprite Sprite, int Left, int Top)>();
        if (followed is not null) placed.AddRange(followed.Select(f => (f.Cloud.Sprite, f.X - cam.X, f.Y - cam.Y)));
        foreach (var c in layer.Clouds)
        {
            if (followed is not null && followed.Any(f => ReferenceEquals(f.Cloud, c))) continue;
            if (layer.LinePlacement(c, time, cam.X, cam.Y, vp.X + vp.Width, vp.Y + vp.Height, MinObservations) is { } p) placed.Add((c.Sprite, p.Left, p.Top));
        }
        foreach (var (s, l, t) in placed)
        {
            int x0 = Math.Max(0, vp.X - l), x1 = Math.Min(s.Width, vp.X + vp.Width - l), y0 = Math.Max(0, vp.Y - t), y1 = Math.Min(s.Height, vp.Y + vp.Height - t);
            for (var y = y0; y < y1; y++)
            {
                var row = (t + y - vp.Y) * mask.Width + l - vp.X;
                for (var x = x0; x < x1; x++)
                {
                    if (s.Alpha[y * s.Width + x] == 0) continue;
                    // The column either side too.
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var sx = l + x + dx - vp.X;
                        if ((uint)sx >= (uint)mask.Width || mask.Bits[row + x + dx] != 0) continue;
                        mask.Bits[row + x + dx] = 1;
                        n++;
                    }
                }
            }
        }
        return n;
    }

    private static async Task Pass(Func<IFrameSource> replay, IReadOnlyDictionary<long, BackgroundBuild.FrameInfo> frames, ScreenMask mask, int mapId,
        Action<long, BackgroundBuild.FrameInfo, Frame> action)
    {
        using var source = replay();
        await foreach (var frame in source.Frames())
            using (frame)
                if (frames.TryGetValue(frame.Index, out var f) && f.Map.MapId == mapId)
                {
                    mask.Reset();
                    mask.Exclude(f.Panel, 4);
                    mask.Exclude(OwnCharacter.Box(f.Tag, OwnCharacter.MosaicPad));
                    action(frame.Index, f, frame);
                }
    }
}
