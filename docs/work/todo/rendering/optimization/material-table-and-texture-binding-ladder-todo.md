# Material Table And Texture Binding Ladder TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Material Binding Policy](../../../../architecture/rendering/material-binding-policy.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md), [Vulkan Backend Parity Validation](../../../testing/rendering/vulkan-backend-parity-validation.md)

## Current State
The bounded Vulkan production rung exists. Code includes `EMaterialTextureBindingRung`, active rung statistics, Vulkan descriptor-indexed material references, material and texture dirty ranges, backend capability probing, GPU-safe texture lifetime paths, and cache keys that include layout and texture-reference mode. OpenGL texture-array policy, sparse or virtual texture integration, portable coarse-bucket fallback, preference overrides, editor diagnostics, and broader visual validation remain open.

## Open Code Items

### Rung model and capability probes
- [ ] Add or complete backend preference settings and environment overrides for texture binding rung selection. Runtime settings and resolver. Done when: users can request a rung and invalid values fail loud.
- [ ] Probe OpenGL texture array limits, bindless support, sparse texture support, residency behavior, and known vendor limitations. OpenGL renderer. Done when: the resolver has a reason for each OpenGL rung result.
- [ ] Probe Vulkan descriptor indexing and sparse residency capabilities. Vulkan renderer. Done when: the resolver can select or reject each Vulkan rung with a reason.
- [ ] Report active rung in editor diagnostics. Editor diagnostics. Done when: frame stats, profile JSON, and editor UI agree on rung and reason.

### Texture array rung
- [ ] Restrict texture arrays to compatible groups. Material and texture grouping code. Done when: grouping requires identical dimensions, format, mip count, sampler behavior, and color space.
- [ ] Add deterministic grouping by semantic and compatibility key. Grouping code. Done when: identical inputs produce stable groups.
- [ ] Add layer allocation and dirty-layer upload tracking. Texture array allocator. Done when: layer writes and uploads are counted.
- [ ] Reject incompatible wrap modes, UV transforms, mixed color spaces, and mixed compression requirements. Resolver and diagnostics. Done when: incompatible inputs visibly fall back.
- [ ] Integrate compatible array manifests from avatar or material consolidation. Import and material systems. Done when: declared compatible groups can select the array rung.
- [ ] Add counters for array count, layer count, layer uploads, and fallback reasons. Stats and profiler. Done when: captures show these counters.
- [ ] Add tests for compatible groups, incompatible formats, missing mips, and wrap-mode rejection. `XREngine.UnitTests/Rendering/`. Done when: each case has deterministic coverage.

### Bindless and descriptor-indexed rung
- [ ] Create and manage 64-bit OpenGL texture handles when `ARB_bindless_texture` is supported and allowed. OpenGL renderer. Done when: handles become resident and lifetime is tracked.
- [ ] Store OpenGL handles in a GPU buffer indexed by material row texture indices. Material table code. Done when: shaders consume `MaterialTextureHandleTable` for OpenGL bindless.
- [ ] Add a driver or vendor denylist or warning table when validation exposes broken bindless behavior. Capability code. Done when: denied drivers report a visible reason.
- [ ] Complete Vulkan descriptor-indexed equivalent through dynamic material binding paths. Vulkan renderer and material binding code. Done when: descriptor indices remain valid in generated material rows.
- [ ] Add counters for handles created, resident, retired, failed, and fallback events. Stats and profiler. Done when: captures include these counters.
- [ ] Add tests for handle indexing, lifetime, dirty updates, and unavailable-bindless fallback. `XREngine.UnitTests/Rendering/`. Done when: each behavior is covered.
- [ ] Add debug names to the global material texture descriptor pool and descriptor set layout. `VulkanRenderer.BindlessMaterialTextureTable.cs`. Done when: RenderDoc shows named pool, layout, and set objects.
- [ ] Add a deterministic Vulkan bindless material test fixture. `XREngine.UnitTests/Rendering/VulkanFullyBindlessMaterialTests.cs`, `GPUMaterialTable`. Done when: textured, missing-texture, and material-edit cases prove descriptor indices and dirty slots.
- [ ] Add profiler counters for material-table row upload bytes, bindless draw count, fallback draw count, and material-table shader variant cache misses. Vulkan renderer, `HybridRenderingManager`, stats. Done when: counters appear in profiler and profile JSON.
- [ ] Prove material descriptor indices stay valid across deferred passes. Material texture slot lifetime. Done when: a contract test or diagnostic proves no slot retires before a deferred pass completes.

### Sparse, virtual, and coarse fallback rungs
- [ ] Define the material-table to virtual-texture page-table boundary. Material and texturing code. Done when: material rows can reference virtual pages without another row refactor.
- [ ] Add material row fields for sparse or virtual texture references where pass layouts declare them. Material layout generation. Done when: declared layouts can carry page-table references.
- [ ] Report `Sparse` as active only when the API boundary is valid. Resolver and diagnostics. Done when: partial support does not claim full virtual texturing.
- [ ] Route page residency feedback through the texturing runtime. Texturing integration. Done when: rendering does not own ad hoc page feedback.
- [ ] Add fallback from sparse to bindless, texture arrays, or coarse buckets. Resolver. Done when: unsupported sparse support selects a visible lower rung.
- [ ] Add counters for sparse page references, feedback writes, fallback events, and page misses. Stats and profiler. Done when: captures include these counters.
- [ ] Define deterministic texture-set identity for fallback buckets. Material grouping. Done when: equal texture sets produce equal keys.
- [ ] Group draws by state class and texture-set identity. Scatter and submission code. Done when: buckets consume compact active work.
- [ ] Bind each bucket once and draw compatible active work. Backend submission code. Done when: fallback bucket cost scales with active buckets.
- [ ] Preserve pass semantics and transparent ordering. Submission code. Done when: transparent output remains correct.
- [ ] Add tests for deterministic grouping and transparent-order preservation. `XREngine.UnitTests/Rendering/`. Done when: both cases have coverage.

### Dirty updates and prewarm
- [ ] Update material rows by dirty ranges. `GPUMaterialTable`. Done when: editing one material updates only affected rows.
- [ ] Update texture handle tables separately from material rows. Material texture table code. Done when: texture changes do not rewrite unchanged material constants.
- [ ] Cache generated shader sources by source hash, pass layout hash, backend feature mask, and static property hash. Shader generation. Done when: warm starts reuse known variants.
- [ ] Include active texture rung in shader and program cache keys where it changes source or bindings. Shader cache. Done when: rung changes cannot reuse incompatible programs.
- [ ] Persist OpenGL program binaries and Vulkan or DX12 pipeline caches where supported. Backend pipeline caches. Done when: supported backends warm from persisted cache.
- [ ] Ensure material table rows are ready before measured render frames. Material preparation. Done when: warm frames do not generate or link known material-table variants.
- [ ] Add counters for row bytes uploaded, dirty row ranges, generated variants, cache hits, and cache misses. Stats and profiler. Done when: captures include these counters.

## Decisions Needed
- [ ] Choose whether OpenGL bindless needs a default denylist before v1. Owner: rendering lead.
- [ ] Choose the sparse-rung API boundary shared with virtual texturing. Owner: rendering and texturing leads.

## Out Of Scope
- Pass-declared row layout generation not needed by this ladder. See dynamic material binding design.
- Full virtual texturing runtime ownership.
