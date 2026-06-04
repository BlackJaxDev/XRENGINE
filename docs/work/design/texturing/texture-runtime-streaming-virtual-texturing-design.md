# Texture Runtime, Streaming, And Virtual Texturing Design

Last Updated: 2026-05-19
Status: canonical work design
Scope: imported texture streaming, cooked texture cache payloads, sparse residency, upload scheduling, virtual texturing roadmap, bindless deferred texturing integration, and neural compression integration.

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

- [Texture Compression And Cooked Texture Cache Design](texture-compression-and-cooked-cache-design.md)

Companion trackers:

- [Texture Compression And Cooked Texture Cache TODO](../../todo/texturing/texture-compression-and-cooked-cache-todo.md)

## Summary

XRENGINE now has a working v1 texture runtime: imported textures are registered with a streaming manager, source data can come from metadata-first cooked cache assets, uploads are budgeted and generation-gated, and OpenGL can use sparse texture mip residency when the hardware and texture dimensions allow it.

That implementation is not full virtual texturing yet. The active sparse path can avoid committing the full logical mip chain, but it does not have GPU page feedback, a physical tile cache, page-table sampling, UDIM-scale page indirection, or runtime virtual texture generation. This document treats the existing implementation as the v1 foundation and defines the next architecture steps toward streaming virtual textures, bindless material resolve, Vulkan parity, and optional neural compression.

## Current Implementation Snapshot

The current runtime is split into focused services:

- `ImportedTextureStreamingManager` is the frame-level coordinator. It collects snapshots, asks policy for desired residency, queues transitions, finalizes sparse exposure, publishes telemetry, and emits summaries.
- `TextureStreamingRegistry` owns weak texture records, main-view usage recording, material binding observations, compaction, and immutable snapshots.
- `TextureResidencyPolicy` owns deterministic decisions: desired resident size, budget fitting, role multipliers, priority scoring, fairness groups, transition reason text, promotion fade, and sparse page selection.
- `TextureTransitionQueue` owns pending transition replacement, stale transition repair, lifecycle timestamps, cancellation, and pending-state reset.
- `TextureUploadScheduler` owns progressive upload queueing, priority ordering, duplicate coalescing, active-slot gates, frame budgets, generation cancellation, and queue-wait telemetry.
- `TextureResidencyState` centralizes mutable sparse/dense residency fields on `XRTexture2D` while public properties keep `SetField(...)` mutation semantics.
- `GLTieredTextureResidencyBackend` is the compatibility backend. It swaps resident mip chains or resident dimensions.
- `GLSparseTextureResidencyBackend` is the preferred OpenGL backend for eligible imported `XRTexture2D` assets. It allocates logical sparse texture storage and commits only the resident mip range.

The active source path is:

- First import may decode the original source file.
- Fresh cache paths prefer `AssetTextureStreamingSource`.
- Stale, missing, or unusable cache entries fall back to `ThirdPartyTextureStreamingSource`.
- A short-lived resident-data reuse cache lets superseded transitions reuse compatible prepared mip data.
- Cooked cache usability is metadata-first and no longer requires hydrating resident mips just to decide whether an asset is streamable.

## Current Sparse Residency Reality

OpenGL sparse support is implemented around `GL_ARB_sparse_texture`:

- Renderer init probes `GL_ARB_sparse_texture` and `GL_ARB_sparse_texture2`.
- It queries `GL_VIRTUAL_PAGE_SIZE_X_ARB` and `GL_VIRTUAL_PAGE_SIZE_Y_ARB` for `Rgba8`.
- Textures whose logical dimensions are not page-aligned use the tiered fallback.
- Sparse storage is created by setting `GL_TEXTURE_SPARSE_ARB = GL_TRUE` before `glTextureStorage2D`.
- The backend queries `GL_NUM_SPARSE_LEVELS_ARB` and treats the mip tail as an atomic commitment region.
- Promotions commit and upload new mips before lowering `GL_TEXTURE_BASE_LEVEL`.
- Demotions expose the lower-detail range before uncommitting inaccessible high-detail mips.
- Shared-context sparse promotions are fence-gated. The render thread only exposes newly uploaded mips after the fence signals.
- VRAM accounting uses committed bytes, not the logical full texture size.

Partial sparse page machinery exists, but it is not active by default. The code has:

- `SparseTextureStreamingPageSelection`
- `SparseTextureStreamingPageRegion`
- UV-bounds-derived page selection in `RenderableMesh`
- partial region commit/uncommit helpers in `GLTexture2D.SparseStreaming.cs`
- byte estimation for partial sparse regions
- texture-streaming telemetry fields for current and desired page coverage

The current policy intentionally returns full page coverage because `EnablePartialSparsePageResidency` is `false`. The previous UV-bounds-only approach is too coarse for default use: it does not account for material UV transforms, wrap modes, anisotropic/filter guard bands, shader-generated UVs, virtual-geometry visibility, or rapid camera movement. Until page requests are driven by actual sampling domains or feedback, page-level sparse residency must remain opt-in or disabled.

## What This Is Not Yet

The current runtime is not a full SVT/MegaTexture system.

Missing pieces:

- GPU-driven texture-page feedback.
- Per-frame page request resolve.
- A physical tile cache shared across many logical textures.
- Shader-side virtual page table lookup.
- Page-addressable cooked texture blobs.
- UDIM/material-set page indirection.
- Per-eye page residency for VR.
- Runtime virtual textures generated from terrain, decals, splines, or procedural materials.

The current runtime is also not bindless deferred texturing yet. `GPUMaterialTable` and descriptor-indexing groundwork exist elsewhere, but the classic geometry pass still materializes material properties in the usual render paths.

Neural texture compression is still a future asset-pipeline feature. It should not be wired into material shaders until the cooked asset contract, validation metrics, and fallback paths exist.

## Design Invariants

### Stable Material Texture Identity

Materials should keep stable `XRTexture` references. Streaming may change resident mips, committed sparse pages, internal GL storage, or backend state, but material slots should not churn object references unless the asset itself changes.

### Sampling Safety

Sampling must never reach missing or uncommitted data.

- Progressive uploads hide partially uploaded mips.
- Sparse textures clamp `GL_TEXTURE_BASE_LEVEL` and `GL_TEXTURE_MAX_LEVEL` to exposed resident data.
- Sparse promotion lowers the base level only after commit, upload, and fence completion.
- Sparse demotion must make the target lower-detail range valid before high-detail uncommit.

### Generation-Gated GPU Work

Every queued upload or sparse transition captures the texture storage generation it was prepared against. If storage is resized, recreated, deleted, replaced by external memory, or switched between sparse and dense storage, stale work must cancel or restart.

### Renderer-Neutral Policy

Policy code should not know GL handles, Vulkan image layouts, or backend-specific sparse flags. It should speak in logical dimensions, resident size, base mip, page selection, estimated committed bytes, priority, deadline class, and telemetry.

### Metadata-First Source Loading

Runtime streaming must inspect compact cache metadata before reading texture blobs. Full YAML hydration, raw source decode, or full mip-chain reads are fallback paths, not the warm-cache steady state.

### Hot-Path Allocation Discipline

Per-frame visibility, scoring, upload submission, and render-thread upload code are hot paths. They must avoid LINQ, captured closures, boxing, string construction, and transient heap allocations after warmup unless profiling proves the cost is harmless.

## Runtime Flow

1. Imported materials register texture assets with `ImportedTextureStreamingManager`.
2. The registry tracks source path, texture role, backend, current residency, pending transitions, and last usage.
3. Main non-shadow passes record visible usage through `ImportedTextureStreamingUsage`.
4. Policy computes desired resident size and page selection from projected pixel span, screen coverage, distance, UV density, sampler role, recency, fairness, and VRAM pressure.
5. The transition queue coalesces identical work and cancels superseded work.
6. The source layer loads the selected mip range from cooked cache where possible.
7. The backend applies the transition:
   - tiered backend uploads a resident mip chain into dense storage
   - sparse backend commits sparse pages/mips, uploads data, and exposes sampling only after safe completion
8. Telemetry and logs report desired residency, queue wait, upload timing, committed bytes, validation failures, binding risk, fallback use, and VRAM summaries.

## Cooked Texture Cache

The implemented cooked payload uses the streamable mip section in `XRTexture2D.StreamingPayload.cs`.

Current properties:

- Magic: `0x58525453` (`XRTS`)
- Per-mip descriptors with width, height, format, byte offset, and byte length
- Explicit preview base mip index
- Selected mip-range reads
- Metadata-first streamability checks
- Uncompressed `Rgba8` mip blobs as the first portable format

Current limitations:

- Color-space metadata is still incomplete.
- Texture role metadata is not complete enough for all policy and compression decisions.
- The payload is mip-addressable, not page-addressable.
- BCn payloads are future work and must be gated by dependency/license review plus backend capability checks.
- Sparse compressed page commitment requires `GL_ARB_sparse_texture2`, which has narrower hardware coverage than base sparse textures.

Target upgrades:

- Add color space and texture role to the manifest.
- Add optional page descriptors for page-addressable SVT.
- Add BC7/BC5/BC4 payload variants where platform support and license review allow.
- Keep an `Rgba8` fallback for sparse paths that only support `GL_ARB_sparse_texture`.
- Make cache logs distinguish file I/O, manifest parse, blob copy, CPU conversion, and GPU upload time.

## Full Streaming Virtual Textures

SVT is the natural next major step after sparse mip residency.

Target architecture:

- A logical virtual texture owns source dimensions, mip count, format, tile size, and a stable texture asset identity.
- A physical tile cache owns resident GPU memory. It is budgeted globally and reused across many virtual textures.
- A page table maps `(texture id, mip, tile x, tile y)` to physical cache coordinates or fallback pages.
- A feedback pass records visible page requests from actual screen-space sampling demand.
- A resolver filters, expands, and prioritizes page requests.
- A streamer reads page blobs from cooked assets and uploads them to the physical cache.
- Shaders sample through the page table and fall back to coarser resident mips while fine pages stream in.

Feedback should include guard bands:

- texture filtering footprint
- anisotropy
- material UV transform
- wrap mode
- mip bias
- camera velocity
- stereo/per-eye visibility

SVT must keep the tiered and sparse-mip paths as fallbacks. It should be opt-in per asset class until tooling and debug views are mature.

## Runtime Virtual Textures

Runtime virtual textures are a different feature from imported-texture SVT.

RVT target use cases:

- terrain/object blending
- spline paths on landscapes
- procedural terrain material caches
- large decal projection caches
- baked material composition for repeated landscape shading

RVT should share concepts with SVT, such as page size, page residency, and debug views, but its data source is GPU rendering rather than cooked disk blobs. It belongs in a later phase after SVT page-cache infrastructure exists.

## Bindless Deferred Texturing

Bindless deferred texturing remains the best renderer-level partner for virtual texturing.

Target direction:

- The opaque deferred geometry pass writes geometry data and material id instead of sampling material textures.
- A material resolve pass fetches material data through a GPU material table.
- Compatibility mode writes the existing `AlbedoOpacity`, `Normal`, and `RMSE` buffers so downstream lighting and decals keep working.
- Native mode lets lighting, decals, and material modifiers consume bindless material data directly.

The material fetch contract must be API-neutral. Vulkan can use descriptor indexing and runtime descriptor arrays. OpenGL support needs explicit extension gating for bindless handles and a fallback path that keeps the classic materialized G-buffer.

Bindless deferred should not be a prerequisite for the current texture streamer, but it will make SVT and neural feature decode much cleaner because material fetch work moves out of the overdraw-heavy geometry pass.

## Neural Texture Compression

Neural texture compression should enter as an asset-pipeline feature, not as an ad hoc shader experiment.

Recommended modes:

1. Decode-on-load or cook-time reconstruction to conventional BCn textures. This is the first shippable path because current shaders and material bindings stay intact.
2. Learned feature texture decode in the bindless material resolve pass. This becomes viable after bindless deferred texturing is real.
3. High-end direct latent decode during sampling. This is experimental and requires explicit capability, project, and content opt-in.

Neural compression must remain selective. Good candidates are large terrain/material sets, repeated environment kits, and memory-dominant hero environments. It should not become the default for every texture channel.

The cooked neural asset should include:

- source bundle hash
- channel conventions and color space
- decoder/training profile id
- feature textures or latent grids
- decoder weights
- prebuilt conventional fallback payloads
- quality metrics from the cook step

## Vulkan Parity

Vulkan should consume the same policy and telemetry contract:

- desired resident size
- desired page selection
- committed byte estimate
- upload priority class
- source version
- generation id
- queue wait and execution timing

Vulkan-specific work belongs below the backend layer:

- sparse image creation and memory binding
- transfer queue upload and barriers
- image layout transitions
- descriptor indexing for bindless material tables
- residency telemetry mapped back into renderer-neutral records

OpenGL remains the first production implementation, but new policy fields should be rejected if they leak OpenGL concepts above `ITextureResidencyBackend`.

## Diagnostics And Tooling

`log_textures.txt` is the primary runtime diagnostic surface. It should continue to report:

- cache hit/miss/stale/fallback/write/read timing
- imported preview queue and ready states
- visibility and material binding observations
- desired residency and transition reasons
- transition queueing, coalescing, cancellation, application, and finalization
- upload chunks and slow upload events
- storage allocation/recreation and validation failures
- fallback binding and binding risk
- VRAM pressure and summaries

The ImGui texture streaming panel should remain the live view for:

- source path and texture name
- logical dimensions
- resident size or base mip
- current and desired committed bytes
- backend
- visibility state
- pending transition state
- priority score
- queue wait
- upload time
- current and desired page coverage

SVT and RVT add required debug views:

- page-table visualization
- physical tile cache occupancy
- feedback heatmap
- missing-page fallback heatmap
- per-texture residency history
- per-eye residency in VR

## Validation Strategy

Current v1 validation must close before the next architecture leap:

- cold-cache Sponza startup
- warm-cache Sponza startup
- low VRAM budget demotion run
- shadow-heavy scene with pending texture promotions
- unsupported sparse hardware or forced-tiered fallback
- page-alignment fallback for NPOT/non-aligned textures
- texture log schema review
- hot-path allocation audit

Future SVT validation adds:

- camera sweep over a large tiled material
- high-speed camera movement over UV-wrapped assets
- stereo page request divergence
- missing-page fallback visual stability
- cache eviction under pressure
- page feedback latency
- physical cache fragmentation

## Risks

- Sparse driver behavior differs across vendors. Keep tiered fallback mandatory.
- Page-level sparse commits can expose black holes if the request domain is wrong. Default to full coverage until feedback or full material sampling domains are available.
- Cooked compression can complicate sparse residency. Gate BCn sparse by `GL_ARB_sparse_texture2` and retain `Rgba8`.
- Bindless deferred changes decal and lighting assumptions. Ship compatibility resolve first.
- Neural compression can trade memory pressure for shader cost. Start with decode-on-load and require metric-gated rollout.
- Runtime texture work competes with shadows, shader compilation, mesh uploads, and presentation. Shared budget telemetry is part of correctness, not just performance reporting.
