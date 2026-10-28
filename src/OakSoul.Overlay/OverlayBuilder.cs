using OakSoul.Capture;
using OakSoul.Detect;
using OakSoul.Vision;
using OakSoul.Vision.Readers;
using OakSoul.World;

namespace OakSoul.Overlay;

/// <summary>
/// What the watcher holds about one frame when it builds the overlay: the readings, the world state (the tracker's, and
/// after the registered camera was fed back), and the entity stage's result. Nothing here is computed for the overlay.
/// </summary>
/// <param name="Time">Seconds into the session (pts minus the session's first capture pts).</param>
/// <param name="TrackerPlace">The world tracker's state before the entity stage refined it (its camera is the guess).</param>
/// <param name="Place">The world state after the entity stage (the registered camera fed back), or the tracker's.</param>
/// <param name="Entities">The detector's result when it ran on this frame.</param>
/// <param name="Tags">The tag boxes the detector found in this frame (viewport coordinates), when it ran.</param>
/// <param name="OwnBox">The own-character box the detector masked out, when it ran and registered.</param>
/// <param name="Skip">Why the detector did not run on this frame, when it did not.</param>
public sealed record OverlayInputs(
    long Sequence, double Pts, bool Filled, double Time, HudLayout Layout,
    StatusReading Status, IdentityReading? Identity, IReadOnlyList<QuickslotCell> Slots, IReadOnlyList<BuffIcon> Buffs,
    MinimapReading Minimap, (string Region, string Map)? MapNames, CaptureRect? NameBlock, bool NamesUnsoftened,
    NameTagHit? OwnTag, WorldState? TrackerPlace, WorldState? Place,
    EntityFrame? Entities, IReadOnlyList<TagBox>? Tags, CaptureRect? OwnBox, string? Skip);

/// <summary>
/// Builds a frame's <see cref="OverlayFrame"/> from <see cref="OverlayInputs"/>: a projection of state the watcher already
/// holds, rounded to integer pixels, with no detection of its own. Runs on the watcher's thread (well under a millisecond);
/// serialization happens on <see cref="OverlayWriter"/>'s task.
/// </summary>
public static class OverlayBuilder
{
    /// <summary>World geometry is listed when it comes within this many pixels of the frame.</summary>
    public const int WorldMargin = 0;

    public static OverlayFrame Build(OverlayInputs x)
    {
        var place = x.Place ?? x.TrackerPlace;
        var camera = place?.Camera;
        var items = new List<OverlayItem>(64);
        if (place?.Map is { } map && camera is { } cam) World(items, map, cam, x.Layout.FrameWidth, x.Layout.FrameHeight);
        Minimap(items, x, place);
        Hud(items, x);
        CameraItem(items, x, place);
        if (x.Tags is { } tags) Tags(items, tags, x.Layout);
        Self(items, x, place);
        if (x.Entities is { } result)
        {
            foreach (var e in result.Entities) items.Add(Entity(e, x.Time));
            foreach (var b in result.HpBars) items.Add(HpBarItem(b));
            if (result.Cursor is { } cursor)
                items.Add(new OverlayItem(OverlayLayers.Hud, "cursor").Set("rect", Rc(cursor.Rect)).Set("point", P(cursor.Hotspot)).Set("state", cursor.State)
                    .Set("frame", cursor.Frame).Set("score", D2(cursor.Score)).Set("label", "cursor"));
        }
        return new OverlayFrame
        {
            Sequence = x.Sequence, Pts = x.Pts, Filled = x.Filled, Time = Rd(x.Time, 3),
            MapId = place?.MapId, Camera = camera is { } c ? (R(c.X), R(c.Y)) : null,
            Detect = x.Entities is null ? null : x.Entities.Note ?? "ok",
            Skip = x.Entities is null ? x.Skip ?? "entities off" : null,
            Items = items,
        };
    }

    private static int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    private static int[] P(PointD p) => [R(p.X), R(p.Y)];
    private static int[] P(double x, double y) => [R(x), R(y)];
    private static int[] Rc(CaptureRect r) => [r.X, r.Y, r.Width, r.Height];
    private static double D2(double v) => Rd(v, 2);

    /// <summary>Rounded, and never negative zero (which would print as "-0" and not survive a round trip).</summary>
    private static double Rd(double v, int digits) => Math.Round(v, digits) is var r && r == 0 ? 0 : r;

    private static void World(List<OverlayItem> items, MapData map, PointD cam, int width, int height)
    {
        int x0 = -WorldMargin, y0 = -WorldMargin, x1 = width + WorldMargin, y1 = height + WorldMargin;
        bool Visible(double ax, double ay, double bx, double by) =>
            Math.Max(ax, bx) - cam.X >= x0 && Math.Min(ax, bx) - cam.X <= x1 && Math.Max(ay, by) - cam.Y >= y0 && Math.Min(ay, by) - cam.Y <= y1;
        foreach (var f in map.Footholds)
        {
            if (!Visible(f.X1, f.Y1, f.X2, f.Y2)) continue;
            items.Add(new OverlayItem(OverlayLayers.World, "foothold").Set("line", new[] { P(f.X1 - cam.X, f.Y1 - cam.Y), P(f.X2 - cam.X, f.Y2 - cam.Y) })
                .Set("id", f.Id).Set("wall", f.IsWall ? true : null));
        }
        for (var i = 0; i < map.LadderRopes.Count; i++)
        {
            var l = map.LadderRopes[i];
            if (!Visible(l.X, l.Y1, l.X, l.Y2)) continue;
            items.Add(new OverlayItem(OverlayLayers.World, l.Kind).Set("line", new[] { P(l.X - cam.X, l.Y1 - cam.Y), P(l.X - cam.X, l.Y2 - cam.Y) }).Set("id", i));
        }
        foreach (var p in map.Portals)
        {
            if (!Visible(p.X, p.Y, p.X, p.Y)) continue;
            items.Add(new OverlayItem(OverlayLayers.World, "portal").Set("point", P(p.X - cam.X, p.Y - cam.Y)).Set("name", p.Name).Set("type", p.Type)
                .Set("target", p.HasTarget ? p.TargetMap : null).Set("targetName", p.HasTarget && p.TargetName.Length > 0 ? p.TargetName : null)
                .Set("label", p.HasTarget ? $"{p.Name} -> {p.TargetMap}" : p.Name));
        }
    }

    private static void Minimap(List<OverlayItem> items, OverlayInputs x, WorldState? place)
    {
        var m = x.Minimap;
        if (!m.Valid) return;
        items.Add(new OverlayItem(OverlayLayers.Minimap, "panel").Set("rect", Rc(m.Panel)).Set("collapsed", m.Collapsed ? true : null));
        if (!m.Collapsed) items.Add(new OverlayItem(OverlayLayers.Minimap, "map").Set("rect", Rc(m.MapArea)));
        foreach (var d in m.Dots) items.Add(Dot(d));
        if (m.Self is { } self && !m.Dots.Contains(self)) items.Add(Dot(self));
        if (x.NameBlock is { } block)
        {
            var label = x.MapNames is { } n ? $"{n.Region} / {n.Map}{(place?.MapId is { } id ? $" -> {id}" : "")}" : "names not read";
            items.Add(new OverlayItem(OverlayLayers.Minimap, "names").Set("rect", Rc(block)).Set("street", x.MapNames?.Region).Set("mapName", x.MapNames?.Map)
                .Set("mapId", place?.MapId).Set("unsoftened", x.NamesUnsoftened ? true : null).Set("label", label));
        }
    }

    private static OverlayItem Dot(MinimapDot d) =>
        new OverlayItem(OverlayLayers.Minimap, "dot").Set("point", P(d.X, d.Y)).Set("dot", d.Kind.ToString().ToLowerInvariant()).Set("pixels", d.Pixels);

    /// <summary>Regions of the layout that are always drawn and read (or HUD chrome); the minimap's are in the minimap layer.</summary>
    private static readonly string[] HudRegions = ["status.level", "status.class_name", "status.hp", "status.mp", "status.exp", "status.buttons",
        "chat.input_line", "chat.icons", "quickslot", "buffs.row1", "exp_loot_log"];

    private static void Hud(List<OverlayItem> items, OverlayInputs x)
    {
        var s = x.Status;
        foreach (var name in HudRegions)
        {
            if (!x.Layout.TryGet(name, out var region)) continue;
            var item = new OverlayItem(OverlayLayers.Hud, "region").Set("rect", Rc(region.Rect)).Set("name", name);
            switch (name)
            {
                case "status.level":
                    item.Set("level", x.Identity?.Level).Set("label", $"Lv {x.Identity?.Level?.ToString() ?? "?"}");
                    break;
                case "status.class_name":
                    // The label is the name alone so it fits over the region; the job is in its field.
                    item.Set("job", x.Identity?.Job).Set("character", x.Identity?.Name).Set("label", x.Identity?.Name ?? "?");
                    break;
                case "status.hp":
                    item.Set("hp", s.HpValue is { } hp ? new[] { (int)hp.Current, (int)hp.Maximum } : null).Set("bar", s.Hp.Valid ? Rd(s.Hp.Ratio, 3) : null)
                        .Set("label", s.HpValue is { } h ? $"HP {h.Current}/{h.Maximum}" : "HP ?");
                    break;
                case "status.mp":
                    item.Set("mp", s.MpValue is { } mp ? new[] { (int)mp.Current, (int)mp.Maximum } : null).Set("bar", s.Mp.Valid ? Rd(s.Mp.Ratio, 3) : null)
                        .Set("label", s.MpValue is { } m ? $"MP {m.Current}/{m.Maximum}" : "MP ?");
                    break;
                case "status.exp":
                    item.Set("exp", s.ExpValue?.Total).Set("percent", s.ExpValue is { } e0 ? Rd(e0.Percent, 2) : null).Set("bar", s.Exp.Valid ? Rd(s.Exp.Ratio, 3) : null)
                        .Set("label", s.ExpValue is { } e ? $"EXP {e.Total:N0} {e.Percent:0.00}%" : "EXP ?");
                    break;
                case "quickslot":
                    var slots = string.Concat(x.Slots.Select(c => c.Occupied ? c.Key : '.'));
                    item.Set("slots", slots).Set("label", $"slots {slots}");
                    break;
                case "buffs.row1":
                    var buffs = string.Join("+", x.Buffs.Select(b => b.Name + (b.Faded ? "~" : "")));
                    item.Set("buffs", buffs).Set("label", buffs.Length == 0 ? "no buffs" : $"buffs {buffs}");
                    break;
            }
            items.Add(item);
        }
        foreach (var c in x.Slots)
            items.Add(new OverlayItem(OverlayLayers.Hud, "slot").Set("rect", Rc(c.Rect)).Set("key", c.Key.ToString()).Set("occupied", c.Occupied));
        foreach (var b in x.Buffs)
            items.Add(new OverlayItem(OverlayLayers.Hud, "buff").Set("rect", Rc(b.Rect)).Set("name", b.Name).Set("faded", b.Faded ? true : null)
                .Set("correlation", D2(b.Correlation)).Set("label", b.Name + (b.Faded ? "~" : "")));
    }

    public static string SourceName(WorldSource s) => s switch
    {
        WorldSource.DotAndTag => "dot+tag",
        WorldSource.Dot => "dot",
        WorldSource.TagHeldCamera => "tag",
        WorldSource.Held => "held",
        _ => "none",
    };

    private static void CameraItem(List<OverlayItem> items, OverlayInputs x, WorldState? place)
    {
        if (place?.Camera is not { } cam) return;
        var guess = x.TrackerPlace?.Camera;
        var reg = x.Entities?.Registration;
        var registered = x.Entities?.Camera is not null;
        int[]? offset = registered && guess is { } g ? [R(cam.X) - R(g.X), R(cam.Y) - R(g.Y)] : null;
        var source = SourceName(x.TrackerPlace?.Source ?? place.Source);
        var label = $"cam {R(cam.X)},{R(cam.Y)}" + (offset is { } o ? $" reg {o[0]:+0;-0;0},{o[1]:+0;-0;0} {reg?.Score:0.00}" : reg is { } r ? $" reg failed {r.Score:0.00}" : "") + $" {source}";
        items.Add(new OverlayItem(OverlayLayers.Camera, "camera").Set("camera", P(cam)).Set("guess", guess is { } gg ? P(gg) : null).Set("offset", offset)
            .Set("score", reg is { } rr ? Rd(rr.Score, 3) : null).Set("source", source).Set("confidence", D2(place.Confidence)).Set("label", label));
    }

    private static void Tags(List<OverlayItem> items, IReadOnlyList<TagBox> tags, HudLayout layout)
    {
        var vp = layout["world.viewport"].Rect;
        foreach (var t in tags)
            items.Add(new OverlayItem(OverlayLayers.Tags, "tag").Set("rect", new[] { t.X0 + vp.X, t.Y0 + vp.Y, t.Width, t.Height }).Set("text", t.Text)
                .Set("level", t.Level).Set("name", t.Name).Set("label", t.Text));
    }

    private static void Self(List<OverlayItem> items, OverlayInputs x, WorldState? place)
    {
        if (x.OwnTag is { } tag)
            items.Add(new OverlayItem(OverlayLayers.Self, "tag").Set("rect", Rc(tag.Tag)).Set("feet", P(tag.Feet.X, tag.Feet.Y)).Set("agree", new[] { tag.Matched, tag.Expected }));
        var world = place?.Position;
        if (x.OwnBox is null && world is null) return;
        var item = new OverlayItem(OverlayLayers.Self, "character").Set("rect", x.OwnBox is { } box ? Rc(box) : null).Set("masked", x.OwnBox is not null ? true : null);
        if (world is { } w && place!.Camera is { } cam) item.Set("feet", P(w.X - cam.X, w.Y - cam.Y));
        if (world is { } wp)
            item.Set("world", P(wp)).Set("foothold", place!.Foothold?.Foothold.Id).Set("support", place.Support).Set("source", SourceName(place.Source))
                .Set("confidence", D2(place.Confidence)).Set("label", $"self {R(wp.X)},{R(wp.Y)} {place.Support}");
        items.Add(item);
    }

    /// <summary>How an entity was found: a track's predicted pose verified, a transition of it verified, or the full search.</summary>
    public static string Found(FoundBy f) => f switch
    {
        FoundBy.Prediction => "prediction",
        FoundBy.Transition => "transition",
        FoundBy.Tag => "tag",
        _ => "search",
    };

    /// <summary>The colour state of a sprite match: as drawn, frozen, through steam, or both.</summary>
    public static string Tint(SpriteMatch m) => (m.Tint == SpriteTint.Frozen, m.Fogged > 0) switch
    {
        (true, true) => "frozen+steam",
        (true, false) => "frozen",
        (false, true) => "steam",
        _ => "drawn",
    };

    /// <summary>A mob's HP bar: its frame, the fill read (share and columns of 46), the fill's colour and the mob below it.</summary>
    public static OverlayItem HpBarItem(HpBar b) =>
        new OverlayItem(OverlayLayers.Entity, "hpbar").Set("rect", Rc(b.Rect)).Set("fill", Rd(b.Fill, 3)).Set("fillColumns", b.FillColumns).Set("colour", b.Colour)
            .Set("track", b.Track).Set("label", $"HP {Math.Round(b.Fill * 100):0}%{(b.Track is { } t ? $" #{t}" : "")}");

    /// <param name="time">The frame's time into the session, for the carried identity's label ("last 1.00 at 23.0").</param>
    public static OverlayItem Entity(DetectedEntity e, double time = 0)
    {
        var kind = e.Kind.ToString().ToLowerInvariant();
        var item = new OverlayItem(OverlayLayers.Entity, kind)
            .Set("rect", Rc(e.DisplayBox)).Set("feet", P(e.Feet)).Set("track", e.TrackId);
        // An identified mob's rect is its hitbox; the blob it was seen as is kept apart.
        if (e.Hitbox is not null) item.Set("box", e.HitboxFromClient ? "hitbox" : "sprite").Set("blob", e.Pixels > 0 ? Rc(e.Box) : null);
        var id = e.Identity;
        var sprite = e.Sprite;
        var verified = id is not null && id.Verified && sprite is { } s0 && s0.Species.Id == id.MobId;
        item.Set("name", id?.Name ?? e.Name).Set("level", e.Level).Set("tagName", id is not null && e.Name is { } n && n != id.Name ? n : null).Set("tag", e.Tag);
        if (id is not null)
        {
            item.Set("mob", id.MobId.ToString("D7", System.Globalization.CultureInfo.InvariantCulture)).Set("anim", id.Anim).Set("frame", id.FrameIndex)
                .Set("facing", id.Facing > 0 ? "R" : "L");
            // A carried identity shows the pose and score of its last match, which is not a score of this frame.
            if (id.Verified) item.Set("score", D2(id.Score));
            else item.Set("lastScore", D2(id.Score)).Set("lastMatchedSeq", id.LastMatchedSequence).Set("carriedFor", Rd(id.CarriedFor, 2))
                .Set("lastMatchedAt", Rd(Math.Max(0, time - id.CarriedFor), 1));
            item.Set("evidence", id.Verified ? "matched" : "carried").Set("sightings", id.Sightings);
            if (e.State == MobState.Dying) item.Set("state", "dying");
        }
        else if (e.Kind == EntityKind.Mob) item.Set("evidence", "unverified");
        if (verified && sprite is { } m)
        {
            item.Set("tint", Tint(m)).Set("fogged", m.Fogged > 0 ? m.Fogged : null).Set("basis", m.Basis.ToString().ToLowerInvariant())
                .Set("agree", new[] { m.Matches, m.Compared })
                .Set("sprite", new[] { m.Left + m.Frame.X0, m.Top + m.Frame.Y0, m.Frame.ExtentWidth, m.Frame.ExtentHeight });
            if (m.Rival is { } rival)
                item.Set("rival", rival.IdText).Set("rivalName", rival.Name).Set("rivalScore", D2(m.RivalScore));
        }
        else if (sprite is { } other)
            item.Set("match", $"{other.Species.IdText} {other.Species.Name} {other.Frame.Anim}/{other.Frame.Index}{(other.Frame.Facing > 0 ? "R" : "L")} {other.Score:0.00}");
        if (e.Swept) item.Set("sweep", true);
        // The pet: its species and pose as matched (the client's pet frames), or found by its tag alone.
        if (e.PetMatch is { } pm)
            item.Set("petSpecies", pm.Species.Id.ToString("D7", System.Globalization.CultureInfo.InvariantCulture)).Set("speciesName", pm.Species.Name).Set("anim", pm.Frame.Anim)
                .Set("frame", pm.Frame.Index).Set("facing", pm.Frame.Facing > 0 ? "R" : "L").Set("score", D2(pm.Score)).Set("evidence", "matched").Set("agree", new[] { pm.Matches, pm.Compared });
        else if (e.Kind == EntityKind.Pet) item.Set("evidence", "tag");
        // An own skill effect: the skill and its frame.
        if (e.Skill is { } sk)
            item.Set("skill", sk.Species.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Set("skillName", sk.Species.Name).Set("anim", sk.Frame.Anim)
                .Set("frame", sk.Frame.Index).Set("facing", sk.Frame.Facing > 0 ? "R" : "L").Set("score", D2(sk.Score)).Set("agree", new[] { sk.Matches, sk.Compared });
        item.Set("found", Found(e.Found));
        if (e.Drop is { } d)
            item.Set("item", d.ItemId).Set("category", d.IsMeso ? "meso" : d.Category).Set("score", D2(d.Score)).Set("evidence", "matched").Set("agree", new[] { d.Matches, d.Compared })
                .Set("frame", d.IsMeso ? d.IconFrame : null).Set("anchor", d.Anchor is { } an ? P(an) : null);
        if (e.InFlight) item.Set("inFlight", true);
        else if (e.Kind == EntityKind.Drop) item.Set("evidence", "unverified");
        if (e.Damage is { } dm)
            item.Set("value", dm.Value).Set("digits", dm.Digits).Set("direction", dm.DirectionText).Set("critical", dm.Critical ? true : null)
                .Set("set", dm.Family).Set("complete", dm.Complete).Set("score", D2(dm.Score)).Set("alpha", D2(dm.Alpha));
        item.Set("world", P(e.World)).Set("foothold", e.FootholdId).Set("surface", e.FootholdDistance is { } fd ? Rd(fd, 1) : null)
            .Set("age", e.Age).Set("confidence", D2(e.Confidence)).Set("velocity", P(e.Velocity)).Set("pixels", e.Pixels > 0 ? e.Pixels : null)
            .Set("hpBar", e.HpBar ? true : null).Set("hpFill", e.HpFill is { } hf ? Rd(hf, 3) : null);
        item.Set("label", OverlayLabels.Entity(item));
        return item;
    }
}
