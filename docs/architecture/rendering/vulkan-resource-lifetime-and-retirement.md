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

## Interned Image Views

`VulkanImageResourceService` owns the native lifetime of interned image views.
Each acquisition returns a `VulkanInternedImageViewReference` with the handle
and its registered generation. The service captures that pair under the intern
cache lock and the lifetime tracker lock, in that order.

Texture-view wrappers and compute closures retain this reference. A release
decrements only the cache entry with the same handle and generation. A missing
or replaced entry is a no-op. It does not transfer native retirement authority
to the caller. This prevents an old wrapper from retiring another resource
after Vulkan reuses a handle.

Zero-reference entries remain cached. Backing-image retirement owns their
removal and native retirement through the existing lifetime ledger. Texture-view
wrappers still own their samplers. Cached descriptor and attachment checks use
the acquired view generation as well as the backing-image identity.

## Owned Image Views

`VkImageBackedTexture` retains each owned view as a
`VulkanOwnedImageView`, which stores the native handle and its creation
generation. This applies to primary views, attachment views, and views held in
the physical-image cache. Imported texture upload preparation and publication,
including retained descriptor-slot retirement, preserve the same view receipt.
View reuse checks the exact generation.

Owned-view retirement carries each expected view generation. A missing, stale,
or destroyed receipt is a no-op before retirement admission fencing. The
resource-lifetime core checks the exact identity again after publishing
dependency information. Matching pending tickets are reused, and the
retirement queue deduplicates by handle and generation.

This view receipt does not change image or sampler ownership. Lifecycle locking
also remains unchanged.

## Recording And Allocation

Persistent Vulkan image or buffer creation is rejected while the render-graph
command-recording scope is active. Persistent resources must be allocated by
planning or upload preparation before recording begins. This preserves the
allocation-free steady-state recording and submission paths; the organization
refactor adds no per-frame collections, closures, or delegates.
