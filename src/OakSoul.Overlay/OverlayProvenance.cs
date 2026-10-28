using System.Diagnostics;

namespace OakSoul.Overlay;

/// <summary>Where a stream came from: the commit of the code that produced it and the recording's path.</summary>
public static class OverlayProvenance
{
    /// <summary>
    /// Files whose changes cannot change a stream: the review tool and its tests, the documentation, review packets and
    /// ground truth. Neither a stream's "+dirty" nor a comparison of commits counts them.
    /// </summary>
    public static readonly IReadOnlyList<string> Unaffecting = ["src/OakSoul.Review", "src/OakSoul.Review.Tests", "docs", "truth", "reviews", "*.md"];

    private static string[] Pathspec => ["--", ".", .. Unaffecting.Select(p => $":(exclude){p}")];

    /// <summary>
    /// The git commit checked out in <paramref name="dir"/>, with "+dirty" when tracked files that can affect a stream
    /// differ from it, or null when git is not available or the directory is not a work tree.
    /// </summary>
    public static string? GitCommit(string dir)
    {
        var sha = Git(dir, ["rev-parse", "HEAD"]).Output;
        if (sha is null) return null;
        var status = Git(dir, ["status", "--porcelain", "--untracked-files=no", .. Pathspec]).Output;
        return status is { Length: > 0 } ? sha + "+dirty" : sha;
    }

    /// <summary>
    /// Whether two commits hold the same files that can affect a stream (commits that changed only the review tool or the
    /// documentation make the same streams); null when git cannot tell.
    /// </summary>
    public static bool? SameStreamCode(string dir, string a, string b) =>
        Git(dir, ["diff", "--quiet", a, b, .. Pathspec]).Code switch { 0 => true, 1 => false, _ => null };

    private static (string? Output, int? Code) Git(string dir, IEnumerable<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in (string[])["-C", dir, .. arguments]) info.ArgumentList.Add(a);
            using var git = Process.Start(info);
            if (git is null) return (null, null);
            var output = git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            if (!git.WaitForExit(5000)) return (null, null);
            return (git.ExitCode == 0 ? output.Trim() : null, git.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (null, null);
        }
    }

    /// <summary>A path relative to the working directory when it lies under it, with forward slashes.</summary>
    public static string RelativePath(string path)
    {
        var full = Path.GetFullPath(path);
        var cwd = Directory.GetCurrentDirectory();
        var rel = Path.GetRelativePath(cwd, full);
        return (rel.StartsWith("..", StringComparison.Ordinal) ? full : rel).Replace('\\', '/').TrimEnd('/');
    }
}
