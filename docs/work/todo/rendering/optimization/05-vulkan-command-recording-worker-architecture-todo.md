# 05 - Vulkan Command Recording Worker Architecture TODO

Last Updated: 2026-07-28
Owner: Rendering / Vulkan Command Buffers
Status: Blocked By Workstream 04
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

- Do not start this workstream until workstream 04 is marked `Complete`.
- Workers must consume the immutable preparation contract from workstream 04;
  they must not restore concurrent access to mutable global scene state.
- Do not start workstream 06 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

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

## Scope

- Worker lifetime, ownership, scheduling, and shutdown.
- Per-worker command pools and command-buffer lifetime.
- Primary-variant ownership in chain keys and secondary lifetime through the
  last primary/submission that can execute them.
- Resource-plan access and deterministic state-result merging.
- Dynamic-rendering inheritance, pass ordering, transparent ordering, and
  volatile-overlay isolation inherited from the packet/chain migration.
- Dirty-chain partitioning and serial/parallel threshold policy.
- Worker activation, queueing, active interval, wait, and encoded-chain
  telemetry.
- Exceptional, cancelled, resized, and device-lost paths.

## Non-Goals

- Parallelizing stable frames that should reuse command buffers.
- Parallelizing work too small to amortize scheduling and merge cost.
- Allowing multiple workers to mutate one global planner.
- Counting task creation as useful parallelism.

## Phase 0 - Prove Current Behavior

- [ ] Capture scheduled, queued, worker-started, worker-completed, serially
  recorded, and reused chain counts separately.
- [ ] Capture worker overlap, queue delay, record time, merge time, and
  render-thread wait.
- [ ] Add a deterministic dirty-chain stress scene large enough to expose
  parallel benefit.
- [ ] Reuse predecessor characterization cases for structural/frame-data
  signatures, descriptor/resource/pipeline generations, primary-owned
  secondaries, dynamic-rendering inheritance, and volatile overlays.
- [ ] Capture serial and current worker-path baselines with identical prepared
  inputs.

Acceptance criteria:

- [ ] Telemetry proves whether two or more workers actually encode
  concurrently.
- [ ] Disabled, rejected, and serial-fallback work cannot appear as parallel
  work.

## Phase 1 - Remove Shared Mutable Planning

- [ ] Give workers immutable resource/dependency plans from workstream 04.
- [ ] Use per-worker command pools and thread-owned temporary state.
- [ ] Represent worker-produced state effects as deterministic merge results
  rather than global mutations during encoding.
- [ ] Define conflict detection and a visible serial path for chains that
  cannot be independent.
- [ ] Prove all referenced resources outlive worker recording and submission.
- [ ] Prove a secondary cannot be reset, freed, or rebound while any cached
  primary variant or in-flight submission can execute it.

Acceptance criteria:

- [ ] No renderer-wide planner lock surrounds worker command encoding.
- [ ] Merge order is deterministic and covered by state-transition tests.
- [ ] Conflicting chains are explicit and cannot race.

## Phase 2 - Persistent Worker Scheduling

- [ ] Replace synchronous per-frame `Task.Run` batching with persistent
  renderer-owned workers.
- [ ] Preallocate work nodes, completion state, and per-frame result storage.
- [ ] Add a measured threshold below which serial encoding is faster.
- [ ] Bound render-thread completion waits and attribute them separately.
- [ ] Handle resize, device loss, cancellation, exception, and shutdown without
  leaked or reused-in-flight command buffers.
- [ ] Preserve exact render-graph/pass order and transparent ordering; worker
  completion order must never become execution order.

Acceptance criteria:

- [ ] Worker recording introduces zero steady-state managed allocations.
- [ ] The render thread does not block on a generic task scheduler.
- [ ] Worker failure produces a visible frame failure or explicit safe serial
  recovery, never partial submission.

## Phase 3 - Performance And Correctness Validation

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

- [ ] Persistent worker recording is either safely enabled with proven benefit
  or the unsupported configuration is removed so behavior is not misleading.
- [ ] Dirty-chain stress captures show actual worker activation and bounded
  render-thread wait.
- [ ] Stable primary/secondary reuse and zero-readback contracts remain intact.
- [ ] Release build, focused tests, stress runs, validation layers, and
  canonical performance cohorts pass.
- [ ] Evidence and the serial threshold policy are recorded.
- [ ] This document is marked `Complete`.

Only after this gate is complete may work begin on
[06 - Forward+ Prepass And Render-Graph Cost](06-forward-prepass-and-render-graph-cost-todo.md).
