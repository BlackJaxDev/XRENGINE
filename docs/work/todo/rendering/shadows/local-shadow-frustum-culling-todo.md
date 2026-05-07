# Local Shadow Frustum Culling TODO

> Status: **implemented in code; validation/manual smoke still pending**
> Last reconciled: **2026-05-07**
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

This is a narrow tactical improvement that can land before the broader [Shadow Atlas Major Overhaul TODO](shadow-atlas-overhaul-todo.md). The larger relevance system inside that overhaul should eventually own the same camera-set and receiver-driven concepts.

## Source Context

- [Dynamic Shadow Atlas And LOD Allocation TODO](dynamic-shadow-atlas-lod-todo.md)
- [Shadow Atlas Major Overhaul TODO](shadow-atlas-overhaul-todo.md)
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

As of 2026-05-07, the runtime code now implements the tactical local-frustum slice:

- `Lights3DCollection` builds a reusable local shadow relevance camera set from active world viewports and VR eye viewports.
- `LocalShadowFrustumRelevance` provides conservative point-face and spot-frustum intersection queries.
- Point lights cache a six-bit `ShadowFaceRelevanceMask`, skip non-relevant legacy cubemap faces, and upload compact face indices for layered/GS rendering.
- Spot lights cache `ShadowFrustumRelevant`; non-relevant standalone spot renders skip collection, swap, render, and moment-map mip regeneration.
- Point atlas faces and spot atlas requests can publish `SkipReason.NotRelevant`; previously resident local tiles stay available as stale fallback metadata when useful.
- ImGui light diagnostics show point relevance/render masks and spot relevance state.

Remaining non-code validation is still open: baseline captures, editor visual smoke, VR stereo smoke, and the final branch merge back to `main`.
Targeted runtime/editor builds passed on 2026-05-07. The unit-test project currently fails to compile before test filters run because of unrelated project-wide test compile errors.

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

- [x] Create a dedicated branch, for example `local-shadow-frustum-culling`.
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

- [x] Introduce a small reusable camera-set helper for local shadow relevance.
- [ ] Inputs:
  - [x] active desktop/world viewports that target the world
  - [x] VR left eye viewport when active
  - [x] VR right eye viewport when active
  - [ ] optional mirror/capture/probe cameras only when explicitly flagged as relevance consumers
- [x] Store prepared frusta in a reusable scratch list or fixed-capacity buffer owned by `Lights3DCollection`.
- [x] Add point-face query:
  - [x] `bool IsPointFaceRelevant(PointLightComponent light, int faceIndex, in ShadowRelevanceCameraSet cameras)`
  - [x] uses the face `XRCamera.WorldFrustum().Prepare()` or cached equivalent
  - [x] returns true when any relevance camera intersects that face frustum
- [x] Add spot query:
  - [x] `bool IsSpotShadowRelevant(SpotLightComponent light, in ShadowRelevanceCameraSet cameras)`
  - [x] uses the actual `ShadowCamera.WorldFrustum()` when available
  - [x] falls back to conservative cone/sphere relevance if the shadow camera does not exist yet
- [x] Add a no-camera policy:
  - [x] atlas keeps existing `NoConsumerCamera` behavior
  - [x] legacy runtime rendering returns conservative relevant unless the world is explicitly headless/no-render
- [x] Add geometry tests:
  - [x] disjoint frusta returns false
  - [x] touching/edge-overlap returns true
  - [x] contained frustum returns true
  - [x] invalid light/camera state returns conservative true

## Phase 2: Point Atlas Face Submission And Skip Reasons

Goal: stop submitting or rendering point atlas faces that cannot affect the camera set.

- [x] Add `SkipReason.NotRelevant` to `ShadowAtlasTypes.cs`.
- [x] Extend point-face request generation in `Lights3DCollection.SubmitPointShadowAtlasRequests`.
- [ ] For each face:
  - [x] compute face relevance using the shared query
  - [ ] if not relevant and no previous resident tile is needed, do not submit the request
  - [x] if not relevant and previous metadata should remain visible for diagnostics/fallback, publish a skipped allocation with `NotRelevant`
  - [x] if relevant, submit normally and keep the existing priority/resolution logic
- [x] Decide stale-tile behavior:
  - [x] not relevant + previous resident tile -> may keep stale metadata but does not schedule rendering
  - [x] relevant again + stale tile -> schedules a fresh render immediately
- [x] Ensure receivers for missing point faces use the existing explicit fallback path, usually contact-only or lit, never undefined atlas samples.
- [ ] Add unit tests:
  - [ ] six-face point light with only one relevant face submits/renders one face
  - [ ] non-relevant face produces `NotRelevant` when metadata is published
  - [ ] previously skipped face becoming relevant schedules render on the next frame
  - [ ] no active atlas cameras still uses `NoConsumerCamera`, not `NotRelevant`

## Phase 3: Spot Atlas Submission

Goal: skip spot atlas work when the spot shadow frustum is outside the relevance camera set.

- [x] Gate `SubmitSpotShadowAtlasRequests` with the shared spot relevance query.
- [ ] Decide whether non-relevant spot lights:
  - [ ] do not submit at all when no previous metadata is useful
  - [x] publish `SkipReason.NotRelevant` when diagnostics or stale fallback require a record
- [x] Keep moment-map behavior unchanged: VSM/EVSM spot lights that bypass the atlas must be handled by the legacy path phases.
- [ ] Add unit tests:
  - [ ] disjoint spot frustum submits no atlas request
  - [ ] intersecting spot frustum submits one `SpotPrimary` request
  - [ ] edge-overlap remains conservative and submits
  - [ ] diagnostics report `NotRelevant` when requested by the chosen metadata policy

## Phase 4: Legacy Point Non-GS Six-Pass Rendering

Goal: skip individual cubemap face passes in the non-GS path.

- [x] Compute and cache the current point-face mask before `CollectVisibleItems`, `SwapBuffers`, and `RenderShadowMap`.
- [ ] In non-GS collection:
  - [x] collect only relevant face viewports
  - [ ] leave skipped face command buffers untouched or explicitly clear them according to the stale-face policy
- [ ] In non-GS swap:
  - [x] swap only relevant face viewports
- [x] In non-GS render:
  - [x] render only relevant face FBO attachments
  - [x] skip the per-face framebuffer attachment setup for non-relevant faces
- [x] If the relevance mask is empty, skip the point shadow render entirely.
- [x] Add tests or source-contract checks:
  - [x] render loop checks a face relevance mask before binding/rendering a face
  - [x] collect and swap use the same mask source as render
  - [x] empty mask short-circuits render

## Phase 5: Legacy Point GS Layer Masking

Goal: make the GS path avoid non-relevant cubemap layers without losing the single-draw fast path when most faces are relevant.

- [x] Choose the GS partial-face strategy:
  - [x] preferred: add a six-bit `PointShadowFaceMask` uniform consumed by `PointLightShadowDepth.gs`
  - [ ] fallback: when the mask is partial, temporarily use the non-GS per-face renderer for relevant faces only
- [x] If using a GS mask:
  - [x] upload `PointShadowFaceMask` in `PointLightComponent.SetShadowMapUniforms`
  - [x] skip `gl_Layer` emission for masked-off faces
  - [x] keep view-projection array uploads unchanged for relevant faces
  - [x] skip the whole draw when the mask is zero
- [x] Review collection for GS:
  - [x] current sphere collection is conservative and acceptable for correctness
  - [ ] optionally add a union-of-relevant-face-frusta collection volume later if CPU collection cost remains high
- [x] Add shader/source tests:
  - [x] GS declares and uses `PointShadowFaceMask`
  - [x] masked-off faces do not emit vertices
  - [x] zero mask avoids the draw at the C# render-call level
- [ ] Validate with GS on and off in the same scene.

## Phase 6: Legacy Spot Rendering

Goal: skip standalone spot shadow-map rendering when the spot shadow frustum cannot affect the camera set.

- [x] Add a relevance gate to `OneViewLightComponent` or `SpotLightComponent` without affecting directional primary shadows.
- [ ] For spot legacy collection:
  - [x] skip `PrimaryShadowViewport.CollectVisible(false)` when not relevant
- [ ] For spot legacy swap:
  - [x] skip `PrimaryShadowViewport.SwapBuffers()` when not relevant
- [ ] For spot legacy render:
  - [x] skip `PrimaryShadowViewport.Render(...)` when not relevant
- [x] Preserve VSM/EVSM standalone mip generation:
  - [x] do not generate mips when the shadow map was not rendered
  - [x] regenerate normally when the spot becomes relevant again
- [x] Add tests/source checks:
  - [x] spot relevance gate does not affect directional one-view lights
  - [x] mip generation is conditional on an actual render
  - [x] non-relevant spot render returns without touching the FBO

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
  - [x] point light face mask
  - [ ] atlas face skip reasons
  - [x] spot relevance state
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
  - [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
  - [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
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

- [x] Cross-link final behavior into [shadow-atlas-overhaul-todo.md](shadow-atlas-overhaul-todo.md).
- [x] Update [dynamic-shadow-atlas-lod-todo.md](dynamic-shadow-atlas-lod-todo.md) for point-face skip/fallback parity.
- [x] Update [shadow-filtering-vsm-evsm-todo.md](shadow-filtering-vsm-evsm-todo.md) if standalone moment spot rendering changes.
- [x] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if behavior becomes user-visible or diagnostic settings are added.
- [ ] File follow-up work for receiver-visibility scoring if this TODO remains purely frustum-based.

## Final Task

- [ ] Merge the dedicated `local-shadow-frustum-culling` branch back into `main` after all phases are complete, validation has passed, docs are updated, and any follow-up TODOs have been filed.
