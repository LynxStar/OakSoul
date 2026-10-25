// OakSoul capture shim: wraps ScreenCaptureKit behind a tiny C ABI so .NET can P/Invoke it.
//
// Exports (all @_cdecl):
//   oaksoul_init()                       initialise the CoreGraphics window-server connection (call once, main thread)
//   oaksoul_list_windows() -> char*      JSON array of shareable windows; free with oaksoul_free_string
//   oaksoul_start_capture(...) -> i64    start streaming one window by ID; 0 on failure with *errorOut set
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
    configuration.width = Int(width > 0 ? width : Int32(window.frame.size.width))
    configuration.height = Int(height > 0 ? height : Int32(window.frame.size.height))
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
    if semaphore.wait(timeout: .now() + 10) == .timedOut { return fail("startCapture timed out") }
    if let e = startError { return fail("startCapture failed: \(e.localizedDescription)") }

    sessionsLock.lock()
    let handle = nextHandle
    nextHandle += 1
    sessions[handle] = Session(stream: stream, output: output, queue: queue)
    sessionsLock.unlock()
    return handle
}

@_cdecl("oaksoul_stop_capture")
public func oaksoul_stop_capture(_ handle: Int64) {
    sessionsLock.lock()
    let session = sessions.removeValue(forKey: handle)
    sessionsLock.unlock()
    guard let session = session else { return }
    let semaphore = DispatchSemaphore(value: 0)
    session.stream.stopCapture { _ in semaphore.signal() }
    _ = semaphore.wait(timeout: .now() + 10)
}

@_cdecl("oaksoul_pump_main_loop")
public func oaksoul_pump_main_loop(_ seconds: Double) {
    _ = CFRunLoopRunInMode(CFRunLoopMode.defaultMode, seconds, false)
}
