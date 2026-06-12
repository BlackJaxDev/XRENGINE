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

- `Any`: background worker threads.
- `MainThread`: scene, editor, or UI work that must run on the main thread.
- `CollectVisibleSwap`: render-prep work synchronized with collect-visible and swap timing.
- `Remote`: transport-backed out-of-process work.

## Useful Environment Variables

- `XR_JOB_WORKERS`: worker thread count.
- `XR_JOB_WORKER_CAP`: maximum worker count.
- `XR_JOB_QUEUE_LIMIT`: bounded queue capacity.
- `XR_JOB_QUEUE_WARN`: queue warning threshold.

## Deeper Docs

- [XR Job Manager](../developer-guides/runtime/job-system.md)
- [Engine API](../developer-guides/runtime/engine-api.md)
