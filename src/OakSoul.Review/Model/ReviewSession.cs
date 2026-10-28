using System.Text.Json.Nodes;
using OakSoul.Bake;
using OakSoul.Overlay;

namespace OakSoul.Review.Model;

/// <summary>Where the open stream stands.</summary>
public enum StreamState { Loading, Generating, Ready, Missing, Failed }

/// <summary>A place on the frame to point at: a note's or truth entry's region, until the reviewer moves on.</summary>
public sealed record Focus((int X, int Y, int W, int H) Region, bool Point, string Text, bool Truth);

/// <summary>
/// One open recording in one mode: its timeline, the stream (read, or regenerated in the background), the frame
/// pixels, the review packets and the ground truth, and the cursor and selection the views share. Lives on the UI
/// thread; background work reports back through <c>post</c>.
/// </summary>
public sealed class ReviewSession : IDisposable
{
    private readonly Action<Action> _post;
    private readonly ToolOptions _options;
    private CancellationTokenSource? _loadCancel;
    private StreamGenerator? _generator;
    private string? _character, _pet;
    private int _feed;

    /// <summary>
    /// The sink of one load or regeneration: once another has started, what the old one still had queued is dropped,
    /// so a cancelled feed never adds frames to the new stream.
    /// </summary>
    private sealed class Feed(ReviewSession session) : IStreamSink
    {
        private readonly int _id = ++session._feed;
        private bool Current => _id == session._feed;
        public void Header(OverlayHeader header, IReadOnlyList<KeyValuePair<string, string>> fields, string? problem) { if (Current) session.OnHeader(header, fields, problem); }
        public void Frames(IReadOnlyList<ParsedFrame> frames, int badLines) { if (Current) session.OnFrames(frames, badLines); }
        public void Done(bool ok, string message) { if (Current) session.OnDone(ok, message); }
    }

    public ReviewSession(RepoPaths paths, ToolOptions options, string session, bool video, Action<Action> post)
    {
        Paths = paths;
        _options = options;
        _post = post;
        Bake = Session.Resolve(session);
        Name = Bake.Name;
        Video = video;
        Timeline = Timeline.Build(Bake.Recording, video);
        if (Timeline.Count == 0) throw new BakeException($"{Bake.Recording} has no {(video ? "video frames in frames.jsonl" : "keyframes")}");
        Pixels = video ? new VideoPixels(Bake.Recording, Timeline, post) : new KeyframePixels(Bake.Recording, Timeline, post);
        Packets = new PacketStore(paths, Name);
        Truth = new TruthBook(paths, Name);
        Species = new Species(paths.Root);
        CheckoutCommit = OverlayProvenance.GitCommit(paths.Root);
        StreamPath = paths.Overlays is { } overlays ? Path.Combine(overlays, Name, Path.GetFileName(Bake.DefaultStream(video))) : Bake.DefaultStream(video);
        _character = options.Character;
        _pet = options.Pet;
    }

    public RepoPaths Paths { get; }
    public Session Bake { get; }
    public string Name { get; }
    public bool Video { get; }
    public string Mode => Video ? "video" : "keyframes";
    public Timeline Timeline { get; }
    public FramePixels Pixels { get; }
    public PacketStore Packets { get; }
    public TruthBook Truth { get; }
    public Species Species { get; }
    public string Reviewer => _options.Reviewer;
    public string? CheckoutCommit { get; }

    private readonly Dictionary<(string, string), bool?> _sameStreamCode = [];

    /// <summary>Whether two commits make the same streams (asked of git once per pair).</summary>
    private bool? SameStreamCode(string a, string b)
    {
        if (!_sameStreamCode.TryGetValue((a, b), out var same)) _sameStreamCode[(a, b)] = same = OverlayProvenance.SameStreamCode(Paths.Root, a, b);
        return same;
    }
    public string StreamPath { get; }

    public ReviewStream? Stream { get; private set; }
    public Provenance? Provenance { get; private set; }
    public StreamState State { get; private set; } = StreamState.Loading;
    public string StatusText { get; private set; } = "";
    public IReadOnlyList<string> GeneratorOutput => _generator?.Output ?? [];

    public int Cursor { get; private set; }
    public long? SelectedTrack { get; private set; }
    public Focus? Focus { get; private set; }

    /// <summary>The layers and entity kinds drawn, labels, and the notes' and truth's places on the frame.</summary>
    public HashSet<string> Layers { get; } = [.. OverlayLayers.Names];
    public HashSet<string> Kinds { get; } = [.. EntityKinds];
    public bool Labels { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowTruth { get; set; } = true;

    /// <summary>The entity kinds of the stream schema, in the registry's order.</summary>
    public static readonly IReadOnlyList<string> EntityKinds = [.. OverlayLayers.Find(OverlayLayers.Entity)!.Kinds.Select(k => k.Kind)];

    public event Action? CursorChanged;
    public event Action? StreamChanged;
    public event Action? SelectionChanged;
    public event Action? NotesChanged;
    public event Action? ViewChanged;

    public TimelineEntry Entry => Timeline[Cursor];
    public OverlayFrame? Frame => Stream?.At(Cursor);

    /// <summary>Opens the stream: reads it when it exists (a stale one is shown as stale), else regenerates it.</summary>
    public void Start()
    {
        if (File.Exists(StreamPath)) LoadStream();
        else Regenerate();
    }

    private void LoadStream()
    {
        _loadCancel?.Cancel();
        _loadCancel = new CancellationTokenSource();
        Stream = new ReviewStream(StreamPath, Timeline);
        Provenance = null;
        State = StreamState.Loading;
        StatusText = $"reading {StreamPath}";
        StreamChanged?.Invoke();
        _ = StreamFeed.Load(StreamPath, _post, new Feed(this), _loadCancel.Token);
    }

    /// <summary>Runs the watcher over the recording again (Bake's regenerate) and shows the new stream as it grows.</summary>
    public void Regenerate()
    {
        _loadCancel?.Cancel();
        _generator?.Dispose();
        // The stream's own character and pet are kept unless the tool was given others.
        var character = _character ?? Stream?.Header?.Name;
        var pet = _pet ?? (Stream?.Header is { } h ? h.Pet ?? "none" : null);
        Stream = new ReviewStream(StreamPath, Timeline);
        Provenance = null;
        State = StreamState.Generating;
        _generator = new StreamGenerator(Name, Video, StreamPath, character, pet, _post, new Feed(this));
        try
        {
            _generator.Start();
            StatusText = $"regenerating: {_generator.Command}";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            State = StreamState.Failed;
            StatusText = $"could not start the regeneration: {e.Message}";
        }
        StreamChanged?.Invoke();
    }

    public bool Generating => _generator?.Running == true && State == StreamState.Generating;

    private void OnHeader(OverlayHeader header, IReadOnlyList<KeyValuePair<string, string>> fields, string? problem)
    {
        if (Stream is null) return;
        Stream.SetHeader(header, fields, problem);
        Provenance = Model.Provenance.Check(header, problem, CheckoutCommit, Video, Name, _character, _pet, SameStreamCode);
        StreamChanged?.Invoke();
    }

    private void OnFrames(IReadOnlyList<ParsedFrame> frames, int badLines)
    {
        if (Stream is null) return;
        var cursorHad = Stream.At(Cursor) is not null;
        foreach (var f in frames) Stream.Add(f);
        for (var i = 0; i < badLines; i++) Stream.CountBadLine();
        Pixels.Count = Timeline.Count;
        StreamChanged?.Invoke();
        if (!cursorHad && Stream.At(Cursor) is not null) CursorChanged?.Invoke();
    }

    private void OnDone(bool ok, string message)
    {
        if (Stream is null) return;
        Stream.Complete = ok;
        State = ok ? StreamState.Ready : StreamState.Failed;
        StatusText = message;
        if (ok && _generator is not null && Stream.Header is { } h)
            Provenance = Model.Provenance.Check(h, Stream.HeaderProblem, CheckoutCommit, Video, Name, _character, _pet, SameStreamCode);
        Truth.Evaluate(Stream, Timeline);
        StreamChanged?.Invoke();
        NotesChanged?.Invoke();
    }

    // ---- cursor and navigation ----

    public void Seek(int index, bool playing = false)
    {
        index = Math.Clamp(index, 0, Timeline.Count - 1);
        Pixels.Want(index, playing, index >= Cursor ? 1 : -1);
        if (index == Cursor) return;
        Cursor = index;
        // A note's or truth entry's region stays pointed at only on its own frame.
        if (_focusIndex != Cursor) Focus = null;
        CursorChanged?.Invoke();
    }

    private int _focusIndex = -1;

    public void Step(int frames) => Seek(Cursor + frames);

    /// <summary>To the next or previous keyframe (every 15th captured frame): one entry in keyframe mode.</summary>
    public void StepKeyframe(int direction)
    {
        if (!Video) { Step(direction); return; }
        for (var i = Cursor + direction; i >= 0 && i < Timeline.Count; i += direction)
            if (Timeline[i].HasKeyframe) { Seek(i); return; }
        Step(direction * Timeline.KeyframeStep);
    }

    public void StepSeconds(double seconds)
    {
        var target = Timeline.NearestTime(Entry.Time + seconds);
        if (target == Cursor) target += Math.Sign(seconds);
        Seek(target);
    }

    /// <summary>Jumps to a capture sequence ("781") or a time ("0:26", "26.5s"); false when the text is neither.</summary>
    public bool Jump(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        if (text.Contains(':') || text.EndsWith('s'))
        {
            try { Seek(Timeline.AtTime(Options.ParseTime(text.TrimEnd('s')))); return true; }
            catch (Exception e) when (e is FormatException or BakeException) { return false; }
        }
        if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seq)) return false;
        Seek(Timeline.Nearest(seq));
        return true;
    }

    public IEnumerable<int> KillMarks => Stream?.Kills ?? [];
    public IEnumerable<int> MapMarks => Stream?.MapChanges.Select(m => m.Index) ?? [];
    public IEnumerable<int> NoteMarks => Packets.Notes.Select(NoteIndex);
    public IEnumerable<int> FailMarks => Truth.Entries.Where(e => e.Verdict?.Pass == false).Select(e => Timeline.Nearest(e.Sequence));

    public int NoteIndex(ReviewNote n)
    {
        if (!n.Filled && Timeline.IndexOf(n.Sequence) is var i and >= 0) return i;
        // A fill (or, in keyframe mode, a frame between keyframes): the nearest frame of that time.
        var near = Timeline.Nearest(n.Sequence);
        return Math.Abs(Timeline[near].Time - n.Time) < 0.5 ? Timeline.AtTime(n.Time) : near;
    }

    /// <summary>To the next (or previous) mark after the cursor; false when there is none.</summary>
    public bool NextMark(IEnumerable<int> marks, int direction)
    {
        var sorted = marks.Distinct().Order().ToList();
        int? target = direction > 0 ? sorted.Where(m => m > Cursor).Cast<int?>().FirstOrDefault() : sorted.Where(m => m < Cursor).Cast<int?>().LastOrDefault();
        if (target is not { } t) return false;
        Seek(t);
        return true;
    }

    // ---- selection and focus ----

    public void Select(long? track)
    {
        if (SelectedTrack == track) return;
        SelectedTrack = track;
        SelectionChanged?.Invoke();
    }

    public void FocusOn(int index, Focus focus)
    {
        Focus = focus;
        _focusIndex = Math.Clamp(index, 0, Timeline.Count - 1);
        Seek(index);
        CursorChanged?.Invoke();
    }

    public void ViewUpdated() => ViewChanged?.Invoke();

    /// <summary>Stops a regeneration in progress; the frames read so far stay, the previous stream file is left as it was.</summary>
    public void StopGenerating()
    {
        if (_generator is null || !_generator.Running) return;
        _generator.Cancel();
        _feed++;
        State = StreamState.Failed;
        StatusText = "regeneration stopped; the stream file on disk is unchanged";
        StreamChanged?.Invoke();
    }

    // ---- notes and truth ----

    /// <summary>
    /// Saves a note on the frame at <paramref name="index"/> into the active packet; <paramref name="crops"/> writes its raw
    /// and overlay crops to the paths given (and returns an error, or null).
    /// </summary>
    public (ReviewNote Note, string? CropError) SaveNote(int index, (int X, int Y, int W, int H) region, bool point, string category, NoteExpect expect, string text,
        Func<string, string, string?> crops)
    {
        var json = NoteBuilder.Build(Reviewer, Name, Video, Timeline[index], Stream?.At(index), region, point, category, expect, text, Stream, Provenance,
            OverlayLayers.Names.Where(Layers.Contains), EntityKinds.Where(Kinds.Contains), Paths);
        string? raw = null, overlay = null;
        var note = Packets.Append(json, (packet, n, o) =>
        {
            var dir = Packets.CropDir(packet);
            raw = Path.Combine(dir, $"{n:D3}-raw.png");
            overlay = Path.Combine(dir, $"{n:D3}-overlay.png");
            o["crops"] = new JsonObject { ["raw"] = Paths.Relative(raw), ["overlay"] = Paths.Relative(overlay) };
        });
        var error = crops(raw!, overlay!);
        NotesChanged?.Invoke();
        return (note, error);
    }

    public void RemoveNote(ReviewNote note)
    {
        Packets.Remove(note);
        NotesChanged?.Invoke();
    }

    /// <summary>The truth entries the notes would become (notes already promoted, or with nothing checkable, are left out).</summary>
    public List<(ReviewNote Note, JsonObject Entry)> Promotions(IEnumerable<ReviewNote> notes)
    {
        var taken = Truth.Ids();
        var list = new List<(ReviewNote, JsonObject)>();
        foreach (var n in notes)
            if (n.Promoted is null && Truth.Promotion(n, Reviewer, taken) is { } e) list.Add((n, e));
        return list;
    }

    /// <summary>Appends the entries to the truth file and records each note's entry id in its packet.</summary>
    public void Promote(IReadOnlyList<(ReviewNote Note, JsonObject Entry)> promotions)
    {
        if (promotions.Count == 0) return;
        Truth.Append(promotions.Select(p => p.Entry));
        foreach (var (note, entry) in promotions) note.Json["promoted"] = entry["id"]!.GetValue<string>();
        foreach (var packet in promotions.Select(p => p.Note.Packet).Distinct()) Packets.Rewrite(packet);
        Truth.Evaluate(Stream, Timeline);
        NotesChanged?.Invoke();
    }

    public void ReloadNotesAndTruth()
    {
        Packets.Load();
        Truth.Load();
        Truth.Evaluate(Stream, Timeline);
        NotesChanged?.Invoke();
    }

    /// <summary>Re-checks truth against the stream as it grows (cheap; called by the views after stream updates).</summary>
    public void Recheck() => Truth.Evaluate(Stream, Timeline);

    public void Dispose()
    {
        _loadCancel?.Cancel();
        _generator?.Dispose();
        Pixels.Dispose();
    }
}
