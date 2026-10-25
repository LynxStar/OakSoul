# Capture notes

Design rationale and measurements for the capture layer.

## Design decisions

**Per-window capture, not screen capture.** ScreenCaptureKit captures one window by ID even when it is unfocused or covered. That is what allows several client windows to be captured independently.

**Capture at 1x points.** On a Retina display the window is requested at its point size, so frames contain the original game pixels. This keeps template matching exact and halves the work. `--width` and `--height` override it.

**Crop before scale.** `--crop X,Y,W,H` (or the `--crop-top N` shortcut) sets ScreenCaptureKit's source rectangle, in points from the window's top-left, so the title bar of a windowed client never enters the frame. Measured on the real client: windowed, the window is 1366x796 with a 28 point title bar over a native 1366x768 render, and `--crop-top 28` gives pixel-exact frames. Fullscreen, the same client is 1728x1116 with 72 point black bars top and bottom and the render scaled 1.27x, so fullscreen captures are resampled and softer; prefer windowed for vision work.

**Why a Swift shim.** Python can reach Objective-C frameworks at run time through PyObjC, but .NET interop is P/Invoke to C functions. ScreenCaptureKit needs selectors, blocks and a delegate class, so a few dozen lines of Swift in `native/OakSoulCapture/Capture.swift` expose plain C functions instead (`oaksoul_init`, `oaksoul_list_windows`, `oaksoul_start_capture`, `oaksoul_stop_capture`, `oaksoul_pump_main_loop`). The .NET macOS workload would avoid the shim but requires full Xcode. The shim is built with `swiftc` from the Command Line Tools by an MSBuild target in `OakSoul.Capture.csproj`.

**Main run loop.** A console host has no AppKit run loop, so `Program.cs` calls `PumpMainLoop` in a loop on the main thread while capturing. Frames arrive on a native thread, are copied into pooled buffers and queued on a bounded channel that drops the oldest frame when full.

**Exact window selection.** A window is chosen by window ID, or by pid or app name plus exact title. There is no fuzzy fallback, so the recorder never attaches to an unintended window.

**Recording strategy.** Continuous video through ffmpeg, plus sparse lossless PNG keyframes, plus per-frame JSONL metadata. The video is for replay and detector training. The PNGs are pixel-exact, for template extraction and threshold tuning. The metadata joins them.

**Constant frame rate output.** ScreenCaptureKit only delivers a frame when the window changed. The video sink keeps a constant-rate timeline anchored at the first frame: gaps of up to two seconds are filled by repeating the previous frame, longer idle stretches are cut out of the video and reported as `cutSeconds`, and a frame that arrives ahead of the timeline is left out and reported as `skippedVideoFrames`. The per-frame metadata keeps true timestamps, so nothing is lost; the video is simply not a clock.

**ffmpeg in its own process group.** .NET starts children in the parent's process group, so a terminal Ctrl-C would hit ffmpeg too and it would exit before the recorder finished writing. The shim spawns ffmpeg with `POSIX_SPAWN_SETPGROUP` instead, so only the recorder sees the signal; it stops the capture, drains the queue, closes ffmpeg's stdin and waits for it to finalize the file.

## Storage benchmark

Measured on an M5 Pro Mac running macOS 26, capturing a synthetic 1366x768 sprite-game-style window at 29 to 30 fps for one minute, through a Python prototype of the same ScreenCaptureKit path.

| Format | One minute | Per hour |
|---|---|---|
| Raw BGRA, 30 fps | 7.38 GB | 453 GB |
| Raw BGRA, 15 fps (derived) | 3.69 GB | 227 GB |
| PNG every frame, 30 fps | 1.23 MB per frame | 133 GB |
| FFV1 lossless | 1.53 GB | 94 GB |
| x264 lossless RGB | 828 MB | 51 GB |
| H.264 x264 CRF 18, encoded live | 7.9 MB | 0.47 GB |
| H.264 x264 CRF 18, 4:4:4 chroma | 6.0 MB | 0.37 GB |
| H.264 VideoToolbox q65 (hardware) | 8.3 MB | 0.51 GB |
| HEVC VideoToolbox q65 (hardware) | 5.8 MB | 0.36 GB |
| x264 CRF 18 at 15 fps | 6.4 MB | 0.39 GB |

**Caveat.** The synthetic scene has flat colors and simple shapes, so the lossy rows are a floor. Real game art with textured backgrounds and many sprites will compress several times worse, still a few GB per hour. The raw row is exact.

Observations:

- 4:4:4 came out smaller than 4:2:0, because chroma subsampling smears sharp sprite edges and the encoder spends bits repairing them. Record 4:4:4 for vision datasets.
- The hardware HEVC encoder ran about fifteen times faster than real time with almost no CPU, so it is the default for continuous recording.
- Write cost was about 1.5 ms per frame for both the raw file and the ffmpeg pipe.

## Other findings

- ScreenCaptureKit captured the entire window from its backing store even when only a 48 pixel corner was on screen.
- A fully covered AppKit window stops drawing, so a hidden window yields stale frames. Whether a game under CrossOver keeps rendering when fully covered is not yet verified. The safe layout for several clients is to keep each window at least partially on screen.
- Screen Recording permission is granted per responsible application. When running from Terminal, Terminal must be allowed under System Settings > Privacy & Security > Screen Recording. Without it, listing windows times out or returns nothing.
- A shell spawned by some desktop apps runs child processes with coarse kernel timers (about 100 ms). This only affected the synthetic test window, not capture, and does not apply to processes started from Terminal.
