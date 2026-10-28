using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using OakSoul.Wz;

namespace OakSoul.SpriteExport;

/// <summary>
/// Exports canvas bitmaps from the client's WZ archives to PNG, plus a manifest and two contact sheets for
/// checking the decoding by eye. The archives are only ever opened read-only and nothing is written next to them.
/// </summary>
internal static partial class Program
{
    private const string DefaultClient =
        "/Users/lynxstar/Library/Application Support/MapleStoryNA/Bottles/maplestory/drive_c/Nexon/Library/maplestorycw/appdata/Data";

    private const string Usage = """
        Usage: OakSoul.SpriteExport [selectors] [options]
          --mobs         mob animation frames      -> mob/<id>/<animation>_<frame>.png
          --items        item icons (Special, Consume, Etc, Install, Cash, Pet)
                                                   -> item/<category>/<id>/iconRaw_<n>.png, icon.png
          --skills       skill icons               -> skill/<skillId>/icon.png
          --maps         minimap canvases          -> minimap/<mapId>.png
          --effects      damage number digits (Effect BasicEff.img No*: NoRed0, NoViolet1, ...)
                                                   -> effect/BasicEff/<set>/<digit>.png, effect/damage-digits.json
          --pets         every pet's animation frames -> pet/<id>/<anim>_<n>.png, pet/frames.json
          --cursor       the mouse cursor (UI Basic.img/Cursor) -> ui/cursor/<state>_<n>.png, ui/cursor/frames.json
          --mesos        the meso coins' spinning frames with their origins -> item/meso/<id>/iconRaw_<n>.png, item/meso/frames.json
          --all          all of the above (the default when no selector is given)
          --skill-effects JOBS  the effect frames of every skill of these jobs (comma-separated, e.g. 200,220)
                                                   -> skill-effect/<skillId>/<node>_<n>.png, skill-effect/frames.json
          --verify       decode every canvas in every _Canvas archive without writing anything, and report
                         counts per pixel format and any failures
          --client DIR   client Data folder (default: the MapleStoryNA bottle's appdata/Data)
          --out DIR      output folder (default: data/sprites under the repository root)
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] ItemCategories = ["Special", "Consume", "Etc", "Install", "Cash", "Pet"];

    private static int Main(string[] args)
    {
        string client = DefaultClient;
        string? outDir = null;
        bool mobs = false, items = false, skills = false, maps = false, effects = false, verify = false, pets = false, cursor = false, mesos = false;
        string[] skillJobs = [];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--client": client = Next(); break;
                    case "--out": outDir = Next(); break;
                    case "--mobs": mobs = true; break;
                    case "--items": items = true; break;
                    case "--skills": skills = true; break;
                    case "--maps": maps = true; break;
                    case "--effects": effects = true; break;
                    case "--pets": pets = true; break;
                    case "--cursor": cursor = true; break;
                    case "--mesos": mesos = true; break;
                    case "--skill-effects": skillJobs = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--all": mobs = items = skills = maps = effects = pets = cursor = mesos = true; break;
                    case "--verify": verify = true; break;
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

        if (verify)
            return Verify(client);

        if (!(mobs || items || skills || maps || effects || pets || cursor || mesos) && skillJobs.Length == 0)
            mobs = items = skills = maps = effects = pets = cursor = mesos = true;

        outDir = Path.GetFullPath(outDir ?? Path.Combine(RepoRoot(), "data", "sprites"));
        if (IsUnder(outDir, client))
        {
            Console.Error.WriteLine($"Refusing to write inside the client folder: {outDir}");
            return 1;
        }

        var total = Stopwatch.StartNew();
        var run = new ExportRun(client, outDir);
        if (mobs)
            run.ExportMobs();
        if (items)
            run.ExportItems();
        if (skills)
            run.ExportSkills();
        if (maps)
            run.ExportMinimaps();
        if (effects)
            run.ExportEffects();
        if (pets)
            run.ExportPets();
        if (cursor)
            run.ExportCursor();
        if (mesos)
            run.ExportMesos();
        if (skillJobs.Length > 0)
            run.ExportSkillEffects(skillJobs);

        run.WriteContactSheets();
        run.WriteManifest();
        total.Stop();
        run.Report(Console.Out, total.Elapsed);
        return run.Failures.IsEmpty ? 0 : 3;
    }

    /// <summary>Decodes every canvas of every <c>_Canvas</c> archive under the client folder; writes nothing.</summary>
    private static int Verify(string client)
    {
        var sw = Stopwatch.StartNew();
        var stats = new Stats();
        var archives = Directory.EnumerateDirectories(client, "_Canvas", SearchOption.AllDirectories)
            .SelectMany(ArchiveParts).Order(StringComparer.Ordinal).ToList();
        foreach (var path in archives)
        {
            var archiveSw = Stopwatch.StartNew();
            using var file = WzFile.Open(path);
            string rel = Path.GetRelativePath(client, path);
            int before = stats.Decoded;
            Parallel.ForEach(file.Root.EnumerateImages(), img =>
            {
                try
                {
                    foreach (var node in Canvases(img.Root))
                    {
                        try
                        {
                            node.Canvas.Decode();
                            stats.Count(rel, node.Canvas);
                        }
                        catch (WzFormatException ex)
                        {
                            stats.Fail(rel, node.Canvas, ex.Message);
                        }
                    }
                }
                catch (WzFormatException ex)
                {
                    stats.Failures.Add(new Failure(rel, img.Path, "image parse: " + ex.Message));
                }
                finally
                {
                    img.Unload();
                }
            });
            Console.WriteLine($"{rel}: {stats.Decoded - before} canvases in {archiveSw.Elapsed.TotalSeconds:F1}s");
        }
        sw.Stop();
        Console.WriteLine();
        stats.Print(Console.Out);
        Console.WriteLine($"Total time: {sw.Elapsed.TotalSeconds:F1}s");
        return stats.Failures.IsEmpty ? 0 : 3;
    }

    /// <summary>All canvas nodes in a tree, depth first, in stored order.</summary>
    internal static IEnumerable<WzNode> Canvases(WzNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == WzNodeType.Canvas)
                yield return child;
            foreach (var c in Canvases(child))
                yield return c;
        }
    }

    /// <summary>Numbered parts of the archive family in <paramref name="dir"/>: _Canvas/_Canvas_000.wz, ... (the stub is skipped).</summary>
    internal static IEnumerable<string> ArchiveParts(string dir)
    {
        if (!Directory.Exists(dir))
            yield break;
        string stem = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        var pattern = new Regex("^" + Regex.Escape(stem) + @"_\d{3}\.wz$", RegexOptions.IgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.wz").Order(StringComparer.Ordinal))
            if (pattern.IsMatch(Path.GetFileName(file)))
                yield return file;
    }

    internal static string RepoRoot()
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

    internal static string Stem(string imageName) =>
        imageName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

    internal static string Flatten(string nodePath) => nodePath.Replace('/', '_');

    internal static JsonSerializerOptions JsonOptions => Json;

    internal static IReadOnlyList<string> Items => ItemCategories;

    internal static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
