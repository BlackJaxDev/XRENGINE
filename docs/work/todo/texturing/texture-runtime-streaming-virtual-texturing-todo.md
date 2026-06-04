# Texture Runtime, Streaming, And Virtual Texturing TODO

Last Updated: 2026-05-19
Status: active phased roadmap
Source design: [Texture Runtime, Streaming, And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
Compression/cache design: [Texture Compression And Cooked Texture Cache Design](../../design/texturing/texture-compression-and-cooked-cache-design.md)
Compression/cache tracker: [Texture Compression And Cooked Texture Cache TODO](texture-compression-and-cooked-cache-todo.md)
Validation ledger: [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)

Supersedes:

- [Texture Streaming Consolidation TODO](texture-streaming-consolidation-todo.md)
- [Texture Management Runtime TODO](texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](texture-streaming-cooked-cache-todo.md)

## Goal

Carry the implemented texture-streaming v1 system through validation, then extend it toward safe page-level sparse residency, full streaming virtual textures, Vulkan parity, bindless deferred material resolve, and optional neural texture compression.

## Current Baseline

Implemented today:

- [x] `ImportedTextureStreamingManager` frame-level orchestration.
- [x] `TextureStreamingRegistry` records, usage, material binding observations, snapshots, and compaction.
- [x] `TextureResidencyPolicy` desired residency, priority, role multipliers, fairness, cooldowns, pressure fitting, and promotion fade.
- [x] `TextureTransitionQueue` pending transition replacement, cancellation, stale repair, and lifecycle state.
- [x] `TextureUploadScheduler` priority queueing, duplicate coalescing, generation cancellation, budget gates, and upload telemetry.
- [x] `TextureResidencyState` for `XRTexture2D` sparse runtime fields with `SetField(...)` property mutation.
- [x] `GLTieredTextureResidencyBackend` fallback.
- [x] `GLSparseTextureResidencyBackend` sparse mip residency for page-aligned OpenGL `Rgba8` textures.
- [x] Shared-context sparse promotion path with fence-gated exposure.
- [x] Metadata-first cooked texture streamability checks.
- [x] Cooked mip-addressable `XRTS` payload with preview mip and per-mip offsets.
- [x] Dedicated texture logging and ImGui texture streaming diagnostics.

Known current limits:

- [ ] Full scene validation remains incomplete.
- [ ] Unit-test execution is blocked by unrelated duplicate `Engine` type compile errors in the unit-test project.
- [ ] Partial sparse page residency is scaffolded but disabled by policy.
- [ ] Cooked payloads are mip-addressable, not page-addressable.
- [ ] Color-space and texture-role metadata are incomplete.
- [ ] GPU-native compressed texture payloads and compressed uploads are not implemented.
- [ ] Vulkan sparse/image-upload parity is not implemented.
- [ ] Bindless deferred texturing is design-only.
- [ ] Neural texture compression is design-only.

## Phase 0: Branch, Canonical Docs, And Baseline

**Goal:** move future texture work to one design and one todo without losing historical phase records.

- [ ] Create a dedicated branch for this roadmap, for example `texture-runtime-vt-roadmap`.
- [x] Add the canonical design doc.
- [x] Add this canonical phased TODO.
- [x] Mark superseded texturing design docs as historical with links to the canonical design.
- [x] Mark superseded implemented TODO docs as historical with links to this TODO and the validation ledger.
- [x] Move implemented-TODO validation checks into the consolidated validation ledger.
- [x] Update `docs/work/README.md` and `docs/README.md` links.
- [ ] Preserve or link the latest texture validation logs:
  - [ ] cold-cache Sponza run
  - [ ] warm-cache Sponza run
  - [ ] `log_textures.txt`
  - [ ] `log_opengl.txt`
  - [ ] `log_rendering.txt`
  - [ ] profiler FPS-drop and render-stall logs

## Phase 1: Validate And Close v1 Streaming

**Goal:** prove the implemented streamer is stable before enabling finer residency.

- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Re-run targeted tests after the unrelated unit-test duplicate `Engine` issue is fixed:
  - [ ] `ImportedTextureStreamingPhaseTests`
  - [ ] `ImportedTextureStreamingContractTests`
  - [ ] `GLTexture2DContractTests`
  - [ ] `RuntimeRenderingHostServicesTests`
- [ ] Run cold-cache Sponza startup and record:
  - [ ] raw source decode count
  - [ ] cache miss/write count
  - [ ] first visible preview time
  - [ ] all visible previews resident time
  - [ ] pending visible transition drain time
  - [ ] upload validation failure count
- [ ] Run warm-cache Sponza startup and record:
  - [ ] cache hit count
  - [ ] slow cache read count
  - [ ] worst `cacheReadMs`
  - [ ] worst `cacheParseMs`
  - [ ] promotion queue wait
  - [ ] render-thread upload chunk timings
- [ ] Confirm `Texture.UploadValidationFailed` remains zero in normal runs.
- [ ] Confirm no texture-upload `GL_INVALID_VALUE` appears in `log_opengl.txt`.
- [ ] Confirm `Texture.BindingRisk` entries are expected non-streaming paths or filed follow-ups.
- [ ] Confirm `Texture.SparseStateClearedForDenseUpload` appears only for legitimate sparse-to-dense handoffs.
- [ ] Confirm `TextureStreaming.FinalizeSparseTransitions` never waits for multi-second spans.
- [ ] Confirm texture/shadow shared-budget counters explain any delayed promotion.
- [ ] Confirm the ImGui texture streaming panel remains responsive with hundreds of tracked textures.

## Phase 2: Harden The Current Streamer

**Goal:** fix remaining v1 quality, metadata, and diagnostics gaps without changing the residency model.

- [ ] Split cache timing logs into file I/O, manifest parse, mip blob copy, CPU conversion, and GPU upload.
- [ ] Add complete color-space metadata to the cooked texture manifest.
- [ ] Add texture role metadata to the cooked texture manifest:
  - [ ] albedo/base color
  - [ ] normal/bump
  - [ ] roughness
  - [ ] metallic
  - [ ] mask/opacity/alpha
  - [ ] emissive
  - [ ] unknown
- [ ] Include color space and page selection in resident-data reuse cache keys where relevant.
- [ ] Add configurable minimum resident detail for hero assets.
- [ ] Add adaptive decode concurrency based on CPU count, current frame time, and active import pressure.
- [ ] Keep total decode concurrency bounded so preview urgency does not steal editor responsiveness.
- [ ] Audit hot paths with the allocation reporting tool:
  - [ ] registry snapshot
  - [ ] usage recording
  - [ ] policy scoring
  - [ ] transition queueing
  - [ ] scheduler submit/execute
  - [ ] OpenGL upload chunks
- [ ] Confirm `log_textures.txt` line schema is stable or document breaking changes for tooling.
- [ ] Delete or archive obsolete partial-class files only after their active serialization/import/sparse content has moved.
- [ ] Add diagnostic coverage for non-streaming black-surface cases that have no texture upload validation failure.

## Phase 3: Safe Partial Sparse Page Residency

**Goal:** turn the existing partial-page scaffold into a safe optional feature.

- [ ] Keep partial page residency disabled by default until this phase is validated.
- [ ] Replace mesh-UV-bounds-only page requests with a material sampling domain model:
  - [ ] UV transform
  - [ ] wrap mode
  - [ ] mip bias
  - [ ] anisotropy/filter footprint
  - [ ] normal/parallax UV perturbation policy
  - [ ] shader-generated UV opt-out
- [ ] Add configurable guard-band expansion around requested UV bounds.
- [ ] Add camera-velocity and stereo guard-band expansion.
- [ ] Track page selections per texture role if different samplers use different UV transforms.
- [ ] Make page selection hysteresis slower than mip promotion to avoid page churn.
- [ ] Delay uncommit of previously resident pages for a short TTL after selection changes.
- [ ] Add page-selection telemetry:
  - [ ] requested coverage
  - [ ] committed coverage
  - [ ] guard-band-expanded coverage
  - [ ] pages committed/uncommitted this frame
- [ ] Add tests for partial-page region math with page alignment and mip-tail behavior.
- [ ] Add tests that partial page selection normalizes to full coverage near the full-coverage threshold.
- [ ] Add tests for wrap-mode or out-of-range UV fallback to full coverage.
- [ ] Validate with high-speed camera movement over wrapped and transformed UVs.
- [ ] Enable partial page residency only behind a renderer setting after validation.

## Phase 4: Full Streaming Virtual Textures

**Goal:** add SVT where only visible texture tiles need to be resident.

- [ ] Define a virtual texture asset model:
  - [ ] logical dimensions
  - [ ] logical mip count
  - [ ] tile size
  - [ ] border/guard texels
  - [ ] format
  - [ ] source version
  - [ ] texture id
- [ ] Extend cooked texture payloads with page-addressable blobs:
  - [ ] per-page offsets
  - [ ] per-page byte lengths
  - [ ] per-page format/block metadata
  - [ ] optional compression
  - [ ] fallback mip chain
- [ ] Implement a physical tile cache:
  - [ ] fixed GPU memory budget
  - [ ] tile allocation table
  - [ ] free list
  - [ ] LRU/priority eviction
  - [ ] per-format pools or a documented single-format first pass
- [ ] Implement a virtual page table:
  - [ ] CPU representation
  - [ ] GPU texture/buffer representation
  - [ ] fallback-to-coarser-page encoding
  - [ ] update batching
- [ ] Add a feedback pass:
  - [ ] texture id
  - [ ] requested mip
  - [ ] page x/y
  - [ ] screen coverage or sample count
  - [ ] per-eye id for VR
- [ ] Add feedback resolve:
  - [ ] deduplicate requests
  - [ ] expand guard bands
  - [ ] prioritize visible/high-error pages
  - [ ] keep coarse fallback pages resident
- [ ] Add page streaming:
  - [ ] async page blob reads
  - [ ] upload queue integration
  - [ ] generation cancellation
  - [ ] stale request cancellation
  - [ ] partial update batching
- [ ] Add shader sampling helpers for page-table lookup and physical tile sampling.
- [ ] Add fallback path to sparse mip residency or tiered residency when SVT is unsupported.
- [ ] Add debug views:
  - [ ] physical cache occupancy
  - [ ] page table
  - [ ] feedback heatmap
  - [ ] missing-page fallback
  - [ ] eviction history
- [ ] Validate with large 16k-style test textures and camera sweeps.
- [ ] Validate per-eye page residency in VR before enabling SVT for stereo views.

## Phase 5: Vulkan Texture Residency Parity

**Goal:** bring the renderer-neutral streaming policy to Vulkan without leaking Vulkan details above the backend.

- [ ] Implement a Vulkan `ITextureResidencyBackend`.
- [ ] Add staged image upload integration with `TextureUploadScheduler`.
- [ ] Add generation-gated cancellation for Vulkan image storage changes.
- [ ] Add Vulkan sparse image capability probes.
- [ ] Add Vulkan sparse image allocation/binding path where supported.
- [ ] Add Vulkan fallback to dense tiered residency.
- [ ] Route upload telemetry through `TextureRuntimeTelemetry`.
- [ ] Add descriptor update strategy for streamed texture changes.
- [ ] Add barrier/layout transition coverage for partial image updates.
- [ ] Add tests or source-contract checks that no Vulkan/OpenGL handles leak above the backend interface.
- [ ] Validate Vulkan warm-cache imported scene startup.

## Phase 6: Bindless Deferred Texturing

**Goal:** move opaque deferred material texture sampling out of the geometry pass.

- [ ] Finalize an API-neutral deferred material record.
- [ ] Populate real texture handles/indices in `GPUMaterialTable`.
- [ ] Add Vulkan descriptor-indexed material texture arrays.
- [ ] Add OpenGL bindless texture support with explicit extension gating.
- [ ] Keep a classic materialized G-buffer fallback path.
- [ ] Add geometry-only deferred attachments:
  - [ ] depth
  - [ ] packed tangent frame or normal basis
  - [ ] UV0
  - [ ] depth/UV gradients as needed
  - [ ] material id
  - [ ] transform id
- [ ] Add compatibility material resolve pass that reconstructs `AlbedoOpacity`, `Normal`, and `RMSE`.
- [ ] Keep deferred decals working against reconstructed buffers in compatibility mode.
- [ ] Add native bindless lighting mode after compatibility mode is stable.
- [ ] Add texture streaming residency gates so bindless material records never reference invalid texture data.
- [ ] Validate non-stereo opaque deferred first.
- [ ] Add MSAA, stereo, transparent, and forward-only follow-ups after the core path is stable.

## Phase 7: Neural Texture Compression

**Goal:** add optional neural material compression through the asset pipeline.

- [ ] Define a canonical neural-eligible material bundle:
  - [ ] base color
  - [ ] tangent normal
  - [ ] roughness
  - [ ] metallic
  - [ ] ambient occlusion
  - [ ] emissive
  - [ ] color-space metadata
  - [ ] mip policy
- [ ] Add `XRNeuralMaterialAsset` and cook settings.
- [ ] Add an offline training/optimization tool under `Tools/`.
- [ ] Add metric output:
  - [ ] per-channel error
  - [ ] perceptual image diff
  - [ ] normal angular error
  - [ ] frame-space material diff captures
- [ ] Ship decode-on-load or cook-time reconstruction to conventional BCn first.
- [ ] Require owner approval and dependency/license review before adding compression/training dependencies.
- [ ] Integrate neural fallback textures with the existing streaming cache.
- [ ] Add feature-texture shader decode only after bindless deferred resolve is stable.
- [ ] Add direct latent decode only as an explicit high-end experimental path.

## Phase 8: Runtime Virtual Textures

**Goal:** add GPU-generated virtual texture pages for terrain, decals, and procedural material caches.

- [ ] Define RVT page producer interface.
- [ ] Add terrain and landscape page producer.
- [ ] Add decal/spline projection producer.
- [ ] Share page cache concepts with SVT where practical.
- [ ] Add dirty-region tracking.
- [ ] Add page render scheduling under the shared render-work budget.
- [ ] Add page invalidation and temporal reuse.
- [ ] Add fallback when RVT page generation misses the current frame.
- [ ] Validate terrain-object blend and large decal cases.

## Phase 9: Stable Documentation And Closeout

**Goal:** move durable texture architecture into stable docs once the roadmap phases settle.

- [ ] Promote the final runtime texture architecture into `docs/architecture/rendering/` or `docs/features/`.
- [ ] Keep historical TODOs as phase ledgers only.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if bindless or virtual-texture paths change render-pass invariants.
- [ ] Update user-facing setup docs if new settings, launch flags, cache formats, or diagnostics workflows are added.
- [ ] Refresh dependency docs and licenses after any compression/tooling dependency change.
- [ ] Merge the dedicated roadmap branch back into `main` after validation and owner review.
