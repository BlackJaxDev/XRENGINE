# OpenXR Validation

Scope: runtime, visual, performance, and hardware checks for OpenXR rendering, stereo modes, eye recording, frame pacing, desktop mirror output, and the Monado no-HMD lanes.

Architecture: [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md), [VR Output Pacing And Mirror Policy](../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation), [OpenXR Runtime Guide](../../../developer-guides/vr/openxr-runtime.md), [Unit Testing World](../../../developer-guides/testing/unit-testing-world.md)

Code todos: [OpenXR Vulkan Parallel Eye Recording TODO](../../todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md), [OpenXR Monado CI And Hardware Follow-ups](../../todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md)

Hardware rows: [OpenXR SteamVR Hardware Validation](openxr-steamvr-hardware-validation.md)

## Setup

- Tasks: `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug`, `Test-OpenXR-Monado-Smoke`, `Test-OpenXR-SceneOnlyVR-Smoke`, `Test-OpenXR-SteamVR-Smoke`.
- Scripts: `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1`, `Tools/OpenXR/Run-OpenXrSceneOnlyVrSmoke.ps1`, `Tools/OpenXR/Find-MonadoRuntime.ps1`.
- Settings: `VR.Mode`, `VR.ViewRenderMode`, `VR.AllowDesktopEditing`, `VR.Foveation`, `Rendering.VrMirrorMode`, `OpenXrEyeResolution`.
- Environment: `XR_RUNTIME_JSON`, `XRE_SMOKE_FRAMES`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_*`.

## Imported Checks

### From openxr-monado-vulkan-parallel-rendering-todo-2026-06-25.md

- [ ] Run Vulkan with Monado in the Unit Testing World. Expected: both OpenXR eyes show the HMD camera views, not content that follows the editor view.
- [ ] Capture fresh left and right eye previews after the eye source is stable. Expected: the stereo preview orientation matches each eye.
- [ ] Profile steady-state OpenXR Vulkan frames. Expected: the submit fence wait does not dominate the frame time. Record the frame-time breakdown.
- [ ] After each fix in this area, run the editor MCP loop. Record the screenshots and the log session in an investigation under `investigations/rendering/`.

### From openxr-vulkan-true-parallel-eye-primary-recording-todo.md

- [ ] Run a short OpenXR Vulkan smoke and keep the CPU and GPU profiler dumps with the smoke summary.
- [ ] Launch the Unit Testing World through the F5-equivalent profile and through the explicit CLI/MCP path. Expected: both show stable desktop, left-eye, and right-eye views with no user-visible flicker.
- [ ] Move the editor camera near the world origin and the eye rig. Expected: no editor-origin or eye-camera flicker. See [editor origin and eye camera flicker](../../investigations/rendering/archive/editor-origin-eye-camera-flicker-2026-06-28.md).
- [ ] Measure warm default-pipeline GPU p95 in the target scene. Expected: below 8.33 ms.
- [ ] Measure warm render-thread p95. Expected: below 8.33 ms, with OpenXR preparation and visibility work outside the render-thread critical path.
- [ ] Compare `ParallelCommandBufferRecording` against the batched eye path, serial eye submit, `SinglePassStereo`, `CollectVisibleThread` pacing, and `DedicatedThread` pacing. Measure dedicated pacing first. Record the mode comparison in the active OpenXR performance progress doc.
- [ ] Record render-thread CPU cost before and after collect-visible preparation changes.
- [ ] With `VR.AllowDesktopEditing=true`, confirm that desktop collect-visible runs independently from VR eye collect-visible.
- [ ] With `VR.AllowDesktopEditing=false`, confirm that one collect-visible pass serves the left eye, the right eye, and the cyclopean desktop view.
- [ ] Run a 90-frame OpenXR Vulkan smoke with `VR.Foveation.Mode=EyeTracked` or `RuntimePreferred` on a runtime and device that support it.
- [ ] Regression scan: no black left or right preview, no over-bright or wrong-color final output without a known cause, and no command-chain cache aliasing between eyes, images, or views.

### From openxr-monado-testing-pipeline-todo.md

- [ ] Run the final Monado lane evidence set: targeted OpenXR contract tests, the local Monado smoke, and the OpenXR hot-path allocation audit (`Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`). Record the result.
- [ ] Run the hardware OpenXR rows that a change touches. Use [OpenXR SteamVR Hardware Validation](openxr-steamvr-hardware-validation.md).

### From openxr-stereo-temporal-isolation-todo.md

- [ ] Run OpenXR true single-pass stereo on hardware with Vulkan validation enabled. Expected: no multiview begin-rendering or inheritance VUIDs.

### From desktop-vr-shared-render-thread-frame-pacing-todo.md

- [ ] Capture three baselines from the same scene with the frame-output manifest: desktop only, OpenXR with `VrMirrorMode=Off` or `BlitSubmittedEye`, and OpenXR with `FullIndependentRender`. Record the active `EVrVisibilityPolicy` and mirror settings beside each baseline.
- [ ] Compare the baselines. Expected: the cheap mirror mode reduces render-thread time and does not degrade XR output.
- [ ] Run the standard VR profiling mode (cheap mirror). Expected: the render thread does not spend hundreds of milliseconds on combined desktop and VR work.

### From runtime-modularization-phase4-todo.md and runtime-modularization-phase5-todo.md

- [ ] Run the physical OpenXR and OpenVR headset acceptance lanes on the modular runtime (Editor and VRClient). Record the results in the hardware validation doc.

### From rendering-profiler-and-benchmarking-todo.md

- [ ] Generate a profile capture with a real stereo runtime session active. Expected: the frame samples report the active stereo mode (not `mono`), the manifest records the target refresh rate and frame budget, and per-eye timing fields are present where the runtime exposes them. The last emulated-stereo capture recorded `mono` samples.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
