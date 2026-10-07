# Vulkan Backend Parity Validation

Architecture: [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md), [Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract), [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md), [Vulkan Memory Allocation](../../../architecture/rendering/vulkan-memory-allocation.md).

Code todos: [Vulkan Wrapper Parity TODO](../../todo/rendering/vulkan-wrapper-parity-todo.md), [Material Table And Texture Binding Ladder TODO](../../todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md), [Vulkan Managed Allocator VMA Concepts TODO](../../todo/rendering/optimization/vulkan-managed-vma-concepts-allocator-todo.md), [Vulkan frame-loop master](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md).

## Setup

- Use the run header, run modes, and fast software checks in [Vulkan Core Validation](vulkan-core-validation.md#setup).
- Compare against OpenGL with the same scene, camera, and resolution.
- Enable validation with `XRE_VULKAN_VALIDATION=1` and `XRE_VULKAN_SYNC_VALIDATION=1`.
- Select the allocator with `VulkanRobustnessSettings.AllocatorBackend` (`Legacy`, `Managed`, `Vma`). Read allocator statistics with the MCP `get_vulkan_memory_statistics` action.
- Select bindless material mode with `XRE_VULKAN_BINDLESS_MATERIAL_MODE`.

## Checks

### Rendering Parity Matrix

Procedure: render each item on OpenGL and Vulkan from the same camera and compare captures. Expected: no unexplained visual difference.

- [ ] Basic scene: opaque deferred, opaque forward, masked forward, transparent forward, on-top and debug rendering, background and skybox. Last evidence: none.
- [ ] Primitive types: triangles, lines, line strips, points. Last evidence: none.
- [ ] FBO and post: G-buffer creation and recreation, MSAA G-buffer resolve, light combine, forward pass, bloom, motion blur and depth of field, temporal accumulation, exposure update, FXAA, SMAA, and TSR output. Last evidence: none.
- [ ] UI: ImGui overlay, screen-space UI pipeline, batched UI geometry, and isolation with `XRE_SKIP_UI_PIPELINE=1` and `XRE_SKIP_IMGUI=1`. Last evidence: none.
- [ ] Capture paths: screenshot, depth readback, stencil picking and readback, scene capture cube faces, octahedral light probe encoding, mirror and reflection passes. Last evidence: none.
- [ ] GPU-driven paths: indirect count, non-count fallback, GPU culling, `GpuIndirectZeroReadback` (zero steady readback bytes), `GpuIndirectInstrumented` (expected diagnostic counters), material-table draws with descriptor indexing, bindless material textures with high material diversity, dynamic material row layout fallback diagnostics, `GpuMeshletZeroReadback` on `VK_EXT_mesh_shader` hardware, `GpuMeshletInstrumented`, occlusion culling diagnostics, secondary command buffers, and parallel secondary recording thresholds. Last evidence: none.
- [ ] Default-world opaque, forward, shadow, and debug-primitive output from the same camera. Last evidence: none.
- [ ] Ordinary, shadow, depth-normal, FBO and post, and bindless-material paths. Last evidence: none.

### Wrapper Parity

- [ ] `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` Vulkan draw paths. Expected: no read of count, visibility, or indirect buffers in steady state; readback counters stay at zero after warmup. Last evidence: none.
- [ ] On `VK_EXT_mesh_shader` hardware, Vulkan meshlet dispatch uses GPU-written task records and indirect-count dispatch. Last evidence: none.
- [ ] Directional cascade and point-light shadow passes with validation layers. Expected: no new VUIDs. Last evidence: none.
- [ ] One mesh with triangle, line, point, interleaved, instanced, skinned, and blendshape buffers on both backends. Expected: equal output. Last evidence: none.
- [ ] Profile the Vulkan prepared-source and async shader compile path against OpenGL parallel compile. Record warmup latency and completion polling cost. Last evidence: none.
- [ ] Vulkan shader compilation regression tests and a default-world pipeline prewarm pass. Last evidence: none.
- [ ] Compute skinning, GPUScene, indirect draw, readback, UI PBO and webview, and texture-buffer paths with validation layers. Expected: no new VUIDs. Last evidence: none.
- [ ] Vulkan device-address consumer path. Expected: the OpenGL path still renders through the shared buffer identity contract. Last evidence: none.

### Textures

- [ ] Non-filterable texture formats and their fallback behavior on Vulkan. Last evidence: none.
- [ ] `XRTexture1D`: upload of empty or missing mip levels matches OpenGL. Last evidence: none.
- [ ] `XRTexture1DArray`: array-layer upload order and per-layer missing-data handling. Last evidence: none.
- [ ] `XRTexture2D`: video-frame upload matches the OpenGL import and update paths. Last evidence: none.
- [ ] `XRTextureRectangle`: sampler constraints map to Vulkan 2D image-view behavior, and no mipmaps are generated. Last evidence: none.
- [ ] `XRTexture3D`: row and slice layout matches OpenGL. Last evidence: none.
- [ ] `XRTextureCube`: face order and layer indices match OpenGL. Last evidence: none.
- [ ] `XRTextureBuffer`: uniform and storage texel-buffer descriptors. Last evidence: none.
- [ ] Default-pipeline FBO and post textures, point shadows, cascaded shadows, cube captures, 2D-array captures, UI textures, texture buffers, and texture views match OpenGL. Last evidence: none.

### Bindless Materials

- [ ] Vulkan bindless runtime smoke. Expected: textured materials have nonzero descriptor indices, different materials sample different descriptors, missing textures sample the fallback descriptor, and update-after-bind causes no validation errors. Last evidence: none.
- [ ] Capture one RenderDoc frame. Examine the global descriptor table (`set = 2`, `binding = 31`), material-table buffer values, sampled image descriptors, and final colors. Last evidence: none.
- [ ] Material-diverse scene with validation layers and descriptor indexing. Expected: no validation errors and correct output. Last evidence: none.
- [ ] Bindless material sampling through `GpuIndirectZeroReadback` when Vulkan GPU dispatch is enabled. Last evidence: none.
- [ ] Unit Testing World with `XRE_VULKAN_BINDLESS_MATERIAL_MODE=Required`. Last evidence: none.
- [ ] Bindless off. Expected: traditional Vulkan material rendering still works. Last evidence: none.

### Allocators

- [ ] Targeted Vulkan allocator unit tests, Vulkan buffer parity tests, and a `XREngine.Runtime.Rendering` build. Last evidence: none.
- [ ] Vulkan buffer parity tests with `AllocatorBackend = Managed`, and with `AllocatorBackend = Vma` on hardware. Last evidence: none.
- [ ] Start the editor in Vulkan mode with `Legacy`, `Managed`, and `Vma`. Expected: each backend starts and reports its name in `log_vulkan.log`. Last evidence: none.
- [ ] Read allocator statistics before and after you create and destroy staging buffers, texture images, render targets, and scene-database buffers. Expected: allocation counts and bytes return to the baseline. Last evidence: none.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
