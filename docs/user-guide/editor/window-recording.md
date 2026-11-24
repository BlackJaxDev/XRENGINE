# Window Video Recording

Press **Shift+F12** in an editor window to start a video recording of that window.
Press **Shift+F12** again to stop it. **F12** without Shift still takes a screenshot.

The shortcut reads native window input, so it works in edit mode, in play mode, and
when a game mode owns input.

## Status

The window title is the only status display. While a recording runs, the title ends
with `● REC m:ss` and shows the elapsed time. After one hour, the time shows as
`h:mm:ss`. When you stop the recording, the original title comes back.

If a recording cannot start or stops unexpectedly, the title shows a short message such
as `REC: ffmpeg not found` for five seconds. The editor log holds the full reason.

## Output

Each recording is one MP4 file in `<user-profile>/Videos/XRENGINE/`, with the name
`XRENGINE_yyyy-MM-dd_HH-mm-ss.mp4`. If the user profile has no Videos folder, the file
goes to the desktop.

The settings make small files that you can upload to social media sites directly:

| Setting | Value |
|---|---|
| Codec | H.264 High profile, level 4.2, `yuv420p` |
| Frame rate | 60 fps, constant |
| Size | The window size, scaled down to fit 1920x1080 with the same aspect ratio |
| Quality | CRF 26 with an 8 Mbit/s peak limit (`libx264`, preset `faster`) |
| Keyframes | One every two seconds |
| Audio | None |
| Layout | The MP4 index is at the start of the file (`+faststart`) |

The recording includes the mouse cursor.

## Requirements

The recorder starts ffmpeg as a separate process. ffmpeg reads the window with Windows
Graphics Capture, so the engine does no extra GPU readback. The ffmpeg build must have
the `gfxcapture` filter (ffmpeg 8.0 or later on Windows) and the `libx264` encoder.

The editor uses the first ffmpeg that has both, in this order:

1. `XRE_FFMPEG_PATH`, which can name the executable or its folder.
2. The portable copy that `Tools/Dependencies/Get-PortableFfmpeg.ps1` extracts into a
   source checkout.
3. `ffmpeg` on `PATH`.

The editor keeps the found path for the session. If no ffmpeg is usable, install one
and press **Shift+F12** again. You do not need to restart the editor.

## Limits

- The window must not be minimized when the recording starts.
- A graphics backend switch replaces the native window, so it stops the recording.
- One recording runs at a time. **Shift+F12** in another window stops the active
  recording.
