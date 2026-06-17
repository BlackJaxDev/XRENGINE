# Clustered Light Binning And Deferred MSAA Design

Status: reference design
Last Updated: 2026-06-17

Related docs:

- [Deferred Texturing Integration Design](deferred-texturing-integration-design.md)
- [Vulkan Fully Bindless Materials TODO](../../todo/rendering/vulkan-fully-bindless-materials-todo.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Vulkan Bindless And Deferred Texturing Audit](../../audit/vulkan-bindless-and-deferred-texturing-audit-2026-06-17.md)
- [TheRealMJP/DeferredTexturing](https://github.com/TheRealMJP/DeferredTexturing)
- [MJP: Bindless Texturing For Deferred Rendering And Decals](https://therealmjp.github.io/posts/bindless-texturing-for-deferred-rendering-and-decals/)

## Summary

XRENGINE's current Forward+ implementation is a 2D tiled light list. It builds one light list per screen-space tile and eye, and forward shaders index that list from `gl_FragCoord.xy`. There is no Z slice in the producer or consumer path.

The deferred renderer does not use those light lists. It renders one additive light volume per point or spot light, plus fullscreen directional lights. Its MSAA path marks complex pixels in stencil, then runs every light twice: once for simple pixels and once per sample for complex pixels.

This design upgrades the shared lighting path to 3D frustum-space clustered light binning, then uses that same cluster data from:

- forward shading
- materialized deferred shading
- future deferred texturing
- future clustered decals

It also replaces the fragile deferred MSAA graphics/stencil scheduling with the tile-classified compute approach described in MJP's sample: classify pixels/tiles, shade non-edge tiles cheaply, shade edge tiles per sample only where needed, then resolve.

## MJP Reference Points

MJP's DeferredTexturing sample uses one clustered light/decal selection model for both clustered forward and deferred texturing:

- 16x16 screen-space tiles
- 16 linearly partitioned depth buckets
- light and decal lists indexed by the pixel's XYZ cluster
- non-MSAA deferred compute shading over all pixels
- MSAA edge detection before deferred shading
- 8x8 MSAA tile classification into edge and non-edge dispatch lists
- non-edge tiles shade one subsample per pixel
- edge tiles shade one subsample for simple pixels and all subsamples for edge pixels

The important engine lesson is not only deferred texturing. It is that light selection, decal selection, and MSAA deferred scheduling become shared compute-visible data, rather than separate forward-only tiled lists and per-light deferred volume draws.

## Current XRENGINE State

### Forward+

Current files:

- `VPRC_ForwardPlusLightCullingPass.cs`
- `Build/CommonAssets/Shaders/Scene3D/ForwardPlus/LightCulling.comp`
- `Build/CommonAssets/Shaders/Scene3D/ForwardPlus/LightCullingStereo.comp`
- `Build/CommonAssets/Shaders/Snippets/ForwardLighting.glsl`
- `Lights3DCollection.ForwardLighting.cs`

Current behavior:

- Tile size is 16x16.
- Buffers are named `ForwardPlusLocalLights`, `ForwardPlusVisibleIndices`, and `ForwardPlusTileLightCounts`.
- `LightCulling.comp` dispatches `tileCountX x tileCountY x 1`.
- `tileLinear = tileY * tileCountX + tileX`.
- Consumer shaders compute tile index from screen XY only.
- Stereo doubles the tile buffer by eye, not by depth slice.
- The culling shader computes per-tile depth min/max, but does not use it for near/far planes. It uses camera near/far because the pass can run before forward-rendered depth is populated.
- There is no `ForwardPlusDepthSliceCount`, `ForwardPlusClusterCountZ`, or equivalent state.

This is tiled Forward+, not clustered forward shading.

### Deferred Lighting

Current files:

- `VPRC_LightCombinePass.cs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingPoint.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingSpot.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightingDir.fs`
- `Build/CommonAssets/Shaders/Scene3D/DeferredLightCombine.fs`

Current behavior:

- Local lights render as additive light volumes.
- Directional lights render as fullscreen passes.
- The shader samples the materialized GBuffer and evaluates exactly one `LightData` uniform per draw.
- There is no per-pixel or per-tile local light list in deferred lighting.
- The final combine pass applies ambient/probe/IBL-style work after local light accumulation.

This avoids brute-force looping in a fullscreen shader, but it still pays CPU/draw/pipeline overhead per local light and duplicates GBuffer reads across overlapping light volumes.

### Deferred MSAA

Current files:

- `VPRC_MarkComplexMsaaPixels.cs`
- `Build/CommonAssets/Shaders/Scene3D/MarkComplexMsaaPixels.fs`
- `VPRC_ResolveMsaaGBuffer.cs`
- `VPRC_LightCombinePass.cs`
- `DefaultRenderPipeline.CommandChain.cs`

Current behavior:

- The MSAA GBuffer is resolved to the normal deferred GBuffer before lighting.
- A fullscreen stencil pass marks complex pixels by comparing MSAA normal and depth samples.
- Simple pixels are lit from the resolved GBuffer with stencil `!= complex`.
- Complex pixels are lit from MSAA GBuffer textures with sample shading enabled and stencil `== complex`.
- Every light is submitted in both simple and complex phases.
- Lighting is then blitted from `MsaaLightingFBOName` to `LightingAccumFBOName`.

This is conceptually valid, but brittle. It depends on stencil state, graphics sample shading, light-volume draws, an early resolved GBuffer, and duplicated per-light submission. It also lacks MJP-style tile compaction, so sparse edge pixels can still schedule poorly.

## Goals

- Replace 2D Forward+ tiled lists with 3D frustum-space clustered light lists.
- Reuse the same clustered light data in forward and deferred paths.
- Keep the first clustered implementation independent of a depth prepass.
- Keep directional lights outside local cluster lists.
- Support point and spot lights in phase 1.
- Preserve stereo support by indexing clusters per eye.
- Add deferred clustered lighting as an option while retaining current light-volume fallback.
- Repair deferred MSAA with explicit edge masks and tile classification.
- Build toward clustered decals and deferred texturing without another light-list refactor.

## Non-Goals

- Do not require deferred texturing for clustered deferred lighting.
- Do not remove existing light-volume deferred rendering in the first phase.
- Do not require conservative rasterization for the first clustered-light implementation.
- Do not solve all shadow-map performance issues in this design.
- Do not enable VR clustered deferred or MSAA deferred by default until screenshots, logs, and GPU captures are clean.

## Naming And Ownership

The engine should introduce a backend-neutral clustered lighting concept rather than stretching the current Forward+ names further.

Recommended engine-facing names:

- `VPRC_ClusteredLightBinningPass`
- `ClusteredLocalLightsBuffer`
- `ClusteredVisibleLightIndicesBuffer`
- `ClusteredLightGridBuffer`
- `ClusteredLightCountsBuffer`
- `ClusteredLightingDebugOverlay`

Keep compatibility aliases for existing shader includes during migration:

- `ForwardPlusEnabled`
- `ForwardPlusLocalLights`
- `ForwardPlusVisibleIndices`

Long term, shaders should use `ClusteredLightingEnabled` and cluster-specific uniforms.

## Cluster Grid Contract

Use a fixed frustum-space cluster grid:

| Axis | Phase-1 Default |
| --- | --- |
| X/Y | 16x16 pixel tiles |
| Z | 16 view-depth slices |
| Stereo | one cluster grid per eye |

Cluster index:

```text
clusterIndex =
    eyeIndex * (tileCountX * tileCountY * depthSliceCount) +
    depthSlice * (tileCountX * tileCountY) +
    tileY * tileCountX +
    tileX
```

Depth slicing:

- Start with linear view-depth slices to match MJP's sample and keep reasoning simple.
- Add logarithmic or hybrid depth slicing later if light distribution needs it.
- Do not depend on depth buffer min/max for phase 1. This keeps clustered forward independent of a depth prepass.

Required uniforms/state:

- `ClusteredScreenSize`
- `ClusteredTileSize`
- `ClusteredTileCountX`
- `ClusteredTileCountY`
- `ClusteredDepthSliceCount`
- `ClusteredNearZ`
- `ClusteredFarZ`
- `ClusteredDepthScaleBias` or equivalent slice mapping constants
- `ClusteredMaxLightsPerCluster`
- `ClusteredEyeCount`

Required buffers:

- local light records
- visible light indices
- per-cluster range/count metadata
- optional per-cluster debug count
- optional overflow count

## Buffer Layout

The current fixed `cluster * MaxLightsPerTile` index buffer is simple but expensive when moving from 2D tiles to 3D clusters. With 16 Z slices, the existing `1024` fixed count becomes too large.

Recommended phase-1 layout:

```csharp
struct ClusterLightGridEntry
{
    public uint Offset;
    public uint Count;
}
```

Buffers:

- `ClusteredLightGrid`: one `Offset/Count` per cluster
- `ClusteredVisibleLightIndices`: compact index list
- `ClusteredLightCounts`: debug count or unclamped count per cluster

Two implementation options:

1. Fixed-per-cluster list for first bring-up:
   - simplest migration from current Forward+
   - use much smaller `MaxLightsPerCluster`, such as 128
   - acceptable for a short diagnostic phase

2. Compact list with prefix sum:
   - scalable final path
   - one pass counts lights per cluster
   - prefix-sum counts into offsets
   - one pass fills compact index list

The final implementation should use the compact list. The design allows a fixed-list prototype only if it is clearly marked as temporary.

## Light Binning Algorithm

### Phase 1: Compute Cluster Culling

Build cluster frusta analytically from camera projection, tile bounds, and Z slice range.

For each cluster:

- build or load the cluster's six frustum planes
- test point lights as spheres
- test spot lights as cones
- append intersecting light indices
- record count and overflow

This is close to the existing compute pass and is the lowest-risk upgrade from 2D tiles.

Improvements over the current path:

- culling volume has finite Z thickness
- forward shaders avoid over-including lights across the full camera depth range
- deferred shaders can use the same list per pixel
- no dependency on forward depth population

### Phase 2: Better Spotlight Accuracy

MJP notes that plane-based cone/frustum tests can produce many false positives for spotlights. The phase-1 compute test should be treated as conservative but not final.

Follow-up options:

- tighter cone-vs-cluster tests in view space
- sphere or cone AABB prefilter before plane tests
- per-light projected bounds to restrict tested cluster ranges
- rasterized light bounding geometry into cluster occupancy for spotlights
- conservative rasterization when available
- MSAA raster fallback for light-bin marking when conservative rasterization is unavailable

The rasterized occupancy path should be optional and measured. It is likely most useful for large shadow-casting spotlights and later clustered decals.

## Forward Shading Integration

Forward shaders should compute cluster index from screen XY and fragment view depth.

Required shader changes:

- add clustered lighting uniforms
- add `ClusteredDepthSliceCount`
- add `XRENGINE_GetClusterDepthSlice(viewDepth)`
- replace 2D tile base index with cluster-grid lookup
- loop over `ClusteredLightGrid[clusterIndex].Count`
- index into `ClusteredVisibleLightIndices[Offset + i]`

The existing `ForwardLighting.glsl` already resolves view/camera context and local light records. The first migration can keep the local light record shape and shadow metadata bindings, then swap only the list lookup.

Compatibility:

- if `ClusteredLightingEnabled` is false, keep current brute-force forward fallback
- while migrating, `ForwardPlusEnabled` can map to clustered lighting enabled
- `ForwardPlusDebugOverlay` should become a clustered debug overlay with a selectable Z slice

## Deferred Lighting Integration

Add a clustered deferred light pass that writes `LightingAccumTexture`.

Recommended pass:

```csharp
public sealed class VPRC_ClusteredDeferredLightPass : ViewportRenderCommand
```

Inputs:

- `AlbedoOpacity`
- `Normal`
- `RMSE`
- `DepthView`
- clustered light records
- clustered light grid
- visible light indices
- local shadow metadata
- directional light data
- ambient/probe resources if the pass also replaces final combine work

Outputs:

- `LightingAccumTexture` or `MsaaLightingTexture` depending on mode

Phase-1 split:

- local point/spot lights move to clustered deferred compute
- directional lights can remain fullscreen graphics or move into the compute pass
- probe/IBL combine can remain in `DeferredLightCombine.fs`

Final shape:

- one deferred compute shader reconstructs surface data
- resolves cluster by pixel view depth
- loops local lights in the cluster
- applies directional lights
- applies shadow terms
- writes lighting accumulation

Fallback:

- keep current light-volume rendering as `DeferredLightVolumes`
- add setting `DeferredLocalLightMode = LightVolumes | Clustered`
- default to light volumes until clustered deferred validation passes

## Deferred Texturing Integration

Clustered deferred lighting should be designed as the lighting backend for both:

- materialized deferred GBuffer
- future deferred texturing

For materialized deferred:

- read material properties from `AlbedoOpacity`, `Normal`, and `RMSE`
- use cluster lists for local lights

For deferred texturing:

- read geometry-only GBuffer
- resolve material textures from bindless descriptors
- use the same cluster lists for local lights
- optionally apply clustered decals before or during shading

This prevents a second light-list implementation when deferred texturing lands.

## Deferred MSAA Repair

### Current Problem

The current deferred MSAA path has several fragile traits:

- it resolves the GBuffer before lighting
- it uses stencil as the scheduling structure
- it duplicates every light draw across simple and complex phases
- complex pixels are sparse, but scheduled through graphics light volumes
- edge detection only compares normal and depth, missing some material-boundary cases

### Target Model

Use explicit MSAA edge masks and tile classification.

Recommended resources:

- `MsaaEdgePixelMask`: bitset or `R8UI` texture marking complex pixels
- `MsaaEdgeTileList`: compact list of 8x8 tiles containing edge pixels
- `MsaaNonEdgeTileList`: compact list of 8x8 tiles with no edge pixels
- `MsaaTileDispatchArgs`: indirect dispatch args for edge and non-edge lists
- optional `MsaaEdgePixelCount` and debug counters

Classification pass:

```text
Read MSAA GBuffer samples
For each pixel:
    compare material/transform identity where available
    compare depth or depth-gradient
    compare normal
    optionally compare RMSE/albedo thresholds for same-depth material changes
    write edge mask
For each 8x8 tile:
    append tile to edge list if any edge pixel exists
    otherwise append tile to non-edge list
    write indirect dispatch args
```

For current materialized deferred, use `TransformId`, depth, normal, and optional materialized property thresholds. When deferred texturing adds `DeferredMaterialId` and depth gradients, prefer MJP-style material ID plus depth-gradient edge detection.

### MSAA Clustered Deferred Shading

Use two compute variants:

1. Non-edge tile shader:
   - dispatched over `MsaaNonEdgeTileList`
   - shades one representative sample per pixel
   - writes all samples or writes a single resolved lighting target depending on chosen storage model

2. Edge tile shader:
   - dispatched over `MsaaEdgeTileList`
   - for non-edge pixels inside an edge tile, shade one sample
   - for edge pixels, shade all samples
   - uses cluster lookup per sample when sample depths differ

Then resolve MSAA lighting to `LightingAccumTexture`.

This mirrors MJP's scheduling advantage: sparse complex pixels are compacted into tile lists, and the non-edge shader avoids shared-memory/per-sample overhead.

### Storage Choice

Phase 1 should keep an MSAA lighting target:

- compute writes `MsaaLightingTexture`
- resolve writes `LightingAccumTexture`
- downstream passes continue reading non-MSAA lighting

Later, non-edge tiles may write directly to non-MSAA lighting if synchronization and blending stay simple, but that is an optimization.

## Pipeline Placement

Materialized deferred, non-MSAA:

```text
GBuffer
ClusteredLightBinning
ClusteredDeferredLightPass
DeferredLightCombine / probes / IBL
Forward pass
```

Materialized deferred, MSAA:

```text
MSAA GBuffer
Resolve classic GBuffer for compatibility/debug consumers if needed
ClusteredLightBinning
MsaaDeferredClassifyTiles
ClusteredDeferredMsaaLightPass
Resolve MSAA lighting
DeferredLightCombine / probes / IBL
Forward pass
```

Forward:

```text
Optional depth/prepass
ClusteredLightBinning
OpaqueForward / MaskedForward consume clusters
Transparent forward consumes clusters where safe
```

Deferred texturing compatibility:

```text
Geometry-only GBuffer
ClusteredLightBinning
BindlessMaterialResolve
Deferred decals
ClusteredDeferredLightPass
```

The exact order of `BindlessMaterialResolve`, decals, and clustered light pass depends on whether the phase is compatibility or native deferred texturing. The cluster data itself can be shared.

## Settings

Add settings:

```csharp
public enum ELocalLightSelectionMode
{
    LightVolumes,
    TiledForwardPlus2D,
    Clustered3D,
}

public enum EDeferredMsaaLightingMode
{
    Disabled,
    StencilSplitGraphics,
    ClusteredComputeTiles,
}
```

Recommended defaults during bring-up:

- forward local light selection: `Clustered3D` in diagnostics only, then default when validated
- deferred local light selection: `LightVolumes` until clustered deferred passes visual tests
- deferred MSAA lighting: keep disabled or current path until `ClusteredComputeTiles` is validated

Explicit requested modes should fail visibly when required compute, descriptor, or texture support is unavailable.

## Diagnostics

Add counters:

- tile count X/Y
- depth slice count
- cluster count
- local light count
- total cluster light references
- max lights in one cluster
- average lights per cluster
- overflowed clusters
- clustered light-bin GPU time
- clustered deferred light GPU time
- MSAA edge pixel count
- MSAA edge tile count
- MSAA non-edge tile count
- MSAA per-sample shaded pixel count

Debug views:

- cluster light count heatmap for selected Z slice
- max-over-Z tile heatmap
- selected pixel cluster ID
- per-light cluster occupancy
- MSAA edge mask
- MSAA edge/non-edge tile overlay
- deferred clustered vs light-volume difference view

## Validation

Source tests:

- cluster index formula includes Z slice
- forward shader consumes `ClusteredDepthSliceCount`
- deferred clustered pass consumes clustered buffers
- old Forward+ 2D path remains selectable during migration
- MSAA tile classification resources are declared in render graph metadata

Runtime smoke:

- one point light in near slice does not affect far-only geometry in the same XY tile
- one point light in far slice does not affect near-only geometry in the same XY tile
- large spotlight does not overflow most clusters unnecessarily
- stereo renders each eye with the correct cluster grid
- deferred clustered and light-volume modes match within tolerance for simple scenes
- MSAA edge mask appears only on geometry/material boundaries
- MSAA clustered compute path matches non-MSAA lighting on simple pixels

RenderDoc/MCP validation:

- inspect cluster buffers and counts
- inspect selected pixel cluster index
- inspect MSAA edge/non-edge tile lists
- compare light-volume and clustered deferred captures
- verify no Vulkan validation errors around compute/graphics transitions

## Rollout Plan

### Phase 0 - Baseline And Debug

- Document current 2D Forward+ behavior and deferred light-volume behavior.
- Add counters for current tile counts and light counts.
- Add a debug overlay that can show current max light count per 2D tile.

### Phase 1 - Shared Clustered Light Data

- Add clustered lighting state and engine binding names.
- Add 3D cluster buffers.
- Add `VPRC_ClusteredLightBinningPass`.
- Keep old Forward+ pass available.
- Add CPU-side and shader-side cluster index helpers.

### Phase 2 - Forward Clustered Shading

- Update `ForwardLighting.glsl` and `UberShader.frag` to consume cluster grid entries.
- Keep compatibility aliases for existing `ForwardPlus*` uniforms.
- Validate forward scenes and stereo.
- Retire old 2D Forward+ only after validation.

### Phase 3 - Deferred Clustered Lighting

- Add `VPRC_ClusteredDeferredLightPass`.
- Implement non-MSAA materialized deferred compute lighting for local lights.
- Keep directional/probe combine path stable.
- Compare against current light volumes.
- Add setting to switch deferred local light mode.

### Phase 4 - MSAA Tile Classification

- Add `VPRC_MsaaDeferredClassifyTiles`.
- Add edge mask, tile lists, and indirect dispatch args.
- Compare material/transform ID, depth/depth-gradient, and normal samples.
- Add debug views.

### Phase 5 - Clustered Deferred MSAA

- Add non-edge and edge compute shader variants.
- Write MSAA lighting target.
- Resolve lighting to non-MSAA `LightingAccumTexture`.
- Validate against current stencil split and non-MSAA baseline.

### Phase 6 - Better Spotlights And Decals

- Add tighter spotlight cluster tests.
- Prototype rasterized occupancy for large spotlights if compute false positives remain high.
- Extend cluster lists to decals after deferred texturing bindless material access is ready.

### Phase 7 - Vulkan And VR Hardening

- Validate clustered lighting under Vulkan dynamic rendering and validation layers.
- Validate OpenVR/OpenXR-related stereo paths when Vulkan VR paths are active.
- Keep MSAA deferred off by default in VR until frame-time and capture evidence is clean.

## Open Questions

- Should phase 1 use fixed-per-cluster lists for speed of implementation, or start directly with compact prefix-summed lists?
- Should Z slices be linear to match MJP first, or logarithmic to better distribute common game-camera depth ranges?
- Should deferred clustered lighting fully replace local light volumes, or remain a mode selected by light count/scene profile?
- Should directional lights move into clustered deferred compute, or stay fullscreen graphics for simpler cascade shadow handling?
- What is the minimum identity signal for MSAA edge detection before `DeferredMaterialId` exists: `TransformId`, material row ID, or materialized albedo/RMSE thresholds?
- Should clustered light binning eventually run on CPU for static/few-light scenes, as MJP notes is feasible, or stay GPU-only?

## Recommendation

Build 3D clustered light binning as a shared renderer service, not as another Forward+ special case. Migrate forward shading first because it already consumes light lists. Then add clustered deferred lighting as an alternative to per-light volume rendering. Finally, repair deferred MSAA with explicit edge masks and compact tile dispatches instead of stencil-only graphics scheduling.

This aligns XRENGINE's forward, deferred, and deferred-texturing paths around one light-selection model and gives deferred MSAA a clear path out of the current broken/light-volume-heavy implementation.
