using System.Globalization;
using OakSoul.Bake;
using OakSoul.Overlay;

// OakSoul.Bake: bake the overlay stream over a recording's frames, for reviewing what the pipeline saw frame by frame.
// Recordings only: nothing is captured, attached to or drawn on screen. Run from the repository root.
//
//   regenerate SESSION [--video] [--out PATH] [--character NAME] [--pet NAME|none]
//       run the current watcher pipeline over the recording's keyframes (or, with --video, its lossless video) and write a
//       fresh stream to data/overlays/SESSION/overlay.jsonl.gz (overlay-video.jsonl.gz); the watcher's log goes next to it;
//       --character and --pet are passed on as the watcher's --name and --pet (defaults LynxHope and Mara)
//   render SESSION SELECTION [--stream PATH] [--layers a,b | --hide a,b] [--scale N] [--out DIR] [--name NAME] [--no-sheet]
//       one PNG per selected frame, <seq>_<m-ss.s>.png, in data/bake/SESSION/NAME/ (default name from the selection), plus a
//       contact sheet of the selection and items.txt (the list below for every frame); frames from the keyframe PNG when one
//       exists for the sequence, else from the lossless video (--video-pixels: always from the video); --scale (default 2)
//       magnifies nearest-neighbour
//   list SESSION SELECTION [--stream PATH] [--layers a,b | --hide a,b]
//       one line per item of each selected frame, with its label, rect and feet
//   sheets SESSION [--stream PATH] [--out DIR]
//       the watcher's --entity-sheet contact sheets, rendered from the stream (default data/bake/SESSION/entity-sheets)
//   windows SESSION [--seconds S] [--stream PATH]
//       rank windows of S seconds (default 15) by what goes on in them: mob detections, kills (EXP gains), drops, the pet near mobs
//   check SESSION [--stream PATH]
//       validate a stream: JSON round trip, the layer registry, and the join with frames.jsonl (sequence and pts)
//   truth SESSION [--stream PATH] [--truth FILE]
//       compare a stream with the reviewed ground truth (default truth/SESSION.jsonl): pass or fail per entry, and totals
//   layers                      print the layer registry (as OakSoul.Watch --list-layers)
//   diff A B [--bucket N]       compare two streams of one recording frame by frame, without the track ids: how many frames are
//       identical, differ only in a tag's text pending on one side, only in an entity named on one side, in the detector running
//       on one side only, or in more (an entity's kind or place), per N frames (default 500), with examples; A and B are stream
//       paths or session names (their default streams)
//
// SELECTION: --frame N (a capture sequence, the number in the keyframe file name), --frames 1411,1426,1441-1500 (sequences
// and inclusive ranges), --from M:SS[.s] --seconds S (time into the session), or --all.

try
{
    return await Run(args);
}
catch (BakeException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return args.Length == 0 ? 2 : 0; }
    var command = args[0];
    if (command is "layers" or "--list-layers") { OverlayLayers.Print(Console.Out); return 0; }
    if (command == "diff") return StreamDiff.Run(args[1..]);
    var o = Options.Parse(args[1..]);
    var session = Session.Resolve(o.Positional.FirstOrDefault() ?? throw new BakeException($"{command}: name a session (recordings/<name>) or a session folder"));
    switch (command)
    {
        case "regenerate": return await Commands.Regenerate(session, o);
        case "render": return Commands.Render(session, o);
        case "list": return Commands.List(session, o);
        case "sheets": return Commands.EntitySheets(session, o);
        case "windows": return Commands.Windows(session, o);
        case "check": return Commands.Check(session, o);
        case "truth": return Truth.Run(session, o);
        default: Console.Error.WriteLine($"unknown command {command}"); Usage(); return 2;
    }
}

static void Usage()
{
    Console.Error.WriteLine("""
        usage: OakSoul.Bake COMMAND SESSION [options]   (run from the repository root)
          regenerate SESSION [--video] [--out PATH] [--character NAME] [--pet NAME|none]
          render SESSION (--frame N | --frames LIST | --from M:SS --seconds S | --all) [--stream PATH] [--layers a,b | --hide a,b] [--scale N] [--out DIR] [--name NAME] [--no-sheet] [--video-pixels]
          list SESSION (selection) [--stream PATH] [--layers a,b | --hide a,b]
          sheets SESSION [--stream PATH] [--out DIR]
          windows SESSION [--seconds S] [--stream PATH]
          check SESSION [--stream PATH]
          truth SESSION [--stream PATH] [--truth FILE]
          layers
          diff A B [--bucket N]
        SESSION is a folder name under recordings/ or a session folder; streams default to data/overlays/SESSION/overlay.jsonl.gz.
        """);
}

namespace OakSoul.Bake
{
    /// <summary>The command line after the command: positional arguments and options.</summary>
    public sealed class Options
    {
        public List<string> Positional { get; } = [];
        public bool Video, All, NoSheet, VideoPixels;
        public string? Stream, Out, Name, From, CharacterName, Pet, Truth;
        public double? Seconds;
        public int Scale = 2;
        public List<(long First, long Last)> Frames { get; } = [];
        public HashSet<string>? Layers, Hide;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new BakeException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--video": o.Video = true; break;
                    case "--all": o.All = true; break;
                    case "--no-sheet": o.NoSheet = true; break;
                    case "--video-pixels": o.VideoPixels = true; break;
                    case "--stream": o.Stream = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--name": o.Name = Next(); break;
                    case "--character": o.CharacterName = Next(); break;
                    case "--pet": o.Pet = Next(); break;
                    case "--truth": o.Truth = Next(); break;
                    case "--from": o.From = Next(); break;
                    case "--seconds": o.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--scale": o.Scale = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--frame":
                        var one = Sequence(Next());
                        o.Frames.Add((one, one));
                        break;
                    case "--frames":
                        foreach (var part in Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            o.Frames.Add(part.Split('-') is [var a, var b] ? (Sequence(a), Sequence(b)) : (Sequence(part), Sequence(part)));
                        break;
                    case "--layers": o.Layers = LayerSet(Next()); break;
                    case "--hide": o.Hide = LayerSet(Next()); break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new BakeException($"unknown option {args[i]}");
                        o.Positional.Add(args[i]);
                        break;
                }
            }
            if (o.Scale is < 1 or > 4) throw new BakeException("--scale must be 1 to 4");
            return o;
        }

        private static long Sequence(string text) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : throw new BakeException($"not a capture sequence: {text}");

        private static HashSet<string> LayerSet(string text)
        {
            var set = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            foreach (var l in set)
                if (OverlayLayers.Find(l) is null) throw new BakeException($"unknown layer {l} (layers: {string.Join(", ", OverlayLayers.Names)})");
            return set;
        }

        /// <summary>The layers to draw or list: --layers, else all but --hide.</summary>
        public IReadOnlySet<string> DrawnLayers => Layers ?? OverlayLayers.Names.Where(l => Hide?.Contains(l) != true).ToHashSet();

        public bool HasSelection => All || Frames.Count > 0 || From is not null;

        /// <summary>"0:47.5" or "47.5" (seconds) or "1:10".</summary>
        public static double ParseTime(string text)
        {
            var parts = text.Split(':');
            return parts.Length switch
            {
                1 => double.Parse(parts[0], CultureInfo.InvariantCulture),
                2 => int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[1], CultureInfo.InvariantCulture),
                _ => throw new BakeException($"not a time: {text} (M:SS or seconds)"),
            };
        }
    }

    /// <summary>
    /// <c>diff A B</c>: the two streams compared frame by frame by <see cref="OverlayDiff"/> (frames paired by capture sequence,
    /// items by kind, name and place, never by track id). The agreed design's other half, one stream's items drawn over the
    /// other's with the unpaired or changed ones styled apart (<see cref="RenderOptions.Style"/>), is still to come.
    /// </summary>
    public static class StreamDiff
    {
        public static int Run(string[] args)
        {
            var paths = new List<string>();
            var bucket = 500;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--bucket") { if (i + 1 >= args.Length || !int.TryParse(args[++i], CultureInfo.InvariantCulture, out bucket) || bucket < 1) throw new BakeException("--bucket needs a positive number"); }
                else if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new BakeException($"unknown option {args[i]}");
                else paths.Add(args[i]);
            }
            if (paths.Count != 2) throw new BakeException("diff: name two streams (paths, or sessions for their default streams)");
            var a = OverlayStream.Load(Resolve(paths[0]));
            var b = OverlayStream.Load(Resolve(paths[1]));
            Console.WriteLine($"A: {a.Path} ({a.Header.Recording ?? "?"}, {a.Frames.Count} frames)");
            Console.WriteLine($"B: {b.Path} ({b.Header.Recording ?? "?"}, {b.Frames.Count} frames)");
            if (a.Header.Recording is { } ra && b.Header.Recording is { } rb && ra != rb) Console.WriteLine($"warning: the streams are of different recordings ({ra}, {rb}); frames pair by sequence all the same");
            OverlayDiff.Compare(a, b, bucket).Print(Console.Out);
            return 0;
        }

        private static string Resolve(string arg)
        {
            if (File.Exists(arg)) return arg;
            var byName = Path.Combine("data", "overlays", arg, "overlay.jsonl.gz");
            if (File.Exists(byName)) return byName;
            throw new BakeException($"diff: no stream at {arg} (nor {byName})");
        }
    }
}
