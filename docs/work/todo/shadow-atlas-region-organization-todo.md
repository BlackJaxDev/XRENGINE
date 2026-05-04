# Shadow Atlas Region Organization And Solver Performance TODO

Status: proposed phased TODO

Source context:
- [Dynamic Shadow Atlas And LOD Allocation TODO](dynamic-shadow-atlas-lod-todo.md)
- [Dynamic Shadow Atlas LOD Plan](../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [Shadow Filtering VSM/EVSM Plan](../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)
- [Shadow Relevance Scoring TODO](shadow-relevance-scoring-todo.md) — upstream request scoring and resolution selection based on visible receivers and active cameras
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`

## Goal

Make shadow atlas region organization faster, more stable across frames, and easier to extend to multi-page atlases, grouped light allocations, and eventual virtual-shadow-map metadata.

The current atlas manager already has deterministic request sorting, per-kind/per-encoding atlas states, balanced resolution demotion, previous-allocation reservation, stale-tile reuse, gutters, and double-buffered published frame data. This TODO focuses on removing avoidable CPU work and layout churn in that foundation.

## Target Outcome

- `ShadowAtlasManager.SolveAllocations()` performs one request classification pass per frame, not one scan per atlas kind and encoding.
- The page allocator uses fixed power-of-two level buckets instead of `SortedDictionary<int, List<ShadowBlock>>`.
- Allocation stats are maintained incrementally and do not rescan all free blocks after each allocation.
- Previous-frame region reuse is a direct buddy-tree reservation path, not a search across all free blocks.
- Stable resident allocations survive across frames unless the request disappears, changes resolution/format/page family, or exceeds eviction policy.
- `MaxShadowAtlasPages` is honored when enabled, including memory-budget checks and tests.
- Directional cascades and point-light faces can be allocated as groups where that improves stability and packing quality.
- Published frame allocation lookup is no longer linear in hot renderer metadata upload paths.
- Allocation, sorting, packing, and frame-data publish perform no per-frame heap allocations after warmup.
- Previous-frame allocation tracking reuses persistent storage across frames instead of allocating a fresh dictionary each solve.

## Non-Goals

- Do not change receiver filtering behavior, bias math, or atlas UV sampling in the allocator cleanup phases.
- Do not add new shadow encodings as part of this work.
- Do not require Vulkan parity before the OpenGL path is optimized, but avoid adding OpenGL-only policy to renderer-neutral atlas contracts.
- Do not remove the legacy non-atlas shadow path in this TODO.
- Do not switch to virtual shadow maps here; keep reserved metadata fields and naming compatible with that later direction.

## Behavioral Invariants

- Existing directional and spot atlas receiver output should remain visually equivalent for unchanged tile layouts.
- A tile that reuses the same page, rect, resolution, atlas id, and content version must keep its `LastRenderedFrame` and avoid a fresh render when the request is clean.
- Dirty stale-tile requests may publish `SkipReason.StaleTileReused` until the tile is rendered again.
- Atlas allocations must remain deterministic for identical request sets and settings.
- Resident regions in the same atlas id and page must never overlap, including grouped cascade and point-face allocations.
- Tile gutters remain part of allocation rects; receiver sampling uses inner rects.
- `ShadowAtlasFrameData.Generation` increments only when the published resident layout changes.
- Dedicated server/headless paths must remain no-op or fallback-only when no renderer resources are available.
- Resident allocations never migrate page or rect within a single frame. Cross-frame migration is allowed only via the explicit compaction/repack path introduced in Phase 5.

### Threading invariants

- `SubmitRequest` is the only public entry point that may be called from non-solve threads, and it remains lock-protected by `_submitSync`.
- `BeginFrame`, `SolveAllocations`, `MarkTileRendered`, and `Publish` run on a single thread (the render scheduler).
- `PublishedFrameData` is read by the render thread via the existing double-buffer swap; persistent state added in Phases 5 and 8 must not be mutated after publish until the next solve begins.

## Phase 0: Branch, Baseline, And Acceptance Tests

Goal: isolate the work and capture the current behavior before changing allocator internals.

- [ ] Create a dedicated branch for this TODO, for example `shadow-atlas-region-organization`.
- [ ] Record the current public settings behavior:
  - [ ] `ShadowAtlasPageSize`
  - [ ] `MaxShadowAtlasPages`
  - [ ] `MaxShadowAtlasMemoryBytes`
  - [ ] `MinShadowAtlasTileResolution`
  - [ ] `MaxShadowAtlasTileResolution`
  - [ ] `MaxShadowTilesRenderedPerFrame`
- [ ] Run and preserve targeted baseline results:
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- [ ] Add or extend tests that lock down:
  - [ ] deterministic layouts across identical frames
  - [ ] no resident region overlap
  - [ ] previous allocation reuse
  - [ ] stale tile reuse
  - [ ] balanced demotion under a one-page cap
  - [ ] allocation failure fallback when minimum resolutions cannot fit
- [ ] Add a lightweight solver stress test or benchmark harness that exercises many mixed spot, point-face, and directional requests without rendering GPU tiles.
- [ ] Capture baseline solve metrics for:
  - [ ] many small tiles
  - [ ] mixed 128/256/512/1024 tiles
  - [ ] four-cascade directional sets
  - [ ] point lights with six face requests
  - [ ] atlas-full demotion retries
- [ ] Capture a baseline GC-allocation budget per solve using `GC.GetAllocatedBytesForCurrentThread()` deltas around `SolveAllocations`. This is the concrete pass/fail metric for the "no per-frame heap allocations after warmup" goal in Phases 1, 2, and 8.
- [ ] Record `_previousAllocations` dictionary churn (current implementation rebuilds it every frame) as a separate baseline so Phase 1's persistent-storage fix has a measurable target.
- [ ] Add a threading-contract acceptance test that exercises concurrent `SubmitRequest` calls during a simulated solve to lock down the lock/double-buffer invariants documented above.

## Phase 1: Single-Pass Request Bucketing

Goal: reduce repeated request scanning while preserving request priority and published behavior.

- [ ] Replace the per-state full `_requests` scan with a single classification pass into fixed buckets indexed by atlas kind and encoding.
- [ ] Sort per bucket rather than globally. Per-bucket `RequestComparer` ordering has smaller N and better cache locality, and allocation already runs per state. Fall back to a single global sort only if a benchmark shows it is cheaper for representative request mixes.
- [ ] Preserve disabled-light and skipped-request publication semantics.
- [ ] Ensure `_frameAllocations` and `_currentAllocationIndices` remain coherent for later `MarkTileRendered` updates.
- [ ] Pre-size bucket storage from `MaxRequestsPerFrame` and reuse it each frame.
- [ ] Replace per-frame `_previousAllocations` dictionary swap with two persistent dictionaries used in alternating roles (or fold previous-allocation state into the Phase 5 persistent resident table and remove `_previousAllocations` entirely). Either way, no fresh dictionary is allocated per solve.
- [ ] Add tests for mixed directional, point, spot, and encoding requests to verify allocations still land in the correct atlas id.
- [ ] Validate no new per-frame allocations after warmup in the solve path (compare against the Phase 0 GC baseline).

## Phase 2: Fixed-Level Buddy Allocator

Goal: replace dictionary-backed free block management with power-of-two level buckets.

- [ ] Replace `SortedDictionary<int, List<ShadowBlock>>` with fixed buckets keyed by block level or size.
- [ ] Precompute page-level metadata from `PageSize`:
  - [ ] maximum level count
  - [ ] level-to-size table
  - [ ] size-to-level mapping for normalized tile sizes
  - [ ] page texel area per level
- [ ] Track non-empty levels with a compact bitmask or level count array so finding the smallest suitable block is fast.
- [ ] Preserve deterministic bottom-left allocation order within each level.
- [ ] Avoid heap allocations during `Reset`, `TryAllocate`, `TryReserve`, and `SplitToSize` after warmup.
- [ ] Tighten `NormalizeSettings` allocator contract:
  - [ ] `PageSize`, `MinTileResolution`, and `MaxTileResolution` must be powers of two and aligned to a buddy level.
  - [ ] Round non-power-of-two values to the nearest valid level (or reject with a clear log) instead of silently producing an inconsistent allocator.
- [ ] Add allocator-only tests for:
  - [ ] exact-size allocation
  - [ ] splitting larger blocks
  - [ ] filling a page
  - [ ] exhaustion
  - [ ] deterministic placement
  - [ ] no overlap across randomized request sizes
  - [ ] non-power-of-two `PageSize`/`MinTileResolution`/`MaxTileResolution` settings normalize to valid levels

## Phase 3: Incremental Free Stats

Goal: remove repeated scans from allocator bookkeeping.

- [ ] Update `FreeTexelCount` incrementally when blocks are added or removed.
- [ ] Update `LargestFreeBlockSize` from level occupancy instead of scanning all free lists.
- [ ] Ensure `Reset` initializes stats directly from one full-page free block.
- [ ] Ensure `AppendPageDescriptors` reads current stats without forcing recalculation.
- [ ] Add tests that compare incremental stats against an explicit slow validation pass in debug/test-only code.
- [ ] Verify metrics still populate:
  - [ ] `LargestFreeRect`
  - [ ] `FreeTexelCount`
  - [ ] `ResidentBytes`
  - [ ] `PageCount`

## Phase 4: Direct Previous-Layout Reservation

Goal: make stable-layout preservation cheap and predictable.

- [ ] Replace `TryReserve` free-list scanning with direct buddy path reservation for known page, x, y, and size.
- [ ] Validate requested regions are power-of-two sized, aligned, and contained in the page before attempting reservation.
- [ ] Split from the root or from the nearest available ancestor block to reserve the exact prior region.
- [ ] Return false instead of throwing when the prior region is no longer available because another higher-priority allocation already claimed it.
- [ ] Keep the existing fail-fast invariant for allocator internal inconsistency in debug builds.
- [ ] Add tests where:
  - [ ] a prior tile reserves the same region
  - [ ] a lower-priority prior tile loses its region to a higher-priority request
  - [ ] a resized prior tile does not reserve an incompatible region
  - [ ] stale-tile reuse survives direct reservation

## Phase 5: Persistent Resident Layout

Goal: stop rebuilding the whole atlas every frame when only a few requests change.

- [ ] Introduce a persistent resident table keyed by `ShadowRequestKey`.
- [ ] Track resident metadata separately from the frame's submitted request list:
  - [ ] atlas kind
  - [ ] encoding
  - [ ] page index
  - [ ] pixel rect
  - [ ] resolution
  - [ ] content version
  - [ ] last rendered frame
  - [ ] last requested frame
  - [ ] eviction score or TTL
- [ ] Keep stable residents allocated across frames when the request is still valid and the resolution/format family is unchanged.
- [ ] Allocate only new, resized, or migrated requests in the common path.
- [ ] Free vanished requests after a short TTL so transient camera/light visibility changes do not immediately churn the atlas.
- [ ] Define the eviction policy explicitly. Eviction score inputs:
  - [ ] frames since last requested
  - [ ] frames since last rendered
  - [ ] priority (lower priority evicts first)
  - [ ] resolution (prefer demoting before evicting when the request is still alive)
  - [ ] whether a higher-priority pending request actually needs the slot this frame
- [ ] Editor-pinned and `EditorPinned == true` requests are never evicted under normal pressure. They may only be displaced by an explicit user-driven settings change or a hard memory cap miss, and that path must log a warning.
- [ ] Add an explicit compaction/repack path that runs only when fragmentation or failed allocations justify it.
- [ ] Preserve stale-tile fallback for dirty tiles that retain their previous region.
- [ ] Add tests for:
  - [ ] stable layout with one changed request
  - [ ] eviction after TTL
  - [ ] no eviction for editor-pinned high-priority requests under normal pressure
  - [ ] compaction generation increments
  - [ ] clean tile render skipping after persistent reuse

## Phase 6: Real Multi-Page Support

Goal: honor `MaxShadowAtlasPages` where renderer metadata and memory budgets allow it.

- [ ] Remove the hard one-page cap. This is a single change with two call sites:
  - [ ] `NormalizeSettings` no longer forces `MaxPages = 1`.
  - [ ] `GetPageLimit(ShadowAtlasManagerSettings)` returns the real configured limit.
- [ ] Preserve the ability to force one-page behavior for tests and low-memory configurations via an explicit settings opt-in.
- [ ] Ensure `MaxMemoryBytes` is checked against all atlas kind/encoding pages.
- [ ] Decide page creation policy:
  - [ ] fill existing pages before creating a new page
  - [ ] optionally prefer pages by atlas kind, encoding, or fragmentation score
  - [ ] avoid creating a new page when demoting a low-priority request is preferable
- [ ] Audit every receiver/shader path for hardcoded `pageIndex == 0` assumptions and update each one. Known call sites to verify:
  - [ ] `Lights3DCollection.Shadows.cs` metadata upload (directional, spot, point)
  - [ ] Atlas binding/sampling for spot and directional receivers
  - [ ] Point-light atlas receiver path when point rendering is enabled
  - [ ] Per-tile uniform writers (page index, atlas id, UV scale/offset)
  - [ ] Any debug visualization that draws atlas contents
- [ ] Add tests for:
  - [ ] two-page success without demotion
  - [ ] one-page forced demotion
  - [ ] memory cap blocking a new page
  - [ ] page descriptors for multiple atlas pages
  - [ ] layout generation when a tile migrates between pages
- [ ] Update any source-contract tests that intentionally assert one-page behavior.

## Phase 7: Grouped Light Allocation Strategies

Goal: keep related tiles coherent and reduce fragmentation for common shadow layouts.

- [ ] Add a grouped allocation request path for directional cascades from the same light and encoding.
- [ ] Prefer a deterministic 2x2 cascade pack when four cascades share a resolution.
- [ ] Support partial cascade groups when only primary or fewer cascades are submitted.
- [ ] Cascade group atomicity: the grouped 2x2 pack is best-effort. If it cannot fit, fall back to ungrouped per-cascade allocation rather than failing the whole light. Document this fallback in tests.
- [ ] Add a grouped allocation path for point-light faces from the same light and encoding.
- [ ] Point face group atomicity: partial residency is the contract. Individual faces may be allocated, demoted, skipped, or evicted independently when budgets require it.
- [ ] Evaluate whether grouped point faces should prefer a 3x2 or 2x3 layout at common resolutions.
- [ ] Preserve per-face priority, dirty state, fallback, and `LastRenderedFrame`.
- [ ] Add tests for:
  - [ ] four-cascade 2x2 packing
  - [ ] mixed cascade resolutions
  - [ ] six point-face allocation
  - [ ] partial point-face residency under budget pressure
  - [ ] no overlap between grouped and ungrouped allocations

## Phase 8: Published Frame Lookup Index

Goal: remove linear allocation lookup from renderer metadata upload paths.

- [ ] Add a no-allocation-after-warmup lookup index to `ShadowAtlasFrameData`.
- [ ] Choose the lookup representation:
  - [ ] reused `Dictionary<ShadowRequestKey, int>` for fastest general lookup
  - [ ] sorted key/index arrays for compact publish-time data and binary search
  - [ ] hybrid if renderer upload needs both ordered iteration and keyed lookup
- [ ] Duplicate-key policy: deterministic last-write-wins in release builds, with a debug-only assert. Both representations (dictionary and sorted-array) must implement the same policy so behavior is consistent if the representation is changed later.
- [ ] Update `TryGetAllocation` and `TryGetAllocationIndex` to use the index.
- [ ] Ensure `SetData` clears stale index entries when allocation count shrinks.
- [ ] Preserve read-only span iteration for renderer upload code.
- [ ] Add tests that verify lookups after:
  - [ ] same-size publish
  - [ ] grow publish
  - [ ] shrink publish
  - [ ] skipped allocation publication
  - [ ] duplicate-key last-write-wins behavior (and debug-build assert)

## Phase 9: Diagnostics And Editor Visibility

Goal: make the optimized allocator observable enough to tune.

- [ ] Extend `ShadowAtlasMetrics` or diagnostics with allocator pressure counters:
  - [ ] reserve hits
  - [ ] reserve misses
  - [ ] new allocations
  - [ ] reused persistent allocations
  - [ ] demotions
  - [ ] repacks
  - [ ] evictions
  - [ ] page creations
  - [ ] failed allocations by reason
- [ ] Add debug logging for repack/eviction events with rate limiting.
- [ ] Surface page count, largest free block, and free texels in existing shadow atlas diagnostics.
- [ ] Avoid per-frame string construction in hot paths unless logging is enabled and rate-limited.
- [ ] Counter storage uses plain `int`/`long`/`uint` fields. No boxing, no per-frame `Dictionary<string, int>`-style updates, no LINQ aggregation.
- [ ] Update docs if any new settings, task entries, launch flags, or editor-visible diagnostics are added.

## Phase 10: Validation And Closeout

Goal: prove the allocator is faster and behaviorally equivalent where expected.

- [ ] Run targeted tests:
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
  - [ ] any new allocator-specific test fixture
- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Run a smoke validation of `Editor (Unit Testing World)` with a shadow-heavy toggle set so the multi-page and grouped-allocation paths get real renderer exercise (unit tests alone do not cover this).
- [ ] Validate an editor unit-testing-world scene with:
  - [ ] directional cascades
  - [ ] many spot lights
  - [ ] point-light atlas path when point rendering is enabled
  - [ ] stale-tile fallback
  - [ ] one-page and multi-page settings
- [ ] Compare baseline and final solve metrics for the stress cases recorded in Phase 0.
- [ ] Confirm hot-path allocation expectations:
  - [ ] no per-frame allocator data-structure allocations after warmup
  - [ ] no LINQ or captured closures in solve/allocation hot paths
  - [ ] no avoidable string formatting in per-frame logs
- [ ] Update related docs and TODOs:
  - [ ] `dynamic-shadow-atlas-lod-todo.md`
  - [ ] `docs/work/README.md`
  - [ ] rendering architecture notes if public behavior or settings changed
- [ ] Split any deferred virtual-shadow-map, Vulkan, or shader metadata follow-ups into focused TODO docs.

## Final Task

- [ ] Merge the dedicated `shadow-atlas-region-organization` branch back into `main` after all phases are complete, validation has passed, docs are updated, and any follow-up TODOs have been filed.
