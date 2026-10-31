using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OakSoul.Capture;

namespace OakSoul.Vision;

/// <summary>How confident the layout is about a region.</summary>
public enum RegionKind
{
    /// <summary>Always at this position in the native frame.</summary>
    Fixed,
    /// <summary>Expected here but not yet confirmed on a frame that shows it.</summary>
    Provisional,
    /// <summary>Moves every frame; the rectangle is only an example and the region must be located by a detector.</summary>
    Dynamic,
}

/// <summary>One attention region of the HUD, in native frame pixels with the origin at the top-left.</summary>
public sealed record HudRegion(int Id, string Name, RegionKind Kind, CaptureRect Rect, string Read)
{
    public int X => Rect.X;
    public int Y => Rect.Y;
    public int Width => Rect.Width;
    public int Height => Rect.Height;

    /// <summary>Copies this region's pixels out of a tightly packed BGRA frame into a new BGRA buffer.</summary>
    public byte[] Extract(ReadOnlySpan<byte> bgra, int frameWidth, int frameHeight)
    {
        CheckFits(frameWidth, frameHeight);
        var result = new byte[Width * Height * 4];
        for (var row = 0; row < Height; row++)
            bgra.Slice(((Y + row) * frameWidth + X) * 4, Width * 4).CopyTo(result.AsSpan(row * Width * 4));
        return result;
    }

    /// <summary>This region of a tightly packed BGRA frame, read in place: no copy, stride the frame's row.</summary>
    public BgraView View(ReadOnlySpan<byte> bgra, int frameWidth, int frameHeight)
    {
        CheckFits(frameWidth, frameHeight);
        var stride = frameWidth * 4;
        // An empty region's view has no rows to step over: its stride is its own (zero) row, so Row(y) on it is empty rather than a throw.
        if (Width == 0 || Height == 0) return new BgraView(ReadOnlySpan<byte>.Empty, Width, Height, Width * 4);
        return new BgraView(bgra.Slice(Y * stride + X * 4, (Height - 1) * stride + Width * 4), Width, Height, stride);
    }

    private void CheckFits(int frameWidth, int frameHeight)
    {
        if (X < 0 || Y < 0 || X + Width > frameWidth || Y + Height > frameHeight)
            throw new ArgumentException($"region {Name} {Rect} does not fit a {frameWidth}x{frameHeight} frame");
    }
}

/// <summary>
/// The set of HUD attention regions for one client layout. The source of truth is a JSON file embedded in this
/// assembly (Layouts/hud-1366x768.json), so the rectangles can be reviewed and edited without touching code.
/// </summary>
public sealed class HudLayout
{
    public string Name { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public IReadOnlyList<HudRegion> Regions { get; }

    private readonly Dictionary<string, HudRegion> _byName;

    private HudLayout(string name, int width, int height, IReadOnlyList<HudRegion> regions)
    {
        Name = name;
        FrameWidth = width;
        FrameHeight = height;
        Regions = regions;
        _byName = regions.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    // Declared before any static initializer that deserializes, since static fields initialize in textual order.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<HudLayout> _native1366x768 = new(() => LoadEmbedded("hud-1366x768.json", 1366, 768));

    /// <summary>The layout for the client running windowed at its native 1366x768, title bar cropped.</summary>
    public static HudLayout Native1366x768 => _native1366x768.Value;

    public HudRegion this[string name] =>
        _byName.TryGetValue(name, out var region) ? region : throw new KeyNotFoundException($"no HUD region named '{name}' in layout {Name}");

    public bool TryGet(string name, out HudRegion region) => _byName.TryGetValue(name, out region!);

    public IEnumerable<HudRegion> OfKind(RegionKind kind) => Regions.Where(r => r.Kind == kind);

    public static HudLayout LoadEmbedded(string resourceName, int frameWidth, int frameHeight)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"embedded layout '{resourceName}' not found");
        return Load(stream, Path.GetFileNameWithoutExtension(resourceName), frameWidth, frameHeight);
    }

    public static HudLayout LoadFile(string path, int frameWidth, int frameHeight)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileNameWithoutExtension(path), frameWidth, frameHeight);
    }

    private static HudLayout Load(Stream json, string name, int frameWidth, int frameHeight)
    {
        var file = JsonSerializer.Deserialize<LayoutFile>(json, JsonOptions)
            ?? throw new InvalidOperationException($"layout '{name}' is empty");
        var regions = file.Regions.Select(r => new HudRegion(
            r.Id, r.Name, r.Kind, new CaptureRect(r.Rect[0], r.Rect[1], r.Rect[2], r.Rect[3]), r.Read)).ToList();
        foreach (var r in regions)
        {
            if (r.Rect.Width <= 0 || r.Rect.Height <= 0 || r.X + r.Width > frameWidth || r.Y + r.Height > frameHeight)
                throw new InvalidOperationException($"layout '{name}': region {r.Name} {r.Rect} does not fit {frameWidth}x{frameHeight}");
        }
        return new HudLayout(name, frameWidth, frameHeight, regions);
    }

    private sealed record LayoutFile(string Frame, List<RegionEntry> Regions);
    private sealed record RegionEntry(int Id, string Name, RegionKind Kind, int[] Rect, string Read);
}
