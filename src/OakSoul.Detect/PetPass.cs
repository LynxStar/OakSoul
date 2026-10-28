using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>The own pet found in a frame: its sprite, how it was found, the foreground it explains and the tag at its place.</summary>
public sealed record PetFind(SpriteMatch Match, FoundBy Found, int Pixels, CaptureRect Box, int Tag);

/// <summary>
/// The own pet, tracked like a mob. Its species is identified once, by an exact match of every pet's frames (the client's pet
/// data, <see cref="Pets"/>) under a tag reading the pet's name alone, or given from an earlier identification
/// (<see cref="PetSpecies"/>). From then on the pet is predicted from its last match pose by pose, searched at its tag when
/// one is read, and along the footholds near where it was or near the own character; the strict matcher verifies it. The tag
/// confirms it but is not required, and there is at most one pet.
/// </summary>
public sealed partial class EntityDetector
{
    /// <summary>Every pet's frames (data/sprites/pet), for identifying the pet's species; null leaves the pet to its tag.</summary>
    public SpriteSet? Pets { get; init; }

    /// <summary>The pet's species, when known: given (an earlier identification) or identified in this session.</summary>
    public MobSpecies? PetSpecies { get; set; }

    /// <summary>Raised once when the pet's species is identified: the species, the frame's capture sequence and the match.</summary>
    public Action<MobSpecies, long, SpriteMatch>? PetIdentified { get; set; }

    /// <summary>
    /// Identification: the best pet species under the pet's tag must agree on this share of its visible pixels, and no other
    /// species (Brown Kitty is Black Kitty's recolour) come within <see cref="PetIdentifyMargin"/>. Mara on fx-combat-exact's
    /// frame 755, half behind ice shards, scores 0.74 as a Black Kitty; whole, a pet agrees on every pixel.
    /// </summary>
    public const double PetIdentifyScore = 0.9, PetIdentifyMargin = 0.2;

    /// <summary>
    /// A pet match needs this share of its pixels, a quarter of them comparable: the own character, the coin it is picking up or
    /// its tag often stand in front of it (Mara carrying a coin on fx-combat-exact's keyframe 436 agrees on 0.36 of her pixels).
    /// Above the matcher's 0.2 for a mob's blob, since the pet is searched without one.
    /// </summary>
    public const double PetThreshold = 0.3, PetMinComparable = 0.25;

    /// <summary>The pet's walking speed bound in world units per second (pets run after their owner: 150 to 170 measured on fx-combat-exact) and the slack of its window.</summary>
    public const double PetSpeed = 260;
    public const int PetSlackX = 4, PetSlackY = 6;

    /// <summary>
    /// The pet's tag sits under its feet: its centre within this many columns of the feet and its top this many rows below them
    /// (measured on fx-combat-exact: centre within 2 columns, top 2 to 4 rows below).
    /// </summary>
    public const int PetTagSideways = 14, PetTagAboveMin = -3, PetTagAboveMax = 10;

    /// <summary>
    /// A pet match this good ends the search at its tag: the pet's frames do not show its accessories (Mara's red bow), so a whole
    /// pet agrees on 0.4 to 0.75 of its pixels and rarely on the 0.9 that ends a mob's.
    /// </summary>
    public const double PetGoodScore = 0.6;

    /// <summary>Window around the tag's feet in which the pet's origin is searched (its tag is centred under it, a few rows below).</summary>
    public const int PetTagWindowX = 5, PetTagWindowY = 5;

    /// <summary>The own character's neighbourhood searched for a lost pet (screen columns either side, rows above and below the feet).</summary>
    public const int PetNearOwnX = 200, PetNearOwnY = 100;

    private (SpriteMatch Match, PointD World, double Time, double Since)? _petLast;

    /// <summary>Pet sightings by how they were found, and the time spent, since construction.</summary>
    public int PetPredicted { get; private set; }
    public int PetAtTag { get; private set; }
    public double PetMs { get; private set; }
    public int PetIdentifyTries { get; private set; }

    /// <summary>
    /// Finds the pet before the mobs: identifies its species if need be, then its prediction, the search at its tag and the
    /// search along the footholds. Its explained pixels leave the foreground (the pet is never a mob's blob), and on a temporal
    /// frame every later comparison leaves them out.
    /// </summary>
    private PetFind? FindPet(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, List<TagBox> tags, CaptureRect? own, bool clear)
    {
        if (_tracker.Names.PetName is null) return null;
        var t0 = Stopwatch.GetTimestamp();
        var petTags = PetTags(ctx, camX, camY, tags, own);
        if (PetSpecies is null)
        {
            if (Pets is not null && petTags.Count > 0) IdentifyPet(frame, frameWidth, ctx, tags, petTags.Where(i => _tracker.Names.IsPetTag(tags[i].Text)).ToList());
            PetMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return null;
        }
        var species = PetSpecies;
        SpriteMatch? best = null;
        var found = FoundBy.Search;
        var pts = ctx.Pts ?? ctx.Time;
        // 1. The prediction: the frame shown and the next of its animation, then the rest of the pet's frames, at the last feet.
        if (_petLast is { } last && ctx.Time - last.Time <= PreciseSeconds)
        {
            var dt = ctx.Time - last.Time;
            int fx = (int)Math.Round(last.World.X - camX), fy = (int)Math.Round(last.World.Y - camY);
            var winX = PetSlackX + (int)Math.Ceiling(PetSpeed * dt);
            _matcher.PrepareRegion(frame, frameWidth, _mask, fx - 70 - winX, fy - 90 - PetSlackY, fx + 70 + winX, fy + 20 + PetSlackY);
            var lf = last.Match.Frame;
            var seq = species.Frames.Where(f => f.Anim == lf.Anim && f.Facing == lf.Facing).OrderBy(f => f.Index).ToList();
            var at = seq.FindIndex(f => f.Index == lf.Index);
            var next = at >= 0 && seq.Count > 1 ? seq[(at + 1) % seq.Count] : null;
            var first = next is not null && pts - last.Since + 1 / 60.0 >= lf.DelayMs / 1000.0 ? new[] { next, lf } : next is not null ? [lf, next] : [lf];
            foreach (var f in first.Concat(species.Frames.Where(f => f.Facing == lf.Facing && f != lf && f != next)).Concat(species.Frames.Where(f => f.Facing != lf.Facing)))
            {
                if (_matcher.Best(new CaptureRect(fx, fy, 1, 1), new PointD(fx, fy), species, f, PetThreshold, winX, PetSlackY, minComparable: PetMinComparable) is { } m
                    && m.Score >= PetThreshold && (best is null || m.Score > best.Value.Score + TransitionMargin)) best = m;
                // The pet's frames lack its accessories, so a good score, not a convincing one, ends the prediction.
                if (best is { } b && (b.Score >= PetGoodScore || b.Matches == b.Compared)) break;
            }
            if (best is not null) { found = FoundBy.Prediction; PetPredicted++; }
        }
        var result = best is { } found0 ? TakePet(found0, found, frame, frameWidth, ctx, camX, camY, tags, clear) : null;
        PetMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// The tags at which the pet may stand: its name alone first; then a tag that read nothing or a piece of the pet's name near where
    /// the pet was or beside the own character (the pet's tag cut by another, the own tag drawn over it).
    /// </summary>
    private List<int> PetTags(DetectionContext ctx, int camX, int camY, List<TagBox> tags, CaptureRect? own)
    {
        var vp = _mask.Viewport;
        var petTags = new List<int>();
        for (var i = 0; i < tags.Count; i++)
            if (_tracker.Names.IsPetTag(tags[i].Text)) petTags.Add(i);
        var near = _petLast is { } nl && ctx.Time - nl.Time <= EntityTracker.MaxMissedSeconds ? new PointD(nl.World.X - camX, nl.World.Y - camY)
            : own is { } on ? new PointD(on.X + on.Width / 2.0, on.Y + on.Height) : (PointD?)null;
        for (var i = 0; i < tags.Count; i++)
            if (!petTags.Contains(i) && near is { } np && (tags[i].Text is null || _tracker.Names.IsPetFragment(tags[i].Text))
                && Math.Abs(tags[i].CentreX + vp.X - np.X) <= PetNearOwnX && Math.Abs(tags[i].Y0 + vp.Y - np.Y) <= PetNearOwnY) petTags.Add(i);
        return petTags;
    }

    /// <summary>
    /// The pet when its prediction did not find it, after the blobs: first in a pet-sized blob standing on one of its tags, then (no
    /// such blob: the pet over background the mosaic cannot vouch for) every frame placed by its origin over the tag, then in a blob
    /// near where it was or near the own character.
    /// </summary>
    private PetFind? FindPetAfterBlobs(List<Blob> blobs, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, List<TagBox> tags, CaptureRect? own, bool clear)
    {
        if (PetSpecies is not { } species || _tracker.Names.PetName is null) return null;
        var t0 = Stopwatch.GetTimestamp();
        var vp = _mask.Viewport;
        var petTags = PetTags(ctx, camX, camY, tags, own);
        PetFind? result = null;
        foreach (var i in petTags)
        {
            var t = tags[i];
            var near = new PointD(t.CentreX + vp.X, t.Y0 + vp.Y - 2);
            if (FindPetInBlobs(blobs, frame, frameWidth, ctx, camX, camY, tags, near, t.Width / 2.0 + 12, 12, clear) is { } r) { result = r; PetAtTag++; break; }
        }
        SpriteMatch? best = null;
        // No blob on the tag (the pet over background the mosaic cannot vouch for): every frame placed by its origin over the tag.
        if (result is null)
        {
            foreach (var i in petTags)
            {
                var t = tags[i];
                int fx = (int)Math.Round(t.CentreX + vp.X), fy = t.Y0 + vp.Y - 3;
                // A tag run into another's ("ara-v, 11", the pet's and a Dark Stump's) has the pet's somewhere along it.
                var winX = _tracker.Names.IsPetTag(t.Text) ? PetTagWindowX : Math.Max(PetTagWindowX, t.Width / 2);
                _matcher.PrepareRegion(frame, frameWidth, _mask, fx - 80 - winX, fy - 100, fx + 80 + winX, fy + 20);
                // The last pose's animation and facing first; a convincing score ends the search.
                var lf = _petLast?.Match.Frame;
                foreach (var f in species.Frames.OrderBy(f => lf is null ? 0 : (f.Facing == lf.Facing ? 0 : 2) + (f.Anim == lf.Anim ? 0 : 1)))
                {
                    if (_matcher.Best(new CaptureRect(fx, fy, 1, 1), new PointD(fx, fy), species, f, PetThreshold, winX, PetTagWindowY, minComparable: PetMinComparable) is { } m
                        && m.Score >= PetThreshold && (best is null || m.Score > best.Value.Score)) best = m;
                    if (best is { } b && (b.Score >= PetGoodScore || b.Matches == b.Compared)) break;
                }
                if (best is not null) { PetAtTag++; break; }
            }
            if (best is { } bm) result = TakePet(bm, FoundBy.Search, frame, frameWidth, ctx, camX, camY, tags, clear);
        }
        if (result is null)
        {
            // In a blob near where it was lately, or near the own character it follows.
            var near = _petLast is { } nl && ctx.Time - nl.Time <= EntityTracker.MaxMissedSeconds ? new PointD(nl.World.X - camX, nl.World.Y - camY)
                : own is { } on ? new PointD(on.X + on.Width / 2.0, on.Y + on.Height) : (PointD?)null;
            if (near is { } np && FindPetInBlobs(blobs, frame, frameWidth, ctx, camX, camY, tags, np, PetNearOwnX, PetNearOwnY, clear) is { } r) { result = r; PetInBlob++; }
        }
        PetMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return result;
    }

    /// <summary>The pet found: its explained pixels leave the foreground (and on a temporal frame every later comparison), its tag is the one under its feet.</summary>
    private PetFind TakePet(SpriteMatch pm, FoundBy found, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, List<TagBox> tags, bool clear)
    {
        var vp = _mask.Viewport;
        var pts = ctx.Pts ?? ctx.Time;
        PetFind result;
        {
            if (!_matcher.PatchCovers(pm.Left, pm.Top, pm.Left + pm.Frame.Width - 1, pm.Top + pm.Frame.Height - 1))
                _matcher.PrepareRegion(frame, frameWidth, _mask, pm.Left - 2, pm.Top - 2, pm.Left + pm.Frame.Width + 2, pm.Top + pm.Frame.Height + 2);
            var (pixels, extent) = _matcher.Explain(pm, _fg.Mask, _fg.Width, _fg.Height, vp, clear: true, clear ? _explained : null);
            var box = extent ?? new CaptureRect(pm.Left + pm.Frame.X0, pm.Top + pm.Frame.Y0, pm.Frame.ExtentWidth, pm.Frame.ExtentHeight);
            // Its tag: a tag box under its feet (any read of it: the pet's name, a piece of it, or a tag run into it).
            var tag = -1;
            for (var i = 0; i < tags.Count && tag < 0; i++)
            {
                var t = tags[i];
                double dx = t.CentreX + vp.X - pm.Feet.X, dy = t.Y0 + vp.Y - pm.Feet.Y;
                if (Math.Abs(dx) <= PetTagSideways && dy >= PetTagAboveMin && dy <= PetTagAboveMax
                    && (_tracker.Names.IsPetTag(t.Text) || _tracker.Names.IsPetFragment(t.Text) || t.Text is null)) tag = i;
            }
            result = new PetFind(pm, found, pixels, box, tag);
            var world = new PointD(camX + pm.Feet.X, camY + pm.Feet.Y);
            var since = _petLast is { } p0 && p0.Match.Frame == pm.Frame ? p0.Since : pts;
            _petLast = (pm, world, ctx.Time, since);
        }
        return result;
    }

    /// <summary>
    /// The pet in a pet-sized blob near a point (within <paramref name="dx"/> columns and <paramref name="dy"/> rows of its bottom),
    /// nearest first: the pet's frames placed on the blob as a mob's are on its blob, its last pose first. The pet's frames do not
    /// show its accessories (Mara's red bow), so a whole pet agrees on 0.4 to 0.75 of its pixels.
    /// </summary>
    private PetFind? FindPetInBlobs(List<Blob> blobs, ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, int camX, int camY, List<TagBox> tags,
        PointD near, double dx, double dy, bool clear)
    {
        var species = PetSpecies!;
        var vp = _mask.Viewport;
        var lf = _petLast?.Match.Frame;
        var order = species.Frames.OrderBy(f => lf is null ? 0 : (f.Facing == lf.Facing ? 0 : 2) + (f.Anim == lf.Anim ? 0 : 1)).Select(f => (species, f)).ToList();
        foreach (var b in blobs.Where(b => b.Width <= PetMaxWidth && b.Height <= PetMaxHeight && b.Pixels >= PetMinBlobPixels
                     && Math.Abs((b.X0 + b.X1) / 2.0 + vp.X - near.X) <= dx && Math.Abs(b.Y1 + vp.Y - near.Y) <= dy)
                     .OrderBy(b => Math.Abs((b.X0 + b.X1) / 2.0 + vp.X - near.X)).ToList())
        {
            var box = new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height);
            _matcher.Prepare(frame, frameWidth, _mask, box, null);
            // The frames that fit the blob, the last pose first, until one is good enough.
            SpriteMatch? m = null;
            foreach (var (sp, f) in order)
            {
                if (!SpriteMatcher.Fits(f, box.Width, box.Height)) continue;
                if (_matcher.Best(box, null, sp, f, Math.Max(PetThreshold, m?.Score ?? 0)) is { } x && x.Score >= PetThreshold && (m is null || x.Score > m.Value.Score)) m = x;
                if (m is { } g && (g.Score >= PetGoodScore || g.Matches == g.Compared)) break;
            }
            if (m is not { } pm) continue;
            blobs.Remove(b);
            return TakePet(pm, FoundBy.Search, frame, frameWidth, ctx, camX, camY, tags, clear);
        }
        return null;
    }

    /// <summary>Smallest blob the pet is looked for in (a pet half hidden still shows a few hundred pixels).</summary>
    public const int PetMinBlobPixels = 200;

    /// <summary>Pets found in a blob since construction.</summary>
    public int PetInBlob { get; private set; }

    /// <summary>
    /// Identifies the pet's species under a tag reading its name alone: every pet's every frame placed by its origin over the tag,
    /// the best needing <see cref="PetIdentifyScore"/> and no other species within <see cref="PetIdentifyMargin"/>.
    /// </summary>
    private void IdentifyPet(ReadOnlySpan<byte> frame, int frameWidth, DetectionContext ctx, List<TagBox> tags, List<int> petTags)
    {
        var vp = _mask.Viewport;
        foreach (var i in petTags)
        {
            var t = tags[i];
            PetIdentifyTries++;
            int fx = (int)Math.Round(t.CentreX + vp.X), fy = t.Y0 + vp.Y - 3;
            _matcher.PrepareRegion(frame, frameWidth, _mask, fx - 90, fy - 110, fx + 90, fy + 20);
            var bySpecies = new List<SpriteMatch>();
            foreach (var species in Pets!.Groups.Values)
            {
                SpriteMatch? best = null;
                foreach (var f in species.Frames)
                    if (_matcher.Best(new CaptureRect(fx, fy, 1, 1), new PointD(fx, fy), species, f, PetIdentifyScore - PetIdentifyMargin, 10, 6) is { } m
                        && m.Score >= PetIdentifyScore - PetIdentifyMargin && (best is null || m.Score > best.Value.Score)) best = m;
                if (best is { } b) bySpecies.Add(b);
            }
            var ranked = bySpecies.OrderByDescending(m => m.Score).ToList();
            if (ranked.Count == 0 || ranked[0].Score < PetIdentifyScore || ranked.Count > 1 && ranked[1].Score > ranked[0].Score - PetIdentifyMargin) continue;
            PetSpecies = ranked[0].Species;
            PetIdentified?.Invoke(ranked[0].Species, ctx.Sequence, ranked[0]);
            return;
        }
    }

    /// <summary>
    /// A tag at the pet's tag position (under where the pet stood last) reading a piece of its name is the pet's: the pet's tag run
    /// into a mob's ("ara oar", "ara-v, 11") never makes a player.
    /// </summary>
    private void MarkPetFragments(List<Candidate> candidates, int camX, int camY, double time)
    {
        if (_petLast is not { } last || time - last.Time > EntityTracker.MaxMissedSeconds) return;
        var vp = _mask.Viewport;
        double px = last.World.X - camX, py = last.World.Y - camY;
        foreach (var c in candidates)
            if (c.Tag is { } t && c.PetSprite is null && !_tracker.Names.IsPetTag(t.Text) && _tracker.Names.IsPetFragment(t.Text)
                && Math.Abs(t.CentreX + vp.X - px) <= PetTagSideways + PetFragmentSlack && t.Y0 + vp.Y - py >= PetTagAboveMin - PetFragmentSlack && t.Y0 + vp.Y - py <= PetTagAboveMax + PetFragmentSlack)
            {
                c.PetTag = true;
                PetFragments++;
            }
    }

    /// <summary>Slack around the pet's tag position for a fragment, since the pet moved since it was last seen.</summary>
    public const int PetFragmentSlack = 30;

    /// <summary>Tags reading a piece of the pet's name taken as the pet's, since construction.</summary>
    public int PetFragments { get; private set; }

    /// <summary>
    /// What the pet's frame left unexplained (the coin it carries, its outline over its tag) is no other sprite: an untagged blob
    /// nothing verified whose box the pet's frame extent covers by half is dropped (on fx-combat-exact's keyframe 436 Mara carrying a
    /// coin was 'Mob?' beside the pet).
    /// </summary>
    private void DropPetLeftovers(List<Candidate> candidates)
    {
        var pet = candidates.FirstOrDefault(c => c.PetSprite is not null)?.PetSprite;
        if (pet is not { } p) return;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var c = candidates[i];
            if (c.Sprite is not null || c.Drop is not null || c.Damage is not null || c.Tag is not null || c.PetSprite is not null || c.Skill is not null || c.Pixels <= 0) continue;
            if (Covers(p, c.Box) < LeftoverShare) continue;
            candidates.RemoveAt(i);
            ExplainedBlobs++;
        }
    }

    /// <summary>
    /// The pet as a candidate: its explained pixels' extent, its feet at the sprite's origin, its tag (the tag at its place, which
    /// no other candidate then takes). Blobs mostly inside its silhouette are its own and are dropped by <see cref="DropExplainedBlobs"/>.
    /// </summary>
    private Candidate PetCandidate(PetFind p, List<TagBox> tags, DetectionContext ctx, int camX, int camY)
    {
        var tag = p.Tag >= 0 ? tags[p.Tag] : null;
        var c = MakeCandidate(ctx, camX, camY, p.Box, p.Match.Feet, Math.Max(1, p.Pixels), tag, false, false, null, _ownWorld);
        c.PetSprite = p.Match;
        c.PetTag = tag is not null;
        c.Found = p.Found;
        c.Floor = FloorFor(c, ctx);
        return c;
    }
}
