# 04 - Next-Frame Preparation And Collect-Visible Handoff TODO

Last Updated: 2026-07-28
Owner: Rendering / Frame Scheduling
Status: Blocked By Workstream 03
Sequence: 04 of 08
Predecessor: [03 - True GPU-Driven Zero-Readback Submission](03-vulkan-true-zero-readback-submission-todo.md)
Blocks: [05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)

Predecessor evidence:

- [Collect-Visible Render Wait Decoupling](../../COMPLETED/collect-visible-render-wait-decoupling-todo.md)
  established the work-versus-wait counters, bounded late-data policy, and
  existing one-frame-ahead synchronization behavior. This workstream is its
  successor for backend-ready preparation.

## Sequential Execution Contract

- Do not start this workstream until workstream 03 is marked `Complete`.
- Treat the submission contract produced by workstream 03 as an input, not a
  moving target.
- Do not start workstream 05 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

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

- [ ] Classify every render-thread stage as scene preparation, backend
  preparation, command encoding, submission, present, wait, or unrelated job.
- [ ] Mark which stages are pure, snapshot-dependent, backend-thread-affine, or
  externally synchronized.
- [ ] Record data dependencies and mutation sources for each movable stage.
- [ ] Establish per-stage budgets from workstream 01.

Acceptance criteria:

- [ ] All CPU time before submit has an owner and a reason for remaining on or
  moving off the render thread.
- [ ] No stage is moved merely by wrapping it in `Task.Run`.

## Phase 1 - Define Ownership And Handoff

- [ ] Define immutable package identity, frame/generation IDs, and in-flight
  lifetime.
- [ ] Define double- or triple-buffered ownership with no producer/consumer
  mutation race.
- [ ] Define how transform, material, texture, pipeline, and render-graph
  changes invalidate prepared data.
- [ ] Define behavior for a late package, skipped frame, resized viewport,
  failed submit, and shutdown.
- [ ] Preserve the distinction between Mode A (collect waiting for slow render)
  and Mode B (render starved by slow collect); stale reuse or blocking policy
  must identify which mode triggered it.
- [ ] Define maximum acceptable package age and latency.
- [ ] Keep producer lookahead and storage strictly bounded. The current handoff
  is approximately one frame ahead; any larger double/triple buffering requires
  an explicit latency and ownership justification.

Acceptance criteria:

- [ ] The render thread can validate package freshness in bounded time without
  traversing scene state.
- [ ] A producer cannot overwrite data referenced by an in-flight frame.
- [ ] Stale or missing data produces an explicit metric and policy decision.

## Phase 2 - Move Pure Preparation

- [ ] Build render packets, sorting keys, material selections, and dependency
  snapshots alongside collect-visible.
- [ ] Prepare resource-plan and descriptor/uniform inputs for the upcoming
  frame.
- [ ] Cache stable data by precise generations rather than rebuilding all
  entries.
- [ ] Use bounded parallel collection only where measured work and ownership
  make it beneficial.
- [ ] Preserve the existing `BlockUntilFresh` default and explicitly count any
  authorized previous-visibility reuse, dropped stale package, or wait.
- [ ] Eliminate per-frame allocations, LINQ, captured closures, and string
  creation from the producer and consumer hot paths.
- [ ] Keep mutable Vulkan object access on its legal owner thread.

Acceptance criteria:

- [ ] The immutable package is complete before publication.
- [ ] Render-thread scene/material traversal is eliminated from steady-state
  submission.
- [ ] Preparation overlaps rendering rather than waiting idle behind it.

## Phase 3 - Consume And Validate

- [ ] Make Vulkan consume only validated package data for steady-state frames.
- [ ] Measure package production, publish, wait, validation, and consumption
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

- [ ] A documented immutable backend-ready frame package is produced alongside
  collect-visible and consumed by Vulkan.
- [ ] The render thread primarily validates, encodes, submits, and presents.
- [ ] Collect/render overlap and backpressure metrics prove improved
  utilization.
- [ ] Evidence shows improvement from overlapped useful preparation, not merely
  a farther-ahead producer while the render thread remains the bottleneck.
- [ ] Static, moving, mutation, resize, and shutdown stress tests pass.
- [ ] Release build, focused tests, and canonical performance cohorts pass.
- [ ] Evidence and remaining thread-affine work are recorded.
- [ ] This document is marked `Complete`.

Only after this gate is complete may work begin on
[05 - Vulkan Command Recording Worker Architecture](05-vulkan-command-recording-worker-architecture-todo.md).
