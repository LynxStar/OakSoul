using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>One detection in one frame, before tracking: a foreground blob, a tag, or both.</summary>
public sealed class Candidate
{
    public required CaptureRect Box { get; init; }
    public required PointD Feet { get; init; }
    public required PointD World { get; init; }
    public FootholdHit? Foothold { get; init; }
    /// <summary>Signed vertical distance from the feet to the nearest surface at that x (positive: the surface is below), or null.</summary>
    public double? SurfaceDistance { get; init; }
    public int Pixels { get; init; }
    public TagBox? Tag { get; init; }
    public bool HpBar { get; set; }
    public bool TouchesOwn { get; init; }
    /// <summary>8x8 thumbnail of the blob's foreground luma, for telling a static drop from an animating mob.</summary>
    public byte[]? Thumb { get; init; }
    /// <summary>World distance to the own character, when known.</summary>
    public double? OwnDistance { get; init; }
}

/// <summary>
/// Nearest-neighbour association of candidates across frames with a short memory, and classification from the
/// evidence a track accumulates: its tag (decisive), whether it stands on a foothold, its size, how far it moved
/// since it appeared and how much its appearance changes. Gives every entity a stable id, an age, a velocity and the
/// frame it was first seen.
/// </summary>
public sealed class EntityTracker
{
    /// <summary>
    /// A track not matched for this long is dropped: two keyframes of fx-combat. A mob hidden behind an effect or the
    /// own character for a moment keeps its id; longer memory would let a killed mob's track grab whatever appears
    /// next nearby. Chosen, not tuned.
    /// </summary>
    public const double MaxMissedSeconds = 1.2;

    /// <summary>
    /// Association gate in world units: a fixed slack for box jitter plus motion. Mob tracks on fx-combat moved at a
    /// median 34 units per second, p90 100 and p99 177 (knock-back), so 160 per second; a track that has stood still
    /// for three sightings gets 60 per second, so a static drop does not jump to a blob appearing elsewhere.
    /// </summary>
    public const double GateBase = 30, GatePerSecond = 160, StillPerSecond = 60;

    /// <summary>Feet within this many units of a foothold surface count as standing on it (the world tracker's stand tolerance).</summary>
    public const double OnFootholdUnits = 6;

    /// <summary>
    /// Largest box of a drop: item and meso icons are at most 32x32, plus a pixel of outline and the open's rounding.
    /// Snails are about 37x28 on fx-combat, so size alone does not separate them; motion and appearance must.
    /// </summary>
    public const int DropMaxSize = 36;

    /// <summary>
    /// Mean absolute thumbnail difference (luma levels) between consecutive sightings below which an entity looks
    /// unchanged. A drop repeats its icon exactly; the value is a guess, since fx-combat holds almost no drops that
    /// lie still long enough to measure (the pet loots them within a second).
    /// </summary>
    public const double StableAppearance = 12;

    /// <summary>World units a drop may move between sightings (it may settle a pixel or two after the bounce); a guess, like <see cref="StableAppearance"/>.</summary>
    public const double StaticUnits = 3;

    /// <summary>A plain-named tag read this close to the own character counts towards it being the pet's (the pet trails within about 100 units).</summary>
    public const double PetRadius = 160;

    /// <summary>Boxes larger than this are skill effects, not sprites (the largest tagged mob box on fx-combat was 94x49).</summary>
    public const int EffectMinWidth = 220, EffectMinHeight = 200;

    /// <summary>
    /// Largest change of foreground area between sightings of one track. Between consecutive sightings of mob tracks
    /// on fx-combat the area changed by a factor of median 1.3, p90 2.8, p99 3.9 (animation and partial occlusion); a
    /// drop is a tenth of the mob that dropped it.
    /// </summary>
    public const int MaxAreaRatio = 4;

    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    /// <summary>Every track that ever existed, for the report: id, kind history, first and last frame.</summary>
    public List<TrackSummary> Finished { get; } = [];

    /// <summary>Names the tags have shown: mob names, the own and the pet's name.</summary>
    public TagNames Names { get; } = new();

    public sealed record TrackSummary(int Id, EntityKind Kind, long FirstFrame, long LastFrame, double FirstTime, double LastTime, int Frames,
        PointD FirstWorld, PointD LastWorld, string? Tag, bool NearOwnAtEnd, bool Hit);

    private sealed class Track
    {
        public int Id;
        public long FirstFrame, LastFrame;
        public double FirstTime, LastTime;
        public int Age;
        public PointD FirstWorld, World;
        public CaptureRect Box;
        public readonly Queue<(double T, PointD W)> History = new();
        public string? Tag, Name;
        public readonly Dictionary<string, int> NameVotes = new(StringComparer.OrdinalIgnoreCase);
        public int? Level;
        public int TaggedFrames;
        public byte[]? Thumb;
        public double LastAppearance = double.NaN;
        public int Animated, Moved;
        public bool HpBar;
        public readonly double[] Votes = new double[6];
        public EntityKind Kind;
        public double Confidence;
        public bool NearOwn;
        public int Pixels;
    }

    public List<DetectedEntity> Update(List<Candidate> candidates, long frame, double time)
    {
        // Greedy assignment by distance to each track's predicted position.
        var pairs = new List<(double Cost, int T, int C)>();
        for (var t = 0; t < _tracks.Count; t++)
        {
            var tr = _tracks[t];
            var dt = Math.Max(0.01, time - tr.LastTime);
            var v = Velocity(tr);
            var predicted = new PointD(tr.World.X + v.X * Math.Min(dt, 0.6), tr.World.Y + v.Y * Math.Min(dt, 0.6));
            // Something that has been standing still gets a tighter gate, so a static drop does not jump to a blob
            // that appears elsewhere while it is briefly hidden.
            var gate = GateBase + (tr.Age >= 3 && tr.Moved == 0 ? StillPerSecond : GatePerSecond) * dt;
            for (var c = 0; c < candidates.Count; c++)
            {
                var cand = candidates[c];
                var name = CanonicalName(cand.Tag?.Text);
                if (name is not null && tr.Name is not null && !string.Equals(name, tr.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var d = predicted.DistanceTo(cand.World);
                if (d > gate) continue;
                // A killed mob's drop appears where it stood: a blob a fraction of the track's size is something else.
                if (cand.Pixels > 0 && tr.Pixels > 0 && (cand.Pixels * MaxAreaRatio < tr.Pixels || tr.Pixels * MaxAreaRatio < cand.Pixels)) continue;
                var cost = d + (name is not null && name == tr.Name ? -50 : 0);
                pairs.Add((cost, t, c));
            }
        }
        var trackUsed = new bool[_tracks.Count];
        var candTrack = new int[candidates.Count];
        Array.Fill(candTrack, -1);
        foreach (var (_, t, c) in pairs.OrderBy(p => p.Cost))
        {
            if (trackUsed[t] || candTrack[c] >= 0) continue;
            trackUsed[t] = true;
            candTrack[c] = t;
        }

        var result = new List<DetectedEntity>(candidates.Count);
        for (var c = 0; c < candidates.Count; c++)
        {
            var cand = candidates[c];
            Track tr;
            if (candTrack[c] >= 0) tr = _tracks[candTrack[c]];
            else
            {
                tr = new Track { Id = _nextId++, FirstFrame = frame, FirstTime = time, FirstWorld = cand.World, World = cand.World };
                _tracks.Add(tr);
            }
            Observe(tr, cand, frame, time);
            result.Add(new DetectedEntity(tr.Id, tr.Kind, tr.Confidence, cand.Box, cand.Feet, cand.World, cand.Foothold?.Foothold.Id,
                cand.SurfaceDistance, cand.Tag?.Text, tr.Level, tr.Name,
                tr.Age, tr.FirstFrame, tr.FirstTime, Velocity(tr), cand.Pixels, cand.HpBar));
        }
        // Forget tracks not seen for a while.
        for (var t = _tracks.Count - 1; t >= 0; t--)
        {
            var tr = _tracks[t];
            if (time - tr.LastTime <= MaxMissedSeconds) continue;
            Finished.Add(Summarize(tr));
            _tracks.RemoveAt(t);
        }
        return result;
    }

    /// <summary>Closes all live tracks (end of a session or a map change).</summary>
    public void Flush()
    {
        foreach (var tr in _tracks) Finished.Add(Summarize(tr));
        _tracks.Clear();
    }

    /// <summary>The name a tag read stands for, after stripping the own and pet names and matching known mob names.</summary>
    private string? CanonicalName(string? text)
    {
        if (text is null) return null;
        var (_, name) = TagDetector.Parse(Names.Strip(text));
        if (name is null) return null;
        return Names.MatchMob(name) ?? (Names.IsPet(name) ? Names.PetName : name);
    }

    /// <summary>A finished track's kind, revised with the names learned since: an early partial read ("Dark Axe") is a mob once "Dark Axe Stump" is known.</summary>
    private TrackSummary Summarize(Track tr) =>
        new(tr.Id, tr.Kind is EntityKind.Player or EntityKind.Unknown && Names.MatchMob(tr.Name) is not null ? EntityKind.Mob
            : tr.Kind is EntityKind.Player && Names.IsPet(tr.Name) ? EntityKind.Pet : tr.Kind, tr.FirstFrame, tr.LastFrame, tr.FirstTime, tr.LastTime, tr.Age, tr.FirstWorld, tr.World, tr.Tag, tr.NearOwn, tr.Level is not null || tr.HpBar);

    private static PointD Velocity(Track tr)
    {
        if (tr.History.Count < 2) return new PointD(0, 0);
        var (t0, w0) = tr.History.Peek();
        var (t1, w1) = tr.History.Last();
        var dt = t1 - t0;
        return dt <= 0 ? new PointD(0, 0) : new PointD((w1.X - w0.X) / dt, (w1.Y - w0.Y) / dt);
    }

    private void Observe(Track tr, Candidate c, long frame, double time)
    {
        var moved = tr.Age > 0 ? tr.World.DistanceTo(c.World) : 0;
        tr.Age++;
        tr.LastFrame = frame;
        tr.LastTime = time;
        tr.World = c.World;
        tr.Box = c.Box;
        if (c.Pixels > 0) tr.Pixels = c.Pixels;
        tr.History.Enqueue((time, c.World));
        while (tr.History.Count > 2 && time - tr.History.Peek().T > 1.5) tr.History.Dequeue();
        if (c.Thumb is not null && tr.Thumb is not null) tr.LastAppearance = ThumbDiff(c.Thumb, tr.Thumb);
        else tr.LastAppearance = double.NaN;
        if (c.Thumb is not null) tr.Thumb = c.Thumb;
        if (!double.IsNaN(tr.LastAppearance) && tr.LastAppearance > StableAppearance) tr.Animated++;
        if (moved > StaticUnits) tr.Moved++;
        if (c.HpBar) tr.HpBar = true;
        tr.NearOwn = c.OwnDistance is { } od && od <= PetRadius;
        if (c.Tag is { Text: { } text })
        {
            tr.TaggedFrames++;
            tr.Tag = text;
            var stripped = Names.Strip(text);
            var (level, name) = TagDetector.Parse(stripped);
            string? vote = null;
            if (level is { } lv and > 0 && name is not null)
            {
                Names.SawMob(name, lv);
                tr.Level = lv;
                vote = Names.MatchMob(name, lv) ?? name;
            }
            else if (level is not null)
            {
                // A badge was there but its number (or the name) did not read.
                if (level > 0 || tr.Level is null) tr.Level = level;
                if (name is not null) vote = Names.MatchMob(name) ?? name;
            }
            else if (name is not null && Names.MatchMob(name) is { } mob) vote = mob;
            else if (name is not null)
            {
                if (tr.NearOwn) Names.SawPlainNearOwn(name);
                vote = Names.IsPet(name) ? Names.PetName : name;
            }
            else if (stripped.Length == 0 && Names.PetName is { } pet && text.Contains(pet, StringComparison.OrdinalIgnoreCase))
                vote = pet; // only the pet's name: the pet's own tag
            if (vote is not null)
            {
                // The track's name is the one its tags showed most often, so one merged or misattributed read does
                // not rename it.
                tr.NameVotes[vote] = tr.NameVotes.GetValueOrDefault(vote) + 1;
                tr.Name = tr.NameVotes.MaxBy(v => v.Value).Key;
            }
        }

        var (kind, confidence) = Classify(tr, c);
        // Votes decay so a track can change its mind, but a single odd frame does not flip it.
        for (var k = 0; k < tr.Votes.Length; k++) tr.Votes[k] *= 0.7;
        tr.Votes[(int)kind] += confidence;
        var best = 0;
        for (var k = 1; k < tr.Votes.Length; k++) if (tr.Votes[k] > tr.Votes[best]) best = k;
        // A tag is decisive at once.
        if (kind is EntityKind.Mob or EntityKind.Player or EntityKind.Pet && confidence >= 0.85) best = (int)kind;
        tr.Kind = (EntityKind)best;
        var total = tr.Votes.Sum();
        // The share of the (decayed) votes behind the reported kind, scaled by this frame's own confidence when it agrees.
        tr.Confidence = Math.Round(total <= 0 ? 0 : tr.Votes[best] / total * (best == (int)kind ? confidence : 0.6), 2);
    }

    private (EntityKind Kind, double Confidence) Classify(Track tr, Candidate c)
    {
        // A level badge (even one whose number did not read) or a known mob name.
        if (tr.Level is not null || Names.MatchMob(tr.Name) is not null) return (EntityKind.Mob, 0.95);
        if (tr.Name is not null) return Names.IsPet(tr.Name) ? (EntityKind.Pet, 0.9) : (EntityKind.Player, 0.85);
        if (c.HpBar || tr.HpBar) return (EntityKind.Mob, 0.8);

        var onFoothold = c.SurfaceDistance is { } sd && Math.Abs(sd) <= OnFootholdUnits;
        int w = c.Box.Width, h = c.Box.Height;
        if (w >= EffectMinWidth || h >= EffectMinHeight) return (EntityKind.Effect, 0.6);
        if (!onFoothold)
        {
            // Damage numbers and spell effects float; a mob in the air (knocked back, jumping) is the exception.
            var rising = Velocity(tr).Y < -40;
            if (c.TouchesOwn) return (EntityKind.Effect, 0.7);
            if (tr.Age <= 2 || rising) return (EntityKind.Effect, rising ? 0.75 : 0.55);
            return (EntityKind.Unknown, 0.4);
        }
        var small = w <= DropMaxSize && h <= DropMaxSize;
        var stable = !double.IsNaN(tr.LastAppearance) && tr.LastAppearance <= StableAppearance;
        if (small)
        {
            // Small pieces next to the own character are the pet's or the own sprite's edges, not loot.
            if (c.TouchesOwn) return (EntityKind.Unknown, 0.3);
            if (tr.Age >= 2 && tr.Moved == 0 && tr.Animated == 0 && stable) return (EntityKind.Drop, Math.Min(0.9, 0.5 + 0.1 * tr.Age));
            if (tr.Moved > 0 || tr.Animated > 0) return (EntityKind.Mob, 0.6);
            return (EntityKind.Unknown, 0.35);
        }
        if (c.TouchesOwn && tr.Age <= 1) return (EntityKind.Unknown, 0.4);
        return (EntityKind.Mob, tr.Moved > 0 || tr.Animated > 0 ? 0.75 : 0.6);
    }

    private static double ThumbDiff(byte[] a, byte[] b)
    {
        var sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}
