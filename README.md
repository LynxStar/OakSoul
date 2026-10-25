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

Record by window ID with the x264 4:4:4 preset and a keyframe every second:

```
dotnet run --project src/OakSoul.Recorder -- --window-id 5678 --encoder x264 --keyframe-every 1
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
```

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
| `video.mp4` or `video.mkv` | Constant frame rate video from ffmpeg |
| `keyframes/NNNNNNN.png` | Lossless PNG every N seconds; the number is the frame sequence |
| `frames.jsonl` | One JSON line per frame: `sequence`, `pts`, `wallClockUtc`, `videoFrame`, `keyframe` (path, only on keyframes) |
| `session.json` | Summary: window, size, encoder, duration, delivered, dropped, idle and filled frame counts, keyframe count, video bytes |
| `ffmpeg.log` | ffmpeg output |

ScreenCaptureKit only delivers a frame when the window changed, so the video fills gaps by repeating the previous frame. `filledVideoFrames` in `session.json` reports how many were filled.

## Permission

Screen Recording permission is granted per responsible application. When running from Terminal, allow Terminal under System Settings > Privacy & Security > Screen Recording. Without it, listing windows times out or returns nothing.

## Notes

Design rationale, the storage benchmark and other findings are in [docs/capture-notes.md](docs/capture-notes.md).

## Next steps

- HUD readers (HP, MP, EXP) running on recorded frames
- Minimap-based localisation
- Name-tag detection for players
- Template matching for mobs
- Input injection, later
