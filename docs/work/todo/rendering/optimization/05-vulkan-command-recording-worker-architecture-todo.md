# 05 - Vulkan Command Recording Worker Architecture TODO

Last Updated: 2026-07-30
Owner: Rendering / Vulkan Command Buffers
Status: Implementation Complete; Acceptance Deferred
Sequence: 05 of 08
Predecessor: [04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md)
Blocks: [06 - Forward+ Prepass And Render-Graph Cost](06-forward-prepass-and-render-graph-cost-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)

Related tracker:

- [Vulkan Primary Command Recording Fast Path](vulkan-primary-command-recording-fast-path-todo.md)

Predecessor evidence:

- [Completed Vulkan Parallel Command Chain Refactor](../../COMPLETED/vulkan-parallel-command-chain-refactor-todo.md)
  established the packet/chain model and feature-flagged migration. This
  workstream is the canonical successor for safe production worker enablement;
  the predecessor's completed status does not assert real parallel encoding.

## Sequential Execution Contract

- Workstream 04 reached `Implementation Complete; Acceptance Deferred` under
  the owner-authorized closeout sequence, so implementation could begin.
- Workers must consume the immutable preparation contract from workstream 04;
  they must not restore concurrent access to mutable global scene state.
- Workstream 06 may begin because implementation is complete. The unchecked
  acceptance and Exit Gate criteria remain owned by the
  [01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md).

## Goal

Provide safe, allocation-free, genuinely parallel secondary-command recording
for dirty workloads. Remove misleading parallel settings, global planner
serialization, per-frame task creation, and unbounded render-thread waits.

## Starting Evidence

- `ParallelCommandChainWorkerRecordingSafe` is hard-disabled.
- The fallback records dirty chains serially on the render thread.
- The worker implementation takes a renderer-wide mutable resource-planner
  lock and the render thread waits on a countdown event.
- Zero-readback and mutable GPU-driven operations are excluded from command
  chain lowering, so prior parallel flags did not affect the main measured
  path.
- Prior telemetry could show scheduling intent without proving concurrent
  command encoding.
- Workstream 03's retained Monado RVC Quick capture reported
  `VulkanRecordCommandBufferAllocatedBytesTotal=3,263,104` and
  `VulkanPrimaryRecordingAllocatedBytesTotal=3,255,936`, while secondary
  recording reported zero. These generic command-encoding allocations are an
  explicit workstream-05 handoff; any allocation attributed to the predecessor's
  compact submission code remains owned by workstream 03.

## Scope

- Worker lifetime, ownership, scheduling, and shutdown.
- Per-worker command pools and command-buffer lifetime.
- Primary-variant ownership in chain keys and secondary lifetime through the
  last primary/submission that can execute them.
- Resource-plan access and deterministic state-result merging.
- Dynamic-rendering inheritance, pass ordering, transparent ordering, and
  volatile-overlay isolation inherited from the packet/chain migration.
- Dirty-chain partitioning and serial/parallel threshold policy.
- Allocation attribution and removal across serial primary encoding and worker
  secondary encoding, coordinated with the primary-recording fast-path child.
- Worker activation, queueing, active interval, wait, and encoded-chain
  telemetry.
- Exceptional, cancelled, resized, and device-lost paths.

## Non-Goals

- Parallelizing stable frames that should reuse command buffers.
- Parallelizing work too small to amortize scheduling and merge cost.
- Allowing multiple workers to mutate one global planner.
- Counting task creation as useful parallelism.

## Phase 0 - Prove Current Behavior

- [x] Capture scheduled, queued, worker-started, worker-completed, serially
  recorded, and reused chain counts separately.
- [x] Capture worker overlap, queue delay, record time, merge time, and
  render-thread wait.
- [x] Add a deterministic dirty-chain stress scene large enough to expose
  parallel benefit.
- [ ] Reuse predecessor characterization cases for structural/frame-data
  signatures, descriptor/resource/pipeline generations, primary-owned
  secondaries, dynamic-rendering inheritance, and volatile overlays.
- [ ] Capture serial and current worker-path baselines with identical prepared
  inputs.
- [x] Attribute primary/secondary command-encoding allocations to exact
  operations and distinguish generic encoder work from predecessor-owned
  submission preparation.

Acceptance criteria:

- [ ] Telemetry proves whether two or more workers actually encode
  concurrently.
- [ ] Disabled, rejected, and serial-fallback work cannot appear as parallel
  work.

## Phase 1 - Remove Shared Mutable Planning

- [x] Give workers immutable resource/dependency plans from workstream 04.
- [x] Use per-worker command pools and thread-owned temporary state.
- [x] Represent worker-produced state effects as deterministic merge results
  rather than global mutations during encoding.
- [x] Define conflict detection and a visible serial path for chains that
  cannot be independent.
- [x] Prove all referenced resources outlive worker recording and submission.
- [x] Prove a secondary cannot be reset, freed, or rebound while any cached
  primary variant or in-flight submission can execute it.

Acceptance criteria:

- [x] No renderer-wide planner lock surrounds worker command encoding.
- [x] Merge order is deterministic and covered by state-transition tests.
- [x] Conflicting chains are explicit and cannot race.

## Phase 2 - Persistent Worker Scheduling

- [x] Replace synchronous per-frame `Task.Run` batching with persistent
  renderer-owned workers.
- [x] Preallocate work nodes, completion state, and per-frame result storage.
- [x] Add an explicit threshold below which work remains serial.
  The implementation floor is two independent chains, the first batch capable
  of overlap; empirical hardware tuning remains in closeout.
- [x] Bound render-thread completion waits and attribute them separately.
- [x] Handle resize, device loss, cancellation, exception, and shutdown without
  leaked or reused-in-flight command buffers.
- [x] Preserve exact render-graph/pass order and transparent ordering; worker
  completion order must never become execution order.

Acceptance criteria:

- [ ] Worker recording introduces zero steady-state managed allocations.
- [ ] Serial primary recording and worker secondary recording both report zero
  steady-state managed allocation on their applicable canonical cohorts.
- [x] The render thread does not block on a generic task scheduler.
- [x] Worker failure produces a visible frame failure or explicit safe serial
  recovery, never partial submission.

## Phase 3 - Performance And Correctness Validation

Deferred in full to the
[01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)
by owner direction. These criteria remain intentionally unchecked.

- [ ] Compare persistent workers against serial encoding on small, medium, and
  large dirty-chain cohorts.
- [ ] Confirm stable cohorts continue to reuse rather than invoke workers.
- [ ] Validate CPU-direct and zero-readback paths supported by command-chain
  lowering.
- [ ] Run validation layers, resize, shader hot reload, scene churn, device
  shutdown, and repeated start/stop stress.
- [ ] Compare p50/p95/p99 record, merge, wait, and total render time.

Acceptance criteria:

- [ ] Large dirty workloads show real overlapping worker intervals and meet
  the improvement threshold established in workstream 01.
- [ ] Small and stable workloads do not regress beyond declared variance.
- [ ] No global lock, allocation, command-pool ownership, or Vulkan validation
  regression remains.

## Exit Gate

- [x] Persistent worker recording is safely enabled for independent dirty
  chains without retaining a misleading hard-disabled implementation. Benefit
  remains a closeout measurement.
- [ ] Dirty-chain stress captures show actual worker activation and bounded
  render-thread wait.
- [x] Stable primary/secondary reuse and zero-readback ownership contracts
  remain intact in the implementation; canonical stress remains deferred.
- [ ] The workstream-03 generic command-encoding allocation handoff is closed
  without moving allocation into preparation, merge, or submission.
- [ ] Release build, focused tests, stress runs, validation layers, and
  canonical performance cohorts pass.
- [x] Evidence and the serial threshold policy are recorded.
- [ ] This document is marked `Complete`.

Implementation may now proceed to
[06 - Forward+ Prepass And Render-Graph Cost](06-forward-prepass-and-render-graph-cost-todo.md).
Acceptance completion remains blocked on the shared closeout.

## Implementation Closeout

Implementation evidence and contracts are recorded in
[Vulkan Command Recording Worker Architecture Progress](../../../progress/rendering/vulkan-command-recording-worker-architecture-2026-07-30.md).

The completed implementation:

- replaces generic per-frame task dispatch with bounded, persistent
  renderer-owned threads;
- gives every worker frame-slot-owned Vulkan command pools and thread-local
  planner switching state;
- captures immutable planner runtime snapshots before dispatch and removes the
  renderer-wide planner lock from command encoding;
- pins every renderer touched by a chain to one worker for the batch, with
  deterministic serial fallback when ownership components conflict;
- records worker queueing, activation, completion, concurrency, overlap,
  aggregate record time, merge time, wait time, reuse, conflicts, failures,
  and timeouts without relabeling schedule-build time as worker work;
- quarantines a faulted worker domain and fails the frame rather than
  submitting a partial result;
- bounds dispatch, cancellation, idle, and teardown waits;
- preserves scheduled chain order as execution order regardless of worker
  completion order;
- removes the obsolete parallel-secondary setting and all command-recording
  `Task.Run` paths; and
- keeps mutable zero-readback indirect/count streams on their existing
  explicit primary-command quarantine.
