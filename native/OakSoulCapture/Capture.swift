// OakSoul capture shim: wraps ScreenCaptureKit behind a tiny C ABI so .NET can P/Invoke it.
//
// Exports (all @_cdecl):
//   oaksoul_init()                       initialise the CoreGraphics window-server connection (call once, main thread)
//   oaksoul_list_windows() -> char*      JSON array of shareable windows; free with oaksoul_free_string
//   oaksoul_start_capture(...) -> i64    start streaming one window by ID, optionally cropped; 0 on failure with *errorOut set
//   oaksoul_stop_capture(handle)         stop a stream started above
//   oaksoul_pump_main_loop(seconds)      run the main CFRunLoop for up to `seconds` (hosts without AppKit call this)
//
// Frame callback status codes: 0 = complete frame with pixels, 1..5 = SCFrameStatus (idle, blank, suspended,
// started, stopped) with no pixels, -1 = stream stopped with an error.

import Foundation
import CoreGraphics
import CoreMedia
import CoreVideo
import ScreenCaptureKit

public typealias OakSoulFrameCallback = @convention(c) (
    UnsafeMutableRawPointer?,   // caller context
    UnsafePointer<UInt8>?,      // BGRA pixels, valid only during the callback and only when status == 0
    Int32, Int32, Int32,        // width, height, bytes per row
    Double,                     // presentation timestamp in seconds (host clock)
    Int32                       // status
) -> Void

private final class StreamOutput: NSObject, SCStreamOutput, SCStreamDelegate {
    private let callback: OakSoulFrameCallback
    private let context: UnsafeMutableRawPointer?

    init(callback: @escaping OakSoulFrameCallback, context: UnsafeMutableRawPointer?) {
        self.callback = callback
        self.context = context
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        var status: Int32 = 0
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
           let first = attachments.first,
           let raw = first[.status] as? Int {
            status = Int32(raw)
        }
        guard status == 0, let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else {
            callback(context, nil, 0, 0, 0, 0, status)
            return
        }
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
        let width = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let height = Int32(CVPixelBufferGetHeight(pixelBuffer))
        let bytesPerRow = Int32(CVPixelBufferGetBytesPerRow(pixelBuffer))
        let pts = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        callback(context, base.assumingMemoryBound(to: UInt8.self), width, height, bytesPerRow, pts, 0)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        callback(context, nil, 0, 0, 0, 0, -1)
    }
}

private final class Session {
    let stream: SCStream
    let output: StreamOutput
    let queue: DispatchQueue
    init(stream: SCStream, output: StreamOutput, queue: DispatchQueue) {
        self.stream = stream
        self.output = output
        self.queue = queue
    }
}

private var sessions: [Int64: Session] = [:]
private var nextHandle: Int64 = 1
private let sessionsLock = NSLock()

private func fetchShareableContent() -> (SCShareableContent?, Error?) {
    let semaphore = DispatchSemaphore(value: 0)
    var content: SCShareableContent?
    var error: Error?
    SCShareableContent.getExcludingDesktopWindows(true, onScreenWindowsOnly: false) { c, e in
        content = c
        error = e
        semaphore.signal()
    }
    if semaphore.wait(timeout: .now() + 10) == .timedOut {
        return (nil, NSError(domain: "OakSoul", code: 1, userInfo: [NSLocalizedDescriptionKey: "timed out listing shareable content; is Screen Recording permission granted?"]))
    }
    return (content, error)
}

@_cdecl("oaksoul_init")
public func oaksoul_init() {
    _ = CGMainDisplayID()
}

@_cdecl("oaksoul_list_windows")
public func oaksoul_list_windows() -> UnsafeMutablePointer<CChar>? {
    let (content, error) = fetchShareableContent()
    guard let content = content else {
        let message = error?.localizedDescription ?? "unknown error"
        let payload = try! JSONSerialization.data(withJSONObject: ["error": message])
        return strdup(String(data: payload, encoding: .utf8)!)
    }
    var rows: [[String: Any]] = []
    for window in content.windows {
        rows.append([
            "id": window.windowID,
            "pid": window.owningApplication?.processID ?? 0,
            "app": window.owningApplication?.applicationName ?? "",
            "title": window.title ?? "",
            "x": window.frame.origin.x,
            "y": window.frame.origin.y,
            "width": window.frame.size.width,
            "height": window.frame.size.height,
            "onScreen": window.isOnScreen,
            "layer": window.windowLayer,
        ])
    }
    let payload = try! JSONSerialization.data(withJSONObject: rows)
    return strdup(String(data: payload, encoding: .utf8)!)
}

@_cdecl("oaksoul_free_string")
public func oaksoul_free_string(_ pointer: UnsafeMutablePointer<CChar>?) {
    free(pointer)
}

@_cdecl("oaksoul_start_capture")
public func oaksoul_start_capture(
    _ windowID: UInt32, _ width: Int32, _ height: Int32, _ fps: Int32,
    _ cropX: Int32, _ cropY: Int32, _ cropWidth: Int32, _ cropHeight: Int32,
    _ callback: OakSoulFrameCallback, _ context: UnsafeMutableRawPointer?,
    _ errorOut: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?
) -> Int64 {
    func fail(_ message: String) -> Int64 {
        errorOut?.pointee = strdup(message)
        return 0
    }
    let (content, error) = fetchShareableContent()
    guard let content = content else { return fail(error?.localizedDescription ?? "unknown error") }
    guard let window = content.windows.first(where: { $0.windowID == windowID }) else {
        return fail("no shareable window with id \(windowID)")
    }

    let filter = SCContentFilter(desktopIndependentWindow: window)
    let configuration = SCStreamConfiguration()
    // An optional crop, in points relative to the window's top-left corner (for example to drop a title bar).
    let hasCrop = cropWidth > 0 && cropHeight > 0
    if hasCrop {
        configuration.sourceRect = CGRect(x: CGFloat(cropX), y: CGFloat(cropY), width: CGFloat(cropWidth), height: CGFloat(cropHeight))
    }
    let baseWidth = hasCrop ? cropWidth : Int32(window.frame.size.width)
    let baseHeight = hasCrop ? cropHeight : Int32(window.frame.size.height)
    configuration.width = Int(width > 0 ? width : baseWidth)
    configuration.height = Int(height > 0 ? height : baseHeight)
    configuration.pixelFormat = kCVPixelFormatType_32BGRA
    configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(fps, 1)))
    configuration.queueDepth = 8
    configuration.showsCursor = false
    configuration.shouldBeOpaque = true
    if #available(macOS 14.0, *) {
        configuration.ignoreShadowsSingleWindow = true
    }

    let output = StreamOutput(callback: callback, context: context)
    let stream = SCStream(filter: filter, configuration: configuration, delegate: output)
    let queue = DispatchQueue(label: "oaksoul.capture.\(windowID)")
    do {
        try stream.addStreamOutput(output, type: .screen, sampleHandlerQueue: queue)
    } catch {
        return fail("addStreamOutput failed: \(error.localizedDescription)")
    }

    let semaphore = DispatchSemaphore(value: 0)
    var startError: Error?
    stream.startCapture { e in
        startError = e
        semaphore.signal()
    }
    if semaphore.wait(timeout: .now() + 10) == .timedOut {
        stream.stopCapture { _ in }
        queue.sync {}
        return fail("startCapture timed out")
    }
    if let e = startError {
        queue.sync {}
        return fail("startCapture failed: \(e.localizedDescription)")
    }

    sessionsLock.lock()
    let handle = nextHandle
    nextHandle += 1
    sessions[handle] = Session(stream: stream, output: output, queue: queue)
    sessionsLock.unlock()
    return handle
}

@_cdecl("oaksoul_stop_capture")
public func oaksoul_stop_capture(_ handle: Int64) -> Int32 {
    sessionsLock.lock()
    let session = sessions.removeValue(forKey: handle)
    sessionsLock.unlock()
    guard let session = session else { return 1 }
    let semaphore = DispatchSemaphore(value: 0)
    session.stream.stopCapture { _ in semaphore.signal() }
    let stopped = semaphore.wait(timeout: .now() + 10) != .timedOut
    // Drain the sample queue so no callback can run after the caller releases its context.
    session.queue.sync {}
    return stopped ? 1 : 0
}

@_cdecl("oaksoul_pump_main_loop")
public func oaksoul_pump_main_loop(_ seconds: Double) {
    _ = CFRunLoopRunInMode(CFRunLoopMode.defaultMode, seconds, false)
}

// MARK: - Child processes in their own process group
//
// .NET's Process class leaves children in the parent's process group, so a terminal Ctrl-C aimed at the
// recorder also hits ffmpeg, which then stops reading and exits before the recorder can finish cleanly.
// These two functions spawn a child with POSIX_SPAWN_SETPGROUP so terminal signals never reach it; the
// child still exits on its own when the recorder closes its stdin.

/// Spawns `argv[0]` (searched on PATH) with the given arguments, joined by newlines. Returns the pid, or a
/// negative errno. On success `stdinFd` is the write end of the child's stdin and `stderrFd` the read end of
/// its stderr; the caller owns both.
@_cdecl("oaksoul_spawn_detached")
public func oaksoul_spawn_detached(
    _ argvJoined: UnsafePointer<CChar>,
    _ stdinFd: UnsafeMutablePointer<Int32>,
    _ stderrFd: UnsafeMutablePointer<Int32>
) -> Int32 {
    let arguments = String(cString: argvJoined).components(separatedBy: "\n")
    guard let program = arguments.first, !program.isEmpty else { return -EINVAL }

    var inPipe: [Int32] = [0, 0]
    var errPipe: [Int32] = [0, 0]
    guard pipe(&inPipe) == 0 else { return -errno }
    guard pipe(&errPipe) == 0 else {
        close(inPipe[0]); close(inPipe[1])
        return -errno
    }

    var actions: posix_spawn_file_actions_t? = nil
    posix_spawn_file_actions_init(&actions)
    posix_spawn_file_actions_adddup2(&actions, inPipe[0], 0)
    posix_spawn_file_actions_adddup2(&actions, errPipe[1], 2)
    posix_spawn_file_actions_addinherit_np(&actions, 1)

    var attributes: posix_spawnattr_t? = nil
    posix_spawnattr_init(&attributes)
    // SETPGROUP with pgroup 0 puts the child in a new group of its own; CLOEXEC_DEFAULT closes every
    // descriptor the file actions do not mention, so the child does not inherit our other pipes.
    posix_spawnattr_setflags(&attributes, Int16(truncatingIfNeeded: POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT))
    posix_spawnattr_setpgroup(&attributes, 0)

    var argv: [UnsafeMutablePointer<CChar>?] = arguments.map { strdup($0) }
    argv.append(nil)
    var pid: pid_t = 0
    let rc = posix_spawnp(&pid, program, &actions, &attributes, argv, environ)
    for pointer in argv { free(pointer) }
    posix_spawn_file_actions_destroy(&actions)
    posix_spawnattr_destroy(&attributes)
    close(inPipe[0])
    close(errPipe[1])
    if rc != 0 {
        close(inPipe[1]); close(errPipe[0])
        return -rc
    }
    stdinFd.pointee = inPipe[1]
    stderrFd.pointee = errPipe[0]
    return pid
}

/// Waits for a child spawned above. Returns its exit code (128 + signal if it was killed), -1 on error, or
/// -2 when `noHang` is nonzero and the child is still running.
@_cdecl("oaksoul_wait_process")
public func oaksoul_wait_process(_ pid: Int32, _ noHang: Int32) -> Int32 {
    var status: Int32 = 0
    while true {
        let result = waitpid(pid, &status, noHang != 0 ? WNOHANG : 0)
        if result == pid { break }
        if result == 0 { return -2 }
        if errno == EINTR { continue }
        return -1
    }
    let termSignal = status & 0x7f
    if termSignal == 0 { return (status >> 8) & 0xff }
    return 128 + termSignal
}
