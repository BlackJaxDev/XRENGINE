# Texture Runtime, Streaming, And Virtual Texturing TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Texture Streaming](../../../architecture/rendering/texture-streaming.md), [Cooked Texture Payloads](../../../architecture/assets/cooked-texture-payloads.md)  Design: [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md), [Sparse Residency And Streaming Virtual Texturing Backend Guide](../../design/texturing/sparse-residency-and-svt-backend-guide.md)  Validation: [Texture Validation](../../testing/texturing/texture-validation.md)

## Current State

The mip-streaming runtime exists in `XREngine.Runtime.Rendering/Objects/Textures/2D/`. `ImportedTextureStreamingManager`, `TextureStreamingRegistry`, `TextureResidencyPolicy`, `TextureTransitionQueue`, `TextureUploadScheduler`, `TextureResidencyState`, OpenGL dense and sparse backends, and `VulkanDenseTextureResidencyBackend` are present. `VulkanTextureUploadService` owns dense imported texture upload and publication. Contract tests exist in `XREngine.UnitTests/Rendering/ImportedTextureStreamingContractTests.cs`, `ImportedTextureStreamingPhaseTests.cs`, and `GLTexture2DContractTests.cs`. Open scene validation remains open in the validation doc.

## Open Code Items

### Cooked metadata and source behavior

- [ ] Split cache timing logs into file I/O, manifest parse, mip blob copy, CPU conversion, GPU upload, and publication. Files or types: `TextureRuntimeDiagnostics`, `AssetTextureStreamingSource`, Vulkan upload diagnostics. Done when slow cache rows identify each timing stage.
- [ ] Add complete color-space metadata to the cooked texture manifest. Files or types: `XRTexture2D.StreamingPayload.cs`, cache manifest reader/writer. Done when resident data carries color-space metadata.
- [ ] Add texture role metadata for albedo/base color, normal/bump, roughness, metallic, mask/opacity/alpha, emissive, and unknown. Files or types: texture import settings, streaming manifest, policy input. Done when role affects policy and cache identity.
- [ ] Include color space, role, format, and page selection in resident-data reuse cache keys. Files or types: `TextureStreamingResidentDataReuseCache`. Done when incompatible resident data cannot be reused.
- [ ] Add configurable minimum resident detail for hero assets. Files or types: texture asset metadata, residency policy. Done when hero textures keep a configured floor.
- [ ] Add adaptive decode concurrency based on CPU count, current frame time, and active import pressure. Files or types: `ImportedTextureStreamingManager`, upload/decode scheduler. Done when preview urgency adapts without stealing editor responsiveness.
- [ ] Keep total decode concurrency bounded. Files or types: decode scheduler. Done when high-priority previews cannot create unbounded decode work.

### OpenGL sparse capability hardening

- [ ] Query and retain X/Y/Z geometry for every reported virtual page layout. Files or types: OpenGL sparse capability probing. Done when layout selection can use all dimensions.
- [ ] Define a page-layout selection policy. Use logical tile size, page count, edge waste, format block geometry, and validation evidence. Files or types: `GLSparseTextureResidencyBackend`. Done when layout selection is documented and deterministic.
- [ ] Prefer a standardized sparse-texture2 index-zero layout where appropriate while staying query-driven. Files or types: OpenGL sparse policy. Done when the policy is clear and does not assume universal support.
- [ ] Distinguish base `GL_ARB_sparse_texture` allocation restrictions from `GL_ARB_sparse_texture2` arbitrary base dimensions. Files or types: OpenGL capability gates. Done when non-page-aligned bases are gated correctly.
- [ ] Permit non-page-aligned base dimensions only on the sparse-texture2 path after edge-commit tests pass. Files or types: OpenGL sparse allocation. Done when edge mips are validated.
- [ ] Keep individual page commitment origins and extents compliant with selected page geometry and mip-edge rules. Files or types: sparse commit code. Done when commits never exceed legal rectangles.
- [ ] Replace assumptions that sparse compressed formats require `GL_ARB_sparse_texture2`. Files or types: capability docs and code comments. Done when support is exact-format query-driven.
- [ ] Query exact compressed formats with `GL_NUM_VIRTUAL_PAGE_SIZES_ARB` and fall back when the result is zero. Files or types: OpenGL capability probing. Done when unsupported compressed sparse formats cannot activate.
- [ ] Add `KHR_debug` diagnostics with target, format, mip, rectangle, page shape/index, storage generation, and transition generation. Files or types: OpenGL diagnostics. Done when upload failures include these fields.
- [ ] Avoid release hot-path `glGetError` polling. Files or types: OpenGL upload and sparse paths. Done when diagnostics use debug-only or event-based checks.

### Memory, telemetry, and allocation

- [ ] Rename or document OpenGL sparse committed bytes as an estimate. Files or types: telemetry names, docs, MCP rows. Done when users cannot mistake the estimate for exact allocation.
- [ ] Add telemetry for logical payload bytes, estimated OpenGL sparse physical bytes, exact dense physical-cache bytes, exact Vulkan bound sparse bytes when implemented, and staging/transfer bytes in flight. Files or types: `ImportedTextureStreamingTextureTelemetry`, MCP tools, ImGui panel. Done when each value is labeled exact or estimated.
- [ ] Stabilize the `log_textures.*` line schema or document breaking changes for tooling. Files or types: `TextureRuntimeDiagnostics`. Done when tooling can parse the schema.
- [ ] Add diagnostic coverage for black-surface cases that have no upload validation failure. Files or types: material binding diagnostics, texture diagnostics. Done when binding, shader, or lighting issues are separable from streaming failures.
- [ ] Audit registry snapshot, usage recording, policy scoring, transition queueing, scheduler submit/execute, OpenGL upload/finalization, Vulkan preparation/publication, and diagnostics panel open and closed paths for allocations. Files or types: hot-path texturing code and `Report-NewAllocations`. Done when avoidable LINQ, captures, boxing, transient lists, and formatting are removed or tracked.
- [ ] Delete or archive obsolete partial-class files after serialization, import, and sparse content moves. Files or types: `XRTexture2D` partial files and related docs. Done when file ownership is clear and no active content is lost.

### Optional OpenGL partial sparse page residency

- [ ] Keep partial sparse page residency disabled by default. Files or types: `ImportedTextureStreamingManager`, OpenGL sparse policy. Done when the feature requires an explicit renderer setting.
- [ ] Replace mesh-UV-bounds-only requests with a material sampling-domain model. Cover UV transform, wrap mode, mip bias, anisotropy and filter footprint, normal/parallax UV perturbation, shader-generated UV opt-out, multiple visible instances, and disjoint regions. Files or types: material visibility and residency policy. Done when page selection uses material sampling data.
- [ ] Replace the single normalized rectangle with a bounded page-set representation when needed. Files or types: `SparseTextureStreamingPageSelection`. Done when disjoint coverage is representable.
- [ ] Add configurable filtering, camera/head velocity, and stereo guard-band expansion. Files or types: sparse page policy. Done when fast motion and stereo divergence expand demand.
- [ ] Track selections per texture role when samplers use different UV transforms. Files or types: material binding observations. Done when role-specific sampling does not share wrong coverage.
- [ ] Make page-selection hysteresis slower than mip promotion and delay uncommit through a short TTL and frame-retirement boundary. Files or types: residency policy and sparse retirement. Done when page churn is bounded and submitted frames cannot sample freed pages.
- [ ] Keep the complete mip tail or another valid coarse fallback pinned. Files or types: sparse residency backend. Done when missing fine pages always sample a valid fallback.
- [ ] Prevent intentional sampling of uncommitted regions. Files or types: shader and page policy. Done when sparse-texture2 zero reads are never used as material fallback.
- [ ] Add page-selection telemetry for requested coverage, committed coverage, guard-band-expanded coverage, pages committed/uncommitted this frame, page faults, and fallback events. Files or types: telemetry and panel. Done when page decisions are visible.
- [ ] Add tests for page-aligned region math, sparse-texture2 edge regions, mip-tail behavior, near-full requests, repeat, mirror, clamp, out-of-range UVs, and generated-UV fallback. Files or types: `XREngine.UnitTests`. Done when page selection and fallback rules have deterministic coverage.

### Streaming virtual texture assets and payloads

- [ ] Define a virtual texture asset model. Include stable texture ID, optional material-set/layer ID, logical dimensions, logical mip count, logical tile and stored tile dimensions, border policy, format, color space, texture role, wrap mode, source/cooker generation, and fallback mips or ancestors. Files or types: new asset types. Done when virtual textures have stable identity.
- [ ] Define `VirtualPageId` semantics for mip, page X, page Y, layer, and source generation. Files or types: virtual texture page model. Done when page IDs are stable across cache and runtime.
- [ ] Support synchronized material-page bundles for base color, normal, and scalar channels. Files or types: virtual texture asset and scheduler. Done when bundles can retain matching detail.
- [ ] Keep logical tile dimensions independent of OpenGL and Vulkan hardware page geometry. Files or types: asset model and backend mapping. Done when API hardware pages do not define asset tile size.
- [ ] Extend cooked payloads with page-addressable blobs. Include page identity, per-page offsets and byte lengths, per-page format and compression block metadata, row/slice metadata, checksums, source/cook version, and fallback mip descriptors. Files or types: cooked payload reader/writer. Done when page reads can avoid unrelated blobs.
- [ ] Generate wrap-aware borders before GPU-native compression for repeat, mirror, and clamp. Files or types: cooker. Done when border pixels match wrap mode.
- [ ] Keep stored dimensions and offsets block-aligned for BCn payloads. Files or types: cooker and payload validation. Done when compressed pages align to block requirements.
- [ ] Add page-group metadata for material bundles where useful. Files or types: payload manifest. Done when bundled pages can stream together.
- [ ] Validate metadata-first page selection without hydrating unrelated blobs. Files or types: payload reader tests. Done when page selection reads only metadata.

### Physical tile cache and page table

- [ ] Implement a globally budgeted physical cache using dense 2D-array texture banks first. Files or types: virtual texture cache service. Done when physical tiles have a shared budget.
- [ ] Allocate one tile slot per array layer and create multiple banks when layer limits are reached. Files or types: cache allocator. Done when large caches scale across banks.
- [ ] Implement per-format or per-role pools for BC7/sRGB and linear color, BC5 normals, BC4 scalar data, and RGBA8 fallback. Files or types: cache allocator. Done when incompatible formats do not share slots.
- [ ] Implement slot allocation, free lists or bitmaps, ownership generation, and pin state. Files or types: cache allocator. Done when slot ownership is generation-safe.
- [ ] Implement LRU/priority eviction with fairness, hysteresis, and minimum TTL. Files or types: eviction policy. Done when pages evict predictably.
- [ ] Track exact cache allocation and live slot occupancy. Files or types: cache telemetry. Done when diagnostics show exact cache use.
- [ ] Prevent slot reuse until all page-table versions that reference the old owner retire. Files or types: cache and frame retirement. Done when submitted frames cannot sample reused slots.
- [ ] Keep hardware-sparse cache backing as a later optional backend. Files or types: architecture and capability gates. Done when dense 2D-array cache remains the portable baseline.
- [ ] Define a packed API-neutral page-table entry with valid/resident state, physical cache bank, physical slot/layer, resolved resident mip, ancestor/fallback delta, mapping generation, and optional format/material-set flags. Files or types: page-table model. Done when both backends can consume the entry.
- [ ] Implement a CPU shadow page table. Files or types: page table service. Done when updates are staged on CPU before GPU publication.
- [ ] Implement OpenGL and Vulkan GPU page-table representations with integer textures or buffers according to measured performance. Files or types: renderer backends. Done when shaders can sample the table.
- [ ] Add update batching, dirty-range tracking, and double or triple buffering or equivalent versioned publication. Files or types: page-table publication. Done when submitted frames observe immutable versions.
- [ ] Publish ancestor mappings before eviction and slot reuse. Files or types: page-table and cache eviction. Done when fallback is visible before fine page removal.

### Page streaming lifecycle

- [ ] Implement promotion states: `Requested`, `IoQueued`, `PayloadReady`, `CacheSlotReserved`, `UploadSubmitted`, `GpuComplete`, `MappingPublished`, and `Resident`. Files or types: virtual texture streaming service. Done when promotion can be diagnosed at each state.
- [ ] Implement eviction states: `Resident`, `AncestorMappingPublished`, `OldTableVersionsRetired`, optional sparse unbind complete, `CacheSlotReleased`, and `Evicted`. Files or types: virtual texture streaming service. Done when eviction is ordered.
- [ ] Integrate async page reads with generation cancellation and stale-request cancellation. Files or types: IO scheduler. Done when stale pages cannot publish.
- [ ] Reuse OpenGL shared-context/PBO and Vulkan staging/transfer/publication infrastructure. Files or types: backend upload services. Done when page uploads use existing synchronization rules.
- [ ] Batch uploads and page-table updates under shared render-work budgets. Files or types: render-work budget coordinator. Done when virtual texture work is bounded per frame.
- [ ] Prevent publication of partially uploaded slots. Files or types: page streaming service. Done when slots publish only after GPU completion.

### Shader sampling and GPU feedback

- [ ] Add shared GLSL/SPIR-V virtual texture sampling helpers. Files or types: shader library. Done when materials can sample virtual textures on both renderers.
- [ ] Compute LOD from derivatives of original virtual UVs. Files or types: shader helpers. Done when physical UV remapping does not change LOD choice.
- [ ] Resolve requested pages or valid resident ancestors. Files or types: shader helpers and page table. Done when missing fine pages sample valid ancestors.
- [ ] Remap UVs into the physical tile interior and use stored borders for bilinear continuity. Files or types: shader helpers and cooker. Done when tile seams are hidden.
- [ ] Resolve adjacent virtual mips and implement virtual trilinear blending. Files or types: shader helpers. Done when mip transitions are smooth.
- [ ] Establish an initial maximum anisotropy supported by the border width. Files or types: material and sampler policy. Done when unsupported anisotropy is clamped or disabled.
- [ ] Add fallback or opt-out for generated, unbounded, or unsupported UV domains. Files or types: material import and shader policy. Done when unsupported materials remain valid.
- [ ] Ensure normal and scalar fallback values remain semantically valid through ancestor sampling. Files or types: shader helpers. Done when fallback normals and masks are correct.
- [ ] Implement an initial feedback path using a reduced integer target, storage-buffer hash/bitset, or material resolve output. Files or types: feedback pass. Done when GPU can report demanded pages.
- [ ] Include or reconstruct virtual page ID, requested mip, sample count/screen coverage, eye/view/foveation priority, and optional texture role. Files or types: feedback data. Done when requests are prioritizable.
- [ ] Implement GPU request deduplication, aggregation, bounded compaction, overflow detection, and deterministic degradation. Files or types: feedback resolve. Done when feedback readback is bounded and overload is safe.
- [ ] Read compact feedback through a multi-frame mapped or staged ring with no render-thread wait. Files or types: backend readback. Done when feedback readback is asynchronous.
- [ ] Add guard-band and neighborhood expansion, camera/head velocity prediction, starvation prevention, and teleport handling. Files or types: feedback policy and scheduler. Done when fast motion and teleports stay stable.

### VR and multi-view behavior

- [ ] Carry eye, view, and foveation identity through feedback for priority analysis. Files or types: feedback data and XR views. Done when requests know their source view.
- [ ] Union identical logical page requests across both eyes before physical streaming. Files or types: feedback resolver. Done when duplicate per-eye pages do not allocate twice.
- [ ] Select the finest page demanded by any important view. Files or types: priority policy. Done when foveal or close-eye demand wins.
- [ ] Prioritize HMD foveal, HMD peripheral, gameplay-critical auxiliary cameras, desktop mirror, reflections/probes, and background captures. Files or types: priority policy. Done when priority order is explicit.
- [ ] Expand prediction neighborhoods for head rotation and gaze/foveation motion. Files or types: prediction policy. Done when VR motion prefetch is stable.
- [ ] Avoid duplicate per-eye physical caches. Files or types: physical cache service. Done when both eyes share the same physical cache.

### Debugging and diagnostics

- [ ] Add debug views for physical cache occupancy, page table and version, feedback heatmap, missing-page/ancestor fallback heatmap, eviction and delayed slot-reuse history, and per-eye/foveation demand. Files or types: debug views and ImGui panels. Done when these views can be captured.
- [ ] Add telemetry for page faults, fallback rate, upload latency, churn, evictions, cache hit rate, feedback overflow, and predicted versus requested pages. Files or types: telemetry and MCP tools. Done when virtual texture behavior is measurable.
- [ ] Keep dense mip and OpenGL sparse-mip fallbacks for nonvirtualized or unsupported assets. Files or types: residency policy. Done when unsupported assets remain renderable.

### Vulkan dense streaming closure

- [ ] Seal renderer-neutral backend interfaces against Vulkan/OpenGL handle leaks. Files or types: `ITextureResidencyBackend`, `VulkanDenseTextureResidencyBackend`, OpenGL backends. Done when no renderer-specific handles appear above the backend interfaces.
- [ ] Add a dedicated transfer-queue path for imported texture uploads with explicit queue-family release and acquire semaphore chain. Files or types: `VulkanTextureUploadService`, `VulkanTextureUploadTransfer.cs`, `VulkanTextureUploadQueuePolicy.cs`. Done when `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE` routes uploads to a transfer family on devices that have one, graphics acquires ownership before publication, and the `[Vulkan Compat]` graphics-queue log appears only when no transfer family exists.
- [ ] Route Vulkan per-mip progressive uploads through the synchronized upload service. Files or types: `VkTexture2D.PushMipLevel`, `XRTexture2D.ShouldUseProgressiveRenderThreadUpload`, `VulkanTextureUploadService`. Done when `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD=1` no longer uses the render-thread `PushMipLevel` path for imported textures and a source-contract test covers service-owned per-mip requests.
- [ ] Create the progressive destination image with full mip capacity and expose each mip only after its copy completes. Files or types: `VulkanTextureUploadPreparation.cs`, `VulkanTextureUploadPublication.cs`. Done when barriers per mip and an explicit published sampled-mip range replace texture-state mutation before GPU completion.
- [ ] Add telemetry for visible base mip, visible max mip, and pending uploaded mips. Files or types: `ImportedTextureStreamingTextureTelemetry`, texture streaming panel, `list_texture_streaming_textures`. Done when the fields appear in the panel and MCP rows.

### Vulkan hardware sparse residency

- [ ] Probe `sparseBinding`, `sparseResidencyImage2D`, optional `shaderResourceResidency`, sparse queue families, and exact sparse image format support. Files or types: Vulkan capabilities. Done when dense fallback is selected if sparse support is absent.
- [ ] Create sampled sparse images with sparse binding and sparse residency flags, sampled and transfer-destination usage, and optimal tiling where supported. Files or types: Vulkan image creation. Done when sparse images can be allocated.
- [ ] Query normal and sparse image memory requirements. Files or types: Vulkan memory allocation. Done when sparse blocks use compatible memory types.
- [ ] Implement device-local sparse block pools by compatible memory type. Do not allocate one `VkDeviceMemory` object per page. Files or types: sparse memory pool. Done when block allocation is pooled.
- [ ] Track block owner, generation, pending bind/unbind state, and deferred reclamation. Files or types: sparse memory pool. Done when memory reuse is generation-safe.
- [ ] Bind non-tail regions, opaque mip tails, per-array-layer mip tails, and metadata aspects when reported. Files or types: sparse bind code. Done when all sparse regions bind correctly.
- [ ] Account exactly for bound blocks, mip tails, and metadata allocations. Files or types: telemetry. Done when Vulkan sparse memory bytes are exact.
- [ ] Implement explicit bind-to-copy ordering with `vkQueueBindSparse`, bind-complete synchronization, transfer/copy wait, upload completion, and publication after completion. Files or types: sparse synchronization. Done when copy never runs before bind.
- [ ] Define direct sparse image layout strategy and keep dense 2D-array SVT cache layers as the portable per-tile transition baseline. Files or types: Vulkan layout policy and architecture gates. Done when direct sparse images are optional.
- [ ] Publish ancestor/coarser mapping before sparse unbind, wait for old frame versions to retire, submit null-memory unbind, wait for unbind completion, and return blocks to the pool. Files or types: sparse eviction. Done when old mappings never point at freed memory.
- [ ] Add cancellation, stale-generation handling, and device-loss cleanup or quarantine while bind, copy, and unbind work is in flight. Files or types: sparse lifecycle. Done when canceled or lost sparse work cannot leak or publish.
- [ ] Add source-contract tests for capability and fallback boundaries, non-tail binds, edge blocks, mip tails, metadata binds, array layers, ordering, block reuse, queue configurations, and exact bound-byte accounting. Files or types: `XREngine.UnitTests`. Done when sparse contracts are covered.

### Bindless deferred texturing

- [ ] Finalize an API-neutral deferred material record. Files or types: material table and render records. Done when material texture data can be resolved outside the geometry pass.
- [ ] Populate real texture handles or indices in `GPUMaterialTable`. Files or types: material table builder. Done when material records reference texture entries.
- [ ] Add Vulkan descriptor-indexed material texture arrays. Files or types: Vulkan descriptor layout and material binding. Done when Vulkan can sample material texture arrays by index.
- [ ] Add OpenGL bindless texture support with explicit extension gating. Files or types: OpenGL material binding. Done when bindless is used only when supported.
- [ ] Keep a classic materialized G-buffer fallback. Files or types: deferred renderer. Done when unsupported hardware can use the old path.
- [ ] Add geometry-only deferred attachments for depth, packed tangent frame or normal basis, UV0, depth/UV gradients as needed, material ID, and transform ID. Files or types: G-buffer layout. Done when geometry pass does not sample opaque material textures.
- [ ] Add a compatibility material resolve pass that reconstructs `AlbedoOpacity`, `Normal`, and `RMSE`. Files or types: render pipeline. Done when downstream passes see equivalent buffers.
- [ ] Keep deferred decals working against reconstructed buffers in compatibility mode. Files or types: decal pipeline. Done when decal behavior is unchanged.
- [ ] Add texture residency gates so material records never reference invalid dense, sparse, or virtual data. Files or types: material table and streaming state. Done when stale texture generations cannot be sampled.
- [ ] Integrate SVT feedback generation with the material resolve path where useful. Files or types: material resolve and feedback pass. Done when feedback can be generated from material texture use.
- [ ] Add native bindless lighting mode after compatibility mode is stable. Files or types: lighting passes. Done when native bindless lighting can run without compatibility resolve.

### Neural texture compression

- [ ] Define a canonical neural-eligible material bundle with base color, tangent normal, roughness, metallic, ambient occlusion, emissive, color-space metadata, and mip/page policy. Files or types: asset model. Done when bundle eligibility is explicit.
- [ ] Add `XRNeuralMaterialAsset` and cook settings. Files or types: asset types and import UI. Done when neural material assets can be serialized.
- [ ] Add an offline training/optimization tool under `Tools/`. Files or types: tool project or script. Done when training can run outside the editor.
- [ ] Add metric output for per-channel error, perceptual image difference, normal angular error, and frame-space material comparison captures. Files or types: tool diagnostics. Done when outputs can validate quality.
- [ ] Ship decode-on-load or cook-time reconstruction to conventional BCn first. Files or types: cooker and load path. Done when neural output can fall back to conventional textures.
- [ ] Require owner approval and dependency/license review before adding compression or training dependencies. Files or types: dependency docs. Done when dependencies are approved.
- [ ] Integrate conventional neural fallback textures with the current mip streamer and future SVT cache. Files or types: streaming cache. Done when fallback textures use existing residency rules.
- [ ] Add feature-texture shader decode only after bindless deferred resolve is stable. Files or types: shaders. Done when decode is behind explicit capability gates.
- [ ] Add direct latent decode only as an explicit high-end experimental path. Files or types: renderer settings and shaders. Done when the path cannot activate by default.

### Runtime virtual textures

- [ ] Define an RVT page-producer interface. Files or types: runtime virtual texture service. Done when producers can request generated pages.
- [ ] Add terrain/landscape page producers. Files or types: terrain components. Done when terrain can generate RVT pages.
- [ ] Add decal and spline projection producers. Files or types: decal and spline components. Done when projected content can write RVT pages.
- [ ] Reuse SVT page IDs, physical caches, page tables, publication, and eviction where practical. Files or types: RVT and SVT services. Done when RVT does not duplicate cache ownership rules.
- [ ] Add dirty-region tracking. Files or types: RVT producers. Done when only changed regions regenerate.
- [ ] Add page render scheduling under the shared render-work budget. Files or types: render-work scheduler. Done when RVT generation cannot overrun the frame.
- [ ] Add page invalidation and temporal reuse. Files or types: RVT cache. Done when unchanged pages persist.
- [ ] Add producer generation and stale-work cancellation. Files or types: RVT scheduler. Done when old producer work cannot publish.
- [ ] Add fallback when RVT page generation misses the current frame. Files or types: shaders and RVT page table. Done when missed pages sample valid prior or fallback data.

### Documentation and closeout facts

- [ ] Promote final runtime texture architecture into stable architecture or developer-guide docs after code and validation settle. Files or types: docs. Done when finished design is not trapped in todos.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` when bindless or virtual-texture paths change pass invariants. Files or types: architecture docs. Done when pipeline invariants match code.
- [ ] Update user-facing setup docs for settings, flags, cache formats, and diagnostics. Files or types: user docs. Done when users can configure and diagnose texture streaming.
- [ ] Refresh dependency and license docs after any compression or tooling dependency change. Files or types: dependency docs. Done when added tools have license records.

## Decisions Needed

- [ ] Choose how progressive Vulkan uploads expose mips: reuse one full-size image and expose lower mips one at a time, or keep one complete image per dense residency generation. Owner: Rendering.
- [ ] Decide how bindless material texture table slots observe texture generation changes when CPU-direct and bindless Vulkan paths both use streamed textures. Owner: Rendering.
- [ ] Decide whether Vulkan dense imported streaming generates mipmaps or relies only on cooked or imported mip data. Owner: Rendering.
- [ ] Decide whether to remove `XRE_VULKAN_ASYNC_TEXTURE_UPLOAD` and `XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER` or keep them as compatibility toggles. Owner: Rendering.

## Out Of Scope

- Manual scene runs, hardware matrices, profiler runs, screenshots, RenderDoc captures, and editor smoke checks. These belong in [Texture Validation](../../testing/texturing/texture-validation.md).
- Compression encoder selection and compressed payload implementation. Use [Texture Compression And Cooked Texture Cache TODO](texture-compression-and-cooked-cache-todo.md).
- Unreviewed dependencies or licensing changes.
- Claims that runtime validation passed without fresh validation evidence.
