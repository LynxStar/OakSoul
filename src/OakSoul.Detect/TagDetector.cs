using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OakSoul.Capture;
using OakSoul.Vision.Readers;

namespace OakSoul.Detect;

/// <summary>A name-tag box found in a frame, in viewport pixels (inclusive), with its OCR'd text when read.</summary>
public sealed record TagBox(int X0, int Y0, int X1, int Y1, double TextFraction, ulong Fingerprint)
{
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;
    public double CentreX => (X0 + X1) / 2.0;
    public string? Text { get; set; }
    public int? Level { get; set; }
    public string? Name { get; set; }
    public bool FromCache { get; set; }

    /// <summary>The tag's text is still being read on the OCR worker: <see cref="Text"/> is null until it comes back.</summary>
    public bool Pending { get; set; }

    /// <summary>
    /// On the frame a tag's text came in later than its first sighting: how many frames later. Null on every other frame, while
    /// pending, when the text was read in the sighting's own frame, and for a tag the cache file knew.
    /// </summary>
    public int? NamedAfter { get; set; }

    /// <summary>
    /// What the tag labels, set by the detector after tracking: <c>mob</c>, <c>pet</c>, <c>player</c>, <c>self</c> (the own tag)
    /// or <c>none</c>; a tag reading a level badge is a mob's whatever took it. <see cref="OwnerTrack"/> is the labelled entity's track.
    /// </summary>
    public string? Owner { get; set; }
    public int? OwnerTrack { get; set; }
}

/// <summary>
/// Finds name tags: the client draws every tag (own, other players, pets, and mobs once they are hit) as near-white
/// text on a translucent black box 16 rows tall, mobs with a smaller level badge box just left of the name box.
/// Against the mosaic the box is unmistakable: the background darkened to 0.30 to 0.40 of its brightness, with the
/// same factor on all three channels. The detector stacks horizontal runs of box-or-text pixels into rectangles of
/// tag height, checks their text content, fingerprints the binarized text and reads each distinct fingerprint once:
/// on the <see cref="OcrQueue"/>'s worker thread when one is given, so no frame waits for Vision, and a box whose text has
/// not come back is handed out <see cref="TagBox.Pending"/>, as a box that read nothing is, until it has. One detector
/// serves every map's <see cref="EntityDetector"/>: tags render alike everywhere, and the cache can be kept in a file
/// between sessions (<see cref="Save"/>, <see cref="Load"/>).
/// </summary>
public sealed partial class TagDetector
{
    /// <summary>
    /// Box height range in rows. Every single tag measured on fx-combat (own, pet, Fire Boar, Dark Axe Stump) is 16
    /// rows; a box partly hidden by a sprite or the HUD edge, or merged with an overlapping one, ends up between.
    /// </summary>
    public const int MinHeight = 12, MaxHeight = 19;

    /// <summary>Shortest run that can be part of a box: the shortest name seen ("Mara") gives a box 32 wide.</summary>
    public const int MinWidth = 14;

    /// <summary>Background pixels a row run bridges: glyph edges that happen to match it, or the 1-2 column seam between badge and name.</summary>
    public const int MaxGap = 3;

    /// <summary>Gap of solid pixels bridged: glyph edges inside the box (up to five in a row inside "Boar").</summary>
    public const int MaxSolidGap = 7;

    /// <summary>
    /// Fraction of a box's pixels that must be text: glyphs covered 13% of the pet's box and 15% of a Fire Boar's on
    /// fx-combat; far fewer is a dark sprite part whose shading happens to match, far more a bright sprite or cloud.
    /// </summary>
    public const double MinText = 0.06, MaxText = 0.6;

    /// <summary>
    /// Fraction of a box's pixels that must be darkened background (a glyph's anti-aliased edge can match the
    /// background exactly, so the rest is text, unchanged pixels and a little sprite overlap).
    /// </summary>
    public const double MinShade = 0.3;

    /// <summary>Largest fraction of a box that may be solid foreground or unreliable background: a sprite overlapping it.</summary>
    public const double MaxSolid = 0.3;

    /// <summary>
    /// Two fingerprints within this fraction of differing text bits are the same tag: the anti-aliased glyph edges
    /// blend with whatever is behind the box, so the binarized text flips a few edge pixels from frame to frame
    /// (chosen, not tuned; fx-combat needed 227 OCR calls for 541 tag sightings).
    /// </summary>
    public const double FuzzyBits = 0.08;

    /// <summary>
    /// One box's text as the detector keeps it: its size, the binarized text bits it was first seen with, and the text read,
    /// or the read still in flight on the worker. Entries are matched in the order they were added, the first within
    /// <see cref="FuzzyBits"/> taken, so a replay with the worker waited on reads exactly as the synchronous detector did.
    /// </summary>
    private sealed class CacheEntry
    {
        public required int Width, Height;
        public required ulong Hash;
        public required bool[] Bits;
        public required int Ones;
        public string? Text;
        public int? Level;
        public string? Name;
        /// <summary>The read in flight, until it is drained; null once the text is in.</summary>
        public OcrRequest? Request;
        public bool Pending => Request is not null;
        /// <summary>The frame of the first sighting; -1 for an entry the cache file brought.</summary>
        public long FirstFrame;
        /// <summary>The frame the text came in on, and how many frames after the first sighting that was.</summary>
        public long ArrivedFrame = -1;
        public int? NamedAfter;
        public int Sightings;
        public bool FromFile;
    }

    private readonly List<CacheEntry> _cache = [];
    private readonly Dictionary<ulong, CacheEntry> _byHash = [];
    private readonly List<CacheEntry> _inFlight = [];
    private readonly List<int> _namedAfter = [];
    private readonly List<double> _readLatencyMs = [];

    /// <summary>The worker the reads go to; null reads on the calling thread, as the detector did before it had a worker.</summary>
    public OcrQueue? Queue { get; init; }

    /// <summary>
    /// With a <see cref="Queue"/>: wait for each read before going on, so every frame sees its tags' text at once (the worker still
    /// does the reading). For tests and measurement, not for watching.
    /// </summary>
    public bool Blocking { get; init; }

    /// <summary>
    /// The pet's tag as binarized text bits, learned from the first tag box read as the pet's name alone (the name is given,
    /// so this happens the first time the pet's tag shows by itself). Tags drawn side by side are found as one box ("Mara
    /// wild Boar" on fx-combat-exact's frame 751); <see cref="SplitPet"/> finds the pet's tag inside such a box by these bits
    /// and cuts the box at the tag's edges, so each part is read on its own.
    /// </summary>
    private (bool[] Bits, int Width, int Height, int Ones)? _petTag;

    /// <summary>Whether a read is the pet's name alone (set by the detector from the tracker's names).</summary>
    public Func<string?, bool>? IsPetText { get; set; }

    /// <summary>Tag boxes split around the pet's tag since construction.</summary>
    public int PetSplits { get; private set; }

    /// <summary>Rows the pet's tag may sit above or below a box it was found in.</summary>
    public const int SplitRows = 6;

    /// <summary>Debug: <c>OAKSOUL_TAG_DUMP=dir</c> writes every crop sent to OCR, as captured, for trying OCR settings offline.</summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("OAKSOUL_TAG_DUMP");

    /// <summary>Reads made (queued or run), and the recognition time they took, wherever it ran.</summary>
    public int OcrCalls { get; private set; }
    public double OcrMilliseconds { get; private set; }

    /// <summary>Milliseconds the calling thread spent inside a recognition or waiting for one (none when the worker reads unwaited).</summary>
    public double WaitedMilliseconds { get; private set; }

    /// <summary>Milliseconds the calling thread spent in <see cref="Read"/> altogether: lookups, crops, queueing and any waiting.</summary>
    public double ReadMilliseconds { get; private set; }

    /// <summary>Boxes read since construction, and how many of them were handed out with their text still in flight.</summary>
    public int TagSightings { get; private set; }
    public int PendingSightings { get; private set; }

    /// <summary>Reads that came back with an error from the recogniser (their text is null).</summary>
    public int OcrErrors { get; private set; }

    /// <summary>Reads still in flight on the worker.</summary>
    public int InFlight => _inFlight.Count;

    /// <summary>Entries in the cache, those with a text, and those the cache file brought.</summary>
    public int Entries => _cache.Count;
    public int ReadableEntries => _cache.Count(e => e.Text is not null);
    public int Loaded { get; private set; }

    /// <summary>For every read that came back: the frames from the tag's first sighting to its text (0 when read at once), in order.</summary>
    public IReadOnlyList<int> NamedAfterFrames => _namedAfter;

    /// <summary>For every read that came back: milliseconds from queueing to completion (the wait behind other reads included).</summary>
    public IReadOnlyList<double> ReadLatencyMs => _readLatencyMs;

    public IReadOnlyCollection<string> DistinctTexts => _cache.Where(c => !string.IsNullOrWhiteSpace(c.Text)).Select(c => c.Text!).Distinct().ToList();

    private static bool TagLike(byte c) => c is (byte)PixelClass.Shade or (byte)PixelClass.Bright or (byte)PixelClass.BrightFog or (byte)PixelClass.DarkConsistent;

    /// <summary>Finds tag boxes in the classified viewport of the last <see cref="ForegroundExtractor.Difference"/>.</summary>
    public List<TagBox> Find(ForegroundExtractor fg, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport)
    {
        int w = fg.Width, h = fg.Height;
        var cls = fg.Classes;
        var found = new List<TagBox>();
        // Open stacks: x range and first row; one per run of the previous row that continued.
        var open = new List<(int X0, int X1, int Y0, List<(int A, int B)> Runs)>();
        var runs = new List<(int A, int B)>();
        for (var y = 0; y <= h; y++)
        {
            runs.Clear();
            if (y < h)
            {
                var row = y * w;
                int start = -1, lastOn = -100;
                var gapOther = 0;
                for (var x = 0; x < w; x++)
                {
                    var c = cls[row + x];
                    if (!TagLike(c))
                    {
                        // Anti-aliased glyph edges blend white into the darkened box and read as solid or steam, several
                        // in a row, and now and then exactly as the background.
                        if (start >= 0 && c is not ((byte)PixelClass.Solid or (byte)PixelClass.Fog)) gapOther++;
                        if (start >= 0 && (x - lastOn > MaxSolidGap || gapOther > MaxGap))
                        {
                            if (lastOn - start + 1 >= MinWidth) runs.Add((start, lastOn));
                            start = -1;
                        }
                        continue;
                    }
                    if (start < 0) start = x;
                    lastOn = x;
                    gapOther = 0;
                }
                if (start >= 0 && lastOn - start + 1 >= MinWidth) runs.Add((start, lastOn));
            }
            var next = new List<(int X0, int X1, int Y0, List<(int A, int B)> Runs)>();
            var used = new bool[runs.Count];
            foreach (var s in open)
            {
                var continued = false;
                for (var r = 0; r < runs.Count; r++)
                {
                    var (a, b) = runs[r];
                    var overlap = Math.Min(b, s.X1) - Math.Max(a, s.X0) + 1;
                    // A box row covers mostly the same columns as the one above: give or take a rounded corner, or the level badge, which is two rows shorter than the name box.
                    if (overlap >= 0.6 * (s.X1 - s.X0 + 1) && overlap >= 0.6 * (b - a + 1))
                    {
                        s.Runs.Add((a, b));
                        next.Add((Math.Min(s.X0, a), Math.Max(s.X1, b), s.Y0, s.Runs));
                        used[r] = continued = true;
                        break;
                    }
                }
                if (!continued) Close(s.Y0, y - 1, s.Runs, cls, w, frame, frameWidth, viewport, found);
            }
            for (var r = 0; r < runs.Count; r++)
                if (!used[r]) next.Add((runs[r].A, runs[r].B, y, [runs[r]]));
            open = next;
        }
        return Dedupe(found);
    }

    private void Close(int y0, int y1, List<(int A, int B)> runs, byte[] cls, int w, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, List<TagBox> found)
    {
        var height = y1 - y0 + 1;
        if (height < MinHeight) return;
        // Tall stacks are a box merged with dark sprite rows above or below it: keep the best 16-row window.
        var x0s = runs.Select(r => r.A).Order().ToList();
        var x1s = runs.Select(r => r.B).Order().ToList();
        int bx0 = x0s[x0s.Count / 2], bx1 = x1s[x1s.Count / 2];
        if (bx1 - bx0 + 1 < MinWidth) return;
        var windows = height <= MaxHeight ? [(y0, y1)] : Enumerable.Range(y0, height - 16 + 1).Select(t => (t, t + 15)).ToList();
        TagBox? best = null;
        var bestScore = 0.0;
        foreach (var (wy0, wy1) in windows)
        {
            int text = 0, shade = 0, solid = 0, n = 0;
            for (var y = wy0; y <= wy1; y++)
                for (var x = bx0; x <= bx1; x++)
                {
                    var c = (PixelClass)cls[y * w + x];
                    n++;
                    if (c is PixelClass.Bright or PixelClass.BrightFog) text++;
                    else if (c is PixelClass.Shade or PixelClass.DarkConsistent) shade++;
                    else if (c is PixelClass.Solid or PixelClass.Ignored) solid++;
                }
            var tf = (double)text / n;
            var cf = (double)shade / n;
            if (tf < MinText || tf > MaxText || cf < MinShade || (double)solid / n > MaxSolid) continue;
            if (cf > bestScore) { bestScore = cf; best = new TagBox(bx0, wy0, bx1, wy1, tf, Fingerprint(frame, frameWidth, viewport, bx0, wy0, bx1, wy1)); }
        }
        if (best is not null) found.Add(best);
    }

    private static List<TagBox> Dedupe(List<TagBox> boxes)
    {
        var result = new List<TagBox>();
        foreach (var b in boxes.OrderByDescending(b => b.Width * b.Height))
            if (!result.Any(r => Math.Min(r.X1, b.X1) - Math.Max(r.X0, b.X0) > 0 && Math.Min(r.Y1, b.Y1) - Math.Max(r.Y0, b.Y0) > 4)) result.Add(b);
        return result;
    }

    /// <summary>Hash of the binarized text pixels (luma at or above <see cref="ForegroundExtractor.TextLuma"/>) inside the box.</summary>
    private static ulong Fingerprint(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, int x0, int y0, int x1, int y1)
    {
        var hash = 14695981039346656037UL;
        hash = (hash ^ (ulong)(x1 - x0)) * 1099511628211UL;
        hash = (hash ^ (ulong)(y1 - y0)) * 1099511628211UL;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var o = ((y + viewport.Y) * frameWidth + x + viewport.X) * 4;
                var bit = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2 >= ForegroundExtractor.TextLuma ? 1UL : 0UL;
                hash = (hash ^ bit) * 1099511628211UL;
            }
        return hash;
    }

    private static bool[] Bits(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, TagBox b)
    {
        var bits = new bool[b.Width * b.Height];
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var o = ((b.Y0 + y + viewport.Y) * frameWidth + b.X0 + x + viewport.X) * 4;
                bits[y * b.Width + x] = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2 >= ForegroundExtractor.TextLuma;
            }
        return bits;
    }

    /// <summary>
    /// Gives each box its text: from the cache when the fingerprint (or a near-identical one) was read before,
    /// otherwise by OCR of the box and a little margin, upscaled. The crop of the pixel-exact frame is binarized first,
    /// light text as black ink on white paper (<see cref="OcrInput.InkOnPaper"/>, as the level badge is read), not
    /// softened as the status bar and map name are: on fx-exact's five crops of the pet's tag Vision read Mara on 2 as
    /// captured (lara and a cut-off Ma on the others), on none softened (empty or "BJBI"), and on 3 binarized, and the
    /// mob tag "Lv. 17 Axe Stump" as captured "Lw, 17", softened "Lw, 17", binarized "Lv. 17"; on fx-anttunnel-open's
    /// player and pet tags (Slivan, Black Kitty) all three read alike. With a <see cref="Queue"/> the reads that came back
    /// since the last call are taken in first, and a box whose read is still out is handed back pending.
    /// </summary>
    /// <param name="frameIndex">The frame's index in the run, for how many frames a tag waited for its text.</param>
    public void Read(List<TagBox> boxes, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, bool ocr = true, long frameIndex = 0)
    {
        var t0 = Stopwatch.GetTimestamp();
        Drain(frameIndex);
        ReadBoxes(boxes, frame, frameWidth, viewport, ocr, frameIndex);
        if (_petTag is null && IsPetText is { } isPet)
            foreach (var b in boxes)
                if (isPet(b.Text) && b.Width <= 80)
                {
                    var bits = Bits(frame, frameWidth, viewport, b);
                    _petTag = (bits, b.Width, b.Height, bits.Count(v => v));
                    break;
                }
        ReadMilliseconds += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// Cuts boxes wider than the pet's tag that hold it (its text bits within <see cref="FuzzyBits"/> at some column offset,
    /// a row up or down allowed) into the pet's tag and the parts beside it at least <see cref="MinWidth"/> wide.
    /// </summary>
    public void SplitPet(List<TagBox> boxes, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport)
    {
        if (_petTag is not { } t) return;
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (b.Width < t.Width + MinWidth || Math.Abs(b.Height - t.Height) > 3) continue;
            var bits = Bits(frame, frameWidth, viewport, b);
            // Two tags side by side need not share a top: the pet's may sit a few rows above or below the box found.
            var best = (Diff: int.MaxValue, Dx: 0, Dy: 0, Ones: 1);
            for (var dy = -SplitRows; dy <= SplitRows; dy++)
            {
                int y0 = Math.Max(0, -dy), y1 = Math.Min(t.Height, b.Height - dy), onesIn = 0;
                for (var y = y0; y < y1; y++)
                    for (var x = 0; x < t.Width; x++) if (t.Bits[y * t.Width + x]) onesIn++;
                if (onesIn < 0.8 * t.Ones) continue;
                for (var dx = 0; dx + t.Width <= b.Width; dx++)
                {
                    var diff = 0;
                    for (var y = y0; y < y1; y++)
                        for (var x = 0; x < t.Width; x++)
                            if (t.Bits[y * t.Width + x] != bits[(y + dy) * b.Width + dx + x]) diff++;
                    if ((double)diff / onesIn < (double)best.Diff / best.Ones) best = (diff, dx, dy, onesIn);
                }
            }
            if (best.Diff > FuzzyBits * best.Ones) continue;
            int px0 = b.X0 + best.Dx, px1 = px0 + t.Width - 1, py0 = Math.Max(0, b.Y0 + best.Dy);
            var parts = new List<TagBox> { Box(px0, py0, px1, Math.Min(viewport.Height - 1, py0 + t.Height - 1), frame, frameWidth, viewport) };
            // The neighbour tag's box starts a column or two past the pet's.
            if (px0 - 2 - b.X0 + 1 >= MinWidth) parts.Add(Box(b.X0, b.Y0, px0 - 2, b.Y1, frame, frameWidth, viewport));
            if (b.X1 - (px1 + 2) + 1 >= MinWidth) parts.Add(Box(px1 + 2, b.Y0, b.X1, b.Y1, frame, frameWidth, viewport));
            boxes.RemoveAt(i);
            boxes.InsertRange(i, parts);
            i += parts.Count - 1;
            PetSplits++;
        }
    }

    private static TagBox Box(int x0, int y0, int x1, int y1, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport)
    {
        var text = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                var o = ((y + viewport.Y) * frameWidth + x + viewport.X) * 4;
                if ((frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2 >= ForegroundExtractor.TextLuma) text++;
            }
        return new TagBox(x0, y0, x1, y1, (double)text / ((x1 - x0 + 1) * (y1 - y0 + 1)), Fingerprint(frame, frameWidth, viewport, x0, y0, x1, y1));
    }

    /// <summary>Takes in the reads that came back since the last call: their entries get their text, and stop being pending.</summary>
    private void Drain(long frameIndex)
    {
        for (var i = 0; i < _inFlight.Count; i++)
        {
            if (!_inFlight[i].Request!.Done) continue;
            Complete(_inFlight[i], frameIndex);
            _inFlight.RemoveAt(i--);
        }
    }

    private void Complete(CacheEntry e, long frameIndex)
    {
        var r = e.Request!;
        e.Request = null;
        OcrMilliseconds += r.Milliseconds;
        if (r.Error is not null)
        {
            // The recogniser failed: the entry is forgotten, so the tag is read again when it is seen again.
            OcrErrors++;
            Forget(e);
            return;
        }
        var text = TextOf(r.Lines);
        var (level, name) = Parse(text);
        e.Text = text; e.Level = level; e.Name = name;
        e.NamedAfter = (int)Math.Max(0, frameIndex - e.FirstFrame);
        e.ArrivedFrame = frameIndex;
        _namedAfter.Add(e.NamedAfter.Value);
        _readLatencyMs.Add(r.Latency);
    }

    private void Forget(CacheEntry e)
    {
        _cache.Remove(e);
        if (_byHash.TryGetValue(e.Hash, out var held) && ReferenceEquals(held, e)) _byHash.Remove(e.Hash);
    }

    private void ReadBoxes(List<TagBox> boxes, ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, bool ocr, long frameIndex)
    {
        foreach (var b in boxes)
        {
            TagSightings++;
            bool[]? bits = null;
            if (!_byHash.TryGetValue(b.Fingerprint, out var hit))
            {
                bits = Bits(frame, frameWidth, viewport, b);
                hit = Fuzzy(bits, b.Width, b.Height);
            }
            var fresh = hit is null;
            if (hit is null)
            {
                if (!ocr) continue;
                hit = new CacheEntry { Width = b.Width, Height = b.Height, Hash = b.Fingerprint, Bits = bits!, Ones = bits!.Count(v => v), FirstFrame = frameIndex };
                Add(hit);
                var crop = Crop(frame, frameWidth, viewport, b, out var cw, out var ch);
                OcrCalls++;
                var ink = OcrInput.InkOnPaper(crop, cw, ch);
                if (Queue is null)
                {
                    var sw = Stopwatch.StartNew();
                    IReadOnlyList<TextLine> lines;
                    try { lines = TextRecognizer.Recognize(ink, cw, ch, upscale: 4); }
                    catch (InvalidOperationException)
                    {
                        // As before there was a worker, but the failed read is forgotten rather than thrown: the tag is read again when seen again.
                        OcrErrors++;
                        Forget(hit);
                        WaitedMilliseconds += sw.Elapsed.TotalMilliseconds;
                        continue;
                    }
                    var ms = sw.Elapsed.TotalMilliseconds;
                    WaitedMilliseconds += ms; OcrMilliseconds += ms;
                    var text = TextOf(lines);
                    (hit.Level, hit.Name) = Parse(text);
                    hit.Text = text; hit.NamedAfter = 0; hit.ArrivedFrame = frameIndex;
                    _namedAfter.Add(0); _readLatencyMs.Add(ms);
                }
                else
                {
                    hit.Request = Queue.Enqueue(ink, cw, ch, 4);
                    if (Blocking)
                    {
                        var sw = Stopwatch.StartNew();
                        hit.Request.Wait();
                        WaitedMilliseconds += sw.Elapsed.TotalMilliseconds;
                        Complete(hit, frameIndex);
                        if (hit.Text is null && hit.Request is null && !_cache.Contains(hit)) continue; // the read failed and was forgotten
                    }
                    else _inFlight.Add(hit);
                }
            }
            hit.Sightings++;
            b.Text = hit.Text; b.Level = hit.Level; b.Name = hit.Name;
            b.FromCache = !fresh;
            b.Pending = hit.Pending;
            // Told once, on the frame the text came in, when it came in later than the first sighting: the stream's record of a late name.
            b.NamedAfter = hit.ArrivedFrame == frameIndex && hit.NamedAfter is > 0 ? hit.NamedAfter : null;
            if (hit.Pending) PendingSightings++;
        }
    }

    private void Add(CacheEntry e)
    {
        _cache.Add(e);
        _byHash.TryAdd(e.Hash, e);
    }

    /// <summary>The first entry (in the order added) of about the box's size whose bits differ within <see cref="FuzzyBits"/>, or null.</summary>
    private CacheEntry? Fuzzy(bool[] bits, int width, int height)
    {
        var ones = bits.Count(v => v);
        foreach (var c in _cache)
        {
            if (Math.Abs(c.Width - width) > 2 || Math.Abs(c.Height - height) > 1) continue;
            var limit = (int)Math.Floor(FuzzyBits * Math.Max(ones, c.Ones));
            if (Differing(c, bits, width, height, limit) <= limit) return c;
        }
        return null;
    }

    /// <summary>
    /// Differing bits between an entry and a box at the same top-left, the fewest over one-column shifts either way (the box
    /// edges are found to a pixel or two). A shift stops counting once it passes <paramref name="limit"/>: only whether the
    /// best shift is within the limit matters, so the answer is the same and most entries are rejected after a row or two.
    /// </summary>
    private static int Differing(CacheEntry c, bool[] bits, int w, int h, int limit)
    {
        var best = int.MaxValue;
        int rows = Math.Min(h, c.Height), cols = Math.Min(w, c.Width);
        for (var dx = -1; dx <= 1; dx++)
        {
            var diff = 0;
            for (var y = 0; y < rows && diff <= limit; y++)
                for (var x = 0; x < cols; x++)
                {
                    var cx = x + dx;
                    var a = (uint)cx < (uint)c.Width && c.Bits[y * c.Width + cx];
                    if (a != bits[y * w + x]) diff++;
                }
            best = Math.Min(best, diff);
        }
        return best;
    }

    /// <summary>The box and a small margin, copied out of the frame as captured (a wider crop picked up the pet's tag and sprite parts as stray letters).</summary>
    private byte[] Crop(ReadOnlySpan<byte> frame, int frameWidth, CaptureRect viewport, TagBox b, out int w, out int h)
    {
        int x0 = Math.Max(0, b.X0 + viewport.X - 3), y0 = Math.Max(0, b.Y0 + viewport.Y - 2);
        int x1 = Math.Min(frameWidth - 1, b.X1 + viewport.X + 3), y1 = Math.Min(viewport.Y + viewport.Height - 1, b.Y1 + viewport.Y + 2);
        w = x1 - x0 + 1; h = y1 - y0 + 1;
        var crop = new byte[w * h * 4];
        for (var y = 0; y < h; y++) frame.Slice(((y0 + y) * frameWidth + x0) * 4, w * 4).CopyTo(crop.AsSpan(y * w * 4));
        if (DumpDir is { } dump) { Directory.CreateDirectory(dump); Png.WriteBgra(Path.Combine(dump, $"tag{OcrCalls:000}_{x0}_{y0}.png"), crop, w, h); }
        return crop;
    }

    /// <summary>
    /// The text of a read: fragments of other tags and sprite edges come back as two- and three-letter junk ("eJe", "PJP", "M"),
    /// so the lines kept can be a name (four characters or more) or part of a level badge, joined left to right; null when none.
    /// </summary>
    public static string? TextOf(IReadOnlyList<TextLine> lines)
    {
        var kept = lines.Where(l => l.Confidence >= 0.5 && (l.Text.Trim().Length >= 4 || l.Text.Any(char.IsDigit) || l.Text.TrimStart().StartsWith('L'))).ToList();
        if (kept.Count == 0) return null;
        return string.Join(" ", kept.OrderBy(l => l.X).Select(l => l.Text.Trim())).Trim();
    }

    /// <summary>
    /// Writes the entries that have a text (the reads that returned nothing are left out: a fragment's bits rarely come
    /// back) as JSON, in the order they were added, so a later session matches them in the same order. Returns how many.
    /// </summary>
    public int Save(string path)
    {
        var readable = _cache.Where(e => e.Text is not null && !e.Pending).ToList();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartArray("entries");
            foreach (var e in readable)
            {
                w.WriteStartObject();
                w.WriteNumber("w", e.Width);
                w.WriteNumber("h", e.Height);
                w.WriteString("hash", e.Hash.ToString("x16", CultureInfo.InvariantCulture));
                w.WriteBase64String("bits", Pack(e.Bits));
                w.WriteString("text", e.Text);
                if (e.Level is { } level) w.WriteNumber("level", level);
                if (e.Name is { } name) w.WriteString("name", name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        // Written beside and moved into place, so a reader never sees a half-written file.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, overwrite: true);
        return readable.Count;
    }

    /// <summary>Adds the entries of a file <see cref="Save"/> wrote, before anything read in this session; returns how many.</summary>
    public int Load(string path)
    {
        if (!File.Exists(path)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var n = 0;
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            int w = e.GetProperty("w").GetInt32(), h = e.GetProperty("h").GetInt32();
            var hash = ulong.Parse(e.GetProperty("hash").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (_byHash.ContainsKey(hash)) continue;
            var bits = Unpack(e.GetProperty("bits").GetBytesFromBase64(), w * h);
            Add(new CacheEntry
            {
                Width = w, Height = h, Hash = hash, Bits = bits, Ones = bits.Count(v => v),
                Text = e.GetProperty("text").GetString(),
                Level = e.TryGetProperty("level", out var level) ? level.GetInt32() : null,
                Name = e.TryGetProperty("name", out var name) ? name.GetString() : null,
                FirstFrame = -1, FromFile = true,
            });
            n++;
        }
        Loaded += n;
        return n;
    }

    private static byte[] Pack(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++) if (bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        return bytes;
    }

    private static bool[] Unpack(byte[] bytes, int count)
    {
        var bits = new bool[count];
        for (var i = 0; i < count && (i >> 3) < bytes.Length; i++) bits[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
        return bits;
    }

    /// <summary>
    /// "Lv. 23 Dark Axe Stump" to level and name. Vision reads the tiny badge as "Lv. 23", "Lv, 23", "LV.23", "Lw, 11",
    /// "Lx23", ":23" or just "23", so the level is the last number of one to three digits followed by a capitalised
    /// word, and the name is the text after it (whatever precedes it is badge debris or a neighbouring tag). A badge
    /// whose number is lost still marks a mob, with level 0. A badge with its number before a name whose first letter read
    /// in lower case ("Lv, 25 vvild Boar" on fx-combat-exact's video frame 5, the W read as vv) gives that number: before,
    /// the level was lost and the number became part of the name ("25 vvild Boar").
    /// </summary>
    public static (int? Level, string? Name) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        // Pure, and asked of the same few thousand reads again and again every frame: kept, so the regexes and their strings run once per read.
        if (ParseMemo.TryGetValue(text, out var known)) return known;
        if (ParseMemo.Count >= MemoLimit) ParseMemo.Clear();
        return ParseMemo[text] = ParseRead(text);
    }

    private const int MemoLimit = 50_000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int? Level, string? Name)> ParseMemo = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> BadgeMemo = new(StringComparer.Ordinal);

    private static (int? Level, string? Name) ParseRead(string text)
    {
        var m = LevelPattern().Matches(text).LastOrDefault();
        if (m is not null && int.TryParse(m.Groups[1].Value, out var level))
        {
            var name = CleanName(text[(m.Index + m.Length)..]);
            return (level, name);
        }
        var numbered = NumberedBadgePattern().Matches(text).LastOrDefault();
        if (numbered is not null && int.TryParse(numbered.Groups[1].Value, out var badgeLevel) && CleanName(text[(numbered.Index + numbered.Length)..]) is { } badgeName)
            return (badgeLevel, badgeName);
        // A badge whose number did not read ("LV Fire"): a mob, level unknown (0).
        var badge = BadgePattern().Match(text);
        if (badge.Success) return (0, CleanName(text[(badge.Index + badge.Length)..]));
        return (null, CleanName(text));
    }

    /// <summary>Whether a read shows a level badge: one <see cref="Parse"/> gives a level for, or a badge with its number and no name ("Lw,23").</summary>
    public static bool HasBadge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (BadgeMemo.TryGetValue(text, out var known)) return known;
        if (BadgeMemo.Count >= MemoLimit) BadgeMemo.Clear();
        return BadgeMemo[text] = Parse(text).Level is not null || NumberedBadgePattern().IsMatch(text);
    }

    private static string? CleanName(string s)
    {
        var name = CollapseSpaces(s.Trim(' ', '|', '-', '.', ',', ':', ';', '*', ']', '['));
        return name.Count(char.IsLetter) >= 3 ? name : null;
    }

    private static string CollapseSpaces(string s) => SpacePattern().Replace(s, " ");

    [GeneratedRegex(@"(\d{1,3})[^\dA-Za-z]*(?=[A-Z])")]
    private static partial Regex LevelPattern();

    [GeneratedRegex(@"^\W*L[vVwWxy][.,:;]?(?=\s|$)")]
    private static partial Regex BadgePattern();

    [GeneratedRegex(@"(?<![A-Za-z])L[vVwWxy][.,:;]?\s*(\d{1,3})(?!\d)")]
    private static partial Regex NumberedBadgePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacePattern();
}
