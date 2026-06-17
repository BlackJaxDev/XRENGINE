# Vulkan Bindless And Deferred Texturing Audit - 2026-06-17

## Scope

This audit answers two questions:

1. Is the current Vulkan implementation truly bindless?
2. Should MJP-style deferred texturing become another selectable mode in the default render pipeline?

Reference material:

- [TheRealMJP/DeferredTexturing](https://github.com/TheRealMJP/DeferredTexturing)
- [MJP: Bindless Texturing For Deferred Rendering And Decals](https://therealmjp.github.io/posts/bindless-texturing-for-deferred-rendering-and-decals/)
- [Bindless Deferred Texturing Plan](../design/texturing/bindless-deferred-texturing-plan.md)
- [Dynamic Indirect Material Bindings](../design/rendering/dynamic-indirect-material-bindings.md)
- [Material Binding Policy](../../architecture/rendering/material-binding-policy.md)

## Executive Summary

The Vulkan backend is descriptor-indexing-aware, but it is not a true end-to-end bindless material renderer yet.

The code already has Vulkan feature probing, descriptor-indexing layout flags, variable descriptor count allocation for the reserved bindless binding, and a `VulkanBindlessMaterialTable.glsl` helper that samples `XR_BindlessMaterialTextures[nonuniformEXT(index)]`. That is useful groundwork.

The missing piece is the actual production binding model: there is no renderer-owned global Vulkan texture descriptor table, no descriptor slot allocator feeding material rows, and no active Vulkan material-table shader path that consumes descriptor indices. The active bindless material-table path is OpenGL-only through `GL_ARB_bindless_texture` handles. On Vulkan, `BindlessMaterialTable` currently falls back to the non-bindless material-table shader path or builds material rows with zero texture indices.

Deferred texturing is worth integrating, but it should be an explicit optional render mode after Vulkan bindless is made real. It is not a small replacement for the current light combine pass. It changes the deferred GBuffer contract from materialized surface data to geometric interpolation data plus material IDs, then moves material texture sampling into a later full-screen or compute resolve.

## Current Vulkan Bindless State

### What Exists

Vulkan capability plumbing exists:

- `LogicalDevice` queries runtime descriptor arrays, partially-bound descriptors, and update-after-bind support.
- `VulkanFeatureProfile.EnableDescriptorIndexing` gates descriptor indexing through the runtime profile.
- `VulkanDescriptorLayoutCache` sets `PartiallyBoundBit`, `UpdateAfterBindBit`, and `VariableDescriptorCountBit` for the reserved bindless texture array binding.
- `VulkanBindlessMaterialDescriptors` reserves `set = 2`, `binding = 31`, name `XR_BindlessMaterialTextures`, and a maximum count of 4096 descriptors.
- `VkMaterial` can allocate descriptor sets with `DescriptorSetVariableDescriptorCountAllocateInfo`.
- `Build/CommonAssets/Shaders/Common/VulkanBindlessMaterialTable.glsl` declares `layout(set = 2, binding = 31) uniform sampler2D XR_BindlessMaterialTextures[]` and samples with `nonuniformEXT`.

The engine also has a logical material-table model:

- `GPUMaterialTable` packs material rows with texture indirection fields and constants.
- The indirect renderer can forward a material ID into generated material-table shaders.
- Existing tests assert several static source contracts around descriptor indexing and material-table packing.

### What Is Not Actually Bindless Yet

The descriptor array is not a global texture table. In `VkMaterial`, bindless descriptor writes resolve `arrayIndex` through `MaterialTextureBindingResolver.Resolve(... bindlessMaterialArray: true)`, which maps directly into the current material's `Textures[arrayIndex]`. That is per-material descriptor population, not a renderer-wide descriptor index space shared by material rows.

The material table does not populate Vulkan descriptor indices. `PrepareMaterialTableAndValidateResidency` only builds real texture handle entries when the active renderer is `OpenGLRenderer` and supports bindless texture handles. On Vulkan, `BuildMaterialEntry` validates residency but returns zero handles/indices, so `AlbedoHandleIndex`, `NormalHandleIndex`, and `RMHandleIndex` stay zero.

The active bindless material-table shader generation is OpenGL-specific. `HybridRenderingManager.CreateMaterialTableFragmentShader(bindless: true, ...)` emits `GL_ARB_gpu_shader_int64` and `GL_ARB_bindless_texture`, then calls `SampleBindlessTexture(...)` from the OpenGL handle-table path. The Vulkan `nonuniformEXT` helper exists, but search only finds the helper and static unit-test coverage, not runtime shader generation or pipeline use.

The zero-readback bindless route is explicitly OpenGL-gated. `RenderZeroReadbackMaterialTableBuckets(... bindless: true)` computes `useBindless = bindless && SupportsOpenGLBindlessMaterialTable()`. If Vulkan requests `BindlessMaterialTable`, the path logs that bindless is unsupported and falls back to the material-table shader.

Vulkan GPU render dispatch is still profile-disabled. `VulkanFeatureProfile.ProfileAllowsGpuRenderDispatch` returns `false` because indirect draw submission is not yet integrated with Vulkan render pass ownership. That makes a production Vulkan bindless material path harder to validate as part of the intended GPU-driven renderer.

## Fixes Needed For True Vulkan Bindless

1. Add a renderer-owned Vulkan material texture table.

   Create a Vulkan service that owns the global sampled-image descriptor array for material textures. It should allocate stable descriptor slots, reserve slot 0 as the fallback/null texture, update dirty descriptors only, retire slots safely after frames in flight, and expose stats for capacity, used slots, dirty writes, and fallback hits.

2. Store Vulkan descriptor indices in material rows.

   `GPUMaterialTable` can keep the current logical fields, but Vulkan needs those fields to hold descriptor table indices rather than OpenGL handle-table indices. OpenGL handle entries and Vulkan descriptor slots should be backend-specific implementations of one material texture reference contract, not a shared `ulong` handle path.

3. Stop treating bindless arrays as per-material descriptor sets.

   The current `VkMaterial` descriptor population loops up to 4096 and maps each index to `material.Textures[index]`. A true bindless path binds one global descriptor set/table for the frame or pass and lets material rows index into it. Per-material descriptor sets should remain available for traditional shaders, but they should not be the bindless table implementation.

4. Generate Vulkan bindless shaders from the material-layout system.

   The material-table generator needs a Vulkan backend that emits the descriptor-indexed sampling path:

   - include or generate the `XR_BindlessMaterialTextures[]` declaration
   - use `nonuniformEXT(material.AlbedoHandleIndex)` style access
   - avoid OpenGL bindless extensions and `uint64_t` sampler handles
   - keep the same material row layout validation as the non-bindless path

5. Define backend capability tiers and visible failure modes.

   The engine should distinguish:

   - descriptor indexing available
   - global bindless material table ready
   - bindless material-table draw path ready
   - deferred texturing ready

   If a user explicitly requests Vulkan bindless and the device or engine path cannot honor it, the renderer should log a targeted diagnostic and disable that path visibly rather than silently behaving as if bindless succeeded.

6. Integrate texture lifetime and residency.

   Descriptor slots must track texture upload/readiness, default descriptors, streaming replacement, deletion, and resource retirement. If a texture is missing or evicted, its material row should point to a known fallback descriptor, and diagnostics should count that as a fallback sample source.

7. Add runtime validation, not only static tests.

   Current tests check source contracts. Add hardware/runtime checks that render several materials sharing one Vulkan bindless shader, verify descriptor indices differ, verify missing textures sample fallback descriptors, and exercise descriptor update-after-bind across frames.

8. Unblock Vulkan GPU-driven draw integration or scope bindless validation separately.

   If Vulkan GPU render dispatch remains disabled, the first Vulkan bindless smoke test can be a CPU-submitted material-table draw. Production sign-off should still include the intended GPU-driven zero-readback path once Vulkan render-pass ownership is fixed.

## Current Deferred Pipeline State

The default deferred path is a materialized GBuffer pipeline.

`AppendDeferredGBufferPass` binds `DeferredGBufferFBOName` or `MsaaGBufferFBOName`, renders `EDefaultRenderPass.OpaqueDeferred`, then renders `EDefaultRenderPass.DeferredDecals` into the same targets. It then resolves MSAA GBuffer data when needed and captures diagnostic views for `AlbedoOpacity`, `Normal`, and `RMSE`.

`CreateDeferredGBufferFBO` attaches:

| Attachment | Current Contents |
| --- | --- |
| `AlbedoOpacity` | materialized base color and opacity |
| `Normal` | packed normal |
| `RMSE` | roughness/metallic/specular/emission-style packed material values |
| `TransformId` | transform/object ID |
| `DepthStencil` | depth and stencil |

`AppendLightingPass` later runs `VPRC_LightCombinePass` with `AlbedoOpacity`, `Normal`, `RMSE`, and `DepthView`. That means the light combine pass assumes material texture sampling already happened during rasterization.

Forward+ support already exists as clustered light culling layered into the command chain, but the renderer does not currently expose a clean user-facing render-path matrix like `Forward`, `Forward+`, `Deferred`, `Deferred+`, or `DeferredTexturing`.

## Deferred Texturing Fit

MJP's DeferredTexturing sample demonstrates a different contract:

- geometry pass writes surface interpolation data, not materialized texture results
- the GBuffer includes enough data to reconstruct texture sampling later, such as UVs, tangent frame/basis, material ID, and gradients
- the deferred pass maps material ID to descriptor indices and samples only visible pixels
- decals and clustered lighting are designed around the same bindless/clustered material access model

That maps well to XRENGINE's long-term direction because the engine already has:

- material IDs in GPU draw metadata
- a material-table concept
- Vulkan descriptor-indexing groundwork
- Forward+ light culling infrastructure
- existing docs recommending compatibility-first bindless deferred texturing

The main mismatch is that current deferred decals modify materialized `AlbedoOpacity`, `Normal`, and `RMSE`. In a native deferred-texturing renderer, decals either need to run after a material resolve, or become clustered/deferred material modifiers that are consumed during the bindless shading pass.

## Recommendation

Yes, add deferred texturing as a selectable default-pipeline mode, but do it after true Vulkan bindless exists and name the modes precisely.

Recommended first public/internal enum shape:

| Mode | Meaning |
| --- | --- |
| `Forward` | current forward path without clustered light-list requirement |
| `ForwardPlus` | forward shading with clustered/Forward+ light culling |
| `Deferred` | current materialized GBuffer path |
| `DeferredTexturing` | texture-free geometry pass plus bindless material resolve into the classic GBuffer |
| `DeferredTexturingPlus` | native clustered deferred texturing with clustered lights/decals/material modifiers |

Avoid using `DeferredPlus` as the only name until the engine defines what `+` means for deferred. In the existing renderer, `Forward+` means clustered light assignment. In the MJP reference, the important new feature is deferred texturing, and clustered decals/lights are related but separable. The name should make those axes explicit.

## Integration Plan

### Phase 0: Vulkan Bindless Foundation

- Implement the Vulkan global material texture descriptor table.
- Populate material rows with descriptor indices.
- Generate Vulkan descriptor-indexed material-table shaders.
- Add hardware smoke tests and RenderDoc/MCP validation scenes.

### Phase 1: Compatibility Deferred Texturing

- Add a render setting for `DeferredTexturing`.
- Split the current GBuffer contract into:
  - classic materialized GBuffer
  - geometry-only deferred-texturing GBuffer
- Add geometry-only opaque deferred shader variants that output basis/normal frame, UV0, material ID, transform ID, and gradients.
- Add `VPRC_BindlessMaterialResolve` as a fullscreen or compute pass.
- Have the resolve write the existing `AlbedoOpacity`, `Normal`, and `RMSE` outputs.
- Keep existing deferred decals and lighting after the resolve.

This gives the engine the core overdraw/material-fetch benefit while minimizing downstream pipeline churn.

### Phase 2: Native Deferred Texturing

- Move lighting to sample material textures directly from descriptor indices.
- Convert deferred decals into clustered decal/material modifiers.
- Avoid materializing `AlbedoOpacity`, `Normal`, and `RMSE` unless a debug view or downstream pass requires them.
- Decide how AO, SSR, GI, velocity, and editor picking read the new geometry-only buffers.

### Phase 3: MSAA, Stereo, And VR

- Treat MSAA and stereo as follow-up validation tracks.
- MJP-style MSAA deferred texturing needs edge/tile classification and per-sample shading decisions.
- VR needs multiview-compatible attachments and careful bandwidth measurement before enabling by default.

## Open Risks

- GBuffer bandwidth can grow if basis, UVs, material IDs, and gradients are stored naively.
- Texture sampling moves later, so cache behavior and wave divergence need measurement on real scenes.
- Normal mapping quality depends on the stored tangent-frame representation and derivative strategy.
- Decals are the largest architectural mismatch with the current materialized deferred path.
- Vulkan descriptor limits vary by hardware; the table needs capacity diagnostics and fallback behavior.
- The existing material-table layout is opaque-deferred-specific and must become layout-driven before this scales to Forward+ and custom material families.

## Bottom Line

Vulkan bindless is currently a scaffold, not a shipped implementation. The engine should not consider Vulkan `BindlessMaterialTable` complete until material rows contain real descriptor indices into a global descriptor table and shaders sample through `nonuniformEXT` in an active Vulkan render path.

Deferred texturing is a good fit for XRENGINE, but it should enter the default render pipeline as an explicit optional mode. Build the Vulkan bindless substrate first, then land a compatibility resolve that reconstructs the current GBuffer, then graduate to a native clustered deferred-texturing path once decals and lighting are ready to consume bindless material data directly.
