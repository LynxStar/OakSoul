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
/// items, colours and label text as <see cref="FrameRenderer"/> bakes, labels stacked in a fixed order that holds from frame to frame (colours and which items are
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
    /// frame (<paramref name="frameSize"/> game pixels, when given); labels may stand in the margin around it.
    /// </summary>
    public static void Paint(DrawingContext dc, OverlayFrame frame, double zoom, Point origin, PaintOptions o, Size? frameSize = null,
        LabelLayout? layout = null, int frameIndex = -1)
    {
        var labels = new List<(OverlayItem Item, uint Colour)>();
        Point P(double x, double y) => new(origin.X + x * zoom, origin.Y + y * zoom);
        using (frameSize is { } fs ? dc.PushClip(new Rect(origin.X, origin.Y, fs.Width * zoom, fs.Height * zoom)) : default(DrawingContext.PushedState?))
            Shapes(dc, frame, zoom, o, labels, P);
        if (labels.Count > 0) PaintLabels(dc, labels, zoom, origin, o, layout ?? new LabelLayout(), frameIndex);
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

    /// <summary>Label text height in game pixels: Bake's labels are its 5x7 font in 6x8 cells with a pixel of margin.</summary>
    public const double LabelSize = 8.5;

    /// <summary>
    /// Labels above their item's top-left corner (an entity's box, a region, a portal), on a translucent backing so the
    /// sprite under them still shows. They are part of the frame's picture: laid out in game pixels at the size Bake
    /// bakes them, and drawn through the same zoom and pan as the pixels, so the view zoomed and panned anywhere shows
    /// exactly what an exported frame would, and moving the view never moves a label. Overlapping labels stack in one
    /// fixed order, the same every frame: the HUD's and the map's first, then entities by track, the oldest nearest its
    /// box; a label raised by the stack is joined to its item by a line. <paramref name="layout"/> carries each label's
    /// level from frame to frame: a raised label keeps its level while that is free and drops only once the level below
    /// has been free for <see cref="LabelLayout.Settle"/> frames, so labels hold still in playback. The hovered and the
    /// selected item's labels are drawn last, over any they overlap; that never moves them.
    /// </summary>
    private static void PaintLabels(DrawingContext dc, List<(OverlayItem Item, uint Colour)> labels, double zoom, Point origin,
        PaintOptions o, LabelLayout layout, int frameIndex)
    {
        var ordered = labels
            .Select(l => (l.Item, l.Colour, Key: LabelLayout.Key(l.Item), Track: l.Item.Layer == OverlayLayers.Entity ? l.Item.Long("track") ?? long.MaxValue : -1))
            .OrderBy(l => l.Track).ToList();
        var levels = layout.Begin(frameIndex);
        // Everything below is in game pixels.
        var placed = new List<(Rect Box, Point Anchor, int Level, FormattedText Text, uint Colour, OverlayItem Item)>();
        foreach (var (item, colour, key, _) in ordered)
        {
            var text = new FormattedText(item.Label!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, LabelSize, Brush(colour));
            var anchor = item.Rect is { } rr ? new Point(rr.X, rr.Y)
                : item.Point("point") is { } pp ? new Point(pp.X, pp.Y - 4)
                : item.Point("feet") is { } ff ? new Point(ff.X, ff.Y)
                : default;
            double w = text.Width + 2, h = text.Height;
            Rect At(int level) => new(anchor.X, anchor.Y - h - 1 - level * h, w, h);
            bool Free(int level) => !placed.Any(p => p.Box.Intersects(At(level)));
            var level = levels.Placed(key, Free);
            placed.Add((At(level), anchor, level, text, colour, item));
        }
        bool OnTop(OverlayItem item) => ReferenceEquals(item, o.Hover) || (o.Selected is { } t && item.Long("track") == t);
        using (dc.PushTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(origin.X, origin.Y)))
            foreach (var (box, anchor, level, text, colour, _) in placed.OrderBy(p => OnTop(p.Item)))
            {
                if (level > 0) dc.DrawLine(Pen(colour, 1), new Point(box.X, box.Bottom), anchor);
                dc.DrawRectangle(LabelBacking, null, box);
                dc.DrawText(text, new Point(box.X + 1, box.Y));
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

/// <summary>
/// Where each label stood (its level: 0 just above its item, 1 a label's height higher, and so on), carried from one
/// frame to the next so a raised label stays raised rather than jumping. A frame painted again (hover, zoom) reuses its
/// own levels; a jump of more than a few frames starts afresh.
/// </summary>
public sealed class LabelLayout
{
    /// <summary>Frames a lower level must stay free before a raised label drops to it (a quarter second of video).</summary>
    public const int Settle = 8;

    /// <summary>The most frames a step may move for the levels to carry on (playback at 2x shows every other frame or so).</summary>
    private const int Continuous = 5;

    private const int MaxLevel = 12;
    private int _frame = -1;
    private Dictionary<string, (int Level, int Free)> _last = [], _now = [];
    private bool _repaint;

    /// <summary>A label's identity across frames: an entity's track, else its layer, kind and region name.</summary>
    public static string Key(OverlayItem item) =>
        item.Long("track") is { } t ? $"t{t}" : $"{item.Layer}/{item.Kind}/{item.Str("name") ?? item.Label}";

    internal LabelLayout Begin(int frame)
    {
        _repaint = frame >= 0 && frame == _frame;
        if (!_repaint)
        {
            if (frame < 0 || _frame < 0 || Math.Abs(frame - _frame) > Continuous) _now.Clear();
            (_last, _now) = (_now, _last);
            _now.Clear();
            _frame = frame;
        }
        return this;
    }

    /// <summary>The level of a label, given which levels are free of the labels placed before it.</summary>
    internal int Placed(string key, Func<int, bool> free)
    {
        if (_repaint && _now.TryGetValue(key, out var same)) return same.Level;
        var lowest = Enumerable.Range(0, MaxLevel).FirstOrDefault(free, MaxLevel);
        var (level, waited) = (lowest, 0);
        if (_last.TryGetValue(key, out var before) && before.Level > lowest && free(before.Level))
        {
            // Raised last frame and still free there: drop only once the lower level has stayed free a while.
            waited = before.Free + 1;
            level = waited >= Settle ? lowest : before.Level;
            if (waited >= Settle) waited = 0;
        }
        _now[key] = (level, waited);
        return level;
    }
}
