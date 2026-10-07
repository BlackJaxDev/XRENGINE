# OpenXR Vulkan Parallel Eye Recording TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md#view-render-modes-and-view-scoped-state), [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md)

## Current State

The code uses `EVrViewRenderMode` for requested VR view rendering. `ParallelCommandBufferRecording` is the default in settings and is Vulkan-only. `OpenXrEyeRenderTargetContext`, `VulkanOpenXrViewResourcePlannerContextKey`, `OpenXrPreparedEyeCommandBufferInput`, `OpenXrEyeRecordWorkerScheduler`, and `OpenXrVulkanSubmissionTracker` support view-scoped Vulkan eye recording and submission. `OpenXrRenderPacingMode.DedicatedThread` is the default. The old `SinglePassStereoVR` setting still exists as a migration input in Unit Testing World code.

## Open Code Items

### Diagnostics

- [ ] Document or remove unstable OpenXR Vulkan diagnostic flags. Update `XREngine.Data/Environment/XREngineEnvironmentVariables.cs` and the Vulkan diagnostics guide for `XRE_VULKAN_TARGET_TRACE`, `XRE_VULKAN_INDIRECT_TRACE`, `XRE_VULKAN_COUNTER_DIAGNOSTICS`, `XRE_VULKAN_DESCRIPTOR_TRACE`, `XRE_OPENXR_VULKAN_TRACE`, `XRE_VULKAN_PARALLEL_RECORDING_VALIDATE`, and `XRE_VULKAN_CAPTURE_EYE_OUTPUTS`. Done when: each flag is in a stable guide with its output, or the flag and its code path are removed.

### Settings migration

- [ ] Remove the `SinglePassStereoVR` migration input. Update `UnitTestingWorldSettings`, `UnitTestingWorld.Toggles.cs`, `UnitTestingWorld.cs`, `BootstrapRenderSettings`, `UnitTestingWorldSettingsStore`, and `VrViewRenderModeContractTests`. Done when: no runtime or settings code reads `SinglePassStereoVR`, `Tools/Generate-UnitTestingWorldSettings.ps1` regenerates the schema, and the contract tests assert only `VR.ViewRenderMode`.

### Collect-visible preparation

- [ ] Add an immutable collect-visible to eye-recording handoff if the owner moves frame-op capture onto the collect-visible thread. Update `OpenXRAPI.FrameLifecycle.cs`, `VulkanFrameLoop.OpenXR.EyeRendering.cs`, and `OpenXrEyeFrameOpEmission`. Done when: frame-op capture on the collect-visible thread reads no renderer-global state, desktop rendering cannot observe partial eye inputs, and a contract test covers the handoff.

## Decisions Needed

- [ ] Select the work that may run on the collect-visible thread in `CollectVisibleThread` pacing: next-frame wait/begin/locate, predicted pose cache update, predicted rig recalculation, combined stereo visibility, and frame-op capture. Owner: Rendering / XR.
- [ ] Select the measured default for pacing beyond the current `DedicatedThread` default. Candidates are dedicated pacing, collect-visible pacing, or a hybrid where collect-visible prepares immutable inputs and the dedicated thread owns blocking OpenXR calls. Owner: Rendering / XR.
- [ ] Decide if the persistent per-view render-object cache replaces the current per-physical-image view cache plus per-attachment-signature framebuffer cache. Owner: Rendering.

## Out Of Scope

- OpenGL `ParallelCommandBufferRecording`. OpenGL rejects that mode with a diagnostic.
- Direct OpenXR array swapchains for `SinglePassStereo`. The current path renders into an engine-owned layered target.
