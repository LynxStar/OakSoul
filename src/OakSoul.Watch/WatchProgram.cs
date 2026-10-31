using System.Diagnostics;
using OakSoul.Capture;
using OakSoul.Capture.Replay;
using OakSoul.Detect;
using OakSoul.Overlay;
using OakSoul.Vision;
using OakSoul.Vision.Readers;
using OakSoul.World;

namespace OakSoul.Watch;

/// <summary>
/// The watcher: runs the HUD readers, the world model and the entity detector on a live window or a recording and prints
/// values as they change. <see cref="RunAsync"/> takes the command line; OakSoul.Watch's entry point calls it, and so does
/// OakSoul.Bake to regenerate an overlay stream from a recording with the current pipeline.
/// </summary>
public static class WatchProgram
{
    // OakSoul.Watch: run the HUD readers on a live window or a recording and print values as they change.
    //   --list | --window-id N | --pid N --title "T" | --app "Name" --title "T"      live capture
    //   --recording DIR [--keyframes] [--speed X]                                      replay a recorder session
    //   --name NAME (default LynxHope): the character's name, given by the operator; the status bar is only checked against it
    //   --pet NAME (default Mara; none for a character without a pet): the character's one pet, given by the operator; its tag
    //     locates it, and the tag beside the character is only checked against it
    //   --crop-top N (28 for a windowed client)   --fps N (default 30)   --seconds S (default 0 = until the end or Ctrl-C)
    //   --scale N: live capture at N times the point size, decimated to the game's own pixels (default: the window's backing scale)
    //   --maps DIR (default data/maps): map metadata for world positions   --world-report: print the world-model validation at the end
    //   --build-background: (recordings of pixel-exact sessions only) build each visited map's background mosaic into --backgrounds DIR
    //     (default data/backgrounds)
    //   --entities: detect mobs, players, pets, drops and effects against the cached mosaic and print a line per change; only on
    //     frames that are the game's own pixels (exact capture, keyframes, a lossless video), the rest are skipped and counted
    //   --entity-sheet DIR: write annotated contact sheets of a dozen evenly spaced frames (keep DIR gitignored, e.g. data/ or recordings/)
    //   --entity-report: print the entity validation at the end (implies --entities)
    //   --mobinfo DIR (default data/mobinfo): mob sprites for verification   --sprites DIR (default data/sprites): item icons for drops
    //   --overlay-out PATH: write the overlay stream (gzip JSON Lines, one line per processed frame; implies --entities)
    //   --list-layers: print the overlay stream's layers with their item kinds and fields, and exit
    public static async Task<int> RunAsync(string[] args)
    {
        uint? windowId = null; int? pid = null; string? app = null, title = null, recording = null, buffsPath = null, mapsPath = null, backgroundsPath = null, sheetDir = null, mobInfoPath = null, spritesPath = null, overlayPath = null;
        int cropTop = 0, fps = 30; double seconds = 0, speed = 1; var list = false; var keyframes = false; var worldReport = false;
        var buildBackground = false; var entities = false; var entityReport = false;
        int? captureScale = null;
        var ownName = "LynxHope";
        string? petName = "Mara";
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--list": list = true; break;
                case "--window-id": windowId = uint.Parse(Next()); break;
                case "--pid": pid = int.Parse(Next()); break;
                case "--app": app = Next(); break;
                case "--title": title = Next(); break;
                case "--crop-top": cropTop = int.Parse(Next()); break;
                case "--fps": fps = int.Parse(Next()); break;
                case "--seconds": seconds = double.Parse(Next()); break;
                case "--recording": recording = Next(); break;
                case "--keyframes": keyframes = true; break;
                case "--speed": speed = double.Parse(Next()); break;
                case "--buffs": buffsPath = Next(); break;
                case "--maps": mapsPath = Next(); break;
                case "--world-report": worldReport = true; break;
                case "--build-background": buildBackground = true; break;
                case "--backgrounds": backgroundsPath = Next(); break;
                case "--entities": entities = true; break;
                case "--entity-sheet": sheetDir = Next(); entities = true; break;
                case "--entity-report": entityReport = true; entities = true; break;
                case "--mobinfo": mobInfoPath = Next(); break;
                case "--sprites": spritesPath = Next(); break;
                case "--scale": captureScale = int.Parse(Next()); break;
                case "--name": ownName = Next(); break;
                case "--pet": petName = Next(); break;
                case "--overlay-out": overlayPath = Next(); entities = true; break;
                case "--list-layers": OverlayLayers.Print(Console.Out); return 0;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }

        if (captureScale is < 1 or > 4) { Console.Error.WriteLine("error: --scale must be between 1 and 4"); return 2; }
        if (ownName.Trim().Length < 3) { Console.Error.WriteLine("error: --name must be at least 3 characters"); return 2; }
        ownName = ownName.Trim();
        petName = petName?.Trim() is { Length: > 0 } pn && !string.Equals(pn, "none", StringComparison.OrdinalIgnoreCase) ? pn : null;

        ScreenCapture.Initialize();
        IFrameSource source;
        if (recording is not null)
        {
            source = new RecordingFrameSource(recording, keyframes, cropTop, speed);
        }
        else
        {
            var windows = ScreenCapture.ListWindows();
            if (list)
            {
                foreach (var w in windows.OrderBy(w => w.App).ThenBy(w => w.Title))
                    Console.WriteLine($"{w.Id,7}  pid={w.Pid,-6} {w.Width,5:0}x{w.Height,-5:0} {(w.OnScreen ? "on " : "off")}  {w.App}: {w.Title}");
                return 0;
            }

            var matches = windowId is { } id ? windows.Where(w => w.Id == id).ToList()
                : pid is { } p && title is not null ? windows.Where(w => w.Pid == p && w.Title == title).ToList()
                : app is not null && title is not null ? windows.Where(w => w.App == app && w.Title == title).ToList()
                : [];
            if (matches.Count != 1)
            {
                Console.Error.WriteLine(matches.Count == 0 ? "error: choose exactly one window with --window-id, or --pid/--app plus --title (see --list)." : "error: several windows match; use --window-id.");
                foreach (var m in matches) Console.Error.WriteLine($"  {m}");
                return 1;
            }
            var window = matches[0];
            if (window.Width <= 0 || window.Height <= 0) { Console.Error.WriteLine("error: window reports a zero-size frame."); return 1; }
            CaptureRect? crop = cropTop > 0 ? new CaptureRect(0, cropTop, (int)window.Width, (int)window.Height - cropTop) : null;
            // Exact capture, as the recorder does: request the region at the display's backing scale, where every game pixel is a
            // uniform block, and decimate each frame back to point size, so the readers see the game's own pixels in sRGB.
            var scale = captureScale ?? Math.Max(1, (int)Math.Round(window.Scale));
            source = new CaptureFrameSource(CaptureStream.Start(window.Id, 0, 0, fps, 8, crop, scale), $"{window}{(crop is null ? "" : $"  crop {crop}")}  scale {scale}, {CaptureStream.ColorSpace}");
        }
        Console.WriteLine($"source   {source.Description}");
        Console.WriteLine($"name     {ownName}");
        Console.WriteLine($"pet      {petName ?? "none"}");

        // Text recognition runs on a worker thread of its own, so no frame waits for Vision. OAKSOUL_OCR_THREAD=tags keeps only the
        // name tags' reads there (the map name, the status bar and the own tag's learning read on the frame thread, as before);
        // off reads everything on the frame thread, as before there was a worker, which is the mode a replay reproduces exactly.
        var ocrMode = Environment.GetEnvironmentVariable("OAKSOUL_OCR_THREAD") switch { "off" or "0" => "off", "tags" => "tags", _ => "on" };
        using var ocr = ocrMode == "off" ? null : new OcrQueue();
        var readersOcr = ocrMode == "on" ? ocr : null;
        // The tag cache file: the tag renderings read so far with their text, kept between live sessions. A replay starts without
        // one unless OAKSOUL_TAG_CACHE names a file, so a replay's output is its own; off keeps none.
        var tagCacheSetting = Environment.GetEnvironmentVariable("OAKSOUL_TAG_CACHE");
        var tagCachePath = tagCacheSetting is "off" or "0" ? null : tagCacheSetting is { Length: > 0 } ? tagCacheSetting
            : recording is null ? Path.Combine(Directory.GetCurrentDirectory(), "data", "cache", "tag-reads.json") : null;
        // The tag font learned from confirmed reads, kept the same way (OAKSOUL_TAG_FONT=path; a replay learns within the run unless a
        // file is named, so a font learned on one recording can be tried on another; off neither learns nor renders, so every tag is read
        // as before there was a font, apart from names a tag cache file written with the font on may still hold).
        var tagFontSetting = Environment.GetEnvironmentVariable("OAKSOUL_TAG_FONT");
        var useFont = tagFontSetting is not ("off" or "0");
        var tagFontPath = !useFont ? null : tagFontSetting is { Length: > 0 } ? tagFontSetting
            : recording is null ? Path.Combine(Directory.GetCurrentDirectory(), "data", "cache", "tag-font.json") : null;
        Console.WriteLine($"ocr      {(ocr is null ? "on the frame thread" : ocrMode == "tags" ? "name tags on a worker thread, the rest on the frame thread" : "on a worker thread")}; tag cache {tagCachePath ?? "none"}; tag font {(!useFont ? "off" : tagFontPath ?? "learned within the run")}");

        var layout = HudLayout.Native1366x768;
        // Keyframe replay is one frame per second, so a stability gate of one frame; live and video keep three.
        var stableFrames = source is RecordingFrameSource { Fps: <= 1 } ? 2 : 3;
        var tracker = new MapNameTracker(stableFrames, readersOcr);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        using var frameSource = source;
        var identity = new IdentityReader(layout, stableFrames, readersOcr) { ExpectedName = ownName };
        var quickslot = new QuickslotReader(layout);
        var buffReader = new BuffReader(layout);
        buffsPath ??= Path.Combine(Directory.GetCurrentDirectory(), "recordings", "known-buffs.json");
        if (File.Exists(buffsPath)) Console.WriteLine($"buffs    {buffReader.Load(buffsPath)} known icons from {buffsPath}");
        var tagLocator = new NameTagLocator(ownName) { Queue = readersOcr };
        var nameWarned = false;
        CaptureRect? lastUnlearned = null;
        mapsPath ??= Path.Combine(Directory.GetCurrentDirectory(), "data", "maps");
        MapCatalog? catalog = File.Exists(Path.Combine(mapsPath, "index.json")) ? new MapCatalog(mapsPath) : null;
        Console.WriteLine(catalog is null ? $"maps     none at {mapsPath} (run OakSoul.MapExport --out data/maps); world positions off" : $"maps     {catalog.Count} maps from {mapsPath}");
        var world = catalog is null ? null : new WorldTracker(catalog);
        // On exact frames a map name the catalog does not know is read again from the unsoftened name block.
        if (catalog is not null) tracker.IsKnownName = (street, map) => catalog.FindCandidates(street, map).Exact;
        // The map names' learned fonts and name-block cache, tried on the block before any read and taught by reads the catalog confirms
        // (OAKSOUL_MAP_FONT=path|off): a live session keeps data/cache/map-font.json, a replay starts clean unless a file is named.
        var mapFontSetting = Environment.GetEnvironmentVariable("OAKSOUL_MAP_FONT");
        var useMapFont = mapFontSetting is not ("off" or "0");
        var mapFontPath = !useMapFont ? null : mapFontSetting is { Length: > 0 } ? mapFontSetting
            : recording is null ? Path.Combine(Directory.GetCurrentDirectory(), "data", "cache", "map-font.json") : null;
        MapNameFonts? mapFonts = null;
        if (!useMapFont) Console.WriteLine("map font off: every map change is read");
        else if (catalog is not null)
        {
            mapFonts = new MapNameFonts();
            if (mapFontPath is null) Console.WriteLine("map font learned within the run");
            else
            {
                try
                {
                    if (MapNameFonts.Load(mapFontPath) is { } loaded) { mapFonts = loaded; Console.WriteLine($"map font {loaded.Describe()}, from {mapFontPath}"); }
                    else Console.WriteLine($"map font no file at {mapFontPath}; the fonts and the cache are learned from confirmed reads and written there at the end");
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException or OverflowException or IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"map font the file {mapFontPath} could not be read ({e.Message}); learning anew");
                }
            }
            mapFonts.SetNames(catalog.AllNames.Select(n => (n.Street, n.Map)));
            tracker.Fonts = mapFonts;
        }
        var report = worldReport && world is not null ? new WorldReport() : null;
        backgroundsPath ??= Path.Combine(Directory.GetCurrentDirectory(), "data", "backgrounds");
        if (buildBackground && (recording is null || world is null)) { Console.Error.WriteLine("error: --build-background needs --recording and map metadata"); return 2; }
        var screenMask = new ScreenMask(layout);
        var buildFrames = new Dictionary<long, BackgroundBuild.FrameInfo>();
        var sessionTimes = new SessionTimes(recording is not null && keyframes ? recording : null);
        mobInfoPath ??= Path.Combine(Directory.GetCurrentDirectory(), "data", "mobinfo");
        spritesPath ??= Path.Combine(Directory.GetCurrentDirectory(), "data", "sprites");
        var entityWatch = entities && world is not null ? new EntityWatch(layout, backgroundsPath, sessionTimes, sheetDir,
            recording is not null && keyframes && Directory.Exists(Path.Combine(recording, "keyframes")) ? Directory.GetFiles(Path.Combine(recording, "keyframes"), "*.png").Length : null,
            mobInfoPath, spritesPath, diagnostics: entityReport, petName: petName, character: ownName,
            session: recording is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(recording)),
            ocr: ocr, tagCache: tagCachePath, tagFont: tagFontPath, useFont: useFont) : null;
        if (entities && world is null) Console.WriteLine("entities off: no map metadata");

        // A replayed frame is stamped with its time into the session (its capture pts less the session's first), which is
        // the time in a baked frame's file name; live frames with the wall clock.
        var replay = source as RecordingFrameSource;
        double? originPts = replay?.OriginPts;
        double SessionTime(Frame frame) => frame.Pts - (originPts ??= frame.Pts);
        string Stamp(Frame frame) => source.NeedsMainLoop ? DateTime.Now.ToString("HH:mm:ss.fff") : $"t={TimeSpan.FromSeconds(Math.Max(0, SessionTime(frame))):m\\:ss}  ";
        FrameGeometry? geometry = null;
        // Whether the source delivers the game's own pixels (exact capture) rather than the point-size capture's blurred copy:
        // set once from the first frame whose status bar text shows one-pixel strokes (a blurred frame never does). The entity
        // detector compares sprites pixel for pixel and runs only on such frames, and only when nothing lossy came between the
        // capture and the frame: a lossy video moves a pixel by several levels (x264 at CRF 18, docs/capture-notes.md), even when
        // its status bar text still looks sharp; keyframes, live frames and a lossless video (session.json) do not.
        var exactFrames = false;
        var lossyVideo = !source.Lossless;
        await using var overlay = overlayPath is null ? null : new OverlayWriter(overlayPath, new OverlayHeader(OverlayHeader.CurrentSchema, "OakSoul.Watch",
            OverlayProvenance.GitCommit(Directory.GetCurrentDirectory()), recording is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(recording)),
            recording is null ? null : OverlayProvenance.RelativePath(recording), recording is null ? "live" : keyframes ? "keyframes" : "video",
            recording is null ? source.Description : $"{OverlayProvenance.RelativePath(recording)} ({(keyframes ? "keyframes" : replay!.LosslessVideo ? "lossless video" : "lossy video")})",
            layout.FrameWidth, layout.FrameHeight, ownName, SpriteMatcher.Threshold, DropMatcher.Threshold, DropMatcher.Threshold, OverlayLayers.Names, petName)
            {
                PetSpecies = entityWatch?.PetSpecies is { } ps ? ps.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                PetSpeciesName = entityWatch?.PetSpecies?.Name,
            });
        var overlayMs = new List<double>();
        State? last = null;
        long frames = 0;
        double readMs = 0;
        // The loop's work on each frame after it arrived (readers, world, entities, overlay build), for the percentiles at the end.
        var loopMs = new List<double>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var frame in frameSource.Frames(cancellation.Token))
            {
                using (frame)
                {
                    var sw = Stopwatch.StartNew();
                    if (geometry is null || geometry.FrameWidth != frame.Width || geometry.FrameHeight != frame.Height)
                    {
                        geometry = FrameGeometry.Detect(layout, frame.Pixels, frame.Width, frame.Height);
                        Console.WriteLine($"geometry {geometry}");
                        if (geometry.BarsConfirmed == false)
                            Console.WriteLine("warning: the frame does not look like the native layout; for a windowed client pass --crop-top 28");
                    }
                    if (!exactFrames && OcrInput.IsPixelExact(geometry, frame.Pixels)) exactFrames = true;
                    var status = StatusReader.Read(geometry, frame.Pixels);
                    var minimap = MinimapReader.Read(geometry, frame.Pixels);
                    if (minimap.Valid) tracker.Update(geometry, frame.Pixels, minimap);
                    identity.Update(geometry, frame.Pixels);
                    // The operator gives the name; the status bar is only checked against it, and a mismatch is warned about once.
                    if (!nameWarned && identity.Current?.Name is { Length: > 0 } statusName && !string.Equals(statusName, ownName, StringComparison.OrdinalIgnoreCase))
                    {
                        nameWarned = true;
                        Console.WriteLine($"warning: status bar reads '{statusName}', expected '{ownName}'");
                    }
                    // The own name tag is learned once, from a frame where OCR sees the name by itself, then tracked by pixels.
                    if (tagLocator is { Ready: false }) { if (tagLocator.TryLearn(geometry, frame.Pixels)) Console.WriteLine($"tag      learned '{tagLocator.Name}' template {tagLocator.TemplateWidth}x{tagLocator.TemplateHeight} at {tagLocator.LearnedAt!.Value.X},{tagLocator.LearnedAt.Value.Y}"); }
                    var tag = tagLocator is { Ready: true } ? tagLocator.Locate(geometry, frame.Pixels) : null;
                    if (tagLocator is { Ready: false, Unlearned: { } unlearned } && unlearned != lastUnlearned)
                    {
                        lastUnlearned = unlearned;
                        Console.WriteLine($"tag      forgot the template from {unlearned.X},{unlearned.Y}: it never matched again");
                    }
                    var cells = quickslot.Read(geometry, frame.Pixels);
                    foreach (var idx in quickslot.CountChanges(cells)) Console.WriteLine($"{Stamp(frame)}  quickslot {cells[idx].Key} count changed");
                    var buffs = buffReader.Read(geometry, frame.Pixels);
                    WorldObservation? observation = null;
                    WorldState? place = null;
                    if (world is not null)
                    {
                        observation = new WorldObservation(tracker.Current?.Region, tracker.Current?.Map,
                            minimap.Valid && !minimap.Collapsed ? new PixelRect(minimap.MapArea.X, minimap.MapArea.Y, minimap.MapArea.Width, minimap.MapArea.Height) : null,
                            minimap.SelfInMap is { } self ? new PointD(self.X, self.Y) : null,
                            tag is { } hit ? new PointD(hit.Feet.X, hit.Feet.Y) : null,
                            minimap.Dots.Where(d => d.Kind == MinimapDotKind.Portal).Select(d => new MinimapMarker(new PointD(d.X - minimap.MapArea.X, d.Y - minimap.MapArea.Y), d.Pixels)).ToList(),
                            minimap.Collapsed, frame.Clock, NameWidensPanel: tracker.NameWidensPanel);
                        place = world.Update(observation);
                        report?.Add(frame.Sequence, observation, place);
                    }
                    // Background building only notes which frames have a measured camera; the mosaic passes replay them afterwards.
                    if (buildBackground && geometry.IsNative && place is { Source: WorldSource.DotAndTag, Camera: { } voteCamera, MapId: { } mapId, Map: { } mapData } && tag is { } ownTag)
                        buildFrames[frame.Index] = new BackgroundBuild.FrameInfo(mapData, voteCamera, world!.CameraModel!.IsClamped(voteCamera).X && place.Confidence >= 0.9,
                            minimap.Valid ? minimap.Panel : ScreenMask.DefaultMinimapPanel, ownTag.Tag);
                    var trackerPlace = place;
                    if (entityWatch is not null)
                    {
                        var notExact = !geometry.IsNative ? "scaled frames (a fullscreen or resized client is resampled)"
                            : lossyVideo ? "lossy video (replay its keyframes, or record with --encoder lossless)"
                            : !exactFrames ? "not pixel-exact (no one-pixel strokes in the status bar: a point-size capture, or no status bar yet)"
                            : null;
                        place = entityWatch.Update(frame, geometry, place, world, tag, minimap.Valid ? minimap.Panel : null, status, ownName, Stamp(frame), notExact);
                    }
                    // The overlay stream: a projection of what this frame concluded, built here and serialized on the writer's task.
                    if (overlay is not null)
                    {
                        var o0 = Stopwatch.GetTimestamp();
                        overlay.Post(OverlayBuilder.Build(new OverlayInputs(frame.Sequence, frame.Pts, frame.Filled, SessionTime(frame), layout,
                            status, identity.Current, cells, buffs, minimap, tracker.Current is { } names ? (names.Region, names.Map) : null, tracker.NameBlock,
                            tracker.LastReadUnsoftened, tag, trackerPlace, place, entityWatch?.LastResult, entityWatch?.LastResult is null ? null : entityWatch.LastTags,
                            entityWatch?.LastOwnBox, entityWatch is null ? world is null ? "no map metadata" : "entities off" : entityWatch.LastSkip)));
                        overlayMs.Add(Stopwatch.GetElapsedTime(o0).TotalMilliseconds);
                    }
                    var state = new State(identity.Current?.Level, identity.Current?.Job, identity.Current?.Name, status.HpValue, status.MpValue, status.ExpValue,
                        tracker.Current is { } n ? $"{n.Region} / {n.Map}" : null,
                        minimap.SelfInMap is { } s ? ((int)Math.Round(s.X), (int)Math.Round(s.Y)) : null,
                        minimap.Valid && !minimap.Collapsed ? minimap.Players : null, minimap.Collapsed,
                        tag is { } t ? ((int)Math.Round(t.Feet.X), (int)Math.Round(t.Feet.Y)) : null,
                        string.Concat(cells.Select(c => c.Occupied ? c.Key : '.')),
                        string.Join("+", buffs.Select(b => b.Name + (b.Faded ? "~" : ""))),
                        place?.MapId, place?.Position is { } wp ? ((int)Math.Round(wp.X), (int)Math.Round(wp.Y)) : null, place?.Support,
                        place?.Camera is { } cam ? ((int)Math.Round(cam.X), (int)Math.Round(cam.Y)) : null);
                    var loopElapsed = sw.Elapsed.TotalMilliseconds;
                    readMs += loopElapsed;
                    loopMs.Add(loopElapsed);
                    frames++;
                    if (state != last)
                    {
                        Console.WriteLine($"{Stamp(frame)}  {state.Describe(last)}");
                        last = state;
                    }
                }
            }
        });

        var started = Stopwatch.StartNew();
        var nextBeat = TimeSpan.FromSeconds(10);
        var capture = (source as CaptureFrameSource)?.Stream;
        while (!cancellation.IsCancellationRequested && !(capture?.Failed ?? false) && !consumer.IsCompleted && (seconds <= 0 || started.Elapsed.TotalSeconds < seconds))
        {
            if (source.NeedsMainLoop) ScreenCapture.PumpMainLoop(TimeSpan.FromMilliseconds(100)); else await Task.Delay(100);
            if (started.Elapsed >= nextBeat)
            {
                Console.WriteLine($"-- {started.Elapsed.TotalSeconds:0}s: {frames} frames, {frames / started.Elapsed.TotalSeconds:0.0} fps, read {readMs / Math.Max(frames, 1):0.0} ms/frame{(capture is null ? "" : $", dropped {capture.DroppedFrames}, idle {capture.IdleFrames}")}");
                nextBeat += TimeSpan.FromSeconds(10);
            }
        }
        capture?.Stop();
        cancellation.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }
        // Read after the consumer finished, so the list is complete and no longer written.
        var sortedLoop = loopMs.Order().ToArray();
        double LoopPct(double p) => sortedLoop.Length == 0 ? 0 : sortedLoop[Math.Clamp((int)Math.Ceiling(p / 100 * sortedLoop.Length) - 1, 0, sortedLoop.Length - 1)];
        Console.WriteLine($"done: {frames} frames in {started.Elapsed.TotalSeconds:0.0}s, read {readMs / Math.Max(frames, 1):0.0} ms/frame" +
            $" (the loop's work per frame: p50 {LoopPct(50):0.0}, p90 {LoopPct(90):0.0}, p99 {LoopPct(99):0.0}, max {LoopPct(100):0.0})");
        if (replay is { UnrecordedKeyframes: > 0 } r0) Console.WriteLine($"replay: {r0.UnrecordedKeyframes} keyframe(s) without a capture record in frames.jsonl; their pts extrapolated");
        if (overlay is not null)
        {
            await overlay.CompleteAsync();
            // The first frame pays for JIT compilation of the builder; it is reported apart.
            var first = overlayMs.Count > 0 ? overlayMs[0] : 0;
            var rest = overlayMs.Skip(1).Order().ToList();
            double Q(double q) => rest.Count == 0 ? 0 : rest[Math.Min(rest.Count - 1, (int)(q * rest.Count))];
            Console.WriteLine($"overlay  {overlay.Frames} frames to {overlay.Path} ({new FileInfo(overlay.Path).Length / 1024.0:0} KB gzip, {overlay.Bytes / 1024.0:0} KB JSON); " +
                $"built on the watcher thread in median {Q(0.5):0.000} ms, p99 {Q(0.99):0.000}, max {(rest.Count == 0 ? 0 : rest[^1]):0.000}, {rest.Count(ms => ms >= 1)} frames over 1 ms (first frame {first:0.000}, JIT)");
        }
        if (report is not null && catalog is not null) report.Print(Console.Out, catalog);
        entityWatch?.WriteSheets();
        if (entityWatch is not null) Console.WriteLine(entityWatch.SkipSummary());
        if (entityReport && entityWatch is not null) EntityReport.Print(Console.Out, entityWatch);
        if (entityReport) entityWatch?.PrintGapFiller(Console.Out);
        entityWatch?.SaveTagCache();
        if (ocr is not null)
        {
            var latencies = ocr.Latencies.Order().ToList();
            double LatencyPct(double p) => latencies.Count == 0 ? 0 : latencies[Math.Clamp((int)Math.Ceiling(p / 100 * latencies.Count) - 1, 0, latencies.Count - 1)];
            Console.WriteLine($"ocr      {ocr.Completed} reads on the worker thread, {ocr.WorkerMilliseconds:0} ms of recognition; queued to done median {LatencyPct(50):0.0} ms, p90 {LatencyPct(90):0.0}, max {LatencyPct(100):0.0};" +
                $" at most {ocr.MaxPending} waiting at once, {ocr.Pending} still out; Vision loaded in {ocr.WarmUp?.Milliseconds ?? 0:0} ms on that thread{(ocr.Errors > 0 ? $"; {ocr.Errors} reads failed (first: {ocr.FirstError})" : "")}");
        }
        if (mapFonts is not null)
        {
            // Name blocks settled: one per map change, and one more where a transition dimmed the block long enough to be read again.
            Console.WriteLine($"map font {tracker.Reads + tracker.Recognised} name blocks: {mapFonts.ByCache} named by the cache, {mapFonts.ByFont} by the fonts, {tracker.Reads} read" +
                $" ({tracker.ReadsConfirmed} confirmed by the catalog; {mapFonts.LinesTaught} lines taught, {mapFonts.LinesCached} cached, {mapFonts.Contradicted} refused as contradicting the fonts," +
                $" {mapFonts.CacheRefuted} cached lines dropped on a hit, {mapFonts.LinesDropped} for a text the catalog lacks, {mapFonts.Unsegmented} blocks not cut into lines); {mapFonts.Describe()}");
            if (mapFontPath is not null && mapFonts.Changed)
            {
                try { mapFonts.Save(mapFontPath); Console.WriteLine($"map font written to {mapFontPath}"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"map font not written to {mapFontPath}: {e.Message}"); }
            }
        }
        // The detector compares exact frames with the mosaic, so only a pixel-exact session builds one. A video of such a session
        // votes (a lossy one too: its errors spread around the true value), and its keyframes then replace the voted pixels they
        // saw with their own values (MosaicBuilder.ExactRefine).
        if (buildBackground && recording is not null && !exactFrames)
            Console.WriteLine("background: the session's frames are not pixel-exact (no one-pixel strokes in the status bar); nothing built");
        else if (buildBackground && recording is not null)
            await BackgroundBuild.Run(Console.Out, () => new RecordingFrameSource(recording, keyframes, cropTop, 0), buildFrames, screenMask, backgroundsPath,
                !keyframes && cropTop == 0 ? BackgroundBuild.KeyframesOf(recording) : null,
                Environment.GetEnvironmentVariable("OAKSOUL_CLOUDS") is "off" or "0" ? null : CloudArt.Load(spritesPath));
        return 0;
    }
}

/// <summary>Everything the watcher reports; a change in any field prints a line.</summary>
sealed record State(int? Level, string? Job, string? Name, (long Current, long Maximum)? Hp, (long Current, long Maximum)? Mp, (long Total, double Percent)? Exp, string? Map, (int X, int Y)? Position, int? Players, bool Collapsed, (int X, int Y)? Feet, string Slots, string Buffs,
    int? MapId = null, (int X, int Y)? World = null, string? Support = null, (int X, int Y)? Camera = null)
{
    public string Describe(State? previous)
    {
        string Mark<T>(T value, T? before) => previous is not null && !Equals(value, before) ? "*" : " ";
        var hp = Hp is { } h ? $"{h.Current}/{h.Maximum} ({100.0 * h.Current / h.Maximum:0}%)" : "?";
        var mp = Mp is { } m ? $"{m.Current}/{m.Maximum}" : "?";
        var exp = Exp is { } e ? $"{e.Total:N0} {e.Percent:0.00}%" : "?";
        var pos = Collapsed ? "closed" : Position is { } p ? $"{p.X},{p.Y}" : "?";
        var who = $"Lv{Mark(Level, previous?.Level)}{Level?.ToString() ?? "?",-4}{Mark(Job, previous?.Job)}{Job ?? "?",-22}{Mark(Name, previous?.Name)}{Name ?? "?",-12}";
        var feet = Feet is { } f ? $"{f.X},{f.Y}" : "?";
        var worldPos = World is { } w ? $"{w.X},{w.Y}" : "?";
        var camera = Camera is { } c ? $"{c.X},{c.Y}" : "?";
        var place = MapId is null && World is null ? "" : $" id{Mark(MapId, previous?.MapId)}{MapId?.ToString() ?? "?",-9} world{Mark(World, previous?.World)}{worldPos,-10} on{Mark(Support, previous?.Support)}{Support ?? "?",-10} cam{Mark(Camera, previous?.Camera)}{camera,-10}";
        return $"{who} HP{Mark(Hp, previous?.Hp)}{hp,-16} MP{Mark(Mp, previous?.Mp)}{mp,-10} EXP{Mark(Exp, previous?.Exp)}{exp,-16} map{Mark(Map, previous?.Map)}{Map ?? "?",-28} pos{Mark(Position, previous?.Position)}{pos,-8} players{Mark(Players, previous?.Players)}{Players?.ToString() ?? "?",-3} feet{Mark(Feet, previous?.Feet)}{feet,-9} slots{Mark(Slots, previous?.Slots)}{Slots} buffs{Mark(Buffs, previous?.Buffs)}{Buffs}{place}";
    }
}
