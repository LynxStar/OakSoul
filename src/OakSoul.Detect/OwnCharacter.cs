using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>
/// Where the own character is drawn, from its name tag. On the fx-combat keyframes looked at, the sprite (cap to
/// shoes) is about 45 pixels wide and reaches about 70 rows above the tag's top; the box adds room for the staff and
/// casting poses. The tag box is 16 rows tall; the locator's template covers its 11 text rows.
/// </summary>
public static class OwnCharacter
{
    /// <summary>Half width and height above the tag of the box that covers the sprite in any pose.</summary>
    public const int HalfWidth = 40, Above = 82;

    /// <summary>Extra margin around the box when building the mosaic, where a sprite pixel costs more than a lost background pixel (chosen, not tuned).</summary>
    public const int MosaicPad = 24;

    /// <summary>The box covering the sprite and its tag, padded by <paramref name="pad"/>, in native frame pixels.</summary>
    public static CaptureRect Box(CaptureRect tag, int pad = 0)
    {
        var cx = tag.X + tag.Width / 2;
        var top = tag.Y - Above - pad;
        return new CaptureRect(cx - HalfWidth - pad, top, 2 * (HalfWidth + pad), tag.Y + tag.Height + 3 + pad - top);
    }

    /// <summary>The tag's Feet point (as <c>NameTagHit.Feet</c>: centred, one row above the tag box), from the tag box.</summary>
    public static OakSoul.World.PointD TagFeet(CaptureRect tag) => new(tag.X + tag.Width / 2.0, tag.Y - 1);

    /// <summary>The same box for a character whose soles are at a screen point (the tag hidden), widened by the uncertainty.</summary>
    public static CaptureRect BoxFromSoles(double solesX, double solesY, int pad)
    {
        var tagY = (int)Math.Round(solesY) + 5;
        return Box(new CaptureRect((int)Math.Round(solesX) - 27, tagY, 54, 11), pad);
    }
}
