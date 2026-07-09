# OpenXR Vulkan True Parallel Eye Primary Recording TODO

Date: 2026-06-28

Status: active planning checklist

Related progress doc: `docs/work/progress/rendering/openxr-monado-vulkan-120hz-performance-2026-06-27.md`

Related flicker investigation: `docs/work/investigations/rendering/editor-origin-eye-camera-flicker-2026-06-28.md`

Related foveation/RVC design: `docs/work/todo/rendering/vr/retinal-visibility-cache-rendering-todo.md`

## Goal

Make Monado OpenXR + Vulkan rendering consistently exceed 120 Hz for both eyes and the editor view with the default render pipeline enabled. The render thread should do near-zero OpenXR per-frame init and minimal CPU recording work; next-frame OpenXR wait/begin/locate and visibility preparation may run from collect-visible while the render thread finishes the current frame.

Do not disable features to hit the number. Shadows, post-processing, sky, editor rendering, stereo preview/mirror, texture streaming, default render pipeline passes, and visible diagnostics must remain available unless a diagnostic run explicitly records that one variable is being isolated.

## Current Verdict

- The current batched eye path is faster than serial eye submit and the current single-pass stereo path in the Sponza/unit-testing scene.
- The new `OpenXrRenderPacingMode.CollectVisibleThread` mode is viable after the scoped prep-owner, duplicate-worker gate, and shutdown recheck fixes, but it does not remove the main bottleneck by itself.
- True left/right eye primary recording cannot safely be parallelized by wrapping the current helper in two tasks. `VulkanRenderer.TryRecordOpenXrEyeSwapchainCommandBuffer` temporarily mutates renderer-wide target/resource-planner/upload state.
- The next safe optimization is to split OpenXR eye recording into explicit per-eye/per-thread contexts, then compare true parallel eye primaries against single-pass stereo again.
- The left-eye/editor-origin flicker and the 120 Hz parallel-recording work are the same class of architecture problem: mutable per-view render state is still effectively global in the Vulkan renderer. Fix serial desktop + left-eye + right-eye state isolation first; parallel recording comes after the isolation is correct.

## Architectural Direction: View-Scoped Render State

The target architecture is a renderer with shared device-level services and explicit per-view/per-eye render contexts.

Shared renderer/device state may include:

- Vulkan instance/device/queues and queue-family capabilities.
- Immutable or explicitly synchronized pipeline/shader caches.
- Shared mesh buffers, material assets, texture assets, staging/upload services, descriptor layout caches, and resource retirement services with explicit ownership.
- OpenXR runtime/session ownership, while preserving one clear owner for `xrWaitFrame`, `xrBeginFrame`, `xrLocateViews`, `xrEndFrame`, acquire, wait, and release.

Mutable state must be scoped to the active desktop view, left eye, right eye, or a future stereo view context:

- Render target/swapchain image identity, image view, depth target, render area, viewport, scissor, and external target mode.
- Resource planner, resource allocator, barrier planner, planner revision/signature, and fast-path keys.
- Vulkan texture/image-view and framebuffer attachment identity for allocator-backed pipeline resources.
- Descriptor frame slots, descriptor publication state, dynamic uniform ring slices, frame-source bindings, command buffers, command-chain schedules, and cache keys.
- Camera/view/projection data, render-pipeline history resources, visibility results, and frame-op inputs.
- Foveation state: runtime capability, foveal center/gaze source, fixed or eye-tracked policy, shading-rate/density-map images, quality rings, and per-view fallback reason.

Correctness sequence:

1. Make serial desktop + left eye + right eye rendering stable with view-scoped state.
2. Prove the physical-image handle ping-pong and FBO rebuild loop are gone or intentionally bounded.
3. Prove the editor-origin/eye-camera flicker is gone in F5-equivalent and explicit CLI launches.
4. Then enable true parallel left/right primary recording on top of those isolated contexts.
5. Compare true parallel eye recording against the current batched eye path, serial eye submit, and single-pass stereo.

Single-pass stereo uses the same rule: it should be one explicit stereo render context with left/right subview data, not a shortcut that writes transient stereo state into renderer globals.

## View Render Mode Setting Contract

Replace the current VR stereo mode booleans with an enum setting that selects the rendering strategy explicitly. Keep legacy `SinglePassStereoVR` as a migration shim only until settings/schema generation has moved to the enum.

Proposed setting:

- `VR.ViewRenderMode`

Proposed enum:

- `SequentialViews`
  - Supported by Vulkan and OpenGL.
  - Records/renders each output view as its own view context.
  - Used as the correctness/reference path.
- `SinglePassStereo`
  - Supported by Vulkan and OpenGL.
  - Renders both eye subviews through one explicit stereo render context.
  - Must still keep desktop editing state separate when `VR.AllowDesktopEditing=true`.
- `ParallelCommandBufferRecording`
  - Vulkan only.
  - Builds left/right eye Vulkan command buffers in parallel after immutable view inputs and visibility are prepared.
  - Must fail visibly or report an explicit unsupported-mode diagnostic on OpenGL; do not silently fall back to sequential rendering.

Mode-selection rules:

- The selected mode controls how the VR eye views are rendered, not whether the desktop editor is active.
- `SequentialViews` and `SinglePassStereo` are compatibility modes and must remain available for comparison, debugging, and regression isolation.
- `ParallelCommandBufferRecording` is an optimization mode layered on top of the same per-view/per-eye state isolation required by the other modes.
- All modes share the same view/visibility data model so switching the enum changes scheduling/recording strategy, not scene semantics.
- Performance reports must record the selected `VR.ViewRenderMode`, render backend, pacing mode, `VR.AllowDesktopEditing`, and whether the run used an explicit fallback or unsupported-mode rejection.
- All modes must carry foveation metadata through the same view context model. Foveation must not depend on renderer-global "current eye" state.

## Desktop Editing And Visibility Contract

`VR.AllowDesktopEditing` controls whether the desktop view is an independent editor view or a desktop mirror/cyclopean runtime view.

When `VR.AllowDesktopEditing=true`:

- The desktop editor camera is a completely separate view from VR.
- The desktop view owns its own camera pose, frustum, render context, render-pipeline history, visible set, and collect-visible pass.
- VR eyes may still use a combined left/right-eye visibility pass for VR-only rendering, but desktop visibility must not be folded into that VR visible set.
- Desktop movement near the eye rig must not mutate left/right eye planner, image-view, framebuffer, descriptor, or command-buffer state.

When `VR.AllowDesktopEditing=false`:

- The desktop view is the runtime desktop presentation of VR, not an independent editor camera.
- The desktop view uses a smoothed cyclopean camera between the two eyes.
- Collect-visible must run once for the full runtime view group: left eye, right eye, and the smoothed cyclopean desktop view.
- The single collect-visible frustum must conservatively cover all three views: left eye, right eye, and the cyclopean desktop view.
- All three views consume the same visible set for that frame, while keeping per-view camera matrices, render targets, history resources, and command recording state distinct.
- This one-pass visibility rule applies regardless of `VR.ViewRenderMode`; the mode only changes how command buffers/render passes are produced from the prepared view group.

## Foveated Rendering Contract

Foveated rendering is part of the view architecture, not an optional global renderer toggle. Prioritize Vulkan implementation first, then support OpenGL where a reliable capability exists.

Proposed setting group:

- `VR.Foveation`
  - `Mode`: `Off`, `Fixed`, `EyeTracked`, `RuntimePreferred`
  - `QualityPreset`: `Conservative`, `Balanced`, `Aggressive`, or explicit ring/rate settings
  - `RequireRequested`: when true, unsupported foveation fails visibly instead of silently disabling

Backend priority:

- Vulkan primary path:
  - Prefer `VK_KHR_fragment_shading_rate` for attachment or dynamic fragment shading-rate based foveation.
  - Evaluate `VK_EXT_fragment_density_map` for runtimes/devices where density maps are a better fit.
  - Integrate OpenXR runtime foveation hints/extensions such as runtime-preferred fixed foveation, eye-tracked foveation, and quad-view/foveated view configurations when available.
  - Foveation attachments and rate images are per-view or per-subview resources owned by the view context/resource planner state.
- OpenGL support:
  - Support fixed foveation only through explicit, detected driver/runtime extension paths or a deliberate multi-resolution render path.
  - Do not hide unsupported OpenGL foveation behind an unreported quality fallback.
  - `SinglePassStereo` on OpenGL must preserve per-eye foveation metadata even if the effective backend path is `Off`.

Per-view foveation context must include:

- foveation mode requested and effective mode selected
- backend capability path and fallback reason
- fixed fovea or eye-tracked gaze source
- foveal center in view, projection, and render-target space
- foveal/guard/mid/peripheral region definitions
- shading-rate or density-map image identity, image view, layout, descriptor/framebuffer attachment state, and resource lifetime
- per-view quality metrics and debug overlay data

Visibility rules:

- Foveation must not shrink the conservative visibility frustum. Visibility for a view group still covers the full output views.
- Foveation may influence LOD, material quality, lighting budgets, shadow budgets, and retinal visibility cache bucketing only after the conservative visible set exists.
- When `VR.AllowDesktopEditing=false`, the one collect-visible pass still covers left eye, right eye, and cyclopean desktop; each consuming view can apply its own foveation quality policy during frame-op generation/recording.
- When `VR.AllowDesktopEditing=true`, the desktop editor view may run with foveation disabled or with an explicit editor preview policy, but it must not reuse eye-tracked foveation state as if it were an HMD eye.

Mode interactions:

- `SequentialViews`: each view records with its own foveation context.
- `SinglePassStereo`: the stereo context must support distinct left/right foveal centers and rate metadata; it cannot assume one shared foveal center unless the effective mode explicitly says so.
- `ParallelCommandBufferRecording`: worker inputs must include immutable per-eye foveation context and prebuilt/owned shading-rate or density-map resources.

## Ground Rules

- [x] Fix regressions before continuing performance work.
- [x] Treat editor/eye flicker and resource-planner/image-view ping-pong as correctness blockers before new performance optimization.
- [x] Keep validation evidence in `Build/_AgentValidation/<run>/` and durable findings in `docs/work/`.
- [x] Change one architectural variable per runtime iteration.
- [x] Keep diagnostics opt-in unless they are lightweight steady-state counters.
- [x] Do not hide OpenXR/Vulkan failures behind silent CPU or non-XR fallbacks.
- [x] Treat per-frame allocations in collect-visible, render submission, swap/present, and frame recording as bugs unless proven unavoidable.
- [x] Keep OpenXR API ownership explicit: only the current pacing owner may call `xrWaitFrame`, `xrBeginFrame`, `xrLocateViews`, `xrEndFrame`, acquire, wait, and release.
- [x] Do not move Vulkan resource creation/upload work off the render/renderer-owned path until there is an explicit handoff model and validation for it.
- [x] Do not silently remap unsupported `VR.ViewRenderMode` values. Reject or visibly diagnose `ParallelCommandBufferRecording` on non-Vulkan backends.
- [x] Do not silently disable explicitly requested foveation. Report the effective foveation mode and fallback reason in logs/profiler summaries.

## Mutable State Hazards To Remove Before Parallel Recording

The following shared state is currently touched by OpenXR eye recording or its downstream helpers and must not be shared unsafely between left/right workers or between eye workers and desktop/editor rendering:

- [x] Swapchain arrays and target identity:
  - `swapChainImages`
  - `swapChainImageViews`
  - `swapChainFramebuffers`
  - `_swapchainImageEverPresented`
  - `swapChainImageFormat`
  - `swapChainExtent`
- [x] Depth target fields:
  - `_swapchainDepthImage`
  - `_swapchainDepthMemory`
  - `_swapchainDepthView`
  - `_swapchainDepthFormat`
  - `_swapchainDepthAspect`
- [x] External target mode:
  - `_openXrExternalSwapchainRenderDepth`
  - `IsRenderingExternalSwapchainTarget`
  - `TryGetExternalSwapchainTargetRegion`
  - `AllowSynchronousResourceUploads`
- [x] Resource planner state:
  - `_resourcePlanner`
  - `_resourcePlannerRevision`
  - `_resourcePlannerSignature`
  - `_failedResourcePlannerSignature`
  - `_resourcePlannerFastPathKey`
  - `_frameOpResourcePlannerStates`
  - active frame-op planner key/switching fields
- [x] Allocator-backed texture and framebuffer cache state:
  - `VkImageBackedTexture` cached physical group/image/view/sampler/attachment views
  - `VkFrameBuffer` cached attachment signatures, attachment views, extents, and target descriptors
  - descriptor image-info generation and dirty/clean state derived from allocator-backed image views
  - per-physical-resource cleanup when a planner state is destroyed or replaced
- [x] Renderer-global active state:
  - `AbstractRenderer.Current`
  - active viewport/window/camera-derived render state
  - dynamic uniform ring buffer frame slot
  - descriptor frame slot capacity/floor
  - cached pipeline, descriptor, vertex/index buffer, viewport, and scissor binding state
- [x] OpenXR upload publication state:
  - `_openXrRecordedTextureUploadsForSubmit`
  - recorded upload cancellation/publication queues
  - retire queues drained by frame slot completion
- [x] Command-buffer and command-chain state:
  - primary command-buffer cache entries and signatures
  - secondary command-buffer generation and handles
  - command-pool allocation/free paths
  - command-chain schedules and per-frame command-chain reuse markers
- [x] Descriptor/frame-data state:
  - descriptor frame slots and mutable frame-source bindings
  - frame-data descriptor refresh/publish rules
  - descriptor-set retirement and pool ownership
- [x] Foveation/VRS state:
  - requested/effective foveation mode and fallback reason
  - per-view gaze/foveal center and quality ring data
  - Vulkan fragment shading-rate or fragment-density map images/views/layouts
  - OpenXR runtime foveation/quad-view metadata
  - OpenGL extension or multi-resolution foveation state when supported
  - foveation debug overlays and profiler counters

## Acceptance Criteria

- [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passes without new warnings from touched files.
- [x] Focused rendering tests pass:
  - [x] `OpenXrTimingPipelineContractTests`
  - [x] `VulkanCommandChainDataModelTests`
  - [x] new parallel eye recording context tests
  - [x] new upload-publication isolation tests
  - [x] new resource-planner isolation tests
- [ ] Vulkan/Monado editor launch with Unit Testing World, MCP, command chains, and parallel packet build enabled renders:
  - [x] desktop editor viewport
  - [x] left OpenXR eye preview
  - [x] right OpenXR eye preview
  - [x] final post-process output
- [x] No recurring steady-state Vulkan validation errors.
- [x] No recurring steady-state physical-image handle ping-pong for unchanged logical resources.
- [x] No recurring steady-state FBO rebuild loop for unchanged attachment signatures.
- [x] No descriptor binding failures or skipped draws.
- [x] No EndFrame failures in smoke runs.
- [x] Per-frame hot-path allocation bytes stay at zero in the 90-frame smoke summaries.
- [ ] Warm default-pipeline GPU p95 is below 8.33 ms in the target scene.
- [ ] Warm render-thread p95 is below 8.33 ms, with OpenXR prep and visibility work outside the render-thread critical path.
- [ ] True parallel eye primary recording is measured against:
  - [ ] current batched eye path
  - [ ] serial eye submit
  - [ ] current single-pass stereo
  - [ ] collect-visible pacing mode
  - [ ] dedicated pacing mode
- [ ] Editor-origin/eye-camera flicker investigation reports no active regression, or the flicker fix is applied first.
- [ ] F5-equivalent Unit Testing World launch and explicit CLI/MCP launch both render stable desktop, left-eye, and right-eye views.
- [x] `VR.ViewRenderMode` enum is exposed in startup settings/schema and replaces `SinglePassStereoVR` as the primary mode selector.
- [x] `SequentialViews`, `SinglePassStereo`, and `ParallelCommandBufferRecording` all select distinct validated paths, with `ParallelCommandBufferRecording` Vulkan-only.
- [ ] `VR.AllowDesktopEditing=true` runs desktop collect-visible independently from VR eye collect-visible.
- [ ] `VR.AllowDesktopEditing=false` runs exactly one collect-visible pass for left eye, right eye, and the smoothed cyclopean desktop view.
- [x] Foveation settings are represented as per-view/per-view-group context state, not renderer globals.
- [x] Vulkan foveation capability detection and effective-mode reporting are validated before enabling foveated rendering by default.
- [x] OpenGL foveation is supported only when a tested extension/path exists; otherwise explicit requests fail visibly or report a clear unsupported diagnostic.

## Phase 0: Evidence And Regression Gate

- [x] Update the active progress doc before starting code work.
- [x] Record `git status --short` in the run root.
- [x] Run the focused tests that currently pass before touching recording architecture.
- [ ] Run a short OpenXR Vulkan smoke with current code and save:
  - [x] smoke summary JSON
  - [x] latest log session path
  - [x] `log_vulkan.log`
  - [x] `log_rendering.log`
  - [x] `profiler-render-stats.ndjson`
  - [ ] any CPU/GPU dump paths
- [x] Confirm there are no active regressions from the previous image-view/pipeline-layout lifetime fixes.
- [x] Confirm repeated `DispatchCompute emitted with invalid render-graph pass index` warnings are still warnings only, or fix them first if they correlate with command-chain invalidation/flicker.
- [x] Count steady-state `Physical group image handle changed` and `Rebuilding framebuffer` warnings in the baseline logs.
- [ ] Record whether the F5-equivalent launch reproduces left-eye or editor-origin flicker.

## Phase 0.5: Fix Serial View-State Isolation Before Performance Work

- [x] Treat this as the shared prerequisite for both flicker elimination and true parallel eye recording.
- [x] Identify every allocator-backed pipeline texture and FBO wrapper currently shared by desktop, left-eye, and right-eye render contexts.
- [x] Decide the first safe cache boundary:
  - [ ] resource-planner-state-aware Vulkan render-object cache for allocator-backed textures/FBOs, or
  - [x] per-physical-image image-view cache plus per-attachment-signature framebuffer cache, with safe cleanup on physical resource destruction.
- [x] Ensure a desktop render context cannot make left/right eye wrappers observe different physical images for the same logical texture name unless the active context changes intentionally.
- [x] Ensure a left-eye render context cannot dirty right-eye or desktop framebuffer/image-view state.
- [x] Ensure resource planner state transitions do not mutate renderer-global texture/FBO wrappers during command recording.
- [x] Add focused tests for:
  - [x] desktop/left/right planner state separation
  - [x] no cross-eye logical resource aliasing through `VkImageBackedTexture`
  - [x] no cross-view `VkFrameBuffer` attachment-signature invalidation
  - [x] descriptor image-info generation scoped to the active view context
  - [x] cleanup of per-view/per-physical-resource image views and FBOs when a planner state is destroyed
- [ ] Re-run explicit CLI/MCP launch and F5-equivalent launch before moving to Phase 1.
- [ ] Gate exit on stable logs:
  - [x] physical-image handle changes occur only on intentional resize/resource-plan replacement/startup, not steady-state every frame
  - [x] FBO rebuilds occur only on intentional attachment/dimension changes/startup, not steady-state every frame
  - [ ] no user-visible desktop, left-eye, or right-eye flicker

## Phase 1: Document The Eye Recording Data Model

- [x] Draw the current call graph from `OpenXRAPI.Vulkan.cs` into `VulkanRenderer.OpenXR.cs`.
- [x] List every field read/written by `TryRecordOpenXrEyeSwapchainCommandBuffer`.
- [x] Mark each field as:
  - [x] immutable frame input
  - [x] per-eye target state
  - [x] per-thread recording state
  - [x] renderer-global state that must remain single-owner
  - [x] publication state that must be merged after successful submit
- [x] Add source-contract tests for the no-parallelization hazard so future refactors cannot reintroduce hidden global mutation.
- [x] Decide the minimal first boundary that lets eye target setup stop writing `swapChainImages` and depth fields globally.
- [x] Define the `VR.ViewRenderMode` enum in settings design and map legacy `SinglePassStereoVR=true` to `SinglePassStereo` during migration.
- [x] Define a backend capability resolver:
  - [x] Vulkan supports `SequentialViews`, `SinglePassStereo`, and `ParallelCommandBufferRecording`
  - [x] OpenGL supports `SequentialViews` and `SinglePassStereo`
  - [x] OpenGL rejects or visibly diagnoses `ParallelCommandBufferRecording`
- [x] Define view-group inputs for:
  - [x] independent desktop + VR eyes when `VR.AllowDesktopEditing=true`
  - [x] left eye + right eye + smoothed cyclopean desktop when `VR.AllowDesktopEditing=false`
- [x] Define `VR.Foveation` settings and the requested/effective foveation mode resolver.
- [x] Define backend capability resolution:
  - [x] Vulkan fragment shading rate
  - [x] Vulkan fragment density map
  - [x] OpenXR runtime foveation/quad-view hints
  - [x] OpenGL extension or multi-resolution foveation support where available

## Phase 2: Introduce `OpenXrEyeRenderTargetContext`

- [x] Add an immutable context object/struct containing:
  - eye index
  - acquired OpenXR swapchain image index
  - Vulkan image handle
  - Vulkan image view handle
  - image format
  - extent
  - depth image/view/format/aspect
  - external target region
  - command-chain image key
  - frame-data slot
  - resource-planner state key
- [x] Update target-begin/dynamic-rendering helpers to accept the context directly.
- [x] Update swapchain layout transitions to resolve the target image from the context instead of `swapChainImages`.
- [x] Update framebuffer/attachment diagnostics to print context identity.
- [x] Keep the old global path active until tests prove the context path is equivalent.
- [x] Add tests that left and right contexts cannot alias each other's image/view/depth/command key.

## Phase 2.5: Introduce `ViewRenderGroupContext`

- [x] Add an immutable per-frame view group that contains every output view that should share visibility.
- [x] Include per-view records for:
  - [x] left eye
  - [x] right eye
  - [x] desktop editor view when `VR.AllowDesktopEditing=true`
  - [x] smoothed cyclopean desktop view when `VR.AllowDesktopEditing=false`
- [x] Include a group visibility policy:
  - [x] independent desktop collect-visible plus VR eye collect-visible when `VR.AllowDesktopEditing=true`
  - [x] one combined left/right/cyclopean collect-visible when `VR.AllowDesktopEditing=false`
- [x] Build the combined runtime frustum from left eye, right eye, and smoothed cyclopean desktop matrices.
- [x] Store one immutable visible set per visibility group, then bind it to each consuming view without copying or mutating renderer-global state.
- [x] Add tests proving `VR.AllowDesktopEditing=false` performs exactly one collect-visible pass for all three runtime views.
- [x] Add tests proving `VR.AllowDesktopEditing=true` keeps desktop visible collection independent from VR eye collection.

## Phase 2.6: Introduce `ViewFoveationContext`

- [x] Add immutable per-view foveation context records to `ViewRenderGroupContext`.
- [x] Store requested and effective foveation mode, backend capability path, fallback reason, and quality preset.
- [x] Store fixed foveal center or eye-tracked gaze source independently for left eye, right eye, and desktop/cyclopean views.
- [x] For Vulkan, add resource-planner ownership for shading-rate/density-map attachments so they are scoped to the active view/planner context.
- [x] For OpenGL, add capability-gated placeholders for supported foveation paths and explicit unsupported diagnostics for missing paths.
- [x] Ensure foveation context is an input to frame-op generation and command recording, not read from renderer-global state.
- [x] Add tests proving `SinglePassStereo` can carry distinct left/right foveal centers.
- [x] Add tests proving `ParallelCommandBufferRecording` workers receive immutable foveation context.
- [x] Add tests proving visibility remains conservative when foveation is enabled.

## Phase 3: Split Resource Planner State Per Eye/View

- [x] Add a recording API that receives `FrameOpContext`/planner state explicitly.
- [x] Stop `EnterOpenXrResourcePlannerScope` from swapping renderer-global planner fields during command recording.
- [x] Maintain per-eye planner runtime state by resource-planner state key.
- [x] Ensure planner fast-path state is scoped by pipeline identity, viewport identity, target identity, and eye context.
- [x] Ensure allocator-backed texture/FBO resolution receives the view/planner context explicitly instead of consulting only the renderer's current global allocator.
- [x] Ensure descriptor and framebuffer cache keys include enough view/planner/physical-image identity to prevent desktop/left/right ping-pong.
- [x] Include foveation rate/density resources in planner state keys and framebuffer/rendering attachment identity.
- [x] Add tests for concurrent left/right planner preparation using distinct contexts.
- [x] Add diagnostics for planner context changes during eye recording.

## Phase 4: Split Command Pools And Command Buffer State

- [x] Add per-thread/per-eye command pools for OpenXR eye recording.
- [x] Add ownership labels to allocated command buffers.
- [x] Ensure primary command-buffer cache entries include:
  - [x] eye index
  - [x] OpenXR image index
  - [x] Vulkan image handle
  - [x] depth target generation
  - [x] resource-planner revision/signature
  - [x] frame-op signature
  - [x] secondary command-buffer handles and generations
  - [x] descriptor-frame-slot identity
- [x] Make primary cache lookup thread-safe without coarse locking around the whole record path.
- [x] Ensure cached primary command buffers are only reused after all referenced secondaries/descriptors/resources are still live.
- [x] Add tests for cache-key separation and stale-secondary invalidation.

## Phase 5: Isolate Frame Op Capture And Upload Publication

- [ ] Decide which work may run during collect-visible:
  - [ ] OpenXR next-frame wait/begin/locate
  - [ ] predicted pose cache update
  - [ ] predicted rig recalc
  - [ ] combined stereo visibility collect
  - [ ] frame-op capture if all inputs are immutable and no renderer-global state is touched
- [x] Keep synchronous texture uploads on renderer-owned execution until an explicit publication handoff exists.
- [x] Replace `_openXrRecordedTextureUploadsForSubmit` with per-eye publication buffers.
- [x] Merge publication buffers only after the batched submit completes successfully.
- [x] Cancel only the eye-local uploads that belong to a failed recording/submission.
- [x] Add tests for upload publication isolation on:
  - [x] left record failure
  - [x] right record failure
  - [x] submit failure
  - [x] device lost after recording
  - [x] successful batched submit

## Phase 6: Implement True Parallel Eye Primary Recording

- [x] Add a bounded eye-record worker scheduler.
- [x] Dispatch left and right eye primary recording after both contexts and frame-op inputs are prepared.
- [x] Avoid captured closures/allocations in the hot path.
- [x] Record worker start/end timings and owner thread IDs under a diagnostic flag.
- [x] Join both records before queue submit.
- [x] Preserve deterministic failure behavior:
  - [x] if one eye fails to record, do not submit either eye
  - [x] release acquired images according to OpenXR rules
  - [x] keep failure reasons visible in logs
- [ ] Compare first with dedicated pacing, then collect-visible pacing.
- [x] Gate this path behind `VR.ViewRenderMode=ParallelCommandBufferRecording`.
- [x] Keep `SequentialViews` and `SinglePassStereo` selectable throughout the implementation for A/B validation.

## Phase 7: Submit, Fence, And Timeline Safety

- [x] Review whether the current submit-and-wait path can be replaced by timeline/fence recycling without waiting one frame interval on the render thread.
- [x] Keep OpenXR acquire/wait/release order exact.
- [x] Ensure both recorded command buffers can be submitted together with correct ordering and resource lifetime.
- [x] Publish uploads and retire resources only after completion is known.
- [x] Add diagnostics for:
  - [x] queue submit time
  - [x] fence wait time
  - [x] completed frame slot
  - [x] skipped/cancelled upload count
  - [x] resource retirement flush count

## Phase 8: Integrate With Collect-Visible Prep

- [x] Keep `DedicatedThread` as the safe default until true parallel recording is stable.
- [x] Re-test `CollectVisibleThread` after recording state isolation lands.
- [ ] If frame-op capture moves into collect-visible, prove it does not race desktop/editor rendering.
- [x] Keep `xrEndFrame` and final layer submission on the OpenXR render path.
- [ ] Record render-thread CPU cost before/after collect-visible integration.

## Phase 9: Validation Matrix

- [x] Focused unit tests pass.
- [x] Editor build passes.
- [x] 90-frame OpenXR Vulkan smoke, dedicated pacing, `VR.ViewRenderMode=SequentialViews`.
- [x] 90-frame OpenXR Vulkan smoke, dedicated pacing, `VR.ViewRenderMode=SinglePassStereo`.
- [x] 90-frame OpenXR Vulkan smoke, dedicated pacing, `VR.ViewRenderMode=ParallelCommandBufferRecording`.
- [x] 90-frame OpenXR Vulkan smoke, collect-visible pacing, `VR.ViewRenderMode=SequentialViews`.
- [x] 90-frame OpenXR Vulkan smoke, collect-visible pacing, `VR.ViewRenderMode=SinglePassStereo`.
- [x] 90-frame OpenXR Vulkan smoke, collect-visible pacing, `VR.ViewRenderMode=ParallelCommandBufferRecording`.
- [x] 90-frame OpenXR Vulkan smoke with `VR.Foveation.Mode=Fixed` for each Vulkan view render mode.
- [ ] 90-frame OpenXR Vulkan smoke with `VR.Foveation.Mode=EyeTracked` or `RuntimePreferred` when runtime/device capabilities exist.
- [x] 90-frame OpenGL smoke, `VR.ViewRenderMode=SequentialViews`.
- [x] 90-frame OpenGL smoke, `VR.ViewRenderMode=SinglePassStereo`.
- [x] OpenGL `VR.ViewRenderMode=ParallelCommandBufferRecording` unsupported-mode diagnostic/rejection test.
- [x] OpenGL foveation unsupported-mode diagnostic/rejection test when no extension/path is available.
- [ ] `VR.AllowDesktopEditing=true` validation: independent desktop collect-visible plus VR collect-visible.
- [ ] `VR.AllowDesktopEditing=false` validation: one combined left/right/cyclopean collect-visible pass for all three runtime views.
- [x] Foveation validation: full-frustum visibility remains stable while per-view quality/rate metadata changes.
- [x] MCP captures for desktop, left preview, right preview, and final post-process output.
- [x] Log scan for validation errors, descriptor failures, skipped draws, EndFrame failures, and shutdown-only noise.
- [ ] RenderDoc capture if screenshots/logs do not prove the first failing pass.

## Phase 10: Cleanup And Default Selection

- [x] Remove transitional global target mutation from the OpenXR eye path.
- [ ] Delete obsolete diagnostics or keep them behind documented flags.
- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md`.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if render-pipeline invariants change.
- [x] Update unit-testing settings/schema generation to expose `VR.ViewRenderMode`.
- [x] Update unit-testing settings/schema generation to expose `VR.Foveation`.
- [x] Remove or deprecate direct runtime use of `SinglePassStereoVR` after migration.
- [ ] Update the active performance progress doc with final mode comparison.
- [ ] Choose the default mode based on measured evidence:
  - [ ] dedicated pacing plus true parallel recording
  - [ ] collect-visible pacing plus true parallel recording
  - [ ] a hybrid where collect-visible prepares immutable inputs and dedicated pacing owns OpenXR blocking calls

## Regression Checklist

- [ ] No eye output follows the editor camera instead of the HMD rig.
- [ ] No near-origin/editor-view flicker.
- [x] No black desktop capture regression.
- [ ] No black left/right preview regression.
- [ ] No over-bright/color-wrong final output regression without explanation.
- [x] No Vulkan image-view or pipeline-layout lifetime validation regression.
- [ ] No command-chain cache aliasing between eyes/images/views.
- [x] No per-frame allocation regression.
- [x] No hidden fallback from Vulkan/OpenXR to CPU or non-XR rendering.
