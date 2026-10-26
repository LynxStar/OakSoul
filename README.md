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
--encoder P                x264 (default, readers stay exact), x264-420, hevc, h264vt, lossless
--ffmpeg-args "..."        raw encoder arguments instead of a preset (use with --ext)
--scale N                  capture at N times the point size and keep the bottom-right pixel of every
                           NxN block, so frames are the game's own pixels at point size (default:
                           the backing scale of the window's display, 2 on Retina; 1 = capture at
                           point size, which ScreenCaptureKit blurs on a Retina display)
--width N --height N       raw capture size in pixels, no decimation; excludes --scale above 1
--queue N                  frames buffered between capture and encoder (default 30)
--crop X,Y,W,H             capture only this region of the window, in points from its top-left
--crop-top N               drop N points from the top, e.g. 28 for a standard macOS title bar
```

A windowed CrossOver client carries a standard 28 point macOS title bar, so `--crop-top 28` yields frames of exactly the game's native size (1366x768 for a 1366x796 window). The crop is applied by ScreenCaptureKit before scaling, and `session.json` records it.

On a Retina display the compositor shows the game's 1x render as 2x2 blocks. By default the recorder asks ScreenCaptureKit for the region at that backing scale (2732x1536 for the crop above) and keeps the bottom-right pixel of every block as each frame arrives, so frames are still 1366x768 but hold the game's own pixels; colours come in sRGB rather than the display's Display P3. `--scale 1` captures at point size instead, which ScreenCaptureKit produces by blurring the 2x image, so use it only on a non-Retina display. The recordings made before this (`fx-combat`, `fx-maps`) are blurred and colour-shifted; [docs/capture-notes.md](docs/capture-notes.md) has the measurements.

Exact capture of a windowed client, and the old point-size capture:

```
dotnet run --project src/OakSoul.Recorder -- --pid 1234 --title "MapleStory" --crop-top 28
dotnet run --project src/OakSoul.Recorder -- --pid 1234 --title "MapleStory" --crop-top 28 --scale 1
```

## Encoder presets

| Preset | ffmpeg arguments | Container |
|---|---|---|
| `x264` (default) | `-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv444p` | mp4 |
| `hevc` | `-vf pad=... -c:v hevc_videotoolbox -q:v 65 -pix_fmt yuv420p -tag:v hvc1` | mp4 |
| `h264vt` | `-vf pad=... -c:v h264_videotoolbox -q:v 65 -pix_fmt yuv420p` | mp4 |
| `x264-420` | `-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p` | mp4 |
| `lossless` | `-c:v libx264rgb -preset veryfast -qp 0` | mkv |

With `--ffmpeg-args` the container defaults to mkv unless `--ext` is given. The default is `x264` because recordings double as test fixtures: re-encoding the lossless keyframes with each preset and running the readers on the decoded frames, `x264` at CRF 18 read all eleven frames exactly (glyph distance 2) while the hardware `hevc` preset at q65 read HP on five, MP on six and EXP on none, so use `hevc` only for long archival recordings where exact reads do not matter. Sizes are in [docs/capture-notes.md](docs/capture-notes.md).

## Session folder

Each run creates `recordings/<timestamp>-<title>/` (or the name given with `--name`):

| Path | Contents |
|---|---|
| `video.mp4` or `video.mkv` | Constant frame rate video from ffmpeg; `videoFrame` in `frames.jsonl` is -1 for frames left out of it |
| `keyframes/NNNNNNN.png` | Lossless PNG every N seconds; the number is the frame sequence |
| `frames.jsonl` | One JSON line per frame: `sequence`, `pts`, `wallClockUtc`, `videoFrame`, `keyframe` (path, only on keyframes) |
| `session.json` | Summary: window (with its display's scale), frame size, capture size requested from ScreenCaptureKit, scale, colour space, encoder, duration, delivered, dropped, idle, filled, skipped and cut counts, mean frame copy time, keyframe count, video bytes, ffmpeg exit code |
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

`MinimapReader.Read(geometry, frame)` returns the minimap panel as drawn, the map image inside it, the player's own dot and every other marker. The panel's top-left corner is fixed but its size follows the map, so the right edge is anchored on the orange WORLD button in the title bar and the bottom on the border colours down the left side; the map image sits at fixed insets inside. Dots are colour blobs: yellow is you, red other players, teal portals. `SelfInMap` gives your dot relative to the map image, which is what the server's minimap metadata will turn into world coordinates. About half a millisecond on a native frame. Collapsed, the panel is a 20-row bar reading "Region : Map" followed by the minus, plus and WORLD buttons; there the WORLD caption crosses the title-bar row, so the button is found on the rows above and below its caption instead, which gives the bar's true width (215 on fx-anttunnel, 284 on fx-maps' Perion Street Corner, where the reading used to stop at the minus button, 149 and 219) and `NameRight`, the column where the buttons start, 62 columns before the WORLD button's outline. The panel with its name block hidden but its map shown (fx-maps around 1:02) also reads as collapsed, since the name block is what the check looks for; its left border runs on past the bar, so it gets no `NameRight`.

`MapNameTracker` reads the region and map names (for example Dungeon and Ant Tunnel IV) through Apple's Vision text recognizer, exposed by the shim as `TextRecognizer`. It only re-reads when a coarse hash of the name block changes, so the roughly 30 ms OCR cost is paid on map change rather than per frame; the first call takes a few hundred milliseconds while Vision loads. Validated on all sixteen keyframes, windowed and fullscreen alike. On the collapsed bar the text is read up to `NameRight`, so neither the button captions nor the world beyond the bar (whose scrolling kept the hash from settling: fx-anttunnel's bar was read on its eighth keyframe) enter the crop; where the buttons were not found it falls back to a generous span and cuts the text at the WORLD caption, which OCR does not always read whole (`WORL`, `WORLO`, `WORLE` and `WORLI` on fx-anttunnel; any word after the first that is a prefix of WORLD or one letter off it, also glued to the numeral as in `IVWORL`). With `IsKnownName` set (the watcher checks the catalog), a pixel-exact frame whose softened name block reads as a name the catalog does not know is read once more unsoftened, and that reading is kept when the catalog knows it (see Exact frames). After each read `NameWidensPanel` says whether the name text reaches within 24 pixels of the panel's right edge, so that the panel may be as wide as it is for its name rather than its map (5 or 6 pixels on fx-maps' five such panels, 8 to 11 on Rocky Road, 53 or more elsewhere); the world tracker uses it to tell apart the maps a misread name may be.

**Exact frames.** The OCR readers were tuned on point-size captures, which ScreenCaptureKit blurs. On exact captures (`fx-exact`, and the `fx-2x` keyframes decimated) Vision misread the game's sharp pixel fonts: the status bar's LynxHope as LunxHope at every upscale from 4x to 10x, the map name as Deep Valley !, and on fx-2x the level badge as 3. So `OcrInput` softens every OCR crop of a pixel-exact frame with that same capture kernel before Vision sees it, which reads LynxHope and Deep Valley I on all 40 of those keyframes, and leaves blurred frames as they are. A frame counts as exact when the status bar's job and name text shows one-pixel strokes at full contrast (177 luma levels above the background on exact frames, at most 129 after the blur on every keyframe of fx-combat and fx-maps), so live captures and recordings of either kind need no flag. The level badge is also redrawn as black ink on white paper before OCR: its white digits on orange tiles read only at 8x before, and now read 31 at 6x, 8x and 10x on every keyframe of all four recordings (bar fx-maps' black transition frames). On an exact frame the badge is binarized as captured, not softened first: softened, LV. 32 read as "3 2" at 8x on every keyframe of fx-anttunnel and fx-anttunnel-open, which the parser then rejected, so the level stayed unknown; unsoftened it reads 32 at 6x, 8x and 10x there and 31 still at all three on fx-exact and fx-2x. The parser also joins the digits of all number lines right of the label, left to right, so "3 2" or separate "3" and "2" lines read as 32. Softening is still right for the map name block in general (Deep Valley I), but not always: Ant Tunnel IV's block on fx-anttunnel-open reads "Ant TunnelM" softened at the 3x upscale used, on every keyframe, and "Ant Tunnel IV" unsoftened, which is why an unknown softened name is read again unsoftened. Map names accept `!` as the numeral I (fx-maps itself had read East Rocky Mountain I as East Rocky Mountain !). The minimap reader finds the panel's right and bottom edges on both kinds of frame: on an exact frame the WORLD button's dark outline directly follows its orange run, and the bar's lowest light-blue row sits right on a dark line and passed for the white bottom line; before that fix the panel read as missing or up to 440 wide, so the names were never read. On all 20 fx-exact keyframes the panel now reads 191x220 as on fx-combat, and from the second keyframe on (the first is the stability gate) the level reads 31, the name LynxHope and the map Warning Street / Deep Valley I with its id 10004100. HP, MP and EXP already matched their glyphs at distance 0 on exact frames, since the glyphs are binarized. The readings of fx-combat and fx-maps are unchanged apart from that one map name.

### Character, quickslot and buffs

`NameTagLocator` finds your own character in the world viewport by its name tag. The client renders a tag as light text on a translucent dark box, identically every frame, so the locator learns a binarized template once, from a frame where Vision OCR sees your name on its own (the name is the one given to the watcher with `--name`, the status bar only being checked against it), and then matches by pixel signature: nearly all text pixels bright, nearly all gap pixels dark, judged as a combined fraction so a bright background behind the box still matches. It searches near the last hit first and falls back to the whole viewport; about 20 ms per frame on misses, far less while tracking. On the combat fixture it placed the tag on three quarters of the keyframes, and the misses are frames where another tag or a mob overlaps yours. `NameTagHit.Feet` is the point just above the tag, which is where the character stands. The OCR line may differ from the status bar's name by one substituted character (Vision reads y as u and p as n on this font; a line with a character more or less is not taken, since the template is cut from its box), and edge columns that cross the box's dark margin rows above and below the text are cut from the template, so world art beside the box does not join it. On an exact frame an unhidden tag matches the template pixel for pixel, so when the strict match fails there, the tag is looked for within 128 pixels of where it was last seen by the share of its text columns that match exactly: a tag under a lightning bolt, a rope with the pet's tag, or the pet itself kept 53 to 88% of them on fx-exact, nothing else in the viewport (the tag one pixel off included) more than 33%, and 45% is required. That search runs before the whole-viewport one and finds a hidden tag in 15 to 30 ms; when the tag is gone it adds about 15 ms to the miss. Blurred frames keep the strict match alone, since soft edges cost an unhidden tag up to 30% of its columns there. On fx-exact the feet are known on 17 of 20 keyframes; of the other three, the first comes before the name is read, on the second OCR read the tag as LynxHopell (learned one keyframe later), and on the third the tag is under the chat bar.

The name can appear elsewhere on screen: the party window lists LynxHope as dark text on a grey panel, and on fx-anttunnel it was the only line reading the name on the keyframe where the name became known, so the template was learned there (16x19 of lit cave art, feet at the top right). So `TryLearn` cuts a template from every line reading the name and keeps only those shaped like a tag: a text band 6 to 12 rows high (11 for LynxHope, ascender to descender; 10 or 11 on the blurred fixtures), light text at least three quarters of the OCR line's width, the box's dark margin in the two rows above and the two below the band across at least 90% of its columns, and at least half the band dark. The party window's lines failed on band height (13 to 21 rows) and margin (at most 0.87) on all eleven keyframes where OCR read the name there on fx-anttunnel and fx-anttunnel-open, while the 155 tags accepted on all five fixtures (one per keyframe at most) had bands of 9 to 12 rows, margins of 0.93 or more and bands at least 0.64 dark. Of several qualifying lines, exact readings first, the one nearest where the camera keeps a resting character (tag top centre at 683, 491) wins. Edge columns lit only outside the text band are trimmed too (the pet's tag next to the own one made fx-anttunnel-open's template 58 wide; now 54). A template that never matches on any frame after the one it was cut from is forgotten after six misses and its place is not learned from again (the watcher prints `tag forgot ...`); one that matches once is kept for good.

`QuickslotReader` reads the eight cells: which key, whether the cell holds anything (an empty cell is flat grey in the middle), a fingerprint of the icon, and a fingerprint of the count corner. The item counts themselves are tiny anti-aliased digits drawn over the icon art at the same brightness as the art, and no thresholding separates them, so they are not read as numbers; instead `CountChanges` reports the cells whose count corner changed since the last frame. On the combat fixture those events line up exactly with the potions used. A bot that knows a starting count can count down from the events.

`BuffReader` reads the buff row at the top-right. A slot is occupied when it correlates with a buff icon the reader has been taught (`Learn`, `Save`, `Load`; the watcher loads `recordings/known-buffs.json` if present), compared outside the corner that shows the remaining minutes. Correlation rather than difference, because the client draws the icon faded on roughly a third of frames, reported as `Faded`. The minute digit is not read yet, and the slot pitch has only been seen with one buff active.

### Live watcher

`OakSoul.Watch` runs the readers on a live capture and prints a line whenever a value changes:

```
dotnet run --project src/OakSoul.Watch -- --pid 1234 --title "MapleStory" --crop-top 28
```

It takes the same window selection as the recorder plus `--name`, `--crop-top`, `--fps`, `--seconds` and `--scale`. Like the recorder it captures exactly: the region is requested at the backing scale of the window's display (2 on Retina) and each frame decimated to the game's own pixels in sRGB, so the readers see what the game drew; `--scale N` overrides the factor, and `--scale 1` captures at point size, which ScreenCaptureKit blurs on a Retina display (the readers handle both, see Readers). Each line shows level, job and name, HP with its percentage, MP, EXP total and percent, region and map name, your minimap position and the number of other players on the minimap, your feet position from the name tag, the occupied quickslot keys and the active buffs (a `~` marks a faded icon), with a `*` after each field that changed. Quickslot count changes print as their own lines. Every ten seconds a heartbeat reports frames, frame rate, reader time per frame and dropped frames; the readers cost well under a millisecond per native frame. If the frame does not match the native layout, for example a windowed client captured with its title bar, it says so and suggests `--crop-top 28`.

**The character's name.** The watcher does not discover who is playing; the operator provides the name with `--name NAME` (default `LynxHope`, the only character so far) and it prints `name     LynxHope` among its startup lines. The name tag locator is created from the first frame with that name, instead of waiting for the status bar to be read, and the same name is the own name for entity tracking. The status bar is only checked against it: the name line is reported as the configured name when OCR reads it ignoring case or one substituted character off (Vision's slips on this font), and as read otherwise, so a different character logged in shows in the lines; the first time it differs the watcher prints `warning: status bar reads 'X', expected 'LynxHope'` once. Replayed on the five fixtures the readings are the same as when the name came from the status bar, except that the tag is learned on the first keyframe of fx-exact, fx-anttunnel and fx-anttunnel-open (it was the third), so their first two keyframes now show a feet position and, where the world model runs, a camera placed with it; fx-combat and fx-maps learn it on the same keyframe as before and read identically. With `--name Nobody` on fx-exact no tag is learned and the warning prints once.

### Replaying recordings

The watcher takes a recorder session folder instead of a window, so the whole pipeline after capture runs on real game samples with the game closed:

```
dotnet run --project src/OakSoul.Watch -- --recording recordings/<session> --keyframes --crop-top 28 --speed 0
```

`--keyframes` replays the sparse PNG keyframes, which are lossless copies of the captured frames; without it the video is replayed frame by frame, which is only as faithful as its preset (the entity detector therefore matches sprites on a replayed video with its tolerant path, even for an exact recording; see Sprite verification). `--speed 1` paces playback at the recorded rate, `0` runs as fast as frames decode. `--crop-top` applies to replay exactly as it would have at capture time, for sessions recorded with the title bar. Under the hood every consumer takes an `IFrameSource`, with `CaptureFrameSource` for live windows and `RecordingFrameSource` for sessions.

### Fixture recordings

Recordings under `recordings/` (gitignored) are the test fixtures. `fx-combat` and `fx-maps` were captured at point size, blurred and in Display P3; `fx-exact` is ten seconds of exact capture on Deep Valley I (20 keyframes, `scale` 2 and `colorSpace` sRGB in its `session.json`), the fixture for the readers on exact frames. `fx-anttunnel` (5 s, 10 keyframes every half second) and `fx-anttunnel-open` (5 s, 11 keyframes) are exact captures on Dungeon / Ant Tunnel IV (10005063) at level 32, with a party window open at the top right listing LynxHope, and the minimap collapsed in the first (chat expanded over the character on keyframes 4 to 7) and expanded in the second; they found the level badge read as "3 2", the party window taken for the name tag, the WORLD caption read as "WORL", and Ant Tunnel IV's name read as "Ant TunnelM" and resolved to Ant Tunnel I. The first multi-map one, `fx-maps`, is a 197-second walk from Perion to Ellinia with a keyframe every second: 189 lossless frames across ten maps. Replayed through the watcher it yields a complete, correctly spelled map timeline, stable per-map minimap panel sizes (the client resizes the panel per map and sometimes within one), the collapsed-minimap state, and unknown readings only on map-transition frames. Each new fixture has found something: the hazy Perion sky defeating the old panel-bottom scan, the collapsed minimap, roman numerals read as "IIl", and the bold W in "Wizard".

## Map metadata from the client

`src/OakSoul.Wz` is a read-only reader for the client's WZ archives (the PKG1 format: header, version hash, encrypted offsets, property trees), and `src/OakSoul.MapExport` uses it to export every map's metadata to JSON:

```
dotnet run --project src/OakSoul.MapExport -- --list
dotnet run --project src/OakSoul.MapExport -- --out data/maps
```

The archives are opened read-only in place, under the client's Wine bottle, and are never copied into the repository; the export lands in `data/maps/` (gitignored), one `<mapId>.json` per map plus `index.json`, with view bounds, minimap parameters (`centerX`, `centerY`, `mag`, size), footholds with their links, ladders and ropes, portals with targets, and life spawns. Re-run it after a client update to stay in sync. The whole client, 336 maps, exports in under half a second. Findings from the first run: this client renumbers maps (Henesys is 10001000, Ellinia 10002000, Perion 10004000), every archive is version 779 with strings masked but not keyed, the minimap bitmaps live in separate `_Canvas` archives while the numbers sit in the map image, and `--dump <id>` prints a map's raw property tree.

## World model

`src/OakSoul.World` turns the readers' output into world coordinates using the exported map metadata. `MapCatalog` loads `data/maps` by id or by the street and map names the HUD shows; `FootholdIndex` answers which foothold lies under a point, which one the character stands on, which ladder or rope it is on, where the foothold chain leads, and the nearest portal; `MinimapTransform` converts the own dot to world and back; `CameraModel` holds the clamping and anchor rules; `WorldTracker` combines dot, name tag and metadata per frame into the map id, world position, camera, foothold or rope, and a confidence.

What the fixtures settled, with the measurements in the code comments:

- **Minimap.** The HUD draws the minimap canvas at a fixed quarter size, 16 world units per pixel (4 x mag), not stretched or fitted to the panel. The visible image is the reader's `MapArea` moved one column left and two rows down, 2 wider and 5 shorter. A map narrower than the panel (which widens for a long name) is centred; a map taller than the panel allows (Perion, Ellinia, East Domain of Perion, The Forest North of Ellinia: 112 rows shown) is a window centred on the player and clamped to the image, so on those maps the dot gives no height while the window is mid-map; the tracker then places the window from the portal markers, follows the foothold chain, or holds the camera. The own dot is drawn at the floor of its 16-unit cell, 6 pixels up, so the dot alone is good to +-8 units.
- **Camera.** It clamps to vr with the full 1366x768 frame as the view (not the 702-row world viewport). The tag's `Feet` point sits 4 pixels below the soles. Unclamped, the camera eases towards the player with a dead zone: resting feet sit around x 683 (655 to 733) and y 490, and while walking the feet run about 122 pixels ahead of centre.
- **Map changes.** The names lag the new panel by the OCR stability gate, so the tracker treats a change in the panel size as "map unknown" until the names change.
- **Misread names.** A reading that matches no map exactly is never resolved to its closest name alone: Ant Tunnel IV read as "Ant TunnelM" is closest to Ant Tunnel I. `MapCatalog.FindCandidates` returns every map within two edits (street and map together) and every map whose name before its last word starts the reading (all "Ant Tunnel *"), and the tracker takes the one candidate whose predicted image explains the minimap (`MinimapTransform.StrictSizeMismatch` within 1.5 pixels and better than every other): an image wider than the area never fits, a narrower one only when the name may have widened the panel (`NameWidensPanel`), a taller one only through the 112-row window. Ant Tunnel IV's 219x172 image explains fx-anttunnel-open's 219x112 area exactly, while Ant Tunnel I, II and III are 17, 39 and 6 pixels off; fx-maps' "Rocky RoadI" and "The Tree That GrewI" still resolve, and East Rocky Mountain I and IV, which share a size, are left unknown. With the minimap collapsed or no candidate fitting the map stays unknown and is tried again on later frames.

`OakSoul.Watch` loads `data/maps` (or `--maps DIR`) and adds the map id, world position, foothold or rope, and camera to each line; `--world-report` prints the validation at the end of a replay (minimap model per map scored against portal markers, foothold agreement, camera anchor and bounds, self-consistency, and the portals used at each map change):

```
dotnet run --project src/OakSoul.Watch -- --recording recordings/fx-combat --keyframes --speed 0 --world-report
```

## Entities: mobs, players, pets, drops

`src/OakSoul.Detect` finds what is drawn over a map and tracks it. Foreground is measured against a background learned from recordings, then verified and identified against the client's own mob frames (`data/mobinfo`, from `OakSoul.MobExport --sprites`) and drop icons (`data/sprites`, from `OakSoul.SpriteExport --items`). The project depends on Vision and World (which stay independent of each other).

Build the background of every map a session visited (offline, cached in `data/backgrounds/`, gitignored; sessions of exact frames in `data/backgrounds/exact/`, see Exact backgrounds below), then run the detector on the replay:

```
dotnet run --project src/OakSoul.Watch -- --recording recordings/fx-combat --keyframes --speed 0 --build-background
dotnet run -c Release --project src/OakSoul.Watch -- --recording recordings/fx-combat --keyframes --speed 0 --entity-report --entity-sheet data/entity-sheets/fx-combat
```

`--entities` prints a line whenever the counts by kind or the nearest mob change (`entities mob 5 player 0 pet 1 drop 0 effect 3 unknown 2  nearest mob #20 'Lv. 23 Dark Axe Stump' at 96 units`), `--entity-report` (implies `--entities`) prints the validation below, `--entity-sheet DIR` writes two contact sheets of twelve evenly spaced annotated frames (keep DIR under `data/` or `recordings/`), and `--backgrounds DIR`, `--mobinfo DIR` and `--sprites DIR` move the caches. Sheet labels name the mob, animation, frame and facing (`#116 Dark Axe Stump move/1R`; `~` when the identity is carried by the track rather than matched in that frame, `Mob?` for a mob nothing verified). For a closer look, `OAKSOUL_DEBUG_DUMP=dir OAKSOUL_DEBUG_FRAMES=16,200` writes full-size overlays of those frames with the foreground classes tinted, and `OAKSOUL_SPRITE_DUMP=dir` (with `--entity-report`) writes, for every tagged or sprite-matched candidate, its surroundings beside the best frame and their difference at 3x (`OAKSOUL_SPRITE_DUMP_UNMATCHED=1` adds the large unmatched ones on footholds); `OAKSOUL_SPRITE_CSV=file` (with `--entity-report`) writes every scored candidate's best placement per species, and `OAKSOUL_TAG_DUMP=dir` every tag crop sent to OCR, as captured.

- **Background mosaic** (`MosaicBuilder`, `BackgroundMosaic`). Frames with a measured camera (dot and own tag) are stitched in world space over the map's vr box with the HUD regions, the minimap panel, the top rows of the chat bar and a box around the own character masked out. Each frame is first registered to the pixel against the mosaic so far (the dot gives x only to +-8), then casts a streaming majority vote per pixel; a second pass counts how many camera positions agreed with the voted value (the background under steam or a name-tag box agrees too). The whole thing runs twice, the second round registering against the first round's reliable pixels. The mosaic is anchored by a frame whose camera sits on a map bound (x exact), or, on a map where none does, by the minimap dot over all frames (see Exact backgrounds). Cached as `<mapId>.png` (RGBA), `<mapId>.mask.png` (disagreement, 0..253; 254 too few samples; 255 unseen) and `<mapId>.json`.
- **Per frame** (`EntityDetector`). Register the frame against the mosaic (narrow search around a measured camera; otherwise a coarse search of 4x4 block means over +-64 pixels, then a fine one), difference it, classify each differing pixel as solid, a darkening (name-tag box), a whitening (steam, clouds) or bright text, open 3x3 and label components. Pixels the mosaic cannot vouch for fall back to a screen-space model while the camera rests (parallax layers hold still on screen). Name-tag boxes are found as 16-row stacks of darkened background with near-white text, fingerprinted and OCR'd once per distinct appearance, and attached to the blob standing on them. Candidates get their feet (from the tag when there is one), world position and foothold, then a nearest-neighbour tracker with a one-second memory assigns track ids, ages, velocities and kinds.
- **Kinds.** A sprite match (now or earlier on the track) makes a Mob and a drop icon a Drop, decisively; otherwise a level badge or a known mob name makes a Mob; a plain name is the Pet when it is the name read repeatedly beside the own character, else a Player; without a tag, feet on a foothold make a Mob (or a Drop when small, static and unchanging), floating short-lived blobs are Effects (damage numbers, spells), the rest Unknown. The tracker learns mob names and levels from the tags and cleans later reads against them (`Dark Axe Stu`, `Fire BMara` with the pet's tag run into it).
- **API.** `EntityDetector.Detect(frame, width, DetectionContext)` returns an `EntityFrame` with `DetectedEntity` records: kind, confidence, screen box, feet, world position, foothold id, tag text, level and name, track id, age, first frame, velocity, and for mobs a `MobIdentity` (client id, name, facing, animation, frame, score, whether matched in this frame) with the `SpriteMatch`, for drops the `DropMatch` (item id).

What fx-combat (403 keyframes in Deep Valley I) settled in the first milestone, before sprite verification:

- **Mob tags are drawn only on mobs that were hit**, together with an HP bar above them, wherever they are (a Fire Boar hit from 300 pixels away had one), and are gone again some time later (not measured); mobs standing right next to the character but not hit have none, so they are neither always drawn nor proximity-based. So they are a strong classifier but a rare one: 7% of mob detections and 22% of mob tracks carried a readable tag. The badge reads as `Lv. 32`, `Lv, 23`, `LV.25`, `Lw, 11` or just `23`; the mobs seen were Lv. 23 Dark Axe Stump, Lv. 32 Fire Boar, Lv. 25 Wild Boar, Lv. 11 Dark Stump and Snail (its badge never read); also the pet Mara and one other player, MrSoySauce. Pets and players have the same box without a badge.
- **Mosaic.** The vr box is 2906x2189 world units; 83.1% was seen, 77.7% often enough from shifted viewpoints, and 41.4% of that is unreliable at 25% disagreement (parallax cliffs and clouds, the waterfall, steam, mob walkways); 69.8% is usable for foreground at 45%. Registration moved the tracker's camera by up to 19 x and 22 y and raised the exact-match fraction from 0.14 to 0.88.
- **Validation** (`--entity-report`, label-free): 387 of 402 frames registered; per frame on average 3.6 mobs, 0.35 pets, 0.14 players, 0.02 drops, 4.2 effects, 3.8 unknown; 84% of mob detections have their feet within 6 units of a foothold (98% of the tagged ones); mob tracks run a median of 5 frames (p90 17). Of the 50 EXP gains, 84% have a mob track ending within 450 units from three keyframes before to one after, against 64% for an arbitrary frame; for hit mobs (tag or HP bar seen) 38% against 29%. Drops barely occur: the pet loots them within a second.
- **Cost.** 11.6 ms per frame on average (median 10.1, p90 15.6; Release, native keyframes, OCR excluded), of which registration 3.9, difference 4.7, tags 2.0; the frames needing the wide registration search make the tail.
- **Limits.** Mobs in front of parallax background are only seen while the camera rests, and a mob idling in one spot for seconds is absorbed by the screen-space model; the mosaic knows only what the recordings showed, so a map visited briefly is mostly unreliable; tags drawn over each other OCR as one; the HP bar is seldom separable from the sprite; drop classification is untested on this fixture.

### Sprite verification and identity

- **Templates per map** (`MobLibrary`, `MapMobSet`). From the map's `life` list: the distinct mob ids with their spawn counts, the floors their spawns stand on (a floor is a connected component of the foothold prev/next graph, `FootholdIndex.FloorOf`), their mobility and every frame of every animation in both facings (the stored frames face left; right is the mirror). Linked mobs share their target's frames, in-mob frame links are loaded once, and the Blue Snail's `revive` puff is left out (it matched steam). Built once per map.
- **The client draws the exported frames 1:1.** On exact captures a mob on screen is its WZ frame, pixel for pixel, at an integer position: an exhaustive search of the fx-exact and fx-anttunnel-open keyframes for every frame of every species found 59 and 27 whole mobs that agree on every visible opaque pixel (Red Snail 1026/1026, Axe Stump 3047/3047, Zombie Mushroom 2951/2951), with no colour conversion. The mob art seen so far has 4 bits per channel (every level a multiple of 17). Only the point-size recordings (fx-combat, fx-maps) differ, because that capture blurred and colour-shifted the game's pixels (docs/capture-notes.md, Exactness).
- **Two ways of comparing, chosen per frame.** A frame counts as exact when `OcrInput.IsPixelExact` sees one-pixel strokes in the status bar (the watcher sets the flag once from the first such frame; a blurred frame never shows them). Exact frames are scored strictly unless they were decoded from a lossy video (`--recording` without `--keyframes`); blurred frames and video frames take the tolerant 3x3 path, as before.
- **Strict path** (`SpriteMatcher`, exact keyframes and live frames). Every fully opaque pixel of the frame (alpha 255; anti-aliased edges blend with whatever is behind) is compared with the screen pixel under it, and a placement scores the share of its visible opaque pixels that agree (at most 512 per placement, a spread subset). The tolerance is 0: every whole mob the exhaustive search found agrees exactly, and the x264 video, which moves a sprite pixel by a median of 9 levels in its worst channel (p90 21, p99 38), would need 24 per channel to keep 93% of them, which is not a strict comparison any more. Pixels another sprite, a tag, a damage number or a spell covers simply disagree, so a partly hidden mob scores the share that shows, and a frame pixel whose colour equals the background behind it (the mosaic at the registered camera) is left out, since the screen shows that colour with or without the mob: on Ant Tunnel IV's black floor the black outlines of a misplaced Horny Mushroom had agreed on 41 of 512 pixels. The placement search stays integer: every offset of the +-6 x +-4 window is tried on 8 spread pixels (an exact match has a one-pixel peak a grid could step over), the 6 best on 32, and the 2 best of those are scored in full; a frame whose coarse agreement is under half the score needed is dropped there. A box may be up to half the frame's extent on an exact frame (70% larger on a blurred one), because a mob that stood still through most of a short recording is partly baked into its mosaic and shows only where it has moved since. On an exact frame the remaining frames of the species that passed are scored too and the best wins, since a neighbouring frame of the same animation shares most pixels.
- **Colour states, refitted on exact frames.** The freeze is a per-channel multiply by (255, 168, 10) / 255 in B, G, R, rounded: on fx-anttunnel-open's frozen Zombie Mushroom every art level of every channel mapped to exactly one screen level (G 85 to 56, 255 to 168; R 85 to 3, 255 to 10; B unchanged), 3161 of 3161 pixels (the blurred fit had been (1.0, 0.67, 0.30)). Steam is a light translucent layer whose alpha varies across the mob (0.04 to 0.59 over the steamed Axe Stump on fx-exact's keyframe 256, the same in all three channels to within rounding), so the old single alpha per sprite is gone: a pixel agrees through steam when it is no darker than the art, one alpha between 0.03 and 0.75 explains all three channels to within 6 levels (the layer is a light grey-blue, not pure white), the art pixel is coloured (grey outlines fit any lighter grey, which spells and damage numbers supply) and its right or lower neighbour fits with an alpha within 0.1. The hit flash is not in the data: no candidate on the exact keyframes, the hit Lv. 17 Axe Stump and the hit, frozen Zombie Mushrooms with HP bars included, shows a uniform brightening on more than a tenth of its pixels (at most 9.3%, on spell effects), and the hit mobs matched as drawn or frozen, so the strict path has no flash.
- **Threshold 0.2.** Every candidate of the two exact fixtures was scored against every species (`--entity-report`) and joined with the exhaustive search: candidates lying on no mob reached at most 0.094 (effects 0.094, unknown blobs 0.092, the tagged candidates of the pet and the other players 0.078; on Ant Tunnel IV, with Slivan, Oakofoami, Black Kitty and Mara in view, 0.052), while candidates on a mob scored 0.57 to 1.00 on fx-exact (21) and 0.25 to 1.00 on fx-anttunnel-open (5), the two frozen ones 0.57 and 0.97, and a frozen Zombie Mushroom three quarters under a lightning bolt 0.17 to 0.25. 0.2 is twice the best non-mob. Tag-labelled mobs are too few on these short fixtures to give a distribution (one Lv. 17 Axe Stump, matched).
- **Tolerant path** (blurred and video frames). 3x3 means of the frame's interior (all nine pixels opaque, at most 400 spread samples) against 3x3 means of the screen: a sample agrees when the B, G and R differences of the means sum to at most 45. The freeze as fitted on the blurred frames, (1.0, 0.67, 0.30), a hit flash of up to 80 levels and one steam alpha up to 0.7 per sprite are fitted per placement, plain and frozen. Threshold 0.65: scored against every species, fx-combat's pet, player and effect candidates reach 0.6 in 0.2% of cases and 0.7 in 0.1%, while tag-labelled mobs reach 0.6 for their own species in 69% and 0.7 in 58%. The tolerance was swept: 30 matched 21% of mob detections and called the single Axe Stump spawn two Axe Stumps on 10 frames, 45 matched 34% and never exceeded a spawn count, 60 matched 37% but let wrong species reach 0.7 on 41% of the tag-labelled candidates instead of 25%. An earlier variant modelled the point-size capture's blur as a calibrated separable kernel; it is gone, and the 3x3 means it replaced give fx-combat the numbers below.
- **Search and contest.** Each candidate's floor is the foothold under its feet or the nearest surface within 16 units. Species are tried in order: a tag read on the candidate or its track names the only ones; otherwise the species with a spawn on that floor, then jumpers by the vertical distance to their spawns, then flyers (floor-bound species never off their floors); within a group more spawns first, and the track's species first. A frame is placed with its visible extent on the box (bottoms and centres aligned; for a box at least 60 wide that fits nothing, on its left or right edge, and the rest becomes a new candidate). Frames whose extent is more than 30% smaller than the box are skipped. Without flyers on the map, candidates with no surface within 90 units below are not tried. The first frame to pass names a shape, not yet a species: recolours (Stump and Dark Stump, Axe Stump and Dark Axe Stump, the snails) share every frame's shape, and a Dark Stump's body is a Dark Axe Stump's without the axe (0.85 of the Dark Axe Stump frame on fx-exact). So every other species is scored too and the winner is the best score times the share of the blob's foreground its silhouette covers, plus 0.05 for the track's species, minus 0.03 per factor e of fewer spawns. The spawn counts also cap how many mobs of a species one frame may show.
- **Identity in tracking.** A match moves the candidate's feet to the frame's origin and votes for the track's species; association adds 40 units of bonus for the same species and 60 of penalty for another (a track matched twice never takes another species); identified tracks are remembered for 5 s instead of 1.2, but past 1.2 s only a candidate matched to the same species takes them up again, so a killed mob's track cannot grab what appears next to it. `EntityTracker.Hint` gives the matcher the likely track before association.
- **Drops** (`DropMatcher`). Small candidates on a surface are compared with every `iconRaw` up to 40 pixels (1022 icons, 15 of them meso coins) whose extent fits the box to 3 pixels, over +-2 x +-3, the same way as mobs (strictly on exact frames, without the freeze). Exact frames need 0.8: fx-exact's one drop on the ground (item 04000004) matched at 1.00 on all four keyframes, and no other small candidate on a surface of either exact fixture reached 0.20. Blurred frames need 0.9 (meso coins 0.8: gold coins scored 0.83 to 0.88 and a bronze coin beside a snail 0.83, all confirmed by eye, while no other small candidate reached 0.79). A track whose icon matched stays a drop (coins spin, which the appearance test took for a mob). Equipment icons are not exported, so equipment drops stay unidentified.
- **Tags on exact frames.** `TagDetector` OCRs a tag crop of an exact frame binarized, light text as black ink on white paper (`OcrInput.InkOnPaper`, as for the level badge), not softened like the status bar and the map name: on fx-exact's five crops of the pet's tag Vision read Mara on 2 as captured (lara and a cut-off Ma on the others), on none softened (empty or "BJBI") and on 3 binarized, and the mob tag as "Lw, 17", "Lw, 17" and "Lv. 17 Axe Stump"; fx-anttunnel-open's player and pet tags (Slivan, Black Kitty) read alike all three ways. Its Zombie Mushroom tags were not found as tag boxes at all (drawn over the black floor, beside Mara's tag). Blurred frames are OCR'd as captured, as before.
- **Exact backgrounds.** The mosaic cache is keyed by exactness: a session whose frames are exact builds into `data/backgrounds/exact/` (`BackgroundMosaic.DirFor`; `--backgrounds DIR` moves the whole cache), and the detector loads from there for exact frames, so the blurred mosaics fx-combat and fx-maps need are never overwritten. Neither exact fixture has a camera on a map bound, which the build used to require to anchor the mosaic: now the first frame anchors it provisionally, and the minimap dot fixes the offset from all frames afterwards (the tracker's camera lies within half a 16-unit dot cell of the true one; the offset is where one cell fits every frame's difference to its registered camera, outliers a cell off dropped), once after each round. Deep Valley I's exact mosaic agrees with fx-combat's mosaic, which a bound anchored, to one pixel (the softened exact mosaic matches the blurred one on 97% of reliable pixels shifted one column, 83% unshifted). Ten seconds are enough when built from the video: fx-exact's 20 keyframes are half a second apart and the camera moves up to 80 pixels between them, so only 4 of 18 could be registered incrementally, while the video's 298 frames voted 263; the 18 keyframes then replaced 2.2 M voted pixels with their lossless values (`MosaicBuilder.ExactRefine`; the video's own values sit 7 levels off and more at edges, which the foreground threshold saw as blobs). That gives 34% of Deep Valley I's vr box seen, 1.28 M world units usable for foreground, x anchored -6 (the dot allows -6..-5) and nothing left after the second round. Ant Tunnel IV is built from fx-anttunnel-open's keyframes: its camera rests within 23 pixels for five seconds, so the video taught the parallax cave walls as background and they came back as 43 effect blobs per frame (12.5 with the keyframe mosaic); 10 keyframes vote, 9% of the vr box is seen (0.62 M usable), and the dot puts x at +6 (4..7 over 10 frames; the video build said 6..7 over 114). Registration of the exact keyframes matches 0.89 of the samples at the median on both. A mob or a drop that stood still through most of a 5 to 10 second recording is part of the mosaic (Deep Valley I's Dark Stump and Axe Stump by the crosses, a Red Snail, a shell; Ant Tunnel IV's Zombie Mushrooms on the upper ledges and the coins on the floor) and shows only where it moved since.

```
dotnet run -c Release --project src/OakSoul.Watch -- --recording recordings/fx-exact --speed 0 --build-background
dotnet run -c Release --project src/OakSoul.Watch -- --recording recordings/fx-anttunnel-open --keyframes --speed 0 --build-background
dotnet run -c Release --project src/OakSoul.Watch -- --recording recordings/fx-exact --keyframes --speed 0 --entity-report --entity-sheet data/entity-sheets/fx-exact
```

- **Camera feedback.** A registered camera is pixel-exact, so `WorldTracker.Refine` makes it the held camera and the own position the camera plus the tag's soles; the dot gives x only to +-8 units.

What the exact fixtures settled (`--entity-report`, Release, keyframes, strict), next to fx-combat (tolerant):

| | fx-exact (Deep Valley I) | fx-anttunnel-open (Ant Tunnel IV) | fx-combat (Deep Valley I) |
|---|---|---|---|
| keyframes with a background, registered | 19, 18 | 10, 10 | 402, 387 |
| mob detections per frame | 1.7 | 2.2 | 4.0 |
| matched a sprite in their frame | 22/31 = 71% | 7/22 = 32% | 533/1529 = 35% |
| carry an identity | 77% | 36% | 64% |
| species matched / with identity (spawns, most in one frame) | Red Snail 20/22 (15, 3), Axe Stump 2/2 (1, 1) | Zombie Mushroom 7/8 (23, 1), 2 of the 7 frozen | 7 species, see below |
| a species over its spawn count | never | never | never |
| matched feet on a foothold (sprite origin), median distance | 100%, 0.2 units | 100%, 0.3 | 98%, 0.2 |
| pet, player or effect at or above the threshold | none (best 0.094) | none (best 0.052) | 0.2% at 0.6 |
| whole mobs on the keyframes (exhaustive search) / candidates on them / accepted | 59 / 21 / 21 | 27 / 5 / 5 | n/a |
| EXP gains, found as an identified track ending | 2 (Red Snail, 8 each), 1 | 2 (23 and 24, party-shared, no species' exp), 0 | 50, 17 of the right species |
| drops | 1 track, item 04000004 at 1.00 | none | 7 tracks, 5 meso |
| cost per frame, median / p90 / mean (ms) | 17.8 / 22.6 / 20.2 | 18.7 / 31.0 / 23.3 | 15.0 / 21.4 / 16.0 |
| of which sprites, median / mean | 2.9 / 4.4 | 2.6 / 4.1 | 2.6 / 3.2 |

On the exact fixtures 15 of 21 accepted Deep Valley I matches agree on every visible opaque pixel, one needed steam; on Ant Tunnel IV 2 of 7 are frozen. Recall is limited by the short recordings' mosaics rather than by the matcher: every candidate that landed on a mob the exhaustive search found was accepted, but most whole mobs never became candidates (baked into the mosaic, or on background the mosaic cannot vouch for). The cost is over the 15 ms budget by registration on fx-exact (7.2 ms; 5 of 18 frames needed the wide search as the camera jumped and climbed) and by tags on Ant Tunnel IV (5.4 ms; the black cave yields many tag-box candidates); the sprite stage itself stays near 3 ms at the median. Replayed from its video, fx-exact goes through the tolerant path with the exact mosaic: 37% of mob detections matched in their frame, 69% carry an identity.

What fx-combat settled with sprites (`--entity-report`, Release, keyframes, tolerant path):

- **Verification.** 35% of mob detections matched a sprite in their frame and 64% carry an identity (matched now or earlier on the track); of all 4613 blob candidates 11.5% matched. Unverified mob detections: 482 matched no frame (steam, merged sprites, hit and death poses, partial blobs), 439 carry their track's identity, 75 are tags with no or a tagged blob. Matched mobs stand on a foothold in 98% of cases with the feet at the sprite's origin (96% at the box bottom), median distance 0.2 units.
- **Species.** Red Snail 226 matched detections (15 spawns, at most 4 in one frame), Dark Axe Stump 145 (8, 3), Wild Boar 60 (5, 2), Dark Stump 44 (6, 2), Fire Boar 34 (4, 4), Blue Snail 24 (1, 1), Axe Stump 0 (1): no frame shows more of a species than it has spawns. Tag-labelled candidates were accepted as the tag's species 21 times, as another species never.
- **Tracks.** Mob tracks with an identity run a median of 8 keyframes (p90 22, max 56; 4.6 s), those without one 3; a random mob sighting belongs to a track of median 12 keyframes (p90 40). 10 of 107 identified tracks are followed by a new track of the same species within 4 keyframes and 150 units. All mob tracks together still have a median of 5, because unverified foreground on footholds (steam edges, the pet, parallax) makes many short ones.
- **Kills.** Every EXP gain on fx-combat equals one species' client `exp` (Red Snail 8, Dark Stump 21, Axe Stump 32, Dark Axe Stump 43, Wild Boar 46, Fire Boar 60, Blue Snail 4), so the gains label the kills: for 34% of the 50 kills an identified track of that very species ended within 450 units from three keyframes before to one after (9.5% for an arbitrary frame), for 24% only another species' did, for 42% none. Fire Boars were found for 5 of 6 kills, Red Snails for 2 of 12. Any mob track ending: 88% against 74% by chance.
- **Drops.** 7 drop tracks, 5 identified as meso (3 bronze, 2 gold coins), 4 of the 7 starting from a keyframe before to two after an EXP gain; median lifetime 2.6 s (the pet loots).
- **Camera feedback.** Over 224 frames with dot, tag and registration near a surface, the unsnapped dot-and-tag position sat a median 5.6 units off the surface (3% exact); the registered camera plus the tag puts it on the surface in 92% (the tracker's own snapping to the dot's cell gets 96%, but cannot see the x error); the feedback moved the own x by a median 3 units, p90 8, max 25.
- **Cost.** 15.0 ms per frame at the median (mean 16.0, p90 21.4; OCR and report diagnostics excluded; about 1 ms of noise between runs): registration 4.2, difference 4.9, tags 2.2, components 0.9, sprites 3.2 (median 2.6), drops 0.5, tracking 0.2.
- **fx-maps.** Its mob maps were visited for a few keyframes each, so only Rocky Road II and III got usable backgrounds (East Rocky Mountain I, Rocky Road I, East Domain of Perion and the Ellinia fields could not be anchored or have no reliable pixels; `--build-background` skips such maps). On the 17 frames that registered, 4 mob detections matched (3 Red Snails, 1 Axe Stump) and no pet, player or effect candidate scored above 0.45.
- **Limits.** Steam over Deep Valley I hides colour on blurred frames; merged crowds split only into two; hit and death poses rarely match (3 die1, 2 hit1 of 530 accepted on fx-combat), so a kill is seen as a track ending, not a death; on blurred frames Axe Stump against Dark Axe Stump rests on colour and the spawn prior (on exact frames the colours settle it: on fx-exact the best other species scored at most 0.74 where the accepted one scored 1.00 at the median); maps without a good background get nothing to verify, and a mosaic from a few seconds of recording holds the mobs that stood still.

## Notes

Design rationale, the storage benchmark and other findings are in [docs/capture-notes.md](docs/capture-notes.md).

## Next steps

- HUD readers (HP, MP, EXP) running on recorded frames
- Minimap-based localisation
- Name-tag detection for players
- Entities beyond the second milestone: a fixture with drops left lying (no pet), mobs in front of parallax, more recordings per map for the mosaic, sprite search on maps without a background
- Input injection, later
