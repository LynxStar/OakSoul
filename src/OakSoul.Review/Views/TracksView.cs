using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OakSoul.Bake;
using OakSoul.Overlay;
using OakSoul.Review.Model;

namespace OakSoul.Review.Views;

/// <summary>How the Tracks tab orders its rows.</summary>
public enum TrackOrder { First, Name, Length }

/// <summary>
/// Every track of the stream as a row across the recording's time, coloured by evidence frame by frame (matched green,
/// carried amber, unverified red-grey, present without evidence the kind's colour), map changes as blue lines through
/// all rows, a dying frame as a white dot and the end of a track (before the stream's end) as a white bar,
/// so fragmentation (one creature split over many short rows) shows at a glance. Click a row to go to that frame in the
/// Review tab with the track selected; the wheel scrolls, Cmd+wheel zooms time, shift+wheel pans it.
/// </summary>
public sealed class TracksView : Control
{
    private const double LabelWidth = 300, RowHeight = 15, HeaderHeight = 24, Right = 12;
    private ReviewSession? _session;
    private List<TrackInfo> _rows = [];
    private double _scroll;
    private double _start, _span = -1;
    private int? _hoverRow;
    private int _hoverIndex = -1;

    public TracksView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public HashSet<string> Kinds { get; } = ["mob", "player", "pet", "drop"];
    public int MinFrames { get; set; } = 1;
    public TrackOrder Order { get; set; } = TrackOrder.First;

    /// <summary>A row was clicked: the track and the frame (timeline index) under the pointer.</summary>
    public event Action<TrackInfo, int>? Picked;

    /// <summary>What the pointer is over, for the status line.</summary>
    public event Action<string>? Status;

    public int RowCount => _rows.Count;

    public ReviewSession? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.StreamChanged -= Rebuild;
                _session.CursorChanged -= InvalidateVisual;
                _session.SelectionChanged -= InvalidateVisual;
            }
            _session = value;
            _span = -1;
            _scroll = 0;
            if (_session is not null)
            {
                _session.StreamChanged += Rebuild;
                _session.CursorChanged += InvalidateVisual;
                _session.SelectionChanged += InvalidateVisual;
            }
            Rebuild();
        }
    }

    /// <summary>Recomputes the rows from the stream's tracks and the filters.</summary>
    public void Rebuild()
    {
        var tracks = _session?.Stream?.Tracks.Values.Where(t => Kinds.Contains(t.Kind) && t.Cells.Count >= MinFrames) ?? [];
        _rows = (Order switch
        {
            TrackOrder.Name => tracks.OrderBy(t => t.Kind).ThenBy(t => t.Name ?? "~", StringComparer.Ordinal).ThenBy(t => t.First),
            TrackOrder.Length => tracks.OrderByDescending(t => t.Cells.Count).ThenBy(t => t.First),
            _ => tracks.OrderBy(t => t.First).ThenBy(t => t.Id),
        }).ToList();
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        InvalidateVisual();
    }

    /// <summary>Scrolls so the selected track's row is in view.</summary>
    public void Reveal(long track)
    {
        var i = _rows.FindIndex(t => t.Id == track);
        if (i < 0) return;
        var visible = RowsHeight;
        if (i * RowHeight < _scroll || (i + 1) * RowHeight > _scroll + visible) _scroll = Math.Clamp(i * RowHeight - visible / 3, 0, MaxScroll);
        InvalidateVisual();
    }

    private double RowsHeight => Math.Max(0, Bounds.Height - HeaderHeight);
    private double MaxScroll => Math.Max(0, _rows.Count * RowHeight - RowsHeight);
    private int Frames => _session?.Timeline.Count ?? 1;
    private double Plot => Math.Max(1, Bounds.Width - LabelWidth - Right);

    private void EnsureSpan()
    {
        if (_span <= 0) { _start = 0; _span = Frames; }
        _span = Math.Clamp(_span, Math.Min(20, Frames), Frames);
        _start = Math.Clamp(_start, 0, Math.Max(0, Frames - _span));
    }

    private double X(double index) => LabelWidth + (index - _start) / _span * Plot;
    private double IndexAt(double x) => _start + (x - LabelWidth) / Plot * _span;

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(OverlayPainter.Brush(0xFF121316), new Rect(Bounds.Size));
        var s = _session;
        if (s is null) return;
        EnsureSpan();
        var processed = s.Stream?.ProcessedUpTo ?? 0;
        var plot = new Rect(LabelWidth, HeaderHeight, Plot, RowsHeight);
        // Unprocessed time.
        if (processed < Frames && X(processed) < plot.Right)
            dc.FillRectangle(OverlayPainter.Brush(0xFF1E1F22), new Rect(Math.Max(plot.X, X(processed)), 0, plot.Right - Math.Max(plot.X, X(processed)), Bounds.Height));
        // Header: time ticks and map changes.
        // Ticks at least 56 pixels apart.
        var seconds = Math.Max(1e-3, s.Timeline[Math.Min(Frames - 1, (int)(_start + _span) - 1)].Time - s.Timeline[(int)_start].Time);
        var step = new[] { 1, 2, 5, 10, 15, 30, 60 }.FirstOrDefault(st => st * Plot / seconds >= 56, 120);
        var tFirst = s.Timeline[(int)Math.Floor(_start)].Time;
        var tLast = s.Timeline[Math.Min(Frames - 1, (int)Math.Ceiling(_start + _span) - 1)].Time;
        for (var t = Math.Ceiling(tFirst / step) * step; t <= tLast; t += step)
        {
            var x = X(s.Timeline.AtTime(t));
            dc.DrawLine(OverlayPainter.Pen(0xFF30343A, 1), new Point(x, HeaderHeight - 4), new Point(x, Bounds.Height));
            Text(dc, OverlayFrame.FormatTime(t)[..^2], new Point(x + 2, 2), 0xFF8A9098, 10);
        }
        using (dc.PushClip(new Rect(LabelWidth, 0, Plot, Bounds.Height)))
        {
            foreach (var m in s.MapMarks) dc.DrawLine(OverlayPainter.Pen(TimelineView.MapColour, 2), new Point(X(m), 0), new Point(X(m), Bounds.Height));
        }
        Text(dc, $"{_rows.Count} tracks{(s.Stream?.Tracks.Count is { } all && all != _rows.Count ? $" of {all}" : "")}", new Point(6, 4), 0xFFB8C4D0, 11);

        using (dc.PushClip(new Rect(0, HeaderHeight, Bounds.Width, RowsHeight)))
        {
            var firstRow = (int)Math.Floor(_scroll / RowHeight);
            for (var r = firstRow; r < _rows.Count && (r - firstRow) * RowHeight < RowsHeight + RowHeight; r++)
            {
                var t = _rows[r];
                var y = HeaderHeight + r * RowHeight - _scroll;
                var selected = s.SelectedTrack == t.Id;
                if (selected) dc.FillRectangle(OverlayPainter.Brush(0xFF2A3A50), new Rect(0, y, Bounds.Width, RowHeight));
                else if (_hoverRow == r) dc.FillRectangle(OverlayPainter.Brush(0xFF1E242C), new Rect(0, y, Bounds.Width, RowHeight));
                dc.FillRectangle(OverlayPainter.Brush(FrameRenderer.KindColour(t.Kind)), new Rect(4, y + 3, 8, RowHeight - 6));
                Text(dc, Label(t), new Point(16, y + 1), selected ? 0xFFFFFFFF : 0xFFC8D0D8, 10.5, LabelWidth - 20);
                using (dc.PushClip(new Rect(LabelWidth, y, Plot, RowHeight)))
                    Cells(dc, t, y, processed);
            }
        }
        // The cursor.
        var cx = X(s.Cursor + 0.5);
        if (cx >= LabelWidth && cx <= Bounds.Width - Right) dc.DrawLine(OverlayPainter.Pen(0xC0FFFFFF, 1), new Point(cx, 0), new Point(cx, Bounds.Height));
        // A scroll indicator.
        if (MaxScroll > 0)
        {
            var h = RowsHeight * RowsHeight / (_rows.Count * RowHeight);
            var y = HeaderHeight + _scroll / MaxScroll * (RowsHeight - h);
            dc.FillRectangle(OverlayPainter.Brush(0xFF3A4048), new Rect(Bounds.Width - 6, y, 4, Math.Max(12, h)), 2);
        }
    }

    /// <summary>"#14 Dark Axe Stump (mob)  46 fr  100% matched", "#87 Mob?  3 fr  0% matched" (as the labels write an unverified mob).</summary>
    private static string Label(TrackInfo t)
    {
        var matched = t.Kind is "mob" or "drop" ? $"  {100 * t.Matched / Math.Max(1, t.Cells.Count)}% matched" : "";
        return $"#{t.Id} {(t.Name is { } n ? $"{n} ({t.Kind})" : OverlayLabels.Title(t.Kind) + (t.Kind is "mob" or "drop" ? "?" : ""))}  {t.Cells.Count} fr{matched}";
    }

    /// <summary>
    /// A row's cells: a frame at least a pixel wide is drawn as itself; narrower frames are merged per screen column (the
    /// strongest evidence in the column wins); neighbouring pieces of one colour are drawn as one run.
    /// </summary>
    private void Cells(DrawingContext dc, TrackInfo t, double y, int processed)
    {
        double runStart = -1, runEnd = -1;
        uint runColour = 0;
        var col = int.MinValue;
        var best = Model.Evidence.None;
        void Add(double x0, double x1, uint colour)
        {
            if (runStart >= 0 && colour == runColour && x0 <= runEnd + 0.5) { runEnd = Math.Max(runEnd, x1); return; }
            FlushRun();
            runStart = x0; runEnd = x1; runColour = colour;
        }
        void FlushColumn()
        {
            if (col != int.MinValue) Add(col, col + 1, OverlayPainter.EvidenceColour(best, t.Kind));
            col = int.MinValue;
        }
        void FlushRun()
        {
            if (runStart >= 0) dc.FillRectangle(OverlayPainter.Brush(runColour), new Rect(runStart, y + 2, Math.Max(1, runEnd - runStart), RowHeight - 4));
            runStart = -1;
        }
        var end = _start + _span;
        foreach (var c in t.Cells)
        {
            if (c.Index + 1 < _start || c.Index > end) continue;
            double x0 = X(c.Index), x1 = X(c.Index + 1);
            if (x1 - x0 >= 1)
            {
                FlushColumn();
                Add(x0, x1 - (x1 - x0 >= 4 ? 1 : 0), OverlayPainter.EvidenceColour(c.Evidence, t.Kind));
                continue;
            }
            var x = (int)Math.Floor(x0);
            if (x != col)
            {
                FlushColumn();
                col = x;
                best = c.Evidence;
            }
            else if (Rank(c.Evidence) > Rank(best)) best = c.Evidence;
        }
        FlushColumn();
        FlushRun();
        foreach (var c in t.Cells)
            if (c.Dying && c.Index >= _start && c.Index <= end)
                dc.DrawEllipse(OverlayPainter.Brush(0xFFFFFFFF), null, new Point(X(c.Index + 0.5), y + RowHeight / 2), 2.5, 2.5);
        // The track ends before the stream does: a white bar after its last frame.
        if (t.Last < processed - 1 && t.Last + 1 >= _start && t.Last <= end)
        {
            var x = Math.Max(X(t.Last + 1), X(t.Last) + 1);
            dc.FillRectangle(OverlayPainter.Brush(0xFFFFFFFF), new Rect(x, y + 1, 2, RowHeight - 2));
        }
    }

    private static int Rank(Model.Evidence e) => e switch
    {
        Model.Evidence.Matched => 4,
        Model.Evidence.Carried => 3,
        Model.Evidence.Unverified => 2,
        Model.Evidence.Present => 1,
        _ => 0,
    };

    private static void Text(DrawingContext dc, string text, Point at, uint colour, double size, double maxWidth = double.PositiveInfinity)
    {
        var t = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, OverlayPainter.LabelFace, size, OverlayPainter.Brush(colour)) { MaxTextWidth = maxWidth, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(t, at);
    }

    private int? RowAt(double y)
    {
        if (y < HeaderHeight) return null;
        var r = (int)Math.Floor((y - HeaderHeight + _scroll) / RowHeight);
        return r >= 0 && r < _rows.Count ? r : null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (RowAt(p.Y) is not { } r || _session is null) return;
        var t = _rows[r];
        var index = p.X < LabelWidth ? t.First : Math.Clamp((int)Math.Floor(IndexAt(p.X)), 0, Frames - 1);
        // On the track's row but off its frames: its nearest frame.
        if (t.At(index) is null) index = t.Cells.MinBy(c => Math.Abs(c.Index - index)).Index;
        Picked?.Invoke(t, index);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var row = RowAt(p.Y);
        var index = p.X >= LabelWidth && _session is not null ? Math.Clamp((int)Math.Floor(IndexAt(p.X)), 0, Frames - 1) : -1;
        if (row != _hoverRow || index != _hoverIndex)
        {
            _hoverRow = row;
            _hoverIndex = index;
            InvalidateVisual();
            if (_session is { } s && row is { } r)
            {
                var t = _rows[r];
                var at = index >= 0 ? s.Timeline[index] : null;
                var cell = index >= 0 ? t.At(index) : null;
                Status?.Invoke($"{t.Title} ({t.Kind}): frames {s.Timeline[t.First].Sequence}..{s.Timeline[t.Last].Sequence}, {t.Cells.Count} frames over {t.Span}" +
                               (at is null ? "" : $"   at {OverlayFrame.FormatTime(at.Time)} seq {at.Sequence}: {(cell is { } c ? c.Evidence.ToString().ToLowerInvariant() + (c.Dying ? ", dying" : "") : "absent")}"));
            }
            else Status?.Invoke("");
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverRow = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        EnsureSpan();
        var p = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var at = p.X >= LabelWidth ? IndexAt(p.X) : _start + _span / 2;
            var factor = e.Delta.Y > 0 ? 0.8 : 1.25;
            var span = Math.Clamp(_span * factor, Math.Min(20, Frames), Frames);
            _start = at - (at - _start) * span / _span;
            _span = span;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            _start -= (Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y) * _span / 20;
        else _scroll = Math.Clamp(_scroll - e.Delta.Y * RowHeight * 3, 0, MaxScroll);
        EnsureSpan();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }
}
