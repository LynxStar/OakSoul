using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace OakSoul.Review;

/// <summary>
/// The Dock's icon on macOS. Avalonia gives a window's icon to the Dock only for an app bundle; run with
/// <c>dotnet run</c> the process is <c>dotnet</c>, so the icon is handed to AppKit directly
/// (<c>[NSApp setApplicationIconImage:]</c>).
/// </summary>
internal static class DockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern IntPtr Class(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector, IntPtr a, nuint b);

    public static void Set(string asset)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            using var stream = AssetLoader.Open(new Uri(asset));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = Send(Class("NSData"), Selector("dataWithBytes:length:"), pinned.AddrOfPinnedObject(), (nuint)bytes.Length);
                var image = Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
                if (image == IntPtr.Zero) return;
                var app = Send(Class("NSApplication"), Selector("sharedApplication"));
                Send(app, Selector("setApplicationIconImage:"), image);
            }
            finally { pinned.Free(); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException) { }
    }
}
