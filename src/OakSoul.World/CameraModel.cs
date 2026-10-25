namespace OakSoul.World;

/// <summary>
/// The client's camera for one map: the world position of the screen's top-left. Sprites are drawn 1:1, so
/// world = camera + screen. The camera follows the player and is clamped so the view never leaves the map's vr box.
///
/// Measured on fx-combat (Deep Valley I, vr -823,26 to 2083,2215) and fx-maps:
/// <list type="bullet">
/// <item>View size 1366x768, the whole native frame, not the 702-row world viewport above the HUD. Horizontally, with
/// the player at the left edge the dot matched cameraX = vr.Left on every clamped keyframe, and at the right edge
/// (rope at x 1981) cameraX = vr.Right - 1366. Vertically, standing on the bottom floor (y 2025) the tag feet read
/// 582 on all 18 such keyframes: 2025 - 582 + 4 = 1447 = vr.Bottom - 768 (a 702-row view would put the feet at 516).</item>
/// <item>The tag's <c>Feet</c> point is 4 pixels below the soles: on that floor the shoes end on row 577-578 while
/// the tag text starts on row 583 (Feet = 582).</item>
/// <item>Anchor: the camera eases towards the player and stops inside a dead zone, so the feet do not sit at one
/// screen point. With the camera unclamped, resting feet (dot and tag unchanged for several keyframes) sat at
/// x 655..733 and y 490 on most rests (from tag y 494), with other rests at tag y 536..550. The anchor used for
/// prediction is x 683 (the screen centre, the middle of the resting spread) and foot y 490 (the dominant rest).</item>
/// <item>While walking the camera trails the player by about 122 pixels: feet x medians 805 walking right on fx-maps
/// (52 keyframes) and 774 on fx-combat (39, some still accelerating), 563 walking left on fx-combat (45).</item>
/// </list>
/// A map narrower or shorter than the view is assumed centred; no fixture covers one.
/// </summary>
public sealed class CameraModel
{
    /// <summary>The view the camera clamps with: the full native frame (see class remarks).</summary>
    public const int ViewWidth = 1366, ViewHeight = 768;

    /// <summary>Screen point the camera steers the player's feet towards when it is not clamped (see class remarks).</summary>
    public const double AnchorX = 683, AnchorY = 490;

    /// <summary>Rows from the name tag's <c>Feet</c> point up to where the character actually stands (see class remarks).</summary>
    public const double TagFeetToSoles = 4;

    /// <summary>How far ahead of the anchor the feet run while walking (see class remarks).</summary>
    public const double WalkLagX = 122;

    public ViewBounds Bounds { get; }

    public CameraModel(ViewBounds bounds) => Bounds = bounds;

    public double MinX => Bounds.Width >= ViewWidth ? Bounds.Left : Bounds.Left - (ViewWidth - Bounds.Width) / 2.0;
    public double MaxX => Bounds.Width >= ViewWidth ? Bounds.Right - ViewWidth : MinX;
    public double MinY => Bounds.Height >= ViewHeight ? Bounds.Top : Bounds.Top - (ViewHeight - Bounds.Height) / 2.0;
    public double MaxY => Bounds.Height >= ViewHeight ? Bounds.Bottom - ViewHeight : MinY;

    /// <summary>Where the character stands on screen, from the name tag's Feet point.</summary>
    public static PointD SolesFromTag(PointD tagFeet) => new(tagFeet.X, tagFeet.Y - TagFeetToSoles);

    /// <summary>The tag's Feet point for a character standing at a screen point (inverse of <see cref="SolesFromTag"/>).</summary>
    public static PointD TagFromSoles(PointD soles) => new(soles.X, soles.Y + TagFeetToSoles);

    public PointD Clamp(PointD camera) => new(Math.Clamp(camera.X, MinX, MaxX), Math.Clamp(camera.Y, MinY, MaxY));

    /// <summary>Which axes of a camera sit on a bound (within half a unit).</summary>
    public (bool X, bool Y) IsClamped(PointD camera) =>
        (Math.Abs(camera.X - MinX) < 0.5 || Math.Abs(camera.X - MaxX) < 0.5, Math.Abs(camera.Y - MinY) < 0.5 || Math.Abs(camera.Y - MaxY) < 0.5);

    /// <summary>The camera that puts world feet at screen feet, clamped to the bounds.</summary>
    public PointD FromFeet(PointD worldFeet, PointD screenFeet) => Clamp(worldFeet - screenFeet);

    /// <summary>The unclamped camera that puts world feet at screen feet: how far it lies outside the bounds tests the model.</summary>
    public static PointD Raw(PointD worldFeet, PointD screenFeet) => worldFeet - screenFeet;

    public static PointD ScreenToWorld(PointD camera, PointD screen) => camera + screen;

    public static PointD WorldToScreen(PointD camera, PointD world) => world - camera;

    /// <summary>
    /// The camera for a player at <paramref name="worldFeet"/>: feet at the anchor, shifted by the walking lag when
    /// <paramref name="walking"/> is -1 (left) or +1 (right), then clamped.
    /// </summary>
    public PointD Predict(PointD worldFeet, int walking = 0) => Clamp(worldFeet - new PointD(AnchorX + Math.Sign(walking) * WalkLagX, AnchorY));

    /// <summary>Where the feet (soles) should appear on screen for a player at <paramref name="worldFeet"/>.</summary>
    public PointD PredictScreenFeet(PointD worldFeet, int walking = 0) => WorldToScreen(Predict(worldFeet, walking), worldFeet);
}
