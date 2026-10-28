using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>One detection in one frame, before tracking: a foreground blob, a tag, or both.</summary>
public sealed class Candidate
{
    public required CaptureRect Box { get; set; }
    /// <summary>Where the candidate stands on screen: from its tag, its box, or (once matched) its sprite's origin.</summary>
    public required PointD Feet { get; set; }
    public required PointD World { get; set; }
    public FootholdHit? Foothold { get; set; }
    /// <summary>Signed vertical distance from the feet to the nearest surface at that x (positive: the surface is below), or null.</summary>
    public double? SurfaceDistance { get; set; }
    /// <summary><see cref="SurfaceDistance"/> as measured from the box or tag, before a sprite match moved the feet.</summary>
    public double? BoxSurfaceDistance { get; set; }
    /// <summary>The floor (foothold chain component) the candidate stands on or just above, if any.</summary>
    public int? Floor { get; set; }
    /// <summary>The sprite frame that verified the candidate as a mob, if one did.</summary>
    public SpriteMatch? Sprite { get; set; }
    /// <summary>The drop sprite that identified the candidate, if one did.</summary>
    public DropMatch? Drop { get; set; }
    public int Pixels { get; set; }
    public TagBox? Tag { get; set; }
    public bool HpBar { get; set; }
    public bool TouchesOwn { get; init; }
    /// <summary>8x8 thumbnail of the blob's foreground luma, for telling a static drop from an animating mob.</summary>
    public byte[]? Thumb { get; init; }
    /// <summary>World distance to the own character, when known.</summary>
    public double? OwnDistance { get; init; }
    /// <summary>A damage number read here (the candidate is then a damage number, whatever its blob was).</summary>
    public DamageMatch? Damage { get; set; }
    /// <summary>Found by the direct search along a foothold (no foreground blob, or a blob no box alignment matched).</summary>
    public bool Swept { get; set; }

    /// <summary>How the candidate was found: by the full search, or by verifying a track's prediction (see <see cref="EntityDetector"/>, the temporal pass).</summary>
    public FoundBy Found { get; set; }

    /// <summary>The track whose prediction this candidate verified (its own species' sprite at its predicted place): association takes that track first.</summary>
    public int? PredictedTrack { get; set; }
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

    /// <summary>
    /// Memory of a mob track whose sprite matched at least <see cref="IdentifiedSightings"/> times: long enough for a
    /// mob to walk behind another, behind the own character or under an effect and come out again. Past
    /// <see cref="MaxMissedSeconds"/> such a track is only taken up again by a candidate whose sprite matched the same
    /// species, so a killed mob's track cannot grab whatever appears next to it. Chosen, not tuned.
    /// </summary>
    public const double IdentifiedMemorySeconds = 5.0;

    public const int IdentifiedSightings = 2;

    /// <summary>
    /// Association cost (world units) added when a candidate's sprite names a different species than a track that is
    /// not yet sure of its own, and taken off when it names the same one. A track sure of its species (matched
    /// <see cref="IdentifiedSightings"/> times) never takes a candidate of another species.
    /// </summary>
    public const double IdentityPenalty = 60, IdentityBonus = 40;

    /// <summary>
    /// How far a mob of a known species may move between sightings: <see cref="GateBase"/> for jitter, plus this many times its
    /// walking speed (<see cref="MobSpecies.WalkSpeed"/>, from the client's speed field) for the time between, plus
    /// <see cref="KnockBack"/> when it was hit within <see cref="HitMemorySeconds"/> (a tag, an HP bar or the hit or die pose
    /// seen). On fx-combat-exact's keyframes matched move-to-move steps ran at a median 1.0 and p90 1.2 times the walking
    /// speed; the fastest (2.3 to 3.8 times) were knock-backs. A Dark Stump (35 units/s) gets 30 + 26 units in half a second.
    /// </summary>
    public const double SpeedFactor = 1.5, KnockBack = 80, HitMemorySeconds = 2;

    /// <summary>
    /// A carried identity (no sprite of its species matched in the frame) only moves onto a blob whose box is between these
    /// shares of the body rectangle of the track's last frame (a partly hidden mob shows less; two run together more), and
    /// whose height-to-width ratio is within <see cref="ShapeRatio"/> of the body's. The dead Dark Stump's track took up the
    /// 33x21 leaf it dropped (0.22 of its 62x51 body) and the Wild Boar's an ice shard 20x66 (0.37, but 4.7 times as tall
    /// for its width).
    /// </summary>
    public const double MinBodyShare = 0.25, MaxBodyShare = 2.5, ShapeRatio = 2.5;

    /// <summary>After a kill was credited to a track (<see cref="Kill"/>), only its own die pose continues it, for this long.</summary>
    public const double DeathHoldSeconds = 1.5;

    /// <summary>The species of the map by id (walking speed, mobility, frames), set by the detector; null leaves the species rules off.</summary>
    public Func<int, MobSpecies?>? Species { get; set; }

    /// <summary>The species ids a tag read names on this map (<see cref="MapMobSet.NamedLoose"/>), set by the detector.</summary>
    public Func<string?, IReadOnlyCollection<int>>? TagSpecies { get; set; }

    /// <summary>Associations refused since construction, by rule (tag, floor, speed, shape, absent, dead), for the report.</summary>
    public Dictionary<string, int> Refused { get; } = [];

    /// <summary>Kills credited to a track by an EXP gain, and gains that found no track of their species.</summary>
    public int KillsCredited { get; private set; }
    public int KillsUnmatched { get; private set; }

    /// <summary>The pet's one track id for the session (kept across maps), once the pet was seen.</summary>
    public int? PetTrackId => _petId;
    private int? _petId;
    private Track? _pet;

    /// <summary>Frames in which a candidate under the pet's tag continued the pet's track, and in which more than one claimed it.</summary>
    public int PetFrames { get; private set; }
    public int PetDuplicates { get; private set; }

    private long _update;
    private long _lastFull;

    /// <summary>
    /// How many mobs of a species (by id) one frame may show (the detector's <see cref="EntityDetector.CapOf"/>), or
    /// null. Identities carried by tracks beyond it (fewest verified sightings first) are left out of that frame's
    /// output, so the frame never shows more of a species than the cap.
    /// </summary>
    public Func<int, int>? SpeciesCap { get; set; }

    /// <summary>Carried identities left out by <see cref="SpeciesCap"/> since construction.</summary>
    public int CappedIdentities { get; private set; }

    private readonly List<Track> _tracks = [];
    private int _nextId = 1;

    /// <summary>Every track that ever existed, for the report: id, kind history, first and last frame.</summary>
    public List<TrackSummary> Finished { get; } = [];

    /// <summary>Names the tags have shown: mob names, the own and the pet's name.</summary>
    public TagNames Names { get; } = new();

    public sealed record TrackSummary(int Id, EntityKind Kind, long FirstFrame, long LastFrame, double FirstTime, double LastTime, int Frames,
        PointD FirstWorld, PointD LastWorld, string? Tag, bool NearOwnAtEnd, bool Hit, int? MobId = null, string? MobName = null, int Verified = 0,
        string? DropItem = null, int MapId = 0, bool Died = false);

    /// <summary>What the track a candidate most likely continues knows, for ordering the sprite search.</summary>
    /// <param name="TagName">The mob name the track's tags showed (only when a level badge or a known mob name backs it).</param>
    public sealed record TrackHint(int TrackId, int? MobId, string? TagName, SpriteMatch? LastSprite);

    /// <summary>
    /// A live identified mob track, for the temporal pass: its last match (frame, placement) and where that put its feet in
    /// the world, when it was, its velocity, floor and state.
    /// </summary>
    public sealed record LiveTrack(int TrackId, SpriteMatch Last, PointD LastWorld, double LastMatchTime, double LastSeenTime, PointD Velocity, int? Floor,
        bool Dead, double DeathTime, double HitTime, int Verified);

    /// <summary>
    /// The identified mob tracks whose sprite matched within <paramref name="memory"/> seconds (the pet excluded; a dead track
    /// only while its die pose may still show), most verified first.
    /// </summary>
    public List<LiveTrack> Live(double time, double memory) =>
        _tracks.Where(t => !t.IsPet && t.MobId is not null && t.LastSprite is not null && time - t.LastMatchTime <= memory && (!t.Dead || time - t.DeathTime <= DeathHoldSeconds))
            .OrderByDescending(t => t.Verified).ThenBy(t => t.Id)
            .Select(t => new LiveTrack(t.Id, t.LastSprite!.Value, t.LastMatchWorld, t.LastMatchTime, t.LastTime, Velocity(t), t.Floor, t.Dead, t.DeathTime, t.HitTime, t.Verified))
            .ToList();

    /// <summary>The map the tracks belong to, stamped on their summaries.</summary>
    public int MapId { get; set; }

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
        public readonly double[] Votes = new double[7];
        public EntityKind Kind;
        public double Confidence;
        public bool NearOwn;
        public int Pixels;
        public readonly Dictionary<int, int> SpeciesVotes = [];
        public int? MobId;
        public string? MobName;
        public SpriteMatch? LastSprite;
        public int Verified;
        public DropMatch? Drop;
        public int MapId;
        public int? Floor;
        public double HitTime = double.NegativeInfinity;
        public bool Dead;
        public double DeathTime;
        public double LastMatchTime;
        public long LastMatchSequence;
        /// <summary>The feet's world position at the last match (the sprite's origin).</summary>
        public PointD LastMatchWorld;
        /// <summary>The last non-empty <see cref="Update"/> that observed the track.</summary>
        public long SeenUpdate;
        public bool IsPet;
        public DamageMatch? Damage;
    }

    /// <summary>The track a candidate most likely continues (the nearest predicted position within the gate), or null.</summary>
    public TrackHint? Hint(Candidate cand, double time)
    {
        Track? best = null;
        var bestD = double.MaxValue;
        foreach (var tr in _tracks)
        {
            if (tr.IsPet || tr.Dead) continue;
            var dt = Math.Max(0.01, time - tr.LastTime);
            if (dt > (tr.MobId is not null ? IdentifiedMemorySeconds : MaxMissedSeconds)) continue;
            var d = Predicted(tr, time).DistanceTo(cand.World);
            if (d <= Gate(tr, dt) && d < bestD) { bestD = d; best = tr; }
        }
        if (best is null) return null;
        var tagName = best.Name is not null && (best.Level is not null || Names.MatchMob(best.Name) is not null) ? best.Name : null;
        return new TrackHint(best.Id, best.MobId, tagName, best.LastSprite);
    }

    private static PointD Predicted(Track tr, double time)
    {
        var dt = Math.Max(0.01, time - tr.LastTime);
        var v = Velocity(tr);
        return new PointD(tr.World.X + v.X * Math.Min(dt, 0.6), tr.World.Y + v.Y * Math.Min(dt, 0.6));
    }

    /// <summary>
    /// The association gate after <paramref name="dt"/> seconds: <see cref="GateBase"/> plus motion, at most
    /// <see cref="MaxMissedSeconds"/> worth of it at full speed; beyond that (identified tracks only) half speed.
    /// </summary>
    private static double Gate(Track tr, double dt)
    {
        var speed = tr.Age >= 3 && tr.Moved == 0 ? StillPerSecond : GatePerSecond;
        return GateBase + speed * Math.Min(dt, MaxMissedSeconds) + speed / 2 * Math.Max(0, dt - MaxMissedSeconds);
    }

    /// <param name="sequence">The frame's capture sequence, kept as the last-matched sequence of tracks whose sprite matched.</param>
    public List<DetectedEntity> Update(List<Candidate> candidates, long frame, double time, long sequence = 0)
    {
        if (candidates.Count > 0) _update++;
        var previousFull = _lastFull;
        // The pet: the one candidate under its tag continues the pet's one track, wherever it is; no other candidate does,
        // and that candidate continues no other track.
        var petCandidate = -1;
        for (var c = 0; c < candidates.Count; c++)
        {
            if (!Names.IsPetTag(candidates[c].Tag?.Text)) continue;
            if (petCandidate < 0) { petCandidate = c; continue; }
            PetDuplicates++;
            // Of two, the one with a blob nearer where the pet was; the other loses the tag.
            var keep = candidates[c].Pixels > 0 && (candidates[petCandidate].Pixels == 0 || (_pet is { } pt && candidates[c].World.DistanceTo(pt.World) < candidates[petCandidate].World.DistanceTo(pt.World)));
            if (keep) { candidates[petCandidate].Tag = null; petCandidate = c; } else candidates[c].Tag = null;
        }
        // Greedy assignment by distance to each track's predicted position.
        var pairs = new List<(double Cost, int T, int C)>();
        var characterTag = candidates.Select(c => Names.IsCharacterTag(c.Tag?.Text)).ToArray();
        var tagSpecies = candidates.Select(c => c.Tag?.Text is { } t && TagSpecies is { } f ? f(Names.Strip(t)) : null).ToArray();
        for (var t = 0; t < _tracks.Count; t++)
        {
            var tr = _tracks[t];
            if (tr.IsPet) continue;
            var dt = Math.Max(0.01, time - tr.LastTime);
            var predicted = Predicted(tr, time);
            // Something that has been standing still gets a tighter gate, so a static drop does not jump to a blob
            // that appears elsewhere while it is briefly hidden.
            var gate = Gate(tr, dt);
            var sure = tr.MobId is not null && tr.Verified >= IdentifiedSightings;
            var species = tr.MobId is { } sid ? Species?.Invoke(sid) : null;
            // Absent from the last frame that had candidates, for at least AbsentSeconds: only its own species, matched,
            // takes it up again (one missed keyframe; on a video, a quarter second of frames, not a frame or two of flicker).
            var gone = tr.SeenUpdate < previousFull && dt >= AbsentSeconds;
            var absent = tr.MobId is not null && gone;
            for (var c = 0; c < candidates.Count; c++)
            {
                if (c == petCandidate) continue;
                var cand = candidates[c];
                // A candidate that verified this track's own prediction (its species' sprite at the place and in the pose the
                // track predicts, within the reach the rules below allow) continues it before anything else: the pixels say it
                // is this mob, whatever a tag read or the nearest-neighbour gate say. A dead track's prediction tries its die
                // pose only, so the death rule holds by construction.
                if (cand.PredictedTrack == tr.Id)
                {
                    pairs.Add((BoundCost, t, c));
                    continue;
                }
                var sameSpecies = cand.Sprite is { } sp0 && sp0.Species.Id == tr.MobId;
                // A dead mob (its kill credited by an EXP gain) shows only its die pose until it is gone.
                if (tr.Dead && !(sameSpecies && cand.Sprite!.Value.Frame.Anim.StartsWith("die", StringComparison.Ordinal) && time - tr.DeathTime <= DeathHoldSeconds)) { Refuse("dead"); continue; }
                // Past the normal memory only the same species, verified by its sprite, takes an identified track up again.
                if (dt > MaxMissedSeconds && !sameSpecies) continue;
                if (absent && !sameSpecies) { Refuse("absent"); continue; }
                // Any track back after an absence takes up only a blob much like the one it left: a dead mob's spot
                // turns into the drops it left (the dying Dark Stump's 1162-pixel death pose, then the 355-pixel Leaf).
                if (gone && !sameSpecies && cand.Pixels > 0 && tr.Pixels > 0
                    && (cand.Pixels * ResumeAreaRatio < tr.Pixels || tr.Pixels * ResumeAreaRatio < cand.Pixels || !SimilarShape(cand.Box, tr.Box))) { Refuse("resume"); continue; }
                // The pet's tag never continues a mob's or a drop's track (on fx-combat-exact the pet's blob took up a mob's track
                // and its identity on 44 detections), and a verified mob or drop never continues the pet's.
                if (characterTag[c] && (tr.MobId is not null || tr.Drop is not null || tr.Level is not null || Names.MatchMob(tr.Name) is not null)) continue;
                if ((cand.Sprite is not null || cand.Drop is not null) && Names.IsPet(tr.Name)) continue;
                // A damage number continues only a damage number of the same value and kind, and nothing else continues one.
                if ((cand.Damage is { } dm) != (tr.Damage is not null)) continue;
                if (cand.Damage is { } dmg && (tr.Damage!.Digits != dmg.Digits || tr.Damage.Family != dmg.Family)) continue;
                var name = CanonicalName(cand.Tag?.Text);
                var sameName = name is not null && tr.Name is not null && SameTagName(name, tr.Name);
                if (name is not null && tr.Name is not null && !sameName) continue;
                // A tag naming another species of the map than the track's breaks the track there.
                if (tr.MobId is { } trackSpecies && tagSpecies[c] is { Count: > 0 } named && !named.Contains(trackSpecies)) { Refuse("tag"); continue; }
                var d = predicted.DistanceTo(cand.World);
                if (d > gate) continue;
                if (species is not null && !Reachable(tr, species, cand, time, dt)) continue;
                // A carried identity only moves onto a blob shaped like its body.
                if (species is not null && !sameSpecies && cand.Pixels > 0 && tr.LastSprite is { } ls && !BodyLike(cand.Box, ls.Frame)) { Refuse("shape"); continue; }
                // A killed mob's drop appears where it stood: a blob a fraction of the track's size is something else.
                // A sprite-verified pair is exempt: the area of one mob changes with its frame and with what covers it.
                if (!sameSpecies && cand.Pixels > 0 && tr.Pixels > 0 && (cand.Pixels * MaxAreaRatio < tr.Pixels || tr.Pixels * MaxAreaRatio < cand.Pixels)) continue;
                var cost = d + (sameName ? -50 : 0);
                if (tr.MobId is { } id && cand.Sprite is { } sp)
                {
                    if (sp.Species.Id == id) cost -= IdentityBonus;
                    else if (sure) continue;
                    else cost += IdentityPenalty;
                }
                // An identified drop continues only as the same item.
                if (tr.Drop is { } td && cand.Drop is { } cd && td.ItemId != cd.ItemId) { Refuse("drop"); continue; }
                // A drop whose icon matched lies still and keeps its size: a blob twice its area or another shape is
                // something run into it (the Leaf beside the dying Wild Boar on fx-combat-exact's frame 766).
                if (tr.Drop is not null && cand.Drop is null && cand.Pixels > 0 && tr.Pixels > 0
                    && (cand.Pixels > ResumeAreaRatio * tr.Pixels || !SimilarShape(cand.Box, tr.Box))) { Refuse("drop"); continue; }
                // A drop is not a mob, whatever stood there before, and a mob is not a drop.
                if (cand.Drop is not null && (tr.MobId is not null || tr.Kind is not (EntityKind.Drop or EntityKind.Unknown))) continue;
                if (cand.Sprite is not null && tr.Drop is not null) continue;
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
            if (c == petCandidate)
            {
                if (_pet is null)
                {
                    _pet = new Track { Id = _petId ??= _nextId++, FirstFrame = frame, FirstTime = time, FirstWorld = cand.World, World = cand.World, MapId = MapId, IsPet = true };
                    _tracks.Add(_pet);
                }
                tr = _pet;
                PetFrames++;
            }
            else if (candTrack[c] >= 0) tr = _tracks[candTrack[c]];
            else
            {
                tr = new Track { Id = _nextId++, FirstFrame = frame, FirstTime = time, FirstWorld = cand.World, World = cand.World, MapId = MapId };
                _tracks.Add(tr);
            }
            Observe(tr, cand, frame, time, sequence);
            MobIdentity? identity = null;
            if (tr.MobId is { } mobId && tr.LastSprite is { } last)
            {
                var verified = cand.Sprite is { } now && now.Species.Id == mobId;
                var shown = verified ? cand.Sprite!.Value : last;
                identity = new MobIdentity(mobId, tr.MobName!, shown.Frame.Facing, shown.Frame.Anim, shown.Frame.Index, shown.Score, verified, tr.Verified)
                {
                    LastMatchedSequence = tr.LastMatchSequence,
                    CarriedFor = verified ? 0 : Math.Round(Math.Max(0, time - tr.LastMatchTime), 3),
                };
            }
            var dying = tr.Dead || (identity is { } idn && idn.Anim.StartsWith("die", StringComparison.Ordinal));
            result.Add(new DetectedEntity(tr.Id, tr.Kind, tr.Confidence, cand.Box, cand.Feet, cand.World, cand.Foothold?.Foothold.Id,
                cand.SurfaceDistance, cand.Tag?.Text, tr.Level, tr.IsPet ? Names.PetName : tr.Name ?? tr.MobName,
                tr.Age, tr.FirstFrame, tr.FirstTime, Velocity(tr), cand.Pixels, cand.HpBar, identity, cand.Sprite, cand.Drop, cand.BoxSurfaceDistance)
            {
                State = dying && identity is not null ? MobState.Dying : MobState.Alive,
                Damage = cand.Damage,
                Swept = cand.Swept,
                Found = cand.Found,
            });
        }
        if (candidates.Count > 0) _lastFull = _update;
        // The frame shows no more of a species than its cap: carried identities beyond it, least verified first, are left out.
        if (SpeciesCap is { } cap)
            foreach (var g in result.Select((e, i) => (E: e, I: i)).Where(p => p.E.Identity is not null).GroupBy(p => p.E.Identity!.MobId))
                foreach (var (e, i) in g.OrderByDescending(p => p.E.Identity!.Verified).ThenByDescending(p => p.E.Identity!.Sightings).Skip(cap(g.Key)))
                {
                    result[i] = e with { Identity = null, State = MobState.Alive };
                    CappedIdentities++;
                }
        // Forget tracks not seen for a while (the pet's is kept: it is one track for the session).
        for (var t = _tracks.Count - 1; t >= 0; t--)
        {
            var tr = _tracks[t];
            if (tr.IsPet) continue;
            var memory = tr.Dead ? DeathHoldSeconds : tr.MobId is not null && tr.Verified >= IdentifiedSightings ? IdentifiedMemorySeconds : MaxMissedSeconds;
            if (time - tr.LastTime <= memory) continue;
            Finished.Add(Summarize(tr));
            _tracks.RemoveAt(t);
        }
        return result;
    }

    /// <summary>
    /// Two tag reads name the same thing: equal, a few letters apart (<see cref="TagNames.SameName"/>), or naming the same
    /// species of the map ("25 wvild Boar", the badge's number run into the name, and "Wild Boar").
    /// </summary>
    private bool SameTagName(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || TagNames.SameName(a, b) || TagNames.SameName(b, a)) return true;
        return TagSpecies is { } f && f(a) is { Count: > 0 } sa && f(b) is { Count: > 0 } sb && sa.Intersect(sb).Any();
    }

    /// <summary>
    /// A track counts as absent (see the resume rules in <see cref="Update"/>) when the last frame with candidates did not
    /// show it and it was last seen at least this long ago: any missed keyframe of fx-combat-exact (one every half second),
    /// but on a lossless video at 30 per second not the frame or two a blob flickers away for.
    /// </summary>
    public const double AbsentSeconds = 0.25;

    /// <summary>Association cost of a candidate that verified the track's own prediction: lower than any other pair's, so it is assigned first.</summary>
    public const double BoundCost = -1e6;

    /// <summary>A track absent from the last frame resumes only on a blob within this factor of its last area (and of a similar shape).</summary>
    public const int ResumeAreaRatio = 2;

    private static bool SimilarShape(CaptureRect a, CaptureRect b)
    {
        var ra = (double)a.Height / Math.Max(1, a.Width);
        var rb = (double)b.Height / Math.Max(1, b.Width);
        return ra <= ShapeRatio * rb && rb <= ShapeRatio * ra;
    }

    private void Refuse(string rule) => Refused[rule] = Refused.GetValueOrDefault(rule) + 1;

    /// <summary>
    /// Whether a mob of the track's species can have reached the candidate: a floor-bound species never changes floor, and
    /// none moves further sideways than <see cref="SpeedFactor"/> times its walking speed allows (plus <see cref="KnockBack"/>
    /// shortly after a hit).
    /// </summary>
    private bool Reachable(Track tr, MobSpecies species, Candidate cand, double time, double dt)
    {
        if (species.Mobility is MobMobility.Floor or MobMobility.Static && tr.Floor is { } f0 && cand.Floor is { } f1 && f0 != f1) { Refuse("floor"); return false; }
        if (species.Mobility == MobMobility.Fly) return true;
        var reach = GateBase + SpeedFactor * species.WalkSpeed * Math.Min(dt, MaxMissedSeconds) + (time - tr.HitTime <= HitMemorySeconds ? KnockBack : 0);
        if (Math.Abs(cand.World.X - tr.World.X) > reach) { Refuse("speed"); return false; }
        return true;
    }

    /// <summary>Whether a blob's box can show the body of a frame (see <see cref="MinBodyShare"/>).</summary>
    public static bool BodyLike(CaptureRect box, SpriteFrame frame)
    {
        var (l, t, r, b) = frame.Body;
        double bw = Math.Max(1, r - l), bh = Math.Max(1, b - t);
        var share = (double)box.Width * box.Height / (bw * bh);
        if (share < MinBodyShare || share > MaxBodyShare) return false;
        var ratio = (double)box.Height / Math.Max(1, box.Width) / (bh / bw);
        return ratio <= ShapeRatio && ratio >= 1 / ShapeRatio;
    }

    /// <summary>
    /// Credits a kill (an EXP gain equal to the species' client exp) to one live track of that species: of those seen in the
    /// last <see cref="MaxMissedSeconds"/>, one in its hit or die pose or hit lately first, then the nearest to the own
    /// character. The track is dying from then on, shows only its own die pose for <see cref="DeathHoldSeconds"/> and is
    /// never continued by anything else. Returns its id, or null when no track of the species qualifies.
    /// </summary>
    public int? Kill(int speciesId, PointD? own, double time)
    {
        var pick = _tracks.Where(t => !t.Dead && !t.IsPet && t.MobId == speciesId && time - t.LastTime <= MaxMissedSeconds)
            .OrderByDescending(t => t.LastSprite is { } ls && (ls.Frame.Anim.StartsWith("hit", StringComparison.Ordinal) || ls.Frame.Anim.StartsWith("die", StringComparison.Ordinal)) || time - t.HitTime <= HitMemorySeconds)
            .ThenBy(t => own is { } o ? t.World.DistanceTo(o) : 0).FirstOrDefault();
        if (pick is null) { KillsUnmatched++; return null; }
        pick.Dead = true;
        pick.DeathTime = time;
        KillsCredited++;
        return pick.Id;
    }

    /// <summary>Closes all live tracks (end of a session or a map change).</summary>
    public void Flush()
    {
        foreach (var tr in _tracks) Finished.Add(Summarize(tr));
        _tracks.Clear();
        _pet = null;
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
        new(tr.Id, tr.IsPet ? EntityKind.Pet : tr.Kind is EntityKind.Player or EntityKind.Unknown && Names.MatchMob(tr.Name) is not null ? EntityKind.Mob : tr.Kind, tr.FirstFrame, tr.LastFrame, tr.FirstTime, tr.LastTime, tr.Age, tr.FirstWorld, tr.World, tr.Tag, tr.NearOwn, tr.Level is not null || tr.HpBar,
            tr.MobId, tr.MobName, tr.Verified, tr.Drop?.ItemId, tr.MapId, tr.Dead);

    private static PointD Velocity(Track tr)
    {
        if (tr.History.Count < 2) return new PointD(0, 0);
        var (t0, w0) = tr.History.Peek();
        var (t1, w1) = tr.History.Last();
        var dt = t1 - t0;
        return dt <= 0 ? new PointD(0, 0) : new PointD((w1.X - w0.X) / dt, (w1.Y - w0.Y) / dt);
    }

    private void Observe(Track tr, Candidate c, long frame, double time, long sequence)
    {
        tr.SeenUpdate = _update;
        if (c.Floor is { } floor) tr.Floor = floor;
        if (c.HpBar || c.Tag?.Text is not null && TagDetector.Parse(c.Tag.Text).Level is not null
            || c.Sprite is { } hs && (hs.Frame.Anim.StartsWith("hit", StringComparison.Ordinal) || hs.Frame.Anim.StartsWith("die", StringComparison.Ordinal)))
            tr.HitTime = time;
        if (c.Damage is not null) tr.Damage = c.Damage;
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
        if (c.Sprite is { } sprite)
        {
            // The species is the one the sprites matched most often, so one odd match does not rename the track.
            tr.SpeciesVotes[sprite.Species.Id] = tr.SpeciesVotes.GetValueOrDefault(sprite.Species.Id) + 1;
            var top = tr.SpeciesVotes.MaxBy(v => v.Value).Key;
            if (top == sprite.Species.Id) { tr.MobId = top; tr.MobName = sprite.Species.Name; tr.LastSprite = sprite; tr.LastMatchTime = time; tr.LastMatchSequence = sequence; tr.LastMatchWorld = c.World; }
            tr.Verified++;
        }
        if (c.Drop is { } drop) tr.Drop = drop;
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
        if (kind is EntityKind.Mob or EntityKind.Player or EntityKind.Pet or EntityKind.Drop or EntityKind.Damage && confidence >= 0.85) best = (int)kind;
        tr.Kind = (EntityKind)best;
        var total = tr.Votes.Sum();
        // The share of the (decayed) votes behind the reported kind, scaled by this frame's own confidence when it agrees.
        tr.Confidence = Math.Round(total <= 0 ? 0 : tr.Votes[best] / total * (best == (int)kind ? confidence : 0.6), 2);
    }

    private (EntityKind Kind, double Confidence) Classify(Track tr, Candidate c)
    {
        // The pet is the one candidate under its tag, on the pet's one track; the own name's tag outside the own box is a player's.
        if (tr.IsPet) return (EntityKind.Pet, 0.95);
        if (c.Damage is not null) return (EntityKind.Damage, 0.97);
        if (Names.IsCharacterTag(c.Tag?.Text)) return Names.IsPet(TagDetector.Parse(c.Tag!.Text).Name) ? (EntityKind.Unknown, 0.4) : (EntityKind.Player, 0.9);
        // A sprite of a mob that spawns here, matched now or before, is decisive; so is a drop's icon.
        if (c.Sprite is not null) return (EntityKind.Mob, 0.97);
        if (c.Drop is not null) return (EntityKind.Drop, 0.95);
        if (tr.MobId is not null) return (EntityKind.Mob, 0.9);
        // A track whose icon matched once stays a drop: meso coins spin, so the appearance test alone calls them mobs.
        if (tr.Drop is not null) return (EntityKind.Drop, 0.9);
        // A level badge (even one whose number did not read) or a known mob name.
        if (tr.Level is not null || Names.MatchMob(tr.Name) is not null) return (EntityKind.Mob, 0.95);
        // A name a species of the map ends with ("loar", a Boar whose start the own tag covered) is a mob's, not a player's.
        if (tr.Name is not null && TagSpecies?.Invoke(tr.Name) is { Count: > 0 }) return (EntityKind.Mob, 0.9);
        if (tr.Name is not null && !Names.IsPet(tr.Name)) return (EntityKind.Player, 0.85);
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
