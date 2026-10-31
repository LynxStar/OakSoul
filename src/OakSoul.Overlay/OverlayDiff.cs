namespace OakSoul.Overlay;

/// <summary>
/// Compares two overlay streams of one recording frame by frame, for the question a change to the pipeline raises: how many
/// frames differ, and in what. Frames pair by capture sequence (<see cref="OverlayStream.Captured"/>, fills left out); within a
/// frame the entities are their kind, name and feet and the tags their rect and text, never the track ids, which change
/// between regenerations. A frame is identical, or differs only in a tag's text being pending on one side, or only in an
/// entity being named on one side, or in the detector having run on one side only, or in more than that: an entity of another
/// kind, at another place, or missing. The last are told apart by whether the kinds' counts still agree, and for those the
/// matched entities' displacement is counted, so a one-pixel shift of everything reads as what it is.
/// </summary>
public static class OverlayDiff
{
    public enum Verdict { Identical, PendingOnly, NamesOnly, DetectorDiffers, Other }

    public sealed record FrameResult(long Sequence, Verdict Verdict, string Detail, bool SameKindCounts);

    public sealed class Report
    {
        public int FramesInA, FramesInB, FramesInBoth;
        public readonly Dictionary<Verdict, int> Counts = [];
        /// <summary>Of the other frames: with the kinds' counts the same, and not.</summary>
        public int OtherSameCounts, OtherKindCounts;
        /// <summary>On other frames with the same kind counts, entities of A matched by kind to the nearest of B: how far they moved.</summary>
        public int Moved0, Moved1, Moved2To3, Unmatched;
        /// <summary>Per bucket of sequences: identical, pending-only, names-only, detector, other.</summary>
        public readonly SortedDictionary<long, int[]> Buckets = [];
        public int Bucket = 500;
        /// <summary>Why the detector ran on one side only, by reason, with how many frames.</summary>
        public readonly Dictionary<string, int> DetectorReasons = [];
        /// <summary>Names present on one side only on names-only frames, by name.</summary>
        public readonly Dictionary<string, int> LateNames = [];
        public readonly List<FrameResult> Examples = [];
        public (long First, long Last, int Frames)? RanA, RanB;

        public int this[Verdict v] => Counts.GetValueOrDefault(v);

        public void Print(TextWriter o)
        {
            o.WriteLine($"frames: {FramesInA} in A, {FramesInB} in B, {FramesInBoth} in both (paired by capture sequence, fills left out)");
            o.WriteLine($"identical {this[Verdict.Identical]}, a tag's text pending on one side only {this[Verdict.PendingOnly]}, an entity named on one side only {this[Verdict.NamesOnly]}," +
                $" the detector ran on one side only {this[Verdict.DetectorDiffers]}, other {this[Verdict.Other]} ({OtherSameCounts} with the kinds' counts the same, {OtherKindCounts} not)");
            if (RanA is { } ra) o.WriteLine($"detector ran: A seq {ra.First}..{ra.Last} on {ra.Frames} frames" + (RanB is { } rb ? $"; B seq {rb.First}..{rb.Last} on {rb.Frames} frames" : ""));
            foreach (var (k, n) in DetectorReasons.OrderByDescending(x => x.Value)) o.WriteLine($"  {n,6}  {k}");
            if (OtherSameCounts > 0)
                o.WriteLine($"on other frames with the kinds' counts the same, A's entities matched by kind to the nearest of B: {Moved0} in place, {Moved1} moved 1 px, {Moved2To3} moved 2 to 3 px, {Unmatched} further or of another kind");
            if (LateNames.Count > 0) o.WriteLine("names on one side only, on names-only frames: " + string.Join(", ", LateNames.OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key} x{x.Value}")));
            o.WriteLine($"per {Bucket} frames (first sequence: identical / pending / names / detector / other):");
            foreach (var (k, v) in Buckets) o.WriteLine($"  {k * Bucket,6}: {v[0],5} / {v[1],5} / {v[2],4} / {v[3],4} / {v[4],5}");
            if (Examples.Count > 0)
            {
                o.WriteLine("examples:");
                foreach (var e in Examples) o.WriteLine($"  seq {e.Sequence} {e.Verdict}: {e.Detail}");
            }
        }
    }

    public static Report Compare(OverlayStream a, OverlayStream b, int bucket = 500, int examples = 12) =>
        Compare(a.Frames.Where(f => !f.Filled).ToList(), b.Frames.Where(f => !f.Filled).ToList(), bucket, examples);

    public static Report Compare(IReadOnlyList<OverlayFrame> framesA, IReadOnlyList<OverlayFrame> framesB, int bucket = 500, int examples = 12)
    {
        var a = BySequence(framesA);
        var b = BySequence(framesB);
        var report = new Report { FramesInA = a.Count, FramesInB = b.Count, Bucket = Math.Max(1, bucket) };
        report.RanA = Ran(a.Values);
        report.RanB = Ran(b.Values);
        var otherExamples = 0;
        foreach (var seq in a.Keys.Intersect(b.Keys).Order())
        {
            report.FramesInBoth++;
            var result = CompareFrame(a[seq], b[seq], report);
            report.Counts[result.Verdict] = report.Counts.GetValueOrDefault(result.Verdict) + 1;
            var slot = report.Buckets.TryGetValue(seq / report.Bucket, out var v) ? v : report.Buckets[seq / report.Bucket] = new int[5];
            slot[(int)result.Verdict switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 4 }]++;
            if (result.Verdict == Verdict.Other && otherExamples++ < examples) report.Examples.Add(result);
            else if (result.Verdict == Verdict.NamesOnly && report.Examples.Count(e => e.Verdict == Verdict.NamesOnly) < 3) report.Examples.Add(result);
        }
        return report;
    }

    private static Dictionary<long, OverlayFrame> BySequence(IReadOnlyList<OverlayFrame> frames)
    {
        var d = new Dictionary<long, OverlayFrame>();
        foreach (var f in frames) if (!f.Filled) d.TryAdd(f.Sequence, f);
        return d;
    }

    private static (long First, long Last, int Frames)? Ran(IEnumerable<OverlayFrame> frames)
    {
        var ran = frames.Where(f => f.Detect == "ok").Select(f => f.Sequence).ToList();
        return ran.Count == 0 ? null : (ran.Min(), ran.Max(), ran.Count);
    }

    private static string? Skipped(OverlayFrame f) => f.Skip ?? (f.Detect is { } d && d != "ok" ? d : null);

    private static HashSet<(string Kind, string? Name, (int X, int Y) Feet)> Entities(OverlayFrame f) =>
        f.Layer(OverlayLayers.Entity).Where(i => i.Kind != "hpbar").Select(i => (i.Kind, i.Str("name"), i.Point("feet") ?? (0, 0))).ToHashSet();

    private static HashSet<((int X, int Y, int W, int H) Rect, string Text)> Tags(OverlayFrame f) =>
        f.Layer(OverlayLayers.Tags).Select(i => (i.Rect ?? (0, 0, 0, 0), i.Bool("pending") ? "(pending)" : i.Str("text") ?? "(none)")).ToHashSet();

    private static FrameResult CompareFrame(OverlayFrame fa, OverlayFrame fb, Report report)
    {
        var seq = fa.Sequence;
        string? skipA = Skipped(fa), skipB = Skipped(fb);
        var ea = Entities(fa); var eb = Entities(fb);
        var ta = Tags(fa); var tb = Tags(fb);
        if (skipA == skipB && ea.SetEquals(eb) && ta.SetEquals(tb)) return new FrameResult(seq, Verdict.Identical, "", true);
        if (skipA != skipB)
        {
            var reason = $"A {skipA ?? "ran"} / B {skipB ?? "ran"}";
            report.DetectorReasons[reason] = report.DetectorReasons.GetValueOrDefault(reason) + 1;
            return new FrameResult(seq, Verdict.DetectorDiffers, reason, false);
        }
        var placesA = ea.Select(e => (e.Kind, e.Feet)).ToHashSet();
        var placesB = eb.Select(e => (e.Kind, e.Feet)).ToHashSet();
        var rectsA = ta.Select(t => t.Rect).ToHashSet();
        var rectsB = tb.Select(t => t.Rect).ToHashSet();
        if (placesA.SetEquals(placesB) && rectsA.SetEquals(rectsB))
        {
            var onlyA = ea.Except(eb).ToList();
            var onlyB = eb.Except(ea).ToList();
            // The same entities, some named on one side only.
            if ((onlyA.Count > 0 || onlyB.Count > 0)
                && onlyA.All(e => e.Name is null || eb.Any(o => o.Kind == e.Kind && o.Feet == e.Feet && o.Name is null))
                && onlyB.All(e => e.Name is null || ea.Any(o => o.Kind == e.Kind && o.Feet == e.Feet && o.Name is null)))
            {
                foreach (var e in onlyB.Where(e => e.Name is not null)) report.LateNames[e.Name!] = report.LateNames.GetValueOrDefault(e.Name!) + 1;
                foreach (var e in onlyA.Where(e => e.Name is not null)) report.LateNames[e.Name! + " (A)"] = report.LateNames.GetValueOrDefault(e.Name! + " (A)") + 1;
                return new FrameResult(seq, Verdict.NamesOnly, string.Join("; ", onlyA.Concat(onlyB).Select(Describe)), true);
            }
            // The same entities and names, a tag's text pending on one side.
            if (onlyA.Count == 0 && onlyB.Count == 0 && !ta.SetEquals(tb)) return new FrameResult(seq, Verdict.PendingOnly, "", true);
        }
        var kindsA = ea.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}").ToList();
        var kindsB = eb.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}").ToList();
        var same = kindsA.SequenceEqual(kindsB);
        if (same) report.OtherSameCounts++; else report.OtherKindCounts++;
        var missingInB = ea.Except(eb).ToList();
        var missingInA = eb.Except(ea).ToList();
        if (same)
            foreach (var e in missingInB)
            {
                var near = missingInA.Where(o => o.Kind == e.Kind).Select(o => (Entity: o, D: Math.Max(Math.Abs(o.Feet.X - e.Feet.X), Math.Abs(o.Feet.Y - e.Feet.Y)))).OrderBy(x => x.D).FirstOrDefault();
                if (near.Entity.Kind is null || near.D > 3) report.Unmatched++; else if (near.D == 0) report.Moved0++; else if (near.D == 1) report.Moved1++; else report.Moved2To3++;
            }
        var detail = $"A [{string.Join(",", kindsA)}] B [{string.Join(",", kindsB)}]; only in A: {string.Join("; ", missingInB.Take(4).Select(Describe))} | only in B: {string.Join("; ", missingInA.Take(4).Select(Describe))}";
        return new FrameResult(seq, Verdict.Other, detail, same);
    }

    private static string Describe((string Kind, string? Name, (int X, int Y) Feet) e) => $"{e.Kind}{(e.Name is null ? "" : $" '{e.Name}'")} at {e.Feet.X},{e.Feet.Y}";
}
