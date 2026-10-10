# Unity Poiyomi Advanced Parity TODO

Last Updated: 2026-10-08
Status: Active. The source avatar cannot finish import.
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Import work: [Unity Prefab Parity Import TODO](../assets/unity-prefab-parity-import-todo.md)

## Current State

`SerializedSceneImporter` now completes the exact prefab through Assimp. Native model-base publication remains guarded until coordinate and cluster-bind conversion is complete. The Poiyomi importer preserves many common Uber modules, but Advanced publishes standard material rows and executes standard PBR shading. Imported late transparency lacks explicit Advanced eligibility metadata. The avatar is visible in Vulkan, with deformed and detached geometry and incorrect shading. Visual parity and frame-time acceptance remain open.

## Open Code Items

### Native Uber Material Contract

- [ ] Add an explicit native Uber layout and semantic encoder in `AdvancedGpuScenePublisher.MaterialTransitions.cs`, `AdvancedGpuMaterialPublisher.cs`, and `MaterialBindingSourceEncoder.cs`. Done when: active Uber constants and texture semantics reach the canonical rows, and an unsupported Uber program produces an explicit rejection instead of standard PBR admission. Key variants by compatible feature topology and layout. Keep ordinary material values in GPU rows.
- [ ] Add executable native Uber shading for the active common Pro 9.3.66 features through `AdvancedRenderPipeline.NativeShading.cs`, the Vulkan native shading dispatch, and `Build/CommonAssets/Shaders/Advanced/Shading/`. Done when: native dispatch selects the compatible Uber evaluator and preserves toon lighting, normal maps, emission, matcaps, rim lighting, and the enabled clothing effects. Use reconstructed sampling gradients. Expose each unsupported active feature in diagnostics.
- [ ] Share the active Uber coverage rules with `VisibilityRasterMasked.frag` and `DirectionalShadowRasterMasked.frag`. Done when: visibility and shadow coverage use the same texture semantics, UV transforms, cutoff, and enabled alpha-mask or dissolve behavior as the material. Route otherwise opaque materials with active discard effects through the required coverage evaluator before depth writes. Read current alpha-to-coverage controls and effective shader state.

### Late Transparency

- [ ] Set explicit `AdvancedLatePassMetadata` in `SerializedMaterialImporter.cs` for supported transparent source states. Done when: blend, depth-write, sorting, scene-color dependency, and temporal-reactivity requirements select a permitted late lane. Claimed temporal participation also has velocity and reactive shader variants with matching coverage and compatible geometry; otherwise, diagnostics report the temporal limitation. Opaque and masked materials continue through native visibility and shading. Unsupported mixed states fail with a reason.

## Decisions Needed

- Choose the first active outfit, face weights, and enabled feature set from the Unity reference. Owner: avatar author.
- Choose the target desktop or VR frame budget and output resolution. Owner: application owner.

## Related Work And Out Of Scope

- General descriptor and material-cache work remains in [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md).
- General avatar simplification and LOD tooling remains in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md). The root failure does not establish a need for a new optimizer.
- Full Pro-exclusive feature support, outline support, and Unity plugin/menu execution are outside the first still-image parity slice unless the selected outfit requires them.
- Release shader readiness debugging remains in the linked investigation. Do not select a code fix before its cause is established.
