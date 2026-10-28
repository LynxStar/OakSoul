using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using OakSoul.Review.Model;

[assembly: AvaloniaTestApplication(typeof(OakSoul.Review.Tests.TestApp))]

namespace OakSoul.Review.Tests;

/// <summary>The app as the headless tests run it: Skia rendering, so frames can be captured to PNGs.</summary>
public sealed class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
}

/// <summary>The repository the tests run in, the fixture recording, and where screenshots and scratch output go.</summary>
public static class Fixture
{
    public const string Session = "fx-combat-exact";

    public static string? Root { get; } = RepoPaths.FindRoot();

    /// <summary>Whether fx-combat-exact and its stream (keyframes, or video) are here; they are gitignored.</summary>
    public static bool Has(bool video = false) =>
        Root is not null && File.Exists(Path.Combine(Root, "recordings", Session, "session.json")) &&
        File.Exists(Path.Combine(Root, "data", "overlays", Session, video ? "overlay-video.jsonl.gz" : "overlay.jsonl.gz"));

    /// <summary>data/review-shots/ (gitignored): the PNGs to look at.</summary>
    public static string Shots => Path.Combine(Root!, "data", "review-shots");

    /// <summary>
    /// Paths with the tracked folders (reviews/, truth/) and the crops moved to a fresh scratch folder under
    /// data/review-shots/, so a test never writes into the repository's review packets or ground truth; the session's
    /// truth file is copied there.
    /// </summary>
    public static RepoPaths ScratchPaths(string name)
    {
        var scratch = Path.Combine(Shots, "scratch-" + name);
        if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        Directory.CreateDirectory(Path.Combine(scratch, "truth"));
        var truth = Path.Combine(Root!, "truth", Session + ".jsonl");
        if (File.Exists(truth)) File.Copy(truth, Path.Combine(scratch, "truth", Session + ".jsonl"));
        return new RepoPaths { Root = Root!, Reviews = Path.Combine(scratch, "reviews"), Truth = Path.Combine(scratch, "truth"), ReviewData = Path.Combine(scratch, "data-reviews") };
    }

    /// <summary>Runs the UI thread's queued work until <paramref name="done"/> holds.</summary>
    public static void Pump(Func<bool> done, double seconds = 120)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException("the window did not get there in time");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
