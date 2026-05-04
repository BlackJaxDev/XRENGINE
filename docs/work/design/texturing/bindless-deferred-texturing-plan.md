# Bindless Deferred Texturing Plan

Last Updated: 2026-03-16
Status: design
Scope: renderer-level plan for adding bindless deferred texturing to XRENGINE across OpenGL and Vulkan, using TheRealMJP DeferredTexturing sample as the main external reference point.

Related docs:

- [GPU-Based Rendering TODO](../todo/gpu-rendering.md)
- [GPU Render Pass Pipeline](gpu-render-pass-pipeline.md)
- [Transparency And OIT Implementation Plan](transparency-and-oit-implementation-plan.md)
- [OpenGL Renderer](../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../architecture/rendering/vulkan-renderer.md)

---

## 1. Executive Summary

XRENGINE currently uses a conventional deferred path: the geometry pass writes materialized surface properties into the G-Buffer, and later passes light or modify those surface properties. That works, but it forces texture sampling during rasterization, duplicates material fetch work under overdraw, and makes the geometry pass heavier than it needs to be.

This design introduces a new optional rendering mode: **bindless deferred texturing**. In that mode, the geometry pass stops sampling material textures. Instead it writes only the geometric inputs needed to reconstruct surface data later:

- depth
- packed tangent frame / normal basis
- UV set 0
- depth gradients
- optional UV gradients
- material ID
- transform ID

The deferred stage then uses a bindless material table to fetch the appropriate textures for each visible pixel. The core idea matches MJP's sample, but the implementation must fit XRENGINE's existing constraints:

1. One engine-level material fetch contract must work for both OpenGL and Vulkan.
2. Vulkan is the primary production path.
3. OpenGL support must be explicit about extension requirements and fallback behavior.
4. Existing downstream passes must keep working during migration.
5. GPU-driven rendering must remain the default architecture, not an afterthought.

### Recommended Direction

Ship this in two layers:

1. **Compatibility mode first**: geometry pass becomes texture-free, then a fullscreen material resolve pass reconstructs the current `AlbedoOpacity`, `Normal`, and `RMSE` buffers so the rest of the deferred pipeline stays mostly intact.
2. **Native mode second**: lighting, decals, and material layering consume bindless material data directly without first materializing the classic G-Buffer.

That phased approach captures most of the win early while avoiding a large renderer rewrite.

---

## 2. Current State

### 2.1 Relevant Code

| Area | Files |
|------|-------|
| Default deferred textures | `XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs` |
| GPU scene / indirect rendering | `XRENGINE/Rendering/Commands/GPUScene.cs`, `GPURenderPassCollection*.cs` |
| GPU material table | `XRENGINE/Rendering/Materials/GPUMaterialTable.cs` |
| Vulkan descriptor-indexing support | `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs`, `VulkanFeatureProfile.cs`, `VulkanDescriptorLayoutCache.cs` |
| Deferred decals | `XRENGINE/Scene/Components/Misc/DeferredDecalComponent.cs` |
| Deferred material import setup | `XRENGINE/Core/ModelImporter.cs` |
| Renderer settings | `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs` |
| Bindless material shader header | `Build/CommonAssets/Shaders/Common/MaterialTable.glsl` |
| Bindless mesh fragment stub | `Build/CommonAssets/Shaders/Graphics/BindlessMesh.frag` |

### 2.2 Current Deferred Attachment Layout

The default non-MSAA deferred path currently materializes these attachments during rasterization:

| Attachment | Current role |
|-----------|--------------|
| `DepthStencil` / `DepthView` | scene depth |
| `AlbedoOpacity` | base color + opacity |
| `Normal` | packed normal data |
| `RMSE` | roughness / metallic / specular-emissive-style packed data |
| `TransformId` | object/transform lookup |

That means the geometry pass already assumes it can evaluate most material inputs immediately.

### 2.3 Existing Bindless Groundwork

The engine already contains partial groundwork that this design should build on instead of bypassing:

- Vulkan settings already expose `EnableVulkanDescriptorIndexing` and `EnableVulkanBindlessMaterialTable`.
- Vulkan device creation already queries and enables runtime descriptor arrays, partially bound descriptors, and update-after-bind when supported.
- `GPUMaterialTable` already exists and is populated during GPU-driven passes.
- The current table is only a residency gate today: material entries are populated, but the stored handles remain zero.
- OpenGL already probes advanced extensions and already uses NV bindless indirect draw extensions, but not bindless texture sampling yet.

### 2.4 Existing Shader Stubs

`MaterialTable.glsl` already defines a shared GLSL `MaterialEntry` struct and `XR_CombineHandle()` utility matching the C# `GPUMaterialEntry` layout. `BindlessMesh.frag` uses these but its `SampleBindless()` function is a placeholder that returns UV-colored output instead of real texture sampling. These files are the starting point for the shader-side implementation.

### 2.5 Key Mismatch With Current Pipeline

The current deferred decal path writes directly into `AlbedoOpacity`, `Normal`, and `RMSE`. That is fundamentally coupled to a materialized G-Buffer. A bindless deferred texturing path can no longer assume those buffers exist during decal projection.

This is the largest architectural consequence of the feature.

---

## 3. Goals and Non-Goals

### 3.1 Goals

- Remove texture sampling from the opaque deferred geometry pass.
- Keep the geometry pass cheap and overdraw-friendly.
- Use one material ID indirection model for both OpenGL and Vulkan.
- Support GPU-driven rendering without CPU-side material rebinding.
- Preserve a practical migration path for current deferred lighting and decals.
- Make the feature opt-in and capability-gated per backend.

### 3.2 Non-Goals For Initial Delivery

- No attempt to support arbitrary multiple UV sets in phase 1.
- No attempt to support every specialized material graph path in phase 1.
- No requirement to replace the existing deferred path immediately.
- No requirement to ship MSAA parity on day one.
- No requirement to support the feature in VR for the first milestone.

### 3.3 Initial Product Boundaries

The first shippable version of this feature should target:

- desktop OpenGL and Vulkan
- non-stereo cameras
- opaque deferred materials only
- one UV set
- no per-material custom deferred shading branches beyond the standard PBR contract

Transparent materials, exact OIT, forward-only materials, and stereo support can remain on existing paths until the core path is stable.

---

## 4. Proposed Architecture

### 4.1 High-Level Frame Flow

### Phase 1: Compatibility-First

```
Depth / optional prepass
    ↓
Opaque geometry pass (texture-free)
    ↓
Bindless material resolve pass
    ↓
Deferred decals using reconstructed material buffers
    ↓
Deferred lighting / post-processing / debug views
```

This mode keeps most of the current downstream pipeline alive. It is the recommended first implementation.

### Pipeline Insertion Points (Phase 1)

The existing `DefaultRenderPipeline.CreateViewportTargetCommands` executes deferred geometry roughly as:

```
VPRC_RenderMeshesPass(OpaqueDeferred)          ← geometry + material sampling today
VPRC_RenderMeshesPass(DeferredDecals)           ← writes into AlbedoOpacity/Normal/RMSE
VPRC_ResolveMsaaGBuffer()                       ← if MSAA
[AO pass]
[ForwardPlusLightCullingPass]
[Deferred lighting combine]
```

In compatibility mode, the chain becomes:

```
VPRC_RenderMeshesPass(OpaqueDeferred)          ← texture-free; writes basis/UV/materialID
VPRC_BindlessMaterialResolve()                 ← NEW: reconstructs AlbedoOpacity/Normal/RMSE
VPRC_RenderMeshesPass(DeferredDecals)           ← unchanged, targets reconstructed buffers
VPRC_ResolveMsaaGBuffer()                       ← if MSAA
[AO pass]
[ForwardPlusLightCullingPass]
[Deferred lighting combine]
```

`VPRC_BindlessMaterialResolve` is a new viewport pipeline render command that performs the fullscreen or compute resolve. It is not a new `EDefaultRenderPass` value — it is a standalone VPRC pass that reads the geometry-only attachments plus the bindless material table and writes into the existing `AlbedoOpacity`, `Normal`, and `RMSE` targets.

### EDefaultRenderPass Interaction

The existing `EDefaultRenderPass.OpaqueDeferred` value is reused for the texture-free geometry pass. No new enum value is needed for the geometry pass itself — only the shader and FBO attachment set change. The resolve pass is a VPRC command, not a render-pass-sorted mesh pass, so it does not need an enum entry.

### Phase 2: Native Bindless Deferred

```
Depth / optional prepass
    ↓
Opaque geometry pass (texture-free)
    ↓
Clustered decal/material modifier pass
    ↓
Bindless deferred lighting pass
    ↓
Post-processing / debug views
```

In native mode, lighting fetches material textures directly from the bindless tables and applies decals/material modifiers in the same resolve domain.

### 4.2 Geometry Pass Contract

The new geometry pass writes only geometric interpolation data plus material indirection metadata.

### Proposed Core Attachments

| Attachment | Proposed contents | Notes |
|-----------|-------------------|-------|
| `DepthView` | depth | keep existing depth path |
| `DeferredBasis` | packed tangent frame + handedness | can reuse current packed-normal approach only if tangent-frame reconstruction stays accurate enough for normal maps |
| `DeferredUvDepthGrad` | `uv0.xy + dzdx + dzdy` | recommended always-on attachment |
| `DeferredUvGrad` | `dudx + dudy + dvdx + dvdy` | optional in phase 1, can be computed later if disabled |
| `DeferredMaterialId` | material ID | integer attachment |
| `TransformId` | existing transform/object ID | preserve editor/debug picking path |

### Recommended Formats

These are starting points, not final commitments:

| Attachment | Candidate format |
|-----------|------------------|
| `DeferredBasis` | `RGBA8_SNORM` packed quaternion or `R16G16B16A16_SNORM` if precision issues appear |
| `DeferredUvDepthGrad` | `RGBA16F` for implementation simplicity |
| `DeferredUvGrad` | `RGBA16F` |
| `DeferredMaterialId` | `R16UI` if material count stays below 65535, otherwise `R32UI` |

The compatibility-first version should prefer simpler formats over highly optimized bit packing until image quality and tooling are validated.

### 4.3 Material Fetch Contract

At the engine level, the bindless path should treat a material as a compact descriptor record containing:

- texture references for albedo, normal, roughness/metallic, emissive, AO, and optional future slots
- scalar material parameters not worth sampling from textures
- flags describing which slots are valid
- shading model ID if multiple deferred shading models remain supported

### Proposed Logical Material Record

```csharp
public struct GpuDeferredMaterialRecord
{
    public uint AlbedoRef;
    public uint NormalRef;
    public uint RmRef;
    public uint EmissiveRef;
    public Vector4 Scalar0;
    public Vector4 Scalar1;
    public uint Flags;
    public uint ShadingModel;
    public uint Reserved0;
    public uint Reserved1;
}
```

The important point is not the exact field list. The important point is that the engine-level contract must be **API-neutral** even if the backend encoding differs.

### Required Refactor

`GPUMaterialEntry` is currently biased toward OpenGL-style 64-bit handles but is populated with zero handles on Vulkan. That should be replaced by one of these approaches:

1. a backend-neutral logical record plus backend-specific upload packing
2. separate `GLGpuMaterialEntry` and `VkGpuMaterialEntry` layouts hidden behind one material-table builder

Recommendation: use option 1. Shared logical material records should live in engine code, while backend packing lives in renderer-specific code.

### 4.4 Compatibility Resolve Pass

The first implementation should add a fullscreen or compute pass that reconstructs the current materialized buffers from the new geometry-only G-Buffer.

### Outputs

- `AlbedoOpacity`
- `Normal`
- `RMSE`

### Inputs

- `DepthView`
- `DeferredBasis`
- `DeferredUvDepthGrad`
- optional `DeferredUvGrad`
- `DeferredMaterialId`
- bindless material table
- global texture table

### Why This Pass Is Worth Keeping Initially

- It minimizes downstream breakage.
- Existing deferred lighting can continue mostly unchanged.
- Existing debug views can continue to inspect familiar buffers.
- Existing deferred decals can continue to target materialized attachments.
- It lets the team validate G-Buffer accuracy before rewriting lighting.

This pass is not the final destination. It is the lowest-risk migration step.

---

## 5. Backend Design

### 5.1 Vulkan

Vulkan is the cleanest and most portable implementation path. The engine already has most of the prerequisite device feature plumbing.

### Required Vulkan Features

- runtime descriptor arrays
- partially bound descriptors
- sampled-image update-after-bind
- descriptor set layout support for update-after-bind pools

These are already queried during logical device creation and should remain the gate for enabling the feature.

### Binding Model

Use a global descriptor set containing:

- large runtime array of sampled textures or combined image samplers
- optional sampler table if samplers are separated
- storage buffer for the material table

### Recommendation

For phase 1, prefer a **global combined image sampler array**. That avoids splitting image and sampler indices in the material table and keeps shader code closer to the mental model of "material slot -> sampleable texture reference".

### Material Record Encoding

Each texture field in the material record stores a descriptor index into the global array.

Shader-side sampling then becomes conceptually:

```glsl
uint descriptorIndex = material.AlbedoRef;
vec4 albedo = texture(sampler2D(g_Textures[descriptorIndex]), uv);
```

The actual syntax depends on the chosen GLSL/SPIR-V pipeline and descriptor declarations, but the architecture is straightforward: descriptor index indirection, not raw handle indirection.

### Vulkan-Specific Work Items

- Add a dedicated descriptor table manager for bindless sampled textures.
- Allocate update-after-bind descriptor pools sized for worst-case loaded texture counts.
- Register every texture used by deferred materials in the global bindless table.
- Store descriptor indices in the Vulkan material table upload path.
- Add validation logging for out-of-range or unregistered descriptor indices.
- Handle texture streaming / unloading: when a texture is released, its descriptor slot must be rewritten to point at the fallback texture before the slot is reused. Never leave a stale descriptor index in the table.

### Vulkan Fallback Rule

If descriptor indexing is unavailable or disabled, the renderer must fall back to the current deferred pipeline automatically.

### 5.2 OpenGL

OpenGL support is viable, but it is inherently less portable because true bindless texturing depends on vendor extensions.

### Required OpenGL Capability

The full path should require one of these bindless texture extensions:

- `GL_ARB_bindless_texture`
- `GL_NV_bindless_texture`

If neither is present, XRENGINE should not attempt to emulate this feature through ad hoc texture rebinding. It should fall back to the current deferred path.

### Binding Model

OpenGL does not need descriptor arrays in the Vulkan sense. Instead, the material table stores bindless texture-sampler handles that the shader can convert into sampler objects.

### Material Record Encoding

For OpenGL, each texture field should store a 64-bit texture-sampler handle obtained from the API object layer.

That means the GL backend needs:

- a texture residency manager
- handle creation for texture + sampler pairs
- residency lifetime tracking when textures are loaded, reconfigured, or destroyed
- shader plumbing for 64-bit handle consumption

### Required OpenGL Refactor

The current engine-wide `GPUMaterialTable` comment explicitly assumes "bindless handles split into two uints". That is appropriate only for the OpenGL backend. The OpenGL upload path should keep that packing, but Vulkan should no longer inherit it.

### OpenGL Risk Callout

OpenGL bindless texture support is good enough for an optional backend feature, but not good enough to dictate engine-wide shader and material abstractions. Vulkan should define the primary abstraction. OpenGL should implement that abstraction with backend-specific packing.

### 5.3 Shared Backend Rules

Both backends must agree on these engine-level rules:

- `MaterialID` always indexes the same logical material table.
- Missing textures must map to valid fallback resources, not invalid references.
- Bindless deferred texturing is enabled only when the entire backend feature set is valid.
- Shader permutations must not fork engine semantics between Vulkan and OpenGL.
- Texture format handling: material textures may differ in format (sRGB vs linear, BC1 vs BC7, etc.). The shader resolve pass must sample through views that present a uniform interface (e.g., `sampler2D` returning linear float), relying on hardware sRGB decode and format-compatible image views rather than per-format shader branches.

---

## 6. Decals, Material Layering, and the Current Deferred Decal Path

### 6.1 Problem Statement

`DeferredDecalComponent` currently assumes it can sample and rewrite:

- `AlbedoOpacity`
- `Normal`
- `RMSE`

That is only true if those textures already exist before lighting.

### 6.2 Phase 1 Plan

Keep deferred decals working by running them **after** the compatibility material resolve pass and **before** deferred lighting.

That preserves current behavior and avoids forcing a decal rewrite into the first milestone.

### 6.3 Phase 2 Plan

Move decals to a clustered or per-pixel material modifier model closer to MJP's approach:

- build per-cluster decal lists
- project decals during material resolve or lighting
- blend decal material properties directly into the resolved surface state

This aligns better with a native bindless deferred architecture and eliminates the requirement for a materialized pre-lighting G-Buffer.

### 6.4 Recommendation

Treat the current deferred decal path as a compatibility bridge, not the long-term design.

---

## 7. MSAA, Stereo, and Specialized Paths

### 7.1 MSAA

Deferred texturing has the same MSAA complications as ordinary deferred shading, plus derivative reconstruction concerns.

Recommendation:

1. Ship non-MSAA first.
2. Keep the current deferred path as the MSAA fallback until the bindless path is validated.
3. Revisit MJP-style edge classification only after the non-MSAA path is solid.

### 7.2 Stereo / VR

XRENGINE has multiview and stereo-specific texture allocation paths. Bindless deferred texturing should not block those paths, but it also should not target them first.

Recommendation:

- first milestone: mono only
- second milestone: stereo compatibility mode
- third milestone: native bindless stereo if profiling justifies it

### 7.3 Specialized Materials

Some materials will not fit the standard deferred PBR contract cleanly.

Examples:

- parallax-heavy materials
- custom procedural shading functions
- special editor/debug materials
- materials with multiple UV sets driving core shading

Recommendation:

Keep an explicit opt-out path so those materials can stay on the existing deferred or forward pipelines until a proper abstraction exists.

---

## 8. Implementation Plan

### Phase 0: Preparation

- Add renderer capability flags for bindless deferred texturing per backend.
- Split logical material records from backend upload packing.
- Add fallback textures for every sampled slot.
- Add a renderer-owned bindless texture registration service.

### Phase 1: Vulkan Compatibility Path

- Add new geometry-only deferred attachments.
- Route opaque deferred materials to the new geometry pass.
- Populate Vulkan material records with real descriptor indices.
- Add a fullscreen or compute material resolve pass that outputs `AlbedoOpacity`, `Normal`, and `RMSE`.
- Keep current lighting and deferred decals unchanged.
- Gate behind a runtime setting.

This is the recommended first shipping milestone.

### Phase 2: OpenGL Compatibility Path

- Add bindless texture capability probing.
- Implement GL texture-sampler handle creation and residency tracking.
- Upload GL material records with real 64-bit handles.
- Reuse the same geometry-only pass and compatibility resolve logic.
- Fallback automatically when bindless extensions are unavailable.

### Phase 3: Native Vulkan Deferred Lighting

- Replace the compatibility resolve plus classic deferred lighting sequence with one bindless deferred lighting pass.
- Material fetch, normal reconstruction, and BRDF evaluation happen in one step.
- Keep compatibility mode available for debugging until parity is proven.

### Phase 4: Native Decals and Material Modifiers

- Replace G-Buffer-writing deferred decals with clustered decal lists or a similar material-layering system.
- Merge decal application into the deferred resolve/lighting domain.

### Phase 5: Advanced Features

- MSAA tile classification and edge shading
- stereo / multiview support
- visibility-buffer-style experiments
- material-specialized tile classification if warranted

---

## 9. Validation Plan

### 9.1 Functional Validation

- Verify pixel parity between current deferred and bindless compatibility mode on reference scenes.
- Validate normal mapping, tangent handedness, UV seams, and mip selection.
- Validate missing-texture fallback behavior.
- Validate decal ordering in compatibility mode.

### 9.2 Backend Validation

- Vulkan with descriptor indexing enabled
- Vulkan with descriptor indexing disabled to verify fallback
- OpenGL with bindless texture support
- OpenGL without bindless texture support to verify fallback

### 9.3 Performance Validation

Capture GPU timings for:

- geometry pass cost before and after
- material resolve cost
- total deferred frame time
- overdraw-heavy scenes
- material-dense scenes with divergent material IDs

The expected win is a cheaper geometry pass and reduced wasted texture sampling under overdraw. The total frame win is not guaranteed and must be measured.

### 9.4 Debugging Requirements

Add debug visualizations for:

- material ID
- UV set 0
- depth gradients
- UV gradients
- packed tangent frame decode
- resolved albedo / normal / RM outputs

Without those visualizations, derivative and basis bugs will be expensive to diagnose.

---

## 10. Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Derivative reconstruction artifacts | wrong mip selection, visible seams | ship stored UV gradients first; optimize later |
| Current decal path is tightly coupled to the classic G-Buffer | major migration friction | use compatibility resolve pass first |
| OpenGL bindless availability is vendor-specific | feature fragmentation | explicit extension gate + fallback |
| Specialized materials do not fit the standard deferred contract | correctness regressions | keep per-material opt-out |
| Extra attachments offset some of the geometry-pass savings | weak net performance win | measure before making it default |
| GPU-driven + bindless debugging becomes harder | slower bring-up | add strong debug views and validation logging |

---

## 11. Final Recommendation

XRENGINE should implement bindless deferred texturing, but it should do so as a **measured migration**, not a renderer rewrite.

The correct first target is:

1. Vulkan
2. compatibility resolve mode
3. non-MSAA mono cameras
4. opaque deferred materials only

OpenGL should follow with the same logical pipeline when bindless texture extensions are present. The current deferred pipeline should remain available as the fallback and as the reference path during validation.

The critical architectural decision is to separate:

- a shared engine-level material fetch contract
- backend-specific bindless encoding
- a temporary compatibility resolve pass

If that separation is done cleanly, the engine can get the main benefit of deferred texturing quickly while leaving space for a more native clustered material/decal pipeline later.
