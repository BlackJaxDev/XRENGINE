# Engine Work Scheduler and Job System

The process-wide `EngineWorkScheduler` owns both cooperative general jobs and
renderer-neutral render-critical work. `JobManager` remains the public
cooperative-job API, but its `Any` affinity is now drained by the scheduler's
persistent general domain rather than by a second worker pool.

## Architectural Overview

- **One process owner**: engine startup constructs one `EngineWorkScheduler`.
  `Engine.Jobs`, `RuntimeEngine.Jobs`, and
  `RuntimeRenderingHostServices.Work.GeneralJobs` reference its same
  `JobManager` instance.
- **Cooperative general jobs**: jobs are enumerator-driven
  (`IEnumerator`/`IEnumerable`). Each dispatch advances `Job.Step()`, handles
  yielded values, and requeues when needed.
- **Persistent domains**: `EngineGeneralWorkDomain` owns the general lanes;
  `RenderWorkDomain` owns stable renderer-neutral lanes and pooled batches.
  Neither domain relies on thread-pool growth heuristics for its persistent
  lanes.
- **Affinities**: general jobs choose `Any` (general workers), `RenderThread`,
  `AppThread`, `CollectVisibleSwap`, or `Remote`. Foreground affinities are
  polled by their owning engine phase; remote dispatch keeps its existing lazy
  transport loop.
- **Render-thread intent**: render-thread jobs can carry a `RenderThreadJobKind` such as `TextureUpload`, `BufferUpload`, `Readback`, or `RenderPipelineResource`. Debug dispatch diagnostics use this metadata instead of relying on profiler-label string guesses.
- **Priorities**: Five priority buckets (`Lowest`..`Highest`) exist per affinity. Aging prevents starvation by picking the longest-waiting job (over ~2s) before raw priority order.
- **Bounded queue (optional)**: When enabled, a semaphore gates total enqueued jobs (default cap 8192, warn at 2048). Slots free on completion.
- **Remote dispatch**: If `RemoteTransport` is set, `ScheduleRemote` wraps a request into a job that lives in the remote affinity lane.

## Process Execution Topology and Scheduler

`EngineExecutionTopology` is an immutable composition-root snapshot resolved
after startup settings and profile contracts, but before window, renderer, or
OpenXR construction. The request records effective logical processors, the
foreground-loop reservation, general and render-domain counts and caps,
dedicated-background reservations, render-worker QoS, and the source of every
setting.

The precedence is environment, user, project, then engine default. The default
32-logical-processor configuration resolves to four foreground reservations,
16 general workers, and zero background render workers. The render domain still
has logical lane 0, which is the participating render thread. Caps are validated
in `1..32`; general and render counts also permit `0`, and worker counts permit
`-1` for auto.

The sum of foreground, general, render, and declared dedicated lanes may
not exceed `Environment.ProcessorCount` unless
`AllowCpuOversubscription` is explicitly enabled. Invalid environment values or
an invalid/oversubscribed topology fail startup before native windows are
created and include the requested and effective counts in the diagnostic.

`EngineWorkScheduler` applies the snapshot once during startup:

- The general count creates the persistent `XRE-General-*` lanes. `G == 0`
  uses a reentrancy-safe cooperative inline drain and creates no hidden worker.
- The render count creates `XRE-Render-1..R`; logical lane 0 remains the calling
  render thread. `R == 0` is a fully functional inline render domain.
- `RuntimeEngine.Jobs` is an alias to the installed scheduler-owned general
  manager; it cannot construct a second engine pool.
- `RenderWorkerQos.High` is applied only to background render lanes and remains
  a Windows diagnostic policy pending broader hardware acceptance.

This is still a backend migration boundary. Vulkan command-chain workers,
OpenXR eye workers, command-pool ownership, recording order, submission, and
presentation are unchanged in Phase 1B. Their work must move onto this domain in
a later phase rather than creating another scheduler. Existing compiler,
backend, and lazy deferred/remote loops are not yet fully represented by
`DedicatedBackgroundThreadCount`, so the topology snapshot must not yet be
treated as proof of every process thread.

## Threads, Queues, and Dispatch

- **General workers**: During engine startup, the scheduler creates the resolved
  number of persistent lanes. A directly constructed standalone `JobManager`
  retains its compatibility behavior: `XR_JOB_WORKERS` or
  `processorCount - 4` (minimum 1), capped by `XR_JOB_WORKER_CAP` or 16.
- **Queues per affinity**: `ConcurrentQueue<Job>[5]` exists for each priority in
  `Any`, `RenderThread`, `AppThread`, `CollectVisibleSwap`, and `Remote`.
  Each tracks counts for metrics and logging.
- **Dispatch loop**: idle general workers block on the domain signal without a
  polling timeout, dequeue with aging, then call `ExecuteJob`. Remote jobs use a
  lazily created task that runs
  `RemoteWorkerLoop` and idle-times out after 30 seconds if no work remains.
- **Requeue rules**: A job that is `Waiting`, `Idle`, or exceeds the per-dispatch step cap is requeued without consuming an extra queue slot. Completed jobs release the slot.
- **Per-dispatch cap**: Up to 64 `Job.Step()` calls per dispatch to avoid monopolizing a worker.
- **Backpressure logging**: When bounded, acquisition waits in 50 ms polls and logs every second if blocked.

## Renderer-Neutral Render Batches

`RenderWorkDomain` is the Phase 1B primitive for coarse, bounded preparation
work. It is available through `IRuntimeRenderWorkServices.RenderWork`; runtime
modules do not construct their own pool.

Nested synchronous render-batch execution is rejected from every render-work
lane, including attempts to enter a different domain. The process architecture
owns one render domain, and cross-domain nesting would consume the outer lane
while the inner join can wait on it. Idle background render lanes block on their
signals until work or shutdown arrives; they do not wake on a polling timer.

- Lane IDs are stable for the domain lifetime. Lane 0 is the render-thread
  caller; lanes `1..R` are persistent background workers.
- `RentBatch` returns a generation-checked pooled lease. The owner fills
  preallocated `RenderWorkItem` and dependency storage, calls
  `ExecuteAndWait`, then disposes the lease. Stale or cross-domain leases are
  rejected.
- Batches with four or fewer items default to lane-0 inline execution. Larger
  batches queue independent work across the available lanes while lane 0 also
  participates.
- Items may be migratable or lane-affine and may depend on other item indices.
  Migratable work can be stolen; affine work never moves between lanes.
- Per-lane queues are bounded. Overflow faults the batch visibly; it does not
  create an unbounded side queue or silently fall back to another execution
  system.
- `RenderLaneBackendAttachments` stores lane/frame-slot-local opaque backend
  state without introducing a Runtime.Core dependency on Vulkan.
- Cancellation and faults invalidate all partial output. The first authoritative
  executing fault wins over a concurrent cancellation, completion is published
  once, and faulted pooled storage is quarantined until lane 0 finalizes it. A
  quarantine exception poisons the domain and retains the batch; it is never
  counted as successful quarantine or returned to the pool.
- Scheduler-controlled waits use a default two-second lifecycle bound. The
  scheduler cannot preempt arbitrary synchronous code executing on lane 0, so
  `IRenderWorkExecutor.Execute` and `QuarantineFaultedBatch` must be nonblocking,
  must never wait on GPU/task/fence completion, and must return inside that
  bound. Once scheduler control is regained, a non-quiescent batch is
  process-fatal because returning would release state a worker may still use.

The startup smoke executes one four-item renderer-neutral preparation batch and
one general-domain diagnostic decode. `CompletedDiagnosticPayload` accepts only
array-backed, already CPU-visible words and cannot carry a fence, task, wait
handle, polling callback, or custom blocking memory manager. Pending GPU
completion therefore cannot occupy a general worker item by construction.

## Job Lifecycle
1. **Creation**: A `Job` subclass implements `IEnumerable Process()`.
   `EnumeratorJob` wraps an `IEnumerable` or factory; `ActionJob` and
   `CoroutineJob` cover simple cases. Lifecycle/notification tracking is fully
   initialized before extensible `Process()` or `GetEnumerator()` code runs,
   factory code runs outside the lifecycle lock, and a pre-start cancellation
   remains authoritative.
2. **Scheduling**: `Schedule` sets priority, affinity, links cancellation
   tokens, attaches a `TaskCompletionSource`, marks queue usage, and enqueues.
   The returned `JobHandle` exposes `Wait/WaitAsync`, `Cancel`, and status flags.
3. **Execution**: `Job.Step()` advances the iterator and interprets yields:
   - `IEnumerator`/`IEnumerable`: pushes nested routines.
   - `Task`/`ValueTask`/`Func<Task>`: attaches; job requeues while awaiting.
   - `JobProgress`, `float`, `double`: updates progress (optionally payload).
   - `Action`: invoked immediately.
   - `WaitForNextDispatch.Instance`: yields control and requeues next dispatch.
   - Any other object: stored as `Payload` and treated as progress.
4. **Completion paths**: `Completed`, `Canceled`, or `Faulted` all clear
   execution state, dispatch callbacks, and resolve the completion source
   accordingly. Manager ownership remains active until posted terminal/progress
   notifications acknowledge execution. Each context-posted progress
   notification releases that ownership exactly once, including custom contexts
   that dispatch inline and then throw. Completion and fault paths first claim an
   internal terminal-owner state; a fault publishes its authoritative exception
   and fault flag before `IsCompleted` can become visible, and shutdown observes
   rather than replaces an in-progress terminal owner.
5. **Starvation detection**: Jobs remember enqueue timestamps. If a job waits 2s+, a warning logs once. Average wait per priority can be queried via `GetAverageWait`.

## Affinity Lanes and Engine Integration
- **Any (default)**: Runs on the scheduler-owned general domain. With `G == 0`, submission cooperatively drains this affinity inline.
- **RenderThread/MainThread**: Enqueued jobs run when `Engine.Jobs.ProcessMainThreadJobs()` is called from the render-thread pump. Use only for graphics-context or render-thread-owned work.
- **AppThread**: Enqueued jobs run when `Engine.Jobs.ProcessAppThreadJobs()` is called from the update/app-thread phase. Use for scene, editor, and UI mutations owned by that phase.
- **CollectVisibleSwap**: Consumed inside `EngineTimer.CollectVisibleThread` before swap buffers. Use for render-graph prep that must synchronize with collect-visible/swap cadence.
- **Remote**: Uses `IRemoteJobTransport` to send `RemoteJobRequest` and await `RemoteJobResponse`. A dedicated loop exists only while work is queued.

For work that specifically requires the graphics context, prefer the render-thread helpers that accept `RenderThreadJobKind`. Keep scene, editor, networking, and other non-GPU work on `AppThread`/update-thread paths so it does not stall `RenderFrame`.

## Progress, Callbacks, and Payloads
- `Job.ProgressChanged` and `Job.ProgressWithPayload` fire on the job's `SynchronizationContext` (captured at construction unless overridden).
- `Completed`, `Canceled`, and `Faulted` events mirror lifecycle transitions; `EnumeratorJob` wires optional delegates passed to `Schedule` helpers.
- `SetPayload` stores the last payload seen. Yielding a `JobProgress(value, payload)` both advances progress and persists the payload.

## Cancellation and Fault Handling
- `Schedule` accepts an external `CancellationToken`; the job links and cancels if the token fires. Manual `Job.Cancel()` or `JobHandle.Cancel()` also works.
- If a yielded `Task` faults, the job faults with the base exception. If the task is canceled, the job cancels. Exceptions thrown inside the iterator also fault the job.
- Cancel/fault outcomes propagate to the `Task` inside `JobHandle` so callers can `await` with standard semantics.
- Shutdown first closes admission for both scheduler domains, signals both, then
  joins them against one shared two-second deadline. An admitted `Schedule`
  call, queued cancellation, posted terminal/progress callbacks, and asynchronous
  `CancelAsync` completion remain tracked until they settle. User iterator
  factories are never entered after admission closes.
- `Shutdown(waitForWorkers: false)` initiates shutdown but returns `false`; it is
  a process-exit abandonment signal, not permission to tear down referenced
  resources. `Dispose()` throws `TimeoutException` if bounded quiescence fails,
  and dependent executor/backend state must remain alive.

## Priorities and Aging
- Buckets map directly to `JobPriority` enum (0-4). Higher buckets are dequeued first unless a lower-priority job has starved past the aging threshold (~2s), in which case the oldest starving job wins.
- Queue length warnings: for the default bounded queue, logs at 2048 pending items per bucket (clamped to the configured cap) no more than once per second.

## Queue Bounding and Backpressure
- **Enabled when `maxQueueSize > 0`** (default 8192). Each scheduled job reserves a slot; slot is released when the job completes. Requeues do not consume extra slots.
- **Acquisition behavior**: Poll every 50 ms. While blocked, log a backpressure message once per second. If the manager is shutting down, acquisition aborts.
- **Metrics**: `QueueSlotsAvailable`, `QueueSlotsInUse`, and `QueueCapacity` expose current pressure. Per-priority counts are available via `GetQueuedCount`.

## Remote Jobs
- `RemoteJobRequest` describes the operation (`Operation` string), payload, transfer mode (`RequestFromRemote` or `PushDataToRemote`), and optional metadata/sender/target IDs.
- `ScheduleRemote` wraps the request into a job on the `Remote` lane and requires `RemoteTransport` to be assigned. The returned `Task<RemoteJobResponse>` mirrors job completion.
- `RemoteJobResponse` reports success, payload, and optional error; helper `FromError` exists for failures.

## Usage Examples
```csharp
// Cooperative enumerator job with progress and cancellation
var handle = Engine.Jobs.Schedule(
    routine: DownloadAndStreamAssets(),
    progress: p => Debug.Log($"{p:P0} downloaded"),
    completed: () => Debug.Log("Assets ready"),
    canceled: () => Debug.Log("Download canceled"),
    error: ex => Debug.LogError(ex),
    cancellationToken: cts.Token,
    priority: JobPriority.High
);

// Inside the routine
IEnumerable DownloadAndStreamAssets()
{
    yield return new JobProgress(0f);
    foreach (var chunk in chunks)
    {
        yield return FetchChunkAsync(chunk); // awaitable Task
        yield return new JobProgress(chunkIndex++ / (float)chunks.Count);
    }
}

// Main-thread-only job (e.g., scene mutation)
Engine.Jobs.Schedule(new ActionJob(() => SceneGraph.AddNode(node)),
    JobPriority.Normal,
    JobAffinity.MainThread);

// Render-thread GPU work with explicit intent metadata
Engine.EnqueueRenderThreadTask(
    () => texture.PushData(),
    "XRTexture2D.UploadMipmaps",
    RenderThreadJobKind.TextureUpload);

// Remote job
var response = await Engine.Jobs.ScheduleRemote(new RemoteJobRequest
{
    Operation = RemoteJobRequest.Operations.AssetLoad,
    Payload = requestBytes,
    Metadata = meta,
}, JobPriority.Normal, cts.Token);
```

## Environment Variables
- `XR_JOB_WORKERS`: General worker request (`-1` auto, `0` cooperative inline,
  or `1..32` at engine startup).
- `XR_JOB_WORKER_CAP`: General worker cap (`1..32`, default 16).
- `XRE_RENDER_WORKER_THREADS`: Renderer-neutral render-domain request (`-1`
  auto, `0` lane-0 only, or `1..32` background workers; default 0).
- `XRE_RENDER_WORKER_THREAD_CAP`: Render-domain auto cap (`1..32`, default 8).
- `XRE_RESERVED_FOREGROUND_THREADS`: Foreground-loop reservation (`-1` auto or
  `1..32`).
- `XRE_ALLOW_CPU_OVERSUBSCRIPTION`: Diagnostic oversubscription opt-in (`true`,
  `false`, `1`, or `0`; default false).
- `XRE_RENDER_WORKER_QOS`: Renderer-neutral render-worker QoS (`OsDefault` or
  diagnostic `High`).
- `XR_JOB_QUEUE_LIMIT`: Max enqueued jobs when bounded (default 8192; 0 disables bounding).
- `XR_JOB_QUEUE_WARN`: Warning threshold per priority bucket (default 2048; clamped to limit).

All execution-topology variables are read once at startup. `High` QoS remains a
diagnostic request until the hardware matrix validates it for production.

## Operational Tips
- Prefer yielding `Task`/`ValueTask` for I/O; avoid CPU-heavy work without yielding or offload to dedicated threads.
- Keep per-dispatch work small; long tight loops should occasionally `yield return WaitForNextDispatch.Instance` or yield progress to stay responsive.
- Use affinities intentionally: only mark `MainThread` or `CollectVisibleSwap` when required to keep worker threads free.
- Monitor wait times (`GetAverageWait`) and queue lengths in hot scenes; increase limits or reduce job burst sizes if backpressure appears.
- Stop producers first, then call the scheduler's bounded `Shutdown()` during
  engine teardown. Never release scheduler-referenced state after a failed
  quiesce.
