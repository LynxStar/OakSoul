using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using OakSoul.Bake;
using OakSoul.Overlay;

namespace OakSoul.Review.Views;

/// <summary>What the painter draws and what it marks.</summary>
/// <param name="Selected">The selected track, outlined.</param>
/// <param name="Hover">The entity under the pointer, outlined.</param>
public sealed record PaintOptions(IReadOnlySet<string> Layers, IReadOnlySet<string> Kinds, bool Labels, long? Selected = null, OverlayItem? Hover = null);

/// <summary>
/// Draws a frame's overlay items as vectors over the frame, so boxes, lines and labels stay sharp at any zoom: the same
/// items, colours, label text and label placement as <see cref="FrameRenderer"/> bakes (colours and which items are
/// labelled come from <see cref="FrameRenderer.DefaultStyle"/>, the text from the stream's labels, which
/// <see cref="OverlayLabels"/> built), with strokes and text sized in screen pixels instead of scaled with the frame.
/// </summary>
public static class OverlayPainter
{
    public static readonly FontFamily Mono = new("Menlo, Consolas, Inter");
    public static readonly Typeface LabelFace = new(Mono);
    private static readonly Dictionary<uint, IImmutableSolidColorBrush> Brushes = [];
    private static readonly Dictionary<(uint, double, bool), IPen> Pens = [];
    public static readonly IBrush LabelBacking = new ImmutableSolidColorBrush(Color.FromArgb(153, 0, 0, 0));

    public static IImmutableSolidColorBrush Brush(uint argb)
    {
        if (!Brushes.TryGetValue(argb, out var b)) Brushes[argb] = b = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
        return b;
    }

    public static IPen Pen(uint argb, double thickness, bool dashed = false)
    {
        if (!Pens.TryGetValue((argb, thickness, dashed), out var p))
            Pens[(argb, thickness, dashed)] = p = new ImmutablePen(Brush(argb), thickness, dashed ? new ImmutableDashStyle([4, 3], 0) : null);
        return p;
    }

    public static Color Colour(uint argb) => Color.FromUInt32(argb);

    /// <summary>Whether an item is drawn: its layer is on and, for an entity, its kind.</summary>
    public static bool Visible(OverlayItem item, PaintOptions o) =>
        o.Layers.Contains(item.Layer) && (item.Layer != OverlayLayers.Entity || o.Kinds.Contains(item.Kind));

    /// <summary>Stroke width in screen pixels for an item at <paramref name="zoom"/> screen pixels per game pixel.</summary>
    private static double Thickness(OverlayItem item, double zoom) => item.Layer switch
    {
        OverlayLayers.Entity => 2,
        OverlayLayers.Tags => 1.5,
        OverlayLayers.Self => item.Kind == "tag" ? 1 : 2,
        // Footholds, ropes and ladders: Bake draws them 2.5 game pixels wide; here at most 4 screen pixels so the frame shows through.
        OverlayLayers.World => Math.Clamp(1.25 * zoom, 1.5, 4),
        _ => 1,
    };

    /// <summary>
    /// Paints the frame's visible items with game pixel (x, y) at <paramref name="origin"/> + (x, y) x <paramref name="zoom"/>,
    /// in the registry's drawing order (entities on top), then the labels over everything. Shapes are clipped to the
    /// frame (<paramref name="frameSize"/> game pixels, when given); labels may sit in the margin around it, within
    /// <paramref name="bounds"/>.
    /// </summary>
    public static void Paint(DrawingContext dc, OverlayFrame frame, double zoom, Point origin, PaintOptions o, double labelSize = 11, Rect? bounds = null, Size? frameSize = null)
    {
        var labels = new List<(OverlayItem Item, uint Colour)>();
        Point P(double x, double y) => new(origin.X + x * zoom, origin.Y + y * zoom);
        using (frameSize is { } fs ? dc.PushClip(new Rect(origin.X, origin.Y, fs.Width * zoom, fs.Height * zoom)) : default(DrawingContext.PushedState?))
            Shapes(dc, frame, zoom, o, labels, P);
        if (labels.Count > 0) PaintLabels(dc, labels, zoom, origin, labelSize, bounds);
    }

    private static void Shapes(DrawingContext dc, OverlayFrame frame, double zoom, PaintOptions o, List<(OverlayItem Item, uint Colour)> labels, Func<double, double, Point> P)
    {
        foreach (var layer in OverlayLayers.Names)
            foreach (var item in frame.Items)
            {
                if (item.Layer != layer || !Visible(item, o)) continue;
                var style = FrameRenderer.DefaultStyle(item, 2);
                var colour = style.Colour;
                var t = Thickness(item, zoom);
                var selected = o.Selected is { } sel && item.Layer == OverlayLayers.Entity && item.Long("track") == sel;
                var hovered = ReferenceEquals(item, o.Hover);
                if (item.Rect is { } r && !(item.Layer == OverlayLayers.Minimap && item.Kind == "dot"))
                {
                    var rect = new Rect(P(r.X, r.Y), P(r.X + r.W, r.Y + r.H));
                    if (selected) dc.DrawRectangle(null, Pen(0xFFFFFFFF, 2), rect.Inflate(3));
                    if (hovered) dc.DrawRectangle(null, Pen(0xFFFFFFFF, 1, true), rect.Inflate(2));
                    dc.DrawRectangle(null, Pen(colour, selected ? t + 1 : t), rect.Deflate(t / 2));
                }
                if (item.Line() is { Length: >= 2 } line)
                {
                    var pen = Pen(colour & (item.Layer == OverlayLayers.World ? 0xC0FFFFFF : 0xFFFFFFFF), t);
                    for (var i = 1; i < line.Length; i++)
                        dc.DrawLine(pen, P(line[i - 1][0] + 0.5, line[i - 1][1] + 0.5), P(line[i][0] + 0.5, line[i][1] + 0.5));
                }
                if (item.Point("point") is { } p)
                {
                    var size = Math.Max(3, (item.Layer == OverlayLayers.Minimap ? 2 : 4) * zoom);
                    var c = P(p.X + 0.5, p.Y + 0.5);
                    dc.DrawRectangle(null, Pen(colour, 1.5), new Rect(c.X - size, c.Y - size, 2 * size, 2 * size));
                }
                if (item.Point("feet") is { } feet && item.Layer is OverlayLayers.Entity or OverlayLayers.Self)
                {
                    var c = P(feet.X + 0.5, feet.Y + 0.5);
                    var arm = Math.Max(4, 2 * zoom);
                    var pen = Pen(colour, 1.5);
                    dc.DrawLine(pen, new Point(c.X - arm, c.Y), new Point(c.X + arm, c.Y));
                    dc.DrawLine(pen, new Point(c.X, c.Y - arm), new Point(c.X, c.Y + arm));
                }
                if (o.Labels && style.Labelled && item.Label is { Length: > 0 }) labels.Add((item, colour));
            }
    }

    /// <summary>
    /// Labels above their item (an entity's box, a region, a portal), moved up past labels already placed so none
    /// overlap, below the box when there is no room above, joined to the item by a line when moved; on a translucent
    /// backing so the sprite under them still shows. Bake's placement, in screen pixels.
    /// </summary>
    private static void PaintLabels(DrawingContext dc, List<(OverlayItem Item, uint Colour)> labels, double zoom, Point origin, double size, Rect? bounds)
    {
        var placed = new List<Rect>();
        var top = bounds?.Top ?? 0;
        var left = bounds?.Left ?? 0;
        var right = bounds?.Right ?? double.MaxValue;
        foreach (var (item, colour) in labels)
        {
            var text = new FormattedText(item.Label!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, size, Brush(colour));
            var anchor = item.Rect is { } rr ? new Point(origin.X + rr.X * zoom, origin.Y + rr.Y * zoom)
                : item.Point("point") is { } pp ? new Point(origin.X + pp.X * zoom, origin.Y + pp.Y * zoom - 4 * zoom)
                : item.Point("feet") is { } ff ? new Point(origin.X + ff.X * zoom, origin.Y + ff.Y * zoom)
                : origin;
            double w = text.Width + 4, h = text.Height + 1;
            // Kept inside the bounds; a label wider than them keeps its start (the track id) in view.
            var x = Math.Max(left, Math.Min(anchor.X, right - w));
            var y = anchor.Y - h - 1;
            var moved = false;
            for (var tries = 0; tries < 12 && placed.Any(l => l.Intersects(new Rect(x, y, w, h))); tries++) { y -= h; moved = true; }
            if (y < top)
            {
                y = item.Rect is { } rb ? origin.Y + (rb.Y + rb.H) * zoom + 2 : anchor.Y + 2;
                moved = true;
            }
            var box = new Rect(x, y, w, h);
            placed.Add(box);
            if (moved) dc.DrawLine(Pen(colour, 1), new Point(x, y + h), anchor);
            dc.DrawRectangle(LabelBacking, null, box);
            dc.DrawText(text, new Point(x + 2, y));
        }
    }

    /// <summary>The entity under a game pixel (the smallest box containing it), among the visible ones.</summary>
    public static OverlayItem? EntityAt(OverlayFrame frame, double gx, double gy, PaintOptions o) =>
        frame.Layer(OverlayLayers.Entity)
            .Where(e => Visible(e, o) && e.Rect is { } r && gx >= r.X - 1 && gx <= r.X + r.W + 1 && gy >= r.Y - 1 && gy <= r.Y + r.H + 1)
            .OrderBy(e => e.Rect!.Value.W * e.Rect.Value.H)
            .FirstOrDefault();

    /// <summary>Evidence colours of the track views: matched green, carried amber, unverified grey-red, present without evidence the kind's colour dimmed.</summary>
    public static uint EvidenceColour(Model.Evidence e, string kind) => e switch
    {
        Model.Evidence.Matched => 0xFF3DD68C,
        Model.Evidence.Carried => 0xFFE8A23A,
        Model.Evidence.Unverified => 0xFFB05A5A,
        Model.Evidence.Present => (FrameRenderer.KindColour(kind) & 0x00FFFFFF) | 0xA0000000,
        _ => 0x00000000,
    };
}
