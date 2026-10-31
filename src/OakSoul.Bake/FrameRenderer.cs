using System.Globalization;
using OakSoul.Detect;
using OakSoul.Overlay;

namespace OakSoul.Bake;

/// <summary>How to draw one item; the default comes from its layer and kind (<see cref="FrameRenderer.DefaultStyle"/>).</summary>
public sealed record ItemStyle(uint Colour, int Thickness, bool Labelled = true);

/// <summary>What to render and how.</summary>
/// <param name="Scale">Nearest-neighbour magnification of the frame (labels are drawn at this text scale too).</param>
/// <param name="Layers">The layers drawn.</param>
/// <param name="Style">
/// Overrides the style of an item (null keeps the default). This is the seam for the agreed diff mode, which will render
/// the items of two streams of one recording together, matched by capture sequence and pts, with the items only one
/// stream has (or whose fields differ) styled apart.
/// </param>
public sealed record RenderOptions(int Scale, IReadOnlySet<string> Layers, Func<OverlayItem, ItemStyle?>? Style = null);

/// <summary>
/// Renders one frame of the overlay stream over the recording's pixels, for review: a header strip naming the session,
/// capture sequence, time, map and camera with the frame's counts and a legend; the frame magnified nearest-neighbour
/// with every layer drawn (entities labelled "#116 Dark Stump move/3 R 0.97", "~" carried, "?" unverified, the rival
/// species when close); and a footer listing every entity with its rect and feet, so a label can be matched to its item.
/// Drawn with Detect's Canvas and 5x7 font; the colours are the contact sheets' (Unknown grey, Mob red, Player blue, Pet
/// cyan, Drop yellow, Effect purple, tag boxes in their owner's colour, orange while their text is still being read, white own character).
/// </summary>
public static class FrameRenderer
{
    public const uint White = 0xFFFFFFFF, Cyan = 0xFF00FFFF, Foothold = 0xFF50D050, Rope = 0xFFD09040, Portal = 0xFFFF70D0,
        HudGrey = 0xFFA0A0A0, MinimapLine = 0xFFE0E0E0, HeaderBack = 0xFF181818, Dim = 0xFFB8B8B8, Pending = 0xFFFFA000;

    public static uint KindColour(string kind) => kind switch
    {
        "mob" => EntityDetector.KindColour(EntityKind.Mob),
        "player" => EntityDetector.KindColour(EntityKind.Player),
        "pet" => EntityDetector.KindColour(EntityKind.Pet),
        "drop" => EntityDetector.KindColour(EntityKind.Drop),
        "effect" => EntityDetector.KindColour(EntityKind.Effect),
        "damage" => EntityDetector.KindColour(EntityKind.Damage),
        _ => EntityDetector.KindColour(EntityKind.Unknown),
    };

    public static ItemStyle DefaultStyle(OverlayItem item, int scale) => item.Layer switch
    {
        OverlayLayers.Entity => new ItemStyle(KindColour(item.Kind), Math.Max(1, scale)),
        OverlayLayers.Tags => new ItemStyle(item.Bool("pending") ? Pending : TagColour(item.Str("owner")), Math.Max(1, scale / 2 + scale % 2), Labelled: false),
        OverlayLayers.Self => new ItemStyle(White, item.Kind == "tag" ? 1 : Math.Max(1, scale)),
        OverlayLayers.World => new ItemStyle(item.Kind switch { "foothold" => Foothold, "portal" => Portal, _ => Rope }, item.Kind == "portal" ? Math.Max(1, scale) : WorldLine(scale), item.Kind == "portal"),
        OverlayLayers.Minimap => new ItemStyle(item.Kind == "dot" ? DotColour(item.Str("dot")) : MinimapLine, 1, item.Kind == "names"),
        OverlayLayers.Hud => new ItemStyle(HudGrey, 1, item.Kind == "region"),
        _ => new ItemStyle(White, 1, false),
    };

    /// <summary>A tag box in its owner's colour (a mob's red, the pet's cyan, a player's blue, the own white, none grey); cyan in streams without owners.</summary>
    public static uint TagColour(string? owner) => owner switch
    {
        "mob" or "pet" or "player" => KindColour(owner),
        "self" => White,
        "none" => KindColour("unknown"),
        _ => Cyan,
    };

    /// <summary>Width of footholds, ropes and ladders in output pixels: 5 at the default 2x (2.5 per game pixel, rounded).</summary>
    public static int WorldLine(int scale) => Math.Max(1, (5 * scale + 1) / 2);

    private static uint DotColour(string? dot) => dot switch
    {
        "self" => 0xFFFFFF40,
        "player" => 0xFFFF4040,
        "portal" => 0xFF40E0E0,
        "npc" => 0xFF40FF40,
        _ => 0xFFFF80FF,
    };

    private const int Glyph = 6, LineHeight = 10;

    public static Canvas Render(OverlayHeader header, OverlayFrame f, byte[] bgra, int width, int height, RenderOptions o)
    {
        var s = Math.Max(1, o.Scale);
        var ts = s;
        var frame = Magnify(bgra, width, height, s);
        var items = f.Items.Where(i => o.Layers.Contains(i.Layer)).ToList();
        var labels = new List<(int X, int Y, int W, int H)>();
        // Drawing order is the registry's: world, minimap, hud, camera, tags, self, entity (entities on top).
        foreach (var layer in OverlayLayers.Names)
            foreach (var item in items.Where(i => i.Layer == layer))
                Draw(frame, item, o.Style?.Invoke(item) ?? DefaultStyle(item, s), s, ts, labels);

        var head = Header(header, f, items, o, frame.Width, ts);
        var foot = Footer(f, items, frame.Width, ts);
        var canvas = new Canvas(frame.Width, head.Height + frame.Height + foot.Height, HeaderBack);
        canvas.Paste(head, 0, 0);
        canvas.Paste(frame, 0, head.Height);
        canvas.Paste(foot, 0, head.Height + frame.Height);
        return canvas;
    }

    /// <summary>The frame at <paramref name="s"/> times its size, each pixel an s x s block.</summary>
    public static Canvas Magnify(byte[] bgra, int width, int height, int s)
    {
        if (s == 1) return new Canvas(bgra, width, height);
        var canvas = new Canvas(width * s, height * s);
        var dst = canvas.Pixels;
        for (var y = 0; y < height; y++)
        {
            var row = y * s * width * s * 4;
            for (var x = 0; x < width; x++)
            {
                var px = BitConverter.ToUInt32(bgra, (y * width + x) * 4) | 0xFF000000;
                for (var dx = 0; dx < s; dx++) BitConverter.TryWriteBytes(dst.AsSpan(row + (x * s + dx) * 4, 4), px);
            }
            for (var dy = 1; dy < s; dy++) Array.Copy(dst, row, dst, row + dy * width * s * 4, width * s * 4);
        }
        return canvas;
    }

    private static void Draw(Canvas c, OverlayItem item, ItemStyle style, int s, int ts, List<(int X, int Y, int W, int H)> labels)
    {
        var col = style.Colour;
        if (item.Rect is { } r && !(item.Layer == OverlayLayers.Minimap && item.Kind == "dot"))
            c.Rect(r.X * s, r.Y * s, r.W * s, r.H * s, col, style.Thickness);
        if (item.Line() is { Length: >= 2 } line)
            for (var i = 1; i < line.Length; i++)
                DrawLine(c, line[i - 1][0] * s + s / 2, line[i - 1][1] * s + s / 2, line[i][0] * s + s / 2, line[i][1] * s + s / 2, col, style.Thickness);
        // A portal with its activation area (rect) is the area; its point is only drawn where there is no area (a spawn point, or a stream from before).
        if (item.Point("point") is { } p && !(item.Layer == OverlayLayers.World && item.Kind == "portal" && item.Rect is not null))
        {
            var size = item.Layer == OverlayLayers.Minimap ? 2 * s : 4 * s;
            c.Rect(p.X * s - size, p.Y * s - size, 2 * size + 1, 2 * size + 1, col);
        }
        if (item.Point("feet") is { } feet && item.Layer is OverlayLayers.Entity or OverlayLayers.Self)
            Cross(c, feet.X * s + s / 2, feet.Y * s + s / 2, 2 * s, col);
        if (!style.Labelled || item.Label is not { Length: > 0 } text) return;
        // Labels sit above the item (an entity's box, a region, a portal), moved up past labels already placed so none
        // overlap, on a translucent backing so the sprite under them still shows.
        var anchor = item.Rect is { } rr ? (X: rr.X * s, Y: rr.Y * s) : item.Point("point") is { } pp ? (X: pp.X * s, Y: pp.Y * s - 4 * s) : item.Point("feet") is { } ff ? (X: ff.X * s, Y: ff.Y * s) : (X: 0, Y: 0);
        var lw = text.Length * Glyph * ts + 2;
        var lh = 8 * ts + 2;
        var x = Math.Clamp(anchor.X, 0, Math.Max(0, c.Width - lw));
        var y = anchor.Y - lh - 1;
        var moved = false;
        for (var tries = 0; tries < 12 && labels.Any(l => Overlaps(l, (x, y, lw, lh))); tries++) { y -= lh; moved = true; }
        if (y < 0) { y = Math.Min(c.Height - lh, (item.Rect is { } rb ? (rb.Y + rb.H) * s : anchor.Y) + 2); moved = true; }
        labels.Add((x, y, lw, lh));
        if (moved) DrawLine(c, x, y + lh, anchor.X, anchor.Y, col, 1);
        for (var yy = y; yy < y + lh; yy++)
            for (var xx = x; xx < x + lw; xx++) c.Tint(xx, yy, 0xFF000000, 0.6);
        c.Text(x + 1, y + 1, text, col, ts, null);
    }

    private static bool Overlaps((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static void Cross(Canvas c, int x, int y, int r, uint col)
    {
        for (var d = -r; d <= r; d++) { c.Set(x + d, y, col); c.Set(x, y + d, col); }
    }

    public static void DrawLine(Canvas c, int x0, int y0, int x1, int y1, uint col, int thickness = 1)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        // A thick line is centred on the thin one: rows above and below a mostly horizontal line, columns beside a steep one.
        var first = -(thickness - 1) / 2;
        for (var n = 0; n < 20000; n++)
        {
            for (var t = first; t < first + thickness; t++) c.Set(x0 + (dx >= -dy ? 0 : t), y0 + (dx >= -dy ? t : 0), col);
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static Canvas Header(OverlayHeader h, OverlayFrame f, List<OverlayItem> items, RenderOptions o, int width, int ts)
    {
        var lines = new List<(string Text, uint Colour)>();
        var cam = items.FirstOrDefault(i => i.Layer == OverlayLayers.Camera)?.Label ?? (f.Camera is { } c ? $"cam {c.X},{c.Y}" : "no camera");
        var names = items.FirstOrDefault(i => i.Layer == OverlayLayers.Minimap && i.Kind == "names")?.Label;
        lines.Add(($"{h.Session ?? "?"}  seq {f.Sequence:D7}{(f.Filled ? " (filled)" : "")}  t {f.Clock} ({f.Time.ToString("0.000", CultureInfo.InvariantCulture)} s)  map {f.MapId?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(names is null ? "" : $" ({names})")}  {cam}" +
                   $"{(f.Detect is { } d && d != "ok" ? $"  detector: {d}" : "")}{(f.Skip is { } sk ? $"  detector skipped: {sk}" : "")}", White));
        var ents = f.Layer(OverlayLayers.Entity).ToList();
        var mobs = ents.Where(e => e.Kind == "mob").ToList();
        var counts = $"mob {mobs.Count} (matched {mobs.Count(m => m.Str("evidence") == "matched")}, carried ~{mobs.Count(m => m.Str("evidence") == "carried")}, unverified ?{mobs.Count(m => m.Str("evidence") == "unverified")})" +
                     string.Concat(new[] { "player", "pet", "drop", "effect", "damage", "unknown" }.Select(k => $"  {k} {ents.Count(e => e.Kind == k)}"));
        var hud = string.Join("  ", f.Layer(OverlayLayers.Hud).Where(i => i.Kind == "region" && i.Str("name") is "status.level" or "status.hp" or "status.mp" or "status.exp").Select(i => i.Label));
        lines.Add(($"{counts}    {hud}", Dim));
        var canvas = new Canvas(width, 6 + (lines.Count + 1) * (LineHeight * ts) + 2, HeaderBack);
        var y = 4;
        foreach (var (text, colour) in lines) { canvas.Text(6, y, text, colour, ts, null); y += LineHeight * ts; }
        Legend(canvas, 6, y, ts, o.Layers);
        return canvas;
    }

    /// <summary>The colours and marks, on one line.</summary>
    private static void Legend(Canvas c, int x, int y, int ts, IReadOnlySet<string> layers)
    {
        void Swatch(string name, uint colour)
        {
            c.Fill(x, y, 10 * ts, 7 * ts, colour);
            c.Text(x + 12 * ts, y, name, White, ts, null);
            x += 12 * ts + Canvas.TextWidth(name, ts) + 8 * ts;
        }
        foreach (var k in new[] { "mob", "player", "pet", "drop", "effect", "damage", "unknown" }) Swatch(OverlayLabels.Title(k), KindColour(k));
        Swatch("tag box: its owner's", TagColour("none"));
        Swatch("tag: text pending", Pending);
        Swatch("own character", White);
        if (layers.Contains(OverlayLayers.World)) { Swatch("foothold", Foothold); Swatch("rope/ladder", Rope); Swatch("portal activation area", Portal); }
        c.Text(x, y, "mob box: client hitbox (lt/rb); #track name anim/frame facing score; [p] predicted, [t] transition, [s] search; ~carried (last match); ? unverified; + feet", Dim, ts, null);
    }

    /// <summary>Every entity, one line each, in its kind's colour: the label, then where it is and why it is what it is.</summary>
    private static Canvas Footer(OverlayFrame f, List<OverlayItem> items, int width, int ts)
    {
        var lines = ListLines(items.Where(i => i.Layer == OverlayLayers.Entity)).ToList();
        if (lines.Count == 0) lines.Add(("no entities" + (f.Skip is { } s ? $" (detector skipped: {s})" : f.Detect is { } d && d != "ok" ? $" ({d})" : ""), Dim));
        var canvas = new Canvas(width, 6 + lines.Count * LineHeight * ts, HeaderBack);
        var y = 4;
        foreach (var (text, colour) in lines) { canvas.Text(6, y, text, colour, ts, null); y += LineHeight * ts; }
        return canvas;
    }

    /// <summary>One line per entity: label, then rect, feet, world, foothold, age, confidence, evidence, rival, tag.</summary>
    public static IEnumerable<(string Text, uint Colour)> ListLines(IEnumerable<OverlayItem> entities)
    {
        foreach (var e in entities)
        {
            var parts = new List<string> { $"{e.Kind,-7} {e.Label}" };
            if (e.Rect is { } r) parts.Add($"{(e.Str("box") is { } bx ? bx : "rect")} {r.X},{r.Y},{r.W},{r.H}");
            if (e.Ints("blob") is [var bx0, var by0, var bw, var bh]) parts.Add($"blob {bx0},{by0},{bw},{bh}");
            if (e.Point("feet") is { } p) parts.Add($"feet {p.X},{p.Y}");
            if (e.Point("world") is { } w) parts.Add($"world {w.X},{w.Y}");
            if (e.Long("foothold") is { } fh) parts.Add($"fh{fh}");
            parts.Add($"age {e.Long("age")}");
            parts.Add($"conf {e.Double("confidence")?.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (e.Str("evidence") is { } ev) parts.Add(ev == "carried" ? $"carried {(e.Double("carriedFor") ?? 0).ToString("0.0", CultureInfo.InvariantCulture)} s since seq {e.Long("lastMatchedSeq")}" : ev);
            if (e.Str("state") is { } st) parts.Add(st);
            if (e.Bool("sweep")) parts.Add("found along the foothold");
            if (e.Str("found") is "prediction" or "transition") parts.Add($"found by {e.Str("found")}");
            if (e.Str("set") is { } set) parts.Add($"{set} {e.Str("digits")} a {e.Double("alpha")?.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (e.Str("basis") is { } b) parts.Add($"by {b}");
            if (e.Ints("agree") is [var m, var n]) parts.Add($"{m}/{n} px");
            if (e.Str("rivalName") is { } rn) parts.Add($"rival {rn} {e.Double("rivalScore")?.ToString("0.00", CultureInfo.InvariantCulture)}");
            if (e.Str("match") is { } mt) parts.Add($"also matched {mt}");
            if (e.Str("tag") is { } tag) parts.Add($"tag '{tag}'");
            if (e.Str("tagName") is { } tn) parts.Add($"tags name it {tn}");
            if (e.Bool("hpBar")) parts.Add("HP bar");
            yield return (string.Join("  ", parts), KindColour(e.Kind));
        }
    }
}
