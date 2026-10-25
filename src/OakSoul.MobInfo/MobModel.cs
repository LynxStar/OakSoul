namespace OakSoul.MobInfo;

/// <summary>Top-level shape of <c>data/mobs.json</c>.</summary>
public sealed class MobTable
{
    public string GeneratedUtc { get; set; } = "";
    public string Source { get; set; } = "meowdb.com/msclassic";

    /// <summary>Keyed by the 7-digit zero-padded id the client's sprite folders use ("0000030").</summary>
    public SortedDictionary<string, MobRecord> Mobs { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MobRecord
{
    public int Id { get; set; }

    /// <summary>Name from the client's String archive (preferred), falling back to the site's name.</summary>
    public string? Name { get; set; }
    public string? SiteName { get; set; }
    public int? Level { get; set; }
    public int? Hp { get; set; }
    public int? Exp { get; set; }

    /// <summary>The site's speed label, e.g. "Normal".</summary>
    public string? Speed { get; set; }

    /// <summary>The number in the site's "(speed -50)" hint when shown, else null.</summary>
    public int? SpeedModifier { get; set; }

    /// <summary>"floor", "jump", "fly" or "unknown" (movement label missing or not understood).</summary>
    public string Mobility { get; set; } = "unknown";
    public int? Knockback { get; set; }
    public double? StaggerSeconds { get; set; }
    public TouchDamage? TouchDamage { get; set; }

    /// <summary>The site's M.DMG, null for the many mobs that have none.</summary>
    public int? MagicDamage { get; set; }
    public int? PhysicalDefence { get; set; }
    public int? MagicDefence { get; set; }
    public int? Accuracy { get; set; }
    public int? Avoid { get; set; }
    public List<ElementEffect> Elements { get; set; } = [];

    /// <summary>Entries in the site's attribute list that are not "Element relation" pairs, verbatim (e.g. "MP 30").</summary>
    public List<string> OtherAttributes { get; set; } = [];
    public Size? Sprite { get; set; }
    public Size? TouchZone { get; set; }

    /// <summary>Animation name (Stand, Move, Jump, Hit, Die, ...) to duration in ms.</summary>
    public SortedDictionary<string, int>? AnimationMs { get; set; }
    public List<MobMap> Maps { get; set; } = [];
}

public sealed record TouchDamage(int? Min, int? Max, int? Base);

/// <summary>An element relation such as Fire/strong; <c>Note</c> carries the site's suffix when present ("25% extra dmg").</summary>
public sealed record ElementEffect(string Element, string Relation, string? Note = null);

public sealed record Size(int Width, int Height);

/// <summary>
/// One row of the site's map table. <c>RespawnText</c> is the site's cell ("~7.5s", "1m", "20s-40s", "1m + ~7.5s"): a "~" marks the
/// shared wave spawn, a bare time or range is an individual spawn-point timer. <c>RespawnSeconds</c> is the shortest individual timer
/// when there is one, else the wave time.
/// </summary>
public sealed record MobMap(
    int MapId, string? Name, string? Street, int? Count, double? MobRate,
    double? RespawnSeconds, double? IndividualMinSeconds, double? IndividualMaxSeconds, double? WaveSeconds, string? RespawnText);
