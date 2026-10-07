# Editor Memory Reduction TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [CPU Memory Ownership](../../../../architecture/rendering/cpu-memory-ownership.md), [Texture Streaming](../../../../architecture/rendering/texture-streaming.md)
Validation: [Runtime And AOT Validation](../../../testing/runtime/runtime-and-aot-validation.md)

## Current State
The editor memory work has reduced live managed memory, device-local memory, texture CPU pixel retention, mesh vertex object retention, and capacity-sized renderer arrays. The remaining code work is to map cooked mesh data directly when the format supports it, upload cooked buffers without extra processing, add BCn texture payloads after an approved encoder choice, keep sparse residency deferred, prove safe transient target aliasing, and repair an OpenXR planner commit lifetime issue.

## Open Code Items

### Cooked Mesh CPU Copies
- [ ] Map stored cooked mesh data directly from published archives. Files or types: `PublishedArchiveHandle`, `FileMap`, `XRMesh.CookedBinary.CopyReaderToBuffer`, and mesh buffer load code. Done when stored large entries can keep the archive mapping as their CPU source instead of copying into new native memory.
- [ ] Make cooked buffer bytes match the GPU upload layout. Files or types: `XRMesh.CookedBinary`, mesh buffer encoding, and Vulkan buffer upload. Done when upload can copy from mapped pages to staging without intermediate processing for raw entries.
- [ ] Route supported compressed mesh entries through GPU decompression. Files or types: `SetGpuCompressedPayload`, `Compression.GDeflateBackend`, and Vulkan upload. Done when GDeflate entries upload through `VK_NV_memory_decompression` when available, and unsupported devices report visible missing CPU data.

### Texture Payloads And Residency
- [ ] Add BCn texture payload variants after encoder approval. Files or types: texture binary cache, Vulkan format mapping, BC7 colour, BC5 normal, and BC4 or BC7 mask payloads. Done when `textureCompressionBC` devices stream compressed variants and unsupported devices report a diagnostic.
- [ ] Keep sparse residency as deferred design work. Files or types: Vulkan texture streaming capability and texture runtime design. Done when dense residency remains explicit until sparse residency receives its own implementation plan.

### Render-Target Ownership
- [ ] Implement lifetime-aware transient target aliasing. Files or types: `VulkanAliasGroupKey`, `VulkanCompiledRenderGraphPlan`, `VulkanTransientAttachmentPlan`, `VulkanResourceAllocator.UpdatePlan`, and `VulkanBarrierPlanner`. Done when alias grouping proves non-overlap, emits handoff barriers, discards on first use, and audits name-based lookups.
- [ ] Keep transient buffers dedicated until buffer lifetime aliasing is proven. Files or types: `BufferResourceDescriptor.SupportsAliasing` and allocation planning. Done when buffers cannot alias without a lifetime proof.
- [ ] Defer OpenXR planner commits to planner scope exit. Files or types: OpenXR planner session and allocator retirement logic. Done when a commit inside the planner scope does not retire the live eye-planner allocator and startup black does not recur.

## Decisions Needed

- [ ] Choose a BCn encoder for texture cache payloads. Owner: Rendering owner.
- [ ] Decide the published cook format for large mesh streams. Owner: Runtime and rendering owners.
- [ ] Decide whether read-only spill views are safe enough to lower commit. Owner: Rendering owner.
- [ ] Decide whether `DOTNET_GCConserveMemory` or `System.GC.ConserveMemory` becomes an editor default. Owner: Runtime owner.
- [ ] Decide whether transient aliasing and the OpenXR planner commit fix remain separate projects. Owner: Rendering owner.
- [ ] Keep play-mode memory and time work outside this todo. Owner: Rendering owner.

## Out Of Scope

- Profiler capture, screenshot, hardware, and runtime validation. Those checks live in the runtime validation doc.
- Dependency addition without owner approval and dependency report updates.
- CPU fallback for missing GPU decompression or missing resident data.
