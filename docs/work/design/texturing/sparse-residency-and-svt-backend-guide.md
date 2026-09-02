# Sparse Residency And Streaming Virtual Texturing Backend Guide

Last Updated: 2026-09-02
Status: canonical companion design
Scope: normative OpenGL and Vulkan sparse-residency behavior, portable streaming virtual texture architecture, synchronization, physical-cache ownership, filtering, feedback, VR prioritization, and validation.

Related documents:

- [Texture Runtime, Streaming, And Virtual Texturing Design](texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](../../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)
- [Texture Compression And Cooked Texture Cache Design](texture-compression-and-cooked-cache-design.md)
- [Sparse Texture Streaming Plan](sparse-texture-streaming-plan.md) *(historical implementation ledger)*

Normative external references:

- [GL_ARB_sparse_texture](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_sparse_texture.txt)
- [GL_ARB_sparse_texture2](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_sparse_texture2.txt)
- [Vulkan sparse resources](https://docs.vulkan.org/spec/latest/chapters/sparsemem.html)
- [Vulkan image resources and subresources](https://docs.vulkan.org/spec/latest/chapters/resources.html)

## 1. Purpose

XRENGINE already has two useful texture-streaming implementations:

- OpenGL dense/tiered mip streaming plus hardware sparse whole-mip residency.
- Vulkan dense, generation-gated, synchronized mip uploads with deferred descriptor publication and resource retirement.

Neither implementation is a complete streaming virtual texture system. This guide defines the exact boundary between mip streaming, hardware sparse residency, software-indirected streaming virtual texturing, and runtime-generated virtual texturing. It also specifies the synchronization and publication rules that future backends must follow.

The portable production target is a software-indirected streaming virtual texture system built around page-addressable cooked assets, dense physical tile caches, virtual page tables, GPU feedback, and ancestor fallback. Hardware sparse resources are optional backend optimizations. They are not the cross-API asset or shader contract.

## 2. Terminology And Feature Tiers

### 2.1 Dense mip streaming

A texture is represented by an ordinary fully backed image containing only the selected resident mip range. Promotion or demotion can recreate the image, upload a replacement image, or republish a different image view or descriptor.

This is the current Vulkan path and the OpenGL compatibility path.

### 2.2 Hardware sparse mip residency

One stable logical image exposes the complete authored dimensions and mip count, but only selected complete mips are physically committed. Sampling is clamped to the committed range.

This is the current preferred OpenGL path for eligible textures.

### 2.3 Hardware sparse page residency

Individual hardware-defined regions inside a sparse mip are committed and uncommitted. The page geometry is device-, API-, target-, and format-dependent. Hardware page commitment alone does not provide a virtual page table, tile borders, filtering across page boundaries, disk addressing, feedback, or an ancestor fallback policy.

XRENGINE has OpenGL region-selection and commit scaffolding, but policy keeps it disabled by default.

### 2.4 Streaming virtual texturing (SVT)

A logical texture is divided into engine-defined pages. A virtual page table maps logical page identifiers to slots in a physical tile cache. GPU feedback requests missing pages, the streamer loads page-addressable cooked blobs, and shaders fall back to a resident ancestor until the requested page is safely published.

This is not implemented yet.

### 2.5 Runtime virtual texturing (RVT)

RVT uses the same logical-page, physical-cache, page-table, and eviction concepts as SVT, but page content is rendered or generated at runtime instead of loaded from cooked files.

This belongs after the shared SVT cache and page-table infrastructure is stable.

## 3. Current Backend Capability Matrix

| Capability | OpenGL | Vulkan |
|---|---:|---:|
| Dense/tiered mip streaming | Implemented | Implemented |
| Generation-gated asynchronous preparation | Implemented | Implemented |
| GPU-completion-gated publication | Implemented for shared-context sparse promotion | Implemented for dense descriptor publication |
| Hardware sparse whole-mip residency | Implemented for eligible `Rgba8` `XRTexture2D` assets | Not implemented |
| Hardware sparse partial-page residency | Scaffolded; disabled by policy | Not implemented |
| Page-addressable cooked payload | Not implemented | Not implemented |
| Shared physical tile cache | Not implemented | Not implemented |
| Virtual page table | Not implemented | Not implemented |
| GPU page feedback and resolve | Not implemented | Not implemented |
| Full SVT | Not implemented | Not implemented |
| RVT page production | Not implemented | Not implemented |

## 4. Cross-API Invariants

### 4.1 Stable logical identity

Materials retain a stable logical texture or virtual-texture identity. Backend storage, committed pages, image handles, descriptors, cache slots, and page-table generations may change underneath that identity.

### 4.2 Never expose incomplete data

A finer mip, sparse page, physical tile, image generation, or page-table mapping becomes shader-visible only after all required memory binding, data upload, barriers, and cross-queue or cross-context synchronization have completed.

### 4.3 Revoke before reuse

A physical page or cache slot cannot be rebound or reused until:

1. every page-table or descriptor mapping that references it has been replaced with a valid fallback;
2. every submitted frame that could observe the old mapping has retired; and
3. any API-specific sparse unbind has completed.

### 4.4 Always-resident fallback

Every streamable texture keeps a valid coarse fallback. For sparse mip streaming this is the committed mip tail or a pinned coarse range. For SVT this is a pinned ancestor page chain or compact fallback mip atlas.

Correctness must not depend on the value returned by a nonresident hardware sparse read.

### 4.5 Generation-gated work

Every I/O request, decode/transcode result, memory bind, upload, page-table publication, and eviction captures the relevant texture, cache, and storage generations. Stale work is canceled or discarded before publication.

### 4.6 Renderer-neutral policy

Policy deals in logical dimensions, logical pages, resident quality, estimated or exact physical bytes, priority, deadline class, source version, and completion tickets. OpenGL handles, Vulkan handles, image layouts, memory offsets, extension enums, and queue-family indices remain below backend interfaces.

### 4.7 No blocking hot-path readback

GPU feedback readback uses a ring and intentional latency. Rendering never waits synchronously for page requests. Feedback latency is hidden with coarse fallback, neighborhood expansion, velocity prediction, and hysteresis.

## 5. OpenGL Hardware Sparse Residency Contract

### 5.1 Capability probing

Probe `GL_ARB_sparse_texture` and `GL_ARB_sparse_texture2` during renderer initialization. Sparse support must be queried for each exact texture target and sized internal format that the backend intends to use.

For every target/format pair:

1. query `GL_NUM_VIRTUAL_PAGE_SIZES_ARB` with `glGetInternalformativ`;
2. if the result is zero, mark that pair unsupported;
3. query the complete corresponding arrays for:
   - `GL_VIRTUAL_PAGE_SIZE_X_ARB`;
   - `GL_VIRTUAL_PAGE_SIZE_Y_ARB`;
   - `GL_VIRTUAL_PAGE_SIZE_Z_ARB`;
4. choose and retain a page-size index through an explicit backend policy;
5. report the selected index and geometry through diagnostics.

Do not assume that every format uses 64x64 or 128x128 pages. `GL_ARB_sparse_texture2` standardizes index-zero page sizes for several uncompressed formats, but compressed formats and other layouts remain query-driven.

### 5.2 Page-layout selection

The first nonzero layout is a functional fallback, not a complete policy. Selection should consider:

- the engine logical tile interior size;
- physical page count per upload;
- edge waste and partial-edge behavior;
- format block geometry;
- expected filtering footprint;
- driver validation results;
- and whether index zero is the standardized sparse-texture2 layout for the format.

The selected layout is backend state. It must not be written into the portable cooked asset as the logical page size.

### 5.3 Sparse immutable storage creation

The creation order is mandatory:

1. create or generate the texture object;
2. set `GL_TEXTURE_SPARSE_ARB = GL_TRUE`;
3. set `GL_VIRTUAL_PAGE_SIZE_INDEX_ARB` to the chosen index;
4. allocate the full logical immutable storage with `glTextureStorage2D` or the equivalent target-specific call;
5. query `GL_NUM_SPARSE_LEVELS_ARB` from the created texture;
6. commit and upload a valid fallback range before exposing the texture to materials.

Sparse flags cannot be retrofitted after immutable storage exists. A texture that already owns incompatible dense storage must be recreated before entering the sparse path.

### 5.4 Base-dimension eligibility

With base `GL_ARB_sparse_texture`, the logical allocation must satisfy the extension's sparse page-alignment restrictions. Textures that do not satisfy them use the dense/tiered fallback.

When `GL_ARB_sparse_texture2` is present, arbitrary base dimensions may be accepted. Individual commitments must still use page-aligned origins and page-sized extents, except where an extent reaches the edge of a mip according to the extension rules.

XRENGINE currently applies the conservative base-extension alignment gate even when sparse-texture2 is available. Relaxing that gate is a separate implementation task and requires dedicated edge-commit validation.

### 5.5 Mip tail

`GL_NUM_SPARSE_LEVELS_ARB` is the first mip level in the opaque mip tail. Mips below that index are independently sparse-manageable; the tail is committed or uncommitted as one atomic region.

The backend must:

- query the tail boundary per texture allocation;
- pin the tail or another complete coarse fallback before sampling;
- never estimate the tail boundary from dimensions alone;
- and never attempt to treat tail mips as independent pages.

### 5.6 Promotion sequence

A whole-mip or page promotion follows this order:

```text
Resolve page-aligned physical coverage
→ commit pages with glTexPageCommitmentARB
→ upload every texel that may be sampled
→ insert a GL sync object when work occurs on a shared context
→ flush the producer context
→ wait or poll from the render context
→ verify storage generation
→ expose the finer base mip or publish the new virtual mapping
```

Newly committed memory has undefined contents. Commitment is not initialization.

For shared-context uploads, `glFenceSync` plus `glFlush` on the producer context and `glClientWaitSync` or `glWaitSync` on the consumer context form the publication boundary. The render thread must not lower `GL_TEXTURE_BASE_LEVEL` before that boundary completes.

### 5.7 Demotion and eviction sequence

Whole-mip demotion follows this order:

```text
Ensure a coarser range is populated
→ raise GL_TEXTURE_BASE_LEVEL
→ retire draws that may still sample the finer range
→ uncommit the inaccessible finer pages
```

SVT cache eviction follows the cross-API mapping sequence instead:

```text
Publish ancestor mapping
→ retire old frames/page-table versions
→ uncommit optional sparse cache backing
→ release physical slot
```

### 5.8 Nonresident sampling

Base `GL_ARB_sparse_texture` does not provide a portable material value for nonresident reads. `GL_ARB_sparse_texture2` defines zero-like behavior for nonresident components and adds sparse residency-query operations, but zero is still not a valid universal material fallback.

Examples of invalid zero fallback include black albedo, zero opacity, invalid tangent normals, and unintended mirror-like roughness. Shaders must sample only committed data or resolve missing virtual pages to valid resident ancestors.

### 5.9 Compressed formats

Sparse compressed texture support does not universally require `GL_ARB_sparse_texture2`. It is implementation-dependent and must be queried for the exact compressed internal format using `GL_NUM_VIRTUAL_PAGE_SIZES_ARB`.

The backend policy is:

- use sparse BCn only when the exact format reports usable page layouts and validation passes;
- retain dense BCn cache banks as the normal portable SVT path;
- retain an uncompressed portable fallback where required;
- keep block-aligned page offsets, extents, and stored dimensions;
- and use compressed subimage upload calls for precompressed cooked pages.

### 5.10 Diagnostics and byte accounting

`glTexPageCommitmentARB` submission is not proof that a transition was valid. Debug builds should use `KHR_debug` and log the target, format, mip, rectangle, selected page geometry, storage generation, and transition generation for every failure.

Avoid hot-path `glGetError` polling in release builds.

OpenGL committed bytes are generally an estimate because opaque tail allocation, page padding, and driver allocation behavior are not fully exposed. Telemetry should distinguish:

- logical payload bytes;
- estimated sparse physical bytes;
- exact dense physical-cache bytes;
- and driver-reported global memory pressure where available.

## 6. Vulkan Hardware Sparse Residency Contract

### 6.1 Device and queue capability probing

Before enabling a Vulkan sparse backend, require:

- `sparseBinding`;
- `sparseResidencyImage2D` for 2D textures;
- an appropriate queue family with `VK_QUEUE_SPARSE_BINDING_BIT`;
- support for the exact format, image type, tiling, usage, sample count, and creation flags;
- and `shaderResourceResidency` only when shaders will query sparse residency directly.

Use `vkGetPhysicalDeviceSparseImageFormatProperties2` for every intended image configuration. A zero property count means that configuration is unsupported and must use a dense fallback.

Do not require a dedicated sparse queue. A combined graphics/sparse queue is valid, but sparse binding remains explicitly synchronized queue work even when the same queue performs subsequent copies or rendering.

### 6.2 Sparse image creation

A sampled sparse image normally uses:

```text
VK_IMAGE_CREATE_SPARSE_BINDING_BIT
VK_IMAGE_CREATE_SPARSE_RESIDENCY_BIT
VK_IMAGE_USAGE_SAMPLED_BIT
VK_IMAGE_USAGE_TRANSFER_DST_BIT
VK_IMAGE_TILING_OPTIMAL
```

Only add `VK_IMAGE_CREATE_SPARSE_ALIASED_BIT` when the design intentionally aliases the same memory across sparse resources and the device exposes the corresponding feature. Initial implementations should avoid sparse aliasing.

### 6.3 Memory requirements, mip tails, and metadata

After image creation, query:

- `vkGetImageMemoryRequirements2` for allocation size/alignment and memory types;
- `vkGetImageSparseMemoryRequirements2` for sparse granularity, mip-tail layout, per-layer tail stride, single-tail behavior, and metadata requirements.

Non-tail regions use `VkSparseImageMemoryBind` in `VkSparseImageMemoryBindInfo`.

Mip tails use opaque memory binds through `VkSparseImageOpaqueMemoryBindInfo`. When sparse requirements expose a metadata aspect, bind it through an opaque bind using `VK_SPARSE_MEMORY_BIND_METADATA_BIT` before the image is used.

Tail and metadata requirements are implementation-defined. Never infer them solely from texture dimensions.

### 6.4 Physical sparse memory pools

Do not allocate one `VkDeviceMemory` object per page. Allocate large device-local chunks for each compatible memory type and suballocate aligned sparse blocks.

A sparse pool tracks:

- memory type and heap;
- allocation chunks;
- allocation alignment and block size;
- free blocks or ranges;
- page owner and generation;
- pending bind/unbind state;
- and deferred reclamation after GPU completion.

For non-tail image blocks, physical-byte accounting is the number of bound blocks multiplied by the required aligned block allocation. Add tail and metadata allocations separately.

### 6.5 Bind, upload, and publication ordering

Sparse memory binding uses `vkQueueBindSparse`; it is not a command-buffer operation and is not implicitly ordered with transfer, graphics, or compute submissions.

Promotion follows this dependency chain:

```text
Reserve physical blocks
→ submit vkQueueBindSparse
→ signal bind-complete semaphore/timeline value
→ transfer or graphics submission waits for bind completion
→ transition/copy the page data
→ signal upload-complete timeline value
→ verify texture/cache generation
→ publish image-view LOD state or virtual page-table mapping
```

Binary or timeline semaphores may be used as supported by the submission path. The dependency must remain explicit even when sparse bind and copy execute on the same queue.

### 6.6 Eviction and unbind ordering

Eviction follows:

```text
Publish a valid ancestor/fallback mapping
→ retire every frame that can observe the old mapping
→ submit vkQueueBindSparse with VK_NULL_HANDLE memory for the old region
→ wait for unbind completion
→ return blocks to the sparse pool
```

Returning memory to the pool before the old mapping and submissions retire can cause previous frames to observe unrelated data that reused the same block.

### 6.7 Image-layout strategy

Sparse binding does not perform image layout transitions. Vulkan image layouts and barriers operate on image subresources such as aspect, mip, and array layer; they do not transition one rectangular sparse tile independently inside a mip.

For a direct sparse logical image with concurrent sampling and page uploads in the same mip, the practical choices are:

1. keep streamed subresources in `VK_IMAGE_LAYOUT_GENERAL` and use explicit transfer-write-to-shader-read dependencies; or
2. use a software SVT physical cache where each tile occupies a separate 2D-array layer, allowing per-layer transitions and copies.

The second option is the recommended portable production baseline. Direct sparse logical images remain an optional advanced backend after cross-vendor validation.

### 6.8 Descriptor publication

Dense Vulkan streaming may replace the underlying image/view and therefore requires descriptor publication after upload completion.

A direct sparse image can retain a stable image descriptor. Its publication boundary is instead the page table, resident LOD clamp, or another shader-visible residency structure. That structure still cannot be updated until sparse bind and upload completion are proven.

### 6.9 Nonresident sampling

`residencyNonResidentStrict` determines whether nonresident reads have strict zero behavior. Without it, read values are undefined, though memory safety is preserved. Correct rendering must not depend on either behavior.

Use a page-table ancestor fallback or prevent sampling of unbound direct-sparse regions.

## 7. Portable Streaming Virtual Texture Architecture

### 7.1 Why software indirection is the baseline

A software-indirected SVT provides the same asset and shader contract on OpenGL and Vulkan, works when sparse resources are unsupported, provides deterministic missing-page fallback, and decouples engine page geometry from hardware page geometry.

The first production implementation should use dense physical cache banks. Hardware sparse memory can later back cache banks or selected direct images without changing logical page identifiers or cooked assets.

### 7.2 Proposed runtime owners

Keep the current mip streamer and add a separate subsystem rather than overloading `ITextureResidencyBackend`:

- `VirtualTextureManager` — frame-level orchestration and budgets.
- `VirtualTextureRegistry` — logical assets, users, material sets, and generations.
- `VirtualTextureFeedbackResolver` — request deduplication, weighting, guard bands, and prefetch.
- `VirtualTexturePageStreamer` — page I/O, decode/transcode, cancellation, and upload scheduling.
- `VirtualTexturePhysicalCache` — per-format cache banks, slot ownership, eviction, and deferred reuse.
- `VirtualTexturePageTable` — CPU shadow, GPU representation, versioning, and publication.
- `IVirtualTexturePageBackend` — API-specific page upload and page-table publication.

`ImportedTextureStreamingManager` remains responsible for ordinary imported-texture preview and whole-mip residency. It also provides the fallback path for assets that are not virtualized.

### 7.3 Logical page identity

A logical page identifier contains at least:

```text
texture id
material-set or layer id
mip level
page x
page y
source/cook generation
```

Requests are discrete page IDs, not one normalized UV rectangle. This permits disjoint visible regions, many instances, multiple materials, multiple views, and independent page priorities.

For material texture sets, page identity should support synchronized bundles such as base color, normal, and scalar channels. The policy may stream them as one logical material page to avoid mismatched detail across channels.

### 7.4 Logical tiles versus hardware pages

The cooked asset defines a stable logical interior tile size and stored border size. A backend maps that logical tile to its physical representation:

- one logical tile per dense array layer;
- one logical tile spanning multiple OpenGL or Vulkan sparse blocks;
- multiple logical tiles packed into a compatible larger allocation;
- or dense mip fallback when hardware geometry is unsuitable.

Never encode `GL_VIRTUAL_PAGE_SIZE_INDEX_ARB`, OpenGL page dimensions, Vulkan sparse granularity, queue families, or device memory offsets into the portable logical page contract.

### 7.5 Cooked page-addressable payload

The virtual-texture payload includes:

- logical texture dimensions and mip count;
- logical interior tile width and height;
- stored width and height including borders;
- border size and generation policy;
- format, color space, and texture role;
- wrap mode used to generate borders;
- page mip/X/Y/layer identity;
- per-page byte offset and byte length;
- compressed block geometry and row/slice metadata where applicable;
- content checksum or hash;
- source and cooker versions;
- fallback mip descriptors;
- and optional page-group metadata for material bundles.

Borders are generated before GPU-native compression so runtime upload remains copy-only. Stored extents and offsets must satisfy the compression format's block alignment.

### 7.6 Dense physical cache banks

The recommended first backend uses 2D-array texture banks:

- one array layer per tile slot;
- multiple banks when layer limits are reached;
- fixed dimensions including borders;
- exact memory accounting;
- per-format or per-role pools;
- and no dependency on hardware sparse-resource support.

Initial format banks should include, as platform support permits:

- BC7 or equivalent sRGB/linear color banks;
- BC5 normal banks;
- BC4 scalar banks;
- RGBA8 portable fallback banks.

A cache slot records its owner page, generation, last use, priority, pin state, and the frame/page-table version after which it may be reused.

### 7.7 Virtual page table

A page-table entry encodes at least:

- valid/resident bit;
- physical cache bank;
- physical slot or array layer;
- resolved resident mip;
- fallback/ancestor mip delta;
- mapping generation;
- and optional format/material-set flags.

The table may be stored in integer textures, texture buffers, or storage buffers according to backend capability and lookup cost. Its public logical layout must be shared across OpenGL and Vulkan shader implementations.

Use double- or triple-buffered page-table publication, or an equivalent versioned scheme, so mappings consumed by in-flight frames are immutable. Cache slots referenced by an old table version remain unavailable for reuse until that version retires.

### 7.8 Page lifecycle

Promotion uses the following state machine:

```text
Requested
→ IoQueued
→ PayloadReady
→ CacheSlotReserved
→ UploadSubmitted
→ GpuComplete
→ MappingPublished
→ Resident
```

Eviction uses:

```text
Resident
→ AncestorMappingPublished
→ OldTableVersionsRetired
→ OptionalSparseUnbindComplete
→ CacheSlotReleased
→ Evicted
```

Failures at any stage retain or restore a valid ancestor mapping. A partially uploaded slot is never published.

### 7.9 Feedback generation

The first feedback path should be GPU-generated and asynchronously resolved. Viable implementations include:

- a reduced-resolution integer feedback target written during material sampling;
- a storage-buffer hash or bitset updated with atomics;
- or a dedicated material resolve pass that emits page IDs.

The feedback record should include or permit reconstruction of:

- logical page ID;
- requested mip;
- sample count or screen coverage;
- view/eye/foveation priority;
- and optional material role.

A compute resolve pass deduplicates requests, aggregates importance, expands guard bands, and compacts a bounded request list. CPU readback uses a persistently mapped or staged ring with one or more frames of latency and no render-thread wait.

### 7.10 Request resolution and prediction

The resolver applies:

- fallback-aware deduplication;
- screen-error or requested-mip weighting;
- page neighborhood expansion for filtering;
- camera and head angular/linear velocity prediction;
- short residency TTLs and hysteresis;
- starvation prevention across assets;
- and global physical-cache and upload budgets.

A camera teleport or invalid shader-generated UV domain should temporarily request a coarser complete fallback rather than flooding the streamer with unbounded pages.

### 7.11 Filtering and borders

Hardware filtering cannot cross unrelated physical cache slots. The virtual sampling helper must:

1. compute LOD from derivatives of the original virtual UVs;
2. resolve the requested page or a resident ancestor;
3. remap UVs into the physical tile interior;
4. sample with border texels for bilinear continuity;
5. resolve the adjacent virtual mip for trilinear filtering;
6. sample its independently resolved page/ancestor;
7. blend the two mip results.

The border must cover the supported filtering footprint. Initial anisotropy should be capped to what the configured border safely supports. Higher anisotropy can use wider borders, additional taps, or a more advanced page neighborhood scheme.

Borders must honor authored wrap behavior:

- repeat borders wrap to the opposite logical edge;
- mirror borders mirror the source neighborhood;
- clamp borders duplicate edge texels;
- and unsupported procedural or shader-generated addressing opts out or uses a conservative fallback.

### 7.12 VR and foveated views

Physical residency is shared across views. Feedback carries view identity and priority, but identical logical page requests from both eyes are unioned before streaming.

Recommended priority order:

```text
HMD foveal samples
> HMD peripheral samples
> gameplay-critical auxiliary views
> desktop mirror
> reflections, probes, and background captures
```

The resolver chooses the finest page required by any important view and retains one shared physical copy. Stereo divergence, head rotation, and foveation movement expand prefetch neighborhoods rather than creating per-eye duplicate caches.

## 8. Integration With Existing XRENGINE Streaming

### 8.1 Keep the existing mip contract intact

`ITextureResidencyBackend` is intentionally mip-oriented: it accepts resident dimensions, mip arrays, sparse mip metadata, and one coarse page-selection hint. It should not be expanded into a general SVT page API.

Add an independent page-batch backend, conceptually:

```csharp
internal interface IVirtualTexturePageBackend
{
    VirtualTextureCapabilities Capabilities { get; }

    PageUploadTicket EnqueuePageUploads(
        ReadOnlySpan<VirtualTexturePageUpload> uploads,
        long cacheGeneration);

    PageTablePublishTicket PublishMappings(
        ReadOnlySpan<VirtualTexturePageMapping> mappings,
        long tableGeneration);

    void RevokeMappings(
        ReadOnlySpan<VirtualPageId> pages,
        long tableGeneration);

    bool IsComplete(PageUploadTicket ticket);
}
```

The exact public types can change during implementation. The invariant is that the interface operates on logical page batches and completion tickets without leaking API-native objects.

### 8.2 OpenGL backend strategy

Implement in this order:

1. dense 2D-array cache banks and integer page table;
2. asynchronous PBO/shared-context tile uploads with fence-gated mapping publication;
3. optional hardware-sparse backing for very large cache banks;
4. optional direct sparse logical-image experiments only after robust residency-aware fallback and cross-vendor testing.

The current sparse whole-mip backend remains the fallback for nonvirtualized large textures.

### 8.3 Vulkan backend strategy

Implement in this order:

1. dense 2D-array cache banks integrated with the existing staging, transfer, timeline, and publication service;
2. per-layer layout transitions and copies;
3. timeline-gated page-table publication and frame-safe slot retirement;
4. hardware sparse image capability probing and memory pools;
5. optional sparse backing for cache banks or selected direct images.

Reuse the current Vulkan generation ledger, worker preparation, bounded transfer batching, descriptor/publication authority, and deferred resource retirement patterns rather than creating a parallel unbounded uploader.

## 9. Validation Requirements

### 9.1 OpenGL sparse mip validation

- base `GL_ARB_sparse_texture` page-aligned allocation;
- sparse-texture2 nonaligned base dimensions;
- every selected page-layout index used by policy;
- mip-tail atomic commitment and uncommit;
- edge commitment rectangles;
- promotion upload before base-level exposure;
- demotion exposure before uncommit;
- shared-context fence completion;
- stale storage-generation cancellation;
- exact-format compressed sparse fallback;
- `KHR_debug` clean runs on supported vendors;
- dense fallback on unsupported target/format pairs.

### 9.2 Vulkan dense streaming validation

- cold- and warm-cache imported scenes;
- generation cancellation and descriptor publication ordering;
- bounded worker, transfer, completion, and retirement work;
- exact frame readiness behavior for visible textures;
- low-memory retries;
- device-loss cancellation and cleanup;
- validation-layer clean runs.

### 9.3 Vulkan sparse validation

- dedicated and combined sparse queue families;
- exact-format property rejection and fallback;
- non-tail image block binds;
- single and per-layer mip tails;
- metadata aspect binding when reported;
- bind-before-copy semaphore ordering;
- upload-before-mapping publication;
- fallback-before-unbind ordering;
- exact bound-byte accounting;
- cancellation while bind or copy work is in flight;
- device loss with outstanding sparse ownership.

### 9.4 SVT validation

- bilinear and trilinear continuity at page boundaries;
- repeat, mirror, and clamp border correctness;
- compressed page block alignment;
- oblique surfaces and anisotropic filtering limits;
- high-speed camera movement and camera teleport;
- dual-eye page divergence and union;
- foveation priority changes;
- cache exhaustion, eviction, and slot-generation reuse;
- old page-table versions referencing retired slots;
- stale I/O and upload cancellation;
- missing-page ancestor stability;
- feedback overflow and bounded compaction;
- no synchronous render-thread feedback readback;
- exact dense-cache memory accounting.

## 10. Implementation Order

1. Close validation of current OpenGL and Vulkan mip streaming.
2. Correct capability terminology and backend telemetry.
3. Add page-addressable cooked texture payloads and fallback mips.
4. Add dense per-format physical cache banks on both APIs.
5. Add a versioned virtual page table and manual page requests.
6. Add shader sampling helpers with borders and ancestor fallback.
7. Add GPU feedback, asynchronous resolve/readback, and prediction.
8. Validate stereo/foveated behavior and cache pressure.
9. Add Vulkan hardware sparse residency as an optional backend.
10. Evaluate OpenGL/Vulkan sparse backing for physical caches.
11. Add RVT producers after SVT ownership and publication are stable.

This order produces a portable, deterministic system first and treats hardware sparse residency as an optimization rather than a prerequisite.