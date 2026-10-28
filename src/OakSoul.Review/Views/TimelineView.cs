using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OakSoul.Overlay;
using OakSoul.Review.Model;

namespace OakSoul.Review.Views;

/// <summary>
/// The recording's time as a bar: the part the stream covers lit, the rest greyed, with marks for map changes
/// (blue), review notes (yellow) and failing truth entries (red), the cursor, and time ticks.
/// Click or drag to move the cursor.
/// </summary>
public sealed class TimelineView : Control
{
    private const double Side = 10, TickRow = 14;
    private ReviewSession? _session;
    private bool _dragging;
    private double? _hoverX;

    public const uint MapColour = 0xFF4A9BFF, NoteColour = 0xFFFFD84A, FailColour = 0xFFFF5A5A;

    public TimelineView()
    {
        Height = 50;
        ClipToBounds = true;
    }

    public ReviewSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.CursorChanged -= InvalidateVisual;
                _session.StreamChanged -= InvalidateVisual;
                _session.NotesChanged -= InvalidateVisual;
            }
            _session = value;
            if (_session is not null)
            {
                _session.CursorChanged += InvalidateVisual;
                _session.StreamChanged += InvalidateVisual;
                _session.NotesChanged += InvalidateVisual;
            }
            InvalidateVisual();
        }
    }

    private double Width0 => Math.Max(1, Bounds.Width - 2 * Side);

    private double X(int index) => Side + (_session is { Timeline.Count: > 1 } s ? index / (double)(s.Timeline.Count - 1) : 0) * Width0;

    private int Index(double x) => _session is { } s ? Math.Clamp((int)Math.Round((x - Side) / Width0 * (s.Timeline.Count - 1)), 0, s.Timeline.Count - 1) : 0;

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(OverlayPainter.Brush(0xFF15171A), new Rect(Bounds.Size));
        var s = _session;
        if (s is null) return;
        var top = TickRow;
        var h = Bounds.Height - TickRow - 4;
        var bar = new Rect(Side, top, Width0, h);
        dc.FillRectangle(OverlayPainter.Brush(0xFF26292E), bar);
        var processed = s.Stream?.ProcessedUpTo ?? 0;
        if (processed > 0) dc.FillRectangle(OverlayPainter.Brush(0xFF2F4258), new Rect(Side, top, X(Math.Min(processed, s.Timeline.Count - 1)) - Side, h));
        if (processed < s.Timeline.Count)
        {
            // Hatch the part the stream has not reached.
            using (dc.PushClip(new Rect(X(processed), top, bar.Right - X(processed), h)))
                for (var x = X(processed) - h; x < bar.Right; x += 7)
                    dc.DrawLine(OverlayPainter.Pen(0xFF34373D, 1), new Point(x, top + h), new Point(x + h, top));
        }
        // Time ticks: every 10 s, labelled every 30 s (every 5 s and 10 s for short recordings).
        var duration = s.Timeline.Duration;
        var step = duration > 60 ? 10 : 5;
        for (var t = 0; t <= duration; t += step)
        {
            var x = X(s.Timeline.AtTime(t));
            var major = t % (step * 3) == 0;
            dc.DrawLine(OverlayPainter.Pen(0xFF5A6068, 1), new Point(x, top - (major ? 5 : 3)), new Point(x, top));
            if (major) Text(dc, OverlayFrame.FormatTime(t)[..^2], new Point(x + 2, 0), 0xFF8A9098, 10);
        }
        foreach (var m in s.MapMarks) Mark(dc, X(m), top, h, MapColour, 0, 1);
        foreach (var f in s.FailMarks) Mark(dc, X(f), top, h, FailColour, 0.3, 0.7);
        foreach (var n in s.NoteMarks)
        {
            var x = X(n);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x, top + h - 7), true);
                c.LineTo(new Point(x + 4, top + h));
                c.LineTo(new Point(x - 4, top + h));
                c.EndFigure(true);
            }
            dc.DrawGeometry(OverlayPainter.Brush(NoteColour), null, g);
        }
        var cx = X(s.Cursor);
        dc.DrawLine(OverlayPainter.Pen(0xFFFFFFFF, 2), new Point(cx, top - 2), new Point(cx, top + h));
        if (_hoverX is { } hx)
        {
            var e = s.Timeline[Index(hx)];
            var text = $"{OverlayFrame.FormatTime(e.Time)}  seq {e.Sequence}{(e.Filled ? " (fill)" : "")}";
            var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayPainter.LabelFace, 10, OverlayPainter.Brush(0xFFE0E0E0));
            var x = Math.Clamp(hx + 6, 0, Bounds.Width - t.Width - 4);
            dc.DrawRectangle(OverlayPainter.LabelBacking, null, new Rect(x - 2, top, t.Width + 4, t.Height));
            dc.DrawText(t, new Point(x, top));
            dc.DrawLine(OverlayPainter.Pen(0x80FFFFFF, 1), new Point(hx, top), new Point(hx, top + h));
        }
    }

    private static void Mark(DrawingContext dc, double x, double top, double h, uint colour, double from, double to) =>
        dc.DrawLine(OverlayPainter.Pen(colour, 2), new Point(x, top + h * from), new Point(x, top + h * to));

    private static void Text(DrawingContext dc, string text, Point at, uint colour, double size)
    {
        var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayPainter.LabelFace, size, OverlayPainter.Brush(colour));
        dc.DrawText(t, at);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session is null) return;
        _dragging = true;
        e.Pointer.Capture(this);
        _session.Seek(Index(e.GetPosition(this).X));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        if (_dragging) _session?.Seek(Index(_hoverX.Value));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }
}

/// <summary>
/// A track's history, one cell per frame from before it starts to after it ends, coloured by evidence (matched,
/// carried, unverified, present without evidence; empty where the track is absent; hatched where the stream has not
/// reached), the cursor's frame outlined, dying frames marked. Too long for the width, it shows the frames around the
/// cursor. Click a cell to go to that frame.
/// </summary>
public sealed class TrackStrip : Control
{
    private const double MinCell = 4;
    private ReviewSession? _session;
    private TrackInfo? _track;
    private int _first, _count;
    private double _cell;

    public TrackStrip()
    {
        Height = 26;
    }

    public void Show(ReviewSession? session, TrackInfo? track)
    {
        _session = session;
        _track = track;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(OverlayPainter.Brush(0xFF15171A), new Rect(Bounds.Size));
        if (_session is not { } s || _track is not { } t) return;
        var first = Math.Max(0, t.First - 2);
        var last = Math.Min(s.Timeline.Count - 1, t.Last + 2);
        var span = last - first + 1;
        var fit = (int)Math.Floor(Bounds.Width / MinCell);
        if (span > fit)
        {
            // Too many frames for the width: those around the cursor.
            first = Math.Clamp(s.Cursor - fit / 2, first, Math.Max(first, last - fit + 1));
            span = fit;
        }
        _first = first;
        _count = span;
        _cell = Bounds.Width / Math.Max(1, span);
        var h = Bounds.Height - 6;
        var processed = s.Stream?.ProcessedUpTo ?? 0;
        for (var i = 0; i < span; i++)
        {
            var index = first + i;
            var r = new Rect(i * _cell, 3, Math.Max(1, _cell - (_cell >= 4 ? 1 : 0)), h);
            if (t.At(index) is { } c)
            {
                dc.FillRectangle(OverlayPainter.Brush(OverlayPainter.EvidenceColour(c.Evidence, t.Kind)), r);
                if (c.Dying) dc.FillRectangle(OverlayPainter.Brush(0xFFFFFFFF), new Rect(r.X, r.Bottom - 4, r.Width, 3));
            }
            else dc.FillRectangle(OverlayPainter.Brush(index < processed ? 0xFF22252A : 0xFF3A3A3A), r);
            if (index == s.Cursor) dc.DrawRectangle(null, OverlayPainter.Pen(0xFFFFFFFF, 1.5), r.Inflate(1));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session is null || _count == 0) return;
        var i = (int)Math.Floor(e.GetPosition(this).X / _cell);
        if (i >= 0 && i < _count) _session.Seek(_first + i);
    }
}
