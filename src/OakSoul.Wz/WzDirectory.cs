namespace OakSoul.Wz;

/// <summary>A directory inside a WZ archive: named subdirectories and images.</summary>
public sealed class WzDirectory
{
    private readonly List<WzDirectory> _directories = [];
    private readonly List<WzImage> _images = [];

    internal WzDirectory(WzFile file, string name, WzDirectory? parent, uint offset)
    {
        File = file;
        Name = name;
        Parent = parent;
        Offset = offset;
    }

    public WzFile File { get; }
    public string Name { get; }
    public WzDirectory? Parent { get; }

    /// <summary>Absolute file offset of this directory's entry list.</summary>
    public uint Offset { get; }

    /// <summary>
    /// True when the entry points past the end of this file. Index stubs such as <c>Map/Map.wz</c> list
    /// subdirectories whose contents live in sibling archives (<c>Map0/Map0_000.wz</c>); those stay empty here.
    /// </summary>
    public bool IsExternal { get; internal set; }

    public IReadOnlyList<WzDirectory> Directories => _directories;
    public IReadOnlyList<WzImage> Images => _images;

    /// <summary>Slash-separated path from the archive root (the root has an empty path).</summary>
    public string Path => Parent is null ? string.Empty
        : Parent.Parent is null ? Name
        : Parent.Path + "/" + Name;

    public WzDirectory? GetDirectory(string name) => _directories.FirstOrDefault(d => d.Name == name);

    /// <summary>Finds an image by a slash-separated path relative to this directory, e.g. "Map.img" or "Map0/000010000.img".</summary>
    public WzImage? GetImage(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        WzDirectory? dir = this;
        for (int i = 0; i < parts.Length - 1 && dir is not null; i++)
            dir = dir.GetDirectory(parts[i]);
        return dir?._images.FirstOrDefault(img => img.Name == parts[^1]);
    }

    /// <summary>All images in this directory and, depth first, its subdirectories.</summary>
    public IEnumerable<WzImage> EnumerateImages(bool recursive = true)
    {
        foreach (var img in _images)
            yield return img;
        if (!recursive)
            yield break;
        foreach (var dir in _directories)
            foreach (var img in dir.EnumerateImages(true))
                yield return img;
    }

    internal void Add(WzDirectory dir) => _directories.Add(dir);
    internal void Add(WzImage img) => _images.Add(img);

    public override string ToString() => $"{(Path.Length == 0 ? "/" : Path)} [{_directories.Count} dirs, {_images.Count} images]";
}
