namespace OakSoul.Overlay;

/// <summary>A field an item of a layer may carry: its name, JSON type and meaning.</summary>
public sealed record OverlayField(string Name, string Type, string Meaning);

/// <summary>A layer: what it shows, the kinds of item it holds and the fields they may carry.</summary>
public sealed record OverlayLayer(string Name, string Description, IReadOnlyList<(string Kind, string Meaning)> Kinds, IReadOnlyList<OverlayField> Fields)
{
    public bool HasKind(string kind) => Kinds.Any(k => k.Kind == kind);
    public bool HasField(string name) => Fields.Any(f => f.Name == name);
}

/// <summary>
/// The layer registry: every layer of the overlay stream with its item kinds and fields, in drawing order. The producer
/// (<see cref="OverlayBuilder"/>) emits only what is registered here, the stream's header lists the layers it holds, and
/// <c>--list-layers</c> prints this. <see cref="Check"/> validates an item against it.
/// </summary>
public static class OverlayLayers
{
    public const string World = "world", Minimap = "minimap", Hud = "hud", Camera = "camera", Tags = "tags", Self = "self", Entity = "entity";

    private static OverlayField F(string name, string type, string meaning) => new(name, type, meaning);

    private static readonly OverlayField Rect = F("rect", "[x,y,w,h]", "box in frame pixels");
    private static readonly OverlayField Label = F("label", "string", "the text drawn next to it");

    public static readonly IReadOnlyList<OverlayLayer> All =
    [
        new(World, "map geometry from the client's data near the camera, projected through the frame's camera (screen = world - camera)",
            [("foothold", "a walkable segment (a wall when vertical)"), ("ladder", "a ladder"), ("rope", "a rope"), ("portal", "a portal")],
            [F("line", "[[x,y],...]", "foothold, ladder or rope in frame pixels"), F("point", "[x,y]", "portal position in frame pixels"),
             F("id", "int", "foothold id (the client's); index of the ladder or rope in the map's list"), F("wall", "bool", "the foothold is vertical"),
             F("name", "string", "portal name"), F("type", "int", "portal type (0 spawn point)"), F("target", "int", "portal's target map id"),
             F("targetName", "string", "portal's target portal"), Label]),
        new(Minimap, "the minimap panel as read: panel, map image, dots, and the name block with the names read and the map id they resolved to",
            [("panel", "the panel as drawn"), ("map", "the map image inside it"), ("dot", "a marker on the map image"), ("names", "the name block OCR reads")],
            [Rect, F("collapsed", "bool", "the panel is collapsed to its title bar"), F("point", "[x,y]", "dot centre"),
             F("dot", "string", "self, player, portal, npc or other"), F("pixels", "int", "dot size in pixels"),
             F("street", "string", "street (region) name read"), F("mapName", "string", "map name read"), F("mapId", "int", "map id the world tracker resolved"),
             F("unsoftened", "bool", "the names were read from the unsoftened crop"), Label]),
        new(Hud, "the HUD layout's regions labelled with their readings, the quickslot cells and the buff icons",
            [("region", "a fixed region of the HUD layout"), ("slot", "a quickslot cell"), ("buff", "a buff icon"), ("cursor", "the mouse cursor, matched exactly against the client's cursor frames")],
            [Rect, F("name", "string", "region name in the layout (status.hp, ...); buff name"), F("level", "int", "character level"),
             F("job", "string", "job line"), F("character", "string", "name line"), F("hp", "[cur,max]", "HP numbers"), F("mp", "[cur,max]", "MP numbers"),
             F("exp", "long", "EXP total"), F("percent", "double", "EXP percent"), F("bar", "double", "fill ratio of the bar"),
             F("slots", "string", "occupied quickslot keys ('.' empty)"), F("buffs", "string", "active buffs ('~' faded)"),
             F("key", "string", "quickslot key"), F("occupied", "bool", "the cell holds something"), F("faded", "bool", "the buff icon is drawn faded"),
             F("correlation", "double", "buff icon correlation"), F("point", "[x,y]", "cursor: its hotspot (the frame's origin)"),
             F("state", "string", "cursor: the client's cursor state (UI Basic.img/Cursor/N)"), F("frame", "int", "cursor: frame of the state"),
             F("score", "double", "cursor: share of its pixels that agree"), Label]),
        new(Camera, "the frame's camera (world position of the screen's top-left): the world tracker's guess and the registered camera",
            [("camera", "the camera")],
            [F("camera", "[x,y]", "the camera used (registered when the detector registered the frame)"), F("guess", "[x,y]", "the world tracker's camera before registration"),
             F("offset", "[dx,dy]", "registered minus guess"), F("score", "double", "registration match fraction"),
             F("source", "string", "what placed the character: dot+tag, dot, tag, held"), F("confidence", "double", "world tracker confidence"), Label]),
        new(Tags, "name and level tags found in the world (own, players, pets, hit mobs), with their OCR text",
            [("tag", "a tag box")],
            [Rect, F("text", "string", "OCR text"), F("level", "int", "level read from the badge (0: badge seen, number not read)"),
             F("name", "string", "name parsed from the text"), Label]),
        new(Self, "the own character: its name tag and feet, the box masked out of detection, world position and what it stands on",
            [("tag", "the own name tag as located"), ("character", "the own character")],
            [Rect, F("feet", "[x,y]", "tag: the point above the tag; character: the soles on screen"), F("agree", "[n,of]", "tag: text columns matched of expected"),
             F("masked", "bool", "character: rect is the box the detector masked out"), F("world", "[x,y]", "world position (soles)"),
             F("foothold", "int", "foothold stood on"), F("support", "string", "fh123, rope@86, ladder@40 or air"),
             F("source", "string", "dot+tag, dot, tag or held"), F("confidence", "double", "world tracker confidence"), Label]),
        new(Entity, "every detected entity (EntityDetector.Detect): box, feet, world position, track, kind and the evidence for its identity",
            [("mob", "a monster"), ("player", "another player"), ("pet", "the own pet (one track for the session, found by its sprite or its tag)"), ("drop", "an item or meso drop"),
             ("effect", "spells and other brief overlays (own skill effects named by their skill)"), ("damage", "a damage number read from the client's digit sprites"), ("unknown", "foreground that fits no kind"),
             ("hpbar", "a mob's HP bar, read exactly, attached to the mob below it")],
            [F("rect", "[x,y,w,h]", "box in frame pixels: an identified mob's hitbox (see box), a damage number's digits, else the foreground blob"),
             F("box", "string", "what an identified mob's rect is: hitbox (the client's lt/rb body rectangle of the frame, at the sprite origin) or sprite (the frame's visible extent, for frames without lt/rb)"),
             F("blob", "[x,y,w,h]", "an identified mob's foreground blob extent, when it had one"),
             F("feet", "[x,y]", "where it stands on screen (a matched sprite's origin; an identified mob's snapped onto its foothold)"), F("world", "[x,y]", "world position of the feet"),
             F("foothold", "int", "foothold under the feet (within 6 units)"), F("surface", "double", "signed distance from the feet to the nearest surface (units, + below)"),
             F("track", "int", "track id (changes between regenerations)"), F("age", "int", "frames the track has been seen in"),
             F("confidence", "double", "track's kind confidence"), F("velocity", "[vx,vy]", "world units per second"), F("pixels", "int", "foreground pixels"),
             F("hpBar", "bool", "an HP bar was seen over it"), F("tag", "string", "tag text read on it in this frame"),
             F("name", "string", "species (mob with identity), else the name its tags showed"), F("level", "int", "level from its tags (0: badge seen, number not read)"),
             F("tagName", "string", "the name its tags showed, when it differs from the species"),
             F("mob", "string", "species id, 7 digits as in data/mobinfo"), F("anim", "string", "animation of the matched frame"), F("frame", "int", "frame index"),
             F("facing", "string", "L or R"), F("score", "double", "share of visible sprite (or icon, or digit pair) pixels that agree, in this frame"),
             F("lastScore", "double", "a carried identity: the score of its last match"), F("lastMatchedSeq", "long", "a carried identity: capture sequence of its last match"),
             F("carriedFor", "double", "a carried identity: seconds since its last match"), F("lastMatchedAt", "double", "a carried identity: session time of its last match"),
             F("state", "string", "dying: the die pose matched (now or last), or an EXP gain credited the kill to the track"),
             F("sweep", "bool", "found by the direct search along a foothold"),
             F("found", "string", "how it was found in this frame: prediction (its track's predicted pose verified at the predicted place), transition (another pose of its species verified there: a neighbouring frame, stand or move, the other facing, hit, die) or search (the full search); for the pet also tag (its tag alone)"),
             F("value", "long", "damage number value"), F("digits", "string", "damage number digits as read"), F("direction", "string", "damage: dealt (NoRed, NoCri), taken (NoViolet) or other"),
             F("critical", "bool", "damage: a critical hit (NoCri)"), F("set", "string", "damage: the digit set family"), F("complete", "bool", "damage: the leftmost digit is a first-digit glyph (no leading digit hidden)"),
             F("alpha", "double", "damage: the opacity the digits were drawn with (they fade out at the end)"),
             F("evidence", "string", "matched (verified in this frame), carried (identity carried by the track; the sheets' ~) or unverified (the sheets' ?); for the pet, matched or tag"),
             F("sightings", "int", "frames that matched the track's species"), F("tint", "string", "colour state of the match: drawn, frozen, steam or frozen+steam"),
             F("fogged", "int", "pixels that matched only through steam"), F("basis", "string", "what the species rests on: shape, pixels, track or spawns"),
             F("agree", "[n,of]", "sprite or icon pixels agreeing of compared"), F("sprite", "[x,y,w,h]", "the matched frame's visible extent on screen"),
             F("rival", "string", "the species contest's best other species (id)"), F("rivalName", "string", "its name"), F("rivalScore", "double", "its score"),
             F("match", "string", "a sprite matched in this frame that the identity does not reflect (another species, or over the spawn cap)"),
             F("item", "string", "drop item id, 8 digits (09000000.. meso)"), F("category", "string", "drop item category"),
             F("anchor", "[x,y]", "meso coin: its anchor (the client's origin of its spinning frames, the same for every frame)"),
             F("inFlight", "bool", "drop: matched in the air, popping out of a killed mob"),
             F("petSpecies", "string", "pet: the client's pet id (7 digits) its frames matched"), F("speciesName", "string", "pet: the species' name (Black Kitty)"),
             F("skill", "string", "effect: the own skill whose effect frame matched (skill id)"), F("skillName", "string", "effect: the skill's name (Cold Beam)"),
             F("hpFill", "double", "mob: the fill of the HP bar over it (0..1)"),
             F("fill", "double", "hpbar: current HP as a share of the bar, to a column (1/46)"), F("fillColumns", "int", "hpbar: fill columns of 46"),
             F("colour", "string", "hpbar: the fill's colour (green over red, or red over black; empty, covered)"), Label]),
    ];

    public static IReadOnlyList<string> Names => All.Select(l => l.Name).ToList();

    public static OverlayLayer? Find(string name) => All.FirstOrDefault(l => l.Name == name);

    /// <summary>Problems with an item against the registry (unknown layer, kind or field); empty when it conforms.</summary>
    public static IEnumerable<string> Check(OverlayItem item)
    {
        var layer = Find(item.Layer);
        if (layer is null) { yield return $"unknown layer {item.Layer}"; yield break; }
        if (!layer.HasKind(item.Kind)) yield return $"layer {item.Layer}: unknown kind {item.Kind}";
        foreach (var (name, _) in item.Fields)
            if (!layer.HasField(name)) yield return $"layer {item.Layer}: unknown field {name}";
    }

    public static void Print(TextWriter o)
    {
        o.WriteLine($"overlay stream {OverlayHeader.CurrentSchema}: layers in drawing order, their item kinds and fields");
        o.WriteLine("every item has layer and kind; geometry is integer pixels of the 1366x768 frame, world positions map units");
        foreach (var l in All)
        {
            o.WriteLine();
            o.WriteLine($"{l.Name}: {l.Description}");
            o.WriteLine($"  kinds: {string.Join(", ", l.Kinds.Select(k => $"{k.Kind} ({k.Meaning})"))}");
            foreach (var f in l.Fields) o.WriteLine($"  {f.Name,-12} {f.Type,-12} {f.Meaning}");
        }
    }
}
