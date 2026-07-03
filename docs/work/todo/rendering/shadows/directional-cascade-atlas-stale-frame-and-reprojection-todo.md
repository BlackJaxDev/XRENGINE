# Directional Cascade Atlas Stale Frame And Reprojection TODO

Status: active, created 2026-07-02.

Latest update 2026-07-02: framerate is stable. The latest user report ruled out
the arbitrary camera/exposure frame-count hold and exposed a separate black
main-view flicker regression. The hold was removed. The current fixes are:
stable Vulkan physical resource planning for persistent targets, filtered
mipless Vulkan auto-exposure metering for planner-backed HDR sources, forward
directional cascade binding that keeps using published desktop cascades when a
material pass has no concrete camera component, and same-slot cascade atlas
publication that preserves previous rendered uniforms as `StaleTile` while a
fresh render catches up. The latest audited MCP move showed smooth
`AutoExposureTex` values, no post-startup forward bind rows with
`shaderAtlasEnabled=False`, same-slot pending refreshes publishing
`decision=PreservedPrevious`, `fallback=StaleTile`, `sampleable=True`, and
`DirectionalCascade.MixedGenerationPrevented` only during startup.
See the follow-ups in
`docs/work/rendering/shadow-atlas-framerate-regression-2026-07-02.md`.

Goal: eliminate directional cascade shadow lag, one-frame post-camera-stop
jitter, and movement-related frame drops in the shadow atlas path. Stale atlas
tiles may be reused only when their texture contents and sampling uniforms are
from the same rendered cascade generation.

Primary code:

- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`

Related notes:

- `docs/work/rendering/shadow-atlas-framerate-regression-2026-07-02.md`
- `docs/work/todo/rendering/shadows/shadow-atlas-allocation-and-threading-todo.md`
- `docs/work/todo/rendering/optimization/vulkan-primary-command-recording-fast-path-todo.md`

## Answer: Can We Temporally Reproject Stale Atlas Frames?

Yes, but it should be the fallback, not the first fix.

There are two levels:

1. **Shader-side stale sampling / matrix reprojection.** Keep the atlas tile as
   it was rendered, and sample it with the exact world-to-light matrix, split,
   bias, receiver offset, UV rect, and depth params that were used to render
   that tile. This is cheap and should be the default stale-tile behavior. It
   prevents a current-camera cascade matrix from sampling an older tile.
2. **Physical atlas reprojection.** Reproject the old shadow depth tile into a
   new tile by reconstructing world positions from old light-space depth and
   splatting/min-reducing them into the new cascade projection. This can reduce
   visible lag during sustained movement, but it cannot invent newly visible
   casters, it can create holes/disocclusion, and it adds GPU work exactly when
   the frame is already under pressure.

The practical plan is:

- First make render-plan execution race-free: the render thread must only ever
  execute an immutable, swap-published plan.
- Then make stale sampling coherent.
- Then make directional cascade refresh atomic enough that mixed generations do
  not reach the lighting shader.
- Then reduce refresh pressure at the source (texel-stable content hashes,
  per-cascade refresh cadence).
- Then add a short-lived shader-side stale reprojection mode.
- Only add physical atlas reuse if measurements prove the coherent stale path
  is not good enough — and for orthographic directional cascades prefer
  scroll/toroidal reuse of the overlapping region (exact for translation-only
  deltas) over depth splatting.

Phases 1-4 are strictly ordered (correctness). Phase 7's refresh-pressure
items (hash quantization, cadence, caster-set caching) are independent of
Phases 5-6 and can land any time after Phase 4; if they succeed, Phases 5-6
may not be needed at all.

## Current Failure Mode

- `UpdateCascadeShadows(...)` rebuilds current cascade slices from the moving
  camera every frame.
- `SubmitShadowAtlasRequest(...)` hashes the cascade camera view/projection, so
  directional cascades become dirty whenever the camera-dependent fit moves.
- `PublishShadowAtlasFrame(...)` currently publishes allocations/light atlas
  slots before `ShadowAtlas.RenderScheduledTiles(...)` renders the scheduled
  tiles.
- `MarkTileRendered(...)` only enqueues a completion, and completions are
  reconciled on the next atlas `BeginFrame(...)`. `BeginFrame(...)` runs on the
  collect thread concurrently with the previous frame's
  `RenderScheduledTiles(...)`, so whether a completion is visible one or two
  frames later is a race.
- `DirectionalLightComponent.SetCascadeAtlasSlot(...)` builds cascade slot
  uniforms from the current cascade slice, unless a narrow previous-slot
  preservation check succeeds.
- `SelectRenderPlanForExecution()` on the render thread returns the pending
  planning-thread plan whenever `pending.FrameId == _frameId`. Both the pending
  plan contents and `_frameId` are mutated concurrently by the collect thread,
  and `BeginFrame(...)` stamps `_frameId` from
  `RuntimeEngine.Rendering.State.RenderFrameId`, which it reads while the
  previous frame is still rendering. The pending plan for the next frame
  therefore commonly carries the same id the render thread compares against,
  so `RenderScheduledTiles(...)` can execute a mid-write (torn) or next-frame
  plan whose lights still have this frame's swapped command buffers.
- `RenderScheduledTiles(...)` still reads planner-owned state from the render
  thread: `_requests.Count`, `_requests[i]` via
  `ShouldRenderRefreshPastTimeBudgetAtIndex(...)`/`...PastBudgetAtIndex(...)`,
  and `MarkTileRendered(...)` stamps completions from the concurrently mutated
  `_frameId`. `EnqueuePlanMemberCompletions(...)` and the sequential cascade
  fallback re-resolve `SelectRenderPlanForExecution()` mid-entry and can
  observe a different plan than the loop started with.

That allows a stale atlas tile rendered for cascade generation N-1 to be sampled
with cascade matrix N. While the camera moves, this looks like shadow lag. On
the first frame after the camera stops, the just-drained completion can still
describe the previous rendered generation while the slice data has already
advanced to the stopped camera. That creates the exact one-frame incorrect
jitter.

The randomness comes from the races above: whether the render thread executes
the published or the mid-write pending plan, and whether a given completion is
drained after one frame or two, both depend on how far the collect thread has
progressed when `RenderScheduledTiles(...)` runs. The sustained lag while the
camera moves has a second contributor: `ContentChanged` directional dirtiness
does not qualify for the time-budget bypass, so with the sequential Vulkan
path exceeding `MaxRenderMilliseconds`, cascades are deferred and old tile
content is sampled with current slice matrices.

## Non-Goals

- Do not hide missing shadow data behind silent CPU fallbacks.
- Do not use physical reprojection as a replacement for rendering fresh
  cascades when budget allows.
- Do not publish mixed cascade generations as if they were a clean cascade set.
- Do not spend GPU time on reprojection without counters showing it is cheaper
  than direct cascade refresh.

## Phase 0 - Reproduce And Instrument

- [x] Add a directional cascade provenance audit line that prints, per cascade:
  request content hash, allocation content hash, last rendered frame, slot
  sample content hash, slot sample frame, current cascade matrix hash, rendered
  sample matrix hash, fallback mode, page, rect, and source.
- [x] Add a debug counter for `DirectionalCascade.StaleSampled`,
  `DirectionalCascade.MixedGenerationPrevented`,
  `DirectionalCascade.PhysicalReprojected`, and
  `DirectionalCascade.ForcedFreshRender`.
- [ ] Capture a camera-move-then-stop repro with directional shadow audit
  enabled and record the frame where the one-frame jitter appears.
- [ ] Capture CPU/GPU profile dumps for the same repro with Vulkan directional
  cascade atlas enabled.
- [x] Count plan-execution source per frame (published vs pending) and tile
  completion latency in frames (plan id rendered vs plan id at drain); log any
  pending-plan execution as an error, not a statistic.
- [x] Confirm via captures that the post-stop jitter is confined to shadowed
  regions (cascade texture/matrix mismatch) and is not a whole-frame
  present-order or temporal-history regression.
  Earlier live iteration showed a separate whole-frame exposure/present
  artifact, but the follow-up user report isolated the remaining issue to the
  cascade+atlas path. The expired-stale policy could produce a one-frame
  lit/contact fallback in shadowed regions after camera movement. A later
  render-on-demand validation isolated and fixed another whole-frame exposure
  artifact; post-fix captures kept final post-process output stable while
  holding GPU auto exposure during editor render-on-demand idle/settle frames.

Acceptance criteria:

- [x] The bad one-frame sample can be identified as either a matrix/content
  mismatch, a partial cascade generation, a pending-plan execution, or a fresh
  render that arrived too late for the lighting pass.
  The latest cascade+atlas-only report was identified as an expired stale atlas
  slot reaching the shader's lit/contact fallback path; the earlier
  present/auto-exposure history loss was a separate whole-frame artifact.

## Phase 1 - Make Render-Plan Execution Race-Free

The plan/execute split still shares mutable state across the collect and
render threads. This is the most likely source of the "random incorrect
frame" behavior and must be fixed first; provenance work cannot be validated
on top of torn plan reads.

- [x] Replace the frame-id heuristic in `SelectRenderPlanForExecution()`:
  stamp plans with a planner-owned monotonic plan id (never
  `RuntimeEngine.Rendering.State.RenderFrameId`, which is read cross-thread
  and off-by-one during collect), and have the render thread execute only the
  plan published at the swap barrier. Resolve the plan once per
  `RenderScheduledTiles(...)` call and pass that instance down the execute
  call chain (grouped render, sequential fallback,
  `EnqueuePlanMemberCompletions`) instead of re-resolving.
- [x] Route the synchronous capture path (`EnsureShadowMapsCurrentForCapture`,
  `RenderShadowMapsInternal(collectVisibleNow: true)`) through the same
  published-plan handoff (it already plans and publishes before rendering on
  one thread), and assert the execute path can never observe the pending plan.
- [x] Remove all render-thread reads of planner-owned collections: replace
  `_requests.Count` and `_requests[i]` usage
  (`ShouldRenderRefreshPastTimeBudgetAtIndex(...)`,
  `ShouldRenderDirectionalRefreshPastBudgetAtIndex(...)`) with plan-entry
  fields precomputed at plan time (per-entry time-budget bypass flag; source
  request count already stored on the plan).
- [x] Stamp tile completions from the executing plan (plan id plus
  entry/member payload), never from the shared `_frameId` field
  (`MarkTileRendered` / `EnqueueTileCompletion`).
- [x] Delete the dead pre-plan group render overloads
  (`TryRenderDirectionalCascadeGroup(seedRequest, group, ...)`,
  `TryRenderPointFaceGroup(seedRequest, group, ...)`, and the group-based
  sequential fallback) that still read `_frameAllocations`/`TryFindRequest`
  on the render thread.
- [x] Fix `TrimResidentAllocations()`: its over-capacity check consults
  `_currentAllocations` after `PublishFrameData()` swapped and cleared it, so
  it can evict residents that are actually current. Check the dictionary that
  holds this frame's allocations after the swap.
- [x] Extend the DEBUG thread-affinity asserts to planner-state readers
  (`RequiresTileRender`, resident lookups, `_requests` access) so future
  cross-thread regressions fail loudly instead of becoming visual noise.

Acceptance criteria:

- [ ] The render thread provably executes only the swap-published plan; the
  Phase 0 counter records zero pending-plan executions in a camera-motion
  soak run.
- [x] Every completion record carries the plan id of the plan that rendered
  it.

## Phase 2 - Fix Cascade Slot Provenance

- [x] Add a small immutable directional cascade sample payload, for example
  `DirectionalCascadeSampleState`, containing split, blend width, bias,
  receiver offset, world-to-light matrix, source, cascade index, content hash,
  and render frame.
- [x] Freeze that payload into each directional cascade `ShadowMapRequest` when
  the request is built.
- [x] Carry the frozen payload through `ShadowAtlasRenderPlanEntry` or
  `ShadowAtlasRenderPlanMember` so the render completion knows exactly which
  cascade generation was rendered.
- [x] Extend the tile completion path for directional cascades to include the
  rendered sample payload, not only `(key, contentHash, frameId)`.
- [x] Reconcile `LastRenderedFrame` in `ReconcileTileCompletion(...)` from the
  completion's plan id/payload rather than the raced `_frameId` capture, so
  the slot preservation equality checks stop passing or failing
  nondeterministically.
- [x] Store the latest rendered sample payload per directional cascade slot on
  `DirectionalLightComponent`.
- [x] Change `SetCascadeAtlasSlot(...)` so a sampleable stale atlas slot uses
  the latest rendered sample payload, never the current `state.Slices[index]`
  payload, unless the current request content hash has actually rendered.
- [x] Remove or loosen the fragile preservation rule that requires the previous
  slot's `ContentVersion` and `LastRenderedFrame` to match the new allocation
  exactly; prefer explicit rendered sample payload availability.
- [x] If no rendered payload exists for a resident stale tile, mark the slot not
  sampleable and fall back visibly to lit/legacy rather than pairing unknown
  content with current matrices.

Acceptance criteria:

- [x] A stale directional atlas tile and its shader uniforms always share the
  same content hash/render generation.
- [x] Invariant: a sampleable slot's texture-content generation and its
  uniform payload generation are identical every frame, regardless of
  completion timing or budget deferral.
- [x] Stopping camera movement no longer produces a single-frame wrong cascade
  matrix/atlas texture pairing in the current live validation. Warm repeated
  Unit Testing World captures showed no directional cascade snap; the remaining
  validation gap is automation.

## Phase 3 - Fix Publish Ordering And Same-Frame Visibility

- [x] Split atlas publish into allocation publish and post-render completion
  publish, or move the light-slot publish after `RenderScheduledTiles(...)`
  when the render thread builds/renders in the same frame.
- [x] Add a render-thread `CommitCurrentFrameCompletions(...)` path that can
  reconcile completions from `RenderScheduledTiles(...)` immediately before the
  lighting pass consumes directional light uniforms. The natural commit point
  is the end of `RenderShadowMapsInternal(...)` (still inside
  `GlobalPreRender`, after tiles render, before any viewport lighting runs).
- [x] Ensure the current-frame completion path does not mutate planner-owned
  collections while the collect/planning thread can read them.
- [x] Do not mutate the published `ShadowAtlasFrameData` buffer in place from
  the render thread: the planning thread reads `PublishedFrameData` during
  request building. Same-frame completion commits should update only per-light
  locked slot state (plus a render-thread-owned reconciliation view); the
  planner keeps consuming completions through the queue.
- [x] Move the planner's previous-allocation/dirty-reason lookup
  (`SubmitShadowAtlasRequest`, `ResolveShadowDirtyReason`,
  `CanReuseShadowAtlasPreviousFrame`) from `PublishedFrameData` to
  planner-owned reconciled resident state, so request dirtiness no longer
  depends on publish timing at all.
- [x] Update `PublishShadowAtlasDiagnostics()` so directional light atlas slots
  are based on reconciled rendered state when available.
- [x] Add a guard that prevents `PublishedFrameData.Metrics.TilesScheduledThisFrame`
  from reporting zero/stale values when same-frame render completions have been
  committed after the first publish.

Acceptance criteria:

- [x] A tile rendered before the main lighting pass can become visible to that
  same lighting pass without waiting for the next `BeginFrame(...)`.
- [x] The diagnostics frame id, rendered frame id, and light slot sample frame
  explain the same state.

## Phase 4 - Make Directional Cascade Refresh Atomic

- [x] Treat all cascades for one `(light, source, encoding)` as one generation
  in the scheduler.
- [x] If any cascade in the group is dirty due to
  `ProjectionOrCameraFitChanged`, either render the full group or keep sampling
  the previous coherent group.
- [x] Prevent publishing partially refreshed cascade groups as a clean cascade
  set.
- [x] Ensure the execute-side time budget cannot split a cascade group
  mid-render: once a group entry starts, all members render or the group
  fails atomically; budget checks may only occur on entry boundaries.
- [x] For Vulkan sequential fallback, create an atomic sequential group entry
  even when grouped atlas rendering is unsupported, so the scheduler sees one
  cascade set instead of independent cascade tiles.
- [x] If the full group cannot fit the current tile/time budget, prefer one of:
  keep coherent stale group, render legacy cascade texture-array fallback, or
  force a full group render with a visible budget warning.
- [x] Add a setting for maximum directional cascade stale age during camera
  motion, defaulting to a very small value such as 1-2 frames.
- [x] Treat a directional cascade that reaches the stale-age ceiling as forced
  fresh work. Do not publish an expired `StaleTile` allocation that the shader
  must reject into lit/contact fallback for a frame.

Acceptance criteria:

- [x] The lighting pass never mixes cascade 0 from generation N with cascade 1
  from generation N-1 for the same light/source.
- [x] Moving the camera may show a coherent stale shadow set for a bounded age,
  but not a per-cascade jittering set.

## Phase 5 - Add Shader-Side Stale Reprojection

- [x] Add `RenderedCascadeMatrices`, `RenderedCascadeSplits`, rendered bias,
  rendered receiver offsets, and stale age to the directional light uniforms
  when atlas cascades are stale.
- [x] Keep cascade selection based on the current view/cascade policy, but when
  the selected atlas slot is stale, project the receiver world position with
  the rendered sample matrix for that slot.
- [x] Add an overlap/validity test: if projected UV/depth falls outside the
  rendered tile's valid region or beyond a conservative depth range, fade to
  lit/legacy rather than sampling garbage.
- [x] Add a small edge fade near the stale tile border to hide missing coverage
  where the current cascade moved outside the rendered cascade footprint.
- [x] Limit stale reprojection to directional lights and to short age windows.
- [x] Add debug visualization modes for current cascade matrix, rendered stale
  matrix, stale age, and stale UV validity.

Acceptance criteria:

- [ ] Stale cascade sampling remains visually stable during small camera moves.
- [ ] Large camera jumps fail gracefully to lit/legacy/fresh render instead of
  projecting stale depth across unrelated world regions.

## Phase 6 - Evaluate Physical Atlas Reuse (Scroll First, Splat Last)

Directional cascade projections are orthographic and the fit is already
texel-snapped (`SnapCascadeCenterToTexels`), so camera translation usually
changes the rendered matrix by a pure whole-texel translation in light space.
The standard cheap reuse for that case is scrolling/toroidal update (copy the
still-valid overlap, render only the newly exposed strips), not depth
splatting: the overlap copy is exact, produces no holes, and needs no
min-depth heuristics.

- [ ] Detect translation-only deltas between the rendered payload matrix and
  the current cascade matrix (identical scale/rotation, offset by a whole
  number of texels).
- [ ] Prototype scroll reuse: copy the overlapping region from the rendered
  tile into the destination tile (quad blit or image copy within the atlas
  page), render casters only into the newly exposed strips, then publish the
  tile as a fresh generation with current matrices.
- [ ] Track scroll hit rate, copied vs rendered texel counts, GPU time, and
  barrier cost; compare against full cascade re-render on the same camera
  path.
- [ ] Only if scroll reuse proves insufficient, prototype the
  disabled-by-default depth splat/min-reduce reprojection pass
  (reconstruct world positions from old tile UV/depth, project into the new
  cascade matrix), tracking holes, disocclusion rate, and out-of-bounds
  samples. Keep it disabled unless it is measurably cheaper than direct
  render and visually better than coherent stale sampling; expected outcome
  is that splatting loses (holes, disocclusion, slope-depth error).

Acceptance criteria:

- [ ] Physical reuse has measured value before it becomes a runtime option.
- [ ] Scroll reuse never publishes a tile whose overlap region differs from a
  fresh render by more than depth-format quantization.

## Phase 7 - Vulkan Cascade Performance And Refresh-Pressure Fixes

- [x] Implement a real Vulkan grouped directional cascade atlas path, or make
  the fallback explicitly reported as `SequentialVulkanCascadeAtlas`.
- [x] Remove OpenGL-only capability gates from the Vulkan grouped decision path
  where Vulkan has equivalent dynamic viewport/scissor or multiview support.
- [x] Ensure grouped or atomic sequential rendering collects visible casters
  once per cascade group where possible, not once per cascade tile.
- [x] Verify texel snapping actually stabilizes the cascade content hash for
  small camera motion: `BuildShadowContentHash` hashes raw view/projection
  floats, so any non-snapped fit component or sub-texel jitter dirties every
  cascade every frame. Quantize the hashed matrix inputs to the snapped texel
  grid (and validate the fit is rotation-stable, e.g. sphere-based) so a
  slowly moving camera yields hash-stable far cascades.
- [x] Treat interactive directional camera-fit/content refreshes as critical
  atlas work, including hard tile-budget and time-budget bypass, so coherent
  cascade groups cannot sit stale forever under startup throttling such as
  `StartupMaxShadowTilesRenderedPerFrame=1`.
- [x] Add a per-cascade refresh cadence policy applied at request-build time
  (near cascade every frame; farther cascades every N frames, staggered;
  forced full-group refresh on light rotation or large camera jumps). Cadence
  must produce coherent stale groups through the Phase 2/4 payload path,
  never mixed generations.
- [x] Cache per-cascade caster visible sets keyed by the snapped cascade
  matrix/content hash so cadence-skipped and hash-stable cascades skip
  visibility collection entirely.
- [ ] Revisit primary command-buffer reuse for shadow cascade frames; current
  dirty reason `primary-frame-state` keeps CPU recording high even when GPU
  work is small (tracked in
  `docs/work/todo/rendering/optimization/vulkan-primary-command-recording-fast-path-todo.md`;
  keep the shadow-specific reuse criteria here).
- [x] Add profiler counters for cascade group CPU collection time, command
  generation time, command recording time, and GPU time.

Implementation note: the request builder now marks per-cascade atlas render
requests and cadence-skipped resident tiles as `SkipReason.StaleTileReused`.
Directional collection uses those marks to skip cascade viewport collection and
swap for stale/cache-hit cascades, while full dirty groups still use grouped
rendering and partial refreshes render only the dirty subset. CPU frame dumps
now include `DirectionalCascade.Group.CollectVisible`,
`DirectionalCascade.Cascade.CollectVisible`, and the existing shadow atlas
render/diagnostic scopes; GPU timing attribution remains through the render
pipeline GPU profiler and should be validated in Phase 8 captures.

Acceptance criteria:

- [ ] Moving the camera with one four-cascade directional light does not create
  five expensive visibility/command-recording passes unless diagnostics
  explicitly report why.

## Phase 8 - Tests And Validation

- [x] Add a regression test (source-scrape or instrumentation) that
  `RenderScheduledTiles` and everything it calls access no planner-owned
  collections (`_requests`, pending plan, `_frameAllocations`,
  `_currentAllocations`) and stamp completions only from plan data.
- [x] Add a deterministic unit/source test that stale directional atlas slots
  use rendered sample payloads rather than current slice payloads.
- [x] Add a test that a freshly rendered directional cascade completion can be
  committed before the lighting pass observes light uniforms.
- [x] Add a test that cascade groups are published atomically by generation.
- [ ] Add a camera-move-then-stop automated editor scenario that captures N
  frames before/after stopping and checks that no one-frame atlas/matrix
  mismatch is logged.
- [x] Validate with Unit Testing World Vulkan: moving camera, stopping camera,
  and quick camera jump. Warm MCP captures after the present/exposure fix were
  visually stable for repeated move/stop cycles; the expired-stale follow-up
  move/stop run showed forced-fresh requests while moving and zero stale/mixed
  directional cascade counters in the stopped tail. The later
  render-on-demand/exposure follow-up verified JSONC startup render-on-demand,
  stable cached HDR/final post-process captures, and exposure-hold log lines
  under `Build/_AgentValidation/20260702-194022-render-on-demand-exposure`.
  The latest follow-up preserves coherent rendered samples through pending
  `ContactOnly` directional cascade allocations and moves exposure holding onto
  `ColorGradingSettings` itself. The final MCP run under
  `Build/_AgentValidation/20260702-202000-dir-cascade-stop-flicker` produced
  byte-identical non-black stopped-frame captures after camera movement, zero
  descriptor fallbacks, zero Vulkan validation errors, zero dropped frame ops,
  and directional audit rows with `decision=RenderedSample`,
  `fallback=None`, `sampleable=True` after startup. A later no-arbitrary-hold
  run under `Build/_AgentValidation/20260702-222600-stable-vulkan-resource-plan`
  kept `AutoExposureTex` bounded during a five-second scripted move, showed no
  post-startup forward atlas disable rows, preserved same-slot pending cascade
  refreshes as sampleable `StaleTile`, and stopped repeated allocation of
  `HDRSceneTex`, `AutoExposureTex`, and `FinalPostProcessOutputTexture`.
- [ ] Validate GPU time, render-thread CPU time, frame ops, and dropped frame
  count before and after each phase.

Acceptance criteria:

- [x] No visible one-frame shadow snap occurs after camera motion stops in the
  latest warm MCP validation. User confirmation in the normal F5 path is still
  needed because MCP screenshots block the render loop while capturing.
- [x] Directional cascade shadows are either fresh or coherently stale in the
  latest audited MCP validation; user confirmation in the interactive F5 path
  is still needed for the reported drag case.
- [ ] Movement-related frame drops have an attributed, bounded cost in the
  profiler.
