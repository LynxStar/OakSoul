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
    IReadOnlyList<Life> Life,
    bool Cloud,
    IReadOnlyList<BackLayer> Back);

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

/// <summary>pt is the portal type; targetMap 999999999 means no destination; script is the portal's script, empty for most.</summary>
internal sealed record Portal(string Name, int Type, int X, int Y, int TargetMap, string TargetName, string Script);

/// <summary>type "m" is a mob, "n" an NPC; id kept as stored (may have leading zeros); fh is the foothold id.</summary>
internal sealed record Life(string Type, string Id, int X, int Y, int Fh);

/// <summary>
/// One back/&lt;n&gt; entry: the art (bS, no, ani: Map/Back/&lt;bS&gt;.img back/&lt;no&gt;, or ani/&lt;no&gt; when animated), its
/// place (x, y), parallax factors (rx, ry), tiling distances (cx, cy), type (0 static, 1 tiled horizontally, 2
/// vertically, 3 both, 4 to 7 the same moving: 4 and 6 horizontally, 5 and 7 vertically), alpha a, flip f and whether it
/// is drawn in front of the map (front 1). The order is the stored order, the draw order.
/// </summary>
internal sealed record BackLayer(int Index, string Bs, int No, bool Ani, int X, int Y, int Rx, int Ry, int Cx, int Cy, int Type, int A, bool Front, bool Flip);

internal sealed record IndexEntry(string? StreetName, string? MapName, string File);
