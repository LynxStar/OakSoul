namespace OakSoul.Detect;

/// <summary>
/// The names tags have shown so far, to clean up OCR: mob names (read with a level badge), the own name and the own
/// pet's name. OCR of a tag drawn over another (a mob's tag across the pet's) runs the texts together, and a box cut
/// by a sprite loses letters, so a read is matched against the names already seen.
/// </summary>
public sealed class TagNames
{
    private readonly Dictionary<string, int> _mobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, int>> _levels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _plainNearOwn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A mob name counts once read this often with a level, so a single garbled read does not become a name.</summary>
    public const int MobSightings = 2;

    /// <summary>A plain name read this often next to the own character, while the pet's own name never was, is reported once as a mismatch.</summary>
    public const int PetSightings = 3;

    public string? OwnName { get; set; }

    /// <summary>
    /// The own pet's name, given by the operator (the watcher's <c>--pet</c>, default Mara; null for a character without a
    /// pet), never learned: a character has at most one pet. OCR only checks it (<see cref="PetMismatch"/>).
    /// </summary>
    public string? PetName { get; set; }

    /// <summary>
    /// Set once when a plain name other than the own and the pet's was read next to the own character
    /// <see cref="PetSightings"/> times before the pet's name was ever read there: the pet may be named differently than
    /// configured. The watcher prints it once, as it does a status bar that reads another name.
    /// </summary>
    public string? PetMismatch { get; private set; }

    private bool _petSeen;

    public IEnumerable<string> MobNames => _mobs.Where(m => m.Value >= MobSightings).Select(m => m.Key);

    /// <summary>Removes the own and the pet's name from a read (they run into overlapping mob tags).</summary>
    public string Strip(string text)
    {
        foreach (var n in new[] { PetName, OwnName })
            if (n is { Length: >= 3 }) text = text.Replace(n, " ", StringComparison.OrdinalIgnoreCase);
        return text.Trim();
    }

    /// <summary>Counts a mob name read with a level, under the known name it matches if there is one.</summary>
    public void SawMob(string name, int level)
    {
        var key = MatchMob(name, level) ?? name;
        _mobs[key] = _mobs.GetValueOrDefault(key) + 1;
        if (!_levels.TryGetValue(key, out var levels)) _levels[key] = levels = [];
        levels[level] = levels.GetValueOrDefault(level) + 1;
    }

    /// <summary>The level most often read with a known mob name.</summary>
    public int? LevelOf(string name) => _levels.TryGetValue(name, out var l) && l.Count > 0 ? l.MaxBy(e => e.Value).Key : null;

    /// <summary>Counts a plain name read next to the own character, for the <see cref="PetMismatch"/> check only.</summary>
    public void SawPlainNearOwn(string name)
    {
        if (IsPet(name)) { _petSeen = true; return; }
        if (OwnName is { } own && Distance(Normalize(name), Normalize(own)) <= 1) return;
        var n = _plainNearOwn[name] = _plainNearOwn.GetValueOrDefault(name) + 1;
        if (PetMismatch is null && !_petSeen && n >= PetSightings)
            PetMismatch = PetName is null ? $"the tag beside the character reads '{name}', and no pet is configured (--pet)" : $"the tag beside the character reads '{name}', expected the pet '{PetName}'";
    }

    /// <summary>Whether a tag read is the pet's tag alone: the pet's name without a level badge (the own name stripped).</summary>
    public bool IsPetTag(string? text)
    {
        if (PetName is null || string.IsNullOrWhiteSpace(text)) return false;
        var (level, name) = TagDetector.Parse(text);
        return level is null && name is not null && IsPet(name);
    }

    /// <summary>
    /// The species of the map (its life list) with their client levels, set by the detector: a read close to one of their names
    /// is that name (<see cref="MatchSpecies"/>), before any name learned from reads.
    /// </summary>
    public IReadOnlyList<(string Name, int Level)> Species
    {
        get => _species;
        set { _species = value; _speciesMemo.Clear(); }
    }

    private IReadOnlyList<(string Name, int Level)> _species = [];

    /// <summary><see cref="MatchSpecies"/> by read and level: the tracker asks it of the same reads every frame.</summary>
    private readonly Dictionary<(string, int), string?> _speciesMemo = [];

    /// <summary>
    /// The species name a read most likely is, by the rule of <see cref="SameName"/>, or null: "Vvild Boar" (the W read as vv) is
    /// Wild Boar. With a level read, only species of that level (as for learned names: "Lv. 23 Axe StumpR", a Dark Axe Stump cut by
    /// the screen's edge, is no Lv. 17 Axe Stump); the nearest; two species equally near ("Snail" ends both Red and Blue Snail)
    /// name neither.
    /// </summary>
    public string? MatchSpecies(string? name, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(name) || Species.Count == 0) return null;
        var key = (name, level is > 0 ? level.Value : 0);
        if (_speciesMemo.TryGetValue(key, out var known)) return known;
        if (_speciesMemo.Count >= 50_000) _speciesMemo.Clear();
        return _speciesMemo[key] = MatchSpeciesNow(name, level);
    }

    private string? MatchSpeciesNow(string name, int? level)
    {
        var near = Species.Where(s => level is not > 0 || s.Level == level).Select(s => (s.Name, s.Level, Score: Similarity(name, s.Name))).Where(s => s.Score <= MaxDistance).ToList();
        if (near.Count == 0) return null;
        var best = near.MinBy(s => s.Score);
        return near.Count(s => s.Score == best.Score && !string.Equals(s.Name, best.Name, StringComparison.OrdinalIgnoreCase)) > 0 ? null : best.Name;
    }

    /// <summary>Whether a read is a mob's tag by its level badge: pets and players are never tagged "Lv. N".</summary>
    public static bool IsMobTag(string? text) => TagDetector.HasBadge(text);

    /// <summary>
    /// The known mob name a read most likely is, or null: a species of the map (<see cref="MatchSpecies"/>), else a name learned
    /// from reads. With a level, names known at a different level are skipped: "Lv. 11 Dark Stump" is not a misread
    /// "Lv. 23 Dark Axe Stump".
    /// </summary>
    public string? MatchMob(string? name, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (MatchSpecies(name, level) is { } species) return species;
        string? best = null;
        var bestScore = double.MaxValue;
        foreach (var known in MobNames)
        {
            if (level is { } lv && LevelOf(known) is { } kl && kl != lv) continue;
            var score = Similarity(name, known);
            if (score < bestScore || (score == bestScore && best is not null && known.Length > best.Length)) { bestScore = score; best = known; }
        }
        return bestScore <= MaxDistance ? best : null;
    }

    /// <summary>Whether a read names a known name by the same rule as <see cref="MatchMob"/> (a few misread letters, or a long enough start or end).</summary>
    public static bool SameName(string read, string known) => Similarity(read, known) <= MaxDistance;

    /// <summary>
    /// Whether a read holds a piece of the pet's name: a word of three letters or more within one edit of a piece of the name
    /// ("ara" of Mara, from "ara oar" and "ara-v, 11" where the pet's tag ran into a boar's), or holding the whole name with more
    /// run into it ("JarMara" on fx-combat-exact's video frame 35: the end of a dying boar's "Boar" read into the pet's tag, which
    /// as a tag alone made a player "Jar"). Only a tag at the pet's tag position is asked: there such a read is the pet's tag,
    /// never a player's. A read with a level badge is a mob's tag (<see cref="IsMobTag"/>), never a piece of the pet's.
    /// </summary>
    public bool IsPetFragment(string? text)
    {
        if (PetName is null || string.IsNullOrWhiteSpace(text) || IsMobTag(text)) return false;
        var name = Normalize(PetName);
        foreach (var raw in text.Split([' ', '-', ',', '.', ':', ';', '\'', '"'], StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Normalize(raw);
            if (w.Length < 3 || w.Any(char.IsDigit)) continue;
            if (name.Length >= 3 && w.Length > name.Length && w.Contains(name, StringComparison.Ordinal)) return true;
            for (var len = Math.Max(2, w.Length - 1); len <= Math.Min(name.Length, w.Length + 1); len++)
                for (var i = 0; i + len <= name.Length; i++)
                    if (Distance(w, name.Substring(i, len)) <= 1) return true;
        }
        return false;
    }

    public bool IsPet(string? name) => name is not null && PetName is not null && Distance(Normalize(name), Normalize(PetName)) <= 1;

    /// <summary>
    /// Whether a tag read is the pet's or the own character's tag: that name alone, without a level badge (a mob's tag
    /// drawn across the pet's, "Fire BMara", still names the mob). Such a tag never labels a mob or a drop.
    /// </summary>
    public bool IsCharacterTag(string? text)
    {
        var (level, name) = TagDetector.Parse(text);
        if (level is not null || name is null) return false;
        return IsPet(name) || (OwnName is { Length: >= 3 } own && Distance(Normalize(name), Normalize(own)) <= 1);
    }

    /// <summary>
    /// A read matches a known name when their edit distance is at most a fifth of the known name's length (one or two
    /// misread letters; "Dark Stump" is three edits and 25% from "Dark Axe Stump", and a different mob), or when
    /// the read is a prefix or suffix of at least <see cref="MinPrefix"/> letters ("Dark Axe Stu" for "Dark Axe Stump",
    /// "Fire B" for "Fire Boar" when the pet's tag covered the rest, "e Stump" when a sprite covered the start).
    /// </summary>
    public const double MaxDistance = 0.2;

    public const int MinPrefix = 5;

    private static double Similarity(string read, string known)
    {
        string a = Normalize(read), b = Normalize(known);
        if (a.Length >= MinPrefix && (b.StartsWith(a, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal))) return 0;
        // A covered start leaves a broken first word ("ik Axe Stump"): the rest may still be the name's ending.
        var space = read.Trim().IndexOf(' ');
        if (space > 0 && Normalize(read.Trim()[(space + 1)..]) is { Length: >= MinPrefix } rest && b.EndsWith(rest, StringComparison.Ordinal)) return 0;
        return (double)Distance(a, b) / Math.Max(1, b.Length);
    }

    /// <summary>Lower case, letters and digits only, and "vv" as the "w" OCR split it into ("Vvild Boar" on fx-combat-exact's first frames).</summary>
    private static string Normalize(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("vv", "w", StringComparison.Ordinal);

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
