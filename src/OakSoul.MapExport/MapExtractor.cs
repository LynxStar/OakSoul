using System.Globalization;
using System.Text.RegularExpressions;
using OakSoul.Wz;

namespace OakSoul.MapExport;

/// <summary>Turns a map image (<c>&lt;9-digit id&gt;.img</c>) into <see cref="MapData"/>.</summary>
internal static partial class MapExtractor
{
    /// <summary>Map id from an image name like "100000000.img", or null if the name is not a map.</summary>
    public static int? MapIdFromImageName(string name)
    {
        var m = MapImageName().Match(name);
        return m.Success ? int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Returns null when the image has no <c>info</c> node.</summary>
    public static MapData? Extract(int mapId, WzImage image, MapNames.Entry? names)
    {
        var root = image.Root;
        var info = root["info"];
        if (info is null)
            return null;

        var vr = new ViewRange(info.GetInt("VRLeft"), info.GetInt("VRTop"), info.GetInt("VRRight"), info.GetInt("VRBottom"));

        MiniMapInfo? miniMap = root["miniMap"] is { } mm
            ? new MiniMapInfo(mm.GetInt("centerX"), mm.GetInt("centerY"), mm.GetInt("mag"), mm.GetInt("width"), mm.GetInt("height"))
            : null;

        return new MapData(
            mapId,
            names?.StreetName,
            names?.MapName,
            info.GetInt("returnMap"),
            vr,
            miniMap,
            Footholds(root["foothold"]),
            LadderRopes(root["ladderRope"]),
            Portals(root["portal"]),
            Life(root["life"]),
            // info/cloud = 1: the client draws drifting clouds (Map.wz MapHelper.img weather/cloud) in front of the map.
            info.GetInt("cloud") == 1,
            Back(root["back"]));
    }

    /// <summary>back/&lt;n&gt;, in numeric order (the client's draw order).</summary>
    private static List<BackLayer> Back(WzNode? node) =>
        Ordered(node).Select(n => new BackLayer(
            ParseName(n), n.GetString("bS") ?? string.Empty, n.GetInt("no"), n.GetInt("ani") == 1,
            n.GetInt("x"), n.GetInt("y"), n.GetInt("rx"), n.GetInt("ry"), n.GetInt("cx"), n.GetInt("cy"),
            n.GetInt("type"), n.GetInt("a", 255), n.GetInt("front") == 1, n.GetInt("f") == 1)).ToList();

    /// <summary>foothold/&lt;layer&gt;/&lt;group&gt;/&lt;id&gt; with x1, y1, x2, y2, prev, next.</summary>
    private static List<Foothold> Footholds(WzNode? footholdRoot)
    {
        var list = new List<Foothold>();
        if (footholdRoot is null)
            return list;
        foreach (var layer in footholdRoot.Children)
            foreach (var group in layer.Children)
                foreach (var fh in group.Children)
                {
                    list.Add(new Foothold(
                        ParseName(fh), ParseName(layer), ParseName(group),
                        fh.GetInt("x1"), fh.GetInt("y1"), fh.GetInt("x2"), fh.GetInt("y2"),
                        fh.GetInt("prev"), fh.GetInt("next")));
                }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    private static List<LadderRope> LadderRopes(WzNode? node) =>
        Ordered(node).Select(n => new LadderRope(
            n.GetInt("x"), n.GetInt("y1"), n.GetInt("y2"), n.GetInt("l") == 1, n.GetInt("page"), n.GetInt("uf"))).ToList();

    private static List<Portal> Portals(WzNode? node) =>
        Ordered(node).Select(n => new Portal(
            n.GetString("pn") ?? string.Empty, n.GetInt("pt"), n.GetInt("x"), n.GetInt("y"),
            n.GetInt("tm", 999999999), n.GetString("tn") ?? string.Empty, n.GetString("script") ?? string.Empty)).ToList();

    private static List<Life> Life(WzNode? node) =>
        Ordered(node).Select(n => new Life(
            n.GetString("type") ?? string.Empty, n.GetString("id") ?? string.Empty,
            n.GetInt("x"), n.GetInt("y"), n.GetInt("fh"))).ToList();

    /// <summary>Children of a numbered list node (portal/0, portal/1, ...) in numeric order.</summary>
    private static IEnumerable<WzNode> Ordered(WzNode? node) =>
        node is null ? [] : node.Children.OrderBy(ParseName).ThenBy(c => c.Name, StringComparer.Ordinal);

    private static int ParseName(WzNode n) =>
        int.TryParse(n.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;

    [GeneratedRegex(@"^(\d{9})\.img$")]
    private static partial Regex MapImageName();
}
