using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OakSoul.Bake;
using OakSoul.Capture.Replay;
using OakSoul.Overlay;
using OakSoul.Review.Model;
using OakSoul.Review.Views;

namespace OakSoul.Review;

/// <summary>A row of the notes list.</summary>
public sealed class NoteRow(ReviewNote note, Verdict? verdict, string session, string root)
{
    public ReviewNote Note { get; } = note;
    public string Verdict { get; } = note.Promoted is not null && verdict is null ? "truth" : verdict?.Word ?? "note";
    public IBrush VerdictBrush { get; } = MainWindow.VerdictBrush(verdict?.Pass, verdict is null);
    public string Heading { get; } = $"{note.Id}  ·  {NoteCategory.Find(note.Category).Title}  ·  seq {note.Sequence:D7}{(note.Filled ? " (fill)" : "")} at {OverlayFrame.FormatTime(note.Time)}";
    public string Words { get; } = note.Text.Length == 0 ? "(no words)" : note.Text;
    public string Detail { get; } = Describe(note, verdict, session);
    public bool CanPromote { get; } = note.Promoted is null && TruthBook.Expectation(note) is not null;
    public Bitmap? RawCrop { get; } = Load(note.RawCrop, root);
    public Bitmap? OverlayCrop { get; } = Load(note.OverlayCrop, root);
    public bool HasCrops => RawCrop is not null || OverlayCrop is not null;

    /// <summary>A crop the note names (relative to the repository), or none when it is missing or unreadable.</summary>
    private static Bitmap? Load(string? path, string root)
    {
        if (path is null) return null;
        try { return new Bitmap(Path.GetFullPath(path, root)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static string Describe(ReviewNote n, Verdict? v, string session)
    {
        var e = n.Expect;
        var parts = new List<string> { n.IsPoint ? $"point {n.Point![0]},{n.Point[1]}" : $"rect {string.Join(",", n.Rect ?? [])}" };
        if (!e.IsEmpty)
            parts.Add("expect " + string.Join(" ", new[] { e.Kind, e.Name is null ? null : $"'{e.Name}'", e.State, e.NotKind is null ? null : $"not {e.NotKind}" }.OfType<string>()));
        if (n.Promoted is { } id) parts.Add($"promoted to truth {id}");
        if (v is not null) parts.Add(v.Why);
        if (n.Json["session"]?.GetValue<string>() is { } s && s != session) parts.Add($"of {s}");
        return string.Join("  ·  ", parts);
    }
}

/// <summary>The notes of one frame in the notes list.</summary>
public sealed record NoteGroup(string Header, List<NoteRow> Notes);

/// <summary>A frame of the events list and what happened on it.</summary>
public sealed record EventFrame(int Index, string Header, List<EventLine> Lines);

/// <summary>One event of a frame: a kill, a map change, a review note or a truth entry (with its region, to focus on).</summary>
public sealed record EventLine(int Index, string What, IBrush Brush, string Text, Focus? Focus = null);

/// <summary>A row of the truth list.</summary>
public sealed class TruthRow(TruthEntry entry)
{
    public TruthEntry Entry { get; } = entry;
    public string Verdict { get; } = entry.Verdict?.Word ?? "…";
    public IBrush VerdictBrush { get; } = MainWindow.VerdictBrush(entry.Verdict?.Pass, false);
    public string Heading { get; } = $"{entry.Id}  ·  seq {entry.Sequence:D7}  ·  {(entry.IsPoint ? "point" : "rect")} {string.Join(",", entry.Point ?? entry.Rect ?? [])}  ·  expect {entry.ExpectText}";
    public string Why { get; } = entry.Verdict?.Why ?? "";
    public string Words { get; } = $"{entry.Note}  — {entry.Source}";
}

/// <summary>A recording in the open panel.</summary>
public sealed class SessionRow(string name, string info)
{
    public string Name { get; } = name;
    public string Info { get; } = info;
}

/// <summary>
/// The review window: the Review tab (the frame with the overlay, the frame's readings, layers, the inspector and the
/// note editor), the Tracks tab, the Events tab (every frame where something happened, with what), the Notes tab (the
/// notes by frame with their crops, and the truth), and under them the transport and the timeline. More tabs
/// (pipeline stages, per-pixel matches, prediction ghosts, the world map, compare mode, cost graphs) slot into the tab
/// control; Cmd+1..9 selects tabs by position.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly double[] Speeds = [0.25, 1, 2];
    private readonly Queue<(double Wall, double Time)> _rate = new();
    private static readonly string[] KindChoices = ["—", .. ReviewSession.EntityKinds, NoteExpect.Nothing];
    private static readonly string[] NotKindChoices = ["—", .. ReviewSession.EntityKinds];

    private readonly ToolOptions _options;
    private readonly RepoPaths _paths;
    private readonly DispatcherTimer _playTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, CheckBox> _layerBoxes = [], _kindBoxes = [];
    private readonly List<ToggleButton> _categoryButtons = [];
    private ReviewSession? _session;
    private bool _playing, _refreshQueued;
    private TimeSpan _lastTick;
    private double _playTime;
    private NoteCategory? _category;
    private (int X, int Y, int W, int H) _pendingRegion;
    private bool _pendingPoint;
    private int _pendingIndex = -1;
    private Action? _confirm;
    private List<(ReviewNote Note, System.Text.Json.Nodes.JsonObject Entry)> _promotions = [];

    public MainWindow() : this(new ToolOptions()) { }

    public MainWindow(ToolOptions options, RepoPaths? paths = null)
    {
        _options = options;
        _paths = paths ?? new RepoPaths();
        InitializeComponent();
        BuildToggles();
        BuildEditor();
        Wire();
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, OnPlayTick);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => _session?.Dispose();
        if (options.Session is { } s) Open(s, options.Video, options.Frame, options.Time);
        else ShowOpenPanel();
    }

    public ReviewSession? Session => _session;

    // ---- opening ----

    /// <summary>Opens a recording in keyframe or video mode, at a capture sequence or a time.</summary>
    public void Open(string name, bool video, long? frame = null, string? time = null)
    {
        StopPlaying();
        CloseEditor();
        ReviewSession session;
        try { session = new ReviewSession(_paths, _options, name, video, a => Dispatcher.UIThread.Post(a)); }
        catch (Exception e) when (e is BakeException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Status($"cannot open {name}: {e.Message}");
            ShowOpenPanel();
            return;
        }
        if (_session is { } old)
        {
            old.CursorChanged -= OnCursor;
            old.StreamChanged -= QueueRefresh;
            old.SelectionChanged -= OnSelection;
            old.NotesChanged -= QueueRefresh;
            old.Dispose();
        }
        _session = session;
        foreach (var (layer, box) in _layerBoxes) if (box.IsChecked != true) session.Layers.Remove(layer);
        foreach (var (kind, box) in _kindBoxes) if (box.IsChecked != true) session.Kinds.Remove(kind);
        session.Labels = LabelsToggle.IsChecked == true;
        session.ShowNotes = NotesToggle.IsChecked == true;
        session.ShowTruth = TruthToggle.IsChecked == true;
        session.CursorChanged += OnCursor;
        session.StreamChanged += QueueRefresh;
        session.SelectionChanged += OnSelection;
        session.NotesChanged += QueueRefresh;
        session.Pixels.Failed += m => Status($"pixels: {m}");
        Frame.Session = session;
        TimelineBar.Session = session;
        Tracks.Session = session;
        Strip.Show(session, null);
        KeyframesMode.IsChecked = !video;
        VideoMode.IsChecked = video;
        SessionTitle.Text = session.Name;
        Title = $"OakSoul review: {session.Name} ({session.Mode})";
        OpenPanel.IsVisible = false;
        session.Start();
        if (frame is { } seq) session.Seek(session.Timeline.Nearest(seq));
        else if (time is not null) session.Jump(time);
        Status($"opened {session.Bake.Recording} in {session.Mode} mode: {session.Timeline.Count} frames");
        RefreshAll();
    }

    private void ShowOpenPanel()
    {
        var rows = _paths.Sessions().Select(n => new SessionRow(n, Describe(n))).ToList();
        SessionList.ItemsSource = rows;
        SessionList.SelectedItem = rows.FirstOrDefault(r => r.Name == _session?.Name) ?? rows.FirstOrDefault(r => r.Name == "fx-combat-exact") ?? rows.FirstOrDefault();
        OpenRoot.Text = rows.Count == 0 ? $"no recordings under {_paths.Recordings}" : $"recordings under {_paths.Recordings} (read-only)";
        OpenVideo.IsChecked = _session?.Video ?? false;
        OpenKeyframes.IsChecked = !(_session?.Video ?? false);
        OpenCancel.IsVisible = _session is not null;
        OpenPanel.IsVisible = true;
        SessionList.Focus();
    }

    private string Describe(string name)
    {
        try
        {
            var dir = Path.Combine(_paths.Recordings, name);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "session.json")));
            var r = doc.RootElement;
            double D(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var streams = new[] { ("keyframes", "overlay.jsonl.gz"), ("video", "overlay-video.jsonl.gz") }
                .Where(s => File.Exists(Path.Combine("data", "overlays", name, s.Item2))).Select(s => s.Item1).ToList();
            return $"{D("durationSeconds"):0} s, {D("keyframes"):0} keyframes, {D("deliveredFrames"):0} frames, " +
                   $"{(RecordingFrameSource.IsLosslessVideo(r) ? "lossless" : "lossy")} video; streams: {(streams.Count == 0 ? "none yet" : string.Join(", ", streams))}";
        }
        catch (Exception e) when (e is IOException or JsonException) { return e.Message; }
    }

    // ---- building the panels ----

    private static Control Swatch(uint colour, double w = 10, double h = 10) =>
        new Border { Width = w, Height = h, Background = OverlayPainter.Brush(colour), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };

    private static Control Legend(uint colour, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 4,
        Children = { Swatch(colour), new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center } },
    };

    private void BuildToggles()
    {
        foreach (var layer in OverlayLayers.All)
        {
            var box = new CheckBox { Content = layer.Name, IsChecked = true };
            ToolTip.SetTip(box, layer.Description);
            box.IsCheckedChanged += (_, _) => Toggle(_session?.Layers, layer.Name, box.IsChecked == true);
            _layerBoxes[layer.Name] = box;
            LayerToggles.Children.Add(box);
        }
        foreach (var kind in ReviewSession.EntityKinds)
        {
            var box = new CheckBox
            {
                IsChecked = true,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Swatch(FrameRenderer.KindColour(kind)), new TextBlock { Text = OverlayLabels.Title(kind) } } },
            };
            ToolTip.SetTip(box, OverlayLayers.Find(OverlayLayers.Entity)!.Kinds.First(k => k.Kind == kind).Meaning);
            box.IsCheckedChanged += (_, _) => Toggle(_session?.Kinds, kind, box.IsChecked == true);
            _kindBoxes[kind] = box;
            KindToggles.Children.Add(box);

            var track = new ToggleButton
            {
                IsChecked = Tracks.Kinds.Contains(kind), FontSize = 12,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Swatch(FrameRenderer.KindColour(kind), 8, 8), new TextBlock { Text = kind } } },
            };
            track.IsCheckedChanged += (_, _) =>
            {
                if (track.IsChecked == true) Tracks.Kinds.Add(kind); else Tracks.Kinds.Remove(kind);
                Tracks.Rebuild();
            };
            TrackKindToggles.Children.Add(track);
        }
        LabelsToggle.IsCheckedChanged += (_, _) => { if (_session is { } s) { s.Labels = LabelsToggle.IsChecked == true; s.ViewUpdated(); } };
        NotesToggle.IsCheckedChanged += (_, _) => { if (_session is { } s) { s.ShowNotes = NotesToggle.IsChecked == true; s.ViewUpdated(); } };
        TruthToggle.IsCheckedChanged += (_, _) => { if (_session is { } s) { s.ShowTruth = TruthToggle.IsChecked == true; s.ViewUpdated(); } };

        foreach (var (e, text) in new[] { (Model.Evidence.Matched, "matched"), (Model.Evidence.Carried, "carried"), (Model.Evidence.Unverified, "unverified") })
        {
            EvidenceLegend.Children.Add(Legend(OverlayPainter.EvidenceColour(e, "mob"), text));
            StripLegend.Children.Add(Legend(OverlayPainter.EvidenceColour(e, "mob"), text));
        }
        EvidenceLegend.Children.Add(Legend(OverlayPainter.EvidenceColour(Model.Evidence.Present, "pet"), "present (no evidence; kind colour)"));
        EvidenceLegend.Children.Add(Legend(0xFFFFFFFF, "dot dying, bar track ends"));
        EvidenceLegend.Children.Add(Legend(TimelineView.KillColour, "kill"));
        EvidenceLegend.Children.Add(Legend(TimelineView.MapColour, "map change"));
        StripLegend.Children.Add(Legend(0xFF22252A, "absent"));

        TrackOrderBox.ItemsSource = new[] { "first seen", "kind and name", "longest" };
        TrackOrderBox.SelectedIndex = 0;
        TrackOrderBox.SelectionChanged += (_, _) =>
        {
            Tracks.Order = TrackOrderBox.SelectedIndex switch { 1 => TrackOrder.Name, 2 => TrackOrder.Length, _ => TrackOrder.First };
            Tracks.Rebuild();
        };
        MinFrames.ValueChanged += (_, _) =>
        {
            Tracks.MinFrames = (int)(MinFrames.Value ?? 1);
            Tracks.Rebuild();
        };
        SpeedBox.ItemsSource = Speeds.Select(v => $"{v.ToString("0.##", CultureInfo.InvariantCulture)}x").ToList();
        SpeedBox.SelectedIndex = 1;
    }

    private void Toggle(HashSet<string>? set, string name, bool on)
    {
        if (set is null) return;
        if (on) set.Add(name); else set.Remove(name);
        _session?.ViewUpdated();
    }

    private void BuildEditor()
    {
        foreach (var c in NoteCategory.All)
        {
            var b = new ToggleButton { Content = $"{c.Key} {c.Title}", Margin = new Thickness(0, 0, 3, 3), FontSize = 12, Tag = c };
            b.Click += (_, _) => PickCategory(c);
            _categoryButtons.Add(b);
            CategoryButtons.Children.Add(b);
        }
        ExpectKind.ItemsSource = KindChoices;
        ExpectNotKind.ItemsSource = NotKindChoices;
        ExpectState.ItemsSource = new[] { "dying" };
    }

    private void Wire()
    {
        OpenButton.Click += (_, _) => ShowOpenPanel();
        OpenCancel.Click += (_, _) => OpenPanel.IsVisible = false;
        OpenConfirm.Click += (_, _) => OpenSelected();
        SessionList.DoubleTapped += (_, _) => OpenSelected();
        KeyframesMode.IsCheckedChanged += (_, _) => SwitchMode(false, KeyframesMode);
        VideoMode.IsCheckedChanged += (_, _) => SwitchMode(true, VideoMode);
        RegenerateButton.Click += (_, _) =>
        {
            if (_session is not { } s) return;
            if (s.Generating) s.StopGenerating();
            else s.Regenerate();
        };
        NewPacketButton.Click += (_, _) =>
        {
            _session?.Packets.StartNewPacket();
            UpdatePacket();
        };

        SelectTool.Click += (_, _) => SetTool(Tool.Select);
        RectTool.Click += (_, _) => SetTool(Tool.Rect);
        PointTool.Click += (_, _) => SetTool(Tool.Point);
        ZoomIn.Click += (_, _) => Frame.ZoomBy(1);
        ZoomOut.Click += (_, _) => Frame.ZoomBy(-1);
        ZoomFit.Click += (_, _) => Frame.Fit();
        Frame.ZoomChanged += UpdateZoom;
        Frame.ShownChanged += UpdateFrameInfo;
        Frame.HoverChanged += UpdateHover;
        Frame.EntityClicked += item => _session?.Select(item?.Long("track"));
        Frame.RegionDrawn += OpenEditor;

        SaveNoteButton.Click += (_, _) => SaveNote();
        CancelNoteButton.Click += (_, _) => CloseEditor();
        ExpectName.TextChanged += (_, _) =>
        {
            // A species picked with no kind given is a mob.
            if (ExpectKind.SelectedIndex <= 0 && ExpectName.Text is { Length: > 0 } t && (ExpectName.ItemsSource as IEnumerable<string>)?.Contains(t) == true)
                ExpectKind.SelectedItem = "mob";
        };

        StartButton.Click += (_, _) => Navigate(() => _session?.Seek(0));
        EndButton.Click += (_, _) => Navigate(() => _session?.Seek(_session.Timeline.Count - 1));
        BackFrame.Click += (_, _) => Navigate(() => _session?.Step(-1));
        FwdFrame.Click += (_, _) => Navigate(() => _session?.Step(1));
        PlayButton.Click += (_, _) => TogglePlay();
        GotoBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { GoToFrame(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseGoto(); e.Handled = true; }
        };

        Tracks.Picked += (t, index) =>
        {
            if (_session is not { } s) return;
            s.Select(t.Id);
            s.Seek(index);
            Tabs.SelectedItem = ReviewTab;
            if (s.Stream?.At(index)?.Layer(OverlayLayers.Entity).FirstOrDefault(e => e.Long("track") == t.Id)?.Rect is { } r) Frame.CentreOn(r);
        };
        Tracks.Status += text => TracksStatus.Text = text;

        PromoteAllButton.Click += (_, _) => ShowPromote(_session?.Packets.Notes.ToList() ?? []);
        ReloadButton.Click += (_, _) => _session?.ReloadNotesAndTruth();
        ConfirmCancel.Click += (_, _) => CloseConfirm();
        ConfirmButton.Click += (_, _) =>
        {
            var action = _confirm;
            CloseConfirm();
            action?.Invoke();
        };
    }

    private void OpenSelected()
    {
        if (SessionList.SelectedItem is SessionRow row) Open(row.Name, OpenVideo.IsChecked == true);
    }

    private void SwitchMode(bool video, RadioButton source)
    {
        if (source.IsChecked != true || _session is not { } s || s.Video == video) return;
        Open(s.Name, video, s.Entry.Sequence);
    }

    // ---- refreshing ----

    private void OnCursor()
    {
        if (_pendingIndex >= 0)
        {
            Frame.Pending = _session?.Cursor == _pendingIndex ? _pendingRegion : null;
            Frame.PendingIsPoint = _pendingPoint;
        }
        UpdateFrameInfo();
        UpdateInspector();
    }

    private void OnSelection()
    {
        UpdateInspector();
        if (_session?.SelectedTrack is { } t) Tracks.Reveal(t);
    }

    /// <summary>Stream and notes updates come in bursts while a stream is read; the panels follow at most once per dispatch.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshAll();
        }, DispatcherPriority.Background);
    }

    public void RefreshAll()
    {
        _session?.Recheck();
        UpdateStream();
        UpdateFrameInfo();
        UpdateInspector();
        UpdateNotes();
        UpdatePacket();
        UpdateZoom();
    }

    public static IBrush VerdictBrush(bool? pass, bool none) =>
        OverlayPainter.Brush(none ? 0xFF4A4F57 : pass switch { true => 0xFF1F7A4C, false => 0xFF9A2D2D, _ => 0xFF55595F });

    private void UpdateStream()
    {
        if (_session is not { } s) return;
        var stream = s.Stream;
        var (badge, colour) = s.State switch
        {
            StreamState.Generating => ("generating", 0xFF2D5F9Au),
            StreamState.Failed => ("failed", 0xFF9A2D2Du),
            StreamState.Missing => ("no stream", 0xFF55595Fu),
            _ => s.Provenance?.Level switch
            {
                Freshness.Current => ("current", 0xFF1F7A4Cu),
                Freshness.Unknown => ("provenance unknown", 0xFF8A6A1Au),
                Freshness.Stale => ("stale", 0xFF9A2D2Du),
                _ => ("reading", 0xFF55595Fu),
            },
        };
        StreamBadgeText.Text = badge;
        StreamBadge.Background = OverlayPainter.Brush(colour);
        var count = stream?.Count ?? 0;
        var total = s.Timeline.Count;
        var why = s.Provenance is { } p ? p.Reasons.FirstOrDefault() : null;
        StreamText.Text = s.State switch
        {
            StreamState.Generating => $"{count} of {total} frames processed  ·  {s.StatusText}",
            StreamState.Loading => $"reading {s.StreamPath}: {count} frames",
            StreamState.Failed => $"{s.StatusText}  ({count} frames read)",
            _ => $"{s.StreamPath}  ·  {count} frames{(why is null ? "" : $"  ·  {why}")}",
        };
        var tip = new StringBuilder();
        tip.AppendLine(s.StreamPath);
        if (s.Provenance is { } prov) foreach (var r in prov.Reasons) tip.AppendLine($"• {r}");
        if (stream is not null)
        {
            if (stream.BadLines > 0) tip.AppendLine($"{stream.BadLines} unreadable lines skipped");
            if (stream.Misplaced + stream.Unplaced > 0) tip.AppendLine($"{stream.Misplaced} frames placed by sequence, {stream.Unplaced} not on the timeline");
            foreach (var (k, v) in stream.HeaderFields) tip.AppendLine($"{k}: {v}");
        }
        foreach (var line in s.GeneratorOutput.TakeLast(12)) tip.AppendLine(line);
        ToolTip.SetTip(StreamText, tip.ToString().TrimEnd());
        ToolTip.SetTip(StreamBadge, tip.ToString().TrimEnd());
        StreamProgress.IsVisible = s.State == StreamState.Generating;
        StreamProgress.Value = total == 0 ? 0 : (double)count / total;
        RegenerateButton.Content = s.Generating ? "Stop" : "Regenerate";
    }

    private void UpdateZoom()
    {
        ZoomText.Text = $"zoom {Frame.Zoom.ToString("0.##", CultureInfo.InvariantCulture)}×";
        ToolTip.SetTip(ZoomText, $"{Frame.Zoom.ToString("0.##", CultureInfo.InvariantCulture)} screen pixels per game pixel ({Frame.Scale.ToString("0.##", CultureInfo.InvariantCulture)} points)");
    }

    private void UpdatePacket()
    {
        if (_session is not { } s) { PacketText.Text = ""; return; }
        var (name, review, isNew) = s.Packets.NextPacket();
        var count = s.Packets.Active is { } a && a.Name == name ? a.Notes.Count : 0;
        PacketText.Text = isNew ? $"next note starts packet {name} (review {review})" : $"packet {name} (review {review}, {count} note{(count == 1 ? "" : "s")})";
        ToolTip.SetTip(PacketText, Path.Combine(s.Packets.Dir, name + ".jsonl"));
    }

    // Each info panel shows a fixed number of short lines (blank where a value is absent, never wrapped), so the panels
    // keep their size as frames advance.
    private const int FrameLines = 12, HudLines = 8, CountLines = 7;

    private void UpdateFrameInfo()
    {
        if (_session is not { } s) return;
        var e = s.Entry;
        var f = s.Frame;
        var info = new List<string>
        {
            $"frame {s.Cursor + 1} of {s.Timeline.Count}",
            $"{s.Name}  {s.Mode}",
            $"seq {e.Sequence:D7}{(e.Filled ? "  (fill: repeats it)" : "")}",
            $"t {OverlayFrame.FormatTime(e.Time)}  ({(f?.Time ?? e.Time).ToString("0.000", CultureInfo.InvariantCulture)} s)",
            $"pts {(f?.Pts ?? e.Pts).ToString("0.000", CultureInfo.InvariantCulture)}",
            $"pixels {(s.Video ? $"video frame {e.VideoFrame}" : "keyframe PNG")}{(Frame.Shown != s.Cursor ? " (decoding)" : "")}",
        };
        if (f is null)
            info.Add(s.State == StreamState.Generating ? "not processed yet" : "not in the stream");
        else
        {
            var names = f.Layer(OverlayLayers.Minimap).FirstOrDefault(i => i.Kind == "names")?.Label;
            info.Add($"map {f.MapId?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            info.Add($"  {names ?? "(names not read)"}");
            var cam = f.Layer(OverlayLayers.Camera).FirstOrDefault();
            info.Add(cam?.Label ?? (f.Camera is { } c ? $"cam {c.X},{c.Y}" : "no camera"));
            info.Add(cam is null ? "" : $"  guess {Pair(cam.Ints("guess"))}  offset {Pair(cam.Ints("offset"))}");
            info.Add(cam is null ? "" : $"  score {cam.Double("score")?.ToString("0.000", CultureInfo.InvariantCulture) ?? "-"}  conf {cam.Double("confidence")?.ToString("0.00", CultureInfo.InvariantCulture)}");
            info.Add(f.Detect is { } d ? $"detector {d}" : $"detector skipped: {f.Skip}");
            if (s.Stream?.ExtrasAt(s.Cursor) is { } extras)
                foreach (var (k, v) in extras) info.Add($"{k} = {v}  (new field)");
        }
        FrameInfo.Text = Lines(info, FrameLines);

        var hud = new List<string>();
        if (f is not null)
        {
            OverlayItem? Region(string name) => f.Layer(OverlayLayers.Hud).FirstOrDefault(i => i.Kind == "region" && i.Str("name") == name);
            var id = Region("status.class_name");
            hud.Add($"{Region("status.level")?.Label ?? "Lv ?"}  {id?.Str("character") ?? "?"}");
            hud.Add(id?.Str("job") ?? "");
            foreach (var name in new[] { "status.hp", "status.mp", "status.exp" })
                hud.Add(Region(name) is { } r ? $"{r.Label}{(r.Double("bar") is { } bar ? $"  bar {bar.ToString("0.000", CultureInfo.InvariantCulture)}" : "")}" : "");
            hud.Add(Region("buffs.row1")?.Label ?? "");
            hud.Add(Region("quickslot")?.Label ?? "");
            hud.Add(f.Layer(OverlayLayers.Self).FirstOrDefault(i => i.Kind == "character") is { } self
                ? (self.Label ?? "self (no world position)") + (self.Bool("masked") ? "  masked" : "")
                : "");
        }
        CharacterInfo.Text = Lines(hud, HudLines);

        var counts = new List<string>();
        if (f is not null)
        {
            var ents = f.Layer(OverlayLayers.Entity).ToList();
            var mobs = ents.Where(x => x.Kind == "mob").ToList();
            counts.Add($"mob {mobs.Count}: matched {mobs.Count(m => m.Str("evidence") == "matched")}");
            counts.Add($"  carried ~{mobs.Count(m => m.Str("evidence") == "carried")}  unverified ?{mobs.Count(m => m.Str("evidence") == "unverified")}");
            counts.AddRange(ReviewSession.EntityKinds.Where(k => k != "mob").Select(k => $"{k} {ents.Count(x => x.Kind == k)}").Append($"tags {f.Layer(OverlayLayers.Tags).Count()}")
                .Chunk(3).Select(row => string.Join("  ", row)));
        }
        while (counts.Count < CountLines - 2) counts.Add("");
        if (s.Stream is { } st)
        {
            counts.Add($"so far: {st.Kills.Count(k => k <= s.Cursor)} kills of {st.Kills.Count}");
            counts.Add($"  {st.Tracks.Count} tracks");
        }
        CountsInfo.Text = Lines(counts, CountLines);
    }

    /// <summary>The lines, padded with blank ones to at least <paramref name="count"/>.</summary>
    private static string Lines(List<string> lines, int count) =>
        string.Join("\n", lines.Concat(Enumerable.Repeat("", Math.Max(0, count - lines.Count))));

    private static string Pair(int[]? v) => v is [var a, var b] ? $"{a},{b}" : "-";

    private void UpdateInspector()
    {
        if (_session is not { } s) return;
        if (s.SelectedTrack is not { } id || s.Stream?.Tracks.GetValueOrDefault(id) is not { } track)
        {
            InspectorTitle.Text = s.SelectedTrack is { } missing ? $"track #{missing} is not in this stream" : "click an entity to inspect its track";
            Strip.Show(s, null);
            InspectorText.Text = "";
            return;
        }
        var first = s.Timeline[track.First];
        var last = s.Timeline[track.Last];
        InspectorTitle.Text = $"{track.Title}  ({track.Kind})  seq {first.Sequence}..{last.Sequence}, {OverlayFrame.FormatTime(first.Time)}..{OverlayFrame.FormatTime(last.Time)}, in {track.Cells.Count} of {track.Span} frames";
        Strip.Show(s, track);
        var item = s.Frame?.Layer(OverlayLayers.Entity).FirstOrDefault(e => e.Long("track") == id);
        if (item is null)
        {
            InspectorText.Text = s.Frame is null ? "this frame is not processed yet" : "not in this frame (the strip shows where it is)";
            return;
        }
        InspectorText.Text = Record(item);
    }

    /// <summary>An item's full record: every field in the stream's order, fields the registry does not know marked as new.</summary>
    private static string Record(OverlayItem item)
    {
        var text = new StringBuilder();
        var unknown = OverlayLayers.Check(item).ToList();
        text.AppendLine($"layer = {item.Layer}");
        text.AppendLine($"kind = {item.Kind}");
        foreach (var (name, value) in item.Fields)
            text.AppendLine($"{name} = {OverlayItem.Format(value)}{(unknown.Any(u => u.EndsWith($"unknown field {name}", StringComparison.Ordinal)) ? "   (new field)" : "")}");
        return text.ToString().TrimEnd();
    }

    private void UpdateHover()
    {
        PointerText.Text = Frame.PointerPixel is { } p ? $"x {p.X}  y {p.Y}{(Frame.Hover?.Long("track") is { } t ? $"  #{t}" : "")}" : "";
        if (Frame.Hover is not { } item)
        {
            HoverCard.IsVisible = false;
            return;
        }
        HoverTitle.Text = item.Label ?? $"{item.Kind}";
        HoverTitle.Foreground = OverlayPainter.Brush(FrameRenderer.KindColour(item.Kind));
        HoverText.Text = string.Join("\n", FrameRenderer.ListLines([item]).Select(l => l.Text));
        // Below the hovered box (above it when there is no room), so the card never hides what it describes.
        var box = item.Rect is { } r ? Frame.ScreenRect(r) : Frame.PointerPixel is { } px ? Frame.ScreenRect((px.X, px.Y, 1, 1)) : default;
        HoverCard.IsVisible = true;
        HoverCard.Measure(Size.Infinity);
        var size = HoverCard.DesiredSize;
        var x = Math.Clamp(box.X, 0, Math.Max(0, Frame.Bounds.Width - size.Width));
        var y = box.Bottom + 10 + size.Height <= Frame.Bounds.Height ? box.Bottom + 10 : Math.Max(0, box.Y - size.Height - 10);
        Canvas.SetLeft(HoverCard, x);
        Canvas.SetTop(HoverCard, y);
        HoverCard.IsVisible = true;
    }

    private void UpdateNotes()
    {
        if (_session is not { } s) return;
        var notes = s.Packets.Notes.Select(n => (Index: s.NoteIndex(n), Row: new NoteRow(n, TruthBook.CheckNote(n, s.Stream, s.Timeline), s.Name, _paths.Root))).ToList();
        var entries = s.Truth.Entries;
        // The lists are rebuilt only when what they show changed (stream updates come often while a stream is read).
        var key = string.Join("|", notes.Select(n => $"{n.Row.Note.Id}{n.Row.Verdict}{n.Row.Detail}{n.Row.Note.Text}")) + "#" + string.Join("|", entries.Select(e => $"{e.Id}{e.Verdict?.Word}{e.Verdict?.Why}"))
                  + $"#{s.Stream?.Kills.Count}#{s.Stream?.MapChanges.Count}";
        if (key == _listsKey) return;
        _listsKey = key;
        NotesList.ItemsSource = notes.GroupBy(n => n.Index).OrderBy(g => g.Key)
            .Select(g => new NoteGroup(FrameHeader(s, g.Key), [.. g.Select(n => n.Row)])).ToList();
        var packets = s.Packets.Packets.Count;
        NotesTitle.Text = $"Review notes: {notes.Count} in {packets} packet{(packets == 1 ? "" : "s")}";
        ToolTip.SetTip(NotesTitle, s.Packets.Dir);
        TruthList.ItemsSource = entries.Select(e => new TruthRow(e)).ToList();
        TruthTitle.Text = $"Ground truth: {entries.Count} entries, {entries.Count(e => e.Verdict?.Pass == true)} pass, " +
                          $"{entries.Count(e => e.Verdict?.Pass == false)} fail{(entries.Any(e => e.Verdict?.Pass is null) ? $", {entries.Count(e => e.Verdict?.Pass is null)} waiting for the stream" : "")}";
        ToolTip.SetTip(TruthTitle, s.Truth.Path);
        UpdateEvents(s, notes);
    }

    private static string FrameHeader(ReviewSession s, int index)
    {
        var e = s.Timeline[index];
        return $"Frame {index + 1}  ·  {OverlayFrame.FormatTime(e.Time)}  ·  seq {e.Sequence:D7}{(e.Filled ? " (fill)" : "")}";
    }

    /// <summary>Every frame where something happened (a kill, a map change, a note, a truth entry), in order.</summary>
    private void UpdateEvents(ReviewSession s, List<(int Index, NoteRow Row)> notes)
    {
        var lines = new List<EventLine>();
        if (s.Stream is { } st)
        {
            foreach (var k in st.Kills)
            {
                var exp = st.At(k)?.Layer(OverlayLayers.Hud).FirstOrDefault(i => i.Str("name") == "status.exp");
                lines.Add(new EventLine(k, "kill", OverlayPainter.Brush(0xFF1F7A4C), $"EXP went up{(exp?.Label is { } l ? $": {l}" : "")}"));
            }
            foreach (var m in st.MapChanges)
                lines.Add(new EventLine(m.Index, "map", OverlayPainter.Brush(0xFF2D5F9A), $"map {m.From?.ToString(CultureInfo.InvariantCulture) ?? "?"} → {m.To}"));
        }
        foreach (var (index, row) in notes)
        {
            var n = row.Note;
            lines.Add(new EventLine(index, "note", OverlayPainter.Brush(0xFF8A7420), $"{n.Id}  ·  {NoteCategory.Find(n.Category).Title}  ·  {row.Words}", new Focus(n.Region, n.IsPoint, n.Id, false)));
        }
        foreach (var t in s.Truth.Entries)
            lines.Add(new EventLine(s.Timeline.Nearest(t.Sequence), $"truth {t.Verdict?.Word ?? "…"}", VerdictBrush(t.Verdict?.Pass, false),
                $"{t.Id}  ·  expect {t.ExpectText}{(t.Verdict?.Why is { Length: > 0 } why ? $"  ·  {why}" : "")}", new Focus(t.Region, t.IsPoint, t.Id, true)));
        var frames = lines.GroupBy(l => l.Index).OrderBy(g => g.Key).Select(g => new EventFrame(g.Key, FrameHeader(s, g.Key), [.. g])).ToList();
        EventsList.ItemsSource = frames;
        EventsTitle.Text = $"Events: {frames.Count} frames, {lines.Count(l => l.What == "kill")} kills, {lines.Count(l => l.What == "map")} map changes, " +
                           $"{notes.Count} notes, {s.Truth.Entries.Count} truth entries";
    }

    private string? _listsKey;

    // ---- navigation ----

    private void SetTool(Tool tool)
    {
        Frame.Tool = tool;
        SelectTool.IsChecked = tool == Tool.Select;
        RectTool.IsChecked = tool == Tool.Rect;
        PointTool.IsChecked = tool == Tool.Point;
        Frame.Cursor = tool == Tool.Select ? Cursor.Default : new Cursor(StandardCursorType.Cross);
    }

    private void SelectTab(int index)
    {
        if (index < Tabs.ItemCount) Tabs.SelectedIndex = index;
    }

    // ---- playback ----

    private void TogglePlay()
    {
        if (_playing) StopPlaying();
        else StartPlaying();
    }

    private void StartPlaying()
    {
        if (_session is not { } s) return;
        if (s.Cursor >= s.Timeline.Count - 1) s.Seek(0);
        _playing = true;
        _playTime = s.Entry.Time;
        _lastTick = _clock.Elapsed;
        _playTimer.Start();
        _rate.Clear();
        RateText.Text = "";
        PlayButton.Content = "❚❚ Pause";
        s.Pixels.Want(s.Cursor, true);
    }

    private void StopPlaying()
    {
        if (!_playing) return;
        _playing = false;
        _playTimer.Stop();
        RateText.Text = "";
        PlayButton.Content = "▶ Play";
        if (_session is { } s) s.Pixels.Want(s.Cursor);
    }

    /// <summary>
    /// Advances the cursor to the frame of the play clock, only over frames already decoded: when decoding falls behind,
    /// the clock waits for it rather than skip frames.
    /// </summary>
    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_session is not { } s || !_playing) return;
        var now = _clock.Elapsed;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _playTime += Math.Min(dt, 0.25) * Speeds[Math.Clamp(SpeedBox.SelectedIndex, 0, Speeds.Length - 1)];
        var target = s.Timeline.AtTime(_playTime);
        if (target <= s.Cursor) { s.Pixels.Want(s.Cursor, true); return; }
        var next = s.Cursor;
        while (next < target && s.Pixels.Has(next + 1)) next++;
        if (next == s.Cursor)
        {
            _playTime = Math.Min(_playTime, s.Timeline[Math.Min(s.Cursor + 1, s.Timeline.Count - 1)].Time);
            s.Pixels.Want(s.Cursor + 1, true);
            return;
        }
        if (next < target) _playTime = s.Timeline[next].Time;
        s.Seek(next, playing: true);
        ShowRate(now.TotalSeconds, s.Entry.Time);
        if (next >= s.Timeline.Count - 1) StopPlaying();
    }

    /// <summary>Shows the recording seconds played per wall second over the last second, against the speed asked for.</summary>
    private void ShowRate(double wall, double time)
    {
        _rate.Enqueue((wall, time));
        while (_rate.Count > 2 && wall - _rate.Peek().Wall > 1) _rate.Dequeue();
        var (w0, t0) = _rate.Peek();
        if (wall - w0 < 0.5) return;
        var actual = (time - t0) / (wall - w0);
        var asked = Speeds[Math.Clamp(SpeedBox.SelectedIndex, 0, Speeds.Length - 1)];
        var behind = actual < asked * 0.9;
        RateText.Text = $"playing {actual.ToString("0.00", CultureInfo.InvariantCulture)}x{(behind ? " (decode)" : "")}";
        RateText.Foreground = OverlayPainter.Brush(behind ? 0xFFE0A040u : 0xFF8FC89Fu);
    }

    // ---- keyboard ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var command = mods.HasFlag(KeyModifiers.Meta) || mods.HasFlag(KeyModifiers.Control);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        if (ConfirmPanel.IsVisible)
        {
            if (e.Key == Key.Escape) { CloseConfirm(); e.Handled = true; }
            return;
        }
        if (GotoPanel.IsVisible)
        {
            if (e.Key == Key.Escape) { CloseGoto(); e.Handled = true; }
            return;
        }
        if (OpenPanel.IsVisible)
        {
            if (e.Key == Key.Escape && _session is not null) { OpenPanel.IsVisible = false; e.Handled = true; }
            else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
            return;
        }
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        if (Editor.IsVisible)
        {
            if (e.Key == Key.Escape) { CloseEditor(); e.Handled = true; return; }
            if (e.Key == Key.Enter && (command || !typing)) { SaveNote(); e.Handled = true; return; }
            if (!typing && !command && NoteCategory.All.FirstOrDefault(c => KeyChar(e.Key) == c.Key) is { } cat) { PickCategory(cat); e.Handled = true; return; }
        }
        if (typing)
        {
            if (e.Key == Key.Escape) { Frame.Focus(); e.Handled = true; }
            return;
        }
        if (command && KeyChar(e.Key) is >= '1' and <= '9' and var tab) { SelectTab(tab - '1'); e.Handled = true; return; }
        var s = _session;
        var handled = true;
        switch (e.Key)
        {
            case Key.Left when s is not null: Navigate(() => { if (shift) s.StepSeconds(-5); else s.Step(-1); }); break;
            case Key.Right when s is not null: Navigate(() => { if (shift) s.StepSeconds(5); else s.Step(1); }); break;
            case Key.Home when s is not null: Navigate(() => s.Seek(0)); break;
            case Key.End when s is not null: Navigate(() => s.Seek(s.Timeline.Count - 1)); break;
            case Key.Space: TogglePlay(); break;
            case Key.V: SetTool(Tool.Select); break;
            case Key.R: SetTool(Tool.Rect); break;
            case Key.P: SetTool(Tool.Point); break;
            case Key.S:
                SpeedBox.SelectedIndex = (SpeedBox.SelectedIndex + 1) % Speeds.Length;
                break;
            case Key.G when command && s is not null: ShowGoto(); break;
            case Key.O: ShowOpenPanel(); break;
            case Key.L: LabelsToggle.IsChecked = LabelsToggle.IsChecked != true; break;
            case Key.OemPlus or Key.Add: Frame.ZoomBy(1); break;
            case Key.OemMinus or Key.Subtract: Frame.ZoomBy(-1); break;
            case Key.D0 or Key.NumPad0: Frame.Fit(); break;
            case Key.Escape:
                if (Frame.Tool != Tool.Select) SetTool(Tool.Select);
                else s?.Select(null);
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void Navigate(Action move)
    {
        StopPlaying();
        move();
    }

    private static char KeyChar(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
        _ => '\0',
    };

    // ---- notes ----

    private void OpenEditor((int X, int Y, int W, int H) region, bool point)
    {
        if (_session is not { } s) return;
        StopPlaying();
        _pendingRegion = region;
        _pendingPoint = point;
        _pendingIndex = s.Cursor;
        Frame.Pending = region;
        Frame.PendingIsPoint = point;
        Frame.InvalidateVisual();
        var e = s.Entry;
        EditorRegion.Text = $"{(point ? $"point {region.X},{region.Y}" : $"rect {region.X},{region.Y},{region.W},{region.H}")} on seq {e.Sequence:D7}{(e.Filled ? " (fill)" : "")} at {OverlayFrame.FormatTime(e.Time)}";
        var frame = s.Frame;
        var under = frame is null ? [] : NoteBuilder.Under(frame, region).ToList();
        EditorUnder.Text = frame is null ? "the stream has not reached this frame: the note will have no snapshot"
            : under.Count == 0 ? "nothing of the stream here" : "here now:\n" + string.Join("\n", under.Select(u => $"  {u.Kind} {u.Label}"));
        var names = s.Species.ForMap(frame?.MapId ?? s.Stream?.MapChanges.LastOrDefault()?.To)
            .Concat(frame?.Layer(OverlayLayers.Entity).Select(x => x.Str("name")).OfType<string>() ?? [])
            .Distinct().Order(StringComparer.Ordinal).ToList();
        ExpectName.ItemsSource = names;
        _category = null;
        foreach (var b in _categoryButtons) b.IsChecked = false;
        ExpectKind.SelectedIndex = 0;
        ExpectNotKind.SelectedIndex = 0;
        ExpectName.Text = "";
        ExpectState.Text = "";
        NoteText.Text = "";
        var (packet, review, isNew) = s.Packets.NextPacket();
        EditorPacket.Text = $"goes to {Path.Combine(s.Packets.Dir, packet + ".jsonl")}{(isNew ? $" (new, review {review})" : "")}; crops to {Path.Combine(_paths.ReviewData, s.Name, packet)}";
        Editor.IsVisible = true;
        Status("note: pick a category with 1–7, then write what is wrong; Cmd+Enter saves, Esc cancels");
        Frame.Focus();
    }

    private void PickCategory(NoteCategory category)
    {
        _category = category;
        foreach (var b in _categoryButtons) b.IsChecked = ReferenceEquals(b.Tag, category);
        NoteText.Focus();
    }

    private void CloseEditor()
    {
        Editor.IsVisible = false;
        _pendingIndex = -1;
        Frame.Pending = null;
        Frame.InvalidateVisual();
        Frame.Focus();
    }

    private static string? Choice(ComboBox box) => box.SelectedItem is string s && s != "—" ? s : null;

    private static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void SaveNote()
    {
        if (_session is not { } s || _pendingIndex < 0) return;
        if (_category is not { } category)
        {
            Status("pick a category first (1–7)");
            return;
        }
        var kind = Choice(ExpectKind);
        var name = Text(ExpectName.Text);
        if (kind is null && name is not null && s.Species.ForMap(s.Stream?.At(_pendingIndex)?.MapId).Contains(name)) kind = "mob";
        var expect = new NoteExpect(kind, name, Text(ExpectState.Text), Choice(ExpectNotKind));
        var words = (NoteText.Text ?? "").TrimEnd();
        var index = _pendingIndex;
        var (region, point) = (_pendingRegion, _pendingPoint);
        try
        {
            var (note, cropError) = s.SaveNote(index, region, point, category.Id, expect, words, (raw, overlay) => WriteCrops(s, index, region, point, raw, overlay));
            CloseEditor();
            Status($"saved note {note.Id} to {note.Packet.Path}{(cropError is null ? "" : $"; crops not written: {cropError}")}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status($"the note was not saved: {e.Message}");
        }
    }

    /// <summary>Margin around a note's region in its crops, in game pixels.</summary>
    public const int CropMargin = 24;

    /// <summary>Writes a note's crops at 4x: the raw pixels of the region and margin, and the same with the overlay as shown.</summary>
    private string? WriteCrops(ReviewSession s, int index, (int X, int Y, int W, int H) region, bool point, string rawPath, string overlayPath)
    {
        var pixels = s.Pixels.Copy(index);
        if (pixels is null) return "the frame's pixels are not decoded";
        try
        {
            using var bitmap = new WriteableBitmap(new PixelSize(s.Pixels.Width, s.Pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = bitmap.Lock())
                for (var y = 0; y < s.Pixels.Height; y++) Marshal.Copy(pixels, y * s.Pixels.Width * 4, fb.Address + y * fb.RowBytes, s.Pixels.Width * 4);
            int x0 = Math.Clamp(region.X - CropMargin, 0, s.Pixels.Width - 1), y0 = Math.Clamp(region.Y - CropMargin, 0, s.Pixels.Height - 1);
            int x1 = Math.Clamp(region.X + region.W + CropMargin, x0 + 1, s.Pixels.Width), y1 = Math.Clamp(region.Y + region.H + CropMargin, y0 + 1, s.Pixels.Height);
            var options = new PaintOptions(s.Layers, s.Kinds, s.Labels);
            var (raw, overlay) = FrameView.Crops(bitmap, s.Stream?.At(index), (x0, y0, x1 - x0, y1 - y0), options, region, point);
            using (raw)
            using (overlay)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rawPath))!);
                raw.Save(rawPath);
                overlay.Save(overlayPath);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return e.Message;
        }
    }

    private void OnNoteTapped(object? sender, TappedEventArgs e)
    {
        if (InsideButton(e.Source) || (sender as Control)?.DataContext is not NoteRow row || _session is not { } s) return;
        var n = row.Note;
        Go(s.NoteIndex(n), new Focus(n.Region, n.IsPoint, n.Id, false));
    }

    private void OnTruthTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TruthRow row || _session is not { } s) return;
        var t = row.Entry;
        var index = s.Timeline.IndexOf(t.Sequence);
        if (index < 0)
        {
            index = s.Timeline.Nearest(t.Sequence);
            Status($"seq {t.Sequence} is not a frame of the {s.Mode} timeline; showing seq {s.Timeline[index].Sequence}");
        }
        Go(index, new Focus(t.Region, t.IsPoint, t.Id, true));
    }

    private void Go(int index, Focus focus)
    {
        if (_session is not { } s) return;
        StopPlaying();
        s.FocusOn(index, focus);
        Tabs.SelectedItem = ReviewTab;
        Frame.CentreOn(focus.Region);
    }

    private void OnEventFrameTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EventFrame f || _session is not { } s) return;
        Navigate(() => s.Seek(f.Index));
        Tabs.SelectedItem = ReviewTab;
    }

    private void OnEventLineTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EventLine line || _session is not { } s) return;
        if (line.Focus is { } focus) Go(line.Index, focus);
        else
        {
            Navigate(() => s.Seek(line.Index));
            Tabs.SelectedItem = ReviewTab;
        }
    }

    // ---- going to a frame ----

    private void ShowGoto()
    {
        if (_session is not { } s) return;
        StopPlaying();
        GotoBox.Text = "";
        GotoHint.Text = $"1 to {s.Timeline.Count}; now on frame {s.Cursor + 1}. Enter goes, Esc cancels.";
        GotoHint.Foreground = OverlayPainter.Brush(0xFF8A9098);
        GotoPanel.IsVisible = true;
        GotoBox.Focus();
    }

    private void CloseGoto()
    {
        GotoPanel.IsVisible = false;
        Frame.Focus();
    }

    /// <summary>Goes to the frame number typed (1 is the first frame of the timeline), or says why not.</summary>
    private void GoToFrame()
    {
        if (_session is not { } s) return;
        var text = (GotoBox.Text ?? "").Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > s.Timeline.Count)
        {
            GotoHint.Text = $"not a frame: \"{text}\" (1 to {s.Timeline.Count})";
            GotoHint.Foreground = OverlayPainter.Brush(0xFFE07060);
            return;
        }
        s.Seek(n - 1);
        Tabs.SelectedItem = ReviewTab;
        CloseGoto();
    }

    private static bool InsideButton(object? source) => (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    private void OnPromoteNote(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NoteRow row) ShowPromote([row.Note]);
        e.Handled = true;
    }

    private void OnDeleteNote(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not NoteRow row || _session is not { } s) return;
        Confirm($"Delete note {row.Note.Id} from {row.Note.Packet.Path}?", PacketStore.Serialize(row.Note.Json), "Delete", () =>
        {
            s.RemoveNote(row.Note);
            Status($"deleted note {row.Note.Id}");
        });
    }

    private void ShowPromote(IReadOnlyList<ReviewNote> notes)
    {
        if (_session is not { } s) return;
        _promotions = s.Promotions(notes);
        if (_promotions.Count == 0)
        {
            Status("nothing to promote: only notes that say what is really there (a kind, nothing, or a kind it is not), and are not promoted yet, become truth");
            return;
        }
        var text = string.Join("\n\n", _promotions.Select(p => PacketStore.Serialize(p.Entry)));
        var promotions = _promotions;
        Confirm($"Append {promotions.Count} entr{(promotions.Count == 1 ? "y" : "ies")} to {s.Truth.Path}?", text, "Append to truth", () =>
        {
            s.Promote(promotions);
            Status($"appended {promotions.Count} truth entr{(promotions.Count == 1 ? "y" : "ies")} to {s.Truth.Path}");
        });
    }

    private void Confirm(string title, string text, string button, Action action)
    {
        ConfirmTitle.Text = title;
        ConfirmText.Text = text;
        ConfirmButton.Content = button;
        _confirm = action;
        ConfirmPanel.IsVisible = true;
    }

    private void CloseConfirm()
    {
        ConfirmPanel.IsVisible = false;
        _confirm = null;
    }

    private void Status(string text) => StatusText.Text = text;
}
