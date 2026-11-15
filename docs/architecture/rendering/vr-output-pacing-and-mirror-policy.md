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

In `WorkerRecord`, the desktop worker owns its command pools from the batch submit until the batch join. These pools are the lane 2 arena pools and every pool that the worker thread allocates a command buffer from, such as command-chain pools and the worker thread's own pool. Vulkan requires external synchronization of a pool while a thread records, resets or frees its command buffers, and the worker does not take the command pool gate. While the batch is in flight, other threads do not free or reset command buffers from these pools. The retired command-buffer drain and the invalidated-reset drain keep those entries queued, and the join frees and resets them. The MCP profiler stats show `retired_resources.command_buffer_pending_count` and `retired_resources.command_buffer_worker_owned_deferrals_total` for this queue.

Dense GPU profiler queries (`XRE_GPU_TIMESTAMP_DENSE=1`) are recorded only on the render thread, because the profiler recording cursor and its query pools are shared state without a lock. A desktop frame that the `WorkerRecord` worker records therefore has no GPU timing scopes, and the GPU pipeline dump states this as a backend status. To time the VR desktop frame on the GPU, select `Inline` (`VrDesktopFrameExecutionMode`, or `XRE_UNIT_TEST_VR_DESKTOP_FRAME_EXECUTION_MODE=Inline` in the unit-testing world).

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

The decision is made at the start of the collect that prepares the frame (the timer's `PrepareFrameOutputs` event), with the XR slack known at that time. The collect, the swap and the render of the frame use this one decision. A collect in which no desktop viewport renders also retains the desktop shadow cascades (see [shadow atlas](shadow-atlas.md)).

The desktop keeps a minimum rate of 15 Hz, so the window cannot freeze. A frame that is due at this rate waits for the start of the next slack window (just after `xrEndFrame`). It runs only when its measured cost goes past the next XR wake by at most 1.5 ms. When the cost does not fit, a 4 Hz fallback frame runs early in a slack window, with at least 1.5 ms of slack left. Below 1 Hz, a frame runs at any time. A frame that starts just before an XR wake delays the eye frame by most of its cost and makes the eye frame miss its display slot; on the imported avatar at 90 Hz, this change reduced missed slots from 42 to 13 of about 450 frames.

## Render priority

The user setting `UserSettings.VrRenderPriority` selects which output gets render-thread time first while VR is active. The default is `Vr`.

- `Vr`: the XR frame priority above applies. Desktop frames that do not fit before the next XR frame are held, and the window keeps its last image.
- `Desktop`: every due desktop frame runs. The XR budget skip, the XR slack hold and the WorkerRecord slack gate are off. A late eye frame is reprojected by the XR runtime.

The priority output also owns the per-frame mesh deformation (compute skinning and blendshapes, and the mesh atlas publish). `SkinningPrepassDispatcher.OwnsDeformation` makes the decision. With `Vr`, the stereo eye render dispatches the deformation inside its own submission, and the desktop and its shadow views only read the latest results. With `Desktop`, the desktop render owns it and the eyes only read. The reading output never waits for the owner, so the two outputs keep independent rates. Its pose can be one owner frame old. Both outputs submit to one graphics queue, so queue order keeps a read from overlapping a write. Shadow views never dispatch the deformation, because they render before the owning pipeline is active. Without an active VR session, every render owns the deformation.

On the imported-avatar benchmark scene at 90 Hz (zero-readback, WorkerRecord), `Desktop` gave 31 Hz VR and 31 Hz desktop with the avatar visible, and `Vr` gave 30 Hz VR and 12 Hz desktop. When the eyes cannot reach the runtime rate, the hold mostly costs desktop frames.

## Next-frame collect

Only one OpenXR frame can be between `xrBeginFrame` and `xrEndFrame`. In single-pass stereo, the collect thread therefore collects the commands for the next frame while the current frame renders, after the swap publishes the current commands. When the next frame is located, the swap assigns these commands to it and publishes its located view set. Without this, each frame waits for a collect pass after `xrWaitFrame` returns, and that wait adds to the render time.

These commands use the last pose, because the next pose is not known yet. The combined culling projection is scaled by 0.85, which widens the volume by about 8 degrees at each edge. Eye rendering still applies the exact late pose, so the pose only selects the collected objects. The collect does not change the eye cameras, the pipelines, or the post-process state, because the current frame reads them on the render thread. A change of view mode or a failed view-set publication drops these commands, and the frame does a full collect.

Set `XRE_OPENXR_SPECULATIVE_COLLECT=0` to turn this off. Parallel command buffer recording does not use it.

## XR collect pacing

The engine setting `OpenXrCollectPacing` (off by default) paces the collect loop to OpenXR frames. `XRE_OPENXR_COLLECT_PACING=1` or `0` overrides the setting. Without it, the collect loop publishes again after each render dispatch (render pacing), also while the render thread only waits for the next XR frame. In the avatar benchmark that was about 2.6 publications for each XR frame, and an XR frame waited for the collect that was in progress when `xrWaitFrame` returned.

With pacing, there is one collect and one publication for each XR frame:

1. The render thread enters `xrEndFrame` for frame N. This releases the collect for frame N+1 (`OpenXRAPI.EndFrameWithTiming`). The release is at the entry, not the return. Monado blocks in `xrEndFrame` until the GPU work is done; other runtimes block in `xrWaitFrame`. From the entry, the collect overlaps the block in both cases.
2. The collect runs the next-frame (speculative) stereo collect described above, while the render thread is still in `xrEndFrame` and the pacing thread then blocks in `xrWaitFrame` for N+1.
3. After the swap, a publication gate waits for a located frame newer than N, assigns the speculative commands to it and marks it prepared. Only then does the engine timer publish the generation, so the render thread receives frame N+1 and its commands together.

Pacing applies only to true single-pass stereo with the next-frame collect, with the dedicated pacing thread (`OpenXrRenderPacingMode.DedicatedThread`), while the session is focused and the last XR frame submitted its eyes. Two-pass and emulated stereo, focus loss, a session stop, a frame without eyes and a runtime loss use render pacing at once. Each wait is bounded: the limit is the larger of four display periods and two times the largest wake-to-end work of the last four frames, at most 100 ms. A wait that reaches the limit uses render pacing for that collect and is counted.

Measured on Monado with the Sponza avatar benchmark (Vulkan, Advanced pipeline, welded meshes):

| View | Size | Pacing off | Pacing on |
|---|---|---|---|
| Atrium | Monado default | 53.1 Hz | 53.8 Hz |
| Origin | Monado default | 51.1 Hz | 63.6 Hz |
| Atrium | 2016x2240 | 36.4 Hz | 29.7 Hz |
| Origin | 2016x2240 | 40.9 Hz | 36.3 Hz |

Pacing raises the XR rate when the render thread is the limit. When the eye GPU time is more than about two display periods (27.5 ms and 20.5 ms at 2016x2240), the rate is lower: the CPU work is the same, but `xrEndFrame` blocks about 4 to 5 ms longer, and most frames take one display period more. The cause of this is not known yet, so the setting is off by default.

Desktop frames in VR use the same admission policy, but a paced collect starts when the eyes enter `xrEndFrame`, and the desktop frame that it admits renders after the next eye frame. So the admission reads the slack of that later window from completed frames: the median slack after `xrEndFrame` of the last four frames, minus the largest measured lead from the `xrEndFrame` return to the desktop render start. At the atrium the desktop rate stayed within 1 Hz (9.0 Hz to 8.1 Hz). At the origin it fell from 20.7 Hz to 7.5 Hz while the XR rate rose.

With pacing, collect-keyed budgets keep their real-time rate. `CollectBudgetPeriods` counts the XR display periods since the last paced collect (the fraction carries to the next collect). The directional cascade refresh limit, the texture streaming transition limits and their frame clock, and the VR desktop pressure hold multiply their per-collect values by it. With render pacing the value is 1.

`get_render_profiler_stats` reports `vulkan.xr_collect_pacing` (paced and render-paced collects, the waits, their time-outs, deactivations, and publications with and without an XR frame), `speculative_view_angle_buckets` (the angle between the head view of the next-frame collect and the predicted view of the frame that receives it), and `frame_lifecycle.phase_totals.render_wait_for_xr_frame`, `collect_wait_for_xr_submit` and `publication_age`.

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
