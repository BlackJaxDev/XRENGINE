# Shadow Relevance Scoring TODO

Status: proposed phased TODO

Source context:
- [Shadow Atlas Region Organization And Solver Performance TODO](shadow-atlas-region-organization-todo.md)
- [Dynamic Shadow Atlas And LOD Allocation TODO](dynamic-shadow-atlas-lod-todo.md)
- [Dynamic Shadow Atlas LOD Plan](../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [Shadow Filtering VSM/EVSM Plan](../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/RenderTree/` (visibility/culling)

## Goal

Drive shadow request submission, resolution selection, and skip decisions from a per-light **relevance score** computed against the player's actual on-screen view (desktop camera + both VR eyes + any active mirror/probe cameras), so that:

- Lights whose shadows do not affect any pixel the player can see are skipped or demoted to the minimum resolution.
- On-screen lights receive resolution proportional to their screen-space footprint and importance, instead of all eligible lights competing on flat priority.
- The atlas budget is spent on shadows the player will actually perceive.

This sits **upstream of the allocator**. The region-organization TODO makes the allocator faster; this TODO makes the inputs to the allocator smarter.

## Dependencies

- Builds on the **persistent resident table** introduced in Phase 5 of [shadow-atlas-region-organization-todo.md](shadow-atlas-region-organization-todo.md). Sticky relevance decisions need cross-frame state to avoid flicker, and the resident table is the natural carrier.
- Reuses the active-camera set already passed into `ShadowAtlasManager.BeginFrame(IRuntimeRenderWorld, ReadOnlySpan<XRCamera>)`.
- Reuses existing forward+ light culling and visibility passes; this TODO does not introduce a new full visibility pass.

## Target Outcome

- A `ShadowRelevanceScore` is computed once per light per frame from a small, well-defined set of inputs.
- `ShadowMapRequest.RequestedResolution` is derived from the relevance score, clamped to `MinTileResolution`/`MaxTileResolution`.
- Lights whose receivers are entirely outside the camera-set frustum are submitted at minimum resolution or skipped via an explicit `SkipReason.NotRelevant`.
- Cascades that fall entirely outside the camera-set frustum are demoted or skipped per cascade, not per light.
- Stereo VR uses the union of both eyes (plus mirror/probe cameras) so neither eye drops a shadow the other can see.
- Score-driven resolution decisions use hysteresis to avoid per-frame popping at ladder boundaries.
- Editor-pinned lights bypass relevance demotion, matching the existing pin policy.
- The relevance pass adds no per-frame heap allocations after warmup.

## Non-Goals

- Do not change the allocator's region packing, stats, or page logic — that is the region-organization TODO.
- Do not introduce a new full scene visibility traversal. Reuse the visibility set the main render already produces.
- Do not change shadow filtering (VSM/EVSM, PCF) or bias math.
- Do not gate this work on virtual shadow maps. Keep score outputs and metadata compatible with a later VSM direction.
- Do not alter the legacy non-atlas shadow path.

## Behavioral Invariants

- Relevance is a function of **receivers in the camera set's view**, not of caster on-screen presence. A caster behind the camera that shadows an on-screen receiver is fully relevant.
- A light flagged `EditorPinned` is never demoted or skipped by relevance scoring under normal pressure. A hard memory cap miss may still displace it via the allocator path, with a logged warning.
- `ShadowAtlasFrameData.Generation` semantics are unchanged. Resolution changes driven by relevance only bump the generation if they cause a layout change.
- Stale-tile fallback continues to apply: a relevance-demoted tile that retains its previous region may publish `SkipReason.StaleTileReused`.
- Relevance evaluation must be deterministic for identical camera state, light state, and visibility set.
- Headless/dedicated-server paths must short-circuit the relevance pass to a no-op (no cameras, no shadows).

## Phase 0: Branch, Baseline, And Acceptance Tests

Goal: isolate the work and capture current shadow submission behavior.

- [ ] Create a dedicated branch, for example `shadow-relevance-scoring`.
- [ ] Confirm the dependency chain. If [shadow-atlas-region-organization-todo.md](shadow-atlas-region-organization-todo.md) Phase 5 (persistent resident table) is not yet complete, either:
  - [ ] block this TODO until it lands, or
  - [ ] add a temporary per-light hysteresis cache in `ShadowAtlasManager` and migrate to the resident table later. Document the chosen path.
- [ ] Capture baseline metrics for representative scenes:
  - [ ] number of submitted shadow requests per frame
  - [ ] sum of requested tile texels per frame
  - [ ] number of off-screen lights still receiving non-minimum resolution
  - [ ] number of cascades fully outside the camera frustum still receiving full resolution
  - [ ] shadow render cost (timer or `MaxShadowTilesRenderedPerFrame` saturation)
- [ ] Add a relevance-pass benchmark harness that fabricates a synthetic scene with N lights, M visible receivers, and a configurable camera set, with no GPU work.
- [ ] Add or extend tests that lock down current behavior so regressions are visible:
  - [ ] all eligible lights produce shadow requests today
  - [ ] no skip path currently uses a `NotRelevant` reason
  - [ ] cascades are submitted as a uniform set per directional light today
- [ ] Capture a GC-allocation budget for the relevance pass using `GC.GetAllocatedBytesForCurrentThread()` deltas.

## Phase 1: Camera Relevance Set

Goal: define the authoritative set of cameras whose visibility drives relevance.

- [ ] Introduce a `ShadowRelevanceCameraSet` that is built once per frame and reused.
- [ ] Inputs to the camera set:
  - [ ] desktop main camera(s)
  - [ ] both VR eye cameras when XR is active
  - [ ] active mirror/preview cameras flagged as relevance-contributing
  - [ ] optional reflection/probe cameras flagged as relevance-contributing
- [ ] Each camera contributes:
  - [ ] world-space frustum
  - [ ] near/far range
  - [ ] viewport pixel size
  - [ ] an "importance" weight (main camera = 1.0, mirrors/probes = configurable, default lower)
- [ ] Provide a fast union-frustum query (point/sphere/AABB) without allocating.
- [ ] Wire `ShadowAtlasManager.BeginFrame(IRuntimeRenderWorld, ReadOnlySpan<XRCamera>)` to construct or accept the relevance camera set.
- [ ] Add tests:
  - [ ] desktop-only set
  - [ ] VR stereo set (both eyes contribute)
  - [ ] VR + mirror set
  - [ ] empty set (headless) produces no relevance contributions and short-circuits the pass

## Phase 2: Receiver-In-View Aggregation

Goal: collect, per visible receiver, which lights affect it, without a new visibility pass.

- [ ] Reuse the existing forward+/visibility pass that already tags renderables with affecting lights.
- [ ] Per frame, build a per-light bitset or counter: "is at least one receiver of this light visible to any camera in the relevance set?"
- [ ] Per light, accumulate a screen-space AABB built from the union of its visible receivers' projected bounds across the relevance set's cameras.
  - [ ] Project each receiver AABB into each camera's NDC, clip to viewport.
  - [ ] Union the results, weighted by camera importance.
- [ ] Cost guardrail: per-light receiver iteration is bounded by the existing per-light receiver list. If that list is unbounded for a given light type, gate aggregation behind a max-receivers-per-light setting and log when truncated.
- [ ] Add tests:
  - [ ] receiver fully off-screen, light marked not-in-view
  - [ ] receiver partially on-screen, screen-space AABB clamped correctly
  - [ ] stereo VR with receiver visible only to one eye still flags the light as in-view
  - [ ] truncated receiver list still produces a valid (conservative) screen-space AABB

## Phase 3: Caster-To-Visible-Receiver Coupling

Goal: confirm the light's casters can actually shadow on-screen receivers, not just that the light overlaps an on-screen receiver.

- [ ] For each light flagged in-view by Phase 2, run a cheap caster/receiver coupling test:
  - [ ] directional: cascade slice AABB ∩ visible-receiver bounds
  - [ ] spot: spot frustum ∩ visible-receiver bounds
  - [ ] point: light sphere ∩ visible-receiver bounds, per face for grouped point allocations
- [ ] Output per light (and per cascade / per face when applicable):
  - [ ] `HasVisibleReceiver` flag
  - [ ] `VisibleReceiverScreenAreaPixels` (sum or AABB area, deterministic choice)
- [ ] **Do not** filter by caster on-screen visibility. Off-screen casters that shadow on-screen receivers must remain relevant. Add a regression test for this case explicitly.
- [ ] Add tests:
  - [ ] caster off-screen, receiver on-screen → light remains relevant
  - [ ] caster on-screen, receiver off-screen → light is not relevant
  - [ ] partial cascade coverage: only cascades that intersect on-screen receivers stay at full resolution

## Phase 4: Relevance Score And Resolution Mapping

Goal: convert per-light/per-cascade/per-face relevance signals into a `RequestedResolution`.

- [ ] Define `ShadowRelevanceScore` inputs:
  - [ ] receiver pixel area (Phase 2)
  - [ ] caster-receiver coupling area (Phase 3)
  - [ ] light intensity / contribution estimate
  - [ ] light priority and `EditorPinned`
  - [ ] distance / angular size to nearest relevance camera
  - [ ] caster motion / dirtiness flag (static caster + static receiver = candidate for cached reuse)
- [ ] Compute a scalar score with documented weights. Weights live in settings and have safe defaults.
- [ ] Map score → tile resolution via the existing `MinTileResolution`/`MaxTileResolution` ladder.
- [ ] Editor-pinned lights bypass demotion; their score sets a floor, not a ceiling.
- [ ] When score → 0 (no visible receiver) submit with `SkipReason.NotRelevant` and the minimum allowed resolution if the request is still required for fallback, otherwise skip submission entirely. Document which path applies per light type.
- [ ] Add tests:
  - [ ] score → resolution mapping is deterministic
  - [ ] pinned light keeps its floor under low score
  - [ ] zero-score light produces `SkipReason.NotRelevant`
  - [ ] equal scores produce equal resolutions for equivalent requests

## Phase 5: Hysteresis And Stability

Goal: prevent per-frame resolution popping at ladder boundaries.

- [ ] Per-light (and per-cascade, per-face) sticky resolution stored in the persistent resident table from the region-organization TODO Phase 5, or in the temporary cache decided in Phase 0.
- [ ] Promotion vs demotion thresholds use different score margins (classic hysteresis band).
- [ ] Demotion is delayed by N frames of sub-threshold score before applying. N is a setting.
- [ ] Promotion may apply immediately for newly visible receivers, since the worse failure mode is sustained low resolution on suddenly important lights.
- [ ] Pinned lights ignore the demotion delay (they cannot demote anyway) and ignore the promotion delay.
- [ ] Add tests:
  - [ ] score oscillating across a ladder boundary produces stable resolution
  - [ ] sustained drop demotes after the configured frame count
  - [ ] sudden visibility promotes within one frame
  - [ ] pinned light is unaffected by hysteresis logic

## Phase 6: Stale-Tile And Skip Interaction

Goal: integrate relevance decisions cleanly with existing skip and stale-tile paths.

- [ ] Define interaction matrix:
  - [ ] relevance-demoted + clean tile + same region available → reuse, no re-render
  - [ ] relevance-demoted + dirty tile + previous region available → publish `SkipReason.StaleTileReused`
  - [ ] relevance-zero + previous region available → publish `SkipReason.NotRelevant`, may keep stale tile for one-frame fallback
  - [ ] relevance-zero + no previous region → do not submit
- [ ] Decide one-frame popping policy when a previously-skipped light becomes relevant: either accept a one-frame stale render or pre-allocate a minimum-resolution placeholder. Document the chosen policy.
- [ ] Add tests covering each row of the interaction matrix.

## Phase 7: Cascade-Level And Face-Level Granularity

Goal: drive relevance per cascade and per point face, not just per light.

- [ ] Compute relevance per cascade for directional lights using the cascade's slice AABB.
- [ ] Cascades fully outside the camera-set frustum demote to minimum resolution or skip with `SkipReason.NotRelevant`.
- [ ] Compute relevance per face for point lights using the face frustum.
- [ ] Faces with no on-screen receivers demote or skip independently.
- [ ] Preserve the grouped allocation paths from the region-organization TODO Phase 7 — relevance changes the *requested* resolution per cascade/face but not the grouping decision.
- [ ] Add tests:
  - [ ] far cascade off-screen, near cascades on-screen → only far demoted
  - [ ] point light with three of six faces facing on-screen receivers → other three demoted
  - [ ] grouped 2x2 cascade pack still occurs when all four are relevant

## Phase 8: VR / Stereo Specifics

Goal: ensure stereo and mirror-display paths do not drop shadows that one eye or mirror can see.

- [ ] Verify the camera set always includes both eyes when XR is active, even if a single eye queries relevance independently elsewhere.
- [ ] Verify mirror/preview cameras can be flagged as relevance-contributing without forcing them on by default (they may be lower importance).
- [ ] Add a stereo regression test where a receiver visible only to the right eye keeps its light at full resolution.
- [ ] Add a mirror-display test where a receiver visible only on the desktop mirror keeps its light at the mirror's importance-weighted score.

## Phase 9: Diagnostics And Editor Visibility

Goal: make relevance decisions observable.

- [ ] Per-light diagnostic record: score inputs, computed score, mapped resolution, hysteresis state, skip reason.
- [ ] Editor inspector view (ImGui path) for the active relevance camera set and per-light score breakdown.
- [ ] Counters (plain `int`/`long`/`uint` fields, no boxing or per-frame dictionaries):
  - [ ] lights demoted by relevance
  - [ ] lights skipped with `NotRelevant`
  - [ ] lights promoted within one frame
  - [ ] cascades demoted independently
  - [ ] point faces demoted independently
- [ ] Rate-limited debug logging for promotions/demotions in editor builds.
- [ ] Update docs if any new settings, task entries, launch flags, or editor-visible diagnostics are added.

## Phase 10: Validation And Closeout

Goal: prove the relevance pass reduces shadow texel cost without visible regressions.

- [ ] Run targeted tests:
  - [ ] new relevance fixture
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Smoke validation in `Editor (Unit Testing World)`:
  - [ ] desktop scene with many off-screen lights → confirm submitted texel count drops vs. baseline
  - [ ] VR scene with stereo eyes → confirm no per-eye popping
  - [ ] mirror display enabled → confirm mirror-only-visible lights remain at appropriate score
  - [ ] camera rapidly turning across boundary → confirm hysteresis prevents popping
- [ ] Compare baseline and final metrics from Phase 0:
  - [ ] submitted requests per frame
  - [ ] sum of requested tile texels per frame
  - [ ] off-screen lights at non-minimum resolution
  - [ ] shadow render cost
- [ ] Confirm hot-path allocation expectations:
  - [ ] no per-frame allocations in the relevance pass after warmup
  - [ ] no LINQ or capturing closures in the score path
  - [ ] no avoidable string formatting in per-frame logs
- [ ] Update related docs and TODOs:
  - [ ] [shadow-atlas-region-organization-todo.md](shadow-atlas-region-organization-todo.md) cross-link
  - [ ] [dynamic-shadow-atlas-lod-todo.md](dynamic-shadow-atlas-lod-todo.md) if LOD inputs overlap
  - [ ] `docs/work/README.md` index
  - [ ] rendering architecture notes if public behavior or settings changed
- [ ] Split any deferred follow-ups (virtual shadow maps, GPU-driven relevance, ray-traced relevance occlusion) into focused TODO docs.

## Final Task

- [ ] Merge the dedicated `shadow-relevance-scoring` branch back into `main` after all phases are complete, validation has passed, docs are updated, and any follow-up TODOs have been filed.
