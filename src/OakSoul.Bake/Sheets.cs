using System.Globalization;
using OakSoul.Detect;
using OakSoul.Overlay;

namespace OakSoul.Bake;

/// <summary>
/// Contact sheets rendered from the stream: the watcher's <c>--entity-sheet</c> re-implemented (twelve evenly spaced
/// frames, six to a sheet, two columns, a legend; each tile the frame at half size with the own box white, tag boxes cyan,
/// entity boxes in their kind's colour and the sheet label above each box, drawn as <c>EntityDetector.Annotate</c> does),
/// and the same tiles for any selection of frames.
/// </summary>
public static class Sheets
{
    public const int SheetFrames = 12, FramesPerSheet = 6;
    private const int Caption = 14, LegendHeight = 18;

    /// <summary>A frame annotated at half size, exactly as <c>EntityDetector.Annotate(..., foreground: false, half: true)</c>.</summary>
    public static Canvas Tile(OverlayFrame f, byte[] bgra, int width, int height, Func<OverlayItem, string>? label = null)
    {
        var canvas = new Canvas(bgra, width, height);
        if (f.Layer(OverlayLayers.Self).FirstOrDefault(i => i.Kind == "character" && i.Bool("masked")) is { Rect: { } own })
            canvas.Rect(own.X, own.Y, own.W, own.H, 0xFFFFFFFF, 2);
        foreach (var tag in f.Layer(OverlayLayers.Tags))
            if (tag.Rect is { } t) canvas.Rect(t.X, t.Y, t.W, t.H, 0xFF00FFFF, 2);
        var entities = f.Layer(OverlayLayers.Entity).ToList();
        foreach (var e in entities)
            if (e.Rect is { } r) canvas.Rect(r.X, r.Y, r.W, r.H, FrameRenderer.KindColour(e.Kind), 3);
        canvas = canvas.Half();
        foreach (var e in entities)
            if (e.Rect is { } r) canvas.Text(r.X / 2, Math.Max(0, r.Y / 2 - 9), (label ?? OverlayLabels.Sheet)(e), FrameRenderer.KindColour(e.Kind));
        return canvas;
    }

    /// <summary>
    /// The frames the watcher's <c>--entity-sheet</c> would show: twelve picked by position in the replay, 1 + round(i (n - 1) / 11)
    /// with n the keyframe count (360 for a video replay, whose length the watcher does not know), and of those only the ones
    /// the detector ran on.
    /// </summary>
    public static List<OverlayFrame> EntitySheetFrames(OverlayStream stream)
    {
        var n = stream.Header.Replay == "keyframes" ? stream.Frames.Count : SheetFrames * 30;
        var picks = Enumerable.Range(0, SheetFrames).Select(i => 1 + (long)Math.Round(i * (n - 1) / (double)(SheetFrames - 1))).ToHashSet();
        var result = new List<OverlayFrame>();
        for (var i = 0; i < stream.Frames.Count && result.Count < SheetFrames; i++)
            if (picks.Contains(i + 1) && stream.Frames[i].DetectorRan) result.Add(stream.Frames[i]);
        return result;
    }

    /// <summary>Composes tiles into sheets of <paramref name="perSheet"/> frames in <paramref name="cols"/> columns with the legend, as the watcher does.</summary>
    public static List<string> Write(string dir, string prefix, IReadOnlyList<(OverlayFrame Frame, Canvas Tile)> tiles, int perSheet, int cols)
    {
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        if (tiles.Count == 0) return paths;
        var tileW = tiles[0].Tile.Width;
        var tileH = tiles[0].Tile.Height;
        for (var sheet = 0; sheet * perSheet < tiles.Count; sheet++)
        {
            var page = tiles.Skip(sheet * perSheet).Take(perSheet).ToList();
            var rows = (page.Count + cols - 1) / cols;
            var canvas = new Canvas(cols * tileW, LegendHeight + rows * (tileH + Caption));
            var x = 4;
            foreach (var kind in Enum.GetValues<EntityKind>())
            {
                var name = kind.ToString();
                canvas.Fill(x, 5, 10, 8, EntityDetector.KindColour(kind));
                canvas.Text(x + 14, 5, name, 0xFFFFFFFF, 1, null);
                x += 14 + Canvas.TextWidth(name) + 12;
            }
            canvas.Text(x, 5, "cyan: tag box  white: own character (masked)  label: #track name anim/frame L|R (~ carried by the track, ? unverified)", 0xFFFFFFFF, 1, null);
            for (var i = 0; i < page.Count; i++)
            {
                var (frame, tile) = page[i];
                int tx = i % cols * tileW, ty = LegendHeight + i / cols * (tileH + Caption);
                canvas.Text(tx + 4, ty + 3, $"frame {frame.Sequence}  t={frame.Time.ToString("0.0", CultureInfo.InvariantCulture)}s", 0xFFFFFFFF, 1, null);
                canvas.Paste(tile, tx, ty + Caption);
            }
            var path = Path.Combine(dir, $"{prefix}-{sheet + 1}.png");
            canvas.Save(path);
            paths.Add(path);
        }
        return paths;
    }
}
