# 08 - Render Tail Latency: Shadows, Streaming, And Jobs TODO

Last Updated: 2026-07-28
Owner: Rendering / Assets / Frame Scheduling
Status: Blocked By Workstream 07
Sequence: 08 of 08
Predecessor: [07 - Occlusion Systems Performance](07-occlusion-systems-performance-todo.md)
Blocks: Final Desktop 200+ Hz And RVC 120 Hz Promotion

Canonical ownership: this document owns the ordered execution, render-tail
performance evidence, subsystem budgets, and final desktop 200+ Hz and Vulkan
RVC zero-readback 120 Hz promotion gates for directional shadows, Vulkan
texture publication/synchronization, and unrelated render-thread jobs. Child
trackers retain detailed architecture, feature, and correctness ownership; none
may independently claim the performance gate.

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md)

Related trackers:

- [Shadow Atlas Solve Efficiency](../shadows/shadow-atlas-solve-efficiency-todo.md)
- [Shadow Atlas Allocation And Threading](../shadows/shadow-atlas-allocation-and-threading-todo.md)
- [Directional Cascade Stale Frame And Reprojection](../shadows/directional-cascade-atlas-stale-frame-and-reprojection-todo.md)
- [Shadow Atlas Overhaul](../shadows/shadow-atlas-overhaul-todo.md)
- [Texture Runtime, Streaming, And Virtual Texturing](../../texturing/texture-runtime-streaming-virtual-texturing-todo.md)
- [Rendering Clean Performance Baseline Profile Contract](rendering-clean-performance-baseline-profile-contract-todo.md)

Child-tracker disposition:

- The shadow solve and allocation/threading trackers own solver and
  plan/publish/execute implementation details; their remaining live benchmarks
  are prerequisites of Phase 1 here.
- The directional-cascade tracker owns stale-frame, reprojection, slot
  provenance, and visual-correctness policy.
- The shadow-atlas overhaul remains the broad feature/quality roadmap. This
  workstream owns only the directional-update and final tail-latency gates.
- The canonical texture roadmap owns residency, sparse/virtual texturing, and
  asset policy. This workstream owns the bounded Vulkan upload, finalization,
  descriptor-publication, and queue-synchronization cost.

## Sequential Execution Contract

- Do not start this workstream until workstream 07 is marked `Complete`.
- This is the final integration and tail-latency workstream. It may not waive a
  failed earlier exit gate.
- A predecessor's recorded miss against the final 5.00 ms or 8.33 ms
  whole-frame budget is a required input, not an earlier exit-gate failure,
  provided that predecessor passed its subsystem-local correctness,
  relative-cost, scaling, and allocation contract.
- Completion requires both final promotion matrices, not only isolated
  subsystem improvements.

## Goal

Remove engine-owned render-thread tail spikes caused by directional shadows,
texture publication and transfer synchronization, and unrelated render-thread
jobs. Finish with stable desktop-only 200+ Hz and Vulkan RVC zero-readback
120 Hz results on the canonical warm static and moving-camera workloads.

## Starting Evidence

- Camera motion invalidated and re-recorded all four directional shadow
  cascades, reducing performance to about 10.5 FPS while GPU time remained near
  4.3 ms.
- Disabling the directional light roughly halved CPU frame cost in that prior
  motion investigation.
- Representative launches uploaded 35-243 MB across 78-89 texture jobs, but
  warmed capture windows contained zero texture-upload work.
- Sparse transition finalization and Vulkan publication can still enqueue
  render-thread jobs and invalidate descriptors or command reuse during churn.
- One-shot Vulkan submission shares the desktop queue lock and may hold it
  while waiting on a fence; that wait is not currently attributed.
- Generic main-thread jobs have a nominal 4 ms frame budget but an individual
  job cannot be preempted. BVH raycast and GPU physics work are additional
  interaction risks.
- Workstream 03's retained Monado RVC Quick capture recorded
  34.778/109.139/112.717 ms render p50/p95/p99 against the 8.33 ms target. That
  absolute miss is carried here for final whole-renderer promotion after
  workstreams 04-07 complete their owned preparation, recording, render-graph,
  and occlusion work.

## Scope

- Directional cascade invalidation, reuse, update scheduling, and budgets.
- Texture upload finalization, descriptor publication, and transfer/graphics
  queue synchronization.
- Queue-lock acquisition and fence-wait ownership.
- Generic render/main-thread jobs, BVH work, and GPU physics
  dispatch/completion processing.
- Warm steady-state, deterministic camera motion, controlled streaming churn,
  and combined stress.
- Final p50/p95/p99 desktop 200+ Hz and RVC 120 Hz promotion matrices.

## Non-Goals

- Hiding startup compilation or upload work without exposing readiness.
- Skipping visibly required shadow updates without a documented temporal
  policy.
- Moving thread-affine work unsafely.
- Declaring success from average FPS while desktop p95 misses 5.00 ms or the
  RVC zero-readback whole-frame p95 misses 8.33 ms.

## Phase 0 - Tail Attribution

- [ ] Attribute every desktop frame above 5.00 ms and every RVC zero-readback
  frame above 8.33 ms to shadows, streaming/publication, queue/fence wait,
  generic job, BVH, physics, encoding, GPU, or unexplained time.
- [ ] Record queue delay and execution duration for each render-thread job
  source.
- [ ] Record per-cascade invalidation reason, record/reuse decision, draw count,
  CPU encode time, and GPU time.
- [ ] Record upload bytes, batches, finalization, descriptor invalidation,
  queue-lock wait, submission, and fence wait.
- [ ] Add a hard `Unattributed` failure bucket.

Acceptance criteria:

- [ ] Every p95/p99 spike has a subsystem owner.
- [ ] Queue-lock wait and fence wait are never merged into generic submit time.

## Phase 1 - Directional Shadow Stability

- [ ] Define cascade invalidation from camera, light, caster, receiver, atlas,
  and quality changes.
- [ ] Stabilize cascade projections and reuse unaffected command/data state.
- [ ] Add a bounded per-frame cascade update budget and temporal policy.
- [ ] Avoid all-cascade command re-record from camera motion unless each
  cascade's content or projection genuinely requires it.
- [ ] Validate fast motion, camera cuts, light motion, caster motion, resize,
  and atlas changes.

Acceptance criteria:

- [ ] Ordinary deterministic camera motion does not re-record all cascades
  every frame.
- [ ] Shadow update CPU/GPU cost stays within its declared per-frame budget.
- [ ] No stale, swimming, missing, or incorrectly delayed shadows are observed.

## Phase 2 - Texture Publication And Queue Synchronization

- [ ] Keep decode, transcode, mip preparation, and pure upload planning off the
  render thread.
- [ ] Batch transfer recording, sparse transitions, finalization, and
  descriptor publication.
- [ ] Publish immutable generation changes compatible with workstream 04.
- [ ] Ensure the desktop queue lock is not held while waiting for a fence.
- [ ] Prefer timeline/fence ownership that permits unrelated desktop rendering
  progress where Vulkan correctness allows.
- [ ] Bound upload work per frame and report deferred residency visibly.
- [ ] Close the still-live validation requirements migrated from the historical
  Vulkan imported/async texture-upload trackers: worker preparation, transfer
  submission/polling, descriptor publication, dirty-scope containment,
  generation cancellation, lazy retirement, and dense promotion/demotion.
- [ ] Prove sparse-transition finalization does not scan or enqueue
  render-thread work in warm steady state when there is nothing to finalize.
- [ ] Measure publication-triggered descriptor-generation changes and primary
  or secondary invalidations; attribute each invalidation to the exact
  published texture generation.

Acceptance criteria:

- [ ] Warm steady state reports zero texture jobs and bytes unless assets
  actually change.
- [ ] Controlled streaming churn has a declared bounded render-thread cost.
- [ ] No desktop queue lock is held across a blocking fence wait.
- [ ] Publication does not trigger broad descriptor or command-buffer
  invalidation.

## Phase 3 - Bound Unrelated Render Jobs

- [ ] Inventory generic jobs, BVH raycasts, GPU physics dispatch/completion,
  capture work, and other non-render operations allowed on the render thread.
- [ ] Move pure preparation to its correct worker/collect owner.
- [ ] Split or incrementally process work that can exceed its frame budget.
- [ ] Add admission control so one job cannot consume an unbounded frame.
- [ ] Preserve explicit affinity for API calls that legally require the render
  thread.
- [ ] Stress combined camera motion, physics, raycasts, and streaming.

Acceptance criteria:

- [ ] No non-render job can silently exceed its declared render-thread budget.
- [ ] Job queue backlog, deferral, and deadline misses are visible.
- [ ] Combined stress has no unexplained render-thread hitch.

## Phase 4 - Final Desktop And RVC Promotion

- [ ] Run at least three repetitions of canonical Deferred and Uber static
  desktop-only cohorts.
- [ ] Run at least three repetitions of deterministic Deferred and Uber
  desktop-only moving-camera cohorts.
- [ ] Run at least three repetitions of the Vulkan
  `GpuIndirectZeroReadback` RVC workload. Every retained sample must contain a
  freshly rendered desktop output; every submitted XR projection frame must
  contain both freshly rendered eyes, and each repetition must observe both
  eyes at the runtime-owned cadence.
- [ ] Run the RVC workload with foveation disabled and enabled wherever the
  runtime supports both modes. Foveation state does not change the whole-frame
  budget.
- [ ] Run controlled streaming and combined interaction stress separately from
  warm steady-state promotion.
- [ ] Verify primary reuse, zero-readback, next-frame preparation, worker,
  render-graph, and occlusion contracts remain satisfied.
- [ ] Publish stage and top-level p50/p95/p99, worst frame, missed-budget count,
  GPU time, allocations, readbacks, and tail attribution.

Acceptance criteria:

- [ ] On the workstream 01 target hardware and canonical warm desktop-only
  workloads, render p95 is at most 5.00 ms for both static and deterministic
  moving-camera Deferred and Uber cohorts.
- [ ] On the same target hardware, the complete Vulkan
  `GpuIndirectZeroReadback` RVC frame has render p95 at most 8.33 ms while
  rendering the desktop every sample and both eyes together on every submitted
  XR projection frame.
- [ ] The RVC gate passes in every supported foveation state without
  synchronous readback, skipped/reused eye output masquerading as a render, or
  silent fallback. Additional quad/foveated views or renders do not relax the
  whole-frame budget.
- [ ] Render p99 meets the explicit tail budget established in workstream 01,
  and no engine-owned steady-state frame exceeds 16.67 ms without attribution.
- [ ] GPU time, CPU time, waits, allocations, and readbacks all remain within
  their declared contracts.
- [ ] Streaming stress has a separately declared, bounded budget and never
  disguises missing residency or a silent CPU fallback.

## Exit Gate

- [ ] Directional shadow updates are selective, reusable, and budgeted.
- [ ] Texture publication is batched and no queue lock is held across a fence
  wait.
- [ ] Unrelated render-thread jobs are attributed and bounded.
- [ ] All four warm desktop-only canonical cohorts pass the 5.00 ms p95
  promotion gate.
- [ ] All required Vulkan RVC zero-readback cohorts pass the 8.33 ms
  whole-frame p95 promotion gate with at least three submitted XR projection
  frames per repetition.
- [ ] Combined stress, Release build, focused tests, validation layers, and
  long-duration performance runs pass.
- [ ] Final evidence, hardware manifest, risks, and remaining non-blocking
  follow-ups are recorded in the investigation.
- [ ] This document is marked `Complete`.

Completion of this gate finishes the ordered eight-workstream Vulkan desktop
200+ Hz and RVC zero-readback 120 Hz performance program.
