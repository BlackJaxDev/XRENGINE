# 04 - Next-Frame Preparation And Collect-Visible Handoff TODO

Last Updated: 2026-07-29
Owner: Rendering / Frame Scheduling
Status: Implementation Complete; Acceptance Deferred To 01-08 Closeout
Sequence: 04 of 08
Predecessor: [03 - True GPU-Driven Zero-Readback Submission](03-vulkan-true-zero-readback-submission-todo.md)
Blocks: [05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)
- [Workstream 04 Implementation Progress](../../../progress/rendering/next-frame-preparation-and-collect-visible-handoff-2026-07-29.md)
- [01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)

Predecessor evidence:

- [Collect-Visible Render Wait Decoupling](../../COMPLETED/collect-visible-render-wait-decoupling-todo.md)
  established the work-versus-wait counters, bounded late-data policy, and
  existing one-frame-ahead synchronization behavior. This workstream is its
  successor for backend-ready preparation.

## Sequential Execution Contract

- Workstream 03 is implementation complete. Its remaining acceptance work is
  deferred intact to the 01-08 closeout by owner direction on 2026-07-29.
- Treat the submission contract produced by workstream 03 as an input, not a
  moving target.
- Workstream 05 may begin after this workstream is marked
  `Implementation Complete; Acceptance Deferred`. Targeted failures still
  block progression; canonical performance and stress acceptance runs after
  workstreams 01-08 are implementation complete.

## Goal

Use the collect-visible side of the render/collect handoff to prepare an
immutable, backend-ready package for the upcoming frame. The render thread
should primarily validate, encode, submit, and present; pure scene traversal,
sorting, material selection, dependency discovery, and data planning should
not consume its critical path.

## Starting Evidence

- Collect-visible usually completed in 1-2 ms.
- Generation age stayed at one frame or less and stale reuse was zero.
- Collect wait for render tracked the slow 20-180 ms render duration.
- That is the predecessor's Mode A: collect is ready and waits behind render.
  Increasing lookahead alone cannot improve throughput in that mode and may
  only add latency.
- The handoff publishes generic `RenderCommandCollection` data only.
- Vulkan packets, material fan-out, resource plans, descriptor work,
  dependency snapshots, and frame-data refresh are still render-thread work.
- Viewport collection is serial across listeners despite a parallel invocation
  implementation existing.
- Workstream 03's retained Monado RVC Quick capture reported
  `VulkanFrameDataRefreshAllocatedBytesTotal=40,384`. This is an explicit
  workstream-04 handoff: eliminate generic frame-data-refresh allocation while
  preserving the predecessor's validated zero-readback submission contract.

## Target Package Contract

The upcoming-frame package should contain, where ownership permits:

- immutable visibility results and stable scene/resource generation IDs;
- sorted render packets and pass membership;
- material, variant, tier, and pipeline selections;
- resource-use and transition-plan inputs;
- descriptor and uniform update inputs, not ad hoc scene reads;
- reusable-command dependency snapshots and precise dirty keys;
- per-view and per-frame data needed for final Vulkan encoding;
- explicit readiness, cancellation, stale-data, and lifetime state.

Backend handles with strict thread affinity may remain render-thread-owned, but
their pure planning inputs must be prepared before the render stage.

## Non-Goals

- Recording Vulkan command buffers on collect-visible.
- Allowing the render thread to consume partially published mutable data.
- Adding another frame of latency without an explicit, measured policy.
- Hiding missing packages by silently rendering stale state indefinitely.

## Phase 0 - Account For Render-Thread Work

- [x] Classify every render-thread stage as scene preparation, backend
  preparation, command encoding, submission, present, wait, or unrelated job.
- [x] Mark which stages are pure, snapshot-dependent, backend-thread-affine, or
  externally synchronized.
- [x] Record data dependencies and mutation sources for each movable stage.
- [x] Establish per-stage budgets from workstream 01.

Acceptance criteria:

- [x] All CPU time before submit has an owner and a reason for remaining on or
  moving off the render thread.
- [x] No stage is moved merely by wrapping it in `Task.Run`.

## Phase 1 - Define Ownership And Handoff

- [x] Define immutable package identity, frame/generation IDs, and in-flight
  lifetime.
- [x] Define double- or triple-buffered ownership with no producer/consumer
  mutation race.
- [x] Define how transform, material, texture, pipeline, and render-graph
  changes invalidate prepared data.
- [x] Define behavior for a late package, skipped frame, resized viewport,
  failed submit, and shutdown.
- [x] Preserve the distinction between Mode A (collect waiting for slow render)
  and Mode B (render starved by slow collect); stale reuse or blocking policy
  must identify which mode triggered it.
- [x] Define maximum acceptable package age and latency.
- [x] Keep producer lookahead and storage strictly bounded. The current handoff
  is approximately one frame ahead; any larger double/triple buffering requires
  an explicit latency and ownership justification.

Acceptance criteria:

- [x] The render thread can validate package freshness in bounded time without
  traversing scene state.
- [x] A producer cannot overwrite data referenced by an in-flight frame.
- [x] Stale or missing data produces an explicit metric and policy decision.

## Phase 2 - Move Pure Preparation

- [x] Build render packets, sorting keys, material selections, and dependency
  snapshots alongside collect-visible.
- [x] Prepare resource-plan and descriptor/uniform inputs for the upcoming
  frame.
- [x] Cache stable data by precise generations rather than rebuilding all
  entries.
- [x] Use bounded parallel collection only where measured work and ownership
  make it beneficial.
- [x] Preserve the existing `BlockUntilFresh` default and explicitly count any
  authorized previous-visibility reuse, dropped stale package, or wait.
- [x] Eliminate per-frame allocations, LINQ, captured closures, and string
  creation from the producer and consumer hot paths.
- [x] Keep mutable Vulkan object access on its legal owner thread.

Acceptance criteria:

- [x] The immutable package is complete before publication.
- [x] Render-thread scene/material traversal is eliminated from steady-state
  submission.
- [x] Preparation overlaps rendering rather than waiting idle behind it.
- [ ] Canonical stable captures report zero steady-state managed allocation in
  frame-data refresh and in the package producer/consumer hot paths.

## Phase 3 - Consume And Validate

- [x] Make Vulkan consume only validated package data for steady-state frames.
- [x] Measure package production, publish, wait, validation, and consumption
  separately.
- [ ] Stress rapid scene mutation, streaming publication, viewport changes,
  camera motion, pause/resume, and shutdown.
- [ ] Verify deterministic draw ordering and visual parity.
- [ ] Compare input latency and generation age against the prior handoff.

Acceptance criteria:

- [ ] Generation age stays within the declared policy with no unreported stale
  reuse.
- [ ] Render-thread non-encoding preparation meets its workstream 01 budget.
- [ ] No new race, lifetime violation, or per-frame allocation is observed.

## Exit Gate

- [x] A documented immutable backend-ready frame package is produced alongside
  collect-visible and consumed by Vulkan.
- [x] The render thread primarily validates, encodes, submits, and presents.
- [ ] Collect/render overlap and backpressure metrics prove improved
  utilization.
- [ ] Evidence shows improvement from overlapped useful preparation, not merely
  a farther-ahead producer while the render thread remains the bottleneck.
- [ ] The workstream-03 frame-data-refresh allocation handoff is closed without
  moving allocation, stale state, or mutable ownership into another stage.
- [ ] Static, moving, mutation, resize, and shutdown stress tests pass.
- [ ] Release build, focused tests, and canonical performance cohorts pass.
- [x] Evidence and remaining thread-affine work are recorded.
- [x] Implementation is marked complete and every unchecked acceptance item is
  retained in the 01-08 closeout.

Implementation work may proceed to
[05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md).
Do not promote workstream 04 acceptance until its remaining gates pass in the
01-08 closeout.
