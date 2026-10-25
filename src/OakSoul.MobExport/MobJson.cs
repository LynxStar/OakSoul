using System.Globalization;
using System.Text.Json.Nodes;
using OakSoul.Wz;

namespace OakSoul.MobExport;

/// <summary>Turns a mob image's property tree into the exported JSON shape.</summary>
internal static class MobJson
{
    /// <summary>Info fields that carry movement speed (used for the jump/fly summary).</summary>
    private static readonly string[] SpeedFields = ["speed", "flySpeed", "chaseSpeed"];

    /// <summary>
    /// Builds the mob's JSON. <paramref name="animationSource"/> is the image whose animations are exported: the mob's
    /// own image, or for a mob whose info/link points at another mob (and has no animations of its own) that mob's image.
    /// </summary>
    public static JsonObject Build(int id, string? name, WzImage image, WzImage? animationSource)
    {
        var root = image.Root;
        var info = root["info"];
        var animRoot = (animationSource ?? image).Root;
        var obj = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["entry"] = image.Path,
            ["info"] = info is null ? null : Convert(info),
        };
        string? link = info?["link"]?.ToStringValue();
        obj["link"] = link;
        obj["animationsFrom"] = animationSource is null || animationSource == image ? null : animationSource.Path;

        // Movement signals: which locomotion animations exist and which info fields look like speeds.
        var speedFields = new JsonObject();
        if (info is not null)
            foreach (var f in info.Children)
                if (SpeedFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase)
                    || f.Name.Contains("jump", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Contains("fly", StringComparison.OrdinalIgnoreCase))
                    speedFields[f.Name] = Convert(f);
        obj["movement"] = new JsonObject
        {
            ["mobility"] = Mobility(root, animRoot),
            ["hasJump"] = animRoot["jump"] is not null,
            ["hasFly"] = animRoot["fly"] is not null,
            ["hasMove"] = animRoot["move"] is not null,
            ["fields"] = speedFields,
        };

        var animations = new JsonObject();
        foreach (var child in AnimationNodes(animRoot))
            animations[child.Name] = Animation(child);
        obj["animations"] = animations;
        return obj;
    }

    /// <summary>Top-level children other than info: the animations (stand, move, jump, fly, hit1, die1, attack1, ...).</summary>
    public static IEnumerable<WzNode> AnimationNodes(WzNode root) => root.Children.Where(c => c.Name != "info");

    /// <summary>
    /// "fly" when the mob has a fly animation or an info/flySpeed, "jump" when it has a jump animation, "floor" when it
    /// has a move animation, else "static". <paramref name="animRoot"/> is the image holding the animations.
    /// </summary>
    public static string Mobility(WzNode root, WzNode animRoot) =>
        animRoot["fly"] is not null || root["info/flySpeed"] is not null ? "fly"
        : animRoot["jump"] is not null ? "jump"
        : animRoot["move"] is not null ? "floor"
        : "static";

    /// <summary>
    /// An animation node: numerically named children are frames (canvas or UOL link); anything else (an attack's
    /// info block, zigzag flags, ...) goes into "props".
    /// </summary>
    private static JsonObject Animation(WzNode anim)
    {
        var frames = new JsonArray();
        var props = new JsonObject();
        foreach (var child in anim.Children)
        {
            if (int.TryParse(child.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                frames.Add(Frame(index, child));
            else
                props[child.Name] = Convert(child);
        }
        var obj = new JsonObject { ["type"] = anim.Type.ToString(), ["frameCount"] = frames.Count };
        if (anim.Type == WzNodeType.Uol)
            obj["link"] = anim.UolTarget;
        obj["frames"] = frames;
        if (props.Count > 0)
            obj["props"] = props;
        return obj;
    }

    /// <summary>Internal bookkeeping children of a canvas that are not exported as frame properties.</summary>
    private static readonly HashSet<string> FrameSkip = ["origin", "delay", "_hash", "_inlink", "_outlink"];

    /// <summary>
    /// One frame. A frame is a link when it is a UOL ("uol", relative to the animation) or a placeholder canvas with
    /// "_inlink" (path from the image root) or "_outlink" (path into another archive). Size comes from the resolved
    /// canvas; origin and delay from the frame itself when present, else from the target.
    /// </summary>
    private static JsonObject Frame(int index, WzNode node)
    {
        var frame = new JsonObject { ["index"] = index };
        (string Kind, string Target)? link = node.Type == WzNodeType.Uol ? ("uol", node.UolTarget)
            : node["_inlink"] is { Type: WzNodeType.String } il ? ("inlink", il.String)
            : node["_outlink"] is { Type: WzNodeType.String } ol ? ("outlink", ol.String)
            : null;
        var target = node.ResolveLink();
        if (link is { } l)
            frame["link"] = new JsonObject { ["kind"] = l.Kind, ["target"] = l.Target, ["resolved"] = target?.Path };
        else
            frame["link"] = null;

        var own = node.Type == WzNodeType.Uol ? null : node;
        if (target is { Type: WzNodeType.Canvas })
        {
            var (w, h) = target.CanvasSize;
            frame["width"] = w;
            frame["height"] = h;
        }
        else if (own is { Type: WzNodeType.Canvas })
        {
            var (w, h) = own.CanvasSize;
            frame["width"] = w;
            frame["height"] = h;
        }
        else
            frame["nodeType"] = (target ?? node).Type.ToString();

        var originNode = own?["origin"] ?? target?["origin"];
        frame["origin"] = originNode is { Type: WzNodeType.Vector } o ? Vector(o) : null;
        frame["delay"] = (own?["delay"] ?? target?["delay"])?.ToInt();
        if (own?["delay"] is null && target?["delay"] is not null && target != own)
            frame["delayFromTarget"] = true;
        var props = new JsonObject();
        foreach (var c in (own ?? target)?.Children ?? [])
            if (!FrameSkip.Contains(c.Name))
                props[c.Name] = Convert(c);
        if (props.Count > 0)
            frame["props"] = props;
        return frame;
    }

    private static JsonObject Vector(WzNode v) => new() { ["x"] = v.Vector.X, ["y"] = v.Vector.Y };

    /// <summary>Generic typed conversion of a node (containers become objects).</summary>
    public static JsonNode? Convert(WzNode n)
    {
        switch (n.Type)
        {
            case WzNodeType.Short or WzNodeType.Int or WzNodeType.Long:
                return JsonValue.Create(n.Integer);
            case WzNodeType.Float or WzNodeType.Double:
                return JsonValue.Create(n.Double);
            case WzNodeType.String:
                return JsonValue.Create(n.String);
            case WzNodeType.Null:
                return null;
            case WzNodeType.Vector:
                return Vector(n);
            case WzNodeType.Uol:
                return new JsonObject { ["uol"] = n.UolTarget };
            case WzNodeType.Sound:
                return JsonValue.Create("sound");
            case WzNodeType.Unknown:
                return new JsonObject { ["unknownType"] = n.TypeName };
            case WzNodeType.Canvas:
            {
                var (w, h) = n.CanvasSize;
                var o = new JsonObject { ["canvas"] = $"{w}x{h}" };
                foreach (var c in n.Children)
                    o[c.Name] = Convert(c);
                return o;
            }
            default:
            {
                var o = new JsonObject();
                foreach (var c in n.Children)
                    o[c.Name] = Convert(c);
                return o;
            }
        }
    }
}
