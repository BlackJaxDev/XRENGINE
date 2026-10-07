# VR Output Pacing And Mirror Policy

While VR is active, the desktop window, editor panels, mirror composition, and XR swapchains share one render thread and one `EngineTimer` fence chain (collect, swap, render). See [Frame Lifecycle And Dispatch Paths](frame-lifecycle-and-dispatch-paths.md). This document describes how the engine decides which outputs render each frame, how it reports the cost of each output, and how the desktop mirror policy controls duplicate scene work.

## Mirror policy

`Engine.Rendering.Settings.VrMirrorMode` (`EVrMirrorMode`) is the one setting that controls desktop output while VR is active. The default is `BlitSubmittedEye` (`RuntimeRenderingHostServiceDefaults.VrMirrorMode`).

| Mode | Desktop output |
|---|---|
| `Off` | No desktop output while in VR. Use for eye-submit-only performance runs. |
| `BlitSubmittedEye` | Composes the desktop image from the submitted eye output. No second scene render. Standard profiling mode. |
| `CyclopeanReconstruct` | Composes a cyclopean image from the eye textures. |
| `LowRatePreview` | Low-rate desktop preview. |
| `FullIndependentRender` | Renders the desktop scene independently. Expensive; use for editor diagnostics. Visibility uses `EVrVisibilityPolicy.IndependentDesktopAndVrEyes`. |

All other modes use `EVrVisibilityPolicy.CombinedRuntimeLeftRightCyclopean`. The legacy flags `RenderWindowsWhileInVR` and `VrMirrorComposeFromEyeTextures` are compatibility views of `VrMirrorMode`: setting `RenderWindowsWhileInVR=false` selects `Off`, and setting `VrMirrorComposeFromEyeTextures=true` selects `BlitSubmittedEye`. The Unit Testing World bootstrap selects `FullIndependentRender` when a VR pawn renders desktop windows. A mirror mode change never changes XR eye submission.

## Frame-output manifest

`RuntimeEngine.Rendering.Stats.FrameOutputs` records one manifest per completed render frame. `LastManifest` holds the latest snapshot.

- Each output is keyed by `EFrameOutputKind` (for example `DesktopScene`, `DesktopMirror`, `EditorScenePanel`, `OpenXREyeSubmit`, `OpenVRSubmit`, `ImGuiOverlay`, `DynamicTextOverlay`, `Present`, `Shadow`, `SceneCapture`) and by `EVrOutputViewKind` (`LeftEye`, `RightEye`, `DesktopEditor`, `CyclopeanDesktop`, and the wide, inset, secondary, and debug views).
- `FrameOutputTelemetry` and `FrameOutputWorkTelemetry` carry CPU time by `EFrameOutputPhase` (`Collect`, `Swap`, `Render`, `Submit`, `GpuComplete`, `Overlay`, `Present`), GPU time when available, command counts, and skip counts.
- The manifest records the active mirror mode, the visibility policy, whether the desktop path is a mirror or a separate scene render, the whole-frame render-thread time, and whole-frame p50, p90, p95, p99, and worst-frame values.
- The profiler packet (`ProfilerStatsPacket.FrameOutputs`), the profile capture NDJSON (`frame_output_*` and `vr_mirror_mode` fields), the MCP profiler actions, and the profiler UI Render Stats panel expose the manifest.

Accumulators are pooled; recording an output does not allocate per frame.

## Output cadence

Each VR output has a target rate in hertz: `VrLeftEyeTargetRateHz`, `VrRightEyeTargetRateHz`, `VrDesktopEditorTargetRateHz`, and `VrCyclopeanDesktopTargetRateHz` (default 60). A value of `0` means "match the source rate". The source rate is the measured VR render rate while in VR; otherwise it is the timer's target render frequency.

`FrameOutputs.EvaluatePacing(...)` returns a `FrameOutputPacingDecision` for an output and a frame id:

- XR-critical outputs (the eyes) are always due.
- A desktop-facing output (`DesktopEditor`, `CyclopeanDesktop`) is due when `floor(frameId * target / source)` increases. The decision is deterministic for a frame id, so collect, swap, and render agree. For example, a 60 Hz cyclopean output against 90 Hz VR skips one of every three frames.
- A skipped output reports `EFrameOutputSkipReason` (`Cadence`, `Budget`, `MirrorOff`, `SurfaceUnavailable`, `VrGated`, `Disabled`, `HeldLastImage`). The decision also carries the configured rate, the achieved rate, and the render and skip counts.

On a skipped frame the desktop output holds its last image; the scene does not render again for that output, and the UI overlay can still update. `XRViewport` accumulates the render delta of skipped frames and applies it to the next real desktop render, so temporal effects on the desktop or cyclopean view see the correct elapsed time. When the cyclopean output is not due under combined visibility, the combined frustum uses only the left and right eye views (`ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum` two-view overload). Under independent visibility, the desktop visibility group does not collect on a skipped frame.

## Budget bands

The manifest attributes each frame to a budget band:

| Band | Budget |
|---|---|
| `Desktop60` | 16.67 ms (not in VR and not a stereo pass) |
| `VR72` | 13.89 ms (VR rate at least 65 Hz) |
| `VR90` | 11.11 ms (VR rate at least 81 Hz, and the fallback) |
| `VR120` | 8.33 ms (VR rate at least 110 Hz) |

When `VrDesktopAutoSkipWhenOverBudget` is true (default) and the previous whole-frame time exceeded the active band, a desktop-facing output skips with reason `Budget` instead of making the eyes miss their deadline. The skip is counted in the manifest.

## Limits

- All outputs still share one render thread and fence chain. Separate pacing domains for desktop and XR depend on the [dedicated render-thread window ownership plan](../../work/design/rendering/dedicated-render-thread-window-ownership-plan.md) and are not implemented.
- `LowRatePreview` and `CyclopeanReconstruct` are policy values; measure their cost with the manifest before using them as a profiling baseline.

## Source map

| Responsibility | Source |
|---|---|
| Contracts | `XREngine.Runtime.Core/Settings/Contracts/Enums/EVrMirrorMode.cs`, `EFrameOutputKind.cs`, `EFrameOutputPhase.cs`, `EFrameOutputSkipReason.cs`; `Records/FrameOutputPacingDecision.cs`, `FrameOutputTelemetry.cs`, `FrameOutputWorkTelemetry.cs` |
| Manifest, cadence, and budget | `XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs` |
| Settings | `XREngine.Runtime.Rendering/Runtime/Settings/RuntimeEngine.Rendering.EngineSettings.cs` |
| Desktop output decisions and skipped-delta scope | `XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs`, `XREngine.Runtime.Rendering/Rendering/XRViewport.cs` |
| Profiler export | `XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs`, `XREngine.Data/Profiling/ProfilerStatsPacket.cs` |

Validation: [OpenXR Validation](../../work/testing/xr/openxr-validation.md).
