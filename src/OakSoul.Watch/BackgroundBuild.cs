using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// Builds the background mosaic of every map a replayed session visited, offline, from the frames whose camera the
/// world tracker measured (dot and own tag). Four replays: vote with each frame registered against the votes so far,
/// count agreement, then once more registered against that first mosaic's reliable pixels, and count again. The
/// second round fixes frames the incremental registration got wrong early on, when the votes so far were thin. Only
/// pixel-exact sessions are built (the caller checks), since the detector compares exact frames with the mosaic.
/// </summary>
public static class BackgroundBuild
{
    /// <summary>One usable frame: its map, the tracker's camera, whether that camera is exact, and what to mask.</summary>
    public sealed record FrameInfo(MapData Map, PointD Camera, bool Exact, CaptureRect Panel, CaptureRect Tag);

    /// <param name="keyframes">For a mosaic voted from a session's video: the session's lossless keyframes with the
    /// video sequence each one copies (<see cref="KeyframesOf"/>); their pixels replace the video's where they agree
    /// (<see cref="MosaicBuilder.ExactRefine"/>).</param>
    public static async Task Run(TextWriter o, Func<IFrameSource> replay, IReadOnlyDictionary<long, FrameInfo> frames, ScreenMask mask, string dir,
        IReadOnlyList<(long VideoSequence, string Path)>? keyframes = null)
    {
        var builders = frames.Values.Select(f => f.Map).DistinctBy(m => m.MapId).ToDictionary(m => m.MapId, m => new MosaicBuilder(m.MapId, m.Vr));
        // A map where the camera never sat on a bound has no exact camera to anchor the mosaic: its first frame
        // anchors it provisionally, and after the first round the minimap dot fixes the offset (see DotAnchor).
        var anchored = frames.Values.Where(f => f.Exact).Select(f => f.Map.MapId).ToHashSet();
        var provisional = new Dictionary<int, long>();
        foreach (var (seq, f) in frames.OrderBy(p => p.Key))
            if (!anchored.Contains(f.Map.MapId)) provisional.TryAdd(f.Map.MapId, seq);
        var refined = new Dictionary<long, (int X, int Y)>();
        Dictionary<int, BackgroundMosaic>? first = null;
        for (var round = 0; round < 2; round++)
        {
            refined.Clear();
            await Pass(replay, frames, mask, (seq, f, px, w) =>
            {
                var reference = first?.GetValueOrDefault(f.Map.MapId);
                var exact = f.Exact || (round == 0 && provisional.TryGetValue(f.Map.MapId, out var anchor) && anchor == seq);
                if (builders[f.Map.MapId].Vote(px, w, f.Camera, mask, exact, reference) is { } cam) refined[seq] = cam;
            });
            await Pass(replay, frames, mask, (seq, f, px, w) =>
            {
                if (refined.TryGetValue(seq, out var cam)) builders[f.Map.MapId].Count(px, w, cam.X, cam.Y, mask);
            });
            if (round == 0)
            {
                // A map whose frames never voted (no exact camera to anchor it, e.g. a short visit) has nothing to build.
                first = builders.Where(b => b.Value.Counted > 0).ToDictionary(b => b.Key, b => b.Value.Build());
                foreach (var (mapId, _) in provisional)
                {
                    if (!first.TryGetValue(mapId, out var mosaic) || DotAnchor(builders[mapId].Refinements) is not { } fix) continue;
                    first[mapId] = mosaic.Shifted(-fix.X, -fix.Y);
                    o.WriteLine($"background {mapId}: no camera on a bound; anchored on the first frame and corrected by the minimap dot over {fix.Frames} frames" +
                                $" ({fix.Outliers} a cell off): x {fix.X:+0;-0;0} (the dot allows {fix.X - fix.SlackLow:0}..{fix.X + fix.SlackHigh:0}), y {fix.Y:+0;-0;0}");
                }
                foreach (var b in builders.Values) b.StartOver();
            }
        }
        if (keyframes is { Count: > 0 })
        {
            var used = new Dictionary<int, (int Frames, long Pixels)>();
            foreach (var (seq, path) in keyframes)
            {
                if (!frames.TryGetValue(seq, out var f) || !refined.TryGetValue(seq, out var cam)) continue;
                var (px, w, _) = Png.ReadBgra(path);
                mask.Reset();
                mask.Exclude(f.Panel, 4);
                mask.Exclude(OwnCharacter.Box(f.Tag, OwnCharacter.MosaicPad));
                var n = builders[f.Map.MapId].ExactRefine(px, w, cam.X, cam.Y, mask);
                var (k, p) = used.GetValueOrDefault(f.Map.MapId);
                used[f.Map.MapId] = (k + 1, p + n);
            }
            foreach (var (mapId, (k, p)) in used)
                o.WriteLine($"background {mapId}: {k} lossless keyframes replaced {p:N0} voted video pixels with their exact values");
        }
        // The first correction rests on the frames the incremental registration placed; the second round registers every
        // frame against the corrected mosaic, so the dot is consulted once more over all of them and the rest of the
        // offset is taken out of the saved mosaic's origin.
        var residual = new Dictionary<int, (int X, int Y)>();
        foreach (var (mapId, _) in provisional)
            if (builders[mapId].Counted > 0 && DotAnchor(builders[mapId].Refinements, includeAnchor: false) is { } fix)
            {
                residual[mapId] = (fix.X, fix.Y);
                o.WriteLine($"background {mapId}: second round, the minimap dot over {fix.Frames} frames ({fix.Outliers} a cell off) leaves x {fix.X:+0;-0;0}" +
                            $" (the dot allows {fix.X - fix.SlackLow:0}..{fix.X + fix.SlackHigh:0}), y {fix.Y:+0;-0;0}");
            }
        foreach (var (mapId, builder) in builders)
        {
            if (builder.Counted == 0) { o.WriteLine($"background {mapId}: no frame could be anchored (no exact camera); skipped"); continue; }
            var mosaic = builder.Build();
            if (residual.TryGetValue(mapId, out var shift) && shift != (0, 0)) mosaic = mosaic.Shifted(-shift.X, -shift.Y);
            mosaic.Save(dir);
            BackgroundReport.Print(o, builder, mosaic, dir);
        }
    }

    /// <summary>The keyframes of a session (frames.jsonl) with the video sequence (1-based, as replay numbers frames) each one copies.</summary>
    public static List<(long VideoSequence, string Path)> KeyframesOf(string session)
    {
        var list = new List<(long, string)>();
        var path = Path.Combine(session, "frames.jsonl");
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.TryGetProperty("keyframe", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String
                && r.TryGetProperty("videoFrame", out var v) && v.GetInt64() >= 0)
                list.Add((v.GetInt64() + 1, Path.Combine(session, k.GetString()!)));
        }
        return list;
    }

    /// <summary>The dot's cell: the tracker's camera x is the player's 16-unit minimap cell centre minus the tag's x, so it lies within half a cell of the true camera.</summary>
    public const int DotCell = 16;

    /// <summary>
    /// The offset of a provisionally anchored mosaic, from the first round's registrations. Registration gives every
    /// frame's camera exactly relative to the anchor frame (r = true + e), while the tracker's camera t lies within
    /// half a dot cell of the true one (t = true + d, d in (-8, 8]). So t - r = d - e, and over frames whose players
    /// stood in different places within their cells these differences span the cell: e is where the cell fits all of
    /// them (the middle of the feasible range), after dropping the frames a cell off (the dot lags a moving player).
    /// Vertically the tracker snaps onto footholds, so e is the median difference. Null with fewer than three frames.
    /// </summary>
    public static (int X, int Y, int Frames, int Outliers, double SlackLow, double SlackHigh)? DotAnchor(IReadOnlyList<(int GuessX, int GuessY, Registration Result)> refinements,
        bool includeAnchor = true)
    {
        // In the first round the anchor frame itself was voted at its tracker camera: difference 0.
        var vx = includeAnchor ? new List<int> { 0 } : [];
        var vy = includeAnchor ? new List<int> { 0 } : [];
        foreach (var (gx, gy, r) in refinements)
            if (r.Compared >= 150 && r.Score >= MosaicBuilder.MinVoteScore) { vx.Add(gx - r.CameraX); vy.Add(gy - r.CameraY); }
        if (vx.Count < 3) return null;
        vx.Sort();
        // The densest window one cell wide (inliers), then the middle of the range of e it leaves.
        int bestLo = 0, bestN = 0;
        for (int lo = 0, hi = 0; lo < vx.Count; lo++)
        {
            while (hi < vx.Count && vx[hi] - vx[lo] < DotCell) hi++;
            if (hi - lo > bestN) { bestN = hi - lo; bestLo = lo; }
        }
        int min = vx[bestLo], max = vx[bestLo + bestN - 1];
        // d = v + e in (-8, 8] for every inlier: e in (-8 - min, 8 - max].
        double low = -DotCell / 2.0 - min, high = DotCell / 2.0 - max;
        var ex = (int)Math.Round((low + high) / 2);
        vy.Sort();
        var ey = -vy[vy.Count / 2];
        return (ex, ey, vx.Count, vx.Count - bestN, ex - low, high - ex);
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
