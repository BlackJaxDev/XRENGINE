# OpenXR Validation

Scope: Validate OpenXR startup, frame pacing, view render modes, Monado no-HMD lanes, editor runtime switching, VR mirror output, and whole-frame VR performance.

Architecture: [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md), [VR Output Pacing And Mirror Policy](../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md), [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md#29-openxr-stereo-temporal-isolation), [OpenXR Runtime](../../../developer-guides/vr/openxr-runtime.md), [Unit Testing World](../../../developer-guides/testing/unit-testing-world.md). Code todos: [OpenXR Vulkan Parallel Eye Recording](../../todo/rendering/vr/openxr-vulkan-parallel-eye-recording-todo.md), [OpenXR Future Work](../../todo/rendering/vr/openxr-future-work-todo.md), [OpenXR Monado CI And Hardware Follow-ups](../../todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md), [OpenXR Runtime Code Organization](../../todo/rendering/vr/openxr-runtime-code-organization-todo.md), [Editor OpenXR Toggle](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md), [VR Mirror Cyclopean Reconstruction](../../todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md), [OpenXR Timing Tests](../../todo/tests/openxr-timing-tests-todo.md), [VR Rendering Performance Contract](../../todo/rendering/optimization/vr-rendering-performance-contract-todo.md). Hardware rows: [OpenXR SteamVR Hardware Validation](openxr-steamvr-hardware-validation.md).

## Setup

Use `Build-Editor` before editor validation. Use `Start-Editor-UnitTesting-OpenXR-Monado-NoDebug` for the Monado editor lane. Use `Start-Editor-UnitTesting-OpenXR-SteamVR-NoDebug` for the SteamVR editor lane when hardware is present. Use `Test-OpenXR-Monado-Smoke`, `Test-OpenXR-SceneOnlyVR-Smoke`, and `Test-OpenXR-SteamVR-Smoke` for smoke lanes. Use `Benchmark-Vulkan-Clean-OpenXR` for the quick Vulkan OpenXR benchmark lane. Use `Build-Monado` and `Install-Monado` only when the local Monado runtime must be built or staged.

Launch profiles: `Editor (Unit Testing OpenXR SteamVR)` starts the editor with `XRE_WORLD_MODE=UnitTesting`, `XRE_UNIT_TEST_WORLD_KIND=Default`, `XRE_UNIT_TEST_VR_MODE=OpenXR`, and `XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS=1`. `Editor (Unit Testing World)` is the base profile for manual OpenXR runtime selection.

Settings: `VR.Mode`, `VR.ViewRenderMode`, `VR.AllowDesktopEditing`, `VR.Foveation`, `Rendering.VrMirrorMode`, `OpenXrRenderPacingMode`, `OpenXrEyeResolutionPreset`, `OpenXrEyeResolutionScale`, `OpenXrCustomEyeResolutionWidth`, and `OpenXrCustomEyeResolutionHeight`. Environment variables include `XR_RUNTIME_JSON`, `XRE_SMOKE_FRAMES`, `XRE_OPENXR_RENDER_PACING_MODE`, `XRE_OPENXR_POSE_TIME_OFFSET_MS`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_PRESET`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_SCALE`, `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_WIDTH`, and `XRE_UNIT_TEST_OPENXR_EYE_RESOLUTION_HEIGHT`.

## Checks

### Monado no-HMD runtime lane

Architecture: [OpenXR Runtime, no-HMD test lanes](../../../developer-guides/vr/openxr-runtime.md#no-hmd-test-lanes).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Local Monado runtime smoke | Run `Test-OpenXR-Monado-Smoke` after OpenXR timing, frame-submission, or hardware-lane changes. | The runner uses the Monado runtime, submits the requested OpenXR frames, writes a valid smoke summary, exits with success, and stops only a service that it started. | Open | Last evidence: none. |
| Final Monado evidence set | Run the targeted OpenXR contract tests, the local Monado smoke, and `Tools/Reports/Find-NewAllocations.ps1 -FailOnOpenXrHotPathAllocations`. | The contract tests pass, the smoke summary passes its assertions, and no new OpenXR hot-path allocation is reported without a documented exception. | Open | Last evidence: none. |
| Scene-only VR lane | Run `Test-OpenXR-SceneOnlyVR-Smoke`. | The editor VR scene path runs without using the OpenXR API and reports a valid scene-only result. | Open | Last evidence: none. |
| Lane independence | Run the OpenXR timing contract tests without Monado available. | Lane 0 contract tests fail timing regressions before process and runtime orchestration starts. | Open | Last evidence: none. |
| `XR_EXT_conformance_automation` support | Query the extension in the selected Monado build and in the relevant hardware runtimes. Record the supported actions and limits in the runtime guide. | The runtime guide lists which runtimes expose the extension and which automation actions work. | Open | Last evidence: none. |

### OpenXR frame pacing and timing

Architecture: [OpenXR VR Rendering, frame lifecycle and pose timing](../../../architecture/rendering/openxr-vr-rendering.md#frame-lifecycle-and-pose-timing).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Dedicated pacing hardware matrix | On SteamVR/OpenXR and Oculus/OpenXR, run OpenGL and Vulkan with mirror plus ImGui, headset-off/session-stopping, and loss-pending recovery cases. | `DedicatedThread` keeps one outstanding `xrBeginFrame` per `xrEndFrame`, shuts down cleanly after session loss, and does not leave an `XR Pacing` thread alive after session end. | Open | Last evidence: none. |
| Desktop mirror with headset active | Enable OpenXR with desktop mirror and headset active. Compare desktop ImGui FPS to the non-VR baseline. | Desktop ImGui FPS is within 10 percent of the non-VR baseline after `DedicatedThread` pacing is enabled. | Open | Last evidence: none. |
| Pacing handoff stalls | Inspect `VrXrPacingHandoffStalls` during steady-state load. | The counter does not grow without bound. | Open | Last evidence: none. |
| Pacing idle time | Inspect `VrXrPacingThreadIdleTimeMs` during steady-state load. | Idle time approximately equals frame interval minus active preparation time. The pacing thread waits instead of spinning. | Open | Last evidence: none. |
| Tracking-loss warning streak | Cover the HMD sensors for about 10 seconds. | The log emits one tracking-loss warning per streak and one `FreezeLastValid` to identity warning only when no cached views exist. | Open | Last evidence: none. |
| Lifecycle baseline | Run a reference scene with `OpenXrDebugLifecycle=true`. | Lifecycle logs show predicted and late locate cadence, submit cadence, and no unexpected teardown. | Open | Last evidence: none. |
| Dedicated pacing baseline | Re-run the reference scene with `OpenXrRenderPacingMode=DedicatedThread`. | Lifecycle logs and stats show the dedicated thread owns wait, begin, and locate work. | Open | Last evidence: none. |
| Runtime action-sync audit | Sweep existing OpenXR input bindings with the `OpenXrActionSyncPolicy.PredictedOnly` default. | No binding regresses. Any binding that needs late action sync selects `PredictedAndLate` explicitly. | Open | Last evidence: none. |
| Per-runtime `xrWaitFrame` behavior | On SteamVR/OpenXR and Oculus/OpenXR, record when `xrWaitFrame` returns and the predicted display times under `DedicatedThread` pacing. Update the runtime guide if the result changes the recommended pacing mode. | The runtime guide states whether each runtime returns predicted frame times early enough for the pacing thread to help. | Open | Last evidence: none. |

### View render modes and parallel eye recording

Architecture: [OpenXR VR Rendering, view render modes and view-scoped state](../../../architecture/rendering/openxr-vr-rendering.md#view-render-modes-and-view-scoped-state).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Monado Vulkan eye output | Run Vulkan with Monado in the Unit Testing World. | Both OpenXR eyes show the HMD camera views, not content that follows the editor view. | Open | Last evidence: none. |
| Eye preview orientation | Capture fresh left and right eye previews after the eye source is stable. | Stereo preview orientation matches each eye. | Open | Last evidence: none. |
| Submit fence cost | Profile steady-state OpenXR Vulkan frames. | `OpenXR.Vulkan.SubmitFenceWait` does not dominate frame time. The frame-time breakdown identifies any remaining fence cost. | Open | Last evidence: none. |
| OpenXR Vulkan smoke profile | Run a short OpenXR Vulkan smoke and keep the CPU and GPU profiler dumps with the smoke summary. | The run records CPU and GPU data for the active view render mode. | Open | Last evidence: none. |
| F5 and explicit launch parity | Launch the Unit Testing World through the F5-equivalent profile and through the explicit CLI or MCP path. | Both paths show stable desktop, left-eye, and right-eye views with no user-visible flicker. | Open | Last evidence: none. |
| Editor-origin flicker regression | Move the editor camera near the world origin and the eye rig. | No editor-origin or eye-camera flicker occurs. | Open | Last evidence: none. |
| Warm GPU frame budget | Measure warm default-pipeline GPU p95 in the target OpenXR scene. | p95 GPU time is below 8.33 ms for the measured target scene. | Open | Last evidence: none. |
| Warm render-thread budget | Measure warm render-thread p95. | p95 render-thread time is below 8.33 ms, with OpenXR preparation and visibility work outside the render-thread critical path. | Open | Last evidence: none. |
| Mode comparison | Compare `ParallelCommandBufferRecording` with the batched eye path, serial eye submit, `SinglePassStereo`, `CollectVisibleThread`, and `DedicatedThread`. | The comparison records the active pacing mode, view render mode, CPU time, GPU time, and missed-deadline counters. | Open | Last evidence: none. |
| Collect-visible preparation cost | Record render-thread CPU cost before and after collect-visible preparation changes. | The change does not move blocking OpenXR or renderer-global state reads onto the render thread. | Open | Last evidence: none. |
| Independent desktop collect-visible | Set `VR.AllowDesktopEditing=true` and run OpenXR. | The desktop view collects visibility independently from the VR eye visibility group. | Open | Last evidence: none. |
| Combined runtime collect-visible | Set `VR.AllowDesktopEditing=false` and run OpenXR. | One combined collect-visible pass serves left eye, right eye, and cyclopean desktop view. | Open | Last evidence: none. |
| Runtime foveation request | Run a 90-frame OpenXR Vulkan smoke with `VR.Foveation.Mode=EyeTracked` or `RuntimePreferred` on a supported runtime and device. | The summary reports the effective foveation mode or a clear fallback reason. | Open | Last evidence: none. |
| Eye output regression scan | Inspect left and right previews, final output, and command-chain cache keys. | No black eye, no unexplained over-bright or wrong-color output, and no command-chain cache aliasing between eyes, images, or views. | Open | Last evidence: none. |
| True single-pass hardware validation | Run OpenXR true single-pass stereo on hardware with Vulkan validation enabled. | No multiview begin-rendering or inheritance VUIDs occur. | Open | Last evidence: none. |

### Editor OpenXR toggle and import responsiveness

Architecture: [OpenXR Runtime, startup behavior](../../../developer-guides/vr/openxr-runtime.md#startup-behavior).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Desktop texture upload recovery | Reproduce import followed by Monado startup. Inspect desktop presents, texture transition age, staging reserve occupancy, and outstanding chunk owners. | Desktop presents advance. Required texture uploads finish or report a terminal actionable failure. | Open | 2026-09-25 source investigation found a staging lease selection risk; live validation remained open. |
| SteamVR upload parity | Repeat the import and runtime switch sequence with SteamVR, then switch back to desktop. | Desktop images and eye images continue to update during motion. Required-generation readiness and ownership checks stay intact. | Open | Last evidence: none. |
| BRDF producer ordering | Validate the required-producer change in `AbstractRenderer`, `BrdfIntegrationResources`, Vulkan required-producer plumbing, and the frame operation queue. | BRDF production reaches a successful published generation without repeated abandoned submission or mixed-context failures. | Open | 2026-09-25 source review complete; build and runtime validation open. |
| BRDF capture path | Repeat the BRDF validation with RenderDoc capture enabled. | The capture path still submits XR frames and does not hide a producer-ordering failure. | Open | Last evidence: none. |
| Avatar shading capture | After upload and BRDF validation, capture a current failing stereo deferred combine draw with RenderDoc. Inspect constants and bound textures. | The capture identifies illumination, depth/background composition, stale resource publication, or another concrete fault before shader math changes. | Open | Last evidence: none. |
| Monado and SteamVR visual parity | Run the same scene, environment, and view on Monado and SteamVR. Capture both eyes before and after motion and after runtime replacement. | Exported images show the same expected avatar shading and eye orientation, or the difference has one documented cause. | Open | Last evidence: none. |
| Headset output sign-off | Ask the user to verify actual headset output after engine-side captures are correct. | Moving headset images are correct. No brightness or matrix workaround is accepted without root-cause evidence. | Open | Last evidence: none. |
| Import latency profile | Profile the interval from import completion to first complete visible avatar. Separate file parsing, worker preparation, owner-thread influence copying, native buffer allocation, static GPU append, texture publication, and scene attachment. | The profile identifies the longest remaining stall and first-visible latency for cold import, warm import, and renderer recreation with desktop and XR active. | Open | Last evidence: none. |
| Mesh payload cache validation | Validate cancellation and source revision invalidation of the immutable mesh payload cache. | Unsafe skin-buffer reads stay on their owner thread. Stale payloads are rejected. | Open | Last evidence: none. |
| Runtime-toggle matrix | Exercise the actual checkbox with Monado, SteamVR, cancel, runtime unavailable, startup failure, and toggle-off during preparation. | The original desktop pawn is restored, temporary rigs are cleaned up, authored rigs are preserved, and `VR.Mode=Desktop` remains saved. | Open | Last evidence: none. |
| Runtime-toggle overlap | Repeat desktop to Monado to desktop to SteamVR to desktop while imports overlap. | Callbacks, leases, publication pins, and upload progress remain correct. | Open | Last evidence: none. |
| Test clearance | After live acceptance, request explicit clearance before adding regression tests for this feature-regression path. | No tests are added before the live path is validated and clearance is given. | Open | Last evidence: none. |

### VR mirror and cyclopean reconstruction

Architecture: [VR Output Pacing And Mirror Policy](../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md#mirror-policy).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Baseline mirror behavior | Record the current one-eye stretch behavior before changing mirror composition. | The baseline makes the current `CyclopeanReconstruct` gap visible. | Open | Last evidence: none. |
| Vulkan visual reconstruction | Validate `CyclopeanReconstruct` with screenshots from at least two HMD or head poses on Vulkan. | The desktop mirror uses both eye color and resolved depth, outputs one stable cyclopean view, preserves aspect ratio, and does not include editor ImGui in runtime mirror mode. | Open | Last evidence: none. |
| Mirror debug modes | Capture contribution, reprojection error, invalid mask, iteration count, and final color debug modes. | Each debug mode shows the intended reconstruction data and no repeated fallback warning. | Open | Last evidence: none. |
| RenderDoc mirror layout | Inspect a Vulkan capture for the reconstruction pass. | Eye color and depth inputs are in sampled-read layouts, history targets are declared, and no eye swapchain texture is mutated. | Open | Last evidence: none. |
| OpenGL mirror follow-up | After Vulkan validation, repeat screenshot validation on the OpenGL path. | OpenGL uses per-eye color and depth inputs and no longer blits the last eye when reconstruction is selected. | Open | Last evidence: none. |
| Mirror failure diagnostics | Run with missing depth or mixed-frame inputs. | The renderer emits a clear diagnostic and holds the last composed image or uses an explicit `BlitSubmittedEye` fallback. | Open | Last evidence: none. |
| Mirror performance | Compare `CyclopeanReconstruct` against the third-render desktop camera path. | The report shows cost, quality, held-image cadence, and whether the mode should remain opt-in. | Open | Last evidence: none. |

### VR performance contract

Architecture: [VR Output Pacing And Mirror Policy](../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md), [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Runtime and backend inventory | For each capture, record runtime, HMD, refresh rate, render resolution, render path, graph revision, stereo path, foveation/VRS state, cache state, validation state, and reprojection state. | A profile capture explains its target budget and active stereo implementation without external notes. | Open | Last evidence: none. |
| Runtime frame budgets | Capture 72 Hz, 90 Hz, and 120 Hz target modes where hardware or runtime support exists. | Reports use one whole submitted XR frame budget, not a per-eye budget. | Open | Last evidence: none. |
| Zero-readback promotion workload | Run the Vulkan RVC `GpuIndirectZeroReadback` workload with desktop, left eye, and right eye renders in one frame. | Complete-frame p95 is at or below 8.33 ms for 120 Hz in both supported foveation states. | Open | Last evidence: none. |
| Stereo mode reporting | Inspect profiler output for mono, multiview, view-instance, instanced-stereo, and two-pass paths. | Two-pass fallback is reported as compatibility/debug fallback and draw counters distinguish mono, single-pass, and CPU two-pass work. | Open | Last evidence: none. |
| Multiview and shadow validation | Validate OpenGL multiview, Vulkan multiview, and shadow rendering on supported paths. | Compatible geometry, depth, and visibility passes render single-pass stereo. Shadows render mono unless a pass explicitly requires per-eye shadows. | Open | Last evidence: none. |
| Per-eye resource correctness | Validate depth, normal, velocity, visibility, post-process, and mirror resources for mono, stereo array, and multiview layouts. | Both eyes see the same scene state with correct per-eye projection and no stale shared resource hazards. | Open | Last evidence: none. |
| Motion-vector contract | Validate previous transforms, previous skinned positions, jitter convention, and velocity coverage. | Temporal AA, upscalers, and reprojection get dense and correct motion vectors for skinned avatar motion. | Open | Last evidence: none. |
| VRS and foveation | Validate capability probes, fixed foveation, eye-tracked foveation, shading-rate distribution, and UI readability. | VRS or foveation is a measured and reported option. Unsupported modes have explicit fallbacks. | Open | Last evidence: none. |
| Reprojection friendliness | Validate depth, zero velocity for static overlays, incompatible post-effect opt-outs, reprojection counters, and warnings. | Missed XR budgets remain visible even when runtime reprojection keeps the display moving. | Open | Last evidence: none. |
| Benchmark discipline | Run standardized VR scenes and camera paths with validation layers and debug output disabled. | Reports include p50, p90, p99, dropped frames, reprojection events, stereo mode, and cache policy. | Open | Last evidence: none. |
| Mono regression | Run a desktop mono regression after stereo changes. | Stereo work does not break mono rendering. | Open | Last evidence: none. |

### OpenXR runtime organization

Architecture: [OpenXR VR Rendering](../../../architecture/rendering/openxr-vr-rendering.md), [OpenXR Runtime](../../../developer-guides/vr/openxr-runtime.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Refactor baseline | Before moving OpenXR files, capture a clean editor build and the narrowest deterministic OpenXR tests. | The baseline gives a clear failure boundary for structural changes. | Open | Last evidence: none. |
| Post-move validation | After each structural move, build `XREngine.Runtime.Rendering`, `XREngine.Runtime.XR.OpenXR`, and `XREngine.Editor`; run targeted OpenXR policy, timing, RVC, view-mode, and strict-stereo tests. | Behavior is unchanged, and tests do not fail only because a method moved to another file. | Open | Last evidence: none. |
| Smoke schema validation | After smoke model changes, run JSON generation, normalization, evidence validation, exit-code, and teardown checks. | All report-consuming scripts use one schema version. Runtime and validators agree. | Open | Last evidence: none. |
| Backend compile paths | Validate both OpenGL and Vulkan OpenXR compilation paths after layout changes. | Backend-specific graphics bindings still build and register. | Open | Last evidence: none. |
| Hot-path allocation scan | Run the OpenXR hot-path allocation audit after diagnostics or sink changes. | No new allocation appears in render, collect-visible, pacing, swapchain, or submission hot paths. | Open | Last evidence: none. |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Editor OpenXR toggle and import responsiveness | Monado eye frames can continue while the desktop image stalls on a required texture upload. | [Editor OpenXR Toggle](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md) |
| Editor OpenXR toggle and avatar shading | Earlier Monado and SteamVR captures showed black or background-colored avatars before headset composition. | [Runtime toggle and stereo investigation](../../investigations/editor/openxr-runtime-toggle-2026-09-24.md) |
| SteamVR OpenXR tracker exposure | SteamVR hardware showed tracked devices through OpenVR, but HTCX OpenXR tracker enumeration returned zero paths. | [OpenXR SteamVR Hardware Validation](openxr-steamvr-hardware-validation.md) |

