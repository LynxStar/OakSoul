# OakSoul

OakSoul is a hobby project for driving game clients on a personal private MapleStory server using computer vision, without modifying the game client or the server. The long-term goal is AI-driven allies that play alongside the owner.

The approach: capture each client window with ScreenCaptureKit, run vision on the frames, decide, and later inject input. Input is out of scope for now. This first milestone is the capture layer, a per-window frame recorder.

## Prerequisites

- macOS 14 or newer
- Xcode Command Line Tools (provides `swiftc`; full Xcode is not required)
- .NET 10 SDK
- ffmpeg on PATH (for example `brew install ffmpeg`)

## Build

```
dotnet build OakSoul.slnx
```

The build compiles `native/OakSoulCapture/Capture.swift` with `swiftc` into `native/out/liboaksoulcapture.dylib` and copies it next to the built binaries.

## Run

List shareable windows:

```
dotnet run --project src/OakSoul.Recorder -- --list
```

Record one minute of a window chosen by process ID and exact title:

```
dotnet run --project src/OakSoul.Recorder -- --pid 1234 --title "Exact Title" --seconds 60
```

Record by window ID with the x264 4:4:4 preset, a keyframe every second, and the macOS title bar cropped off:

```
dotnet run --project src/OakSoul.Recorder -- --window-id 5678 --encoder x264 --keyframe-every 1 --crop-top 28
```

Window selection is exact. There is no fuzzy matching, and if zero or several windows match, the recorder refuses and says so.

## Options

```
--list                     print every shareable window and exit
--window-id N              capture this window ID (from --list)
--pid N --title "T"        capture the window with this exact title owned by this process
--app "Name" --title "T"   same, matching the owning application's name instead of pid
--fps N                    frames per second to request (default 30)
--seconds S                stop after S seconds (default 0 = until Ctrl-C)
--out DIR                  root for session folders (default recordings)
--name NAME                session folder name (default timestamp + window title)
--keyframe-every S         seconds between lossless PNG keyframes (default 2, 0 = none)
--encoder P                hevc (default), h264vt, x264, x264-420, lossless
--ffmpeg-args "..."        raw encoder arguments instead of a preset (use with --ext)
--width N --height N       capture size in pixels (default: window size in points = 1x)
--queue N                  frames buffered between capture and encoder (default 30)
--crop X,Y,W,H             capture only this region of the window, in points from its top-left
--crop-top N               drop N points from the top, e.g. 28 for a standard macOS title bar
```

A windowed CrossOver client carries a standard 28 point macOS title bar, so `--crop-top 28` yields frames of exactly the game's native size (1366x768 for a 1366x796 window). The crop is applied by ScreenCaptureKit before scaling, and `session.json` records it.

## Encoder presets

| Preset | ffmpeg arguments | Container |
|---|---|---|
| `hevc` (default) | `-c:v hevc_videotoolbox -q:v 65 -pix_fmt yuv420p -tag:v hvc1` | mp4 |
| `h264vt` | `-c:v h264_videotoolbox -q:v 65 -pix_fmt yuv420p` | mp4 |
| `x264` | `-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv444p` | mp4 |
| `x264-420` | `-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p` | mp4 |
| `lossless` | `-c:v libx264rgb -preset veryfast -qp 0` | mkv |

With `--ffmpeg-args` the container defaults to mkv unless `--ext` is given. For vision datasets prefer `x264` (4:4:4); for continuous recording the default `hevc` uses the hardware encoder. Sizes are in [docs/capture-notes.md](docs/capture-notes.md).

## Session folder

Each run creates `recordings/<timestamp>-<title>/` (or the name given with `--name`):

| Path | Contents |
|---|---|
| `video.mp4` or `video.mkv` | Constant frame rate video from ffmpeg; `videoFrame` in `frames.jsonl` is -1 for frames left out of it |
| `keyframes/NNNNNNN.png` | Lossless PNG every N seconds; the number is the frame sequence |
| `frames.jsonl` | One JSON line per frame: `sequence`, `pts`, `wallClockUtc`, `videoFrame`, `keyframe` (path, only on keyframes) |
| `session.json` | Summary: window, size, encoder, duration, delivered, dropped, idle, filled, skipped and cut counts, keyframe count, video bytes, ffmpeg exit code |
| `ffmpeg.log` | ffmpeg output |

ScreenCaptureKit only delivers a frame when the window changed, so the video fills gaps of up to two seconds by repeating the previous frame (`filledVideoFrames`). Longer idle stretches are cut out of the video (`cutSeconds`), and a frame that arrives ahead of the constant-rate timeline is left out (`skippedVideoFrames`). The metadata keeps the true timestamp of every frame either way.

ffmpeg runs in its own process group, so Ctrl-C stops the recorder cleanly: the capture stops first, the remaining frames are written, and ffmpeg finalizes the file.

## Permission

Screen Recording permission is granted per responsible application. When running from Terminal, allow Terminal under System Settings > Privacy & Security > Screen Recording. Without it, listing windows times out or returns nothing.

## Vision

`src/OakSoul.Vision` holds the HUD attention regions for the client at its native 1366x768 (title bar cropped) in `Layouts/hud-1366x768.json`, loaded through `HudLayout.Native1366x768`. Each region has a name, a kind (fixed, provisional, or dynamic), a rectangle in frame pixels from the top-left, and a note on what gets read from it. Edit the JSON to move a region; the loader rejects rectangles that do not fit the frame.

`FrameGeometry` maps that native layout onto any captured frame. The client aspect-fits its render into the window, so a frame is the native image at one uniform scale, centered, with black bars on the spare axis; `FrameGeometry.Detect` computes the scale and offset from the frame size, checks that the predicted bars are black (an uncropped title bar fails this check), maps rectangles and points both ways, and `ExtractNative` resamples a region back to its native size so readers are written once for native pixels. Verified on a fullscreen 1728x1116 recording: content 1728x972 at offset 72, scale 1.265.

### Readers

`StatusReader.Read(geometry, frame)` returns HP, MP and EXP from one frame: the exact numbers from the text row, and each bar's fill ratio as an independent cross-check.

- **Numbers** come from matching binarized glyphs against the client's own bitmap font, embedded as `Fonts/status-font.json` and learned from frames whose values were known (`GlyphFont.Learn`). Label letters are skipped because they fill the top of the text band where digits leave two empty rows; the green brackets split the EXP total from its percentage; touching digits are split at their thinnest column. On native frames every glyph matches at distance zero.
- **Bars** are measured from their highlight line and the run of saturated columns, so they survive small layout shifts. They read about one percent high on HP; the numbers are the authoritative value.
- **Scaled frames** are best effort: the reader tries three ink thresholds and keeps the reading with the fewest unknown glyphs. On the 1.265x fullscreen recording HP read exactly on every keyframe, MP on three of five and EXP on four of five; the misses are blurred glyphs reported as `?`, never wrong digits.

`MinimapReader.Read(geometry, frame)` returns the minimap panel as drawn, the map image inside it, the player's own dot and every other marker. The panel's top-left corner is fixed but its size follows the map, so the right edge is anchored on the orange WORLD button in the title bar and the bottom on the border colours down the left side; the map image sits at fixed insets inside. Dots are colour blobs: yellow is you, red other players, teal portals. `SelfInMap` gives your dot relative to the map image, which is what the server's minimap metadata will turn into world coordinates. About half a millisecond on a native frame.

`MapNameTracker` reads the region and map names (for example Dungeon and Ant Tunnel IV) through Apple's Vision text recognizer, exposed by the shim as `TextRecognizer`. It only re-reads when a coarse hash of the name block changes, so the roughly 30 ms OCR cost is paid on map change rather than per frame; the first call takes a few hundred milliseconds while Vision loads. Validated on all sixteen keyframes, windowed and fullscreen alike.

### Live watcher

`OakSoul.Watch` runs the readers on a live capture and prints a line whenever a value changes:

```
dotnet run --project src/OakSoul.Watch -- --pid 1234 --title "MapleStory" --crop-top 28
```

It takes the same window selection as the recorder plus `--crop-top`, `--fps` and `--seconds`. Each line shows HP with its percentage, MP, EXP total and percent, region and map name, your minimap position and the number of other players on the minimap, with a `*` after each field that changed. Every ten seconds a heartbeat reports frames, frame rate, reader time per frame and dropped frames; the readers cost well under a millisecond per native frame. If the frame does not match the native layout, for example a windowed client captured with its title bar, it says so and suggests `--crop-top 28`.

## Notes

Design rationale, the storage benchmark and other findings are in [docs/capture-notes.md](docs/capture-notes.md).

## Next steps

- HUD readers (HP, MP, EXP) running on recorded frames
- Minimap-based localisation
- Name-tag detection for players
- Template matching for mobs
- Input injection, later
