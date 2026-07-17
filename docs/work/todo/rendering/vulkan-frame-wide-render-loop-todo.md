# Vulkan Frame-Wide Render Loop TODO

Last updated: 2026-07-16

Owner: Rendering / Vulkan / XR

Status: Proposed

Related documentation and work:

- [Vulkan Render Loop Design](../../design/rendering/vulkan-render-loop-design.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [GPU-Driven Occlusion Culling Architecture TODO](gpu/gpu-driven-occlusion-culling-architecture-todo.md)
- [VR Rendering Performance Contract TODO](optimization/vr-rendering-performance-contract-todo.md)
- [Vulkan Parallel Command Chain Refactor Design](../../design/rendering/vulkan-parallel-command-chain-refactor-design.md)
- [Render Submission Performance Debug Plan](../../design/rendering/render-submission-perf-debug-plan.md)
- [Vulkan Desktop Frame Loop Decomposition TODO](vulkan-desktop-frame-loop-decomposition-todo.md)
- [Vulkan Runtime Code Organization TODO](vulkan-runtime-code-organization-todo.md)
- [OpenXR Runtime Code Organization TODO](vr/openxr-runtime-code-organization-todo.md)
- [Vulkan Dynamic Rendering And Modern Backend Completion TODO](vulkan-dynamic-rendering-migration-todo.md)

## Goal

Implement a frame-wide Vulkan render architecture in which one immutable set of
logical views drives visibility, render batching, histories, graph scheduling,
recording, and output submission.

The target should support the common six-view workload described by the design
document:

1. XR outer left and right.
2. XR inset left and right when a quad-view runtime is active.
3. Desktop/editor output.
4. A secondary camera or published texture.

The implementation must remain generalized rather than hard-coded to exactly
six views. It should use the existing `RenderFrameViewSet` limit and stable view
identities, capability-gate optional quad-view behavior, and omit inactive
outputs without changing the meaning of the remaining view IDs.

The end state should provide:

- One authoritative frame view set.
- One exact masked BVH/frustum traversal for the active main-camera views.
- Frame-scoped visibility candidates reused by render passes.
- Two multiview XR render batches for a typical quad-view frame, plus ordinary
  desktop and secondary batches.
- One outer-FoV Hi-Z hierarchy per physical XR eye when the runtime view
  relationship makes that reuse conservative.
- Deadline-aware graph scheduling that cannot let optional desktop, secondary,
  capture, or debug work delay OpenXR submission.
- Resource-versioned graph compilation with explicit validation and diagnostics.

## Existing Strengths To Preserve

This work is an orchestration and visibility refactor, not a replacement for
the production backend. Preserve these existing contracts:

- `CpuDirect` uses the CPU scene hierarchy, with the CPU BVH as the default.
- GPU indirect and meshlet strategies request the internal `GPUScene` BVH.
- `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` do not read GPU draw,
  visibility, or count data back on the steady-state hot path.
- Unsupported accelerated paths fail or downgrade visibly according to the
  mesh-submission resolver; do not introduce a silent CPU fallback.
- Dynamic rendering, legacy render-pass compatibility, synchronization2,
  timeline semaphore, WSI binary semaphore, queue ownership, and resource
  lifetime behavior remain shared backend primitives.
- OpenXR keeps its current `WaitFrame` / `BeginFrame` / `LocateViews` /
  `EndFrame` ownership, predicted-time discipline, pacing-thread serialization,
  strict single-pass behavior, and failure diagnostics.
- Stable resource generations continue rendering while pending replacement
  generations are prepared and validated.
- Vulkan command-chain caching, secondary command-buffer reuse, descriptor
  lifetime tracking, and volatility invalidation remain compatible with the new
  view and graph identities.
- Hot render paths remain allocation-free.

## Current Implementation Snapshot

The implementation already contains several useful contracts, but they are not
connected into one runtime path:

- `RenderFrameViewSet` represents up to eight dense logical views, including
  stereo, wide, inset, desktop, and debug roles.
- `RenderFrameViewBatchPlanner` can describe layered quad rendering, two
  layered stereo pairs, parallel per-view recording, or sequential views.
- `GPUViewSet` supports 64-bit masks, per-view constants, output rectangles,
  parent IDs, and per-view visible-index ranges.
- Runtime GPU view setup independently constructs at most five descriptors.
  Its optional foveated descriptors reuse the regular left/right camera
  constants rather than actual OpenXR wide/inset projections.
- Vulkan OpenXR true multiview accepts exactly two views. Four-view OpenXR
  configurations currently fall through to sequential view rendering.
- OpenXR CPU single-pass collection can traverse one conservative combined-eye
  frustum, but it does not retain exact left/right visibility bits.
- GPU flat and BVH culling accept one frustum. The GPU BVH shader starts from
  leaves and walks parent chains, repeating ancestor tests between leaves.
- GPU command view masks currently express render-pass eligibility, not exact
  per-view frustum or occlusion results.
- GPU culling is invoked from each `GPURenderPassCollection`, so passes repeat
  main-camera visibility work instead of consuming a frame-scoped result.
- External sequential OpenXR view rendering currently selects a pass-through
  cull path rather than exact per-eye GPU BVH culling.
- GPU Hi-Z supports useful current/history depth selection, invalidation, and
  diagnostics, but its current visibility is single-phase, generally per-pass,
  and not persistent per logical view. Meshlet stereo Hi-Z is disabled.
- The Vulkan graph compiler topologically sorts explicit dependencies, batches
  compatible passes, and plans synchronization, but it does not yet version
  resource writes, reject cycles, reject uninitialized reads, or schedule from
  an OpenXR deadline.
- Vulkan physical-image aliasing is deliberately disabled until scheduling and
  layout correctness can prove safe non-overlap.

## Ownership Boundaries

This tracker owns integration of views, visibility, render batches, graph
scheduling, and output deadlines.

Related trackers retain detailed ownership of their subsystems:

- The GPU-driven occlusion TODO owns the detailed persistent/two-phase Hi-Z
  algorithm and visibility-buffer format. This tracker owns making that model
  consume the canonical frame view set and participate in the frame DAG.
- The Vulkan and OpenXR organization TODOs own extraction of focused runtime
  authorities. New code from this plan should land behind those intended
  boundaries instead of adding more broad partial-class state.
- The parallel command-chain design owns secondary recording and cache reuse.
  This tracker supplies stable view-batch and graph-plan identities to it.
- The desktop frame-loop TODO owns desktop acquire/submit/present state-machine
  decomposition. This tracker defines the rule that desktop work is optional
  with respect to the XR critical path.
- The dynamic-rendering tracker owns render-target capabilities and modern
  backend feature completion. This tracker consumes those capabilities when
  selecting multiview batches.

## Required Invariants

- [ ] Build one immutable frame view set after the relevant OpenXR views have
  been located. Do not mutate matrices after visibility generation begins.
- [ ] Use the same predicted display time for the OpenXR view state, render
  snapshot, culling inputs, histories, and submitted projection layer.
- [ ] Key temporal state by stable logical view identity, not transient batch
  position or swapchain image index.
- [ ] Keep view eligibility, exact frustum visibility, exact occlusion
  visibility, and output/render-batch membership as distinct concepts.
- [ ] A rejected parent/outer view may reject a contained inset only when the
  runtime relationship has been validated for that frame/configuration.
- [ ] Main-camera visibility may be shared across passes; shadow, reflection,
  probe, and other independent visibility domains remain separate.
- [ ] `CpuDirect` never consumes GPU compute-cull output.
- [ ] GPU strategies use the GPU BVH whenever its shader and provider buffers
  are ready, with a visible temporary flat-GPU fallback when they are not.
- [ ] Zero-readback strategies never require CPU-visible candidate, bucket, or
  draw counts.
- [ ] Desktop, secondary, capture, and debug work cannot delay the required
  OpenXR submit path.
- [ ] Transient aliasing remains disabled until actual semaphore-constrained
  execution intervals prove that two allocations cannot overlap.
- [ ] No per-frame LINQ, captured delegates, per-command managed allocations,
  or readback-dependent decisions are added to visibility or submission paths.

## Phase 0 - Baseline And Contract Lock

- [ ] Record the current CPU Direct, GPU instrumented, GPU zero-readback, and
  supported meshlet behavior for mono desktop, OpenXR sequential stereo, true
  two-eye multiview, and available quad-view lanes.
- [ ] Capture candidate counts, emitted draw counts, culling CPU/GPU duration,
  command-recording duration, XR missed frames, desktop cost, queue ownership
  transfers, barrier-stage flushes, and steady-state allocations.
- [ ] Record which render passes currently dispatch flat or BVH culling so the
  removal of duplicate per-pass work is measurable.
- [ ] Add or retain focused policy tests proving that CPU Direct selects the CPU
  BVH and every GPU strategy requests the GPU BVH without an environment gate.
- [ ] Add contract tests distinguishing pass eligibility masks from exact view
  visibility masks.
- [ ] Record Vulkan validation, OpenXR runtime, GPU/driver, headset or Monado,
  render mode, submission strategy, occlusion mode, and active outputs with the
  baseline evidence.

### Phase 0 acceptance

- [ ] The baseline is reproducible from documented settings and tasks.
- [ ] No implementation phase starts without counts and timings that can prove
  whether traversal and pass duplication were actually reduced.
- [ ] Existing failures are recorded separately from regressions introduced by
  this plan.

## Phase 1 - Canonical Runtime Frame View Set

- [ ] Introduce a focused frame-view builder that captures OpenXR, desktop, and
  secondary descriptors into one `RenderFrameViewSet` without heap allocation.
- [ ] Build OpenXR descriptors from the actual located view pose, projection,
  recommended extent, output layer, and previous-view-projection history.
- [ ] Represent wide/inset parent relationships explicitly and validate the
  pose/containment assumptions required by shared visibility or Hi-Z.
- [ ] Preserve inactive stable IDs or an explicit stable-key mapping so history
  does not migrate between logical views when an optional output toggles.
- [ ] Add an explicit secondary/published-output role if `Debug` is not a clear
  durable semantic owner.
- [ ] Add an allocation-free adapter from `RenderFrameViewSet` to `GPUViewSet`
  descriptor and constant buffers.
- [ ] Replace `RenderCommandCollection.ConfigureGpuViewSet`'s independent
  five-view construction with the canonical frame set.
- [ ] Delete duplicated foveated descriptors that merely reuse ordinary eye
  projections; foveated shading-rate metadata may remain separate from real
  OpenXR inset views.
- [ ] Make every view consumer obtain matrices, output rectangles, parent IDs,
  and history keys from the captured frame set.

### Phase 1 acceptance

- [ ] Runtime mono, stereo, quad, desktop, and secondary diagnostics enumerate
  the same logical views and matrices at every consumer boundary.
- [ ] Toggling an optional output does not assign another view's history to a
  surviving view.
- [ ] No runtime code assumes exactly six views or allocates a per-frame view
  descriptor collection.

## Phase 2 - Runtime View-Batch Planning

- [ ] Make `RenderFrameViewBatchPlanner` consume real backend and swapchain
  capabilities instead of remaining a contract/test-only planner.
- [ ] Keep the existing two-eye true multiview path as the first production
  consumer of a planned `LayeredStereoPair`.
- [ ] Replace the global `_viewCount != 2` rejection with planned batch
  validation.
- [ ] For a typical four-view OpenXR configuration, record one wide stereo pair
  and one inset stereo pair when extents, formats, samples, and target layouts
  are compatible.
- [ ] Permit a four-layer batch only when the target and backend explicitly
  support its layer/extent contract; do not force it over the safer two-pair
  layout.
- [ ] Plan desktop and secondary views as ordinary single-view output batches.
- [ ] Preserve parallel-command-recording and sequential fallbacks with exact,
  visible reasons.
- [ ] Include view mask, attachment signature, output identity, and temporal
  generation in command-chain and secondary-command-buffer cache keys.

### Phase 2 acceptance

- [ ] Two-eye OpenXR renders through a planned stereo batch with existing visual
  and temporal behavior preserved.
- [ ] A supported four-view runtime records two layered stereo batches rather
  than four unrelated sequential views.
- [ ] Unsupported batch combinations fail or fall back at the planner boundary,
  not from incidental recording failures later in the frame.
- [ ] Strict single-pass requests never silently enter sequential rendering.

## Phase 3 - Frame-Scoped Exact Visibility

### 3.1 Shared data contract

- [ ] Define a frame-scoped candidate record containing stable instance or draw
  identity plus an exact active-view mask.
- [ ] Define separate buffers or fields for pass eligibility, frustum-visible
  views, occlusion-visible views, and final render-batch membership.
- [ ] Define capacity, overflow, retirement, and frame-slot ownership for all
  candidate and compaction buffers.
- [ ] Keep candidate data GPU-resident for GPU strategies and use GPU-written
  counts for production submission.

### 3.2 CPU Direct masked traversal

- [ ] Add a multi-frustum CPU BVH collection API that carries an active-view
  mask from parent to child.
- [ ] Load/test each node bound once and remove view bits rejected at that node.
- [ ] Emit exact per-view command collections without traversing the same main
  scene hierarchy independently for every output.
- [ ] Preserve the existing conservative combined-stereo collector as a
  diagnostic comparison lane until exact masked traversal is validated.

### 3.3 GPU masked BVH traversal

- [ ] Upload all active main-view frusta in a compact frame-view buffer.
- [ ] Replace the current leaf-to-parent BVH shader walk with a root-down
  work-queue or bounded stack traversal that propagates the surviving view mask.
- [ ] Test only active frusta at each node and skip inset tests when a validated
  containing outer view has already rejected the node.
- [ ] Compact surviving leaf/object candidates with subgroup or prefix-sum
  operations instead of one global atomic per possible view when supported.
- [ ] Produce one exact visibility mask for every surviving candidate.
- [ ] Define deterministic overflow behavior that is safe, visible, and does
  not fall back to CPU on zero-readback strategies.
- [ ] Remove the external OpenXR pass-through exception once exact multi-view
  GPU visibility covers that path.

### 3.4 Reuse across render passes

- [ ] Move main-camera BVH/frustum dispatch out of individual
  `GPURenderPassCollection` execution.
- [ ] Make depth, opaque, masked, motion-vector, transparent, and other
  main-camera passes consume the shared candidate set and apply their own pass
  eligibility/material classification.
- [ ] Keep shadows, probes, reflections, and other independent cameras in
  separate visibility domains with their own reuse opportunities.
- [ ] Rebuild the scene BVH at most once per scene revision/frame before any
  visibility domain consumes it.

### Phase 3 acceptance

- [ ] Instrumentation reports one main-view BVH traversal for the frame, not one
  traversal per main render pass or logical view.
- [ ] CPU and GPU masked results match a per-view reference collector across
  mono, stereo, quad, desktop, and secondary configurations.
- [ ] GPU strategies use exact GPU-produced masks; no CPU-generated pass mask is
  presented as a frustum visibility result.
- [ ] Zero-readback paths perform no visibility/count readback in steady state.
- [ ] Visibility generation adds zero steady-state managed allocations.

## Phase 4 - Batch-Oriented GPU Draw Generation

- [ ] Classify visible candidates by view batch, render pass, pipeline/state
  class, material, mesh, and LOD.
- [ ] Build union indirect lists for compatible multiview batches while storing
  the exact logical-view mask in draw metadata.
- [ ] Define the traditional indirect implementation for suppressing a draw in
  an ineligible layer when clip/cull distance is supported.
- [ ] Define meshlet/task-record behavior that can compact or reject work per
  view without forcing the traditional indirect mechanism.
- [ ] Measure stereo union, intersection, and Jaccard similarity for every
  multiview batch.
- [ ] Add a hysteretic policy that may split a low-similarity multiview batch
  into single-view draw lists when saved vertex/meshlet work exceeds the extra
  submission cost.
- [ ] Keep transparent sorting per point of view even when opaque work shares a
  batch.
- [ ] Keep LOD projection per logical view and define the conservative/shared
  LOD policy used by a multiview union draw.

### Phase 4 acceptance

- [ ] No geometry appears in a view whose exact visibility bit is clear.
- [ ] Shared draws preserve material, instance, skinning, transform, and
  previous-transform identity across every strategy.
- [ ] Adaptive split/union decisions are stable under small camera motion and
  report their reason and measured similarity.

## Phase 5 - Physical-Eye Hi-Z And Persistent Visibility

- [ ] Implement the persistent/two-phase visibility model owned by the
  GPU-driven occlusion TODO against stable logical view IDs.
- [ ] Maintain outer-eye Hi-Z history for left and right physical XR eyes plus
  independent desktop and secondary histories when those outputs are active.
- [ ] Frustum-test inset views using their exact inset projection before any
  shared-eye occlusion test.
- [ ] Project inset candidate bounds with the corresponding outer projection
  before sampling the outer-eye Hi-Z.
- [ ] Fall back to an independent inset hierarchy or disabled inset occlusion
  when pose/containment invariants are not established.
- [ ] Support temporal Hi-Z as the low-latency/default scheduling option.
- [ ] Support optional current-frame outer-eye occluder depth and layered Hi-Z
  generation when measured benefit exceeds its XR critical-path dependency.
- [ ] Invalidate or conservatively bypass history after camera cuts, tracking
  jumps, projection discontinuities, resource-generation changes, and unsafe
  scene revisions.
- [ ] Extend meshlet occlusion to exact per-view stereo/quad data; remove the
  current mono-only `ActiveViewCount <= 1` restriction when validated.
- [ ] Periodically run an occlusion-disabled validation frame and compare its
  visible set with the occluded result.

### Phase 5 acceptance

- [ ] No outer-eye occlusion result is sampled in inset coordinates.
- [ ] No inset shares an outer hierarchy without a validated relationship.
- [ ] Hidden validation frames report zero false-negative visibility failures.
- [ ] Camera cuts, tracking jumps, and rapidly moving objects do not cause
  visible popping from stale occlusion.
- [ ] Pyramid construction is shared at the intended physical-eye/output
  frequency rather than repeated by every render pass.

## Phase 6 - Render-Graph Dataflow Correctness

- [ ] Represent every resource use with resource identity, subresource range,
  stage, access, layout, and read/write intent.
- [ ] Version each logical resource after every write.
- [ ] Derive producer-to-consumer dependencies from resource versions instead
  of relying only on declaration order or manually supplied dependencies.
- [ ] Reject cycles with a diagnostic containing the dependency chain; do not
  append cyclic passes in declaration order.
- [ ] Reject reads of uninitialized internal resources unless the resource is
  explicitly imported with a valid initial state.
- [ ] Calculate transient lifetimes from the scheduled graph and queue waits.
- [ ] Preserve synchronization2 barriers, queue-family transfers, timeline
  semaphore waits/signals, and binary WSI synchronization through one plan.
- [ ] Batch adjacent same-queue passes while preserving useful overlap and
  keeping submission count bounded.
- [ ] Produce an immutable, versioned `VulkanRenderGraphPlan` with a complete
  cache identity.
- [ ] Emit a graph dump containing pass order, resource versions, edges,
  barriers, queue assignments, submissions, output deadlines, lifetimes, and
  predicted/measured durations.
- [ ] Keep physical image aliasing disabled until tests prove that candidate
  lifetimes cannot overlap across actual asynchronous execution intervals.

### Phase 6 acceptance

- [ ] Unit tests reject cycles, missing producers, uninitialized reads, invalid
  subresource transitions, and unsafe queue-family ownership plans.
- [ ] Resource-derived dependencies can reorder independent declaration order
  without changing results.
- [ ] Standard and synchronization validation report no new VUIDs across mono,
  multiview, async-compute, resize, and OpenXR lanes.

## Phase 7 - Deadline-Aware Output Scheduling

- [ ] Represent OpenXR submission, desktop present, secondary publication, and
  capture/debug completion as explicit output terminal nodes.
- [ ] Attach predicted or measured duration, output priority, deadline, and
  reuse/degradation policy to relevant graph nodes.
- [ ] Calculate the reverse longest path to OpenXR submission and schedule that
  path before optional output work.
- [ ] Make desktop image acquisition nonblocking for XR-owned frames where the
  platform path permits it.
- [ ] Permit prior-frame desktop reuse, resolution reduction, or skipped
  desktop updates instead of extending the XR critical path.
- [ ] Double-buffer secondary output and permit rate reduction or prior-frame
  reuse, especially when its texture is consumed by the XR scene.
- [ ] Keep same-frame secondary-to-XR dependencies only for explicitly declared
  pose-sensitive behavior.
- [ ] Add budget policies for optional SSAO, SSR, volumetrics, outer-eye quality,
  current-frame occlusion, captures, and debug work.
- [ ] Make outer-versus-inset quality priority a measured runtime policy rather
  than an unconditional hard-coded ordering.
- [ ] Feed per-node GPU timestamps and queue idle measurements into the existing
  queue-overlap promotion/demotion hysteresis.
- [ ] Do not automatically move all compute work to async compute; retain
  graphics-queue execution when contention or transfer cost is worse.

### Phase 7 acceptance

- [ ] A blocked or unavailable desktop/secondary output cannot block the
  required OpenXR submission.
- [ ] Graph diagnostics identify every pass on the OpenXR critical path and its
  measured duration.
- [ ] Optional-output reuse and degradation are visible in telemetry.
- [ ] XR missed-frame rate and p95/p99 submit timing do not regress against the
  Phase 0 baseline on retained validation workloads.

## Phase 8 - Instrumentation, Validation, And Promotion

### Required telemetry

- [ ] Candidate count after shared BVH/frustum traversal.
- [ ] Exact visibility count for every active logical-view bit.
- [ ] Per-batch union, intersection, and Jaccard similarity.
- [ ] Frustum and occlusion rejection count per view.
- [ ] Hi-Z history source, invalidation, bypass, false-negative validation, and
  pyramid-build frequency per physical eye/output.
- [ ] GPU timestamp duration per graph node and output critical path.
- [ ] Queue overlap, idle gaps, ownership transfers, stage flushes, and submit
  count.
- [ ] XR missed frames and predicted-versus-actual submit margin.
- [ ] Desktop/secondary cost, update frequency, reuse, and degradation count.
- [ ] Draw/triangle/meshlet count per view batch and pipeline bucket.
- [ ] Steady-state allocation and zero-readback violation counters.

### Validation matrix

- [ ] Strategies: CPU Direct, GPU indirect instrumented, GPU indirect
  zero-readback, and supported meshlet strategies.
- [ ] Views: mono desktop, OpenXR sequential stereo, parallel recording, true
  two-eye multiview, quad view, desktop mirror, and secondary output.
- [ ] Occlusion: disabled, temporal Hi-Z, current-frame Hi-Z, camera cut,
  tracking jump, moving occluders, and dirty scene/BVH.
- [ ] Graph queues: graphics only, graphics plus compute, and dedicated transfer
  only on hardware where it is actually selected.
- [ ] Lifecycle: resize, minimize/restore, swapchain recreation, resource
  generation replacement/failure, shader reload, scene transition, runtime
  session loss, and device-loss diagnostics.
- [ ] Rendering content: opaque, masked, transparent/OIT, skinned, instanced,
  meshlet, motion vectors, temporal effects, shadows, UI, and post-processing.

### Promotion requirements

- [ ] Zero steady-state managed allocations in frame-view construction,
  visibility traversal, compaction, graph scheduling, and submission.
- [ ] Zero unexpected CPU readback bytes for zero-readback strategies.
- [ ] Zero new standard/synchronization validation errors in retained Vulkan
  and OpenXR lanes.
- [ ] Exact visibility matches the per-view reference collector.
- [ ] A supported quad-view runtime uses two stereo render batches by default.
- [ ] Main-camera BVH/frustum traversal occurs once per frame visibility domain.
- [ ] Two-eye baseline CPU and GPU frame cost does not regress by more than 5%
  without a documented downstream win and owner approval.
- [ ] XR p95/p99 submit margin and missed-frame rate are at least as good as the
  Phase 0 baseline.
- [ ] Durable validation findings are recorded under `docs/work/testing/` or
  the relevant investigation/progress document; disposable captures and logs
  remain under `Build/_AgentValidation/`.

## Suggested Implementation Sequence

1. Canonical live `RenderFrameViewSet` and allocation-free GPU adapter.
2. Runtime batch planning for existing two-eye multiview.
3. Four-view OpenXR as wide and inset stereo pairs.
4. Exact two-eye masked CPU/GPU traversal reused across render passes.
5. Expand the visibility mask to quad, desktop, and secondary views.
6. Batch-oriented indirect generation and adaptive union/split policy.
7. Physical-eye Hi-Z reuse and persistent per-view visibility.
8. Resource-versioned graph correctness and graph dumping.
9. Output deadlines, optional-output reuse, and timestamp-driven queue policy.
10. Full validation matrix and default-path promotion.

## Risks And Guardrails

- A multiview union list can waste vertex or meshlet work when view overlap is
  low. Measure overlap and retain a stable split path.
- A single masked traversal can serialize poorly if its work queue or atomics
  are designed around the worst-case view count. Validate two-view performance
  before expanding the mask.
- Quad-view runtimes may expose different extents and optional capabilities.
  Select batches from actual runtime/target properties, not assumed layout.
- Outer/inset Hi-Z sharing is unsafe when pose, containment, projection, depth
  convention, or history generation do not match the declared relationship.
- Current-frame occluder depth may improve correctness while harming XR latency.
  Keep it graph-selectable and driven by timestamps.
- Resource aliasing across async queues is unsafe when only topological order is
  considered. Require disjoint semaphore-constrained intervals.
- View-batch cache identities can grow without bound if transient matrices or
  frame indices enter structural keys. Separate structural identity from
  frame-data refresh.
- The frame-wide graph must not absorb unrelated shadows or probes merely to
  claim one traversal; use explicit visibility domains.
- Do not turn this plan into another `VulkanRenderer` or `OpenXRAPI` partial
  monolith. Follow the focused owners in the runtime-organization TODOs.

## Completion Criteria

This tracker is complete when the canonical frame view set drives every live
Vulkan/OpenXR view consumer; exact main-view visibility is produced by one
masked CPU or GPU BVH traversal per frame domain and reused across passes;
quad-view XR renders as planned stereo batches; physical-eye Hi-Z and
persistent per-view visibility are conservative and validated; the render graph
versions resources and rejects invalid dataflow; and optional outputs are
scheduled so they cannot delay OpenXR submission.

The result must retain the current production lifecycle, synchronization,
resource-generation, zero-readback, command-cache, and failure-diagnostic
contracts while meeting the validation and performance requirements above.
