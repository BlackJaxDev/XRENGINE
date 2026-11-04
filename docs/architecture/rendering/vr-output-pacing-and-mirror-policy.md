# VR Output Pacing And Mirror Policy

While VR is active, the desktop window, editor panels, mirror composition, and XR swapchains share one render thread and one `EngineTimer` fence chain for collect, swap, and render work. See [Frame Lifecycle And Dispatch Paths](frame-lifecycle-and-dispatch-paths.md). This document defines how the engine decides which outputs render each frame, how it reports the cost of each output, and how desktop mirror policy controls duplicate scene work.

## Mirror policy

`Engine.Rendering.Settings.VrMirrorMode` (`EVrMirrorMode`) controls desktop output while VR is active. The default is `BlitSubmittedEye` (`RuntimeRenderingHostServiceDefaults.VrMirrorMode`).

| Mode | Desktop output |
|---|---|
| `Off` | No desktop output while in VR. Use this mode for eye-submit-only performance runs. |
| `BlitSubmittedEye` | Compose the desktop image from the submitted eye output. It does not render the scene a second time. This is the standard profiling mode. |
| `CyclopeanReconstruct` | Compose a cyclopean desktop image from eye color and depth textures. This mode is a policy value. The full reconstruction pass is still under implementation. Until that pass is available, the renderer must report a loud fallback instead of silently stretching one eye. |
| `LowRatePreview` | Render a low-rate desktop preview. |
| `FullIndependentRender` | Render the desktop scene independently. This mode is expensive. Use it for editor diagnostics. Visibility uses `EVrVisibilityPolicy.IndependentDesktopAndVrEyes`. |

With `Off`, `XRWindow` does not record or present the window on most frames. It presents at a keep-alive rate of 2 Hz, so window and desktop-frame maintenance still run. Before this hold, each VR frame acquired a swapchain image, deferred an empty recording, and presented a rejected image, which delayed the next XR frame.

`Engine.Rendering.Settings.VrDesktopFrameExecutionMode` (`EVrDesktopFrameExecutionMode`) selects how a recording desktop view (`FullIndependentRender`) executes while VR is active. `Inline` (the default) records, submits and presents the desktop frame on the render thread after the eyes. `WorkerRecord` records the desktop primary on reserved background render lane 2 into an engine-owned offscreen color image for the frame slot (created on first use with the swapchain format and extent, retired with the desktop WSI generation). After the scene, the render thread records a short copy from that image into the acquired swapchain image, and the ImGui and dynamic text overlays follow it. The render thread still joins the worker before it submits; a later change moves the join to the next desktop opportunity so that the desktop presents one XR frame later. `WorkerRecord` needs dynamic rendering and no Streamline frame generation, and fails loudly without them. `SeparateDomain` is reserved for a separate desktop render domain; selecting it holds the desktop window at its last image with a warning. No mode records inline instead.

All modes except `FullIndependentRender` use `EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean`. The legacy flags `RenderWindowsWhileInVR` and `VrMirrorComposeFromEyeTextures` are compatibility views of `VrMirrorMode`: setting `RenderWindowsWhileInVR=false` selects `Off`, and setting `VrMirrorComposeFromEyeTextures=true` selects the submitted-eye mirror path. The Unit Testing World bootstrap selects `FullIndependentRender` when a VR pawn renders desktop windows. A mirror mode change never changes XR eye submission.

## Cyclopean reconstruction contract

`CyclopeanReconstruct` composes one desktop spectator image from the rendered left and right eye textures. The composition path must not mutate eye swapchain textures. It must consume renderer-owned eye color and resolved linear view-space depth. It must publish diagnostics when required inputs are missing.

The intended reconstruction input contract is:

- left and right eye color sampled through sRGB views,
- left and right resolved linear depth in `R32_SFLOAT`,
- per-eye frame id, dimensions, formats, near and far planes, clip-space Y direction, reversed-Z state, view matrix, projection matrix, and inverse matrices,
- a cyclopean middle camera built from the eye midpoint and HMD orientation,
- optional middle color and depth history targets for temporal stability.

The reconstruction path rejects mixed-frame left and right inputs. It holds the last composed image or uses an explicit `BlitSubmittedEye` fallback with a diagnostic. It does not require eye tracking. Optional gaze can bias the seed depth only after the base path is stable. Invalid pixels must not use a raw 50/50 eye average because that double-images disocclusions.

The Vulkan path is the primary implementation target. OpenGL follows after Vulkan validation because it still needs separate per-eye sampleable color and depth inputs for this mode.

## Frame-output manifest

`RuntimeEngine.Rendering.Stats.FrameOutputs` records one manifest per completed render frame. `LastManifest` holds the latest snapshot.

- Each output is keyed by `EFrameOutputKind` and by `EVrOutputViewKind`.
- `FrameOutputTelemetry` and `FrameOutputWorkTelemetry` carry CPU time by `EFrameOutputPhase`, GPU time when available, command counts, and skip counts.
- The manifest records the active mirror mode, the visibility policy, whether the desktop path is a mirror or a separate scene render, the whole-frame render-thread time, and whole-frame p50, p90, p95, p99, and worst-frame values.
- The profiler packet, profile capture NDJSON, MCP profiler actions, and profiler UI Render Stats panel expose the manifest.

Accumulators are pooled. Recording an output does not allocate per frame.

## Output cadence

Each VR output has a target rate in hertz: `VrLeftEyeTargetRateHz`, `VrRightEyeTargetRateHz`, `VrDesktopEditorTargetRateHz`, and `VrCyclopeanDesktopTargetRateHz` (default 60). A value of `0` means "match the source rate". The source rate is the measured VR render rate while in VR; otherwise it is the timer target render frequency.

`FrameOutputs.EvaluatePacing(...)` returns a `FrameOutputPacingDecision` for an output and a frame id:

- XR-critical outputs, the eyes, are always due.
- A desktop-facing output, `DesktopEditor` or `CyclopeanDesktop`, is due when `floor(frameId * target / source)` increases. The decision is deterministic for a frame id, so collect, swap, and render agree.
- A skipped output reports `EFrameOutputSkipReason`: `Cadence`, `Budget`, `MirrorOff`, `SurfaceUnavailable`, `VrGated`, `Disabled`, or `HeldLastImage`. The decision also carries the configured rate, the achieved rate, and render and skip counts.

On a skipped frame the desktop output holds its last image. The scene does not render again for that output, and the UI overlay can still update. `XRViewport` accumulates the render delta of skipped frames and applies it to the next real desktop render. Temporal effects on the desktop or cyclopean view see the correct elapsed time. When the cyclopean output is not due under combined visibility, the combined frustum uses only the left and right eye views. Under independent visibility, the desktop visibility group does not collect on a skipped frame.

## Budget bands

The manifest attributes each frame to a budget band:

| Band | Budget |
|---|---|
| `Desktop60` | 16.67 ms. |
| `VR72` | 13.89 ms. |
| `VR90` | 11.11 ms. |
| `VR120` | 8.33 ms. |

When `VrDesktopAutoSkipWhenOverBudget` is true and the previous whole-frame time exceeded the active band, a desktop-facing output skips with reason `Budget` instead of making the eyes miss their deadline. The skip is counted in the manifest.

## XR frame priority

The eyes render first in the render loop. With `VrDesktopAutoSkipWhenOverBudget` on, at most one independent desktop frame runs per XR frame, and only when its last measured cost fits before the next XR frame. `OpenXRAPI.NextFrameSlackMs` estimates that time from the eye work time and the runtime period. A held frame reports `EFrameOutputSkipReason.Budget`, and the window keeps its last image.

The desktop keeps a minimum rate of 15 Hz, so the window cannot freeze. A frame that is due at this rate waits for the start of the next slack window (just after `xrEndFrame`). It runs only when its measured cost goes past the next XR wake by at most 1.5 ms. When the cost does not fit, a 4 Hz fallback frame runs early in a slack window, with at least 1.5 ms of slack left. Below 1 Hz, a frame runs at any time. A frame that starts just before an XR wake delays the eye frame by most of its cost and makes the eye frame miss its display slot; on the imported avatar at 90 Hz, this change reduced missed slots from 42 to 13 of about 450 frames.

## Render priority

The user setting `UserSettings.VrRenderPriority` selects which output gets render-thread time first while VR is active. The default is `Vr`.

- `Vr`: the XR frame priority above applies. Desktop frames that do not fit before the next XR frame are held, and the window keeps its last image.
- `Desktop`: every due desktop frame runs. The XR budget skip, the XR slack hold and the WorkerRecord slack gate are off. A late eye frame is reprojected by the XR runtime.

On the imported-avatar benchmark scene at 90 Hz (zero-readback, WorkerRecord), `Desktop` gave 31 Hz VR and 31 Hz desktop with the avatar visible, and `Vr` gave 30 Hz VR and 12 Hz desktop. When the eyes cannot reach the runtime rate, the hold mostly costs desktop frames.

## Next-frame collect

Only one OpenXR frame can be between `xrBeginFrame` and `xrEndFrame`. In single-pass stereo, the collect thread therefore collects the commands for the next frame while the current frame renders, after the swap publishes the current commands. When the next frame is located, the swap assigns these commands to it and publishes its located view set. Without this, each frame waits for a collect pass after `xrWaitFrame` returns, and that wait adds to the render time.

These commands use the last pose, because the next pose is not known yet. The combined culling projection is scaled by 0.85, which widens the volume by about 8 degrees at each edge. Eye rendering still applies the exact late pose, so the pose only selects the collected objects. The collect does not change the eye cameras, the pipelines, or the post-process state, because the current frame reads them on the render thread. A change of view mode or a failed view-set publication drops these commands, and the frame does a full collect.

Set `XRE_OPENXR_SPECULATIVE_COLLECT=0` to turn this off. Parallel command buffer recording does not use it.

## Limits

- All outputs still share one render thread and fence chain. Separate pacing domains for desktop and XR depend on dedicated render-thread window ownership and are not implemented.
- `LowRatePreview` and `CyclopeanReconstruct` are policy values. Measure their cost with the manifest before using them as a profiling baseline.
- `CyclopeanReconstruct` needs per-eye sampleable depth, per-eye metadata, and a fullscreen sampled composition pass before it can replace a submitted-eye blit.

## Source map

| Responsibility | Source |
|---|---|
| Mirror contracts | `XREngine.Runtime.Core/Settings/Contracts/Enums/EVrMirrorMode.cs`, `EFrameOutputKind.cs`, `EFrameOutputPhase.cs`, `EFrameOutputSkipReason.cs`, `Records/FrameOutputPacingDecision.cs`, `FrameOutputTelemetry.cs`, `FrameOutputWorkTelemetry.cs` |
| Manifest, cadence, and budget | `XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs` |
| Settings | `XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs` |
| Desktop output decisions and skipped-delta scope | `XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs`, `XREngine.Runtime.Rendering/Rendering/XRViewport.cs` |
| Profiler export | `XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs`, `XREngine.Data/Profiling/ProfilerStatsPacket.cs` |
