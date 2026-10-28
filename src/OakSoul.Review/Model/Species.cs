using System.Text.Json;
using OakSoul.World;

namespace OakSoul.Review.Model;

/// <summary>
/// The species names a map can show, for the note editor's autocomplete: the map's mob spawns (data/maps, the client's
/// life list) named through data/mobs.json. Read once per map.
/// </summary>
public sealed class Species
{
    private readonly MapCatalog? _maps;
    private readonly Dictionary<string, string> _names = [];
    private readonly Dictionary<int, IReadOnlyList<string>> _byMap = [];

    public Species(string root)
    {
        var maps = Path.Combine(root, "data", "maps");
        try
        {
            if (File.Exists(Path.Combine(maps, "index.json"))) _maps = new MapCatalog(maps);
            var mobs = Path.Combine(root, "data", "mobs.json");
            if (File.Exists(mobs))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mobs));
                if (doc.RootElement.TryGetProperty("mobs", out var all))
                    foreach (var m in all.EnumerateObject())
                        if (m.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) _names[m.Name] = n.GetString()!;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    public IReadOnlyList<string> ForMap(int? mapId)
    {
        if (mapId is not { } id || _maps is null) return [];
        if (_byMap.TryGetValue(id, out var cached)) return cached;
        IReadOnlyList<string> list;
        try
        {
            list = _maps.Load(id)?.MobSpawns.Select(s => _names.GetValueOrDefault(s.Id)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { list = []; }
        _byMap[id] = list;
        return list;
    }
}
