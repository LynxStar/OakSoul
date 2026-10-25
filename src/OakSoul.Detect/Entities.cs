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
    /// <summary>Damage numbers, skill effects, the cursor and other brief or floating overlays.</summary>
    Effect,
}

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
    int Age, long FirstFrame, double FirstTime, PointD Velocity, int Pixels, bool HpBar)
{
    public override string ToString() =>
        $"#{TrackId} {Kind} {Confidence:0.00} {Box.Width}x{Box.Height}+{Box.X}+{Box.Y} world {World} {(FootholdId is { } f ? $"fh{f}" : "air")}{(Tag is null ? "" : $" '{Tag}'")} age {Age}";
}

/// <summary>The detector's answer for one frame.</summary>
public sealed record EntityFrame(long Frame, double Time, IReadOnlyList<DetectedEntity> Entities, (int X, int Y)? Camera,
    Registration? Registration, double Milliseconds, double OcrMilliseconds, string? Note = null)
{
    public static EntityFrame Empty(long frame, double time, string note) => new(frame, time, [], null, null, 0, 0, note);

    public int Count(EntityKind kind) => Entities.Count(e => e.Kind == kind);
}
