# Vulkan Rendering Parity Assessment

## Overview
The current Vulkan backend in `VulkanRenderer` diverges significantly from the mature OpenGL pipeline. The renderer skeleton establishes instance/swapchain management, but the higher-level rendering contract exposed by the XR wrapper classes is not satisfied. The list below captures the critical gaps and highlights why several behaviours cannot currently be abstracted away without deeper architectural work.

## State Management Features
- Depth, stencil, and colour state controls (`StencilMask`, `AllowDepthWrite`, `ClearDepth`, `ClearStencil`, `EnableDepthTest`, `DepthFunc`, `ClearColor`, `ColorMask`) are all left as `NotImplemented`.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs†L94-L143】【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L13-L114】  
  *OpenGL counterpart:* `OpenGLRenderer` forwards these calls straight into GL state toggles, so engine subsystems assume they work immediately.【F:XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs†L86-L182】  
  *Abstraction challenge:* Vulkan requires explicit pipeline state baked into `VkPipeline` objects, not transient state switches. Exposing parity would need a render-state cache plus on-demand pipeline recompilation, which is not encapsulated by the existing `XR*` wrappers.

## Resource Binding & Descriptors
- `CreateAPIRenderObject` is unimplemented, preventing Vulkan specialisations of `GenericRenderObject` from producing backend resources.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L117-L134】  
- Buffer and texture wrappers (`VkDataBuffer`, `VkTexture2D`, etc.) contain numerous stubbed methods for creation, updates, and descriptor integration.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture2D.cs†L55-L378】  
  *OpenGL counterpart:* GPU resources are bound by ID directly at draw time, matching the assumptions in the `XR*` abstractions.  
  *Abstraction challenge:* Vulkan demands descriptor sets and memory allocation upfront. The XR abstractions expose bind/unbind hooks, but they assume immediate binding semantics which Vulkan lacks, so additional lifecycle hooks are necessary.

## Graphics Pipeline Creation
- The `GraphicsPipeline` partial class is empty, and shader wrappers stop at module creation without linking them into pipeline state or descriptor layouts.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Objects/GraphicsPipeline.cs†L1-L6】【F:XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs†L8-L71】  
  *OpenGL counterpart:* Programs are linked via `GLRenderProgram`, and the renderer simply binds them per draw call.  
  *Abstraction challenge:* Vulkan pipelines combine shaders, fixed function, and render pass information. The XR shader abstraction does not currently describe pipeline layouts, push constants, or descriptor bindings, so parity requires expanding the abstraction layer.

## Framebuffer & Render Pass Mapping
- Framebuffer operations (`BindFrameBuffer`, `SetReadBuffer`, `Clear`, `CropRenderArea`, `SetRenderArea`) are all missing implementations.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs†L106-L134】【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L95-L114】  
- The XR framebuffer abstraction tracks OpenGL-style draw/read stacks and attachment enums that do not map 1:1 onto Vulkan’s render pass & subpass concepts.【F:XRENGINE/Rendering/API/Rendering/Objects/FBO/XRFrameBuffer.cs†L12-L145】  
  *Abstraction challenge:* Vulkan requires render pass objects and framebuffers created per attachment combination, incompatible with the dynamic stacking semantics expected by the XR classes.

## Command Encoding & Submission
- `WindowRenderCallback` only enqueues the swapchain image and submits a pre-recorded command buffer array `_commandBuffers`, but the engine never populates those buffers with scene commands because the higher-level draw/compute entry points are stubbed.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L140-L213】  
  *OpenGL counterpart:* Rendering commands call straight into GL during the callback.  
  *Abstraction challenge:* Vulkan needs a command-buffer recording phase separate from submission. The XR abstraction currently pushes draw calls synchronously, so parity would require a command scheduler bridging generic render commands into Vulkan command buffers before each frame.

## Asynchronous Readbacks & Utilities
- Readback helpers (`GetDepth`, `GetDepthAsync`, `GetPixelAsync`, `GetScreenshotAsync`, `CalcDotLuminance*`) remain unimplemented.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L15-L113】  
  *OpenGL counterpart:* These operations rely on `glReadPixels` style APIs, which the abstraction assumes.  
  *Abstraction challenge:* Vulkan readbacks require staging buffers and explicit synchronization, which is not encapsulated by the current helper interfaces.

## Swapchain & Synchronisation
- Core swapchain setup exists, but image layout transitions, framebuffer recreation, and per-frame uniform uploads are commented out, leaving the loop incomplete.【F:XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs†L160-L213】  
  *Impact:* Without uniform updates and recorded draw commands, the Vulkan path cannot display content even if the swapchain is alive.

## Summary of Required Work
Achieving feature parity will require:
1. Extending XR abstractions to describe render state objects, descriptor layouts, and render pass requirements suitable for Vulkan.
2. Implementing resource creation/update paths that allocate Vulkan memory and populate descriptor sets for all XR resource types.
3. Building a pipeline cache that reflects shader permutations, vertex formats, depth/stencil modes, and blending options demanded by existing materials.
4. Translating generic render commands into Vulkan command buffer recording each frame, including synchronization and layout transitions.
5. Providing async readback utilities via staging resources to fulfil engine tooling expectations.

Until these architectural pieces are in place, the Vulkan backend cannot operate like the OpenGL renderer, and attempting to call it will result in runtime `NotImplementedException` crashes.
