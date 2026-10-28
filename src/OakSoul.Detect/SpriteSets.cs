using System.Globalization;
using System.Text.Json;

namespace OakSoul.Detect;

/// <summary>
/// A set of animated sprites exported from the client with their origins and delays by <c>OakSoul.SpriteExport</c>
/// (<c>--pets</c>, <c>--cursor</c>, <c>--mesos</c>, <c>--skill-effects</c>): <c>data/sprites/&lt;set&gt;/frames.json</c> lists
/// every frame's PNG, the image and node it came from, its origin, delay and body rectangle. The frames are grouped (one pet,
/// one cursor state, one meso coin, one skill's effect node) and each group is a <see cref="MobSpecies"/>, so the
/// <see cref="SpriteMatcher"/> compares them with the screen exactly as it does mobs: every fully opaque pixel must show as
/// stored.
/// </summary>
public sealed class SpriteSet
{
    /// <summary>The groups by key (a pet's id, a cursor state, a meso item id, "skillId node"), each with its frames.</summary>
    public IReadOnlyDictionary<string, MobSpecies> Groups { get; }

    /// <summary>The client's names by group key, where it has one (pets, skills).</summary>
    public IReadOnlyDictionary<string, string> Names { get; }

    public string Set { get; }

    private SpriteSet(string set, Dictionary<string, MobSpecies> groups, Dictionary<string, string> names)
    {
        Set = set;
        Groups = groups;
        Names = names;
    }

    /// <summary>
    /// Loads a set, or returns null when it was not exported. <paramref name="group"/> names a frame's group and its animation
    /// and index from its image and node path (null leaves the frame out); <paramref name="mirror"/> adds every frame facing
    /// right as well (pets and skill effects turn with whoever draws them; the cursor and coins do not).
    /// </summary>
    public static SpriteSet? Load(string spritesDir, string set, Func<string, string, (string Group, string Anim, int Index)?> group, bool mirror,
        Func<string, string>? nameKey = null)
    {
        var index = Path.Combine(spritesDir, set, "frames.json");
        if (!File.Exists(index)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(index));
        var root = doc.RootElement;
        var frames = new Dictionary<string, List<SpriteFrame>>(StringComparer.Ordinal);
        foreach (var f in root.GetProperty("frames").EnumerateArray())
        {
            var image = f.GetProperty("image").GetString() ?? "";
            var path = f.GetProperty("path").GetString() ?? "";
            if (group(image, path) is not { } g) continue;
            var file = Path.Combine(spritesDir, f.GetProperty("file").GetString() ?? "");
            if (!File.Exists(file)) continue;
            var (bgra, w, h) = Png.ReadBgra(file);
            (int, int, int, int)? body = f.TryGetProperty("lt", out var lt) && lt.ValueKind == JsonValueKind.Array && f.TryGetProperty("rb", out var rb) && rb.ValueKind == JsonValueKind.Array
                ? (lt[0].GetInt32(), lt[1].GetInt32(), rb[0].GetInt32(), rb[1].GetInt32()) : null;
            int ox = f.GetProperty("originX").GetInt32(), oy = f.GetProperty("originY").GetInt32(), delay = f.GetProperty("delay").GetInt32();
            if (!frames.TryGetValue(g.Group, out var list)) frames[g.Group] = list = [];
            foreach (var facing in mirror ? new[] { -1, 1 } : new[] { -1 })
                list.Add(SpriteFrame.Create(g.Anim, g.Index, facing, bgra, w, h, ox, oy, delay, body));
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Object)
            foreach (var p in n.EnumerateObject()) names[p.Name] = p.Value.GetString() ?? p.Name;
        var groups = new Dictionary<string, MobSpecies>(StringComparer.Ordinal);
        foreach (var (key, list) in frames.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var id = int.TryParse(key.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var name = names.TryGetValue(nameKey?.Invoke(key) ?? key, out var nm) ? nm : key;
            groups[key] = new MobSpecies { Id = id, Name = name, Mobility = MobMobility.Jump, SpriteId = id, Frames = list.OrderBy(f => f.Anim, StringComparer.Ordinal).ThenBy(f => f.Index).ThenBy(f => f.Facing).ToList() };
        }
        return groups.Count == 0 ? null : new SpriteSet(set, groups, names);
    }

    /// <summary>Every pet's animation frames (<c>pet</c>; groups by pet id, both facings).</summary>
    public static SpriteSet? Pets(string spritesDir) => Load(spritesDir, "pet", (image, path) =>
        path.Split('/') is [var anim, var n] && int.TryParse(n, out var i) && anim is not ("info" or "interact" or "food" or "slang")
            ? (image.Replace(".img", ""), anim, i) : null, mirror: true);

    /// <summary>The mouse cursor (<c>ui/cursor</c>; groups by cursor state, as drawn).</summary>
    public static SpriteSet? Cursor(string spritesDir) => Load(spritesDir, "ui/cursor", (_, path) =>
        path.Split('/') is ["Cursor", var state, var n] && int.TryParse(n, out var i) ? (state, "cursor" + state, i) : null, mirror: false);

    /// <summary>The meso coins' spinning frames (<c>item/meso</c>; groups by item id, as drawn).</summary>
    public static SpriteSet? Mesos(string spritesDir) => Load(spritesDir, "item/meso", (_, path) =>
        path.Split('/') is [var id, "iconRaw", var n] && int.TryParse(n, out var i) ? (id, "iconRaw", i) : null, mirror: false);

    /// <summary>The own skills' effect frames (<c>skill-effect</c>; groups by "skillId node", both facings).</summary>
    public static SpriteSet? SkillEffects(string spritesDir) => Load(spritesDir, "skill-effect", (_, path) =>
    {
        var parts = path.Split('/');
        if (parts.Length < 4 || parts[0] != "skill" || !int.TryParse(parts[^1], out var i)) return null;
        var node = string.Join('/', parts[2..^1]);
        return ($"{parts[1]} {node}", node, i);
    }, mirror: true, nameKey: key => key.Split(' ')[0]);
}
