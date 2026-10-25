using System.Globalization;
using OakSoul.Wz;

namespace OakSoul.MobInfo;

/// <summary>Mob ids and names from the client's <c>String/String_NNN.wz</c> archive, image <c>Mob.img</c>, children <c>&lt;id&gt;/name</c>. Read-only.</summary>
internal static class ClientMobNames
{
    public static SortedDictionary<int, string?> Load(string clientDataDir)
    {
        var result = new SortedDictionary<int, string?>();
        string dir = Path.Combine(clientDataDir, "String");
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"String folder not found: {dir}");

        foreach (var path in Directory.EnumerateFiles(dir, "String_*.wz").Order(StringComparer.Ordinal))
        {
            using var file = WzFile.Open(path);
            var img = file.Root.EnumerateImages().FirstOrDefault(i => i.Name == "Mob.img");
            if (img is null)
                continue;
            foreach (var mob in img.Root.Children)
                if (int.TryParse(mob.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                    result.TryAdd(id, mob.GetString("name"));
        }
        return result;
    }
}
