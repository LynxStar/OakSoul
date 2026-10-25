namespace OakSoul.Vision.Readers;

/// <summary>
/// Job names as the classic client prints them on the status bar. OCR occasionally mangles the bold first
/// letter ("Wvizard"), so a read is snapped to the nearest known name when it is within a few edits.
/// </summary>
public static class JobNames
{
    public static readonly IReadOnlyList<string> Known =
    [
        "Beginner",
        "Warrior", "Fighter", "Page", "Spearman", "Crusader", "Knight", "Dragon Knight", "Hero", "Paladin", "Dark Knight",
        "Magician", "Wizard (Fire,Poison)", "Wizard (Ice,Lightning)", "Cleric",
        "Mage (Fire,Poison)", "Mage (Ice,Lightning)", "Priest", "Arch Mage (Fire,Poison)", "Arch Mage (Ice,Lightning)", "Bishop",
        "Bowman", "Hunter", "Crossbowman", "Ranger", "Sniper", "Bow Master", "Marksman",
        "Thief", "Assassin", "Bandit", "Hermit", "Chief Bandit", "Night Lord", "Shadower",
        "Pirate", "Brawler", "Gunslinger", "Marauder", "Outlaw", "Buccaneer", "Corsair",
    ];

    /// <summary>Returns the known name within <paramref name="maxEdits"/> of the read, else the read unchanged.</summary>
    public static string Snap(string read, int maxEdits = 3)
    {
        var best = read;
        var bestDistance = int.MaxValue;
        foreach (var name in Known)
        {
            var d = Levenshtein(read, name);
            if (d < bestDistance) { bestDistance = d; best = name; }
        }
        return bestDistance <= maxEdits ? best : read;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
