# Deferred+ Render Path Design

Status: design proposal
Last Updated: 2026-06-30
Owner: Rendering

## Related Docs

- [Deferred Texturing Integration Design](deferred-texturing-integration-design.md)
- [Clustered Light Binning And Deferred MSAA Design](clustered-light-binning-and-deferred-msaa-design.md)
- [Dynamic Indirect Material Bindings](dynamic-indirect-material-bindings.md)
- [XRE Virtual Geometry Design](xre-virtual-geometry-design.md)
- [GPU Meshlet Zero-Readback Rendering Design](gpu-meshlet-zero-readback-rendering-design.md)
- [Deferred+ Render Path TODO](../../todo/rendering/optimization/deferred-plus-render-path-todo.md)
- [Visibility Buffer Rendering TODO](../../todo/rendering/optimization/visibility-buffer-rendering-todo.md)
- [Material Table And Texture Binding Ladder TODO](../../todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md)
- [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)

## External References

- [Nanite Macro Rendering Overview](https://www.elopezr.com/a-macro-view-of-nanite/)

## Summary

Deferred+ is an optional opaque render path that combines:

- a compact visibility-buffer geometry pass
- froxel or cluster visibility classification
- material-table texture indirection
- deferred texture evaluation
- material-region shading
- shared clustered froxel lighting data

The goal is to keep the geometry pass bandwidth tiny while still allowing rich
materials to use their own shading kernels. The geometry pass writes only depth,
surface identity, and the minimum data needed to reconstruct attributes later.
A later classification pass groups visible pixels into screen/depth froxels and
material regions. Material passes then shade only the visible regions that use a
compatible material kernel.

Forward+ should use the same clustered froxel light data. The current 2D
screen-region light binning model is a migration state only; the target
Forward+ path indexes local light lists by screen tile plus depth slice so
near, mid, and far fragments in the same XY tile do not all inherit the same
over-broad light set.

In one sentence: Deferred+ renders visibility first, then runs material and
lighting work only for visible surface regions.

## Motivation

The current materialized deferred path writes material properties during
geometry rasterization. That is robust, but it spends texture bandwidth before
knowing which fragments survive overdraw and it forces every deferred material
through the same GBuffer contract.

Deferred texturing improves that by moving texture samples into a later resolve
pass. A visibility buffer pushes the idea further:

- the geometry pass no longer writes albedo, normals, roughness, metallic, or
  other bulky materialized properties
- texture mapping is deferred until the visible pixel is known
- material diversity becomes a classification problem rather than a CPU binding
  loop
- different material families can run different material shading kernels
- clustered light and decal lists can be reused by forward, deferred, and
  Deferred+ paths

Deferred+ should not replace the current deferred renderer immediately. It
should be a selectable render path for dense opaque, material-diverse scenes
where GBuffer bandwidth, overdraw, and CPU material fan-out are bottlenecks.

## Design Answer

Yes, the requested path is possible, but the production version should not draw
one full-screen quad for every material instance. That would trade geometry
overdraw for screen-space quad overdraw and can diverge badly.

The better shape is:

```text
visibility pass
  -> froxel/material classification
  -> compact material region dispatches
  -> material-specific shading kernels
  -> clustered lighting / output resolve
```

The "screenspace quads over regions" idea is still valid as one execution
backend. A material region can be rendered by drawing tile-sized quads or
scissored rectangles with a material-id mask. The final architecture should also
allow compute dispatch over compact pixel lists, because compute can avoid
shading masked-out pixels inside a quad.

Deferred texture mapping is also possible. The material pass can sample albedo,
normal, roughness, metallic, emission, and custom textures after visibility is
known, using:

- material rows for constants and texture descriptor indices
- primitive or meshlet identity to reconstruct attributes
- explicit UV gradients or analytical derivatives for stable mip selection
- fallback descriptors for missing or nonresident textures

## Naming

Use `DeferredPlus` for the engine-facing render path name. The existing
`DeferredTexturing` design remains the compatibility-first path that reconstructs
the classic GBuffer after a texture-free geometry pass.

Recommended render-path enum shape:

```csharp
public enum EDefaultRenderPath
{
    Forward,
    ForwardPlus,
    Deferred,
    DeferredTexturing,
    DeferredPlus,
}
```

Mode meanings:

| Mode | Meaning |
| --- | --- |
| `Deferred` | Current materialized GBuffer path. |
| `DeferredTexturing` | Texture-free geometry, then material resolve into classic `AlbedoOpacity`, `Normal`, and `RMSE` buffers. |
| `DeferredPlus` | Visibility buffer plus froxel/material classification and material-region shading. Classic GBuffer reconstruction is optional and only used for compatibility/debug consumers. |

`DeferredPlus` is not a silent alias for `DeferredTexturing`. If required
features are unavailable and the user requested `DeferredPlus` explicitly, the
engine should fail visibly or report the selected fallback according to the
render-path selection policy.

`ForwardPlus` remains the user-facing forward clustered-lighting mode name, but
its production implementation should be clustered-froxel based rather than a
flat 2D tiled light list. Legacy 2D tiled Forward+ may stay as a validation or
fallback mode while clustered froxels are brought up, but new shared-lighting
work should target the backend-neutral clustered resources.

## Core Terms

| Term | Meaning |
| --- | --- |
| Visibility buffer | A compact per-pixel payload that identifies the visible surface rather than storing fully evaluated material properties. |
| Froxel | A frustum-space voxel, usually `screen tile X/Y + depth slice Z`, used to group pixels, lights, decals, and material work. |
| Material region | A compact screen-space region, tile list, or pixel list where the visible surface uses a compatible material shading kernel. |
| Material shading kernel | A generated or authored shader family for one material model, such as standard PBR, skin, cloth, toon, terrain, or unlit. |
| Deferred texture mapping | Sampling material textures after the geometry pass, using reconstructed attributes and material-table texture references. |
| Compatibility resolve | A pass that writes classic GBuffer outputs so existing deferred decals, AO, lighting, and debug views can keep working. |
| Native Deferred+ shading | A pass that shades directly from visibility, material, and light data without reconstructing the classic GBuffer unless a consumer asks for it. |

## Goals

- Add a selectable `DeferredPlus` render path for opaque material-diverse scenes.
- Minimize geometry-pass bandwidth by storing depth plus compact surface identity.
- Defer texture sampling until material-region passes.
- Let material families use distinct shading kernels and lighting logic.
- Use the material table and texture binding ladder for constants and textures.
- Reuse clustered/froxel light lists across forward, deferred, and Deferred+.
- Upgrade Forward+ light binning from 2D screen regions to clustered froxels.
- Keep current deferred and forward paths as correctness fallbacks.
- Preserve editor picking, selection IDs, debug views, and RenderDoc-friendly
  inspection points.
- Avoid CPU readbacks and per-frame allocations in the production path.

## Non-Goals

- Do not remove the current deferred path during the first implementation.
- Do not support alpha-blended transparency in Deferred+ phase 1.
- Do not make every arbitrary custom shader Deferred+ compatible automatically.
- Do not require mesh shaders or virtual geometry for the first version.
- Do not require MSAA, stereo, or VR support for the first version.
- Do not hide missing bindless or descriptor-indexed texture support behind an
  unreported CPU fallback.

## High-Level Frame Flow

Compatibility bring-up:

```text
GPU scene and material table update
Depth / visibility geometry pass
Froxel light and material classification
Material resolve into classic GBuffer
Deferred decals
AO / GI / clustered deferred lighting
Forward fallback and transparency
Post-processing
```

Native Deferred+:

```text
GPU scene and material table update
Depth / visibility geometry pass
Froxel light and material classification
Material-region shading and lighting
Optional classic GBuffer reconstruction for debug/compatibility consumers
Forward fallback and transparency
Post-processing
```

The compatibility version is the low-risk first step. It proves visibility,
attribute reconstruction, texture derivatives, material-table reads, and
fallback policy while keeping existing downstream passes mostly unchanged.

The native version is the target architecture. It avoids reconstructing bulky
buffers unless a specific pass needs them.

## Geometry Pass Contract

The Deferred+ geometry pass should be texture-free and as small as possible.
It writes visibility, depth, and identity. It does not sample albedo, normal,
roughness, metallic, emission, or custom material textures.

Recommended minimum outputs:

| Output | Contents | Notes |
| --- | --- | --- |
| `DepthStencil` | depth and stencil | Depth reconstructs view/world position later. |
| `DeferredPlusVisibility` | packed draw, primitive, meshlet, or cluster identity | The key used to recover geometry and material data. |
| `TransformId` | editor/object identity | May be packed with visibility if format allows. |
| `Velocity` | optional current/previous motion | Required for TAA/TSR quality. Can be phase 2. |

Optional sidecar outputs:

| Output | Contents | When needed |
| --- | --- | --- |
| `DeferredPlusBarycentrics` | barycentric coordinates or packed primitive-local coordinates | Simpler attribute reconstruction when primitive identity alone is not enough. |
| `DeferredPlusUvGrad` | explicit UV gradients | First-quality path for stable texture mip selection. |
| `DeferredPlusNormalBasis` | geometric normal or tangent-frame seed | Useful before full vertex-fetch reconstruction is mature. |

Do not store world position. Reconstruct it from depth and camera matrices.
Do not store materialized albedo or RMSE in the visibility path. Those belong in
the material pass.

## Visibility Payload

The payload should be backend-neutral and stable across CPU direct, indirect,
meshlet, and future virtual-geometry paths.

Candidate logical fields:

| Field | Purpose |
| --- | --- |
| `DrawId` | Finds draw metadata, transform, mesh, material, skinning, and state class. |
| `PrimitiveId` | Finds triangle-local vertex attributes for classic mesh rendering. |
| `MeshletId` | Finds meshlet-local primitive data for meshlet rendering. |
| `MaterialId` | Optional fast path; can also be loaded from draw metadata. |
| `TransformId` | Editor selection, picking, and object debug identity. |

Packing options:

| Option | Pros | Cons |
| --- | --- | --- |
| 32-bit packed ID | Very small and fast | Requires indirection tables and tight limits. |
| 64-bit packed ID | More direct identity coverage | Format/support and bandwidth need validation. |
| Two 32-bit integer targets | Portable and inspectable | More bandwidth than a single target. |

Phase 1 should prefer correctness and inspection over clever packing. Once
captures prove the payload, tighten formats.

## Froxel Visibility Domain

Deferred+ uses froxels for two related but separate jobs:

1. Light/decal visibility:
   - which point lights, spot lights, local probes, and decals can affect each
     froxel
2. Material work scheduling:
   - which material kernels and material IDs actually appear in each froxel or
     screen tile

Froxel index:

```text
froxelIndex =
    eyeIndex * (tileCountX * tileCountY * depthSliceCount) +
    depthSlice * (tileCountX * tileCountY) +
    tileY * tileCountX +
    tileX
```

Recommended defaults should match clustered lighting unless a measured reason
appears:

| Axis | Phase-1 Default |
| --- | --- |
| X/Y | 16x16 pixel tiles |
| Z | 16 view-depth slices |
| Stereo | one froxel grid per eye when stereo is enabled |

The same froxel grid can back Forward+, materialized deferred clustered
lighting, deferred texturing, Deferred+, and future clustered decals.

## Forward+ Clustered Froxel Update

Forward+ should consume the shared froxel grid instead of maintaining a separate
2D screen-region light binning path. The old model builds one light list per
screen tile and eye, then every fragment in that tile loops the same local
lights regardless of depth. That over-includes lights whenever near and far
surfaces share an XY tile, and it prevents deferred, Deferred+, decals, and
forward shading from sharing one light-selection contract.

Target Forward+ behavior:

- build local point/spot light lists per clustered froxel, not per 2D tile
- keep directional lights outside the froxel local-light lists
- compute the consumer index from `screenXY + viewDepth + eyeIndex`
- use the same froxel index formula as Deferred+ material classification
- share light records, visible-light index buffers, overflow counters, and debug
  views with clustered deferred and Deferred+
- retain the old 2D tiled path only as an explicit compatibility/debug fallback
  during migration

The forward shader lookup changes from:

```text
tileIndex = tileY * tileCountX + tileX
lightList = ForwardPlusTileLights[tileIndex]
```

to:

```text
depthSlice = GetFroxelDepthSlice(fragmentViewDepth)
froxelIndex =
    eyeIndex * (tileCountX * tileCountY * depthSliceCount) +
    depthSlice * (tileCountX * tileCountY) +
    tileY * tileCountX +
    tileX
lightList = ClusteredFroxelLightGrid[froxelIndex]
```

Resource naming should migrate away from `ForwardPlus*` for the shared data.
Compatibility aliases are acceptable while existing shader includes are updated,
but the owning pass should be a clustered/froxel light-binning pass, not a
Forward+-only pass.

Recommended shared resources:

| Resource | Purpose |
| --- | --- |
| `ClusteredFroxelLightRecords` | Local point/spot light records and shadow metadata. |
| `ClusteredFroxelLightGrid` | Per-froxel `Offset/Count` into the visible-light index list. |
| `ClusteredFroxelVisibleLightIndices` | Compact light index list shared by forward and deferred consumers. |
| `ClusteredFroxelLightCounts` | Debug/unclamped count data for overlays and overflow analysis. |
| `ClusteredFroxelOverflowCounters` | Visible diagnostics when bounded lists overflow. |

This update makes Forward+ the first runtime consumer of the same clustered
froxel lighting service that Deferred+ needs. Deferred+ should not introduce a
second light-list builder; it should depend on the shared Forward+/clustered
froxels once that migration is in place.

## Material Classification

After the visibility pass, a compute classifier reads depth and visibility and
builds compact material work.

Outputs:

| Resource | Purpose |
| --- | --- |
| `DeferredPlusFroxelGrid` | Per-froxel light/decal/material metadata. |
| `DeferredPlusMaterialRangeMap` | Coarse tile metadata describing which material IDs or kernels appear in each region. |
| `DeferredPlusMaterialDepthOrMask` | Optional graphics-backend mask/depth target for rejecting pixels outside the active material region. |
| `DeferredPlusMaterialTileList` | Active screen tiles grouped by material kernel and optionally material ID. |
| `DeferredPlusPixelList` | Optional compact per-pixel list for high-diversity or high-overdraw tiles. |
| `DeferredPlusRegionDispatchArgs` | Indirect draw or dispatch arguments for material passes. |
| `DeferredPlusOverflowCounters` | Visible diagnostics for bounded-list overflow. |

Classification key:

```text
materialRegionKey =
    shadingKernelId
    + materialLayoutHash
    + materialStateClass
    + optional materialId
```

The key should group by shader compatibility first, not by material instance.
Many material instances should share one material shading kernel and differ only
by material-table row data.

Overflow policy must be explicit:

- route overflow tiles to compatibility deferred or forward fallback
- or run a conservative full-tile material pass with a visible counter
- never silently drop pixels

## Material Range And Graphics Scoping

Nanite's material resolve path demonstrates a useful graphics-backend
optimization for visibility buffers: classify the screen into coarse material
ranges, then use a cheap mask or depth/stencil-style test so each material draw
only shades pixels that actually belong to that material.

Deferred+ should allow the same idea as a bring-up and compatibility backend:

- build a coarse `DeferredPlusMaterialRangeMap` during classification
- optionally write a `DeferredPlusMaterialDepthOrMask` target where material or
  kernel identity can be tested cheaply by fixed-function depth/stencil or a
  tiny shader-side integer compare
- draw material-region quads only over tiles whose range/map says the material
  or kernel may be present
- reject individual pixels by comparing the visibility payload's material or
  kernel identity against the active region key
- keep the visibility buffer as the authoritative source of primitive,
  instance, and material identity

The range map may be a min/max material range, compact bitset, Bloom-like
membership filter, or sorted per-tile range depending on material-ID density.
The contract is conservative: false positives are allowed and only cost extra
shading, but false negatives are correctness bugs.

This does not replace the compute pixel-list backend. It makes the graphics
region backend less wasteful and gives RenderDoc-friendly stepping stones before
the fully compact compute path is ready.

## Material Region Execution

Deferred+ should support two execution backends.

### Graphics Region Backend

The graphics backend draws screen-space rectangles or tile quads for material
regions. The shader rejects pixels whose visibility payload does not match the
region key.

Advantages:

- closer to existing graphics shader infrastructure
- easier to support material-authored fragment shaders
- works well for large contiguous regions

Costs:

- quad overdraw inside mixed-material tiles
- derivative hazards at material boundaries
- many small regions can increase draw overhead

Use scissor rectangles, tile masks, stencil, or material-id masks to bound the
region. Do not draw one full-screen quad per material except as a diagnostic
fallback.

### Compute Pixel-List Backend

The compute backend dispatches material kernels over compact pixel lists or
tile-local pixel lists.

Advantages:

- shades only active pixels
- easier to compact sparse material regions
- natural fit for indirect dispatch, explicit barriers, and MSAA tile lists

Costs:

- material shaders need compute-compatible entry points or generated adapters
- derivatives must be explicit
- blending and fixed-function state are not available

This should be the target production backend for native Deferred+.

## Material Shading Kernels

Deferred+ should not compile one pipeline per material instance. It should
compile one kernel per material family and layout.

Examples:

- Standard PBR opaque
- Skin PBR
- Cloth/fabric
- Toon/cel shaded opaque
- Terrain/splat
- Unlit/emissive
- Decal-modified opaque

Each kernel:

- loads material constants from the material table
- loads texture descriptor indices or bindless handle indices from the material
  row
- reconstructs surface attributes from the visibility payload
- samples textures using explicit gradients or analytically reconstructed
  derivatives
- uses the froxel light list when it performs lighting
- writes either classic GBuffer outputs or final lighting/color outputs

Material-specific lighting is allowed, but it must be declared as part of the
kernel contract. For example, unlit materials can skip local light loops, skin
can use a skin BRDF, and toon can quantize lighting. The same light-list data
should still be shared.

## Deferred Texture Mapping

Texture mapping can be deferred, but the material pass must have enough data to
sample textures correctly.

Attribute sources:

| Source | Description | Use |
| --- | --- | --- |
| Stored UVs and gradients | Geometry pass writes UV and derivative sidecars. | Simplest first implementation, higher bandwidth. |
| Primitive ID plus barycentrics | Material pass fetches vertex attributes and interpolates. | Good long-term path for classic meshes. |
| Meshlet ID plus local primitive | Material pass fetches meshlet-local compressed attributes. | Good for meshlet and virtual geometry paths. |
| Generated surface cache | Precomputed or cached shade inputs for virtualized geometry. | Future optimization. |

Mip selection:

- Prefer explicit `textureGrad` style sampling in Deferred+ shaders.
- Derivatives from a full-screen quad are not trustworthy across material,
  primitive, UV seam, or depth discontinuities.
- Analytical derivatives from barycentric partials are the long-term preferred
  path.
- A stored `DeferredPlusUvGrad` sidecar is acceptable for the first quality
  slice if it is measured against the bandwidth budget.

Limitations:

- Alpha blending remains forward/OIT.
- Alpha testing needs special handling because coverage affects visibility. A
  masked material cannot wait until the material pass to decide whether its
  depth existed unless the visibility pass can evaluate the same coverage test.
- Parallax occlusion and displacement can be approximated in material shading,
  but they cannot change the already-written visibility depth without a
  specialized visibility path.

## Lighting Model

Deferred+ should consume the same clustered/froxel light data as Forward+ and
clustered deferred lighting.

Lighting options:

| Option | Description | Role |
| --- | --- | --- |
| Compatibility lighting | Material pass reconstructs classic GBuffer; existing lighting consumes it. | First bring-up. |
| Native material lighting | Material kernel evaluates local/directional/probe lighting and writes HDR output. | Final Deferred+ target. |
| Split lighting | Material pass writes compact material properties; shared clustered lighting computes common BRDFs. | Useful middle ground. |

Native material lighting lets material families own their lighting setup, but it
should still use shared light records, shadow maps, probes, and froxel light
lists. The customization point is the BRDF/material response, not a separate
light-culling system per material.

Directional lights can remain a shared fullscreen or compute pass at first.
Local point and spot lights should use froxel lists to avoid per-light volume
draw fan-out.

## Decals And Material Modifiers

Compatibility mode keeps decals after material resolve:

```text
Visibility
Material resolve -> classic GBuffer
Deferred decals
Lighting
```

Native Deferred+ should treat decals as material modifiers:

```text
Visibility
Froxel decal list
Material-region shading applies relevant decal records
Lighting/output
```

This avoids requiring materialized albedo/normal/RMSE buffers before decals.
Native clustered decals are not required for phase 1.

## Fallback Policy

Every visible pixel must resolve through exactly one path:

- Deferred+ native material-region shading
- Deferred+ compatibility resolve
- current materialized deferred path
- forward/Forward+ fallback
- transparent/OIT path

Unsupported material classes should be counted and visible in diagnostics.
Examples that should fall back initially:

- alpha-blended materials
- complex masked materials whose coverage requires expensive texture sampling
- custom shaders without material binding metadata
- shaders with side effects or framebuffer dependencies
- materials requiring unsupported texture binding rungs
- materials requiring displacement that changes visibility

Requested accelerated modes should fail loudly when their required backend
features are absent unless the user selected `Auto` fallback behavior.

## Backend Requirements

OpenGL phase 1:

- integer render targets for visibility identity
- SSBO material rows
- optional `ARB_bindless_texture` for texture diversity
- compute shader support for classification
- explicit visible fallback when bindless texture support is unavailable

Vulkan phase 1:

- storage buffers for scene, visibility metadata, and material rows
- descriptor indexing for material textures
- compute classification and indirect dispatch/draw support
- explicit barriers between visibility writes, classification, material shading,
  and downstream consumers

Both backends:

- must report selected texture binding rung
- must report whether Deferred+ is native, compatibility, or fallback
- must keep resource declarations inside the render pipeline resource lifecycle
  rather than ad hoc cache commands for stable core targets

## Resource Layout

Proposed resource names:

| Resource | Kind |
| --- | --- |
| `DeferredPlusVisibility` | integer texture |
| `DeferredPlusBarycentrics` | optional texture |
| `DeferredPlusUvGrad` | optional texture |
| `DeferredPlusNormalBasis` | optional texture |
| `DeferredPlusFroxelGrid` | buffer |
| `DeferredPlusMaterialRangeMap` | optional texture or buffer |
| `DeferredPlusMaterialDepthOrMask` | optional depth/stencil or integer texture |
| `DeferredPlusMaterialTileList` | buffer |
| `DeferredPlusPixelList` | optional buffer |
| `DeferredPlusRegionDispatchArgs` | indirect args buffer |
| `DeferredPlusDebugTexture` | optional debug output |

Compatibility outputs reuse existing targets:

- `AlbedoOpacity`
- `Normal`
- `RMSE`
- `TransformId`
- `DepthView`

Resource descriptors must include formats, dimensions, stereo layer policy,
sample count policy, resize behavior, and aliases to classic GBuffer targets
when compatibility mode is active.

## Settings And Diagnostics

Settings:

```csharp
public enum ELocalLightSelectionMode
{
    LightVolumes,
    TiledForwardPlus2D,
    ClusteredFroxels,
}

public enum EDeferredPlusMode
{
    Disabled,
    Auto,
    Compatibility,
    Native,
    Required,
}

public enum EDeferredPlusRegionBackend
{
    GraphicsRegions,
    ComputePixelLists,
}

public enum EDeferredPlusDerivativeMode
{
    StoredGradients,
    AnalyticalBarycentrics,
    ConservativeLodBias,
}
```

Diagnostics should report:

- selected render path and selection reason
- local light selection mode: light volumes, 2D Forward+ tiles, or clustered
  froxels
- Deferred+ mode: disabled, compatibility, native, or fallback
- region backend
- visibility payload format
- froxel tile size and depth slice count
- clustered froxel light-reference count and overflow count
- material region count
- material range-map tile count and false-positive estimate when available
- material tile count
- pixel-list count and overflow count
- material kernel count
- fallback material count and pixel count
- texture binding rung and reason
- explicit derivative mode
- geometry pass GPU time
- classification GPU time
- material shading GPU time
- lighting GPU time
- compatibility GBuffer reconstruction cost

Debug views:

- visibility ID
- material ID
- material kernel ID
- froxel depth slice
- material region heatmap
- tile/pixel-list overflow
- UVs and UV gradients
- reconstructed normal
- sampled albedo/roughness/metallic
- fallback route overlay
- light count per froxel

## Shader And Material Integration

Deferred+ depends on dynamic indirect material bindings.

Required material layout data:

- material constants
- texture references
- shading kernel ID
- material layout hash
- feature flags
- fallback eligibility

Required shader metadata:

- material-scoped fields
- texture slots and semantics
- required vertex attributes
- derivative requirements
- lighting model
- fallback path

Unknown material uniforms must resolve to `PerMaterialRequired` or `Invalid`.
They must not be silently packed into a Deferred+ material row.

The shader prewarm graph should include Deferred+ kernels and generated
adapters. Runtime shader parsing or layout synthesis must not occur in the
per-frame hot path.

## MSAA And Stereo

MSAA is a later phase.

Deferred+ MSAA needs:

- per-sample visibility or explicit complex-pixel masks
- sample-aware material classification
- tile lists for simple and complex pixels
- derivative policy across samples and edges
- lighting resolve policy before post-processing

Stereo and VR are also later phases.

Stereo needs:

- one visibility target per eye or a stereo array target
- one froxel grid per eye unless a proven shared-head-space grid is available
- per-eye projection in attribute reconstruction
- correct motion vectors and temporal history isolation

Do not enable Deferred+ in VR by default until captures prove both eyes and all
temporal inputs are correct.

## Performance Model

Potential wins:

- lower geometry-pass bandwidth
- fewer wasted texture samples under overdraw
- fewer CPU material binds
- material work proportional to visible coverage
- shared clustered/froxel light selection
- better fit for GPU-driven and meshlet submission

Potential costs:

- classification pass overhead
- attribute reconstruction cost
- material-region dispatch overhead
- explicit derivative computation
- cache-unfriendly vertex/texture fetches in screen space
- fallback complexity in mixed scenes

Deferred+ is expected to win when:

- opaque overdraw is significant
- material count is high
- textures are large or streaming
- CPU material binding is a bottleneck
- GBuffer bandwidth is expensive

It may lose when:

- the scene has few materials
- overdraw is low
- material shaders are cheap
- geometry is tiny enough that reconstruction dominates
- most visible content falls back to forward or classic deferred

## Rollout Plan

### Phase 0 - Contract And Baseline

- Define `DeferredPlus` as a selectable but disabled render path.
- Capture baseline images and timings for current `Deferred`, `ForwardPlus`,
  and `DeferredTexturing` where available.
- Record the current 2D Forward+ tile-light behavior so clustered froxels can be
  compared against it.
- Decide the initial visibility payload format.
- Decide phase-1 derivative mode.
- Add diagnostics fields before shader bring-up.

### Phase 1 - Visibility Geometry

- Add Deferred+ resource declarations.
- Add CPU-direct opaque visibility pass for static meshes.
- Add transform/editor identity preservation.
- Add debug view for visibility payload and depth.
- Route unsupported materials to current deferred/forward fallback.

### Phase 2 - Froxel And Material Classification

- Add froxel grid buffers.
- Add or consume the shared clustered froxel light-binning pass.
- Migrate Forward+ consumers from 2D tile light lookup to froxel lookup behind a
  selectable setting.
- Classify visible pixels into material regions.
- Build a conservative material range map for graphics-region bring-up.
- Build indirect region dispatch/draw arguments.
- Add overflow handling and diagnostics.
- Validate empty scene, single-material scene, mixed-material scene, and
  overflow cases.

### Phase 3 - Compatibility Material Resolve

- Add a standard PBR material resolve kernel.
- Reconstruct `AlbedoOpacity`, `Normal`, and `RMSE`.
- Sample material textures through the active texture binding rung.
- Validate UVs, normals, roughness, metallic, emission, and fallback textures.
- Keep existing decals, AO, and lighting after the resolve.

### Phase 4 - Native Material-Region Shading

- Add direct HDR/lighting output for the standard PBR kernel.
- Consume froxel light lists from the material kernel.
- Add unlit/emissive kernel.
- Add a difference/debug view against compatibility resolve.
- Keep classic GBuffer reconstruction available on demand.

### Phase 5 - More Material Families

- Add skin, cloth, terrain, toon, or other engine-owned material kernels as
  pass-declared layouts.
- Add editor diagnostics for material Deferred+ compatibility.
- Add shader prewarm coverage for Deferred+ variants.

### Phase 6 - Meshlet And GPU-Driven Integration

- Emit visibility from zero-readback indirect draws.
- Emit visibility from meshlet paths where backend support exists.
- Integrate with virtual-geometry main/post HZB visibility producers when
  available, and require both passes to write the same Deferred+ visibility
  payload contract.
- Avoid CPU readbacks for material region counts in production mode.
- Validate with material-diverse avatar and dense static scenes.

### Phase 7 - MSAA, Stereo, And VR

- Add MSAA complex-pixel classification.
- Add per-sample or edge-tile material shading.
- Add stereo-array resource declarations.
- Validate per-eye captures and temporal inputs.
- Keep VR disabled by default until frame-time and visual evidence are clean.

## Validation Strategy

Source and unit tests:

- render-path selection and fallback policy
- visibility payload pack/unpack
- material classification for empty, single-material, mixed, and overflow tiles
- material range-map false positives are allowed, but false negatives are caught
  by tests or debug validation
- material binding layout compatibility
- shader prewarm key includes Deferred+ mode, layout hash, backend feature mask,
  and derivative mode

Runtime smoke:

- static textured opaque mesh
- material-diverse opaque scene
- missing texture fallback
- normal mapped material
- decal after compatibility resolve
- clustered local lights in different depth slices
- mixed Deferred+ and fallback content

GPU inspection:

- RenderDoc capture of visibility payload
- froxel grid and material region buffers
- material range map and optional material depth/mask target
- reconstructed attributes
- material texture descriptor indices or bindless handles
- material resolve outputs
- native lighting output

Performance:

- geometry bandwidth estimate
- geometry pass GPU time
- classification GPU time
- material shading GPU time
- total frame comparison against current deferred and Forward+
- material region count and overdraw
- fallback pixel count

## Acceptance Criteria

- Deferred+ can be selected explicitly and reports whether it is native,
  compatibility, or fallback.
- The visibility pass writes enough data to map every opaque pixel back to a
  valid material, transform, and primitive or meshlet record.
- Standard opaque PBR materials can defer texture sampling until the material
  pass.
- Texture mip selection is stable at ordinary UV seams and material boundaries.
- Unsupported materials are visibly routed to fallback paths.
- Forward+ can consume clustered froxel light lists and can be compared against
  the legacy 2D tiled path during migration.
- Compatibility mode reconstructs classic GBuffer outputs well enough for
  existing decals, AO, and lighting to work.
- Native mode can shade at least one standard material kernel directly from
  visibility, material table, textures, and froxel light lists.
- Production Deferred+ mode performs no CPU readbacks in the steady-state render
  path.

## Open Questions

- Should phase 1 store UV gradients, or start directly with analytical
  barycentric derivatives?
- Should `MaterialId` be stored in the visibility payload or loaded from draw
  metadata?
- Should material regions group by material ID in phase 1 for simplicity, or by
  material kernel from the start?
- Should native Deferred+ write final HDR color, lighting accumulation, or a
  compact material-light intermediate?
- Is the material range plus material-depth/mask graphics backend worth keeping
  after compute pixel lists are stable, or should it become a debug-only path?
- How much classic GBuffer reconstruction should remain available for debug
  views and downstream compatibility?
- What is the first non-standard material family to validate after standard
  PBR: skin, cloth, terrain, toon, or unlit?
- Should material-region graphics draws remain as a supported backend, or only
  as a bring-up/debug path once compute pixel lists are working?

## Recommendation

Build Deferred+ as a staged evolution of the existing deferred texturing and
visibility-buffer work.

First, implement a compatibility path: compact visibility, material/froxel
classification, and material resolve back into the classic GBuffer. That proves
the hardest data contracts while preserving decals, AO, and lighting.

Then implement native Deferred+ material-region shading for standard PBR,
consuming shared froxel light lists and writing directly to lighting or HDR
output. Additional material families should opt in through pass-declared
material layouts and generated material-table bindings.

The important boundary is this: Deferred+ should make material diversity a
visible GPU data problem, not a hidden CPU binding loop and not a full-screen
quad per material instance.
