# Vulkan Managed Allocator VMA Concepts Todo

Current status: Vulkan uses `IVulkanMemoryAllocator` with `VulkanLegacyAllocator`, `VulkanBlockAllocator`, and the native `VulkanVmaAllocator`. `VulkanRobustnessSettings.AllocatorBackend` exposes `Legacy`, `Managed`, and `Vma`; `Vma` is the default backend and `Managed` remains the C# block allocator. This todo tracks improving the managed allocator using Vulkan Memory Allocator (VMA) concepts as reference, without taking a native dependency.

References:

- VMA repository: <https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator>
- VMA docs: <https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/>
- VMA product page: <https://gpuopen.com/vulkan-memory-allocator/>

## Goals

- Keep a fully managed C# allocator available and competitive as an explicit backend.
- Reduce per-resource `vkAllocateMemory` churn.
- Preserve explicit failure diagnostics for requested GPU paths.
- Make memory placement, heap pressure, fragmentation, and mapping behavior visible.
- Bring the managed allocator close enough to VMA behavior that the native VMA backend can share high-level engine contracts.

## Non-Goals

- Do not line-by-line port VMA.
- Do not add a native dependency in this work item.
- Do not silently fall back from device-local or device-address paths to CPU-visible memory when the caller requested the accelerated path.
- Do not implement full GPU defragmentation until resources can be safely moved, rebound, and synchronized.

## Phase 0 - Backend Contract

- [x] Rename the normal C# allocator backend to `EVulkanAllocatorBackend.Managed`.
- [x] Add `EVulkanAllocatorBackend.Vma` as a selectable backend.
- [x] Keep `EVulkanAllocatorBackend.Legacy` for diagnostics.
- [x] Initially default runtime and editor settings to `Managed`.
- [x] Make `Vma` fail visibly until the native wrapper exists.
- [x] Move the default runtime and editor settings to `Vma` after native wrapper implementation.
- [x] Add source-contract tests that `InitializeMemoryAllocator()` has explicit switch arms for `Legacy`, `Managed`, and `Vma`.
- [ ] Document how persisted settings should be handled after the `Suballocator` to `Managed` rename.

## Phase 1 - Allocation Descriptor

- [ ] Replace raw `(resource, MemoryPropertyFlags)` allocator calls with a managed allocation descriptor.
- [ ] Include resource kind: buffer, image, linear/transient, external-memory, sparse page.
- [ ] Include required flags, preferred flags, forbidden flags, and caller intent.
- [ ] Include usage hints matching VMA concepts: GPU-only, CPU-to-GPU, GPU-to-CPU, transient attachment, persistent upload, readback.
- [ ] Include placement requirements: dedicated required, dedicated preferred, suballocation allowed, never suballocate.
- [ ] Include device-address and external-memory requirements.
- [ ] Include debug name, owner subsystem, estimated lifetime, and allocation class for diagnostics.

## Phase 2 - Memory Type Selection

- [ ] Replace exact-only memory type selection with ranked selection over required/preferred flags.
- [ ] Prefer device-local memory for GPU-only resources.
- [ ] Prefer host-cached memory for readback when available.
- [ ] Prefer host-coherent memory for simple upload only when explicit flush is not requested.
- [ ] Record the selected heap/type and the reason lower-ranked candidates were rejected.
- [ ] Keep explicit OOM and unsupported-placement diagnostics instead of hidden CPU fallback.

## Phase 3 - Pool Matrix

- [ ] Split pools by memory type.
- [ ] Split pools by allocation flags that must match the backing `VkDeviceMemory`, including device address and external-memory export/import.
- [ ] Keep buffer/image separation or equivalent padding rules for `bufferImageGranularity`.
- [ ] Add linear/ring pools for staging, transient upload, dynamic uniforms, and short-lived render-graph scratch.
- [ ] Add fixed-size small-allocation pools for high-churn uniform and descriptor helper buffers.
- [ ] Add pool trim policies based on idle frames, heap pressure, and caller-owned residency hints.

## Phase 4 - Dedicated Allocation Rules

- [ ] Query dedicated requirements with `vkGetBufferMemoryRequirements2` and `vkGetImageMemoryRequirements2` when available.
- [ ] Use dedicated allocation when Vulkan reports it is required.
- [ ] Prefer dedicated allocation for very large resources, external-memory resources, and resources with special backing flags.
- [ ] Track dedicated allocations separately from suballocated blocks.
- [ ] Report whether dedicated placement was required by Vulkan, preferred by policy, or forced by block-size limits.

## Phase 5 - Alignment And Non-Coherent Safety

- [ ] Centralize alignment calculation for allocation offset, non-coherent atom size, and buffer-image granularity.
- [ ] Ensure host-visible non-coherent allocations do not share a `nonCoherentAtomSize` cache line.
- [ ] Keep flush/invalidate ranges aligned and clamped to the tracked allocation.
- [ ] Add tests for non-zero suballocation offsets and boundary flush/invalidate behavior.
- [ ] Keep hot-path allocation and free operations free of LINQ and avoidable heap churn.

## Phase 6 - Mapping Model

- [ ] Add reference-counted block mapping for host-visible memory.
- [ ] Add persistently mapped pools for upload rings and dynamic uniform rings.
- [ ] Return allocation-relative mapped pointers instead of making callers add offsets manually.
- [ ] Make coherent vs non-coherent behavior explicit in diagnostics.
- [ ] Add guard rails for mapping image memory and other suspicious usage.

## Phase 7 - Device Address Integration

- [ ] Stop routing device-address buffers through the legacy dedicated path by default.
- [ ] Add device-address-capable managed pools whose backing allocations use `MemoryAllocateFlags.DeviceAddressBit`.
- [ ] Ensure all buffers in a device-address pool were created with `ShaderDeviceAddressBit`.
- [ ] Keep diagnostics for scene-database and indirect-copy device-address consumers.
- [ ] Add validation tests that device-address buffer creation uses the selected allocator backend.

## Phase 8 - Budget, Stats, And Dumps

- [ ] Enable `VK_EXT_memory_budget` when available.
- [ ] Publish allocator heap usage and budget alongside existing tracked VRAM stats.
- [ ] Add allocator stats per heap, memory type, pool, block, and allocation class.
- [ ] Add fragmentation metrics: free bytes, largest free range, used bytes, committed bytes, and allocation count.
- [ ] Add a JSON dump with blocks, free ranges, allocation names, owners, and flags.
- [ ] Add editor/profiler display for allocator pressure and high-fragmentation pools.

## Phase 9 - Defragmentation Preparation

- [ ] Add a virtual allocator abstraction for subranges of large buffers and atlases.
- [ ] Start defragmentation with virtual allocations only.
- [ ] Define which resources are movable, copyable, and rebindable.
- [ ] Require timeline/fence ownership before any physical allocation move.
- [ ] Keep full image/buffer defragmentation as a later opt-in maintenance operation.

## Validation

- [ ] Run targeted Vulkan allocator unit tests.
- [ ] Run Vulkan buffer parity tests.
- [ ] Build `XREngine.Runtime.Rendering`.
- [ ] Run the editor in Vulkan mode with `Managed` selected.
- [ ] Confirm `Legacy` still starts for diagnostics.
- [x] Confirm `Vma` no longer uses the temporary startup failure.
