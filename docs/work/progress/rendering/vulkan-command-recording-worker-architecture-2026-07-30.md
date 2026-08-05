# Vulkan Command Recording Worker Architecture Progress

Last Updated: 2026-08-04
Owner: Rendering / Vulkan Command Buffers
Status: Closed as an implementation ledger on 2026-08-04; all deferred
acceptance is owned by the linked workstream 03-05 validation gate

Owning work item:

- [Workstream 05 validation](../../testing/rendering/03-05-optimization-validation-todo.md#workstream-05-validation)

Pre-06 validation:

- [Vulkan Optimization Workstreams 03-05 Validation](../../testing/rendering/03-05-optimization-validation-todo.md#workstream-05-validation)

## Implemented Architecture

Dirty scheduled mesh chains can now record through persistent renderer-owned
threads. The worker domain is created lazily, bounded to eight workers while
leaving one logical processor for the render thread, and retained across
frames. Each worker owns graphics command pools for every indexed frame slot
and a private resource-planner switching state.

Before dispatch, the render thread:

1. completes pipeline, descriptor, image-transition, and frame-data
   preparation;
2. captures the exact immutable `ResourcePlannerRuntimeState` for each dirty
   chain;
3. invalidates every dirty secondary so failure cannot leave a stale
   executable result;
4. assigns renderer ownership for the batch; and
5. releases only independent chains to workers.

A heterogeneous chain may touch several `VkMeshRenderer` instances. The first
chain to touch a renderer pins it to one worker for that batch. A later chain
that would bridge two already assigned worker ownership components is recorded
serially after worker completion and increments the conflict counter. This
keeps mutable renderer bind/draw state single-threaded without requiring every
chain to contain only one renderer family.

Contexts that differ only by captured planner, resource, descriptor, or
diagnostic generations may share a worker batch. Render target, dimensions,
pipeline, viewport, queue family, stereo/multiview, registry identity, and
submission-order policy remain exact compatibility requirements.

## Scheduling And Failure Contract

- A single dirty chain remains serial. Two independent chains are the minimum
  dispatch batch because that is the first cohort capable of overlap.
- Hardware-specific threshold tuning is deferred to the canonical small,
  medium, and large closeout cohorts.
- Worker completion order never controls execution order. The primary executes
  secondary command buffers in the original scheduled chain order.
- Dispatch and cancellation waits are bounded to two seconds and attributed
  separately.
- A worker exception or timeout faults and quarantines the worker domain,
  invalidates the affected frame, and prevents partial submission.
- Future frames use the visible serial path after a worker-domain fault.
- Resize, command-buffer destruction, and renderer teardown first request
  cancellation, wait within the same bound, and destroy only pools whose
  owning thread has stopped. A timed-out pool is retained instead of being
  destroyed while potentially in use.

## Allocation And Ownership Contract

Worker batch arrays, job indices, planner snapshots, completion state, renderer
ownership entries, and result buffers grow and are reused. Worker threads,
events, switching state, and per-frame-slot command pools persist. No
`Task.Run`, captured closure, or generic scheduler wait remains in Vulkan
command recording.

Primary and secondary encoding allocations remain separately observable
through Vulkan CPU-stage telemetry. Canonical zero-allocation promotion is
deferred; the targeted smoke sampled zero secondary-recording allocation but
still observed primary-recording allocations owned by the broader deferred
allocation closeout.

## Telemetry

The runtime stats, profile capture, and editor MCP profiler surface now expose:

- scheduled, queued, worker-started, worker-completed, serially recorded, and
  reused chain counts;
- renderer-ownership conflicts, worker failures, and wait timeouts;
- peak concurrent workers;
- maximum queue delay;
- aggregate worker record time;
- worker active span and derived overlap;
- deterministic merge time; and
- render-thread wait for chain workers.

Schedule evaluation is no longer reported as worker record or worker-wait time.
A sampled frame with no dispatched workers therefore reports zero worker
activation, record, active-span, overlap, and wait metrics.

## Targeted Evidence

- `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj -c Release --no-restore -p:UseSharedCompilation=false`
  passed with zero compiler errors and no new compiler warnings.
- The isolated `ws05-workers` editor session built and reached MCP readiness
  with Vulkan command chains and validation enabled.
- The deterministic built-in Unit Testing World box cohort was exercised with
  256 boxes through
  `Build/_AgentValidation/renderer-root-trace/workstream05-settings.jsonc`.
- Sampled Vulkan telemetry reported zero validation-layer errors, zero worker
  failures, zero worker wait timeouts, and zero secondary-recording managed
  allocation. It also verified that frames with no worker dispatch report zero
  worker time instead of schedule-build time.
- Source-contract tests cover worker policy, persistent threads, bounded waits,
  per-worker pools, thread-local planner state, removal of generic task
  dispatch, and command-chain batch compatibility.
- The focused unit-test project remains blocked before test execution by
  unrelated pre-existing compile errors in stale Vulkan/OpenXR tests
  (`nint.ShouldBe`, missing `OpenXrViewResourcePlannerContextKey`,
  `EOpenXrResourcePlannerPurpose`, and `EDesktopFramePhase`).

The live run is implementation smoke evidence only. Worker-overlap proof,
serial-versus-parallel performance, allocation promotion, visual parity, and
lifecycle stress are intentionally not claimed here.

## Deferred acceptance in the canonical validation gate

This document owns no remaining execution work. The shared 03-05 closeout must
still:

- capture identical serial and persistent-worker baselines;
- prove two or more concurrent worker intervals on large dirty cohorts;
- tune or confirm the two-chain dispatch floor on target hardware;
- compare small, medium, large, and stable cohorts at p50/p95/p99;
- prove zero steady-state primary and secondary encoding allocation;
- validate CPU-direct behavior and the explicit primary-owned zero-readback
  quarantine;
- run StandardValidation, resize, shader hot reload, scene churn, device-loss
  handling, shutdown, and repeated start/stop stress; and
- confirm stable primary/secondary reuse and exact visual/draw-order parity.

