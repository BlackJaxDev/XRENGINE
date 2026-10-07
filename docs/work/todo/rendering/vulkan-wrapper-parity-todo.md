# Vulkan Wrapper Parity TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Vulkan Renderer: Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract), [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md)
Validation: [Vulkan Backend Parity Validation](../../testing/rendering/vulkan-backend-parity-validation.md)

## Current State

The Vulkan wrappers `VkMeshRenderer`, `VkMaterial`, `VkShader`, `VkImageBackedTexture` and its `VkTexture*` types, and `VkDataBuffer` implement the engine-facing behavior of their OpenGL counterparts. Source-contract tests in `XRMeshAndMeshRendererVulkanParityContractTests`, `XRMaterialAndShaderVulkanParityContractTests`, `XRTextureVulkanParityContractTests`, and `VkDataBufferParityContractTests` cover registration, event symmetry, readiness separation, material and shader artifacts, and buffer lifetime. Vulkan checks `FormatFeatureFlags` before mipmap generation (`VkImageBackedTexture.Mipmaps.cs`) and blit (`VulkanRenderer.BlitRecording.cs`). The OpenGL backend has no render-graph pass-metadata validation. The remaining items close the gaps below.

## Open Code Items

### Mesh Renderer

- [ ] Add a parity test for patch topology. `VkMeshRenderer.Drawing.cs` (`EPrimitiveType.Patches` to `PrimitiveTopology.PatchList`), `GLMeshRenderer.UsesPatchTopology`, `GLMeshRenderer.PatchVertexCount`, `XRMeshAndMeshRendererVulkanParityContractTests`. Done when: a unit test proves that both backends select patch topology, use the same control-point count, and apply the same topology fallback.

### Material And Shader

- [ ] Audit descriptor-array shader variants outside the generated material-table path. Shaders under `Build/CommonAssets/Shaders/` and generated variants that do not come from `MaterialBindingGlslGenerator`. Done when: every descriptor-array index that comes from per-draw, per-material, or GPU-written data uses `nonuniformEXT` or a validated equivalent variant, and a source-contract test fails on a new unqualified index.
- [ ] Validate std140, std430, and scalar member offsets from reflection before Vulkan serializes material parameters. `VkMaterial` loose-uniform path (`GetShaderVarSize`, `GetShaderVarArrayStride`, `TryWriteShaderVar`) and `AutoUniformBlockInfo`. Done when: serialization compares each member offset and size with the reflected block, reports a rate-limited mismatch diagnostic, and a unit test covers vec3, array, and mat3 cases.
- [ ] Add a unit test that OpenGL and Vulkan shader source identity inputs agree for a representative resolved shader. `ResolvedShaderSource.SourceIdentity`, `VulkanShaderCompiler.BuildArtifactIdentity`, `GLRenderProgram` cache identity. Done when: the test fails if one backend adds or drops a source-shaping input that the other backend uses.

### Textures

- [ ] Check `FormatFeatureFlags` before Vulkan selects the upload, storage-image, color-attachment, depth/stencil-attachment, linear-filter sampler, linear-tiling, and texel-buffer paths. `VkImageBackedTexture` and `VkTexture*` types. Done when: each path queries format features before use and reports texture name, format, and the missing feature when a path is rejected.
- [ ] Add a unit test for the texture-view compatibility matrix. `VkTextureView`, `GLTextureView`, `XRTextureVulkanParityContractTests`. Done when: the test proves that both backends accept and reject the same view format, type, and aspect combinations.

### OpenGL Backfill

- [ ] Validate render-graph pass metadata in the OpenGL path while the OpenGL executor stays sequential. `XREngine.Runtime.Rendering.OpenGL` render path and the shared render-graph metadata. Done when: OpenGL reports the same missing-resource, attachment-intent, and pass-dependency errors that Vulkan barrier planning reports, including FBO and post-process texture attachment intent, and a unit test covers one invalid pass.

## Decisions Needed

None.

## Out Of Scope

- Hardware and visual comparison between backends. The validation doc owns these checks.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/COMPLETED/vulkan-wrapper-parity.md`

- [ ] Validate `VkFormatFeatureFlags` before choosing upload, blit, mipmap,
  storage-image, attachment, depth/stencil, filtering, linear-tiling,
  sampled-image, and texel-buffer paths.
