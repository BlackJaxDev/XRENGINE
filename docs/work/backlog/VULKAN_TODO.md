# Vulkan Backend Implementation Plan

This document tracks the progress of bringing the Vulkan rendering backend up to 1:1 parity with the existing OpenGL backend.
It also merges and replaces `vulkan-cpu-octree-dag-todo.md`.

_Last validated: 2026-02-11 (workspace code audit)._ 

## 1. Core Renderer Implementation (`VulkanRenderer`)

The main entry point for the renderer needs to be fleshed out to handle the render loop, state management, and object creation.

- [x] **Object Factory**
    - [x] Implement `CreateAPIRenderObject` switch statement to instantiate `Vk*` classes.
- [x] **Render Loop (`WindowRenderCallback`)**
    - [x] Implement command buffer recording loop.
    - [x] Handle `BeginRenderPass` / `EndRenderPass`.
    - [x] Submit command buffers to graphics queue.
- [x] **State Management (VulkanStateTracker + pass metadata wired)**
    - [x] `BindFrameBuffer` (handles registry syncing + allocator-backed attachments when FBOs are bound).
        - [x] Bind operations now refresh planner context and command buffers emit pass-scoped transitions/barriers from metadata during recording.
    - [x] `SetRenderArea` (Viewport) & `CropRenderArea` (Scissor) tracked in renderer state and emitted as Vulkan dynamic viewport/scissor during command recording.
    - [x] `Clear`, `ClearColor`, `ClearDepth`, `ClearStencil` update tracked clear values for swapchain and explicit FBO attachments.
    - [x] `Blit` (color + depth/stencil copies, swapchain/FBO endpoint support, and viewport-to-FBO coverage with explicit layout transitions).
    - [x] `MemoryBarrier` (pipeline barriers) - pending masks now flush into `vkCmdPipelineBarrier` alongside metadata-driven image layout transitions; planner groups transitions per render pass, applies stage masks that respect the pass domain, and command buffers emit pass-scoped barriers when metadata is present (falling back to coarse flush otherwise).
- [x] **Uniforms & Descriptors**
    - [x] `SetEngineUniforms` (camera/scene data) routed through Vulkan.
    - [x] `SetMaterialUniforms` (material properties) routed through Vulkan.
    - [x] Complete descriptor schema/pool reuse refactor for deterministic caching and lifetime handling.
- [x] **Readback & Compute**
    - [x] `GetPixelAsync` (texture readback, including bound read-FBO path + swapchain fallback).
    - [x] `GetDepthAsync` (depth buffer readback, including direct FBO depth attachment reads).
    - [x] `GetScreenshotAsync` (readback from bound read FBO or swapchain).
    - [x] `CalcDotLuminance` (texture readback on smallest mip texel path).
    - [x] `DispatchCompute`.

## 2. API Object Implementations (`Vulkan/Types/`)

These classes implement the `AbstractAPIRenderObject` interface and bridge the engine's data types to Vulkan handles.

### Textures & Samplers
- [x] **`VkTexture2D` (allocator-aware)**
    - [x] Image creation (dedicated path + allocator-provided handles, usage flags inferred from descriptors).
    - [x] Memory allocation & binding (device-local or allocator-managed).
    - [x] Staging buffer upload for initial data (buffer helpers now wired through `PushTextureData`).
    - [x] ImageView creation & sampler creation toggles.
    - [x] Mipmap generation / blit-based mip chain.
    - [x] Fix accessibility + `LinkData` overrides so Vulkan texture wrappers compile (current build blockers reported in net9.0 target).
- [x] **`VkTexture2DArray` / `VkTextureCube` / `VkTexture3D`**
    - [x] Replace placeholder wrappers with allocator-backed implementations (samplers, staging uploads, and mipmap hooks included).
    - [x] Same accessibility/override cleanup as `VkTexture2D` (see compiler errors).
- [x] **`VkTexture1D` / `VkTexture1DArray` / `VkTextureRectangle` / `VkTextureCubeArray` / `VkTextureBuffer` / `VkTextureView`**
    - [x] Add Vulkan object-factory mappings for all remaining texture object types and texture views.
    - [x] Implement upload/layout support for remaining image-backed texture variants (including 3D upload path and cube-array layers).
    - [x] Implement Vulkan texture-view object recreation/wiring against viewed resources (image and buffer-backed view cases).
    - [x] Add texel-buffer descriptor support (`UniformTexelBuffer` / `StorageTexelBuffer`) across render-program, material, and mesh-renderer descriptor writes.
    - [x] Extend framebuffer/blit attachment resolution to use generic Vulkan texture descriptor sources (instead of only 2D/2DArray/Cube hardcoded paths).
- [x] **`VkSampler`**
    - [x] Wire up address modes, filtering, anisotropy (sampler creation now consumes XR sampler descriptors).

### Shaders & Pipelines
- [x] **`VkShader`**
    - [x] `VkShaderModule` creation (loads `XRShader.Source` into modules).
    - [x] Reflection (layout info / descriptor bindings).
- [x] **`VkRenderProgram`**
    - [x] `VkPipelineLayout` creation.
    - [x] `VkDescriptorSetLayout` management.
    - [x] `VkPipeline` creation (graphics + dynamic state wiring).
    - [x] Event wiring for uniforms/samplers/images/SSBO binds and compute dispatch callbacks.
- [x] **`VkRenderProgramPipeline`** (Compute/Compute pipelines).

### Geometry & Buffers
- [x] **`VkMeshRenderer`**
    - [x] Vertex input state setup (`VkPipelineVertexInputStateCreateInfo`).
    - [x] `vkCmdBindVertexBuffers` / `vkCmdBindIndexBuffer` / draw calls.
    - [x] Indirect draw support + count buffers.
- [x] **`VkDataBuffer`**
    - [x] Ensure proper staging buffer usage for `StaticDraw`/`StaticCopy` (device-local path + buffer copy helpers).
    - [x] Ensure proper host mapping for dynamic/stream usages (persistent mapping + flush helpers).
    - [x] Vulkan subscriber path for `XRDataBuffer.BindTo(...)` / `BindSSBORequested`.

### Framebuffers & Render Targets
- [x] **`VkFrameBuffer`**
    - [x] Basic `VkFramebuffer` creation for color attachments resolved via allocator-backed `VkTexture2D`.
    - [x] RenderPass compatibility checks / depth-stencil attachment wiring.
    - [x] Attachment view management for arrays, cube maps, render buffers.
- [x] **`VkRenderBuffer`** (depth/stencil attachments) â€“ now allocator-aware and view-ready.
- [x] HDR scene/bloom textures promote to half-float targets and Vulkan swap chain now requests HDR formats when enabled.

### Materials
- [x] **`VkMaterial`**
    - [x] Descriptor set allocation.
    - [x] `vkUpdateDescriptorSets` for textures and uniforms.
    - [x] Binding logic (per-pass/per-material slots).

### Queries
- [x] **`VkRenderQuery`**
    - [x] Occlusion queries (`vkCmdBeginQuery`, `vkCmdEndQuery`).
    - [x] Result retrieval (`vkGetQueryPoolResults`).

## 3. Infrastructure & Systems

Supporting systems required for the above implementations.

- [x] **Memory Management**
    - [x] Mirror render graph descriptors into `VulkanResourcePlanner`.
    - [x] Build alias groups and allocate VkImages/device memory via `VulkanResourceAllocator`.
    - [x] Extend allocator coverage (buffers, alias pooling, transient reuse beyond 2D color targets).
- [x] **Descriptor System**
    - [x] `DescriptorPool` management (fragmentation/exhaustion handling).
    - [x] `DescriptorSet` caching/reuse keyed by schema.
- [x] **Command Buffer Management**
    - [x] Primary command buffer per swapchain image + dirty-flag re-recording.
    - [x] Command pool per thread (for multi-threaded recording).
    - [x] Secondary command buffer support (optional, perf).
- [x] **Synchronization**
    - [x] Dependency-aware/topological barrier planning via `ExplicitDependencies` (`VulkanBarrierPlanner`).
    - [x] Frame-op context capture for planning barriers from captured pipeline/viewport metadata.
    - [x] Image layout transitions / buffer barriers generated from metadata (planner coverage is partial; still need true per-pass submission wiring + full buffer coverage).
    - [x] Buffer memory barriers (per resource usage).
    - [x] Semaphore/Fence management for frame synchronization (image acquisition + frame-in-flight fences).
- [x] **Staging Manager**
    - [x] Shared staging buffer system for texture/buffer uploads (current helpers allocate ad hoc buffers).

## 4. Render Graph Roadmap

The current OpenGL-oriented pipeline executes commands immediately, which blocks us from reaping Vulkanâ€™s explicit render-graph benefits. These items outline the architectural shifts we need before pursuing the per-feature TODOs above.

- [x] **Pass Graph Metadata**
    - [x] Extend `RenderCommandCollection`/`ViewportRenderCommandContainer` so passes declare inputs/outputs, load/store ops, and dependencies instead of executing directly.
    - [x] Record pass usage types (color target, depth target, sampled read, compute read/write) to drive barrier generation.
    - [x] Recurse metadata traversal into branch containers (`VPRC_IfElse`, `VPRC_Switch`).
    - [x] Add `DescribeRenderPass` coverage for major feature commands (Forward+, LightCombine, Bloom, ReSTIR, LightVolumes, SpatialHash AO, Radiance Cascades, Surfel GI, Voxel Cone Tracing, Temporal Accumulation).
    - [x] Ensure emitted draw/blit/compute ops resolve to valid pass indices (invalid indices now warn and fall back).
    - [x] Add pipeline/viewport identity to scheduling to avoid pass-index collisions between camera/UI pipelines.
- [x] **Resource Virtualization**
    - [x] Replace `_textures`/`_frameBuffers` dictionaries with a logical resource registry (format, size policy, lifetime class).
    - [x] Teach cache commands to publish texture/FBO descriptor data (size policy, lifetime) into the registry.
    - [x] Expose registry snapshots to the Vulkan backend so it can plan physical allocations before implementing aliasing.
    - [x] Build a Vulkan allocation planner that groups logical textures by lifetime/alias key ahead of VkImage creation.
    - [x] Introduce a Vulkan resource allocator skeleton that consumes the planner and prepares alias groups for VkImage creation.
    - [x] Capture preliminary VkImage create templates (extent/format metadata) per alias group for future physical allocation.
    - [x] Resolve size policies against the active viewport and build physical alias groups with inferred VkImage usage flags.
    - [x] Allocate VkImages + device memory per alias group (still basic usage, no alias pooling yet).
    - [x] Expose allocator-managed VkImage handles for consumption by Vulkan texture/framebuffer wrappers (VkTexture2D + VkFrameBuffer now bind allocator images/views).
    - [x] Expand allocator-backed integration to remaining texture/attachment types (arrays, cube maps, render buffers).
    - [x] Implement a Vulkan allocator that maps logical handles to physical `VkImage`/`VkBuffer` objects with aliasing support for transient attachments.
- [x] **Synchronization Metadata Layer**
    - [x] Store per-edge access masks and pipeline stage requirements so the Vulkan backend can emit `vkCmdPipelineBarrier`, subpass dependencies, or async queue waits.
    - [x] Mirror this data in a backend-agnostic description so other APIs can eventually benefit.
- [x] **Descriptor & Uniform Refactor**
    - [x] Define descriptor set schemas for engine uniforms (camera/scene) and per-material resources, then have passes reference those schemas rather than pushing data ad hoc.
    - [x] Build a descriptor pool/set recycling system keyed off the render graphâ€™s resource usage.
- [x] **Backend Compilation Step**
    - [x] Introduce a `VulkanRenderGraphCompiler` that linearizes the DAG, batches compatible passes into render passes/subpasses, and schedules pipeline barriers.
    - [x] Provide an OpenGL fallback executor that simply walks the same graph sequentially so existing pipelines keep working during the transition.
- [x] **Incremental Adoption Plan**
    - [x] Route a simple pipeline (e.g., UI or forward-only path) through the new render graph to validate the architecture before migrating complex passes.
    - [x] Document migration guidance for pipeline authors so they can describe passes/resources without dealing with API specifics.

## 5. Vulkan CPU Octree + Screen UI DAG (Merged)

Goal: get 3D scene rendering and 2D screen-space UI rendering fully working on Vulkan through the CPU octree render path, with 1-by-1 render calls driven by a DAG compiled from the camera render pipeline.

### P0 - Verified Completed

- [x] Implement Vulkan compute dispatch (`DispatchCompute` in `Vulkan/Init.cs`).
- [x] Add Vulkan `XRRenderProgram` event wiring for uniform sets, samplers, image binds, SSBO binds, and compute dispatch callbacks.
- [x] Implement Vulkan subscriber path for `XRDataBuffer.BindTo(...)` so Forward+/compute SSBO binds work.
- [x] Complete Vulkan material/render-state parity (blend, cull/winding, stencil state, depth/stencil PSO variants).
- [x] Expand Vulkan engine-uniform coverage to include missing `EEngineUniform` values (notably UI and prev-frame variants).
- [x] Implement/route `SetEngineUniforms` and `SetMaterialUniforms` in Vulkan to avoid silent no-op behavior in shared paths.
- [x] Fix UI transparency correctness on Vulkan (UI blend state now follows material/render state).
- [x] Decide and complete Vulkan ImGui path via dead-path containment (`SupportsImGui == false` and no active Vulkan ImGui render path).
- [x] Make render-pass metadata traversal recurse into branch containers (`VPRC_IfElse`, `VPRC_Switch`).
- [x] Capture pipeline/resource context per frame op and plan barriers from captured context.

### P0 - Still Open

- [x] Move screen-space UI rendering into the camera pipeline DAG as the default runtime path on Vulkan.
  - Vulkan now forces pipeline UI path and ignores `RenderUIThroughPipeline`.
- [x] Integrate or retire the overlay fallback so there is a single authoritative screen-space UI path on Vulkan.
  - Overlay fallback remains for non-Vulkan backends; Vulkan no longer uses it.
- [x] Add a strict 1x1 draw mode switch for UI path:
  - [x] disable batching when strict mode is requested.
  - [x] force per-item CPU render command path.

### P1 - CPU Octree Path Hardening

- [x] Enforce and verify `GPURenderDispatch = false` behavior for this target path (including runtime preference propagation paths).
  - `VisualScene3D.ApplyRenderDispatchPreference` now re-resolves through `Engine.Rendering.ResolveGpuRenderDispatchPreference`; pipeline setters in `DebugOpaqueRenderPipeline` and `SurfelDebugRenderPipeline` apply the same guard.
- [x] Add a Vulkan-safe feature profile that disables compute-dependent passes until compute + descriptor wiring is complete.
  - New `VulkanFeatureProfile` static class gates `EnableComputeDependentPasses`, `EnableGpuRenderDispatch`, `EnableGpuBvh`, and `EnableImGui` centrally; consumed by `DefaultRenderPipeline` and `SurfelDebugRenderPipeline`.

### P1 - Buffer and Resource Correctness

- [x] Fix Vulkan buffer usage flag derivation to account for `EBufferTarget` (index/storage/uniform/indirect), not just `EBufferUsage` in `VkDataBuffer.ToVkUsageFlags`.
  - `StaticDraw` now maps to `TransferDstBit`; `StaticCopy` maps to `TransferSrcBit | TransferDstBit`. Both `VkDataBuffer` and `VulkanResourceAllocator` updated.
- [x] Fix staging-buffer cleanup leak in Vulkan `PushSubData` device-local path.
  - `VulkanStagingManager.Trim()` added with idle-frame eviction policy (3 frames idle, max 32 pooled); called each frame from `Drawing.WindowRenderCallback`.
- [x] Audit `PushData` early-return logic so same-size updates still upload when expected.
  - `TryGetUploadSlice` now emits `Debug.VulkanWarningEvery` when CPU-side data source has no valid address, instead of returning false silently.
- [x] Replace allocator usage heuristics based on resource-name strings (for example, `"depth"` checks) with pass-metadata/resource-descriptor-driven usage.
  - `InferImageUsage` now accepts `VulkanResourcePlanner` and queries FBO attachment descriptors (`FrameBufferAttachmentDescriptor`) for depth vs color classification; format-string fallback retained only when no descriptor data is available.
- [x] Start using transient lifetimes/aliasing from cache commands (`UseLifetime`, `UseSizePolicy`) rather than defaulting resources to persistent lifetime.
  - 8 FBO cache commands in `DefaultRenderPipeline` (LightCombine, ForwardPass, RestirComposite, LightVolumeComposite, RadianceCascadeComposite, SurfelGIComposite, Velocity, PostProcess) now use `RenderResourceLifetime.Transient`; temporal/history FBOs remain Persistent.

### P1 - Barrier Timing Correctness

- [x] Replace global pending memory-barrier flush at frame start with pass-scoped emission ordered relative to the actual producer/consumer passes.
  - Removed global `EmitPendingMemoryBarriers` from top of `RecordCommandBuffer`; barriers now emit at per-pass boundaries via `EmitPassBarriers` which drains both global and per-pass masks (`VulkanRenderer.State.DrainMemoryBarrierForPass`).

### P2 - Validation and Exit Criteria

- [ ] Add runtime assert/warning for any enqueued op with invalid pass index.
- [ ] Add metadata completeness checks for branch-executed passes vs generated metadata.
- [ ] Add Vulkan validation scenes/tests for:
  - [ ] CPU octree 3D visibility correctness.
  - [ ] Screen-space UI visibility correctness.
  - [ ] Transparency/blend correctness.
  - [ ] Expected draw-call counts in strict 1x1 mode.

### Definition of Done

- [ ] 3D scene renders correctly via CPU octree path on Vulkan.
- [ ] 2D screen-space UI renders correctly on Vulkan through the same camera-driven DAG path.
- [ ] Camera pipeline command graph produces complete and dependency-correct pass metadata.
- [ ] Vulkan backend executes that DAG with correct barriers/transitions and no invalid-pass fallthrough.
- [ ] Strict 1x1 call mode can be enabled and validated with profiler counters.

## 6. Immediate Next Steps

1. **Refine pipeline barrier planner + FBO binding**: wire per-pass barrier plans into render-pass execution, add buffer hazards/queue ownership, and have `BindFrameBuffer` transitions actually influence recorded passes instead of just invalidating command buffers.
2. **Descriptor schemas & materials**: define the engine/global descriptor layouts, update `VkRenderProgram`/`VkMaterial` to allocate descriptor sets, and hook them into draw submission.
3. **Render-pass compatibility validation** inside `VkFrameBuffer`/`VkRenderPass`: ensure depth/stencil formats, load/store ops, and sample counts match planned render graph attachments before encoding.
4. **Expand blit/readback coverage**: add depth/stencil and viewport blits plus staging-backed readback paths to unblock screenshot/picking features.
5. **Staging/allocator consolidation**: migrate the per-texture staging buffers into a shared staging manager so large uploads can be batched and reused across texture/buffer transfers.
