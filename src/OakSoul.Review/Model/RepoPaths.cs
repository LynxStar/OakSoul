namespace OakSoul.Review.Model;

/// <summary>
/// Where the tool reads and writes, relative to the repository root (the process's working directory): recordings/
/// (read-only), the streams in data/overlays/, review packets in reviews/ and ground truth in truth/ (both tracked), and
/// the packets' crops in data/reviews/ (gitignored). Tests point the tracked folders somewhere else.
/// </summary>
public sealed class RepoPaths
{
    public string Root { get; init; } = Directory.GetCurrentDirectory();
    public string Recordings => Path.Combine(Root, "recordings");
    public string Reviews { get; init; } = "reviews";
    public string Truth { get; init; } = "truth";
    public string ReviewData { get; init; } = Path.Combine("data", "reviews");

    /// <summary>Where streams are read and regenerated, when not Bake's data/overlays/ (tests).</summary>
    public string? Overlays { get; init; }

    /// <summary>
    /// The repository root: <paramref name="given"/>, else the nearest folder at or above the working directory, then
    /// above the program, that holds OakSoul.slnx and a recordings folder (falling back to OakSoul.slnx alone).
    /// </summary>
    public static string? FindRoot(string? given = null)
    {
        if (given is not null) return Directory.Exists(given) ? Path.GetFullPath(given) : null;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            if (Up(start, d => File.Exists(Path.Combine(d, "OakSoul.slnx")) && Directory.Exists(Path.Combine(d, "recordings"))) is { } withRecordings)
                return withRecordings;
        return Up(Directory.GetCurrentDirectory(), d => File.Exists(Path.Combine(d, "OakSoul.slnx")));
    }

    private static string? Up(string start, Func<string, bool> test)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
            if (test(d.FullName)) return d.FullName;
        return null;
    }

    /// <summary>The recorder sessions under recordings/ (folders with a session.json), by name.</summary>
    public IReadOnlyList<string> Sessions() =>
        Directory.Exists(Recordings)
            ? Directory.GetDirectories(Recordings).Where(d => File.Exists(Path.Combine(d, "session.json"))).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>A path relative to the root with forward slashes, as packets and truth record them.</summary>
    public string Relative(string path)
    {
        var rel = Path.GetRelativePath(Root, Path.GetFullPath(path, Root));
        return (rel.StartsWith("..", StringComparison.Ordinal) ? Path.GetFullPath(path, Root) : rel).Replace('\\', '/');
    }
}
