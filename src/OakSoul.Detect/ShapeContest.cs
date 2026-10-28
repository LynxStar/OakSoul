namespace OakSoul.Detect;

/// <summary>
/// Look-alike species that are not recolours: a Dark Stump's body is a Dark Axe Stump's without the axe, so wherever the axe is
/// hidden or over background the mosaic cannot vouch for, both frames score alike on their own pixels ("Dark Axe Stump 0.97 vs
/// Dark Stump 1.00" on the top platform of fx-combat-exact's frame 781). Such a pair is decided on the pixels where the two
/// frames, each at its own placement, differ: those only one of them draws (the axe), or that both draw in other colours.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>Contests settled on the pixels where look-alike shapes differ, and those whose winner that changed, since construction.</summary>
    public int ShapeSettled { get; private set; }
    public int ShapeChanged { get; private set; }

    /// <summary>
    /// The winner of a contest against the look-alikes it met (<see cref="ShapeRival"/> either way, feet within two pixels): the
    /// one whose telling pixels the screen shows, with at least <see cref="MinTellingPixels"/> and <see cref="SettleRatio"/> times
    /// the other's. Otherwise the contest's winner stands.
    /// </summary>
    private SpriteMatch SettleShapes(SpriteMatch winner, List<SpriteMatch> found)
    {
        foreach (var x in found)
        {
            if (x.Species.Id == winner.Species.Id || _mobs!.Recolours(winner.Species).Contains(x.Species)) continue;
            if (Math.Abs(x.Feet.X - winner.Feet.X) > 2 || Math.Abs(x.Feet.Y - winner.Feet.Y) > 2) continue;
            if (!ShapeRival(winner.Species, x.Species) && !ShapeRival(x.Species, winner.Species)) continue;
            var (onlyW, onlyX) = TellShapes(winner, x);
            if (onlyX >= MinTellingPixels && onlyX >= SettleRatio * onlyW)
            {
                ShapeSettled++;
                ShapeChanged++;
                winner = x with { Basis = SpeciesBasis.Pixels, Rival = winner.Species, RivalScore = winner.Score };
            }
            else if (onlyW >= MinTellingPixels && onlyW >= SettleRatio * onlyX)
            {
                ShapeSettled++;
                winner = winner with { Basis = SpeciesBasis.Pixels };
            }
        }
        return winner;
    }

    /// <summary>
    /// The telling pixels of two frames each at its own placement, on the prepared patch: a pixel opaque in one frame counts for it
    /// when the screen shows its colour (as drawn or frozen, under its match's tint) and the other frame there is transparent or
    /// another colour; for the other when the other draws it there in its colour, or draws nothing and the screen shows the
    /// background the mosaic vouches for. Pixels both draw alike, and masked ones, say nothing.
    /// </summary>
    private (int OnlyA, int OnlyB) TellShapes(SpriteMatch a, SpriteMatch b)
    {
        var p = _matcher.Patch;
        int onlyA = 0, onlyB = 0;
        foreach (var (m, o, sign) in new[] { (a, b, 1), (b, a, -1) })
        {
            var f = m.Frame;
            var g = o.Frame;
            var frozen = m.Tint == SpriteTint.Frozen;
            var otherFrozen = o.Tint == SpriteTint.Frozen;
            for (var i = 0; i < f.PixelX.Length; i++)
            {
                int sx = m.Left + f.PixelX[i], sy = m.Top + f.PixelY[i];
                int px = sx - p.X0, py = sy - p.Y0;
                if ((uint)px >= (uint)p.Width || (uint)py >= (uint)p.Height) continue;
                var k = py * p.Width + px;
                if (p.Ok[k] == 0) continue;
                var mine = frozen ? f.PixelFrozen[i] : f.PixelPacked[i];
                int gx = sx - o.Left, gy = sy - o.Top, alpha = 0, theirs = -1;
                if ((uint)gx < (uint)g.Width && (uint)gy < (uint)g.Height)
                {
                    var go = (gy * g.Width + gx) * 4;
                    alpha = g.Bgra[go + 3];
                    int tb = g.Bgra[go], tg = g.Bgra[go + 1], tr = g.Bgra[go + 2];
                    if (otherFrozen) { tb = (tb * SpriteMatcher.FrozenB + 127) / 255; tg = (tg * SpriteMatcher.FrozenG + 127) / 255; tr = (tr * SpriteMatcher.FrozenR + 127) / 255; }
                    theirs = tb | tg << 8 | tr << 16;
                }
                if (alpha == SpriteFrame.SolidAlpha && theirs == mine) continue;
                // Pixels of both frames are counted once, from the first frame's side.
                if (sign < 0 && alpha == SpriteFrame.SolidAlpha) continue;
                var screen = p.Packed[k];
                if (screen == mine) { if (sign > 0) onlyA++; else onlyB++; }
                else if (alpha == SpriteFrame.SolidAlpha && screen == theirs) { if (sign > 0) onlyB++; else onlyA++; }
                else if (alpha == 0 && p.Background[k] >= 0 && screen == p.Background[k]) { if (sign > 0) onlyB++; else onlyA++; }
            }
        }
        return (onlyA, onlyB);
    }
}
