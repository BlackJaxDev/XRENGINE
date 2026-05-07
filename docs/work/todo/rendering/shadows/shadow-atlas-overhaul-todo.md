# Shadow Atlas Major Overhaul TODO

Status: proposed phased TODO. Supersedes and merges:
- `shadow-atlas-region-organization-todo.md` (allocator and packing performance)
- `shadow-relevance-scoring-todo.md` (score-driven request resolution from camera visibility)
- `point-light-atlas-stability-todo.md` (per-face stability under contention)

Source files (primary edit surface):
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/RenderTree/` (visibility/culling reuse)
- `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`

Related docs:
- [Dynamic Shadow Atlas And LOD Allocation TODO](dynamic-shadow-atlas-lod-todo.md)
- [Local Shadow Frustum Culling TODO](local-shadow-frustum-culling-todo.md)
- [Dynamic Shadow Atlas LOD Plan](../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [Shadow Filtering VSM/EVSM Plan](../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)

## Problem Statement

Three independent gaps add up to one user-visible failure mode (flicker and wasted shadow budget):

1. **Allocator is slow and frame-coupled.** `SolveAllocations` rescans `_requests` per atlas state, the page allocator uses `SortedDictionary<int, List<ShadowBlock>>`, free-block stats are recomputed by scanning, prior-region reuse goes through generic free-list searches, and `_previousAllocations` is a fresh dictionary every frame. The whole atlas is reasoned about as if every frame were the first.
2. **Inputs are still too shallow.** The tactical local shadow-frustum culling path can skip spot requests and point faces with `SkipReason.NotRelevant`, but the broader allocator still needs receiver-aware scoring, sticky demotion, and unified relevance across directional cascades, spots, and point faces. Off-screen lights and far cascades can still compete for atlas space against on-screen lights when the tactical frustum test is not enough.
3. **Allocation under contention is unstable.** When the atlas is full, `TryReduceLargestBalancedAllocation` halves the *largest, lowest-priority, first-encountered* entry per retry, and `TryBuildBalancedAllocations` resets every buddy allocator at the top of every retry iteration. The chosen victim and the resulting placement both rotate frame-to-frame. For point lights specifically, `TryAllocateCandidate` only reuses a prior slot when `prior.Resolution == candidate`, so any forced downsize relinquishes the slot, the buddy allocator places the smaller tile elsewhere, and the 6-frame `LodChangeCooldownFrames` becomes an oscillation period under sustained pressure.

Result: shadow flicker on contended lights (especially point lights), per-face slot churn even at constant resolution, and shadow texel budget spent on shadows the player cannot see.

## Light-Type-Specific Concerns

The three light types have different stability and packing needs. The overhaul respects all three.

### Directional Lights

- Multiple cascades per light (typically 4) with strongly correlated content; share a per-light view direction.
- Cascades naturally pack into a 2×2 grid at equal resolution and benefit from grouped allocation for layered-render efficiency.
- **Cascade-level relevance is independent.** A far cascade fully outside the camera frustum should demote or skip without affecting near cascades. A cascade slice AABB ∩ visible-receiver bounds is the correct signal.
- Editor-pinned directional lights are common; pin must hold even under heavy contention.
- Atlas mode is authoritative when `UseDirectionalShadowAtlas` is enabled; the allocator must reduce cascade tile resolutions until every required active cascade is resident, never falling through to legacy maps mid-frame.

### Spot Lights

- One frustum, one tile per light.
- Often many spot lights compete for the spot atlas at once (city/interior scenes).
- Spot atlas pages are depth-only today; VSM/EVSM-encoded spot lights bypass the spot atlas (standalone moment maps with mip chains). The allocator must not assume every spot request lives in the atlas.
- Stability concerns: tile-id sort order changes can swap two equal-priority spot lights between slots and induce one-frame churn even at the same resolution.
- Spot lights respond well to anchor-slot strategies because each light is one tile.

### Point Lights

- Six independent faces per light (each is its own `ShadowMapRequest` with `ProjectionType == PointFace`).
- **Per-face heterogeneous resolution is desirable, not a bug.** A face the camera does not see can render at 1/4 or 1/16 of the user-facing face's size, freeing texels for what the player perceives. The overhaul preserves and exploits this.
- Cube faces of one light should still co-locate on a single page when possible to keep the layered/`gl_ViewportIndex` grouped render path productive. The grouped reservation is a heterogeneous mosaic, not a uniform 3×2 of equal tiles.
- The flicker root cause for point lights is the *demotion target* changing across frames (which face goes to lower res) plus *slot churn* on resolution changes. Both must be stabilized without forcing atomic per-light resolution.
- PCF/PCSS taps cross face seams; per-face metadata already carries individual `UvScaleBias` and `InnerPixelRect`. Heterogeneous face sizes within one light are sampling-safe today.
- The legacy non-atlas point cubemap path remains available when point atlas mode is disabled; this overhaul does not remove it.

## Goals

Allocator and frame data:
- `ShadowAtlasManager.SolveAllocations()` performs one classification pass per frame, not one scan per atlas kind and encoding.
- Page allocator uses fixed power-of-two level buckets, not `SortedDictionary`.
- Allocation stats are maintained incrementally.
- Previous-frame region reuse is a direct buddy-tree reservation, not a free-list scan.
- Stable resident allocations survive across frames unless invalidated.
- `MaxShadowAtlasPages` is honored where memory and metadata allow.
- Published frame allocation lookup is O(1)/O(log n) keyed access for renderer upload paths.
- Persistent storage replaces per-frame dictionary churn.
- No per-frame heap allocations on the steady-state hot path inside `SolveAllocations` after warmup.

Relevance and budget:
- A `ShadowRelevanceScore` per light (per cascade for directional, per face for point) is computed once per frame from the active camera set.
- `ShadowMapRequest.RequestedResolution` is derived from the relevance score, clamped to the configured ladder.
- Lights/cascades/faces with no visible receivers submit at minimum resolution or skip via `SkipReason.NotRelevant`.
- Stereo VR uses the union of both eyes plus mirror/probe cameras.
- Off-screen casters that shadow on-screen receivers remain fully relevant.

Stability:
- The selected demotion victim is determined by relevance, not request sort order, and is sticky across frames under hysteresis.
- A light/cascade/face that fits last frame retains its slot this frame whenever the desired layout still fits, regardless of submission order.
- A resolution change does not relocate the slot unless the prior slot cannot host the new size.
- Asymmetric hysteresis: forced downsizes have a long re-promotion cooldown; voluntary changes have a short cooldown; in-place upgrades that the buddy can satisfy "for free" bypass the cooldown.

## Non-Goals

- Do not remove the legacy non-atlas shadow path.
- Do not change receiver-side filtering, sampling, bias math, or PCF/PCSS seam handling.
- Do not introduce new shadow encodings.
- Do not switch to virtual shadow maps; keep metadata names and reserved fields compatible with that later direction.
- Do not require Vulkan parity before the OpenGL path is optimized, but avoid OpenGL-only policy in renderer-neutral atlas contracts.
- Do not force atomic per-light resolution. Heterogeneous per-face point sizes are intentional.
- Do not introduce a new full visibility pass; reuse forward+/visibility data already produced.

## Behavioral Invariants

- Existing directional, spot, and point atlas receiver output remains visually equivalent for unchanged tile layouts.
- A tile that reuses the same atlas id, page, rect, resolution, and content version keeps its `LastRenderedFrame` and avoids a fresh render when the request is clean.
- Dirty stale-tile requests may publish `SkipReason.StaleTileReused` until rerendered.
- Atlas allocations are deterministic for identical request sets and settings.
- Resident regions in the same atlas id and page never overlap, including grouped cascade and point-face allocations.
- Tile gutters remain part of allocation rects; receivers sample inner rects.
- `ShadowAtlasFrameData.Generation` increments only when the published resident layout changes.
- Relevance is computed against **receivers in view**, never against caster on-screen presence; off-screen casters that shadow on-screen receivers stay fully relevant.
- `EditorPinned` lights are not demoted or skipped by relevance under normal pressure. They may only be displaced by an explicit user settings change or hard memory cap miss, with a logged warning.
- Six `PointFace` requests for one light continue to share a single page when grouped rendering applies; cube faces may have different `Resolution` values, including in the same group.
- Resident allocations never migrate page or rect within a single frame. Cross-frame migration is allowed only via the explicit compaction/repack path.
- Headless/dedicated-server paths short-circuit relevance and resolve to no-op or fallback-only when no renderer resources are available.

## Threading Invariants

- `SubmitRequest` is the only public entry point that may be called from non-solve threads, and it remains lock-protected by `_submitSync`.
- `BeginFrame`, `SolveAllocations`, `MarkTileRendered`, and `Publish` run on the render scheduler thread.
- `PublishedFrameData` is read by the render thread via the existing double-buffer swap; persistent state added by this overhaul must not be mutated after publish until the next solve begins.

---

## Phase 0 — Branch, Baseline, Acceptance Tests

Goal: isolate the work and capture current behavior across all three concerns before changing anything.

- [ ] Create dedicated branch: `shadow-atlas-overhaul`.
- [ ] Record current public settings behavior:
  - [ ] `ShadowAtlasPageSize`
  - [ ] `MaxShadowAtlasPages`
  - [ ] `MaxShadowAtlasMemoryBytes`
  - [ ] `MinShadowAtlasTileResolution`
  - [ ] `MaxShadowAtlasTileResolution`
  - [ ] `MaxShadowTilesRenderedPerFrame`
  - [ ] `LodChangeCooldownFrames` (will be split in Phase 8)
- [ ] Run and preserve targeted baselines:
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- [ ] Add or extend tests that lock down current behavior so regressions are visible:
  - [ ] deterministic layouts across identical frames
  - [ ] no resident region overlap
  - [ ] previous allocation reuse
  - [ ] stale tile reuse
  - [ ] balanced demotion under a one-page cap
  - [ ] allocation failure fallback when minimum resolutions cannot fit
  - [ ] all eligible lights produce shadow requests today
  - [ ] pre-local-frustum-culling baseline: no skip path used a `NotRelevant` reason
  - [ ] cascades are submitted as a uniform set per directional light today
- [ ] Add a lightweight solver stress test/benchmark harness exercising mixed spot, point-face, and directional requests with no GPU work.
- [ ] Add a relevance-pass benchmark harness with N lights, M visible receivers, and a configurable camera set, no GPU work.
- [ ] Add a `PointShadowAtlasStabilityTests` fixture in `XREngine.UnitTests/Rendering/` that builds a contended scenario (≥6 point lights, single page) and asserts face slot identity is stable across 60 frames once the layout has settled. The test will initially fail; failing-state output goes into the test log as the baseline.
- [ ] Capture baseline solve metrics for: many small tiles, mixed 128/256/512/1024 tiles, four-cascade directional sets, point lights with six face requests, atlas-full demotion retries.
- [ ] Capture baseline GC-allocation budget per solve via `GC.GetAllocatedBytesForCurrentThread()` deltas around `SolveAllocations` and around the relevance pass once it exists.
- [ ] Record `_previousAllocations` dictionary churn (current code rebuilds every frame) as a separate baseline so Phase 2's persistent-storage fix has a measurable target.
- [ ] Capture baseline scene-level metrics: submitted requests/frame, sum of requested texels/frame, off-screen lights at non-minimum resolution, cascades fully off-screen at full resolution, shadow render cost.
- [ ] Add a profiler block `ShadowAtlas.PointFaceDemotion` recording per point light: requested resolutions, granted resolutions, page index, pixel rect for each face.
- [ ] Add a debug `XREngine.Debug.LightingEvery` line tagged `ShadowAtlas.PointFace.SlotChurn` when a face's `(PageIndex, X, Y)` changes between frames at the same `Resolution`. Track count per light per frame.
- [ ] Add a threading-contract acceptance test that exercises concurrent `SubmitRequest` calls during a simulated solve.

---

## Phase 1 — Allocator Core Performance

Owner module: `ShadowAtlasManager.SolveAllocations`, `ShadowAtlasEncodingState`, `ShadowBuddyPageAllocator`.

Combines the four allocator-internals goals from the region-organization TODO into one phase since they share the same code path.

### 1A — Single-Pass Request Bucketing

- [ ] Replace the per-state full `_requests` scan with a single classification pass into fixed buckets indexed by atlas kind and encoding.
- [ ] Sort per bucket rather than globally (smaller N, better cache locality). Fall back to a single global sort only if a benchmark shows it cheaper for representative request mixes.
- [ ] Preserve disabled-light and skipped-request publication semantics.
- [ ] Keep `_frameAllocations` and `_currentAllocationIndices` coherent for `MarkTileRendered`.
- [ ] Pre-size bucket storage from `MaxRequestsPerFrame`; reuse each frame.
- [ ] Add tests for mixed directional/point/spot/encoding requests verifying allocations land in the correct atlas id.

### 1B — Fixed-Level Buddy Allocator

- [ ] Replace `SortedDictionary<int, List<ShadowBlock>>` with fixed buckets keyed by block level.
- [ ] Precompute page-level metadata from `PageSize`: maximum level count, level-to-size table, size-to-level mapping for normalized tile sizes, page texel area per level.
- [ ] Track non-empty levels with a compact bitmask or level count array.
- [ ] Preserve deterministic bottom-left allocation order within each level.
- [ ] No heap allocations during `Reset`, `TryAllocate`, `TryReserve`, or `SplitToSize` after warmup.
- [ ] Tighten `NormalizeSettings`: `PageSize`, `MinTileResolution`, `MaxTileResolution` must be powers of two and aligned to a buddy level. Round non-power-of-two values to the nearest valid level (or reject with a clear log).
- [ ] Allocator-only tests: exact-size allocation, splitting larger blocks, filling a page, exhaustion, deterministic placement, no overlap across randomized request sizes, non-power-of-two settings normalize correctly.

### 1C — Incremental Free Stats

- [ ] Update `FreeTexelCount` incrementally on add/remove.
- [ ] Update `LargestFreeBlockSize` from level occupancy, not by scanning free lists.
- [ ] `Reset` initializes stats directly from one full-page free block.
- [ ] `AppendPageDescriptors` reads current stats without forcing recalculation.
- [ ] Compare incremental stats against an explicit slow validation pass in debug-only code.
- [ ] Verify metrics still populate: `LargestFreeRect`, `FreeTexelCount`, `ResidentBytes`, `PageCount`.

### 1D — Direct Previous-Layout Reservation

- [ ] Replace `TryReserve` free-list scanning with direct buddy-path reservation for known page, x, y, size.
- [ ] Validate requested regions are power-of-two sized, aligned, and contained in the page before attempting reservation.
- [ ] Split from the root or nearest available ancestor to reserve the exact prior region.
- [ ] Return false (not throw) when a higher-priority allocation already claimed the prior region.
- [ ] Keep fail-fast invariant for allocator inconsistency in debug builds.
- [ ] Tests: prior tile reserves the same region, lower-priority prior loses to higher-priority, resized prior does not reserve an incompatible region, stale-tile reuse survives direct reservation.

### 1E — Validation

- [ ] No new per-frame allocations after warmup in the solve path (compare against Phase 0 GC baseline).

---

## Phase 2 — Persistent Resident Layout

Goal: stop rebuilding the atlas every frame when only a few requests change.

- [ ] Introduce a persistent resident table keyed by `ShadowRequestKey`.
- [ ] Track resident metadata separately from the frame's submitted request list:
  - [ ] atlas kind, encoding, page index, pixel rect, resolution, content version
  - [ ] last rendered frame, last requested frame
  - [ ] eviction score or TTL
  - [ ] LOD transition reason and frame (Phase 8 input)
  - [ ] prior placement key tuple `(PageIndex, X, Y)` for sort tiebreak (Phase 8 input)
- [ ] Replace per-frame `_previousAllocations` dictionary swap with this persistent table or with two persistent dictionaries used in alternating roles. Either way, no fresh dictionary is allocated per solve.
- [ ] Keep stable residents allocated across frames when the request is still valid and the resolution/format family is unchanged.
- [ ] Allocate only new, resized, or migrated requests in the common path.
- [ ] Free vanished requests after a short TTL so transient visibility changes do not churn the atlas.
- [ ] Eviction policy inputs: frames since last requested, frames since last rendered, priority, resolution (prefer demote-then-evict for live requests), whether a higher-priority pending request actually needs the slot this frame.
- [ ] Editor-pinned requests are never evicted under normal pressure; only displaceable by an explicit settings change or hard memory cap miss, with a logged warning.
- [ ] Add an explicit compaction/repack path that runs only when fragmentation or failed allocations justify it.
- [ ] Preserve stale-tile fallback for dirty tiles retaining their previous region.
- [ ] Tests: stable layout with one changed request, eviction after TTL, no eviction for editor-pinned high-priority, compaction generation increments, clean tile render skipping after persistent reuse.
- [ ] Guarantee the table is pre-sized in `Configure` and cleared (not reallocated) in `ResetResources`.

---

## Phase 3 — Multi-Page Support

Goal: honor `MaxShadowAtlasPages` where renderer metadata and memory budgets allow.

- [ ] Remove the hard one-page cap:
  - [ ] `NormalizeSettings` no longer forces `MaxPages = 1`.
  - [ ] `GetPageLimit(ShadowAtlasManagerSettings)` returns the real configured limit.
- [ ] Preserve a forced one-page mode for tests and low-memory configurations via explicit settings opt-in.
- [ ] Check `MaxMemoryBytes` against all atlas kind/encoding pages.
- [ ] Page creation policy: fill existing pages before creating a new page; optionally prefer pages by atlas kind, encoding, or fragmentation score; avoid creating a new page when demoting a low-priority request is preferable.
- [ ] Audit every receiver/shader path for hardcoded `pageIndex == 0`:
  - [ ] `Lights3DCollection.Shadows.cs` metadata upload (directional, spot, point)
  - [ ] Atlas binding/sampling for spot and directional receivers
  - [ ] Point-light atlas receiver path when point rendering is enabled
  - [ ] Per-tile uniform writers (page index, atlas id, UV scale/offset)
  - [ ] Any debug visualization that draws atlas contents
- [ ] Tests: two-page success without demotion, one-page forced demotion, memory cap blocking a new page, page descriptors for multiple pages, layout generation when a tile migrates between pages.
- [ ] Update any source-contract tests that intentionally assert one-page behavior.

---

## Phase 4 — Published Frame Lookup Index

Goal: remove linear allocation lookup from renderer metadata upload paths.

- [ ] Add a no-allocation-after-warmup lookup index to `ShadowAtlasFrameData`.
- [ ] Choose representation: reused `Dictionary<ShadowRequestKey, int>` for fastest general lookup, sorted key/index arrays for compact data + binary search, or a hybrid if upload needs both ordered iteration and keyed lookup.
- [ ] Duplicate-key policy: deterministic last-write-wins in release, debug-only assert. Both representations implement identical policy.
- [ ] Update `TryGetAllocation` and `TryGetAllocationIndex` to use the index.
- [ ] Ensure `SetData` clears stale index entries when allocation count shrinks.
- [ ] Preserve read-only span iteration for renderer upload code.
- [ ] Tests: lookup after same-size publish, grow publish, shrink publish, skipped allocation publication, duplicate-key last-write-wins (with debug assert).

---

## Phase 5 — Camera Relevance Set

Goal: define the authoritative set of cameras whose visibility drives relevance.

- [ ] Introduce `ShadowRelevanceCameraSet`, built once per frame, reused.
- [ ] Inputs: desktop main camera(s), both VR eye cameras when XR is active, active mirror/preview cameras flagged as relevance-contributing, optional reflection/probe cameras flagged as relevance-contributing.
- [ ] Each camera contributes: world-space frustum, near/far range, viewport pixel size, importance weight (main = 1.0, mirrors/probes = configurable, default lower).
- [ ] Provide a fast, allocation-free union-frustum query (point/sphere/AABB).
- [ ] Wire `ShadowAtlasManager.BeginFrame(IRuntimeRenderWorld, ReadOnlySpan<XRCamera>)` to construct or accept the relevance camera set.
- [ ] The fallback `BeginFrame(ulong, int)` overload still produces safe (uniform) demotion using current heuristics for headless/diagnostic paths.
- [ ] Tests: desktop-only set, VR stereo set (both eyes contribute), VR + mirror set, empty set (headless) short-circuits the pass.

---

## Phase 6 — Receiver-In-View And Caster Coupling

Goal: per-light/per-cascade/per-face evidence that shadows would actually affect on-screen pixels.

### 6A — Receiver-In-View Aggregation

- [ ] Reuse the existing forward+/visibility pass that already tags renderables with affecting lights.
- [ ] Build a per-light bitset: "is at least one receiver of this light visible to any camera in the relevance set?"
- [ ] Per light, accumulate a screen-space AABB from the union of visible receivers' projected bounds across the camera set, weighted by camera importance.
- [ ] Cost guardrail: bound per-light receiver iteration by an existing list. If unbounded for a given light type, gate aggregation behind a max-receivers-per-light setting and log when truncated.
- [ ] Tests: receiver fully off-screen → light not in-view; receiver partially on-screen → screen-space AABB clamped correctly; stereo VR with receiver visible only to one eye still flags the light as in-view; truncated receiver list still produces a valid conservative AABB.

### 6B — Caster-To-Visible-Receiver Coupling

- [ ] For each in-view light, run a cheap caster/receiver coupling test:
  - [ ] **directional**: cascade slice AABB ∩ visible-receiver bounds (per cascade)
  - [ ] **spot**: spot frustum ∩ visible-receiver bounds
  - [ ] **point**: light sphere ∩ visible-receiver bounds, plus per-face frustum intersection for each cube face
- [ ] Output per light (and per cascade / per face): `HasVisibleReceiver`, `VisibleReceiverScreenAreaPixels` (deterministic sum or AABB area).
- [ ] **Do not** filter by caster on-screen visibility. Off-screen casters that shadow on-screen receivers stay relevant. Add an explicit regression test.
- [ ] Tests: caster off-screen + receiver on-screen → relevant; caster on-screen + receiver off-screen → not relevant; partial cascade coverage demotes only off-screen cascades.

---

## Phase 7 — Relevance Score And Resolution Mapping

Goal: convert per-light/per-cascade/per-face signals into a `RequestedResolution`.

- [ ] Define `ShadowRelevanceScore` inputs:
  - [ ] receiver pixel area (Phase 6A)
  - [ ] caster-receiver coupling area (Phase 6B)
  - [ ] light intensity / contribution estimate
  - [ ] light priority and `EditorPinned`
  - [ ] distance / angular size to nearest relevance camera
  - [ ] caster motion / dirtiness (static + static = candidate for cached reuse)
- [ ] Per light type, additional inputs:
  - [ ] **directional**: cascade index distance from camera, cascade slice volume covered by visible receivers
  - [ ] **spot**: spot cone tightness, projected on-screen tile area estimate
  - [ ] **point**: per-face camera-axis dot (`saturate(dot(faceAxis, normalize(cameraPos - lightPos)))`), weighted by per-face screen-space radius
- [ ] Compute a scalar score with documented weights stored in settings with safe defaults.
- [ ] Map score → tile resolution via the existing `MinTileResolution`/`MaxTileResolution` ladder.
- [ ] Editor-pinned lights bypass demotion; their score sets a floor, not a ceiling.
- [ ] Score → 0 (no visible receiver):
  - [ ] **directional cascade**: skip with `SkipReason.NotRelevant` per cascade.
  - [ ] **spot**: submit at minimum resolution if the light is still required for fallback; otherwise skip submission.
  - [ ] **point face**: skip with `SkipReason.NotRelevant` per face; do not submit a missing-face placeholder unless an existing receiver contract requires it (document the chosen path).
- [ ] Tests: deterministic score → resolution mapping; pinned light keeps its floor under low score; zero-score produces `SkipReason.NotRelevant`; equal scores produce equal resolutions for equivalent requests.

---

## Phase 8 — Stability Under Contention

Owner module: `ShadowAtlasManager.SolveAllocationsForState`, `TryReduceLargestBalancedAllocation`, `TryAllocateCandidate`, `TryBuildBalancedAllocations`, `RequestComparer`, `ApplyLodHysteresis`, and `_lodStates` (or its persistent-table replacement from Phase 2).

This phase contains the changes that directly fix the flicker problem.

### 8A — Relevance-Driven, Stable Demotion Target

- [ ] Extend `BalancedAllocationEntry` with `RelevanceScore` (float) and `DemotionStickiness` (small int, frame counter).
- [ ] Replace the largest/lowest-priority pick in `TryReduceLargestBalancedAllocation` with: among entries where `Resolution > MinimumResolution`, pick the entry with the **lowest** `RelevanceScore`. Tie-break by lowest `Priority`, then deterministic `Key` ordering.
- [ ] Add demotion hysteresis: once a face/cascade/spot is demoted this frame, store `(LightId, FaceOrCascadeIndex, demotedFrame)` in a persistent dictionary `_demotionState`. On the next solve, bias the relevance comparison so a previously-demoted entry stays the demotion target unless another entry's relevance is lower than it by a configurable margin (e.g. 25%) for at least `DemotionPromotionCooldownFrames` (default 12).
- [ ] When a previously-demoted entry is not chosen this frame, only clear persistent state after the cooldown expires; do not clear on a single frame's reprieve.
- [ ] Bound dictionary capacity by `MaxRequestsPerFrame`; pre-size in `Configure`.
- [ ] Tests:
  - [ ] static camera + static lights → demotion target does not change across 120 frames
  - [ ] camera 180° swing eventually rotates the demotion target after ≥ cooldown frames, not within one
  - [ ] point-light specific: with three on-screen-facing faces and three back-facing, the back-facing faces are the consistent demotion victims

### 8B — Sticky Placement Under Resolution Change

- [ ] Extend the prior-slot reuse path in `TryAllocateCandidate` so that when `candidate < prior.Resolution`, the manager attempts to reserve a sub-square of the prior slot at `candidate` size (anchored at `(prior.X, prior.Y)` if buddy alignment allows; otherwise the nearest aligned sub-rect inside the prior region).
- [ ] When `candidate > prior.Resolution`, attempt an in-place upgrade: ask the buddy whether the surrounding `candidate`-aligned block containing the prior slot is fully free. If yes, reserve and reuse. If no, leave the entry at the prior resolution this frame and record `DeferredUpgrade` so promotion attempts again next frame; do not relocate.
- [ ] Add `ShadowBuddyPageAllocator.TryReserveAlignedSubBlock(int x, int y, int requestedSize)` that finds the smallest free buddy block containing `(x, y)` of at least `requestedSize` and reserves the requested size at the aligned origin.
- [ ] Carry `LastRenderedFrame` and `ContentVersion` across resolution change when the new rect is contained in the prior rect (texels remain valid; receiver samples by `InnerPixelRect` and `UvScaleBias`).
- [ ] When a face/spot/cascade shrinks in-place we still re-render to redistribute texels; mark the tile dirty but allow `SkipReason.StaleTileReused` against prior content until rerendered.
- [ ] Tests: a light forced from 1024 → 512 keeps its top-left 512 sub-rect on the same page with `LastRenderedFrame` preserved; an in-place upgrade succeeds when the surrounding block is free, otherwise stays put.
- [ ] Audit: when `_repackRequested` is true, all sticky paths are bypassed (already true in `TryAllocateCandidate`); confirm new sticky paths honor it.

### 8C — Incremental Reservation, No Full Reset On Retry

- [ ] Replace the "reset all allocators and retry from scratch" loop in `TryBuildBalancedAllocations` with incremental reservation:
  - [ ] Maintain a per-entry `Allocation` plus a *reserved* flag.
  - [ ] On placement failure for entry `i`, do not call `state.BeginFrame()`. Pick a demotion target (8A) among entries `[0, i]`. If the chosen target is `j < i`, free only `j`'s reservation, halve its `Resolution`, re-reserve `j`, then continue from `i`. If the target is `i` itself, halve `i` and retry only `i`.
  - [ ] When a previously-placed entry is freed, prefer its prior `(PageIndex, X, Y)` first via the 8B sticky path.
- [ ] Hard cap on demotion iterations per state per frame (e.g. `entryCount * 2`); on cap, fall back to the existing wholesale reset path and log `ShadowAtlas.SolverFallback`.
- [ ] Tests: contended scenario where adding a low-priority light forces one face/cascade/spot to demote leaves all other reservations bit-identical to the previous frame.
- [ ] Profiler check: median `SolveAllocations` cost does not regress; under contention it should improve.

### 8D — Sort Tiebreaker By Prior Slot Identity

- [ ] Pre-compute a per-request "prior placement key" on `Submit`/`BeginFrame` from the persistent resident table (Phase 2).
- [ ] In `RequestComparer`, after `Priority`, tie-break by prior `(PageIndex, PixelRect.X, PixelRect.Y)` for entries that had a resident allocation last frame. New entrants tie-break by `Key` (current behavior).
- [ ] Verify the comparer remains a total, stable ordering. Add a comparer self-test in `ShadowAtlasManagerPhaseTests`.
- [ ] Test: two equal-priority lights retain stable relative ordering across 60 frames despite per-frame `IsDirty` toggles.

### 8E — Asymmetric LOD Hysteresis

- [ ] Replace `LodChangeCooldownFrames` with two values:
  - [ ] `LodDownsizeRePromotionCooldownFrames` (default 30–60): wait after a *forced* downsize before allowing growth.
  - [ ] `LodVoluntaryChangeCooldownFrames` (default 6): wait between voluntary LOD changes (e.g. distance-based).
- [ ] Tag prior LOD transition reason in `ShadowLodState`: `Voluntary | ForcedDownsize | ForcedUpsize`. The reason determines which cooldown is consulted on the next promotion attempt.
- [ ] An in-place upgrade (8B) that the buddy can satisfy "for free" without displacing any other entry bypasses the cooldown.
- [ ] Promotion may apply immediately for newly visible receivers (relevance promotion), since the worse failure mode is sustained low resolution on suddenly important lights.
- [ ] Pinned lights ignore both delays.
- [ ] Tests:
  - [ ] sustained contention: forced-down entry stays demoted until contention drops for ≥ `LodDownsizeRePromotionCooldownFrames` consecutive frames
  - [ ] score oscillating across a ladder boundary produces stable resolution
  - [ ] sudden visibility promotes within one frame
  - [ ] pinned light unaffected by hysteresis

### 8F — Stale-Tile And Skip Interaction Matrix

- [ ] Define and implement:
  - [ ] relevance-demoted + clean tile + same region available → reuse, no re-render
  - [ ] relevance-demoted + dirty tile + previous region available → publish `SkipReason.StaleTileReused`
  - [ ] relevance-zero + previous region available → publish `SkipReason.NotRelevant`, may keep stale tile for one-frame fallback
  - [ ] relevance-zero + no previous region → do not submit
- [ ] Decide and document the one-frame popping policy when a previously-skipped light becomes relevant: either accept a one-frame stale render or pre-allocate a minimum-resolution placeholder.
- [ ] Tests covering each row.

---

## Phase 9 — Light-Type Granularity

Goal: drive resolution per cascade and per face, and reserve groups in a layout that respects each light type's render path.

### 9A — Directional Cascade Granularity And Grouping

- [ ] Compute relevance per cascade using the cascade slice AABB (Phase 6B).
- [ ] Cascades fully outside the camera-set frustum demote to minimum resolution or skip with `SkipReason.NotRelevant`.
- [ ] Add a grouped allocation path for cascades from the same light and encoding.
- [ ] Prefer a deterministic 2×2 cascade pack when four cascades share a resolution.
- [ ] Support partial cascade groups when only primary or fewer cascades are submitted.
- [ ] **Cascade group atomicity**: best-effort. If the 2×2 pack cannot fit, fall back to ungrouped per-cascade allocation rather than failing the whole light.
- [ ] When `UseDirectionalShadowAtlas` is true, the allocator must reduce cascade tile resolutions until every required active cascade is resident; never fall through to legacy `ShadowMap`/`ShadowMapArray` mid-frame.
- [ ] Receiver binding enables the directional atlas for a light only when its required active directional tiles are sampleable.
- [ ] Tests: far cascade off-screen + near cascades on-screen → only far demoted; mixed cascade resolutions; grouped 2×2 still occurs when all four are relevant.

### 9B — Spot Specifics

- [ ] Spot relevance is one tile per light; resolution maps from `Phase 7` score directly.
- [ ] Spot atlas pages remain depth-only. A spot light whose resolved `ShadowMapEncoding` is VSM or EVSM bypasses the spot atlas (standalone moment shadow map with mip chain) and is excluded from atlas balancing for that frame; relevance still drives its resolution.
- [ ] Anchor-slot strategy (Phase 10) is especially effective for many-spot scenes; ensure the per-spot prior placement key is stable.
- [ ] Tests: VSM/EVSM spot bypasses the atlas while relevance-zero non-VSM spot publishes `NotRelevant`; many-spot scene retains stable per-spot slot across 60 frames.

### 9C — Point Face Granularity

- [ ] Compute relevance per face using the face frustum (Phase 6B) and the per-face camera-axis dot (Phase 7).
- [ ] Faces with no on-screen receivers demote or skip independently. A point light may have one face at full resolution and five at 1/4 or 1/16, or any other heterogeneous combination, in the same group.
- [ ] **Heterogeneous cube-face group reservation**:
  - [ ] Add a pre-allocation step `TryReservePointLightFaceGroup(LightId, EncodingState, faceSizes[6])`:
    - [ ] sort faces by relevance descending
    - [ ] compute the smallest power-of-two bounding square that can host the six face sizes as buddy sub-tiles (1×`S`, 2×`S/2`, 4×`S/4`, etc.)
    - [ ] reserve that bounding block on a single page; sub-divide deterministically; assign each face to its sub-tile
  - [ ] Use this grouped reservation when at least two faces of the same light are dirty in the same frame; otherwise fall back to per-face allocation.
  - [ ] Update `BuildPointFaceGroups` to emit `ShadowAtlasGroupedPointFaceAllocation` with **heterogeneous** `InnerPixelRect`/`UvScaleBias` per member, ordered by face index.
- [ ] **Point face group atomicity is partial**: individual faces may be allocated, demoted, skipped, or evicted independently when budgets require it.
- [ ] Receiver code path is unchanged; per-face metadata already carries individual `UvScaleBias` and `InnerPixelRect`.
- [ ] PCF/PCSS/Vogel taps that cross face seams continue to perturb the receiver direction, re-select the owning face, and sample that face's atlas metadata.
- [ ] Missing or demoted faces use their published fallback (usually a stale tile when a previous render exists, or contact-only before first render) instead of sampling undefined atlas texels.
- [ ] Tests:
  - [ ] light with one full-res face and five quarter-res faces produces a single group on one page with non-overlapping tiles and stable layout across frames
  - [ ] grouped layered render for heterogeneous faces issues in one pass (`gl_ViewportIndex` per face); scissors set per face from `PixelRect`
  - [ ] adjacent dirty faces continue past the soft per-frame budget once refresh starts (matches current contract)
  - [ ] PCF taps crossing a face edge between two different-resolution faces sample the correct neighbor metadata

---

## Phase 10 — Anchor Slots For High-Priority Lights

Goal: bound visible churn under heavy pressure to lower-priority lights.

- [ ] Setting `ShadowAnchorLightCount` (default small, e.g. 2 per kind). The top-K lights per kind by `(EditorPinned, Priority, RelevanceScore)` get an anchor reservation other lights cannot displace within a frame.
- [ ] Implementation: solve anchor lights' `BalancedAllocationEntry`s first as a separate pass, then solve the rest.
- [ ] Anchor slots are sticky reservations; when an anchor light's prior slot is free, it is always taken first.
- [ ] Tests: with K = 2 and four contended lights of a kind, the two anchor lights retain stable slots across 120 frames; the other two may demote/swap but cannot evict anchors.

---

## Phase 11 — VR / Stereo

- [ ] Verify the camera set always includes both eyes when XR is active.
- [ ] Verify mirror/preview cameras can be flagged as relevance-contributing without forcing them on by default (lower importance by default).
- [ ] Stereo regression test: a receiver visible only to the right eye keeps its light at full resolution.
- [ ] Mirror-display test: a receiver visible only on the desktop mirror keeps its light at the mirror's importance-weighted score.
- [ ] Confirm point-face per-face relevance handles per-eye visibility correctly (a face seen by only one eye stays at full res).

---

## Phase 12 — Diagnostics And Editor Visibility

- [ ] Per-light diagnostic record: score inputs, computed score, mapped resolution, hysteresis state, skip reason.
- [ ] ImGui editor inspector for the active relevance camera set and per-light score breakdown.
- [ ] Counters using plain `int`/`long`/`uint` fields (no boxing, no per-frame `Dictionary<string, int>`-style updates, no LINQ aggregation):
  - [ ] reserve hits / misses
  - [ ] new allocations / reused persistent allocations
  - [ ] demotions / repacks / evictions / page creations / failed allocations by reason
  - [ ] lights demoted by relevance / skipped with `NotRelevant` / promoted within one frame
  - [ ] cascades and point faces demoted independently
  - [ ] point-face slot churn count (target zero in steady state)
- [ ] Surface page count, largest free block, and free texels in existing shadow atlas diagnostics.
- [ ] Rate-limited debug logging for repack/eviction/promotion/demotion in editor builds; no per-frame string construction unless logging is enabled and rate-limited.
- [ ] Update docs when settings, task entries, launch flags, or editor diagnostics change.

---

## Phase 13 — Persistent State Lifecycle Audit

- [ ] All persistent dictionaries pre-sized in `Configure`, cleared (not reallocated) in `ResetResources`.
- [ ] None of the new state is touched between `Publish` and the next `BeginFrame`.
- [ ] Bounded eviction policy keyed on `_frameId - lastSeenFrame > N` so dictionaries do not grow unbounded when lights are destroyed.
- [ ] No-allocation steady state: profiler/test guard asserts zero managed allocations during `SolveAllocations` and the relevance pass after warmup.

---

## Phase 14 — Validation And Closeout

- [ ] Run targeted tests:
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter PointShadowAtlasStabilityTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
  - [ ] new relevance fixture
  - [ ] any new allocator-specific test fixture
- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Smoke validation in `Editor (Unit Testing World)`:
  - [ ] directional cascades, many spot lights, point-light atlas path, stale-tile fallback, one-page and multi-page settings
  - [ ] desktop scene with many off-screen lights → submitted texel count drops vs. baseline
  - [ ] VR scene with stereo eyes → no per-eye popping
  - [ ] mirror display enabled → mirror-only-visible lights remain at appropriate score
  - [ ] camera rapidly turning across a ladder boundary → hysteresis prevents popping
  - [ ] ≥6 dynamic point lights inside a single page's reach with static camera → `ShadowAtlas.PointFace.SlotChurn` is 0
  - [ ] same scene with moving camera → churn rises during motion and decays after motion stops within `LodDownsizeRePromotionCooldownFrames`
- [ ] Compare baseline and final metrics from Phase 0:
  - [ ] submitted requests/frame, submitted texels/frame, off-screen lights at non-minimum resolution, shadow render cost
  - [ ] median and 99th percentile `SolveAllocations` cost
  - [ ] GC allocations per solve
- [ ] Confirm hot-path expectations: no per-frame allocator data-structure allocations after warmup, no LINQ or captured closures in solve/relevance hot paths, no avoidable string formatting in per-frame logs.
- [ ] Update related docs and TODOs:
  - [ ] [dynamic-shadow-atlas-lod-todo.md](dynamic-shadow-atlas-lod-todo.md) cross-reference
  - [ ] `docs/work/README.md` index
  - [ ] rendering architecture notes if public behavior or settings changed
- [ ] Split any deferred follow-ups (virtual shadow maps, GPU-driven relevance, ray-traced relevance occlusion, Vulkan parity) into focused TODO docs.

---

## Risks

- Relevance scoring depends on having the active camera set at solve time. Ensure the runtime path uses `BeginFrame(IRuntimeRenderWorld, ReadOnlySpan<XRCamera>)`. The fallback overload must remain safe for headless/diagnostic paths.
- Sticky placement (Phase 8B) interacts with `RequestRepack`. When `_repackRequested` is true, all sticky paths must be bypassed; audit each Phase 8 change against that.
- Heterogeneous group reservation (Phase 9C) must not serialize what is currently a layered render. Keep the contract: one reservation, one render submission, six viewports for a fully-grouped light.
- Multi-page support (Phase 3) requires every receiver path to read `pageIndex` correctly; an audit miss produces silent sampling of the wrong page.
- Anchor slots (Phase 10) can starve lower-priority lights in pathological scenes; keep `ShadowAnchorLightCount` small and configurable.
- Asymmetric hysteresis (Phase 8E) interacting with rapid contention changes (e.g. `RequestRepack`) needs explicit documented behavior to avoid surprise pinning.

---

## Final Task

- [ ] Merge the dedicated `shadow-atlas-overhaul` branch back into `main` after all phases complete, validation passes, docs are updated, and any follow-up TODOs are filed.
