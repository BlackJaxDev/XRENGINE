# Shadow Atlas Overhaul TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Shadow Atlas](../../../../architecture/rendering/shadow-atlas.md), [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
Design: [Dynamic Shadow Atlas LOD Plan](../../../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md), [Shadow Filtering VSM/EVSM Plan](../../../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md), [Post-v1 Advanced Shadow Features Plan](../../../design/rendering/shadows/post-v1-advanced-shadow-features-plan.md)
Validation: [Shadow Validation](../../../testing/rendering/shadow-validation.md)

## Current State

`ShadowAtlasManager` owns pages per `(EShadowAtlasKind, EShadowMapEncoding)` with persistent buddy-page occupancy. The solver resets temporary solve state for each balanced solve attempt, reuses persistent placements, pre-reserves directional cascade groups, applies the feasibility waterline, and repairs failed placements locally with a bounded demotion loop. `DirectionalLightComponent` requests `Auto` by default, while the effective render mode starts as `Sequential` and falls back to `Sequential` when grouped or layered support is unavailable. `ResolveRelevanceScore` still uses request priority only. Spot and point atlases are depth-only. Vulkan directional atlases are depth-only. `RequestRepack()` exists, but automatic compaction does not. `ShadowAtlasSolveDiagnostics.DeterministicFallbackDemotionCount` remains published, but the current solver does not increment it.

## Open Code Items

### Solver Cleanup

- [ ] Remove or reconnect the unused deterministic fallback demotion counter and warning. `ShadowAtlasManager.cs`, `ShadowAtlasTypes.cs`, profiler render stats panel. Done when: no diagnostic field is published that the solver cannot change, or the local-repair path increments it.
- [ ] Add per-page fragmentation metrics and targeted compaction. `ShadowAtlasManager.ShadowBuddyPageAllocator`, `ShadowAtlasMetrics`. Done when: metrics report fragmentation per page and a unit test covers the compaction trigger.
- [ ] Publish the repack reason and affected atlas kind and encoding. `ShadowAtlasManager.RequestRepack`, `ShadowAtlasFrameData`. Done when: unit tests show the reason, generation change, and same-frame metadata safety.
- [ ] Add pinned slots after relevance and grouping are stable. `ShadowAtlasManager`. Done when: a pinned request keeps its slot under pressure in a unit test.

### Receiver-Aware Relevance

- [ ] Replace priority-only relevance with `ShadowRelevanceScore` built from visible and culling data. `ShadowAtlasManager.cs`, `Lights3DCollection.Shadows.cs`. Done when: request fields carry the score inputs and unit tests cover visible receivers and off-screen lights.
- [ ] Score each directional cascade from its slice and affected visible receiver bounds. `DirectionalLightComponent.CascadeShadows.cs`. Done when: a far cascade with no receivers demotes or skips independently.
- [ ] Score spot lights from cone/frustum intersection, projected screen influence, receiver overlap, distance, brightness, and stale-tile age. `SpotLightComponent.cs`, `LocalShadowFrustumRelevance.cs`. Done when: unit tests cover each input.
- [ ] Combine point-face receiver overlap with camera-face alignment. `PointLightComponent.cs`, `LocalShadowFrustumRelevance.cs`. Done when: a unit test shows the combined score per face.
- [ ] Score against both VR eyes and contributing mirror cameras. Done when: a two-eye unit test gives a stable score.
- [ ] Define zero-score cascade or face fallback behavior in code and architecture docs. Done when: zero-score requests publish `NotRelevant` or a documented fallback consistently.

### Grouping And Directional Rendering

- [ ] Support partial directional groups when fewer than four cascades are active or relevant. `ShadowAtlasManager.TryAllocateDirectionalCascadeGroups`. Done when: a unit test allocates a two- or three-cascade group on one page.
- [ ] Demote lower-relevance cascades before falling back to independent sequential tiles. Done when: a unit test shows group demotion before ungrouping.
- [ ] Add heterogeneous point-face group pre-reservation. `ShadowAtlasManager`. Done when: one full-resolution face plus five quarter-resolution faces pack deterministically on one page.
- [ ] Keep grouped atlas rendering available on OpenGL 4.6 hardware with indexed viewport/scissor and viewport index support. `DirectionalLightComponent.CascadeShadows.cs`. Done when: a capability gate and unit test cover that feature set.
- [ ] Limit directional critical-refresh budget bypass to first render and real camera-fit changes. Render plan budget classes. Done when: sub-texel jitter does not produce `CriticalBypass`.
- [ ] Expose requested/effective cascade render mode and fallback reason in logs and the editor. Done when: the light inspector shows both values.

### Moment Encodings And Filtering

- [ ] Rename `ESoftShadowMode` and `SoftShadowMode` to depth-filter terms while the pre-v1 API window is open. Done when: the rename compiles across the solution.
- [ ] Implement spot VSM and EVSM atlas pages. Done when: spot moment requests no longer bypass the atlas.
- [ ] Implement point VSM and EVSM atlas pages. Done when: point moment requests no longer bypass the atlas.
- [ ] Implement Vulkan directional moment atlases or document the explicit bypass. `UsesDirectionalShadowAtlasForCurrentEncoding`. Done when: code path or architecture bypass exists.
- [ ] Make directional VSM and EVSM cascaded by default. Done when: default settings use cascades for moment encodings.
- [ ] Blend cascade visibility values, not raw moment vectors. `ShadowMomentEncoding.glsl`, `ShadowSampling.glsl`. Done when: shaders blend after visibility evaluation.
- [ ] Add tile-aware blur or mip generation that clamps to inner rects and clears gutters. Done when: blur shader reads tile bounds.
- [ ] Add moment atlas debug views. Done when: M1, M2, EVSM warped channels, variance, bleed mask, and clear sentinel views are selectable.
- [ ] Clamp or disable derivative-derived moment variance for cube faces, atlas tiles, cascades, and masked casters. Done when: shader code applies the clamp.

### Temporal Cache And Contact Shadows

- [ ] Track static and dynamic caster sets per request, and add caster/material state to `ContentHash`. Done when: a material change dirties the tile in a unit test.
- [ ] Implement the chosen temporal cache model with invalidation rules. Done when: the model is in code and refreshes static pages only when required.
- [ ] Add cadence controls for far directional cascades and low-relevance local faces. Done when: settings exist and unit tests cover cadence.
- [ ] Add optional screen-space temporal filtering of final shadow visibility. Done when: history rejects on receiver motion, light motion, tile generation change, cascade change, disocclusion, and normal or depth mismatch.
- [ ] Prototype disabled-by-default scroll reuse for directional cascades. Done when: a unit test proves overlap matches a fresh render within depth quantization.
- [ ] Reuse primary command buffers for settled cascade-only frames if the Vulkan contract allows it. Done when: reuse rules are in code and architecture docs.
- [ ] Refactor `XRENGINE_SampleContactShadowScreenSpace` to avoid per-sample world reconstruction. Shadow shaders. Done when: the loop computes clip, UV, and depth deltas once.
- [ ] Add contact-shadow early-outs for backfacing, fade range, invalid depth, and missing contact depth texture. Done when: each early-out is in the helper.

### Diagnostics And Tests

- [ ] Extend per-light atlas diagnostics with score inputs, resolution, skip reason, fallback mode, age, frame, page, rect, encoding, churn, and dirty reason. Done when: fields are in frame data diagnostics.
- [ ] Show grouped directional state in the editor. Done when: backend, requested/effective mode, fallback reason, pass count, and shadow time are visible.
- [ ] Add point-face slot churn diagnostics and same-page group status. Done when: values are published per point light.
- [ ] Add an atlas occupancy panel. Done when: owner, LOD, dirty state, encoding, last rendered frame, and fallback are visible.
- [ ] Add rate-limited slow-render summaries with no per-frame string formatting when logging is off. Done when: solve cost and tile render cost have separate profiler scopes.
- [ ] Add unit tests for persistent-layout determinism, waterline demotion determinism, completion reconciliation, and depth-only page selection. Done when: each behavior has deterministic coverage.
- [ ] Add an automated editor scenario for move-stop stale-frame detection after owner clearance. Done when: the scenario reports pass or fail from a script or test.
- [ ] Add deterministic in-budget and over-budget shadow benchmark scenes. Done when: both scenes load from named settings and the over-budget scene triggers demotion.

## Decisions Needed

- [ ] Choose the v1 temporal cache model after static-heavy redraw measurement. Owner: rendering lead.
- [ ] Decide whether local VSM and EVSM atlases are in v1 or stay on standalone moment maps. Owner: rendering lead.
- [ ] Decide whether a half-resolution per-light contact-shadow pass is worth it after helper measurement. Owner: rendering lead.

## Out Of Scope

- DLSS Frame Generation, DLSS Super Resolution, and DLAA as shadow generators.
- Virtual or sparse atlas page tables before the v1 atlas is stable.
