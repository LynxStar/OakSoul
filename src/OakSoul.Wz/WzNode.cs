using System.Globalization;

namespace OakSoul.Wz;

/// <summary>What a <see cref="WzNode"/> holds.</summary>
public enum WzNodeType
{
    /// <summary>A plain container ("Property"); the image root is one of these.</summary>
    Property,
    Null,
    Short,
    Int,
    Long,
    Float,
    Double,
    String,
    /// <summary>"Shape2D#Vector2D": a point (X, Y).</summary>
    Vector,
    /// <summary>"Canvas": a bitmap; see <see cref="WzNode.Canvas"/> to decode it. May also have child properties.</summary>
    Canvas,
    /// <summary>"Shape2D#Convex2D": a list of child vectors.</summary>
    Convex,
    /// <summary>"UOL": a link to another node by relative path.</summary>
    Uol,
    /// <summary>"Sound_DX8": audio. Only its presence is recorded.</summary>
    Sound,
    /// <summary>An extended property whose type name this reader does not understand; see <see cref="WzNode.TypeName"/>.</summary>
    Unknown,
}

/// <summary>
/// One node of an image's property tree. Containers (Property, Canvas, Convex) have children;
/// leaves carry a typed value. Lookups return null when a child is missing, so chains read as
/// <c>image["info"]?["VRLeft"]?.ToInt()</c>, or use the helpers like <see cref="GetInt"/>.
/// </summary>
public sealed class WzNode
{
    private static readonly IReadOnlyList<WzNode> NoChildren = [];

    private List<WzNode>? _children;
    private Dictionary<string, WzNode>? _byName;
    private long _integer;
    private double _real;
    private string? _text;
    private int _x, _y;

    internal WzNode(string name, WzNodeType type, WzNode? parent)
    {
        Name = name;
        Type = type;
        Parent = parent;
    }

    public string Name { get; }
    public WzNodeType Type { get; }
    public WzNode? Parent { get; }

    /// <summary>For extended properties, the type name as stored ("Canvas", "UOL", ...). Null for primitives.</summary>
    public string? TypeName { get; private init; }

    public IReadOnlyList<WzNode> Children => _children ?? NoChildren;

    /// <summary>Slash-separated path from the image root (the root itself has an empty path).</summary>
    public string Path => Parent is null ? string.Empty
        : Parent.Parent is null ? Name
        : Parent.Path + "/" + Name;

    /// <summary>Child lookup by name, or by a slash-separated relative path such as "info/VRLeft".</summary>
    public WzNode? this[string path]
    {
        get
        {
            WzNode? node = this;
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = part == ".." ? node.Parent : node.Child(part);
                if (node is null)
                    return null;
            }
            return node;
        }
    }

    private WzNode? Child(string name)
    {
        if (_children is null)
            return null;
        if (_byName is null)
        {
            // Built on first lookup. Duplicate names are rare; the first occurrence wins.
            _byName = new Dictionary<string, WzNode>(_children.Count, StringComparer.Ordinal);
            foreach (var c in _children)
                _byName.TryAdd(c.Name, c);
        }
        return _byName.GetValueOrDefault(name);
    }

    // ---- typed values (throw when the node is not of that type) ----

    /// <summary>Value of a Short, Int or Long node.</summary>
    public long Integer => Type is WzNodeType.Short or WzNodeType.Int or WzNodeType.Long ? _integer : throw WrongType("an integer");
    public int Int => checked((int)Integer);
    public float Float => Type == WzNodeType.Float ? (float)_real : throw WrongType("a float");
    public double Double => Type is WzNodeType.Double or WzNodeType.Float ? _real : throw WrongType("a double");
    public string String => Type == WzNodeType.String ? _text! : throw WrongType("a string");
    public (int X, int Y) Vector => Type == WzNodeType.Vector ? (_x, _y) : throw WrongType("a vector");
    public (int Width, int Height) CanvasSize => Type == WzNodeType.Canvas ? (_x, _y) : throw WrongType("a canvas");
    /// <summary>Pixel format code of a Canvas as stored (without format2; see <see cref="WzCanvas.PixelFormat"/>).</summary>
    public int CanvasFormat => Type == WzNodeType.Canvas ? (int)_integer : throw WrongType("a canvas");

    /// <summary>The bitmap of a Canvas node, with its pixel block location, ready to <see cref="WzCanvas.Decode"/>.</summary>
    public WzCanvas Canvas => Type != WzNodeType.Canvas ? throw WrongType("a canvas")
        : CanvasInfo ?? throw new InvalidOperationException($"WZ node '{Path}' was parsed without an owning image, so its pixels cannot be read.");

    internal WzCanvas? CanvasInfo { get; set; }
    public string UolTarget => Type == WzNodeType.Uol ? _text! : throw WrongType("a UOL");

    public bool IsNumeric => Type is WzNodeType.Short or WzNodeType.Int or WzNodeType.Long or WzNodeType.Float or WzNodeType.Double;

    // ---- lenient conversions (WZ data sometimes stores numbers as strings and vice versa) ----

    /// <summary>Numeric value as an int: integers as-is, reals truncated, numeric strings parsed; otherwise <paramref name="fallback"/>.</summary>
    public int ToInt(int fallback = 0) => ToLong(fallback) is var v && v is >= int.MinValue and <= int.MaxValue ? (int)v : fallback;

    public long ToLong(long fallback = 0) => Type switch
    {
        WzNodeType.Short or WzNodeType.Int or WzNodeType.Long => _integer,
        WzNodeType.Float or WzNodeType.Double => (long)_real,
        WzNodeType.String when long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => fallback,
    };

    public double ToDouble(double fallback = 0) => Type switch
    {
        WzNodeType.Short or WzNodeType.Int or WzNodeType.Long => _integer,
        WzNodeType.Float or WzNodeType.Double => _real,
        WzNodeType.String when double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => fallback,
    };

    /// <summary>Strings as-is, numbers formatted invariantly, UOLs as their target; otherwise null.</summary>
    public string? ToStringValue() => Type switch
    {
        WzNodeType.String or WzNodeType.Uol => _text,
        WzNodeType.Short or WzNodeType.Int or WzNodeType.Long => _integer.ToString(CultureInfo.InvariantCulture),
        WzNodeType.Float or WzNodeType.Double => _real.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Int value of the node at a relative path, or <paramref name="fallback"/> when missing or not numeric.</summary>
    public int GetInt(string path, int fallback = 0) => this[path]?.ToInt(fallback) ?? fallback;

    public long GetLong(string path, long fallback = 0) => this[path]?.ToLong(fallback) ?? fallback;

    public double GetDouble(string path, double fallback = 0) => this[path]?.ToDouble(fallback) ?? fallback;

    /// <summary>String value of the node at a relative path (numbers converted), or <paramref name="fallback"/>.</summary>
    public string? GetString(string path, string? fallback = null) => this[path]?.ToStringValue() ?? fallback;

    /// <summary>
    /// Follows a UOL link (relative path, ".." meaning parent, starting from this node's parent), repeatedly
    /// if the target is itself a UOL. Returns this node if it is not a UOL, or null if the target does not exist.
    /// </summary>
    public WzNode? ResolveUol(int maxHops = 16)
    {
        WzNode? node = this;
        for (int hop = 0; node is { Type: WzNodeType.Uol } && hop < maxHops; hop++)
            node = node.Parent?[node._text!];
        return node is { Type: WzNodeType.Uol } ? null : node;
    }

    /// <summary>
    /// Follows frame links until a real node is reached: UOLs (see <see cref="ResolveUol"/>) and canvases carrying an
    /// "_inlink" string (a placeholder canvas, usually 1x1, whose pixels live at that path from the image root).
    /// Returns this node when it is not a link, or null when a target is missing or the link is an "_outlink" into
    /// another archive (not resolvable inside one image).
    /// </summary>
    public WzNode? ResolveLink(int maxHops = 16)
    {
        WzNode? node = this;
        for (int hop = 0; node is not null && hop < maxHops; hop++)
        {
            if (node.Type == WzNodeType.Uol)
                node = node.Parent?[node._text!];
            else if (node.Type == WzNodeType.Canvas && node["_inlink"] is { Type: WzNodeType.String } inlink)
                node = node.ImageRoot[inlink._text!];
            else if (node.Type == WzNodeType.Canvas && node["_outlink"] is { Type: WzNodeType.String })
                return null;
            else
                return node;
        }
        return null;
    }

    /// <summary>The root of the image this node belongs to.</summary>
    public WzNode ImageRoot
    {
        get
        {
            var node = this;
            while (node.Parent is not null)
                node = node.Parent;
            return node;
        }
    }

    public override string ToString() => Type switch
    {
        WzNodeType.Property or WzNodeType.Convex => $"{Name} [{Type}, {Children.Count} children]",
        WzNodeType.Null => $"{Name} = null",
        WzNodeType.Vector => $"{Name} = vector({_x}, {_y})",
        WzNodeType.Canvas => $"{Name} = canvas {_x}x{_y} (format {_integer}, {Children.Count} children)",
        WzNodeType.Uol => $"{Name} -> uol \"{_text}\"",
        WzNodeType.String => $"{Name} = \"{_text}\"",
        WzNodeType.Sound => $"{Name} = sound",
        WzNodeType.Unknown => $"{Name} = unknown extended type \"{TypeName}\"",
        _ => $"{Name} = {ToStringValue()} ({Type})",
    };

    private InvalidOperationException WrongType(string expected) =>
        new($"WZ node '{Path}' is {Type}, not {expected}.");

    // ---- construction, used by the image parser ----

    internal void Add(WzNode child) => (_children ??= []).Add(child);

    internal static WzNode Container(string name, WzNodeType type, WzNode? parent, string? typeName = null) =>
        new(name, type, parent) { TypeName = typeName };

    internal static WzNode OfInteger(string name, WzNodeType type, WzNode parent, long value) =>
        new(name, type, parent) { _integer = value };

    internal static WzNode OfReal(string name, WzNodeType type, WzNode parent, double value) =>
        new(name, type, parent) { _real = value };

    internal static WzNode OfString(string name, WzNode parent, string value) =>
        new(name, WzNodeType.String, parent) { _text = value };

    internal static WzNode OfVector(string name, WzNode parent, int x, int y) =>
        new(name, WzNodeType.Vector, parent) { TypeName = "Shape2D#Vector2D", _x = x, _y = y };

    internal static WzNode OfCanvas(string name, WzNode parent) =>
        new(name, WzNodeType.Canvas, parent) { TypeName = "Canvas" };

    internal void SetCanvas(int width, int height, int format)
    {
        _x = width;
        _y = height;
        _integer = format;
    }

    internal static WzNode OfUol(string name, WzNode parent, string target) =>
        new(name, WzNodeType.Uol, parent) { TypeName = "UOL", _text = target };
}
