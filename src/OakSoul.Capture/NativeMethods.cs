using System.Runtime.InteropServices;

namespace OakSoul.Capture;

/// <summary>P/Invoke surface of the Swift shim (native/OakSoulCapture/Capture.swift).</summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "liboaksoulcapture";

    [LibraryImport(Lib, EntryPoint = "oaksoul_init")]
    internal static partial void Init();

    [LibraryImport(Lib, EntryPoint = "oaksoul_list_windows")]
    internal static partial nint ListWindows();

    [LibraryImport(Lib, EntryPoint = "oaksoul_free_string")]
    internal static partial void FreeString(nint s);

    [LibraryImport(Lib, EntryPoint = "oaksoul_start_capture")]
    internal static partial long StartCapture(
        uint windowId, int width, int height, int scale, int fps,
        int cropX, int cropY, int cropWidth, int cropHeight,
        delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, double, int, void> callback,
        nint context, out nint error);

    /// <summary>Returns 1 when the stream stopped and its queue drained, 0 when the stop timed out.</summary>
    [LibraryImport(Lib, EntryPoint = "oaksoul_stop_capture")]
    internal static partial int StopCapture(long handle);

    /// <summary>Spawns argv (joined by newlines) in its own process group; returns the pid or a negative errno.</summary>
    [LibraryImport(Lib, EntryPoint = "oaksoul_spawn_detached", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SpawnDetached(string argvJoined, out int stdinFd, out int stderrFd);

    /// <summary>Exit code, -1 on error, or -2 if still running and noHang is nonzero.</summary>
    [LibraryImport(Lib, EntryPoint = "oaksoul_wait_process")]
    internal static partial int WaitProcess(int pid, int noHang);

    [LibraryImport(Lib, EntryPoint = "oaksoul_pump_main_loop")]
    internal static partial void PumpMainLoop(double seconds);

    /// <summary>Apple Vision text recognition on a BGRA image; returns the line count or -1 with an error string.</summary>
    [LibraryImport(Lib, EntryPoint = "oaksoul_recognize_text")]
    internal static partial int RecognizeText(byte* bgra, int width, int height, int bytesPerRow, int upscale, out nint json, out nint error);

    /// <summary>Copies a native UTF-8 string into managed memory and frees the native one.</summary>
    internal static string? TakeString(nint pointer)
    {
        if (pointer == 0) return null;
        try { return Marshal.PtrToStringUTF8(pointer); }
        finally { FreeString(pointer); }
    }
}
