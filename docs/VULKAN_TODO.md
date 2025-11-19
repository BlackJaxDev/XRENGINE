# Vulkan Backend Implementation Plan

This document tracks the progress of bringing the Vulkan rendering backend up to 1:1 parity with the existing OpenGL backend.

## 1. Core Renderer Implementation (`VulkanRenderer`)

The main entry point for the renderer needs to be fleshed out to handle the render loop, state management, and object creation.

- [x] **Object Factory**
    - [x] Implement `CreateAPIRenderObject` switch statement to instantiate `Vk*` classes.
- [x] **Render Loop (`WindowRenderCallback`)**
    - [x] Implement command buffer recording loop.
    - [x] Handle `BeginRenderPass` / `EndRenderPass`.
    - [x] Submit command buffers to graphics queue.
- [ ] **State Management**
    - [ ] `BindFrameBuffer` (Handle RenderPass transitions).
    - [ ] `SetRenderArea` (Viewport) & `CropRenderArea` (Scissor).
    - [ ] `Clear`, `ClearColor`, `ClearDepth`, `ClearStencil`.
    - [ ] `Blit` (Image blitting with barriers).
    - [ ] `MemoryBarrier` (Pipeline barriers).
- [ ] **Uniforms & Descriptors**
    - [ ] `SetEngineUniforms` (Camera/Scene data).
    - [ ] `SetMaterialUniforms` (Material properties).
- [ ] **Readback & Compute**
    - [ ] `GetPixelAsync` (Texture readback).
    - [ ] `GetDepthAsync` (Depth buffer readback).
    - [ ] `GetScreenshotAsync`.
    - [ ] `CalcDotLuminance` (Compute shader or mipmap reduction).
    - [ ] `DispatchCompute`.

## 2. API Object Implementations (`Vulkan/Types/`)

These classes implement the `AbstractAPIRenderObject` interface and bridge the engine's data types to Vulkan handles.

### Textures & Samplers
- [ ] **`VkTexture2D`**
    - [ ] Image creation (usage flags, tiling).
    - [ ] Memory allocation & binding.
    - [ ] Staging buffer upload for initial data.
    - [ ] ImageView creation.
    - [ ] Mipmap generation (Blit).
- [ ] **`VkTexture2DArray`**
- [ ] **`VkTextureCube`**
- [ ] **`VkTexture3D`**
- [ ] **`VkSampler`**
    - [ ] Address modes, filtering, anisotropy.

### Shaders & Pipelines
- [ ] **`VkShader`**
    - [ ] `VkShaderModule` creation.
    - [ ] Reflection (if needed for layout).
- [ ] **`VkRenderProgram`**
    - [ ] `VkPipelineLayout` creation.
    - [ ] `VkDescriptorSetLayout` management.
    - [ ] `VkPipeline` creation (Graphics).
- [ ] **`VkRenderProgramPipeline`** (Compute/Compute Pipelines).

### Geometry & Buffers
- [ ] **`VkMeshRenderer`**
    - [ ] Vertex Input State setup (`VkPipelineVertexInputStateCreateInfo`).
    - [ ] `vkCmdBindVertexBuffers`.
    - [ ] `vkCmdBindIndexBuffer`.
    - [ ] `vkCmdDrawIndexed` / `vkCmdDraw`.
    - [ ] Indirect draw support.
- [ ] **`VkDataBuffer`** (Review existing `VkDataBuffer.cs` implementation).
    - [ ] Ensure proper staging buffer usage for `StaticDraw`.
    - [ ] Ensure proper host mapping for `DynamicDraw`.

### Framebuffers & Render Targets
- [ ] **`VkFrameBuffer`**
    - [ ] `VkFramebuffer` creation.
    - [ ] RenderPass compatibility checks.
    - [ ] Attachment image view management.
- [ ] **`VkRenderBuffer`** (Depth/Stencil attachments).

### Materials
- [ ] **`VkMaterial`**
    - [ ] Descriptor Set allocation.
    - [ ] `vkUpdateDescriptorSets` for textures and uniforms.
    - [ ] Binding logic.

### Queries
- [ ] **`VkRenderQuery`**
    - [ ] Occlusion queries (`vkCmdBeginQuery`, `vkCmdEndQuery`).
    - [ ] Result retrieval (`vkGetQueryPoolResults`).

## 3. Infrastructure & Systems

Supporting systems required for the above implementations.

- [ ] **Memory Management**
    - [ ] Implement a proper allocator (e.g., VMA or a simple chunk allocator) instead of `vkAllocateMemory` per object.
- [ ] **Descriptor System**
    - [ ] `DescriptorPool` management (handling fragmentation/exhaustion).
    - [ ] `DescriptorSet` caching/reuse.
- [ ] **Command Buffer Management**
    - [ ] Command pool per thread (if multi-threaded).
    - [ ] Secondary command buffer support (optional, for performance).
- [ ] **Synchronization**
    - [ ] Image Layout Transitions (Barriers).
    - [ ] Buffer Memory Barriers.
    - [ ] Semaphore/Fence management for frame synchronization.
- [ ] **Staging Manager**
    - [ ] A shared staging buffer system for texture/buffer uploads to avoid creating/destroying buffers constantly.
