using System.Globalization;
using Avalonia;
using OakSoul.Bake;
using OakSoul.Review.Model;

namespace OakSoul.Review;

// OakSoul.Review: the review tool, a desktop window for reviewing what the watcher concluded about a recording, frame by
// frame. Recordings only: nothing is captured, attached to or drawn over the game. Run from the repository root (or pass
// --root); it reads recordings/ and data/, writes review packets to reviews/ and promoted entries to truth/.
//
//   OakSoul.Review [SESSION] [--video] [--frame SEQ | --time M:SS] [--root DIR] [--reviewer NAME] [--character NAME] [--pet NAME|none]
//       open SESSION (a folder name under recordings/, or a session folder) in keyframe mode, or with --video in video mode;
//       without SESSION the window opens on the list of recordings. --reviewer names the reviewer in packets and promoted
//       truth (default Arthur); --character and --pet are passed to the watcher when a stream is regenerated (default: the
//       stream's own, else the watcher's defaults)
//   OakSoul.Review regenerate SESSION [--video] [--out PATH] [--character NAME] [--pet NAME|none]
//       what the window runs in a child process to make a stream in the background: OakSoul.Bake's regenerate, unchanged
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["regenerate", ..]) return Regenerate(args[1..]);
        ToolOptions options;
        try { options = ToolOptions.Parse(args); }
        catch (ArgumentException e) { Console.Error.WriteLine($"error: {e.Message}"); return 2; }
        var root = RepoPaths.FindRoot(options.Root);
        if (root is null)
        {
            Console.Error.WriteLine("error: no OakSoul repository here; run from the repository root or pass --root DIR");
            return 2;
        }
        Directory.SetCurrentDirectory(root);
        App.Options = options;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    /// <summary>The child process of a background regeneration: Bake's regenerate in this process, from the repository root.</summary>
    private static int Regenerate(string[] args)
    {
        try
        {
            var o = Options.Parse(args);
            var session = Session.Resolve(o.Positional.FirstOrDefault() ?? throw new BakeException("regenerate: name a session"));
            return Commands.Regenerate(session, o).GetAwaiter().GetResult();
        }
        catch (BakeException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }
}

/// <summary>The window's command line.</summary>
public sealed class ToolOptions
{
    public string? Root, Session, Character, Pet, Time;
    public string Reviewer = "Arthur";
    public bool Video;
    public long? Frame;

    public static ToolOptions Parse(string[] args)
    {
        var o = new ToolOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--root": o.Root = Next(); break;
                case "--video": o.Video = true; break;
                case "--reviewer": o.Reviewer = Next(); break;
                case "--character": o.Character = Next(); break;
                case "--pet": o.Pet = Next(); break;
                case "--time": o.Time = Next(); break;
                case "--frame":
                    o.Frame = long.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : throw new ArgumentException("--frame takes a capture sequence");
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unknown option {args[i]}");
                    o.Session = args[i];
                    break;
            }
        }
        return o;
    }
}
