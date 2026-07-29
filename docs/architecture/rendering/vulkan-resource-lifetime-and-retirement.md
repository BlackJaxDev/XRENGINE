# Vulkan Resource Lifetime And Retirement

Vulkan resource lifetime state is owned by
`VulkanResourceLifetimeTracker`. Deferred-destruction queue state and
deduplication are owned by `VulkanResourceRetirementQueue`. Renderer partials
may coordinate native API calls, but they must not introduce parallel lifetime
registries, retirement queues, or completion watermarks.

## Exactly-Once Invariants

1. A non-zero native handle is registered with a monotonically increasing
   generation before it can be published to recording or descriptor state.
2. Recorded, queued, and submitted references pin the exact
   `(object type, handle, generation)` they observed. Handle equality alone is
   never sufficient after retirement begins.
3. Retirement captures the maximum graphics, transfer, and other-queue
   completion observations plus its generation pins. The capture also marks the
   generation pending retirement and invalidates dependent cached work.
4. `VulkanResourceRetirementQueue` admits a native handle only once across all
   frame slots. A duplicate enqueue is a no-op; it cannot create a second native
   destruction opportunity.
5. A queued entry becomes destroyable only after all captured queue sequences
   have completed, all recorded/queued generation pins have been released, and
   any external-ownership flag has been cleared.
6. Removing a ready entry releases its queue deduplication reservation exactly
   once. The renderer then performs the corresponding Vulkan destroy/free call
   and reports completion to `VulkanResourceLifetimeTracker`.
7. Completion marks the exact generation destroyed and removes its dependency
   indexes. A stale or repeated destroy request cannot match a live generation
   and is rejected or ignored before invoking Vulkan.
8. Forced teardown may bypass completion readiness only inside the explicit
   forced-retirement scope. It still passes through the same completion path
   and records forced-destruction diagnostics.

## Ownership Boundaries

- `VulkanBufferResourceManager` owns buffer allocator selection, engine buffer
  allocation records, legacy device-address allocations, and live-handle
  deduplication.
- `VulkanImageAllocationTracker` contains only engine-owned image allocations
  and copied allocation diagnostics. Imported or external images enter it only
  if allocation ownership explicitly transfers to the renderer.
- `VkImageBackedTexture` keeps wrapper state but separates lifecycle,
  engine-owned allocation, imported upload preparation/publication, view cache,
  sampler, layout, transfer, events, staging, and mipmap behavior into focused
  partial files.
- `VkDataBuffer` contains wrapper behavior only. Renderer-level buffer
  allocation, mapping, upload, and destruction behavior lives under
  `Resources/Buffers`.
- Imported texture upload contracts, preparation, transfer submission,
  publication, and queue policy are separate source owners. Prepared resources
  are published only after transfer completion; replaced resources then enter
  the normal retirement path.

## Recording And Allocation

Persistent Vulkan image or buffer creation is rejected while the render-graph
command-recording scope is active. Persistent resources must be allocated by
planning or upload preparation before recording begins. This preserves the
allocation-free steady-state recording and submission paths; the organization
refactor adds no per-frame collections, closures, or delegates.

