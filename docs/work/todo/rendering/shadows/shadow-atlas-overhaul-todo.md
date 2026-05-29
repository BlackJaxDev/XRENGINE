# Shadow System Overhaul TODO

Status: active master plan, refreshed 2026-05-28.

This document is the single source of truth for the shadow-system overhaul.
It absorbs the previously separate atlas/LOD allocation, VSM/EVSM filtering,
and contact-shadow optimization TODOs. The workstreams below are intended to
be worked in parallel where dependencies allow; cross-cutting design rules and
the immediate atlas bugs are at the top because everything else assumes them.

Primary files:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowMapResources.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/LocalShadowFrustumRelevance.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent*.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`
- `Build/CommonAssets/Shaders/Snippets/ShadowMomentEncoding.glsl`
- `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`
- `XREngine.UnitTests/Rendering/PointShadowAtlasStabilityTests.cs`
- `XREngine.UnitTests/Rendering/LocalShadowFrustumRelevanceTests.cs`
- `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`

Related docs:

- [Dynamic Shadow Atlas LOD Plan](../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [Shadow Filtering VSM/EVSM Plan](../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)
- [Shadow Resource Migration Audit](../design/shadow-resource-migration-audit.md)
- [Post-v1 Advanced Shadow Features Plan](../design/post-v1-advanced-shadow-features-plan.md)

## Workstream Index

1. [Current Runtime Snapshot](#current-runtime-snapshot)
2. [Immediate Bugs](#immediate-bugs)
3. [Workstream A: Atlas Allocator And Relevance Overhaul](#workstream-a-atlas-allocator-and-relevance-overhaul)
4. [Workstream B: Dynamic Atlas And LOD Allocation (Remaining Phases)](#workstream-b-dynamic-atlas-and-lod-allocation-remaining-phases)
5. [Workstream C: VSM And EVSM Shadow Filtering](#workstream-c-vsm-and-evsm-shadow-filtering)
6. [Workstream D: Contact Shadow Optimizations](#workstream-d-contact-shadow-optimizations)
7. [Cross-Cutting Policy Decisions](#cross-cutting-policy-decisions)
8. [Closeout Checklist](#closeout-checklist)

## Current Runtime Snapshot

Implemented:

- Single-pass request bucketing by atlas kind and encoding.
- Fixed-level buddy allocator buckets.
- Direct prior-slot reservation and prior-placement sort tiebreaks.
- Sticky sub-rect reuse on downsize and deferred in-place upgrade attempts.
- Resident allocation table with TTL reuse.
- Relevance/priority-driven sticky demotion state.
- Split LOD cooldown constants for voluntary changes and forced downsize
  re-promotion.
- `NotRelevant` stale-tile reservation after live allocations.
- O(1)-style published allocation lookup through `ShadowAtlasFrameData`.
- Multi-page settings are honored per atlas family/encoding.
- Depth-only spot and point atlas eligibility; moment-encoded spot/point lights
  stay on standalone maps.
- Per-face point requests, per-face point relevance mask, and point face
  resolution demotion from camera-face alignment.
- Published directional cascade and point-face group metadata.
- Directional atlas render path with sequential tile rendering and an optional
  grouped viewport/scissor path.
- Spot/point/directional standalone moment-map rendering and receivers
  (`Depth`, `Variance2`, `ExponentialVariance2`, `ExponentialVariance4`).
- Local shadow-frustum relevance publishes `NotRelevant` skip metadata for
  spot atlas requests and point atlas faces; standalone spot moment renders
  and mip regeneration are skipped when the spot frustum is not relevant.

Not complete:

- Directional atlas performance is not equivalent to the legacy layered cascade
  path.
- Directional VSM/EVSM selection is internally inconsistent and can disable
  directional shadows (see [Immediate Bugs](#immediate-bugs)).
- Broad receiver-aware relevance scoring is still missing. The allocator's
  `ResolveRelevanceScore` currently derives score from request priority.
- Directional cascade relevance is not independent. Cascades are always
  submitted by active cascade index and static priority.
- Point face group pre-reservation is not implemented. Groups are discovered
  after independent allocation, so co-location is opportunistic.
- Retry solving still resets allocator state and starts over after demotion.
- Fragmentation-triggered compaction is not implemented. `RequestRepack()` is
  manual only.
- Anchor slots are not implemented.
- Diagnostics do not yet expose score inputs, grouped-render fallback reasons,
  demotion counts, reserve misses, churn counts, or fragmentation ratios.
- Unified GPU `ShadowAtlasTile` SSBO publish, tile-aware separable blur for
  atlas tiles, atlas moment-tile mip generation, and warmup-free allocation in
  submit/solve/publish remain open.
- Moment encodings currently use standalone shadow maps for local lights and
  the directional legacy path. Full VSM/EVSM atlas and cascaded-atlas support
  is intentionally tracked as future work under Workstream C.
- The focused shadow-atlas tests are not currently green in this worktree.
  Several failures are caused by `RuntimeShaderServices.Current` not being
  configured in test setup; several are direct atlas assertion failures and
  need triage.

## Immediate Bugs

### Directional Atlas Halves Framerate

Observed question: directional lights can roughly halve framerate when
`UseDirectionalShadowAtlas` is enabled.

Current likely causes:

- Legacy directional cascades can render all cascades through one layered
  texture-array pass when the selected backend is instanced or geometry
  layered rendering.
- Atlas mode only keeps that one-pass property when all active cascade tiles
  publish a coherent group and the OpenGL viewport/scissor-index path is
  supported. If the group is missing or the GPU capability checks fail, atlas
  mode falls back to one `viewport.Render(...)` per cascade tile.
- Directional dirty requests are treated as critical. When the dirty reason
  matches any of `FirstSubmission`, `ContentChanged`,
  `ProjectionOrCameraFitChanged`, `DynamicLight`, `ReuseDisabled`, or
  `NeverRendered`, the request bypasses the per-frame tile budget through
  `ShouldRenderDirectionalRefreshPastBudget` / `HasCriticalDirtyReason`.
- Dynamic directional lights, unstable cascade fits, or content hashes that
  change every frame therefore force every active cascade through the atlas
  render path every frame.
- The atlas path preserves existing page contents with render/crop rectangles.
  That is correct for atlases, but it makes sequential fallback especially
  expensive compared with a full layered cascade texture render.

Fix plan:

- [ ] Add a directional atlas frame diagnostic that records, per light:
  requested cascades, resident cascades, dirty cascades, rendered cascades,
  grouped render attempted, grouped render succeeded, selected cascade backend,
  fallback reason, elapsed shadow time, and whether critical budget bypass was
  used.
- [ ] Add a warning when directional atlas mode falls back to sequential
  cascade rendering while legacy layered rendering is available.
- [ ] Make a same-page 2x2 cascade reservation path for equal-resolution
  directional cascades before independent allocation. Do not rely on
  post-allocation group discovery for performance-critical directional lights.
- [ ] Ensure grouped atlas rendering remains the default on GL 4.6 hardware
  with viewport/scissor index support.
- [ ] Revisit directional critical-refresh bypass. First render may bypass the
  budget; steady-state projection jitter should not force all cascades every
  frame.
- [ ] Add profiler counters
  `ShadowAtlas.Directional.SequentialFallbackFrames` and
  `ShadowAtlas.Directional.GroupedFrames`.
- [ ] Acceptance: with a static camera and one 4-cascade directional light,
  atlas mode renders no more cascade passes per frame than legacy after the
  first warmup frames.
- [ ] Acceptance: with grouped rendering supported, atlas-on framerate is
  within 10 percent of atlas-off legacy layered cascades for the Unit Testing
  World.

### Directional VSM/EVSM Does Not Work

Observed question: VSM and EVSM directional shadows do not work.

There are two current contract mismatches.

Atlas-on mismatch:

- `Lights3DCollection.SubmitShadowAtlasRequest` hardcodes the request
  encoding to `EShadowMapEncoding.Depth` for every atlas request (directional,
  spot, point). Spot and point are saved by their explicit depth-only atlas
  gates; directional is not.
- `DirectionalLightComponent.UsesDirectionalShadowAtlasForCurrentEncoding`
  does not gate atlas use to depth encoding. It only checks
  `DemotionReason != SkipReason.UnsupportedEncoding`, while
  `SpotLightComponent.UsesSpotShadowAtlasForCurrentEncoding` and
  `PointLightComponent.UsesPointShadowAtlasForCurrentEncoding` explicitly
  require `Encoding == EShadowMapEncoding.Depth`.
- `VPRC_LightCombinePass.BindDirectionalAtlasShadows` later asks the atlas
  for the directional light's resolved encoding. For VSM/EVSM this asks for a
  moment atlas page that was never submitted.
- Because `useDirectionalShadowAtlas` remains true, the deferred pass also
  avoids binding the legacy `ShadowMap` / `ShadowMapArray` path. The shader
  uniform `ShadowMapEncoding` is set to VSM/EVSM, but
  `materialProgram.Sampler("ShadowMapArray", DummyShadowMapArray, ...)` is
  bound instead of the cascade texture. Result: no valid directional shadow
  source.

Atlas-off or legacy mismatch:

- When atlas is disabled, `ShouldRenderLegacyDirectionalShadowMap` treats
  moment directional shadows as a primary single-map path and sets
  `renderCascades = false`.
- When atlas is enabled, the same method unconditionally sets
  `renderCascades = false` regardless of encoding, because cascades are
  expected to come from the atlas.
- The deferred light combine pass still enables cascaded directional sampling
  whenever the camera requests cascades, `EnableCascadedShadows` is true, and
  either the atlas is in use or `CascadedShadowMapTexture` exists.
- In the VSM/EVSM + atlas-enabled state, moment shaders sample
  `ShadowMapArray`, but the cascade moment array was intentionally not
  rendered for that frame, and the dummy texture is bound. Result: stale,
  empty, or invalid cascade moment sampling instead of the primary moment map.

Near-term fix plan:

- [x] Decide the v1 contract: directional atlas is depth-only until a moment
  atlas is implemented.
- [x] Change `UsesDirectionalShadowAtlasForCurrentEncoding` to match
  spot/point behavior: return true only when the resolved directional
  sampling encoding is `Depth`.
- [x] When the resolved directional encoding is VSM/EVSM, force the legacy
  directional path even if `UseDirectionalShadowAtlas` is enabled.
- [x] Make the deferred pass disable cascaded directional sampling for the
  primary-single-map moment path, or render and validate moment cascade
  arrays.
- [x] Add a test for `UseDirectionalShadowAtlas = true` plus directional VSM:
  atlas must be bypassed and the legacy moment map must be bound.
- [x] Add a test for directional EVSM with cascades enabled: either cascaded
  sampling is disabled and the primary map is sampled, or every cascade
  moment layer is rendered and sampled intentionally.

Longer-term option (tracked under [Workstream C](#workstream-c-vsm-and-evsm-shadow-filtering)):

- [ ] Implement moment-encoded directional atlases end to end: submit
  requests with the resolved encoding, allocate moment atlas pages, render
  `Frag_ShadowMomentOutput` into the atlas sampling texture with a separate
  raster depth attachment, generate mipmaps/blur for moment atlases, bind the
  same encoding in receivers, and validate VSM/EVSM bias/moment depth against
  atlas UVs.

### Point VSM/EVSM Does Not Work

Observed issue: point lights using `Variance2`, `ExponentialVariance2`, or
`ExponentialVariance4` can fail even though spot and directional standalone
moment maps work.

Current cause:

- Point-light receivers compare normalized radial light distance.
- The shared `PointLightShadowDepth.fs` shader writes radial moments correctly,
  but point shadow caster material variants for cutout/uber materials still
  wrote plain radial depth, so VSM/EVSM receivers interpreted missing moment
  channels as invalid moments.
- Some point caster variants also used the generic projected-depth moment
  helper. That is correct for spot/directional maps, but wrong for cubemap
  point shadows.

Fix status:

- [x] Add `XRENGINE_WritePointShadowCasterDepth(...)` to
  `ShadowMomentEncoding.glsl`; it encodes normalized radial depth with the
  active shadow encoding.
- [x] Update point shadow caster variants in common alpha/cutout shaders and
  the uber shader to use the radial point moment writer.
- [x] Make geometry-shader point caster variants use the source material's
  point shadow fragment variant when one exists, preserving alpha discards and
  moment encoding.
- [ ] Add/refresh a visual validation scene for point VSM, EVSM2, and EVSM4
  with masked casters and receivers crossing cube-face boundaries.

### Moving Point/Spot Atlas Shadows Reuse Stale Tiles

Observed issue: dragging or animating point/spot lights while their depth
shadows are in the atlas can reuse an old atlas tile instead of updating the
shadow immediately.

Current cause:

- The atlas request content hash already includes `LightComponent.MovementVersion`.
- A moved local light is therefore marked dirty, but `ShadowFallbackMode.StaleTile`
  was still allowed for point and spot dirty refreshes before the new tile was
  rendered.
- The published allocation could advertise the previous tile as sampleable,
  making the receiver display a shadow from the old light transform.

Fix status:

- [x] Classify recent point/spot movement as
  `ProjectionOrCameraFitChanged`, not generic `LightOrSettingsChanged`.
- [x] Disallow stale-tile fallback for local atlas requests whose dirty reason
  is a projection/camera-fit change.
- [x] Keep stale-tile fallback available for non-movement local atlas pressure
  cases such as relevance misses and low-priority contention.
- [ ] Add an editor smoke test: drag and animate one point light and one spot
  light with atlas mode enabled, confirm shadow tiles refresh with the light.

## Workstream A: Atlas Allocator And Relevance Overhaul

### A.1 Make Tests Trustworthy Again

- [ ] Configure runtime shader services or replace light construction helpers
  so shadow-atlas allocator tests do not fail before the allocator is
  exercised.
- [ ] Fix current direct assertion failures:
  - `SolveAllocations_ReusesResidentTileAfterTransientMissingRequest`
  - `SolveAllocations_ReusesNotRelevantStaleTileWhenRegionRemainsFree`
  - `Submit_WhenQueueIsFullPublishesQueueOverflowDiagnostic`
- [ ] Split pure solver tests from render-path tests. Solver tests should not
  need shader or renderer services.
- [ ] Add regression tests for directional VSM/EVSM atlas bypass.
- [ ] Add regression tests for directional grouped atlas render selection.

Validation targets:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter PointShadowAtlasStabilityTests
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter LocalShadowFrustumRelevanceTests
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
```

### A.2 Receiver-Aware Relevance

Current local relevance is frustum-based and tactical:

- Spot lights can submit `SkipReason.NotRelevant`.
- Point faces can submit `SkipReason.NotRelevant`.
- Point face resolution can demote from camera-face alignment.

Still needed:

- [ ] Compute a `ShadowRelevanceScore` per request, not just request priority.
- [ ] Reuse existing visible/culling data. Do not add a new broad visibility
  pass for v1.
- [ ] Score from receivers in view, not from caster visibility. Off-screen
  casters that shadow visible receivers must remain relevant.
- [ ] Directional cascades: score each cascade from the cascade slice and the
  visible receiver bounds it can affect.
- [ ] Spot lights: score cone/frustum intersection, projected on-screen
  influence, receiver overlap, distance, brightness, and stale-tile age.
- [ ] Point faces: combine face frustum receiver overlap with the existing
  camera-face alignment estimate.
- [ ] Map score to the existing power-of-two tile ladder.
- [ ] Submit zero-score cascades/faces as `NotRelevant` only when receiver
  fallback behavior is defined.
- [ ] Preserve VR behavior by scoring against both eyes and any explicitly
  contributing mirror camera.

Acceptance:

- [ ] Static off-screen local lights with no visible receivers no longer
  consume full-resolution atlas tiles.
- [ ] A visible receiver shadowed by an off-screen caster keeps the caster's
  tile relevant.
- [ ] Far directional cascades that do not affect visible receivers demote or
  skip independently of near cascades.

### A.3 Directional Group Reservation

Current grouping is discovered after allocation. That is not strong enough for
directional performance.

- [ ] Add a pre-allocation path for active cascades of one directional light.
- [ ] Prefer a deterministic 2x2 pack when four cascades share a resolution.
- [ ] Support partial groups when fewer cascades are active or relevant.
- [ ] If a grouped pack cannot fit, demote lower-relevance cascades before
  falling back to independent sequential tiles.
- [ ] Publish a reason when grouped allocation is not possible.
- [ ] Keep receiver binding conservative: enable atlas sampling only when
  every required cascade tile is sampleable or has an explicit fallback.

Acceptance:

- [ ] Four equal-resolution cascades allocate as one page-coherent group.
- [ ] Grouped atlas rendering issues one render submission on capable GL 4.6
  hardware.
- [ ] If one far cascade demotes, the remaining group remains deterministic
  and non-overlapping.

### A.4 Incremental Solver Retry

Current solver retry demotes a victim, resets all allocators for the state,
and replays allocations. That is deterministic enough for some cases, but it
churns work under contention.

- [ ] Track reserved allocations per balanced entry.
- [ ] On placement failure, demote one selected entry and free only affected
  reservations.
- [ ] Prefer prior placement when re-reserving demoted entries.
- [ ] Bound retry iterations and log a solver fallback if the incremental
  path cannot converge.
- [ ] Keep the existing full reset path as a debug fallback until the
  incremental path has coverage.

Acceptance:

- [ ] Adding a low-priority light to a full atlas does not move unrelated
  high-priority residents.
- [ ] Median and 99th percentile solve time do not regress versus the current
  reset/retry path.

### A.5 Point Face Group Pre-Reservation

Current point-face grouping is metadata over independently allocated faces.
That allows grouped rendering when faces happen to share a page, but it does
not reserve a heterogeneous mosaic as a unit.

- [ ] Add `TryReservePointLightFaceGroup(LightId, state, faceSizes[6])`.
- [ ] Sort faces by relevance, then assign deterministic buddy sub-tiles
  inside the smallest containing power-of-two block.
- [ ] Keep point face atomicity partial: individual faces may still demote,
  skip, or evict when budget requires it.
- [ ] Update group metadata to carry each member's own `InnerPixelRect` and
  `UvScaleBias`, ordered by face index.
- [ ] Validate grouped rendering with different face resolutions.

Acceptance:

- [ ] One full-resolution face plus five quarter-resolution faces can form a
  single page-coherent point-face group.
- [ ] Neighbor face sampling remains metadata-driven; no receiver assumes
  equal face sizes.

### A.6 Fragmentation And Repack Policy

Current repack support is manual through `RequestRepack()`.

- [ ] Add per-page fragmentation metrics: `FreeTexelCount`,
  `LargestFreeRect`, and a fragmentation ratio.
- [ ] Trigger compaction only on failed allocation, explicit editor request,
  or sustained high fragmentation.
- [ ] Increment `ShadowAtlasFrameData.Generation` on repack.
- [ ] Publish repack reason and affected atlas family/encoding.
- [ ] Do not repack during the same frame a receiver is using stale published
  metadata.

### A.7 Anchor Slots

Anchor slots are still useful, but only after relevance and grouped
directional allocation are stable.

- [ ] Add `ShadowAnchorLightCount` or a per-kind equivalent.
- [ ] Solve top-K requests by `(EditorPinned, Priority, RelevanceScore)`
  first.
- [ ] Reserve their prior slots before ordinary requests.
- [ ] Do not allow non-anchor requests to displace anchors within a frame.
- [ ] Keep the default small so anchors do not starve dense scenes.

### A.8 Diagnostics And Editor Visibility

- [ ] Expose per-light atlas diagnostics in ImGui: score, score inputs,
  requested resolution, allocated resolution, skip reason, fallback mode,
  resident age, last rendered frame, page, rect, and encoding.
- [ ] Expose directional grouped-render state: selected backend, fallback
  reason, grouped/ungrouped pass count, and time.
- [ ] Count reserve hits, reserve misses, demotions, deferred upgrades,
  evictions, repacks, page creations, failed allocations, and `NotRelevant`
  skips.
- [ ] Add point-face slot churn diagnostics.
- [ ] Add directional atlas sequential fallback diagnostics.
- [ ] Avoid per-frame string formatting unless logging is enabled and
  rate-limited.

## Workstream B: Dynamic Atlas And LOD Allocation (Remaining Phases)

This workstream tracks the original allocator-plan items that are still open
after the 2026-05 bring-up. Items already shipped (request bucketing, buddy
allocator, prior-slot reuse, multi-page atlases, depth-only spot/point atlas
eligibility, per-face point requests, published group metadata, etc.) are
captured in the [Current Runtime Snapshot](#current-runtime-snapshot).

### B.1 Request Model And Diagnostics (Phase 1 remainder)

- [ ] Add projected-screen-area scoring, per-face frustum visibility, and
  editor pinning to desired/minimum resolution computation.
- [ ] Add caster set and material state to `ContentHash` inputs.
- [ ] Add explicit dirty-reason reporting in the per-light ImGui diagnostics.
- [ ] Add tests for cascade request expansion.
- [ ] Run targeted rendering/unit tests for light components once unrelated
  compile blockers in the unit-test project are cleared.

### B.2 Atlas Manager, Resources, And Allocator (Phase 2 remainder)

- [ ] Implement VSM/EVSM tile rendering and receiver sampling on the atlas
  path (see [Workstream C](#workstream-c-vsm-and-evsm-shadow-filtering)).
- [ ] Add the GPU metadata SSBO publish for the unified `ShadowAtlasTile`
  contract; current directional/spot paths still bind compact uniform
  arrays / family-specific SSBO metadata.
- [ ] Add memory and fragmentation metrics:
  - [ ] bytes / max budget,
  - [ ] tiles allocated / possible.
- [ ] Add a standalone atlas occupancy panel with owner/LOD/dirty-state
  details (per-light ImGui previews already exist).
- [ ] Make the warmed allocation/solve path allocation-free in debug
  instrumentation.

Allocator validation:

- [ ] Add gutter-math, editor-pinned budget bypass, and full stress tests
  after the unit-test project compile blockers are cleared.
- [ ] Stress test many mixed-size requests beyond capacity.

### B.3 Spot Lights In Atlas (Phase 3 validation)

- [ ] Visual scene with more shadowed spot lights than fit the atlas.
- [ ] Forward and deferred spot receivers.
- [ ] Forced fallback scene covering `Lit`, `ContactOnly`, `StaleTile`, and
  `Disabled`.

### B.4 Directional Cascades In Atlas (Phase 4 validation)

- [ ] VR stereo edge artifacts are not introduced by single-eye fitting.
- [ ] VR active viewport validation when available.

### B.5 Point Lights In Atlas (Phase 5 remainder)

- [ ] Add projected receiver/caster overlap and editor pinning to per-face
  active-consumer scoring (frustum-based scoring already lands).
- [ ] Optional per-face request skip for faces outside active consumers
  (beyond the current `SkipReason.NotRelevant` frustum gate).

Validation:

- [ ] Point light in a six-sided orientation test scene.
- [ ] Moving receiver across face boundaries.
- [ ] Oversubscribed point-light scene near the camera.
- [ ] Partial-face scene where only one to three faces are resident.
- [ ] Visual GS path versus sequential path comparison with identical face
  masks.

### B.6 Unified Forward+ Local Shadow Metadata (Phase 6)

- [ ] Validate Forward+ scenes with more than four shadowed point lights and
  more than four shadowed spot lights in atlas mode.
- [ ] Forward+ scene with many local shadowed lights.
- [ ] Shader compile/permutation test for atlas and legacy paths.

### B.7 Budgeted Updates, Hysteresis, And Stability (Phase 7 remainder)

- [ ] Add caster/material set hashing to the dirty-reason contract.
- [ ] Add optional LOD-transition strength fade.
- [ ] Moving-camera stress scene.
- [ ] Profiler capture of solve time, render tiles per frame, and repack
  frequency.
- [ ] Visual check for shadow shimmer during LOD transitions.

### B.8 Caster Materials, VR, And Probe Policy (Phase 8)

- [ ] Wire `ShadowCasterFilterMode` from request to shadow draw record to
  material variant.
- [ ] Add opaque depth-only variant.
- [ ] Add alpha-tested variant with alpha clip.
- [ ] Add two-sided raster state handling.
- [ ] Keep `AlphaToCoverage` out of v1 unless separately scheduled.
- [ ] Add probe classification: shadow consumers vs non-consumers.
- [ ] Add per-probe `UsesShadowAtlas` opt-in.
- [ ] Ensure multi-camera priority uses max projected score across consumers,
  not sum.
- [ ] Preserve one shared tile for both VR eyes in v1.

Validation:

- [ ] Foliage/cutout caster visual scene.
- [ ] Probe capture scene proving no unexpected atlas request explosion.
- [ ] Stereo cascade coverage validation.

### B.9 Static / Dynamic Caster Split (Phase 9)

- [ ] Measure redraw cost in representative static-heavy scenes.
- [ ] Track static and dynamic caster sets per request.
- [ ] Choose implementation based on profiling: two-tile-per-light,
  hash-stable single tile with static copy, or continue single-tile full
  redraw.
- [ ] If enabled, schedule static refresh only when light or static set
  changes.
- [ ] Composite dynamic movers over static cache.
- [ ] Add inspector view for static/dynamic composition state.
- [ ] Cache invalidation tests for moved light, changed material, and changed
  static set.

### B.10 Virtual Shadow Maps Follow-Up (Phase 10)

- [ ] Gate Virtual Shadow Maps behind a feature flag.
- [ ] Add page-table backing store and residency tracking.
- [ ] Reuse `ShadowMapRequest` schema unchanged.
- [ ] Reuse receiver metadata shape with `vsmPacked` populated.
- [ ] Drive page requests from screen-visible texel analysis using HZB or
  depth-pyramid feedback.
- [ ] Retain v1 atlas as fallback for hardware or driver paths where virtual
  pages are impractical.

### B.11 Allocator Validation Matrix (remaining items)

Unit tests:

- [ ] gutter and inner-rect calculation
- [ ] point light expands to six requests
- [ ] point-light face metadata supports partial residency
- [ ] editor-pinned budget bypass
- [ ] thread-safe deterministic submit under fixed input
- [ ] no allocations after warmup in submit/solve/publish
- [ ] alpha-tested caster discard path
- [ ] non-consuming probes do not submit requests

Visual scenes:

- [ ] many spot lights beyond atlas capacity
- [ ] many point lights near the camera
- [ ] partial point-face residency and fallback
- [ ] mixed moving and static shadow casters
- [ ] moment filtering with gutters
- [ ] broad spot/point atlas validation (forward and deferred)
- [ ] VR active viewport
- [ ] alpha-tested foliage
- [ ] formal mixed-light / mixed-LOD bias regression scene
- [ ] forced fallback oversubscription

Performance counters:

- [ ] atlas solve time
- [ ] shadow tiles rendered per frame
- [ ] `Lights3DCollection.RenderShadowMaps`
- [ ] receiver shader cost
- [ ] atlas memory use
- [ ] fragmentation ratio
- [ ] forward shader sampler count
- [ ] request submit cost from job threads
- [ ] generation/repack frequency

### B.12 Allocator Risk Checklist

- [ ] Tile-edge leaks mitigated with gutters, inner rect clamping, and
  tile-aware blur/mip generation.
- [ ] Shadow shimmer mitigated with reuse, hysteresis, texel snapping, and
  controlled repacks.
- [ ] Point-light seams covered by face transform tests and gutter edge
  handling.
- [ ] Forward shader metadata complexity controlled by migrating spot lights
  first.
- [ ] Bias regression covered by mixed-LOD validation.
- [ ] Allocator fragmentation tracked before adding new allocator modes.
- [ ] Shader permutation growth tracked through existing uber-feature
  tooling.
- [ ] Probe-capture request explosion prevented by default opt-out.

### B.13 Out Of Scope For Atlas v1

- Translucent, colored, stochastic, deep-shadow, or Fourier opacity shadows.
- Ray-traced shadow-map encodings.
- Per-eye virtual page residency.
- Alpha-to-coverage shadow path.
- Cookie or IES profile atlasing.

## Workstream C: VSM And EVSM Shadow Filtering

### C.1 Design Rules

- `Depth` remains the default behavior until a light explicitly opts into
  moment maps.
- `EShadowMapEncoding` is separate from depth filter mode.
- Moment maps encode linear normalized depth, not raw projected
  `gl_FragCoord.z`.
- Point-light moment maps encode radial normalized depth.
- The encoder, clear value, receiver comparison, and reversed-Z/depth-direction
  constant always agree.
- Moment map clears use an unoccluded sentinel, never zero.
- EVSM4 uses signed floating-point formats; unsigned formats are forbidden.
- Format selection probes render-target and linear-filter capability before
  allocation.
- Unsupported formats demote deterministically to a supported encoding,
  logging once per light.
- Moment controls on `XRBase`-derived light components use `SetField(...)`.
- Cascades blend post-filter visibility values, never raw depth or moment
  vectors.
- Atlas gutters use the same encoding-specific unoccluded clear sentinel.
- Contact shadows stay independent and multiply on top of both depth and
  moment visibility.

### C.2 Pre-v1 API Cleanup

- [ ] Rename `ESoftShadowMode` to `EShadowDepthFilterMode`.
- [ ] Rename `SoftShadowMode` to `DepthShadowFilterMode`.
- [ ] Present both encoding and filtering under an editor group named
  `Shadow Filtering`.

### C.3 Clear Sentinels And Format Defaults

- [ ] Use the same encoding-specific clear sentinel for untouched atlas
  texels and gutters.
- [ ] Validate / expose an `R32f` color option for color-depth atlas users.

### C.4 Standalone Resource Slice Remainder

- [ ] With default settings, validate that existing depth shadows render as
  before (Phase 1 exit criterion still open).

### C.5 Spot Moment Slice Remainder

- [ ] Add per-resource separable blur for non-atlas moment spot maps.
- [ ] Implement moment debug viewer for `sampler2D` resources: `M1`, `M2`,
  variance, EVSM warped channels, bleed mask, active clear sentinel.

Validation:

- [ ] Visual comparison: `Depth + PCSS`, VSM, EVSM2, EVSM4.
- [ ] Long-range spot light scene.
- [ ] Masked/cutout caster scene.
- [ ] Profiler capture for shadow render, blur, and receiver cost.

### C.6 Point Moment Remainder

- [ ] Validate masked caster variants write correct radial moments.
- [ ] Geometry-shader and six-pass paths match within expected filtering
  differences.
- [ ] Face seams are no worse than depth mode.

Validation:

- [ ] Point light near shadow casters.
- [ ] Moving receiver crossing cube face boundaries.
- [ ] Masked point-shadow caster.
- [ ] EVSM overflow/clamp test for point lights.

### C.7 Directional Single-Map Remainder

- [ ] Confirm unified color `Depth` path quality against the legacy hardware
  depth path.
- [ ] Depth mode quality remains acceptable with manual compare.

Validation:

- [ ] Directional single-light scene.
- [ ] Volumetric fog scene if applicable.
- [ ] Depth manual-compare reference against previous behavior.

### C.8 Directional Cascaded Remainder

- [ ] Validate cascade debug colors and blend widths.
- [ ] Moment maps do not amplify cascade jitter beyond acceptable tolerance.

Validation:

- [ ] Directional cascaded scene with camera movement.
- [ ] Cascade transition-band scene.
- [ ] Mixed depth and moment encoding preset checks.

### C.9 Blur, Mip Filtering, And MSAA

- [ ] Keep Phase 2 per-resource blur for non-atlas resources until atlas blur
  is ready.
- [ ] Add tile-aware separable blur once atlas tile rects are available.
- [ ] Clamp blur samples to tile inner rects.
- [ ] Add optional MSAA shadow rasterization for non-atlas resources first.
- [ ] Add single-sample moment resolve from MSAA depth source.
- [ ] Keep tile-aware MSAA resolve out of v1 atlas unless separately
  validated.

Validation:

- [ ] Wide soft spot shadow with blur.
- [ ] Atlas gutter leak scene once atlas integration exists.
- [ ] MSAA moment resolve comparison on non-atlas spot or directional
  resource.

### C.10 Atlas Integration

- [ ] Replace the depth-only atlas request path with encoding-aware requests
  for all light families. `SubmitShadowAtlasRequest` must use each light's
  resolved `ShadowMapFormatSelection.Encoding`, not a hardcoded `Depth`.
- [ ] Keep separate atlas pages per `(atlas kind, encoding)`, including
  `Variance2`, `ExponentialVariance2`, and `ExponentialVariance4`.
- [ ] Spot VSM/EVSM atlas path: render `Frag_ShadowMomentOutput` into the
  atlas color page with a separate raster depth attachment, publish moment
  filter params, and sample through the same atlas metadata as depth spots.
- [ ] Point VSM/EVSM atlas path: render radial moments per face into the
  point atlas color page, keep per-face near/far and resolution metadata, and
  validate face-boundary filtering against the standalone cubemap path.
- [ ] Directional VSM/EVSM atlas path: support both primary single-map moment
  atlas tiles and cascaded moment atlas tiles. Cascades must blend filtered
  visibility results, not raw moment vectors.
- [ ] Cascaded moment support must work in both legacy texture-array mode and
  atlas mode, with a single receiver contract deciding whether the source is
  a standalone map, cascade array, or atlas tile.
- [ ] Use clear sentinel for untouched atlas texels and gutters.
- [ ] Implement agreed demotion policy when encoding budget is exhausted.
- [ ] Publish moment filter parameters through `ShadowAtlasTile.filterParams`.
- [ ] Add atlas debug views for moment channels.
- [ ] Moment filtering respects atlas tile boundaries.
- [ ] Generate moment mipmaps and/or tile-aware separable blur per atlas tile
  without bleeding across inner rects or gutters.

Validation:

- [ ] Mixed encoding atlas scene.
- [ ] Encoding flip at runtime.
- [ ] Oversubscribed moment atlas scene exercising demotion and fallback.
- [ ] Directional VSM/EVSM cascades in atlas mode and legacy array mode.
- [ ] Spot and point VSM/EVSM atlas scenes, deferred and forward.

### C.11 Other Shadow Consumers And Legacy Cleanup

- [ ] Convert or explicitly exclude volumetric fog directional sampling.
- [ ] Convert or explicitly exclude SSGI / probe GI directional shadowing.
- [ ] Convert or explicitly exclude water and translucency shadow sampling.
- [ ] Convert or explicitly exclude decals using shadow matrices.
- [ ] Convert or explicitly exclude GPU particle lighting.
- [ ] Add a build-time or test-time check that legacy binding names are not
  used outside approved compatibility shims.
- [ ] Remove compatibility shims once common materials and debug views use
  the dispatcher.
- [ ] Update relevant docs and editor help text for user-visible settings.

Validation:

- [ ] Shader source scan for legacy binding names.
- [ ] Visual smoke test of each converted consumer.
- [ ] Editor inspector smoke test.

### C.12 Shader Helper Remainder

Encoding helpers:

- [ ] `XRENGINE_EncodeVsmMoments(float depth, float minVariance)`.
- [ ] `XRENGINE_EncodeEvsm2Moments(float depth, float exponent, float minVariance)`.
- [ ] `XRENGINE_EncodeEvsm4Moments(float depth, float positiveExponent, float negativeExponent, float minVariance)`.
- [ ] Clamp exponents based on selected format.
- [ ] Add a derivative-free or derivative-clamped path for cube faces, atlas
  tiles, cascades, and masked casters.

Sampling helpers:

- [ ] `XRENGINE_ChebyshevUpperBound(...)`.
- [ ] EVSM2 visibility helper.
- [ ] EVSM4 visibility helper using min of positive and negative visibility
  estimates.
- [ ] `sampler2D`, `sampler2DArray`, and `samplerCube` dispatchers.
- [ ] Keep depth compare helpers for hard, Poisson, Vogel, and PCSS.
- [ ] Apply contact shadows after map visibility.

### C.13 Filtering Validation Matrix

Unit tests:

- [ ] enum/default values
- [ ] `LightComponent` moment settings use `SetField(...)`
- [ ] format selection per encoding and light type
- [ ] format capability demotion
- [ ] EVSM exponent clamps
- [ ] clear sentinel calculation
- [ ] central depth-direction consistency
- [ ] shader source contains moment encoding helpers
- [ ] shader source contains receiver dispatchers
- [ ] cascade receiver does not blend raw moment vectors
- [ ] default `Depth` mode remains unchanged

Visual tests:

- [ ] one directional light with cascades
- [ ] one long-range spot light
- [ ] one point light near shadow casters
- [ ] masked foliage or cutout material
- [ ] moving receiver
- [ ] moving light
- [ ] cascade transition bands
- [ ] atlas gutter leak scene after atlas integration

Compare these presets:

- [ ] `Depth + Hard`
- [ ] `Depth + FixedPoisson`
- [ ] `Depth + VogelDisk`
- [ ] `Depth + ContactHardeningPcss`
- [ ] `Variance2`
- [ ] `ExponentialVariance2`
- [ ] `ExponentialVariance4`

Performance tests:

- [ ] `Lights3DCollection.RenderShadowMaps`
- [ ] moment blur pass cost
- [ ] deferred light passes
- [ ] forward material draws with local lights
- [ ] `GLMeshRenderer.Render.SetMaterialUniforms`
- [ ] shader sampler/binding count
- [ ] memory use by encoding

### C.14 Filtering Risk Checklist

- [ ] Light bleeding mitigated with bleed reduction, min variance, EVSM
  presets, and depth fallback.
- [ ] EVSM overflow mitigated with format-specific exponent clamps and
  optional 32-bit formats.
- [ ] Atlas filtering bleed mitigated with gutters, inner-rect clamps, and
  tile-aware blur/mips.
- [ ] Point-light seams mitigated with radial depth consistency and
  face-boundary validation.
- [ ] Reversed-Z drift mitigated with one central depth-direction constant
  and tests.
- [ ] Shader permutation growth monitored through the existing uber-feature
  tooling.
- [ ] Derivative-derived moment variance disabled or clamped where
  derivatives are unreliable.

### C.15 Filtering Out Of Scope For v1

- Variance-based PCSS / VSSM.
- Tile-aware MSAA resolve for atlas resources.
- Translucent, colored, stochastic, deep-shadow, or Fourier opacity shadows.
- Runtime per-fragment dynamic branching between encodings in hot receiver
  loops.

Post-v1 features are tracked in
[Post-v1 Advanced Shadow Features Plan](../design/post-v1-advanced-shadow-features-plan.md).

## Workstream D: Contact Shadow Optimizations

Reduce per-pixel cost of contact shadows so they can stay enabled by default
on multi-light scenes without dragging frame time.

Today, `XRENGINE_SampleContactShadowScreenSpace` in
`Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl` runs a world-space
ray march. Per sample it executes roughly four `mat4 * vec4` reconstructions:

- `viewProjectionMatrix * worldPos` — project current sample to clip
- `inverseProjMatrix * clipPos` — reconstruct view-space from sampled depth
- `inverseViewMatrix * viewPos` — back to world to compare distances
- `viewMatrix * samplePosWS` — recompute the sample's view depth

Default sample counts:
`DirectionalLightComponent.ContactShadowSamples = 16`,
`SpotLightComponent.ContactShadowSamples = 16`,
`LightComponent` base (point inherits) `= 4`.

Worst-case per-pixel-per-light is ~16 samples × ~4 mat4×vec4 + a depth fetch
+ a few dot products, which compounds quickly across multiple shadow-casting
lights.

### D.0 Branch Setup

- [ ] Create branch `rendering/contact-shadow-optimizations` and move all
  subsequent work onto it.

### D.1 Refactor To Pure Screen-Space Marching (highest gain)

- [ ] Replace per-sample world-space reprojection with a constant per-step UV
  + clip-depth delta:
  - Compute start clip position (`viewProjectionMatrix * worldPos`) once.
  - Compute end clip position
    (`viewProjectionMatrix * (worldPos + rayDir * maxDist)`) once.
  - Convert to screen UV + clip-depth, derive `vec2 duv` and `float dz` per
    step at function entry.
  - Loop body:
    `uv += duv; rayClipDepth += dz; sceneDepth = textureLod(SceneDepth, uv, 0); compare`.
- [ ] Drop `inverseViewMatrix` and the redundant `viewMatrix * samplePosWS`
  per-sample multiplications; keep `inverseProjMatrix` only if still needed
  for thickness compare, and prefer linear depth comparison instead.
- [ ] Move all four overloads to a shared internal helper to keep the four
  entry points thin.

### D.2 Compare In View Space, Not World Space

- [ ] Build the ray once in view space (`viewMatrix * worldPos`,
  `viewMatrix * lightDir`) at function entry, then compare against linearized
  scene depth without ever reconstructing a sample world position.
- [ ] Keep the existing thickness / fade parameters; only the coordinate
  system changes.

### D.3 Lower Default Sample Counts

- [ ] `DirectionalLightComponent.ContactShadowSamples` 16 → 8.
- [ ] `SpotLightComponent.ContactShadowSamples` 16 → 8.
- [ ] `LightComponent` base 4 → 6 (point lights gain a bit; cost still capped
  by short `ContactShadowDistance` defaults).
- [ ] Update
  `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
  expectations (lines around 1075 / 1216 at time of writing).
- [ ] Add a short note in
  `docs/architecture/rendering/default-render-pipeline-notes.md` documenting
  the new defaults and rationale.

### D.4 Tighter Early-Outs

- [ ] Skip the sample loop when `dot(N, L) <= 0` (already partially done in
  some paths — audit all four overloads).
- [ ] Skip the loop when `viewDepth > contactFadeEnd` before computing the
  ray start/delta.
- [ ] Add a per-light tile-size early-out: if the light's clip-space bound
  does not overlap the fragment's screen tile, skip contact shadow entirely.
  (Optional; revisit after D.1–D.3.)

### D.5 (Optional Follow-Up) Per-Light Screen-Space Contact-Shadow Pass

- [ ] Evaluate moving contact shadows to a half-res compute pass that writes
  a per-light occlusion texture, sampled as a single texture fetch in
  lighting. Useful when many shadow-casters overlap on screen.
- [ ] Out of scope until D.1–D.4 land and are profiled.

### D.6 Final Merge

- [ ] After validation, merge `rendering/contact-shadow-optimizations` back
  into `main`.

### D.7 Contact Shadow Validation Plan

1. Build editor: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
2. Boot `Start-Editor-NoDebug` with the unit testing world; confirm contact
   shadows still appear under directional + at least one spot and one point
   light.
3. Run `Test-SurfelGi` and any forward-lighting / deferred shading smoke
   tests.
4. Capture before/after profiler GPU traces in a multi-light scene
   (`profiler-render-stalls.log`, `profiler-fps-drops.log`).
5. Update or add a test in
   `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`
   (or a new contact-shadow-specific file) that asserts the new sample-count
   defaults.

### D.8 Contact Shadow Risks / Notes

- The four `XRENGINE_SampleContactShadowScreenSpace` overloads must stay in
  sync — refactor one, port the same pattern to the others in the same
  commit.
- View-space vs world-space switch can subtly change thickness behavior for
  long rays (`ContactShadowDistance > ~5m`); keep the fade parameters in the
  same units and visually compare at distance.
- Sample-count changes are user-visible defaults; surface in patch notes.

## Cross-Cutting Policy Decisions

- Directional, spot, and point atlas paths are depth-only until a complete
  moment-atlas path exists.
- VSM/EVSM local lights use standalone shadow maps in the near term, but the
  atlas architecture must preserve a clean path to moment atlas pages and
  cascaded moment atlas sampling.
- Legacy non-atlas paths stay available for debug, fallback, and moment
  encodings.
- The atlas must not silently suppress shadows when an encoding is
  unsupported. It must either bypass to legacy or publish an explicit
  fallback/diagnostic.
- Receiver shaders must treat atlas metadata as authoritative. They must not
  assume one page, one resolution, or equal point-face sizes.
- Hot paths must avoid managed allocations after warmup. New diagnostics must
  use fixed counters or preallocated storage.
- `Depth` shadow behavior remains the default until a light opts into moment
  maps; the encoder, clear, receiver compare, and depth-direction constant
  always agree.
- Probe captures do not consume live atlas shadows by default.
- VR directional cascade fitting uses the union of both eye frusta.
- Contact shadows always multiply on top of map visibility, independent of
  encoding.

## Closeout Checklist

- [ ] Directional depth atlas performance is measured and no longer halves
  framerate versus legacy layered cascades in the same scene.
- [ ] Directional VSM/EVSM works with `UseDirectionalShadowAtlas` both
  enabled and disabled, using the documented bypass or a complete
  moment-atlas path.
- [ ] Focused shadow-atlas tests pass.
- [ ] Runtime rendering build passes without new warnings.
- [ ] Editor Unit Testing World smoke test covers: directional cascades,
  directional VSM/EVSM, many spots, point atlas, stale-tile fallback,
  one-page pressure, and multi-page settings.
- [ ] Forward+ scene shades more than four shadowed point lights and more
  than four shadowed spot lights in atlas mode.
- [ ] Spot, point, and directional VSM/EVSM end-to-end visual comparisons
  recorded.
- [ ] Contact shadow refactor merged and default sample counts updated, with
  before/after profiler captures in a multi-light scene.
- [ ] Docs are updated if settings, editor diagnostics, launch flags, or
  shadow encoding behavior changes.
