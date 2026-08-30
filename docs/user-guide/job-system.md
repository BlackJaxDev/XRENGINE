# Job System

[Back to user guide](README.md)

Use the job system when work should progress asynchronously without blocking the main editor or gameplay loop. For the scheduler internals and full API details, see [XR Job Manager](../developer-guides/runtime/job-system.md).

## When To Use Jobs

Jobs are appropriate for:

- asset loading and streaming,
- staged import or cooking work,
- background analysis,
- long operations that can yield progress,
- main-thread or render-adjacent work that must be queued for a specific frame phase.

Avoid putting tight CPU loops into a job without yielding. Long work should report progress or yield back to the scheduler.

## Affinity Choices

- `Any`: scheduler-owned general workers, or cooperative inline execution when
  the resolved general-worker count is zero.
- `RenderThread`/`MainThread`: graphics-context or render-thread-owned work.
- `AppThread`: scene, editor, or UI work owned by the update/app thread.
- `CollectVisibleSwap`: render-prep work synchronized with collect-visible and swap timing.
- `Remote`: transport-backed out-of-process work.

## Startup Execution Topology

The engine resolves one `EngineExecutionTopology` and constructs one
`EngineWorkScheduler` before creating windows or a render backend. The default
policy reserves up to four foreground engine loops, selects general workers
automatically with a cap of 16, and creates no background render workers.
Logical render lane 0 still exists and runs on the participating render thread.

Execution settings follow the normal engine, project, user, then environment
override order. An explicit configuration that reserves more threads than
`Environment.ProcessorCount` fails startup unless CPU oversubscription is
deliberately enabled for a diagnostic run. The ImGui effective-settings panel
shows the resolved counts and their sources under **Execution**.

`Engine.Jobs` is the application-facing general-job API. Runtime rendering code
resolves that same scheduler-owned domain through
`RuntimeRenderingHostServices.Work.GeneralJobs`; the former
`RuntimeEngine.Jobs` compatibility facade has been removed. General-worker count
`0` uses cooperative inline execution and creates no general background
threads. Render-worker settings are also active for renderer-neutral pooled
preparation work: `0` selects lane-0-only execution, `1..32` creates that many
background lanes, and `-1` uses the topology's auto policy. A setting change
requires restart.

The scheduler also owns two topology-reserved background lanes: one admits
jobs after a bounded general queue frees a slot, and one dispatches `Remote`
transport work. Both remain signal-blocked when idle and do not borrow .NET
thread-pool workers or render-critical lanes.

Vulkan command-chain recording and paired OpenXR eye-primary recording now use
these same logical render lanes. Each Vulkan lane owns separate transient and
retained command-pool arenas per scheduler frame slot and queue family; reusable
artifacts stay in retained pools. Small or unprofitable command-chain batches
remain inline on lane 0. `XRE_VULKAN_COMMAND_CHAIN_WORKER_COUNT` is retained only
as a controlled benchmark cap over the configured render lanes and never creates
another worker pool.

Small renderer-neutral batches run inline on lane 0. Larger batches expose at
most four migratable items per logical lane and use background lanes only when
at least two items are independent and predicted savings clear the scheduler's
measured queue, wake, merge, and hysteresis cost. Surplus or unprofitable work
stays on lane 0. Batch queues are bounded, and cancellation, faults, or timeouts
are surfaced rather than hidden behind another worker pool or CPU fallback.

Startup performs a post-warmup 32-batch allocation proof over rent/build,
dispatch, execute, join, and lease return. A nonzero stage allocation fails
startup, and the result is recorded in `work-scheduler.log`.

Render-work executors are bounded CPU preparation callbacks. They must not wait
for GPU completion, tasks, or fences; synchronous callback code cannot be
preempted by the scheduler. A fault-quarantine callback that throws poisons the
domain and retains the batch instead of silently recycling partial output.

## Useful Environment Variables

- `XR_JOB_WORKERS`: general worker request (`-1` for auto, `0` for cooperative
  inline execution, or `1..32`).
- `XR_JOB_WORKER_CAP`: general worker cap (`1..32`).
- `XRE_RENDER_WORKER_THREADS`: render-domain workers used by renderer-neutral
  preparation and lane-affine backend recording (`-1` for auto, `0` for lane 0
  only, or `1..32` background lanes).
- `XRE_RENDER_WORKER_THREAD_CAP`: render-domain background-lane auto cap
  (`1..32`).
- `XRE_RESERVED_FOREGROUND_THREADS`: foreground-loop reservation (`-1` for auto,
  or `1..32`).
- `XRE_ALLOW_CPU_OVERSUBSCRIPTION`: diagnostic opt-in (`true`, `false`, `1`, or
  `0`).
- `XRE_RENDER_WORKER_QOS`: render-domain background-lane policy (`OsDefault`
  or diagnostic `High`).
- `XR_JOB_QUEUE_LIMIT`: bounded queue capacity.
- `XR_JOB_QUEUE_WARN`: queue warning threshold.

Execution-topology variables are startup-only and require a restart.

## Deeper Docs

- [XR Job Manager](../developer-guides/runtime/job-system.md)
- [Engine API](../developer-guides/runtime/engine-api.md)
