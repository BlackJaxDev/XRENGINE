# Render Queries

XRENGINE exposes render queries through immutable backend-neutral descriptors.
`XRRenderQuery` owns a `RenderQueryDescriptor`; backend wrappers own recording
state, native pool slots, epochs, and submission state.

## Query model

- `RenderQueryDescriptor` selects the semantic family, occlusion mode,
  pipeline-statistics mask, stream, property, and provider value count.
- `RenderQueryResultLayout` describes values per native query, query count,
  active view slots, availability position, integer width, field order, and
  aggregation.
- `RenderQueryTicket` identifies one epoch by pool, contiguous range, and exact
  submission. A stale ticket is never allowed to consume a newer result.
- `RenderQueryReadResult` returns `Ready`, `NotReady`, `Unsupported`,
  `InvalidState`, `StaleTicket`, `BufferTooSmall`, `BudgetExhausted`,
  `SubsystemUnavailable`, `DeviceLost`, or `ApiError`.

Normal reads are nonblocking. The raw API accepts caller-provided
`Span<ulong>`, and typed helpers decode occlusion, timestamps, elapsed time,
pipeline statistics, transform feedback, primitive counts, and property
values without per-read collections. An explicit Vulkan wait requires a
diagnostic caller name and increments wait telemetry; render paths must not use
it.

## Semantics

| Descriptor | Result |
| --- | --- |
| Boolean occlusion | Visible when any active view slot is nonzero. Vulkan maps both high-level any-samples intents to the same non-precise occlusion query. |
| Exact occlusion | 64-bit sample count, summed across active view slots. Vulkan requires enabled `occlusionQueryPrecise`. |
| Timestamp | Raw ticks plus nanoseconds. OpenGL ticks are already nanoseconds; Vulkan masks queue-family valid bits and applies `timestampPeriod`. |
| Elapsed time | Nanoseconds. Vulkan uses two contiguous timestamp slots and wrap-safe subtraction; OpenGL uses `GL_TIME_ELAPSED`. |
| Pipeline statistics | One value for every enabled bit, in Vulkan bit order. The mask must be nonzero and contain only supported counters. |
| Transform feedback | Primitives written and primitives needed for the selected stream. |
| Primitive/mesh generation | Exact generated primitive count when the required extension feature is enabled. |

OpenGL reports multi-value families it cannot represent with the same contract
as `Unsupported`; it does not substitute an approximate scalar result. OpenGL
query results are read through the 64-bit API.

## Vulkan lifecycle

Vulkan pools are renderer-owned arenas keyed by native query type, statistics
mask, recording provider, queue family, layout, and property. Arenas allocate
bounded 256-slot chunks, grow to at most 16 chunks per compatibility key, and
suballocate contiguous ranges. Exhaustion skips the optional query with an
actionable diagnostic; it never overwrites a pending slot.

Fresh command-buffer recording emits `vkCmdResetQueryPool` before rendering.
Cached-primary replay advances the epoch while retaining the recorded
queue-ordered reset. A slot cannot be reset, released, or returned to a
descriptor-compatible handle pool until its exact queue submission completes
and its availability words are final. Query pools participate in the normal
Vulkan command-buffer resource tracker and renderer retirement path.

`QueryOp` represents reset, begin, end, timestamp, property-write, and result-
copy operations. Query scopes are scheduling barriers and remain inline in the
primary command buffer. Nested or mismatched begin/end operations are rejected
and fail visible rather than ending an unrelated scope. Secondary command
chains are excluded while a query bracket is active; inherited-query execution
is not used until a complete inheritance contract exists.

## Capability and provider rules

`VulkanQueryCapabilities` distinguishes physical-device advertisement,
logical-device enablement, extension and command loading, queue timestamp bits
and stage mask, and subsystem ownership. `VulkanQueryDescriptorMapper` returns
one fully specified native plan or one precise rejection reason before any
command is recorded.

Acceleration-structure, micromap, performance-counter, vendor, and video
queries use `IVulkanSpecializedQueryProvider`. The current renderer publishes
`SubsystemUnavailable` for owners that are not active. A provider must own its
required lock/profile/external state before recording; optional extensions are
not enabled merely to make the capability table appear complete.

The existing Vulkan frame-timing and render-pipeline profiler arenas remain
specialized renderer-owned systems because they already batch dense timing
points and have distinct frame publication rules. `BvhGpuProfiler` uses the
shared backend-neutral timestamp handles on both OpenGL and Vulkan, preserves
partially resolved pairs, and caps pending scopes.

## Diagnostics

Vulkan startup logs the query capability matrix. `RenderQueryTelemetry` counts
allocations, releases, recordings by kind, read statuses, unsupported requests,
explicit waits, host-read bytes, and copied bytes. `VulkanQueryArenaStats`
reports pools, capacity, allocated/high-water slots, allocation/release/growth/
exhaustion counts, reset epochs, and retired pools. CPU occlusion continues to
report per-output pending ages, recovery, forced-visible reasons, budgets, and
submission/resolution latency through `OcclusionTelemetry`.
