using System.Collections.Concurrent;
using OakSoul.Wz;

namespace OakSoul.SpriteExport;

internal sealed record Failure(string Archive, string Node, string Reason);

/// <summary>Thread-safe decode counters: per category, per pixel format, failures and non-zlib canvases.</summary>
internal sealed class Stats
{
    private int _decoded;

    public int Decoded => _decoded;
    public ConcurrentDictionary<string, int> ByCategory { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<int, int> ByFormat { get; } = new();
    public ConcurrentBag<Failure> Failures { get; } = [];
    public ConcurrentBag<string> NotZlib { get; } = [];

    public void Count(string category, WzCanvas canvas)
    {
        Interlocked.Increment(ref _decoded);
        ByCategory.AddOrUpdate(category, 1, (_, n) => n + 1);
        ByFormat.AddOrUpdate(canvas.PixelFormat, 1, (_, n) => n + 1);
    }

    public void Fail(string archive, WzCanvas canvas, string reason)
    {
        // Messages from WzCanvas repeat the file and node; keep only the reason after "): ".
        string prefix = $"canvas '{canvas.FullPath}' ({canvas.Width}x{canvas.Height}): ";
        int at = reason.IndexOf(prefix, StringComparison.Ordinal);
        Failures.Add(new Failure(archive, canvas.FullPath, at >= 0 ? reason[(at + prefix.Length)..].TrimEnd('.') : reason));
        if (!canvas.IsZlib())
            NotZlib.Add(canvas.FullPath);
    }

    public void Print(TextWriter w)
    {
        w.WriteLine($"Decoded {Decoded} canvases.");
        w.WriteLine("Per category:");
        foreach (var (k, v) in ByCategory.OrderBy(p => p.Key, StringComparer.Ordinal))
            w.WriteLine($"  {k,-48} {v,7}");
        w.WriteLine("Per pixel format:");
        foreach (var (k, v) in ByFormat.OrderBy(p => p.Key))
            w.WriteLine($"  {k,6} {FormatName(k),-28} {v,7}");
        w.WriteLine($"Non-zlib (keyed) canvases: {NotZlib.Count}");
        w.WriteLine($"Failures: {Failures.Count}");
        foreach (var g in Failures.GroupBy(f => (f.Archive, f.Reason)).OrderBy(g => g.Key.Archive, StringComparer.Ordinal))
            w.WriteLine($"  {g.Count(),6} x {g.Key.Archive}: {g.Key.Reason} (e.g. {g.Select(f => f.Node).Min(StringComparer.Ordinal)})");
        if (!Failures.IsEmpty)
            w.WriteLine("Failed nodes:");
        foreach (var f in Failures.OrderBy(f => f.Archive, StringComparer.Ordinal).ThenBy(f => f.Node, StringComparer.Ordinal).Take(50))
            w.WriteLine($"  {f.Archive} :: {f.Node}: {f.Reason}");
        if (Failures.Count > 50)
            w.WriteLine($"  ... {Failures.Count - 50} more");
    }

    public static string FormatName(int pixelFormat) => pixelFormat switch
    {
        1 => "BGRA4444",
        2 => "BGRA8888",
        3 => "BGRA4444 1/4 scale",
        257 => "BGRA5551",
        513 => "BGR565",
        517 => "BGR565 1/16 scale",
        1026 => "DXT3",
        2050 => "DXT5",
        _ => "unsupported",
    };
}
