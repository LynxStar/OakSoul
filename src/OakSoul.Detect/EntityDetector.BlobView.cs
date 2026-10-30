using OakSoul.Capture;

namespace OakSoul.Detect;

/// <summary>
/// The blob view of a debug frame (OAKSOUL_DEBUG_DUMP): what the blob step saw, in eight solid colours and nothing else (no
/// text, no boxes), one per kind of pixel, so the excluded areas, what an earlier step explained and the blobs read apart.
/// Separate blobs are separate red islands: two blobs never touch (touching pixels are one blob).
/// </summary>
public sealed partial class EntityDetector
{
    // The foreground, the blobs and the explained pixels as the blob step found them (taken on debug frames only).
    private byte[]? _viewMask, _viewExplained;
    private List<Blob>? _viewBlobs;

    private void SnapshotBlobs(List<Blob> blobs)
    {
        _viewMask = (byte[])_fg.Mask.Clone();
        _viewExplained = _matcher.Explained is { } e ? (byte[])e.Clone() : null;
        _viewBlobs = [.. blobs];
    }

    /// <summary>The eight colours of the blob view (0xAARRGGBB).</summary>
    public const uint ViewMasked = 0xFF000000, ViewBackground = 0xFF808080, ViewIgnored = 0xFF1F2F70, ViewFog = 0xFF40C040,
        ViewTag = 0xFF00E0E0, ViewExplained = 0xFFFFD700, ViewBlob = 0xFFE02020, ViewLoose = 0xFFFFFFFF;

    /// <summary>
    /// Every pixel in one of eight colours: masked (the HUD, minimap, log and your character) black; matched the background
    /// grey; background not trusted and not compared navy; clouds and steam green; name-tag boxes cyan; explained before the
    /// blob step (the predicted mobs, the pet, the coins) yellow; in a blob red; differing but in no blob (too small, too
    /// faint, a speck the cleaning removed, the cursor) white.
    /// </summary>
    public Canvas BlobView(ReadOnlySpan<byte> frame, int frameWidth)
    {
        int fh = frame.Length / 4 / frameWidth;
        var vp = _mask.Viewport;
        int w = _mask.Width, h = _mask.Height;
        var view = new Canvas(frameWidth, fh, ViewBackground);
        var owner = BlobOwners(w, h, _viewBlobs ?? []);
        var own = LastOwnBox;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int sx = x + vp.X, sy = y + vp.Y, i = y * w + x;
                var c = (PixelClass)_fg.Classes[i];
                var inTag = LastTags.Any(t => x >= t.X0 && x < t.X0 + t.Width && y >= t.Y0 && y < t.Y0 + t.Height);
                view.Set(sx, sy,
                    own is { } ob && sx >= ob.X && sx < ob.X + ob.Width && sy >= ob.Y && sy < ob.Y + ob.Height || _mask.Bits[i] != 0 ? ViewMasked
                    : _viewMask?[i] == 1 ? owner[i] >= 0 ? ViewBlob : ViewLoose
                    : _viewExplained?[i] == 1 ? ViewExplained
                    : inTag || c == PixelClass.Shade ? ViewTag
                    : c == PixelClass.Ignored ? ViewIgnored
                    : c is PixelClass.Fog or PixelClass.BrightFog ? ViewFog
                    : c is PixelClass.Solid or PixelClass.Bright ? ViewLoose
                    : ViewBackground);
            }
        return view;
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
}
