using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakSoul.World;

/// <summary>The view bounds the camera is clamped to, in world units.</summary>
public sealed record ViewBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Minimap canvas parameters. In this client's data CenterX == -vr.Left, CenterY == -vr.Top and Width/Height equal
/// the vr size, so the canvas covers exactly the vr box: canvas = (world + center) / mag.
/// </summary>
public sealed record MiniMapInfo(int CenterX, int CenterY, int Mag, int Width, int Height);

/// <summary>A walkable segment. Y grows downward; a segment with X1 == X2 is a wall.</summary>
public sealed record Foothold(int Id, int Layer, int Group, int X1, int Y1, int X2, int Y2, int Prev, int Next)
{
    public bool IsWall => X1 == X2;
    public int MinX => Math.Min(X1, X2);
    public int MaxX => Math.Max(X1, X2);

    /// <summary>The segment's height at x, or null when x lies outside it or the segment is a wall.</summary>
    public double? YAt(double x)
    {
        if (IsWall || x < MinX || x > MaxX) return null;
        return Y1 + (Y2 - Y1) * (x - X1) / (double)(X2 - X1);
    }
}

/// <summary>A ladder or rope: a vertical segment at X from Y1 (top) to Y2 (bottom).</summary>
public sealed record LadderRope(int X, int Y1, int Y2, bool Ladder, int Page, int Uf)
{
    public string Kind => Ladder ? "ladder" : "rope";
}

/// <summary>
/// A portal. Type 0 ("sp") are spawn points; TargetMap 999999999 means no target; <see cref="Script"/> is the portal's
/// script (the client's <c>script</c> property, empty for most; absent from maps exported before it was kept).
/// </summary>
public sealed record Portal(string Name, int Type, int X, int Y, int TargetMap, string TargetName, string Script = "")
{
    public const int NoTarget = 999999999;
    public bool HasTarget => TargetMap != NoTarget;

    /// <summary>A spawn point (type 0): where a character arrives, not something it enters.</summary>
    public bool IsSpawn => Type == 0;

    /// <summary>
    /// The area around the portal point in which a character can enter the portal, in world units relative to the point
    /// (y grows downward): 25 either side, 100 above and 25 below. The client's data does not hold it (Map.wz's
    /// MapHelper.img has only the portal art and the maps' portal nodes only name, type, point, target and script); this is
    /// the rectangle the Journey client, a reimplementation of the game's client (Gameplay/MapleMap/Portal.cpp, <c>bounds()</c>,
    /// tested by <c>MapPortals::find_warp_at</c> against the player's position, the soles), uses. The map changes of
    /// fx-maps-exact are consistent with it but only bound it, without fixing its edges (README, "Portal activation area").
    /// </summary>
    public const int ActivationLeft = -25, ActivationTop = -100, ActivationRight = 25, ActivationBottom = 25;

    /// <summary>The activation area as x, y, width, height in world units, or null for a spawn point (it has none).</summary>
    public (int X, int Y, int Width, int Height)? Activation =>
        IsSpawn ? null : (X + ActivationLeft, Y + ActivationTop, ActivationRight - ActivationLeft, ActivationBottom - ActivationTop);

    /// <summary>Whether a character whose soles are at (x, y) stands inside the activation area.</summary>
    public bool Activates(double x, double y) =>
        !IsSpawn && x >= X + ActivationLeft && x <= X + ActivationRight && y >= Y + ActivationTop && y <= Y + ActivationBottom;
}

/// <summary>
/// A life spawn point: type "m" (mob) or "n" (NPC), the 7-digit id as in the client's data, the spawn position and the
/// foothold the spawn stands on.
/// </summary>
public sealed record LifeSpawn(string Type, string Id, int X, int Y, int Fh)
{
    public bool IsMob => Type == "m";
}

/// <summary>
/// One back layer of a map (back/&lt;n&gt;, in draw order): its art (Map/Back/&lt;Bs&gt;.img back or ani /&lt;No&gt;), place, parallax
/// factors, tiling distances, type (0 static, 1 to 3 tiled, 4 to 7 tiled and moving), alpha, flip, and whether the client
/// draws it in front of the map (<see cref="Front"/>) rather than behind.
/// </summary>
public sealed record BackLayer(int Index, string Bs, int No, bool Ani, int X, int Y, int Rx, int Ry, int Cx, int Cy, int Type, int A, bool Front, bool Flip);

/// <summary>One map's metadata, as exported by OakSoul.MapExport into data/maps/&lt;id&gt;.json.</summary>
/// <param name="Cloud">info/cloud = 1: the client draws drifting clouds (MapHelper.img weather/cloud) in front of the map's layers and its mobs.</param>
/// <param name="Back">The back layers; empty in an export made before they were exported.</param>
public sealed record MapData(int MapId, string StreetName, string MapName, int ReturnMap, ViewBounds Vr, MiniMapInfo? MiniMap,
    IReadOnlyList<Foothold> Footholds, IReadOnlyList<LadderRope> LadderRopes, IReadOnlyList<Portal> Portals, IReadOnlyList<LifeSpawn>? Life = null,
    bool Cloud = false, IReadOnlyList<BackLayer>? Back = null)
{
    /// <summary>The catalog this map was loaded from (null for a map read on its own), which names the maps its portals lead to.</summary>
    public MapCatalog? Catalog { get; init; }

    /// <summary>The layers drawn in front of the map (back entries with front = 1).</summary>
    public IEnumerable<BackLayer> FrontLayers => (Back ?? []).Where(b => b.Front);

    /// <summary>The mob spawns (life entries of type "m").</summary>
    public IEnumerable<LifeSpawn> MobSpawns => (Life ?? []).Where(l => l.IsMob);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public static MapData Load(string path)
    {
        var dto = JsonSerializer.Deserialize<MapDto>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty map file");
        return new MapData(dto.MapId, dto.StreetName ?? "", dto.MapName ?? "", dto.ReturnMap,
            dto.Vr ?? throw new InvalidDataException($"{path}: no vr"), dto.MiniMap,
            dto.Footholds ?? [], dto.LadderRopes ?? [], dto.Portals ?? [], dto.Life ?? [], dto.Cloud, dto.Back ?? []);
    }

    public override string ToString() => $"{MapId} {StreetName} / {MapName}";

    private sealed record MapDto(int MapId, string? StreetName, string? MapName, int ReturnMap, ViewBounds? Vr, MiniMapInfo? MiniMap,
        List<Foothold>? Footholds, List<LadderRope>? LadderRopes, List<Portal>? Portals, List<LifeSpawn>? Life, bool Cloud = false, List<BackLayer>? Back = null);
}

/// <summary>
/// The exported maps in a directory: loads by id, and resolves the street and map names the HUD shows (which match
/// the client's String data) through index.json. Loaded maps are cached.
/// </summary>
public sealed class MapCatalog
{
    private readonly string _dir;
    private readonly Dictionary<int, (string Street, string Map)> _index = [];
    private readonly Dictionary<int, MapData?> _cache = [];

    public MapCatalog(string dir)
    {
        _dir = dir;
        var path = Path.Combine(dir, "index.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var id)) continue;
            string Text(string name) => entry.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            _index[id] = (Text("streetName"), Text("mapName"));
        }
    }

    public string Directory => _dir;
    public int Count => _index.Count;

    public MapData? Load(int id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var path = Path.Combine(_dir, $"{id}.json");
        var map = File.Exists(path) ? MapData.Load(path) with { Catalog = this } : null;
        _cache[id] = map;
        return map;
    }

    /// <summary>
    /// Map ids whose names match. Names are compared case-insensitively with whitespace collapsed; an empty street
    /// (the HUD showed one line) matches any street. When nothing matches exactly, the closest names within an edit
    /// distance of two are returned, so a single OCR slip still resolves.
    /// </summary>
    public IReadOnlyList<int> FindIds(string? street, string? map)
    {
        if (string.IsNullOrWhiteSpace(map)) return [];
        var s = Normalize(street);
        var m = Normalize(map);
        var exact = _index.Where(e => Normalize(e.Value.Map) == m && (s.Length == 0 || Normalize(e.Value.Street) == s)).Select(e => e.Key).Order().ToList();
        if (exact.Count > 0) return exact;
        var scored = _index.Select(e => (e.Key, Score: Levenshtein(Normalize(e.Value.Map), m) + (s.Length == 0 ? 0 : Levenshtein(Normalize(e.Value.Street), s))))
            .Where(e => e.Score <= 2).ToList();
        if (scored.Count == 0) return [];
        var best = scored.Min(e => e.Score);
        return scored.Where(e => e.Score == best).Select(e => e.Key).Order().ToList();
    }

    /// <summary>
    /// Every map a HUD reading may name, and whether the names matched exactly (the ids are then those of
    /// <see cref="FindIds"/>). A reading that matches nothing exactly is a misread, and the single closest name can be the
    /// wrong map: Ant Tunnel IV's name block read "Ant TunnelM" on exact frames, whose closest name is Ant Tunnel I. So
    /// then every map within an edit distance of two counts (street and map together, as in FindIds), and so does every
    /// map whose name before its last word starts the reading, with at most four characters after it, where the last word
    /// is mangled (all "Ant Tunnel *" for "Ant TunnelM", every "East Rocky Mountain *" for "East Rocky Mountain !"); its
    /// street must be within the same distance. The caller must tell such candidates apart, for example by the minimap.
    /// </summary>
    public (IReadOnlyList<int> Ids, bool Exact) FindCandidates(string? street, string? map)
    {
        if (string.IsNullOrWhiteSpace(map)) return ([], false);
        var s = Normalize(street);
        var m = Normalize(map);
        var exact = _index.Where(e => Normalize(e.Value.Map) == m && (s.Length == 0 || Normalize(e.Value.Street) == s)).Select(e => e.Key).Order().ToList();
        if (exact.Count > 0) return (exact, true);
        var near = new List<int>();
        foreach (var (id, names) in _index)
        {
            var streetEdits = s.Length == 0 ? 0 : Levenshtein(Normalize(names.Street), s);
            if (streetEdits > MaxNameEdits) continue;
            var name = Normalize(names.Map);
            var lastWord = name.LastIndexOf(' ');
            var stem = lastWord > 0 ? name[..lastWord] : "";
            if (Levenshtein(name, m) + streetEdits <= MaxNameEdits
                || stem.Length >= 4 && m.StartsWith(stem, StringComparison.Ordinal) && m.Length - stem.Length <= 4)
                near.Add(id);
        }
        return (near.Order().ToList(), false);
    }

    /// <summary>Edits allowed between a misread name and a catalog name, street and map together.</summary>
    public const int MaxNameEdits = 2;

    public MapData? Find(string? street, string? map) => FindIds(street, map) is [var id] ? Load(id) : null;

    public (string Street, string Map)? Names(int id) => _index.TryGetValue(id, out var n) ? n : null;

    private HashSet<string>? _sharedNames;

    /// <summary>
    /// The name to show for a map, null when the catalog does not know the id: its map name (Deep Valley II), with the
    /// street before it ("Hidden Street / Mushroom Garden") only where the street adds something, that is where another
    /// street has a map of the same name or the map has no name of its own.
    /// </summary>
    public string? DisplayName(int id)
    {
        if (!_index.TryGetValue(id, out var n)) return null;
        var (street, map) = (n.Street.Trim(), n.Map.Trim());
        if (map.Length == 0) return street.Length == 0 ? null : street;
        if (street.Length == 0 || Normalize(street) == Normalize(map)) return map;
        _sharedNames ??= SharedMapNames();
        return _sharedNames.Contains(Normalize(map)) ? $"{street} / {map}" : map;
    }

    /// <summary>Map names that more than one street uses (Mushroom Garden on Hidden Street and on Rainbow Street).</summary>
    private HashSet<string> SharedMapNames() =>
        _index.Values.Where(v => v.Map.Trim().Length > 0).GroupBy(v => Normalize(v.Map), v => Normalize(v.Street))
            .Where(g => g.Distinct().Count() > 1).Select(g => g.Key).ToHashSet();

    internal static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
