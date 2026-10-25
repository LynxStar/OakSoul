using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakSoul.MobInfo;

/// <summary>
/// Builds a local mob stats table (<c>data/mobs.json</c>) from the public MapleStory Classic database at meowdb.com,
/// because the client keeps mob stats in an encrypted pack. Mob ids and names come from the client's String archive
/// (opened read-only). Fetched pages are cached under <c>data/cache/meowdb/monsters/</c>.
/// </summary>
internal static class Program
{
    private const string DefaultClient =
        "/Users/lynxstar/Library/Application Support/MapleStoryNA/Bottles/maplestory/drive_c/Nexon/Library/maplestorycw/appdata/Data";

    private const string Usage = """
        Usage: OakSoul.MobInfo [options]
          --out FILE     output file (default: data/mobs.json under the repository root)
          --client DIR   client appdata Data folder (default: the MapleStoryNA bottle's appdata/Data)
          --ids 30,35    limit to these mob ids (an existing output file is merged, other entries kept)
          --refresh      refetch pages that are already cached (still at most once per id per run)
        Pages are cached in data/cache/meowdb/monsters/{id}.html; fetching is sequential with a 500 ms delay.
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> Main(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string? outPath = null;
        string client = DefaultClient;
        HashSet<int>? only = null;
        bool refresh = false;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--out": outPath = Path.GetFullPath(Next()); break;
                    case "--client": client = Next(); break;
                    case "--ids":
                        only = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0
                                ? v : throw new ArgumentException($"Bad mob id '{s}'"))
                            .ToHashSet();
                        break;
                    case "--refresh": refresh = true; break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException($"Unknown argument '{args[i]}'");
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        outPath ??= Path.Combine(repoRoot, "data", "mobs.json");
        string cacheDir = Path.Combine(repoRoot, "data", "cache", "meowdb", "monsters");

        client = Path.GetFullPath(client);
        SortedDictionary<int, string?> clientMobs;
        try
        {
            clientMobs = ClientMobNames.Load(client);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read the client's String archive: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"Client lists {clientMobs.Count} mobs in String Mob.img.");

        var ids = clientMobs.Keys.Where(id => only is null || only.Contains(id)).ToList();
        if (only is not null)
            foreach (int missing in only.Where(o => !clientMobs.ContainsKey(o)))
                Console.WriteLine($"Note: id {missing} is not in the client's String archive; it will be fetched without a client name.");
        if (only is not null)
            ids = only.Order().ToList();

        var table = new MobTable();
        if (only is not null && File.Exists(outPath))
        {
            try
            {
                table.Mobs = JsonSerializer.Deserialize<MobTable>(File.ReadAllText(outPath), Json)?.Mobs ?? table.Mobs;
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Warning: existing {outPath} is not readable ({ex.Message}); it will be replaced.");
            }
        }

        int fetched = 0, cached = 0, parsed = 0;
        var notFound = new List<int>();
        var failures = new List<(int Id, string Why)>();
        var missingByMob = new SortedDictionary<int, List<string>>();
        var movementTexts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        bool stopped = false;

        using var meow = new MeowDbClient(cacheDir, refresh);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        foreach (int id in ids)
        {
            string? html;
            MeowDbClient.Outcome outcome;
            try
            {
                (html, outcome) = await meow.GetAsync(id, cts.Token);
            }
            catch (FetchStoppedException ex)
            {
                Console.Error.WriteLine($"STOPPED at id {id}: {ex.Message}");
                stopped = true;
                break;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                stopped = true;
                break;
            }

            switch (outcome)
            {
                case MeowDbClient.Outcome.Fetched: fetched++; break;
                case MeowDbClient.Outcome.Cached: cached++; break;
            }
            if (html is null)
            {
                notFound.Add(id);
                Console.WriteLine($"{id,4}  404 (site does not know this monster){(outcome == MeowDbClient.Outcome.NotFoundCached ? " [cached]" : "")}");
                continue;
            }

            try
            {
                var page = MobPageParser.Parse(id, html);
                if (page.Problem is not null)
                {
                    failures.Add((id, page.Problem));
                    Console.WriteLine($"{id,4}  PARSE FAILURE: {page.Problem}");
                    continue;
                }
                var mob = page.Mob;
                clientMobs.TryGetValue(id, out string? clientName);
                mob.Name = clientName ?? mob.SiteName;
                parsed++;
                table.Mobs[id.ToString("D7", CultureInfo.InvariantCulture)] = mob;
                if (page.Missing.Count > 0)
                    missingByMob[id] = page.Missing;
                Console.WriteLine($"{id,4}  {(outcome == MeowDbClient.Outcome.Fetched ? "fetched" : "cached ")}  {mob.Name,-28} Lv{mob.Level,3} HP{mob.Hp,7} {mob.Mobility,-7} maps={mob.Maps.Count}"
                    + (page.Missing.Count > 0 ? "  missing: " + string.Join(",", page.Missing) : ""));
            }
            catch (Exception ex)
            {
                failures.Add((id, ex.GetType().Name + ": " + ex.Message));
                Console.WriteLine($"{id,4}  PARSE FAILURE: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var m in table.Mobs.Values)
            movementTexts[m.Mobility] = movementTexts.GetValueOrDefault(m.Mobility) + 1;

        table.GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        table.Source = "meowdb.com/msclassic";
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(table, Json) + "\n");

        Console.WriteLine();
        Console.WriteLine($"Wrote {table.Mobs.Count} mobs to {outPath}");
        Console.WriteLine($"Ids processed: {ids.Count}; network fetches: {fetched}; from cache: {cached}; parsed: {parsed}; 404: {notFound.Count}; parse failures: {failures.Count}");
        Console.WriteLine("Mobility (all entries in the file): " + string.Join(", ", movementTexts.Select(kv => $"{kv.Key}={kv.Value}")));
        if (notFound.Count > 0)
            Console.WriteLine("Not on the site (404): " + string.Join(", ", notFound));
        foreach (var (id, why) in failures)
            Console.WriteLine($"Parse failure id {id}: {why}");
        if (missingByMob.Count > 0)
        {
            Console.WriteLine("Fields missing per mob:");
            foreach (var (id, fields) in missingByMob)
                Console.WriteLine($"  {id}: {string.Join(", ", fields)}");
        }
        return stopped ? 1 : failures.Count > 0 ? 3 : 0;
    }

    /// <summary>Walks up from the working directory (then the binary) to the folder holding OakSoul.slnx.</summary>
    private static string FindRepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "OakSoul.slnx")))
                    return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
