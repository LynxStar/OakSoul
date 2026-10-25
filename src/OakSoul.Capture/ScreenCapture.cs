using System.Text.Json;

namespace OakSoul.Capture;

/// <summary>Window discovery and main-loop pumping. Capture itself lives in <see cref="CaptureStream"/>.</summary>
public static class ScreenCapture
{
    private static bool _initialized;

    /// <summary>Must be called once from the process's main thread before anything else.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        NativeMethods.Init();
        _initialized = true;
    }

    /// <summary>Lists every shareable window, on-screen or not. Requires Screen Recording permission.</summary>
    public static IReadOnlyList<WindowInfo> ListWindows()
    {
        Initialize();
        var json = NativeMethods.TakeString(NativeMethods.ListWindows()) ?? "[]";
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"ScreenCaptureKit: {error.GetString()}");

        var windows = new List<WindowInfo>();
        foreach (var w in doc.RootElement.EnumerateArray())
        {
            windows.Add(new WindowInfo(
                Id: w.GetProperty("id").GetUInt32(),
                Pid: w.GetProperty("pid").GetInt32(),
                App: w.GetProperty("app").GetString() ?? "",
                Title: w.GetProperty("title").GetString() ?? "",
                X: w.GetProperty("x").GetDouble(),
                Y: w.GetProperty("y").GetDouble(),
                Width: w.GetProperty("width").GetDouble(),
                Height: w.GetProperty("height").GetDouble(),
                OnScreen: w.GetProperty("onScreen").GetBoolean(),
                Layer: w.GetProperty("layer").GetInt32()));
        }
        return windows;
    }

    /// <summary>
    /// Services the main thread's run loop for up to <paramref name="duration"/>. A console host has no AppKit
    /// run loop, so call this from the main thread in a loop while a capture is running.
    /// </summary>
    public static void PumpMainLoop(TimeSpan duration) => NativeMethods.PumpMainLoop(duration.TotalSeconds);
}
