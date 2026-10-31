using System.Text;
using System.Text.Json;
using OakSoul.Capture;

namespace OakSoul.Vision.Readers;

/// <summary>Which rendering of the name block a crop is: the expanded panel's lines (region over map), or the collapsed bar's one line ("Region : Map").</summary>
public enum MapNameForm { Panel, Bar }

/// <summary>How a name block was named without a read: a cached line equal to it, or the fonts.</summary>
public enum MapNameSource { None, Cache, Font }

/// <summary>
/// Map names without a read: the name block of the minimap, which <see cref="MapNameTracker"/> reads once per map change, is first
/// looked up here. Two things are kept, both learned from reads the map catalog confirmed exactly and apart from the name tags'
/// files: a cache of the lines seen, the line's ink bits keyed by its text (a map visited before is named from its first confirmed
/// read, however the fonts stand), and two <see cref="MapFont"/>s, one per rendering, whose glyphs name any catalog name they can
/// spell (the region line against the streets, the map line against that street's maps, the bar's line against "Street : Map").
/// The cache never contradicts the fonts: a line whose letters the font has is cached only when the font finds its text on it, a
/// cached line is checked the same way whenever it is hit once the font can, and a line whose text the catalog no longer has is
/// dropped when the names are set; so a read that was wrong yet a real name (a numeral misread as another map's) is held at most
/// until the font can spell it.
/// Measured on the recordings: the expanded panel draws white anti-aliased text with a dark drop shadow on a flat background of luma
/// 182, pixel-identical for the same string every time, its letters lighter than the background at luma <see cref="PanelInkLuma"/>
/// and apart from each other only at <see cref="PanelSplitLuma"/>; the collapsed bar draws black 1-bit text on grey. The file is
/// data/cache/map-font.json (OAKSOUL_MAP_FONT=path|off).
/// </summary>
public sealed class MapNameFonts
{
    /// <summary>The panel's text is lighter than its background (luma 182) at this luma; its letters stand apart only above the split luma.</summary>
    public const int PanelInkLuma = 190, PanelSplitLuma = 210;

    /// <summary>The bar's text is black on grey (221): ink below this luma.</summary>
    public const int BarInkLuma = 100;

    /// <summary>A text line is a run of rows holding ink, runs closer than this joined (an i's dot, a descender), of at least this many rows.</summary>
    public const int BandJoinRows = 3, MinBandRows = 5;

    /// <summary>
    /// On the bar the buttons follow the name when the crop could not be cut at them: the name ends before the first run of this many
    /// blank columns (a space inside a name leaves at most five, the buttons stand at least eleven columns on).
    /// </summary>
    public const int BarEndBlank = 7;

    /// <summary>Renderings kept per text and form: the client draws a name the same way every time, so more are a dimmed transition or a misread.</summary>
    public const int MaxLinesPerText = 4;

    public MapFont Panel { get; private set; } = new("panel");
    public MapFont Bar { get; private set; } = new("bar");

    private sealed record Line(MapNameForm Form, string Text, int Width, int Height, bool[] Bits);
    private readonly List<Line> _lines = [];
    private readonly Dictionary<(MapNameForm Form, string Text), List<Line>> _byText = [];

    private readonly List<(string Street, string Map, string NStreet, string NMap)> _pairs = [];
    private readonly List<string> _streets = [];
    private readonly Dictionary<string, List<string>> _mapsByStreet = [];
    private readonly List<string> _maps = [];
    private readonly List<string> _barLines = [];

    /// <summary>Name blocks named here, by a cached line and by the fonts (a block of two lines counts for the fonts when either line needed them).</summary>
    public int ByCache { get; private set; }
    public int ByFont { get; private set; }
    public MapNameSource LastSource { get; private set; }

    /// <summary>
    /// Confirmed reads offered to learn from, the lines cached and taught from them, those whose block did not cut into one band per
    /// line, lines refused because the font could spell the text and did not find it, cached lines dropped on a hit the font refuted,
    /// and cached lines dropped for a text the catalog does not have.
    /// </summary>
    public int Offered { get; private set; }
    public int LinesCached { get; private set; }
    public int LinesTaught { get; private set; }
    public int Unsegmented { get; private set; }
    public int Contradicted { get; private set; }
    public int CacheRefuted { get; private set; }
    public int LinesDropped { get; private set; }

    /// <summary>Whether anything was learned since the fonts were loaded or made.</summary>
    public bool Changed { get; private set; }

    public int Lines => _lines.Count;

    public string Describe() => $"panel font {Panel.Letters.Count} letters from {Panel.Samples} lines, bar font {Bar.Letters.Count} letters from {Bar.Samples} lines, cache {_lines.Count} lines";

    /// <summary>
    /// The names the block can show, as the catalog spells them (street, map per map id); duplicates are fine. Cached lines whose text
    /// is none of them (a stale or edited file) are dropped.
    /// </summary>
    public void SetNames(IEnumerable<(string Street, string Map)> names)
    {
        _pairs.Clear(); _streets.Clear(); _mapsByStreet.Clear(); _maps.Clear(); _barLines.Clear();
        var seenPairs = new HashSet<(string, string)>();
        foreach (var (street, map) in names)
        {
            var (s, m) = (street.Trim(), map.Trim());
            if (m.Length == 0) continue;
            var ns = Normalize(s);
            var nm = Normalize(m);
            if (!seenPairs.Add((ns, nm))) continue;
            _pairs.Add((s, m, ns, nm));
            if (!_maps.Contains(m)) _maps.Add(m);
            if (s.Length > 0)
            {
                if (!_streets.Contains(s)) _streets.Add(s);
                if (!_mapsByStreet.TryGetValue(ns, out var maps)) _mapsByStreet[ns] = maps = [];
                if (!maps.Contains(m)) maps.Add(m);
                _barLines.Add($"{s} : {m}");
            }
        }
        foreach (var line in _lines.ToList())
        {
            var known = line.Form == MapNameForm.Panel ? _streets.Contains(line.Text) || _maps.Contains(line.Text) : _barLines.Contains(line.Text) || _maps.Contains(line.Text);
            if (!known) { Remove(line); LinesDropped++; Changed = true; }
        }
    }

    private bool Add(Line line)
    {
        if (!_byText.TryGetValue((line.Form, line.Text), out var list)) _byText[(line.Form, line.Text)] = list = [];
        if (list.Count >= MaxLinesPerText || list.Any(l => l.Width == line.Width && l.Height == line.Height && l.Bits.AsSpan().SequenceEqual(line.Bits))) return false;
        list.Add(line);
        _lines.Add(line);
        return true;
    }

    private void Remove(Line line)
    {
        _lines.Remove(line);
        if (_byText.TryGetValue((line.Form, line.Text), out var list)) { list.Remove(line); if (list.Count == 0) _byText.Remove((line.Form, line.Text)); }
    }

    /// <summary>The catalog's spelling of a read the catalog knows exactly (case and spacing aside), or null; an empty region stays empty.</summary>
    public (string Street, string Map)? Canonical(string? region, string? map)
    {
        var m = Normalize(map);
        if (m.Length == 0) return null;
        var s = Normalize(region);
        foreach (var p in _pairs) if (p.NMap == m && (s.Length == 0 || p.NStreet == s)) return (s.Length == 0 ? "" : p.Street, p.Map);
        return null;
    }

    public static string Normalize(string? text) => string.Join(' ', (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>
    /// The lines a name block shows, when every line is a cached line or a name the fonts spell: for the panel one line per text band
    /// (one or two), for the bar one line "Street : Map" or the map alone. Null when any line is unknown, so the block is read. The
    /// lines carry the text's columns within the crop as a read's boxes would.
    /// </summary>
    public IReadOnlyList<TextLine>? Recognise(byte[] bgra, int width, int height, MapNameForm form)
    {
        LastSource = MapNameSource.None;
        if (_pairs.Count == 0 || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        var (ink, _) = Bits(bgra, width, height, form);
        var bands = Bands(ink, width, height);
        var lines = new List<TextLine>();
        int cached = 0, fonted = 0;
        if (form == MapNameForm.Panel)
        {
            if (bands.Count is not (1 or 2)) return null;
            string? street = null;
            for (var i = 0; i < bands.Count; i++)
            {
                var (top, bottom) = bands[i];
                if (Extent(ink, width, top, bottom, 0) is not { } ext) return null;
                var hit = Cached(Panel, form, ink, width, height, top, bottom, ext);
                int x0 = ext.X0, x1 = ext.X1;
                if (hit is not null) cached++;
                else
                {
                    IEnumerable<string> candidates = bands.Count == 2 && i == 0 ? _streets
                        : street is not null && _mapsByStreet.TryGetValue(Normalize(street), out var ofStreet) ? ofStreet : _maps;
                    if (Best(Panel, candidates, ink, width, height, top, bottom, ext.X0, form) is not { } found) return null;
                    (hit, x0, x1) = (found.Text, found.Alignment.X0, found.Alignment.X1);
                    fonted++;
                }
                if (bands.Count == 2 && i == 0) street = hit;
                lines.Add(new TextLine(hit, 1.0, x0, top, x1 - x0 + 1, bottom - top + 1));
            }
        }
        else
        {
            if (bands.Count == 0) return null;
            var (top, bottom) = bands[0];
            if (Extent(ink, width, top, bottom, BarEndBlank) is not { } ext) return null;
            var hit = Cached(Bar, form, ink, width, height, top, bottom, ext);
            int x0 = ext.X0, x1 = ext.X1;
            if (hit is not null) cached++;
            else
            {
                if (Best(Bar, _barLines, ink, width, height, top, bottom, ext.X0, form) is not { } found) return null;
                (hit, x0, x1) = (found.Text, found.Alignment.X0, found.Alignment.X1);
                fonted++;
            }
            lines.Add(new TextLine(hit, 1.0, x0, top, x1 - x0 + 1, bottom - top + 1));
        }
        if (fonted > 0) { ByFont++; LastSource = MapNameSource.Font; }
        else { ByCache++; LastSource = MapNameSource.Cache; }
        return lines;
    }

    /// <summary>
    /// The text of the cached line equal to the band's ink between the extent's columns, or null. A hit whose text the font can spell
    /// must also be found on the band by the font; one that is not was a wrong read, and is dropped.
    /// </summary>
    private string? Cached(MapFont font, MapNameForm form, bool[] ink, int width, int height, int top, int bottom, (int X0, int X1) ext)
    {
        var (w, h) = (ext.X1 - ext.X0 + 1, bottom - top + 1);
        bool[]? trimmed = null;
        foreach (var line in _lines)
        {
            if (line.Form != form || line.Width != w || line.Height != h) continue;
            trimmed ??= Trim(ink, width, top, bottom, ext);
            if (!line.Bits.AsSpan().SequenceEqual(trimmed)) continue;
            if (font.CanRender(line.Text) && Fit(font, line.Text, ink, width, height, top, bottom, ext.X0, form) is null)
            {
                CacheRefuted++;
                Remove(line);
                Changed = true;
                return null;
            }
            return line.Text;
        }
        return null;
    }

    /// <summary>
    /// Where a text's glyphs fit the band (see <see cref="MapFont.Align"/>) with nothing but stray bits left of them and, right of them,
    /// nothing on the panel or blank columns up to <see cref="BarEndBlank"/> on the bar, before whatever follows; null when they do not.
    /// </summary>
    private static MapNameAlignment? Fit(MapFont font, string text, bool[] ink, int width, int height, int top, int bottom, int firstInk, MapNameForm form)
    {
        if (font.Align(text, ink, width, height, top, bottom, firstInk) is not { } a) return null;
        if (Strays(ink, width, top, bottom, 0, a.X0 - 1) > MapFont.StrayBits) return null;
        if (form == MapNameForm.Panel ? Strays(ink, width, top, bottom, a.X1 + 1, width - 1) > MapFont.StrayBits
            : Strays(ink, width, top, bottom, a.X1 + 1, Math.Min(width - 1, a.X1 + BarEndBlank)) > 0) return null;
        return a;
    }

    /// <summary>The one candidate that fits the band best (<see cref="Fit"/>); null when none fits or two fit equally.</summary>
    private static (string Text, MapNameAlignment Alignment)? Best(MapFont font, IEnumerable<string> candidates, bool[] ink, int width, int height, int top, int bottom, int firstInk, MapNameForm form)
    {
        (string Text, MapNameAlignment Alignment)? best = null;
        var second = int.MaxValue;
        foreach (var text in candidates)
        {
            if (!font.CanRender(text) || Fit(font, text, ink, width, height, top, bottom, firstInk, form) is not { } a) continue;
            if (best is null || a.Differing < best.Value.Alignment.Differing) { second = best?.Alignment.Differing ?? int.MaxValue; best = (text, a); }
            else if (a.Differing < second) second = a.Differing;
        }
        return best is { } b && b.Alignment.Differing < second ? b : null;
    }

    /// <summary>
    /// Learns from a read the catalog confirmed: the crop it was taken from, its form, and the region and map read. Each text band
    /// becomes a cached line under the catalog's spelling, and teaches the form's font; a band whose text the font can already spell
    /// but does not find on it (a read that was wrong, yet a real name) is refused. Returns true when anything was taken.
    /// </summary>
    public bool Learn(byte[] bgra, int width, int height, MapNameForm form, string? region, string? map)
    {
        Offered++;
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4 || Canonical(region, map) is not { } canon) return false;
        var (ink, split) = Bits(bgra, width, height, form);
        var bands = Bands(ink, width, height);
        string[] texts = form == MapNameForm.Panel
            ? (canon.Street.Length > 0 ? [canon.Street, canon.Map] : [canon.Map])
            : [canon.Street.Length > 0 ? $"{canon.Street} : {canon.Map}" : canon.Map];
        if (bands.Count != texts.Length) { Unsegmented++; return false; }
        var taken = false;
        for (var i = 0; i < texts.Length; i++)
        {
            var (top, bottom) = bands[i];
            if (Extent(ink, width, top, bottom, form == MapNameForm.Bar ? BarEndBlank : 0) is not { } ext) continue;
            var font = form == MapNameForm.Panel ? Panel : Bar;
            if (font.CanRender(texts[i]) && Fit(font, texts[i], ink, width, height, top, bottom, ext.X0, form) is null) { Contradicted++; continue; }
            if (Add(new Line(form, texts[i], ext.X1 - ext.X0 + 1, bottom - top + 1, Trim(ink, width, top, bottom, ext))))
            {
                LinesCached++;
                taken = true;
            }
            // On the bar whatever follows the name (the buttons' captions) is cleared, so the line's column groups are the name's alone.
            var lineInk = form == MapNameForm.Bar ? ClearRightOf(ink, width, height, ext.X1) : ink;
            var lineSplit = split is null ? null : form == MapNameForm.Bar ? ClearRightOf(split, width, height, ext.X1) : split;
            if (font.Learn(lineInk, lineSplit, width, height, top, bottom, texts[i])) { LinesTaught++; taken = true; }
        }
        if (taken) Changed = true;
        return taken;
    }

    /// <summary>The crop's ink bits for the form, and for the panel the stricter bits its letters stand apart at.</summary>
    public static (bool[] Ink, bool[]? Split) Bits(byte[] bgra, int width, int height, MapNameForm form)
    {
        var n = width * height;
        var ink = new bool[n];
        var split = form == MapNameForm.Panel ? new bool[n] : null;
        for (var p = 0; p < n; p++)
        {
            var luma = (bgra[p * 4] + 2 * bgra[p * 4 + 1] + bgra[p * 4 + 2]) >> 2;
            if (form == MapNameForm.Panel) { ink[p] = luma > PanelInkLuma; split![p] = luma > PanelSplitLuma; }
            else ink[p] = luma < BarInkLuma;
        }
        return (ink, split);
    }

    /// <summary>The text bands: runs of rows holding ink, joined within <see cref="BandJoinRows"/>, at least <see cref="MinBandRows"/> tall, and not a line across the crop.</summary>
    public static List<(int Top, int Bottom)> Bands(bool[] ink, int width, int height)
    {
        var runs = new List<(int Top, int Bottom)>();
        var start = -1;
        for (var y = 0; y <= height; y++)
        {
            var any = false;
            if (y < height) for (var x = 0; x < width && !any; x++) any = ink[y * width + x];
            if (any && start < 0) start = y;
            if (!any && start >= 0) { runs.Add((start, y - 1)); start = -1; }
        }
        var bands = new List<(int Top, int Bottom)>();
        foreach (var r in runs)
        {
            if (bands.Count > 0 && r.Top - bands[^1].Bottom <= BandJoinRows) bands[^1] = (bands[^1].Top, r.Bottom);
            else bands.Add(r);
        }
        return bands.Where(b => b.Bottom - b.Top + 1 >= MinBandRows && !Enumerable.Range(b.Top, b.Bottom - b.Top + 1).Any(y => Enumerable.Range(0, width).All(x => ink[y * width + x]))).ToList();
    }

    /// <summary>
    /// The band's first ink column and its last: the last of all when <paramref name="endBlank"/> is 0, else the last before the
    /// first run of that many blank columns. Null for a band without ink.
    /// </summary>
    public static (int X0, int X1)? Extent(bool[] ink, int width, int top, int bottom, int endBlank)
    {
        bool Column(int x) { for (var y = top; y <= bottom; y++) if (ink[y * width + x]) return true; return false; }
        var x0 = -1;
        for (var x = 0; x < width && x0 < 0; x++) if (Column(x)) x0 = x;
        if (x0 < 0) return null;
        int last = x0, blank = 0;
        for (var x = x0 + 1; x < width; x++)
        {
            if (Column(x)) { last = x; blank = 0; }
            else if (endBlank > 0 && ++blank >= endBlank) break;
        }
        return (x0, last);
    }

    private static int Strays(bool[] ink, int width, int top, int bottom, int fromColumn, int toColumn)
    {
        var n = 0;
        for (var y = top; y <= bottom; y++) for (var x = Math.Max(0, fromColumn); x <= Math.Min(width - 1, toColumn); x++) if (ink[y * width + x]) n++;
        return n;
    }

    private static bool[] Trim(bool[] ink, int width, int top, int bottom, (int X0, int X1) ext)
    {
        var (w, h) = (ext.X1 - ext.X0 + 1, bottom - top + 1);
        var bits = new bool[w * h];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) bits[y * w + x] = ink[(top + y) * width + ext.X0 + x];
        return bits;
    }

    private static bool[] ClearRightOf(bool[] bits, int width, int height, int lastColumn)
    {
        var copy = (bool[])bits.Clone();
        for (var y = 0; y < height; y++) for (var x = lastColumn + 1; x < width; x++) copy[y * width + x] = false;
        return copy;
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartObject("fonts");
            w.WritePropertyName("panel"); Panel.WriteTo(w);
            w.WritePropertyName("bar"); Bar.WriteTo(w);
            w.WriteEndObject();
            w.WriteStartArray("lines");
            foreach (var line in _lines.OrderBy(l => l.Form).ThenBy(l => l.Text, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("form", line.Form == MapNameForm.Panel ? "panel" : "bar");
                w.WriteString("text", line.Text);
                w.WriteNumber("width", line.Width);
                w.WriteNumber("height", line.Height);
                w.WriteBase64String("bits", Pack(line.Bits));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static MapNameFonts FromJson(string json)
    {
        var fonts = new MapNameFonts();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("map font: another file version");
        var f = root.GetProperty("fonts");
        fonts.Panel = MapFont.Read(f.GetProperty("panel"), "panel");
        fonts.Bar = MapFont.Read(f.GetProperty("bar"), "bar");
        foreach (var e in root.GetProperty("lines").EnumerateArray())
        {
            var form = e.GetProperty("form").GetString() switch { "panel" => MapNameForm.Panel, "bar" => MapNameForm.Bar, var other => throw new InvalidDataException($"map font: a line of form '{other}'") };
            var text = e.GetProperty("text").GetString() ?? throw new InvalidDataException("map font: a line without text");
            int w = e.GetProperty("width").GetInt32(), h = e.GetProperty("height").GetInt32();
            if (w < 1 || h < 1 || w > 4096 || h > 256) throw new InvalidDataException($"map font: a line of {w}x{h}");
            fonts.Add(new Line(form, text, w, h, Unpack(e.GetProperty("bits").GetBytesFromBase64(), w * h)));
        }
        return fonts;
    }

    private static byte[] Pack(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++) if (bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        return bytes;
    }

    private static bool[] Unpack(byte[] bytes, int count)
    {
        if (bytes.Length != (count + 7) / 8) throw new InvalidDataException($"map font: {bytes.Length} bytes of bits for {count}");
        var bits = new bool[count];
        for (var i = 0; i < count; i++) bits[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
        return bits;
    }

    /// <summary>Writes the file beside the path and moves it into place, so a reader never sees a half-written file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, overwrite: true);
        Changed = false;
    }

    public static MapNameFonts? Load(string path) => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
}
