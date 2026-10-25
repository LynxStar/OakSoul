using System.Globalization;
using OakSoul.Wz;

namespace OakSoul.MapExport;

/// <summary>Map names from String's <c>Map.img/&lt;area&gt;/&lt;mapId&gt;</c> (mapName, streetName).</summary>
internal sealed class MapNames
{
    public sealed record Entry(string? StreetName, string? MapName, string Area);

    private readonly Dictionary<int, Entry> _byId = [];

    public int Count => _byId.Count;

    public Entry? Get(int mapId) => _byId.GetValueOrDefault(mapId);

    public static MapNames Load(IEnumerable<string> stringArchives)
    {
        var names = new MapNames();
        foreach (var path in stringArchives)
        {
            using var file = WzFile.Open(path);
            var mapImg = file.Root.EnumerateImages().FirstOrDefault(i => i.Name == "Map.img");
            if (mapImg is null)
                continue;
            foreach (var area in mapImg.Root.Children)
                foreach (var map in area.Children)
                {
                    if (!int.TryParse(map.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        continue;
                    names._byId.TryAdd(id, new Entry(map.GetString("streetName"), map.GetString("mapName"), area.Name));
                }
        }
        return names;
    }
}
