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
        uint windowId, int width, int height, int fps,
        delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, double, int, void> callback,
        nint context, out nint error);

    [LibraryImport(Lib, EntryPoint = "oaksoul_stop_capture")]
    internal static partial void StopCapture(long handle);

    [LibraryImport(Lib, EntryPoint = "oaksoul_pump_main_loop")]
    internal static partial void PumpMainLoop(double seconds);

    /// <summary>Copies a native UTF-8 string into managed memory and frees the native one.</summary>
    internal static string? TakeString(nint pointer)
    {
        if (pointer == 0) return null;
        try { return Marshal.PtrToStringUTF8(pointer); }
        finally { FreeString(pointer); }
    }
}
