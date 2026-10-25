using System.Collections.Concurrent;
using System.Text;

namespace OakSoul.Wz;

/// <summary>
/// A WZ archive opened strictly read-only. <see cref="Open"/> reads the header, detects the game
/// version (needed to decode directory offsets) and loads the whole directory tree; images are
/// parsed lazily. Dispose to close the file.
/// </summary>
/// <remarks>
/// Layout: "PKG1", int64 data size, int32 fstart, a NUL-terminated copyright string, then at fstart a
/// ushort "encrypted version" followed by the root directory. Each directory is a compressed count of
/// entries; each entry has a name, a size, a checksum and an encrypted absolute offset.
/// </remarks>
public sealed class WzFile : IDisposable
{
    /// <summary>Highest game version tried during brute-force detection.</summary>
    public const int MaxDetectVersion = 2000;

    /// <summary>Detected versions keyed by full path, length and modification time, so re-opening is free.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), int> VersionCache = new();

    private readonly FileStream _stream;
    private readonly WzBinaryReader _reader;
    private readonly Lock _ioGate = new();

    private WzFile(string path, FileStream stream)
    {
        FilePath = path;
        _stream = stream;
        _reader = new WzBinaryReader(stream);
        Root = null!;
        Copyright = string.Empty;
    }

    public string FilePath { get; }
    public long Length => _stream.Length;

    /// <summary>Header "fstart": where the data section (version ushort + root directory) begins.</summary>
    public uint FileStart { get; private set; }

    /// <summary>Header data size (file length minus fstart).</summary>
    public long DataSize { get; private set; }

    public string Copyright { get; private set; }

    /// <summary>The ushort stored at fstart.</summary>
    public ushort EncryptedVersion { get; private set; }

    /// <summary>The game version whose hash decodes this file's offsets.</summary>
    public int Version { get; private set; }

    public uint VersionHash { get; private set; }

    /// <summary>
    /// True when <see cref="Version"/> was confirmed by decoding an image offset and finding a valid image
    /// there. False for archives with no reachable image (empty index stubs), where the first version whose
    /// check value matches is used and nothing depends on it.
    /// </summary>
    public bool VersionVerified { get; private set; }

    public WzDirectory Root { get; private set; }

    private long DirectoryStart => FileStart + 2;

    /// <summary>
    /// Opens an archive read-only (FileAccess.Read, FileShare.Read). Pass <paramref name="version"/> to skip
    /// detection; otherwise versions 1..<see cref="MaxDetectVersion"/> are tried.
    /// </summary>
    public static WzFile Open(string path, int? version = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        var file = new WzFile(full, stream);
        try
        {
            file.ReadHeader();
            var rootEntries = file.ReadEntries(file.DirectoryStart);

            var key = (full, stream.Length, File.GetLastWriteTimeUtc(full));
            if (version is { } v)
            {
                file.SetVersion(v);
                file.VersionVerified = file.Validate(rootEntries, 0) == Validation.Valid;
            }
            else if (VersionCache.TryGetValue(key, out int cached))
            {
                file.SetVersion(cached);
                file.VersionVerified = true;
            }
            else
            {
                file.DetectVersion(rootEntries);
                if (file.VersionVerified)
                    VersionCache[key] = file.Version;
            }

            file.Root = new WzDirectory(file, string.Empty, null, (uint)file.DirectoryStart);
            file.LoadDirectory(file.Root, rootEntries, 0);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>All game versions in 1..max whose check value equals <paramref name="encryptedVersion"/>.</summary>
    public static IReadOnlyList<int> VersionCandidates(ushort encryptedVersion, int max = MaxDetectVersion)
    {
        var list = new List<int>();
        for (int v = 1; v <= max; v++)
            if (WzCrypto.EncryptedVersion(WzCrypto.VersionHash(v)) == encryptedVersion)
                list.Add(v);
        return list;
    }

    /// <summary>Reads raw bytes of the archive (used to load an image before parsing). Thread-safe.</summary>
    internal byte[] ReadBytes(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > Length)
            throw new WzFormatException($"{FilePath}: read of {count} bytes at {offset} is outside the file (length {Length}).");
        var buffer = new byte[count];
        lock (_ioGate)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }
        return buffer;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    public override string ToString() => $"{FilePath} (v{Version}{(VersionVerified ? "" : ", unverified")})";

    // ---------------------------------------------------------------- header

    private void ReadHeader()
    {
        _reader.Position = 0;
        if (Length < 16 || Encoding.ASCII.GetString(_reader.ReadBytes(4)) != "PKG1")
            throw new WzFormatException($"{FilePath}: not a WZ archive (missing PKG1 signature).");
        DataSize = _reader.ReadInt64();
        FileStart = _reader.ReadUInt32();
        if (FileStart < 16 || FileStart + 2 > Length)
            throw new WzFormatException($"{FilePath}: implausible fstart {FileStart}.");

        var copyright = new StringBuilder();
        while (_reader.Position < FileStart)
        {
            byte b = _reader.ReadByte();
            if (b == 0)
                break;
            copyright.Append((char)b);
        }
        Copyright = copyright.ToString();

        _reader.Position = FileStart;
        EncryptedVersion = _reader.ReadUInt16();
    }

    // ---------------------------------------------------------------- directories

    /// <summary>A directory entry before its offset is decoded (decoding needs the version hash).</summary>
    private readonly record struct RawEntry(byte Type, string Name, int Size, int Checksum, long FieldPosition, uint EncryptedOffset);

    /// <summary>Reads the entry list of a directory. Names, sizes and checksums do not depend on the version.</summary>
    private List<RawEntry> ReadEntries(long position)
    {
        lock (_ioGate)
        {
            _reader.Position = position;
            int count = _reader.ReadCompressedInt();
            if (count < 0 || count > (Length - position) / 4)
                throw new WzFormatException($"{FilePath}: implausible directory entry count {count} at {position}.");

            var entries = new List<RawEntry>(count);
            for (int i = 0; i < count; i++)
            {
                long entryAt = _reader.Position;
                byte type = _reader.ReadByte();
                string name;
                switch (type)
                {
                    case 1:
                        // Unknown entry kind: int32, int16, then an offset. Nothing to keep.
                        _reader.ReadInt32();
                        _reader.ReadInt16();
                        _reader.ReadUInt32();
                        continue;
                    case 2:
                    {
                        // Name stored elsewhere (deduplicated): at fstart + off sits the real type byte and the name.
                        int off = _reader.ReadInt32();
                        long back = _reader.Position;
                        _reader.Position = FileStart + off;
                        type = _reader.ReadByte();
                        name = _reader.ReadWzString();
                        _reader.Position = back;
                        break;
                    }
                    case 3:
                    case 4:
                        name = _reader.ReadWzString();
                        break;
                    default:
                        throw new WzFormatException($"{FilePath}: unknown directory entry type {type} at {entryAt}.");
                }

                int size = _reader.ReadCompressedInt();
                int checksum = _reader.ReadCompressedInt();
                long fieldPosition = _reader.Position;
                uint encrypted = _reader.ReadUInt32();
                if (type is not (3 or 4))
                    throw new WzFormatException($"{FilePath}: entry '{name}' at {entryAt} resolves to type {type}.");
                entries.Add(new RawEntry(type, name, size, checksum, fieldPosition, encrypted));
            }
            return entries;
        }
    }

    private uint Decode(RawEntry e) => WzCrypto.DecodeOffset(e.FieldPosition, FileStart, VersionHash, e.EncryptedOffset);

    private void LoadDirectory(WzDirectory dir, List<RawEntry> entries, int depth)
    {
        if (depth > 64)
            throw new WzFormatException($"{FilePath}: directory nesting too deep at '{dir.Path}'.");

        foreach (var e in entries)
        {
            uint offset = Decode(e);
            if (e.Type == 4)
            {
                if (offset < DirectoryStart || offset + (long)e.Size > Length)
                    throw new WzFormatException($"{FilePath}: image '{e.Name}' decodes to offset {offset} (size {e.Size}), outside the file. Wrong version?");
                dir.Add(new WzImage(this, dir, e.Name, e.Size, e.Checksum, offset));
            }
            else
            {
                var sub = new WzDirectory(this, e.Name, dir, offset);
                dir.Add(sub);
                if (offset < DirectoryStart || offset >= Length)
                    sub.IsExternal = true; // contents live in another archive (index stub)
                else
                    LoadDirectory(sub, ReadEntries(offset), depth + 1);
            }
        }
    }

    // ---------------------------------------------------------------- version detection

    private enum Validation { Valid, Invalid, Inconclusive }

    private void SetVersion(int version)
    {
        Version = version;
        VersionHash = WzCrypto.VersionHash(version);
    }

    /// <summary>
    /// The stored ushort carries only 8 bits of the version hash, so it narrows 1..2000 to a handful of
    /// candidates. Each candidate is then tested by decoding real offsets: the right one points at images
    /// that begin with the marker 0x73 and the string "Property".
    /// </summary>
    private void DetectVersion(List<RawEntry> rootEntries)
    {
        var candidates = VersionCandidates(EncryptedVersion);
        int? inconclusive = null;
        foreach (int v in candidates)
        {
            SetVersion(v);
            switch (Validate(rootEntries, 0))
            {
                case Validation.Valid:
                    VersionVerified = true;
                    return;
                case Validation.Inconclusive:
                    inconclusive ??= v;
                    break;
            }
        }

        if (inconclusive is { } fallback)
        {
            // No image reachable in this file (e.g. an index stub); offsets are never used for data.
            SetVersion(fallback);
            VersionVerified = false;
            return;
        }

        throw new WzFormatException(
            $"{FilePath}: no game version in 1..{MaxDetectVersion} decodes this archive's offsets " +
            $"(stored check value 0x{EncryptedVersion:X4}, candidates {string.Join(", ", candidates)}).");
    }

    /// <summary>
    /// Checks up to three images in this directory; if there are none, descends into subdirectories
    /// (offsets outside the file are treated as external, i.e. inconclusive).
    /// </summary>
    private Validation Validate(List<RawEntry> entries, int depth)
    {
        int checkedImages = 0;
        foreach (var e in entries.Where(e => e.Type == 4).Take(3))
        {
            uint offset = Decode(e);
            if (offset < DirectoryStart || offset + (long)e.Size > Length || !LooksLikeImage(offset))
                return Validation.Invalid;
            checkedImages++;
        }
        if (checkedImages > 0)
            return Validation.Valid;
        if (depth >= 4)
            return Validation.Inconclusive;

        foreach (var e in entries.Where(e => e.Type == 3))
        {
            uint offset = Decode(e);
            if (offset < DirectoryStart || offset >= Length)
                continue;
            List<RawEntry> sub;
            try
            {
                sub = ReadEntries(offset);
            }
            catch (Exception ex) when (ex is WzFormatException or EndOfStreamException)
            {
                return Validation.Invalid;
            }
            var result = Validate(sub, depth + 1);
            if (result != Validation.Inconclusive)
                return result;
        }
        return Validation.Inconclusive;
    }

    /// <summary>An image starts with the type-name marker 0x73 followed by the WZ string "Property".</summary>
    private bool LooksLikeImage(uint offset)
    {
        lock (_ioGate)
        {
            try
            {
                _reader.Position = offset;
                return _reader.ReadByte() == 0x73 && _reader.ReadWzString() == "Property";
            }
            catch (Exception ex) when (ex is WzFormatException or EndOfStreamException)
            {
                return false;
            }
        }
    }
}
