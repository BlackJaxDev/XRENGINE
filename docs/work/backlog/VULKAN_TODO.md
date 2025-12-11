
# Vulkan Backend Implementation Plan

This document tracks the progress of bringing the Vulkan rendering backend up to 1:1 parity with the existing OpenGL backend.

_Last validated: 2025-11-21 (master)._ 

## 1. Core Renderer Implementation (`VulkanRenderer`)

The main entry point for the renderer needs to be fleshed out to handle the render loop, state management, and object creation.

- [x] **Object Factory**
    - [x] Implement `CreateAPIRenderObject` switch statement to instantiate `Vk*` classes.
- [x] **Render Loop (`WindowRenderCallback`)**
    - [x] Implement command buffer recording loop.
    - [x] Handle `BeginRenderPass` / `EndRenderPass`.
    - [x] Submit command buffers to graphics queue.
- [ ] **State Management (VulkanStateTracker partially wired)**
    - [x] `BindFrameBuffer` (handles registry syncing + allocator-backed attachments when FBOs are bound).
        - [ ] Drive actual render-pass/command-buffer transitions once the render graph compiler emits pass plans (today we only dirty buffers).
    - [x] `SetRenderArea` (Viewport) & `CropRenderArea` (Scissor) tracked through `ApplyDynamicState`.
    - [x] `Clear`, `ClearColor`, `ClearDepth`, `ClearStencil` update tracked clear values (still limited to swapchain attachments).
    - [x] `Blit` (color attachment blits implemented with explicit layout transitions; depth/stencil copies remain TODO).
        - [ ] Add depth/stencil + viewport-to-FBO coverage, including staging buffers for readbacks.
    - [x] `MemoryBarrier` (pipeline barriers) – pending masks now flush into `vkCmdPipelineBarrier` alongside metadata-driven image layout transitions; planner groups transitions per render pass, applies stage masks that respect the pass domain, and command buffers emit pass-scoped barriers when metadata is present (falling back to coarse flush otherwise).
- [ ] **Uniforms & Descriptors**
    - [ ] `SetEngineUniforms` (camera/scene data) via descriptors/push constants.
    - [ ] `SetMaterialUniforms` (material properties).
- [ ] **Readback & Compute**
    - [ ] `GetPixelAsync` (texture readback).
    - [ ] `GetDepthAsync` (depth buffer readback).
    - [ ] `GetScreenshotAsync`.
    - [ ] `CalcDotLuminance` (compute shader or mipmap reduction).
    - [ ] `DispatchCompute`.

## 2. API Object Implementations (`Vulkan/Types/`)

These classes implement the `AbstractAPIRenderObject` interface and bridge the engine's data types to Vulkan handles.

### Textures & Samplers
- [ ] **`VkTexture2D` (allocator-aware)**
    - [x] Image creation (dedicated path + allocator-provided handles, usage flags inferred from descriptors).
    - [x] Memory allocation & binding (device-local or allocator-managed).
    - [x] Staging buffer upload for initial data (buffer helpers now wired through `PushTextureData`).
    - [x] ImageView creation & sampler creation toggles.
    - [x] Mipmap generation / blit-based mip chain.
    - [x] Fix accessibility + `LinkData` overrides so Vulkan texture wrappers compile (current build blockers reported in net9.0 target).
- [ ] **`VkTexture2DArray` / `VkTextureCube` / `VkTexture3D`**
    - [x] Replace placeholder wrappers with allocator-backed implementations (samplers, staging uploads, and mipmap hooks included).
    - [x] Same accessibility/override cleanup as `VkTexture2D` (see compiler errors).
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
- [x] **`VkRenderProgramPipeline`** (Compute/Compute pipelines).

### Geometry & Buffers
- [ ] **`VkMeshRenderer`**
    - [ ] Vertex input state setup (`VkPipelineVertexInputStateCreateInfo`).
    - [ ] `vkCmdBindVertexBuffers` / `vkCmdBindIndexBuffer` / draw calls.
    - [ ] Indirect draw support + count buffers.
- [x] **`VkDataBuffer`**
    - [x] Ensure proper staging buffer usage for `StaticDraw`/`StaticCopy` (device-local path + buffer copy helpers).
    - [x] Ensure proper host mapping for dynamic/stream usages (persistent mapping + flush helpers).

### Framebuffers & Render Targets
- [ ] **`VkFrameBuffer`**
    - [x] Basic `VkFramebuffer` creation for color attachments resolved via allocator-backed `VkTexture2D`.
    - [x] RenderPass compatibility checks / depth-stencil attachment wiring.
    - [x] Attachment view management for arrays, cube maps, render buffers.
- [x] **`VkRenderBuffer`** (depth/stencil attachments) – now allocator-aware and view-ready.
- [x] HDR scene/bloom textures promote to half-float targets and Vulkan swap chain now requests HDR formats when enabled.

### Materials
- [ ] **`VkMaterial`**
    - [ ] Descriptor set allocation.
    - [ ] `vkUpdateDescriptorSets` for textures and uniforms.
    - [ ] Binding logic (per-pass/per-material slots).

### Queries
- [ ] **`VkRenderQuery`**
    - [ ] Occlusion queries (`vkCmdBeginQuery`, `vkCmdEndQuery`).
    - [ ] Result retrieval (`vkGetQueryPoolResults`).

## 3. Infrastructure & Systems

Supporting systems required for the above implementations.

- [ ] **Memory Management**
    - [x] Mirror render graph descriptors into `VulkanResourcePlanner`.
    - [x] Build alias groups and allocate VkImages/device memory via `VulkanResourceAllocator`.
    - [ ] Extend allocator coverage (buffers, alias pooling, transient reuse beyond 2D color targets).
- [ ] **Descriptor System**
    - [ ] `DescriptorPool` management (fragmentation/exhaustion handling).
    - [ ] `DescriptorSet` caching/reuse keyed by schema.
- [ ] **Command Buffer Management**
    - [x] Primary command buffer per swapchain image + dirty-flag re-recording.
    - [ ] Command pool per thread (for multi-threaded recording).
    - [ ] Secondary command buffer support (optional, perf).
- [ ] **Synchronization**
    - [ ] Image layout transitions / buffer barriers generated from metadata (planner now accounts for render pass stages when deriving pipeline stages and emission is hooked for available metadata; still need true per-pass command submission wiring + buffer coverage).
    - [ ] Buffer memory barriers (per resource usage).
    - [x] Semaphore/Fence management for frame synchronization (image acquisition + frame-in-flight fences).
- [ ] **Staging Manager**
    - [ ] Shared staging buffer system for texture/buffer uploads (current helpers allocate ad hoc buffers).

## 4. Render Graph Roadmap

The current OpenGL-oriented pipeline executes commands immediately, which blocks us from reaping Vulkan’s explicit render-graph benefits. These items outline the architectural shifts we need before pursuing the per-feature TODOs above.

- [x] **Pass Graph Metadata**
    - [x] Extend `RenderCommandCollection`/`ViewportRenderCommandContainer` so passes declare inputs/outputs, load/store ops, and dependencies instead of executing directly.
    - [x] Record pass usage types (color target, depth target, sampled read, compute read/write) to drive barrier generation.
- [ ] **Resource Virtualization**
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
    - [ ] Implement a Vulkan allocator that maps logical handles to physical `VkImage`/`VkBuffer` objects with aliasing support for transient attachments.
- [ ] **Synchronization Metadata Layer**
    - [ ] Store per-edge access masks and pipeline stage requirements so the Vulkan backend can emit `vkCmdPipelineBarrier`, subpass dependencies, or async queue waits.
    - [ ] Mirror this data in a backend-agnostic description so other APIs can eventually benefit.
- [ ] **Descriptor & Uniform Refactor**
    - [ ] Define descriptor set schemas for engine uniforms (camera/scene) and per-material resources, then have passes reference those schemas rather than pushing data ad hoc.
    - [ ] Build a descriptor pool/set recycling system keyed off the render graph’s resource usage.
- [ ] **Backend Compilation Step**
    - [ ] Introduce a `VulkanRenderGraphCompiler` that linearizes the DAG, batches compatible passes into render passes/subpasses, and schedules pipeline barriers.
    - [ ] Provide an OpenGL fallback executor that simply walks the same graph sequentially so existing pipelines keep working during the transition.
- [ ] **Incremental Adoption Plan**
    - [ ] Route a simple pipeline (e.g., UI or forward-only path) through the new render graph to validate the architecture before migrating complex passes.
    - [ ] Document migration guidance for pipeline authors so they can describe passes/resources without dealing with API specifics.

## 5. Immediate Next Steps

1. **Refine pipeline barrier planner + FBO binding**: wire per-pass barrier plans into render-pass execution, add buffer hazards/queue ownership, and have `BindFrameBuffer` transitions actually influence recorded passes instead of just invalidating command buffers.
2. **Descriptor schemas & materials**: define the engine/global descriptor layouts, update `VkRenderProgram`/`VkMaterial` to allocate descriptor sets, and hook them into draw submission.
3. **Render-pass compatibility validation** inside `VkFrameBuffer`/`VkRenderPass`: ensure depth/stencil formats, load/store ops, and sample counts match planned render graph attachments before encoding.
4. **Expand blit/readback coverage**: add depth/stencil and viewport blits plus staging-backed readback paths to unblock screenshot/picking features.
5. **Staging/allocator consolidation**: migrate the per-texture staging buffers into a shared staging manager so large uploads can be batched and reused across texture/buffer transfers.
