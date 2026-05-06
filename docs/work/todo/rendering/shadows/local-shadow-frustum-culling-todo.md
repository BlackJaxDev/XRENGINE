# Local Shadow Frustum Culling TODO

> Status: **proposed phased TODO**
> Last reconciled: **2026-05-04**
> Scope: point-light cubemap faces, point-light atlas faces, spot-light shadow frusta, shadow request submission, legacy shadow rendering, diagnostics.

## Target Outcome

Avoid rendering local shadow projections that cannot affect the current camera set:

- A point light only renders cubemap/atlas faces whose face frustum intersects the active camera set's view volume.
- A spot light only renders its shadow map or atlas tile when its spot shadow frustum intersects the active camera set's view volume.
- Both legacy point paths are covered:
  - non-GS six-pass cubemap rendering skips non-relevant faces
  - GS cubemap rendering skips non-relevant layers, or falls back to the per-face path when that is cleaner
- Atlas point faces can be skipped, demoted, or left stale with explicit diagnostics instead of always submitting all six faces.
- The result is conservative: if relevance cannot be proven safely, render the shadow rather than dropping a visible receiver shadow.

This is a narrow tactical improvement that can land before the broader [Shadow Relevance Scoring TODO](shadow-relevance-scoring-todo.md). The larger relevance system should eventually own the same camera-set and receiver-driven concepts.

## Source Context

- [Dynamic Shadow Atlas And LOD Allocation TODO](dynamic-shadow-atlas-lod-todo.md)
- [Shadow Relevance Scoring TODO](shadow-relevance-scoring-todo.md)
- [VSM And EVSM Shadow Filtering TODO](shadow-filtering-vsm-evsm-todo.md)
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.CameraLightIntersections.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/OneViewLightComponent.cs`
- `Build/CommonAssets/Shaders/PointLightShadowDepth.gs`
- `Build/CommonAssets/Shaders/PointLightShadowDepth.fs`

## Current State

- `CullShadowCollectionByCameraFrusta` can cull whole-light shadow collection for local lights when not in VR.
- Point-light whole-light collection relevance uses the point influence sphere, not per-face frusta.
- Spot-light whole-light collection relevance uses the spot cone, not the actual shadow camera frustum.
- Legacy point non-GS collection is per face, but `RenderShadowMap` still renders all six faces.
- Legacy point GS collection uses the influence sphere and renders all six cubemap layers in one pass.
- Point atlas mode submits six `PointFace` requests for every active shadow-casting point light.
- `EstimatePointFaceRelevance` currently uses light-to-camera direction alignment to scale resolution; it does not test face-frustum intersection and does not skip faces.
- Spot atlas mode submits a `SpotPrimary` request for each active eligible spot light without a shadow-frustum relevance gate.
- `SkipReason` does not currently include `NotRelevant`; the broader relevance TODO explicitly tracks that gap.
- `LightComponent.CameraIntersections` already records camera/light-frustum intersections for debug, but that state is not used to drive shadow rendering.

## Non-Negotiable Design Rules

- Relevance is based on potential visible receivers, not caster on-screen visibility. Off-screen casters must still render if they can shadow an on-screen receiver.
- A point face or spot frustum that intersects any relevance camera remains renderable.
- VR uses the union of both eye views. Do not use a single desktop mirror camera to drop shadows visible to either eye.
- Editor preview, scene capture, mirror, and probe cameras only contribute when explicitly included in the active relevance camera set.
- Unknown or empty camera-set state must fail conservatively for legacy rendering: render rather than silently skipping, except for explicit headless/no-consumer atlas paths.
- Atlas skips publish explicit fallback metadata and never leave receivers sampling undefined atlas texels.
- Legacy cubemap skipped faces may retain stale data only when no current visible receiver can sample those faces.
- Face masks and relevance data must not allocate per frame after warmup.
- No LINQ, capturing lambdas, per-frame string formatting, or new heap objects in render, collect-visible, swap, or atlas request hot paths.
- Any new user-facing setting, diagnostic, or editor control must update docs in the same change.

## Definitions

- **Relevance camera set:** the active cameras whose rendered pixels can consume live shadows. Desktop main view, VR left eye, and VR right eye are the core inputs.
- **Point face relevance:** true when a point-light face frustum intersects at least one relevance camera frustum.
- **Spot relevance:** true when the spot shadow camera frustum intersects at least one relevance camera frustum.
- **Conservative intersection:** any uncertain, numerically unstable, or unsupported case returns relevant.
- **Face mask:** a six-bit mask where bit `i` means point shadow face `i` should render this frame.

## Phase 0: Branch, Baseline, And Acceptance Criteria

Goal: isolate the work and record today's behavior before changing scheduling.

- [ ] Create a dedicated branch, for example `local-shadow-frustum-culling`.
- [ ] Capture baseline metrics in a scene with many point and spot lights:
  - [ ] legacy point GS enabled
  - [ ] legacy point GS disabled
  - [ ] point atlas enabled
  - [ ] spot atlas enabled
  - [ ] spot atlas disabled
- [ ] Record per-frame counts:
  - [ ] point lights considered
  - [ ] point faces submitted
  - [ ] point faces rendered
  - [ ] spot lights submitted
  - [ ] spot lights rendered
  - [ ] shadow render time
  - [ ] atlas skipped requests by reason
- [ ] Define acceptance scenes:
  - [ ] point light where exactly one cube face intersects the camera view
  - [ ] point light where three adjacent faces intersect the camera view
  - [ ] point light behind the camera where no face intersects the view
  - [ ] spot light cone fully outside the camera view
  - [ ] spot light cone partially crossing the camera view edge
  - [ ] VR scene where a face is visible to only one eye
- [ ] Confirm no visible shadow popping in rapid camera turns through face boundaries.

## Phase 1: Shared Camera-Set And Frustum Relevance Utility

Goal: create one allocation-free relevance query used by atlas and legacy paths.

- [ ] Introduce a small reusable camera-set helper for local shadow relevance.
- [ ] Inputs:
  - [ ] active desktop/world viewports that target the world
  - [ ] VR left eye viewport when active
  - [ ] VR right eye viewport when active
  - [ ] optional mirror/capture/probe cameras only when explicitly flagged as relevance consumers
- [ ] Store prepared frusta in a reusable scratch list or fixed-capacity buffer owned by `Lights3DCollection`.
- [ ] Add point-face query:
  - [ ] `bool IsPointFaceRelevant(PointLightComponent light, int faceIndex, in ShadowRelevanceCameraSet cameras)`
  - [ ] uses the face `XRCamera.WorldFrustum().Prepare()` or cached equivalent
  - [ ] returns true when any relevance camera intersects that face frustum
- [ ] Add spot query:
  - [ ] `bool IsSpotShadowRelevant(SpotLightComponent light, in ShadowRelevanceCameraSet cameras)`
  - [ ] uses the actual `ShadowCamera.WorldFrustum()` when available
  - [ ] falls back to conservative cone/sphere relevance if the shadow camera does not exist yet
- [ ] Add a no-camera policy:
  - [ ] atlas keeps existing `NoConsumerCamera` behavior
  - [ ] legacy runtime rendering returns conservative relevant unless the world is explicitly headless/no-render
- [ ] Add geometry tests:
  - [ ] disjoint frusta returns false
  - [ ] touching/edge-overlap returns true
  - [ ] contained frustum returns true
  - [ ] invalid light/camera state returns conservative true

## Phase 2: Point Atlas Face Submission And Skip Reasons

Goal: stop submitting or rendering point atlas faces that cannot affect the camera set.

- [ ] Add `SkipReason.NotRelevant` to `ShadowAtlasTypes.cs`.
- [ ] Extend point-face request generation in `Lights3DCollection.SubmitPointShadowAtlasRequests`.
- [ ] For each face:
  - [ ] compute face relevance using the shared query
  - [ ] if not relevant and no previous resident tile is needed, do not submit the request
  - [ ] if not relevant and previous metadata should remain visible for diagnostics/fallback, publish a skipped allocation with `NotRelevant`
  - [ ] if relevant, submit normally and keep the existing priority/resolution logic
- [ ] Decide stale-tile behavior:
  - [ ] not relevant + previous resident tile -> may keep stale metadata but does not schedule rendering
  - [ ] relevant again + stale tile -> schedules a fresh render immediately
- [ ] Ensure receivers for missing point faces use the existing explicit fallback path, usually contact-only or lit, never undefined atlas samples.
- [ ] Add unit tests:
  - [ ] six-face point light with only one relevant face submits/renders one face
  - [ ] non-relevant face produces `NotRelevant` when metadata is published
  - [ ] previously skipped face becoming relevant schedules render on the next frame
  - [ ] no active atlas cameras still uses `NoConsumerCamera`, not `NotRelevant`

## Phase 3: Spot Atlas Submission

Goal: skip spot atlas work when the spot shadow frustum is outside the relevance camera set.

- [ ] Gate `SubmitSpotShadowAtlasRequests` with the shared spot relevance query.
- [ ] Decide whether non-relevant spot lights:
  - [ ] do not submit at all when no previous metadata is useful
  - [ ] publish `SkipReason.NotRelevant` when diagnostics or stale fallback require a record
- [ ] Keep moment-map behavior unchanged: VSM/EVSM spot lights that bypass the atlas must be handled by the legacy path phases.
- [ ] Add unit tests:
  - [ ] disjoint spot frustum submits no atlas request
  - [ ] intersecting spot frustum submits one `SpotPrimary` request
  - [ ] edge-overlap remains conservative and submits
  - [ ] diagnostics report `NotRelevant` when requested by the chosen metadata policy

## Phase 4: Legacy Point Non-GS Six-Pass Rendering

Goal: skip individual cubemap face passes in the non-GS path.

- [ ] Compute and cache the current point-face mask before `CollectVisibleItems`, `SwapBuffers`, and `RenderShadowMap`.
- [ ] In non-GS collection:
  - [ ] collect only relevant face viewports
  - [ ] leave skipped face command buffers untouched or explicitly clear them according to the stale-face policy
- [ ] In non-GS swap:
  - [ ] swap only relevant face viewports
- [ ] In non-GS render:
  - [ ] render only relevant face FBO attachments
  - [ ] skip the per-face framebuffer attachment setup for non-relevant faces
- [ ] If the relevance mask is empty, skip the point shadow render entirely.
- [ ] Add tests or source-contract checks:
  - [ ] render loop checks a face relevance mask before binding/rendering a face
  - [ ] collect and swap use the same mask source as render
  - [ ] empty mask short-circuits render

## Phase 5: Legacy Point GS Layer Masking

Goal: make the GS path avoid non-relevant cubemap layers without losing the single-draw fast path when most faces are relevant.

- [ ] Choose the GS partial-face strategy:
  - [ ] preferred: add a six-bit `PointShadowFaceMask` uniform consumed by `PointLightShadowDepth.gs`
  - [ ] fallback: when the mask is partial, temporarily use the non-GS per-face renderer for relevant faces only
- [ ] If using a GS mask:
  - [ ] upload `PointShadowFaceMask` in `PointLightComponent.SetShadowMapUniforms`
  - [ ] skip `gl_Layer` emission for masked-off faces
  - [ ] keep view-projection array uploads unchanged for relevant faces
  - [ ] skip the whole draw when the mask is zero
- [ ] Review collection for GS:
  - [ ] current sphere collection is conservative and acceptable for correctness
  - [ ] optionally add a union-of-relevant-face-frusta collection volume later if CPU collection cost remains high
- [ ] Add shader/source tests:
  - [ ] GS declares and uses `PointShadowFaceMask`
  - [ ] masked-off faces do not emit vertices
  - [ ] zero mask avoids the draw at the C# render-call level
- [ ] Validate with GS on and off in the same scene.

## Phase 6: Legacy Spot Rendering

Goal: skip standalone spot shadow-map rendering when the spot shadow frustum cannot affect the camera set.

- [ ] Add a relevance gate to `OneViewLightComponent` or `SpotLightComponent` without affecting directional primary shadows.
- [ ] For spot legacy collection:
  - [ ] skip `PrimaryShadowViewport.CollectVisible(false)` when not relevant
- [ ] For spot legacy swap:
  - [ ] skip `PrimaryShadowViewport.SwapBuffers()` when not relevant
- [ ] For spot legacy render:
  - [ ] skip `PrimaryShadowViewport.Render(...)` when not relevant
- [ ] Preserve VSM/EVSM standalone mip generation:
  - [ ] do not generate mips when the shadow map was not rendered
  - [ ] regenerate normally when the spot becomes relevant again
- [ ] Add tests/source checks:
  - [ ] spot relevance gate does not affect directional one-view lights
  - [ ] mip generation is conditional on an actual render
  - [ ] non-relevant spot render returns without touching the FBO

## Phase 7: Diagnostics And Editor Visibility

Goal: make skipped faces/frusta visible and debuggable.

- [ ] Add per-frame counters:
  - [ ] point faces relevant
  - [ ] point faces skipped as not relevant
  - [ ] point faces rendered
  - [ ] spot shadows skipped as not relevant
  - [ ] GS masks used
  - [ ] GS partial masks that fell back to non-GS, if applicable
- [ ] Extend ImGui light diagnostics:
  - [ ] point light face mask
  - [ ] atlas face skip reasons
  - [ ] spot relevance state
  - [ ] last relevant frame
- [ ] Add optional debug draw:
  - [ ] relevant point face frusta in one color
  - [ ] skipped point face frusta in muted color
  - [ ] spot frustum relevance state
- [ ] Rate-limit logs and avoid per-frame string allocations in normal runtime.
- [ ] Update architecture notes if new settings or user-visible diagnostics are added.

## Phase 8: Validation And Performance

Goal: prove the optimization saves work without visible regressions.

- [ ] Run targeted tests:
  - [ ] new local shadow frustum relevance tests
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ShadowAtlasManagerPhaseTests`
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter CascadedShadowDefaultsAndForwardShaderTests`
- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- [ ] Smoke validation in `Editor (Unit Testing World)`:
  - [ ] point light with GS enabled
  - [ ] point light with GS disabled
  - [ ] point atlas enabled
  - [ ] spot atlas enabled
  - [ ] spot atlas disabled
  - [ ] VSM/EVSM spot standalone path
  - [ ] VR stereo camera set
- [ ] Compare against Phase 0 baselines:
  - [ ] submitted atlas point faces reduced when faces are off-screen
  - [ ] rendered legacy point faces reduced in non-GS mode
  - [ ] GS layer emission reduced or fallback selected for partial masks
  - [ ] spot shadow renders skipped when fully off-screen
  - [ ] no visible receiver shadow drops at camera/frustum boundaries
- [ ] Confirm hot-path allocation expectations:
  - [ ] no per-frame allocations after warmup
  - [ ] no LINQ in relevance, request submission, collection, swap, or render paths
  - [ ] no captured delegates in hot paths

## Phase 9: Cross-Plan Reconciliation

Goal: keep the local optimization aligned with the broader shadow roadmap.

- [ ] Cross-link final behavior into [shadow-relevance-scoring-todo.md](shadow-relevance-scoring-todo.md).
- [ ] Update [dynamic-shadow-atlas-lod-todo.md](dynamic-shadow-atlas-lod-todo.md) for point-face skip/fallback parity.
- [ ] Update [shadow-filtering-vsm-evsm-todo.md](shadow-filtering-vsm-evsm-todo.md) if standalone moment spot rendering changes.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if behavior becomes user-visible or diagnostic settings are added.
- [ ] File follow-up work for receiver-visibility scoring if this TODO remains purely frustum-based.

## Final Task

- [ ] Merge the dedicated `local-shadow-frustum-culling` branch back into `main` after all phases are complete, validation has passed, docs are updated, and any follow-up TODOs have been filed.
