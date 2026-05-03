# Dynamic Shadow Atlas And LOD Allocation TODO

> Status: **active phased TODO**
> Last reconciled: **2026-05-03** after the directional/spot atlas bring-up and directional raster-depth parity fix.
> Scope: runtime shadows, renderer integration, editor diagnostics, shader metadata.

## Target Outcome

Replace fixed per-light shadow textures with a budgeted shadow atlas system:

- Lights submit shadow requests instead of owning every live render target.
- The atlas manager chooses active requests, tile resolution, update cadence, and fallback behavior under memory and render-time budgets.
- Directional cascades, spot lights, and point-light faces share one request/allocation model.
- Directional and spot lights currently use the atlas path; point-light atlas requests/resources are reserved but point rendering and sampling remain pending.
- Receiver shaders sample atlas textures plus metadata, with no undefined reads when a request is skipped.
- The v1 receiver contract can later move to Virtual Shadow Maps without another major shader API break.

"Any number of shadow-capable lights" means unbounded scene inputs with bounded active GPU work, not unlimited GPU memory.

## Non-Negotiable Design Rules

- [x] Atlas mode uses manual depth compare and software filtering; hardware PCF remains only for legacy non-atlas resources.
- [x] Spot atlas depth data is stored as linear normalized depth in color textures.
- [x] Directional atlas depth sampling deliberately uses the page raster depth attachment, not the color atlas attachment, to match non-atlased directional depth precision and bias behavior.
- [ ] Decide the final point-face atlas depth representation before Phase 5 rendering begins. Prefer the shared color-linear path unless directional-style raster-depth parity is required for point faces too.
- [x] Bias/filter metadata carries local tile texel size plus requested/allocated scale; current receivers recover authored texel size for parity while atlas taps stay clamped in tile-local UV space.
- [x] Every skipped request publishes an explicit fallback: `Lit`, `ContactOnly`, `StaleTile`, `Disabled`, or transition-only `Legacy`.
- [x] Tile gutters are included in allocation rects; receiver sampling uses only inner rects and clamps atlas-local filter taps.
- [x] Directional cascade matrices are texel-snapped using the current atlas allocation resolution when available, with desired-resolution fallback before first allocation.
- [ ] Point lights become six independent direct-to-atlas 2D face requests in atlas mode; no cubemap shadow texture is produced or sampled on the atlas path.
- [ ] Point-light atlas rendering supports per-face priority, LOD, residency, and skip/fallback. A point light does not require all six faces to be resident in order to cast useful shadows.
- [ ] The optimized point-light atlas path is a true atlas geometry-shader renderer: selected faces fan out directly into atlas pages/tiles, not into an intermediate cubemap.
- [ ] `Submit(in ShadowMapRequest)` performs no heap allocation after warmup.
- [ ] Allocation, sorting, packing, and frame-data publish perform no per-frame hot-path allocations after warmup.
- [ ] `ShadowAtlasFrameData` and the GPU metadata buffer are double-buffered and carry a generation counter.
  - [x] CPU frame data is double-buffered and carries a generation counter.
  - [ ] GPU metadata buffer publish remains pending; current directional/spot paths bind compact uniform arrays/SSBO metadata directly.
- [ ] Probe captures do not consume live atlas shadows by default.
- [x] VR directional cascade fitting uses the union of both eye frusta.
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
    EShadowAtlasKind AtlasKind,
    int AtlasId,
    int PageIndex,
    BoundingRectangle PixelRect,
    BoundingRectangle InnerPixelRect,
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

Current C# frame data includes `AtlasKind` because page index is only unique within the directional, point, or spot family atlas. The shader metadata shape below remains the intended unified SSBO contract; current directional/spot receivers still use compact uniform arrays plus existing per-light SSBOs while Phase 6 is pending.

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

- [x] Page size: `4096 x 4096`.
- [x] Max pages: current runtime clamps to `1` page per light-family atlas (`Directional`, `Point`, `Spot`), so the live depth path can own at most three pages total. Configurable multi-page-per-family sampling is deferred.
- [x] Tile sizes: `4096`, `2048`, `1024`, `512`, `256`, `128` through power-of-two normalization.
- [x] Gutter: currently `2` texels for opaque, `4` for alpha-tested/two-sided; expand toward `8` only if larger filters require it.
- [ ] Depth atlas format:
  - [x] spot uses `R16f` color linear depth by default,
  - [x] directional currently samples a `Depth24` raster depth page for parity with the non-atlased path,
  - [ ] expose/validate an `R32f` color option for color-depth atlas users.
- [ ] Moment atlas formats: `RG16f` for VSM/EVSM2, `RGBA16f` for EVSM4.
- [x] Buddy allocator first; shelf allocator can be added later behind a setting if fragmentation data justifies it.
- [x] Single-tile full redraw is the v1 static/dynamic caster behavior; static cache split is a later optimization.

## Current Implementation Snapshot

As of 2026-05-03:

- [x] Directional and spot atlas paths are live and independently toggleable from the ImGui light editors/settings.
- [x] Directional atlas mode is authoritative: when enabled, forward and deferred directional receivers do not sample legacy `ShadowMap` / `ShadowMapArray`.
- [x] Directional cascades are balanced down to fit all active cascades into the directional family atlas instead of falling back to legacy individual cascade maps.
- [x] The atlas manager has separate resource identity for `Directional`, `Point`, and `Spot`; point atlas rendering is still not implemented.
- [x] Directional atlas page sampling uses the page raster depth texture, which fixed the bias/parity mismatch caused by sampling the `R16f` color page.
- [x] Directional, spot, and atlas tile preview UI now shows actual atlas pages plus compact tile previews with tile overlays.
- [x] `CascadeShadowRenderMode` is exposed in ImGui. `Sequential` is the standard legacy cascade path, `GeometryShader` uses the layered framebuffer path when available, and `InstancedLayered` currently logs/falls back to sequential.
- [ ] Unified GPU shadow metadata SSBO, point-light face atlas rendering/sampling, dirty-reason reporting, LOD hysteresis, and allocation-free hot-path validation remain open.

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
  - [x] pages per encoding and light family,
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
- [ ] Add request generation for directional, spot, and point lights.
  - [x] Directional cascade/primary requests.
  - [x] Spot primary requests.
  - [ ] Point face requests; `SubmitPointShadowAtlasRequests` is still intentionally stubbed.
- [x] Bridge runtime-rendering shadow atlas settings to the host engine settings so editor toggles and budgets are honored.
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
- [x] Keep existing `RenderShadowMap` methods available as the non-atlas/debug path. Spot and directional atlas rendering are now active when their atlas toggles are enabled; point lights still use the legacy cubemap path.

### Exit Criteria

- [x] Enabling diagnostics does not alter rendered shadows.
- [x] Request order is deterministic for fixed scene input.
- [ ] Point lights report exactly six face requests in atlas mode diagnostics.
- [x] Directional lights report one request per active cascade.

### Validation

- [x] Add unit tests for stable keys and LOD choice boundaries.
- [ ] Add tests for point-face and cascade request expansion.
- [ ] Run targeted rendering/unit tests for light components.
  - [ ] `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ShadowAtlas"` currently cannot execute because unrelated unit-test sources fail to compile (`Audio2Face3DComponent`, `OVRLipSyncComponent`, `HumanoidComponent`, `VRIKSolverComponent`, and `TransformAccessorFastPathTests` API drift).

## Phase 2: Atlas Manager, Resources, And Allocator

**Goal:** allocate atlas tiles and publish metadata. Receiver migration happened later in Phases 3-4 and is now live for spot and directional lights.

### Tasks

- [x] Add `ShadowAtlasManager` under the world or render pipeline owner.
- [ ] Implement API:
  - [x] `BeginFrame(XRWorldInstance world, ReadOnlySpan<XRCamera> activeCameras)`
  - [x] `Submit(in ShadowMapRequest request)`
  - [x] `SolveAllocations()`
  - [x] `RenderScheduledTiles()`
  - [x] `PublishFrameData()`
- [ ] Implement per-encoding atlas resources:
  - [x] depth color atlas resource path for spot depth atlas pages,
  - [x] directional raster-depth page sampling path,
  - [x] VSM/EVSM resource descriptors and atlas encoding states,
  - [ ] VSM/EVSM tile rendering and receiver sampling,
  - [x] transient/page raster depth target.
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
  - [x] Per-light ImGui previews show actual atlas pages and compact resident tile previews with tile overlays.
  - [ ] Standalone occupancy panel with owner/LOD/dirty-state details is still pending.
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

Current state: spot atlas rendering/sampling is live and remains the color-linear depth atlas path. Legacy per-light spot maps still exist for atlas-disabled/debug/fallback cases.

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
- [x] Show spot atlas tile previews next to full atlas-page previews with tile outlines.

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
- [x] Show directional atlas page overviews with assigned tile outlines and compact cascade tile previews.
- [x] Make directional atlas mode authoritative so forward/deferred receivers do not sample legacy `ShadowMap` / `ShadowMapArray` when atlas mode is enabled.
- [x] Balance active directional cascade resolutions down until every cascade is resident in the directional atlas page.
- [x] Split atlas resource identity by light family (`Directional`, `Point`, `Spot`) so the live depth atlas can own at most three family pages.
- [x] Bind directional atlas receivers to the page raster depth texture instead of the color atlas texture, matching non-atlased directional depth precision and bias behavior.
- [x] Clamp atlas PCF/PCSS/Vogel taps in tile-local UV space before converting to page UVs.
- [x] Expose `CascadeShadowRenderMode` in ImGui for the legacy non-atlas cascade path.
  - [x] `Sequential` is implemented.
  - [ ] `InstancedLayered` single-pass cascade rendering.
  - [x] `GeometryShader` layered cascade rendering.
- [x] Use union of stereo eye frusta for VR cascade fit.

### Exit Criteria

- [x] Directional atlas path supports cascades with stable tile reuse.
- [x] Cascade blend bands do not sample outside tile inner rects.
- [ ] VR stereo edge artifacts are not introduced by single-eye fitting.

### Validation

- [x] Directional cascade editor validation with atlas on/off and camera movement during the 2026-05-03 bring-up.
- [x] Bias-regression validation for the directional atlas raster-depth fix against the non-atlased path during the 2026-05-03 bring-up.
- [ ] VR active viewport validation when available.

## Phase 5: Point Lights In Atlas

**Goal:** migrate point shadows from cubemap ownership to direct-to-atlas face tiles with independent per-face residency.

Current state: `EShadowProjectionType.PointFace` and `EShadowAtlasKind.Point` exist and the allocator can place point-family requests in a separate atlas resource, but runtime point request generation, direct-to-atlas face rendering, metadata binding, and receiver sampling are not implemented. `SubmitPointShadowAtlasRequests` remains a stub and point lights still use the legacy cubemap path.

### Design Decisions

- Point lights are represented as six independent `PointFace` requests keyed by light id + face index.
- Each face is scored independently from the active consumer camera set. Faces that cannot meaningfully affect the current view may be skipped entirely or kept as stale resident tiles when reuse is allowed.
- Atlas point shadows are rendered as 2D face tiles. The atlas path must not allocate, render, or sample a cubemap as an intermediate representation.
- Sequential rendering is valid as a bring-up/debug path, but it still renders each selected face directly into its atlas tile.
- The production optimized path is true atlas GS fan-out: one draw path may emit only the selected faces and route them to their allocated atlas tile/page state. It must support a face mask, per-face tile metadata, and page grouping or texture-array pages as needed.
- If atlas pages remain separate `sampler2D` resources, true atlas GS batching is grouped by destination page. If pages become a texture array, the GS can route pages through `gl_Layer`; in both cases tile isolation still comes from viewport/scissor state and inner-rect metadata.
- A legacy cubemap GS face-mask optimization is separate from atlas mode. It may remain useful for the debug/fallback cubemap path, but it must not be treated as the point-light atlas implementation.
- Receiver sampling selects a face by major axis, converts the direction to face-local UV, and samples that face's atlas metadata. Missing faces return the published fallback (`Lit`, `ContactOnly`, `StaleTile`, or `Disabled`) without undefined reads.

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
  - [x] max tiles per frame,
  - [x] max shadow render milliseconds.
- [x] Publish shadow atlas queue/render cost into the shared render-work budget coordinator so texture uploads can attribute contention.
- [x] Defer low-priority shadow tiles when urgent visible texture repair is pending; validate with Sponza/shadow-heavy scenes after runtime repros are available.
- [ ] Schedule high-priority dirty directional cascades first, then spots, point faces, static refreshes, and low-priority lights.
  - [x] Current scheduler uses sorted request priority and render-time/tile-count budgets.
  - [ ] Explicit type-order buckets and point-face/static-refresh ordering remain pending.
- [x] Keep resident stale tiles until refreshed when `StaleTile` fallback is allowed.
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

These test sources exist in the working tree, but the unit-test project currently cannot execute because unrelated compile blockers remain in audio/core/editor test sources. Treat checked items here as implemented test/source-contract coverage, not a green `dotnet test` run, until that project compiles again.

- [x] deterministic request keys
- [x] deterministic allocation order
- [x] no overlapping tile rects
- [ ] gutter and inner-rect calculation
- [x] LOD demotion when pages fill
- [x] stable allocation reuse
- [ ] point light expands to six requests
- [ ] point-light face metadata supports partial residency
- [x] directional light expands to cascade requests
- [x] skipped request fallback metadata
- [ ] editor-pinned budget bypass
- [x] repack generation increment
- [ ] thread-safe deterministic submit under fixed input
- [ ] no allocations after warmup in submit/solve/publish
- [x] bias metadata/source-contract coverage across atlas LOD steps
- [ ] alpha-tested caster discard path
- [x] stereo cascade fit covers both eyes
- [ ] non-consuming probes do not submit requests

### Visual Scenes

- [ ] many spot lights beyond atlas capacity
- [ ] many point lights near the camera
- [ ] partial point-face residency and fallback
- [x] one directional light with cascades
- [ ] mixed moving and static shadow casters
- [ ] moment filtering with gutters
- [ ] forward and deferred receivers
  - [x] directional atlas receiver path manually validated in editor.
  - [ ] broad spot/point atlas validation remains pending.
- [ ] VR active viewport
- [ ] alpha-tested foliage
- [ ] mixed-LOD bias regression
  - [x] directional raster-depth atlas parity manually validated against non-atlased path.
  - [ ] formal mixed-light/mixed-LOD scene remains pending.
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

## Next Implementation Slice

Continue from the reconciled 2026-05-03 state:

1. Finish Phase 5 point-light atlas support with sequential direct-to-atlas face rendering first.
2. Add point receiver metadata and shader face selection with explicit fallback for missing/nonresident faces.
3. Move toward Phase 6 unified atlas metadata so the atlas path no longer depends on fixed local shadow sampler arrays.
4. Add Phase 7 dirty-reason reporting, LOD hysteresis, and warmed no-allocation validation once point-face residency exists.
5. Keep `InstancedLayered` / `GeometryShader` cascade acceleration as a separate legacy directional cascade optimization; it does not block atlas point work.
