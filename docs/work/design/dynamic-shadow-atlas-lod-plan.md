# Dynamic Shadow Atlas And LOD Allocation Plan

> Status: **active design**

## Goal

Replace fixed per-light shadow textures with a dynamic shadow atlas system that can accept any number of directional, spot, and point light shadow requests, allocate atlas space by importance, and degrade gracefully through LODs when the budget is exceeded.

"Any number" does not mean infinite GPU memory. It means the scene can contain an unbounded number of shadow-capable lights, while the renderer chooses the best active subset and resolution each frame under a fixed memory and render-time budget.

### Long-term Endpoint: Virtual Shadow Maps

The atlas-with-LOD design described here is **v1**. The intended **v2 endpoint is Virtual Shadow Maps (VSM)** - a sparse page-table backed system where pages are allocated and rendered only for screen-visible texels. The request/metadata schema in this doc is deliberately designed so the *receiver-side* shader contract (one or two atlas textures + an SSBO of tile records + a `worldToShadow` matrix per record) can survive the move to VSM by re-pointing tile records at virtual pages instead of fixed allocations. Backing storage, allocator, and rendering strategy will be replaced; the request and sampling APIs should not have to be.

## Motivation

The current system allocates shadow resources per light:

- directional lights own a single shadow map plus optional cascade array
- spot lights own one shadow map
- point lights own a cubemap
- forward lighting binds only a small fixed number of local shadow samplers

That model is simple, but it does not scale well:

- memory grows with the number of shadow-casting lights
- unused or low-priority lights can keep expensive shadow maps alive
- forward local shadow sampling has hard sampler-count limits
- point lights are expensive because every shadowed light owns six cubemap faces
- per-light resources make cross-light LOD decisions harder

A shadow atlas gives the engine one budgeted pool. Lights submit requests. The atlas manager decides which requests fit, at what resolution, and when they update.

## Current State Summary

Relevant existing paths:

- `Lights3DCollection.CollectVisibleItems()` prepares and collects shadow casters.
- `Lights3DCollection.RenderShadowMaps()` renders all dynamic directional, spot, and point light shadow maps.
- `DirectionalLightComponent.CascadeShadows.cs` already treats one light as multiple shadow slices.
- `PointLightComponent` already supports both one geometry-shader cubemap pass and six per-face fallback passes.
- `ForwardLighting.glsl` already uses packed per-light shadow metadata for point and spot lights.
- Forward+ already uses GPU buffers for local light lists, which is the right direction for arbitrary local shadow records.

## Design Principles

1. Shadow allocation is frame-budgeted and priority-driven.
2. Sampling uses one or a few atlas textures plus metadata, not one sampler per light.
3. Directional cascades, spot lights, and point faces are all represented as atlas requests.
4. Point lights pack as six 2D face tiles, not as cubemaps, in the atlas path.
5. Allocations should be stable across frames to avoid shimmer and repeated re-rendering.
6. LOD means tile resolution first. Texture mip selection is a filtering feature, not the primary allocation mechanism.
7. Atlas operations must stay allocation-free in per-frame hot paths after warmup. Sorting, priority computation, and packing must use pre-sized scratch buffers; no LINQ, no `foreach` over non-struct enumerators, no per-frame allocations of request/allocation arrays.
8. Caster geometry filtering (opaque, alpha-tested, two-sided) is part of the request contract. The shadow pass must reproduce the visual result of the receiver's lighting model.
9. Skipped (un-allocated) requests have a defined fallback contract (lit, contact-only, or stale tile). Receivers never sample undefined memory.
10. Bias is computed per-tile from the *allocated* resolution, not from a constant. Resolution changes must scale bias to keep self-shadow acne and Peter-Panning bounded.
11. The receiver-side shader contract is forward-compatible with Virtual Shadow Maps: tile records describe a virtual region, and the backing storage is opaque to the receiver.
12. Threading: the manager's public API is documented for thread-affinity. `Submit` is callable from job threads during visibility collection; `Solve` and `Render` run on the render thread or a dedicated shadow job.

## Atlas Shape

Use a small set of 2D array atlases, grouped by compatible sampling format:

| Atlas | Sampling format | Used by |
|---|---|---|
| Depth atlas | `R16f` or `R32f` color depth | Depth compare, PCF, Vogel, PCSS |
| VSM atlas | `RG16f` or `RG32f` | VSM moment shadows |
| EVSM2 atlas | `RG16f` or `RG32f` | 2-channel EVSM |
| EVSM4 atlas | `RGBA16f` or `RGBA32f` | 4-channel EVSM |

Each atlas also needs a raster depth target:

- one shared depth page per atlas page, or
- a transient depth texture/renderbuffer rebound for each tile render

The sampling texture should be a regular color `XRTexture2DArray`. This keeps directional, spot, and point-face sampling unified and avoids mixing hardware depth-comparison samplers with moment maps.

Recommended first defaults:

- page size: 4096 x 4096
- max pages: configurable, default 2
- tile sizes: 4096, 2048, 1024, 512, 256, 128
- gutter: 2 to 8 texels depending on filter mode
- formats: depth `R16f`, VSM `RG16f`, EVSM4 `RGBA16f`

### Atlas-As-Color Trade-Off

Using color textures (`R16f`/`R32f`) for the depth atlas instead of true depth textures is a deliberate choice with consequences:

- **Loses hardware PCF** (`sampler2DShadow`). Manual depth-compare and software PCF are required. This is acceptable in a deferred or Forward+ pipeline because the cost is small relative to the lighting math.
- **Loses depth-test during raster of the shadow pass.** Each tile render needs a transient depth attachment (renderbuffer or shared depth page) bound for the duration of the tile, then results are written to the color atlas via the fragment shader (or via a depth-to-color resolve blit).
- **Gains unified sampling** across depth, VSM, and EVSM via the same texture binding kind, plus easy blur/mip generation, easy debug visualization, and a uniform path for cascades, spot, and point-face tiles.
- **Forward-compatible with VSM:** virtual page tables are inherently color-image-backed in most implementations.

The per-light hardware-depth-sampler path is **not** preserved in atlas mode. It remains only as a per-light fallback for lights opted out of the atlas (debug, baking, or a transition flag).

### Depth Representation And Reverse-Z

Atlas tiles store **linear normalized depth** in the encoded color value, not non-linear projected depth. This:

- decouples the atlas from the engine's main reverse-Z convention so cascade/spot/point face renders all use the same encoding,
- improves precision for `R16f` at modest depth ranges (perspective `R16f` near the far plane has unacceptable banding),
- makes VSM/EVSM moments meaningful without a follow-up linearization step,
- makes contact-shadow and screen-space comparisons simpler.

The shadow pass fragment shader writes `linear01 = (viewZ - near) / (far - near)` (with the receiver-side compare doing the matching transform). For `R32f` the linear vs. non-linear choice is performance-neutral; for `R16f` linear is required.

Receivers compute the same linearization from the per-tile `depthParams.xy` (`near`, `far`). This MUST be derived from the same projection used by the shadow pass; the request stores both matrix and near/far explicitly to avoid drift.

### Memory Budget Formulas

A single page of 4096 x 4096 array slice costs:

| Format | Bytes/texel | Per page (1 slice) |
|---|---:|---:|
| `R16f` | 2 | 32 MiB |
| `R32f` | 4 | 64 MiB |
| `RG16f` | 4 | 64 MiB |
| `RG32f` | 8 | 128 MiB |
| `RGBA16f` | 8 | 128 MiB |
| `RGBA32f` | 16 | 256 MiB |

The transient raster depth target adds another 32 MiB (`DEPTH24`) or 64 MiB (`DEPTH32F`) per page if not shared.

The editor must surface, per atlas:

- bytes resident,
- bytes / max bytes budget,
- tiles allocated / tiles possible,
- fragmentation ratio (sum of free rects / largest free rect).

A `MaxShadowAtlasMemoryBytes` budget control complements `MaxShadowAtlasPages`; whichever is reached first stops growth.

## Caster Materials And Translucency

Different caster materials require different shadow-pass paths. The request contract carries a `CasterFilterMode`:

```csharp
public enum ShadowCasterFilterMode : byte
{
    OpaqueDepthOnly,    // fast path, no fragment shader, double-sided clip optional
    AlphaTested,        // runs material's alpha-clip fragment shader
    AlphaToCoverage,    // MSAA-only fallback for foliage; not v1
    DitheredFade,       // LOD crossfade or distance fade
    TwoSided,           // disables backface culling for the shadow pass
}
```

Flags compose: `AlphaTested | TwoSided` is common for foliage.

**Translucent / colored shadows are out of scope for v1.** Hair, glass, smoke, and stochastic colored shadows would require a separate RGBA accumulation atlas (or deep shadow / Fourier opacity layers) and would double the receiver shader's cost. The architecture leaves room: an additional atlas with sampling format `RGBA8` or `RGBA16f` could be added later without changing the request schema, since `Encoding` already selects per-request which atlas to bind.

The shadow pass therefore branches on `CasterFilterMode`:

- `OpaqueDepthOnly`: bind depth-only material variant, no fragment shader.
- `AlphaTested`: bind alpha-clip variant; sample base color alpha and `discard`.
- `TwoSided`: disable backface cull state for the draw.

This makes the shadow pass material-aware without leaking material-system complexity into the atlas manager: each renderable produces a `ShadowDraw` record with an explicit material variant pointer.

## Shadow Requests

Every frame, lights produce shadow requests instead of directly owning all render targets.

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
    ulong ContentHash,        // hash of caster set + light transform; drives IsDirty
    bool IsDirty,
    bool CanReusePreviousFrame,
    bool EditorPinned,        // bypass budget for look-dev / debug
    StereoVisibility StereoVis); // {Mono, LeftEye, RightEye, Both} for VR

public enum ShadowFallbackMode : byte
{
    Lit,            // when un-allocated, treat as fully lit (cheapest)
    ContactOnly,    // rely on screen-space contact shadows
    StaleTile,      // keep last frame's tile contents until budget allows refresh
    Disabled,       // disable shadowing entirely for this light this frame
}
```

`ShadowRequestKey` must be stable:

- directional single shadow: light id + `DirectionalPrimary`
- directional cascade: light id + cascade index
- spot: light id
- point face: light id + cube face index

Directional cascades already have the right mental model: one light creates multiple 2D requests.

Point lights should become six 2D requests in the atlas path:

```text
Point light
  face +X -> atlas tile
  face -X -> atlas tile
  face +Y -> atlas tile
  face -Y -> atlas tile
  face +Z -> atlas tile
  face -Z -> atlas tile
```

The existing cubemap path can remain as a compatibility path until atlas point shadows are validated.

## Allocation Records

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
    bool IsStaticCacheBacked,    // see Static / Dynamic Caster Split
    ShadowFallbackMode ActiveFallback, // valid only when !IsResident
    SkipReason SkipReason);            // diagnostic: why was this not allocated
```

`PixelRect` includes gutter. `InnerPixelRect` is the valid sampling region. Receiver shaders use `UvScaleBias` for the inner region and clamp PCF/VSM offsets to avoid crossing into neighboring tiles.

## LOD Selection

LOD is selected from priority and projected size. Each request computes a desired resolution before packing.

Suggested LOD levels:

| LOD | Tile size | Typical use |
|---:|---:|---|
| 0 | 4096 | hero sun cascade, cinematic key shadows |
| 1 | 2048 | near directional cascade, important spot |
| 2 | 1024 | normal local shadows |
| 3 | 512 | secondary local shadows |
| 4 | 256 | distant or small lights |
| 5 | 128 | last-resort contact/detail hint |
| disabled | 0 | no atlas allocation, treat as lit or contact-only |

Priority inputs:

- camera visibility
- projected screen area of the shadowed influence volume
- light intensity and user importance
- distance to camera
- whether the light or casters moved
- whether the previous allocation is still valid
- shadow debug selection in the editor
- cascade index, with near cascades receiving higher priority
- point face visibility against active camera frusta

Directional cascade rule:

- cascade 0 should usually get the highest resolution
- later cascades should step down unless the user overrides them
- far cascades may be skipped if contact shadows or screen-space effects cover the visible need

Point-light face rule:

- allocate all six faces for fully correct omni shadows when budget permits
- allow per-face LOD differences
- optionally skip faces that cannot affect any active camera frustum this frame
- keep skipped faces resident if their previous content is still valid and the light is static

## Packing Algorithm

Use a power-of-two tile allocator per page.

Recommended first allocator:

- size-class free lists for 4096, 2048, 1024, 512, 256, 128
- buddy or guillotine subdivision
- deterministic request order by priority, then stable key
- no heap allocation during solve after internal arrays are warmed (sort uses a pre-sized scratch span; `Array.Sort` over a `Span<T>` of value types is safe)

### Allocator choice trade-off

| Allocator | Pros | Cons |
|---|---|---|
| Buddy (power-of-two) | Simple, deterministic, fast solve | Fragments under mixed sizes |
| Guillotine | Better packing for non-uniform sizes | Recombination is hard, IDs less stable |
| Shelf (per-LOD shelves) | Stable IDs, very allocation-free, ideal for fixed LOD set | Wastes space when LOD distribution skews |

Start with **buddy** for v1 because the LOD set is power-of-two and stability is important. Add a **shelf** mode behind a setting once profiling shows fragmentation matters.

**Allocation IDs survive across the same atlas configuration.** A repack invalidates IDs by definition; receivers must re-fetch metadata after a repack frame (signaled via a generation counter on `ShadowAtlasFrameData`).

Frame solve:

1. Collect requests.
2. Reuse valid existing allocations for stable keys when the LOD class did not change.
3. Sort new or changed requests by priority.
4. Try desired LOD.
5. If it fails, try the next lower LOD down to `MinimumResolution`.
6. If still full, mark request unallocated and assign its `ShadowFallbackMode`.
7. Schedule dirty resident requests for rendering under the render budget.

Avoid full repacking every frame. Repacking should happen only when:

- atlas page size or format changes
- memory budget changes
- fragmentation crosses a threshold
- the editor explicitly requests a compaction/debug rebuild

## Update Scheduling

Allocation and rendering are separate. A request can be resident but not re-rendered this frame if its content is still valid.

Dirty reasons (each contributes to `ContentHash`):

- light transform changed
- light projection/radius/cascade settings changed
- shadow encoding or filter settings changed
- relevant caster moved or changed material
- camera moved enough to invalidate directional cascade fit
- atlas allocation changed

Budget controls:

```csharp
public int MaxShadowTilesRenderedPerFrame { get; set; }
public float MaxShadowRenderMilliseconds { get; set; }
public int MaxShadowAtlasPages { get; set; }
public uint ShadowAtlasPageSize { get; set; }
public long MaxShadowAtlasMemoryBytes { get; set; }
public uint MinShadowAtlasTileResolution { get; set; }
public uint MaxShadowAtlasTileResolution { get; set; }
```

Scheduling order:

1. visible dirty high-priority directional cascades
2. visible dirty spot lights
3. visible dirty point faces
4. static-cache refreshes
5. low-priority lights

For static or slow lights, keep the previous atlas tile until a new render completes.

### Static / Dynamic Caster Split

Most scenes are mostly-static geometry plus a few movers. Re-rendering the entire shadow tile every frame for a slowly-changing light is wasted work.

Split each shadow tile into two logical layers:

- **Static cache**: rendered from non-moving casters only. Persistent across frames. Re-rendered only when the light moves, the static set changes, or eviction frees the slot.
- **Dynamic overlay**: rendered every frame from the moving subset of casters, composited onto a copy of the static cache.

Implementation options:

1. **Two-tile-per-light**: allocate a static tile and a dynamic tile, copy static -> dynamic, then render movers into the dynamic tile. Doubles tile budget for shadowed lights but is simple.
2. **Hash-stable single tile**: copy static tile into the active tile each frame, then render movers. One tile per light; one extra blit per frame.
3. **Single tile, no split (v1 default)**: re-render everything. Lowest complexity, used until profiling justifies the split.

The request schema's `ContentHash` and `IsStaticCacheBacked` flag are present so the split can be added without re-touching the receiver shader contract.

### Stabilization And Hysteresis

Dynamic resolution and tile location can shimmer. Required behaviors:

- **Texel-snapping** for directional cascade view matrices (snap world-space origin to texel-sized increments at the cascade's effective resolution). This is required, not optional.
- **Cascade fit policy**: sphere-fit (rotation-stable, lower precision) vs frustum-fit (higher precision, prone to shimmer). Default to sphere-fit for v1; expose a per-light toggle.
- **Cascade splits**: PSSM (log/uniform blend with `lambda`) by default; allow per-light overrides. Preserve current splits behavior on migration.
- **Cascade overlap blends**: `cascadeBlendWidths[i]` defines the blend region width into cascade `i+1` in shadow-space units; receivers crossfade across the band.
- **LOD hysteresis**: a request must exceed the LOD-up threshold by some margin (`+15%` of the LOD's projected-area band) before promoting, and stay below by another margin before demoting. Promotions/demotions are rate-limited to once per N frames per request.
- **Allocation reuse**: prefer keeping an existing tile location even if a slightly better fit exists, until the win exceeds a threshold.
- **LOD-transition fade**: optionally crossfade shadow strength across N frames when LOD changes are unavoidable; skip for the first move and apply on subsequent ones.

### Bias And Receiver-Plane Stability

Bias must scale with allocated tile resolution. The per-tile metadata carries:

- `ConstantDepthBias` - a fixed depth offset (in linearized units, multiplied by `1/resolution`)
- `SlopeScaledBias` - multiplied by the receiver's surface slope to the light
- `NormalOffsetBias` - offset along the receiver normal in world units, scaled by `texelSize = lightExtent / resolution`
- `ReceiverPlaneBias` flag - enables receiver-plane depth bias derived from `ddx/ddy` of shadow-space depth

When LOD demotes from 1024 -> 512, `texelSize` doubles, so all three bias terms double. The receiver shader reads `texelSize` from `filterParams` and applies bias from per-tile metadata; bias is **not** stored as a constant.

Wrong bias is the most common visual regression when shadow resolution changes at runtime. Validation must include a bias-regression scene with mixed LODs.

## Renderer Integration

Add an atlas manager under the world or render pipeline layer:

```csharp
public sealed class ShadowAtlasManager
{
    // Thread: render thread (or pipeline owner). Resets per-frame state.
    public void BeginFrame(XRWorldInstance world, ReadOnlySpan<XRCamera> activeCameras);

    // Thread-safe: callable from job/visibility threads. Internally writes to a
    // pre-sized lock-free MPSC queue or per-thread scratch list merged in Solve.
    public void Submit(in ShadowMapRequest request);

    // Thread: render thread. Drains submitted requests, runs allocator, plans renders.
    public void SolveAllocations();

    // Thread: render thread. Issues GPU draws into atlas tiles.
    public void RenderScheduledTiles();

    // Thread: render thread. Returns an immutable snapshot for receiver shaders.
    public ShadowAtlasFrameData PublishFrameData();
}
```

The render path changes from:

```text
for each light:
  light.RenderShadowMap()
```

to:

```text
collect shadow requests   (parallel, jobs)
solve atlas allocation and LOD   (render thread)
for each scheduled resident request:
  set atlas page/layer FBO
  set viewport/scissor to tile rect
  render shadow pass
publish metadata to shaders
```

The existing per-light `RenderShadowMap` methods can be bridged during migration:

- phase 1: lights still render their own resources
- phase 2: lights can produce requests
- phase 3: atlas manager owns rendering
- phase 4: fixed per-light resources become optional fallback/debug resources

**Pre-flight migration audit (Phase 1 deliverable):** grep all consumers of `LightComponent.ShadowMap`, `*.ShadowDepthTexture`, `Lights3DCollection.RenderShadowMaps`, debug overlays, screenshot tools, baking, GI capture. Each consumer needs an explicit migration entry; silent breakage of light inspectors or screenshot tooling is a regression.

## Threading And Frame-Data Lifetime

The atlas manager interacts with three distinct threads:

| Thread | Allowed operations |
|---|---|
| Visibility / job threads | `Submit(in ShadowMapRequest)` only |
| Main / sim thread | Reads inspector snapshots; never mutates atlas state directly |
| Render thread | All other manager operations, including `Solve`, `Render`, and frame data publish |

`ShadowAtlasFrameData` is **double-buffered**. The render thread fills buffer N while receiver shader compilation/binding can still read buffer N-1 if needed. The buffer carries a monotonically increasing `Generation` counter; consumers compare it to detect repacks (which invalidate cached `shadowRecordIndex` values on local lights and force a refetch).

GPU-side: the metadata SSBO is also double-buffered. The receiver pipeline reads via a `BufferRange` bound to the active generation. This avoids fences and races between shadow-pass writes and lighting-pass reads in the same frame on engines that submit lighting before the next frame's shadow renders begin.

Allocation discipline applies: `Submit` must not allocate. Use a per-thread scratch buffer drained at the start of `Solve`, or a fixed-capacity ring queue. Overflow logs a profiler warning and drops requests deterministically (lowest priority first); overflow is treated as a budget bug, not a runtime error.

## Multi-Camera, VR, And Probe Captures

### Active cameras

A frame may have many cameras: editor scene viewport, game viewport, VR mirror, VR left/right eyes, probe captures (reflection, GI), shadow-cascade fit cameras themselves. The atlas manager must distinguish:

- **Shadow consumers**: cameras whose lighting passes will sample atlas tiles this frame.
- **Shadow non-consumers**: cameras that are rendering this frame but are configured to skip dynamic shadows (most probe captures, thumbnail captures).

Only shadow consumers contribute to priority and visibility tests for shadow requests.

### Priority arbitration

When multiple consumer cameras are active, the per-request priority is the **maximum** projected-area / visibility score across consumers, not the sum. This avoids giving an offscreen-but-many-frusta light a misleadingly high score, while still ensuring any camera that needs the shadow gets a usable resolution.

### VR / Stereo

VR is the engine's primary target. The request's `StereoVis` field declares which eye(s) need the shadow:

- **Both eyes**: typical case for most lights; one shared atlas tile.
- **One eye only**: rare, but possible for lights inside one-eye-only opaque geometry. Today: still allocate one tile shared across eyes. `StereoVis` exists for VSM where per-eye paging is meaningful.

Directional cascade fit must consider **the union of both eye frusta**, not a single eye, otherwise edge-of-stereo artifacts appear. The fit camera is computed from a stereo-aware bounding volume.

Multiview (single-pass stereo) does **not** alter the shadow pass: shadows still render once per tile and are sampled by both eyes. Cubemap geometry-shader emission paths and multiview interact only when the receiver pass samples shadows; the shadow pass itself is single-view.

Per-eye reprojection of cached tiles is **not** a valid optimization (shadow space is light-relative, not eye-relative). VR therefore benefits more from stable allocations and the static/dynamic cache split than from per-eye tricks.

### Probe captures

GI probes, reflection probes, and light probes do **not** consume the shadow atlas by default. Probe captures are typically baked or run with a simplified shadow path:

- **Bake-time captures**: use baked or per-capture shadow renders, not the live atlas.
- **Runtime probes**: opt in via a per-probe `UsesShadowAtlas` flag. When opted in they count as shadow consumers and contribute to priority.

This prevents an N-probe scene from quadrupling shadow request counts.

## Metadata For Shaders

Use one atlas texture binding per encoding plus a metadata buffer.

```glsl
struct ShadowAtlasTile
{
    vec4 uvScaleBias;       // xy scale, zw bias into atlas array page
    vec4 depthParams;       // near, far, texelSize, fallbackMode
    vec4 biasParams;        // constantBias, slopeScaledBias, normalOffsetBias, receiverPlaneFlag
    vec4 filterParams;      // radius, min variance, bleed reduction, mip bias
    ivec4 packed0;          // page/layer, encoding, projection type, flags
    ivec4 packed1;          // light index, face/cascade index, lod, debug mode
    ivec4 vsmPacked;        // virtualPageX, virtualPageY, virtualMipLevel, residencyMask
                            //   v1: zeroed; v2 VSM: indexes into page table
    mat4 worldToShadow;
};
```

`vsmPacked` is reserved for the v2 endpoint and is zero in v1. Receivers branch on `packed0.x == VSM_VIRTUAL_PAGE` to take the page-table-resolution path. In v1 this branch is dead-code-eliminated by an uber-feature define.

`depthParams.w` carries the active fallback mode (`Lit`, `ContactOnly`, `StaleTile`, `Disabled`). When non-`StaleTile`, the receiver short-circuits sampling and returns the fallback value without touching the atlas texture.

Directional cascades need an array of tile indices instead of direct texture layers:

```glsl
struct DirectionalShadowRecord
{
    int cascadeCount;
    int cascadeTileIndices[8];
    float cascadeSplits[8];
    float cascadeBlendWidths[8];
};
```

Point lights need either:

- six tile indices, one per cube face, and cube-face UV reconstruction in the shader, or
- six matrices and tile rects, selected by major axis of the receiver-to-light vector

The first option is cheaper. The second option is easier to debug and supports custom face projections. Start with explicit six face records for clarity.

## Sampling Model

All shadow receivers sample a 2D array atlas:

```glsl
vec3 atlasCoord = vec3(tile.uvScaleBias.xy * localUv + tile.uvScaleBias.zw, page);
```

### Fallback short-circuit

Before sampling, the receiver checks `tile.depthParams.w` (fallback mode):

- `Lit` -> return shadow factor `1.0` (fully lit), no texture fetch.
- `ContactOnly` -> sample screen-space contact shadow only.
- `StaleTile` -> proceed with normal sampling using the (possibly stale) tile contents.
- `Disabled` -> return `1.0` and skip all shadow work.

This keeps the receiver path safe when allocation pressure exceeds budget and avoids undefined samples from an unallocated tile.

### Bias application

Projected depth uses bias from `tile.biasParams`:

```glsl
float depthBias = biasParams.x
                + biasParams.y * max(0.0, slope)
                + biasParams.w * receiverPlaneBias(shadowDepthDdx, shadowDepthDdy);
vec3 worldOffset = N * (biasParams.z * tile.depthParams.z); // texelSize
```

All three bias terms scale implicitly with tile resolution because `texelSize` is per-tile.

### Depth compare

- linearize projected depth using `depthParams.xy` (`near`, `far`)
- sample atlas red channel (also linear)
- compare with `depthBias`
- PCF/Vogel offsets are clamped to the inner rect (`uvScaleBias` already maps to inner-rect UV space, but blur radius must respect `texelSize` and the tile's gutter width)

### VSM/EVSM

- sample atlas moment channels
- use linear filtering
- use tile-aware blur/mip support only when gutters are valid; mip chain construction must be a per-tile reduction, not a whole-page mip generation

### Point lights

1. Compute receiver direction from light to receiver.
2. Select cube face by major axis.
3. Convert direction to face-local UV.
4. Use radial normalized receiver depth.
5. Sample the selected face tile through the atlas SSBO record.

### Hardware PCF abandonment

Atlas mode does not use `sampler2DShadow`. Manual depth compare and software PCF (Poisson disk, Vogel disk, or 5x5 tap) are the supported filters. Per-light hardware-PCF paths exist only for the legacy fallback resource.

## Interaction With Forward+

The atlas should remove the fixed forward local shadow sampler limit.

Forward+ already publishes visible local light indices. Extend local light records with:

```glsl
int shadowRecordIndex;
```

Then the forward shader can sample local shadows through the atlas metadata buffer for any visible shadowed point or spot light. The old fixed arrays:

- `PointLightShadowMaps[4]`
- `SpotLightShadowMaps[4]`
- packed per-light shadow arrays

can stay as a transition path, but the final atlas path should use:

- one depth atlas sampler
- optional moment atlas samplers
- one shadow metadata SSBO

## Editor And Debugging

Add render inspector views for:

- atlas pages
- tile occupancy
- per-tile owner light
- LOD level
- dirty/resident state
- last rendered frame
- reason a light was demoted or skipped

Light inspectors should show:

- requested resolution
- allocated resolution
- atlas page and rect
- update frequency
- priority score
- skipped reason, if any

For directional lights, cascade previews should read from atlas tile views rather than assuming one layer per cascade.

## Implementation Plan

### Phase 1: Request model and diagnostics

1. Add `ShadowMapRequest`, `ShadowRequestKey`, and priority calculation.
2. Let each light produce requests while still rendering to its existing resources.
3. Add debug logs or inspector data showing desired LOD and priority.
4. Add tests for deterministic request keys and LOD choices.

### Phase 2: Atlas resource and allocator

1. Add `ShadowAtlasManager`.
2. Add page textures and per-format atlases.
3. Implement deterministic power-of-two allocation.
4. Add atlas visualization in the render inspector.
5. Do not change receiver sampling yet.

### Phase 3: Spot lights in atlas

1. Render spot requests into atlas tiles.
2. Publish spot shadow metadata.
3. Update deferred spot and forward spot sampling to read from the atlas.
4. Keep per-light spot maps as fallback behind a debug flag.

### Phase 4: Directional cascades in atlas

1. Convert each cascade to an atlas request.
2. Publish cascade tile indices and matrices.
3. Update deferred and forward directional cascade sampling.
4. Update cascade previews and debug colors.

### Phase 5: Point lights in atlas

1. Convert point cubemap faces to six 2D atlas requests.
2. Add point face selection and UV reconstruction in `ShadowSampling.glsl`.
3. Validate geometry-shader path or replace it with six tile renders for atlas mode.
4. Keep cubemap shadows as fallback until atlas point seams are validated.

### Phase 6: Unified forward local shadows

1. Move local shadow metadata into SSBOs.
2. Remove fixed four-shadowed-point and four-shadowed-spot limits from the atlas path.
3. Keep compatibility defines for old materials until all common shaders use atlas sampling.

### Phase 7: Budgeted updates and stability

1. Add dirty tracking and per-frame update budgets.
2. Add camera/light movement hysteresis.
3. Add allocation hysteresis to prevent LOD flicker.
4. Add optional compaction/repack tools.

### Phase 8: Caster materials and stereo correctness

1. Wire `ShadowCasterFilterMode` end-to-end (request -> shadow draw -> material variant).
2. Validate alpha-tested foliage shadows.
3. Add stereo-aware cascade fit using union of eye frusta.
4. Add probe-capture opt-in / opt-out for shadow consumption.

### Phase 9: Static / dynamic caster split

1. Track per-light static vs dynamic caster sets.
2. Add static-cache tile (option 2: hash-stable single-tile-with-static-copy is the v1 default).
3. Schedule static refresh only on light or static-set changes.
4. Add inspector view of static-vs-dynamic compositing per tile.

### Phase 10: Virtual Shadow Maps (v2)

1. Add page-table backing store and residency tracking; gate behind a feature flag.
2. Use existing `ShadowMapRequest` schema unchanged; switch backing storage from fixed pages to virtual pages.
3. Drive page allocation from screen-visible texel analysis (HZB / depth-pyramid feedback).
4. Receivers branch on `vsmPacked` to use page-table resolution.
5. Retain v1 atlas as fallback for hardware/driver paths where VSM is impractical.

## Validation Plan

### Unit tests

Add tests for:

- deterministic allocation order
- no overlapping tile rects
- gutter calculation
- LOD demotion when pages fill
- stable reuse of existing allocation
- point light expands to six face requests
- directional light expands to cascade requests
- skipped requests produce the requested fallback metadata (Lit / ContactOnly / StaleTile / Disabled)
- editor-pinned requests bypass budget
- repack increments `Generation` counter
- `Submit` from multiple threads is safe and deterministic under fixed input
- request and allocation arrays do not allocate after warmup (use a `[NoAllocations]`-style assertion in debug)
- bias values scale correctly across LOD steps (ratio test)
- alpha-tested caster path discards correctly in shadow pass
- stereo cascade fit covers union of both eye frusta
- probe captures with `UsesShadowAtlas = false` do not produce requests

### Visual validation

Use scenes with:

- many spot lights beyond atlas capacity
- many point lights near the camera
- one directional light with cascades
- mixed moving and static shadow casters (validate static-cache split when enabled)
- VSM/EVSM moment filtering with gutters
- forward and deferred receivers
- VR active viewports, where shadow culling should prefer stability
- alpha-tested foliage scene (validate AlphaTested + TwoSided shadow path)
- bias-regression scene: a single static caster sampled at multiple LODs simultaneously to confirm no acne or peter-panning emerges as LOD changes
- forced-fallback scene: oversubscribed atlas to verify Lit / ContactOnly / StaleTile fallbacks render without artifacts

### Performance validation

Track:

- atlas solve time
- shadow tiles rendered per frame
- `Lights3DCollection.RenderShadowMaps`
- receiver shader cost
- atlas memory use (bytes resident, bytes / budget, fragmentation ratio)
- sampler count in forward shaders
- request submit cost from job threads
- generation/repack frequency

The atlas should reduce memory and sampler pressure first. Render-time wins depend on update scheduling and how many low-priority lights are demoted or skipped.

## Risks

### Tile-edge leaks

PCF, VSM, EVSM, blur, and mipmaps can sample outside a tile.

Mitigation:

- reserve gutters
- clamp filter taps to the inner rect
- make blur and mip generation tile-aware

### Shadow shimmering from LOD changes

Dynamic allocation can change resolution or tile location.

Mitigation:

- reuse allocations aggressively
- add priority hysteresis
- fade or delay LOD transitions
- do not repack every frame

### Point-light seams

Cubemap-to-atlas face sampling can expose edge seams.

Mitigation:

- use consistent face transforms
- duplicate edge texels into gutters
- validate all six face orientations with debug views

### Forward shader metadata complexity

Moving from fixed samplers to atlas SSBO metadata is a larger shader contract change.

Mitigation:

- migrate spot lights first
- keep existing sampler arrays as fallback
- add shader source tests for every required binding

### Bias regression on LOD change

Wrong bias is the most common visual bug when shadow resolution changes at runtime.

Mitigation:

- always derive bias from per-tile `texelSize`, never from constants
- include a bias-regression validation scene with mixed LODs
- expose bias overrides per-light in the inspector

### Allocator fragmentation under mixed LODs

Guillotine and buddy allocators fragment differently under mixed sizes.

Mitigation:

- track a fragmentation ratio metric per page
- expose an editor-triggered compaction
- offer a shelf-allocator mode behind a setting once profiling justifies it

### Permutation explosion in shader uber-features

Adding atlas mode, fallback handling, and VSM gating risks combinatorial growth.

Mitigation:

- prefer runtime branches with constant uniforms over `#ifdef` permutations
- track permutation count via the existing uber-feature tooling
- gate VSM behind a single feature define so v1 receivers compile to identical code

### Probe-capture request explosion

Reflection/GI probes can multiply shadow request counts if naively included.

Mitigation:

- probes opt out by default (`UsesShadowAtlas = false`)
- bake-time captures use the legacy per-light path

## Out Of Scope For v1

- Translucent / colored / stochastic shadows (hair, glass, smoke) and deep-shadow / Fourier opacity layers.
- Ray-traced shadow maps (DXR/VK_KHR_ray_tracing) as a request encoding.
- Per-eye virtual page residency in VR.
- AlphaToCoverage shadow path (MSAA-only).
- Cookie / IES profile atlasing (orthogonal; tracked separately).

These are intentionally deferred. The v1 schema must not foreclose them: encoding enums, fallback modes, and the `vsmPacked` field are the extension points.

## Recommended Initial Scope

Start with request generation and atlas allocation diagnostics only. Do not switch sampling immediately.

Then move spot lights into the atlas first. Spot lights are one tile per light, already use 2D sampling, and do not carry cascade or cubemap-face complexity. Once spot shadows are stable, directional cascades and point faces can reuse the same allocator and metadata path.

VSM (Phase 10) is **not** part of v1. It is documented here so that the v1 receiver shader contract, request schema, and metadata layout can survive the transition without another contract break.
