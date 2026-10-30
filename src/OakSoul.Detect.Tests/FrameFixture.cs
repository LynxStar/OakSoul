using System.Text.Json;
using OakSoul.Capture;
using OakSoul.Vision;
using OakSoul.World;

namespace OakSoul.Detect.Tests;

/// <summary>One thing on a fixture frame: its track in the detector's stream, what it is, and a box around it (not pixel exact).</summary>
public sealed record Target(int Track, string Kind, string Name, CaptureRect Rect, string Source)
{
    /// <summary>The row the thing stands on: its box's bottom.</summary>
    public int Bottom => Rect.Y + Rect.Height;
}

/// <summary>
/// A recorded frame with what the detector needs to look at it the way it did: the frame's pixels, the map's background
/// and data, the registered camera, the screen mask (HUD, minimap panel, own character), and the targets, everything on
/// the frame. Loaded from a fixture file and the local recordings and data folders.
/// </summary>
public sealed class FrameFixture
{
    public required string Name { get; init; }
    public required byte[] Frame { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required MapData Map { get; init; }
    public required BackgroundMosaic Mosaic { get; init; }
    public required int CameraX { get; init; }
    public required int CameraY { get; init; }
    public required ScreenMask Mask { get; init; }
    public required IReadOnlyList<Target> Targets { get; init; }
    public required string Root { get; init; }

    /// <summary>The repository root (the folder holding OakSoul.slnx), from the test binaries up.</summary>
    public static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OakSoul.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>The fixture, or null when its recording, background or map data are not on this machine.</summary>
    public static FrameFixture? Load(string name)
    {
        var root = FindRoot();
        if (root is null) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")));
        var j = doc.RootElement;
        var keyframe = Path.Combine(root, j.GetProperty("keyframe").GetString()!);
        var mapId = j.GetProperty("mapId").GetInt32();
        var mapPath = Path.Combine(root, "data", "maps", mapId + ".json");
        if (!File.Exists(keyframe) || !File.Exists(mapPath)) return null;
        var mosaic = BackgroundMosaic.Load(Path.Combine(root, "data", "backgrounds"), mapId);
        if (mosaic is null) return null;
        var (frame, width, height) = Png.ReadBgra(keyframe);
        static CaptureRect Rect(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        // The mask as the detector builds it for the frame: the fixed HUD (the log left in), the minimap panel padded by 4, the
        // own character's box, and the log's text with its shadow.
        var layout = HudLayout.Native1366x768;
        var mask = new ScreenMask(layout, [EntityDetector.LogRegion]);
        mask.Exclude(Rect(j.GetProperty("minimapPanel")), 4);
        mask.Exclude(Rect(j.GetProperty("ownBox")));
        EntityDetector.MaskLogText(frame, width, mask, layout[EntityDetector.LogRegion].Rect, null);
        var camera = j.GetProperty("camera");
        return new FrameFixture
        {
            Name = name, Frame = frame, Width = width, Height = height, Map = MapData.Load(mapPath), Mosaic = mosaic,
            CameraX = camera[0].GetInt32(), CameraY = camera[1].GetInt32(), Mask = mask, Root = root,
            Targets = [.. j.GetProperty("targets").EnumerateArray().Select(t => new Target(t.GetProperty("track").GetInt32(), t.GetProperty("kind").GetString()!,
                t.GetProperty("name").GetString()!, Rect(t.GetProperty("rect")), t.GetProperty("source").GetString()!))],
        };
    }

    /// <summary>Every frame, both facings, of the map's ground mobs, named by species.</summary>
    public List<SliceMatcher.Candidate> MobCandidates() =>
        MobLibrary.Open(Path.Combine(Root, "data", "mobinfo")) is { } library
            ? [.. MapMobSet.Build(Map, new FootholdIndex(Map), library).Entries.Where(e => e.Species.Mobility != MobMobility.Fly)
                .SelectMany(e => e.Species.Frames.Select(f => new SliceMatcher.Candidate(e.Species.Name, f)))]
            : [];

    /// <summary>Every frame of every meso icon.</summary>
    public List<SliceMatcher.Candidate> MesoCandidates() =>
        SpriteSet.Mesos(Path.Combine(Root, "data", "sprites")) is { } mesos
            ? [.. mesos.Groups.Values.SelectMany(g => g.Frames).Select(f => new SliceMatcher.Candidate("meso", f))]
            : [];

    /// <summary>The narrowest visible width of any frame of the map's ground mobs (Deep Valley I: 35, the snails' move/0).</summary>
    public int NarrowestMob() =>
        MobLibrary.Open(Path.Combine(Root, "data", "mobinfo")) is { } library
            ? MapMobSet.Build(Map, new FootholdIndex(Map), library).Entries.Where(e => e.Species.Mobility != MobMobility.Fly)
                .SelectMany(e => e.Species.Frames).Select(f => f.ExtentWidth).DefaultIfEmpty(1).Min()
            : 1;

    /// <summary>The narrowest visible width of a meso icon's frames (5: the coin turned edge-on, iconRaw/3).</summary>
    public int NarrowestMeso() =>
        SpriteSet.Mesos(Path.Combine(Root, "data", "sprites")) is { } mesos
            ? mesos.Groups.Values.SelectMany(g => g.Frames).Select(f => f.ExtentWidth).DefaultIfEmpty(1).Min()
            : 1;
}
