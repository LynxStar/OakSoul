# Capture notes

Design rationale and measurements for the capture layer.

## Design decisions

**Per-window capture, not screen capture.** ScreenCaptureKit captures one window by ID even when it is unfocused or covered. That is what allows several client windows to be captured independently.

**Capture at the backing scale, decimate to points.** On a Retina display the compositor shows the game's 1x render as a nearest-neighbour 2x image, so every game pixel is a uniform 2x2 block of the window. Asking ScreenCaptureKit for point-size output makes it filter that 2x image down, which blurs every edge (see Exactness). So the recorder requests the region at the display's backing scale (2732x1536 for the windowed client with `--crop-top 28`) and the frame callback keeps one pixel of every 2x2 block, reading only those pixels from the native buffer, so the 16.8 MB frame is never copied whole. Frames, video, keyframes and metadata stay 1366x768, now with the game's own pixels. The kept pixel is the bottom-right one of each block, source pixel (2x+1, 2y+1): in the 2x frame of the windowed client, row 0 is the title bar's black border line and the game's top row survives only in row 1. `--scale N` sets the factor; the default is the backing scale of the display holding most of the window, which the shim reports per window in the window list (1 when the window is on no display). `--scale 1` is point-size capture as before, for a non-Retina display. `--width` and `--height` request a raw output size with no decimation and cannot be combined with a scale above 1. The scale is fixed when the capture starts.

**sRGB, not the display's colour space.** Unless told otherwise, ScreenCaptureKit converts frames into the display's colour space, Display P3 on a Retina Mac: greys are unchanged but colours are pulled toward grey by a few levels, for example (102,85,68) becomes (99,86,70). The shim sets `colorSpaceName` to sRGB, so frames carry the values the game drew. `session.json` records `captureWidth`, `captureHeight` (what was asked of ScreenCaptureKit), `scale` and `colorSpace`, while `width` and `height` stay the delivered frame size; sessions without those fields were captured at point size in Display P3.

**Crop before scale.** `--crop X,Y,W,H` (or the `--crop-top N` shortcut) sets ScreenCaptureKit's source rectangle, in points from the window's top-left, so the title bar of a windowed client never enters the frame. Measured on the real client: windowed, the window is 1366x796 with a 28 point title bar over a native 1366x768 render, and `--crop-top 28` gives frames of exactly the native render size; with the default scale they also hold its exact pixels. Fullscreen, the same client is 1728x1116 with 72 point black bars top and bottom and the render scaled 1.27x, so fullscreen captures are resampled and softer; prefer windowed for vision work.

**Why a Swift shim.** Python can reach Objective-C frameworks at run time through PyObjC, but .NET interop is P/Invoke to C functions. ScreenCaptureKit needs selectors, blocks and a delegate class, so a few dozen lines of Swift in `native/OakSoulCapture/Capture.swift` expose plain C functions instead (`oaksoul_init`, `oaksoul_list_windows`, `oaksoul_start_capture`, `oaksoul_stop_capture`, `oaksoul_pump_main_loop`). The .NET macOS workload would avoid the shim but requires full Xcode. The shim is built with `swiftc` from the Command Line Tools by an MSBuild target in `OakSoul.Capture.csproj`.

**Main run loop.** A console host has no AppKit run loop, so `Program.cs` calls `PumpMainLoop` in a loop on the main thread while capturing. Frames arrive on a native thread, are copied (and decimated) into pooled buffers and queued on a bounded channel that drops the oldest frame when full.

**Exact window selection.** A window is chosen by window ID, or by pid or app name plus exact title. There is no fuzzy fallback, so the recorder never attaches to an unintended window.

**Recording strategy.** Continuous video through ffmpeg, plus sparse lossless PNG keyframes, plus per-frame JSONL metadata. The video is for replay and detector training. The PNGs are lossless copies of the captured frames, for template extraction and threshold tuning; since exact capture they hold the game's own pixels, while the earlier 1x recordings hold ScreenCaptureKit's blurred, P3-shifted version of them (see Exactness). The metadata joins them.

**Constant frame rate output.** ScreenCaptureKit only delivers a frame when the window changed. The video sink keeps a constant-rate timeline anchored at the first frame: gaps of up to two seconds are filled by repeating the previous frame, longer idle stretches are cut out of the video and reported as `cutSeconds`, and a frame that arrives ahead of the timeline is left out and reported as `skippedVideoFrames`. The per-frame metadata keeps true timestamps, so nothing is lost; the video is simply not a clock.

**ffmpeg in its own process group.** .NET starts children in the parent's process group, so a terminal Ctrl-C would hit ffmpeg too and it would exit before the recorder finished writing. The shim spawns ffmpeg with `POSIX_SPAWN_SETPGROUP` instead, so only the recorder sees the signal; it stops the capture, drains the queue, closes ffmpeg's stdin and waits for it to finalize the file.

## Exactness

What ScreenCaptureKit does to a 1x game window on a Retina display, found by comparing recorded frames of the windowed client with the client's own sprites from the WZ files, and confirmed on a synthetic window (a 1x sRGB image with known colours, edge lines and a 1-pixel checkerboard, shown 2x nearest-neighbour by the compositor as the game is).

- **The window is 2x2 blocks.** In the diagnostic 2x recording `recordings/fx-2x` (keyframes 2732x1536, no colour profile, P3 values), a first sampled measurement found the pixels of each even-column pair equal in 100% of samples and of each row pair in 99.6%; counting every block of all 20 keyframes, 99.996% of the 2x2 blocks below the top row are uniform, the rest sitting in the window's rounded bottom corners. Row 0 is black on every keyframe: the title bar's border line covers the top half of the game's first row, which survives in row 1.
- **Point-size output is blurred.** Asked for 1x output, ScreenCaptureKit filters the 2x image down with a separable kernel, [1/4, 1/2, 1/4] horizontally and [1/8, 3/4, 1/8] vertically. Flat areas survive, edges gain intermediate colours, and a 2x2 block of four different colours comes out as their mean. On the synthetic window only 93.0% of pixels were exact at 1x and none of the checkerboard.
- **Display P3.** Without a colour space set, frames are converted to the display's: greys are unchanged, colours are pulled toward grey. sRGB (102,85,68) came back as (99,86,70), (136,119,85) as (133,120,89), pure red (255,0,0) as (234,51,35). Converting the WZ art from sRGB to P3 reproduces the screen: of a sprite's opaque pixels, 99.5% match exactly for the Dark Axe Stump, 95% for the Red Snail, 91% for the Dark Stump and 90% for the Wild Boar (the rest is occlusion), against 27% without the conversion.
- **The existing recordings.** `fx-combat` and `fx-maps` were made at point size before the fix, so they are blurred and colour-shifted. The HUD readers were learned from such frames and read them as before; comparing sprites pixel for pixel against WZ art is not possible on them.
- **The fix, verified.** On the synthetic window, with the shim's sRGB setting and the default scale (2732x1536 requested, decimated to 1366x768), every rectangle and the background came back as exactly the requested sRGB values, the checkerboard and all edges were exact, row 0 was the image's row 0, and 99.98% of the whole frame matched, every miss within 24 pixels of a rounded bottom corner. With `--scale 1` the interiors are exact sRGB too, but edges stay blurred. Twelve seconds at 30 fps delivered 357 frames, 29.5 fps, none dropped.
- **Cost.** Decimation takes 0.9 ms per frame on the capture callback's thread (NEON, four output pixels per step; the scalar loop took 2.1 ms there and 0.27 ms in an offline benchmark). Copying a 1x frame takes 0.17 ms. `session.json` records the mean as `frameCopyMs`.
- **Readers on exact frames.** Run on the fx-2x keyframes decimated to 1366x768, and on the same converted to sRGB, HP, MP, EXP and the quickslots read as on blurred frames, but the level badge read 3 or nothing instead of 31, Vision OCR read the name LynxHope as LunxHope (so the name-tag template was never learned) and the map name as Deep Valley ! (the map id still resolved). Converting 60 fx-combat keyframes to sRGB alone changed no reading. Measured again with the exact recording fx-exact: the status bar's name read LunxHope at every upscale from 4x to 10x, the level 31 but only at 8x, and the minimap panel was misplaced on both recordings, because on an exact frame the WORLD button's dark outline directly follows its orange run and the bar's lowest light-blue row passes for the white bottom line. Deep Valley I's panel is 191x220 on fx-combat; it read 191 to 287 wide and 217 high (once 358) on fx-2x, and on fx-exact as missing on 7 keyframes and 191 to 440 wide on the rest, so the map name's region never held still long enough to be read. Softening each OCR crop of an exact frame with the kernel above, before Vision sees it, made the name and the map name read right on all 40 exact keyframes; the level badge is binarized, the two minimap edges were fixed, and fx-combat and fx-maps read as before (README, Readers). The watcher now captures exactly, as the recorder does. The exact recordings on Ant Tunnel IV (fx-anttunnel, fx-anttunnel-open) showed that softening does not suit every crop: the binarized level badge LV. 32, softened first, read "3 2" at 8x on all 21 keyframes, and is now binarized from the unsoftened crop (32 at 6x, 8x and 10x; 31 still on fx-exact and fx-2x); and the name block read "Ant TunnelM" softened but "Ant Tunnel IV" as captured, so an exact frame whose softened map name the catalog does not know is read again unsoftened. The collapsed bar's "Dungeon: Ant Tunnel IV" reads right either way once the crop stops before the buttons.
- **Sprites on exact frames, and the video.** On the exact keyframes a mob is its WZ frame pixel for pixel (every visible opaque pixel equal, no colour conversion; the freeze is an exact per-channel multiply by (255, 168, 10) / 255), so the entity detector compares sprites strictly, with a tolerance of 0 (README, Sprite verification). That works only on keyframes and live frames, not on the lossy video: decoding fx-exact's x264 video (CRF 18, 4:4:4) at each of its 20 keyframes' times and comparing, 0.5% of the viewport's pixels and 2.7% of the visible sprite pixels come back exact; a sprite pixel moves by a median of 9 levels in its worst channel (p90 21, p99 38; summed over B, G, R a median of 15, p99 59), the whole viewport by 7 (p90 13), with no bias (mean signed difference under 0.3 per channel). Keeping 93% of the sprite pixels would take 24 levels per channel. A replayed video is matched by the tolerant 3x3 path instead, and a mosaic voted from the video gets the keyframes' exact values where they saw the background.
- **Capture resolution.** `captureResolution` makes no difference between `.automatic` and `.best`; the shim pins `.best`. `.nominal` is not an alternative: with 2x output it returns a misplaced image, and at point size it keeps one pixel per 2x2 block but not the same one everywhere (on a four-colour test block 62% bottom-right, 20% bottom-left, 14% top-right, 4% top-left), so the title bar line could leak into the top row.

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
- While the screen is locked ScreenCaptureKit delivers no complete frames (every frame reports idle), and the window list reports frames shrunk by about a tenth (1366x800 listed as 1230x721), so neither a recording nor a crop computed from the list works until the screen is unlocked.
- Screen Recording permission is granted per responsible application. When running from Terminal, Terminal must be allowed under System Settings > Privacy & Security > Screen Recording. Without it, listing windows times out or returns nothing.
- A shell spawned by some desktop apps runs child processes with coarse kernel timers (about 100 ms). This only affected the synthetic test window, not capture, and does not apply to processes started from Terminal.
