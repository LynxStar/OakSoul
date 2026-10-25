namespace OakSoul.Wz;

/// <summary>
/// An image (".img") entry. Its property tree is parsed lazily on first access to <see cref="Root"/>
/// (or an indexer) and kept until <see cref="Unload"/> is called.
/// </summary>
public sealed class WzImage
{
    private readonly Lock _gate = new();
    private WzNode? _root;
    private IReadOnlySet<string> _unknownTypes = new HashSet<string>();

    internal WzImage(WzFile file, WzDirectory parent, string name, int size, int checksum, uint offset)
    {
        File = file;
        Parent = parent;
        Name = name;
        Size = size;
        Checksum = checksum;
        Offset = offset;
    }

    public WzFile File { get; }
    public WzDirectory Parent { get; }
    public string Name { get; }

    /// <summary>Size of the image data in bytes, as recorded in the directory entry.</summary>
    public int Size { get; }

    public int Checksum { get; }

    /// <summary>Absolute file offset of the image data (decoded from the directory entry).</summary>
    public uint Offset { get; }

    public string Path => Parent.Path.Length == 0 ? Name : Parent.Path + "/" + Name;

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
                    byte[] data = File.ReadBytes(Offset, Size);
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
