# Vulkan Frame Loop Performance TODO

Last Updated: 2026-06-17
Owner: Rendering
Status: In Progress
Target Branch: `vulkan-frame-loop-performance`

## Problem

The default render pipeline can execute quickly, but the Vulkan window frame
loop is currently paying large CPU-side costs after the pipeline has enqueued
its work.

Profiler evidence from the remote profiler tree:

| Scope | Time | Interpretation |
| --- | ---: | --- |
| `XRWindow.Renderer.RenderWindow` | 110 ms | Vulkan backend end-of-frame work. |
| `Vulkan.FrameLifecycle.RecordCommandBuffer` | 68 ms | Draining/sorting frame ops, planning resources/barriers, recording command buffers. |
| `Vulkan.FrameLifecycle.DrainRetiredResources` | 43 ms | Synchronous destruction of retired Vulkan objects on the render thread. |
| `Vulkan.FrameLifecycle.Submit` | 0.5 ms | Queue submission is not the bottleneck. |
| `Vulkan.FrameLifecycle.QueuePresent` | 0.06 ms | Presentation is not the bottleneck. |

These numbers are not yet labeled for build configuration. A 110 ms frame is
roughly 9 FPS, which is implausible for a settled Release scene and strongly
suggests this capture was Debug, with MCP and an attached profiler adding
overhead. Treat the magnitudes as directional only until Phase 0 reproduces them
in Release with instrumentation overhead subtracted; the relative ranking
(record + retirement dominate, submit/present do not) is the durable signal.

The profiler UI currently shows `DrainRetiredResources` as a separate root in
some views, so the `XRWindow.Renderer.RenderWindow` untracked time can look like
a mystery gap. Treat that as an attribution bug until proven otherwise.

## Current Problem Statement

As of the latest manual retest, the visual AO artifact and Vulkan frame-loop
cost did not meaningfully improve after the first mitigation pass. Treat the
black AO image and the slow loop as active, unresolved bugs.

Observed symptoms:

- Turning off the directional light exposes large black AO-shaped bands or
  blocks across the scene.
- The artifact is attributed to ambient occlusion, not the directional light
  itself.
- The actual `DefaultRenderPipeline` pass timings are comparatively reasonable.
- The Vulkan frame loop remains slow, with time still dominated by command
  buffer recording and/or retired-resource draining instead of submit/present.
- Recent logs showed `AmbientOcclusionTexture` physical image handles changing,
  resource-planner signature changes, and command-buffer dirty reasons such as
  `ApplyRenderParameters`, `SetPrepareResult`, render-area/crop changes,
  framebuffer binds, and `MarkIndexBuffersDirty`.

Working diagnosis:

- The AO artifact is not fixed by simply guarding deferred light combine against
  far-depth or invalid AO samples. That means the remaining failure is likely in
  the AO generation/resolve resource path itself: wrong render area, stale
  source texture, wrong physical image after planner replacement, incorrect
  layout/transition, or valid zero AO being written over real geometry.
- The frame-loop cost is not fixed by small allocation cleanup or by suppressing
  obvious no-op dirty calls. That means the dominant remaining cost is likely
  structural: steady-state command-buffer re-recording, resource-plan
  replacement, AO/light-combine FBO resource churn, dynamic data baked into
  command-buffer signatures, or per-frame mesh/index/preparation invalidation.
- AO and frame-loop slowness may be coupled. If AO replaces
  `AmbientOcclusionIntensityTextureName` or invalidates `LightCombineFBO`
  during frame execution, Vulkan must rebuild planning/recording state and then
  drain retired physical resources.

## Tried So Far

These changes are useful instrumentation or cleanup, but they did not resolve
the reported user-visible problem.

- Added Vulkan frame-loop telemetry through stats packets, profiler UI,
  profiler sender, NDJSON capture, and measurement scripts. Result: better
  attribution, no direct performance fix.
- Added lifecycle sub-scope timings for sample timing queries, retired-resource
  drain, acquire bridge submit, swapchain wait, dynamic uniform ring reset,
  command-buffer record, submit, trim, and present. Result: confirmed the slow
  work is not submit/present.
- Added frame-op census, command-buffer cache outcome counters, dirty reason
  aggregation, retired-resource drain counters, resource generation logs, and
  resource-planner signature change logs. Result: exposed churn, did not remove
  it.
- Removed retired buffer/image drain `HashSet` allocations and reduced several
  command-recording allocations in frame-op sorting and secondary-bucket setup.
  Result: lower overhead in those helpers, but no visible change to the
  dominant frame-loop cost.
- Added resource-generation stability tests for structural render profile
  inputs and default-pipeline resource feature masks. Result: guards against one
  churn class, but the live issue persists.
- Added `Measure-VulkanFrameLoop.ps1` and extended
  `Measure-GameLoopRenderPipeline.ps1` with Vulkan lifecycle/resource-plan
  columns and a steady-state churn failure switch. Result: useful for the next
  measured run, no runtime fix by itself.
- Added AO guards in `DeferredLightCombine.fs` and
  `DeferredLightCombineStereo.fs` so far-depth pixels do not sample AO and
  NaN/Inf AO samples become neutral. Result: user retest reports no visible
  improvement, so the remaining black artifact is probably valid zero/stale AO
  written before light combine rather than a combine-only invalid-sample issue.
- Stopped diagnostic detail-string changes in `VkMeshRenderer.SetPrepareResult`
  from dirtying command buffers. Result: reduces one suspect invalidation path,
  but did not resolve the frame-loop cost.
- Stopped normal Vulkan render-state and FBO binding transitions from globally
  dirtying command buffers; frame-op signatures already include that state.
  Result: expected to reduce forced dirty reasons, but user retest reports no
  meaningful change, so command buffers are likely still re-recorded due to
  frame-op signature changes, planner revisions, mesh/index dirtying, or
  resource churn.

What these attempts rule out:

- This is not primarily a queue submit or present bottleneck.
- This is not fixed by small hot-path allocation cleanup alone.
- This is not fixed by treating invalid AO samples as neutral at light combine.
- This is not solely caused by the most obvious no-op dirty calls.

Next useful investigation:

- Capture/export `AmbientOcclusionTexture`, `GTAORawTexture`, and
  `GTAOBlurIntermediateTexture` for the broken frame and verify where the black
  pattern first appears.
- Confirm whether `AmbientOcclusionTexture` and `LightCombineFBO` are replaced
  after command-buffer recording has begun or between frames in steady state.
- Compare command-buffer cache dirty reason counts before/after the no-op dirty
  suppression to identify the remaining re-record trigger.
- Inspect frame-op signatures across two visually identical frames to determine
  whether dynamic camera/model/material values are still forcing structural
  re-record.
- Inspect `VulkanRetiredResourcePlanReplacements` and resource-planner signature
  components for the same retest to determine whether AO resource churn is still
  causing physical plan replacement.

## Goal

Reduce Vulkan frame-loop CPU time as aggressively as possible while preserving
correctness and visible diagnostics for unsupported GPU paths.

Primary targets for a settled editor scene in Release:

- `Vulkan.FrameLifecycle.RecordCommandBuffer` median below 2 ms.
- `Vulkan.FrameLifecycle.RecordCommandBuffer` p95 below 5 ms.
- `Vulkan.FrameLifecycle.DrainRetiredResources` median below 0.25 ms.
- No steady-state `DeviceWaitIdle` or resource-plan replacement during normal
  camera movement.
- No per-frame command-buffer rebuild caused only by camera transforms, object
  transforms, material constants, or dynamic viewport/scissor state.
- Vulkan frame-loop CPU overhead within 20% of OpenGL for the same scene after
  excluding actual GPU work.

Stretch target:

- Static scene with moving camera records only dynamic data updates plus a small
  set of secondary command buffers that truly changed; most command recording is
  reused or generated from stable cached batches.

Detailed refactor design:

- `docs/work/design/rendering/vulkan-parallel-command-chain-refactor-design.md`

## Implementation Notes

2026-06-16:

- Created branch `vulkan-frame-loop-performance`.
- Added `VulkanFrameLoopTelemetryData` and surfaced it through
  `Engine.Rendering.Stats`, `ProfilerStatsPacket`, the in-process editor data
  source, UDP profiler sender, NDJSON capture, and `ProfilerPanelRenderer`.
- Instrumented frame-op census, command-buffer cache outcomes/dirty reasons,
  and retired-resource drain counts.
- Removed per-drain `HashSet` allocations from Vulkan retired buffer/image
  cleanup, relying on the existing frame-slot retirement de-duplication sets.
- Replaced `VulkanRenderGraphCompiler.SortFrameOps` LINQ/anonymous-object sort
  with pooled struct sort keys and pooled scheduling first-occurrence arrays.
- Avoided allocating `secondaryBucketByStart` for no-bucket and tiny-bucket
  command-recording cases.
- Validation completed: Debug editor build, profiler protocol roundtrip tests,
  Release editor build, profiler protocol roundtrip tests, and Vulkan frame-op
  sort contract tests.
- Release build caveat: existing warnings remain in `Build/Submodules/OscCore-NET9`
  and `XREngine.Runtime.Core`; this slice did not introduce warnings in touched
  files.
- Validation caveat: running the broader `VulkanDeferredProbeGiFixesTests`
  fixture surfaced two pre-existing descriptor source-inspection failures
  unrelated to this frame-loop slice.
- Remaining hard gate: capture the Release baseline before larger behavior
  changes such as dynamic uniform re-upload, command-buffer signature split, and
  secondary-command-buffer cache reuse.
- Investigated the 2026-06-16 editor screenshot where disabling the directional
  light exposed large black AO bands. The logs showed `AmbientOcclusionTexture`
  physical handles changing and command buffers being marked dirty thousands of
  times per second by no-op render-state application.
- Added AO resilience in `DeferredLightCombine.fs` and
  `DeferredLightCombineStereo.fs`: deferred combine now rejects far-depth pixels
  before sampling AO and treats NaN/Inf AO samples as neutral. This prevents
  invalid AO data from blacking out empty/background pixels, but does not hide a
  real AO pass that writes valid zero over scene geometry.
- Made Vulkan render-state application change-aware and stopped normal recorded
  render-state/FBO binding transitions from globally dirtying command buffers.
  Frame-op signatures already include viewport/scissor/depth/blend/cull/target
  state, so those signatures now drive re-recording instead of every pass
  transition forcing all swapchain command buffers dirty.
- Stopped mesh prepare diagnostics from dirtying command buffers on detail-only
  string changes; only readiness/result changes invalidate cached commands.
- New validation for this slice:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  passed with 0 warnings/errors.
- Remaining AO/performance root cause: AO resources and `LightCombineFBO` are
  still command-owned in parts of the pipeline. The old TODO in
  `DefaultRenderPipeline.CommandChain.cs` is still true: AO mode refresh can
  replace `AmbientOcclusionIntensityTextureName` and invalidate light combine.
  That is the next structural fix if black AO remains after this guard.

## Relevant Files

- `XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.Core.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Drawing.ResourceRetirement.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanResourceAllocator.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline*.cs`
- `XREngine.Runtime.Rendering/Rendering/Resources/*`
- `Tools/Measure-GameLoopRenderPipeline.ps1`
- `docs/architecture/rendering/vulkan-renderer.md`
- `docs/architecture/rendering/default-render-pipeline-notes.md`

## Invariants

- No resource is destroyed while an in-flight command buffer can reference it.
- Resize, AA/MSAA/HDR/profile changes may rebuild resources, but ordinary camera
  movement must not.
- Pipeline resource layout and barrier planning are structural; dynamic uniform
  values are not structural.
- Frame-op ordering must preserve render-graph dependencies.
- GPU paths must not silently fall back to CPU paths.
- New render hot-path code must avoid per-frame heap allocations unless a
  measured, documented exception is unavoidable.
- Optimizations must be validated with visible output, logs, and profiler data.

## Phase 0 - Measurement And Attribution

- [x] Create the dedicated working branch for this todo before any other work.
- [ ] Create a focused baseline scene for this issue: Vulkan, default pipeline,
  editor ImGui, one camera, deterministic content, no resizing.
- [x] Add a `Measure-VulkanFrameLoop.ps1` wrapper or extend
  `Measure-GameLoopRenderPipeline.ps1` to emit a dedicated Vulkan frame-loop
  summary.
- [ ] Capture Release and Debug baselines with warmup and capture windows.
- [ ] Record median, p95, max, and worst-frame call tree for:
  `WaitFrameSlot`, `SampleTimingQueries`, `DrainRetiredResources`,
  `AcquireNextImage`, `AcquireBridgeSubmit`, `WaitSwapchainImage`,
  `ResetDynamicUniformRing`, `RecordCommandBuffer`, `Submit`, `TrimStaging`,
  and `QueuePresent`.
- [ ] Fix profiler hierarchy attribution so every
  `Vulkan.FrameLifecycle.*` scope nests under `XRWindow.Renderer.RenderWindow`.
- [x] Add a per-frame lifecycle row to `profiler-render-stats.ndjson`.
- [x] Add counters for frame ops: total ops, clears, mesh draws, indirect draws,
  blits, compute ops, swapchain writers, FBO writers, pass count, context count,
  and unique render targets.
- [x] Add initial counters for command-buffer cache outcomes:
  clean reuse, recorded, frame-op signature dirty, resource-plan/planner dirty,
  profiler-state dirty, and forced dirty.
- [ ] Extend command-buffer cache counters to split data-only dirty and
  swapchain dirty after dynamic-data and swapchain invalidation are separated.
- [x] Add counters for retired-resource drains:
  descriptor pools, pipelines, framebuffers, buffers, buffer memories, images,
  image views, samplers, image memories, and total retired VRAM bytes.
- [x] Add profiler-visible aggregation for every `MarkCommandBuffersDirty()`
  call site by caller/reason.
- [x] Add once-per-second log output for aggregated
  `MarkCommandBuffersDirty()` reasons.
- [x] Add explicit logging for every resource-generation request/commit/retire
  reason with generation key deltas.
- [x] Add explicit logging for every resource-planner signature change with
  old/new signature components.
- [x] Add a validation mode that fails the run if steady-state camera movement
  causes resource generation churn.

Acceptance criteria:

- [ ] The profiler tree explains all of `XRWindow.Renderer.RenderWindow` time.
- [ ] A single summary table shows which reason dirtied command buffers each
  frame.
- [ ] A single summary table shows which resources were retired and destroyed
  each frame.
- [ ] Baseline data is captured before behavior changes.
- [ ] The Problem-section magnitudes are reproduced (or corrected) in a Release
  build with instrumentation overhead subtracted. This is a hard gate: if
  Release steady-state does not show record/retirement as the dominant cost, the
  later phases are re-prioritized from the corrected numbers before any behavior
  change.

## Phase 1 - Stop Resource Churn First

- [ ] Identify whether `DrainRetiredResources` spikes are caused by
  `RenderResourceGeneration` commits, Vulkan resource-plan replacement, texture
  upload/resize churn, ImGui buffers, descriptor-pool churn, or pipeline churn.
- [x] Inspect `VulkanRetiredResourcePlanReplacements`,
  `VulkanRetiredResourcePlanImages`, and `VulkanRetiredResourcePlanBuffers` for
  the slow frames.
- [ ] Verify that `XRRenderPipelineInstance.EnsureResourceGenerationForCurrentFrame`
  does not request `FrameProfileChanged` during camera-only movement.
- [x] Verify that `DefaultRenderPipeline.BuildResourceFeatureMaskForGenerationKey`
  is stable for the same settings and scene.
- [ ] Verify that viewport `InternalWidth` and `InternalHeight` are stable when
  TSR/DLSS/XeSS are enabled.
- [ ] Audit AO, bloom, motion blur, depth of field, temporal, atmosphere, fog,
  transparency, and debug FBO paths for active-registry mutation during frame
  execution.
- [ ] Complete migration of stable command-owned default-pipeline FBOs/textures
  to generation-declared resources where resource identity is currently changing
  during frame execution.
- [ ] Stop AO and light-combine paths from replacing
  `AmbientOcclusionIntensityTextureName` or `LightCombineFBOName` mid-frame.
- [ ] Separate resource allocation signatures from active-pass/barrier
  signatures so toggling a no-op pass does not retire physical images.
- [ ] Ensure `BuildMergedFrameOpRegistry` returns an existing registry whenever
  possible instead of creating a merged registry for stable single-pipeline
  frames.
- [ ] Cache merged registries by source registry identities and resource
  generation stamps when multi-pipeline frames require merging.
- [ ] Do not rebuild physical plans when only pass order/barrier metadata
  changes and resource descriptors are identical.
- [ ] Add a regression test that camera movement does not commit a new resource
  generation.
- [ ] Add a regression test that a steady default-pipeline frame does not replace
  the Vulkan resource plan.

Acceptance criteria:

- [ ] In a settled scene, `DrainRetiredResources` usually destroys zero Vulkan
  handles.
- [ ] Normal camera movement does not produce `ResourcePlanReplacement`.
- [ ] Normal camera movement does not call `DeviceWaitIdle`.
- [ ] Generation commits occur for resize/settings/profile changes only.

## Phase 2 - Replace Synchronous Destruction Bursts

- [ ] Before building fixes, confirm with instrumentation which retirement
  sub-cause dominates the measured time: plan-replacement `DeviceWaitIdle`
  stalls, descriptor-pool churn, or pipeline churn. The remedy differs per cause
  (timeline retirement vs pool reuse vs pipeline prewarm), so do not build all
  three speculatively.
- [ ] Remove steady-state `DeviceWaitIdle` from resource-plan replacement.
- [ ] Replace plan-replacement idle waits with timeline-value retirement of old
  allocators.
- [ ] Retire whole allocator generations with the timeline value that last used
  them.
- [ ] Drain old allocator generations incrementally after the relevant timeline
  value is reached.
- [ ] Add a per-frame destruction budget for expensive Vulkan object destruction.
- [ ] Allow the renderer to carry safe retired handles across frames rather than
  destroying a large backlog in one frame.
- [ ] Split retirement queues by object type and cost so image/image-view memory
  destruction can be budgeted separately from cheap handle destruction.
- [ ] Reuse descriptor pools where safe instead of destroying and recreating them
  for stable materials/compute dispatches.
- [ ] Reuse framebuffers and image views for generation-stable attachments.
- [ ] Reuse ImGui vertex/index buffers or grow-only ring buffers so UI does not
  retire buffers every frame.
- [ ] Add high-water diagnostics for retired queues so leaks/backlogs are
  visible.
- [ ] Add a shutdown/resize path that can still force-flush safely after waiting
  idle.

Acceptance criteria:

- [ ] No single `DrainRetiredResources` steady-state frame exceeds 1 ms in
  Release.
- [ ] Large resize/settings transitions may defer cleanup, but do not hitch the
  next interactive frame by tens of milliseconds.
- [ ] Shutdown and device-loss cleanup remain correct.

## Phase 3 - Split Structural Command State From Dynamic Data

This phase and Phase 5 share one mechanism. A command buffer can only be reused
across frames if the per-draw matrices and camera data it references are updated
out-of-band every frame instead of being baked at record time. Phase 3 in
isolation (just removing fields from the signature) would skip recording while
stale uniform data is still bound, producing incorrect rendering. Land the
dynamic re-upload path first, validate it, and flip the signature last. Schedule
Phase 3 and Phase 5 together.

Note: `VulkanDynamicUniformRingBuffer` already exists and is reset per frame in
`Vulkan.FrameLifecycle.ResetDynamicUniformRing`, but `GetDynamicUniformRingBuffer`
is currently never called. Per-draw matrices today flow through per-draw UBOs
written during command recording. Adopting this scaffold is the foundation of the
phase, not new infrastructure.

Build and validate the dynamic data path first:

- [ ] Route per-draw matrices and camera data through the existing
  `VulkanDynamicUniformRingBuffer` (adopt `GetDynamicUniformRingBuffer`) instead
  of UBOs baked during command recording.
- [ ] Persist each draw's ring offset (per swapchain image) at record time so a
  reused command buffer keeps binding the same dynamic-offset slot.
- [ ] Re-write current per-draw matrices and camera data to those persisted ring
  offsets every frame (retained-mode uniform update), guaranteeing no in-flight
  command buffer reads a slot being overwritten for another frame slot. This is
  the make-or-break correctness detail of the whole effort.
- [ ] Gate the new path behind a flag and run it alongside the existing
  baked-UBO path so output can be diffed before the structural signature changes.
- [ ] Validate identical rendered output (MCP screenshots from two camera
  positions plus RenderDoc on a sampled frame) before proceeding.

Then split structural state from dynamic data:

- [ ] Define a `FrameOpStructuralSignature` that excludes dynamic matrices,
  camera vectors, material constants, previous matrices, time, jitter, and other
  per-frame uniform data.
- [ ] Define a separate `FrameOpDataSignature` or dynamic-data dirty stamp for
  diagnostics only.
- [ ] Make viewport and scissor dynamic state where possible so viewport-size
  data does not force pipeline or command-buffer rebuilds unless extent changes.
- [ ] Make blend/depth/stencil/cull state structural only when it changes the
  pipeline or recorded command sequence.
- [ ] Ensure descriptor-set changes distinguish between descriptor layout changes
  and buffer-offset/data changes.

Flip the signature last, behind the same flag:

- [ ] Remove model/view/projection/camera values from command-buffer cache
  invalidation in `VkMeshRenderer.ComputeFrameOpsSignature` only after the
  retained-mode re-upload path is validated.
- [ ] Add a test that moving the editor camera changes dynamic uniform data but
  does not dirty the command buffer structurally.
- [ ] Add a test that moving scene objects changes dynamic data but does not
  structurally dirty commands when draw membership and material topology are
  unchanged.

Acceptance criteria:

- [ ] Camera-only movement no longer forces `RecordCommandBuffer`.
- [ ] Object transform-only movement no longer forces `RecordCommandBuffer`.
- [ ] Dynamic uniform ring updates remain visible and correct, with no in-flight
  overwrite hazard across frame slots.

## Phase 4 - Make Command Recording Allocation-Light

- [x] Replace LINQ in `VulkanRenderGraphCompiler.SortFrameOps` with an
  allocation-free or pooled stable sort.
- [x] Replace anonymous sort records with a reusable struct sort key array.
- [ ] Cache pass order lookup tables by pass metadata identity/revision.
- [x] Replace `Dictionary<int, int> firstOccurrence` with pooled arrays or a
  small stack-friendly map for common scheduling-identity counts.
- [x] Avoid allocating `secondaryBucketByStart` when there are no secondary
  buckets or when bucket count is tiny.
- [ ] Pool `SecondaryRecordingBucket` lists.
- [ ] Pool or persist per-frame dictionaries in `RecordCommandBuffer`:
  swapchain writes by pipeline, writer labels/details, pass indices, pipeline
  names, mesh draw slots, FBO layout tracking.
- [ ] Remove per-frame string building from hot paths; keep once-per-second
  diagnostic formatting behind enabled logging gates.
- [ ] Replace `HashCode`-based signatures in hot loops with deterministic
  allocation-free hashing over numeric IDs where practical.
- [ ] Assign stable integer type IDs for frame-op kinds instead of hashing type
  names.
- [ ] Avoid `ToArray()` in `DrainFrameOps` by double-buffering frame-op lists or
  using pooled arrays.
- [ ] Pre-size all hot dictionaries/lists from previous-frame counts.
- [ ] Add allocation counters for `RecordCommandBuffer` and fail perf validation
  if it allocates in steady state.

Acceptance criteria:

- [ ] `RecordCommandBuffer` allocates zero or near-zero bytes in steady state.
- [ ] Sorting/planning overhead is measurable below 0.5 ms for typical editor
  scenes.

## Phase 5 - Cache And Reuse Command Work

- [ ] Split command recording into stable batches by render graph pass, render
  target, pipeline/material, and draw topology.
- [ ] Cache secondary command buffers for batches whose structural signature did
  not change.
- [ ] Re-record only dirty batches and execute cached secondary command buffers
  from the primary command buffer.
- [ ] Expand secondary-command-buffer eligibility beyond `BlitOp` and
  `IndirectDrawOp` after correctness validation.
- [ ] Add mesh-draw secondary buckets for stable material/pipeline groups.
- [ ] Add compute secondary buckets only where Vulkan rules and resource
  dependencies make reuse safe.
- [ ] Keep primary command buffer recording minimal: acquire image layout,
  begin/end high-level passes, execute secondary buffers, transition present.
- [ ] Make profiler query insertion compatible with cached secondary command
  buffers by disabling reuse only for profiled batches or by using stable query
  ranges.
- [ ] Add a debug overlay/counter showing reused vs re-recorded batches.
- [ ] Add tests for cached secondary reuse across camera movement, object
  movement, material changes, visibility changes, and resize.

Acceptance criteria:

- [ ] Static scene plus moving camera reuses most secondary command buffers.
- [ ] Visibility/material/topology changes re-record only affected batches.
- [ ] GPU profiler mode remains correct, even if it intentionally disables some
  reuse.

## Phase 6 - Resource Planner And Barrier Planner Fast Paths

- [ ] Split `UpdateResourcePlannerFromContext` into resource descriptor planning,
  physical allocation planning, render graph compilation, and barrier planning.
- [ ] Cache compiled render graphs by pass metadata identity/revision plus active
  feature mask.
- [ ] Cache barrier plans by compiled graph, queue ownership config, and resource
  plan revision.
- [ ] Avoid physical allocator rebuild if resource descriptors and extent context
  are unchanged.
- [ ] Replace ordered LINQ signature walks with cached descriptor signatures in
  `RenderResourceRegistry`.
- [ ] Maintain registry revision stamps for texture/FBO/buffer descriptor
  changes.
- [ ] Maintain pass metadata revision stamps for graph changes.
- [ ] Add a cheap check before full signature computation.
- [ ] Add diagnostics that name the exact descriptor/pass/queue field that caused
  planner invalidation.
- [ ] Ensure filtered active-pass metadata cannot accidentally remove resources
  required later in the frame.

Acceptance criteria:

- [ ] Resource planner fast path is O(1) or close to O(1) for unchanged
  descriptor/pass metadata.
- [ ] Barrier planner rebuilds only when dependencies or resource layouts change.
- [ ] Planner invalidation reasons are human-readable.

## Phase 7 - State, Descriptor, And Pipeline Binding Efficiency

- [ ] Audit `RecordCommandBuffer` for redundant `CmdBindPipeline`,
  `CmdBindDescriptorSets`, vertex/index buffer binds, viewport/scissor sets, and
  push constants.
- [ ] Extend command-buffer bind-state tracking so redundant binds are skipped
  across compatible ops.
- [ ] Cache descriptor sets by layout plus resource identity and dynamic-offset
  class.
- [ ] Use dynamic offsets or ring-buffer addresses for per-frame data instead of
  allocating descriptor pools.
- [ ] Ensure material uniform snapshots do not allocate dictionaries per compute
  dispatch in steady state.
- [ ] Add descriptor-pool reuse metrics and warnings when pools churn.
- [ ] Prewarm or asynchronously compile pipelines so pipeline retire/recreate
  does not show up during interactive frames.
- [ ] Add pipeline-cache hit/miss counters to the Vulkan profiler stats packet.

Acceptance criteria:

- [ ] Descriptor pool destruction is zero in a settled scene.
- [ ] Pipeline retirement is zero in a settled scene.
- [ ] Redundant bind counts drop across representative scenes.

## Phase 8 - Parallelize Remaining Recording Work

- [ ] After structural caching and allocation cleanup, identify remaining
  recording work that still scales with draw count.
- [ ] Use worker threads to record independent secondary command buffers for
  render-pass-compatible batches.
- [ ] Create per-worker command pools with reset semantics appropriate for
  secondary buffers.
- [ ] Ensure all Vulkan object access during parallel recording is immutable or
  properly synchronized.
- [ ] Precompute batch command data before worker dispatch to avoid locks during
  recording.
- [ ] Add a setting/env var to disable parallel secondary recording for bisecting.
- [ ] Add profiler scopes per worker/bucket and aggregate wait time separately
  from recording time.

Acceptance criteria:

- [ ] Multi-core recording reduces p95 `RecordCommandBuffer` for high draw-count
  scenes without increasing hitches.
- [ ] Parallel recording can be disabled with identical rendered output.

## Phase 9 - Resize And Profile-Change Paths

- [ ] Keep the active generation rendering while pending generations prepare.
- [ ] Coalesce interactive resize and internal-resolution changes more
  aggressively.
- [ ] Avoid rebuilding swapchain resources for scene-panel size noise that does
  not change effective pixel extent.
- [ ] Add explicit "resize active" profiler state so resize hitches are not
  mistaken for steady-state regressions.
- [ ] Ensure swapchain recreation dirties only swapchain-dependent command
  buffers/batches.
- [ ] Validate TSR/DLSS/XeSS render-scale changes rebuild only the resources
  whose extents actually changed.
- [ ] Add tests for resize, HDR toggle, AA toggle, MSAA sample changes, AO mode
  changes, and debug view toggles.

Acceptance criteria:

- [ ] Resize hitches are bounded and clearly attributed.
- [ ] Settings changes rebuild the minimum necessary resource set.

## Phase 10 - Validation Matrix

- [x] Build Debug editor after each phase:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
- [x] Build Release editor before perf comparison.
- [x] Run targeted unit tests for profiler protocol and render graph ordering.
- [ ] Run targeted unit tests for runtime rendering host services, resource
  lifecycle, and Vulkan planner.
- [ ] Run the Unit Testing World with Vulkan and MCP enabled.
- [ ] Capture MCP screenshots from at least two camera positions to confirm
  non-stale rendering.
- [ ] Inspect `log_vulkan.log`, `log_rendering.log`, `profiler-fps-drops.log`,
  and `profiler-render-stalls.log`.
- [ ] Use RenderDoc when visual correctness or resource transitions are
  ambiguous.
- [ ] Compare OpenGL and Vulkan frame-loop stats on the same scene.
- [ ] Compare Debug and Release to separate instrumentation overhead from real
  renderer cost.
- [ ] Verify no forbidden CPU fallback events appear on GPU paths.
- [ ] After completion and validation, merge the working branch back into
  `master`.

Acceptance criteria:

- [ ] Every optimization phase has before/after numbers.
- [ ] Visual output remains correct.
- [ ] Logs contain no new Vulkan validation errors.
- [ ] No new compiler warnings are introduced.

## Suggested Implementation Order

This order front-loads cheap, low-risk, independently valuable wins (allocation
cleanup and churn reduction) and defers the risky signature/caching redesign
until the Release baseline is confirmed.

1. Confirm the Release baseline (hard gate), fix profiler attribution, and add
   dirty/retirement counters.
2. Stop steady-state resource generation and resource-plan churn.
3. Remove hot-path allocations in sorting, planning, and recording.
4. Replace synchronous plan-replacement idle waits with timeline retirement.
5. Land the per-draw dynamic uniform re-upload path, validate it, then remove
   dynamic matrices/camera data from command-buffer structural signatures.
6. Cache secondary command buffers for structurally stable batches (shares the
   dynamic re-upload mechanism with step 5).
7. Optimize descriptor/pipeline binding churn.
8. Parallelize the recording that remains.
9. Harden resize/settings transitions.
10. Update architecture docs with the final Vulkan frame-loop contract.

## Open Questions

- Which features are still mutating the active default-pipeline registry during
  frame execution?
- Should Vulkan compile the full declared render graph every frame profile, or a
  filtered active graph plus stable allocation graph?
- What minimum dynamic state set should all Vulkan mesh pipelines support?
- How should GPU profiler query insertion interact with cached secondary command
  buffers?
- Can old resource allocator generations be retired by timeline value without a
  conservative `DeviceWaitIdle` in all current swapchain/recreate paths?
- Should ImGui rendering use its own persistent buffer strategy independent of
  engine mesh buffers?
