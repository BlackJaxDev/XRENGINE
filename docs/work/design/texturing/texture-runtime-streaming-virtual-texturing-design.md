# Texture Runtime, Streaming, And Virtual Texturing Design

Last Updated: 2026-09-02
Status: canonical work design
Scope: imported texture streaming, cooked texture cache payloads, dense and sparse residency, upload scheduling, streaming virtual texturing, runtime virtual texturing, bindless deferred texturing integration, and neural compression integration.

Supersedes:

- [Texture Management Runtime Design](texture-management-runtime-design.md)
- [Sparse Texture Streaming Plan](sparse-texture-streaming-plan.md)
- [Bindless Deferred Texturing Plan](bindless-deferred-texturing-plan.md)
- [Neural Texture Compression Implementation Plan](neural%20texture%20compression.md)
- [Texture Management Runtime TODO](../../todo/texturing/texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](../../todo/texturing/texture-streaming-cooked-cache-todo.md)
- [Texture Streaming Consolidation TODO](../../todo/texturing/texture-streaming-consolidation-todo.md)

Execution tracker:

- [Texture Runtime, Streaming, And Virtual Texturing TODO](../../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)

Companion designs:

- [Sparse Residency And Streaming Virtual Texturing Backend Guide](sparse-residency-and-svt-backend-guide.md)
- [Texture Compression And Cooked Texture Cache Design](texture-compression-and-cooked-cache-design.md)

Companion trackers:

- [Texture Compression And Cooked Texture Cache TODO](../../todo/texturing/texture-compression-and-cooked-cache-todo.md)

## Summary

XRENGINE has a working v1 texture runtime. Imported textures register with a streaming manager, metadata-first cooked cache assets can provide selected mip ranges, uploads are budgeted and generation-gated, OpenGL can use hardware sparse whole-mip residency, and Vulkan has a dense synchronized upload path with worker preparation, transfer submission, GPU-completion-gated descriptor publication, and deferred retirement.

That implementation is not full virtual texturing. The active paths stream mip ranges, not arbitrary logical texture pages selected by GPU feedback. XRENGINE does not yet have a page-addressable cooked payload, shared physical tile cache, virtual page table, shader-side page-table sampling, page feedback and resolve, UDIM-scale indirection, or runtime-generated virtual texture pages.

The production cross-API SVT target is a software-indirected system built around dense physical tile cache banks and a versioned page table. Hardware sparse resources are optional backend optimizations. They must not define the portable asset format, logical page size, page identity, shader contract, or correctness fallback.

Normative API details and call ordering live in the [Sparse Residency And Streaming Virtual Texturing Backend Guide](sparse-residency-and-svt-backend-guide.md).

## Terminology And Feature Tiers

These terms are not interchangeable:

- **Dense mip streaming:** create or publish an ordinary fully backed texture containing a selected resident mip range.
- **Hardware sparse mip residency:** keep one stable logical image while physically committing selected complete mips.
- **Hardware sparse page residency:** commit device-defined regions inside independently sparse-manageable mips.
- **Streaming virtual texturing (SVT):** map engine-defined logical pages through a virtual page table into a shared physical tile cache, driven by sampling feedback and backed by page-addressable cooked data.
- **Runtime virtual texturing (RVT):** use the same virtual-page infrastructure, but render or generate page contents at runtime rather than reading cooked page blobs.

Sparse residency is a memory-binding mechanism. It does not by itself provide the asset addressing, physical tile reuse, filtering borders, page-table fallback, feedback, or eviction policy required by SVT.

## Current Backend Capability Matrix

| Capability | OpenGL | Vulkan |
|---|---:|---:|
| Dense/tiered mip streaming | Implemented | Implemented |
| Generation-gated asynchronous preparation | Implemented | Implemented |
| GPU-completion-gated publication | Implemented for shared-context sparse promotion | Implemented for dense image/descriptor publication |
| Hardware sparse whole-mip residency | Implemented for eligible imported `XRTexture2D` assets | Not implemented |
| Hardware sparse partial-page residency | Scaffolded; disabled by policy | Not implemented |
| Page-addressable cooked payload | Not implemented | Not implemented |
| Shared physical tile cache | Not implemented | Not implemented |
| Virtual page table | Not implemented | Not implemented |
| GPU page feedback and resolve | Not implemented | Not implemented |
| Full SVT | Not implemented | Not implemented |
| RVT page production | Not implemented | Not implemented |

## Current Implementation Snapshot

The renderer-neutral runtime is split into focused services:

- `ImportedTextureStreamingManager` is the frame-level coordinator. It collects snapshots, asks policy for desired residency, queues transitions, finalizes safe publication, publishes telemetry, and emits summaries.
- `TextureStreamingRegistry` owns weak texture records, main-view usage recording, material binding observations, compaction, and immutable snapshots.
- `TextureResidencyPolicy` owns deterministic desired-residency decisions, budget fitting, role multipliers, priority scoring, fairness, transition reasons, promotion fade, and the current coarse sparse-page-selection hint.
- `TextureTransitionQueue` owns pending transition replacement, stale transition repair, lifecycle timestamps, cancellation, and pending-state reset.
- `TextureUploadScheduler` owns progressive upload queueing, priority ordering, duplicate coalescing, active-slot gates, frame budgets, generation cancellation, and queue-wait telemetry.
- `TextureResidencyState` centralizes mutable residency fields on `XRTexture2D` while public properties keep `SetField(...)` mutation semantics.

OpenGL backends:

- `GLTieredTextureResidencyBackend` is the dense compatibility backend. It swaps resident mip chains or resident dimensions.
- `GLSparseTextureResidencyBackend` is the preferred OpenGL backend for eligible imported textures. It allocates full logical sparse storage and commits only the required mip range.
- Shared-context sparse promotions upload and commit on a secondary context, insert a fence, and publish the finer sampling range only after render-thread fence completion and storage-generation validation.

Vulkan backends and services:

- `VulkanDenseTextureResidencyBackend` implements dense imported-texture residency and uses the same renderer-neutral policy inputs.
- `VulkanTextureUploadService` owns worker preparation, bounded staging and transfer work, generation tracking, transfer completion, descriptor publication, and retirement.
- `VulkanTextureStreamingBackendProvider` currently routes both default and nominal sparse requests to the dense backend.
- `VulkanSparseTextureStreamingService` explicitly reports true sparse image residency as unsupported and provides only dense compatibility behavior.

The active source path is:

- First import may decode the original source file.
- Fresh cooked cache paths prefer `AssetTextureStreamingSource`.
- Stale, missing, or unusable cache entries fall back to `ThirdPartyTextureStreamingSource`.
- A short-lived resident-data reuse cache lets superseded transitions reuse compatible prepared mip data.
- Cooked cache usability is metadata-first and no longer requires hydrating resident mip blobs merely to decide whether an asset is streamable.

## Current OpenGL Sparse Residency Reality

OpenGL sparse support is implemented around `GL_ARB_sparse_texture` and detects `GL_ARB_sparse_texture2`:

- Renderer initialization probes both extensions.
- It queries the number of virtual page layouts and X/Y page dimensions for `Rgba8`.
- The current implementation chooses the first usable page-size layout.
- Sparse storage is created by setting `GL_TEXTURE_SPARSE_ARB = GL_TRUE` and `GL_VIRTUAL_PAGE_SIZE_INDEX_ARB` before `glTextureStorage2D`.
- The backend queries `GL_NUM_SPARSE_LEVELS_ARB` and treats the mip tail as one atomic commitment region.
- Promotions commit and upload new data before lowering `GL_TEXTURE_BASE_LEVEL`.
- Demotions populate and expose the lower-detail range before uncommitting inaccessible high-detail mips.
- Shared-context sparse promotions are fence-gated.
- VRAM accounting uses an estimate of committed bytes rather than the full logical texture size.

The current implementation conservatively requires the logical base dimensions to be page-aligned even when `GL_ARB_sparse_texture2` is available. Base sparse textures require the applicable alignment restrictions. Sparse-texture2 permits arbitrary base dimensions while individual commitment rectangles must still obey page-origin and mip-edge rules. Relaxing the current gate is future implementation work and requires edge-commit validation.

Partial sparse page machinery exists, including:

- `SparseTextureStreamingPageSelection`;
- `SparseTextureStreamingPageRegion`;
- UV-bounds-derived page selection in `RenderableMesh`;
- partial region commit, uncommit, and upload helpers;
- byte estimation for partial sparse regions;
- telemetry fields for current and desired page coverage.

Policy intentionally returns full coverage because `EnablePartialSparsePageResidency` is disabled. One normalized UV rectangle is not a reliable sampling domain. It does not fully account for material UV transforms, wrap modes, anisotropic and filtering guard bands, normal or parallax perturbation, shader-generated UVs, disjoint visible islands, virtual geometry visibility, multiple instances, stereo divergence, or rapid camera motion.

Direct partial commitment should remain an optional bridge or diagnostic feature. It is not a prerequisite for portable SVT.

## Current Vulkan Dense Streaming Reality

Vulkan imported-texture streaming is not merely placeholder work. The dense path already provides:

- bounded asynchronous decode and cache reads;
- generation-gated acceptance and cancellation;
- worker-owned native preparation;
- bounded staging allocation and upload chunking;
- transfer or graphics queue submission;
- GPU completion polling without device-wide idle;
- descriptor publication only after completion and authority acquisition;
- exact-once callback handling;
- deferred staging and old-resource retirement;
- upload, queue, publication, and failure telemetry.

The remaining Vulkan v1 work is validation and render-tail closure, not basic backend creation.

True Vulkan sparse image residency is still absent. There is no sparse image capability probe, sparse queue selection, sparse block pool, mip-tail or metadata binding, `vkQueueBindSparse` dependency chain, partial sparse image upload path, or sparse unbind and reclamation path.

## What This Is Not Yet

The current runtime is not a full SVT/MegaTexture system. Missing pieces include:

- page-addressable cooked texture blobs;
- stable logical virtual page identifiers;
- GPU-generated page feedback;
- per-frame request deduplication and prioritization;
- a globally budgeted physical tile cache;
- a versioned virtual page table;
- shader-side page-table lookup and ancestor fallback;
- filtering borders and virtual trilinear sampling;
- frame-safe cache-slot reuse;
- UDIM/material-set page indirection;
- shared stereo/foveated page resolution;
- runtime page producers for terrain, decals, splines, or procedural materials.

The current runtime is also not bindless deferred texturing yet. `GPUMaterialTable` and descriptor-indexing groundwork exist elsewhere, but normal render paths still materialize material properties in the usual geometry and forward paths.

Neural texture compression remains a future asset-pipeline feature. It should not be added to material shaders until the cooked asset contract, quality metrics, backend capability contract, and conventional fallback paths exist.

## Design Invariants

### Stable Material And Virtual Texture Identity

Materials retain stable `XRTexture` or virtual-texture references. Streaming may change resident mips, committed hardware pages, backing image storage, cache slots, descriptors, or page-table mappings, but material slots must not churn logical references unless the asset itself changes.

### Sampling Safety

Sampling must never intentionally reach incomplete or unmapped data.

- Progressive dense uploads hide incomplete images or mip ranges.
- Sparse whole-mip textures clamp the sampling range to exposed resident data.
- OpenGL sparse promotion lowers the base level only after commit, upload, fence completion, and generation validation.
- Vulkan dense publication swaps descriptors only after transfer completion.
- SVT publishes a page-table entry only after its physical tile upload completes.
- Missing SVT pages resolve to valid resident ancestors, never to an uninitialized slot.

Correctness must not depend on the value returned by a nonresident OpenGL or Vulkan sparse read.

### Revoke Before Reuse

A cache slot or sparse memory block cannot be reused until all shader-visible references to the old owner have been replaced and all frames that may observe the old mapping have retired. Vulkan sparse memory additionally requires unbind completion before reuse.

### Generation-Gated GPU Work

Every queued read, decode, transcode, upload, sparse bind, page-table update, and publication captures relevant texture, storage, cache, source, and mapping generations. If ownership changes, stale work cancels or becomes nonpublishable.

### Renderer-Neutral Policy

Policy code must not know GL handles, Vulkan images, image layouts, queue families, memory offsets, or extension enums. It speaks in logical dimensions, mip quality, virtual page IDs, desired page sets, estimated or exact bytes, priority, deadline class, source version, generation, and completion state.

### Logical Tiles Are Not Hardware Pages

The cooked asset defines an engine logical tile interior and stored border. A backend maps those tiles to dense array layers or one or more hardware sparse pages. OpenGL page-layout indices and Vulkan sparse image granularity never become the portable logical tile contract.

### Metadata-First Source Loading

Runtime streaming inspects compact cache metadata before reading blobs. Full YAML hydration, source image decode, or full mip-chain reads are fallback paths rather than the warm-cache steady state.

### Hot-Path Allocation Discipline

Per-frame visibility, feedback resolution, scoring, transition queueing, upload submission, page-table updates, and render-thread publication must avoid LINQ, captured closures, boxing, string construction, and transient heap allocations after warmup unless profiling proves them harmless.

## Current Mip Streaming Flow

1. Imported materials register texture assets with `ImportedTextureStreamingManager`.
2. The registry tracks source path, texture role, backend, current residency, pending transitions, and last use.
3. Main non-shadow passes record visible usage through `ImportedTextureStreamingUsage`.
4. Policy computes desired resident quality from projected pixel span, screen coverage, distance, UV density, sampler role, recency, fairness, and memory pressure.
5. The transition queue coalesces identical work and cancels superseded work.
6. The source layer loads the selected mip range from the cooked cache when possible.
7. The backend applies the transition:
   - OpenGL or Vulkan dense backends create and publish a dense resident mip chain;
   - the OpenGL sparse backend commits sparse mips/pages, uploads data, and publishes the finer sampling range only after safe completion.
8. Telemetry reports desired residency, queue wait, upload timing, estimated or exact bytes, validation failures, binding risk, fallback use, and memory summaries.

## Cooked Texture Cache

The implemented cooked payload uses the streamable mip section in `XRTexture2D.StreamingPayload.cs`.

Current properties:

- Magic: `0x58525453` (`XRTS`).
- Per-mip descriptors with width, height, format, byte offset, and byte length.
- Explicit preview base mip index.
- Selected mip-range reads.
- Metadata-first streamability checks.
- Uncompressed `Rgba8` mip blobs as the first portable format.

Current limitations:

- Color-space metadata is incomplete.
- Texture-role metadata is not complete enough for every policy and compression decision.
- The payload is mip-addressable, not page-addressable.
- GPU-native BCn payloads and compressed uploads are not implemented.
- It does not distinguish logical tile interior dimensions from stored dimensions including borders.

Sparse compressed-format support must be queried for the exact target and internal format. Base `GL_ARB_sparse_texture` may expose compressed sparse layouts; `GL_ARB_sparse_texture2` does not make compressed sparse formats universally supported. Dense BCn physical cache banks remain the portable SVT strategy even when direct sparse BCn is unavailable.

Target upgrades:

- Add complete color space and texture role metadata.
- Add page-addressable descriptors with logical page identity, per-page offsets and lengths, format/block metadata, checksum, and source/cook generation.
- Define logical interior tile dimensions separately from stored dimensions including borders.
- Generate wrap-aware borders before GPU-native compression.
- Add BC7, BC5, and BC4 payload variants where platform support and dependency/license review allow.
- Keep a portable uncompressed fallback where required.
- Make cache logs distinguish file I/O, manifest parsing, blob copying, CPU conversion, GPU upload, and publication time.

## Full Streaming Virtual Textures

SVT is the next major architecture step after v1 mip streaming is validated.

### Logical asset

A logical virtual texture owns:

- stable texture and optional material-set identifiers;
- logical dimensions and mip count;
- logical interior tile size;
- stored tile size including borders;
- format, color space, role, and wrap policy;
- source and cooker generation;
- page-addressable payload descriptors;
- an always-resident fallback mip chain or ancestor set.

### Physical tile cache

The first production cache uses dense 2D-array texture banks:

- one tile slot per array layer;
- multiple banks when array-layer limits are reached;
- exact fixed memory accounting;
- per-format banks such as BC7 color, BC5 normal, BC4 scalar, and RGBA8 fallback;
- free-list or bitmap allocation;
- priority/LRU eviction with pinning and hysteresis;
- deferred slot reuse until old page-table versions retire.

Hardware sparse backing can be added later without changing logical page IDs or shader lookup semantics.

### Virtual page table

A page table maps `(texture id, layer/material-set id, mip, page x, page y)` to:

- physical cache bank;
- physical slot or array layer;
- resolved resident mip;
- valid and fallback state;
- mapping generation;
- optional format and material-set flags.

Use double- or triple-buffered table publication, or an equivalent versioned scheme, so mappings consumed by submitted frames are immutable.

### Feedback and resolve

A feedback pass records actual sampling demand. Feedback includes or permits reconstruction of:

- virtual texture and page ID;
- requested mip;
- sample count or screen importance;
- view, eye, and foveation priority;
- optional material role.

A GPU resolve pass deduplicates requests, aggregates importance, expands filtering and prediction neighborhoods, and emits a bounded compact request list. CPU readback uses a staged or persistently mapped ring with intentional latency and no render-thread wait.

### Page lifecycle

Promotion:

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

Eviction:

```text
Resident
→ AncestorMappingPublished
→ OldTableVersionsRetired
→ OptionalSparseUnbindComplete
→ CacheSlotReleased
→ Evicted
```

Failure at any stage retains a valid ancestor mapping. A partially uploaded slot is never published.

### Filtering

The virtual sampling helper:

1. computes LOD from derivatives of the original virtual UVs;
2. resolves the requested page or a resident ancestor;
3. remaps UVs into the physical tile interior;
4. uses stored borders for bilinear continuity;
5. resolves and samples the adjacent virtual mip independently;
6. performs virtual trilinear blending.

Initial anisotropy must be capped to the footprint supported by the chosen border. Repeat, mirror, and clamp borders are generated according to authored wrap behavior. Shader-generated or unbounded UV domains opt out or use conservative fallback behavior.

### VR and foveation

Physical pages are shared across views. Feedback retains eye/view identity for priority analysis, but identical logical pages are unioned before streaming.

Recommended order:

```text
HMD foveal samples
> HMD peripheral samples
> gameplay-critical auxiliary views
> desktop mirror
> reflections, probes, and background captures
```

The finest page required by any important view wins. Head and camera velocity expand the request neighborhood to hide feedback latency without duplicating per-eye physical caches.

## Vulkan Hardware Sparse Residency

True Vulkan sparse residency is an optional backend phase after dense SVT exists.

Required work includes:

- probe `sparseBinding`, `sparseResidencyImage2D`, and exact image format properties;
- select a queue family with `VK_QUEUE_SPARSE_BINDING_BIT`;
- create sparse-resident sampled images with the correct flags;
- query normal and sparse image memory requirements;
- implement device-local sparse block pools rather than one allocation per page;
- bind non-tail image blocks;
- bind opaque mip tails and implementation metadata aspects where reported;
- order `vkQueueBindSparse` before copies through explicit semaphore dependencies;
- publish mappings only after copy completion;
- publish fallback mappings and retire old frames before sparse unbind and memory reuse;
- account for bound blocks, tails, and metadata separately;
- validate combined and dedicated sparse queues, cancellation, and device loss.

Vulkan image layouts apply to image subresources rather than rectangular sparse pages. Dense 2D-array cache layers are therefore the recommended portable SVT baseline because each tile layer can transition and upload independently. Direct sparse logical images may use `VK_IMAGE_LAYOUT_GENERAL` or another proven layout strategy, but remain an advanced optimization.

## Runtime Virtual Textures

RVT is distinct from imported-texture SVT. Its page data source is GPU rendering or compute generation rather than cooked blobs.

Target use cases:

- terrain and object blending;
- spline paths and roads;
- procedural landscape material caches;
- large decal projection caches;
- baked repeated landscape shading.

RVT should reuse logical page IDs, physical cache banks, page-table publication, eviction, telemetry, and debug views where practical. It adds page producers, dirty-region tracking, render-work scheduling, and temporal reuse.

## Bindless Deferred Texturing

Bindless deferred texturing remains a useful renderer-level partner for virtual texturing.

Target direction:

- The opaque deferred geometry pass writes geometry data and material ID rather than sampling every material texture.
- A material resolve pass fetches material records through a GPU table.
- Compatibility mode reconstructs existing `AlbedoOpacity`, `Normal`, and `RMSE` outputs for downstream lighting and decals.
- Native mode lets later passes consume material records and virtual texture indirection directly.

The material fetch contract is API-neutral. Vulkan uses descriptor indexing and runtime arrays. OpenGL uses explicitly gated bindless support or retains the classic materialized G-buffer fallback.

Bindless deferred is not a prerequisite for v1 streaming or SVT, but it centralizes material fetch logic and makes page feedback, neural decode, and compatibility handling cleaner.

## Neural Texture Compression

Neural texture compression enters through the asset pipeline, not as an ad hoc shader experiment.

Recommended modes:

1. Decode-on-load or cook-time reconstruction to conventional BCn textures.
2. Learned feature-texture decode in a bindless material resolve pass.
3. Experimental direct latent decode on explicitly supported high-end hardware.

The cooked neural asset includes:

- source bundle hash;
- channel conventions and color space;
- decoder or training profile ID;
- feature textures or latent grids;
- decoder weights;
- conventional fallback payloads;
- cook-time quality metrics.

Neural compression remains selective and metric-gated. It must not become the default for every texture channel.

## Diagnostics And Byte Accounting

`log_textures.txt` remains the primary runtime diagnostic surface. It should report:

- cache hit, miss, stale, fallback, write, and read timing;
- preview and promotion queue state;
- visibility and binding observations;
- desired mip or page residency and transition reasons;
- queueing, coalescing, cancellation, submission, completion, publication, and retirement;
- storage allocation and recreation;
- fallback binding and binding risk;
- page faults, ancestor fallback, churn, eviction, and feedback overflow;
- exact or estimated memory fields with explicit semantics.

Use distinct metrics:

- `LogicalPayloadBytes`;
- `EstimatedSparsePhysicalBytes` for OpenGL sparse residency;
- `AllocatedPhysicalCacheBytes` for dense cache banks;
- `VulkanBoundSparseBytes` for bound blocks, tails, and metadata;
- staging and in-flight transfer bytes.

The ImGui panel should add:

- physical cache occupancy;
- page-table version and visualization;
- feedback and fault heatmaps;
- missing-page fallback heatmap;
- per-texture residency history;
- per-eye/foveation demand;
- eviction history and delayed slot reuse.

## Validation Strategy

Current v1 validation must close before enabling finer residency:

- cold- and warm-cache Sponza startup on OpenGL and Vulkan;
- low-memory-budget demotion;
- shadow-heavy scenes with pending promotions;
- unsupported sparse hardware or forced dense fallback;
- base sparse and sparse-texture2 dimension cases;
- promotion-after-demotion;
- stale storage generation and cancellation;
- texture log schema and hot-path allocation audit;
- Vulkan publication and retirement tail validation;
- OpenGL `KHR_debug` and Vulkan validation-layer clean runs.

Future hardware sparse validation adds:

- OpenGL page-layout selection and edge commitments;
- mip-tail atomic behavior;
- exact compressed-format fallback;
- Vulkan exact-format probing;
- sparse block, tail, and metadata binds;
- bind-before-copy and fallback-before-unbind dependencies;
- dedicated and combined sparse queues;
- device loss with outstanding sparse ownership.

Future SVT validation adds:

- bilinear and trilinear page-boundary continuity;
- repeat, mirror, and clamp border correctness;
- compressed block alignment;
- oblique anisotropic surfaces;
- camera sweeps, high-speed motion, and teleport;
- stereo divergence and request union;
- foveation priority changes;
- cache exhaustion and eviction;
- page-table generation and cache-slot reuse safety;
- feedback overflow and latency;
- missing-page ancestor stability;
- exact dense-cache memory accounting.

## Risks

- Sparse driver behavior differs across vendors. Dense mip and dense physical-cache fallbacks remain mandatory.
- Direct page-level sparse commitment can expose undefined or zero data when the request domain is wrong. Keep it disabled until sampling-domain or feedback correctness is proven.
- Hardware page geometry differs from logical tile geometry. Do not bake API page shapes into portable assets.
- OpenGL sparse physical memory is estimated, not fully observable.
- Vulkan sparse binding, image layout, descriptor/page-table publication, and frame retirement form one ownership chain. Missing one dependency can cause stale or aliased sampling.
- Filtering borders and virtual trilinear sampling increase stored page size and shader cost. Treat them as correctness requirements rather than optional polish.
- Bindless deferred changes lighting and decal assumptions. Ship compatibility resolve first.
- Neural compression trades memory pressure for decode cost and quality risk. Start with conventional reconstruction and require metrics.
- Runtime texture work competes with shadows, shader compilation, mesh uploads, page feedback, and presentation. Shared budget telemetry is part of correctness, not merely performance reporting.