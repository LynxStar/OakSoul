namespace OakSoul.Wz;

/// <summary>
/// An image (".img") entry of a WZ archive or of a ".ms" pack. Its property tree is parsed lazily on first access to <see cref="Root"/>
/// (or an indexer) and kept until <see cref="Unload"/> is called.
/// </summary>
public sealed class WzImage
{
    private readonly Lock _gate = new();
    private WzNode? _root;
    private IReadOnlySet<string> _unknownTypes = new HashSet<string>();

    internal WzImage(WzFile file, WzDirectory parent, string name, int size, int checksum, uint offset)
    {
        _file = file;
        Parent = parent;
        Name = name;
        Size = size;
        Checksum = checksum;
        Offset = offset;
    }

    /// <summary>An image stored as an entry of a ".ms" pack; <see cref="Path"/> is the entry name ("Mob/0100100.img").</summary>
    internal WzImage(MsFile pack, MsEntry entry)
    {
        Pack = pack;
        PackEntry = entry;
        _packPath = entry.Name;
        int slash = entry.Name.LastIndexOf('/');
        Name = slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
        Size = entry.Size;
        Checksum = entry.CheckSum;
        Offset = checked((uint)entry.Offset);
    }

    private readonly WzFile? _file;
    private readonly string? _packPath;

    /// <summary>The WZ archive holding this image. Throws for images read from a ".ms" pack (see <see cref="Pack"/>).</summary>
    public WzFile File => _file ?? throw new InvalidOperationException($"Image '{Path}' comes from the pack {Pack?.FilePath}, not a WZ archive.");

    /// <summary>The ".ms" pack holding this image, or null for an image inside a WZ archive.</summary>
    public MsFile? Pack { get; }

    /// <summary>The pack entry of this image, or null for an image inside a WZ archive.</summary>
    public MsEntry? PackEntry { get; }

    /// <summary>The directory holding this image inside its WZ archive; null for pack images.</summary>
    public WzDirectory? Parent { get; }

    public string Name { get; }

    /// <summary>Size of the image data in bytes, as recorded in the directory (or pack) entry.</summary>
    public int Size { get; }

    public int Checksum { get; }

    /// <summary>Absolute file offset of the image data (decoded from the directory entry, or the pack entry's data position).</summary>
    public uint Offset { get; }

    /// <summary>Path of the file holding this image (WZ archive or pack).</summary>
    public string SourcePath => _file?.FilePath ?? Pack!.FilePath;

    public string Path => _packPath ?? (Parent!.Path.Length == 0 ? Name : Parent.Path + "/" + Name);

    /// <summary>
    /// Reads plain image bytes at an absolute file offset inside this image. For pack images the encrypted head of
    /// the entry is decrypted transparently, so callers (the parser, canvases) see the same bytes either way.
    /// </summary>
    internal byte[] ReadBytes(long offset, int count) =>
        _file is not null ? _file.ReadBytes(offset, count) : Pack!.ReadEntryBytes(PackEntry!, offset, count);

    public bool IsParsed => _root is not null;

    /// <summary>The image's top-level property, parsed on first access. Thread-safe.</summary>
    public WzNode Root
    {
        get
        {
            if (_root is not null)
                return _root;
            lock (_gate)
            {
                if (_root is null)
                {
                    byte[] data = ReadBytes(Offset, Size);
                    (_root, _unknownTypes) = ImageParser.Parse(data, Name, this);
                }
                return _root;
            }
        }
    }

    /// <summary>Extended type names found in this image that the parser did not understand (empty if none, or not yet parsed).</summary>
    public IReadOnlySet<string> UnknownTypes => _unknownTypes;

    /// <summary>Shortcut for <c>Root[path]</c>: <c>image["info"]?["VRLeft"]</c> or <c>image["info/VRLeft"]</c>.</summary>
    public WzNode? this[string path] => Root[path];

    /// <summary>Drops the parsed tree so it can be garbage collected; the next access parses again.</summary>
    public void Unload()
    {
        lock (_gate)
            _root = null;
    }

    public override string ToString() => $"{Path} ({Size} bytes at 0x{Offset:X})";
}
