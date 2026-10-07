# CPU Memory Ownership

This document describes CPU-side ownership for renderer memory that can dominate the editor process. It covers mesh buffers, texture pixels, capacity-sized renderer arrays, and render-target budget policy. It does not define GPU feature correctness or visual acceptance.

## Architecture Links

- [Mesh Submission Strategies](mesh-submission-strategies.md)
- [Texture Streaming](texture-streaming.md)
- [Vulkan Memory Allocation](vulkan-memory-allocation.md)
- [Runtime And AOT Validation](../../work/testing/runtime/runtime-and-aot-validation.md)

## Ownership Rules

- Keep one authoritative CPU copy for data that must survive after upload.
- Release or spill CPU copies when the runtime has a reload source.
- Do not create a CPU fallback silently when a GPU-produced buffer has no CPU copy.
- Grow renderer storage only before a frame or plan is sealed.
- Keep GPU memory, mapped device-local memory, managed heap, and native `DataSource` memory as separate budget lines.

## Mesh CPU Copies

`XRMesh` no longer uses a long-lived per-vertex object list as the main runtime copy. Current mesh construction and import paths write packed sources and `XRDataBuffer` streams. The important files are:

- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.PackedSource.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMeshVertexView.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.ClientCopyPolicy.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferSpilledDataSource.cs`

`XRDataBuffer.ClientCopyPolicy` decides what happens to CPU storage after the GPU copy is current. Mesh-owned static buffers can use `ReleaseAfterUpload`. Large static copies spill to a session mapping. A spill keeps bytes available through `XRBufferSpilledDataSource`, but those pages still count as commit when the mapping is writable. Read-only spill views remain a separate decision because a missed writer would fault.

The rendering assembly owns the spill queue, settle delay, write lease, and `DataSource` adapter. The application host must register `XRBufferSpillStorageServices.Current` before it uses spill mapping. The desktop bootstrap and browser renderer composition register their providers before sessions start. A standalone rendering host must register its own provider. A missing provider reports a named spill failure and retains the CPU copy. A direct `XRBufferSpilledDataSource.Map` call reports the missing provider.

Each provider writes a delete-on-close session file, flushes it, and creates a copy-on-write mapping. The mapping lease owns one acquired pointer. Disposal releases that pointer and closes the view and mapping. Finalization releases the pointer and leaves the view and mapping handles to SafeHandle finalization. A provider replacement does not close existing leases.

Browser mapping is supported in source: [.NET 10.0.12 includes the Unix memory-mapped file implementation for browser builds](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.IO.MemoryMappedFiles/src/System.IO.MemoryMappedFiles.csproj#L89-L117), and its [Unix mapping path](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.IO.MemoryMappedFiles/src/System/IO/MemoryMappedFiles/MemoryMappedView.Unix.cs) reaches Emscripten. [Emscripten 3.1.56 MEMFS uses a copy for `MAP_PRIVATE`](https://github.com/emscripten-core/emscripten/blob/3.1.56/src/library_memfs.js#L325-L355). The browser provider attempts the same file write and copy-on-write mapping as desktop. MEMFS can keep JS and WASM copies, so a browser spill does not prove lower memory use. Browser spill success and memory effects require runtime validation.

GPU-produced buffers can set `GpuProduced` and avoid a client copy. Consumers that need a CPU read must request an explicit readback or reload source. This keeps missing CPU data visible.

Published cooked mesh buffers still need more work. `PublishedArchiveHandle` maps archives, but `XRMesh.CookedBinary.CopyReaderToBuffer` can copy cooked bytes into new native memory after load. Direct mapping is useful only when the cooked format stores large streams in a mappable form.

## Texture CPU Copies

`XRTexture2D` separates metadata from resident pixels. The important files are:

- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ResidentPixelRelease.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.ImportedStreaming.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.CanonicalPublication.cs`

After a streaming resident mip chain publishes to Vulkan, `ReleaseStreamingResidentPixels` can free CPU pixel data. The mip objects keep width, height, and format metadata. `TryRestoreReleasedResidentPixels` reloads pixels from the streaming source for cold consumers such as serialization or renderer restart rehydration. Do not call it on the render thread.

Texture streaming payload code now has writer overloads that can write into caller-owned storage. Some paths still need lease-based extraction and source reads. BCn payloads are not implemented. The encoder choice must pass the repository license rules before code changes start.

## Capacity-Sized Renderer Arrays

Renderer storage can start small and grow to observed high-water marks. Frame plans and sealed streams must not grow after publication. Current examples include:

- `FrameOperationPayloads` initial advanced-visibility capacity.
- Per-opcode payload columns with smaller initial capacities.
- `VulkanAdvancedVisibilityInputStorage` fixed-row initial capacity.
- `VulkanPreparedStableBinStream` demand-sized rows, manifests, and exception streams.
- Advanced deformation generation mirrors that release destroyed-scene storage.
- `AdvancedDeformedVertexArena` CPU arrays allocated only on first CPU access.
- `MeshletPayload` without a stored vertex copy.
- `FramePlanBuilder.BuildAndSeal` borrowed logical operations for OpenXR eye plans.

These arrays use capacity as an admission limit, not as the default allocation size. Growth is acceptable at cold provisioning points. It is not acceptable in sealed frame loops.

## Render-Target And VRAM Budgets

`VulkanRenderer` reports allocator streaming capability so texture streaming can use VMA heap budget data. Streaming budget policy combines `VramBudgetMB`, `VramBudgetHeapFraction`, and a reserve. `TextureQuality` caps resident dimensions before streaming requests allocate memory.

Render-target declarations must follow active passes. The OpenXR eye pipeline declares RVC targets only when RVC GPU stages are scheduled. Ambient occlusion scratch targets depend on the active AO mode. Bloom uses `R11G11B10F` because consumers use RGB values only.

Transient aliasing stays disabled until lifetime-aware grouping, handoff barriers, discard transitions, and name-based lookup audits are complete. `VulkanAliasGroupKey` currently groups transient requests by equal alias keys, but that is not a proof that lifetimes do not overlap.

## Budgets And Reporting

Use these budget lines in memory reports:
| Budget line | Source | Notes |
|---|---|---|
| Process private bytes | OS process counters | Includes mapped device-local memory on some drivers. |
| Device-local GPU memory | VMA statistics | Report separately from process memory. |
| Managed heap | `GC.GetGCMemoryInfo` and allocation scopes | Record live, committed, and fragmented bytes. |
| Native `DataSource` memory | Runtime counters | Includes retained CPU buffer and texture storage. |
| Texture streaming memory | Texture streaming summary | This is estimated GPU resident size, not CPU memory. |
| Render-target memory | Vulkan planner state | Include desktop and OpenXR planner sets separately. |

## Known Limits

- Stored published mesh entries are not yet backed directly by archive mappings.
- GDeflate mesh upload needs a supported Vulkan decompression path. Unsupported devices must report missing CPU data.
- BCn texture cache payloads need an approved encoder dependency.
- Sparse texture residency is not implemented.
- Transient render-target aliasing is not enabled.
- Read-only spill views are not enabled.
- Spill byte accounting still decrements for directly mapped or abandoned sources,
  while increments occur after a successful buffer swap. This existing counter
  limit is unchanged by the host-storage extraction. It must be resolved before
  using that counter as proof of a memory reduction.

## Validation

Manual and runtime checks live in [Runtime And AOT Validation](../../work/testing/runtime/runtime-and-aot-validation.md). Use that doc for profiler captures, screenshots, hardware rows, and editor runs.
