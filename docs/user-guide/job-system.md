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

`Engine.Jobs` and `RuntimeEngine.Jobs` now use the same scheduler-owned general
domain. General-worker count `0` uses cooperative inline execution and creates
no general background threads. Render-worker settings are also active for renderer-neutral pooled
preparation work: `0` selects lane-0-only execution, `1..32` creates that many
background lanes, and `-1` uses the topology's auto policy. A setting change
requires restart.

Vulkan command-chain recording and OpenXR eye recording have not moved onto
these lanes yet. Changing the generic render-worker count must therefore not be
used as a Vulkan worker-count control until the later backend migration phase.

Small renderer-neutral batches run inline on lane 0; larger independent batches
can overlap across background lanes while lane 0 participates. Batch queues are
bounded, and cancellation, faults, or timeouts are surfaced rather than hidden
behind another worker pool or CPU fallback.

Render-work executors are bounded CPU preparation callbacks. They must not wait
for GPU completion, tasks, or fences; synchronous callback code cannot be
preempted by the scheduler. A fault-quarantine callback that throws poisons the
domain and retains the batch instead of silently recycling partial output.

## Useful Environment Variables

- `XR_JOB_WORKERS`: general worker request (`-1` for auto, `0` for cooperative
  inline execution, or `1..32`).
- `XR_JOB_WORKER_CAP`: general worker cap (`1..32`).
- `XRE_RENDER_WORKER_THREADS`: renderer-neutral render workers (`-1` for auto,
  `0` for lane 0 only, or `1..32` background lanes).
- `XRE_RENDER_WORKER_THREAD_CAP`: renderer-neutral render-worker auto cap
  (`1..32`).
- `XRE_RESERVED_FOREGROUND_THREADS`: foreground-loop reservation (`-1` for auto,
  or `1..32`).
- `XRE_ALLOW_CPU_OVERSUBSCRIPTION`: diagnostic opt-in (`true`, `false`, `1`, or
  `0`).
- `XRE_RENDER_WORKER_QOS`: renderer-neutral render-worker policy (`OsDefault`
  or diagnostic `High`).
- `XR_JOB_QUEUE_LIMIT`: bounded queue capacity.
- `XR_JOB_QUEUE_WARN`: queue warning threshold.

Execution-topology variables are startup-only and require a restart.

## Deeper Docs

- [XR Job Manager](../developer-guides/runtime/job-system.md)
- [Engine API](../developer-guides/runtime/engine-api.md)
