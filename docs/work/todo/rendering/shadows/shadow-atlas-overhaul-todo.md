# Shadow Atlas Overhaul TODO

Status: active master tracker, audited and rewritten 2026-06-18.

This is the working TODO for the dynamic shadow atlas, shadow-map update
scheduling, atlas-aware filtering, and related diagnostics. The older version
mixed current behavior, completed fixes, and future work; this rewrite separates
the live engine contract from stale notes and remaining tasks.

Current architecture references:

- [Default Render Pipeline notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Shadow Atlas Solve Efficiency TODO](shadow-atlas-solve-efficiency-todo.md)
- [Dynamic Shadow Atlas LOD Plan](../../../design/rendering/shadows/dynamic-shadow-atlas-lod-plan.md)
- [Shadow Filtering VSM/EVSM Plan](../../../design/rendering/shadows/shadow-filtering-vsm-evsm-plan.md)
- [Shadow Resource Migration Audit](../../../design/rendering/shadows/shadow-resource-migration-audit.md)
- [Post-v1 Advanced Shadow Features Plan](../../../design/rendering/shadows/post-v1-advanced-shadow-features-plan.md)

Primary code:

- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasFrameData.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/ShadowAtlasTypes.cs`
- `XREngine.Runtime.Rendering/Rendering/Shadows/LocalShadowFrustumRelevance.cs`
- `XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent*.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/SpotLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs`
- `Build/CommonAssets/Shaders/Snippets/ShadowSampling.glsl`
- `Build/CommonAssets/Shaders/Snippets/ShadowMomentEncoding.glsl`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`

Primary tests:

- `XREngine.UnitTests/Rendering/ShadowAtlasManagerPhaseTests.cs`
- `XREngine.UnitTests/Rendering/PointShadowAtlasStabilityTests.cs`
- `XREngine.UnitTests/Rendering/LocalShadowFrustumRelevanceTests.cs`
- `XREngine.UnitTests/Rendering/CascadedShadowDefaultsAndForwardShaderTests.cs`

## Current Runtime Contract

- Dynamic atlas ownership is scoped by light family and encoding. Each live
  `(AtlasKind, EShadowMapEncoding)` owns texture-array pages; page index alone
  is not globally unique, so consumers must use atlas kind plus encoding plus
  page, or the packed `AtlasId`.
- The allocator buckets requests by atlas kind and encoding, then solves each
  bucket with fixed-level buddy pages.
- Prior resident slots are strong placement hints. The solver can reserve a
  previous slot directly, reuse an aligned sub-rect after downsize, and keep a
  rendered previous tile when an upgrade cannot fit yet.
- A bounded resident table preserves recently missing requests so transient
  relevance or submission gaps do not immediately destroy placement stability.
- `SkipReason.NotRelevant` can preserve a stale resident tile, but stale
  reservations are applied only after live allocations. If a live request needs
  the old region, the stale request publishes a non-resident fallback.
- Balanced solve attempts are bounded. The current solver still resets page
  reservation state between attempts, but demotions are batched, page-sized
  candidates can demote together, and an attempt ceiling triggers deterministic
  fallback demotion with diagnostics.
- `ShadowAtlasFrameData` publishes allocations, grouped directional cascade
  records, grouped point-face records, directional light diagnostics, page
  descriptors, atlas metrics, and `ShadowAtlasSolveDiagnostics`.
- Metrics now include resident tile count, skipped requests, page count,
  resident bytes, tile scheduling, queue overflow, `NotRelevant` skips,
  largest free rect, free texels, directional grouped frames, and directional
  sequential fallback frames.
- Solver diagnostics now include request counts by light family and encoding,
  balanced attempts, failed candidates, demotions, sticky demotions,
  directional-group demotions, deterministic fallback demotions, prior reserve
  hits/misses, page allocation/create/clear counts, and group publishing work.
- `RequestRepack()` exists and is exposed from editor diagnostics, but automatic
  fragmentation-triggered compaction is not implemented.

Directional lights:

- Directional atlas requests include the resolved directional shadow encoding.
  This replaced the old hardcoded-depth request path for directional lights.
- On Vulkan directional shadow backends, `UsesDirectionalShadowAtlasForCurrentEncoding`
  is currently depth-only. Non-Vulkan directional atlas paths accept `Depth`,
  `Variance2`, `ExponentialVariance2`, and `ExponentialVariance4`, but moment
  atlas quality and validation are still incomplete.
- Directional primary and cascade atlas slots are published separately.
- Four equal-resolution directional cascades can be pre-reserved as a same-page
  2x2 group before independent allocation.
- Grouped directional atlas rendering requires same-page group metadata,
  indexed viewport/scissor support, and a selected cascade render mode that can
  write viewport indices. Sequential per-tile rendering remains the fallback.
- `CascadeShadowRenderMode` still defaults to `Sequential`; `Auto`,
  `InstancedLayered`, and `GeometryShader` can select grouped/layered paths
  where backend capabilities allow it.
- Receiver binding enables directional atlas sampling only when required slots
  are sampleable, and legacy maps are not silently sampled once atlas mode is
  authoritative.

Spot lights:

- Spot atlas mode is depth-only. Spot VSM/EVSM lights bypass the atlas and use
  standalone moment maps.
- Spot atlas requests can be forced `NotRelevant` from local shadow-frustum
  relevance. Standalone moment spot renders and mip regeneration are also
  skipped while the spot frustum is not relevant.

Point lights:

- Point atlas mode is depth-only. Point VSM/EVSM lights bypass the atlas and
  use standalone cubemap moment maps.
- Point atlas submission expands into per-face `PointFace` requests. Each face
  is tested against the local shadow relevance camera set and can publish
  `NotRelevant` independently.
- Point face desired resolution is demoted from camera-face alignment.
- Same-page point face groups are published and can render through indexed
  viewport/scissor atlas paths when the selected point render mode and backend
  support it. This is still opportunistic; a heterogeneous pre-reservation
  solver does not exist yet.
- Point atlas receivers are cube-seam aware. Filtering taps perturb the sample
  direction, reselect the owning face, and sample that face's tile metadata
  instead of clamping across the original tile edge.

Moment encodings:

- `ShadowMomentEncoding.glsl` contains VSM/EVSM encode and sampling helpers,
  point radial moment writing, Chebyshev visibility, and 2D/array/cube moment
  receivers.
- `LightComponent` moment settings use `SetField(...)`.
- Depth filtering modes (`Hard`, Poisson, Vogel, PCSS/contact hardening) are
  separate from moment encodings. Moment maps use moment parameters, mips, and
  blur; contact shadows multiply on top of either path.

## Stale Audit

These old TODO statements are stale or misleading:

- Directional atlas is not globally depth-only anymore. Vulkan directional
  atlas remains depth-only for moment encodings, but non-Vulkan directional
  requests now carry resolved moment encodings.
- `SubmitShadowAtlasRequest` is no longer a directional hardcoded-depth bug.
  Spot and point still use default depth because their atlas gates are
  intentionally depth-only.
- The focused atlas tests are no longer blocked by missing
  `RuntimeShaderServices.Current`; the shadow atlas tests install test shader
  services and test render host services.
- The previously named failures
  `SolveAllocations_ReusesResidentTileAfterTransientMissingRequest`,
  `SolveAllocations_ReusesNotRelevantStaleTileWhenRegionRemainsFree`, and
  `Submit_WhenQueueIsFullPublishesQueueOverflowDiagnostic` now have explicit
  tests in `ShadowAtlasManagerPhaseTests`.
- Solve retry is not the old unbounded one-demotion-per-full-restart path.
  It still resets allocator state between attempts, but the demotion and
  fallback behavior is bounded and instrumented.
- Group publishing is no longer repeated whole-list scanning per seed. It uses
  keyed build maps and pooled member arrays.
- Point atlas face-seam filtering is no longer future work; shader and source
  contract tests cover seam-aware point atlas sampling.
- Basic fragmentation metrics are no longer wholly missing; largest free rect
  and free texel counts are published. Automatic compaction policy is still
  open.
- Directional grouped-frame and sequential-fallback counters exist.
- Per-light atlas diagnostics exist, but they do not yet expose all relevance
  score inputs, churn history, and grouped-render decision details needed for
  editor-grade triage.

These old concerns remain current:

- Broad receiver-aware relevance scoring is still missing. The solver's
  `ResolveRelevanceScore` still derives from request priority.
- Directional cascade relevance is not independently receiver-aware.
- Directional grouped atlas rendering is available but not the default steady
  state; `CascadeShadowRenderMode` defaults to sequential.
- Partial directional groups and grouped demotion policy are incomplete.
- Point face group pre-reservation is still absent; grouped rendering depends
  on independently allocated faces landing on the same page.
- Automatic fragmentation-triggered repack/compaction is not implemented.
- Anchor/pinned slots are not implemented.
- Unified GPU `ShadowAtlasTile` metadata for every receiver path is still
  incomplete.
- Local spot/point VSM/EVSM atlas support is still open.
- Vulkan directional moment atlas support is still open.
- Live editor performance validation is still required; unit/source-contract
  tests do not prove visual quality or frame time.

## DLSS And Shadow Atlas Policy

DLSS Frame Generation must not be treated as a shadow-map or atlas generator.
It operates on final presented frames with final-frame inputs such as color,
depth, motion, and optical-flow data. Shadow maps, atlas pages, and atlas
allocation are engine-owned intermediate resources.

Use NVIDIA features only at the appropriate stage:

- DLSS Super Resolution, DLAA, and Frame Generation belong after lighting/post
  as presentation or anti-aliasing accelerators.
- DLSS Ray Reconstruction may be relevant to future ray-traced shadow or GI
  denoising, but it is not a replacement for classic shadow-map atlas updates.
- Shadow atlas update cadence, invalidation, caching, fallback, and diagnostics
  must remain explicit engine behavior. Do not hide missing GPU/accelerated
  paths behind silent CPU or presentation-layer fallbacks.

The aggressive path for shadow maps is a temporal shadow-cache system:

- Update far directional cascades less often than near cascades when receivers
  are stable.
- Freeze static-caster atlas pages until light, caster set, material state, or
  receiver contract changes.
- Track separate static and dynamic caster contributions, then composite small
  dynamic overlay tiles over stable static tiles when profiling justifies it.
- Invalidate tiles by light movement, projection/camera-fit movement, caster
  bounds/material changes, receiver relevance changes, and atlas encoding
  changes.
- Reproject cached shadow visibility in screen space only for final shadow
  visibility or denoising, not by warping raw depth atlas texels.
- Use temporal filtering on the final shadow visibility term with disocclusion,
  receiver motion, light motion, and cascade/tile generation rejection.
- Prefer virtual/sparse atlas pages or page-table-style residency for very large
  scenes after v1 atlas behavior is stable.
- Render high-frequency dynamic casters into smaller overlay tiles or force
  near-field refresh rather than refreshing the full static tile every frame.

## Remaining Workstreams

### A. Validation And Baseline

- [ ] Run the current focused unit tests and record results in this doc:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlasManagerPhaseTests|FullyQualifiedName~PointShadowAtlasStabilityTests|FullyQualifiedName~LocalShadowFrustumRelevanceTests|FullyQualifiedName~CascadedShadowDefaultsAndForwardShaderTests"
  ```

- [ ] Build the runtime/editor after atlas changes:

  ```powershell
  dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- [ ] Capture a live Unit Testing World baseline for atlas-on vs atlas-off:
  solve time, shadow tiles rendered per frame, directional sequential fallback
  frames, grouped frames, render-stall logs, and FPS.
- [ ] Add or refresh visual validation scenes for:
  directional cascades, directional moment encodings, many spots, point atlas
  partial residency, point face-boundary sampling, masked casters, stale-tile
  fallback, and one-page pressure.
- [ ] Add editor smoke tests for moving one spot light and one point light with
  atlas mode enabled; moved-light shadows must refresh instead of displaying
  stale transformed tiles.

### B. Receiver-Aware Relevance

- [ ] Replace priority-only `ResolveRelevanceScore` with a real
  `ShadowRelevanceScore`.
- [ ] Reuse existing visible/culling data; do not add a broad new visibility
  pass for v1 unless profiling proves it is needed.
- [ ] Score from visible receivers, not only caster visibility. Off-screen
  casters that affect visible receivers must remain relevant.
- [ ] Directional cascades: score each cascade from the cascade slice and the
  visible receiver bounds it can affect.
- [ ] Spot lights: score cone/frustum intersection, projected screen influence,
  receiver overlap, distance, brightness, and stale-tile age.
- [ ] Point faces: combine face-frustum receiver overlap with the current
  camera-face alignment estimate.
- [ ] Preserve VR stability by scoring against both eyes and any contributing
  mirror camera.
- [ ] Define when zero-score cascades/faces can publish `NotRelevant` and what
  fallback the receiver must use.

Acceptance:

- [ ] Static off-screen local lights with no visible receivers do not consume
  full-resolution atlas tiles.
- [ ] A visible receiver shadowed by an off-screen caster keeps the relevant
  light tile resident.
- [ ] Far directional cascades that do not affect visible receivers demote or
  skip independently of near cascades.

### C. Allocation, Grouping, And Paging

- [ ] Preserve reusable page/free-block state between balanced solve attempts
  when only request levels changed and page topology did not need a full reset.
- [ ] Track the lowest failing size per atlas kind/encoding and skip
  immediately impossible candidates in the next attempt.
- [ ] Support partial directional groups when fewer than four cascades are
  active or relevant.
- [ ] If a directional group cannot fit, demote lower-relevance cascades before
  falling back to independent sequential tiles.
- [ ] Add heterogeneous point-face group pre-reservation:
  `TryReservePointLightFaceGroup(light, faceSizes[6])`.
- [ ] Sort point faces by relevance, then pack them deterministically inside
  the smallest containing power-of-two block.
- [ ] Keep point face atomicity partial: individual faces may demote, skip, or
  evict under pressure.
- [ ] Add automatic repack only on failed allocation, explicit editor request,
  or sustained high fragmentation.
- [ ] Increment `ShadowAtlasFrameData.Generation` on repack and publish the
  repack reason plus affected atlas family/encoding.
- [ ] Add anchor/pinned slots after relevance and grouping are stable.

Acceptance:

- [ ] Four equal-resolution directional cascades allocate as one page-coherent
  group and can render in one grouped pass when backend/mode support exists.
- [ ] One full-resolution point face plus five quarter-resolution faces can
  form a deterministic same-page group.
- [ ] Repack never invalidates metadata still being sampled in the same frame.

### D. Directional Atlas Performance

- [ ] Decide whether `CascadeShadowRenderMode` should move from `Sequential` to
  `Auto` for v1 after grouped atlas validation.
- [ ] Ensure grouped atlas rendering remains available on OpenGL 4.6 hardware
  with indexed viewport/scissor and vertex-stage or geometry-stage viewport
  index support.
- [ ] Revisit directional critical-refresh budget bypass. First render can
  bypass budget; steady-state projection jitter should not force every cascade
  to refresh every frame.
- [ ] Keep receiver binding conservative: atlas sampling is enabled only when
  every required active tile is sampleable or has an explicit fallback.
- [ ] Profile sequential tile rendering versus legacy layered cascades and
  grouped atlas cascades in the same scene.

Acceptance:

- [ ] With a static camera and one 4-cascade directional light, atlas mode
  renders no more cascade passes per frame than legacy after warmup when grouped
  rendering is selected and supported.
- [ ] With grouped rendering supported, atlas-on framerate is within 10 percent
  of atlas-off legacy layered cascades for the same Unit Testing World scene.
- [ ] When grouped rendering is unavailable, logs state the exact fallback
  reason and the editor exposes the effective backend/mode.

### E. Moment Encodings And Filtering

- [ ] Keep the explicit contract: `Depth` encoding uses hard/Poisson/Vogel/PCSS
  depth compare filters; VSM/EVSM encodings use moment parameters, mips, and
  blur, not PCSS kernel radii.
- [ ] Rename `ESoftShadowMode` / `SoftShadowMode` to depth-filter terminology
  before v1 if the API cleanup window is still open.
- [ ] Implement local spot VSM/EVSM atlas pages:
  color moment atlas, separate raster depth attachment, filter params, mip/blur
  generation, and atlas metadata receiver sampling.
- [ ] Implement local point VSM/EVSM atlas pages with radial moments per face,
  per-face near/far metadata, and seam-aware filtering parity with standalone
  cubemaps.
- [ ] Complete directional moment atlas validation on non-Vulkan and implement
  or explicitly keep bypassing Vulkan directional moment atlases.
- [ ] Make directional VSM/EVSM cascaded by default before judging quality
  against depth cascades.
- [ ] Blend cascade visibility values, never raw moment vectors.
- [ ] Add tile-aware separable blur and/or mip generation that clamps to tile
  inner rects and clears gutters with encoding-specific sentinels.
- [ ] Add moment atlas debug views for M1, M2, EVSM warped channels, variance,
  bleed mask, and clear sentinel.
- [ ] Clamp or disable derivative-derived moment variance for cube faces,
  atlas tiles, cascades, and masked casters.

Acceptance:

- [ ] Mixed depth/VSM/EVSM scene renders correctly in deferred and forward.
- [ ] Runtime encoding flips do not sample stale or dummy atlas pages.
- [ ] Directional cascade transition bands blend filtered visibility and match
  per-cascade ground truth.
- [ ] Spot and point moment atlas filtering does not bleed across tile gutters
  or point face boundaries.

### F. Temporal Shadow Cache And Static/Dynamic Split

- [ ] Add per-request static caster set and dynamic caster set tracking.
- [ ] Add caster/material state to `ContentHash` inputs.
- [ ] Measure redraw cost in representative static-heavy scenes before picking
  the cache shape.
- [ ] Choose the v1 cache model:
  single stable tile with content-hash reuse, two-tile static/dynamic
  composition, or no split until profiling justifies it.
- [ ] If split caching is enabled, refresh static pages only when the light,
  static caster set, material state, encoding, or receiver contract changes.
- [ ] Composite dynamic movers over static cache with deterministic invalidation
  and visible diagnostics.
- [ ] Add cadence controls for far directional cascades and low-relevance local
  faces, with motion/disocclusion rejection.
- [ ] Add optional screen-space temporal filtering for final shadow visibility,
  rejecting history on receiver motion, light motion, tile generation changes,
  cascade changes, disocclusion, and normal/depth disagreement.
- [ ] Keep raw shadow depth maps unwarped. Reprojection belongs to final
  visibility history, not atlas page contents.

Acceptance:

- [ ] Static-heavy scenes reduce shadow tile renders after warmup without stale
  moved-light or moved-caster artifacts.
- [ ] Dynamic movers near static casters update without forcing full static
  tile refresh every frame.
- [ ] Temporal visibility history rejects correctly on camera cuts, tile
  repacks, light movement, and cascade refits.

### G. Diagnostics And Editor Visibility

- [ ] Extend per-light atlas diagnostics with score inputs, requested and
  allocated resolution, skip reason, fallback mode, resident age, last rendered
  frame, page, rect, encoding, churn count, and dirty reason.
- [ ] Expose directional grouped-render state in the editor: selected backend,
  requested/effective mode, fallback reason, grouped/ungrouped pass count, and
  elapsed shadow time.
- [ ] Add point-face slot churn diagnostics and same-page group status.
- [ ] Add an atlas occupancy panel with owner, LOD, dirty state, encoding,
  last rendered frame, and fallback.
- [ ] Add automatic slow-solve and slow-render log summaries that avoid
  per-frame string formatting unless logging is enabled and rate-limited.
- [ ] Keep solve cost and render-tile cost separate in profiler output.

### H. Contact Shadow Optimization

Contact shadows are separate from atlas visibility and remain multiplied on top
of depth or moment map visibility.

- [ ] Refactor `XRENGINE_SampleContactShadowScreenSpace` to avoid per-sample
  world-space reprojection. Compute clip/UV/depth deltas once and march in
  screen or view space.
- [ ] Compare in view space or linear depth without reconstructing sample world
  position in every loop iteration.
- [ ] Audit early-outs: `dot(N, L) <= 0`, beyond fade range, invalid depth, and
  unavailable contact depth textures.
- [ ] Revisit default contact-shadow sample counts only after visual and
  profiler captures.
- [ ] Consider a half-res per-light contact-shadow pass only after the shader
  helper refactor is measured.

## Closeout Checklist

- [ ] Current atlas unit/source-contract tests pass.
- [ ] Runtime rendering and editor builds pass without new warnings.
- [ ] Directional atlas performance is measured and no longer unexpectedly
  halves frame rate versus legacy layered cascades in the same scene.
- [ ] Spot and point depth atlases are visually validated in deferred and
  forward, including oversubscription and stale fallback.
- [ ] Point atlas face-boundary filtering is visually validated, not only
  source-tested.
- [ ] Directional VSM/EVSM behavior is documented per backend: complete atlas
  path, explicit Vulkan bypass, or tracked blocker.
- [ ] Local VSM/EVSM atlas support is either implemented or explicitly out of
  v1 with standalone fallback documented.
- [ ] Temporal shadow-cache work is explicitly separated from DLSS FrameGen and
  validated as engine-owned shadow scheduling.
- [ ] Editor diagnostics expose enough state to explain allocation, fallback,
  grouped rendering, and tile refresh decisions without reading source.
- [ ] Documentation is updated when settings, editor labels, launch flags,
  diagnostics, or shadow encoding behavior changes.
