using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using OakSoul.Wz;

namespace OakSoul.MapExport;

/// <summary>
/// Exports map metadata (bounds, minimap, footholds, ladders, portals, life) from the client's WZ archives to JSON.
/// The archives are only ever opened read-only.
/// </summary>
internal static class Program
{
    private const string DefaultClient =
        "/Users/lynxstar/Library/Application Support/MapleStoryNA/Bottles/maplestory/drive_c/Nexon/Library/maplestorycw/appdata/Data";

    private const string Usage = """
        Usage: OakSoul.MapExport [options]
          --client DIR   client Data folder (default: the MapleStoryNA bottle's appdata/Data)
          --out DIR      output folder (default: data/maps under the repository root)
          --map ID       export only this map (index.json is left untouched)
          --list         print id, street name and map name of every map found, then exit
          --dump ID      print the raw property tree of one map image, then exit
          --info         print each archive's header, detected version and top-level entries, then exit
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep apostrophes and non-ASCII names readable
    };

    private static int Main(string[] args)
    {
        string client = DefaultClient;
        string? outDir = null;
        int? onlyMap = null, dumpMap = null;
        bool list = false, info = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--client": client = Next(); break;
                    case "--out": outDir = Next(); break;
                    case "--map": onlyMap = ParseId(Next()); break;
                    case "--dump": dumpMap = ParseId(Next()); break;
                    case "--list": list = true; break;
                    case "--info": info = true; break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 0;
                    default:
                        Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        client = Path.GetFullPath(client);
        if (!Directory.Exists(client))
        {
            Console.Error.WriteLine($"Client data folder not found: {client}");
            return 1;
        }

        var total = Stopwatch.StartNew();
        var mapArchives = ClientArchives.MapArchives(client);
        var stringArchives = ClientArchives.StringArchives(client);
        if (mapArchives.Count == 0 || stringArchives.Count == 0)
        {
            Console.Error.WriteLine($"No Map/Map or String archives under {client}");
            return 1;
        }

        if (info)
        {
            foreach (var path in stringArchives.Concat(mapArchives))
                PrintInfo(client, path);
            return 0;
        }

        var opened = new List<WzFile>();
        try
        {
            foreach (var path in mapArchives)
            {
                var file = WzFile.Open(path);
                opened.Add(file);
                Console.Error.WriteLine($"{Path.GetRelativePath(client, path)}: version {file.Version}{(file.VersionVerified ? "" : " (unverified)")}");
            }

            var names = MapNames.Load(stringArchives);
            Console.Error.WriteLine($"Map names loaded from String: {names.Count}");

            // Map images by id; an id present in two archives keeps the first occurrence.
            var maps = new SortedDictionary<int, WzImage>();
            int otherImages = 0;
            foreach (var file in opened)
                foreach (var image in file.Root.EnumerateImages())
                {
                    if (MapExtractor.MapIdFromImageName(image.Name) is not { } id)
                    {
                        otherImages++;
                        continue;
                    }
                    if (!maps.TryAdd(id, image))
                        Console.Error.WriteLine($"warning: map {id} appears again in {image.File.FilePath} ({image.Path}); keeping the first");
                }
            Console.Error.WriteLine($"Map images found: {maps.Count} (plus {otherImages} other images)");

            if (list)
            {
                foreach (var (id, _) in maps)
                {
                    var n = names.Get(id);
                    Console.WriteLine($"{id}\t{n?.StreetName ?? "?"}\t{n?.MapName ?? "?"}");
                }
                return 0;
            }

            if (dumpMap is { } dumpId)
            {
                if (!maps.TryGetValue(dumpId, out var img))
                {
                    Console.Error.WriteLine($"Map {dumpId} not found.");
                    return 1;
                }
                Console.WriteLine($"# {img.File.FilePath} : {img.Path}");
                WzDump.Dump(img.Root, Console.Out);
                return 0;
            }

            outDir = Path.GetFullPath(outDir ?? Path.Combine(RepoRoot(), "data", "maps"));
            if (IsUnder(outDir, client))
            {
                Console.Error.WriteLine("Refusing to write inside the client folder.");
                return 1;
            }
            Directory.CreateDirectory(outDir);

            return Export(maps, names, outDir, onlyMap, total);
        }
        finally
        {
            foreach (var f in opened)
                f.Dispose();
        }
    }

    private static int Export(SortedDictionary<int, WzImage> maps, MapNames names, string outDir, int? onlyMap, Stopwatch total)
    {
        IEnumerable<KeyValuePair<int, WzImage>> selected = maps;
        if (onlyMap is { } only)
        {
            if (!maps.TryGetValue(only, out var img))
            {
                Console.Error.WriteLine($"Map {only} not found in the map archives.");
                return 1;
            }
            selected = [new(only, img)];
        }

        var index = new SortedDictionary<int, IndexEntry>();
        var noInfo = new List<int>();
        var failed = new List<(int Id, string Error)>();
        var unknownTypes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (id, image) in selected)
        {
            var n = names.Get(id);
            try
            {
                var data = MapExtractor.Extract(id, image, n);
                unknownTypes.UnionWith(image.UnknownTypes);
                if (data is null)
                {
                    noInfo.Add(id);
                    continue;
                }
                string fileName = id.ToString(CultureInfo.InvariantCulture) + ".json";
                File.WriteAllText(Path.Combine(outDir, fileName), JsonSerializer.Serialize(data, Json));
                index[id] = new IndexEntry(n?.StreetName, n?.MapName, fileName);
            }
            catch (WzFormatException ex)
            {
                failed.Add((id, ex.Message));
            }
            finally
            {
                image.Unload(); // keep memory flat across hundreds of maps
            }
        }

        if (onlyMap is null)
            File.WriteAllText(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(index, Json));

        Console.Error.WriteLine($"Exported {index.Count} map(s) to {outDir}");
        Console.Error.WriteLine($"Skipped {noInfo.Count} map image(s) without an info node{(noInfo.Count > 0 ? ": " + string.Join(", ", noInfo.Take(20)) + (noInfo.Count > 20 ? ", ..." : "") : "")}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"Failed to parse {failed.Count} map image(s):");
            foreach (var (id, error) in failed)
                Console.Error.WriteLine($"  {id}: {error}");
        }
        if (unknownTypes.Count > 0)
            Console.Error.WriteLine($"Unparsed extended property types: {string.Join(", ", unknownTypes)}");
        Console.Error.WriteLine($"Elapsed: {total.Elapsed.TotalSeconds:F2} s");
        return failed.Count > 0 ? 1 : 0;
    }

    private static void PrintInfo(string client, string path)
    {
        using var file = WzFile.Open(path);
        Console.WriteLine($"== {Path.GetRelativePath(client, path)}");
        Console.WriteLine($"   length {file.Length}, data size {file.DataSize}, fstart 0x{file.FileStart:X}, \"{file.Copyright}\"");
        Console.WriteLine($"   stored version check 0x{file.EncryptedVersion:X4}, candidates [{string.Join(", ", WzFile.VersionCandidates(file.EncryptedVersion))}]");
        Console.WriteLine($"   detected version {file.Version} (hash 0x{file.VersionHash:X8}){(file.VersionVerified ? "" : " UNVERIFIED")}");
        WzDump.Dump(file.Root, Console.Out, maxDepth: 2, maxEntries: 8);
    }

    private static int ParseId(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw new ArgumentException($"'{s}' is not a map id");

    /// <summary>The nearest ancestor of the working directory containing OakSoul.slnx, else the working directory.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OakSoul.slnx")))
                return dir.FullName;
        return Environment.CurrentDirectory;
    }

    private static bool IsUnder(string path, string root)
    {
        string rel = Path.GetRelativePath(root, path);
        return rel == "." || !(rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel));
    }
}
