# XR Job Manager

High-level documentation for the cooperative job system that powers async work across the engine. The system lives in `XREngine/Jobs` with `JobManager` orchestrating scheduling, threading, priorities, affinities, and lifecycle management.

## Architectural Overview
- **Cooperative stepping**: Jobs are enumerator-driven (`IEnumerator`/`IEnumerable`). Each dispatch calls `Job.Step()` which advances the iterator, handles yielded values, and requeues when needed.
- **Thread pool owned by the manager**: `JobManager` spins up background worker threads (default: CPU count minus 4 reserved, capped at 16) and drives work without relying on .NET thread pool heuristics.
- **Affinities**: Jobs choose one of four lanes: `Any` (worker threads), `MainThread` (polled by `Engine.Jobs.ProcessMainThreadJobs()`), `CollectVisibleSwap` (polled during render/collect-visible swap), and `Remote` (dispatched through a transport for out-of-process work).
- **Priorities**: Five priority buckets (`Lowest`..`Highest`) exist per affinity. Aging prevents starvation by picking the longest-waiting job (over ~2s) before raw priority order.
- **Bounded queue (optional)**: When enabled, a semaphore gates total enqueued jobs (default cap 8192, warn at 2048). Slots free on completion.
- **Remote dispatch**: If `RemoteTransport` is set, `ScheduleRemote` wraps a request into a job that lives in the remote affinity lane.

## Threads, Queues, and Dispatch
- **Workers**: Created in the `JobManager` constructor. Count = `XR_JOB_WORKERS` env var or `processorCount - 4` (minimum 1) capped by `XR_JOB_WORKER_CAP` or 16.
- **Queues per affinity**: `ConcurrentQueue<Job>[5]` for `Any`, `MainThread`, `CollectVisibleSwap`, and `Remote`. Each tracks counts for metrics and logging.
- **Dispatch loop**: Workers wait on `_readySignal`, dequeue with aging, then call `ExecuteJob`. Remote jobs use a lazily created `Task` that runs `RemoteWorkerLoop` and idle-timeouts after 30s if no work remains.
- **Requeue rules**: A job that is `Waiting`, `Idle`, or exceeds the per-dispatch step cap is requeued without consuming an extra queue slot. Completed jobs release the slot.
- **Per-dispatch cap**: Up to 64 `Job.Step()` calls per dispatch to avoid monopolizing a worker.
- **Backpressure logging**: When bounded, acquisition waits in 50 ms polls and logs every second if blocked.

## Job Lifecycle
1. **Creation**: A `Job` subclass implements `IEnumerable Process()`. `EnumeratorJob` wraps an `IEnumerable` or factory; `ActionJob` and `CoroutineJob` cover simple cases.
2. **Scheduling**: `Schedule` sets priority, affinity, links cancellation tokens, attaches a `TaskCompletionSource`, marks queue usage, and enqueues. The returned `JobHandle` exposes `Wait/WaitAsync`, `Cancel`, and status flags.
3. **Execution**: `Job.Step()` advances the iterator and interprets yields:
   - `IEnumerator`/`IEnumerable`: pushes nested routines.
   - `Task`/`ValueTask`/`Func<Task>`: attaches; job requeues while awaiting.
   - `JobProgress`, `float`, `double`: updates progress (optionally payload).
   - `Action`: invoked immediately.
   - `WaitForNextDispatch.Instance`: yields control and requeues next dispatch.
   - Any other object: stored as `Payload` and treated as progress.
4. **Completion paths**: `Completed`, `Canceled`, or `Faulted` all clear execution state, fire callbacks, and resolve the completion source accordingly.
5. **Starvation detection**: Jobs remember enqueue timestamps. If a job waits 2s+, a warning logs once. Average wait per priority can be queried via `GetAverageWait`.

## Affinity Lanes and Engine Integration
- **Any (default)**: Runs on `JobManager` worker threads. `Process()` can be called manually but workers already drive this lane.
- **MainThread**: Enqueued jobs run when `Engine.Jobs.ProcessMainThreadJobs()` is called (e.g., from the main thread pump in `Engine.ProcessPendingMainThreadWork`). Use for UI, scene graph, or API calls that must be on the main thread.
- **CollectVisibleSwap**: Consumed inside `EngineTimer.CollectVisibleThread` before swap buffers. Use for render-graph prep that must synchronize with collect-visible/swap cadence.
- **Remote**: Uses `IRemoteJobTransport` to send `RemoteJobRequest` and await `RemoteJobResponse`. A dedicated loop exists only while work is queued.

## Progress, Callbacks, and Payloads
- `Job.ProgressChanged` and `Job.ProgressWithPayload` fire on the job's `SynchronizationContext` (captured at construction unless overridden).
- `Completed`, `Canceled`, and `Faulted` events mirror lifecycle transitions; `EnumeratorJob` wires optional delegates passed to `Schedule` helpers.
- `SetPayload` stores the last payload seen. Yielding a `JobProgress(value, payload)` both advances progress and persists the payload.

## Cancellation and Fault Handling
- `Schedule` accepts an external `CancellationToken`; the job links and cancels if the token fires. Manual `Job.Cancel()` or `JobHandle.Cancel()` also works.
- If a yielded `Task` faults, the job faults with the base exception. If the task is canceled, the job cancels. Exceptions thrown inside the iterator also fault the job.
- Cancel/fault outcomes propagate to the `Task` inside `JobHandle` so callers can `await` with standard semantics.

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

// Remote job
var response = await Engine.Jobs.ScheduleRemote(new RemoteJobRequest
{
    Operation = RemoteJobOperations.AssetLoad,
    Payload = requestBytes,
    Metadata = meta,
}, JobPriority.Normal, cts.Token);
```

## Environment Variables
- `XR_JOB_WORKERS`: Override worker thread count.
- `XR_JOB_WORKER_CAP`: Hard cap on worker threads (default 16).
- `XR_JOB_QUEUE_LIMIT`: Max enqueued jobs when bounded (default 8192; 0 disables bounding).
- `XR_JOB_QUEUE_WARN`: Warning threshold per priority bucket (default 2048; clamped to limit).

## Operational Tips
- Prefer yielding `Task`/`ValueTask` for I/O; avoid CPU-heavy work without yielding or offload to dedicated threads.
- Keep per-dispatch work small; long tight loops should occasionally `yield return WaitForNextDispatch.Instance` or yield progress to stay responsive.
- Use affinities intentionally: only mark `MainThread` or `CollectVisibleSwap` when required to keep worker threads free.
- Monitor wait times (`GetAverageWait`) and queue lengths in hot scenes; increase limits or reduce job burst sizes if backpressure appears.
- Call `Shutdown()` during engine teardown to stop workers cleanly.