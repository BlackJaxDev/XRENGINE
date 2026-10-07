# OpenXR Vulkan Parallel Eye Recording TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md#view-render-modes-and-view-scoped-state), [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md)

## Current State

The VR view render mode is the `EVrViewRenderMode` setting (`SequentialViews`, `SinglePassStereo`, `ParallelCommandBufferRecording`). The legacy `SinglePassStereoVR` flag is a migration input in `UnitTestingWorldSettingsStore` and `BootstrapRenderSettings`. Vulkan OpenXR eye recording uses explicit per-eye inputs: `OpenXrEyeRenderTargetContext`, `VulkanOpenXrViewResourcePlannerContextKey`, `OpenXrPreparedEyeCommandBufferInput`, and `OpenXrEyeRecordWorkerScheduler` (bounded worker recording for `ParallelCommandBufferRecording`). Submission ownership is in `OpenXrVulkanSubmissionTracker`. View groups, visibility groups, and foveation are immutable records in `XREngine.Runtime.Core/Settings/Contracts/Records/` (`ViewRenderGroupContext`, `ViewFoveationContext`, `RenderFrameViewDescriptor`). OpenXR pacing is `OpenXrRenderPacingMode`; `DedicatedThread` is the default and `CollectVisibleThread` is available. The measured 120 Hz target, the final mode comparison, and the eye-source and flicker regressions are open checks in the validation doc.

## Open Code Items

### Diagnostics

- [ ] Document or remove the OpenXR Vulkan diagnostic flags that have no stable documentation: `XRE_VULKAN_TARGET_TRACE`, `XRE_VULKAN_INDIRECT_TRACE`, `XRE_VULKAN_COUNTER_DIAGNOSTICS`, `XRE_VULKAN_DESCRIPTOR_TRACE`, `XRE_OPENXR_VULKAN_TRACE`, `XRE_VULKAN_PARALLEL_RECORDING_VALIDATE`, and `XRE_VULKAN_CAPTURE_EYE_OUTPUTS`. `XREngine.Data/Environment/XREngineEnvironmentVariables.cs` and the Vulkan diagnostics guide. Done when: each flag is in a stable guide with its output, or the flag and its code path are removed.

### Settings migration

- [ ] Remove the `SinglePassStereoVR` migration input. `UnitTestingWorldSettings`, `UnitTestingWorld.Toggles.cs`, `UnitTestingWorld.cs`, `BootstrapRenderSettings`, `UnitTestingWorldSettingsStore`, `VrViewRenderModeContractTests`. Done when: no runtime or settings code reads `SinglePassStereoVR`, the schema is regenerated with `Tools/Generate-UnitTestingWorldSettings.ps1`, and the contract tests assert only `VR.ViewRenderMode`.

### Collect-visible preparation

- [ ] If the owner moves frame-op capture into the collect-visible thread (see Decisions Needed), add an immutable handoff from collect-visible to the OpenXR recording path. `OpenXRAPI.FrameLifecycle.cs`, `VulkanFrameLoop.OpenXR.EyeRendering.cs`, `OpenXrEyeFrameOpEmission`. Done when: frame-op capture on the collect-visible thread reads no renderer-global state, desktop rendering cannot observe partial eye inputs, and a contract test covers the handoff.

## Decisions Needed

- [ ] Select the work that may run on the collect-visible thread in `CollectVisibleThread` pacing: next-frame wait/begin/locate, predicted pose cache update, predicted rig recalculation, combined stereo visibility, and frame-op capture. Owner: Rendering / XR.
- [ ] Select the default pacing mode from measured evidence. Candidates: dedicated pacing, collect-visible pacing, or a hybrid where collect-visible prepares immutable inputs and the dedicated thread owns blocking OpenXR calls. The default view render mode is `ParallelCommandBufferRecording` in all settings types (owner decision, 2026-10-06). That mode is Vulkan-only; OpenGL VR must select `SequentialViews`. Owner: Rendering / XR.
- [ ] Decide if the persistent per-view render-object cache (resource-planner-state-aware Vulkan texture and FBO cache) replaces the current per-physical-image view cache plus per-attachment-signature framebuffer cache. Owner: Rendering.

## Out Of Scope

- OpenGL `ParallelCommandBufferRecording`. OpenGL rejects that mode with a diagnostic.
- Direct OpenXR array swapchains for `SinglePassStereo`. The current path renders into an engine-owned layered target.
