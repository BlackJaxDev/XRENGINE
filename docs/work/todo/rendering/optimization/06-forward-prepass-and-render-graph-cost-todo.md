# 06 - Forward+ Prepass And Render-Graph Cost TODO

Last Updated: 2026-07-28
Owner: Rendering / Default Pipeline
Status: Blocked By Workstream 05
Sequence: 06 of 08
Predecessor: [05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md)
Blocks: [07 - Occlusion Systems Performance](07-occlusion-systems-performance-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)

Child tracker:

- [Default Pipeline GPU Hotspots](default-pipeline-gpu-hotspots-todo.md)

Cross-cutting contracts and future render paths:

- [VR Rendering Performance Contract](vr-rendering-performance-contract-todo.md)
- [Deferred+ Render Path](deferred-plus-render-path-todo.md)
- [Render Graph Migration Guide](../../../../architecture/rendering/render-graph-migration.md)
- [Render Pipeline Resource Lifecycle](../../../../architecture/rendering/render-pipeline-resource-lifecycle.md)
- [Rendering Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)

## Ownership Contract

This is the canonical tracker for the topology and measured cost of the current
`DefaultRenderPipeline` / `DefaultRenderPipeline2` hybrid graph. It owns:

- whether the Forward+ depth/normal prepass runs and which geometry it replays;
- current-pipeline depth, normal, velocity, and preserved-color production;
- copies, blits, resolves, attachment aliases, transitions, and barriers;
- removal of producers and consumers when a current-pipeline feature is off.

The child [Default Pipeline GPU Hotspots](default-pipeline-gpu-hotspots-todo.md)
tracker owns detailed shader implementation and quality-profile tuning after
this workstream establishes the minimal graph. Its desktop subset executes
inside this workstream when a remaining pass exceeds the workstream 01 budget;
it cannot remain an unowned desktop performance track after this exit gate.
VR-only tuning remains under the independent VR acceptance overlay. Deferred+
is a separate future opaque render path.

## Sequential Execution Contract

- Do not start this workstream until workstream 05 is marked `Complete`.
- Use the corrected CPU submission and recording architecture so render-graph
  measurements are not dominated by known CPU bookkeeping defects.
- Do not start workstream 07 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

## Goal

Reduce the measured Forward+ depth/normal prepass and hybrid default-pipeline
cost without breaking depth, normal, velocity, lighting, AO, TSR, shadows,
probes, bloom, or post-processing correctness.

## Starting Evidence

- Uber CPU-direct with the prepass enabled measured 22.00 ms p50 render time.
- Disabling only the prepass reduced it to 16.09 ms p50, a 5.91 ms delta.
- The delta included about 4.60 ms Vulkan CPU work and 0.97 ms GPU work.
- The prepass replays forward opaque/masked geometry before the lit pass.
- The shared-GBuffer path performs three logical full-resolution color/depth
  copies; Vulkan resolves color and depth separately, yielding six blits and
  up to 24 transition/barrier calls.
- TSR adds a velocity geometry pass, while AO, bloom, temporal accumulation,
  shadows, probes, and post-processing remain in the graph.

## Scope

- Forward opaque/masked geometry replay.
- Shared depth/normal production and consumption.
- Full-resolution color/depth copies, resolves, aliases, and transitions.
- Velocity generation for TSR.
- AO, bloom, temporal, probe, shadow, and post-process dependencies that force
  otherwise redundant work.
- Per-pass CPU encoding and GPU timing.
- RenderDoc inspection after the CPU-side fixes make pass-level analysis
  representative.

## Non-Goals

- Removing a visual feature merely to claim a faster default.
- Comparing non-equivalent Deferred and Uber scenes.
- Treating lower CPU command count as proof of lower GPU bandwidth.
- Implementing Deferred+, visibility-buffer material shading, or clustered
  froxel migration as a substitute for fixing the current default graph.
- Owning general VR stereo, foveation, reprojection, or benchmark policy.
- Retuning AO, exposure, lighting, bloom, or post-process shader quality before
  their required current-graph producers and consumers are known.

## Phase 0 - Build The Pass And Resource Ledger

- [ ] Capture an equivalent isolated Deferred/Uber frame with normal GPU
  timestamps.
- [ ] Capture RenderDoc frames for prepass on and off.
- [ ] List each geometry replay, draw count, attachment, copy/blit, transition,
  barrier, dispatch, and full-screen pass.
- [ ] Map which later consumers require depth, normals, velocity, and preserved
  color.
- [ ] Record CPU encode and GPU duration for each relevant pass.

Acceptance criteria:

- [ ] Every copy and geometry replay has a named consumer and correctness
  requirement.
- [ ] CPU and GPU deltas reconcile with the top-level prepass A/B within the
  measurement tolerance.

## Phase 1 - Define The Minimal Graph

- [ ] Decide whether depth, normals, and velocity can be produced together or
  reused from another required geometry pass.
- [ ] Define when depth prepass is beneficial, required, or safely skipped.
- [ ] Replace preservational copies with attachment lifetime, aliasing, input
  attachment, or explicit transition where supported and correct.
- [ ] Define backend-neutral graph intent and Vulkan-specific realization.
- [ ] Make optional consumers conditionally allocate and execute their inputs.

Acceptance criteria:

- [ ] The proposed graph has no copy, replay, or barrier without a documented
  dependency.
- [ ] Deferred and Uber ownership of shared resources is unambiguous.

## Phase 2 - Remove Redundant Work

- [ ] Eliminate redundant forward geometry replay where an equivalent result
  can be produced once.
- [ ] Eliminate avoidable full-resolution color/depth copies and paired blits.
- [ ] Batch or remove redundant image transitions and barriers.
- [ ] Reuse or co-produce velocity where it avoids another geometry pass.
- [ ] Avoid AO, bloom, probe, shadow, temporal, or post-process work when the
  feature or consumer is disabled.
- [ ] Preserve command-buffer reuse and zero-allocation hot paths.

Acceptance criteria:

- [ ] Actual draw, blit, transition, and pass counts match the minimal graph
  ledger.
- [ ] No disabled feature leaves a hidden full-resolution producer pass.

## Phase 3 - Visual And Performance Validation

- [ ] Validate static and moving views, opaque and masked geometry, camera cuts,
  TSR history, AO, shadows, probes, bloom, and resize.
- [ ] Inspect exported depth, normal, velocity, lighting, temporal, and final
  render targets.
- [ ] Run equivalent Deferred and Uber canonical cohorts.
- [ ] Compare CPU and GPU p50/p95/p99 plus bandwidth-relevant operation counts.
- [ ] Reprofile GTAO, exposure, LightCombine, bloom, TSR, and post-processing
  against the minimal graph. Complete the desktop subset of
  [Default Pipeline GPU Hotspots](default-pipeline-gpu-hotspots-todo.md) for
  every remaining pass that exceeds its workstream 01 budget.
- [ ] Test GPUs with different tile/immediate and bandwidth characteristics
  where available.
- [ ] Apply the independent
  [VR Rendering Performance Contract](vr-rendering-performance-contract-todo.md)
  to any stereo or headset performance claim, including active stereo mode,
  whole-frame budget, per-eye resource correctness, and reprojection state.

Acceptance criteria:

- [ ] Visual output passes image and motion-history checks.
- [ ] Initial performance target: incremental prepass CPU cost is at most
  1.0 ms p50 and incremental GPU cost is at most 1.0 ms p50 in the canonical
  Uber cohort, or a different explicit budget is approved and recorded before
  implementation.
- [ ] No correctness requirement is satisfied through an undocumented full
  geometry replay or full-resolution copy.

## Exit Gate

- [ ] Every remaining prepass, copy, replay, and barrier is justified by the
  resource ledger.
- [ ] The accepted incremental Forward+ prepass budget is met.
- [ ] Remaining default-pipeline GPU passes meet their declared desktop budget,
  or an explicit quality/performance decision is recorded. No desktop hotspot
  is deferred as an ownerless child task.
- [ ] Deferred and Uber visual, motion, resize, and validation-layer tests pass.
- [ ] Primary reuse, zero-readback, next-frame preparation, and worker
  contracts remain intact.
- [ ] Release build, focused tests, RenderDoc evidence, and canonical
  performance cohorts pass.
- [ ] Evidence and accepted quality/performance tradeoffs are recorded.
- [ ] This document is marked `Complete`.

Only after this gate is complete may work begin on
[07 - Occlusion Systems Performance](07-occlusion-systems-performance-todo.md).
