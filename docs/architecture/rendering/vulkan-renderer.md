# Vulkan Renderer

This document describes how the Vulkan renderer is initialized, how it manages the swapchain and synchronization primitives, and how the per-frame render loop works including command buffer recording and presentation.

## Table of Contents

- [Overview](#overview)
- [Settings Ownership](#settings-ownership)
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
  - [Bindless Material Texture Table](#bindless-material-texture-table)
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
// XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs
public unsafe partial class VulkanRenderer(XRWindow window, bool shouldLinkWindow = true)
    : AbstractRenderer<Vk>(window, shouldLinkWindow)
```

The renderer targets **Vulkan 1.3** (instance) with a **1.1 minimum** API version for the swapchain surface, and uses double-buffered frames in flight (`MAX_FRAMES_IN_FLIGHT = 2`).

---

## Settings Ownership

Vulkan-specific runtime defaults live under `Engine.Rendering.Settings.Vulkan`:

- `Vulkan.Startup.FallbackPolicy` controls whether startup may retry OpenGL after a requested Vulkan window fails.
- `Vulkan.TargetMode.RenderTargetMode` selects `Auto`, `DynamicRendering`, or `LegacyRenderPass`. `XRE_VK_RENDER_TARGET_MODE` has highest priority for the current process.
- `Vulkan.GpuDriven` owns the GPU-driven profile and optional geometry-fetch strategy.
- `Vulkan.Descriptors` owns descriptor indexing, the bindless material table, bindless material policy, and descriptor-contract validation.
- `Vulkan.Synchronization` owns queue-overlap policy.
- `Vulkan.Robustness` remains the allocator/synchronization/descriptor-update migration owner.

Flat `Engine.Rendering.EngineSettings` properties such as
`VulkanGpuDrivenProfile`, `VulkanQueueOverlapMode`,
`EnableVulkanDescriptorIndexing`, and `VulkanRenderTargetMode` remain
compatibility aliases that forward to those grouped owners. Runtime renderer
code should read through `RuntimeEngine.EffectiveSettings` or
`Engine.EffectiveSettings.RenderSnapshot.Vulkan` so project/user cascade logic
stays outside backend classes.

Project overrides live under `GameStartupSettings.Rendering.Vulkan` and
`GameStartupSettings.Rendering.Common`; user fallback overrides live under
`UserSettings.Rendering.Common`. Editor-only Vulkan diagnostics are exposed as
`EditorPreferences.Diagnostics.Vulkan` while the existing serialized
`EditorDebugOptions` fields remain compatibility storage.

---

## Source File Inventory

The Vulkan renderer lives under
`XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/` and uses the
same responsibility-based backend taxonomy described in
[Rendering Code Map](code-map.md). Namespaces intentionally remain
`XREngine.Rendering.Vulkan`; folder names are for ownership and navigation.

| Folder | Purpose |
| --- | --- |
| `Bootstrap/` | Instance, surface, physical/logical device setup, extension probes, validation, OBS hook compatibility, and renderer initialization. |
| `Frame/` | Swapchain creation/recreation, per-frame acquire/submit/present flow, synchronization objects, frame timing, and deferred resource retirement. |
| `Commands/` | Command pools, command-buffer allocation/recording, frame-op signatures and diagnostics, blits, readbacks, indirect draw, render-state mutation, command-chain lowering, and queue-overlap policy. |
| `RenderGraph/` | Render-graph compilation, barrier planning, resource planning, and the renderer's resource-planner state refresh path. |
| `Resources/` | Resource allocator, resource registration, framebuffer/image-view helpers, placeholder textures, dynamic uniform and scene database buffers, upload/staging services, and Vulkan memory allocator backends. |
| `Descriptors/` | Descriptor pools, sets, layouts, update templates, descriptor contracts, image layout policy, immutable samplers, compute descriptors, and bindless material texture tables. |
| `Pipelines/` | Legacy render-pass helpers, graphics pipeline setup, render target mode resolution, pipeline cache, compile queue, prewarm database, and graphics pipeline library cache. |
| `Shaders/` | Shader artifact cache, auto-uniform rewriting, source fixups, transform-feedback translation, shaderc compilation, SPIR-V reflection, and shader tool shared types. |
| `Features/` | Auto exposure, feature profile, meshlets, ray tracing, RTX IO memory copy/decompression, texture streaming hooks, Streamline interop, DLSS command buffers, and the OpenGL-to-Vulkan upscale bridge. |
| `UI/` | Vulkan ImGui backend integration. |
| `BackendObjects/` | Vulkan wrappers around engine resources: buffers, textures, framebuffers, materials, mesh renderers, render programs, shaders, queries, samplers, and shared wrapper base types. |
| `Types/` | Small backend value types, enums, and interop helpers such as queue-family indices, format conversions, transform-feedback metadata, and extension structs. |

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
    SetupDebugMessenger();         // 2. Validation layers (opt-in via XRE_VULKAN_VALIDATION)
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

From `Bootstrap/VulkanRenderer.Instance.cs`:

- Creates a Vulkan 1.3 instance with application name "XRENGINE"
- Enumerates available instance extensions via `vkEnumerateInstanceExtensionProperties`
- Adds required extensions from the Silk.NET window surface (`VK_KHR_surface`, platform-specific surface extension)
- When validation layers are enabled, adds `VK_EXT_debug_utils` for validation layer reporting

### Validation & Debug Messenger

From `Validation.cs`:

- `VK_LAYER_KHRONOS_validation` is disabled by default in all configurations because it multiplies primary command recording cost on the render thread; opt in by setting `XRE_VULKAN_VALIDATION=1` (any value other than `0`/`false`/`off`/`no` enables it)
- Registers a `DebugUtilsMessengerEXT` with a callback that routes Vulkan validation messages through the engine's debug logging system
- Filters by severity (verbose, info, warning, error)

### Surface Creation

From `Bootstrap/VulkanRenderer.Surface.cs`:

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

From `Bootstrap/VulkanRenderer.LogicalDevice.cs` (~560 lines):

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
- `VK_KHR_external_memory`
- `VK_KHR_external_memory_win32`
- `VK_KHR_external_semaphore`
- `VK_KHR_external_semaphore_win32`
- `VK_KHR_draw_indirect_count`
- `VK_KHR_shader_draw_parameters`
- `VK_EXT_index_type_uint8`
- `VK_EXT_descriptor_indexing`
- `VK_KHR_dynamic_rendering`
- `VK_NV_memory_decompression`
- `VK_NV_copy_memory_indirect`

**OBS hook compatibility:**

Feature guide: [Vulkan OBS Hook Compatibility](../../developer-guides/rendering/vulkan-obs-hook-compatibility.md).

The Windows OBS Vulkan game-capture path is provided by OBS as the implicit `VK_LAYER_OBS_HOOK` layer. XRENGINE does not ship that layer; instead, startup inspects enabled Windows implicit-layer registry entries and manifests without calling the Vulkan loader, then checks whether the selected device can support the layer's shared-texture path:

- the engine leaves the implicit OBS layer enabled by default (`XRE_VK_OBS_HOOK=Auto`)
- `XRE_VK_OBS_HOOK=Disable` sets `DISABLE_VULKAN_OBS_CAPTURE=1` before `vkCreateInstance`, useful when debugging validation or RenderDoc captures affected by OBS interception
- `XRE_VK_OBS_HOOK=Require` fails startup if `VK_LAYER_OBS_HOOK` is missing, disabled, or the selected Vulkan device cannot import D3D11 texture KMT shared memory through `VK_KHR_external_memory_win32`
- swapchain images are created with `VK_IMAGE_USAGE_TRANSFER_SRC_BIT`, matching the access OBS needs when it captures from `vkQueuePresentKHR`

**Render target mode:**

After logical-device feature resolution, Vulkan selects a render target path
from `RuntimeEngine.EffectiveSettings.VulkanRenderTargetMode`; the
`XRE_VK_RENDER_TARGET_MODE` environment variable overrides the persisted
setting for the current process:

| Value | Behavior |
|-------|----------|
| `Auto` | Uses dynamic rendering when `dynamicRendering` is supported; otherwise uses the retained legacy render-pass/framebuffer path. |
| `DynamicRendering` | Requires dynamic rendering support and fails visibly at initialization if unavailable. |
| `LegacyRenderPass` | Routes swapchain, FBO, and ImGui graphics targets through the retained `VkRenderPass` / `VkFramebuffer` path. |

Startup diagnostics report the requested mode, resolved mode, and dynamic-rendering feature support.

**Bindless material mode:**

Vulkan material bindless mode is selected by
`Engine.Rendering.Settings.Vulkan.Descriptors.BindlessMaterialMode` or the
`XRE_VULKAN_BINDLESS_MATERIAL_MODE` environment variable:

| Value | Behavior |
|-------|----------|
| `Auto` | Uses the descriptor-indexed material path when the feature profile and device capabilities allow it. |
| `Disabled` | Disables the Vulkan global material texture table and routes material-table shaders through non-bindless rows. |
| `Required` | Fails visibly when descriptor indexing, runtime descriptor arrays, partially-bound descriptors, or update-after-bind are unavailable. |
| `Diagnostics` | Enables the same table path as Auto and keeps extra diagnostics/warnings visible. |

Startup logs include `Capability.BindlessMaterialTextures` with mode, tier, capacity, `tableReady`, `shaderReady`, `drawPathReady`, and a reason string when any tier is unavailable.

### Command Pool

From `Commands/VulkanRenderer.CommandPool.cs`:

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

In dynamic-rendering mode, `CreateRenderPass()` leaves the swapchain render-pass handles empty and `CreateFramebuffers()` creates only placeholder slots for command-buffer ownership. In legacy mode, those calls create the retained Vulkan `VkRenderPass` and `VkFramebuffer` objects used by the fallback command-buffer branch.

### Synchronization Objects

From `Frame/VulkanRenderer.SyncObjects.cs`:

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

`RecordCommandBuffer(uint imageIndex)` from `Commands/VulkanRenderer.CommandBufferRecording.cs`:

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

Graphics targets are selected through the resolved render target mode. Dynamic mode records swapchain and `XRFrameBuffer` targets with `vkCmdBeginRendering` / `vkCmdEndRendering`; legacy mode records through `vkCmdBeginRenderPass` / `vkCmdEndRenderPass`. Dynamic FBO scopes reuse `VkFrameBuffer` attachment signatures for image views, formats, load/store ops, clear values, and explicit begin/end layout barriers.

#### Feature-Flagged Command Chains

Vulkan also has a command-chain path that lowers the sorted `FrameOp` stream into reusable packet schedules before recording. It is guarded by environment flags while the legacy frame-op recorder remains the default fallback:

| Flag | Purpose |
| `XRE_VULKAN_COMMAND_CHAINS=1` | Enables command-chain lowering, secondary mesh command buffers, chain cache lookup, and primary schedule signatures. |
| `XRE_VULKAN_COMMAND_CHAINS_SINGLE_THREAD=1` | Forces deterministic single-thread chain processing for bisection. |
| `XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING=1` | Keeps command-chain lowering enabled while disabling worker dispatch. |
| `XRE_VULKAN_PARALLEL_PACKET_BUILD=1` | Builds packet snapshots in parallel and validates them against the sequential result in validation mode. |
| `XRE_VULKAN_COMMAND_CHAIN_VALIDATE=1` | Enables expensive schedule, view-specialization, queue-schedule, and signature checks. |
| `XRE_VULKAN_COMMAND_CHAIN_TRACE=1` | Emits throttled first-dirty-reason and schedule diagnostics. |
| `XRE_VULKAN_COMMAND_CHAIN_MESH_SECONDARY_NOOP=1` | Diagnostic mode that records secondary mesh chains without draw payloads. |
| `XRE_VULKAN_COMMAND_CHAIN_MULTI_QUEUE=1` | Builds and validates queue-schedule sidecar metadata; execution still falls back to the graphics queue. |

Command-chain recording keeps the render graph as the source of ordering truth:

```text
DrainFrameOps()
  -> SortFrameOps()
  -> Split dynamic UI/text overlay ops
  -> Prepare and freeze the Vulkan resource plan revision
  -> Lower static and volatile ops into VisibilityPacket/RenderPacket arrays
  -> Build RenderPassChainGroup schedule by target/pass/view/volatility
  -> Refresh reusable chain frame data or record dirty secondary command buffers
  -> Record/reuse the primary command buffer from the chain group signature
```

The cache is per swapchain image/frame slot and keyed by `CommandChainKey`, which includes render target identity, pass index, view key, volatility, structural signature, and descriptor/resource generation inputs. Static scene chains can refresh camera/model/material frame data without re-recording secondary command buffers. Dynamic UI text and profiler/overlay work is isolated into volatile chains so it does not dirty static scene chains.

Primary command-buffer reuse is tracked separately from secondary reuse. A primary can be reused when the pass-group layout, schedule signature, and ordered secondary command-buffer handles are unchanged. When only frame data changes, the chain metrics report frame-data refreshes rather than command-buffer records. The profiler/runtime stat surface exposes scheduled, recorded, reused, refreshed, dirty-reason, secondary-count, primary-record/reuse, worker-record, and render-thread-wait metrics.

The worker infrastructure owns per-worker graphics/compute command pools, scratch state, bind-state containers, cancellation, and teardown. Current worker execution is conservative: inheritance-sensitive graphics secondary recording remains on the validated primary-compatible path, while worker timing and scheduling boundaries are available for expansion and measurement. Worker state is cancelled on command-buffer destruction and destroyed before the main command pools.

VR and shadow passes use explicit command-chain view specialization. VR eye chains use left/right eye indices, with a multiview sentinel reserved for single-pass stereo. Shadow chains include light identity, cascade/face identity, target identity, and shadow atlas/fallback state in their structural signatures so atlas repacks or stale-tile fallback modes dirty only the affected chains.

Optional multi-queue scheduling is metadata-only in this phase. The queue scheduler classifies graphics, secondary graphics, compute, and transfer eligibility, validates dependency/timeline data in validation mode, then emits a graphics fallback node for actual execution.

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

Buffer-device-address staging is only requested when the NV indirect-copy upload path is explicitly enabled through `CanUseNvIndirectBufferCopyUploads`. When that path is disabled, normal staging uploads avoid `ShaderDeviceAddressBit` and the extra memory requirements that come with it.

### Pipeline Cache

`VulkanPipelineCache.cs` provides persistent pipeline caching:

```
Save location: %LOCALAPPDATA%/XREngine/Vulkan/PipelineCache/pcache_v{vendor}_{device}_{driver}_{api}.bin
```

- Loaded at device creation to skip pipeline compilation on subsequent runs
- Saved during `CleanUp()` to persist newly compiled pipelines
- Auto-saved after batches of newly created graphics/compute pipelines during editor sessions
- Cache key includes vendor, device, driver version, and API version to invalidate on driver updates
- Logs the cache path, loaded byte count, saved byte count, and save duration

Mesh renderers also keep a small per-renderer cache of generated combined `XRRenderProgram` instances keyed by material shader revision, Vulkan feature axes, shader stages, and generated vertex source identity. Pipeline invalidation can retire/recreate `VkPipeline` objects, but it must not destroy and relink the same shader program just because geometry, descriptors, or fixed-function state changed.

### SPIR-V Shader Artifact Cache

`VulkanShaderArtifactCache.cs` provides a persistent cache for the source-to-SPIR-V stage:

```
Save location: Build/Cache/Vulkan/ShaderArtifacts/{artifactIdentity}.spv
Metadata:      Build/Cache/Vulkan/ShaderArtifacts/{artifactIdentity}.spv.json
```

- `VulkanShaderCompiler.Prepare()` resolves includes, optimizes source, applies Vulkan shader rewrites, and computes the rewritten source used for identity validation.
- `VulkanShaderCompiler.CompilePrepared()` runs shaderc only after the artifact cache misses or rejects an entry.
- `VkShader` rehydrates cached SPIR-V, descriptor binding metadata, vertex input locations, and the rewritten-source identity, then creates `VkShaderModule` on the Vulkan device thread.
- Metadata carries a schema version and runtime/compiler fingerprint so stale, corrupt, or incompatible entries are deleted instead of reused.
- Cold compile misses write `.spv` payloads asynchronously, similar to the OpenGL binary shader cache pattern.
- For `XRMeshRenderer.GenerateAsync` renderers, CPU shader preparation and shaderc compilation run on a worker task. Command-buffer recording sees the renderer as pending until the worker artifact is ready and the device-thread module/layout work completes.

### Pipeline Prewarm Manifest

`VulkanPipelinePrewarmDatabase.cs` records semantic pipeline misses for startup prewarm analysis:

- Capture is enabled with `XRE_VK_PIPELINE_PREWARM_CAPTURE=1`.
- The manifest is stored under `%LOCALAPPDATA%/XREngine/Vulkan/PipelinePrewarm/`.
- Persistent keys use shader artifact identities, descriptor/vertex/material/pass fingerprints, fixed-function state, ordered dynamic color formats, depth/stencil formats, and render-pass semantic signatures.
- Persistent keys do not include transient Vulkan handles such as `VkRenderPass`, `VkShaderModule`, or `VkPipelineLayout`.
- The manifest currently records and classifies known startup misses. Concrete ahead-of-first-draw pipeline creation still requires scene/material-specific prewarm orchestration.

### Descriptor Management

Multiple files handle descriptor set management:

- **`VulkanDescriptorLayoutCache`** — Caches `VkDescriptorSetLayout` objects to avoid duplicate creation
- **`VulkanDescriptorContracts`** — Defines descriptor binding contracts (what resources a shader expects)
- **`VulkanBindlessMaterialDescriptors`** — Reserves the global material texture descriptor array at set 2 / binding 31
- **`VulkanComputeDescriptors`** — Specialized descriptor management for compute shaders
- **Per-swapchain descriptor pools/sets** — Allocated in `CreateAllSwapChainObjects()`, rebuilt on swapchain recreation

Compute auto-uniform and unresolved fallback uniform buffers are cached per program/image/set/binding. They are updated in place and destroyed with the program instead of being allocated as one-frame transient buffers.

### Bindless Material Texture Table

Vulkan bindless material texturing uses one renderer-owned global descriptor table:

- Descriptor array: `XR_BindlessMaterialTextures`, `set = 2`, `binding = 31`.
- Descriptor type: `CombinedImageSampler`.
- Descriptor index `0`: reserved null/fallback. Generated material-table shaders return the row fallback constant when an index is zero.
- Capacity: clamped to `VulkanBindlessMaterialDescriptors.MaxTextureDescriptorCount`, `MaxDescriptorSetSampledImages`, and `MaxPerStageDescriptorSampledImages`.
- Layout/pool flags: update-after-bind, partially-bound, and variable descriptor count are used only when enabled device features support them.
- Slot lifetime: material textures receive stable descriptor slots keyed by `XRTexture`, track generation and last-used frame, and retire after the in-flight safety delay.
- Updates: dirty descriptor slots are flushed without rewriting the full table, using pooled update scratch arrays.

The material row contract is backend-neutral. OpenGL rows store indices into `MaterialTextureHandleTable`; Vulkan rows store descriptor indices directly. `GPUMaterialTextureReference` carries the backend-specific payload and `GPUMaterialTable` packs the shader-facing row.

The descriptor-index shader variant emits `GL_EXT_nonuniform_qualifier` and samples `XR_BindlessMaterialTextures[nonuniformEXT(index)]`. It does not emit OpenGL bindless extensions or `uint64_t` sampler handles.

`VulkanRenderer.BindlessMaterialCapability` exposes the current tier:

| Tier | Meaning |
| `DescriptorIndexingUnavailable` | Required descriptor-indexing features are missing or disabled. |
| `DescriptorIndexingReady` | Device/profile prerequisites are available. |
| `GlobalMaterialTextureTableReady` | The global descriptor table, pool, layout, and set are allocated. |
| `BindlessMaterialTableShaderReady` | Generated Vulkan material-table shaders can use descriptor indices. |
| `BindlessMaterialDrawPathReady` | Reserved for the production Vulkan material draw path once the broader GPU dispatch gate is enabled. |

Current diagnostics expose descriptor capacity, used slots, dirty slots, descriptor writes, slot retirements, and fallback references through renderer properties. Descriptor fallback/binding failures are also reported through the Vulkan descriptor fallback/failure stats.

Troubleshooting bindless material textures:

- If `Capability.BindlessMaterialTextures` reports `DescriptorIndexingUnavailable`, check descriptor indexing, runtime descriptor arrays, partially-bound descriptors, and update-after-bind support in the same startup log.
- If mode is `Disabled`, check `VulkanBindlessMaterialMode`, `EnableVulkanBindlessMaterialTable`, and `XRE_VULKAN_BINDLESS_MATERIAL_MODE`.
- If the table is ready but a draw is skipped, check descriptor binding failures for `GlobalMaterialTextureTable`; Required mode intentionally fails visibly instead of silently falling back.
- If textured materials sample fallback constants, inspect material rows for zero descriptor indices and check fallback reference counters for missing, unready, or non-sampled textures.
- If validation reports descriptor set/layout mismatches, verify the shader variant declares `XR_BindlessMaterialTextures` at set 2 / binding 31 and that the frame op was stamped with a bindless material descriptor binding.

### Resource Allocator

`VulkanResourceAllocator.cs` manages physical GPU resource allocation:

- Allocates `VkImage` and `VkBuffer` objects with appropriate memory types
- Uses `FindMemoryType()` to select appropriate memory heaps (device-local, host-visible, host-coherent)
- Tracks all allocations for cleanup during shutdown or swapchain recreation
- `VulkanRobustnessSettings.AllocatorBackend` defaults to `Vma`, the native Vulkan Memory Allocator P/Invoke backend. `Managed` remains selectable as the C# block allocator, and `Legacy` remains selectable for diagnostics but should not be used for normal editor profiling because it issues one Vulkan memory allocation per resource.

> **Note:** The legacy per-object allocator is allocation-heavy and exists primarily as a fallback/debug path. The VMA backend is the intended default path; choose `Managed` when debugging the C# allocator or native wrapper deployment.

Declared render-pipeline resources are synchronized through a staged planner
swap. `VulkanRenderer.ResourcePlannerState` builds a pending `VulkanResourcePlanner` from the
committed logical resource registry, validates render-pass metadata references
against declared textures, buffers, FBOs, and FBO attachment slots, then asks a
pending `VulkanResourceAllocator` to rebuild and allocate the replacement
physical image and buffer plan. The active planner and allocator are not
replaced until the pending plan succeeds; if allocation or validation fails, the
current physical plan remains live and the failure is logged. After a successful
swap, old physical resources are destroyed through the renderer's frame-slot
retirement queues rather than being torn down before the replacement plan is
ready.

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

ImGui draw vertex/index buffers grow with capacity headroom and are retired only when the current capacity is exceeded. They should not be recreated at the exact byte count of every fluctuating UI frame.

The ImGui integration supports registering custom textures (e.g., scene render targets displayed in editor panels). Each custom texture gets a dedicated descriptor set allocated from a per-texture pool.

---

## Advanced Features

### Skinning Buffer Contract

Vulkan should expose the same logical skinning contract as OpenGL and the
compute shaders:

- `BoneInfluenceCoreIndices`: four compact integer lanes per vertex
  (`Core4x8` or `Core4x16`).
- `BoneInfluenceCoreWeights`: four normalized `UNorm8` weight lanes.
- `BoneInfluenceSpillHeaders`: one `uint` per vertex, with offset in bits
  `0..23` and extra influence count in bits `24..31`.
- `BoneInfluenceSpillEntries`: one `uint` per overflow influence, with
  `boneIndexPlusOne` in bits `0..15` and `weightUNorm8` in bits `16..23`.
- `SkinPaletteBuffer`: final affine skin matrices stored as three `vec4` rows
  per bone, indexed through `skinPaletteBase` for shared palette slices.

The active renderer path must not reintroduce the old mesh-wide fixed-4 vs
variable influence branch or a paired bone-world/inverse-bind palette contract.

### Ray Tracing

`Features/Raytracing/VulkanRenderer.Raytracing.cs` provides ray tracing support when `VK_KHR_ray_tracing_pipeline` or `VK_NV_ray_tracing` is available:

- Probed during physical device selection
- Sets `Engine.Rendering.State.HasVulkanRayTracing` / `HasNvRayTracing` flags
- Acceleration structure building and ray tracing pipeline creation

### Auto-Exposure Compute

`VulkanAutoExposure.cs` implements GPU-based auto-exposure via compute shaders:

- Meters scene luminance from a 16×16 sample grid using a single 256-invocation workgroup: each invocation fetches one sample, then the workgroup reduces cooperatively in shared memory (bitonic sort when a top percentile is discarded, parallel tree sum otherwise)
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
- [Rendering Code Map](code-map.md) — Full source file inventory
