using OakSoul.World;

namespace OakSoul.Detect;

/// <summary>
/// The mobs a map can show, from its life list: each distinct species with its spawn count, the floors its spawns
/// stand on (floors are the components of the foothold prev/next graph, <see cref="FootholdIndex.FloorOf"/>), the
/// spawns' heights, and its frames in both facings. Built once per map (<see cref="MobLibrary"/> frames are shared),
/// and asked per candidate for the order in which to try the species (<see cref="Order"/>).
/// </summary>
public sealed class MapMobSet
{
    /// <summary>One species on the map.</summary>
    public sealed record Entry(MobSpecies Species, int Spawns, IReadOnlySet<int> Floors, IReadOnlyList<int> SpawnYs);

    public int MapId { get; }
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Spawn ids the library had no data for (not exported), reported once.</summary>
    public IReadOnlyList<string> Missing { get; }

    private MapMobSet(int mapId, List<Entry> entries, List<string> missing)
    {
        MapId = mapId;
        Entries = entries;
        Missing = missing;
    }

    public static MapMobSet Build(MapData map, FootholdIndex index, MobLibrary library)
    {
        var entries = new List<Entry>();
        var missing = new List<string>();
        foreach (var g in map.MobSpawns.GroupBy(l => l.Id))
        {
            if (!int.TryParse(g.Key, out var id) || library.Get(id) is not { Frames.Count: > 0 } species) { missing.Add(g.Key); continue; }
            var floors = g.Select(l => index.FloorOf(l.Fh)).OfType<int>().ToHashSet();
            entries.Add(new Entry(species, g.Count(), floors, g.Select(l => l.Y).ToList()));
        }
        entries.Sort((a, b) => b.Spawns.CompareTo(a.Spawns));
        return new MapMobSet(map.MapId, entries, missing);
    }

    /// <summary>Whether any species of the map flies.</summary>
    public bool HasFlyers => Entries.Any(e => e.Species.Mobility == MobMobility.Fly);

    public Entry? ById(int id) => Entries.FirstOrDefault(e => e.Species.Id == id);

    private readonly Dictionary<int, List<MobSpecies>> _recolours = [];

    /// <summary>
    /// The other species of the map drawn with the same frame geometry (same animations, frame sizes and origins) but
    /// their own pixels: recolours such as Stump and Dark Stump, Axe Stump and Dark Axe Stump, Snail, Blue Snail and Red
    /// Snail. Only their colours tell them apart, so a match is always compared against them at the same placement.
    /// </summary>
    public IReadOnlyList<MobSpecies> Recolours(MobSpecies species)
    {
        if (_recolours.TryGetValue(species.Id, out var known)) return known;
        var list = new List<MobSpecies>();
        var keys = species.Frames.ToDictionary(f => f.Key);
        foreach (var e in Entries)
        {
            var other = e.Species;
            if (other.Id == species.Id || other.SpriteId == species.SpriteId || other.Frames.Count == 0) continue;
            var shared = other.Frames.Count(f => keys.TryGetValue(f.Key, out var g) && g.Width == f.Width && g.Height == f.Height && g.OriginX == f.OriginX && g.OriginY == f.OriginY);
            if (shared >= Math.Min(keys.Count, other.Frames.Count) * 3 / 4) list.Add(other);
        }
        _recolours[species.Id] = list;
        return list;
    }

    /// <summary>The species whose name a tag read names (exactly, or as the end or start of the name: "Snail" also names
    /// "Red Snail" and "Blue Snail"), or empty when none does.</summary>
    public IReadOnlyList<Entry> Named(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var exact = Entries.Where(e => string.Equals(e.Species.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return Entries.Where(e => TagNames.SameName(name, e.Species.Name)).ToList();
    }

    /// <summary>
    /// The species to try for a candidate, in order.
    /// <list type="bullet">
    /// <item>A tag read on the candidate's track names the species: only those (<paramref name="named"/>).</item>
    /// <item>Otherwise, with the candidate's floor known: the species with a spawn on that floor (floor-bound or not),
    /// then the jumpers spawning elsewhere by vertical distance from the candidate to their nearest spawn, then the
    /// flyers. Floor-bound mobs never leave their floor and are not tried elsewhere. Without a floor (feet in the air):
    /// jumpers and flyers by vertical distance, then the floor-bound ones. Within a group, more spawns first.</item>
    /// <item>The track's identity so far, if any, goes first whatever its group (<paramref name="identity"/>).</item>
    /// </list>
    /// </summary>
    public List<Entry> Order(int? floor, double worldY, int? identity, IReadOnlyList<Entry>? named)
    {
        List<Entry> order;
        if (named is { Count: > 0 }) order = named.OrderByDescending(e => e.Spawns).ToList();
        else
        {
            double Vertical(Entry e) => e.SpawnYs.Count == 0 ? double.MaxValue : e.SpawnYs.Min(y => Math.Abs(y - worldY));
            if (floor is { } f)
            {
                var same = Entries.Where(e => e.Floors.Contains(f)).OrderByDescending(e => e.Spawns);
                var jumpers = Entries.Where(e => e.Species.Mobility == MobMobility.Jump && !e.Floors.Contains(f)).OrderBy(Vertical).ThenByDescending(e => e.Spawns);
                var flyers = Entries.Where(e => e.Species.Mobility == MobMobility.Fly && !e.Floors.Contains(f)).OrderByDescending(e => e.Spawns);
                order = same.Concat(jumpers).Concat(flyers).ToList();
            }
            else
            {
                var movers = Entries.Where(e => e.Species.Mobility is MobMobility.Jump or MobMobility.Fly).OrderBy(Vertical).ThenByDescending(e => e.Spawns);
                var floorBound = Entries.Where(e => e.Species.Mobility is MobMobility.Floor or MobMobility.Static).OrderBy(Vertical).ThenByDescending(e => e.Spawns);
                order = movers.Concat(floorBound).ToList();
            }
        }
        if (identity is { } id && (named is not { Count: > 0 } || named.Any(e => e.Species.Id == id)) && ById(id) is { } known)
        {
            order.Remove(known);
            order.Insert(0, known);
        }
        return order;
    }
}
