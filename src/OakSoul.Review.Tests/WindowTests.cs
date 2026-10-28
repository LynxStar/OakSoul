using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using OakSoul.Bake;
using OakSoul.Overlay;
using OakSoul.Review.Model;
using Xunit;

namespace OakSoul.Review.Tests;

/// <summary>
/// The window rendered headless over fx-combat-exact, driven with the keyboard and mouse as a reviewer would, with
/// screenshots in data/review-shots/: review.png (frame 781 with an entity selected and hovered and two notes),
/// review-zoom.png, editor.png, tracks.png, events.png, notes.png, promote.png, and in video mode review-video.png.
/// </summary>
public class WindowTests
{
    private static void Shot(Window window, string name)
    {
        // Let queued refreshes, layout and a render happen before the frame is captured.
        for (var i = 0; i < 3; i++)
        {
            Fixture.Pump(() => true);
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        Directory.CreateDirectory(Fixture.Shots);
        frame.Save(Path.Combine(Fixture.Shots, name));
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private static Point At(MainWindow window, (int X, int Y) game)
    {
        var screen = window.Frame.ScreenRect((game.X, game.Y, 1, 1)).Center;
        return window.Frame.TranslatePoint(screen, window) ?? throw new InvalidOperationException("the frame view is not in the window");
    }

    private static void Drag(MainWindow window, (int X, int Y) from, (int X, int Y) to)
    {
        window.MouseMove(At(window, from), RawInputModifiers.None);
        window.MouseDown(At(window, from), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(At(window, ((from.X + to.X) / 2, (from.Y + to.Y) / 2)), RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(window, to), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, to), MouseButton.Left, RawInputModifiers.None);
    }

    [AvaloniaFact]
    public void ReviewTracksAndNotesOfTheCombatRecording()
    {
        if (!Fixture.Has()) return;
        Directory.SetCurrentDirectory(Fixture.Root!);
        var paths = Fixture.ScratchPaths("keyframes");
        var window = new MainWindow(new ToolOptions { Session = Fixture.Session, Frame = 781 }, paths) { Width = 2000, Height = 1150 };
        window.Show();
        var s = window.Session!;
        Fixture.Pump(() => s.Stream is { Complete: true } && window.Frame.Shown == s.Cursor);
        Assert.Equal(781, s.Entry.Sequence);
        Assert.Equal(StreamState.Ready, s.State);
        Assert.NotEmpty(s.Stream!.Kills);
        Assert.Equal(s.Timeline.Count, s.Stream.ProcessedUpTo);

        // Keyboard stepping: a frame (a keyframe in this mode) forward and back, a second back and forward.
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(796, s.Entry.Sequence);
        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(781, s.Entry.Sequence);
        Press(window, PhysicalKey.ArrowLeft, RawInputModifiers.Meta);
        Assert.True(s.Entry.Sequence < 781);
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.Meta);
        Assert.Equal(781, s.Entry.Sequence);
        Fixture.Pump(() => window.Frame.Shown == s.Cursor);

        // Two notes, drawn and written as a reviewer would: R, drag, a category key, words, Cmd+Enter.
        Press(window, PhysicalKey.R, RawInputModifiers.None);
        Drag(window, (520, 18), (592, 68));
        Assert.True(window.Editor.IsVisible);
        Press(window, PhysicalKey.Digit1, RawInputModifiers.None);
        window.KeyTextInput("the stump on the top platform has no box");
        window.ExpectKind.SelectedItem = "mob";
        window.ExpectName.Text = "Dark Axe Stump";
        Shot(window, "editor.png");
        Press(window, PhysicalKey.Enter, RawInputModifiers.Meta);
        Assert.False(window.Editor.IsVisible);
        Drag(window, (222, 667), (271, 701));
        Press(window, PhysicalKey.Digit4, RawInputModifiers.None);
        window.KeyTextInput("the box covers the top of the stump and cuts off its lower part");
        window.ExpectKind.SelectedItem = "mob";
        Press(window, PhysicalKey.Enter, RawInputModifiers.Meta);
        var notes = s.Packets.Notes.ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal("missed", notes[0].Category);
        Assert.Equal("the stump on the top platform has no box", notes[0].Text);
        Assert.Equal("wrong-box", notes[1].Category);
        Assert.Equal(781, notes[1].Sequence);
        Assert.All(notes, n => Assert.True(File.Exists(Path.Combine(Fixture.Root!, n.RawCrop!)) && File.Exists(Path.Combine(Fixture.Root!, n.OverlayCrop!)), $"crops of {n.Id}"));
        Assert.NotEmpty(notes[1].Json["entities"]!.AsArray());
        Press(window, PhysicalKey.V, RawInputModifiers.None);

        // Select the first matched mob and hover it.
        var mob = s.Frame!.Layer(OverlayLayers.Entity).First(e => e.Kind == "mob" && e.Str("evidence") == "matched");
        var r = mob.Rect!.Value;
        window.MouseDown(At(window, (r.X + r.W / 2, r.Y + r.H / 2)), MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(At(window, (r.X + r.W / 2, r.Y + r.H / 2)), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(mob.Long("track"), s.SelectedTrack);
        window.MouseMove(At(window, (r.X + r.W / 2, r.Y + r.H / 2)), RawInputModifiers.None);
        Assert.True(window.HoverCard.IsVisible);
        Shot(window, "review.png");

        // Zoomed in on the selection: vectors stay sharp, pixels are blocks.
        window.Frame.ZoomBy(4);
        window.Frame.CentreOn(r);
        window.MouseMove(new Point(2, 2), RawInputModifiers.None);
        Shot(window, "review-zoom.png");
        window.Frame.Fit();

        window.Tabs.SelectedIndex = 1;
        Shot(window, "tracks.png");
        Assert.True(window.Tracks.RowCount > 0);

        window.Tabs.SelectedItem = window.EventsTab;
        Shot(window, "events.png");
        Assert.Contains(window.EventsList.ItemsSource!.Cast<EventFrame>(), f => s.Timeline[f.Index].Sequence == 781 && f.Lines.Any(l => l.What == "note"));

        window.Tabs.SelectedItem = window.NotesTab;
        Shot(window, "notes.png");

        // Promote both notes: the confirmation lists the entries, confirming appends them to the (scratch) truth file.
        var before = s.Truth.Entries.Count;
        window.PromoteAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.ConfirmPanel.IsVisible);
        Shot(window, "promote.png");
        window.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before + 2, s.Truth.Entries.Count);
        Assert.All(s.Packets.Notes, n => Assert.NotNull(n.Promoted));
        var promoted = s.Truth.Entries[^1];
        Assert.StartsWith($"r{notes[1].Packet.Review}-781-", promoted.Id);
        Assert.NotNull(promoted.Verdict?.Pass);

        // Go to the note's frame by its number (Cmd+G).
        var noteFrame = s.NoteIndex(notes[0]);
        s.Seek(0);
        Press(window, PhysicalKey.G, RawInputModifiers.Meta);
        Assert.True(window.GotoPanel.IsVisible);
        window.GotoBox.Text = (noteFrame + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Press(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.False(window.GotoPanel.IsVisible);
        Assert.Equal(781, s.Entry.Sequence);
        window.Close();
    }

    [AvaloniaFact]
    public void VideoModeDecodesTheCapturedPixels()
    {
        if (!Fixture.Has(video: true)) return;
        Directory.SetCurrentDirectory(Fixture.Root!);
        var paths = Fixture.ScratchPaths("video");
        var window = new MainWindow(new ToolOptions { Session = Fixture.Session, Frame = 781, Video = true }, paths) { Width = 2000, Height = 1150 };
        window.Show();
        var s = window.Session!;
        Fixture.Pump(() => s.Stream is { Complete: true } && window.Frame.Shown == s.Cursor, 300);
        Assert.Equal(781, s.Entry.Sequence);
        // The lossless video's frame of a keyframe's sequence is the keyframe, pixel for pixel.
        var keyframe = new RecordingPixels(s.Bake.Recording).Load([781L])[781];
        Assert.Equal(keyframe, s.Pixels.Copy(s.Cursor));
        // Stepping on finds the next frames decoded (read ahead).
        Press(window, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Fixture.Pump(() => window.Frame.Shown == s.Cursor);
        s.StepKeyframe(1);
        Fixture.Pump(() => window.Frame.Shown == s.Cursor);
        Assert.True(s.Entry.HasKeyframe);
        Assert.Equal(new RecordingPixels(s.Bake.Recording).Load([s.Entry.Sequence])[s.Entry.Sequence], s.Pixels.Copy(s.Cursor));
        Shot(window, "review-video.png");
        window.Close();
    }

    [AvaloniaFact]
    public void RegeneratesAMissingStreamInTheBackground()
    {
        // fx-exact is short (20 keyframes); its stream is made in a scratch folder, never data/overlays/.
        if (Fixture.Root is not { } root || !File.Exists(Path.Combine(root, "recordings", "fx-exact", "session.json"))) return;
        Directory.SetCurrentDirectory(root);
        var scratch = Fixture.ScratchPaths("regenerate");
        var paths = new RepoPaths { Root = root, Reviews = scratch.Reviews, Truth = scratch.Truth, ReviewData = scratch.ReviewData, Overlays = Path.Combine(Fixture.Shots, "scratch-regenerate", "overlays") };
        var window = new MainWindow(new ToolOptions { Session = "fx-exact" }, paths) { Width = 2000, Height = 1150 };
        window.Show();
        var s = window.Session!;
        Assert.Equal(StreamState.Generating, s.State);
        // Space plays even when a toolbar button was the last thing clicked: it never presses the button (restarting the regeneration).
        var growing = s.Stream;
        window.RegenerateButton.Focus();
        Press(window, PhysicalKey.Space, RawInputModifiers.None);
        Press(window, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Same(growing, s.Stream);
        Fixture.Pump(() => s.Stream is { Count: >= 6 } || s.State != StreamState.Generating, 180);
        if (s.State == StreamState.Generating)
        {
            // Partway: what is processed can be browsed, the rest is greyed.
            s.Seek(s.Stream!.ProcessedUpTo - 1);
            window.Tabs.SelectedIndex = 0;
            Shot(window, "generating.png");
        }
        Fixture.Pump(() => s.State != StreamState.Generating, 180);
        Assert.True(s.State == StreamState.Ready, s.StatusText + "\n" + string.Join("\n", s.GeneratorOutput));
        Assert.True(File.Exists(s.StreamPath));
        Assert.False(File.Exists(s.StreamPath[..^3]));
        Assert.Equal(s.Timeline.Count, s.Stream!.ProcessedUpTo);
        Assert.NotEqual(Freshness.Stale, s.Provenance!.Level);
        // The file it wrote reads back as the same stream.
        Assert.Equal(s.Timeline.Count, OverlayStream.Load(s.StreamPath).Frames.Count);
        window.Close();
    }
}
