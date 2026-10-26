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

    /// <summary>A plain name read this often next to the own character is the pet's.</summary>
    public const int PetSightings = 3;

    public string? OwnName { get; set; }
    public string? PetName { get; private set; }

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

    public void SawPlainNearOwn(string name)
    {
        var n = _plainNearOwn[name] = _plainNearOwn.GetValueOrDefault(name) + 1;
        if (PetName is null && n >= PetSightings && !string.Equals(name, OwnName, StringComparison.OrdinalIgnoreCase)) PetName = name;
    }

    /// <summary>
    /// The known mob name a read most likely is, or null. With a level, names known at a different level are skipped:
    /// "Lv. 11 Dark Stump" is not a misread "Lv. 23 Dark Axe Stump".
    /// </summary>
    public string? MatchMob(string? name, int? level = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
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

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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
