using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OakSoul.Wz;

namespace OakSoul.MobExport;

/// <summary>
/// Exports mob property trees from the client's ".ms" Mob pack (Data/Packs/Mob_00000.ms) to JSON. The pack and the
/// String archives are only ever opened read-only; output goes under data/mobinfo.
/// </summary>
internal static class Program
{
    private const string DefaultClient =
        "/Users/lynxstar/Library/Application Support/MapleStoryNA/Bottles/maplestory/drive_c/Nexon/Library/maplestorycw/appdata/Data";

    private const string Usage = """
        Usage: OakSoul.MobExport [options]
          --client DIR   client appdata folder or its Data folder (default: the MapleStoryNA bottle's appdata/Data)
          --out DIR      output folder (default: data/mobinfo under the repository root)
          --pack NAME    pack file in Data/Packs (default: Mob_00000.ms)
          --sprites      also write animation frames as PNG under <out>/sprites/<id>/<anim>_<frame>.png
          --list         print the pack's entry names, sizes and offsets, then exit
          --dump ID      print the raw property tree of one mob (id) or entry (full entry name), then exit
          --info         print the pack header, check every entry and parse every image, then exit
          --check        cross-check level/maxHP/exp and mobility against data/mobs.json, then exit
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        string client = DefaultClient;
        string? outDir = null, dump = null;
        string packName = "Mob_00000.ms";
        bool list = false, info = false, check = false, sprites = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--client": client = Next(); break;
                    case "--out": outDir = Next(); break;
                    case "--pack": packName = Next(); break;
                    case "--dump": dump = Next(); break;
                    case "--list": list = true; break;
                    case "--info": info = true; break;
                    case "--check": check = true; break;
                    case "--sprites": sprites = true; break;
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
        if (Directory.Exists(Path.Combine(client, "Data", "Packs")))
            client = Path.Combine(client, "Data");
        string packPath = Path.Combine(client, "Packs", packName);
        if (!File.Exists(packPath))
        {
            Console.Error.WriteLine($"Pack not found: {packPath}");
            return 1;
        }

        var total = Stopwatch.StartNew();
        using var pack = MsFile.Open(packPath);

        if (list)
        {
            foreach (var e in pack.Entries)
                Console.WriteLine($"{e.Name}\t{e.Size}\t0x{e.Offset:X}{(e.CheckSumValid ? "" : "\tBAD CHECKSUM")}");
            return 0;
        }
        if (info)
            return PrintInfo(pack, total);
        if (dump is not null)
        {
            var entry = dump.Contains('/') ? pack.FindEntry(dump) : pack.FindEntry($"Mob/{ParseId(dump):D7}.img");
            if (entry is null)
            {
                Console.Error.WriteLine($"Entry for '{dump}' not found in {pack.FilePath}.");
                return 1;
            }
            Console.WriteLine($"# {pack.FilePath} : {entry}");
            if (!entry.IsImage)
            {
                Console.WriteLine(Encoding.UTF8.GetString(pack.ReadEntry(entry)));
                return 0;
            }
            WzDump.Dump(pack.OpenImage(entry).Root, Console.Out);
            return 0;
        }

        var mobs = MobImages(pack);
        if (check)
            return Check(mobs, Path.Combine(RepoRoot(), "data", "mobs.json"));

        outDir = Path.GetFullPath(outDir ?? Path.Combine(RepoRoot(), "data", "mobinfo"));
        if (IsUnder(outDir, client))
        {
            Console.Error.WriteLine("Refusing to write inside the client folder.");
            return 1;
        }
        Directory.CreateDirectory(outDir);
        var names = LoadNames(client);
        Console.Error.WriteLine($"Mob names loaded from String: {names.Count}");
        return Export(mobs, names, outDir, sprites, total);
    }

    // ---------------------------------------------------------------- export

    private static int Export(SortedDictionary<int, WzImage> mobs, Dictionary<int, string?> names, string outDir, bool sprites, Stopwatch total)
    {
        var index = new JsonArray();
        var failed = new List<(int Id, string Error)>();
        var unknownTypes = new SortedSet<string>(StringComparer.Ordinal);
        int pngs = 0, pngFailures = 0;

        foreach (var (id, image) in mobs)
        {
            try
            {
                names.TryGetValue(id, out string? name);
                var source = AnimationSource(mobs, image);
                var obj = MobJson.Build(id, name, image, source);
                unknownTypes.UnionWith(image.UnknownTypes);
                File.WriteAllText(Path.Combine(outDir, $"{id:D7}.json"), obj.ToJsonString(Json));

                var root = image.Root;
                index.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["level"] = root["info/level"]?.ToInt(),
                    ["maxHP"] = root["info/maxHP"]?.ToLong(),
                    ["speed"] = root["info/speed"]?.ToInt(),
                    ["flySpeed"] = root["info/flySpeed"]?.ToInt(),
                    ["mobility"] = MobJson.Mobility(root, source.Root),
                    ["link"] = source == image ? null : root["info/link"]?.ToStringValue(),
                    ["animations"] = new JsonArray(MobJson.AnimationNodes(source.Root).Select(c => (JsonNode?)c.Name).ToArray()),
                });

                if (sprites && source == image)
                {
                    var (ok, bad) = WriteSprites(id, root, Path.Combine(outDir, "sprites", id.ToString("D7", CultureInfo.InvariantCulture)));
                    pngs += ok;
                    pngFailures += bad;
                }
            }
            catch (WzFormatException ex)
            {
                failed.Add((id, ex.Message));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "index.json"), index.ToJsonString(Json));
        Console.Error.WriteLine($"Exported {index.Count} mob(s) to {outDir}");
        if (sprites)
            Console.Error.WriteLine($"Wrote {pngs} frame PNG(s){(pngFailures > 0 ? $", {pngFailures} frame(s) could not be decoded" : "")}");
        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"Failed to parse {failed.Count} mob image(s):");
            foreach (var (id, error) in failed)
                Console.Error.WriteLine($"  {id}: {error}");
        }
        if (unknownTypes.Count > 0)
            Console.Error.WriteLine($"Unparsed extended property types: {string.Join(", ", unknownTypes)}");
        Console.Error.WriteLine($"Elapsed: {total.Elapsed.TotalSeconds:F2} s");
        return failed.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// The image holding a mob's animations: its own, or (when it has none and info/link names another mob in the
    /// pack) the linked mob's, following up to a few hops.
    /// </summary>
    private static WzImage AnimationSource(SortedDictionary<int, WzImage> mobs, WzImage image)
    {
        var current = image;
        for (int hop = 0; hop < 4; hop++)
        {
            var root = current.Root;
            if (MobJson.AnimationNodes(root).Any())
                return current;
            if (root["info/link"]?.ToStringValue() is not { } link
                || !int.TryParse(link, NumberStyles.None, CultureInfo.InvariantCulture, out int target)
                || !mobs.TryGetValue(target, out var next))
                return current;
            current = next;
        }
        return current;
    }

    /// <summary>Writes every frame of every animation (links resolved) as &lt;anim&gt;_&lt;frame&gt;.png.</summary>
    private static (int Ok, int Failed) WriteSprites(int id, WzNode root, string dir)
    {
        int ok = 0, failed = 0;
        foreach (var anim in root.Children)
        {
            if (anim.Name == "info")
                continue;
            foreach (var frame in anim.Children)
            {
                if (!int.TryParse(frame.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    continue;
                if (frame.ResolveLink() is not { Type: WzNodeType.Canvas } canvas)
                    continue;
                try
                {
                    var c = canvas.Canvas;
                    if (c.Width == 0 || c.Height == 0)
                        continue;
                    byte[] bgra = c.Decode();
                    Directory.CreateDirectory(dir);
                    PngWriter.Save(Path.Combine(dir, $"{anim.Name}_{frame.Name}.png"), c.Width, c.Height, bgra);
                    ok++;
                }
                catch (WzFormatException ex)
                {
                    failed++;
                    Console.Error.WriteLine($"  mob {id} {anim.Name}/{frame.Name}: {ex.Message}");
                }
            }
        }
        return (ok, failed);
    }

    // ---------------------------------------------------------------- --info

    private static int PrintInfo(MsFile pack, Stopwatch total)
    {
        Console.WriteLine($"== {pack.FilePath}");
        Console.WriteLine($"   length {pack.Length}, random bytes {pack.RandomByteCount}, version {pack.Version}, salt \"{pack.Salt}\"");
        Console.WriteLine($"   headerStart {pack.HeaderStart}, hash {pack.Hash}, entryCount {pack.EntryCount}, entryStart {pack.EntryStart}");
        Console.WriteLine($"   entry table end {pack.EntryTableEnd}, dataStart {pack.DataStart}");
        Console.WriteLine($"   checksums valid: {pack.Entries.Count(e => e.CheckSumValid)} of {pack.Entries.Count}");
        var last = pack.Entries.MaxBy(e => e.Offset);
        if (last is not null)
            Console.WriteLine($"   last entry ends at {last.Offset + last.SizeAligned} (file length {pack.Length})");

        int parsed = 0, nodes = 0;
        var failures = new List<string>();
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in pack.Entries)
        {
            if (!e.IsImage)
            {
                string text = Encoding.UTF8.GetString(pack.ReadEntry(e));
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
                Console.WriteLine($"   non-image entry {e.Name} ({e.Size} bytes, {lines.Count} lines): {string.Join(", ", lines.Take(8))}{(lines.Count > 8 ? ", ..." : "")}");
                string dir = e.Name.Contains('/') ? e.Name[..e.Name.LastIndexOf('/')] + "/" : "";
                int present = lines.Count(l => pack.FindEntry(dir + l.Trim()) is not null);
                Console.WriteLine($"     names listed that are also entries of this pack: {present} of {lines.Count}");
                continue;
            }
            var image = pack.OpenImage(e);
            try
            {
                nodes += CountNodes(image.Root);
                unknown.UnionWith(image.UnknownTypes);
                parsed++;
            }
            catch (WzFormatException ex)
            {
                failures.Add($"{e.Name}: {ex.Message}");
            }
            finally
            {
                image.Unload();
            }
        }
        Console.WriteLine($"   images parsed: {parsed} of {pack.Entries.Count(e => e.IsImage)} ({nodes} nodes), failures {failures.Count}");
        foreach (var f in failures)
            Console.WriteLine($"     {f}");
        if (unknown.Count > 0)
            Console.WriteLine($"   unknown extended types: {string.Join(", ", unknown)}");
        Console.WriteLine($"   elapsed {total.Elapsed.TotalSeconds:F2} s");
        return failures.Count > 0 ? 1 : 0;
    }

    private static int CountNodes(WzNode n) => 1 + n.Children.Sum(CountNodes);

    // ---------------------------------------------------------------- --check

    private static int Check(SortedDictionary<int, WzImage> mobs, string mobsJson)
    {
        if (!File.Exists(mobsJson))
        {
            Console.Error.WriteLine($"Not found: {mobsJson}");
            return 1;
        }
        var site = JsonNode.Parse(File.ReadAllText(mobsJson))?["mobs"]?.AsObject()
            ?? throw new InvalidDataException($"{mobsJson} has no 'mobs' object");

        int both = 0, statMismatches = 0, mobilityMatches = 0;
        var onlyPack = new List<int>();
        var mobilityTable = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, image) in mobs)
        {
            if (site[id.ToString("D7", CultureInfo.InvariantCulture)] is not JsonObject s)
            {
                onlyPack.Add(id);
                continue;
            }
            both++;
            var root = image.Root;
            var diffs = new List<string>();
            Compare("level", root["info/level"]?.ToLong(), s["level"]);
            Compare("maxHP/hp", root["info/maxHP"]?.ToLong(), s["hp"]);
            Compare("exp", root["info/exp"]?.ToLong(), s["exp"]);
            if (diffs.Count > 0)
            {
                statMismatches++;
                Console.WriteLine($"stat mismatch {id} {s["name"]}: {string.Join("; ", diffs)}");
            }

            var source = AnimationSource(mobs, image);
            string packMobility = MobJson.Mobility(root, source.Root);
            string siteMobility = s["mobility"]?.GetValue<string>() ?? "?";
            mobilityTable[$"pack {packMobility,-6} / site {siteMobility}"] = mobilityTable.GetValueOrDefault($"pack {packMobility,-6} / site {siteMobility}") + 1;
            if (packMobility == siteMobility)
                mobilityMatches++;
            else
                Console.WriteLine($"mobility mismatch {id} {s["name"]}: pack {packMobility} (anims {string.Join(",", MobJson.AnimationNodes(source.Root).Select(c => c.Name))}, flySpeed {root["info/flySpeed"]?.ToStringValue() ?? "-"}), site {siteMobility}");

            void Compare(string field, long? pack, JsonNode? siteValue)
            {
                long? sv = siteValue is JsonValue v && v.TryGetValue(out long l) ? l : null;
                if (pack != sv)
                    diffs.Add($"{field} pack {pack?.ToString(CultureInfo.InvariantCulture) ?? "-"} site {sv?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            }
        }
        var onlySite = site.Select(kv => kv.Key).Where(k => !mobs.ContainsKey(int.Parse(k, CultureInfo.InvariantCulture))).ToList();
        Console.WriteLine($"mobs in pack {mobs.Count}, in mobs.json {site.Count}, in both {both}");
        Console.WriteLine($"level/maxHP/exp mismatches: {statMismatches} of {both}");
        Console.WriteLine($"mobility agreement: {mobilityMatches} of {both}");
        foreach (var (k, v) in mobilityTable)
            Console.WriteLine($"  {k}: {v}");
        Console.WriteLine($"only in pack: {string.Join(", ", onlyPack)}");
        Console.WriteLine($"only in mobs.json: {string.Join(", ", onlySite)}");
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Mob images of the pack keyed by id ("Mob/0000020.img" -> 20).</summary>
    private static SortedDictionary<int, WzImage> MobImages(MsFile pack)
    {
        var result = new SortedDictionary<int, WzImage>();
        foreach (var e in pack.Entries)
        {
            if (!e.IsImage)
                continue;
            string stem = Path.GetFileNameWithoutExtension(e.Name);
            if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && !result.TryAdd(id, pack.OpenImage(e)))
                Console.Error.WriteLine($"warning: mob {id} appears twice in the pack; keeping the first");
        }
        return result;
    }

    /// <summary>Mob names from String/String_NNN.wz, image Mob.img, children &lt;id&gt;/name (as OakSoul.MobInfo does).</summary>
    private static Dictionary<int, string?> LoadNames(string client)
    {
        var result = new Dictionary<int, string?>();
        string dir = Path.Combine(client, "String");
        if (!Directory.Exists(dir))
            return result;
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

    private static int ParseId(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw new ArgumentException($"'{s}' is not a mob id");

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
