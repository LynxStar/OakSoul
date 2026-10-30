using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>
/// The blob view of a debug frame (OAKSOUL_DEBUG_DUMP): what the blob step saw, every pixel in one solid colour per kind, so
/// the excluded areas, what the background or an earlier step explained, and each blob read apart at a glance.
/// </summary>
public sealed partial class EntityDetector
{
    // The foreground and the blobs as the blob step left them (taken on debug frames only).
    private byte[]? _viewMask;
    private List<Blob>? _viewBlobs;
    private HashSet<Blob>? _viewPredicted;

    private void SnapshotBlobs(List<Blob> blobs, IEnumerable<Blob> predicted)
    {
        _viewMask = (byte[])_fg.Mask.Clone();
        _viewBlobs = [.. blobs];
        _viewPredicted = new HashSet<Blob>(predicted, ReferenceEqualityComparer.Instance);
    }

    /// <summary>Solid colours of the blob view (0xAARRGGBB), each kind of pixel its own.</summary>
    public const uint ViewHud = 0xFF000000, ViewOwn = 0xFFFF00FF, ViewIgnored = 0xFF1F2F70, ViewFog = 0xFF40C040,
        ViewShade = 0xFF00E0E0, ViewExplained = 0xFFE8D27A, ViewDropped = 0xFFFFFFFF;

    /// <summary>One colour per blob, in turn; none of them is a kind's colour (no blue beside the untrusted navy).</summary>
    private static readonly uint[] BlobColours = [0xFFE02020, 0xFFFF8C00, 0xFF9933FF, 0xFFA0522D];

    /// <summary>
    /// The frame in light grey where it matched the background, the excluded and explained pixels in their kinds' solid
    /// colours, and every blob's pixels in a colour of its own with its box and number (and the entity it became), over a legend.
    /// </summary>
    public Canvas BlobView(ReadOnlySpan<byte> frame, int frameWidth, IReadOnlyList<DetectedEntity> entities)
    {
        int fh = frame.Length / 4 / frameWidth;
        var vp = _mask.Viewport;
        int w = _mask.Width, h = _mask.Height;
        var view = new Canvas(frameWidth, fh);
        for (var y = 0; y < fh; y++)
            for (var x = 0; x < frameWidth; x++)
            {
                var o = (y * frameWidth + x) * 4;
                var luma = (frame[o] + 2 * frame[o + 1] + frame[o + 2]) >> 2;
                var g = (uint)(100 + luma * 70 / 255);
                view.Set(x, y, 0xFF000000 | g << 16 | g << 8 | g);
            }
        var blobs = _viewBlobs ?? [];
        var owner = BlobOwners(w, h, blobs);
        var own = LastOwnBox;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int sx = x + vp.X, sy = y + vp.Y, i = y * w + x;
                var c = (PixelClass)_fg.Classes[i];
                uint? colour =
                    own is { } ob && sx >= ob.X && sx < ob.X + ob.Width && sy >= ob.Y && sy < ob.Y + ob.Height ? ViewOwn
                    : _mask.Bits[i] != 0 ? ViewHud
                    : _viewMask?[i] == 1 ? owner[i] >= 0 ? BlobColours[owner[i] % BlobColours.Length] : ViewDropped
                    : c == PixelClass.Ignored ? ViewIgnored
                    : c is PixelClass.Fog or PixelClass.BrightFog ? ViewFog
                    : c == PixelClass.Shade ? ViewShade
                    : c is PixelClass.Solid or PixelClass.Bright ? ViewExplained
                    : null;
                if (colour is { } cv) view.Set(sx, sy, cv);
            }
        // Labels stack upward where they would cover one another (blobs side by side).
        var placed = new List<(int X0, int Y0, int X1, int Y1)>();
        for (var k = 0; k < blobs.Count; k++)
        {
            var b = blobs[k];
            var colour = BlobColours[k % BlobColours.Length];
            var box = new CaptureRect(b.X0 + vp.X, b.Y0 + vp.Y, b.Width, b.Height);
            view.Rect(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2, colour);
            var became = entities.FirstOrDefault(e => e.Box == box);
            var label = $"B{k + 1}{(_viewPredicted?.Contains(b) == true ? " predicted" : "")}{(became is null ? "" : " -> " + Label(became))}";
            int lx = box.X, ly = Math.Max(0, box.Y - 10), lw = Canvas.TextWidth(label);
            while (ly > 0 && placed.Any(p => lx <= p.X1 && lx + lw >= p.X0 && ly <= p.Y1 && ly + 9 >= p.Y0)) ly = Math.Max(0, ly - 10);
            placed.Add((lx, ly, lx + lw, ly + 9));
            view.Text(lx, ly, label, colour);
        }
        return WithLegend(view);
    }

    /// <summary>Per viewport pixel, the index of the blob its 8-connected component went into, or -1 (too small, too faint, or explained).</summary>
    private int[] BlobOwners(int w, int h, List<Blob> blobs)
    {
        var owner = new int[w * h];
        Array.Fill(owner, -1);
        if (_viewMask is not { } m) return owner;
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var pixels = new List<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (m[start] == 0 || seen[start]) continue;
            pixels.Clear();
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, solid = 0;
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                var j = stack.Pop();
                pixels.Add(j);
                int jx = j % w, jy = j / w;
                x0 = Math.Min(x0, jx); y0 = Math.Min(y0, jy); x1 = Math.Max(x1, jx); y1 = Math.Max(y1, jy);
                if (_fg.Classes[j] is (byte)PixelClass.Solid or (byte)PixelClass.Bright) solid++;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = jx + dx, ny = jy + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        var n = ny * w + nx;
                        if (m[n] != 0 && !seen[n]) { seen[n] = true; stack.Push(n); }
                    }
            }
            // A component is a blob's (or a part of one merged with its neighbours) when the blob's box holds it and it passed
            // the size and solid share the blob step asks; a blob kept for its drop icon in steam is its component exactly.
            var kept = pixels.Count >= ForegroundExtractor.MinPixels && solid >= MinSolidFraction * pixels.Count;
            for (var k = 0; k < blobs.Count; k++)
            {
                var b = blobs[k];
                var exact = b.X0 == x0 && b.Y0 == y0 && b.X1 == x1 && b.Y1 == y1;
                if (!exact && !(kept && x0 >= b.X0 && y0 >= b.Y0 && x1 <= b.X1 && y1 <= b.Y1)) continue;
                foreach (var p in pixels) owner[p] = k;
                break;
            }
        }
        return owner;
    }

    private static Canvas WithLegend(Canvas view)
    {
        (uint Colour, string Text)[] items =
        [
            (ViewHud, "HUD, minimap, log (masked)"), (ViewOwn, "your character (masked)"), (ViewIgnored, "background not trusted, not compared"),
            (ViewFog, "clouds and steam"), (ViewShade, "name-tag boxes"), (ViewExplained, "explained before the blob step (pet, cursor, coins, predicted mobs, tag text) or a speck"),
            (ViewDropped, "foreground too small or too faint for a blob"), (BlobColours[0], "blobs: one colour each, Bn = blob n"),
        ];
        const int row = 16, pad = 6, scale = 1;
        var lines = new List<List<(uint, string)>> { new() };
        var x = pad;
        foreach (var item in items)
        {
            var width = 14 + Canvas.TextWidth(item.Text, scale) + 16;
            if (x + width > view.Width - pad && lines[^1].Count > 0) { lines.Add([]); x = pad; }
            lines[^1].Add(item);
            x += width;
        }
        var legendHeight = lines.Count * row + pad;
        var canvas = new Canvas(view.Width, view.Height + legendHeight, 0xFF202020);
        canvas.Paste(view, 0, 0);
        for (var l = 0; l < lines.Count; l++)
        {
            x = pad;
            var y = view.Height + pad / 2 + l * row + 3;
            foreach (var (colour, text) in lines[l])
            {
                canvas.Fill(x, y, 10, 10, colour);
                canvas.Rect(x, y, 10, 10, 0xFFFFFFFF);
                canvas.Text(x + 14, y + 1, text, 0xFFFFFFFF, scale, backing: null);
                x += 14 + Canvas.TextWidth(text, scale) + 16;
            }
        }
        return canvas;
    }
}
