using System.Globalization;
using System.Text.Json;

namespace OakSoul.Detect;

/// <summary>How a mob moves, from the client's data: walks one floor, also jumps between floors, flies, or never moves.</summary>
public enum MobMobility { Floor, Jump, Fly, Static }

/// <summary>
/// One sprite frame ready for matching, in one facing, from the client's own frame (straight alpha).
/// <see cref="OriginX"/>, <see cref="OriginY"/> is the anchor drawn at the mob's position (its feet on the foothold);
/// the box <see cref="X0"/>..<see cref="Y1"/> (inclusive, image pixels) is the extent of the visible pixels.
/// <para>
/// The client draws these pixels 1:1 at integer positions, so on a pixel-exact frame every fully opaque pixel of the
/// frame shows on screen as stored, wherever nothing covers it (<see cref="PixelX"/> and the rest).
/// </para>
/// </summary>
public sealed class SpriteFrame
{
    /// <summary>Alpha at or above which a pixel counts as visible (for the extent).</summary>
    public const int VisibleAlpha = 128;

    /// <summary>
    /// Alpha of a pixel the matcher compares: fully opaque only. Any lower alpha is blended with whatever lies behind the
    /// sprite (the exported frames are opaque at 255 inside and anti-aliased at the edge).
    /// </summary>
    public const byte SolidAlpha = 255;

    public required string Anim { get; init; }
    public required int Index { get; init; }
    /// <summary>+1 facing right (the mirrored image), -1 facing left (as stored in the client's data).</summary>
    public required int Facing { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }
    public required int DelayMs { get; init; }
    public int X0 { get; init; }
    public int Y0 { get; init; }
    public int X1 { get; init; }
    public int Y1 { get; init; }
    public int ExtentWidth => X1 - X0 + 1;
    public int ExtentHeight => Y1 - Y0 + 1;

    /// <summary>
    /// The client's body rectangle of this frame (<c>lt</c> and <c>rb</c> in its frame data), relative to the origin in
    /// sprite pixels: left and top inclusive, right and bottom exclusive (Dark Stump move/0: lt -31,-51, rb 31,0, the
    /// 62x51 body standing on the origin). Mirrored about the origin pixel for a right-facing frame, as the pixels are.
    /// Null where the frame has none (the die frames): <see cref="Body"/> then falls back to the visible extent.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom)? ClientBody { get; init; }

    /// <summary>The body rectangle relative to the origin: the client's (<see cref="ClientBody"/>), else the visible extent.</summary>
    public (int Left, int Top, int Right, int Bottom) Body =>
        ClientBody ?? (X0 - OriginX, Y0 - OriginY, X1 + 1 - OriginX, Y1 + 1 - OriginY);

    /// <summary>
    /// Every fully opaque pixel (<see cref="SolidAlpha"/>) with its colour as stored, ordered so that any prefix is spread
    /// over the whole sprite (a stride coprime with the count), which lets a score stop early. On a pixel-exact frame
    /// these are the pixels the screen must show where the mob is not covered.
    /// </summary>
    public int[] PixelX { get; init; } = [];
    public int[] PixelY { get; init; } = [];
    public int[] PixelB { get; init; } = [];
    public int[] PixelG { get; init; } = [];
    public int[] PixelR { get; init; } = [];

    /// <summary>The opaque pixels packed as b | g &lt;&lt; 8 | r &lt;&lt; 16, as drawn and frozen (see <see cref="SpriteTint.Frozen"/>), for the coarse pass.</summary>
    public int[] PixelPacked { get; init; } = [];
    public int[] PixelFrozen { get; init; } = [];

    /// <summary>Whether an opaque pixel can be tested through steam at all (coloured and not too light, see <see cref="SpriteMatcher.FogMinChroma"/>).</summary>
    public bool[] PixelFoggable { get; init; } = [];

    /// <summary>The frame's straight-alpha BGRA pixels (for drawing and coverage; matching uses the samples).</summary>
    public byte[] Bgra { get; init; } = [];

    public string Key => _key ??= $"{Anim}/{Index}{(Facing > 0 ? "R" : "L")}";
    private string? _key;

    public override string ToString() => $"{Anim}_{Index}{(Facing > 0 ? " right" : " left")} {Width}x{Height} origin {OriginX},{OriginY}";

    /// <summary>
    /// Builds a frame from straight-alpha BGRA pixels, mirrored when <paramref name="facing"/> is +1; <paramref name="body"/>
    /// is the client's lt/rb (left-facing, as stored), mirrored with the pixels.
    /// </summary>
    public static SpriteFrame Create(string anim, int index, int facing, byte[] bgra, int width, int height, int originX, int originY, int delayMs,
        (int Left, int Top, int Right, int Bottom)? body = null)
    {
        // Mirroring negates every pixel's offset from the origin pixel, so columns [l, r) become [1 - r, 1 - l).
        if (body is { } b0 && facing > 0) body = (1 - b0.Right, b0.Top, 1 - b0.Left, b0.Bottom);
        var px = bgra;
        if (facing > 0)
        {
            px = new byte[bgra.Length];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    Array.Copy(bgra, (y * width + x) * 4, px, (y * width + width - 1 - x) * 4, 4);
            originX = width - 1 - originX;
        }
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        var solid = new List<(int X, int Y)>();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var a = px[(y * width + x) * 4 + 3];
                if (a == SolidAlpha) solid.Add((x, y));
                if (a < VisibleAlpha) continue;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
            }
        if (x1 < 0) { x0 = y0 = 0; x1 = y1 = -1; }
        var all = Spread(solid, solid.Count);
        var n = all.Count;
        var qx = new int[n]; var qy = new int[n]; var qb = new int[n]; var qg = new int[n]; var qr = new int[n];
        var packed = new int[n]; var frozen = new int[n]; var foggable = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var (x, y) = all[i];
            var o = (y * width + x) * 4;
            qx[i] = x; qy[i] = y; qb[i] = px[o]; qg[i] = px[o + 1]; qr[i] = px[o + 2];
            packed[i] = qb[i] | qg[i] << 8 | qr[i] << 16;
            frozen[i] = (qb[i] * SpriteMatcher.FrozenB + 127) / 255 | (qg[i] * SpriteMatcher.FrozenG + 127) / 255 << 8 | (qr[i] * SpriteMatcher.FrozenR + 127) / 255 << 16;
            foggable[i] = SpriteMatcher.Foggable(qb[i], qg[i], qr[i]);
        }
        return new SpriteFrame
        {
            Anim = anim, Index = index, Facing = facing, Width = width, Height = height, OriginX = originX, OriginY = originY, DelayMs = delayMs,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, ClientBody = body,
            PixelX = qx, PixelY = qy, PixelB = qb, PixelG = qg, PixelR = qr, PixelPacked = packed, PixelFrozen = frozen, PixelFoggable = foggable, Bgra = px,
        };
    }

    /// <summary>
    /// At most <paramref name="max"/> of the points in an order that spreads every prefix over all of them: a stride
    /// near the golden ratio of the count, made coprime with it.
    /// </summary>
    private static List<(int X, int Y)> Spread(List<(int X, int Y)> points, int max)
    {
        var n = points.Count;
        var stride = n <= 1 ? 1 : Math.Max(1, (int)(n * 0.618));
        while (n > 1 && Gcd(stride, n) != 1) stride++;
        var m = Math.Min(n, max);
        var result = new List<(int X, int Y)>(m);
        for (int i = 0, j = 0; i < m; i++, j = (j + stride) % n) result.Add(points[j]);
        return result;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}

/// <summary>A mob species as the client defines it: id, name, level, how it moves, and its frames in both facings.</summary>
public sealed class MobSpecies
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public int Level { get; init; }
    public int Exp { get; init; }
    public MobMobility Mobility { get; init; }

    /// <summary>The client's movement speed field (data/mobinfo <c>movement.fields.speed</c>, else <c>info.speed</c>): a percentage on a base walk, -70 to 0 on Deep Valley I.</summary>
    public int Speed { get; init; }

    /// <summary>
    /// Walking speed in world units per second, from <see cref="Speed"/>: 1.2 x (100 + speed). Measured on fx-combat-exact's
    /// keyframes (median of matched move-to-move steps): speed -50 (Axe Stump, Red Snail, Dark Axe Stump) 45 to 59, -70 (Dark
    /// Stump) 35, 0 (the boars) 109 to 126.
    /// </summary>
    public double WalkSpeed => WalkUnitsPerPercent * Math.Max(10, 100 + Speed);

    public const double WalkUnitsPerPercent = 1.2;

    /// <summary>The mob whose frames this one shows (itself, or the target of its link).</summary>
    public int SpriteId { get; init; }
    public IReadOnlyList<SpriteFrame> Frames { get; init; } = [];

    public string IdText => Id.ToString("D7", CultureInfo.InvariantCulture);

    public override string ToString() => $"{IdText} {Name} (Lv {Level}, {Mobility}, {Frames.Count} frames)";
}

/// <summary>
/// The mob data exported from the client by <c>OakSoul.MobExport --sprites</c> under <c>data/mobinfo</c>: per mob
/// <c>&lt;id&gt;.json</c> (info, movement, animations with frame sizes, origins, delays and resolved links) and the
/// frames as straight-alpha PNGs under <c>sprites/&lt;id&gt;/&lt;anim&gt;_&lt;frame&gt;.png</c>. A mob that links to another
/// shows the other's frames. Species are loaded on first use and cached; frames that are links to another frame of
/// the same mob are loaded once.
/// </summary>
public sealed class MobLibrary
{
    private readonly string _dir;
    private readonly Dictionary<int, MobSpecies?> _cache = [];
    private readonly Dictionary<int, IReadOnlyList<SpriteFrame>> _frames = [];

    public MobLibrary(string dir) => _dir = dir;

    public string Directory => _dir;

    /// <summary>Animations that do not show the mob itself and are left out of matching.</summary>
    public static readonly HashSet<string> ExcludedAnimations = ["revive"];

    public static MobLibrary? Open(string dir) => File.Exists(Path.Combine(dir, "index.json")) ? new MobLibrary(dir) : null;

    /// <summary>The species with this id (as in a map's life list), or null when it was not exported.</summary>
    public MobSpecies? Get(int id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;
        MobSpecies? species = null;
        var path = Path.Combine(_dir, $"{id:D7}.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var info = root.GetProperty("info");
            var mobility = root.TryGetProperty("movement", out var mv) && mv.TryGetProperty("mobility", out var m) ? m.GetString() : null;
            var speed = mv.ValueKind == JsonValueKind.Object && mv.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("speed", out _)
                ? Int(fields, "speed") : Int(info, "speed");
            // A linked mob shows its target's frames; follow the links to a mob whose sprites were exported.
            var spriteId = id;
            for (var hop = 0; hop < 4 && !System.IO.Directory.Exists(SpriteDir(spriteId)); hop++)
            {
                var link = hop == 0 ? Text(root, "link") : LinkOf(spriteId);
                if (link is null || !int.TryParse(link, NumberStyles.None, CultureInfo.InvariantCulture, out var target)) break;
                spriteId = target;
            }
            species = new MobSpecies
            {
                Id = id,
                Name = Text(root, "name") ?? id.ToString("D7", CultureInfo.InvariantCulture),
                Level = Int(info, "level"),
                Exp = Int(info, "exp"),
                Mobility = mobility switch { "jump" => MobMobility.Jump, "fly" => MobMobility.Fly, "static" => MobMobility.Static, _ => MobMobility.Floor },
                Speed = speed,
                SpriteId = spriteId,
                Frames = FramesOf(spriteId),
            };
        }
        _cache[id] = species;
        return species;
    }

    private string SpriteDir(int id) => Path.Combine(_dir, "sprites", id.ToString("D7", CultureInfo.InvariantCulture));

    private string? LinkOf(int id)
    {
        var path = Path.Combine(_dir, $"{id:D7}.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return Text(doc.RootElement, "link");
    }

    /// <summary>Every distinct frame of a mob's animations, in both facings (links within the mob resolved to one copy).</summary>
    private IReadOnlyList<SpriteFrame> FramesOf(int spriteId)
    {
        if (_frames.TryGetValue(spriteId, out var known)) return known;
        var list = new List<SpriteFrame>();
        var path = Path.Combine(_dir, $"{spriteId:D7}.json");
        var dir = SpriteDir(spriteId);
        if (File.Exists(path) && System.IO.Directory.Exists(dir))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("animations", out var anims) && anims.ValueKind == JsonValueKind.Object)
                foreach (var anim in anims.EnumerateObject())
                {
                    // "revive" is the Blue Snail's puff of white smoke, not the mob: it matches steam and clouds.
                    if (ExcludedAnimations.Contains(anim.Name)) continue;
                    if (!anim.Value.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) continue;
                    foreach (var f in frames.EnumerateArray())
                    {
                        // A frame that links to another frame shows the same pixels; it is matched as that frame.
                        if (f.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Object) continue;
                        var index = Int(f, "index");
                        var png = Path.Combine(dir, $"{anim.Name}_{index}.png");
                        if (!File.Exists(png) || !f.TryGetProperty("origin", out var origin)) continue;
                        var (bgra, w, h) = Png.ReadBgra(png);
                        (int, int, int, int)? body = f.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
                            && props.TryGetProperty("lt", out var lt) && props.TryGetProperty("rb", out var rb) && lt.ValueKind == JsonValueKind.Object && rb.ValueKind == JsonValueKind.Object
                            ? (Int(lt, "x"), Int(lt, "y"), Int(rb, "x"), Int(rb, "y")) : null;
                        foreach (var facing in new[] { -1, 1 })
                            list.Add(SpriteFrame.Create(anim.Name, index, facing, bgra, w, h, Int(origin, "x"), Int(origin, "y"), Int(f, "delay"), body));
                    }
                }
        }
        _frames[spriteId] = list;
        return list;
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt32(),
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => 0,
        } : 0;
}
