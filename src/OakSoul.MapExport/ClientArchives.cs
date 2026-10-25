using System.Text.RegularExpressions;

namespace OakSoul.MapExport;

/// <summary>
/// Locates archives in the client's Data folder. Each category folder holds a tiny index stub
/// (<c>Map0.wz</c>) plus the real self-contained archives <c>Map0_000.wz</c>, <c>Map0_001.wz</c>, ...
/// Only the numbered parts are returned. Nothing here opens files for writing.
/// </summary>
internal static partial class ClientArchives
{
    /// <summary>Numbered parts of the archive family named after <paramref name="dir"/>'s folder, e.g. Map0/Map0_000.wz.</summary>
    public static IEnumerable<string> Parts(string dir)
    {
        if (!Directory.Exists(dir))
            yield break;
        string stem = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        var pattern = new Regex("^" + Regex.Escape(stem) + @"_\d{3}\.wz$", RegexOptions.IgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.wz").Order(StringComparer.Ordinal))
            if (pattern.IsMatch(Path.GetFileName(file)))
                yield return file;
    }

    /// <summary>Map definition archives: <c>Map/Map/Map_NNN.wz</c> and every <c>Map/Map/MapN/MapN_NNN.wz</c>.</summary>
    public static IReadOnlyList<string> MapArchives(string client)
    {
        string mapRoot = Path.Combine(client, "Map", "Map");
        var list = Parts(mapRoot).ToList();
        if (Directory.Exists(mapRoot))
        {
            foreach (var sub in Directory.EnumerateDirectories(mapRoot).Order(StringComparer.Ordinal))
                if (MapFolder().IsMatch(Path.GetFileName(sub)))
                    list.AddRange(Parts(sub));
        }
        return list;
    }

    /// <summary>String archives (map names live in Map.img inside one of them).</summary>
    public static IReadOnlyList<string> StringArchives(string client) => Parts(Path.Combine(client, "String")).ToList();

    [GeneratedRegex(@"^Map\d+$")]
    private static partial Regex MapFolder();
}
