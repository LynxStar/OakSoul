namespace OakSoul.MapExport;

// Shapes of the exported JSON (serialised camelCase). Coordinates are in map pixels as stored.

internal sealed record MapData(
    int MapId,
    string? StreetName,
    string? MapName,
    int ReturnMap,
    ViewRange Vr,
    MiniMapInfo? MiniMap,
    IReadOnlyList<Foothold> Footholds,
    IReadOnlyList<LadderRope> LadderRopes,
    IReadOnlyList<Portal> Portals,
    IReadOnlyList<Life> Life);

/// <summary>Camera bounds from info/VRLeft, VRTop, VRRight, VRBottom (all 0 when the map does not set them).</summary>
internal sealed record ViewRange(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Minimap placement as stored in miniMap. The usual interpretation (as in HaCreator): a map point (x, y)
/// appears on the minimap canvas at ((x + centerX) >> mag, (y + centerY) >> mag); width and height are the
/// covered area in map pixels.
/// </summary>
internal sealed record MiniMapInfo(int CenterX, int CenterY, int Mag, int Width, int Height);

internal sealed record Foothold(int Id, int Layer, int Group, int X1, int Y1, int X2, int Y2, int Prev, int Next);

/// <summary>A vertical climbable: ladder when l == 1, rope otherwise. uf is "upper floor" (can climb off the top).</summary>
internal sealed record LadderRope(int X, int Y1, int Y2, bool Ladder, int Page, int Uf);

/// <summary>pt is the portal type; targetMap 999999999 means no destination.</summary>
internal sealed record Portal(string Name, int Type, int X, int Y, int TargetMap, string TargetName);

/// <summary>type "m" is a mob, "n" an NPC; id kept as stored (may have leading zeros); fh is the foothold id.</summary>
internal sealed record Life(string Type, string Id, int X, int Y, int Fh);

internal sealed record IndexEntry(string? StreetName, string? MapName, string File);
