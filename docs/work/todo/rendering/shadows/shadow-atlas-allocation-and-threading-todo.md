# Shadow Atlas Allocation Efficiency And Off-Render-Thread Solve TODO

Status: implementation complete, validation follow-ups pending, created
2026-07-02. Analysis of `ShadowAtlasManager` allocation strategy,
hot-game-loop cost, and thread placement.
Branch: skipped by explicit request; implementation was done in the current
worktree without creating `rendering/shadow-atlas-plan-execute-split`.

Goal: allocate shadow map tiles with minimal per-frame latency and memory
overhead, and move every CPU-side allocation/solve/publish step off the render
thread. The render thread should only execute an already-frozen tile render
plan.

Primary code:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XRENGINE/Rendering/XRWorldInstance.cs` (`GlobalPreRender`, `GlobalCollectVisible`)
- `XRENGINE/Core/Time/EngineTimer.cs` (`CollectVisibleThread`, `DispatchSwapBuffers`)

Related docs (this TODO supersedes the two deferred solver items in the first):

- [Shadow Atlas Solve Efficiency TODO](shadow-atlas-solve-efficiency-todo.md)
  (deferred items absorbed here: "preserve reusable page/free-block state
  between retries", "track lowest failing size and skip impossible candidates")
- [Shadow Atlas Overhaul TODO](shadow-atlas-overhaul-todo.md) (workstream C/D)

## Current Behavior (verified 2026-07-02)

The entire shadow atlas CPU pipeline runs on the **render thread**, every frame,
inside `XRWorldInstance.GlobalPreRender()` → `Lights3DCollection.RenderShadowMaps(false)`
→ `UpdateShadowAtlasRequests(...)`:

1. `ShadowAtlas.BeginFrame(...)` — also calls `ConfigureFromEngineSettings()`
   (settings re-normalize + ~12 `EnsureCapacity` calls) every frame.
2. `Submit*ShadowAtlasRequests` — builds every request on the render thread,
   taking `_submitSync` per request.
3. `ShadowAtlas.SolveAllocations()` — full repack of every atlas page from
   scratch, every frame, with a demote-and-restart retry loop.
4. `ShadowAtlas.RenderScheduledTiles(...)` — GPU tile execution (must stay on
   the render thread) interleaved with planner-state mutation
   (`MarkTileRendered` writes `_currentAllocations`/`_frameAllocations`).
5. `ShadowAtlas.PublishFrameData()` — group/descriptor/metrics build, O(n)
   dictionary copy into `_previousAllocations`, resident-table maintenance.

Meanwhile the engine already has a dedicated collect-visible thread
(`EngineTimer.CollectVisibleThread`) that runs `Lights.CollectVisibleItems()`
overlapped with rendering, and a swap phase (`DispatchSwapBuffers`) that is
mutually exclusive with the render thread via the `_renderDone`/`_swapDone`
handshake. That is the natural home for the solve and the publish handoff.

## Findings

### F1: All solve cost is render-thread latency

Every millisecond of solve/publish work delays viewport rendering directly.
`SolveAllocations` is already instrumented as multi-millisecond in bad cases
(slow-solve warning threshold defaults to max(2 ms, render budget)). None of
steps 1–3 or 5 above need a GPU context.

### F2: Full re-solve every frame, even in steady state

- `TryBuildBalancedAllocations` calls `state.BeginFrame()` (resetting every
  buddy allocator) on **every attempt of every frame**. A completely unchanged
  layout still re-reserves every tile: each `TryReserve` prior-placement hit
  re-splits blocks from page level down (`SplitToSize` inserts ~3 free blocks
  per level via `InsertFreeBlockSorted` → `List.Insert`, an O(n) memmove).
- On any failure the solver demotes a batch and **restarts the entire pack**
  (`CalculateMaxBalancedSolveAttempts` allows up to 128 attempts). Cost spikes
  exactly when the frame is already under pressure.
- There is no feasibility precheck: the solver discovers over-subscription by
  failing placements one at a time instead of computing it up front from
  requested area vs page capacity.

### F3: O(n²) scans in the per-frame path

- `TryFindRequest(key)` — linear scan of `_requests`; called per group member
  in `TryGetDirectionalCascadeGroupRenderRequirement`,
  `TryGetPointFaceGroupRenderRequirement`, and per member again in the
  `MarkTileRendered` loops after each group render.
- `RemoveOverlappingResidentAllocations` — for each current allocation,
  iterates the whole resident table (current × resident scan each
  `PublishFrameData`).
- `ApplyDirectionalCascadeGroupResolutionCaps` +
  `HasEarlierDirectionalCascadeGroup` + `CountDirectionalCascadeGroupMembers`
  — O(entries²) per bucket per frame.
- `TryGetDirectionalCascadeGroupContainingRequest` / `TryGetFirstPointFaceGroup`
  — linear over groups per request inside the render-tile loop;
  `FindLastDirectionalCascadeGroupRequestIndex` re-scans `_requests` per group.
- `TryGetFrameAllocation` fallback — linear scan of `_frameAllocations`.

### F4: Hidden per-frame overhead and GC pressure

- `WarnIfSlowSolve` → `ResolveSlowSolveWarningThresholdMilliseconds` calls
  `Environment.GetEnvironmentVariable` **every solve** (string allocation +
  syscall on the render thread). Cache it once.
- `XREngine.Debug.LightingEvery("ShadowAtlas.RenderBudget.Deferred", ...)`
  fires whenever `deferredTotal > 0` (steady state under load) with
  `params object[]` — the args array + 7 boxed values allocate at the call
  site even when throttled. Same pattern in several warning paths. Guard with
  `Debug.ShouldLogEvery` first (pattern already used by
  `LogShadowAtlasRenderSummary`).
- `PublishFrameData` rebuilds `_previousAllocations` by full element-wise copy
  each frame; the field is already non-readonly — double-buffer and swap
  references instead.
- `ConfigureFromEngineSettings()` runs the full normalize/ensure-capacity pass
  every `BeginFrame`; gate on a settings version/changed check.
- Buddy allocator details: `GetLevelForSize` is a linear level scan (use
  `BitOperations.Log2`), and sorted free lists pay O(n) insert churn that only
  exists because of the per-frame full reset (fixing F2 mostly removes it).
- `Submit` takes a lock per request although submission is single-threaded
  today; either assert thread affinity and drop the lock, or batch all submits
  under one lock entry.

### F5: Memory overhead

- `EnsureTextureArrays` allocates texture arrays with **all `MaxPages` layers
  up front** on first page use per (kind, encoding) state, and `ResidentBytes`
  jumps to the full-array estimate even with one active page. With
  4096² pages this is 64 MB+ per layer per array.
- Every page state carries **both** a color/moment array **and** a
  `RasterDepthTexture` D24 array, and `ShadowAtlasPageResource.FrameBuffer`
  always attaches both. For the directional `Depth` encoding the receiver
  samples only the raster depth (`ShouldSampleRasterDepth`), so the color
  array is write-only ballast. Both backends support depth-only FBOs (see
  repo note: Vulkan legacy cascade path already went depth-only 2026-07-01).
- Worst case there are 12 (3 kinds × 4 encodings) independent state arrays.

### F6: Phase-coupling that blocks the thread split

- `MarkTileRendered` (render path) mutates solve-owned state
  (`_currentAllocations`, `_frameAllocations`). A plan/execute split needs a
  one-way completion feedback channel instead.
- `SubmitShadowAtlasRequest` reads `ShadowAtlas.PublishedFrameData` (previous
  allocation lookup) — stays coherent as long as request building and solve
  move together.
- `CurrentShadowScratch` is thread-static: today the collect thread and the
  render thread silently use **different** scratch instances for the same
  logical data (`PopulateLocalShadowRelevanceCameras` runs on both). Moving
  everything to the collect phase unifies this (and removes a duplicate
  relevance-frusta rebuild).

## Target Architecture: Plan/Execute Split With A Persistent Allocator

### Plan phase (collect-visible thread, overlapped with rendering)

Runs inside `Lights3DCollection.CollectVisibleItems()` (or a job kicked from
it), **before** lights collect caster render commands:

1. Drain the tile-completion feedback queue from the previous render frame.
2. Build requests (camera render matrices are valid here:
   `PreCollectVisible` applies pending render-matrix changes before
   `CollectVisible` dispatches).
3. Incremental solve (see below), group building, frame-data fill.
4. Freeze into an immutable `ShadowAtlasRenderPlan`: ordered tile/group render
   entries with page FBO references, viewport rects, and budget/bypass
   metadata — everything `RenderScheduledTiles` currently derives on the fly.

Bonus: because the plan now exists before `light.CollectVisibleItems()`,
lights can skip collecting casters for tiles the plan did not schedule
(today they collect for everything and the atlas defers afterwards).

### Publish (swap phase)

`DispatchSwapBuffers` is mutually exclusive with the render thread — publish
the plan + `ShadowAtlasFrameData` by reference swap there (extend
`Lights3DCollection.SwapBuffers()`).

### Execute phase (render thread)

`RenderScheduledTiles` becomes a pure plan interpreter: iterate frozen plan
entries, apply tile/time budgets, issue GPU work, and push
`(ShadowRequestKey, contentHash, frameId)` completion records into a
single-producer/single-consumer queue. No planner-state mutation, no request
scans, no group discovery.

Completion staleness: a plan built while frame N renders sees frame N-1
completions. That is benign — a tile rendered in frame N whose completion is
seen one plan late is simply confirmed clean one frame later; the content-hash
dirty logic already tolerates re-render. Optional refinement: kick the plan job
only after the render thread signals "shadow tiles executed" early in the
frame, giving fresh feedback while still overlapping the rest of the frame.

### Incremental allocation (replaces full repack)

- Keep buddy-allocator occupancy **persistent across frames**. Only process
  deltas: new requests allocate, departed/TTL-expired residents free, and
  resolution changes free+reallocate (prior-slot sub-block logic already
  exists). A static scene solves in O(changed requests) ≈ O(0).
- Full reset happens only on `RequestRepack`, settings/topology change, or a
  fragmentation trigger — never per frame.
- Add a **feasibility waterline** before placement: per (kind, encoding)
  bucket, sum requested tile areas, compare against page capacity × occupancy
  factor, and demote lowest-relevance entries proportionally in one
  deterministic pass. Packing then succeeds in one attempt in the common case;
  the retry loop remains only as a bounded fallback (attempt ceiling ~4, not
  128).
- On a single placement failure, repair locally: free + re-place that entry
  (or its directional group) at the next size down instead of resetting all
  page state.
- Free-list structure: per-level free lists as index bitsets over the level's
  slot grid (pow2 tiles align to a fixed grid per level). Deterministic
  first-fit = lowest set bit scan; O(1) alloc/free, no sorted `List.Insert`,
  no allocation churn.

## Phases

### Phase 0: Branch And Baseline

- [x] Create branch `rendering/shadow-atlas-plan-execute-split` (first task).
  - Skipped by explicit user request: "Don't branch."
- [ ] Capture Unit Testing World baselines with the existing profiler scopes
  (`ShadowAtlasManager.SolveAllocations`,
  `Lights3DCollection.UpdateShadowAtlasRequests`,
  `ShadowAtlasManager.PublishFrameData`) plus
  `Engine.Allocations`/thread-allocation tracking for the render thread:
  steady-state and worst-case ms, per-frame allocated bytes. Record numbers
  here.

### Phase 1: Hot-Path Quick Wins (no architecture change)

- [x] Build a `Dictionary<ShadowRequestKey, int>` request index during
  `ClassifyRequestsForSolve`; replace every `TryFindRequest`,
  `FindLastDirectionalCascadeGroupRequestIndex`, and the
  `TryGetFrameAllocation` linear fallback.
- [x] Replace group discovery in the render loop with per-light lookups built
  once after `BuildDirectionalCascadeGroups`/`BuildPointFaceGroups`
  (`Dictionary<Guid or group key, int>`).
- [x] Single-pass directional group caps: bucket entries by
  (LightId, Domain, Source, Encoding) once, then cap; delete the
  `HasEarlierDirectionalCascadeGroup`/`CountDirectionalCascadeGroupMembers`
  O(n²) scans.
- [x] Bucket `_residentAllocations` by (AtlasId, PageIndex) so
  `RemoveOverlappingResidentAllocations` only scans same-page residents.
- [x] Double-buffer `_previousAllocations`/`_currentAllocations` and swap
  references in `PublishFrameData` instead of copying.
- [x] Gate `ConfigureFromEngineSettings` on a settings-version/equality check.
- [x] Cache the `XRE_SHADOW_ATLAS_SOLVE_WARN_MS` env read (static lazy).
- [x] Pre-check `Debug.ShouldLogEvery` before building args for the
  `ShadowAtlas.RenderBudget.Deferred` log and other per-frame-under-load
  `LightingEvery`/`LightingWarningEvery` call sites (boxing + array alloc).
- [x] `GetLevelForSize` → `BitOperations.Log2`; assert power-of-two instead of
  scanning.
- [x] Decide `Submit` locking: single-producer assert (preferred) or batched
  submission API; remove the per-request `lock`.

### Phase 2: Persistent Allocator And Single-Pass Solve

- [x] Make page occupancy persistent across frames: `BeginFrame` no longer
  resets allocators; track per-request placements and free only on eviction,
  TTL expiry, or resolution change.
- [x] Add the feasibility waterline pre-pass (per bucket: total requested
  texels vs capacity with a fragmentation slack factor; deterministic
  relevance-ordered demotion to fit).
- [x] Replace demote-and-restart with local repair (free + re-place the failed
  entry/group at reduced size); keep a small bounded attempt ceiling with the
  existing deterministic-fallback diagnostics.
- [x] Convert per-level free lists to slot bitsets (deterministic lowest-bit
  first-fit; O(1) alloc/free; kills sorted-insert churn).
- [x] Full reset only on `RequestRepack`, settings shape change, or a
  fragmentation threshold; increment `Generation` and publish the reason
  (aligns with overhaul TODO workstream C).
- [x] Keep `ShadowAtlasSolveDiagnostics` counters accurate for the new flow
  (attempts should read ~1 steady state; add `IncrementalReuseCount`,
  `WaterlineDemotionCount`).
- [x] Update `ShadowAtlasManagerPhaseTests` determinism expectations (same
  request set ⇒ same layout must still hold, now trivially, because layout is
  persistent).

### Phase 3: Plan/Execute Thread Split (off the render thread)

- [x] Introduce `ShadowAtlasRenderPlan` (immutable, pooled): ordered tile and
  group entries with resolved page/FBO reference, inner rect, projection type,
  face/cascade index, budget class (critical bypass, normal, deferrable), and
  the request data needed for rendering. No dictionary lookups at execute
  time.
- [x] Split `RenderScheduledTiles` into plan interpretation (render thread)
  and scheduling policy (plan phase). Budget/deferral decisions that need
  frame-time measurement stay in the executor; ordering and grouping move to
  the plan.
- [x] Replace `MarkTileRendered`'s direct mutation with an SPSC completion
  queue `(key, contentHash, frameId)`; planner drains it at the next
  `BeginFrame`. Published allocations get `LastRenderedFrame` reconciled at
  publish, one frame later (verify fallback/stale-tile logic tolerates this —
  it keys off `LastRenderedFrame != 0`, so first-render latency is the case to
  test).
- [x] Move `UpdateShadowAtlasRequests` (BeginFrame, submits, solve, group
  build, frame-data fill) from `RenderShadowMapsInternal` into the collect
  phase in `Lights3DCollection.CollectVisibleItems()`, before
  `light.CollectVisibleItems()`; publish plan + frame data in
  `Lights3DCollection.SwapBuffers()`.
- [x] Use the plan to drive caster collection: lights skip collecting for
  tiles the plan did not schedule this frame.
- [x] Page resource creation off the render thread: verify
  `XRTexture2DArray`/`XRFrameBuffer` construction is data-only (GPU objects
  created lazily on first bind). If any backend eagerly touches GPU state,
  split `CreatePage` into data-side reservation (plan) + render-thread ensure
  step executed before the first tile that uses the new page.
- [x] Thread-affinity asserts: planner methods assert collect thread (or job),
  executor asserts render thread, publish asserts swap phase. Keep the plain
  synchronous call order (`BeginFrame`/`Submit`/`Solve`/`Publish`) working for
  unit tests via a single-threaded facade.
- [x] Unify the thread-static `CurrentShadowScratch` usage: with everything on
  the collect thread, remove the duplicated relevance-frusta population that
  previously ran on both threads (`PopulateLocalShadowRelevanceCameras` on
  collect + `PrepareLocalShadowRelevanceFrusta` on render).
- [x] Ensure `PublishedFrameData` consumers stay coherent:
  `Lights3DCollection.ForwardLighting` buffer build and
  `VPRC_LightCombinePass` read the swap-published buffer on the render thread;
  the submit-path previous-allocation lookup moves with the planner.
- [x] VR check: request building reads eye viewports/cameras at collect time —
  confirm acceptable pose latency vs the current pre-render read (VR already
  prefers stability over per-frame culling here).

### Phase 4: Memory Overhead

- [x] Depth-only atlas pages: when the encoding's receiver samples raster
  depth (`ShouldSampleRasterDepth` — directional `Depth`), skip the
  color/moment array entirely and attach only the depth array to the page FBO
  (mirror of the 2026-07-01 Vulkan legacy-cascade depth-only change; both
  backends support zero-color FBOs).
- [x] Stop allocating all `MaxPages` layers up front: grow the layer count
  geometrically (recreate + copy at a safe point, generation bump) or switch
  to per-page textures behind the existing page-descriptor indirection.
  Account `ResidentBytes` by actual layers.
- [x] Re-audit `MaxShadowAtlasMemoryBytes` enforcement against the new
  accounting (budget checks currently compare against full-array estimates).

### Phase 5: Validation And Close Out

- [ ] Unit tests: persistent-layout determinism, waterline demotion
  determinism, completion-feedback reconciliation (first render + stale tile),
  plan immutability (executor cannot mutate planner state), depth-only page
  FBO selection per encoding.
- [ ] Benchmark scenes from the solve-efficiency TODO Phase 5 (in-budget and
  over-budget) reused as acceptance gates.
- [ ] Acceptance: steady-state plan build (solve + groups + publish prep)
  < 0.5 ms on the collect thread; render-thread shadow atlas CPU cost =
  tile execution only (no solve/publish scopes on the render thread in the
  profiler).
- [ ] Acceptance: zero steady-state GC allocations in plan build and execute
  paths (verify with thread-allocation tracking; existing
  `Report-NewAllocations` task for static scan).
- [ ] Acceptance: static scene solves with `BalancedSolveAttemptCount == 1`
  and zero allocator churn after warmup.
- [ ] Acceptance: directional-depth atlas memory drops by the color-array
  bytes (page size² × bytes-per-texel × layers) with no visual change.
- [ ] Run `Test-SurfelGi`-adjacent shadow test suites:
  `ShadowAtlasManagerPhaseTests`, `PointShadowAtlasStabilityTests`,
  `DirectionalShadowAtlasFallbackTests`,
  `CascadedShadowDefaultsAndForwardShaderTests` (note: some are source-scrape
  tests — update scraped literals when signatures change).
- [x] Update `docs/architecture/rendering/default-render-pipeline-notes.md`
  (thread placement + publish handoff are downstream-visible invariants) and
  reconcile the two related TODOs.
- [x] Merge `rendering/shadow-atlas-plan-execute-split` into `main` (final
  task).
  - Skipped by explicit user request because no branch was created.

## Risks

- **Completion feedback latency**: plan N+1 may not see frame N tile
  completions (1-frame-stale). Mitigation: content-hash dirty logic already
  re-renders safely; optional early-frame signal ordering removes it entirely.
- **Off-thread GPU object creation**: page texture/FBO construction must be
  data-only off the render thread; needs explicit verification per backend
  (Vulkan eager paths are the risk).
- **Source-scrape tests**: several shadow tests assert on
  `ShadowAtlasManager.cs` source literals and will need updating alongside
  signature changes (pre-existing stale-literal failures already noted in repo
  memory).
- **Behavioral drift under pressure**: waterline demotion replaces iterative
  demotion; sticky-demotion/cooldown semantics (`DemotionPromotionCooldownFrames`,
  `DemotionSwitchMargin`) must be re-expressed in the single-pass form to keep
  tile stability (no LOD flip-flop).
- **Editor/tests calling the manager directly**: keep the synchronous facade
  so `ShadowAtlasManagerPhaseTests` and tools drive frames without the engine
  timer.
