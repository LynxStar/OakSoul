// The ".ms" V2 pack layout and key derivation below are ported from WzComparerR2 (Ms_FileV2.cs, Ms_ImageV2.cs):
//   https://github.com/Kagamia/WzComparerR2
//   MIT License. Copyright (c) Kagamia Studio.
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//   documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//   the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
//   to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//   The above copyright notice and this permission notice shall be included in all copies or substantial portions of
//   the Software.
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//   THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
//   CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
//   DEALINGS IN THE SOFTWARE.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OakSoul.Wz;

/// <summary>One entry of a ".ms" pack's entry table.</summary>
public sealed class MsEntry
{
    internal MsEntry(string name, int checkSum, int flags, int startBlock, int size, int sizeAligned, int unk1, int unk2,
        byte[] key, int unk3, int unk4, long offset)
    {
        Name = name; CheckSum = checkSum; Flags = flags; StartBlock = startBlock; Size = size; SizeAligned = sizeAligned;
        Unk1 = unk1; Unk2 = unk2; Key = key; Unk3 = unk3; Unk4 = unk4; Offset = offset;
        int sum = 0;
        foreach (byte b in key)
            sum += b;
        CalculatedCheckSum = unchecked(flags + startBlock + size + sizeAligned + unk1 + sum);
    }

    /// <summary>Full entry name, such as "Mob/0100100.img" or "Mob/pack_ignore.txt".</summary>
    public string Name { get; }
    public int CheckSum { get; }
    public int CalculatedCheckSum { get; }
    public bool CheckSumValid => CheckSum == CalculatedCheckSum;
    public int Flags { get; }
    /// <summary>Start of the data in 1024-byte blocks from the pack's data start.</summary>
    public int StartBlock { get; }
    /// <summary>Payload size in bytes.</summary>
    public int Size { get; }
    public int SizeAligned { get; }
    public int Unk1 { get; }
    public int Unk2 { get; }
    /// <summary>16-byte per-entry key material.</summary>
    public byte[] Key { get; }
    public int Unk3 { get; }
    public int Unk4 { get; }
    /// <summary>Absolute file offset of the payload.</summary>
    public long Offset { get; }

    public bool IsImage => Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name} ({Size} bytes at 0x{Offset:X})";
}

/// <summary>
/// A MapleStory ".ms" pack (V2, file version 4), opened strictly read-only (FileAccess.Read, FileShare.Read).
/// <see cref="Open"/> decodes the header and entry table; image entries are parsed lazily through
/// <see cref="OpenImage"/>, which returns an ordinary <see cref="WzImage"/>. Dispose to close the file.
/// </summary>
/// <remarks>
/// Layout, with fn the lowercased file name: (sum(fn) % 312 + 30) junk bytes whose arithmetic-shifted values form a
/// mask; a masked version byte (must be 4); int32 hashed salt length; the masked UTF-16 salt; an 8-byte ChaCha20
/// header (hash, entry count); a gap of sum(fn*3) % 212 + 64 bytes; the ChaCha20 entry table; data aligned to 1024.
/// Each entry's first min(size, 1024) bytes are ChaCha20-encrypted with a key from the entry name, the salt hash
/// and the entry key; the rest is plain. The payload of an image entry is a standard WZ image body.
/// </remarks>
public sealed class MsFile : IDisposable
{
    public const int SupportedVersion = 4;
    private const int EncryptedHeadLength = 1024;

    private static readonly byte[] KeyObscure =
    [
        0x7B, 0x2F, 0x35, 0x48, 0x43, 0x95, 0x02, 0xB9, 0xAE, 0x91, 0xA6, 0xE1, 0xD8, 0xD6, 0x24, 0xB4,
        0x33, 0x10, 0x1D, 0x3D, 0xC1, 0xBB, 0xC6, 0xF4, 0xA5, 0xFE, 0xB3, 0x69, 0x6B, 0x56, 0xE4, 0x75,
    ];

    private readonly FileStream _stream;
    private readonly Lock _ioGate = new();
    private readonly Dictionary<MsEntry, WzImage> _images = [];
    private readonly Dictionary<MsEntry, byte[]> _heads = [];
    private List<MsEntry> _entries = [];

    private MsFile(string path, FileStream stream)
    {
        FilePath = path;
        _stream = stream;
        Salt = string.Empty;
    }

    public string FilePath { get; }
    public long Length => _stream.Length;

    /// <summary>Number of junk bytes before the version byte.</summary>
    public int RandomByteCount { get; private set; }
    public int Version { get; private set; }
    public string Salt { get; private set; }
    /// <summary>Position of the 8-byte encrypted header (just after the salt).</summary>
    public long HeaderStart { get; private set; }
    public int Hash { get; private set; }
    public int EntryCount { get; private set; }
    public long EntryStart { get; private set; }
    /// <summary>Position just after the last entry of the table.</summary>
    public long EntryTableEnd { get; private set; }
    public long DataStart { get; private set; }
    public IReadOnlyList<MsEntry> Entries => _entries;

    // per-pack inputs of the entry key derivation
    private uint _keyHash, _keyHash2, _keyHash3;
    private byte[] _keyHashDigits = [];

    public static MsFile Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        var file = new MsFile(full, stream);
        try
        {
            file.ReadHeader();
            file.ReadEntries();
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return file;
    }

    /// <summary>The entry with this full name (case-insensitive), or null.</summary>
    public MsEntry? FindEntry(string name) =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The image for an ".img" entry (created once per entry, parsed lazily on first access to its Root).</summary>
    public WzImage OpenImage(MsEntry entry)
    {
        if (!entry.IsImage)
            throw new ArgumentException($"Pack entry '{entry.Name}' is not an image.", nameof(entry));
        lock (_images)
        {
            if (!_images.TryGetValue(entry, out var image))
                _images[entry] = image = new WzImage(this, entry);
            return image;
        }
    }

    /// <summary>All image entries as images (parsed lazily).</summary>
    public IEnumerable<WzImage> EnumerateImages() => _entries.Where(e => e.IsImage).Select(OpenImage);

    /// <summary>The whole decrypted payload of an entry.</summary>
    public byte[] ReadEntry(MsEntry entry) => ReadEntryBytes(entry, entry.Offset, entry.Size);

    /// <summary>
    /// Reads <paramref name="count"/> payload bytes at absolute file offset <paramref name="offset"/>, which must lie
    /// inside the entry. Bytes in the encrypted head are decrypted.
    /// </summary>
    internal byte[] ReadEntryBytes(MsEntry entry, long offset, int count)
    {
        if (offset < entry.Offset || count < 0 || offset + count > entry.Offset + entry.Size)
            throw new WzFormatException($"{FilePath}: read of {count} bytes at {offset} is outside entry '{entry.Name}'.");
        var buffer = ReadFile(offset, count);
        long headEnd = entry.Offset + Math.Min(entry.Size, EncryptedHeadLength);
        if (offset < headEnd)
        {
            byte[] head = DecryptedHead(entry);
            int from = (int)(offset - entry.Offset);
            int n = (int)Math.Min(count, headEnd - offset);
            head.AsSpan(from, n).CopyTo(buffer);
        }
        return buffer;
    }

    public void Dispose() => _stream.Dispose();

    public override string ToString() => $"{FilePath} (v{Version}, {EntryCount} entries)";

    // ---------------------------------------------------------------- header and entry table

    private void ReadHeader()
    {
        string fn = System.IO.Path.GetFileName(FilePath).ToLowerInvariant();
        int randCount = fn.Sum(c => (int)c) % 312 + 30;
        if (Length < randCount + 5)
            throw new WzFormatException($"{FilePath}: too short for a .ms pack.");
        RandomByteCount = randCount;

        byte[] rand = ReadFile(0, randCount);
        for (int i = 0; i < rand.Length; i++)
            rand[i] = (byte)((sbyte)rand[i] >> 1);

        byte[] head = ReadFile(randCount, 5);
        byte encVer = head[0];
        Version = encVer ^ rand[0];
        if (Version != SupportedVersion)
            throw new WzFormatException($"{FilePath}: .ms version check failed (expected {SupportedVersion}, found {Version}).");

        int hashedSaltLen = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
        int saltLen = (byte)hashedSaltLen ^ rand[0];
        if (saltLen > rand.Length)
            throw new WzFormatException($"{FilePath}: implausible salt length {saltLen}.");
        byte[] saltBytes = ReadFile(randCount + 5, saltLen * 2);
        var salt = new char[saltLen];
        for (int i = 0; i < saltLen; i++)
        {
            int a = rand[i] ^ saltBytes[i * 2];
            salt[i] = (char)(((a | 0x4B) << 1) - a - 75);
        }
        Salt = new string(salt);
        HeaderStart = randCount + 5 + saltLen * 2;

        string fws = fn + Salt;
        Span<byte> key = stackalloc byte[ChaCha20.KeyLength];
        for (int i = 0; i < key.Length; i++)
            key[i] = (byte)((byte)(fws[i % fws.Length] + i) ^ KeyObscure[i]);
        byte[] header = ReadFile(HeaderStart, 8);
        ChaCha20.Xor(key, stackalloc byte[ChaCha20.NonceLength], 0, header);
        Hash = BinaryPrimitives.ReadInt32LittleEndian(header);
        EntryCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

        int actual = unchecked(hashedSaltLen + encVer + Version + EntryCount);
        for (int i = 0; i < saltLen; i++)
            actual = unchecked(actual + BinaryPrimitives.ReadUInt16LittleEndian(saltBytes.AsSpan(i * 2)));
        if (Hash != actual)
            throw new WzFormatException($"{FilePath}: .ms header hash check failed (stored {Hash}, computed {actual}).");
        if (EntryCount < 0 || EntryCount > 1_000_000)
            throw new WzFormatException($"{FilePath}: implausible entry count {EntryCount}.");

        EntryStart = HeaderStart + 8 + fn.Sum(c => c * 3) % 212 + 64;

        // Entry key inputs: FNV-1a over the salt, and the decimal digits of that hash.
        uint h = 0x811C9DC5;
        foreach (char c in Salt)
            h = unchecked((h ^ c) * 0x1000193);
        _keyHash = h;
        _keyHash2 = h >> 1;
        _keyHash3 = _keyHash2 ^ 0x6C;
        _keyHashDigits = h.ToString(CultureInfo.InvariantCulture).Select(ch => (byte)(ch - '0')).ToArray();
    }

    private void ReadEntries()
    {
        string fws = System.IO.Path.GetFileName(FilePath).ToLowerInvariant() + Salt;
        var key = new byte[ChaCha20.KeyLength];
        for (int i = 0; i < key.Length; i++)
            key[i] = (byte)((byte)(i + (i % 3 + 2) * fws[fws.Length - 1 - i % fws.Length]) ^ KeyObscure[i]);

        var reader = new TableReader(this, key, EntryStart);
        var raw = new List<(string Name, int[] Ints, byte[] Key, int Unk3, int Unk4)>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
        {
            int nameLen = reader.ReadInt32();
            if (nameLen < 0 || nameLen > 4096)
                throw new WzFormatException($"{FilePath}: entry {i} has an implausible name length {nameLen}.");
            string name = Encoding.Unicode.GetString(reader.ReadBytes(nameLen * 2));
            var ints = new int[7];
            for (int k = 0; k < 7; k++)
                ints[k] = reader.ReadInt32();
            byte[] entryKey = reader.ReadBytes(16);
            raw.Add((name, ints, entryKey, reader.ReadInt32(), reader.ReadInt32()));
        }
        EntryTableEnd = reader.Position;
        DataStart = (EntryTableEnd + 0x3FF) & ~0x3FFL;

        _entries = new List<MsEntry>(raw.Count);
        foreach (var (name, v, entryKey, unk3, unk4) in raw)
        {
            long offset = DataStart + (long)v[2] * 1024;
            if (v[2] < 0 || v[3] < 0 || offset + v[3] > Length)
                throw new WzFormatException($"{FilePath}: entry '{name}' ({v[3]} bytes at block {v[2]}) lies outside the file.");
            _entries.Add(new MsEntry(name, v[0], v[1], v[2], v[3], v[4], v[5], v[6], entryKey, unk3, unk4, offset));
        }
    }

    /// <summary>
    /// The entry table cipher, reproducing the reference reader exactly: the file is consumed in 64-byte blocks, and
    /// the block counter goes back to 0 whenever a read call ends with the current block fully consumed (otherwise it
    /// keeps counting). Not standard ChaCha20 streaming, but it is what the writer did.
    /// </summary>
    private sealed class TableReader(MsFile file, byte[] key, long start)
    {
        private readonly byte[] _block = new byte[ChaCha20.BlockLength];
        private readonly byte[] _nonce = new byte[ChaCha20.NonceLength];
        private long _next = start;
        private int _used = ChaCha20.BlockLength;
        private uint _counter;

        public long Position => _next - (ChaCha20.BlockLength - _used);

        public byte[] ReadBytes(int count)
        {
            var output = new byte[count];
            int done = 0;
            while (done < count)
            {
                if (_used >= ChaCha20.BlockLength)
                {
                    byte[] cipher = file.ReadFile(_next, ChaCha20.BlockLength);
                    _next += ChaCha20.BlockLength;
                    ChaCha20.Xor(key, _nonce, _counter++, cipher);
                    cipher.CopyTo(_block, 0);
                    _used = 0;
                }
                int n = Math.Min(count - done, ChaCha20.BlockLength - _used);
                Array.Copy(_block, _used, output, done, n);
                _used += n;
                done += n;
            }
            if (_used >= ChaCha20.BlockLength)
                _counter = 0;
            return output;
        }

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
    }

    // ---------------------------------------------------------------- entry payloads

    private byte[] DecryptedHead(MsEntry entry)
    {
        lock (_heads)
        {
            if (_heads.TryGetValue(entry, out var cached))
                return cached;
        }

        string name = entry.Name;
        byte[] d = _keyHashDigits;
        int dn = d.Length;
        Span<byte> key = stackalloc byte[ChaCha20.KeyLength];
        for (int i = 0; i < key.Length; i++)
        {
            int v = i + name[i % name.Length] * (d[i % dn] % 2 + entry.Key[(d[(i + 2) % dn] + i) % 16] + (d[(i + 1) % dn] + i) % 5);
            key[i] = (byte)((byte)v ^ KeyObscure[i]);
        }

        Span<byte> kd = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(kd, _keyHash);
        BinaryPrimitives.WriteUInt32LittleEndian(kd[4..], _keyHash2);
        BinaryPrimitives.WriteUInt32LittleEndian(kd[8..], _keyHash3);
        for (int i = 0, a = 0, b = 0, c = 90, dd = 0; i < 12; i++)
        {
            kd[i] ^= (byte)(dd + 11 * (i / 11) + (c ^ (i >> 2)) + (a ^ b));
            dd--; a += 8; b += 17; c += 43;
        }
        Span<byte> nonce = stackalloc byte[ChaCha20.NonceLength];
        kd[..8].CopyTo(nonce[4..]);
        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(kd[8..]);

        byte[] head = ReadFile(entry.Offset, Math.Min(entry.Size, EncryptedHeadLength));
        ChaCha20.Xor(key, nonce, counter, head);
        lock (_heads)
            _heads[entry] = head;
        return head;
    }

    private byte[] ReadFile(long offset, int count)
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
}
