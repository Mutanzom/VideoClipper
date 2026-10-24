# Video Clipper

A small Windows 11 app for cutting clips out of a video. Open a video, mark a start and an end point,
mark more pairs for more clips, then export them all.

WinUI 3 / .NET 9, unpackaged. FFmpeg does the cutting.

## Using it

| Key | Action |
| --- | --- |
| Space | Play / pause (clicking the video works too) |
| Left / Right | Previous / next frame |
| Shift+Left / Shift+Right | Back / forward 1 second |
| Home / End | Jump to start / end |
| **E** | Mark start of a clip |
| **R** | Mark end: the clip is added to the list |
| Esc | Drop a start you marked but haven't ended |
| Ctrl+O | Open video |

Click or drag on the timeline to seek. Marked clips show on it as numbered ranges. In the clip list
you can type exact start/end times (`1:05.250`, `65.25`), play a single clip, or remove it.

Export modes:

* **Fast (lossless)** copies the streams. It's instant with no quality loss, and the file keeps the
  source's container. The cut starts at the nearest keyframe, so a clip can begin slightly early.
* **Re-encode** writes an H.264/AAC MP4 with frame-exact cuts. Pick a quality, or set **Max MB**
  (e.g. 10 for Discord) and the bitrate is worked out so each clip fits, using a two-pass encode.

Speed:

* The **speed box** next to the time sets how fast the video plays while you watch (e.g. `0.1`,
  `0.25`, `1.5`; type and press Enter). A clip you mark gets this speed.
* Each clip has its own **export speed** box, starting at the speed you were watching at. `0.25` gives 4× slow motion, `2` makes it twice
  as fast; audio keeps its pitch. The clip's length shown in the list is the exported length.
  The play button on a clip previews it at that speed. Any speed other than 1 is re-encoded, even in
  Fast mode (stream copy can't retime frames); in Fast mode that uses High quality.

Tick **Mute audio** to export the clips without sound. It works in both modes; with a size limit the
audio's share of the budget goes to the video.

Clips are saved as `<video>_clip01.mp4`, `_clip02`, … in the video's folder (or one you choose).
Existing files are never overwritten.

## Building

```
dotnet test
dotnet publish src\VideoClipper.App -c Release -o publish
```

FFmpeg isn't in git. Put `ffmpeg.exe` and `ffprobe.exe` in `tools\ffmpeg\` before building (see the
README there) and they are bundled into the output, so `publish\` is self-contained and portable.
At runtime the app looks in `tools\ffmpeg\` next to the exe first, then on PATH.

## Layout

* `src\VideoClipper.Core` has no UI: clip model, time parsing, ffmpeg command building, ffprobe parsing, running ffmpeg.
* `src\VideoClipper.App` is the WinUI window and view model.
* `tests\VideoClipper.Core.Tests` holds the xUnit tests for Core.
