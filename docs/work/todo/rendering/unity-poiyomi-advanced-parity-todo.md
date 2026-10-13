# Unity Poiyomi Advanced Parity TODO

Last Updated: 2026-10-13
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Texture Streaming](../../../architecture/rendering/texture-streaming.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Import work: [Unity Prefab Parity Import TODO](../assets/unity-prefab-parity-import-todo.md)

## Current State

Both prefab routes import the same 21 active Unity materials. Native opaque and masked Uber layouts preserve material rows and texture roles. Source dissolve, detail color, glitter, normals, matcaps, Mochie direct and probe specular, style-0 rim, Face clearcoat, and backface rules have source adapters. Authored directional SH and source local-light composition are implemented. Engine probe convolution, local attenuation, and per-pixel light assignment remain explicit capability boundaries. Small accessory triangles reconstruct without a world-area cutoff. Sorted alpha metadata, trusted generated-variant identity, and texture residency publication are implemented. The validation document owns visual, deformation, and frame-time acceptance.

## Open Code Items

### GPU Record Contract

- [ ] Update `AdvancedGlobalResourceContractTests.GlobalRecords_HaveStableStd430CompatiblePacking` after test-edit clearance. Done when: its environment stride assertion matches the implemented 240-byte record and confirms the appended SH field offsets. The existing assertion still expects 128 bytes. Feature validation does not authorize test source edits.

### Scene Publication

- [ ] Preserve GPU-authored LOD transition state during scene capacity growth in `GPUScene.CommandBuffers.cs` and the render-owner publication boundary. Done when: `EnsureLodTransitionBufferCapacity` does not map a backend buffer on the collection thread, and structural growth preserves transition rows without committing a stale CPU mirror. Keep missing ownership explicit.
- [ ] Retire replaced probe topology buffers after published bindings release them in `ForwardLightProbeInstanceResources.ClearResources` and `AdvancedRenderPipeline.ConsumePendingProbeTopology`. Done when: staged unbind and replacement do not dispose a buffer that a published frame can still resolve. The investigation records the live `XRDataBuffer` disposal failures.
- [ ] Identify and repair the buffer retirement conflict on first local-light activation in `Lights3DCollection.ForwardLighting` and the Vulkan descriptor lifetime path. Add binding and resource-owner context to `VulkanDescriptorLifetimeAuthority` rejection diagnostics. Done when: replacement buffers retain the prior generation until captured draw bindings release it. The first-Spot failure suggests the forward spot or shadow-metadata buffer; the exact owner is not yet confirmed.

## Decisions Needed

- Choose the first active outfit, face weights, and enabled feature set from the Unity reference. Owner: avatar author.
- Choose the target desktop or VR frame budget and output resolution. Owner: application owner.

## Related Work And Out Of Scope

- General descriptor and material-cache work remains in [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md).
- General avatar simplification and LOD tooling remains in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md). The root failure does not establish a need for a new optimizer.
- Full Pro-exclusive feature support, outline support, and Unity plugin/menu execution are outside the first still-image parity slice unless the selected outfit requires them.
- Shader module publication now holds the Vulkan dependency gate through stage metadata and native module creation. The linked investigation records the startup race. The validation document tracks repeated startup and Release acceptance.
