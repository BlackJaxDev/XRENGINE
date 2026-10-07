# Vulkan Backend Parity Validation

## Scope

This document owns validation that compares Vulkan backend behavior with the OpenGL reference path. It covers wrapper parity, texture behavior, bindless material behavior, allocator behavior, and backend visual parity.

Architecture: [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md), [Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract), [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md), [Vulkan Memory Allocation](../../../architecture/rendering/vulkan-memory-allocation.md).

Code todos: [Vulkan Wrapper Parity TODO](../../todo/rendering/vulkan-wrapper-parity-todo.md), [Vulkan frame-loop master](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md). Related external todos: [Material Table And Texture Binding Ladder TODO](../../todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md).

## Setup

- Use the run modes and fast software checks in [Vulkan Core Validation](vulkan-core-validation.md#setup).
- Compare Vulkan against OpenGL with the same scene, camera, resolution, settings, and warmup.
- Enable validation with `XRE_VULKAN_VALIDATION=1` and `XRE_VULKAN_SYNC_VALIDATION=1`.
- Select the allocator with `VulkanRobustnessSettings.AllocatorBackend` (`Legacy`, `Managed`, or `Vma`).
- Read allocator statistics with the MCP `get_vulkan_memory_statistics` action.
- Select bindless material mode with `XRE_VULKAN_BINDLESS_MATERIAL_MODE`.
- Use `XRE_SKIP_IMGUI=1` and `XRE_SKIP_UI_PIPELINE=1` only to isolate overlay or UI pipeline differences.

## Checks

### Rendering Parity Matrix

Architecture: [Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract).

Procedure for this feature: render each row on OpenGL and Vulkan from the same camera and compare captures.
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Basic scene | Compare opaque deferred, opaque forward, masked forward, transparent forward, on-top and debug rendering, background, and skybox. | No unexplained visual difference. | Open | none |
| Primitive types | Compare triangles, lines, line strips, and points. | No unexplained visual difference. | Open | none |
| FBO and post | Compare G-buffer creation and recreation, MSAA G-buffer resolve, light combine, forward pass, bloom, motion blur, depth of field, temporal accumulation, exposure update, FXAA, SMAA, and TSR output. | No unexplained visual difference. | Open | none |
| UI | Compare ImGui overlay, screen-space UI pipeline, batched UI geometry, `XRE_SKIP_UI_PIPELINE=1`, and `XRE_SKIP_IMGUI=1`. | UI isolation changes only the selected overlay or UI pipeline. | Open | none |
| Capture paths | Compare screenshot, depth readback, stencil picking and readback, scene capture cube faces, octahedral light probe encoding, mirror, and reflection passes. | Captures match the visible source. | Open | none |
| GPU-driven paths | Compare indirect count, non-count fallback, GPU culling, `GpuIndirectZeroReadback`, `GpuIndirectInstrumented`, material-table draws with descriptor indexing, bindless material textures, dynamic material row layout fallback diagnostics, `GpuMeshletZeroReadback`, `GpuMeshletInstrumented`, occlusion diagnostics, secondary command buffers, and parallel secondary recording thresholds. | Visual output matches, zero-readback lanes stay at zero steady readback bytes, and instrumented lanes report expected counters. | Open | none |
| Default-world output | Compare opaque, forward, shadow, and debug-primitive output from the same camera. | No unexplained visual difference. | Open | none |
| Ordinary render paths | Compare ordinary, shadow, depth-normal, FBO and post, and bindless-material paths. | No unexplained visual difference. | Open | none |

### Wrapper Parity

Architecture: [Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Zero-readback strategies | Run `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` Vulkan draw paths. | No read of count, visibility, or indirect buffers in steady state. Readback counters stay at zero after warmup. | Open | none |
| Meshlet dispatch | On `VK_EXT_mesh_shader` hardware, run Vulkan meshlet dispatch. | Dispatch uses GPU-written task records and indirect-count dispatch. | Open | none |
| Shadow validation | Run directional cascade and point-light shadow passes with validation layers. | No new VUIDs. | Open | none |
| Mesh buffer parity | Render one mesh with triangle, line, point, interleaved, instanced, skinned, and blendshape buffers on both backends. | Equal output. | Open | none |
| Shader compile profile | Profile the Vulkan prepared-source and async shader compile path against OpenGL parallel compile. | Warmup latency and completion polling cost are recorded. | Open | none |
| Shader regression and prewarm | Run Vulkan shader compilation regression tests and a default-world pipeline prewarm pass. | Tests pass and prewarm completes. | Open | none |
| Validation-layer paths | Run compute skinning, GPUScene, indirect draw, readback, UI PBO and webview, and texture-buffer paths with validation layers. | No new VUIDs. | Open | none |
| Device-address consumer | Run the Vulkan device-address consumer path and the OpenGL shared buffer identity path. | Vulkan uses the device-address path and OpenGL still renders through the shared buffer identity contract. | Open | none |

### Textures

Architecture: [Backend Wrapper Parity Contract](../../../architecture/rendering/vulkan-renderer.md#backend-wrapper-parity-contract).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Non-filterable formats | Render non-filterable texture formats and fallbacks on Vulkan and OpenGL. | Fallback behavior is explicit and equivalent where the APIs support it. | Open | none |
| `XRTexture1D` | Compare upload of empty or missing mip levels. | Vulkan matches OpenGL. | Open | none |
| `XRTexture1DArray` | Compare array-layer upload order and per-layer missing-data handling. | Vulkan matches OpenGL. | Open | none |
| `XRTexture2D` video | Compare video-frame upload with OpenGL import and update paths. | Vulkan matches OpenGL. | Open | none |
| `XRTextureRectangle` | Compare sampler constraints and mipmap behavior. | Vulkan maps to 2D image-view behavior and does not generate mipmaps. | Open | none |
| `XRTexture3D` | Compare row and slice layout. | Vulkan matches OpenGL. | Open | none |
| `XRTextureCube` | Compare face order and layer indices. | Vulkan matches OpenGL. | Open | none |
| `XRTextureBuffer` | Compare uniform and storage texel-buffer descriptors. | Vulkan matches OpenGL. | Open | none |
| Render textures and views | Compare default-pipeline FBO and post textures, point shadows, cascaded shadows, cube captures, 2D-array captures, UI textures, texture buffers, and texture views. | Vulkan matches OpenGL. | Open | none |

### Bindless Materials

Architecture: [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Bindless smoke | Run a Vulkan bindless material runtime smoke. | Textured materials have nonzero descriptor indices. Different materials sample different descriptors. Missing textures sample the fallback descriptor. Update-after-bind causes no validation errors. | Open | none |
| RenderDoc descriptor table | Capture one RenderDoc frame and examine the global descriptor table (`set = 2`, `binding = 31`), material-table buffer values, sampled image descriptors, and final colors. | Descriptor table entries and material rows match the final colors. | Open | none |
| Material diversity | Run a material-diverse scene with validation layers and descriptor indexing. | No validation errors and correct output. | Open | none |
| GPU indirect bindless | Run bindless material sampling through `GpuIndirectZeroReadback`. | Bindless sampling works when Vulkan GPU dispatch is enabled. | Open | none |
| Required mode | Start the Unit Testing World with `XRE_VULKAN_BINDLESS_MATERIAL_MODE=Required`. | Startup succeeds or fails with an explicit unsupported-capability diagnostic. | Open | none |
| Bindless off | Run with bindless material mode disabled. | Traditional Vulkan material rendering still works. | Open | none |

### Allocators

Architecture: [Vulkan Memory Allocation](../../../architecture/rendering/vulkan-memory-allocation.md).
| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Allocator software checks | Run targeted Vulkan allocator unit tests, Vulkan buffer parity tests, and a `XREngine.Runtime.Rendering` build. | Checks pass. | Open | none |
| Managed and VMA parity | Run Vulkan buffer parity tests with `AllocatorBackend = Managed`, and with `AllocatorBackend = Vma` on hardware. | Checks pass for each available backend. | Open | none |
| Allocator startup | Start the editor in Vulkan mode with `Legacy`, `Managed`, and `Vma`. | Each backend starts and reports its name in `log_vulkan.log`. | Open | none |
| Allocation statistics | Read allocator statistics before and after you create and destroy staging buffers, texture images, render targets, and scene-database buffers. | Allocation counts and bytes return to the baseline. | Open | none |

## Hardware Matrix
| System | Role | Status |
|---|---|---|
| NVIDIA Vulkan GPU | Descriptor indexing, mesh shader, and zero-readback lanes | Required where available |
| AMD Vulkan GPU | Cross-vendor parity | Required for promotion |
| Integrated GPU | Portable fallback behavior | Required for promotion |
| Device with `VK_EXT_mesh_shader` | Meshlet dispatch parity | Required for meshlet rows |

## Failures
| Check | Symptom | Investigation or code item |
|---|---|---|
