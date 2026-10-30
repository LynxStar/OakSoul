using Xunit;
using Xunit.Abstractions;

namespace OakSoul.Detect.Tests;

/// <summary>
/// The floor slices (<see cref="FootholdSlices"/>) on fx-combat-exact's frame 511: the camera scrolls there, a third of the
/// frame is background the mosaic does not trust, and a Blue Snail and a Wild Boar stand under a pair of clouds no detector
/// stage knows about. Every one of the frame's eleven things must keep a live slice under it.
/// </summary>
public sealed class FootholdSliceTests(ITestOutputHelper output)
{
    private const string Frame511 = "fx-combat-exact-511";

    /// <summary>Rows a target's box bottom may be from the foothold's line and still stand on it (the boxes are not pixel exact).</summary>
    private const int StandRows = 6;

    [Fact]
    public void FixtureMaskIsTheDetectors()
    {
        if (FrameFixture.Load(Frame511) is not { } f) { output.WriteLine("fixture data not on this machine; nothing checked"); return; }
        // The detector's mask on this frame (its debug dump, 2026-10-30) excluded 140,005 pixels.
        Assert.Equal(140_005, f.Mask.Bits.Count(b => b != 0));
    }

    [Fact]
    public void EveryTargetKeepsALiveSlice()
    {
        if (FrameFixture.Load(Frame511) is not { } f) { output.WriteLine("fixture data not on this machine; nothing checked"); return; }
        // Mobs are searched in runs at least as wide as the narrowest mob frame, mesos in runs as wide as a coin turned edge-on.
        int mobWidth = f.NarrowestMob(), mesoWidth = f.NarrowestMeso();
        List<FootholdSlices.Run> Runs(int minWidth, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var runs = FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, minWidth);
            var ms = sw.Elapsed.TotalMilliseconds;
            var slices = runs.SelectMany(r => r.Slices).ToList();
            output.WriteLine($"{what}: runs at least {minWidth} wide: {runs.Count} runs, {slices.Count} slices " +
                $"({slices.Count(s => s.Kind == FootholdSlices.Kind.Live)} live, {slices.Count(s => s.Kind == FootholdSlices.Kind.Interrupted)} interrupted) in {ms:0.0} ms");
            return runs;
        }
        var mobRuns = Runs(mobWidth, "mobs");
        var mesoRuns = Runs(mesoWidth, "mesos");

        var lost = new List<string>();
        foreach (var t in f.Targets)
        {
            var runs = t.Kind == "meso" ? mesoRuns : mobRuns;
            var under = runs.SelectMany(r => r.Slices)
                .Where(s => s.X >= t.Rect.X && s.X < t.Rect.X + t.Rect.Width && Math.Abs(s.LineY - t.Bottom) <= StandRows).ToList();
            int live = under.Count(s => s.Kind == FootholdSlices.Kind.Live), interrupted = under.Count - live;
            var footholds = string.Join(",", under.Select(s => s.FootholdId).Distinct());
            output.WriteLine($"  {t.Track,4} {t.Name,-15} {t.Rect.Width,3} wide: {live,3} live, {interrupted,3} interrupted slices under it (foothold {(footholds.Length > 0 ? footholds : "none")})");
            if (live == 0) lost.Add($"{t.Track} {t.Name}");
        }
        Draw(f, mobRuns, Path.Combine(f.Root, "data", "slice-tests", Frame511 + "-mobs.png"));
        Assert.True(lost.Count == 0, "no live slice under: " + string.Join("; ", lost));
    }

    /// <summary>
    /// The slices drawn over a black frame: live slices in the frame's colours where a pixel is not known and black where it
    /// is, interrupted slices grey, every target's box red.
    /// </summary>
    private static void Draw(FrameFixture f, List<FootholdSlices.Run> runs, string path)
    {
        var canvas = new Canvas(f.Width, f.Height, 0xFF000000);
        foreach (var s in runs.SelectMany(r => r.Slices))
            for (var y = Math.Max(0, s.LineY - FootholdSlices.Above); y <= Math.Min(f.Height - 1, s.LineY + FootholdSlices.Below); y++)
            {
                if (s.Kind == FootholdSlices.Kind.Interrupted) { canvas.Set(s.X, y, 0xFF808080); continue; }
                if (FootholdSlices.Known(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, s.X, y)) continue;
                var o = (y * f.Width + s.X) * 4;
                canvas.Set(s.X, y, 0xFF000000 | (uint)f.Frame[o + 2] << 16 | (uint)f.Frame[o + 1] << 8 | f.Frame[o]);
            }
        foreach (var t in f.Targets) canvas.Rect(t.Rect.X, t.Rect.Y, t.Rect.Width, t.Rect.Height, 0xFFFF2020);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        canvas.Save(path);
    }
}
