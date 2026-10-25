namespace OakSoul.World;

/// <summary>A foothold found under a point, with the vertical distance from the point down to it (negative: the point is below the surface).</summary>
public readonly record struct FootholdHit(Foothold Foothold, double SurfaceY, double Distance);

/// <summary>A ladder or rope near a point, with the horizontal distance to it.</summary>
public readonly record struct LadderHit(LadderRope Ladder, double DistanceX);

/// <summary>A portal near a point, with the straight-line distance to it.</summary>
public readonly record struct PortalHit(Portal Portal, double Distance);

/// <summary>
/// Spatial queries over one map's footholds, ladders and portals, in world units (y grows downward). Footholds are
/// bucketed by x so a query only looks at the segments spanning that column.
/// </summary>
public sealed class FootholdIndex
{
    private const int BucketWidth = 128;
    private readonly MapData _map;
    private readonly Dictionary<int, List<Foothold>> _buckets = [];
    private readonly Dictionary<int, Foothold> _byId = [];

    public FootholdIndex(MapData map)
    {
        _map = map;
        foreach (var fh in map.Footholds)
        {
            _byId[fh.Id] = fh;
            if (fh.IsWall) continue;
            for (var b = Bucket(fh.MinX); b <= Bucket(fh.MaxX); b++)
            {
                if (!_buckets.TryGetValue(b, out var list)) _buckets[b] = list = [];
                list.Add(fh);
            }
        }
    }

    public MapData Map => _map;

    public Foothold? ById(int id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// The highest foothold surface at x that is at or below the point, allowing the point to sit up to
    /// <paramref name="tolerance"/> units below a surface (a sprite's feet sink a little, and estimates are noisy).
    /// Only surfaces within <paramref name="maxDistance"/> below the point are considered.
    /// </summary>
    public FootholdHit? Below(PointD point, double maxDistance = double.MaxValue, double tolerance = 0)
    {
        FootholdHit? best = null;
        if (!_buckets.TryGetValue(Bucket(point.X), out var list)) return null;
        foreach (var fh in list)
        {
            if (fh.YAt(point.X) is not { } y) continue;
            var d = y - point.Y;
            if (d < -tolerance || d > maxDistance) continue;
            if (best is null || d < best.Value.Distance) best = new FootholdHit(fh, y, d);
        }
        return best;
    }

    /// <summary>The foothold the point stands on: a surface within <paramref name="tolerance"/> units above or below it.</summary>
    public FootholdHit? StandingOn(PointD point, double tolerance = 6) =>
        Below(point, tolerance, tolerance) is { } hit && Math.Abs(hit.Distance) <= tolerance ? hit : null;

    /// <summary>The surface closest to the point vertically (above or below) at its x, within <paramref name="maxDistance"/>.</summary>
    public FootholdHit? Nearest(PointD point, double maxDistance = double.MaxValue)
    {
        FootholdHit? best = null;
        if (!_buckets.TryGetValue(Bucket(point.X), out var list)) return null;
        foreach (var fh in list)
        {
            if (fh.YAt(point.X) is not { } y) continue;
            var d = y - point.Y;
            if (Math.Abs(d) > maxDistance) continue;
            if (best is null || Math.Abs(d) < Math.Abs(best.Value.Distance)) best = new FootholdHit(fh, y, d);
        }
        return best;
    }

    /// <summary>All surfaces at x whose height lies in [top, bottom], nearest to <paramref name="preferY"/> first.</summary>
    public IReadOnlyList<FootholdHit> SurfacesBetween(double x, double top, double bottom, double preferY)
    {
        if (!_buckets.TryGetValue(Bucket(x), out var list)) return [];
        return list.Select(fh => fh.YAt(x) is { } y && y >= top && y <= bottom ? new FootholdHit(fh, y, y - preferY) : (FootholdHit?)null)
            .OfType<FootholdHit>().OrderBy(h => Math.Abs(h.Distance)).ToList();
    }

    /// <summary>
    /// Walks the foothold chain (prev/next links) from <paramref name="from"/> towards <paramref name="toX"/> and returns
    /// the surface reached there: where a character walking along the ground from that foothold would be. Null when the
    /// chain ends, turns into a wall, or needs more than <paramref name="maxSteps"/> links first.
    /// </summary>
    public FootholdHit? Follow(Foothold from, double toX, double referenceY, int maxSteps = 64)
    {
        var current = from;
        for (var step = 0; step <= maxSteps; step++)
        {
            if (current.YAt(toX) is { } y) return new FootholdHit(current, y, y - referenceY);
            if (current.IsWall) return null;
            // Links follow the drawing direction: next continues from (X2, Y2), prev leads back from (X1, Y1).
            var towardsX2 = Math.Abs(toX - current.X2) < Math.Abs(toX - current.X1);
            var link = towardsX2 ? current.Next : current.Prev;
            if (link == 0 || !_byId.TryGetValue(link, out var nextFh) || nextFh.IsWall) return null;
            current = nextFh;
        }
        return null;
    }

    /// <summary>The ladder or rope the point is on: within <paramref name="toleranceX"/> horizontally and inside its span (plus <paramref name="toleranceY"/>).</summary>
    public LadderHit? LadderAt(PointD point, double toleranceX = 12, double toleranceY = 8)
    {
        LadderHit? best = null;
        foreach (var l in _map.LadderRopes)
        {
            var dx = Math.Abs(l.X - point.X);
            if (dx > toleranceX || point.Y < l.Y1 - toleranceY || point.Y > l.Y2 + toleranceY) continue;
            if (best is null || dx < best.Value.DistanceX) best = new LadderHit(l, dx);
        }
        return best;
    }

    /// <summary>The nearest portal, optionally restricted by <paramref name="filter"/>.</summary>
    public PortalHit? NearestPortal(PointD point, Func<Portal, bool>? filter = null)
    {
        PortalHit? best = null;
        foreach (var p in _map.Portals)
        {
            if (filter is not null && !filter(p)) continue;
            var d = point.DistanceTo(new PointD(p.X, p.Y));
            if (best is null || d < best.Value.Distance) best = new PortalHit(p, d);
        }
        return best;
    }

    private static int Bucket(double x) => (int)Math.Floor(x / BucketWidth);
}
