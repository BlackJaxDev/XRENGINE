# Deferred Texturing Integration Design

Status: reference design
Last Updated: 2026-06-17

Implementation prerequisite: [Vulkan Fully Bindless Materials TODO](../../todo/rendering/vulkan-fully-bindless-materials-todo.md).

Related docs:

- [Vulkan Bindless And Deferred Texturing Audit](../../audit/vulkan-bindless-and-deferred-texturing-audit-2026-06-17.md)
- [Bindless Deferred Texturing Plan](../texturing/bindless-deferred-texturing-plan.md)
- [Dynamic Indirect Material Bindings](dynamic-indirect-material-bindings.md)
- [Default Render Pipeline Notes](../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [TheRealMJP/DeferredTexturing](https://github.com/TheRealMJP/DeferredTexturing)
- [MJP: Bindless Texturing For Deferred Rendering And Decals](https://therealmjp.github.io/posts/bindless-texturing-for-deferred-rendering-and-decals/)

## Summary

Deferred texturing moves material texture sampling out of the opaque geometry pass. The geometry pass writes geometric interpolation data plus material IDs, and a later pass uses bindless material texture indices to reconstruct or directly shade visible pixels.

XRENGINE should integrate deferred texturing as an optional default-pipeline mode, not as a replacement for the current materialized deferred path. The first implementation should be compatibility-first: reconstruct the current `AlbedoOpacity`, `Normal`, and `RMSE` buffers after a texture-free geometry pass, then let existing deferred decals, lighting, ambient occlusion, forward passes, and post-processing continue mostly unchanged.

The native version can come later: lighting and clustered decals consume material texture data directly without reconstructing the classic GBuffer unless a debug view or downstream effect asks for it.

## Motivation

The current deferred path samples material textures during rasterization. That is simple, but it makes overdraw expensive and locks material fetch work to the geometry pass. A deferred-texturing path gives the renderer another option:

- cheaper opaque geometry pass
- texture samples only for visible pixels
- one material-ID indirection model shared with GPU-driven rendering
- better alignment with Vulkan descriptor indexing and material-table work
- a path toward clustered decals and richer material modifiers

This is especially relevant for large material-diverse scenes where CPU material binding and overdraw-heavy texture sampling both become pressure points.

## Current Pipeline

The default deferred path is materialized:

1. `AppendDeferredGBufferPass` binds `DeferredGBufferFBOName` or `MsaaGBufferFBOName`.
2. `VPRC_RenderMeshesPass` renders `EDefaultRenderPass.OpaqueDeferred`.
3. `VPRC_RenderMeshesPass` renders `EDefaultRenderPass.DeferredDecals` into the same GBuffer.
4. MSAA GBuffer data resolves when needed.
5. `AppendLightingPass` runs `VPRC_LightCombinePass` using `AlbedoOpacity`, `Normal`, `RMSE`, and `DepthView`.

The current non-MSAA GBuffer contract is:

| Attachment | Role |
| --- | --- |
| `AlbedoOpacity` | materialized base color and opacity |
| `Normal` | packed normal |
| `RMSE` | packed roughness, metallic, specular, emission-style values |
| `TransformId` | transform/object ID |
| `DepthStencil` | depth and stencil |

Deferred texturing changes the first half of that contract. The geometry pass can no longer produce final materialized values because texture sampling moves later.

## Goals

- Add a selectable `DeferredTexturing` path to the default render pipeline.
- Keep `Deferred` as the existing materialized GBuffer path.
- Use Vulkan bindless descriptor indices as the primary production material texture indirection model.
- Make the first version compatible with current deferred decals and lighting.
- Keep the feature opt-in until visual quality, performance, MSAA, and stereo are validated.
- Preserve editor picking/debug needs through `TransformId` or an equivalent object identity attachment.
- Keep failures visible when required bindless features are unavailable.

## Non-Goals

- Do not replace the current deferred path immediately.
- Do not require MSAA support for the first implementation.
- Do not require stereo or VR support for the first implementation.
- Do not support every custom material graph in phase 1.
- Do not make deferred texturing a CPU fallback path for missing Vulkan bindless support.
- Do not merge native clustered decals into phase 1.

## Render Path Model

The default render pipeline should expose explicit mode selection rather than overloading one boolean.

Recommended enum:

```csharp
public enum EDefaultRenderPath
{
    Forward,
    ForwardPlus,
    Deferred,
    DeferredTexturing,
    DeferredTexturingPlus,
}
```

Meaning:

| Mode | Meaning |
| --- | --- |
| `Forward` | forward shading without requiring clustered light lists |
| `ForwardPlus` | forward shading with Forward+ clustered light culling |
| `Deferred` | current materialized GBuffer |
| `DeferredTexturing` | texture-free geometry plus bindless material resolve into the classic GBuffer |
| `DeferredTexturingPlus` | native clustered deferred texturing with clustered lights/decals/material modifiers |

`DeferredTexturingPlus` should stay unavailable until native clustered decals and direct bindless deferred lighting exist. It should not silently alias to `DeferredTexturing`.

## Architecture

### Phase 1: Compatibility Deferred Texturing

Frame flow:

```text
Depth / optional prepass
Opaque geometry pass, texture-free
Bindless material resolve
Deferred decals against reconstructed materialized buffers
Ambient occlusion
Forward+ light culling if enabled
Deferred light combine
Forward/transparency/post-processing
```

The key idea is to preserve existing downstream consumers. `VPRC_BindlessMaterialResolve` writes the same `AlbedoOpacity`, `Normal`, and `RMSE` outputs expected by decals and lighting.

Pipeline change:

```text
Current:
OpaqueDeferred -> DeferredDecals -> Lighting

Compatibility deferred texturing:
OpaqueDeferredGeometryOnly -> BindlessMaterialResolve -> DeferredDecals -> Lighting
```

The geometry-only mesh pass may reuse `EDefaultRenderPass.OpaqueDeferred` for sorting and pass membership, but it must use a different shader/material layout and a different FBO attachment contract. If that becomes too ambiguous in tooling, introduce a new internal pass identity for the geometry-only variant.

### Phase 2: Native Deferred Texturing

Frame flow:

```text
Depth / optional prepass
Opaque geometry pass, texture-free
Clustered decal/material modifier pass
Bindless deferred lighting
Optional classic GBuffer reconstruction for debug/downstream effects
Forward/transparency/post-processing
```

In this mode, lighting samples material textures directly using material IDs and descriptor indices. Decals become clustered material modifiers or projected records consumed by the same resolve/lighting domain.

## Geometry-Only GBuffer Contract

The geometry pass should output only the data needed to reconstruct material shading later.

Recommended phase-1 attachments:

| Attachment | Contents | Notes |
| --- | --- | --- |
| `DeferredBasis` | packed normal or tangent frame | phase 1 can start with normal-only, but normal mapping needs tangent-frame data |
| `DeferredUvDepthGrad` | `uv0.xy`, `dzdx`, `dzdy` | supports material sampling and depth-gradient reconstruction |
| `DeferredUvGrad` | `dudx`, `dudy`, `dvdx`, `dvdy` | optional in phase 1, needed for stable explicit gradients |
| `DeferredMaterialId` | material ID | integer attachment, likely `R32UI` |
| `TransformId` | transform/object ID | preserve picking/debug behavior |
| `DepthStencil` | depth/stencil | reuse existing depth path |

Candidate formats:

| Attachment | Candidate Format |
| --- | --- |
| `DeferredBasis` | `RGBA8_SNORM` or `RGBA16F` until quality is measured |
| `DeferredUvDepthGrad` | `RGBA16F` |
| `DeferredUvGrad` | `RGBA16F` |
| `DeferredMaterialId` | `R32UI` |
| `TransformId` | existing `R32UI` |

Start with simpler formats and optimize after RenderDoc inspection and quality tests. The first goal is correctness and observability.

## Material Resolve Pass

Add a viewport render command:

```csharp
public sealed class VPRC_BindlessMaterialResolve : ViewportRenderCommand
```

Responsibilities:

- read geometry-only attachments
- read the material table
- read the global Vulkan material texture descriptor table
- sample albedo, normal, and roughness/metallic/specular/emission textures
- apply scalar material constants
- reconstruct `AlbedoOpacity`, `Normal`, and `RMSE`
- write existing materialized GBuffer attachments for downstream compatibility

Implementation choices:

- Compute is preferred for flexible tiling, explicit barriers, and future clustered/native evolution.
- Fullscreen graphics is acceptable for the first prototype if it reduces integration risk.
- The chosen path must have clear synchronization with prior geometry writes and later decal reads.

The resolve pass should support debug modes:

- material ID
- descriptor indices
- UVs
- basis/tangent frame
- fallback texture hits
- reconstructed albedo
- reconstructed normals
- reconstructed RMSE

## Material And Shader Requirements

Deferred texturing depends on the dynamic material binding system.

Material rows must provide:

- albedo/opacity descriptor index
- normal descriptor index
- roughness/metallic/specular/emission descriptor index or equivalent packed semantics
- base color and opacity constants
- roughness, metallic, specular, emission constants
- flags for texture presence and fallback state

Shader requirements:

- Geometry-only variants must not sample material textures.
- Geometry-only variants must output material ID.
- Resolve variants must use non-uniform descriptor indexing for material textures.
- Backend-specific bindless code must be generated statically, not selected by runtime branches in hot shader paths.

The first implementation should support the standard opaque deferred PBR contract only. Materials with unsupported custom shading should use the existing `Deferred` or `Forward` path until their material layout is explicitly declared.

## Decal Integration

Phase 1 keeps current deferred decals after material resolve:

```text
Geometry-only GBuffer
Bindless material resolve -> classic AlbedoOpacity/Normal/RMSE
DeferredDecals -> modifies classic materialized buffers
Lighting
```

This preserves current decal behavior and avoids a large decal rewrite.

Native deferred texturing changes decals:

- decal records should be clustered or projected into a decal list
- the lighting/resolve domain should apply decal material modifiers
- decals should no longer require pre-existing materialized `AlbedoOpacity`, `Normal`, and `RMSE`

Native decals are intentionally out of phase 1.

## Ambient Occlusion, GI, And Lighting

Compatibility mode should keep existing AO/GI/light combine behavior by reconstructing the classic buffers before those passes run.

Native mode needs separate review for each consumer:

- AO usually needs depth and normals, so it can consume `DepthView` plus resolved or geometry normals.
- Probe GI and light combine need material properties; native mode should fetch them from material rows/textures.
- SSR and post effects that read the classic GBuffer may need optional reconstruction or new bindings.

## MSAA And Stereo

MSAA is not required for phase 1.

Deferred texturing with MSAA needs additional decisions:

- per-pixel or per-sample material resolve
- edge classification
- complex pixel marking
- explicit gradient handling across sample patterns
- resolve order relative to decals and lighting

Stereo/VR is also not required for phase 1. Before enabling for VR:

- validate multiview-compatible geometry-only attachments
- measure bandwidth cost of added attachments
- confirm descriptor indexing works with stereo shader variants
- inspect captures for both eyes

## Synchronization And Resource Lifetime

The compatibility path introduces a new write/read boundary:

```text
Geometry-only GBuffer writes
Resolve reads geometry-only, writes classic GBuffer
Decals read/write classic GBuffer
Lighting reads classic GBuffer
```

The render pipeline must:

- declare geometry-only attachments as resources
- declare classic reconstructed attachments as resolve outputs
- insert barriers between geometry and resolve
- insert barriers between resolve and decals/lighting
- keep diagnostics clear when dynamic rendering or Vulkan FBO ownership changes affect these resources

The resolve pass must not sample descriptors whose textures have been retired or replaced without a valid fallback.

## Settings And Diagnostics

Add settings for:

- default render path
- deferred texturing enable/disable/required
- geometry-only GBuffer debug view
- material resolve debug view
- fallback descriptor visualization

Diagnostics should report:

- selected render path and reason
- bindless capability tier
- material resolve pass active/inactive
- geometry-only attachment formats
- fallback descriptor hit count
- material rows used
- descriptor table slots used
- resolve pass GPU time

If `DeferredTexturing` is requested and Vulkan bindless is unavailable, the engine should fail visibly or select a documented fallback only when the user asked for `Auto`.

## Performance Expectations

Potential wins:

- reduced texture sampling under overdraw
- fewer material-bound shader variants for opaque deferred
- better batching for material-diverse scenes
- improved fit for GPU-driven rendering

Potential costs:

- more GBuffer bandwidth if geometry-only attachments are large
- extra resolve pass
- descriptor-index divergence
- explicit-gradient storage or reconstruction cost
- native decal rewrite complexity

The first benchmark should compare:

- current `Deferred`
- `DeferredTexturing` compatibility mode
- `ForwardPlus` for material-diverse scenes

Use scenes with controlled overdraw and material diversity so the result explains when deferred texturing is worth selecting.

## Validation Strategy

Source and unit tests:

- render-path enum and setting selection
- geometry-only FBO creation
- material resolve pass insertion order
- material row descriptor index availability
- shader source generation for geometry-only and resolve variants

Runtime smoke:

- Vulkan Unit Testing World with several textured opaque materials
- missing texture fallback
- normal map reconstruction
- decal after resolve
- AO and lighting after resolve
- render-path toggle between `Deferred` and `DeferredTexturing`

Visual/GPU inspection:

- MCP screenshots of material-diverse scene
- RenderDoc capture of geometry-only attachments
- RenderDoc capture of material table buffer and descriptor table
- exported `AlbedoOpacity`, `Normal`, and `RMSE` after resolve

Performance validation:

- GPU time for geometry pass, resolve pass, decals, lighting, and total frame
- descriptor update count
- material row upload bytes
- texture fallback count
- GBuffer bandwidth estimate

## Rollout Plan

### Phase 0 - Prerequisites

- Complete Vulkan global bindless material table.
- Confirm material rows contain descriptor indices.
- Confirm Vulkan bindless shaders sample descriptor arrays in an active path.
- Add diagnostics and fallback descriptors.

### Phase 1 - Pipeline Surface

- Add `EDefaultRenderPath`.
- Add settings and diagnostics for selected path.
- Keep default selection on current `Deferred`.
- Add source tests for path selection.

### Phase 2 - Geometry-Only GBuffer

- Add geometry-only textures/FBO creation.
- Add geometry-only opaque deferred shader variants.
- Output material ID, transform ID, UVs, gradients, and basis data.
- Add debug captures for new attachments.

### Phase 3 - Compatibility Resolve

- Implement `VPRC_BindlessMaterialResolve`.
- Reconstruct `AlbedoOpacity`, `Normal`, and `RMSE`.
- Insert resolve before `DeferredDecals`.
- Keep existing decal and lighting passes unchanged.

### Phase 4 - Runtime Validation

- Validate material-diverse opaque scene.
- Validate missing texture fallback.
- Validate normal mapped materials.
- Validate decals after resolve.
- Validate AO and lighting after resolve.
- Capture and inspect RenderDoc frame.

### Phase 5 - Performance And Quality

- Compare `Deferred`, `DeferredTexturing`, and `ForwardPlus`.
- Tune attachment formats.
- Decide whether explicit UV gradients are required by default.
- Add editor-facing debug views.

### Phase 6 - Native Deferred Texturing

- Prototype direct bindless deferred lighting.
- Prototype clustered decal/material modifiers.
- Avoid reconstructing classic GBuffer except for debug or compatibility consumers.
- Re-evaluate AO, GI, SSR, and post-processing bindings.

### Phase 7 - MSAA, Stereo, And VR

- Add MSAA edge classification and per-sample policy.
- Validate multiview/stereo attachment layouts.
- Validate OpenVR/OpenXR render paths when Vulkan VR path is ready.
- Keep VR disabled by default until frame-time and capture evidence are clean.

## Open Questions

- Should phase 1 store full tangent frame or start with normal-only plus normal-map opt-out?
- Should `DeferredMaterialId` be a separate attachment or packed with another integer target?
- Should material resolve be compute from the start?
- How many standard texture semantics should the first material row support?
- Should unsupported custom deferred materials fall back per material to classic deferred, or should the whole pipeline mode reject them?
- How should editor material/shader inspectors expose active texture indirection and fallback descriptors?

## Recommendation

Build deferred texturing in two steps.

First, ship `DeferredTexturing` compatibility mode once Vulkan bindless material textures are real. It should reconstruct the existing materialized GBuffer so the rest of the default pipeline keeps working.

Second, build `DeferredTexturingPlus` as a native clustered path after decals, lighting, and material modifiers can operate directly on bindless material data. This keeps the first implementation valuable and testable without forcing the entire deferred renderer to change in one pass.
