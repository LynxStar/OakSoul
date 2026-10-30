using Xunit;
using Xunit.Abstractions;

namespace OakSoul.Detect.Tests;

/// <summary>
/// The search on the floor slices (<see cref="SliceMatcher"/>) on fx-combat-exact's frame 511: it must find each of the frame's
/// eleven things as what it is, and nothing else (Arthur: the rest is the own character or fixed UI).
/// </summary>
public sealed class SliceMatcherTests(ITestOutputHelper output)
{
    private const string Frame511 = "fx-combat-exact-511";

    /// <summary>Rows a find's bottom may be from a target's box bottom (the boxes are not pixel exact).</summary>
    private const int BottomRows = 8;

    [Fact]
    public void FindsEveryTargetAndNothingElse()
    {
        if (FrameFixture.Load(Frame511) is not { } f) { output.WriteLine("fixture data not on this machine; nothing checked"); return; }
        // The narrowest widths load the mob library: outside the timing.
        int mobWidth = f.NarrowestMob(), mesoWidth = f.NarrowestMeso();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mobRuns = FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, mobWidth);
        var mesoRuns = FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, mesoWidth);
        var slicesMs = sw.Elapsed.TotalMilliseconds;
        // Again, warm (the floors' plan laid out and the code compiled): what a frame costs in a live run.
        sw.Restart();
        FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, mobWidth);
        FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, mesoWidth);
        var slicesWarmMs = sw.Elapsed.TotalMilliseconds;
        List<SliceMatcher.Candidate> mobs = f.MobCandidates(), mesos = f.MesoCandidates();
        var mobStats = new SliceMatcher.Stats();
        var mesoStats = new SliceMatcher.Stats();
        sw.Restart();
        var mobFinds = SliceMatcher.Search(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, mobRuns, mobs, stats: mobStats);
        var mobMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        // A drop does not rest with its origin on the line the way a mob does: meso 141 of this frame sits 6 rows below it.
        var mesoFinds = SliceMatcher.Search(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, mesoRuns, mesos, new SliceMatcher.Options { FloorRows = 8 }, stats: mesoStats);
        var mesoMs = sw.Elapsed.TotalMilliseconds;
        // Again, warm (the code compiled, the candidate sets laid out): what a frame costs in a live run. The best of ten.
        double warmMob = double.MaxValue, warmMeso = double.MaxValue;
        SliceMatcher.Stats warmMobStats = new(), warmMesoStats = new();
        for (var i = 0; i < 10; i++)
        {
            var ms = new SliceMatcher.Stats();
            sw.Restart();
            SliceMatcher.Search(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, mobRuns, mobs, stats: ms);
            if (sw.Elapsed.TotalMilliseconds < warmMob) (warmMob, warmMobStats) = (sw.Elapsed.TotalMilliseconds, ms);
            ms = new SliceMatcher.Stats();
            sw.Restart();
            SliceMatcher.Search(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, mesoRuns, mesos, new SliceMatcher.Options { FloorRows = 8 }, ms);
            if (sw.Elapsed.TotalMilliseconds < warmMeso) (warmMeso, warmMesoStats) = (sw.Elapsed.TotalMilliseconds, ms);
        }
        var finds = mobFinds.Concat(mesoFinds).ToList();
        output.WriteLine($"slices {slicesMs:0.0} ms, warm {slicesWarmMs:0.0} ms ({mobRuns.Sum(r => r.Slices.Count(s => s.Kind == FootholdSlices.Kind.Live))} live mob slices, " +
            $"{mesoRuns.Sum(r => r.Slices.Count(s => s.Kind == FootholdSlices.Kind.Live))} live meso slices); {finds.Count} finds");
        output.WriteLine($"mobs: {mobMs:0.0} ms over {mobs.Count} frames: {mobStats}");
        output.WriteLine($"mesos: {mesoMs:0.0} ms over {mesos.Count} frames: {mesoStats}");
        output.WriteLine($"warm (best of 10): mobs {warmMob:0.0} ms ({warmMobStats.QueryMs:0.0} ms queries), mesos {warmMeso:0.0} ms ({warmMesoStats.QueryMs:0.0} ms queries), together {warmMob + warmMeso:0.0} ms");

        var used = new HashSet<SliceMatcher.Find>();
        var missing = new List<string>();
        foreach (var t in f.Targets)
        {
            var hit = finds.Where(x => !used.Contains(x) && Covers(x, t)).OrderBy(x => x.Distance).FirstOrDefault();
            if (hit is not null) used.Add(hit);
            var right = hit is not null && (hit.Name == t.Name || t.Kind == "meso" && hit.Name == "meso");
            output.WriteLine($"  {t.Track,4} {t.Name,-15} {(hit is null ? "NOT FOUND" : (right ? "found as " : "WRONG: found as ") + hit)}");
            if (!right) missing.Add($"{t.Track} {t.Name}");
        }
        var extra = finds.Where(x => !used.Contains(x)).ToList();
        foreach (var x in extra) output.WriteLine($"  extra: {x}");
        Assert.True(missing.Count == 0 && extra.Count == 0, $"missed or wrong: {string.Join("; ", missing)}; extra: {extra.Count}");
    }

    /// <summary>
    /// A budgeted search stops at a slice boundary and says where; resumed from there until it finishes, with nothing suppressed
    /// along the way, the searches together cover every slice once and one Resolve over their finds gives the mob result whole:
    /// every mob target as itself, nothing else.
    /// </summary>
    [Fact]
    public void BudgetStopsAndResumes()
    {
        if (FrameFixture.Load(Frame511) is not { } f) { output.WriteLine("fixture data not on this machine; nothing checked"); return; }
        var mobRuns = FootholdSlices.Build(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, f.Map.Footholds, f.NarrowestMob());
        var mobs = f.MobCandidates();
        var o = new SliceMatcher.Options { BudgetMs = 3, KeepOverlapping = true };
        var all = new List<SliceMatcher.Find>();
        long slices = 0; var calls = 0;
        while (calls < 10_000)
        {
            var stats = new SliceMatcher.Stats();
            // The runs whole every time, resumed from where the last search stopped.
            all.AddRange(SliceMatcher.Search(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, mobRuns, mobs, o, stats));
            slices += stats.Slices; calls++;
            if (stats.StoppedRun < 0) break;
            o = o with { Start = (stats.StoppedRun, stats.StoppedSlice) };
        }
        var kept = SliceMatcher.Resolve(f.Frame, f.Width, f.Mask, f.Mosaic, f.CameraX, f.CameraY, all);
        output.WriteLine($"{calls} searches of at most {o.BudgetMs} ms covered {slices} slices; {all.Count} finds unsuppressed, {kept.Count} kept");
        Assert.True(calls > 1, "the budget never stopped a search");
        Assert.Equal(mobRuns.Sum(r => r.Slices.Count), slices);
        var used = new HashSet<SliceMatcher.Find>();
        var missing = new List<string>();
        foreach (var t in f.Targets.Where(t => t.Kind == "mob"))
        {
            var hit = kept.Where(x => !used.Contains(x) && Covers(x, t)).OrderBy(x => x.Distance).FirstOrDefault();
            if (hit is not null) used.Add(hit);
            output.WriteLine($"  {t.Track,4} {t.Name,-15} {(hit is null ? "NOT FOUND" : (hit.Name == t.Name ? "found as " : "WRONG: found as ") + hit)}");
            if (hit is null || hit.Name != t.Name) missing.Add($"{t.Track} {t.Name}");
        }
        var extra = kept.Where(x => !used.Contains(x)).ToList();
        foreach (var x in extra) output.WriteLine($"  extra: {x}");
        Assert.True(missing.Count == 0 && extra.Count == 0, $"missed or wrong: {string.Join("; ", missing)}; extra: {extra.Count}");
    }

    /// <summary>Whether a find stands where a target is: they share half the narrower's columns, and their bottoms are close.</summary>
    private static bool Covers(SliceMatcher.Find x, Target t)
    {
        int x0 = x.Left + x.Frame.X0, x1 = x.Left + x.Frame.X1, bottom = x.Top + x.Frame.Y1 + 1;
        var shared = Math.Min(x1, t.Rect.X + t.Rect.Width - 1) - Math.Max(x0, t.Rect.X) + 1;
        return shared >= 0.5 * Math.Min(x1 - x0 + 1, t.Rect.Width) && Math.Abs(bottom - t.Bottom) <= BottomRows;
    }
}
