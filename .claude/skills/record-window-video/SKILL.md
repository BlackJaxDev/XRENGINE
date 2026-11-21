---
name: record-window-video
description: >
  Record a video of an XRENGINE window (the editor, the Monado preview, or any window by title)
  with ffmpeg Windows Graphics Capture. Use this skill when the user asks for a video, a screen
  recording, a clip, or footage of the editor, the VR or Monado eye view, a game window, or
  animation playback, or asks to "record", "film", or "capture a video" of a window.
  DO NOT use for single screenshots (use the MCP capture tools) or for GPU frame debugging
  (use renderdoc-gpu-debug).
---

# Record Window Video

## Overview

`Tools/Record-WindowVideo.ps1` records one window to an MP4 file with the ffmpeg `gfxcapture`
filter (Windows Graphics Capture). It records each window separately, also when another window
covers it, and it sees Vulkan and OpenGL swapchain output.

Do not use `gdigrab`. It cannot see Vulkan swapchain output: it gives a frozen frame or about one
new frame per second. Do not use the MCP viewport sequence capture for smoothness checks either.
Its readback limits it to about 8 frames per second.

## Requirements

- ffmpeg 8.0 or later with the `gfxcapture` filter. The script finds, in this order: `-FfmpegPath`,
  the portable copy under `Build/_AgentValidation/00000000-000000-shared/tools/ffmpeg`, then ffmpeg on
  `PATH`.
- If no ffmpeg has `gfxcapture`, ask the user before you download one. Then run
  `Tools/Dependencies/Get-PortableFfmpeg.ps1`. It downloads the BtbN nightly (about 200 MB) and
  extracts it to the portable folder. It installs nothing and does not change `PATH`.
- The window must exist and must not be minimized.

## Procedure

1. Choose the window, the duration and the frame rate from the user's request.
   - Defaults: 20 seconds at 60 fps. Use 60 fps or more when the user wants to judge smoothness.
   - Suggest a duration when the user gives none: 15 to 30 seconds shows animation and pacing.
     Use a shorter clip for one event and a longer clip for intermittent problems such as flicker.
2. If the app is not running, start it. For an isolated editor session, use
   `Tools/Manage-McpEditorSession.ps1 Start -Name <name>` and wait until the scene is ready.
   A VR benchmark example is in the "Window videos" section of
   `docs/developer-guides/ai/agent-editor-workflows.md`.
3. Record. To record two windows at the same time, start two script processes in parallel.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Record-WindowVideo.ps1 -Window Editor -SessionName <name> -Seconds 20 -Fps 60
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Record-WindowVideo.ps1 -Window Monado -Seconds 20 -Fps 60
   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Record-WindowVideo.ps1 -Window '^XRE Editor' -OutputPath <run>/mcp-captures/editor.mp4
   ```

   | Parameter | Meaning |
   |---|---|
   | `-Window` | `Editor`, `Monado`, or a regular expression for the window title. |
   | `-SessionName` | With `-Window Editor`, records the editor of this isolated MCP session. |
   | `-ProcessId` | Records the main window of this process. Overrides `-Window`. |
   | `-Seconds`, `-Fps` | Duration and frame rate. |
   | `-OutputPath` | The MP4 file. The default is the `mcp-captures` folder of the newest task run. |
   | `-ShowCursor` | Includes the mouse cursor. |

4. Check the result. The script prints the frame count and the number of changed frames. A count
   of changed frames near 1 means that the capture is frozen or the window is black. Make a contact
   sheet and look at it before you send the video:

   ```powershell
   & <ffmpeg> -y -i <video>.mp4 -vf "select='not(mod(n\,150))',scale=480:-1,tile=4x2" -frames:v 1 <video>-contact.png
   ```

5. Send the video to the user as a file, and say the window, duration, frame rate and anything
   that the contact sheet shows. Stop any session that you started.

## Notes

- Videos are task evidence. Keep them under the task run in `Build/_AgentValidation/`. Do not
  commit them.
- The changed-frame count uses `mpdecimate`. It merges frames with very small differences, so it
  can be lower than the real presentation rate of the window.
