# Vulkan CPU Octree + Screen UI DAG TODO

Goal: get 3D scene rendering and 2D screen-space UI rendering fully working on Vulkan through the CPU octree render path, with 1-by-1 render calls driven by an optimized DAG compiled from the camera render pipeline.

Last updated: 2026-02-10

## P0 - Blocking Work

- [x] Implement Vulkan compute dispatch in `XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs` (`DispatchCompute` currently throws `NotImplementedException`).
- [x] Add Vulkan `XRRenderProgram` event wiring in `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs` for:
  - [x] uniform sets
  - [x] samplers
  - [x] image binds
  - [x] SSBO binds
  - [x] compute dispatch callbacks
- [x] Implement Vulkan subscriber path for `XRDataBuffer.BindTo(...)` (`XRENGINE/Rendering/API/Rendering/Objects/Buffers/XRDataBuffer.cs`) so Forward+/compute SSBO binds work.
- [x] Complete Vulkan material/render-state parity in `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs` and `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs`:
  - [x] blend enable/factors/equations
  - [x] cull mode / winding
  - [x] stencil test state
  - [x] depth/stencil material-driven PSO variants
- [x] Expand Vulkan engine-uniform coverage in `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs` to include missing `EEngineUniform` values (notably UI and prev-frame variants).
- [x] Implement/route `SetEngineUniforms` and `SetMaterialUniforms` in Vulkan (`XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs`) to avoid silent no-op behavior in shared paths.
- [x] Fix UI transparency correctness on Vulkan (UI materials require blending, but pipeline currently forces `BlendEnable = false`).
- [x] Decide and complete Vulkan ImGui path:
  - [ ] either fully implement `RenderImGui(...)`
  - [x] or remove/contain dead-path assumptions and dependencies on ImGui output

## P0 - DAG and Pass Metadata Correctness

- [x] Make render-pass metadata traversal recurse into branch containers (`VPRC_IfElse`, `VPRC_Switch`) during metadata generation (`ViewportRenderCommandContainer.BuildRenderPassMetadata`).
- [x] Add `DescribeRenderPass` coverage for feature commands that currently render/dispatch without metadata (Forward+, LightCombine, Bloom, ReSTIR, LightVolumes, SpatialHash AO, Radiance Cascades, Surfel GI, Voxel Cone Tracing, Temporal Accumulation, etc.).
- [x] Ensure all emitted draw/blit/compute ops resolve to valid pass indices (no `int.MinValue` fallthrough).
- [x] Replace barrier planning by numeric pass sort with dependency-aware/topological planning using `ExplicitDependencies` (`VulkanBarrierPlanner`).
- [x] Capture pipeline/resource context per frame op and plan barriers from that captured context, instead of querying only the current active pipeline at submit time.
- [x] Add pipeline/viewport identity to scheduling so pass-index collisions do not alias between camera and UI pipelines.

## P0 - Camera Pipeline + Screen UI Integration

- [x] Move screen-space UI rendering into the camera pipeline DAG (currently rendered as a separate overlay step in `XRViewport.Render`).
- [x] Either integrate or retire `VPRC_RenderScreenSpaceUI` so there is a single authoritative path.
- [x] Add a strict 1x1 draw mode switch for UI path:
  - [x] disable batching when strict mode is requested
  - [x] force per-item CPU render command path

## P1 - CPU Octree Path Hardening

- [ ] Enforce and verify `GPURenderDispatch = false` behavior for this target path (including runtime preference propagation paths).
- [ ] Add a Vulkan-safe feature profile that disables compute-dependent passes until compute + descriptor wiring is complete, to keep CPU octree main path stable.

## P1 - Buffer and Resource Correctness

- [ ] Fix Vulkan buffer usage flag derivation to account for `EBufferTarget` (index/storage/uniform/indirect), not just `EBufferUsage` in `VkDataBuffer.ToVkUsageFlags`.
- [ ] Fix staging-buffer cleanup leak in Vulkan `PushSubData` device-local path.
- [ ] Audit `PushData` early-return logic so same-size updates still upload when expected.
- [ ] Replace allocator usage heuristics based on resource-name strings (e.g., `"depth"` checks) with pass-metadata/resource-descriptor-driven usage.
- [ ] Start using transient lifetimes/aliasing from cache commands (`UseLifetime`, `UseSizePolicy`) rather than defaulting all resources to persistent lifetime.

## P1 - Barrier Timing Correctness

- [ ] Replace global pending memory-barrier flush at frame start with pass-scoped emission ordered relative to the actual producer/consumer passes.

## P2 - Validation and Exit Criteria

- [ ] Add runtime assert/warning for any enqueued op with invalid pass index.
- [ ] Add metadata completeness checks for branch-executed passes vs generated metadata.
- [ ] Add Vulkan validation scenes/tests for:
  - [ ] CPU octree 3D visibility correctness
  - [ ] screen-space UI visibility correctness
  - [ ] transparency/blend correctness
  - [ ] expected draw-call counts in strict 1x1 mode

## Definition of Done

- [ ] 3D scene renders correctly via CPU octree path on Vulkan.
- [ ] 2D screen-space UI renders correctly on Vulkan through the same camera-driven DAG path.
- [ ] Camera pipeline command graph produces complete and dependency-correct pass metadata.
- [ ] Vulkan backend executes that DAG with correct barriers/transitions and no invalid-pass fallthrough.
- [ ] Strict 1x1 call mode can be enabled and validated with profiler counters.
