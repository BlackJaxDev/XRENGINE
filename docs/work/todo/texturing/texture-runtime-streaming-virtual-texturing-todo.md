# Texture Runtime, Streaming, And Virtual Texturing TODO

Last Updated: 2026-09-02
Status: canonical active texture residency and virtual-texturing roadmap.

Source design: [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
Backend guide: [Sparse Residency And Streaming Virtual Texturing Backend Guide](../../design/texturing/sparse-residency-and-svt-backend-guide.md)
Compression/cache design: [Texture Compression And Cooked Texture Cache Design](../../design/texturing/texture-compression-and-cooked-cache-design.md)
Compression/cache tracker: [Texture Compression And Cooked Texture Cache TODO](texture-compression-and-cooked-cache-todo.md)
Validation ledger: [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)
Vulkan upload/publication tail performance: [Vulkan render-tail code changes](../rendering/vulkan-core-hardening-and-device-loss-todo.md#7-bound-shadow-streaming-and-render-thread-tail-work)

Ownership: this document owns residency policy, OpenGL sparse residency, Vulkan sparse residency, portable SVT/RVT architecture, texture metadata, compression integration, bindless texturing, and feature validation. Workstream 08 owns the bounded render-thread cost of Vulkan upload preparation/finalization, transfer and graphics queue synchronization, descriptor publication, retirement, and command-buffer invalidation. Historical Vulkan upload trackers are evidence, not parallel execution owners.

Supersedes:

- [Texture Streaming Consolidation TODO](../COMPLETED/texture-streaming-consolidation-todo.md)
- [Texture Management Runtime TODO](../COMPLETED/texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](../COMPLETED/texture-streaming-cooked-cache-todo.md)

## Goal

Validate and harden the implemented mip-streaming runtime, then add a portable software-indirected streaming virtual texture system for OpenGL and Vulkan. Hardware sparse resources remain optional backend implementations and optimizations; they do not define the portable logical page, asset, page-table, or fallback contract.

## Current Baseline

Implemented today:

- [x] `ImportedTextureStreamingManager` frame-level orchestration.
- [x] `TextureStreamingRegistry` records, usage, material binding observations, snapshots, and compaction.
- [x] `TextureResidencyPolicy` desired residency, priority, role multipliers, fairness, cooldowns, pressure fitting, and promotion fade.
- [x] `TextureTransitionQueue` pending transition replacement, cancellation, stale repair, and lifecycle state.
- [x] `TextureUploadScheduler` priority queueing, duplicate coalescing, generation cancellation, budget gates, and upload telemetry.
- [x] `TextureResidencyState` for `XRTexture2D` runtime residency fields with `SetField(...)` property mutation.
- [x] `GLTieredTextureResidencyBackend` dense fallback.
- [x] `GLSparseTextureResidencyBackend` sparse whole-mip residency for eligible OpenGL `Rgba8` textures.
- [x] OpenGL shared-context sparse promotion with fence-gated exposure and storage-generation checks.
- [x] `VulkanDenseTextureResidencyBackend` renderer-neutral dense residency backend.
- [x] `VulkanTextureUploadService` worker preparation, bounded staging/transfer work, generation tracking, GPU-completion-gated descriptor publication, and deferred retirement.
- [x] Vulkan dense compatibility routing through `VulkanTextureStreamingBackendProvider`.
- [x] Metadata-first cooked texture streamability checks.
- [x] Cooked mip-addressable `XRTS` payload with preview mip and per-mip offsets.
- [x] Dedicated texture logging and ImGui texture streaming diagnostics.

Known current limits:

- [ ] Full cold/warm scene validation remains incomplete on both renderers.
- [ ] Unit-test execution remains blocked by unrelated duplicate `Engine` type compile errors in the unit-test project.
- [ ] OpenGL partial sparse page residency is scaffolded but disabled by policy.
- [ ] OpenGL currently applies a conservative page-aligned base-dimension gate even when `GL_ARB_sparse_texture2` is present.
- [ ] OpenGL sparse page-layout selection currently chooses the first usable layout rather than a documented cost-based policy.
- [ ] OpenGL committed-byte telemetry is an estimate, not exact physical allocation reporting.
- [ ] Cooked payloads are mip-addressable, not page-addressable.
- [ ] Color-space and texture-role metadata are incomplete.
- [ ] GPU-native compressed texture payloads and compressed uploads are not implemented.
- [ ] True Vulkan sparse image residency is not implemented.
- [ ] A shared physical tile cache, virtual page table, GPU page feedback, and SVT shader sampling are not implemented.
- [ ] Bindless deferred texturing is design-only.
- [ ] Neural texture compression is design-only.

## Phase 0: Canonical Documentation And Baseline

**Goal:** keep one canonical design, one execution tracker, one backend guide, and one validation ledger without losing historical implementation records.

- [ ] Create a dedicated implementation branch for the future runtime work, for example `texture-runtime-vt-roadmap`.
- [x] Add the canonical design doc.
- [x] Add this canonical phased TODO.
- [x] Add the cross-API sparse residency and SVT backend guide.
- [x] Mark superseded texturing design docs as historical with links to the canonical design.
- [x] Mark superseded implementation TODOs as historical with links to this TODO and the validation ledger.
- [x] Move v1 validation checks into the consolidated validation ledger.
- [x] Update `docs/work/README.md` and `docs/README.md` links for the canonical roadmap.
- [ ] Preserve or link the latest texture validation evidence:
  - [ ] cold-cache Sponza run;
  - [ ] warm-cache Sponza run;
  - [ ] `log_textures.txt`;
  - [ ] `log_opengl.txt`;
  - [ ] `log_vulkan.txt` where applicable;
  - [ ] `log_rendering.txt`;
  - [ ] profiler FPS-drop and render-stall logs.

## Phase 1: Validate And Close V1 Mip Streaming

**Goal:** prove the implemented OpenGL and Vulkan mip streamers are stable before enabling page-level residency or SVT.

### Build and unit validation

- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`;
  - [ ] `dotnet build .\XREngine.Runtime.Rendering.OpenGL\XREngine.Runtime.Rendering.OpenGL.csproj --no-restore`;
  - [ ] `dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`;
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`.
- [ ] Re-run targeted tests after the unrelated duplicate `Engine` issue is fixed:
  - [ ] `ImportedTextureStreamingPhaseTests`;
  - [ ] `ImportedTextureStreamingContractTests`;
  - [ ] `GLTexture2DContractTests`;
  - [ ] `RuntimeRenderingHostServicesTests`;
  - [ ] Vulkan upload/publication contract tests.

### OpenGL scene validation

- [ ] Run cold-cache Sponza startup and record:
  - [ ] raw source decode count;
  - [ ] cache miss/write count;
  - [ ] first visible preview time;
  - [ ] all visible previews resident time;
  - [ ] pending visible transition drain time;
  - [ ] upload validation failure count.
- [ ] Run warm-cache Sponza startup and record:
  - [ ] cache hit count;
  - [ ] slow cache read count;
  - [ ] worst `cacheReadMs`;
  - [ ] worst `cacheParseMs`;
  - [ ] promotion queue wait;
  - [ ] render-thread and shared-context upload timings.
- [ ] Confirm `Texture.UploadValidationFailed` remains zero in normal runs.
- [ ] Confirm no texture-upload `GL_INVALID_VALUE` appears in `log_opengl.txt`.
- [ ] Confirm promotion-after-demotion does not expose black or invalid mips.
- [ ] Confirm `Texture.SparseStateClearedForDenseUpload` appears only for legitimate sparse-to-dense handoffs.
- [ ] Confirm sparse transition finalization never blocks for multi-second spans.
- [ ] Confirm texture/shadow shared-budget counters explain delayed promotions.

### Vulkan scene validation

- [ ] Run cold-cache and warm-cache imported scenes with the Vulkan renderer.
- [ ] Confirm visible texture generations reach exact descriptor publication without a device-wide idle.
- [ ] Confirm stale generations cancel without publishing or prematurely retiring resources.
- [ ] Confirm worker preparation, staging, transfer batches, publication, and retirement remain bounded per frame.
- [ ] Confirm low-memory allocation pressure defers or fails cleanly without deadlock.
- [ ] Confirm device-loss cancellation releases or quarantines ownership correctly.
- [ ] Complete workstream 08 Phase 2 before claiming Vulkan v1 performance closure.
- [ ] Produce a Vulkan validation-layer-clean streaming run.

### Shared diagnostics

- [ ] Confirm `Texture.BindingRisk` entries are expected non-streaming paths or filed follow-ups.
- [ ] Confirm the ImGui texture streaming panel remains responsive with hundreds of tracked textures.
- [ ] Confirm no single upload item exceeds the configured texture budget by more than one permitted chunk.
- [ ] Confirm memory-pressure demotion is monotonic and explained by telemetry.

## Phase 2: Harden The Current Streamer And Capability Model

**Goal:** close metadata, capability, telemetry, and diagnostics gaps without changing the fundamental mip-residency model.

### Cooked metadata and source behavior

- [ ] Split cache timing logs into file I/O, manifest parse, mip blob copy, CPU conversion, GPU upload, and publication.
- [ ] Add complete color-space metadata to the cooked texture manifest.
- [ ] Add texture role metadata:
  - [ ] albedo/base color;
  - [ ] normal/bump;
  - [ ] roughness;
  - [ ] metallic;
  - [ ] mask/opacity/alpha;
  - [ ] emissive;
  - [ ] unknown.
- [ ] Include color space, role, format, and page selection in resident-data reuse cache keys where relevant.
- [ ] Add configurable minimum resident detail for hero assets.
- [ ] Add adaptive decode concurrency based on CPU count, current frame time, and active import pressure.
- [ ] Keep total decode concurrency bounded so preview urgency does not steal editor responsiveness.

### OpenGL sparse capability hardening

- [ ] Query and retain X/Y/Z geometry for every reported virtual page layout, not only X/Y.
- [ ] Define a page-layout selection policy using logical tile size, page count, edge waste, format block geometry, and validation evidence.
- [ ] Prefer a standardized sparse-texture2 index-zero layout where appropriate, but remain query-driven.
- [ ] Distinguish base `GL_ARB_sparse_texture` allocation restrictions from `GL_ARB_sparse_texture2` arbitrary base dimensions.
- [ ] Permit non-page-aligned base dimensions only on the sparse-texture2 path after edge-commit tests pass.
- [ ] Keep individual page commitment origins and extents compliant with selected page geometry and mip-edge rules.
- [ ] Replace documentation and capability assumptions that sparse compressed formats require `GL_ARB_sparse_texture2`.
- [ ] Query exact compressed formats with `GL_NUM_VIRTUAL_PAGE_SIZES_ARB` and fall back when the result is zero.
- [ ] Add `KHR_debug` diagnostics containing target, format, mip, rectangle, page shape/index, storage generation, and transition generation.
- [ ] Avoid release hot-path `glGetError` polling.

### Memory and telemetry semantics

- [ ] Rename or document OpenGL sparse committed bytes as an estimate.
- [ ] Add distinct telemetry for:
  - [ ] logical payload bytes;
  - [ ] estimated OpenGL sparse physical bytes;
  - [ ] exact dense physical-cache bytes;
  - [ ] exact Vulkan bound sparse bytes when implemented;
  - [ ] staging and transfer bytes in flight.
- [ ] Confirm `log_textures.txt` line schema is stable or document breaking changes for tooling.
- [ ] Add diagnostic coverage for black-surface cases that have no upload validation failure.

### Allocation audit

- [ ] Audit hot paths with the allocation reporting tool:
  - [ ] registry snapshot;
  - [ ] usage recording;
  - [ ] policy scoring;
  - [ ] transition queueing;
  - [ ] scheduler submit/execute;
  - [ ] OpenGL upload/finalization;
  - [ ] Vulkan preparation/publication;
  - [ ] diagnostics panel closed and open.
- [ ] Delete or archive obsolete partial-class files only after active serialization/import/sparse content has moved.

## Phase 3: Safe Optional OpenGL Partial Sparse Page Residency

**Goal:** turn the existing partial-page scaffold into a safe optional bridge and diagnostic feature. This phase is not a prerequisite for portable SVT.

- [ ] Keep partial sparse page residency disabled by default until this phase is validated.
- [ ] Replace mesh-UV-bounds-only requests with a material sampling-domain model covering:
  - [ ] UV transform;
  - [ ] wrap mode;
  - [ ] mip bias;
  - [ ] anisotropy and filter footprint;
  - [ ] normal/parallax UV perturbation policy;
  - [ ] shader-generated UV opt-out;
  - [ ] multiple visible instances and disjoint regions.
- [ ] Replace the single normalized rectangle when needed with a bounded page-set representation below policy.
- [ ] Add configurable filtering guard-band expansion.
- [ ] Add camera/head velocity and stereo guard-band expansion.
- [ ] Track selections per texture role when samplers use different UV transforms.
- [ ] Make page-selection hysteresis slower than mip promotion.
- [ ] Delay uncommit of previously resident pages through a short TTL and frame-retirement boundary.
- [ ] Keep the complete mip tail or another valid coarse fallback pinned.
- [ ] Never intentionally sample an uncommitted region; sparse-texture2 zero reads are not a material fallback.
- [ ] Add page-selection telemetry:
  - [ ] requested coverage;
  - [ ] committed coverage;
  - [ ] guard-band-expanded coverage;
  - [ ] pages committed/uncommitted this frame;
  - [ ] page faults and fallback events.
- [ ] Add tests for page-aligned region math, sparse-texture2 edge regions, and mip-tail behavior.
- [ ] Add tests that near-full requests normalize to full coverage.
- [ ] Add tests for repeat, mirror, clamp, out-of-range, and generated-UV fallback.
- [ ] Validate transformed/wrapped UVs, oblique anisotropic surfaces, high-speed motion, and stereo divergence.
- [ ] Enable only behind an explicit renderer setting after cross-vendor validation.

## Phase 4: Portable Streaming Virtual Textures

**Goal:** add software-indirected SVT so only demanded logical texture tiles occupy the shared physical cache on both renderers.

### 4.1 Logical asset and identity

- [ ] Define a virtual texture asset model:
  - [ ] stable texture ID;
  - [ ] optional material-set/layer ID;
  - [ ] logical dimensions;
  - [ ] logical mip count;
  - [ ] logical interior tile width/height;
  - [ ] stored tile width/height including borders;
  - [ ] border size and generation policy;
  - [ ] format, color space, texture role, and wrap mode;
  - [ ] source and cooker generation;
  - [ ] always-resident fallback mip chain or ancestor set.
- [ ] Define discrete `VirtualPageId` semantics for mip/page X/page Y/layer/source generation.
- [ ] Support synchronized material-page bundles where base color, normal, and scalar channels should retain matching detail.
- [ ] Keep logical tile dimensions independent of OpenGL and Vulkan hardware page geometry.

### 4.2 Page-addressable cooked payload

- [ ] Extend cooked texture payloads with page-addressable blobs:
  - [ ] page identity;
  - [ ] per-page offsets and byte lengths;
  - [ ] per-page format and compression block metadata;
  - [ ] stored row/slice metadata;
  - [ ] checksums/content hashes;
  - [ ] source/cook version;
  - [ ] fallback mip descriptors.
- [ ] Generate wrap-aware borders before GPU-native compression:
  - [ ] repeat;
  - [ ] mirror;
  - [ ] clamp.
- [ ] Keep stored dimensions and offsets block-aligned for BCn payloads.
- [ ] Add page-group metadata for material bundles where useful.
- [ ] Validate metadata-first page selection without hydrating unrelated blobs.

### 4.3 Physical tile cache

- [ ] Implement a globally budgeted physical cache using dense 2D-array texture banks first.
- [ ] Allocate one tile slot per array layer and multiple banks when layer limits are reached.
- [ ] Implement per-format or per-role pools:
  - [ ] BC7/sRGB and linear color;
  - [ ] BC5 normals;
  - [ ] BC4 scalar data;
  - [ ] RGBA8 portable fallback.
- [ ] Implement slot allocation, free lists/bitmaps, ownership generation, and pin state.
- [ ] Implement LRU/priority eviction with fairness, hysteresis, and minimum TTL.
- [ ] Track exact cache allocation and live slot occupancy.
- [ ] Prevent slot reuse until all page-table versions that reference the old owner retire.
- [ ] Keep hardware-sparse cache backing as a later optional backend.

### 4.4 Virtual page table

- [ ] Define a packed API-neutral page-table entry containing:
  - [ ] valid/resident state;
  - [ ] physical cache bank;
  - [ ] physical slot/layer;
  - [ ] resolved resident mip;
  - [ ] ancestor/fallback delta;
  - [ ] mapping generation;
  - [ ] optional format/material-set flags.
- [ ] Implement a CPU shadow representation.
- [ ] Implement OpenGL and Vulkan GPU representations using integer textures or buffers according to measured performance.
- [ ] Add update batching and dirty-range tracking.
- [ ] Add double/triple buffering or equivalent versioned publication.
- [ ] Ensure submitted frames observe immutable page-table versions.
- [ ] Publish ancestor mappings before eviction and slot reuse.

### 4.5 Page streaming lifecycle

- [ ] Implement the promotion lifecycle:
  - [ ] `Requested`;
  - [ ] `IoQueued`;
  - [ ] `PayloadReady`;
  - [ ] `CacheSlotReserved`;
  - [ ] `UploadSubmitted`;
  - [ ] `GpuComplete`;
  - [ ] `MappingPublished`;
  - [ ] `Resident`.
- [ ] Implement the eviction lifecycle:
  - [ ] `Resident`;
  - [ ] `AncestorMappingPublished`;
  - [ ] `OldTableVersionsRetired`;
  - [ ] optional sparse unbind complete;
  - [ ] `CacheSlotReleased`;
  - [ ] `Evicted`.
- [ ] Integrate async page reads with generation cancellation and stale-request cancellation.
- [ ] Reuse existing OpenGL shared-context/PBO and Vulkan staging/transfer/publication infrastructure.
- [ ] Batch uploads and page-table updates under shared render-work budgets.
- [ ] Never publish partially uploaded slots.

### 4.6 Shader sampling and filtering

- [ ] Add shared GLSL/SPIR-V virtual texture sampling helpers.
- [ ] Compute LOD from derivatives of original virtual UVs.
- [ ] Resolve requested pages or valid resident ancestors.
- [ ] Remap UVs into the physical tile interior.
- [ ] Use stored borders for bilinear continuity.
- [ ] Resolve both adjacent virtual mips and implement virtual trilinear blending.
- [ ] Establish an initial maximum anisotropy supported by the border width.
- [ ] Add fallback or opt-out for generated, unbounded, or unsupported UV domains.
- [ ] Ensure normal and scalar fallback values remain semantically valid through ancestor sampling.

### 4.7 GPU feedback and resolve

- [ ] Implement an initial feedback path using a reduced integer target, storage-buffer hash/bitset, or material resolve output.
- [ ] Include or reconstruct:
  - [ ] virtual page ID;
  - [ ] requested mip;
  - [ ] sample count/screen coverage;
  - [ ] eye/view/foveation priority;
  - [ ] optional texture role.
- [ ] Implement GPU request deduplication, aggregation, and bounded compaction.
- [ ] Add overflow detection and deterministic degradation.
- [ ] Read compact feedback through a multi-frame mapped/staged ring with no render-thread wait.
- [ ] Add guard-band and neighborhood expansion.
- [ ] Add camera/head linear and angular velocity prediction.
- [ ] Add starvation prevention across assets and material bundles.
- [ ] Handle camera teleports by preferring valid coarse fallback over an unbounded page flood.

### 4.8 VR and multi-view behavior

- [ ] Carry eye/view/foveation identity through feedback for priority analysis.
- [ ] Union identical logical page requests across both eyes before physical streaming.
- [ ] Select the finest page demanded by any important view.
- [ ] Prioritize:
  - [ ] HMD foveal;
  - [ ] HMD peripheral;
  - [ ] gameplay-critical auxiliary cameras;
  - [ ] desktop mirror;
  - [ ] reflections/probes/background captures.
- [ ] Expand prediction neighborhoods for head rotation and gaze/foveation motion.
- [ ] Do not create duplicate per-eye physical caches.

### 4.9 Debugging and validation

- [ ] Add debug views:
  - [ ] physical cache occupancy;
  - [ ] page table and version;
  - [ ] feedback heatmap;
  - [ ] missing-page/ancestor fallback heatmap;
  - [ ] eviction and delayed slot-reuse history;
  - [ ] per-eye/foveation demand.
- [ ] Add telemetry for page faults, fallback rate, upload latency, churn, evictions, cache hit rate, feedback overflow, and predicted versus requested pages.
- [ ] Validate with 16k and larger virtual textures, camera sweeps, high-speed motion, and teleport.
- [ ] Validate repeat/mirror/clamp borders, bilinear/trilinear seams, compressed block alignment, and oblique anisotropic surfaces.
- [ ] Validate cache exhaustion and old page-table versions referencing retired slots.
- [ ] Validate stereo divergence, request union, and foveated priority before enabling SVT for VR content.
- [ ] Keep dense mip and OpenGL sparse-mip fallbacks for nonvirtualized or unsupported assets.

## Phase 5A: Vulkan Dense Streaming Closure

**Goal:** validate and document the already implemented Vulkan dense mip-streaming backend before adding true sparse residency.

Implemented in source:

- [x] `VulkanDenseTextureResidencyBackend` implements `ITextureResidencyBackend`.
- [x] Metadata-first selected-mip loading and resident-data reuse.
- [x] Worker-side Vulkan image/staging preparation.
- [x] Generation-gated synchronized upload admission.
- [x] Bounded staging and transfer chunking.
- [x] Transfer/graphics submission and completion polling.
- [x] GPU-completion-gated descriptor publication.
- [x] Publication authority and exact-generation checks.
- [x] Staging and old-image resource retirement.
- [x] Dense fallback/provider routing.
- [x] Upload, queue, timing, publication, cancellation, and failure telemetry.

Remaining closure work:

- [ ] Validate warm-cache imported scene startup.
- [ ] Validate bounded preparation, transfer, completion, publication, and retirement tail cost.
- [ ] Validate exact frame-readiness behavior for visible textures.
- [ ] Validate stale-generation cancellation at every lifecycle state.
- [ ] Validate low-memory allocation-pressure retries.
- [ ] Validate graphics-queue fallback where no separate transfer path is available.
- [ ] Validate device loss with preparation, transfer, and publication work in flight.
- [ ] Confirm no Vulkan/OpenGL handles leak above renderer-neutral backend interfaces.
- [ ] Produce validation-layer-clean evidence and link it from the validation ledger.

## Phase 5B: Vulkan Hardware Sparse Residency

**Goal:** add true Vulkan sparse image binding without changing the renderer-neutral logical page or SVT contracts.

### Capabilities and queues

- [ ] Probe `sparseBinding` and `sparseResidencyImage2D`.
- [ ] Probe `shaderResourceResidency` only for optional residency-query shaders.
- [ ] Select at least one queue family with `VK_QUEUE_SPARSE_BINDING_BIT`.
- [ ] Support both combined graphics/sparse and dedicated sparse queue families.
- [ ] Query exact format/type/tiling/usage/sample-count support with `vkGetPhysicalDeviceSparseImageFormatProperties2`.
- [ ] Fall back to dense residency when the exact image configuration returns no sparse properties.

### Images and memory pools

- [ ] Create sampled sparse images with:
  - [ ] `VK_IMAGE_CREATE_SPARSE_BINDING_BIT`;
  - [ ] `VK_IMAGE_CREATE_SPARSE_RESIDENCY_BIT`;
  - [ ] sampled and transfer-destination usage;
  - [ ] optimal tiling where supported.
- [ ] Query normal and sparse image memory requirements.
- [ ] Implement device-local sparse block pools by compatible memory type.
- [ ] Do not allocate one `VkDeviceMemory` object per page.
- [ ] Track block owner, generation, pending bind/unbind state, and deferred reclamation.
- [ ] Bind non-tail regions with `VkSparseImageMemoryBindInfo`.
- [ ] Bind opaque mip tails with `VkSparseImageOpaqueMemoryBindInfo`.
- [ ] Handle single and per-array-layer mip tails.
- [ ] Bind implementation metadata aspects with `VK_SPARSE_MEMORY_BIND_METADATA_BIT` where reported.
- [ ] Account exactly for bound blocks, mip tails, and metadata allocations.

### Synchronization and publication

- [ ] Implement explicit bind-to-copy ordering:
  - [ ] reserve physical blocks;
  - [ ] submit `vkQueueBindSparse`;
  - [ ] signal bind-complete semaphore/timeline value;
  - [ ] make transfer/graphics copy wait for bind completion;
  - [ ] signal upload completion;
  - [ ] publish LOD/page-table state only after upload completion.
- [ ] Keep dependencies explicit even when bind and copy use the same queue.
- [ ] Define image layout strategy for direct sparse images.
- [ ] Prefer dense 2D-array SVT cache layers for portable per-tile transitions.
- [ ] Implement fallback publication before unbind:
  - [ ] publish ancestor/coarser mapping;
  - [ ] retire frames that can see the old mapping;
  - [ ] submit null-memory sparse unbind;
  - [ ] wait for unbind completion;
  - [ ] return blocks to the pool.
- [ ] Add cancellation and stale-generation handling while bind/copy/unbind work is in flight.
- [ ] Add device-loss ownership cleanup or quarantine rules.

### Tests and validation

- [ ] Add source-contract tests for capability and fallback boundaries.
- [ ] Test non-tail binds, edge blocks, mip tails, metadata binds, and array layers.
- [ ] Test bind-before-copy and upload-before-publication ordering.
- [ ] Test fallback-before-unbind and block-reuse ordering.
- [ ] Test dedicated and combined sparse queue configurations.
- [ ] Test exact bound-byte accounting under promotion/demotion pressure.
- [ ] Produce validation-layer-clean sparse runs on supported hardware.
- [ ] Keep the dense Vulkan backend mandatory as fallback.

## Phase 6: Bindless Deferred Texturing

**Goal:** move opaque deferred material texture sampling out of the geometry pass and provide a clean integration point for virtual-texture feedback and neural decode.

- [ ] Finalize an API-neutral deferred material record.
- [ ] Populate real texture handles/indices in `GPUMaterialTable`.
- [ ] Add Vulkan descriptor-indexed material texture arrays.
- [ ] Add OpenGL bindless texture support with explicit extension gating.
- [ ] Keep a classic materialized G-buffer fallback.
- [ ] Add geometry-only deferred attachments:
  - [ ] depth;
  - [ ] packed tangent frame or normal basis;
  - [ ] UV0;
  - [ ] depth/UV gradients as needed;
  - [ ] material ID;
  - [ ] transform ID.
- [ ] Add a compatibility material resolve pass that reconstructs `AlbedoOpacity`, `Normal`, and `RMSE`.
- [ ] Keep deferred decals working against reconstructed buffers in compatibility mode.
- [ ] Add texture residency gates so material records never reference invalid dense, sparse, or virtual data.
- [ ] Integrate SVT feedback generation with the material resolve path where useful.
- [ ] Add native bindless lighting mode after compatibility mode is stable.
- [ ] Validate non-stereo opaque deferred first.
- [ ] Add MSAA, stereo, transparent, and forward-only follow-ups after the core path is stable.

## Phase 7: Neural Texture Compression

**Goal:** add optional neural material compression through the asset pipeline.

- [ ] Define a canonical neural-eligible material bundle:
  - [ ] base color;
  - [ ] tangent normal;
  - [ ] roughness;
  - [ ] metallic;
  - [ ] ambient occlusion;
  - [ ] emissive;
  - [ ] color-space metadata;
  - [ ] mip/page policy.
- [ ] Add `XRNeuralMaterialAsset` and cook settings.
- [ ] Add an offline training/optimization tool under `Tools/`.
- [ ] Add metric output:
  - [ ] per-channel error;
  - [ ] perceptual image difference;
  - [ ] normal angular error;
  - [ ] frame-space material comparison captures.
- [ ] Ship decode-on-load or cook-time reconstruction to conventional BCn first.
- [ ] Require owner approval and dependency/license review before adding compression or training dependencies.
- [ ] Integrate conventional neural fallback textures with the current mip streamer and future SVT cache.
- [ ] Add feature-texture shader decode only after bindless deferred resolve is stable.
- [ ] Add direct latent decode only as an explicit high-end experimental path.

## Phase 8: Runtime Virtual Textures

**Goal:** add GPU-generated virtual texture pages for terrain, decals, splines, and procedural material caches after SVT cache ownership is stable.

- [ ] Define an RVT page-producer interface.
- [ ] Add terrain/landscape page producers.
- [ ] Add decal and spline projection producers.
- [ ] Reuse SVT page IDs, physical caches, page tables, publication, and eviction where practical.
- [ ] Add dirty-region tracking.
- [ ] Add page render scheduling under the shared render-work budget.
- [ ] Add page invalidation and temporal reuse.
- [ ] Add producer generation and stale-work cancellation.
- [ ] Add fallback when RVT page generation misses the current frame.
- [ ] Validate terrain-object blending and large decal cases.

## Phase 9: Stable Documentation And Closeout

**Goal:** promote durable architecture into stable guides after implementation and validation settle.

- [ ] Keep the canonical design, backend guide, TODO, and validation ledger synchronized.
- [ ] Promote final runtime texture architecture into `docs/architecture/rendering/` or `docs/developer-guides/`.
- [ ] Keep historical TODOs and design plans as ledgers only.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` when bindless or virtual-texture paths change pass invariants.
- [ ] Update user-facing setup docs for settings, flags, cache formats, and diagnostics.
- [ ] Refresh dependency and license docs after any compression/tooling dependency change.
- [ ] Link final OpenGL, Vulkan dense, Vulkan sparse, and SVT validation evidence.
- [ ] Merge the dedicated implementation branch after owner review.

### Documentation acceptance criteria

- [ ] “Virtual texturing” is never used as a synonym for sparse whole-mip residency.
- [ ] Sparse compressed-format support is described as exact-format query-driven, not as universally requiring `GL_ARB_sparse_texture2`.
- [ ] The implemented Vulkan dense backend is documented separately from future Vulkan sparse residency.
- [ ] Every promotion sequence publishes only after memory binding and GPU upload completion.
- [ ] Every eviction sequence revokes mappings before physical memory reuse.
- [ ] No portable path depends on nonresident hardware sparse read values.
- [ ] Logical tile dimensions remain independent of API hardware page dimensions.
- [ ] OpenGL mip-tail and Vulkan mip-tail/metadata behavior are documented.
- [ ] Filtering borders, virtual trilinear sampling, wrap behavior, and initial anisotropy limits are part of the SVT contract.
- [ ] Stereo feedback is unioned rather than creating duplicate physical pages.
- [ ] Both renderers retain explicit dense fallbacks for unsupported capabilities.
- [ ] Every memory field is labeled exact or estimated according to backend observability.