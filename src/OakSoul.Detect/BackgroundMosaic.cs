using System.Text.Json;
using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// A map's static background in world space: for each world pixel the value the frames agreed on, and how much they
/// disagreed. Pixel (0, 0) is world (<see cref="OriginX"/>, <see cref="OriginY"/>), the top-left of the map's vr box,
/// which is everything the camera can show. Cached as <c>&lt;mapId&gt;.png</c> (RGBA, alpha 0 where never seen),
/// <c>&lt;mapId&gt;.mask.png</c> (greyscale disagreement, see <see cref="Disagreement"/>) and <c>&lt;mapId&gt;.json</c>.
/// </summary>
public sealed class BackgroundMosaic
{
    /// <summary>Disagreement value of a pixel no frame saw.</summary>
    public const byte Unseen = 255;

    /// <summary>Disagreement value of a pixel seen by fewer than <see cref="MosaicBuilder.MinSamples"/> frames.</summary>
    public const byte TooFewSamples = 254;

    /// <summary>
    /// Pixels over this disagreement (more than 25% of the camera positions that saw the pixel differ from the mode)
    /// are "unreliable" in the build report, and registration, which needs pixels that repeat exactly, skips them.
    /// </summary>
    public const byte UnreliableLevel = 64;

    /// <summary>
    /// Foreground extraction uses pixels up to this disagreement (45%). The histogram over Deep Valley I is bimodal
    /// with its valley at 40 to 50%: static tiles sit at 0 to 10%, the places mobs walk at 10 to 40% (the mode is still
    /// the background, which is all the difference needs), parallax layers, water and steam at 60% and above. Ignoring
    /// everything over 25% hid the mobs exactly where they walk: mob detections went from 2.5 to 4.1 per keyframe when
    /// this level was raised from 25% to 45%.
    /// </summary>
    public const byte ForegroundLevel = 114;

    public BackgroundMosaic(int mapId, int originX, int originY, int width, int height, uint[] pixels, byte[] disagreement, int frames)
    {
        MapId = mapId; OriginX = originX; OriginY = originY; Width = width; Height = height;
        Pixels = pixels; Disagreement = disagreement; Frames = frames;
        Usable = new byte[disagreement.Length];
        ForegroundUsable = new byte[disagreement.Length];
        for (var i = 0; i < disagreement.Length; i++)
        {
            Usable[i] = disagreement[i] <= UnreliableLevel ? (byte)1 : (byte)0;
            ForegroundUsable[i] = disagreement[i] <= ForegroundLevel ? (byte)1 : (byte)0;
        }
    }

    public int MapId { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public int Width { get; }
    public int Height { get; }
    public int Frames { get; }

    /// <summary>BGRA packed as b | g &lt;&lt; 8 | r &lt;&lt; 16 | a &lt;&lt; 24, row-major; alpha 0 where never seen.</summary>
    public uint[] Pixels { get; }

    /// <summary>
    /// Per pixel: round(253 * fraction of the frames that saw it whose value differed from the mode), so 0 is a
    /// pixel every frame agreed on; <see cref="TooFewSamples"/> and <see cref="Unseen"/> are sentinels.
    /// </summary>
    public byte[] Disagreement { get; }

    /// <summary>1 where the pixel is seen often enough and reliable, per <see cref="UnreliableLevel"/>: for registration.</summary>
    public byte[] Usable { get; }

    /// <summary>1 where the pixel's mode can serve as background for foreground extraction, per <see cref="ForegroundLevel"/>.</summary>
    public byte[] ForegroundUsable { get; }

    public bool Contains(int worldX, int worldY) => (uint)(worldX - OriginX) < (uint)Width && (uint)(worldY - OriginY) < (uint)Height;

    /// <summary>Coverage statistics: world pixels seen at all, seen often enough, reliable, and usable for foreground.</summary>
    public (long Total, long Seen, long Sampled, long Reliable, long ForegroundUsable) Stats()
    {
        long seen = 0, sampled = 0, reliable = 0, usable = 0;
        foreach (var d in Disagreement)
        {
            if (d != Unseen) seen++;
            if (d < TooFewSamples) sampled++;
            if (d <= UnreliableLevel) reliable++;
            if (d <= ForegroundLevel) usable++;
        }
        return ((long)Width * Height, seen, sampled, reliable, usable);
    }

    public static string PathFor(string dir, int mapId, string suffix) => Path.Combine(dir, $"{mapId}{suffix}");

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var bgra = new byte[Pixels.Length * 4];
        Buffer.BlockCopy(Pixels, 0, bgra, 0, bgra.Length);
        Png.WriteBgra(PathFor(dir, MapId, ".png"), bgra, Width, Height);
        Png.WriteGrey(PathFor(dir, MapId, ".mask.png"), Disagreement, Width, Height);
        var (total, seen, sampled, reliable, _) = Stats();
        var meta = new Meta(MapId, OriginX, OriginY, Width, Height, Frames, total, seen, sampled, reliable, UnreliableLevel, DateTime.UtcNow);
        File.WriteAllText(PathFor(dir, MapId, ".json"), JsonSerializer.Serialize(meta, MapDataJson));
    }

    /// <summary>Loads a cached mosaic, or null when there is none for this map.</summary>
    public static BackgroundMosaic? Load(string dir, int mapId)
    {
        var metaPath = PathFor(dir, mapId, ".json");
        if (!File.Exists(metaPath)) return null;
        var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), MapDataJson) ?? throw new InvalidDataException($"{metaPath}: empty");
        var (bgra, w, h) = Png.ReadBgra(PathFor(dir, mapId, ".png"));
        var (mask, mw, mh) = Png.ReadGrey(PathFor(dir, mapId, ".mask.png"));
        if (w != meta.Width || h != meta.Height || mw != w || mh != h) throw new InvalidDataException($"{metaPath}: image sizes disagree with the metadata");
        var pixels = new uint[w * h];
        Buffer.BlockCopy(bgra, 0, pixels, 0, bgra.Length);
        return new BackgroundMosaic(mapId, meta.OriginX, meta.OriginY, w, h, pixels, mask, meta.Frames);
    }

    private static readonly JsonSerializerOptions MapDataJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private sealed record Meta(int MapId, int OriginX, int OriginY, int Width, int Height, int Frames, long Total, long Seen, long Sampled, long Reliable, int UnreliableLevel, DateTime BuiltUtc);

}
