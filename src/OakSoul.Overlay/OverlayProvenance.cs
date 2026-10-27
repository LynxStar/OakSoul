using System.Diagnostics;

namespace OakSoul.Overlay;

/// <summary>Where a stream came from: the commit of the code that produced it and the recording's path.</summary>
public static class OverlayProvenance
{
    /// <summary>
    /// The git commit checked out in <paramref name="dir"/>, with "+dirty" when tracked files differ from it, or null when
    /// git is not available or the directory is not a work tree.
    /// </summary>
    public static string? GitCommit(string dir)
    {
        var sha = Git(dir, "rev-parse HEAD");
        if (sha is null) return null;
        var status = Git(dir, "status --porcelain --untracked-files=no");
        return status is { Length: > 0 } ? sha + "+dirty" : sha;
    }

    private static string? Git(string dir, string arguments)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", $"-C \"{dir}\" {arguments}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (git is null) return null;
            var output = git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            if (!git.WaitForExit(5000) || git.ExitCode != 0) return null;
            return output.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
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
