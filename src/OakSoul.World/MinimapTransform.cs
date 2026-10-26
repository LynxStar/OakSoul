namespace OakSoul.World;

/// <summary>
/// How the HUD minimap places the map's canvas in the detected map area (open question 1).
/// </summary>
public enum MinimapModel
{
    /// <summary>The whole vr box stretched to fill the area, with an independent scale per axis.</summary>
    Stretch,

    /// <summary>The whole vr box scaled uniformly to fit inside the area and centred on the spare axis.</summary>
    Letterbox,

    /// <summary>
    /// A fixed scale of 4 * mag world units per HUD pixel. An axis whose image is smaller than the area is centred
    /// in it; an axis whose image is larger is shown through a window of the area's size that is centred on the
    /// player and clamped to the image. This is the model the fixtures support; see <see cref="MinimapTransform"/>.
    /// </summary>
    FixedScale,
}

/// <summary>How one axis of the canvas maps into the area.</summary>
public enum MinimapAxisMode
{
    /// <summary>The image fills the area along this axis (within a pixel).</summary>
    Fit,
    /// <summary>The image is narrower than the area and centred in it.</summary>
    Centered,
    /// <summary>The image is larger than the area; a window of the area's size scrolls with the player.</summary>
    Window,
}

/// <summary>One axis of a minimap transform. Image pixels count from the canvas origin (world = Origin).</summary>
public readonly record struct MinimapAxis(MinimapAxisMode Mode, double WorldPerPixel, double Origin, double Full, int Visible, double Offset, double AreaOrigin)
{
    /// <summary>World coordinate to (fractional) canvas image pixel.</summary>
    public double ToImage(double world) => (world - Origin) / WorldPerPixel;

    /// <summary>Canvas image pixel to world coordinate.</summary>
    public double ToWorld(double image) => Origin + image * WorldPerPixel;

    /// <summary>
    /// The window position (image pixel shown at the area's first visible pixel) when the player is at
    /// <paramref name="selfImage"/>: centred on the player and clamped, or 0 when the axis is not windowed.
    /// </summary>
    public double WindowFor(double selfImage) => Mode != MinimapAxisMode.Window ? 0 : Math.Clamp(selfImage - Visible / 2.0, 0, Full - Visible);

    /// <summary>The largest window position.</summary>
    public double MaxWindow => Mode == MinimapAxisMode.Window ? Full - Visible : 0;
}

/// <summary>A marker blob on the minimap: centroid relative to MapArea and its pixel count.</summary>
public readonly record struct MinimapMarker(PointD Position, int Pixels);

/// <summary>The player's world position as read from the minimap: per axis a value and a half-width of uncertainty, or unknown.</summary>
public readonly record struct MinimapFix(double? X, double? Y, double ErrorX, double ErrorY)
{
    public bool Complete => X.HasValue && Y.HasValue;
    public PointD? Point => Complete ? new PointD(X!.Value, Y!.Value) : null;
    public override string ToString() => $"{(X is { } x ? $"{x:0}" : "?")},{(Y is { } y ? $"{y:0}" : "?")} (+-{ErrorX:0},{ErrorY:0})";
}

/// <summary>
/// Converts between minimap dot positions (relative to the reader's <c>MapArea</c>, native pixels) and world
/// coordinates, for one map and one detected map area.
///
/// Model selection (open question 1), from the fx-maps and fx-combat keyframes:
/// <list type="bullet">
/// <item>The visible map image is not the reader's MapArea: it starts one column left of it and two rows below it,
/// and is 2 wider and 5 shorter (measured on Deep Valley I, image columns 6..186 and rows 72..207 against MapArea
/// 7,70,179,141; the same insets give the image edges on Perion rows 72..183 and East Domain of Perion columns 6..177).</item>
/// <item>On every map whose image fits, its size is floor(vr / 16) on both axes: Deep Valley I 181x136 (vr 2906x2189),
/// Rocky Road I 131x114, Rocky Road III 131x115, Perion Street Corner 243x85, and the heights of East Rocky Mountain I
/// (128), The Tree That Grew I (119) and The Field Up North of Ellinia (131). So the scale is uniform and fixed at
/// 16 world units per pixel, which is 4 * mag (every map seen has mag 4, so a fixed 16 fits equally well).</item>
/// <item>A map narrower than the panel (whose width follows the name text) is centred: East Domain of Perion's
/// 142-pixel image sits 15 pixels in from both sides of its 172-pixel area.</item>
/// <item>A map taller than the panel allows (Perion, East Domain of Perion, The Forest North of Ellinia, Ellinia;
/// all shown 112 pixels tall) is a window, not a squeeze: portal markers keep the 16-unit spacing and sit at a
/// common vertical offset (Perion: three portals agree on a window top of 63.1 to 63.2 pixels), and the own dot stays
/// at the window centre while the player walks unless the window is at the image's top or bottom.</item>
/// </list>
/// Stretch and Letterbox are kept for comparison; the watcher's world report scores all three against portal markers.
/// </summary>
public sealed class MinimapTransform
{
    /// <summary>The visible image relative to the reader's MapArea: x - 1, y + 2, width + 2, height - 5 (see class remarks).</summary>
    public const int AreaInsetLeft = -1, AreaInsetTop = 2, AreaExtraWidth = 2, AreaExtraHeight = -5;

    /// <summary>
    /// The own dot is drawn at floor(position) + (0, -6) image pixels, and the reader's centroid of that drawn dot is
    /// rounded to undo threshold wobble (21..23-pixel blobs centre a quarter pixel off; 20 and 24 are symmetric).
    /// Measured on fx-combat: of the 101 keyframes whose camera came within a cell of vr.left, floor((feetX) / 16)
    /// equalled the rounded dot x (in image pixels) on 94, and the other 7, all while jumping or walking, were one cell
    /// off; at vr.right 54 of 61. Of 85 keyframes standing still off ropes, 75 had the foothold under the dot 5.96 to
    /// 6.94 pixels below the dot row (a one-pixel cell starting 6 below); the other 10 stood on the far-left slope.
    /// </summary>
    public const double SelfDotOffsetX = 0, SelfDotOffsetY = -6;

    /// <summary>
    /// Portal markers are rings whose teal centroid sits about (-1, -8.5) image pixels from the portal's position.
    /// Measured on Deep Valley I's two visible portals (east00 -1.0, -9.0; west00 -1.0, -7.9) and Perion's four.
    /// </summary>
    public const double PortalDotOffsetX = -1, PortalDotOffsetY = -8.5;

    /// <summary>
    /// A portal ring split into two blobs by the colour threshold (Deep Valley I: 20 + 9 pixels, The Forest North of
    /// Ellinia: 15 + 7) is merged when the blobs lie within this many pixels; a merged ring under
    /// <see cref="WholeMarkerPixels"/> is clipped by the window edge and its centroid is biased, so it is not used.
    /// Whole rings measured 23 to 30 pixels; clipped ones at the top of East Domain of Perion's window 8 to 17.
    /// </summary>
    public const double MarkerMergeDistance = 6;
    public const int WholeMarkerPixels = 20;

    /// <summary>
    /// Uncertainty of a height read through a window located from portal markers: single markers sit up to 1.5 pixels
    /// from the common offset (Deep Valley I -9.0 and -7.9; The Forest North of Ellinia's east00 put the bottom-clamped
    /// window 1.5 pixels high).
    /// </summary>
    public const double PortalWindowError = 24;

    /// <summary>Portal types drawn on the minimap: 2 (regular) and 7 (market). Types 0 (spawn), 1 (hidden) and 6 are not drawn.</summary>
    public static readonly IReadOnlySet<int> DrawnPortalTypes = new HashSet<int> { 2, 7 };

    /// <summary>Canvas pixels per HUD pixel: the HUD shows the canvas at a quarter size, 16 world units per pixel on mag-4 maps (see class remarks).</summary>
    public const double CanvasPixelsPerHudPixel = 4;

    /// <summary>A size difference up to this many pixels still counts as the image filling the area.</summary>
    public const double FitTolerance = 1.5;

    public MinimapModel Model { get; }
    public MinimapAxis Horizontal { get; }
    public MinimapAxis Vertical { get; }
    public PixelRect MapArea { get; }
    public MapData Map { get; }

    private MinimapTransform(MinimapModel model, MapData map, PixelRect mapArea, MinimapAxis horizontal, MinimapAxis vertical)
    {
        Model = model;
        Map = map;
        MapArea = mapArea;
        Horizontal = horizontal;
        Vertical = vertical;
    }

    /// <summary>The visible image size inside a reader's MapArea.</summary>
    public static (int Width, int Height) VisibleSize(PixelRect mapArea) => (mapArea.Width + AreaExtraWidth, mapArea.Height + AreaExtraHeight);

    /// <summary>The full image size the FixedScale model predicts for a map, in HUD pixels.</summary>
    public static (double Width, double Height) PredictedImageSize(MapData map)
    {
        var s = WorldPerPixel(map);
        return (Math.Floor(map.Vr.Width / s), Math.Floor(map.Vr.Height / s));
    }

    public static double WorldPerPixel(MapData map) => CanvasPixelsPerHudPixel * (map.MiniMap?.Mag is > 0 and var mag ? mag : 4);

    /// <summary>
    /// How well the FixedScale model explains a detected area: the larger per-axis gap between the predicted image
    /// and the visible area on axes the model says should fit. Zero to one pixel for the right map; used to tell
    /// same-named maps apart and to accept a panel resize within a map.
    /// </summary>
    public static double SizeMismatch(MapData map, PixelRect mapArea)
    {
        var (pw, ph) = PredictedImageSize(map);
        var (vw, vh) = VisibleSize(mapArea);
        // Wider than the image is allowed horizontally (the panel widens for a long name and centres the image);
        // larger than the area is allowed on either axis (windowed). Anything else should match within a pixel.
        var gapX = pw > vw + FitTolerance || pw < vw ? 0 : Math.Abs(pw - vw);
        var gapY = ph > vh + FitTolerance ? 0 : Math.Abs(ph - vh);
        return Math.Max(gapX, gapY);
    }

    /// <summary>The visible height of a map taller than the panel allows: 112 rows on every such map seen (see class remarks).</summary>
    public const int WindowHeight = 112;

    /// <summary>
    /// How well a map explains a detected area when its name was misread and several maps are candidates. Stricter than
    /// <see cref="SizeMismatch"/>: an image wider than the area counts its whole gap, and so does a narrower one unless
    /// <paramref name="narrowerAllowed"/> (the panel may have been widened for the name, which centres the image); a taller
    /// image fits only when the area shows the full <see cref="WindowHeight"/>, and a shorter one never. Ant Tunnel IV's
    /// 219x172 image explains fx-anttunnel-open's 219x112 visible area with 0; Ant Tunnel I, II and III (202x153, 180x150,
    /// 225x147) are 17, 39 and 6 pixels off there, where the name is far too short to have widened the panel.
    /// </summary>
    public static double StrictSizeMismatch(MapData map, PixelRect mapArea, bool narrowerAllowed = false)
    {
        var (pw, ph) = PredictedImageSize(map);
        var (vw, vh) = VisibleSize(mapArea);
        var gapX = pw < vw && narrowerAllowed ? 0 : Math.Abs(pw - vw);
        var gapY = ph > vh && Math.Abs(vh - WindowHeight) <= FitTolerance ? 0 : Math.Abs(ph - vh);
        return Math.Max(gapX, gapY);
    }

    public static MinimapTransform Create(MapData map, PixelRect mapArea, MinimapModel model = MinimapModel.FixedScale)
    {
        var (vw, vh) = VisibleSize(mapArea);
        double originX = map.MiniMap is { } mm ? -mm.CenterX : map.Vr.Left;
        double originY = map.MiniMap is { } mm2 ? -mm2.CenterY : map.Vr.Top;
        double W = map.Vr.Width, H = map.Vr.Height;
        switch (model)
        {
            case MinimapModel.Stretch:
                return new(model, map, mapArea,
                    new MinimapAxis(MinimapAxisMode.Fit, W / vw, originX, vw, vw, 0, AreaInsetLeft),
                    new MinimapAxis(MinimapAxisMode.Fit, H / vh, originY, vh, vh, 0, AreaInsetTop));
            case MinimapModel.Letterbox:
            {
                var s = Math.Max(W / vw, H / vh);
                return new(model, map, mapArea, Fitted(s, originX, W, vw, AreaInsetLeft), Fitted(s, originY, H, vh, AreaInsetTop));
            }
            default:
            {
                var s = WorldPerPixel(map);
                return new(model, map, mapArea, FixedAxis(s, originX, W, vw, AreaInsetLeft), FixedAxis(s, originY, H, vh, AreaInsetTop));
            }
        }

        static MinimapAxis Fitted(double s, double origin, double size, int visible, int areaOrigin)
        {
            var full = size / s;
            return full >= visible - FitTolerance
                ? new MinimapAxis(MinimapAxisMode.Fit, s, origin, full, visible, 0, areaOrigin)
                : new MinimapAxis(MinimapAxisMode.Centered, s, origin, full, visible, (visible - full) / 2, areaOrigin);
        }

        static MinimapAxis FixedAxis(double s, double origin, double size, int visible, int areaOrigin)
        {
            var full = Math.Floor(size / s);
            if (Math.Abs(full - visible) <= FitTolerance) return new MinimapAxis(MinimapAxisMode.Fit, s, origin, full, visible, 0, areaOrigin);
            if (full < visible) return new MinimapAxis(MinimapAxisMode.Centered, s, origin, full, visible, Math.Floor((visible - full) / 2), areaOrigin);
            return new MinimapAxis(MinimapAxisMode.Window, s, origin, full, visible, 0, areaOrigin);
        }
    }

    /// <summary>
    /// The player's world position from its dot (relative to MapArea). A windowed axis is only known when the window
    /// sits against an end of the image, or when <paramref name="windowY"/> / <paramref name="windowX"/> (from
    /// <see cref="EstimateWindow"/>) say where it is; with the window centred on the player the dot never moves on that axis.
    /// </summary>
    public MinimapFix ToWorld(PointD selfInMap, double? windowX = null, double? windowY = null)
    {
        var (x, ex) = AxisToWorld(Horizontal, selfInMap.X, SelfDotOffsetX, windowX);
        var (y, ey) = AxisToWorld(Vertical, selfInMap.Y, SelfDotOffsetY, windowY);
        return new MinimapFix(x, y, ex, ey);
    }

    /// <summary>
    /// The world band the player must be in when the dot sits at the centre of a windowed axis (the window is away from
    /// both ends of the image), or null when the axis is not windowed. Heights from other sources outside it are wrong.
    /// </summary>
    public (double Min, double Max)? CentredBand(bool vertical = true)
    {
        var axis = vertical ? Vertical : Horizontal;
        if (axis.Mode != MinimapAxisMode.Window) return null;
        return (axis.ToWorld(axis.Visible / 2.0 - 1), axis.ToWorld(axis.Full - axis.Visible / 2.0 + 1));
    }

    /// <summary>Where the own dot is drawn (relative to MapArea) for a player at <paramref name="world"/>.</summary>
    public PointD ToMap(PointD world) =>
        new(AxisToMap(Horizontal, world.X, SelfDotOffsetX), AxisToMap(Vertical, world.Y, SelfDotOffsetY));

    /// <summary>Where a portal's marker centroid is drawn (relative to MapArea), given the window positions.</summary>
    public PointD PortalToMap(Portal portal, double windowX = 0, double windowY = 0) =>
        new(Horizontal.ToImage(portal.X) - windowX + Horizontal.Offset + Horizontal.AreaOrigin + PortalDotOffsetX,
            Vertical.ToImage(portal.Y) - windowY + Vertical.Offset + Vertical.AreaOrigin + PortalDotOffsetY);

    /// <summary>
    /// Locates a scrolled window from the portal markers it shows: every pairing of a drawn portal with a marker whose
    /// position along the other axis agrees within two pixels proposes a window position; the position proposed by the
    /// most distinct markers (within a pixel) wins. Null when the axis is not windowed or no marker pairs up.
    /// </summary>
    public (double Window, int Support)? EstimateWindow(IEnumerable<MinimapMarker> portalMarkersInMap, bool vertical = true)
    {
        var axis = vertical ? Vertical : Horizontal;
        var other = vertical ? Horizontal : Vertical;
        if (axis.Mode != MinimapAxisMode.Window || other.Mode == MinimapAxisMode.Window) return null;
        var candidates = new List<(double W, int Marker)>();
        var markers = WholeMarkers(portalMarkersInMap);
        for (var i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            foreach (var p in Map.Portals)
            {
                if (!DrawnPortalTypes.Contains(p.Type)) continue;
                var predicted = PortalToMap(p);
                var (along, across, predAlong, predAcross) = vertical ? (m.Y, m.X, predicted.Y, predicted.X) : (m.X, m.Y, predicted.X, predicted.Y);
                if (Math.Abs(across - predAcross) > 2) continue;
                var w = predAlong - along;
                if (w < -1 || w > axis.MaxWindow + 1) continue;
                candidates.Add((w, i));
            }
        }
        if (candidates.Count == 0) return null;
        (double W, int Support)? best = null;
        foreach (var (w, _) in candidates)
        {
            var cluster = candidates.Where(c => Math.Abs(c.W - w) <= 1).ToList();
            var support = cluster.Select(c => c.Marker).Distinct().Count();
            var mean = cluster.Average(c => c.W);
            if (best is null || support > best.Value.Support) best = (mean, support);
        }
        return best is { } b ? (Math.Clamp(b.W, 0, axis.MaxWindow), b.Support) : null;
    }

    /// <summary>Merges split portal rings and drops rings clipped by the window (see <see cref="MarkerMergeDistance"/>).</summary>
    public static List<PointD> WholeMarkers(IEnumerable<MinimapMarker> markers)
    {
        var groups = new List<List<MinimapMarker>>();
        foreach (var m in markers)
        {
            var group = groups.FirstOrDefault(g => g.Any(o => o.Position.DistanceTo(m.Position) <= MarkerMergeDistance));
            if (group is null) groups.Add([m]); else group.Add(m);
        }
        return groups.Where(g => g.Sum(m => m.Pixels) >= WholeMarkerPixels)
            .Select(g => new PointD(g.Sum(m => m.Position.X * m.Pixels) / g.Sum(m => m.Pixels), g.Sum(m => m.Position.Y * m.Pixels) / g.Sum(m => m.Pixels)))
            .ToList();
    }

    public override string ToString() =>
        $"{Model} x {Horizontal.Mode} {Horizontal.WorldPerPixel:0.##}/px, y {Vertical.Mode} {Vertical.WorldPerPixel:0.##}/px, image {Horizontal.Full:0}x{Vertical.Full:0} in {Horizontal.Visible}x{Vertical.Visible}";

    private static (double? World, double Error) AxisToWorld(MinimapAxis axis, double dotInArea, double dotOffset, double? window)
    {
        // Drawn integer position in visible-image pixels, then the centre of the pixel cell the player is in.
        var drawn = Math.Round(dotInArea) - axis.AreaOrigin - axis.Offset;
        var visible = drawn - dotOffset + 0.5;
        var half = axis.WorldPerPixel / 2;
        if (axis.Mode != MinimapAxisMode.Window) return (axis.ToWorld(visible), half);
        // The window centres on the player, so the dot sits at the centre unless the window hit an end of the image;
        // off centre, the end it hit gives the position exactly, and a window located from portals is not needed.
        var centre = axis.Visible / 2.0;
        if (visible < centre - 1) return (axis.ToWorld(visible), half);
        if (visible > centre + 1) return (axis.ToWorld(visible + axis.MaxWindow), half);
        if (window is { } w) return (axis.ToWorld(visible + w), PortalWindowError);
        return (null, double.PositiveInfinity);
    }

    private static double AxisToMap(MinimapAxis axis, double world, double dotOffset)
    {
        var image = axis.ToImage(world);
        var window = axis.WindowFor(image);
        return Math.Floor(image - window) + dotOffset + axis.Offset + axis.AreaOrigin;
    }
}
