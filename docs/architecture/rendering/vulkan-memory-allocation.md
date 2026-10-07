# Vulkan Memory Allocation

This document describes how the Vulkan renderer allocates, maps, frees, and reports device memory.

Related documents:

- [Vulkan Renderer](vulkan-renderer.md#resource-allocator)
- [Vulkan Resource Lifetime And Retirement](vulkan-resource-lifetime-and-retirement.md)
- Code todo: [Vulkan Wrapper Parity TODO: Allocator Integration](../../work/todo/rendering/vulkan-wrapper-parity-todo.md#allocator-integration)
- Validation: [Vulkan Backend Parity Validation](../../work/testing/rendering/vulkan-backend-parity-validation.md)

## Ownership

`VulkanResourceRuntime.InitializeMemoryAllocator` creates one allocator for each logical device and stores it in `Allocations.Buffers.MemoryAllocator`. All buffer and image memory goes through that allocator. Engine code outside the Vulkan backend never sees raw VMA handles.

`VulkanRobustnessSettings.AllocatorBackend` selects the backend:
| `EVulkanAllocatorBackend` | Type | Use |
|---|---|---|
| `Vma` (default) | `VulkanVmaAllocator` | Native Vulkan Memory Allocator through a P/Invoke bridge. |
| `Managed` | `VulkanBlockAllocator` | C# block suballocator. Use it to debug the managed allocator or a missing native bridge. |
| `Legacy` | `VulkanLegacyAllocator` | One `vkAllocateMemory` call for each resource. Diagnostics only; do not use it for profiling. |

The selection is explicit. A failure in the `Vma` backend does not switch the renderer to `Managed` or `Legacy`.

## Allocator Contract

All backends implement `IVulkanMemoryAllocator`:

- `TryAllocateForBuffer` and `TryAllocateForImage` return `false` with the `Result` on out-of-memory. `AllocateForBuffer` and `AllocateForImage` throw `VulkanOutOfMemoryException`.
- `Free` releases one allocation.
- `TryMap` returns a pointer relative to the requested offset. `Unmap` releases one map scope.
- `ActiveVkAllocationCount` and `TotalAllocatedBytes` report live allocations.

`VulkanMemoryAllocation` carries `Memory`, `Offset`, `Size`, `MemoryTypeIndex`, `Properties`, `BlockId`, `NativeAllocation`, and `MappedData`. VMA allocations use `BlockId = -2` and a nonzero `NativeAllocation`. Bind, map, and diagnostic code can therefore use the same record for every backend.

## Native VMA Bridge
| Part | Location |
|---|---|
| Native bridge source | `Build/Native/VulkanMemoryAllocatorBridge/VulkanMemoryAllocatorBridge.cpp` and `.vcxproj` |
| Vendored VMA header and license | `Build/Native/VulkanMemoryAllocatorBridge/vendor/VulkanMemoryAllocator/` (version in `VERSION.txt`) |
| Header fetch script | `Tools/Dependencies/Get-VulkanMemoryAllocator.ps1` (task `Install-VulkanMemoryAllocator`) |
| Direct bridge build | `Tools/Build-VulkanMemoryAllocatorBridge.ps1` (task `Build-VulkanMemoryAllocatorBridge`) |
| Managed P/Invoke declarations | `VulkanVmaNative` |
| Managed allocator | `VulkanVmaAllocator` |
| Output DLL | `VulkanMemoryAllocatorBridge.Native.dll` |

`XREngine.Runtime.Rendering.Vulkan.csproj` builds the bridge for the active Debug or Release configuration and copies the DLL to the runtime output. Set `XREngineUseExistingNativeBridges=true` to use the packaged DLL under `XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/` instead. The bridge compiles VMA in one translation unit and exports `extern "C"` functions with the `xre_vma_` prefix: allocator create and destroy, allocate for buffer or image, bind, free, map, unmap, flush, invalidate, allocation info, heap budgets, and statistics strings.

Allocator creation passes the instance, physical device, logical device, API version, and allocator flags. The only allocator flag set today is the buffer-device-address flag, and only when the device enables buffer device address. The startup log records the bridge version, flags, and expected DLL path.

## Mapping

- Persistently mapped VMA allocations (`MappedData != 0`) return the stored pointer. `Unmap` does nothing for them.
- Other VMA allocations keep a reference count for each native allocation under one lock. `TryMap` increments it and `Unmap` decrements it.
- `Free` unmaps any map scopes that the caller leaked and logs a warning.
- A failed map logs a rate-limited warning with the result, allocation, offset, length, and memory properties.

## Failure And Fallback Policy

- A missing, wrong-architecture, or incompatible bridge DLL (`DllNotFoundException`, `EntryPointNotFoundException`, `BadImageFormatException`) becomes a `NotSupportedException` that names the DLL and tells the user to build the bridge or select `Managed`.
- `ErrorOutOfDeviceMemory`, `ErrorOutOfHostMemory`, and `ErrorFeatureNotPresent` from VMA return `false` from the `Try*` methods. Any other failure throws.
- `VulkanResourceRuntime.AllocateBufferMemoryWithFallback` and `AllocateImageMemoryWithFallback` retry a failed device-local request once with host-visible and host-coherent memory. Each retry increments the `RecordVulkanOomFallback` counter. If the retry fails, the method throws `VulkanOutOfMemoryException`.
- Image allocation pressure is checked before allocation. `TryGetDeviceLocalHeapBudgetSnapshot` reads VMA heap budgets for device-local heaps and applies a budget ratio and a reserve.
- `Dispose` does not destroy the VMA allocator while allocations are still live. It logs a warning instead, because that state occurs only after a leak or device loss during shutdown.

## Statistics

- `VulkanVmaAllocator.BuildStatsString(detailedMap)` returns the VMA JSON statistics document: usage for each heap and memory type, and block counts and sizes. With `detailedMap`, it also lists every block and allocation.
- `IRenderBackendDiagnosticsCapability.GetMemoryAllocatorStatistics` exposes that document. It returns `null` for the `Managed` and `Legacy` backends.
- The MCP tool `get_vulkan_memory_statistics` returns the document, or writes it to `output_path`. It is a cold diagnostic; do not call it every frame.

## Known Limits

- Allocations have no debug name or owner metadata in VMA.
- Allocator diagnostics do not yet map VMA heap and memory-type statistics, or report placement for each allocation.
- VMA memory-budget, external-memory (Win32), and memory-priority allocator flags are not enabled.
- External-memory allocations for Vulkan/OpenGL interop do not go through VMA.
- The device-local to host-visible retry is counted but is not refused for callers that explicitly request an accelerated path.
- Allocator creation passes `Vk.Version13` as the VMA API version, although the renderer requires Vulkan 1.4.
- No CI job builds the bridge, and no native or P/Invoke smoke test exists.

The [Vulkan wrapper parity TODO](../../work/todo/rendering/vulkan-wrapper-parity-todo.md#allocator-integration) tracks the code items for these limits.
