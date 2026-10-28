using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OakSoul.Overlay;
using OakSoul.Review.Model;

namespace OakSoul.Review.Views;

/// <summary>The frame's tools: select (hover, click an entity, drag to pan), and note (drag a rectangle or click a point).</summary>
public enum Tool { Select, Note }

/// <summary>
/// The frame at a zoom in device pixels per game pixel, nearest-neighbour (1x on a Retina display is the game pixel for
/// pixel; the wheel zooms smoothly, snapping to whole zooms where game pixels are even blocks), panned with a drag, the
/// overlay painted as vectors over it.
/// Hovering an entity outlines it; clicking selects its track; the rect and point tools draw the region of a note.
/// The pixels and the overlay shown are always of the same frame: while a frame decodes the previous one stays up with
/// its own overlay.
/// </summary>
public sealed class FrameView : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x16));
    private static readonly IBrush Unprocessed = new SolidColorBrush(Color.FromArgb(150, 20, 20, 24));
    private WriteableBitmap? _bitmap;
    private int _shown = -1;
    private ReviewSession? _session;
    private Point _origin;
    private bool _fitted;
    private Point? _panFrom;
    private Point _panOrigin;
    private (int X, int Y)? _dragFrom;
    private (int X, int Y)? _dragTo;
    private readonly LabelLayout _labels = new();
    private static readonly Cursor Arrow = Cursor.Default, Hand = new(StandardCursorType.Hand), Cross = new(StandardCursorType.Cross), Move = new(StandardCursorType.SizeAll);

    public FrameView()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
    }

    private const double MinZoom = 0.25, MaxZoom = 16;

    /// <summary>Device pixels per game pixel.</summary>
    public double Zoom { get; private set; } = 2;

    public Tool Tool { get; set; }

    /// <summary>The region of the note being written (game pixels), drawn until it is saved or cancelled.</summary>
    public (int X, int Y, int W, int H)? Pending { get; set; }
    public bool PendingIsPoint { get; set; }

    public OverlayItem? Hover { get; private set; }

    /// <summary>The game pixel under the pointer.</summary>
    public (int X, int Y)? PointerPixel { get; private set; }

    /// <summary>The timeline index whose pixels and overlay are on screen.</summary>
    public int Shown => _shown;

    public event Action<(int X, int Y, int W, int H), bool>? RegionDrawn;
    public event Action<OverlayItem?>? EntityClicked;
    public event Action? HoverChanged;
    public event Action? ZoomChanged;

    /// <summary>Another frame's pixels are on screen.</summary>
    public event Action? ShownChanged;

    public ReviewSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.CursorChanged -= Refresh;
                _session.StreamChanged -= InvalidateVisual;
                _session.SelectionChanged -= InvalidateVisual;
                _session.ViewChanged -= InvalidateVisual;
                _session.NotesChanged -= InvalidateVisual;
                _session.Pixels.Ready -= OnReady;
            }
            _session = value;
            _shown = -1;
            _bitmap?.Dispose();
            _bitmap = null;
            if (_session is not null)
            {
                _session.CursorChanged += Refresh;
                _session.StreamChanged += InvalidateVisual;
                _session.SelectionChanged += InvalidateVisual;
                _session.ViewChanged += InvalidateVisual;
                _session.NotesChanged += InvalidateVisual;
                _session.Pixels.Ready += OnReady;
                _session.Pixels.Want(_session.Cursor);
            }
            Refresh();
        }
    }

    private void OnReady(int index)
    {
        if (_session is not null && index == _session.Cursor && _shown != index) Refresh();
    }

    /// <summary>Shows the cursor's frame if its pixels are decoded (else keeps the frame on screen until they are).</summary>
    public void Refresh()
    {
        var s = _session;
        if (s is null) { InvalidateVisual(); return; }
        var index = s.Cursor;
        if (_bitmap is null || _bitmap.PixelSize.Width != s.Pixels.Width || _bitmap.PixelSize.Height != s.Pixels.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(s.Pixels.Width, s.Pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _shown = -1;
        }
        if (_shown != index && s.Pixels.Use(index, Copy))
        {
            _shown = index;
            ShownChanged?.Invoke();
        }
        Hover = null;
        InvalidateVisual();
    }

    private void Copy(byte[] bgra)
    {
        using var fb = _bitmap!.Lock();
        var row = _bitmap.PixelSize.Width * 4;
        if (fb.RowBytes == row) Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, row * _bitmap.PixelSize.Height));
        else
            for (var y = 0; y < _bitmap.PixelSize.Height; y++) Marshal.Copy(bgra, y * row, fb.Address + y * fb.RowBytes, row);
    }

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>Screen (logical) pixels per game pixel.</summary>
    public double Scale => Zoom / Scaling;

    private double Snap(double v) => Math.Round(v * Scaling) / Scaling;

    /// <summary>The zoom at which the whole frame fills the view, centred (a whole zoom when one is within a few percent).</summary>
    public void Fit()
    {
        if (_session is null || Bounds.Width <= 0) return;
        var s = Scaling;
        var fill = Math.Min(Bounds.Width * s / _session.Pixels.Width, Bounds.Height * s / _session.Pixels.Height);
        Zoom = Math.Clamp(Math.Floor(fill) >= 1 && Math.Floor(fill) / fill > 0.96 ? Math.Floor(fill) : fill, MinZoom, MaxZoom);
        _origin = new Point(Snap((Bounds.Width - _session.Pixels.Width * Scale) / 2), Snap((Bounds.Height - _session.Pixels.Height * Scale) / 2));
        _fitted = true;
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Zooms in (or out) by <paramref name="steps"/> quarter-octaves (19% each), keeping the game pixel under <paramref name="at"/> (default the centre) in place.</summary>
    public void ZoomBy(double steps, Point? at = null) => ZoomTo(Zoom * Math.Pow(2, steps / 4), at);

    /// <summary>Sets the zoom, snapped to a whole zoom within 3% of it, keeping the game pixel under <paramref name="at"/> (default the centre) in place.</summary>
    public void ZoomTo(double zoom, Point? at = null)
    {
        var whole = Math.Round(zoom);
        var next = Math.Clamp(whole >= 1 && Math.Abs(zoom - whole) / whole < 0.03 ? whole : zoom, MinZoom, MaxZoom);
        if (Math.Abs(next - Zoom) < 1e-9) return;
        var p = at ?? Bounds.Center;
        var before = Scale;
        Zoom = next;
        var after = Scale;
        _origin = new Point(Snap(p.X - (p.X - _origin.X) * after / before), Snap(p.Y - (p.Y - _origin.Y) * after / before));
        Clamp();
        ZoomChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Centres a game region in the view (keeping the zoom).</summary>
    public void CentreOn((int X, int Y, int W, int H) region)
    {
        _origin = new Point(Snap(Bounds.Width / 2 - (region.X + region.W / 2.0) * Scale), Snap(Bounds.Height / 2 - (region.Y + region.H / 2.0) * Scale));
        Clamp();
        InvalidateVisual();
    }

    private void Clamp()
    {
        if (_session is null) return;
        double w = _session.Pixels.Width * Scale, h = _session.Pixels.Height * Scale, keep = 80;
        _origin = new Point(Math.Clamp(_origin.X, keep - w, Bounds.Width - keep), Math.Clamp(_origin.Y, keep - h, Bounds.Height - keep));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_fitted) Fit();
    }

    public (int X, int Y) GamePixel(Point screen) => ((int)Math.Floor((screen.X - _origin.X) / Scale), (int)Math.Floor((screen.Y - _origin.Y) / Scale));

    public Rect ScreenRect((int X, int Y, int W, int H) r) => new(_origin.X + r.X * Scale, _origin.Y + r.Y * Scale, r.W * Scale, r.H * Scale);

    private PaintOptions Options(ReviewSession s) => new(s.Layers, s.Kinds, s.Labels, s.SelectedTrack, Hover);

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(Background, new Rect(Bounds.Size));
        var s = _session;
        if (s is null) return;
        var scale = Scale;
        var frameRect = new Rect(_origin.X, _origin.Y, s.Pixels.Width * scale, s.Pixels.Height * scale);
        if (_bitmap is not null && _shown >= 0)
        {
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None }))
                dc.DrawImage(_bitmap, new Rect(0, 0, s.Pixels.Width, s.Pixels.Height), frameRect);
        }
        else
        {
            dc.DrawRectangle(null, OverlayPainter.Pen(0xFF404040, 1), frameRect);
            Caption(dc, "decoding…", frameRect.Center, 0xFFB0B0B0);
            return;
        }
        var index = _shown;
        var frame = s.Stream?.At(index);
        if (frame is null)
        {
            dc.FillRectangle(Unprocessed, frameRect);
            Caption(dc, s.State == StreamState.Generating ? "not processed yet: the watcher has not reached this frame" : "no stream for this frame", frameRect.Center, 0xFFD0D0D0);
        }
        else OverlayPainter.Paint(dc, frame, scale, _origin, Options(s), new Size(s.Pixels.Width, s.Pixels.Height), _labels, _shown);

        if (s.ShowNotes)
            foreach (var note in s.Packets.Notes.Where(n => s.NoteIndex(n) == index))
                Region(dc, note.Region, note.IsPoint, 0xFFFFD84A, $"note {note.Packet.Name}/{note.Number} {NoteCategory.Find(note.Category).Title.ToLowerInvariant()}", true);
        if (s.ShowTruth)
            foreach (var e in s.Truth.Entries.Where(e => s.Timeline.IndexOf(e.Sequence) == index))
                Region(dc, e.Region, e.IsPoint, e.Verdict?.Pass switch { true => 0xFF3DD68C, false => 0xFFFF5A5A, _ => 0xFFB0B0B0 }, $"truth {e.Id} {e.Verdict?.Word}", true);
        if (s.Focus is { } f && index == s.Cursor)
        {
            var r = ScreenRect(f.Region).Inflate(f.Point ? 10 : 6);
            dc.DrawRectangle(null, OverlayPainter.Pen(0xFF4AD8FF, 2.5), r);
        }
        if (_dragFrom is { } a && _dragTo is { } b)
            dc.DrawRectangle(null, OverlayPainter.Pen(0xFFFFE070, 1.5, true), ScreenRect(Normalize(a, b)));
        if (Pending is { } p) Region(dc, p, PendingIsPoint, 0xFFFFE070, "new note", false);
    }

    /// <summary>
    /// A note's or truth entry's region with its label under it. Like the overlay's labels, the label and a point's
    /// marker are part of the frame's picture, sized in game pixels, so zooming scales them with the frame.
    /// </summary>
    private void Region(DrawingContext dc, (int X, int Y, int W, int H) region, bool point, uint colour, string label, bool dashed)
    {
        var pen = OverlayPainter.Pen(colour, 2, dashed);
        double bottom;
        if (point)
        {
            var c = ScreenRect(region).Center;
            double radius = 4 * Scale, arm = 6 * Scale;
            dc.DrawEllipse(null, pen, c, radius, radius);
            dc.DrawLine(pen, new Point(c.X - arm, c.Y), new Point(c.X + arm, c.Y));
            dc.DrawLine(pen, new Point(c.X, c.Y - arm), new Point(c.X, c.Y + arm));
            bottom = region.Y + 0.5 + 6;
        }
        else
        {
            dc.DrawRectangle(null, pen, ScreenRect(region));
            bottom = region.Y + region.H;
        }
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayPainter.LabelFace, OverlayPainter.LabelSize, OverlayPainter.Brush(colour));
        var x = point ? region.X + 0.5 - 6 : region.X;
        using (dc.PushTransform(Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(_origin.X, _origin.Y)))
        {
            dc.DrawRectangle(OverlayPainter.LabelBacking, null, new Rect(x, bottom + 1, text.Width + 2, text.Height));
            dc.DrawText(text, new Point(x + 1, bottom + 1));
        }
    }

    private static void Caption(DrawingContext dc, string text, Point centre, uint colour)
    {
        var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 15, OverlayPainter.Brush(colour));
        var box = new Rect(centre.X - t.Width / 2 - 8, centre.Y - t.Height / 2 - 4, t.Width + 16, t.Height + 8);
        dc.DrawRectangle(OverlayPainter.LabelBacking, null, box, 4, 4);
        dc.DrawText(t, new Point(box.X + 8, box.Y + 4));
    }

    private static (int X, int Y, int W, int H) Normalize((int X, int Y) a, (int X, int Y) b) =>
        (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var pan = props.IsMiddleButtonPressed || props.IsRightButtonPressed;
        if (!pan && props.IsLeftButtonPressed)
        {
            var g = GamePixel(p);
            switch (Tool)
            {
                case Tool.Note:
                    _dragFrom = _dragTo = g;
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    return;
                default:
                    var hit = _session?.Stream?.At(_shown) is { } f && _session is { } s ? OverlayPainter.EntityAt(f, g.X + 0.5, g.Y + 0.5, Options(s)) : null;
                    if (hit is not null) { EntityClicked?.Invoke(hit); return; }
                    if (e.ClickCount == 2) { EntityClicked?.Invoke(null); return; }
                    pan = true;
                    break;
            }
        }
        if (pan)
        {
            _panFrom = p;
            _panOrigin = _origin;
            e.Pointer.Capture(this);
            SetCursor(Move);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            _origin = new Point(Snap(_panOrigin.X + p.X - from.X), Snap(_panOrigin.Y + p.Y - from.Y));
            Clamp();
            InvalidateVisual();
            return;
        }
        var g = GamePixel(p);
        PointerPixel = g;
        if (_dragFrom is not null)
        {
            _dragTo = g;
            InvalidateVisual();
            HoverChanged?.Invoke();
            return;
        }
        var hover = Tool == Tool.Select && _session?.Stream?.At(_shown) is { } f && _session is { } s ? OverlayPainter.EntityAt(f, g.X + 0.5, g.Y + 0.5, Options(s)) : null;
        if (!ReferenceEquals(hover, Hover))
        {
            Hover = hover;
            InvalidateVisual();
        }
        SetCursor(Tool == Tool.Select ? (hover is null ? Arrow : Hand) : Cross);
        HoverChanged?.Invoke();
    }

    /// <summary>Sets the pointer's cursor only when it changes: a new cursor on every pointer move makes macOS set it again each time.</summary>
    public void SetCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    /// <summary>The cursor for a tool when nothing is hovered.</summary>
    public static Cursor ToolCursor(Tool tool) => tool == Tool.Select ? Arrow : Cross;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerPixel = null;
        if (Hover is not null) { Hover = null; InvalidateVisual(); }
        HoverChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panFrom is not null)
        {
            _panFrom = null;
            e.Pointer.Capture(null);
            SetCursor(Arrow);
            return;
        }
        if (_dragFrom is { } a && _dragTo is { } b)
        {
            _dragFrom = _dragTo = null;
            e.Pointer.Capture(null);
            var r = Normalize(a, b);
            // A click with the note tool is a point.
            if (r.W <= 2 && r.H <= 2) RegionDrawn?.Invoke((a.X, a.Y, 1, 1), true);
            else RegionDrawn?.Invoke(r, false);
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // The wheel zooms (a notch is half a step, 9%; a trackpad's small deltas zoom smoothly); Shift+wheel pans.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (Math.Abs(e.Delta.Y) > 0.001) ZoomBy(Math.Clamp(e.Delta.Y, -4, 4) / 2, e.GetPosition(this));
        }
        else
        {
            _origin = new Point(Snap(_origin.X + e.Delta.X * 40), Snap(_origin.Y + e.Delta.Y * 40));
            Clamp();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    /// <summary>Draws the current frame's region at 4x for a note's crops: the raw pixels, and the pixels with the overlay as shown.</summary>
    public static (RenderTargetBitmap Raw, RenderTargetBitmap Overlay) Crops(WriteableBitmap pixels, OverlayFrame? frame, (int X, int Y, int W, int H) crop, PaintOptions options,
        (int X, int Y, int W, int H) region, bool point, int scale = 4)
    {
        var size = new PixelSize(crop.W * scale, crop.H * scale);
        var origin = new Point(-crop.X * scale, -crop.Y * scale);
        int width = pixels.PixelSize.Width, height = pixels.PixelSize.Height;
        var dest = new Rect(origin.X, origin.Y, width * scale, height * scale);
        var raw = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = raw.CreateDrawingContext())
        using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None }))
            dc.DrawImage(pixels, new Rect(0, 0, width, height), dest);
        var overlay = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = overlay.CreateDrawingContext())
        {
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.None }))
                dc.DrawImage(pixels, new Rect(0, 0, width, height), dest);
            if (frame is not null) OverlayPainter.Paint(dc, frame, scale, origin, options);
            var r = new Rect(origin.X + region.X * scale, origin.Y + region.Y * scale, region.W * scale, region.H * scale);
            var pen = OverlayPainter.Pen(0xFFFFE070, 2, true);
            if (point) dc.DrawEllipse(null, pen, r.Center, 10, 10);
            else dc.DrawRectangle(null, pen, r.Inflate(2));
        }
        return (raw, overlay);
    }

    /// <summary>The bitmap on screen (the cursor's frame when <see cref="Shown"/> equals the cursor).</summary>
    public WriteableBitmap? Bitmap => _bitmap;
}
