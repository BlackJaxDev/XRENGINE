# Shadow Resource Migration Audit

Phase 0 record for the dynamic shadow atlas / LOD allocation work and the VSM / EVSM shadow filtering work.

## Scope And Validation

Date: 2026-04-29.

This audit covers the currently known runtime, shader, editor, diagnostic, bake, and capture consumers of per-light shadow resources. It is the migration contract before atlas-backed resources and moment-shadow filtering start replacing direct per-light frame buffers.

Validation performed:

- Reviewed `.vscode/tasks.json` and `.vscode/launch.json`. No shadow-specific run, debug, or task orchestration changes are needed for Phase 0.
- Reviewed the active plans and TODOs for dynamic shadow atlas / LOD allocation and VSM / EVSM filtering.
- Ran scoped source searches for `ShadowMap`, `ShadowDepthTexture`, `RenderShadowMaps`, `CascadedShadow`, shadow sampler names, point/spot shadow arrays, and shadow sampling helpers across `XREngine`, `XREngine.Editor`, `Build/CommonAssets`, and `docs`.
- Full unbounded `rg` scans over `Build` and `docs` hit generated/dependency-heavy trees and timed out, so the audit uses scoped searches that exclude `Build/Submodules`, `Build/Dependencies`, generated build outputs, logs, and repository submodules.

## Current Ownership Summary

- `LightComponent` is the root owner for `XRMaterialFrameBuffer? ShadowMap`, shadow resolution, storage format, cast-shadow lifecycle, and common uniform hooks.
- `OneViewLightComponent` provides the single-view shadow camera and viewport path used by spot lights and non-cascade directional shadows.
- `DirectionalLightComponent.CascadeShadows.cs` owns the current directional cascade texture array, per-cascade frame buffers, cameras, viewports, matrices, and frustum setup.
- `SpotLightComponent` owns the simplest 2D color-depth shadow texture path and should be the first live atlas migration target.
- `PointLightComponent` owns a depth cubemap plus color cubemap path with both geometry-shader and six-pass fallback rendering. Atlas migration should treat point lights as six 2D face requests.
- `Lights3DCollection` schedules live shadow collection and rendering from `GlobalPreRender`, and also provides the capture refresh path used before auxiliary captures.
- `Lights3DCollection.ForwardLighting.cs`, `VPRC_LightCombinePass.cs`, and the shader snippets are the main runtime consumers that bind or sample shadow textures.

## Migration Matrix

| Consumer | Current dependency | Atlas / moment migration | Fallback or phase |
| --- | --- | --- | --- |
| `LightComponent.ShadowMap` | Per-light `XRMaterialFrameBuffer` with material-owned depth/color attachments. | Keep as a compatibility surface while introducing `ShadowMapResource` as the canonical request/result wrapper. | Do not delete until forward, deferred, editor preview, bake, and capture rows have receivers. |
| `LightComponent.SetShadowMapResolution` | Resizes owned per-light FBOs and calls `DestroyShadowMap`. | Becomes a request hint for desired/min/max resolution; atlas allocator chooses final tile. | Legacy path still applies exact resolution. |
| `LightComponent.ShadowMapStorageFormat` | Selects depth or color storage for the legacy shadow map. | Maps to requested `EShadowMapEncoding` plus format preferences in `ShadowMapRequest`. | Unsupported formats demote through policy below and report `SkipReason`. |
| `LightComponent.SetShadowMapUniforms` | Directly binds per-light shadow state to the current shader. | Routes through dispatcher metadata: atlas page, tile UV rect, encoding, compare mode, depth convention, and optional legacy texture handle. | Compatibility path keeps existing uniforms until every shader reader has atlas metadata. |
| `OneViewLightComponent` | Uses `PrimaryShadowViewport` and `ShadowCamera` to render into `ShadowMap`. | Reuse view/camera setup to fill a render request; destination becomes an atlas tile or legacy FBO. | Phase 1/2 bridge. |
| Directional single-map path | Uses `ShadowMap` with sampler name `ShadowMap`. | Convert to a primary directional atlas request only after spot atlas path is validated. | Legacy path remains for non-cascade diagnostics and fallback. |
| Directional cascade path | Owns `XRTexture2DArray` `ShadowMapArray`, cascade FBOs, and cascade transforms. | Each cascade becomes an independent atlas request keyed by cascade index; cascade metadata replaces array-layer assumptions. | Keep array path until all forward, deferred, fog, debug, and pipeline-script consumers migrate. |
| Spot lights | Own a 2D shadow depth/color target named `ShadowMap`. | First live atlas migration target: one 2D request per light. | Legacy FBO remains for unsupported encoding and debug comparison. |
| Point lights | Own cube shadow targets plus GS/six-pass render paths. | Six independent 2D face requests keyed by cube face. Preserve six-pass fallback as the implementation model for atlas rendering. | Cubemap path remains for deferred/forward compatibility until shader records migrate. |
| `ShadowRenderPipeline` | Renders the current shadow pass into whichever FBO is active. | Keep as the draw pipeline for atlas tile renders; add material-aware caster filtering before moment encodings rely on alpha correctness. | Existing pass sequence stays until filter modes are implemented. |
| `Lights3DCollection.CollectVisibleItems` | Builds directional frusta and collects shadow casters into each light. | Feeds request generation, priority, and camera/frustum context. VR uses both-eye union for atlas requests. | Existing collection remains for legacy FBO rendering. |
| `Lights3DCollection.RenderShadowMaps` | Iterates visible dynamic directional, spot, and point lights and renders each per-light shadow map. | Becomes the central request submission, ranking, allocation, and render-budget dispatcher. | Must retain the old direct render loop behind a compatibility path during migration. |
| `EnsureShadowMapsCurrentForCapture` and auxiliary captures | Forces current live shadow maps before scene captures. | Requests capture-visible shadows through the dispatcher with capture domain metadata. | Capture can continue forcing legacy shadows until atlas capture policy is implemented. |
| Forward directional lighting | Binds `ShadowMap` and `ShadowMapArray` at fixed texture units and uploads cascade uniforms. | Replace fixed directional texture bindings with atlas page arrays plus per-light/cascade shadow records. | Keep fixed bindings for legacy directional paths. |
| Forward local lighting | Binds fixed `PointLightShadowMaps[4]` and `SpotLightShadowMaps[4]` texture units plus packed local shadow arrays. | Replace fixed slot arrays with `shadowRecordIndex` data and atlas page sampling. | Fixed arrays remain until all forward material snippets migrate. |
| Deferred light combine | Binds one `ShadowMap` or `ShadowMapArray` per light combine pass. | Add atlas-aware branch driven by per-light shadow record metadata. | Dummy texture fallback remains for unshadowed lights. |
| `VPRC_ForEachCascade` | Pipeline-script consumer of directional cascade FBOs and matrices. | Either migrate to atlas cascade records or mark as legacy-cascade-only. | Do not remove before script callers have an atlas equivalent. |
| `ShadowSampling.glsl` | Manual compare, PCF, Vogel, PCSS, array, and cubemap helpers. | Centralize atlas/moment sampling here; add depth convention constants and tile-aware guards. | Existing 2D, array, and cube helpers stay during compatibility. |
| `ForwardLighting.glsl` | Samples fixed directional, spot, and point shadow textures. | Reads shadow records, atlas page arrays, encoding, tile UV rects, and moment parameters. | Compatibility branch can keep fixed samplers until materials migrate. |
| Deferred directional shaders | Sample `ShadowMap` / `ShadowMapArray` and cascade uniforms. | Add atlas cascade sampling path from shadow records. | Array path remains until cascade atlas is validated. |
| Deferred spot shader | Samples `ShadowMap`. | Migrate together with spot atlas path. | Legacy 2D path is first comparison oracle. |
| Deferred point shader | Samples cubemap `ShadowMap`. | Migrate after six-face atlas requests and point-face metadata are stable. | Cubemap path remains for fallback. |
| Point shadow depth shaders | Write point light depth through GS/six-pass paths. | Six-pass rendering can target atlas tile FBO views; GS cubemap path remains legacy-only. | Keep both paths until point atlas proves stable. |
| Uber and basic lighting snippets | Import or call shared shadow sampling and forward lighting snippets. | Benefit from central sampler migration if `ShadowSampling.glsl` and `ForwardLighting.glsl` stay authoritative. | No direct resource ownership. |
| Volumetric fog | Consumes forward lighting uniforms and directly samples `ShadowMap` / `ShadowMapArray` for the primary directional light. | Needs atlas directional records or an explicit bridge that exposes primary directional shadow metadata. | Must remain on legacy directional resources until this bridge exists. |
| `LightmapBakeManager` | Forces per-light shadow rendering and samples `ShadowMap` / `ShadowCube` during bake. | Bake/probe requests use a separate request domain and should not consume the live atlas by default. | Legacy per-light bake path remains the v1 fallback. |
| Reflection probes, GI probes, scene capture flows | Mostly consume final rendered views or independent probe render targets. | Do not opt into live atlas residency by default; use separate domain if shadows are needed. | Explicit opt-in only. |
| Surfel GI / SSGI | No direct shadow resource sampling found in scoped audit. | No Phase 1 migration needed. | Re-audit if GI starts sampling shadow records. |
| Water / translucency | Water forward shader did not show direct shadow resource sampling in scoped audit; translucency may inherit shared forward snippets. | Covered by shared forward lighting migration if direct shadows are used. | Keep material-specific re-test before removing legacy samplers. |
| Decals | No direct shadow resource sampling found in scoped audit. | No Phase 1 migration needed. | Re-audit if deferred decals start sampling lighting shadows. |
| GPU particles / cloth | No direct shadow resource sampling found in scoped audit. | No Phase 1 migration needed. | Re-audit if particle lighting adds shadow records. |
| Editor light inspectors and previews | Display `ShadowMap` and `CascadedShadowMapTexture`; expose storage format controls. | Add atlas tile/page preview and per-light allocation diagnostics once `ShadowMapResource` exists. | Keep legacy previews until atlas preview UI lands. |
| Camera editor debug preview | Reads directional cascade texture for diagnostics. | Add cascade record/tile preview. | Legacy cascade array preview remains. |
| Screenshots, MCP viewport capture, readback | Capture final rendered viewport, not shadow resources directly. | No direct migration. | Validate final capture after shader consumers migrate. |
| Material/import tooling | Some code classifies shadow sampler types or names imported textures containing "shadow". | Keep importer semantics separate from live shadow resources. | No runtime migration needed. |

## Phase 0 Policy Decisions

### Unified Depth Path

Atlas-managed `Depth` shadows use the same color-texture path as moment encodings. The default depth atlas format is `R16f`, with `R32f` available for higher precision. Values are non-reversed linear depth in normalized 0..1 light space, where `1.0` is the clear and unoccluded sentinel.

Legacy hardware-depth attachments and direct compare samplers may remain for debugging, bake fallback, and per-light compatibility during migration, but new atlas code should not depend on hardware depth comparison.

### Resource Replacement

Introduce `ShadowMapResource` as the canonical request/result wrapper in the implementation phases. It should carry:

- Stable request key.
- Encoding and concrete storage format.
- Page index, tile rectangle, gutter, resolution, and LOD level.
- Depth convention and clear sentinel.
- Optional legacy `XRMaterialFrameBuffer` / texture handles while compatibility paths remain.
- Skip reason, residency state, age, priority, and last render frame.

Do not remove `LightComponent.ShadowMap`, `DirectionalLightComponent.CascadedShadowMapTexture`, or point/spot depth texture surfaces until all migration rows above either consume `ShadowMapResource` or have a documented fallback.

### Public Settings

Use these public setting names unless implementation discovers a better local convention:

| Setting | Type | Default | Notes |
| --- | --- | --- | --- |
| `ShadowAtlasPageSize` | `uint` | `4096` | Per page width/height. |
| `MaxShadowAtlasPages` | `int` | `2` | Default cap per live encoding group unless overridden by memory cap. |
| `MaxShadowAtlasMemoryBytes` | `long` | `0` | `0` means derive from page caps; positive values are a hard cap. |
| `MaxShadowTilesRenderedPerFrame` | `int` | `16` | Editor-pinned lights bypass ranking but not hard memory limits. |
| `MaxShadowRenderMilliseconds` | `float` | `2.0` | Soft per-frame shadow render budget. |
| `MinShadowAtlasTileResolution` | `uint` | `128` | Lowest demotion target before the request is skipped/fallbacked. |
| `MaxShadowAtlasTileResolution` | `uint` | `4096` | Highest request tile size allowed by the atlas manager. |

### Atlas Budget And Encoding Policy

- Default live atlas page size is 4096.
- `Depth` uses `R16f` by default and may use up to two live pages.
- `Variance2` uses `RG16f` by default, allocates lazily, and starts with one live page.
- `EVSM2` uses `RG16f` by default, can opt into `RG32f`, allocates lazily, and starts with one live page.
- `EVSM4` uses `RGBA16f` by default, can opt into `RGBA32f`, allocates lazily, and starts with one live page.
- Transient raster depth targets are currently owned per page and included in page byte estimates; they may be shared by page size later.
- `MaxShadowAtlasMemoryBytes` is the hard cap when positive. It can reduce page counts below the defaults and can reject editor-pinned requests.

When under pressure, the allocator should demote resolution/LOD within the requested encoding before skipping. It should not silently change an explicit moment encoding only because the atlas is full. Encoding demotion is reserved for unsupported capabilities or explicit automatic-quality policy. Unsupported encoding fallback order is:

1. `EVSM4`
2. `EVSM2`
3. `Variance2`
4. `Depth`

If a request still cannot fit after allowed LOD demotion, reuse a valid stale tile when policy permits it. Otherwise fall back to the request's configured failure behavior: contact-only, fully lit, disabled shadow, or legacy path if available.

### Encoding Flips

`ShadowRequestKey` includes encoding. A runtime encoding change retires the old allocation and submits a new request. Do not reinterpret an existing tile in place as a different encoding or storage format.

### Request Key Stability

Keys must be deterministic for a stable light/cascade/face within a running world:

- Base identity is a stable light id. Prefer serialized component identity when available; otherwise assign a runtime id at registration and include world generation to avoid reuse collisions.
- Directional primary key: light id, `DirectionalPrimary`, encoding.
- Directional cascade key: light id, `DirectionalCascade`, cascade index, encoding.
- Spot key: light id, `SpotPrimary`, encoding.
- Point face key: light id, `PointFace`, cube face index, encoding. Use the OpenGL face order `+X`, `-X`, `+Y`, `-Y`, `+Z`, `-Z`.
- Bake, probe, and capture requests include a separate request domain such as `Live`, `Bake`, `Probe`, or `Capture` so they do not accidentally evict live gameplay shadows.

### Skip Reasons

Use a typed skip/fallback reason rather than stringly typed diagnostics. Initial values:

- `None`
- `DisabledByLight`
- `NoCaster`
- `NoConsumerCamera`
- `BelowMinimumPriority`
- `TileBudgetExceeded`
- `RenderTimeBudgetExceeded`
- `PageBudgetExceeded`
- `MemoryBudgetExceeded`
- `UnsupportedEncoding`
- `UnsupportedFormat`
- `EncodingDemoted`
- `ResolutionBelowMinimum`
- `AllocationFailed`
- `QueueOverflow`
- `ProbeOptOut`
- `BakingUsesLegacyPath`
- `StaleTileReused`
- `EditorPinnedHardMemoryCap`
- `InvalidRequest`
- `ResourceCreationFailed`

### Gutter, Clear, Blur, And MSAA Rules

- Gutter clear uses the encoding's clear sentinel and the request depth convention. Untouched tile texels, gutter texels, and cleared pages must agree.
- Tile-aware blur belongs to the atlas/moment resource pass after allocation. Non-atlas moment blur may remain per-resource until atlas support lands.
- Tile-aware atlas MSAA resolve is out of scope for v1. Non-atlas MSAA for moment maps may land first if it remains isolated from atlas layout.

### Depth Direction Constants

Add a central C# shadow depth convention in the implementation phase, with matching GLSL constants in the shared shadow sampling code. The v1 atlas convention is non-reversed linear 0..1 depth, with clear/unoccluded value `1.0`.

Main scene reverse-Z policy must not leak into atlas or moment shadow encoding unless a future request explicitly opts into a different convention.

### EVSM Clamp Defaults

Clamp exponents before `exp` to keep half-float storage stable:

| Storage | Positive exponent clamp | Negative exponent clamp |
| --- | --- | --- |
| `RG16f` / `RGBA16f` | `5.0` | `5.0` |
| `RG32f` / `RGBA32f` | `15.0` | `15.0` |

Initial moment defaults:

- `ShadowMomentMinVariance`: `0.00002`.
- `ShadowMomentLightBleedReduction`: `0.2`.
- Moment mip generation remains off until gutter-aware downsampling is implemented.

### Explicitly Out Of Scope For V1

- VSSM / variance-based PCSS.
- Ray-traced shadows.
- Colored translucent shadows.
- Alpha-to-coverage shadow caster policy.
- Cookie or IES atlas integration.
- Tile-aware atlas MSAA resolve.

## Removal Guardrails

- No existing per-light shadow resource path should be removed until its migration matrix row has either a `ShadowMapResource` receiver or a documented fallback.
- Spot lights are the first atlas migration target because they exercise 2D atlas allocation without cascade-array or cubemap compatibility concerns.
- Directional cascades, point cubemaps, volumetric fog, and bake paths are compatibility-sensitive and should remain legacy-backed until their shader and editor consumers are migrated.
- Probe, bake, and capture shadows do not consume the live atlas by default. They must opt into separate request domains.
