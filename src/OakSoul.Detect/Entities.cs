using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>What a detection is taken to be.</summary>
public enum EntityKind
{
    /// <summary>Foreground that fits no other kind well enough.</summary>
    Unknown,
    /// <summary>A monster: stands on a foothold, animates or wanders, and shows "Lv. N Name" once hit.</summary>
    Mob,
    /// <summary>Another player: a plain name tag (no level badge) under a sprite that is not the own pet.</summary>
    Player,
    /// <summary>A pet: a plain name tag that keeps to the own character's side.</summary>
    Pet,
    /// <summary>An item or meso drop: small, lying on a foothold, not moving, looking the same frame after frame.</summary>
    Drop,
    /// <summary>Skill effects, the cursor and other brief or floating overlays (damage numbers that were read are <see cref="Damage"/>).</summary>
    Effect,
    /// <summary>A damage number read digit by digit from the client's own digit sprites (<see cref="DamageReader"/>): dealt or taken.</summary>
    Damage,
}

/// <summary>What a mob track is doing: alive, or dying (its die pose matched, or a kill credited by an EXP gain).</summary>
public enum MobState : byte { Alive, Dying }

/// <summary>
/// One entity in one frame. Screen coordinates are native frame pixels; world coordinates are map units (y down).
/// <see cref="Feet"/> is the bottom centre of the box, where a sprite stands. <see cref="Velocity"/> is in world
/// units per second over the track's recent history; <see cref="Age"/> counts the frames the track has been seen in,
/// and <see cref="FirstFrame"/> is the frame it was first seen, so a drop can be "lying there since frame N".
/// <see cref="Level"/> is 0 when a mob's level badge was seen but its number did not read.
/// </summary>
public sealed record DetectedEntity(
    int TrackId, EntityKind Kind, double Confidence, CaptureRect Box, PointD Feet, PointD World,
    int? FootholdId, double? FootholdDistance, string? Tag, int? Level, string? Name,
    int Age, long FirstFrame, double FirstTime, PointD Velocity, int Pixels, bool HpBar,
    MobIdentity? Identity = null, SpriteMatch? Sprite = null, DropMatch? Drop = null, double? BoxFootholdDistance = null)
{
    /// <summary>
    /// For a mob with an identity: the client's body rectangle (its frame's lt/rb, the box it collides with) of the matched
    /// frame, or for a carried identity the track's last frame, placed with its origin on the feet; <see cref="Box"/> stays
    /// the foreground blob's extent. Null for everything else.
    /// </summary>
    public CaptureRect? Hitbox { get; init; }

    /// <summary>Whether <see cref="Hitbox"/> is the client's lt/rb (true) or, for a frame without one (the die frames), the sprite's visible extent.</summary>
    public bool HitboxFromClient { get; init; }

    /// <summary>Dying: the die pose matched (now, or last on the track), or an EXP gain credited the kill to this track.</summary>
    public MobState State { get; init; }

    /// <summary>A damage number read on this entity, when it is one.</summary>
    public DamageMatch? Damage { get; init; }

    /// <summary>Found by the direct search along a foothold rather than from its foreground blob alone.</summary>
    public bool Swept { get; init; }

    /// <summary>The box drawn for the entity: the hitbox for an identified mob, the damage number's digits, else <see cref="Box"/>.</summary>
    public CaptureRect DisplayBox => Hitbox ?? Box;

    public override string ToString() =>
        $"#{TrackId} {Kind} {Confidence:0.00} {Box.Width}x{Box.Height}+{Box.X}+{Box.Y} world {World} {(FootholdId is { } f ? $"fh{f}" : "air")}{(Tag is null ? "" : $" '{Tag}'")}{(Identity is null ? "" : $" {Identity}")} age {Age}";
}

/// <summary>
/// Who a mob is, from its sprite: the species (client id and name), and the facing (+1 right, -1 left), animation and
/// frame it was last matched with. <see cref="Verified"/> is true when a sprite matched in this very frame, false when
/// the identity is carried by the track from earlier frames; <see cref="Sightings"/> counts the frames that agreed.
/// </summary>
public sealed record MobIdentity(int MobId, string Name, int Facing, string Anim, int FrameIndex, double Score, bool Verified, int Sightings)
{
    /// <summary>Capture sequence of the frame the track's species last matched (this frame when <see cref="Verified"/>).</summary>
    public long LastMatchedSequence { get; init; }

    /// <summary>Seconds since that match (0 when verified in this frame): how long the identity has been carried.</summary>
    public double CarriedFor { get; init; }

    public override string ToString() => $"{Name} {Anim}/{FrameIndex}{(Facing > 0 ? "R" : "L")}{(Verified ? "" : "~")}";
}

/// <summary>The detector's answer for one frame.</summary>
public sealed record EntityFrame(long Frame, double Time, IReadOnlyList<DetectedEntity> Entities, (int X, int Y)? Camera,
    Registration? Registration, double Milliseconds, double OcrMilliseconds, string? Note = null)
{
    public static EntityFrame Empty(long frame, double time, string note) => new(frame, time, [], null, null, 0, 0, note);

    public int Count(EntityKind kind) => Entities.Count(e => e.Kind == kind);
}
