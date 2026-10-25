namespace OakSoul.Wz;

/// <summary>
/// Parses one image's bytes into a <see cref="WzNode"/> tree. The parser works on an in-memory copy of
/// the image, so positions here are relative to the image start, which is exactly what the
/// deduplicated-string offsets inside an image are relative to.
/// </summary>
internal sealed class ImageParser
{
    private readonly WzBinaryReader _r;
    private readonly string _imageName;

    /// <summary>Extended type names seen that this parser does not understand (kept for diagnostics).</summary>
    public HashSet<string> UnknownTypes { get; } = new(StringComparer.Ordinal);

    private ImageParser(byte[] data, string imageName)
    {
        _r = new WzBinaryReader(new MemoryStream(data, writable: false));
        _imageName = imageName;
    }

    public static (WzNode Root, IReadOnlySet<string> UnknownTypes) Parse(byte[] data, string imageName)
    {
        var parser = new ImageParser(data, imageName);
        var root = parser.ParseRoot();
        return (root, parser.UnknownTypes);
    }

    private WzNode ParseRoot()
    {
        // An image starts like an extended "Property" body: the type name (normally marker 0x73 + inline
        // string), then a ushort 0, then the property list.
        string type = ReadDedupString();
        if (type != "Property")
            throw Fail($"image does not start with \"Property\" (found \"{type}\")", 0);
        _r.ReadUInt16();
        var root = WzNode.Container(_imageName, WzNodeType.Property, null, "Property");
        ParsePropertyList(root);
        return root;
    }

    /// <summary>
    /// A string that may be stored inline or as a back-reference to an earlier copy in the same image.
    /// Markers 0x00 (names) and 0x73 (type names) mean inline; 0x01 (names) and 0x1B (type names) mean an
    /// int32 offset from the image start follows.
    /// </summary>
    private string ReadDedupString()
    {
        long at = _r.Position;
        byte marker = _r.ReadByte();
        return marker switch
        {
            0x00 or 0x73 => _r.ReadWzString(),
            0x01 or 0x1B => _r.ReadWzStringAt(_r.ReadInt32()),
            _ => throw Fail($"unknown string marker 0x{marker:X2}", at),
        };
    }

    private void ParsePropertyList(WzNode parent)
    {
        long at = _r.Position;
        int count = _r.ReadCompressedInt();
        if (count < 0 || count > _r.Length - _r.Position)
            throw Fail($"implausible property count {count}", at);

        for (int i = 0; i < count; i++)
        {
            string name = ReadDedupString();
            long typeAt = _r.Position;
            byte type = _r.ReadByte();
            switch (type)
            {
                case 0:
                    parent.Add(WzNode.Container(name, WzNodeType.Null, parent));
                    break;
                case 2:
                case 11:
                    parent.Add(WzNode.OfInteger(name, WzNodeType.Short, parent, _r.ReadInt16()));
                    break;
                case 3:
                case 19:
                    parent.Add(WzNode.OfInteger(name, WzNodeType.Int, parent, _r.ReadCompressedInt()));
                    break;
                case 20:
                    parent.Add(WzNode.OfInteger(name, WzNodeType.Long, parent, _r.ReadCompressedLong()));
                    break;
                case 4:
                    // Floats are "compressed": a flag byte 0x80 means a float32 follows, anything else means 0.
                    float f = _r.ReadByte() == 0x80 ? _r.ReadSingle() : 0f;
                    parent.Add(WzNode.OfReal(name, WzNodeType.Float, parent, f));
                    break;
                case 5:
                    parent.Add(WzNode.OfReal(name, WzNodeType.Double, parent, _r.ReadDouble()));
                    break;
                case 8:
                    parent.Add(WzNode.OfString(name, parent, ReadDedupString()));
                    break;
                case 9:
                    // Extended property inside a sized block. Always resume at the block end, so a body we
                    // misread or do not understand cannot desynchronise the rest of the list.
                    int blockSize = _r.ReadInt32();
                    long end = _r.Position + blockSize;
                    if (blockSize < 0 || end > _r.Length)
                        throw Fail($"extended property '{name}' has implausible block size {blockSize}", typeAt);
                    ParseExtended(name, parent);
                    _r.Position = end;
                    break;
                default:
                    // Primitive types have no size prefix, so there is no way to skip an unknown one.
                    throw Fail($"unknown property type {type} for '{name}'", typeAt);
            }
        }
    }

    /// <summary>
    /// Parses an extended property body (type name then body) and adds it to <paramref name="parent"/>.
    /// Returns false when the body was not understood, in which case the stream position is undefined and
    /// the caller must reposition (sized blocks) or stop (unsized Convex children).
    /// </summary>
    private bool ParseExtended(string name, WzNode parent)
    {
        string typeName = ReadDedupString();
        switch (typeName)
        {
            case "Property":
            {
                _r.ReadUInt16();
                var node = WzNode.Container(name, WzNodeType.Property, parent, typeName);
                parent.Add(node);
                ParsePropertyList(node);
                return true;
            }
            case "Canvas":
            {
                var node = WzNode.OfCanvas(name, parent);
                parent.Add(node);
                _r.ReadByte();
                if (_r.ReadByte() == 1)
                {
                    _r.ReadUInt16();
                    ParsePropertyList(node);
                }
                // Bitmap header: width, height, format (compressed ints), format2 (byte), int32 0, int32 length,
                // then the compressed pixel data. Only the header is read; the caller skips the pixels.
                int width = _r.ReadCompressedInt();
                int height = _r.ReadCompressedInt();
                int format = _r.ReadCompressedInt();
                node.SetCanvas(width, height, format);
                return true;
            }
            case "Shape2D#Vector2D":
            {
                int x = _r.ReadCompressedInt();
                int y = _r.ReadCompressedInt();
                parent.Add(WzNode.OfVector(name, parent, x, y));
                return true;
            }
            case "Shape2D#Convex2D":
            {
                var node = WzNode.Container(name, WzNodeType.Convex, parent, typeName);
                parent.Add(node);
                int count = _r.ReadCompressedInt();
                for (int i = 0; i < count; i++)
                {
                    // Convex children are extended bodies without a block size; an unknown one ends parsing here.
                    if (!ParseExtended(i.ToString(System.Globalization.CultureInfo.InvariantCulture), node))
                        return false;
                }
                return true;
            }
            case "Sound_DX8":
                parent.Add(WzNode.Container(name, WzNodeType.Sound, parent, typeName));
                return false; // body skipped via the block size
            case "UOL":
            {
                _r.ReadByte();
                parent.Add(WzNode.OfUol(name, parent, ReadDedupString()));
                return true;
            }
            default:
                UnknownTypes.Add(typeName);
                parent.Add(WzNode.Container(name, WzNodeType.Unknown, parent, typeName));
                return false;
        }
    }

    private WzFormatException Fail(string what, long position) =>
        new($"{_imageName}: {what} at image offset {position}.");
}
