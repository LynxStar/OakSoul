using OakSoul.Detect;

namespace OakSoul.Watch;

/// <summary>Prints what a background build produced: coverage, unreliable fraction, the disagreement histogram and the camera refinements.</summary>
public static class BackgroundReport
{
    public static void Print(TextWriter o, MosaicBuilder builder, BackgroundMosaic mosaic, string dir)
    {
        var (total, seen, sampled, reliable, fgUsable) = mosaic.Stats();
        o.WriteLine();
        o.WriteLine($"== background {mosaic.MapId} ==");
        o.WriteLine($"  saved {BackgroundMosaic.PathFor(dir, mosaic.MapId, ".png")} (+ .mask.png, .json); {builder.Voted} frames voted ({builder.Rejected} more rejected for a poor registration), {builder.Counted} counted");
        o.WriteLine($"  world box (vr) {mosaic.Width}x{mosaic.Height} at {mosaic.OriginX},{mosaic.OriginY}: {total:N0} px (1 px = 1 world unit)");
        o.WriteLine($"  seen {seen:N0} ({100.0 * seen / total:0.0}%), seen enough (>= {MosaicBuilder.MinSamples} frames, shifted viewpoints) {sampled:N0} ({100.0 * sampled / total:0.0}%)");
        o.WriteLine($"  reliable {reliable:N0}; unreliable {sampled - reliable:N0} = {100.0 * (sampled - reliable) / Math.Max(1, sampled):0.0}% of the sampled pixels (disagreement > {BackgroundMosaic.UnreliableLevel}/253 = 25%)");
        o.WriteLine($"  usable for foreground (disagreement <= {BackgroundMosaic.ForegroundLevel}/253 = 45%): {fgUsable:N0} = {100.0 * fgUsable / Math.Max(1, sampled):0.0}% of the sampled pixels, {fgUsable / 1e6:0.00} M world units^2");
        var (sampledBy, shifted) = builder.ViewpointCoverage();
        o.WriteLine($"  of the {sampledBy:N0} pixels seen by >= {MosaicBuilder.MinSamples} frames, {shifted:N0} ({100.0 * shifted / Math.Max(1, sampledBy):0.0}%) were agreed on from viewpoints >= {MosaicBuilder.MinViewpointShift} px apart; the rest count as too few samples");
        var hist = builder.DisagreementHistogram(mosaic);
        o.WriteLine($"  disagreement histogram (tenths, % of sampled): {string.Join(" ", hist.Select((n, i) => $"{i * 10}%:{100.0 * n / Math.Max(1, sampled):0.0}"))}");
        // Row and column extents of the seen area, in world units.
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < mosaic.Height; y++)
            for (var x = 0; x < mosaic.Width; x++)
                if (mosaic.Disagreement[y * mosaic.Width + x] != BackgroundMosaic.Unseen) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX >= 0) o.WriteLine($"  seen extent world x {mosaic.OriginX + minX}..{mosaic.OriginX + maxX}, y {mosaic.OriginY + minY}..{mosaic.OriginY + maxY}");
        var r = builder.Refinements;
        if (r.Count > 0)
        {
            var dx = r.Select(e => e.Result.CameraX - e.GuessX).ToList();
            var dy = r.Select(e => e.Result.CameraY - e.GuessY).ToList();
            o.WriteLine($"  camera refinement over {r.Count} frames: dx {dx.Min()}..{dx.Max()} (|dx|>0 on {dx.Count(d => d != 0)}), dy {dy.Min()}..{dy.Max()} (|dy|>0 on {dy.Count(d => d != 0)})");
            o.WriteLine($"  match fraction at refined camera: median {Median(r.Select(e => e.Result.Score)):0.000} min {r.Min(e => e.Result.Score):0.000}; at the tracker's camera median {Median(r.Select(e => e.Result.GuessScore)):0.000}");
            var scores = r.Select(e => e.Result.Score).Order().ToList();
            o.WriteLine($"  refined match fraction percentiles p5 {scores[scores.Count / 20]:0.000} p10 {scores[scores.Count / 10]:0.000} p25 {scores[scores.Count / 4]:0.000}; frames under 0.8: {scores.Count(v => v < 0.8)}");
            o.WriteLine($"  dx histogram: {string.Join(" ", dx.GroupBy(d => d).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
            o.WriteLine($"  dy histogram: {string.Join(" ", dy.GroupBy(d => d).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        return list.Count == 0 ? 0 : list[list.Count / 2];
    }
}
