using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OakSoul.Wz;

namespace OakSoul.SpriteExport;

/// <summary>One manifest row: a written PNG and where its canvas came from.</summary>
internal sealed record SpriteEntry(
    string Category, string Archive, string Node, int Width, int Height, int Format, string File);

/// <summary>Performs one export: decodes the selected canvases, writes PNGs, then the manifest and contact sheets.</summary>
/// <summary>manifest.json: every written PNG, and for each mob id its animations with frame counts.</summary>
internal sealed record Manifest(
    DateTime GeneratedUtc, string PixelLayout, int EntryCount, List<SpriteEntry> Entries,
    Dictionary<string, Dictionary<string, MobAnimation>> MobAnimations);

/// <summary>
/// Frames of one mob animation present in the canvas archive. Frame numbers can have gaps: the missing frames
/// are links (UOLs) in the property archives, which this client does not ship.
/// </summary>
internal sealed record MobAnimation(int FrameCount, List<int> Frames);

internal sealed partial class ExportRun(string client, string outDir)
{
    private readonly ConcurrentBag<SpriteEntry> _entries = [];
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentBag<int>>> _mobAnimations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Anim, int W, int H, byte[] Bgra)> _mobPreview = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Id, int Frame), (int W, int H, byte[] Bgra)> _mesoFrames = new();
    private readonly List<(string Category, TimeSpan Time)> _timings = [];

    public Stats Stats { get; } = new();
    public ConcurrentBag<Failure> Failures => Stats.Failures;

    /// <summary>Mob/_Canvas: <c>&lt;id&gt;.img/&lt;animation&gt;/&lt;frame&gt;</c> canvases.</summary>
    public void ExportMobs()
    {
        Export("mob", Program.ArchiveParts(Path.Combine(client, "Mob", "_Canvas")), (img, node) =>
        {
            string id = Program.Stem(img.Name);
            var parts = node.Path.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[1], out int frame))
                _mobAnimations.GetOrAdd(id, _ => new(StringComparer.Ordinal)).GetOrAdd(parts[0], _ => []).Add(frame);
            return $"mob/{id}/{Program.Flatten(node.Path)}.png";
        }, OnMobFrame);
    }

    /// <summary>Item/&lt;category&gt;/_Canvas: <c>&lt;group&gt;.img/&lt;id&gt;/iconRaw/&lt;n&gt;</c> and <c>&lt;id&gt;/icon</c>, or <c>&lt;id&gt;.img/...</c>.</summary>
    public void ExportItems()
    {
        foreach (string category in Program.Items)
        {
            string cat = category.ToLowerInvariant();
            Export("item/" + cat, Program.ArchiveParts(Path.Combine(client, "Item", category, "_Canvas")), (img, node) =>
            {
                string stem = Program.Stem(img.Name);
                var parts = node.Path.Split('/');
                // Grouped images (0900.img) hold one subtree per item id; other images are one item each.
                bool grouped = stem.Length == 4 && parts.Length > 1;
                string id = grouped ? parts[0] : stem;
                string rest = grouped ? string.Join('_', parts[1..]) : Program.Flatten(node.Path);
                return $"item/{cat}/{id}/{rest}.png";
            }, category == "Special" ? OnSpecialItem : null);
        }
    }

    /// <summary>Skill/_Canvas: <c>&lt;job&gt;.img/skill/&lt;skillId&gt;/icon</c> only.</summary>
    public void ExportSkills()
    {
        Export("skill", Program.ArchiveParts(Path.Combine(client, "Skill", "_Canvas")), (img, node) =>
        {
            var parts = node.Path.Split('/');
            return parts is ["skill", var id, "icon"] ? $"skill/{id}/icon.png" : null;
        });
    }

    /// <summary>Map/Map/MapN/_Canvas: <c>&lt;mapId&gt;.img/miniMap/canvas</c> only.</summary>
    public void ExportMinimaps()
    {
        string mapRoot = Path.Combine(client, "Map", "Map");
        var archives = Directory.Exists(mapRoot)
            ? Directory.EnumerateDirectories(mapRoot).Where(d => MapFolder().IsMatch(Path.GetFileName(d)))
                .Order(StringComparer.Ordinal).SelectMany(d => Program.ArchiveParts(Path.Combine(d, "_Canvas"))).ToList()
            : [];
        Export("minimap", archives, (img, node) =>
            node.Path == "miniMap/canvas" ? $"minimap/{Program.Stem(img.Name)}.png" : null);
    }

    /// <summary>
    /// Effect/_Canvas BasicEff.img: the damage number digit sets (<c>NoRed0</c>, <c>NoRed1</c>, <c>NoViolet0</c>, ...; a set
    /// ending in 1 or 3 holds the larger first digit) and their other canvases (Miss, guard, the critical bubble), as
    /// <c>effect/BasicEff/&lt;set&gt;/&lt;name&gt;.png</c>, plus <c>effect/damage-digits.json</c> with every canvas's origin
    /// from the property archive (Effect/Effect_000.wz), where the canvases themselves are only outlinks.
    /// </summary>
    public void ExportEffects()
    {
        Export("effect", Program.ArchiveParts(Path.Combine(client, "Effect", "_Canvas")), (img, node) =>
        {
            if (img.Name != "BasicEff.img") return null;
            var parts = node.Path.Split('/');
            return parts is [var set, var name] && set.StartsWith("No", StringComparison.Ordinal) ? $"effect/BasicEff/{set}/{name}.png" : null;
        });
        // Origins live in the property archive; the canvases there are 1x1 outlinks to the _Canvas archive.
        var sets = new SortedDictionary<string, SortedDictionary<string, DigitInfo>>(StringComparer.Ordinal);
        foreach (string path in Program.ArchiveParts(Path.Combine(client, "Effect")))
        {
            using var file = WzFile.Open(path);
            var basic = file.Root.EnumerateImages().FirstOrDefault(i => i.Name == "BasicEff.img");
            if (basic is null) continue;
            foreach (var set in basic.Root.Children.Where(c => c.Name.StartsWith("No", StringComparison.Ordinal)))
                foreach (var n in set.Children.Where(n => n.Type == WzNodeType.Canvas))
                {
                    var file2 = $"effect/BasicEff/{set.Name}/{n.Name}.png";
                    if (!File.Exists(Path.Combine(outDir, file2))) continue;
                    var origin = n["origin"] is { Type: WzNodeType.Vector } o ? o.Vector : (0, 0);
                    if (!sets.TryGetValue(set.Name, out var digits)) sets[set.Name] = digits = new(StringComparer.Ordinal);
                    digits[n.Name] = new DigitInfo(file2, origin.Item1, origin.Item2);
                }
            basic.Unload();
        }
        Directory.CreateDirectory(Path.Combine(outDir, "effect"));
        var json = Path.Combine(outDir, "effect", "damage-digits.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new { source = "Effect/BasicEff.img", sets }, Program.JsonOptions));
        Console.WriteLine($"Wrote {json} ({sets.Count} sets)");
    }

    /// <summary>One canvas of a damage number set: its PNG and origin.</summary>
    internal sealed record DigitInfo(string File, int OriginX, int OriginY);

    /// <summary>
    /// Decodes and writes every canvas for which <paramref name="target"/> returns a relative output path.
    /// Images are processed in parallel and unloaded afterwards.
    /// </summary>
    private void Export(string category, IEnumerable<string> archives,
        Func<WzImage, WzNode, string?> target, Action<WzImage, WzNode, byte[]>? onDecoded = null)
    {
        var sw = Stopwatch.StartNew();
        int count = 0;
        foreach (string path in archives)
        {
            string rel = Path.GetRelativePath(client, path);
            using var file = WzFile.Open(path);
            Parallel.ForEach(file.Root.EnumerateImages(), img =>
            {
                try
                {
                    foreach (var node in Program.Canvases(img.Root))
                    {
                        string? relFile = target(img, node);
                        if (relFile is null)
                            continue;
                        var canvas = node.Canvas;
                        try
                        {
                            byte[] bgra = canvas.Decode();
                            if (canvas.Width == 0 || canvas.Height == 0)
                                throw new WzFormatException("canvas is empty (zero width or height)");
                            PngWriter.Save(Path.Combine(outDir, relFile), canvas.Width, canvas.Height, bgra);
                            Stats.Count(category, canvas);
                            Interlocked.Increment(ref count);
                            _entries.Add(new SpriteEntry(category, rel, canvas.FullPath, canvas.Width, canvas.Height, canvas.PixelFormat, relFile));
                            onDecoded?.Invoke(img, node, bgra);
                        }
                        catch (WzFormatException ex)
                        {
                            Stats.Fail(rel, canvas, ex.Message);
                        }
                    }
                }
                catch (WzFormatException ex)
                {
                    Stats.Failures.Add(new Failure(rel, img.Path, "image parse: " + ex.Message));
                }
                finally
                {
                    img.Unload();
                }
            });
        }
        sw.Stop();
        lock (_timings)
            _timings.Add((category, sw.Elapsed));
        Console.WriteLine($"{category}: {count} PNGs in {sw.Elapsed.TotalSeconds:F1}s");
    }

    private void OnMobFrame(WzImage img, WzNode node, byte[] bgra)
    {
        // Preview frame: move/0 preferred, then stand/0, then any animation's frame 0.
        var parts = node.Path.Split('/');
        if (parts.Length != 2 || parts[1] != "0")
            return;
        int rank = parts[0] switch { "move" => 0, "stand" => 1, _ => 2 };
        var (w, h) = node.CanvasSize;
        _mobPreview.AddOrUpdate(Program.Stem(img.Name), (parts[0], w, h, bgra), (_, old) =>
        {
            int oldRank = old.Anim switch { "move" => 0, "stand" => 1, _ => 2 };
            return rank < oldRank ? (parts[0], w, h, bgra) : old;
        });
    }

    private void OnSpecialItem(WzImage img, WzNode node, byte[] bgra)
    {
        if (img.Name != "0900.img")
            return;
        var parts = node.Path.Split('/');
        if (parts is [var id, "iconRaw", var n] && int.TryParse(n, out int frame))
        {
            var (w, h) = node.CanvasSize;
            _mesoFrames[(id, frame)] = (w, h, bgra);
        }
    }

    public void WriteContactSheets()
    {
        if (!_mobPreview.IsEmpty)
        {
            string path = Path.Combine(outDir, "contact-mobs.png");
            ContactSheets.Mobs(_mobPreview.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => (p.Key, p.Value.Anim, p.Value.W, p.Value.H, p.Value.Bgra)).ToList(), path);
            Console.WriteLine($"Wrote {path} ({_mobPreview.Count} mobs)");
        }
        if (!_mesoFrames.IsEmpty)
        {
            string path = Path.Combine(outDir, "contact-drops.png");
            var rows = _mesoFrames.GroupBy(p => p.Key.Id).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, (IReadOnlyList<(int W, int H, byte[] Bgra)>)g.OrderBy(p => p.Key.Frame).Select(p => p.Value).ToList()))
                .ToList();
            ContactSheets.Drops(rows, path);
            Console.WriteLine($"Wrote {path} ({rows.Count} meso bags)");
        }
    }

    public void WriteManifest()
    {
        var entries = _entries.ToList();
        var mobs = _mobAnimations.ToDictionary(
            p => p.Key,
            p => p.Value.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(
                a => a.Key, a => new MobAnimation(a.Value.Count, a.Value.Order().ToList())), StringComparer.Ordinal);
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, "manifest.json");

        // Merge with an existing manifest so a run with one selector keeps the other categories' rows.
        var written = new HashSet<string>(entries.Select(e => e.Category), StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var old = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Program.JsonOptions);
                if (old is not null)
                {
                    entries.AddRange(old.Entries.Where(e => !written.Contains(e.Category)));
                    if (!written.Contains("mob"))
                        mobs = old.MobAnimations;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Existing manifest not readable ({ex.Message}); replacing it.");
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        var sortedMobs = mobs.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        var manifest = new Manifest(DateTime.UtcNow, "PNG RGBA8, straight alpha", entries.Count, entries, sortedMobs);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Program.JsonOptions));
        Console.WriteLine($"Wrote {path} ({entries.Count} entries)");
    }

    public void Report(TextWriter w, TimeSpan total)
    {
        w.WriteLine();
        Stats.Print(w);
        foreach (var (cat, time) in _timings)
            w.WriteLine($"  time {cat,-16} {time.TotalSeconds,6:F1}s");
        w.WriteLine($"Total time: {total.TotalSeconds:F1}s");
    }

    [GeneratedRegex(@"^Map\d+$")]
    private static partial Regex MapFolder();
}
