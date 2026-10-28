using System.Text.Json;
using OakSoul.Wz;

namespace OakSoul.SpriteExport;

/// <summary>
/// One animated sprite frame of a frame set: its PNG, the image and node it came from, and the properties the client draws it
/// with (origin, delay, the body rectangle lt/rb where there is one). The canvases' pixels live in a <c>_Canvas</c> archive and
/// their properties in the property archive or pack of the same image, so a set is written in two passes.
/// </summary>
internal sealed record FrameInfo(string File, string Image, string Path, int OriginX, int OriginY, int Delay, int[]? Lt, int[]? Rb);

/// <summary>A frame set's index (<c>&lt;set&gt;/frames.json</c>): its frames and, per image or id, the client's name.</summary>
internal sealed record FrameSetIndex(string Source, List<FrameInfo> Frames, SortedDictionary<string, string> Names);

/// <summary>
/// The frame sets the entity detector matches besides mobs and drop icons: the pets (<c>--pets</c>), the mouse cursor
/// (<c>--cursor</c>), the meso coins' animation with its origins (<c>--mesos</c>) and the character's own skill effects
/// (<c>--skill-effects</c>). Each is exported as PNGs plus a <c>frames.json</c> with every frame's origin and delay.
/// </summary>
internal sealed partial class ExportRun
{
    /// <summary>Item/Pet/_Canvas: every pet's animation frames (<c>&lt;id&gt;.img/&lt;anim&gt;/&lt;n&gt;</c>), <c>pet/&lt;id&gt;/&lt;anim&gt;_&lt;n&gt;.png</c>.</summary>
    public void ExportPets()
    {
        var props = new PropertyArchives(Program.ArchiveParts(Path.Combine(client, "Item", "Pet")));
        ExportSet("pet", Program.ArchiveParts(Path.Combine(client, "Item", "Pet", "_Canvas")), (img, node) =>
        {
            var parts = node.Path.Split('/');
            return parts.Length == 2 && parts[0] != "info" && int.TryParse(parts[1], out _) ? $"pet/{Program.Stem(img.Name)}/{parts[0]}_{parts[1]}.png" : null;
        }, props.Find, Names("Pet.img", id => id));
        props.Dispose();
    }

    /// <summary>UI/_Canvas Basic.img/Cursor: the mouse cursor's states and frames, <c>ui/cursor/&lt;state&gt;_&lt;n&gt;.png</c>.</summary>
    public void ExportCursor()
    {
        var props = new PropertyArchives(Program.ArchiveParts(Path.Combine(client, "UI")));
        ExportSet("ui/cursor", Program.ArchiveParts(Path.Combine(client, "UI", "_Canvas")), (img, node) =>
            img.Name == "Basic.img" && node.Path.Split('/') is ["Cursor", var state, var n] ? $"ui/cursor/{state}_{n}.png" : null, props.Find, []);
        props.Dispose();
    }

    /// <summary>Item/Special/_Canvas 0900.img: the meso coins' spinning frames with their origins, <c>item/meso/&lt;id&gt;/iconRaw_&lt;n&gt;.png</c>.</summary>
    public void ExportMesos()
    {
        var props = new PropertyArchives(Program.ArchiveParts(Path.Combine(client, "Item", "Special")));
        ExportSet("item/meso", Program.ArchiveParts(Path.Combine(client, "Item", "Special", "_Canvas")), (img, node) =>
            img.Name == "0900.img" && node.Path.Split('/') is [var id, "iconRaw", var n] ? $"item/meso/{id}/iconRaw_{n}.png" : null, props.Find, []);
        props.Dispose();
    }

    /// <summary>
    /// Skill/_Canvas &lt;job&gt;.img: the effect frames of every skill of the given jobs (<c>skill/&lt;id&gt;/&lt;node&gt;/&lt;n&gt;</c>
    /// for the nodes effect, hit/0, ball, mob, prepare, keydown, affected...; not the icons), <c>skill-effect/&lt;id&gt;/&lt;node&gt;_&lt;n&gt;.png</c>;
    /// the properties come from the skill pack (Packs/Skill_00000.ms).
    /// </summary>
    public void ExportSkillEffects(IReadOnlyList<string> jobs)
    {
        var pack = Path.Combine(client, "Packs", "Skill_00000.ms");
        using var ms = File.Exists(pack) ? MsFile.Open(pack) : null;
        WzNode? Find(string image) => ms?.FindEntry("Skill/" + image) is { } e ? ms.OpenImage(e).Root : null;
        var names = Names("Skill.img", id => id);
        ExportSet("skill-effect", Program.ArchiveParts(Path.Combine(client, "Skill", "_Canvas")), (img, node) =>
        {
            if (!jobs.Contains(Program.Stem(img.Name))) return null;
            var parts = node.Path.Split('/');
            if (parts.Length < 4 || parts[0] != "skill" || parts[^2].StartsWith("icon", StringComparison.Ordinal) || !int.TryParse(parts[^1], out _)) return null;
            return $"skill-effect/{parts[1]}/{string.Join('_', parts[2..])}.png";
        }, Find, names);
    }

    /// <summary>The client's names from String/String_NNN.wz, image <paramref name="image"/>, children &lt;id&gt;/name.</summary>
    private SortedDictionary<string, string> Names(string image, Func<string, string> key)
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Program.ArchiveParts(Path.Combine(client, "String")))
        {
            using var file = WzFile.Open(path);
            var img = file.Root.EnumerateImages().FirstOrDefault(i => i.Name == image);
            if (img is null) continue;
            foreach (var n in img.Root.Children)
                if (n.GetString("name") is { } name) names[key(n.Name)] = name;
            img.Unload();
        }
        return names;
    }

    /// <summary>Exports the canvases <paramref name="target"/> names, then writes <c>&lt;category&gt;/frames.json</c> with their properties.</summary>
    private void ExportSet(string category, IEnumerable<string> canvasArchives, Func<WzImage, WzNode, string?> target, Func<string, WzNode?> properties,
        SortedDictionary<string, string> names)
    {
        Export(category, canvasArchives, target);
        var frames = new List<FrameInfo>();
        foreach (var e in _entries.Where(e => e.Category == category).OrderBy(e => e.File, StringComparer.Ordinal))
        {
            // The canvas's full path: "<image>/<node path>".
            var slash = e.Node.IndexOf(".img/", StringComparison.Ordinal);
            if (slash < 0) continue;
            string image = e.Node[..(slash + 4)], path = e.Node[(slash + 5)..];
            var node = properties(image)?[path];
            var origin = node?["origin"] is { Type: WzNodeType.Vector } o ? o.Vector : (0, 0);
            int[]? V(string name) => node?[name] is { Type: WzNodeType.Vector } v ? [v.Vector.X, v.Vector.Y] : null;
            frames.Add(new FrameInfo(e.File, image, path, origin.Item1, origin.Item2, node?.GetInt("delay") ?? 0, V("lt"), V("rb")));
        }
        var dir = Path.Combine(outDir, category);
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "frames.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new FrameSetIndex(category, frames, names), Program.JsonOptions));
        Console.WriteLine($"Wrote {json} ({frames.Count} frames, {frames.Count(f => f.OriginX != 0 || f.OriginY != 0)} with an origin)");
    }

    /// <summary>The property archives of a family (the images whose canvases are outlinks to <c>_Canvas</c>), opened read-only.</summary>
    private sealed class PropertyArchives(IEnumerable<string> paths) : IDisposable
    {
        private readonly List<WzFile> _files = paths.Select(p => WzFile.Open(p)).ToList();
        private readonly Dictionary<string, WzNode?> _images = [];

        public WzNode? Find(string image)
        {
            lock (_images)
            {
                if (_images.TryGetValue(image, out var n)) return n;
                foreach (var f in _files)
                    if (f.Root.EnumerateImages().FirstOrDefault(i => i.Name == image) is { } img) return _images[image] = img.Root;
                return _images[image] = null;
            }
        }

        public void Dispose() { foreach (var f in _files) f.Dispose(); }
    }
}
