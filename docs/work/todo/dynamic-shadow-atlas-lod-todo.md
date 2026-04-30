# Dynamic Shadow Atlas And LOD Allocation TODO

> Status: **active phased TODO**
> Scope: runtime shadows, renderer integration, editor diagnostics, shader metadata.

## Target Outcome

Replace fixed per-light shadow textures with a budgeted shadow atlas system:

- Lights submit shadow requests instead of owning every live render target.
- The atlas manager chooses active requests, tile resolution, update cadence, and fallback behavior under memory and render-time budgets.
- Directional cascades, spot lights, and point-light faces share one request/allocation model.
- Receiver shaders sample atlas textures plus metadata, with no undefined reads when a request is skipped.
- The v1 receiver contract can later move to Virtual Shadow Maps without another major shader API break.

"Any number of shadow-capable lights" means unbounded scene inputs with bounded active GPU work, not unlimited GPU memory.

## Non-Negotiable Design Rules

- [ ] Atlas depth data is stored as linear normalized depth in color textures, not hardware depth-comparison textures.
- [ ] Atlas mode uses manual depth compare and software filtering; hardware PCF remains only for legacy fallback resources.
- [ ] Bias is derived from allocated tile resolution and per-tile texel size, never from a global constant.
- [ ] Every skipped request publishes an explicit fallback: `Lit`, `ContactOnly`, `StaleTile`, or `Disabled`.
- [ ] Tile gutters are included in allocation rects; receiver sampling uses only inner rects.
- [ ] Directional cascade matrices are texel-snapped at the allocated resolution.
- [ ] Point lights become six independent 2D face requests in atlas mode.
- [ ] `Submit(in ShadowMapRequest)` performs no heap allocation after warmup.
- [ ] Allocation, sorting, packing, and frame-data publish perform no per-frame hot-path allocations after warmup.
- [ ] `ShadowAtlasFrameData` and the GPU metadata buffer are double-buffered and carry a generation counter.
- [ ] Probe captures do not consume live atlas shadows by default.
- [ ] VR directional cascade fitting uses the union of both eye frusta.
- [ ] Receiver metadata keeps a reserved virtual-page field for the later Virtual Shadow Maps path.

## Core Data Contracts

Add or converge on these public runtime concepts during the early phases.

```csharp
public readonly record struct ShadowMapRequest(
    ShadowRequestKey Key,
    LightComponent Light,
    EShadowProjectionType ProjectionType,
    EShadowMapEncoding Encoding,
    ShadowCasterFilterMode CasterMode,
    ShadowFallbackMode Fallback,
    int FaceOrCascadeIndex,
    Matrix4x4 ViewMatrix,
    Matrix4x4 ProjectionMatrix,
    float NearPlane,
    float FarPlane,
    uint DesiredResolution,
    uint MinimumResolution,
    float Priority,
    ulong ContentHash,
    bool IsDirty,
    bool CanReusePreviousFrame,
    bool EditorPinned,
    StereoVisibility StereoVis);
```

```csharp
public readonly record struct ShadowAtlasAllocation(
    ShadowRequestKey Key,
    int AtlasId,
    int PageIndex,
    IntRect PixelRect,
    IntRect InnerPixelRect,
    Vector4 UvScaleBias,
    uint Resolution,
    int LodLevel,
    ulong ContentVersion,
    ulong LastRenderedFrame,
    bool IsResident,
    bool IsStaticCacheBacked,
    ShadowFallbackMode ActiveFallback,
    SkipReason SkipReason);
```

```glsl
struct ShadowAtlasTile
{
    vec4 uvScaleBias;
    vec4 depthParams;   // near, far, texelSize, fallbackMode
    vec4 biasParams;    // constant, slope, normal offset, receiver-plane flag
    vec4 filterParams;  // radius, min variance, bleed reduction, mip bias
    ivec4 packed0;      // page/layer, encoding, projection type, flags
    ivec4 packed1;      // light index, face/cascade index, lod, debug mode
    ivec4 vsmPacked;    // reserved for Virtual Shadow Maps; zero in atlas v1
    mat4 worldToShadow;
};
```

## Recommended Defaults

- [ ] Page size: `4096 x 4096`.
- [ ] Max pages: configurable, default `2`.
- [ ] Tile sizes: `4096`, `2048`, `1024`, `512`, `256`, `128`.
- [ ] Gutter: `2-8` texels based on filter mode.
- [ ] Depth atlas format: `R16f` first, `R32f` option.
- [ ] Moment atlas formats: `RG16f` for VSM/EVSM2, `RGBA16f` for EVSM4.
- [ ] Buddy allocator first; shelf allocator can be added later behind a setting if fragmentation data justifies it.
- [ ] Single-tile full redraw is the v1 static/dynamic caster behavior; static cache split is a later optimization.

## Phase 0: Migration Audit And Policy Decisions

**Goal:** make the ownership change explicit before touching rendering behavior.

### Tasks

- [x] Audit all consumers of current per-light shadow resources:
  - [x] `LightComponent.ShadowMap`
  - [x] `*.ShadowDepthTexture`
  - [x] `Lights3DCollection.RenderShadowMaps`
  - [x] directional cascade arrays
  - [x] spot and point shadow map bindings
  - [x] debug overlays and light inspectors
  - [x] screenshot/capture tooling
  - [x] baking, GI, probes, water, particles, decals, fog
- [x] Add a migration table to this doc or a linked work note with one row per consumer.
- [x] Decide atlas budget policy per encoding with the VSM/EVSM plan:
  - [x] pages per encoding,
  - [x] memory cap behavior,
  - [x] whether pressure demotes encoding first or LOD first.
- [x] Decide runtime ownership for encoding flips: retire old allocation and issue a new request keyed by encoding.
- [x] Decide initial public settings names:
  - [x] `MaxShadowTilesRenderedPerFrame`
  - [x] `MaxShadowRenderMilliseconds`
  - [x] `MaxShadowAtlasPages`
  - [x] `ShadowAtlasPageSize`
  - [x] `MaxShadowAtlasMemoryBytes`
  - [x] `MinShadowAtlasTileResolution`
  - [x] `MaxShadowAtlasTileResolution`
- [x] Define `ShadowRequestKey` stability rules for every light/projection type.
- [x] Define `SkipReason` values for diagnostics.

Phase 0 decisions and the migration table are captured in [Shadow Resource Migration Audit](../design/shadow-resource-migration-audit.md).

### Exit Criteria

- [x] No current shadow consumer is undocumented.
- [x] Encoding/budget coupling with `shadow-filtering-vsm-evsm-plan.md` is resolved.
- [x] No code path is scheduled for removal without a fallback or migration note.

### Validation

- [x] `rg "ShadowMap|ShadowDepthTexture|RenderShadowMaps|CascadedShadow" XRENGINE XREngine.Editor Build docs` scoped with generated/dependency exclusions; unbounded scan timed out as noted in the audit.
- [x] Review `.vscode/tasks.json` and launch profiles for any shadow/debug flows affected by the migration.

## Phase 1: Request Model And Diagnostics

**Goal:** lights produce deterministic requests while existing per-light rendering remains unchanged.

### Tasks

- [x] Add `ShadowMapRequest`, `ShadowRequestKey`, `ShadowFallbackMode`, `ShadowCasterFilterMode`, and `StereoVisibility`.
- [x] Implement stable keys:
  - [x] directional primary: light id + `DirectionalPrimary`
  - [x] directional cascade: light id + cascade index
  - [x] spot: light id
  - [x] point face: light id + cube face index
- [x] Add request generation for directional, spot, and point lights without changing sampling.
- [ ] Compute desired and minimum resolution from projected size, light importance, cascade index, point-face visibility, and editor pinning.
  - [x] Initial deterministic heuristic uses current shadow-map resolution, active camera distance/brightness for local lights, cascade index, and atlas min/max settings.
  - [ ] Add projected-screen-area scoring, per-face frustum visibility, and editor pinning.
- [x] Add deterministic priority scoring.
- [ ] Add `ContentHash` inputs:
  - [x] light transform/settings,
  - [x] projection/radius/cascade data,
  - [x] shadow encoding/filter settings,
  - [ ] caster set and material state,
  - [x] cascade camera fit.
- [ ] Add inspector/debug output for requested resolution, priority, dirty reason, and fallback preference.
  - [x] Per-light ImGui diagnostics show request count, resident count, max requested/allocated resolution, priority, fallback, and skip reason.
  - [ ] Add explicit dirty-reason reporting.
- [x] Keep existing `RenderShadowMap` methods as the active render path.

### Exit Criteria

- [x] Enabling diagnostics does not alter rendered shadows.
- [x] Request order is deterministic for fixed scene input.
- [x] Point lights report exactly six face requests in atlas mode diagnostics.
- [x] Directional lights report one request per active cascade.

### Validation

- [x] Add unit tests for stable keys and LOD choice boundaries.
- [ ] Add tests for point-face and cascade request expansion.
- [ ] Run targeted rendering/unit tests for light components.
  - [ ] `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlas"` currently cannot execute because unrelated unit-test sources fail to compile (`Audio2Face3DComponent`, `OVRLipSyncComponent`, `HumanoidComponent`, `VRIKSolverComponent`, and `TransformAccessorFastPathTests` API drift).

## Phase 2: Atlas Manager, Resources, And Allocator

**Goal:** allocate atlas tiles and publish metadata without changing receiver sampling.

### Tasks

- [x] Add `ShadowAtlasManager` under the world or render pipeline owner.
- [ ] Implement API:
  - [x] `BeginFrame(XRWorldInstance world, ReadOnlySpan<XRCamera> activeCameras)`
  - [x] `Submit(in ShadowMapRequest request)`
  - [x] `SolveAllocations()`
  - [x] `RenderScheduledTiles()`
  - [x] `PublishFrameData()`
- [ ] Implement per-encoding atlas resources:
  - [x] depth color atlas,
  - [x] VSM atlas,
  - [x] EVSM2 atlas,
  - [x] EVSM4 atlas,
  - [x] transient raster depth target.
- [x] Implement a deterministic power-of-two buddy allocator.
- [x] Reuse existing allocations when key and LOD class remain stable.
- [x] Add demotion from desired LOD down to minimum resolution.
- [x] Publish fallback metadata for unallocated requests.
- [ ] Add generation counter and double-buffered CPU/GPU frame data.
  - [x] Double-buffered CPU frame data with generation counter.
  - [ ] GPU metadata buffer publish.
- [ ] Add memory and fragmentation metrics:
  - [x] bytes resident,
  - [ ] bytes / max budget,
  - [ ] tiles allocated / possible,
  - [x] free rect sum / largest free rect.
- [ ] Add an atlas visualization panel showing pages, occupancy, owner, LOD, residency, dirty state, and skip reason.
- [x] Add overflow behavior for `Submit`: profiler warning plus deterministic request drop policy.

### Exit Criteria

- [x] Allocations are deterministic for fixed requests.
- [x] No overlapping tile rects.
- [x] Gutter and inner rect metadata are correct.
- [x] Repack increments generation and invalidates cached indices.
- [ ] Warmed allocation/solve path is allocation-free in debug instrumentation.

### Validation

- [ ] Unit tests for allocator packing, demotion, reuse, gutter math, skip fallback metadata, editor-pinned budget bypass, repack generation, and no-overlap.
  - [x] Added tests for key uniqueness, tile-resolution normalization, deterministic no-overlap allocation, demotion, stable reuse/generation, and queue-overflow fallback metadata.
  - [ ] Add gutter-math, editor-pinned budget bypass, and full stress tests after the unit-test project compile blockers are cleared.
- [ ] Stress test many mixed-size requests beyond capacity.

## Phase 3: Spot Lights In Atlas

**Goal:** migrate the simplest local shadow path to real atlas rendering and sampling.

### Tasks

- [x] Render spot shadow requests into atlas tiles.
- [x] Write linear normalized depth to the depth color atlas.
- [x] Bind transient depth target for tile rasterization.
- [x] Publish spot `shadowRecordIndex` metadata.
- [x] Update deferred spot sampling to read atlas metadata.
- [x] Update forward spot sampling to read atlas metadata.
- [x] Keep legacy per-light spot maps behind a debug/fallback flag.
- [x] Add fallback short-circuit in receiver shader for nonresident spot requests.
- [x] Add spot light inspector fields:
  - [x] requested resolution,
  - [x] allocated resolution,
  - [x] atlas page/rect,
  - [x] priority,
  - [x] last rendered frame,
  - [x] skip reason.

### Exit Criteria

- [ ] Spot atlas path matches existing depth-shadow behavior within expected filtering differences.
- [ ] Oversubscribed spot lights degrade by priority and fallback cleanly.
- [x] Legacy spot path can still be selected for debugging.

### Validation

- [ ] Visual scene with more shadowed spot lights than fit the atlas.
- [ ] Forward and deferred spot receivers.
- [ ] Forced fallback scene covering `Lit`, `ContactOnly`, `StaleTile`, and `Disabled`.

## Phase 4: Directional Cascades In Atlas

**Goal:** represent every directional cascade as an atlas tile with stable fitting and metadata.

### Tasks

- [x] Convert each active cascade to a `ShadowMapRequest`.
- [x] Add sphere-fit cascade policy as the v1 default.
- [x] Preserve current split behavior while adding per-light override points.
- [x] Texel-snap cascade view matrices using allocated resolution.
- [x] Publish cascade tile indices, matrices, splits, and blend widths.
- [x] Update deferred directional sampling.
- [x] Update forward directional sampling.
- [x] Blend cascade visibility scalars, not raw depths or moments.
- [x] Update cascade previews and debug colors to read atlas tiles.
- [x] Use union of stereo eye frusta for VR cascade fit.

### Exit Criteria

- [ ] Directional atlas path supports cascades with stable tile reuse.
- [ ] Cascade blend bands do not sample outside tile inner rects.
- [ ] VR stereo edge artifacts are not introduced by single-eye fitting.

### Validation

- [ ] Directional cascade visual scene with camera motion.
- [ ] Bias-regression scene with multiple cascade LODs.
- [ ] VR active viewport validation when available.

## Phase 5: Point Lights In Atlas

**Goal:** migrate point shadows from cubemap ownership to six atlas face tiles.

### Tasks

- [ ] Convert each point light to six face requests.
- [ ] Add face matrix generation for `+X`, `-X`, `+Y`, `-Y`, `+Z`, `-Z`.
- [ ] Allow per-face LOD and optional per-face skip when the face cannot affect active consumers.
- [ ] Keep valid skipped faces resident when content is reusable.
- [ ] Add shader face selection by major axis.
- [ ] Convert receiver direction to face-local UV.
- [ ] Compare radial normalized receiver depth.
- [ ] Duplicate edge texels or otherwise protect gutters against face seams.
- [ ] Keep cubemap shadows as fallback until seam validation passes.

### Exit Criteria

- [ ] Point atlas path renders all six faces correctly.
- [ ] Face orientation debug view proves no swapped or mirrored faces.
- [ ] Seams are no worse than the legacy cubemap path.

### Validation

- [ ] Point light in a six-sided orientation test scene.
- [ ] Moving receiver across face boundaries.
- [ ] Oversubscribed point-light scene near the camera.

## Phase 6: Unified Forward+ Local Shadow Metadata

**Goal:** remove fixed local shadow sampler limits from the atlas path.

### Tasks

- [ ] Extend local light records with `shadowRecordIndex`.
- [ ] Bind atlas texture(s) plus shadow metadata SSBO in Forward+.
- [ ] Replace fixed local shadow arrays in atlas-enabled shaders:
  - [ ] `PointLightShadowMaps[4]`
  - [ ] `SpotLightShadowMaps[4]`
  - [ ] packed fixed per-light shadow arrays
- [ ] Keep compatibility defines until common materials use atlas sampling.
- [ ] Add shader-source tests for required atlas bindings.

### Exit Criteria

- [ ] Forward+ can shade more than four visible shadowed point lights and more than four visible shadowed spot lights in atlas mode.
- [ ] Legacy fixed arrays are not used by the atlas path.

### Validation

- [ ] Forward+ scene with many local shadowed lights.
- [ ] Shader compile/permutation test for atlas and legacy paths.

## Phase 7: Budgeted Updates, Hysteresis, And Stability

**Goal:** separate residency from render scheduling and prevent visible flicker.

### Tasks

- [ ] Add dirty tracking for light, projection, encoding, caster, material, camera-fit, and allocation changes.
- [ ] Add render scheduling budget:
  - [ ] max tiles per frame,
  - [ ] max shadow render milliseconds.
- [ ] Schedule high-priority dirty directional cascades first, then spots, point faces, static refreshes, and low-priority lights.
- [ ] Keep resident stale tiles until refreshed when `StaleTile` fallback is allowed.
- [ ] Add LOD hysteresis thresholds for promotion/demotion.
- [ ] Rate-limit LOD changes per request.
- [ ] Prefer existing tile locations unless the allocation win exceeds a threshold.
- [ ] Add optional LOD-transition strength fade.
- [ ] Add editor-triggered compaction/repack.

### Exit Criteria

- [ ] Camera movement does not cause rapid LOD oscillation.
- [ ] Dirty-but-not-rendered resident tiles remain safe to sample.
- [ ] Repacking is not performed every frame.

### Validation

- [ ] Moving-camera stress scene.
- [ ] Profiler capture of solve time, render tiles per frame, and repack frequency.
- [ ] Visual check for shadow shimmer during LOD transitions.

## Phase 8: Caster Materials, VR, And Probe Policy

**Goal:** make atlas shadows match material and multi-camera behavior.

### Tasks

- [ ] Wire `ShadowCasterFilterMode` from request to shadow draw record to material variant.
- [ ] Add opaque depth-only variant.
- [ ] Add alpha-tested variant with alpha clip.
- [ ] Add two-sided raster state handling.
- [ ] Keep `AlphaToCoverage` out of v1 unless separately scheduled.
- [ ] Add probe classification:
  - [ ] shadow consumers,
  - [ ] shadow non-consumers.
- [ ] Add per-probe `UsesShadowAtlas` opt-in.
- [ ] Ensure multi-camera priority uses max projected score across consumers, not sum.
- [ ] Preserve one shared tile for both VR eyes in v1.

### Exit Criteria

- [ ] Alpha-tested and two-sided casters match receiver expectations.
- [ ] Probe captures do not multiply request counts unless opted in.
- [ ] VR stereo priority and cascade fit are stable.

### Validation

- [ ] Foliage/cutout caster visual scene.
- [ ] Probe capture scene proving no unexpected atlas request explosion.
- [ ] Stereo cascade coverage validation.

## Phase 9: Static / Dynamic Caster Split

**Goal:** reduce shadow redraw cost for mostly-static scenes after the atlas path is stable.

### Tasks

- [ ] Measure redraw cost in representative static-heavy scenes.
- [ ] Track static and dynamic caster sets per request.
- [ ] Choose implementation based on profiling:
  - [ ] two-tile-per-light,
  - [ ] hash-stable single tile with static copy,
  - [ ] continue single-tile full redraw.
- [ ] If enabled, schedule static refresh only when light or static set changes.
- [ ] Composite dynamic movers over static cache.
- [ ] Add inspector view for static/dynamic composition state.

### Exit Criteria

- [ ] Static cache path is demonstrably faster in target scenes.
- [ ] Cache invalidation is deterministic and debuggable.
- [ ] Static split can be disabled for comparison.

### Validation

- [ ] Static-heavy scene with a few moving casters.
- [ ] Profiler before/after render-tile cost.
- [ ] Cache invalidation tests for moved light, changed material, and changed static set.

## Phase 10: Virtual Shadow Maps Follow-Up

**Goal:** replace fixed atlas backing with sparse virtual pages without changing request and receiver concepts.

### Tasks

- [ ] Gate Virtual Shadow Maps behind a feature flag.
- [ ] Add page-table backing store and residency tracking.
- [ ] Reuse `ShadowMapRequest` schema unchanged.
- [ ] Reuse receiver metadata shape with `vsmPacked` populated.
- [ ] Drive page requests from screen-visible texel analysis using HZB or depth-pyramid feedback.
- [ ] Retain v1 atlas as fallback for hardware or driver paths where virtual pages are impractical.

### Exit Criteria

- [ ] v1 atlas and virtual-page path can share receiver-side shader contract.
- [ ] Virtual-page path can be disabled without changing light components.

## Validation Matrix

### Unit Tests

- [ ] deterministic request keys
- [ ] deterministic allocation order
- [ ] no overlapping tile rects
- [ ] gutter and inner-rect calculation
- [ ] LOD demotion when pages fill
- [ ] stable allocation reuse
- [ ] point light expands to six requests
- [ ] directional light expands to cascade requests
- [ ] skipped request fallback metadata
- [ ] editor-pinned budget bypass
- [ ] repack generation increment
- [ ] thread-safe deterministic submit under fixed input
- [ ] no allocations after warmup in submit/solve/publish
- [ ] bias scales correctly across LOD steps
- [ ] alpha-tested caster discard path
- [ ] stereo cascade fit covers both eyes
- [ ] non-consuming probes do not submit requests

### Visual Scenes

- [ ] many spot lights beyond atlas capacity
- [ ] many point lights near the camera
- [ ] one directional light with cascades
- [ ] mixed moving and static shadow casters
- [ ] moment filtering with gutters
- [ ] forward and deferred receivers
- [ ] VR active viewport
- [ ] alpha-tested foliage
- [ ] mixed-LOD bias regression
- [ ] forced fallback oversubscription

### Performance Counters

- [ ] atlas solve time
- [ ] shadow tiles rendered per frame
- [ ] `Lights3DCollection.RenderShadowMaps`
- [ ] receiver shader cost
- [ ] atlas memory use
- [ ] fragmentation ratio
- [ ] forward shader sampler count
- [ ] request submit cost from job threads
- [ ] generation/repack frequency

## Risk Checklist

- [ ] Tile-edge leaks are mitigated with gutters, inner rect clamping, and tile-aware blur/mip generation.
- [ ] Shadow shimmer is mitigated with reuse, hysteresis, texel snapping, and controlled repacks.
- [ ] Point-light seams are covered by face transform tests and gutter edge handling.
- [ ] Forward shader metadata complexity is controlled by migrating spot lights first.
- [ ] Bias regression is covered by mixed-LOD validation.
- [ ] Allocator fragmentation is tracked before adding new allocator modes.
- [ ] Shader permutation growth is tracked through existing uber-feature tooling.
- [ ] Probe-capture request explosion is prevented by default opt-out.

## Out Of Scope For Atlas v1

- [ ] Translucent, colored, stochastic, deep-shadow, or Fourier opacity shadows.
- [ ] Ray-traced shadow-map encodings.
- [ ] Per-eye virtual page residency.
- [ ] Alpha-to-coverage shadow path.
- [ ] Cookie or IES profile atlasing.

## First Implementation Slice

Start with Phases 0-2, then migrate spot lights in Phase 3.

Spot lights are the best first real atlas consumer because they are one tile per light, already use 2D sampling, and avoid cascade and cubemap-face complexity. Directional cascades and point faces should reuse the same allocator, metadata, and fallback path after spot shadows prove the model.
