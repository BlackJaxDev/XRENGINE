# Vulkan Renderer

This document describes how the Vulkan renderer is initialized, how it manages the swapchain and synchronization primitives, and how the per-frame render loop works including command buffer recording and presentation.

## Table of Contents

- [Overview](#overview)
- [Source File Inventory](#source-file-inventory)
- [Initialization](#initialization)
  - [Initialize() Sequence](#initialize-sequence)
  - [Instance Creation](#instance-creation)
  - [Validation & Debug Messenger](#validation--debug-messenger)
  - [Surface Creation](#surface-creation)
  - [Physical Device Selection](#physical-device-selection)
  - [Logical Device & Feature Chain](#logical-device--feature-chain)
  - [Command Pool](#command-pool)
  - [Swapchain & Dependent Objects](#swapchain--dependent-objects)
  - [Synchronization Objects](#synchronization-objects)
- [The Render Loop](#the-render-loop)
  - [WindowRenderCallback() — Frame-Level Flow](#windowrendercallback--frame-level-flow)
  - [Command Buffer Recording](#command-buffer-recording)
  - [The Render Graph](#the-render-graph)
  - [Frame Operations (FrameOps)](#frame-operations-frameops)
  - [Bind State Tracking](#bind-state-tracking)
- [Swapchain Management](#swapchain-management)
  - [Format Negotiation (HDR/SDR)](#format-negotiation-hdrsdr)
  - [Present Mode Selection](#present-mode-selection)
  - [Swapchain Recreation](#swapchain-recreation)
- [Synchronization Model](#synchronization-model)
- [Render Object Factory](#render-object-factory)
- [Resource Management](#resource-management)
  - [Staging Manager](#staging-manager)
  - [Pipeline Cache](#pipeline-cache)
  - [Descriptor Management](#descriptor-management)
  - [Resource Allocator](#resource-allocator)
- [ImGui Integration](#imgui-integration)
- [Advanced Features](#advanced-features)
  - [Ray Tracing](#ray-tracing)
  - [Auto-Exposure Compute](#auto-exposure-compute)
  - [Memory Decompression & Indirect Copy](#memory-decompression--indirect-copy)

---

## Overview

`VulkanRenderer` is a partial class extending `AbstractRenderer<Vk>` (where `Vk` is Silk.NET's Vulkan binding). It is split across **40+ files** organized by responsibility. Unlike the OpenGL renderer where swap is automatic, the Vulkan renderer **explicitly manages** the entire frame lifecycle: swapchain image acquisition, command buffer recording, queue submission, and presentation.

```csharp
// XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs
public unsafe partial class VulkanRenderer(XRWindow window, bool shouldLinkWindow = true)
    : AbstractRenderer<Vk>(window, shouldLinkWindow)
```

The renderer targets **Vulkan 1.3** (instance) with a **1.1 minimum** API version for the swapchain surface, and uses double-buffered frames in flight (`MAX_FRAMES_IN_FLIGHT = 2`).

---

## Source File Inventory

### Root (`XRENGINE/Rendering/API/Rendering/Vulkan/`)

| File | Purpose |
|------|---------|
| `Init.cs` | Class declaration, `Initialize()`, `CleanUp()` |
| `Drawing.cs` | Render loop (`WindowRenderCallback`), luminance readback, API object factory |
| `SwapChain.cs` | Swapchain creation/recreation, depth buffer, HDR/SDR format selection |
| `PhysicalDevice.cs` | GPU selection, suitability checks, ray tracing probe |
| `Extensions.cs` | Extension flags, required/optional device extension lists |
| `Validation.cs` | Debug messenger, validation layers |
| `FrameBufferRenderPasses.cs` | FBO-specific render pass creation/caching |
| `VulkanRenderer.State.cs` | `VulkanStateTracker`, pipeline state, barrier planner, frame ops |
| `VulkanRenderer.ImGui.cs` | Full ImGui backend (context, font atlas, pipeline, draw commands) |
| `VulkanRenderer.DebugTriangle.cs` | Debug visualization pipeline |
| `VulkanRenderGraphCompiler.cs` | Render graph compilation, topological sort, pass batching |
| `VulkanBarrierPlanner.cs` | Per-pass image/buffer barrier planning |
| `VulkanResourcePlanner.cs` | Resource allocation planning |
| `VulkanResourceAllocator.cs` | Physical image/buffer allocation |
| `VulkanStagingManager.cs` | Staging buffer pool (acquire/release/trim) |
| `VulkanPipelineCache.cs` | Persistent pipeline cache (disk save/load) |
| `VulkanDescriptorLayoutCache.cs` | Descriptor set layout caching |
| `VulkanDescriptorContracts.cs` | Descriptor binding contracts |
| `VulkanComputeDescriptors.cs` | Compute shader descriptor management |
| `VulkanFeatureProfile.cs` | Feature toggles/profile configuration |
| `VulkanShaderTools.cs` | Shader compilation utilities (GLSL → SPIR-V) |
| `VulkanAutoExposure.cs` | Auto-exposure compute resources |
| `VulkanRaytracing.cs` | Ray tracing support |
| `MemoryDecompression.cs` | NV memory decompression (RTX IO) |
| `MemoryCopyIndirect.cs` | NV indirect memory copy (RTX IO) |

### Objects (`Objects/`)

| File | Purpose |
|------|---------|
| `Instance.cs` | `CreateInstance()` — Vulkan 1.3 instance creation |
| `Surface.cs` | `CreateSurface()` — KHR surface from window |
| `LogicalDevice.cs` | `CreateLogicalDevice()` — queues, features, extensions |
| `CommandPool.cs` | Per-thread command pool creation |
| `CommandBuffers.cs` | Command buffer allocation, recording, bind tracking (~2000 lines) |
| `SyncObjects.cs` | Semaphores + fences creation |
| `RenderPasses.cs` | Swapchain render pass (color + depth) |
| `FrameBuffers.cs` | Swapchain framebuffer creation |
| `ImageViews.cs` | Swapchain image view creation |
| `DescriptorSetLayout.cs` | Global UBO descriptor layout |
| `DescriptorPool.cs` | Per-swapchain descriptor pool |
| `DescriptorSets.cs` | Per-swapchain descriptor set allocation |
| `UniformBuffers.cs` | Uniform buffer objects |
| `GraphicsPipeline.cs` | Pipeline placeholder (real pipelines are per-material) |

### Object Types (`Objects/Types/`)

18 files implementing `VkObject` hierarchy — API wrappers for materials, meshes, textures, shaders, buffers, and framebuffers.

---

## Initialization

### Initialize() Sequence

Called from `XRWindow.BeginTick()` when the window first has viewports and a world:

```csharp
public override void Initialize()
{
    if (Window?.VkSurface is null)
        throw new Exception("Windowing platform doesn't support Vulkan.");

    CreateInstance();              // 1. Vulkan instance (1.3)
    SetupDebugMessenger();         // 2. Validation layers (DEBUG only)
    CreateSurface();               // 3. KHR surface from window
    PickPhysicalDevice();          // 4. Select GPU
    CreateLogicalDevice();         // 5. Queues + features + extensions
    CreateCommandPool();           // 6. Per-thread command pool
    CreateDescriptorSetLayout();   // 7. Global UBO layout
    CreateAllSwapChainObjects();   // 8. Swapchain + all dependent resources
    CreateSyncObjects();           // 9. Semaphores + fences
}
```

### Instance Creation

From `Objects/Instance.cs`:

- Creates a Vulkan 1.3 instance with application name "XRENGINE"
- Enumerates available instance extensions via `vkEnumerateInstanceExtensionProperties`
- Adds required extensions from the Silk.NET window surface (`VK_KHR_surface`, platform-specific surface extension)
- In DEBUG builds, adds `VK_EXT_debug_utils` for validation layer reporting

### Validation & Debug Messenger

From `Validation.cs`:

- Enables `VK_LAYER_KHRONOS_validation` in debug builds
- Registers a `DebugUtilsMessengerEXT` with a callback that routes Vulkan validation messages through the engine's debug logging system
- Filters by severity (verbose, info, warning, error)

### Surface Creation

From `Objects/Surface.cs`:

```csharp
private void CreateSurface()
{
    surface = Window!.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
}
```

Uses Silk.NET's built-in VkSurface support from the GLFW window.

### Physical Device Selection

From `PhysicalDevice.cs`:

- Enumerates all physical devices via `vkEnumeratePhysicalDevices`
- Scores each device for suitability (discrete GPU preferred over integrated)
- Checks for required extension support (`VK_KHR_swapchain`)
- Verifies queue family support (graphics, present, compute, transfer)
- Probes for ray tracing capabilities if the extension is available
- Sets engine-wide capability flags (`HasNvRayTracing`, `HasVulkanRayTracing`, `HasVulkanMultiView`, etc.)

### Logical Device & Feature Chain

From `Objects/LogicalDevice.cs` (~560 lines):

The logical device is created with a carefully constructed `pNext` chain of feature structs:

```
DeviceCreateInfo
  └─ pNext → PhysicalDeviceDescriptorIndexingFeatures
       └─ pNext → PhysicalDeviceMemoryDecompressionFeaturesNV
            └─ pNext → PhysicalDeviceCopyMemoryIndirectFeaturesNV
                 └─ pNext → PhysicalDeviceBufferDeviceAddressFeatures
                      └─ pNext → PhysicalDeviceDynamicRenderingFeatures
                           └─ pNext → PhysicalDeviceVulkan11Features
                                └─ pNext → PhysicalDeviceIndexTypeUint8FeaturesEXT
```

**Features enabled:**
- `descriptorIndexing` — Runtime arrays, partial descriptor binding, update-after-bind
- `bufferDeviceAddress` — GPU buffer pointers for advanced rendering
- `dynamicRendering` — Renderpass-less rendering (Vulkan 1.3)
- `shaderDrawParameters` — Built-in draw parameter access in shaders
- `multiview` — Multi-view rendering for VR
- `indexTypeUint8` — 8-bit index buffers for memory efficiency
- `memoryDecompression` / `copyMemoryIndirect` — RTX IO support (NVIDIA)

**Queues obtained:**

| Queue | Purpose |
|-------|---------|
| `graphicsQueue` | Render command submission |
| `presentQueue` | Swapchain image presentation |
| `computeQueue` | Async compute dispatches |
| `transferQueue` | DMA transfers (texture/buffer uploads) |

**Required device extensions:**
- `VK_KHR_swapchain`

**Optional device extensions:**
- `VK_KHR_multiview`
- `VK_KHR_draw_indirect_count`
- `VK_KHR_shader_draw_parameters`
- `VK_EXT_index_type_uint8`
- `VK_EXT_descriptor_indexing`
- `VK_KHR_dynamic_rendering`
- `VK_NV_memory_decompression`
- `VK_NV_copy_memory_indirect`

### Command Pool

From `Objects/CommandPool.cs`:

```csharp
private void CreateCommandPool()
{
    // Created with ResetCommandBufferBit | TransientBit
    // Allows individual command buffer reset and optimizes for short-lived buffers
}
```

The command pool is associated with the graphics queue family. The `TransientBit` flag tells the driver that command buffers will be short-lived and reset frequently (every frame).

### Swapchain & Dependent Objects

`CreateAllSwapChainObjects()` creates the entire swapchain dependency chain in order:

```
CreateAllSwapChainObjects()
  ├─ CreateSwapChain()        — Format negotiation, present mode, extent
  ├─ CreateImageViews()       — One VkImageView per swapchain image
  ├─ [Detect depth format]    — Find best D24/D32/D24S8 format
  ├─ CreateRenderPass()       — Single subpass: 1 color + 1 depth attachment
  ├─ CreateDepth()            — Depth image + view + memory
  ├─ CreateFramebuffers()     — One per swapchain image (color view + depth view)
  ├─ CreateDescriptorPool()   — Per-swapchain descriptor pool
  ├─ CreateDescriptorSets()   — Per-swapchain descriptor sets
  └─ CreateCommandBuffers()   — One primary command buffer per swapchain image
```

### Synchronization Objects

From `Objects/SyncObjects.cs`:

```csharp
Semaphore[] imageAvailableSemaphores;  // Per in-flight frame (2)
Semaphore[] renderFinishedSemaphores;  // Per in-flight frame (2)
Fence[]     inFlightFences;            // Per in-flight frame (2), created SIGNALED
Fence[]     imagesInFlight;            // Per swapchain image (tracks which frame owns it)
```

Fences are created in the signaled state so the first frame doesn't deadlock waiting for a "previous" frame that never existed.

---

## The Render Loop

### WindowRenderCallback() — Frame-Level Flow

From `Drawing.cs`, this is the core per-frame method (~180 lines). Unlike OpenGL where this is empty, the Vulkan renderer performs all explicit GPU work here:

```
WindowRenderCallback(double delta)
│
├─ 1. MarkCommandBuffersDirty()
│     Force re-record every frame (deferred ops model — frame ops are drained during recording)
│
├─ 2. Check surface/swapchain size mismatch
│     Compare live framebuffer size to swapchain extent
│     If mismatched → schedule RecreateSwapChain()
│
├─ 3. Handle pending swapchain recreation
│     If _frameBufferInvalidated → RecreateSwapChain()
│
├─ 4. WaitForFences(inFlightFences[currentFrame])
│     Block until the GPU finishes the previous frame in this slot
│
├─ 5. AcquireNextImage(imageAvailableSemaphores[currentFrame]) → imageIndex
│     Request the next swapchain image
│     ErrorOutOfDateKhr → RecreateSwapChain() + return
│     SuboptimalKhr → RecreateSwapChain() + return
│
├─ 6. Wait on imagesInFlight[imageIndex] if another frame slot owns it
│     Prevents two in-flight frames from using the same swapchain image
│
├─ 7. imagesInFlight[imageIndex] = inFlightFences[currentFrame]
│     Mark this swapchain image as owned by the current frame slot
│
├─ 8. EnsureCommandBufferRecorded(imageIndex)
│     Record all draw commands, barriers, render passes into the command buffer
│
├─ 9. QueueSubmit
│     ├─ Wait: imageAvailableSemaphores[currentFrame] @ ColorAttachmentOutput stage
│     ├─ Execute: commandBuffers[imageIndex]
│     ├─ Signal: renderFinishedSemaphores[currentFrame]
│     └─ Fence: inFlightFences[currentFrame]
│
├─ 10. StagingManager.Trim()
│      Release idle staging buffers to prevent unbounded memory growth
│
├─ 11. QueuePresent
│      ├─ Wait: renderFinishedSemaphores[currentFrame]
│      └─ Present swapchain image to the display
│
├─ 12. Handle out-of-date → RecreateSwapChain()
│
└─ 13. currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT
```

### Command Buffer Recording

`RecordCommandBuffer(uint imageIndex)` from `Objects/CommandBuffers.cs` (~2000 lines):

```
RecordCommandBuffer(imageIndex)
│
├─ Reset command buffer + transient resources
├─ BeginCommandBuffer (one-time submit optimization)
│
├─ DrainFrameOps()
│   Dequeue all pending FrameOps from the concurrent queue
│
├─ VulkanRenderGraphCompiler.SortFrameOps()
│   Topologically sort ops by pass dependencies
│
├─ Emit swapchain image barriers (Undefined → ColorAttachment)
│
├─ For each operation (in sorted order):
│   ├─ Emit pass barriers (image transitions, buffer barriers, memory barriers)
│   ├─ Begin/End render pass as targets change
│   └─ Execute op:
│       ├─ ClearOp          — vkCmdClearColorImage / vkCmdClearDepthStencilImage
│       ├─ MeshDrawOp       — Bind pipeline, descriptors, vertex/index buffers, draw
│       ├─ BlitOp           — vkCmdBlitImage
│       ├─ ComputeDispatchOp — Bind compute pipeline, dispatch
│       └─ (other op types)
│
├─ Render ImGui overlay
│   RenderImGui(commandBuffer, imageIndex)
│
└─ EndCommandBuffer
```

### The Render Graph

`VulkanRenderGraphCompiler.cs` handles dependency-aware pass ordering:

- **Topological sort** of render pass metadata based on resource dependencies
- **Pass batching** — groups compatible passes that share the same stage and attachment signature into `VulkanCompiledPassBatch` objects for reduced render pass transitions
- **Scheduling identity** — frame ops are sorted by `(SchedulingIdentity, PassOrder, OriginalIndex)` to maintain deterministic ordering while respecting dependencies

### Frame Operations (FrameOps)

The engine's render pipeline doesn't directly record Vulkan commands. Instead, it enqueues **frame operations** (`FrameOp`) during viewport rendering. These are deferred command descriptions that the Vulkan backend consumes during command buffer recording:

```
Viewport rendering (runs during RenderWindowViewports)
  ├─ Enqueues MeshDrawOp, ClearOp, BlitOp, ComputeDispatchOp, etc.
  └─ These accumulate in a concurrent queue

Command buffer recording (runs during WindowRenderCallback)
  ├─ DrainFrameOps() — takes all queued ops
  ├─ Sort by render graph dependencies
  └─ Record into Vulkan command buffer
```

This decoupling means the viewport rendering code is API-agnostic — it describes *what* to render, and the Vulkan backend determines *how* to record it.

### Bind State Tracking

The `CommandBufferBindState` tracks currently bound state to avoid redundant Vulkan calls:

- **Graphics/compute pipelines** — only rebind when pipeline handle changes
- **Descriptor sets** — signature-based comparison, only rebind when bindings change
- **Vertex/index buffers** — tracked per-slot to minimize redundant binds

This is important for Vulkan performance since every state change requires a command buffer entry, unlike OpenGL where the driver can often deduplicate internally.

---

## Swapchain Management

### Format Negotiation (HDR/SDR)

From `SwapChain.cs`, the renderer negotiates surface format based on HDR preference:

**HDR preferences (highest to lowest priority):**

| Format | Color Space |
|--------|-------------|
| `R16G16B16A16_SFLOAT` | `EXTENDED_SRGB_LINEAR_EXT` |
| `R16G16B16A16_SFLOAT` | `DISPLAY_P3_NONLINEAR_EXT` |
| `R16G16B16A16_SFLOAT` | `HDR10_ST2084_EXT` |
| `A2B10G10R10_UNORM_PACK32` | `HDR10_ST2084_EXT` |
| `A2R10G10B10_UNORM_PACK32` | `HDR10_ST2084_EXT` |

**SDR preferences:**

| Format | Color Space |
|--------|-------------|
| `B8G8R8A8_SRGB` | `SRGB_NONLINEAR_KHR` |
| `R8G8B8A8_SRGB` | `SRGB_NONLINEAR_KHR` |
| `B8G8R8A8_UNORM` | `SRGB_NONLINEAR_KHR` |
| `R8G8B8A8_UNORM` | `SRGB_NONLINEAR_KHR` |

### Present Mode Selection

```
Preferred: Mailbox (triple-buffered, lowest latency without tearing)
Fallback:  FIFO (V-Sync, guaranteed available on all platforms)
```

### Swapchain Recreation

Swapchain recreation is triggered by:
- `VK_ERROR_OUT_OF_DATE_KHR` from `AcquireNextImage` or `QueuePresent`
- `VK_SUBOPTIMAL_KHR` from either operation
- Proactive size mismatch detection (live framebuffer size ≠ swapchain extent)
- Window resize events (via `_frameBufferInvalidated` flag)

The recreation process:

```csharp
private void RecreateSwapChain()
{
    // Wait for non-zero window size (handles minimize)
    while (framebufferSize.X == 0 || framebufferSize.Y == 0)
    {
        framebufferSize = Window.FramebufferSize;
        Window.DoEvents();
    }

    DeviceWaitIdle();                  // Wait for all GPU work to finish
    DestroyAllSwapChainObjects();      // Tear down old swapchain + dependents
    CreateAllSwapChainObjects();       // Rebuild with new dimensions
    imagesInFlight = new Fence[swapChainImages.Length];  // Reset fence tracking
}
```

`DestroyAllSwapChainObjects()` tears down in reverse dependency order:
1. Debug triangle resources
2. Swapchain ImGui resources
3. Depth buffer
4. Command buffers
5. Framebuffers
6. FBO render passes
7. Render passes
8. Image views
9. Swapchain itself
10. Descriptor pool

---

## Synchronization Model

The Vulkan renderer uses a double-buffered frame model:

```
                Frame Slot 0                    Frame Slot 1
                ┌─────────────┐                 ┌─────────────┐
                │ inFlight    │                 │ inFlight    │
                │ Fence[0]    │                 │ Fence[1]    │
                │             │                 │             │
                │ imgAvail    │                 │ imgAvail    │
                │ Sem[0]      │                 │ Sem[1]      │
                │             │                 │             │
                │ renderDone  │                 │ renderDone  │
                │ Sem[0]      │                 │ Sem[1]      │
                └─────────────┘                 └─────────────┘

Timeline:
  Frame 0: Wait fence[0] → Acquire → Record → Submit (signal fence[0]) → Present
  Frame 1: Wait fence[1] → Acquire → Record → Submit (signal fence[1]) → Present
  Frame 2: Wait fence[0] → ...  (fence[0] is now signaled from Frame 0)
```

**Per-frame semaphores:**
- `imageAvailableSemaphores[i]` — Signaled by `AcquireNextImage`, waited on by `QueueSubmit`
- `renderFinishedSemaphores[i]` — Signaled by `QueueSubmit`, waited on by `QueuePresent`

**Fences:**
- `inFlightFences[i]` — CPU-GPU sync per frame slot; CPU waits before reusing resources
- `imagesInFlight[imageIndex]` — Tracks which frame slot currently owns each swapchain image, preventing two frame slots from recording to the same image simultaneously

---

## Render Object Factory

`CreateAPIRenderObject()` in `Drawing.cs` maps engine-generic render objects to Vulkan-specific wrappers:

| Generic (Engine) | Vulkan Wrapper |
|-------------------|----------------|
| `XRMaterial` | `VkMaterial` |
| `XRShader` | `VkShader` |
| `XRMeshRenderer.BaseVersion` | `VkMeshRenderer` |
| `XRRenderProgram` | `VkRenderProgram` |
| `XRDataBuffer` | `VkDataBuffer` |
| `XRFrameBuffer` | `VkFrameBuffer` |
| `XRTexture2D` | `VkTexture2D` |
| `XRTexture3D` | `VkTexture3D` |
| `XRTextureCube` | `VkTextureCube` |
| `XRTexture2DArray` | `VkTexture2DArray` |
| `XRRenderBuffer` | `VkRenderBuffer` |
| `XRRenderQuery` | `VkRenderQuery` |
| `XRSampler` | `VkSampler` |

These wrappers manage Vulkan-specific resources (descriptor sets, pipeline layouts, image layouts, etc.) and are cached on the base `AbstractRenderer`.

---

## Resource Management

### Staging Manager

`VulkanStagingManager.cs` manages a pool of host-visible staging buffers for CPU→GPU transfers:

- **Acquire**: Get a staging buffer of at least the requested size
- **Release**: Return a buffer to the pool for reuse
- **Trim**: Called each frame after queue submission to free idle buffers, preventing unbounded memory growth

### Pipeline Cache

`VulkanPipelineCache.cs` provides persistent pipeline caching:

```
Save location: %LOCALAPPDATA%/XREngine/Vulkan/PipelineCache/pcache_v{vendor}_{device}_{driver}_{api}.bin
```

- Loaded at device creation to skip pipeline compilation on subsequent runs
- Saved during `CleanUp()` to persist newly compiled pipelines
- Cache key includes vendor, device, driver version, and API version to invalidate on driver updates

### Descriptor Management

Multiple files handle descriptor set management:

- **`VulkanDescriptorLayoutCache`** — Caches `VkDescriptorSetLayout` objects to avoid duplicate creation
- **`VulkanDescriptorContracts`** — Defines descriptor binding contracts (what resources a shader expects)
- **`VulkanComputeDescriptors`** — Specialized descriptor management for compute shaders
- **Per-swapchain descriptor pools/sets** — Allocated in `CreateAllSwapChainObjects()`, rebuilt on swapchain recreation

### Resource Allocator

`VulkanResourceAllocator.cs` manages physical GPU resource allocation:

- Allocates `VkImage` and `VkBuffer` objects with appropriate memory types
- Uses `FindMemoryType()` to select appropriate memory heaps (device-local, host-visible, host-coherent)
- Tracks all allocations for cleanup during shutdown or swapchain recreation

> **Note:** The codebase acknowledges that per-object `vkAllocateMemory` is not ideal for production. The `maxMemoryAllocationCount` limit can be as low as 4096. The resource allocator is designed to eventually use sub-allocation from larger memory blocks.

---

## ImGui Integration

`VulkanRenderer.ImGui.cs` (~1190 lines) provides a complete Vulkan ImGui backend:

### Architecture

- `VulkanImGuiBackend` implements `IImGuiRendererBackend`
- Draw data is **snapshotted** into `ImGuiFrameSnapshot` (deep copy of vertex/index/command data) during `Render()` and consumed asynchronously during command buffer recording
- This decoupling is necessary because ImGui draw data is only valid during the `Render()` call, but Vulkan command buffer recording happens later

### Pipeline

```csharp
EnsureImGuiPipeline()
  ├─ Vertex shader: GLSL 450 → SPIR-V (compiled at runtime)
  ├─ Fragment shader: GLSL 450 → SPIR-V
  ├─ Push constants: scale + translate (orthographic projection, 16 bytes)
  ├─ Descriptor set: single CombinedImageSampler (font atlas)
  ├─ Dynamic state: viewport + scissor
  ├─ Blending: SrcAlpha / OneMinusSrcAlpha (standard alpha blend)
  └─ Depth test: disabled
```

### Font Atlas Upload

- Built CPU-side during context creation
- Uploaded to GPU via staging buffer → `vkCmdCopyBufferToImage`
- Transitioned to `ShaderReadOnlyOptimal` layout

### Draw Recording

```
RenderImGui(commandBuffer, imageIndex)
  ├─ Map vertex/index buffers (host-visible + host-coherent)
  ├─ Begin render pass (swapchain render pass)
  ├─ Bind ImGui pipeline
  ├─ Set push constants (projection matrix)
  ├─ For each command list:
  │    ├─ Copy vertex/index data to mapped buffers
  │    └─ For each draw command:
  │         ├─ Set scissor rect
  │         ├─ Bind texture descriptor set
  │         └─ vkCmdDrawIndexed
  └─ End render pass
```

### Custom Textures

The ImGui integration supports registering custom textures (e.g., scene render targets displayed in editor panels). Each custom texture gets a dedicated descriptor set allocated from a per-texture pool.

---

## Advanced Features

### Ray Tracing

`VulkanRaytracing.cs` provides ray tracing support when `VK_KHR_ray_tracing_pipeline` or `VK_NV_ray_tracing` is available:

- Probed during physical device selection
- Sets `Engine.Rendering.State.HasVulkanRayTracing` / `HasNvRayTracing` flags
- Acceleration structure building and ray tracing pipeline creation

### Auto-Exposure Compute

`VulkanAutoExposure.cs` implements GPU-based auto-exposure via compute shaders:

- Computes scene luminance histogram on the GPU
- Outputs exposure value for tone mapping
- Resources are created/destroyed with the renderer lifecycle

### Memory Decompression & Indirect Copy

NVIDIA RTX IO extensions for streaming:

- `VK_NV_memory_decompression` — GPU-accelerated memory decompression for compressed asset streaming
- `VK_NV_copy_memory_indirect` — Indirect memory copies driven by GPU-generated commands

Both are optional and only enabled when the extensions are available on the physical device.

---

## CleanUp()

Cleanup runs in reverse initialization order:

```csharp
public override void CleanUp()
{
    DeviceWaitIdle();                       // Ensure GPU is idle

    DestroyAutoExposureComputeResources();  // Compute resources
    DisposeImGuiResources();                // ImGui pipeline, font atlas, buffers
    DestroyAllSwapChainObjects();           // Swapchain + all dependents
    DestroyDescriptorSetLayout();           // Global descriptor layout
    _resourceAllocator.DestroyPhysicalImages(this);
    _resourceAllocator.DestroyPhysicalBuffers(this);
    _stagingManager.Destroy(this);          // Staging buffer pool

    DestroySyncObjects();                   // Semaphores + fences
    DestroyCommandPool();                   // Command pool
    DestroyLogicalDevice();                 // Logical device + queues
    DestroyValidationLayers();              // Debug messenger
    DestroySurface();                       // KHR surface
    DestroyInstance();                      // Vulkan instance
}
```

---

## See Also

- [Window Creation & Renderer Initialization](window-creation-and-renderer-init.md) — How windows and renderers are created at startup
- [OpenGL Renderer](opengl-renderer.md) — OpenGL-specific initialization and render loop
- [Rendering Code Map](RenderingCodeMap.md) — Full source file inventory
