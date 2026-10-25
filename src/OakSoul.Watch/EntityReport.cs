using OakSoul.Detect;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// Label-free validation of the entity detector over a replay: counts per kind, foothold support of mobs, track
/// lengths, whether kills (EXP gains) line up with mob tracks ending near the character, whether drops appear after
/// kills, how often mobs carry a readable tag, and the per-frame cost.
/// </summary>
public static class EntityReport
{
    /// <summary>A mob track counts as "near the character" for a kill when it ended within this many world units (spell range and a margin).</summary>
    public const double KillRadius = 450;

    /// <summary>Keyframes before an EXP change in which the killed mob's track must end (the death animation runs about a second).</summary>
    public const int KillWindowFrames = 2;

    /// <summary>A mob track that ends and another that starts this close, this soon after, is one mob tracked twice, not a kill.</summary>
    public const double ReacquireUnits = 80;
    public const int ReacquireFrames = 3;

    public static void Print(TextWriter o, EntityWatch watch)
    {
        var det = watch.Detector;
        if (det is null) { o.WriteLine("entity report: no detector ran"); return; }
        det.Tracker.Flush();
        var rows = watch.Rows;
        var done = rows.Where(r => r.Result.Camera is not null).ToList();
        o.WriteLine();
        o.WriteLine("== entity report ==");
        o.WriteLine($"  frames {rows.Count}, detected on {done.Count}; skipped: {string.Join(", ", rows.Where(r => r.Result.Camera is null).GroupBy(r => r.Result.Note?.Split(' ')[0] ?? "?").Select(g => $"{g.Key} {g.Count()}"))}");
        var kinds = Enum.GetValues<EntityKind>();

        // Per-frame counts by kind.
        o.WriteLine("  per-frame counts by kind (mean / median / max over detected frames):");
        foreach (var k in kinds)
        {
            var c = done.Select(r => (double)r.Result.Count(k)).ToList();
            o.WriteLine($"    {k,-8} {c.Average(),5:0.00} / {Pct(c, 50),3:0} / {c.Max(),3:0}   total {c.Sum():0}");
        }

        // Mob feet on footholds.
        var mobs = done.SelectMany(r => r.Result.Entities).Where(e => e.Kind == EntityKind.Mob).ToList();
        var mobsOn = mobs.Count(e => e.FootholdDistance is { } d && Math.Abs(d) <= 6);
        o.WriteLine($"  mob detections with feet within 6 units of a foothold: {mobsOn}/{mobs.Count} = {Frac(mobsOn, mobs.Count)}");
        var tagged = mobs.Where(e => e.Tag is not null).ToList();
        var taggedOn = tagged.Count(e => e.FootholdDistance is { } d && Math.Abs(d) <= 6);
        o.WriteLine($"    of which tagged (feet from the tag): {taggedOn}/{tagged.Count} = {Frac(taggedOn, tagged.Count)}");

        // Track statistics.
        var tracks = det.Tracker.Finished;
        o.WriteLine($"  tracks: {tracks.Count}");
        foreach (var k in kinds)
        {
            var lengths = tracks.Where(t => t.Kind == k).Select(t => (double)t.Frames).ToList();
            if (lengths.Count == 0) { o.WriteLine($"    {k,-8} none"); continue; }
            o.WriteLine($"    {k,-8} {lengths.Count,5} tracks, length median {Pct(lengths, 50):0} p90 {Pct(lengths, 90):0} max {lengths.Max():0} frames");
        }

        // Kill consistency against EXP changes.
        var frames = rows.Select(r => r.Result.Frame).ToList();
        var index = frames.Select((f, i) => (f, i)).ToDictionary(p => p.f, p => p.i);
        var expChanges = new List<int>();
        long? lastExp = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Exp is not { } e) continue;
            if (lastExp is { } le && e > le) expChanges.Add(i);
            lastExp = e;
        }
        var mobTracks = tracks.Where(t => t.Kind == EntityKind.Mob).ToList();
        o.WriteLine($"  EXP changes: {expChanges.Count}");
        // All mob track ends, and the "disappearances": ends not followed by another mob track starting within
        // ReacquireUnits of the last position within ReacquireFrames (a track that broke and resumed under a new id).
        var vanished = mobTracks.Where(t => !mobTracks.Any(u => u.Id != t.Id && index.TryGetValue(u.FirstFrame, out var uf) && index.TryGetValue(t.LastFrame, out var tl) &&
            uf > tl && uf <= tl + ReacquireFrames && u.FirstWorld.DistanceTo(t.LastWorld) <= ReacquireUnits)).ToList();
        var hit = vanished.Where(t => t.Hit).ToList();
        foreach (var (label, set) in new[] { ("any mob track end", mobTracks), ("a mob disappearance (end not re-acquired)", vanished), ("a hit mob's disappearance (level tag or HP bar seen)", hit) })
        {
            int strict = 0, loose = 0, baseline = 0, looseBaseline = 0;
            bool EndedNear(int i, int from, int to)
            {
                var own = rows[Math.Clamp(i, 0, rows.Count - 1)].Own ?? rows.Take(i + 1).LastOrDefault(r => r.Own is not null)?.Own;
                return set.Any(t => index.TryGetValue(t.LastFrame, out var li) && li >= from && li <= to && (own is null || t.LastWorld.DistanceTo(own.Value) <= KillRadius));
            }
            foreach (var i in expChanges)
            {
                if (EndedNear(i, i - KillWindowFrames, i - 1)) strict++;
                if (EndedNear(i, i - KillWindowFrames - 1, i + 1)) loose++;
            }
            for (var i = KillWindowFrames + 1; i < rows.Count - 1; i++)
            {
                if (EndedNear(i, i - KillWindowFrames, i - 1)) baseline++;
                if (EndedNear(i, i - KillWindowFrames - 1, i + 1)) looseBaseline++;
            }
            var n = rows.Count - KillWindowFrames - 2;
            o.WriteLine($"    {label} within {KillRadius} units in the {KillWindowFrames} keyframes before: {strict} = {Frac(strict, expChanges.Count)} (chance {Frac(baseline, n)});" +
                        $" from 3 before to 1 after: {loose} = {Frac(loose, expChanges.Count)} (chance {Frac(looseBaseline, n)})");
        }

        // Drop consistency.
        var drops = tracks.Where(t => t.Kind == EntityKind.Drop).ToList();
        var afterKill = drops.Where(t => index.TryGetValue(t.FirstFrame, out var fi) && expChanges.Any(e => fi >= e - 1 && fi <= e + KillWindowFrames)).ToList();
        o.WriteLine($"  drop tracks: {drops.Count}; starting within {KillWindowFrames} keyframes of an EXP change: {afterKill.Count} = {Frac(afterKill.Count, drops.Count)}");
        if (drops.Count > 0)
        {
            var lives = drops.Select(t => t.LastTime - t.FirstTime).ToList();
            o.WriteLine($"    lifetime median {Pct(lives, 50):0.0} s, p90 {Pct(lives, 90):0.0} s, max {lives.Max():0.0} s; after-kill drops median {(afterKill.Count == 0 ? 0 : Pct(afterKill.Select(t => t.LastTime - t.FirstTime).ToList(), 50)):0.0} s");
            var gone = drops.Count(t => t.NearOwnAtEnd);
            o.WriteLine($"    drops whose track ended with the own character within {EntityTracker.PetRadius} units (picked up?): {gone}");
        }

        // Mob tag coverage.
        o.WriteLine($"  mob detections with a readable tag in that frame: {mobs.Count(e => e.Tag is not null && e.Name is not null)}/{mobs.Count} = {Frac(mobs.Count(e => e.Tag is not null && e.Name is not null), mobs.Count)}");
        var withTag = mobs.Where(e => e.Tag is not null).ToList();
        o.WriteLine($"    tagged mob detections with an HP bar over them in the same frame: {withTag.Count(e => e.HpBar)}/{withTag.Count} = {Frac(withTag.Count(e => e.HpBar), withTag.Count)};" +
                    $" untagged mob detections with one: {mobs.Count(e => e.Tag is null && e.HpBar)}/{mobs.Count(e => e.Tag is null)}");
        var mobTagged = mobTracks.Count(t => t.Tag is not null);
        o.WriteLine($"    mob tracks that showed a tag at some point: {mobTagged}/{mobTracks.Count} = {Frac(mobTagged, mobTracks.Count)}");
        o.WriteLine($"  distinct tag texts ({det.Tags.OcrCalls} OCR calls, {det.Tags.OcrMilliseconds:0} ms total):");
        foreach (var g in done.SelectMany(r => r.Result.Entities).Where(e => e.Tag is not null).GroupBy(e => (e.Kind, e.Tag)).OrderByDescending(g => g.Count()).Take(40))
            o.WriteLine($"    {g.Key.Kind,-7} '{g.Key.Tag}' x{g.Count()}");
        o.WriteLine($"  mob names learned: {string.Join(", ", det.Tracker.Names.MobNames)}; pet {det.Tracker.Names.PetName ?? "none"}");

        // Cost.
        var ms = done.Select(r => r.Result.Milliseconds).ToList();
        o.WriteLine($"  cost per frame excluding OCR (registration, difference, tags, components, tracking): mean {ms.Average():0.0} ms, median {Pct(ms, 50):0.0}, p90 {Pct(ms, 90):0.0}, max {ms.Max():0.0}");
        if (det.StageFrames > 0)
            o.WriteLine($"    by stage (mean ms): registration {det.StageMs[0] / det.StageFrames:0.0}, difference {det.StageMs[1] / det.StageFrames:0.0}, tags {det.StageMs[2] / det.StageFrames:0.0}, components {det.StageMs[3] / det.StageFrames:0.0}, candidates and tracking {det.StageMs[4] / det.StageFrames:0.0}; wide searches {det.WideSearches}");
        var reg = done.Select(r => r.Result.Registration!.Value.Score).ToList();
        o.WriteLine($"  registration match fraction: median {Pct(reg, 50):0.000}, p10 {Pct(reg, 10):0.000}");
    }

    private static double Pct(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Round(p / 100 * (sorted.Count - 1)), 0, sorted.Count - 1)];
    }

    private static string Frac(int n, int d) => d == 0 ? "n/a" : $"{100.0 * n / d:0.0}%";
}
