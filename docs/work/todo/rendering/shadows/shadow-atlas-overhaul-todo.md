# Shadow Atlas Overhaul TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Shadow Atlas](../../../../architecture/rendering/shadow-atlas.md), [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
Design: [Dynamic Shadow Atlas LOD Plan](../../../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md), [Shadow Filtering VSM/EVSM Plan](../../../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md), [Post-v1 Advanced Shadow Features Plan](../../../design/rendering/shadows/post-v1-advanced-shadow-features-plan.md)
Validation: [Shadow Validation](../../../testing/rendering/shadow-validation.md)

## Current State

`ShadowAtlasManager` owns pages per `(EShadowAtlasKind, EShadowMapEncoding)` with persistent buddy-page occupancy. One balanced solve per bucket reuses persistent placements, pre-reserves directional cascade groups, applies the feasibility waterline, and repairs failed placements locally with a bounded demotion loop (`Math.Clamp(entryCount / 64 + 1, 1, 4)`). It does not reset page reservations between attempts. `DirectionalLightComponent.CascadeShadowRenderMode` defaults to `EDirectionalCascadeShadowRenderMode.Auto`. `ResolveRelevanceScore` still uses request priority only. Spot and point atlases are depth-only. Vulkan directional atlases are depth-only. `RequestRepack()` exists, but no automatic compaction exists. `ShadowAtlasSolveDiagnostics.DeterministicFallbackDemotionCount` and `WarnDeterministicFallbackDemotion` remain, but the current solver never increments or calls them.

## Open Code Items

### Solver Cleanup

- [ ] Remove or reconnect the unused deterministic fallback demotion counter and warning. `ShadowAtlasManager.cs` (`WarnDeterministicFallbackDemotion`, `DeterministicFallbackDemotionCount`), `ShadowAtlasTypes.cs`, profiler render stats panel. Done when: no diagnostic field is published that the solver cannot change, or the local-repair path increments it.
- [ ] Add per-page fragmentation metrics, and trigger targeted compaction or repack only on a failed allocation, an explicit editor request, or sustained high fragmentation. `ShadowAtlasManager.ShadowBuddyPageAllocator`, `ShadowAtlasMetrics`. Done when: metrics report fragmentation per page and a unit test covers the compaction trigger.
- [ ] Publish the repack reason and the affected atlas kind and encoding when a repack runs, and advance the layout generation. `ShadowAtlasManager.RequestRepack`, `ShadowAtlasFrameData`. Done when: a unit test shows the reason and the generation change, and metadata sampled in the same frame stays valid.
- [ ] Add anchor (pinned) slots after relevance and grouping are stable. `ShadowAtlasManager`. Done when: a pinned request keeps its slot under pressure in a unit test.

### Receiver-Aware Relevance

- [ ] Replace the priority-only `ResolveRelevanceScore` with a `ShadowRelevanceScore` built from existing visible and culling data. Score from visible receivers, so an off-screen caster that shadows a visible receiver stays relevant. `ShadowAtlasManager.cs`, `Lights3DCollection.Shadows.cs`. Done when: the score inputs are fields on the request, and unit tests cover a visible receiver with an off-screen caster and an off-screen light with no receivers.
- [ ] Score each directional cascade from its slice and the visible receiver bounds it can affect. `DirectionalLightComponent.CascadeShadows.cs`. Done when: a far cascade with no receivers demotes or skips independently of near cascades in a unit test.
- [ ] Score spot lights from cone and frustum intersection, projected screen influence, receiver overlap, distance, brightness, and stale-tile age. `SpotLightComponent.cs`, `LocalShadowFrustumRelevance.cs`. Done when: unit tests cover each input.
- [ ] Combine point-face receiver overlap with the camera-face alignment estimate. `PointLightComponent.cs`, `LocalShadowFrustumRelevance.cs`. Done when: a unit test shows the combined score per face.
- [ ] Score against both VR eyes and any contributing mirror camera. Done when: a unit test with two eye cameras gives a stable score.
- [ ] Define when a zero-score cascade or face publishes `NotRelevant` and which fallback the receiver uses. Done when: the rule is in code and in [Shadow Atlas](../../../../architecture/rendering/shadow-atlas.md).

### Grouping

- [ ] Support partial directional groups when fewer than four cascades are active or relevant. `ShadowAtlasManager.TryAllocateDirectionalCascadeGroups`. Done when: a unit test allocates a two- or three-cascade group on one page.
- [ ] Demote lower-relevance cascades before the solver falls back to independent sequential tiles when a directional group cannot fit. Done when: a unit test shows group demotion before ungrouping.
- [ ] Add heterogeneous point-face group pre-reservation (`TryReservePointLightFaceGroup(light, faceSizes[6])`). Sort faces by relevance and pack them deterministically in the smallest containing power-of-two block. Single faces can still demote, skip, or evict. Done when: a unit test packs one full-resolution face plus five quarter-resolution faces on one page.

### Directional Atlas Rendering

- [ ] Keep grouped atlas rendering available on OpenGL 4.6 hardware with indexed viewport and scissor and vertex-stage or geometry-stage viewport index support. Done when: the capability gate in `DirectionalLightComponent.CascadeShadows.cs` selects grouped rendering for that feature set, and a unit test covers the gate.
- [ ] Limit the directional critical-refresh budget bypass to the first render and real camera-fit changes, so steady projection jitter does not refresh every cascade each frame. `ShadowAtlasManager` render plan budget classes. Done when: a unit test shows sub-texel jitter produces `Normal` or no work, not `CriticalBypass`.
- [ ] Expose the requested and effective cascade render mode and the exact fallback reason in logs and the editor. Done when: `EffectiveCascadeShadowRenderMode` and the fallback reason show in the light inspector.

### Moment Encodings And Filtering

- [ ] Rename `ESoftShadowMode` and `SoftShadowMode` to depth-filter terms, if the pre-v1 API window is still open. `ESoftShadowMode.cs`, `LightComponent.cs`. Done when: the rename compiles across the solution.
- [ ] Implement spot VSM and EVSM atlas pages: moment color atlas, separate raster depth attachment, filter parameters, mip or blur generation, and atlas metadata sampling. Done when: spot moment requests no longer bypass the atlas.
- [ ] Implement point VSM and EVSM atlas pages with radial moments per face, per-face near and far metadata, and seam-aware filtering. Done when: point moment requests no longer bypass the atlas.
- [ ] Implement Vulkan directional moment atlases, or document the explicit bypass in the architecture doc. `UsesDirectionalShadowAtlasForCurrentEncoding`. Done when: the code path or the documented bypass exists.
- [ ] Make directional VSM and EVSM cascaded by default. Done when: the default settings use cascades for moment encodings.
- [ ] Blend cascade visibility values, not raw moment vectors, in transition bands. `ShadowMomentEncoding.glsl`, `ShadowSampling.glsl`. Done when: the shader blends after visibility evaluation.
- [ ] Add tile-aware separable blur or mip generation that clamps to tile inner rects and clears gutters with encoding sentinels. Done when: the blur shader reads tile bounds.
- [ ] Add moment atlas debug views for M1, M2, EVSM warped channels, variance, bleed mask, and clear sentinel. Done when: the views are selectable in the editor.
- [ ] Clamp or disable derivative-derived moment variance for cube faces, atlas tiles, cascades, and masked casters. Done when: the shader applies the clamp on those paths.

### Temporal Shadow Cache

Rationale: [Post-v1 Advanced Shadow Features Plan](../../../design/rendering/shadows/post-v1-advanced-shadow-features-plan.md).

- [ ] Track static and dynamic caster sets per request, and add caster and material state to `ContentHash`. Done when: a material change dirties the tile in a unit test.
- [ ] Implement the chosen cache model (single stable tile, static plus dynamic overlay, or no split). Refresh static pages only on light, static caster, material, encoding, or receiver-contract changes. Done when: the model is in code with its invalidation rules.
- [ ] Add cadence controls for far directional cascades and low-relevance local faces, with motion and disocclusion rejection. Done when: settings exist and a unit test covers the cadence.
- [ ] Add optional screen-space temporal filtering of the final shadow visibility. Reject history on receiver motion, light motion, tile generation change, cascade change, disocclusion, and normal or depth disagreement. Do not warp raw shadow depth. Done when: the filter is behind a setting with these rejection inputs.
- [ ] (Optional) Prototype scroll reuse for directional cascades. Detect translation-only whole-texel deltas between the rendered sample matrix and the current cascade matrix. Copy the overlap and render casters only into the exposed strips. `ShadowAtlasManager`, `DirectionalLightComponent.CascadeShadows.cs`. Done when: the reuse is behind a disabled-by-default option and a unit test proves the overlap matches a fresh render within depth quantization.
- [ ] (Optional) Reuse primary command buffers for frames with only cascade work (dirty reason `primary-frame-state`). Coordinate with the [Vulkan core hardening TODO](../vulkan-core-hardening-and-device-loss-todo.md). Done when: a settled cascade-only frame reuses the primary command buffer and the reuse rule is in the architecture doc.

### Diagnostics And Editor

- [ ] Extend per-light atlas diagnostics with score inputs, requested and allocated resolution, skip reason, fallback mode, resident age, last rendered frame, page, rect, encoding, churn count, and dirty reason. Done when: the fields are in `ShadowAtlasFrameData` diagnostics.
- [ ] Show the directional grouped-render state in the editor: backend, requested and effective mode, fallback reason, grouped and ungrouped pass count, and shadow time. Done when: the editor panel shows these values.
- [ ] Add point-face slot churn diagnostics and same-page group status. Done when: the values are published per point light.
- [ ] Add an atlas occupancy panel with owner, LOD, dirty state, encoding, last rendered frame, and fallback. Done when: the panel exists in the editor.
- [ ] Add rate-limited slow-render log summaries with no per-frame string formatting when logging is off. Keep solve cost and tile render cost separate in profiler output. Done when: the summaries exist and the profiler shows two separate scopes.

### Contact Shadows

- [ ] Refactor `XRENGINE_SampleContactShadowScreenSpace` to compute clip, UV, and depth deltas once and march in screen or view space without world-space reprojection per sample. `ShadowSampling.glsl`, `DeferredLightingDir.fs`, `DeferredLightingPoint.fs`, `DeferredLightingSpot.fs`, `ForwardLighting.glsl`. Done when: the loop has no per-sample world reconstruction.
- [ ] Add early-outs for `dot(N, L) <= 0`, beyond fade range, invalid depth, and a missing contact depth texture. Done when: each early-out is in the helper.

### Tests (Owner Clearance Required)

- [ ] Add unit tests for persistent-layout determinism, waterline demotion determinism, completion reconciliation (first render and stale tile), and depth-only page selection per encoding. `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`. Done when: each behavior has a deterministic test.
- [ ] Add an automated editor scenario that moves the camera, stops it, captures frames before and after the stop, and fails on a one-frame atlas and matrix mismatch in the audit log. Done when: the scenario runs from a script or test and reports pass or fail.
- [ ] Add deterministic in-budget and over-budget shadow benchmark scenes (one four-cascade directional light, several spot lights, several point lights). Unit Testing World settings or a benchmark fixture. Done when: both scenes load from a named setting, and the over-budget scene triggers waterline or local-repair demotion.

## Decisions Needed

- [ ] Choose the v1 temporal cache model after the static-heavy redraw measurement in [Shadow Validation](../../../testing/rendering/shadow-validation.md). Owner: rendering lead.
- [ ] Decide whether local VSM and EVSM atlases are in v1, or stay on standalone moment maps. Owner: rendering lead.
- [ ] Decide whether a half-resolution per-light contact-shadow pass is worth it after the helper refactor is measured. Owner: rendering lead.

## Out Of Scope

- DLSS Frame Generation, DLSS Super Resolution, and DLAA as shadow generators. See [Shadow Atlas](../../../../architecture/rendering/shadow-atlas.md#upscaler-and-frame-generation-boundary).
- Virtual or sparse atlas page tables before the v1 atlas is stable.
