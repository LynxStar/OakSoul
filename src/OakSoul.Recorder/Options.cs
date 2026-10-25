namespace OakSoul.Recorder;

/// <summary>Command-line options. Window selection is deliberately exact: no fuzzy matching, no defaults.</summary>
public sealed class Options
{
    public bool List { get; private set; }
    public uint? WindowId { get; private set; }
    public int? Pid { get; private set; }
    public string? App { get; private set; }
    public string? Title { get; private set; }
    public int Fps { get; private set; } = 30;
    public double Seconds { get; private set; }
    public string OutputRoot { get; private set; } = "recordings";
    public string? SessionName { get; private set; }
    public double KeyframeEvery { get; private set; } = 2.0;
    public string Encoder { get; private set; } = "hevc";
    public string? FfmpegArgs { get; private set; }
    public string? Extension { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public int Queue { get; private set; } = 30;

    public static readonly IReadOnlyDictionary<string, (string Args, string Extension)> EncoderPresets =
        new Dictionary<string, (string, string)>
        {
            ["hevc"] = ("-c:v hevc_videotoolbox -q:v 65 -pix_fmt yuv420p -tag:v hvc1", "mp4"),
            ["h264vt"] = ("-c:v h264_videotoolbox -q:v 65 -pix_fmt yuv420p", "mp4"),
            ["x264"] = ("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv444p", "mp4"),
            ["x264-420"] = ("-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p", "mp4"),
            ["lossless"] = ("-c:v libx264rgb -preset veryfast -qp 0", "mkv"),
        };

    public (string Args, string Extension) ResolveEncoder()
    {
        if (FfmpegArgs is not null) return (FfmpegArgs, Extension ?? "mkv");
        if (!EncoderPresets.TryGetValue(Encoder, out var preset))
            throw new ArgumentException($"unknown encoder preset '{Encoder}'; known: {string.Join(", ", EncoderPresets.Keys)}");
        return (preset.Args, Extension ?? preset.Extension);
    }

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        string Next(int i) => i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value");
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--list": o.List = true; break;
                    case "--window-id": o.WindowId = uint.Parse(Next(i++)); break;
                    case "--pid": o.Pid = int.Parse(Next(i++)); break;
                    case "--app": o.App = Next(i++); break;
                    case "--title": o.Title = Next(i++); break;
                    case "--fps": o.Fps = int.Parse(Next(i++)); break;
                    case "--seconds": o.Seconds = double.Parse(Next(i++)); break;
                    case "--out": o.OutputRoot = Next(i++); break;
                    case "--name": o.SessionName = Next(i++); break;
                    case "--keyframe-every": o.KeyframeEvery = double.Parse(Next(i++)); break;
                    case "--encoder": o.Encoder = Next(i++); break;
                    case "--ffmpeg-args": o.FfmpegArgs = Next(i++); break;
                    case "--ext": o.Extension = Next(i++); break;
                    case "--width": o.Width = int.Parse(Next(i++)); break;
                    case "--height": o.Height = int.Parse(Next(i++)); break;
                    case "--queue": o.Queue = int.Parse(Next(i++)); break;
                    case "-h": case "--help": return null;
                    default: throw new ArgumentException($"unknown option {args[i]}");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
        if (!o.List && o.WindowId is null && o.Title is null)
        {
            Console.Error.WriteLine("error: choose a window with --window-id, or --pid/--app plus --title (exact). Use --list to see them.");
            return null;
        }
        return o;
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine("""
            OakSoul.Recorder: record one window to video + sparse PNG keyframes + per-frame metadata.

              --list                     print every shareable window and exit
              --window-id N              capture this window ID (from --list)
              --pid N --title "T"        capture the window with this exact title owned by this process
              --app "Name" --title "T"   same, matching the owning application's name instead of pid
              --fps N                    frames per second to request (default 30)
              --seconds S                stop after S seconds (default 0 = until Ctrl-C)
              --out DIR                  root for session folders (default recordings)
              --name NAME                session folder name (default timestamp + window title)
              --keyframe-every S         seconds between lossless PNG keyframes (default 2, 0 = none)
              --encoder P                hevc (default), h264vt, x264, x264-420, lossless
              --ffmpeg-args "..."        raw encoder arguments instead of a preset (use with --ext)
              --width N --height N       capture size in pixels (default: window size in points = 1x)
              --queue N                  frames buffered between capture and encoder (default 30)
            """);
    }
}
