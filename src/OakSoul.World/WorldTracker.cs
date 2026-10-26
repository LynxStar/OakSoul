namespace OakSoul.World;

/// <summary>
/// What the readers saw in one frame, in native pixels: the OCR'd names, the minimap's map area and own dot (relative
/// to the area), the portal markers (relative to the area), the name tag's Feet point, and the frame time in seconds.
/// Any of them may be missing.
/// </summary>
/// <param name="NameWidensPanel">Whether the minimap panel may be as wide as it is for its name text rather than its map
/// (see MapNameTracker.NameWidensPanel); null when unknown. Only used to tell apart the maps a misread name may be.</param>
public sealed record WorldObservation(string? Street, string? MapName, PixelRect? MapArea, PointD? SelfInMap, PointD? TagFeet,
    IReadOnlyList<MinimapMarker>? PortalMarkers = null, bool MinimapCollapsed = false, double? Time = null, bool? NameWidensPanel = null);

/// <summary>Which inputs produced a <see cref="WorldState"/>.</summary>
public enum WorldSource
{
    /// <summary>No map or no position.</summary>
    None,
    /// <summary>Minimap dot and name tag: the camera is measured.</summary>
    DotAndTag,
    /// <summary>Minimap dot only: the camera is predicted from the anchor, the walking lag and the clamping.</summary>
    Dot,
    /// <summary>Name tag only: the last measured camera is held.</summary>
    TagHeldCamera,
    /// <summary>Neither: the last state is held.</summary>
    Held,
}

/// <summary>
/// The tracker's answer for one frame. <see cref="Position"/> is where the character stands (world units, soles);
/// <see cref="RawPosition"/> is the same before it was snapped onto a foothold or rope. <see cref="Confidence"/> is a
/// heuristic in [0, 1]: 0.9 measured and snapped (or clamped on both axes), 0.75 measured, lower when the height came
/// from portals, the foothold chain or a held camera, 0.45 dot only, 0.5 and decaying with a held camera.
/// </summary>
public sealed record WorldState(int? MapId, MapData? Map, PointD? Position, PointD? RawPosition, PointD? Camera, FootholdHit? Foothold,
    LadderHit? Ladder, double Confidence, WorldSource Source, MinimapFix? Dot, MinimapTransform? Transform, string? Note = null)
{
    public static readonly WorldState Empty = new(null, null, null, null, null, null, null, 0, WorldSource.None, null, null);

    /// <summary>"fh123", "rope@86", "air" or "?"; for printing.</summary>
    public string Support => Ladder is { } l ? $"{l.Ladder.Kind}@{l.Ladder.X}" : Foothold is { } f ? $"fh{f.Foothold.Id}" : Position is null ? "?" : "air";
}

/// <summary>
/// Per frame, combines the minimap dot, the name-tag feet and the map metadata into the map id, the character's
/// world position, the camera, what the character stands on and a confidence.
/// <list type="bullet">
/// <item>Map: resolved from the OCR'd street and map names through index.json; same-named maps are told apart by how
/// well each explains the minimap's size. The names lag a map change by the OCR stability gate while the panel
/// already shows the new map, so when the map area changes size the map is treated as unknown until the names
/// change (or, for a genuine resize, until the new size has held for <see cref="ResizeAcceptFrames"/> frames and
/// fits the map).</item>
/// <item>Dot and tag: camera = dot world - screen soles, clamped to the bounds. The dot is good to +-8 units (one
/// minimap pixel is 16), so when a foothold surface lies within that cell under the character the position is snapped
/// onto it (and onto a rope's x when one is that close), which pins the camera to the pixel.</item>
/// <item>Tall maps show the minimap through a window centred on the player, so the dot gives no height while the
/// window is away from the image's ends. The height then comes from the portal markers in the window (+-24), else by
/// walking the foothold chain from the last foothold to the new x, else from the held camera (+-48), each snapped.</item>
/// <item>Tag missing: the camera is predicted from the dot through the anchor, the walking lag and the clamping.</item>
/// <item>Dot missing (minimap collapsed or unread): the last measured camera is held and the tag gives the position.</item>
/// </list>
/// </summary>
public sealed class WorldTracker
{
    /// <summary>Extra units around the dot's cell searched for a surface to snap onto: rounding of the dot and sloped footholds.</summary>
    public const double SnapSlack = 6;

    /// <summary>A standing position may sit this far above or below a surface and still count as on it.</summary>
    public const double StandTolerance = 6;

    /// <summary>Half-height searched for a surface when the height comes from a held camera, not the dot.</summary>
    public const double HeldSnapRange = 48;

    /// <summary>A map-area size held this many frames without a name change is accepted as a resize within the map.</summary>
    public const int ResizeAcceptFrames = 8;

    /// <summary>
    /// A change of more than this many pixels in the map area's width or height counts as a different panel. Zero: on
    /// both fixtures the reader's area never changed within a map, and Rocky Road II and III differ by two rows.
    /// </summary>
    public const int AreaChangeTolerance = 0;

    /// <summary>
    /// How long (seconds of observation time, or frames when there is none) a height from the foothold chain or the held
    /// camera may stand in for the minimap's: the camera follows the player's height, so holding it goes stale quickly.
    /// </summary>
    public const double HeightFallbackSeconds = 2.0;

    /// <summary>Horizontal speed (world units per second) above which the player counts as walking for camera prediction.</summary>
    public const double WalkSpeed = 60;

    private readonly MapCatalog _catalog;
    private (string Street, string Map)? _names;
    private MapData? _map;
    private FootholdIndex? _index;
    private CameraModel? _camera;
    private MinimapTransform? _transform;
    private PixelRect? _establishedArea;
    private PixelRect? _pendingArea;
    private int _pendingFrames;
    private PointD? _lastCamera;
    private int _heldFrames;
    private double _lastHeightTime;
    private int _framesSinceHeight;
    private readonly Queue<(double Time, double X)> _motion = new();
    private WorldState _last = WorldState.Empty;

    public WorldTracker(MapCatalog catalog) => _catalog = catalog;

    public MapData? Map => _map;
    public FootholdIndex? Index => _index;
    public CameraModel? CameraModel => _camera;
    public WorldState Current => _last;

    /// <summary>-1 walking left, +1 walking right, 0 otherwise, from the dot's motion over the last half second or more.</summary>
    public int Walking { get; private set; }

    public WorldState Update(WorldObservation obs)
    {
        ResolveMap(obs);
        if (_map is null || _index is null || _camera is null) return _last = WorldState.Empty;
        if (!AreaTrusted(obs)) return _last = WorldState.Empty with { Note = "minimap changed size; waiting for the map name" };

        if (obs.MapArea is { Width: > 0, Height: > 0 } area && (_transform is null || _transform.MapArea != area))
            _transform = MinimapTransform.Create(_map, area);

        MinimapFix? fix = null;
        if (obs.SelfInMap is { } self && _transform is not null && !obs.MinimapCollapsed)
        {
            double? windowY = null;
            if (_transform.Vertical.Mode == MinimapAxisMode.Window && obs.PortalMarkers is { Count: > 0 } markers)
                windowY = _transform.EstimateWindow(markers)?.Window;
            fix = _transform.ToWorld(self, null, windowY);
            if (fix.Value.X is { } fx && obs.Time is { } t) TrackMotion(t, fx);
        }
        _framesSinceHeight++;
        if (fix?.Y is not null) { _lastHeightTime = obs.Time ?? 0; _framesSinceHeight = 0; }
        _fallbackAllowed = obs.Time is { } now ? now - _lastHeightTime <= HeightFallbackSeconds : _framesSinceHeight <= HeightFallbackSeconds;
        var soles = obs.TagFeet is { } feet ? CameraModel.SolesFromTag(feet) : (PointD?)null;

        WorldState state;
        if (fix is { X: { } dotX } && soles is { } s)
            state = Measure(fix.Value, dotX, s);
        else if (fix is { X: { } onlyX })
        {
            // Dot only: predict the camera from the anchor, the walking lag and the clamping.
            var height = fix.Value.Y is { } y ? (y, fix.Value.ErrorY) : HeightWithoutDot(onlyX, null);
            if (height is not { } h) state = Unknown(fix, "height unknown: minimap window centred, no portal marker, no foothold to follow");
            else
            {
                var raw = new PointD(onlyX, h.Y);
                var world = Snap(raw, h.Error) is { } surface ? new PointD(raw.X, surface.SurfaceY) : raw;
                state = Describe(world, raw, _camera.Predict(world, Walking), 0.45, WorldSource.Dot, fix);
            }
        }
        else if (soles is { } s2 && _lastCamera is { } heldCamera)
        {
            // Tag only: hold the last measured camera.
            _heldFrames++;
            var world = CameraModel.ScreenToWorld(heldCamera, s2);
            state = Describe(world, world, heldCamera, Math.Max(0.1, 0.5 - 0.05 * _heldFrames), WorldSource.TagHeldCamera, fix);
        }
        else
        {
            _heldFrames++;
            state = _last.MapId == _map.MapId
                ? _last with { Source = WorldSource.Held, Confidence = Math.Max(0, _last.Confidence - 0.1), Dot = fix }
                : Unknown(fix, null);
        }
        return _last = state;
    }

    /// <summary>
    /// Feeds back a pixel-exact camera for the current frame (registered against the map's background by the entity
    /// detector). The camera becomes the one held for later frames; with the name tag's Feet point the own position
    /// becomes camera + soles, which the minimap dot only gives to +-8 units. Returns the refined state (unchanged
    /// when the map is unknown).
    /// </summary>
    public WorldState Refine(PointD camera, PointD? tagFeet)
    {
        if (_map is null || _index is null || _last.MapId != _map.MapId) return _last;
        _lastCamera = camera;
        if (tagFeet is not { } feet) return _last = _last with { Camera = camera };
        var world = CameraModel.ScreenToWorld(camera, CameraModel.SolesFromTag(feet));
        var foothold = _index.StandingOn(world, StandTolerance);
        var ladder = foothold is null ? _index.LadderAt(world, 4) : null;
        return _last = _last with { Position = world, Camera = camera, Foothold = foothold, Ladder = ladder, Confidence = Math.Max(_last.Confidence, 0.95) };
    }

    /// <summary>Dot and tag: measure the camera, then snap onto a surface or rope within the dot's uncertainty.</summary>
    private WorldState Measure(MinimapFix fix, double dotX, PointD soles)
    {
        double y, errY;
        var penalty = 0.0;
        if (fix.Y is { } dotY) { y = dotY; errY = fix.ErrorY; if (errY > fix.ErrorX) penalty = 0.15; }
        else if (HeightWithoutDot(dotX, soles) is { } h) { (y, errY) = h; penalty = 0.25; }
        else return Unknown(fix, "height unknown: minimap window centred, no portal marker, no foothold to follow");

        var camera = _camera!.FromFeet(new PointD(dotX, y), soles);
        var rawWorld = CameraModel.ScreenToWorld(camera, soles);
        var (clampX, clampY) = _camera.IsClamped(camera);
        var world = rawWorld;
        var snapped = false;
        if (!clampY && Snap(world, errY) is { } surface)
        {
            camera = _camera.Clamp(new PointD(camera.X, surface.SurfaceY - soles.Y));
            world = CameraModel.ScreenToWorld(camera, soles);
            snapped = true;
        }
        if (!clampX && _index!.StandingOn(world, StandTolerance) is null && _index.LadderAt(world, fix.ErrorX + 2) is { } ladder)
        {
            camera = _camera.Clamp(new PointD(ladder.Ladder.X - soles.X, camera.Y));
            world = CameraModel.ScreenToWorld(camera, soles);
            snapped = true;
        }
        _lastCamera = camera;
        _heldFrames = 0;
        var confidence = (snapped || (clampX && clampY) ? 0.9 : 0.75) - penalty;
        return Describe(world, rawWorld, camera, confidence, WorldSource.DotAndTag, fix);
    }

    /// <summary>
    /// The height when the minimap cannot give it: walk the foothold chain from the last foothold to the new x, else
    /// take the held camera's height for the screen soles, else the last height.
    /// </summary>
    private bool _fallbackAllowed;

    private (double Y, double Error)? HeightWithoutDot(double x, PointD? soles)
    {
        if (!_fallbackAllowed) return null;
        // The dot at the window centre still says the window is away from both ends, which bounds the height.
        var band = _transform?.CentredBand() ?? (double.MinValue, double.MaxValue);
        bool Inside(double y) => y >= band.Min && y <= band.Max;
        if (_last.MapId == _map!.MapId && _last.Foothold is { } last && _index!.Follow(last.Foothold, x, last.SurfaceY) is { } followed && Inside(followed.SurfaceY))
            return (followed.SurfaceY, StandTolerance);
        if (soles is { } s && _lastCamera is { } held && Inside(held.Y + s.Y)) return (held.Y + s.Y, HeldSnapRange);
        if (_last.MapId == _map.MapId && _last.Position is { } p && Inside(p.Y)) return (p.Y, HeldSnapRange);
        return null;
    }

    private void TrackMotion(double time, double x)
    {
        _motion.Enqueue((time, x));
        while (_motion.Count > 2 && time - _motion.ElementAt(1).Time >= 0.5) _motion.Dequeue();
        var (t0, x0) = _motion.Peek();
        var dt = time - t0;
        if (dt < 0.25) return;
        var v = (x - x0) / dt;
        Walking = v > WalkSpeed ? 1 : v < -WalkSpeed ? -1 : 0;
    }

    /// <summary>The surface nearest the estimate within its uncertainty, where the character would be standing.</summary>
    private FootholdHit? Snap(PointD world, double errorY)
    {
        var range = errorY + SnapSlack;
        return _index!.SurfacesBetween(world.X, world.Y - range, world.Y + range, world.Y) is [var first, ..] ? first : null;
    }

    private WorldState Describe(PointD world, PointD raw, PointD camera, double confidence, WorldSource source, MinimapFix? fix)
    {
        var foothold = _index!.StandingOn(world, StandTolerance);
        var ladder = foothold is null ? _index.LadderAt(world, 4) : null;
        return new WorldState(_map!.MapId, _map, world, raw, camera, foothold, ladder, confidence, source, fix, _transform);
    }

    private WorldState Unknown(MinimapFix? fix, string? note) =>
        WorldState.Empty with { MapId = _map!.MapId, Map = _map, Dot = fix, Transform = _transform, Note = note };

    /// <summary>
    /// The one candidate whose predicted minimap image explains the area within <see cref="MinimapTransform.FitTolerance"/>
    /// by <see cref="MinimapTransform.StrictSizeMismatch"/> and better than every other; null without an area, when none
    /// fits, or when two fit equally well (East Rocky Mountain I and IV share a size). An image narrower than the area
    /// fits only when the panel may have been widened for the name.
    /// </summary>
    private int? ChooseBySize(IReadOnlyList<int> ids, PixelRect? mapArea, bool narrowerAllowed)
    {
        if (mapArea is not { Width: > 0, Height: > 0 } area) return null;
        var fits = ids.Select(i => (Id: i, Map: _catalog.Load(i))).Where(e => e.Map is not null)
            .Select(e => (e.Id, Gap: MinimapTransform.StrictSizeMismatch(e.Map!, area, narrowerAllowed)))
            .Where(e => e.Gap <= MinimapTransform.FitTolerance).OrderBy(e => e.Gap).ToList();
        return fits.Count == 0 || fits.Count > 1 && fits[1].Gap == fits[0].Gap ? null : fits[0].Id;
    }

    /// <summary>False while the map area's size disagrees with the one seen when the map was named (see class remarks).</summary>
    private bool AreaTrusted(WorldObservation obs)
    {
        if (obs.MapArea is not { Width: > 0, Height: > 0 } area || obs.MinimapCollapsed) return true;
        if (_establishedArea is not { } established) { _establishedArea = area; return true; }
        if (Math.Abs(area.Width - established.Width) <= AreaChangeTolerance && Math.Abs(area.Height - established.Height) <= AreaChangeTolerance)
        {
            _pendingArea = null;
            return true;
        }
        if (_pendingArea == area) _pendingFrames++;
        else { _pendingArea = area; _pendingFrames = 1; }
        if (_pendingFrames >= ResizeAcceptFrames && MinimapTransform.SizeMismatch(_map!, area) <= MinimapTransform.FitTolerance)
        {
            _establishedArea = area;
            _pendingArea = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Names that matched no map exactly, with the maps they may be (see <see cref="MapCatalog.FindCandidates"/>), while
    /// none of those fits the minimap: re-tried every frame, since the panel may only settle after the names were read.
    /// </summary>
    private IReadOnlyList<int>? _nearCandidates;

    private void ResolveMap(WorldObservation obs)
    {
        if (string.IsNullOrWhiteSpace(obs.MapName)) return;
        var names = (obs.Street ?? "", obs.MapName);
        var fresh = _names != names;
        if (!fresh && _nearCandidates is null) return;
        _names = names;
        int? id;
        if (fresh)
        {
            var (ids, exact) = _catalog.FindCandidates(names.Item1, names.Item2);
            _nearCandidates = exact || ids.Count == 0 ? null : ids;
            id = !exact ? null : ids.Count switch
            {
                1 => ids[0],
                _ => obs.MapArea is { Width: > 0 } area
                    ? ids.Select(i => (Id: i, Map: _catalog.Load(i))).Where(e => e.Map is not null)
                        .OrderBy(e => MinimapTransform.SizeMismatch(e.Map!, area)).Select(e => (int?)e.Id).FirstOrDefault()
                    : ids[0],
            };
        }
        else id = null;
        // A misread name is never taken on its closest match alone: the candidate must be the one map whose size explains
        // the minimap, so with the minimap collapsed or no candidate fitting, the map stays unknown and is tried again.
        if (_nearCandidates is { } near)
        {
            id = ChooseBySize(near, obs.MinimapCollapsed ? null : obs.MapArea, obs.NameWidensPanel != false);
            if (id is not null) _nearCandidates = null;
            else if (!fresh) return;
        }
        // The names were just read, so the panel on screen now belongs to them.
        _establishedArea = obs.MapArea is { Width: > 0 } a ? a : null;
        _pendingArea = null;
        var map = id is { } i2 ? _catalog.Load(i2) : null;
        if (map?.MapId == _map?.MapId) return;
        _map = map;
        _index = map is null ? null : new FootholdIndex(map);
        _camera = map is null ? null : new CameraModel(map.Vr);
        _transform = null;
        _lastCamera = null;
        _heldFrames = 0;
        _motion.Clear();
        Walking = 0;
        _lastHeightTime = double.MinValue / 2;
        _framesSinceHeight = int.MaxValue / 2;
        _last = WorldState.Empty;
    }
}
