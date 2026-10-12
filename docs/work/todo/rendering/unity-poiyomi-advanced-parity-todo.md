# Unity Poiyomi Advanced Parity TODO

Last Updated: 2026-10-12
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Import work: [Unity Prefab Parity Import TODO](../assets/unity-prefab-parity-import-todo.md)

## Current State

Both prefab routes import the same 21 active Unity materials. Native opaque and masked Uber layouts preserve material rows and texture roles. Source Basic dissolve, detail color, circle glitter, detail-normal composition, and matcap mip selection and blend stages now have source adapters. Small visible accessory triangles no longer use a world-area cutoff during reconstruction. Source lighting and backface tangent-basis rules still differ. The horn material has explicit sorted alpha metadata. The validation document owns visual, deformation, and frame-time acceptance. Texture promotion can still expose logical metadata before Vulkan residency is ready.

## Open Code Items

### Source Material Semantics

- [ ] Preserve `_FlipBackfaceNormals` in `SerializedMaterialImporter.SourceToonSurfaceFeatures`, `NativeUberMaterialSchema`, and both Uber normal evaluators. Done when: the saved source flag controls backface N/T/B together instead of an unconditional normal-only flip; engine-authored materials retain their current contract.

### Scene Publication

- [ ] Preserve GPU-authored LOD transition state during scene capacity growth in `GPUScene.CommandBuffers.cs` and the render-owner publication boundary. Done when: `EnsureLodTransitionBufferCapacity` does not map a backend buffer on the collection thread, and structural growth preserves transition rows without committing a stale CPU mirror. Keep missing ownership explicit.
- [ ] Commit imported texture metadata and Vulkan residency together in `VkImageBackedTexture.ImportedUpload.cs` and the descriptor publication boundary. Done when: pending uploads do not expose new dimensions, mip data, or canonical source generation before native resources are ready; failure retains the old published state. Keep canonical source validation enabled.

## Decisions Needed

- Choose the first active outfit, face weights, and enabled feature set from the Unity reference. Owner: avatar author.
- Choose the target desktop or VR frame budget and output resolution. Owner: application owner.

## Related Work And Out Of Scope

- General descriptor and material-cache work remains in [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md).
- General avatar simplification and LOD tooling remains in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md). The root failure does not establish a need for a new optimizer.
- Full Pro-exclusive feature support, outline support, and Unity plugin/menu execution are outside the first still-image parity slice unless the selected outfit requires them.
- Shader readiness debugging in Release and intermittent Debug startup remains in the linked investigation. Do not select a code fix before its cause is established.
