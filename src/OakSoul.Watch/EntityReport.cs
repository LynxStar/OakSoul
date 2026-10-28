using OakSoul.Detect;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// Label-free validation of the entity detector over a replay: counts per kind, foothold support of mobs, sprite
/// verification and identity (fraction matched, species per map against the spawn counts, score distributions),
/// track lengths, whether kills (EXP gains) line up with mob tracks ending near the character and which species
/// died, drops, the own position with the camera fed back, and the per-stage cost.
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
        var detectors = watch.Detectors.ToList();
        if (detectors.Count == 0) { o.WriteLine("entity report: no detector ran"); return; }
        watch.Tracker.Flush();
        var rows = watch.Rows;
        var done = rows.Where(r => r.Result.Camera is not null).ToList();
        o.WriteLine();
        o.WriteLine("== entity report ==");
        o.WriteLine($"  frames {rows.Count} on {rows.Select(r => r.MapId).Distinct().Count()} map(s) with a background, detected on {done.Count}; skipped: {string.Join(", ", rows.Where(r => r.Result.Camera is null).GroupBy(r => r.Result.Note?.Split(' ')[0] ?? "?").Select(g => $"{g.Key} {g.Count()}"))}");
        o.WriteLine($"  frames not given to the detector (not the game's own pixels): {(watch.Skipped.Count == 0 ? "none" : string.Join("; ", watch.Skipped.Select(s => $"{s.Value} {s.Key}")))}");
        if (done.Count == 0) return;
        var kinds = Enum.GetValues<EntityKind>();

        // Per-frame counts by kind.
        o.WriteLine("  per-frame counts by kind (mean / median / max over detected frames):");
        foreach (var k in kinds)
        {
            var c = done.Select(r => (double)r.Result.Count(k)).ToList();
            o.WriteLine($"    {k,-8} {c.Average(),5:0.00} / {Pct(c, 50),3:0} / {c.Max(),3:0}   total {c.Sum():0}");
        }

        // Mob feet on footholds, from the box or tag and from the sprite's origin.
        var mobs = done.SelectMany(r => r.Result.Entities).Where(e => e.Kind == EntityKind.Mob).ToList();
        var mobsOn = mobs.Count(e => On(e.FootholdDistance));
        o.WriteLine($"  mob detections with feet within 6 units of a foothold: {mobsOn}/{mobs.Count} = {Frac(mobsOn, mobs.Count)}");
        var tagged = mobs.Where(e => e.Tag is not null).ToList();
        o.WriteLine($"    of which tagged (feet from the tag): {tagged.Count(e => On(e.FootholdDistance))}/{tagged.Count} = {Frac(tagged.Count(e => On(e.FootholdDistance)), tagged.Count)}");
        var matched = mobs.Where(e => e.Sprite is not null).ToList();
        o.WriteLine($"    of which sprite-matched: feet from the box or tag {Frac(matched.Count(e => On(e.BoxFootholdDistance)), matched.Count)}, from the sprite's origin {Frac(matched.Count(e => On(e.FootholdDistance)), matched.Count)} (n {matched.Count});" +
                    $" |distance| median {Pct(matched.Where(e => e.FootholdDistance is not null).Select(e => Math.Abs(e.FootholdDistance!.Value)).ToList(), 50):0.0} units");

        Sprites(o, watch, done, mobs, detectors);
        Tracks(o, watch, rows);
        Kills(o, watch, rows);
        Drops(o, watch, done);
        Tags(o, watch, done, mobs, detectors);
        CameraFeedback(o, watch, rows);
        Review(o, watch, rows, done, detectors);
        Cost(o, done, detectors);
    }

    /// <summary>
    /// What the first review round added: the foothold search, damage numbers (with the taken ones checked against the
    /// HP the status bar lost), the pet's one track, the own character's slivers, kills credited by EXP gains, dying mobs,
    /// hitboxes and the association rules that refused a track.
    /// </summary>
    private static void Review(TextWriter o, EntityWatch watch, List<EntityWatch.Row> rows, List<EntityWatch.Row> done, List<EntityDetector> detectors)
    {
        var frames = Math.Max(1, done.Count);
        var all = done.SelectMany(r => r.Result.Entities).ToList();
        var swept = all.Where(e => e.Swept && e.Sprite is not null).ToList();
        var scores = detectors.SelectMany(d => d.SweepScores).ToList();
        o.WriteLine($"  foothold search: {detectors.Sum(d => d.SweepFinds)} mobs found where no blob was, {detectors.Sum(d => d.SweepAttached)} blobs verified that no box alignment matched;" +
                    $" {swept.Count} detections in the output rest on it (gap columns per map {string.Join(", ", detectors.Where(d => d.Search is not null).Select(d => $"{d.Mosaic.MapId} {d.Search!.GapColumns}"))})");
        if (scores.Count > 0)
            o.WriteLine($"    scores of the finds: n {scores.Count}, p5 {Pct(scores, 5):0.00}, p10 {Pct(scores, 10):0.00}, median {Pct(scores, 50):0.00}, min {scores.Min():0.00};" +
                        $" histogram (tenths from 0.4) {string.Join(" ", Enumerable.Range(4, 6).Select(t => scores.Count(v => v >= t / 10.0 && (t == 9 || v < (t + 1) / 10.0))))}");
        o.WriteLine($"    by species: {string.Join(", ", swept.GroupBy(e => e.Sprite!.Value.Species.Name).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))};" +
                    $" by animation: {string.Join(", ", swept.GroupBy(e => e.Sprite!.Value.Frame.Anim).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
        o.WriteLine($"    cost: {detectors.Sum(d => d.SweepMs) / frames:0.00} ms per frame mean ({detectors.Sum(d => d.SweepUnderMs) / frames:0.00} under unmatched blobs); placements probed {detectors.Sum(d => d.Search?.Probed ?? 0) / (double)frames:0} per frame, fully scored {detectors.Sum(d => d.Search?.Scored ?? 0) / (double)frames:0.0}");

        // Damage numbers: each track once (its first sighting), by direction.
        var damage = all.Where(e => e.Damage is not null).GroupBy(e => e.TrackId).Select(g => g.First()).ToList();
        o.WriteLine($"  damage numbers: {all.Count(e => e.Damage is not null)} detections, {damage.Count} numbers (tracks):" +
                    $" {string.Join(", ", damage.GroupBy(d => d.Damage!.Family + " " + d.Damage.DirectionText).Select(g => $"{g.Key} {g.Count()}"))};" +
                    $" complete {damage.Count(d => d.Damage!.Complete)}, read fading (opacity under 0.95) {damage.Count(d => d.Damage!.Alpha < 0.95)}; {detectors.Sum(d => d.DamageMs) / frames:0.00} ms per frame mean" +
                    (watch.Damage is { } dr ? $" (regions {dr.RegionsTried / (double)frames:0.0} per frame, searched {dr.RegionsSearched / (double)frames:0.0} of {dr.RegionPixels / (double)Math.Max(1, dr.RegionsSearched):0} pixels, digit placements scored {dr.PlacementsScored / (double)frames:0.0})" : ""));
        foreach (var dir in new[] { DamageDirection.Dealt, DamageDirection.Taken, DamageDirection.Other })
        {
            var values = damage.Where(d => d.Damage!.Direction == dir).Select(d => d.Damage!.Digits + (d.Damage.Complete ? "" : "?")).ToList();
            if (values.Count > 0) o.WriteLine($"    {dir.ToString().ToLowerInvariant()}: {string.Join(" ", values)}");
        }
        // Taken damage against the HP the status bar lost between consecutive frames, around the number's first sighting.
        var hp = rows.Where(r => r.Hp is not null).Select(r => (r.Result.Frame, Hp: r.Hp!.Value)).ToList();
        int agree = 0, checkedN = 0;
        var mismatches = new List<string>();
        foreach (var d in damage.Where(d => d.Damage!.Direction == DamageDirection.Taken && d.Damage.Value is not null))
        {
            var k = hp.FindIndex(h => h.Frame >= d.FirstFrame);
            if (k < 0) continue;
            checkedN++;
            var drops = new List<long>();
            for (var j = Math.Max(1, k - 3); j <= Math.Min(hp.Count - 1, k + 1); j++) drops.Add(hp[j - 1].Hp - hp[j].Hp);
            if (drops.Contains(d.Damage!.Value!.Value)) agree++;
            else mismatches.Add($"{d.Damage.Digits} at seq {watch.SequenceOf(d.FirstFrame)} (HP drops {string.Join("/", drops)})");
        }
        o.WriteLine($"    taken damage equal to an HP drop of the status bar (from 3 frames before its first sighting to 1 after): {agree} of {checkedN}{(mismatches.Count > 0 ? $"; not: {string.Join(", ", mismatches.Take(8))}" : "")}");

        // The pet.
        var pets = all.Where(e => e.Kind == EntityKind.Pet).ToList();
        o.WriteLine($"  pet '{watch.Tracker.Names.PetName ?? "none"}': {pets.Select(e => e.TrackId).Distinct().Count()} track id(s) over the session; {pets.Count} frames with the pet, {pets.Count(e => e.Tag is not null)} located by its tag, {pets.Count(e => e.Tag is null)} without;" +
                    $" at most {done.Select(r => r.Result.Entities.Count(e => e.Kind == EntityKind.Pet)).DefaultIfEmpty(0).Max()} in a frame; {detectors.Sum(d => d.Tags.PetSplits)} tag boxes split at the pet's tag, {watch.Tracker.PetDuplicates} frames with two pet tags");
        o.WriteLine($"  own character: {detectors.Sum(d => d.OwnSlivers)} blobs sticking out of its box dropped as its own; the pet's tag taken off {detectors.Sum(d => d.PetTagsDetached)} blobs too large for the pet;" +
                    $" {detectors.Sum(d => d.DuplicateSprites)} duplicate matches of one mob dropped, {detectors.Sum(d => d.ExplainedBlobs)} blobs dropped as matched mobs and the pet run together");
        // Kills and dying.
        o.WriteLine($"  kills credited by EXP gains: {watch.Tracker.KillsCredited} of {detectors.Sum(d => d.ExpGainsLabelled)} gains equal to one species' exp ({detectors.Sum(d => d.ExpGains)} gains), {watch.Tracker.KillsUnmatched} found no live track of the species;" +
                    $" dying detections {all.Count(e => e.State == MobState.Dying)} ({all.Count(e => e.State == MobState.Dying && e.Identity?.Verified == true && e.Identity.Anim.StartsWith("die", StringComparison.Ordinal))} with the die pose matched)");
        var mobs = all.Where(e => e.Kind == EntityKind.Mob && e.Identity is not null).ToList();
        o.WriteLine($"  hitboxes: {mobs.Count(e => e.Hitbox is not null)} identified mob detections, {mobs.Count(e => e.HitboxFromClient)} from the client's lt/rb, {mobs.Count(e => e.Hitbox is not null && !e.HitboxFromClient)} from the sprite extent (frames without lt/rb);" +
                    $" carried identities {mobs.Count(e => !e.Identity!.Verified)}, carried for median {Pct(mobs.Where(e => !e.Identity!.Verified).Select(e => e.Identity!.CarriedFor).ToList(), 50):0.0} s, max {mobs.Where(e => !e.Identity!.Verified).Select(e => e.Identity!.CarriedFor).DefaultIfEmpty(0).Max():0.0} s");
        o.WriteLine($"  associations refused: {(watch.Tracker.Refused.Count == 0 ? "none" : string.Join(", ", watch.Tracker.Refused.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}")))}");
        if (watch.Tracker.Names.PetMismatch is { } pm) o.WriteLine($"  pet check: {pm}");
    }


    private static bool On(double? d) => d is { } v && Math.Abs(v) <= EntityTracker.OnFootholdUnits;

    /// <summary>Sprite verification: how many mob candidates matched, the species per map against the spawn list, and the score distributions.</summary>
    private static void Sprites(TextWriter o, EntityWatch watch, List<EntityWatch.Row> done, List<DetectedEntity> mobs, List<EntityDetector> detectors)
    {
        var all = done.SelectMany(r => r.Result.Entities).ToList();
        var matched = mobs.Count(e => e.Sprite is not null);
        o.WriteLine($"  sprite verification: {matched}/{mobs.Count} = {Frac(matched, mobs.Count)} of mob detections matched a sprite in their frame;" +
                    $" {mobs.Count(e => e.Identity is not null)}/{mobs.Count} = {Frac(mobs.Count(e => e.Identity is not null), mobs.Count)} carry an identity (matched now or earlier on the track)");
        var blobs = all.Where(e => e.Pixels > 0).ToList();
        o.WriteLine($"    foreground candidates with a blob: {blobs.Count}, matched {blobs.Count(e => e.Sprite is not null)} = {Frac(blobs.Count(e => e.Sprite is not null), blobs.Count)};" +
                    $" unverified mob detections by cause: {string.Join(", ", mobs.Where(e => e.Sprite is null).GroupBy(Cause).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");

        // Species per map, and the spawn-count sanity check.
        foreach (var g in done.GroupBy(r => r.MapId))
        {
            if (!watch.MobSets.TryGetValue(g.Key, out var set)) continue;
            var det = detectors.FirstOrDefault(d => d.Mosaic.MapId == g.Key);
            var ents = g.SelectMany(r => r.Result.Entities).Where(e => e.Kind == EntityKind.Mob).ToList();
            o.WriteLine($"  map {g.Key}: {g.Count()} frames; mob detections by species (matched in frame / with identity), spawns in the client's life list (and more proven at once in one frame), the most in one frame:");
            foreach (var e in set.Entries)
            {
                var now = ents.Count(x => x.Sprite?.Species.Id == e.Species.Id);
                var carried = ents.Count(x => x.Identity?.MobId == e.Species.Id);
                var cap = det?.CapOf(e.Species.Id) ?? e.Spawns;
                var proof = det is not null && det.SeenAtOnce.TryGetValue(e.Species.Id, out var p) && p.Count > e.Spawns ? $" ({p.Count} proven on frame {watch.SequenceOf(p.Frame)})" : "";
                var perFrame = g.Select(r => r.Result.Entities.Count(x => x.Kind == EntityKind.Mob && x.Identity?.MobId == e.Species.Id)).ToList();
                var max = perFrame.Count == 0 ? 0 : perFrame.Max();
                var over = perFrame.Count(n => n > cap);
                var overFrames = g.Where(r => r.Result.Entities.Count(x => x.Kind == EntityKind.Mob && x.Identity?.MobId == e.Species.Id) > cap).Select(r => watch.SequenceOf(r.Result.Frame)).Take(6).ToList();
                var beyondList = perFrame.Count(n => n > e.Spawns);
                o.WriteLine($"    {e.Species.Name,-16} {now,5} / {carried,5}   spawns {e.Spawns,2}{proof}  most in one frame {max,2}{(beyondList > 0 && over == 0 ? $", more than the life list on {beyondList} frames" : "")}" +
                            $"{(over > 0 ? $"  OVER THE CAP on {over} frames ({string.Join(", ", overFrames)}...)" : "")}");
            }
            var unverified = ents.Count(x => x.Identity is null);
            o.WriteLine($"    {"(no identity)",-16} {unverified,5}");
            var bases = ents.Where(x => x.Sprite is not null).GroupBy(x => x.Sprite!.Value.Basis).OrderBy(b => b.Key).Select(b => $"{b.Key.ToString().ToLowerInvariant()} {b.Count()}");
            o.WriteLine($"    species of the matched detections rests on: {string.Join(", ", bases)} (shape: no recolour shares the frame; pixels: settled on the pixels where recolours differ; track, spawns: too few telling pixels)");
        }

        // Score distributions from the diagnostics: every candidate scored against every species of its map.
        var diags = detectors.SelectMany(d => d.Diagnostics).ToList();
        if (diags.Count == 0) return;
        // Debug: OAKSOUL_SPRITE_CSV=file writes every scored candidate with its best placement per species, for joining
        // with an independent search offline.
        if (Environment.GetEnvironmentVariable("OAKSOUL_SPRITE_CSV") is { } csv)
            File.WriteAllLines(csv, diags.SelectMany(d => d.BestBySpecies.Values.Select(b =>
                $"{watch.SequenceOf(d.Frame)},{d.Kind},{d.Box.X},{d.Box.Y},{d.Box.Width},{d.Box.Height},{(d.TagText ?? "").Replace(',', ' ')},{(d.Accepted is { } a ? $"{a.Species.Id}" : "")}," +
                $"{b.Species.Id},{b.Frame.Key},{b.Left},{b.Top},{b.Feet.X},{b.Feet.Y},{b.Matches},{b.Compared},{b.Tint},{b.Fogged},{b.Fog}")));
        var accepted = diags.Where(d => d.Accepted is not null).ToList();
        var acceptedScores = accepted.Select(d => d.Accepted!.Value.Score).ToList();
        var wrongForAccepted = accepted.Select(d => d.BestBySpecies.Where(b => b.Key != d.Accepted!.Value.Species.Id).Select(b => b.Value.Score).DefaultIfEmpty(0).Max()).ToList();
        var labelled = diags.Where(d => d.TagSpecies is not null && d.BestBySpecies.ContainsKey(d.TagSpecies.Value)).ToList();
        var tagTrue = labelled.Select(d => d.BestBySpecies[d.TagSpecies!.Value].Score).ToList();
        var tagWrong = labelled.Select(d => d.BestBySpecies.Where(b => b.Key != d.TagSpecies).Select(b => b.Value.Score).DefaultIfEmpty(0).Max()).ToList();
        var unmatched = diags.Where(d => d.Accepted is null).Select(d => d.BestBySpecies.Values.Select(b => b.Score).DefaultIfEmpty(0).Max()).ToList();
        o.WriteLine($"  sprite scores (share of a frame's visible opaque pixels equal to the screen pixel, as drawn, frozen or through steam; threshold {SpriteMatcher.Threshold:0.00}), {diags.Count} candidates scored against every species of their map:");
        o.WriteLine($"    {"",-34} {"n",5}  {"p5",5} {"p10",5} {"p50",5} {"p90",5} {"p95",5} {"max",5}");
        void Line(string label, List<double> v) =>
            o.WriteLine($"    {label,-34} {v.Count,5}  {Pct(v, 5),5:0.00} {Pct(v, 10),5:0.00} {Pct(v, 50),5:0.00} {Pct(v, 90),5:0.00} {Pct(v, 95),5:0.00} {(v.Count == 0 ? 0 : v.Max()),5:0.00}");
        Line("accepted match", acceptedScores);
        Line("  best other species, same cand.", wrongForAccepted);
        Line("tag-labelled: the tag's species", tagTrue);
        Line("tag-labelled: best other species", tagWrong);
        Line("unmatched: best of any species", unmatched);
        // Label-free negatives: candidates the tracker made a pet or player (by their tags) or an effect (floating, brief).
        var negatives = diags.Where(d => d.Kind is EntityKind.Pet or EntityKind.Player or EntityKind.Effect && d.Accepted is null || d.Kind is EntityKind.Pet or EntityKind.Player)
            .Select(d => d.BestBySpecies.Values.Select(b => b.Score).DefaultIfEmpty(0).Max()).ToList();
        Line("pet, player, effect: best of any", negatives);
        o.WriteLine($"    histogram (tenths)        {string.Join(" ", Enumerable.Range(0, 10).Select(i => $"{i / 10.0:0.0}+".PadLeft(5)))}");
        void Hist(string label, List<double> v) => o.WriteLine($"    {label,-25} {string.Join(" ", Histogram(v).Select(n => n.ToString().PadLeft(5)))}");
        Hist("accepted", acceptedScores);
        Hist("best other species", wrongForAccepted);
        Hist("tag's species", tagTrue);
        Hist("tag: best other", tagWrong);
        Hist("unmatched best", unmatched);
        Hist("pet/player/effect best", negatives);
        var margin = accepted.Select((d, i) => acceptedScores[i] - wrongForAccepted[i]).ToList();
        o.WriteLine($"    matches dropped by the spawn cap: {detectors.Sum(d => d.SpawnCapped)}, carried identities held to it: {watch.Tracker.CappedIdentities}; accepted with a rival species in the contest: {accepted.Count(d => d.Accepted!.Value.Rival is not null)}," +
                    $" won by less than 0.05: {accepted.Count(d => d.Accepted!.Value.Rival is not null && d.Accepted.Value.Score - d.Accepted.Value.RivalScore < 0.05)}");
        o.WriteLine($"    margin accepted minus best other species: p5 {Pct(margin, 5):0.00}, median {Pct(margin, 50):0.00}; accepted below their best other species: {margin.Count(m => m < 0)}");
        var tagAgree = labelled.Count(d => d.Accepted is { } a && a.Species.Id == d.TagSpecies);
        var tagDisagree = labelled.Count(d => d.Accepted is { } a && a.Species.Id != d.TagSpecies);
        o.WriteLine($"    tag-labelled candidates: {labelled.Count}; accepted as the tag's species {tagAgree}, as another species {tagDisagree}, unmatched {labelled.Count - tagAgree - tagDisagree}");
        // What a different threshold would have done.
        foreach (var t in new[] { 0.05, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.6 })
            o.WriteLine($"    at threshold {t:0.0#}: tag-labelled true {Frac(tagTrue.Count(s => s >= t), tagTrue.Count)} pass, best other species {Frac(tagWrong.Count(s => s >= t), tagWrong.Count)} pass;" +
                        $" unmatched candidates reaching it {unmatched.Count(s => s >= t)}; pet/player/effect candidates reaching it {Frac(negatives.Count(s => s >= t), negatives.Count)}");
        o.WriteLine($"    accepted by animation: {string.Join(", ", accepted.GroupBy(d => d.Accepted!.Value.Frame.Anim).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
        var acc = accepted.Select(d => d.Accepted!.Value).ToList();
        o.WriteLine($"    accepted by colour state: frozen {acc.Count(m => m.Tint == SpriteTint.Frozen)} of {acc.Count}; with steam explaining some pixels {acc.Count(m => m.Fogged > 0)}" +
                    $" (over 10% of the agreeing pixels {acc.Count(m => m.Fogged > 0.1 * m.Matches)}); exact on every visible opaque pixel {acc.Count(m => m.Matches == m.Compared)}");
        // The gap: the best any pet, player or effect candidate reached against the weakest tag-labelled mob of its own species.
        foreach (var d in diags.Where(d => d.Kind is EntityKind.Pet or EntityKind.Player or EntityKind.Effect && d.BestBySpecies.Count > 0)
                     .OrderByDescending(d => d.BestBySpecies.Values.Max(b => b.Score)).Take(6))
        {
            var b = d.BestBySpecies.Values.MaxBy(x => x.Score);
            o.WriteLine($"      {d.Kind,-6} frame {watch.SequenceOf(d.Frame)} box {d.Box.Width}x{d.Box.Height}+{d.Box.X}+{d.Box.Y}{(d.TagText is { } tt ? $" tag '{tt}'" : "")}: best {b}{(d.Accepted is { } a ? $"; accepted {a}" : "")}");
        }
        o.WriteLine($"    gap: pet/player/effect candidates at most {(negatives.Count == 0 ? 0 : negatives.Max()):0.000}; tag-labelled mobs, own species, at least {(tagTrue.Count == 0 ? 0 : tagTrue.Min()):0.000} (p5 {Pct(tagTrue, 5):0.000});" +
                    $" accepted matches at least {(acceptedScores.Count == 0 ? 0 : acceptedScores.Min()):0.000}");
    }

    private static string Cause(DetectedEntity e) =>
        e.Pixels == 0 ? "tag only" : e.Identity is not null ? "carried by track" : e.Tag is not null ? "tagged" : e.Box.Width > 160 || e.Box.Height > 150 ? "large box" : "no frame passed";

    private static int[] Histogram(List<double> values)
    {
        var h = new int[10];
        foreach (var v in values) h[Math.Clamp((int)(v * 10), 0, 9)]++;
        return h;
    }

    /// <summary>Track statistics, by kind and for mob tracks by identity.</summary>
    private static void Tracks(TextWriter o, EntityWatch watch, List<EntityWatch.Row> rows)
    {
        var tracks = watch.Tracker.Finished;
        o.WriteLine($"  tracks: {tracks.Count}");
        foreach (var k in Enum.GetValues<EntityKind>())
        {
            var lengths = tracks.Where(t => t.Kind == k).Select(t => (double)t.Frames).ToList();
            if (lengths.Count == 0) { o.WriteLine($"    {k,-8} none"); continue; }
            o.WriteLine($"    {k,-8} {lengths.Count,5} tracks, length median {Pct(lengths, 50):0} p90 {Pct(lengths, 90):0} max {lengths.Max():0} frames");
        }
        var mobTracks = tracks.Where(t => t.Kind == EntityKind.Mob).ToList();
        var identified = mobTracks.Where(t => t.MobId is not null).ToList();
        var anonymous = mobTracks.Where(t => t.MobId is null).ToList();
        string Stat(List<EntityTracker.TrackSummary> ts)
        {
            var l = ts.Select(t => (double)t.Frames).ToList();
            var d = ts.Select(t => t.LastTime - t.FirstTime).ToList();
            return l.Count == 0 ? "none" : $"{l.Count} tracks, length median {Pct(l, 50):0} p90 {Pct(l, 90):0} max {l.Max():0} keyframes, duration median {Pct(d, 50):0.0} s; {l.Sum():0} sightings";
        }
        o.WriteLine($"    mob tracks with a sprite identity: {Stat(identified)}");
        o.WriteLine($"    mob tracks without one:            {Stat(anonymous)}");
        foreach (var g in identified.GroupBy(t => t.MobName).OrderByDescending(g => g.Sum(t => t.Frames)))
            o.WriteLine($"      {g.Key,-16} {Stat(g.ToList())}");
        // Fragmentation: an identified track that ends and a new track of the same species that starts close by soon after.
        var index = rows.Select((r, i) => (r.Result.Frame, i)).ToDictionary(p => p.Frame, p => p.i);
        var broken = identified.Count(t => identified.Any(u => u.Id != t.Id && u.MobId == t.MobId && u.MapId == t.MapId && index.TryGetValue(u.FirstFrame, out var uf)
            && index.TryGetValue(t.LastFrame, out var tl) && uf > tl && uf <= tl + 4 && u.FirstWorld.DistanceTo(t.LastWorld) <= 150));
        o.WriteLine($"    identified tracks followed within 4 keyframes and 150 units by a new track of the same species (a break, not a kill): {broken}/{identified.Count}");
        // Sighting-weighted length: the track a random mob sighting belongs to.
        var weighted = mobTracks.SelectMany(t => Enumerable.Repeat((double)t.Frames, t.Frames)).ToList();
        o.WriteLine($"    sighting-weighted mob track length: median {Pct(weighted, 50):0} p90 {Pct(weighted, 90):0} keyframes");
    }

    /// <summary>Kill consistency against EXP changes, with the species of the track that ended.</summary>
    private static void Kills(TextWriter o, EntityWatch watch, List<EntityWatch.Row> rows)
    {
        var frames = rows.Select(r => r.Result.Frame).ToList();
        var index = frames.Select((f, i) => (f, i)).ToDictionary(p => p.f, p => p.i);
        var expChanges = new List<(int Row, long Delta)>();
        long? lastExp = null;
        int? lastMap = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].MapId != lastMap) { lastExp = null; lastMap = rows[i].MapId; }
            if (rows[i].Exp is not { } e) continue;
            if (lastExp is { } le && e > le) expChanges.Add((i, e - le));
            lastExp = e;
        }
        var mobTracks = watch.Tracker.Finished.Where(t => t.Kind == EntityKind.Mob).ToList();
        o.WriteLine($"  EXP changes: {expChanges.Count}");
        if (expChanges.Count == 0) return;
        var vanished = mobTracks.Where(t => !mobTracks.Any(u => u.Id != t.Id && index.TryGetValue(u.FirstFrame, out var uf) && index.TryGetValue(t.LastFrame, out var tl) &&
            uf > tl && uf <= tl + ReacquireFrames && u.FirstWorld.DistanceTo(t.LastWorld) <= ReacquireUnits && (u.MobId is null || t.MobId is null || u.MobId == t.MobId))).ToList();
        var hit = vanished.Where(t => t.Hit).ToList();
        var identified = vanished.Where(t => t.MobId is not null).ToList();
        List<EntityTracker.TrackSummary> EndedNear(List<EntityTracker.TrackSummary> set, int i, int from, int to)
        {
            var own = rows[Math.Clamp(i, 0, rows.Count - 1)].Own ?? rows.Take(i + 1).LastOrDefault(r => r.Own is not null)?.Own;
            return set.Where(t => t.MapId == rows[Math.Clamp(i, 0, rows.Count - 1)].MapId && index.TryGetValue(t.LastFrame, out var li) && li >= from && li <= to
                && (own is null || t.LastWorld.DistanceTo(own.Value) <= KillRadius)).ToList();
        }
        foreach (var (label, set) in new[] { ("any mob track end", mobTracks), ("a mob disappearance (end not re-acquired)", vanished),
                     ("a hit mob's disappearance (level tag or HP bar seen)", hit), ("an identified mob's disappearance", identified) })
        {
            int strict = 0, loose = 0, baseline = 0, looseBaseline = 0;
            foreach (var (i, _) in expChanges)
            {
                if (EndedNear(set, i, i - KillWindowFrames, i - 1).Count > 0) strict++;
                if (EndedNear(set, i, i - KillWindowFrames - 1, i + 1).Count > 0) loose++;
            }
            for (var i = KillWindowFrames + 1; i < rows.Count - 1; i++)
            {
                if (EndedNear(set, i, i - KillWindowFrames, i - 1).Count > 0) baseline++;
                if (EndedNear(set, i, i - KillWindowFrames - 1, i + 1).Count > 0) looseBaseline++;
            }
            var n = rows.Count - KillWindowFrames - 2;
            o.WriteLine($"    {label} within {KillRadius} units in the {KillWindowFrames} keyframes before: {strict} = {Frac(strict, expChanges.Count)} (chance {Frac(baseline, n)});" +
                        $" from 3 before to 1 after: {loose} = {Frac(loose, expChanges.Count)} (chance {Frac(looseBaseline, n)})");
        }
        // The species killed: the identified disappearance nearest the character in the loose window, and the EXP it gave.
        var killed = new List<(long Delta, string? Species, int? MobId)>();
        foreach (var (i, delta) in expChanges)
        {
            var own = rows[i].Own;
            var cands = EndedNear(identified, i, i - KillWindowFrames - 1, i + 1);
            var best = own is { } ow ? cands.OrderBy(t => t.LastWorld.DistanceTo(ow)).FirstOrDefault() : cands.FirstOrDefault();
            killed.Add((delta, best?.MobName, best?.MobId));
        }
        o.WriteLine($"    killed species (nearest identified disappearance, 3 before to 1 after) with the EXP gained:");
        foreach (var g in killed.GroupBy(k => k.Species ?? "(none identified)").OrderByDescending(g => g.Count()))
        {
            var exp = g.First().MobId is { } id ? watch.MobLibrary?.Get(id)?.Exp : null;
            o.WriteLine($"      {g.Key,-18} {g.Count(),3}   EXP gains {string.Join(" ", g.GroupBy(k => k.Delta).OrderByDescending(x => x.Count()).Select(x => $"{x.Key}x{x.Count()}"))}{(exp is { } e ? $"   (client exp {e})" : "")}");
        }
        // The EXP a kill gives is the species' client exp (this server's rate is 1x: every gain on fx-combat equals one
        // of the map's species' exp), so each gain names the species killed: a label for the identities.
        var map = rows[expChanges[0].Row].MapId;
        var mobSet = watch.MobSets.GetValueOrDefault(map);
        if (mobSet is null) return;
        int labelled = 0, same = 0, otherOnly = 0, none = 0;
        double chance = 0;
        var perSpecies = new Dictionary<string, (int Kills, int Found)>();
        foreach (var (i, delta) in expChanges)
        {
            var species = mobSet.Entries.Where(e => e.Species.Exp == delta).Select(e => e.Species).ToList();
            if (species.Count != 1) continue;
            var sp = species[0];
            labelled++;
            var ended = EndedNear(identified, i, i - KillWindowFrames - 1, i + 1);
            var found = ended.Any(t => t.MobId == sp.Id);
            if (found) same++;
            else if (ended.Count > 0) otherOnly++;
            else none++;
            var (k, f) = perSpecies.GetValueOrDefault(sp.Name);
            perSpecies[sp.Name] = (k + 1, f + (found ? 1 : 0));
            // How often such a disappearance happens at any frame: the chance level for this species.
            var hits = 0;
            for (var j = KillWindowFrames + 1; j < rows.Count - 1; j++)
                if (rows[j].MapId == map && EndedNear(identified, j, j - KillWindowFrames - 1, j + 1).Any(t => t.MobId == sp.Id)) hits++;
            chance += (double)hits / Math.Max(1, rows.Count - KillWindowFrames - 2);
        }
        o.WriteLine($"    kills labelled by their EXP (gain = one species' client exp): {labelled} of {expChanges.Count}; an identified disappearance of that species near the character" +
                    $" (3 keyframes before to 1 after): {same} = {Frac(same, labelled)} (chance {(labelled == 0 ? 0 : 100 * chance / labelled):0.0}%), only of another species {otherOnly}, none identified {none}");
        o.WriteLine($"      by species (kills / found): {string.Join(", ", perSpecies.OrderByDescending(p => p.Value.Kills).Select(p => $"{p.Key} {p.Value.Kills}/{p.Value.Found}"))}");
    }

    private static void Drops(TextWriter o, EntityWatch watch, List<EntityWatch.Row> done)
    {
        var tracks = watch.Tracker.Finished;
        var drops = tracks.Where(t => t.Kind == EntityKind.Drop).ToList();
        var dets = done.SelectMany(r => r.Result.Entities).Where(e => e.Drop is not null).ToList();
        o.WriteLine($"  drops: {drops.Count} drop tracks, {drops.Count(t => t.DropItem is not null)} identified by icon ({string.Join(", ", drops.Where(t => t.DropItem is not null).GroupBy(t => t.DropItem).Select(g => $"{g.Key} x{g.Count()}"))});" +
                    $" icon matches in {dets.Count} detections, scores {string.Join(" ", dets.Select(e => e.Drop!.Score.ToString("0.00")).Take(20))}");
        // Drops appear where a mob died: how many start within a keyframe before to two after an EXP gain.
        var rows = watch.Rows;
        var index = rows.Select((r, i) => (r.Result.Frame, i)).ToDictionary(p => p.Frame, p => p.i);
        var gains = new List<int>();
        for (var i = 1; i < rows.Count; i++)
            if (rows[i].Exp is { } e && rows.Take(i).LastOrDefault(r => r.Exp is not null)?.Exp is { } prev && e > prev && rows[i].MapId == rows[i - 1].MapId) gains.Add(i);
        var afterKill = drops.Count(t => index.TryGetValue(t.FirstFrame, out var fi) && gains.Any(g => fi >= g - 1 && fi <= g + KillWindowFrames));
        o.WriteLine($"    drop tracks starting from a keyframe before to {KillWindowFrames} after an EXP gain: {afterKill}/{drops.Count}; lifetime median {(drops.Count == 0 ? 0 : Pct(drops.Select(t => t.LastTime - t.FirstTime).ToList(), 50)):0.0} s");
        if (watch.Drops is { Scores.Count: > 0 } dm)
        {
            var sc = dm.Scores.Select(x => x.Score).ToList();
            o.WriteLine($"    small candidates on a surface tried as drops: {sc.Count}; best icon score p50 {Pct(sc, 50):0.00} p90 {Pct(sc, 90):0.00} p99 {Pct(sc, 99):0.00} max {sc.Max():0.00};" +
                        $" histogram (tenths) {string.Join(" ", Histogram(sc))}; threshold {DropMatcher.Threshold:0.00}");
            foreach (var x in dm.Scores.OrderByDescending(x => x.Score).Take(8))
                o.WriteLine($"      {x.Score:0.00} {x.ItemId} frame {watch.SequenceOf(x.Frame)} box {x.Box.Width}x{x.Box.Height}+{x.Box.X}+{x.Box.Y}");
        }
        foreach (var t in drops)
            o.WriteLine($"    #{t.Id} {(t.DropItem ?? "unidentified"),-10} frames {watch.SequenceOf(t.FirstFrame)}..{watch.SequenceOf(t.LastFrame)} ({t.Frames} sightings) at {t.LastWorld}{(t.NearOwnAtEnd ? ", own character near at the end" : "")}");
    }

    private static void Tags(TextWriter o, EntityWatch watch, List<EntityWatch.Row> done, List<DetectedEntity> mobs, List<EntityDetector> detectors)
    {
        o.WriteLine($"  mob detections with a readable tag in that frame: {mobs.Count(e => e.Tag is not null && e.Name is not null)}/{mobs.Count} = {Frac(mobs.Count(e => e.Tag is not null && e.Name is not null), mobs.Count)}");
        var mobTracks = watch.Tracker.Finished.Where(t => t.Kind == EntityKind.Mob).ToList();
        o.WriteLine($"    mob tracks that showed a tag at some point: {mobTracks.Count(t => t.Tag is not null)}/{mobTracks.Count} = {Frac(mobTracks.Count(t => t.Tag is not null), mobTracks.Count)}");
        o.WriteLine($"  distinct tag texts ({detectors.Sum(d => d.Tags.OcrCalls)} OCR calls, {detectors.Sum(d => d.Tags.OcrMilliseconds):0} ms total), with the sprite identity of the same detection:");
        foreach (var g in done.SelectMany(r => r.Result.Entities).Where(e => e.Tag is not null).GroupBy(e => (e.Kind, e.Tag, Species: e.Identity?.Name)).OrderByDescending(g => g.Count()).Take(30))
            o.WriteLine($"    {g.Key.Kind,-7} '{g.Key.Tag}' x{g.Count()}{(g.Key.Species is { } s ? $"  [{s}]" : "")}");
        o.WriteLine($"  mob names learned: {string.Join(", ", watch.Tracker.Names.MobNames)}; pet {watch.Tracker.Names.PetName ?? "none"}; the pet's tag taken off a verified mob or drop {detectors.Sum(d => d.CharacterTagsMoved)} times");
        var petTagged = done.SelectMany(r => r.Result.Entities).Where(e => watch.Tracker.Names.IsCharacterTag(e.Tag)).ToList();
        o.WriteLine($"    detections under the pet's or the own tag by kind: {(petTagged.Count == 0 ? "none" : string.Join(", ", petTagged.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()).Select(x => $"{x.Key.ToString().ToLowerInvariant()} {x.Count()}")))}");
    }

    /// <summary>The own position against footholds before (dot and tag) and after the registered camera is fed back.</summary>
    private static void CameraFeedback(TextWriter o, EntityWatch watch, List<EntityWatch.Row> rows)
    {
        var both = rows.Where(r => r.OwnRefined is not null && r.OwnRaw is not null && r.Source == WorldSource.DotAndTag).ToList();
        o.WriteLine($"  camera feedback: {rows.Count(r => r.OwnRefined is not null)} frames with a registered camera and the own tag ({both.Count} also measured by dot and tag)");
        if (both.Count == 0) return;
        // The character stands on a surface or is in the air: only frames where some surface is close count.
        double? Surface(int mapId, PointD p) => watch.Footholds.TryGetValue(mapId, out var idx) ? idx.Nearest(p, 24)?.Distance : null;
        var raw = new List<double>();
        var snapped = new List<double>();
        var after = new List<double>();
        var dx = new List<double>();
        foreach (var r in both)
        {
            dx.Add(r.OwnRefined!.Value.X - r.OwnRaw!.Value.X);
            // Compare on frames where the refined position stands near a surface (not on a rope or mid-jump).
            if (Surface(r.MapId, r.OwnRefined.Value) is not { } a) continue;
            after.Add(Math.Abs(a));
            if (Surface(r.MapId, r.OwnRaw.Value) is { } b) raw.Add(Math.Abs(b)); else raw.Add(24);
            if (r.OwnSnapped is { } s && Surface(r.MapId, s) is { } c) snapped.Add(Math.Abs(c)); else snapped.Add(24);
        }
        string D(List<double> v) => $"median {Pct(v, 50):0.0}, p90 {Pct(v, 90):0.0}, max {(v.Count == 0 ? 0 : v.Max()):0.0}; exact (<0.5) {Frac(v.Count(x => x < 0.5), v.Count)}, within 2 {Frac(v.Count(x => x <= 2), v.Count)}";
        o.WriteLine($"    |own position - nearest surface| over {after.Count} frames near a surface (units):");
        o.WriteLine($"      before, dot and tag unsnapped: {D(raw)}");
        o.WriteLine($"      before, snapped by the tracker: {D(snapped)}");
        o.WriteLine($"      after, registered camera + tag: {D(after)}");
        var adx = dx.Select(Math.Abs).ToList();
        o.WriteLine($"    x shift of the own position by the feedback: |dx| median {Pct(adx, 50):0}, p90 {Pct(adx, 90):0}, max {(adx.Count == 0 ? 0 : adx.Max()):0} (the dot's cell is 16 units)");
    }

    private static void Cost(TextWriter o, List<EntityWatch.Row> done, List<EntityDetector> detectors)
    {
        var ms = done.Select(r => r.Result.Milliseconds).ToList();
        o.WriteLine($"  cost per frame excluding OCR and diagnostics: mean {ms.Average():0.0} ms, median {Pct(ms, 50):0.0}, p90 {Pct(ms, 90):0.0}, max {ms.Max():0.0}" +
                    $" (slowest: {string.Join(", ", done.OrderByDescending(r => r.Result.Milliseconds).Take(3).Select(r => $"seq {r.Sequence} {r.Result.Milliseconds:0}"))})");
        var frames = detectors.Sum(d => d.StageFrames);
        if (frames > 0)
        {
            var stages = EntityDetector.StageNames.Select((n, i) => $"{n} {detectors.Sum(d => d.StageMs[i]) / frames:0.00}");
            o.WriteLine($"    by stage (mean ms): {string.Join(", ", stages)}; wide searches {detectors.Sum(d => d.WideSearches)}");
            var sprite = detectors.SelectMany(d => d.SpriteStageMs).ToList();
            o.WriteLine($"    sprite stage per frame: median {Pct(sprite, 50):0.00} ms, p90 {Pct(sprite, 90):0.00}, max {(sprite.Count == 0 ? 0 : sprite.Max()):0.00};" +
                        $" frames scored {detectors.Sum(d => d.Matcher.FramesScored) / (double)frames:0.0} per frame, {detectors.Sum(d => d.Matcher.FramesSkippedBySize) / (double)frames:0} skipped by size");
            o.WriteLine($"    diagnostics (all species per candidate, report only): {detectors.Sum(d => d.DiagnosticMs) / frames:0.0} ms per frame");
        }
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
