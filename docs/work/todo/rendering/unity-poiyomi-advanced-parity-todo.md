# Unity Poiyomi Advanced Parity TODO

Last Updated: 2026-10-18
Status: Active
Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Texture Streaming](../../../architecture/rendering/texture-streaming.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md), [Unity Avatar Package Validation](../../testing/assets/unity-avatar-package-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Import work: [Unity Prefab Parity Import TODO](../assets/unity-prefab-parity-import-todo.md)

## Current State

Both prefab routes preserve saved outfit and material bindings. Probe outputs
publish their reflection sampling convention. Deformation reuse uses exact mesh
inputs. Membership changes retain unchanged deformation rows. Source-change
diagnostics report the cause of invalidation. Window restoration compares both
applied and committed viewport extents. Compatible legacy texture replacements
preserve binding layout.
Native Uber and authored Uber implement source multilayer border-map and flat-mask
controls. The flat direction is captured per draw. Native Uber also implements
vertex-mode UDIM triangle coverage for supported authored late and shadow paths.
The Vulkan path binds source corner masks per triangle and admits the path only
when the backend supports it. Shadow-atlas content signatures now include caster
geometry and material binding, shader, and Uber revisions. The
[validation document](../../testing/rendering/jax2031-advanced-parity-validation.md)
owns visual, runtime, long-run, and performance results.

## Open Code Items

### GPU Record Contract

- [ ] Update `AdvancedGlobalResourceContractTests.GlobalRecords_HaveStableStd430CompatiblePacking` after test-edit clearance. Done when: its environment stride assertion matches the implemented 240-byte record and confirms the appended SH field offsets. The existing assertion still expects 128 bytes. Feature validation does not authorize test source edits.

### Scene Publication

- [ ] Preserve GPU-authored LOD transition state during scene capacity growth in `GPUScene.CommandBuffers.cs` and the render-owner publication boundary. Done when: `EnsureLodTransitionBufferCapacity` does not map a backend buffer on the collection thread, and structural growth preserves transition rows without committing a stale CPU mirror. Keep missing ownership explicit.

### Required GPU Producers

- [ ] Preserve the relative source order of explicit frame operations and deferred mesh requests in `VulkanFrameLoop.RequiredProducerBatch.cs`. Done when: a callback-issued barrier between two dependent mesh draws remains between them after materialization. The reflection filter currently uses ordered primary draws with automatic sampled-image transitions.

### Transparent Velocity

- [ ] Define the frame-matrix contract for `AdvancedColoredAlphaTemporalVariantFactory` when motion blur requires velocity but temporal AA/history is inactive. Done when: queued transparent velocity work retains valid current and previous view matrices for its exact output without requiring an inactive temporal accumulator. Keep motion-blur velocity enabled; the unused-replay gate does not resolve this separate case.

## Decisions Needed

- Choose the target desktop or VR frame budget and output resolution. Owner: application owner.

## Related Work And Out Of Scope

- General descriptor and material-cache work remains in [Material Table And Texture Binding Ladder TODO](optimization/material-table-and-texture-binding-ladder-todo.md).
- General avatar simplification and LOD tooling remains in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md). The root failure does not establish a need for a new optimizer.
- Full Pro-exclusive feature support and Unity plugin/menu execution remain outside the selected still-image work. Authored masked outlines now have an explicit Advanced route for the supported mono, single-sample case.
- Shader module publication now holds the Vulkan dependency gate through stage metadata and native module creation. The linked investigation records the startup race. The validation document tracks repeated startup and Release acceptance.
