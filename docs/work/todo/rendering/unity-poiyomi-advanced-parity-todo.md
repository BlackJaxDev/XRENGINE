# Unity Poiyomi Advanced Parity TODO

Last Updated: 2026-10-15
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Texture Streaming](../../../architecture/rendering/texture-streaming.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Import work: [Unity Prefab Parity Import TODO](../assets/unity-prefab-parity-import-todo.md)

## Current State

Both prefab routes import the same 21 active Unity materials. Native opaque and masked Uber layouts preserve material rows and texture roles. Source dissolve, detail color, glitter, normals, matcaps, Mochie direct and probe specular, style-0 rim, Face clearcoat, and backface rules have source adapters. Authored directional SH and source local-light composition are implemented. Engine probe convolution, local attenuation, and per-pixel light assignment remain explicit capability boundaries. Small accessory triangles reconstruct without a world-area cutoff. Sorted alpha metadata, trusted generated-variant identity, and texture residency publication are implemented. Probe arrays and forward-light buffers use counted binding ownership. Native and forward source-mode Uber include engine probe diffuse, with one normalized irradiance calculation. Mesh reuse witnesses belong to their static generation. Vulkan planner publication retires older registry revisions after an exact complete replacement is ready. Destroyed capture outputs retire their cached plans and direct-compute descriptors after their completion tickets permit release. Pressured retirement queues receive a bounded scan budget. Uniform-array validity certificates remove repeated whole-array scans during reuse. Capture frame-data reservations still need complete owner retirement. Native Uber material plans can reuse unchanged scalar data after fresh texture-source and sampler checks, including prepared-result consumption. See the [resource ownership contract](../../../architecture/rendering/default-render-pipeline-notes.md#imported-and-forward-buffer-ownership). The validation document owns visual, deformation, and frame-time acceptance.

## Open Code Items

### GPU Record Contract

- [ ] Update `AdvancedGlobalResourceContractTests.GlobalRecords_HaveStableStd430CompatiblePacking` after test-edit clearance. Done when: its environment stride assertion matches the implemented 240-byte record and confirms the appended SH field offsets. The existing assertion still expects 128 bytes. Feature validation does not authorize test source edits.

### Scene Publication

- [ ] Preserve GPU-authored LOD transition state during scene capacity growth in `GPUScene.CommandBuffers.cs` and the render-owner publication boundary. Done when: `EnsureLodTransitionBufferCapacity` does not map a backend buffer on the collection thread, and structural growth preserves transition rows without committing a stale CPU mirror. Keep missing ownership explicit.

### Transparent Velocity

- [ ] Define the frame-matrix contract for `AdvancedColoredAlphaTemporalVariantFactory` when motion blur requires velocity but temporal AA/history is inactive. Done when: queued transparent velocity work retains valid current and previous view matrices for its exact output without requiring an inactive temporal accumulator. Keep motion-blur velocity enabled; the unused-replay gate does not resolve this separate case.

### Capture Frame Data Ownership

- [ ] Bound capture ownership in `VulkanMeshFrameDataFamilyKey`, its comparer and reservation manifest, `VulkanMappedFrameArena`, frequency reservations, uniform publication, and transient IBL teardown. Done when: a stable capture `OutputId` preserves each live component/face family; exact owner retirement removes unused family metadata and frequency reservations; shared ranges remain valid for surviving owners; and an offset can be reused only after authoring references, cached commands, and submitted readers release it. Reused ranges must carry an allocation identity checked during recording and refresh. Keep active family offsets stable. Do not reclaim by fixed delay or force-release ownership pins.

This item covers the remaining long-run reservation growth. Stable face identity alone does not retire transient IBL frequency owners. Keep both owner paths in scope. The [capture follow-up checks](../../testing/rendering/jax2031-advanced-parity-validation.md#continuous-capture-follow-up-checks) own soak and teardown acceptance.

### Continuous Probe Capture Performance

- [ ] Reduce repeated capture preparation and publication work in `LightProbeComponent`, its IBL and convolution target partials, `VulkanResourcePlannerSessionService`, and Vulkan frame-data publication. Use stage profiles to select the changes. Done when: the affected paths reuse compatible unchanged resources or prepared work, invalidate reuse when capture inputs or resource generations change, and avoid the measured repeated work without adding per-frame allocations. Preserve continuous updates, capture resolution, Advanced pipeline shading, and GPU execution. Do not treat reduced capture quality or a lower update rate as this fix.

The [Release capture result](../../testing/rendering/jax2031-advanced-parity-validation.md#release-continuous-capture-result) records the current cost and measurement limits. The [capture follow-up checks](../../testing/rendering/jax2031-advanced-parity-validation.md#continuous-capture-follow-up-checks) own performance and image acceptance for both prefab import routes. The main remaining cost has not yet been isolated.

## Decisions Needed

- Choose the first active outfit, face weights, and enabled feature set from the Unity reference. Owner: avatar author.
- Choose the target desktop or VR frame budget and output resolution. Owner: application owner.

## Related Work And Out Of Scope

- General descriptor and material-cache work remains in [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md).
- General avatar simplification and LOD tooling remains in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md). The root failure does not establish a need for a new optimizer.
- Full Pro-exclusive feature support, outline support, and Unity plugin/menu execution are outside the first still-image parity slice unless the selected outfit requires them.
- Shader module publication now holds the Vulkan dependency gate through stage metadata and native module creation. The linked investigation records the startup race. The validation document tracks repeated startup and Release acceptance.
