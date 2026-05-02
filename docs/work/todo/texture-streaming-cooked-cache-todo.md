# Texture Streaming Cooked Cache TODO

Status: implementation pass complete; cold/warm scene-run validation remains

Source analysis: [Texture Streaming Run Analysis - 2026-05-01 18:06](../testing/texture-streaming-run-analysis-2026-05-01-180642.md)

## Goal

Make imported textures reach useful preview and final residency quickly by using the existing third-party asset import cache as the runtime streaming authority after the first source import.

The first import may read original source files such as PNG, JPG, TGA, or EXR. Every subsequent fresh-cache path should parse optimized cached texture asset data instead of decoding and resizing the original source again.

## Success Criteria

- Visible imported textures get a real preview within a few frames after first visibility under normal Sponza startup load.
- A fresh cached `XRTexture2D` asset is the preferred streaming source for imported textures.
- Raw source decode is limited to first import, stale/missing cache repair, or explicit fallback.
- `log_textures.txt` distinguishes cache hits, cache misses, stale cache fallback, raw source decode, cooked asset read, CPU prep, upload, and lifecycle latency.
- Sparse transition cancellations stop wasting completed CPU resident data when the next request can reuse it.
- Black placeholder surfaces are limited to true failure paths, not normal texture startup.

## Primary Code Areas

- `XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D*.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs`
- `XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.Preview.cs`
- `XREngine.UnitTests/Rendering/*Texture*`
- `docs/work/testing/texture-streaming-run-analysis-2026-05-01-180642.md`

## Phase 0: Branch And Repro Baseline

**Goal:** isolate this follow-up and keep the May 1 regression evidence easy to compare.

- [x] Create a dedicated branch for this TODO, for example `texture-streaming-cooked-cache`.
- [x] Preserve the session root from the analysis as the before baseline:
  - [x] `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/log_textures.txt`
  - [x] `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/log_general.txt`
  - [x] `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/log_rendering.txt`
  - [x] `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/log_opengl.txt`
- [ ] Capture one new run with the CPU timing log change so raw decode, clone, resize, mip-build, and total resident-build timings are available in `log_textures.txt`.
- [ ] Record first-visible-to-all-preview-ready time, transition cancellation count, cache hit/miss count, and final promoted/preview counts.

## Phase 1: Make The Existing Import Cache The Streaming Authority

**Goal:** ensure fresh cached texture assets are used before raw source images.

- [x] Trace the texture import cache path end-to-end:
  - [x] `ShouldUseThirdPartyCache`
  - [x] `CreateImportContext`
  - [x] `TryResolveCacheDirectory`
  - [x] `IsCacheAssetFresh`
  - [x] `XRTexture2D.IsTextureStreamingAssetUsable`
  - [x] `ResolveTextureStreamingAuthorityPath`
  - [x] `TextureStreamingSourceFactory.Create`
  - [x] `AssetTextureStreamingSource.LoadResidentData`
- [ ] Verify the first import writes a streaming-usable cached `XRTexture2D` asset with enough resident data for preview and promotion in a cold scene run.
- [ ] Verify a second import of the same source chooses `AssetTextureStreamingSource` and does not construct `MagickImage` from the original source path in a warm scene run.
- [x] Remove active-import cache-warmup suppression so missing or stale streaming variants are generated on demand instead of falling back permanently for the run.
- [x] Use cache fallback only when the cache is missing, stale, unreadable, or not texture-streaming usable.
- [x] Include texture residency payload settings in the cache variant key when they affect the cooked texture payload.
- [x] Add texture-log events for cache decisions:
  - [x] `Texture.CacheHit`
  - [x] `Texture.CacheMiss`
  - [x] `Texture.CacheStale`
  - [x] `Texture.CacheFallbackToSource`
  - [x] `Texture.CacheWrite`
- [x] Add source-contract tests that prove the cached `.asset` path is preferred over PNG/JPG when fresh.

## Phase 2: Cook Runtime-Streamable Texture Payloads

**Goal:** make cached texture assets cheap to parse and cheap to stream.

- [x] Define the minimum cached texture payload needed by runtime streaming:
  - [x] source width and height
  - [x] resident mip dimensions
  - [x] mip offsets and byte lengths
  - [x] pixel format or block-compressed format
  - [ ] color space
  - [ ] texture role: albedo, normal, scalar mask, roughness, metallic, emissive, or unknown
  - [x] source timestamp and import option freshness checks
- [x] Ensure `XRTexture2D.TryReadResidentDataFromTextureAssetFileBytes` can read exactly the requested mip range without hydrating unnecessary source-sized image data.
- [x] Reject one-mip full-size texture cache assets as not streaming-usable so stale cache files self-regenerate instead of pinning textures at max resident size.
- [x] Convert live one-mip `XRTexture2D` imports into streamable cooked mip-chain assets when writing texture cache entries.
- [x] Generate cooked cache mip chains on the OpenGL render thread with the detail-preserving compute shader and asynchronous RGBA8 PBO readback before falling back to CPU mip generation.
- [x] Keep the initial payload simple if needed: uncompressed cooked mip bytes are acceptable as an intermediate step if they remove raw source decode from the steady-state path.
- [ ] Add a later compression step for GPU-ready payloads:
  - [ ] BC7 for color/albedo where quality matters
  - [ ] BC5 for normal maps
  - [ ] BC4 or packed scalar formats for masks
- [ ] Do not add or upgrade compression dependencies without owner approval and a refreshed license audit.
- [x] Add validation coverage that a cached texture can provide preview data without opening the original source file.

## Phase 3: Preview-First Scheduling

**Goal:** prevent black or placeholder-only surfaces while high-res promotion work is queued.

- [x] Split preview work from promotion work in `ImportedTextureStreamingManager`.
- [x] Give visible previews an urgent lane that is never blocked by 1024/2048 promotion decode.
- [x] Delay non-critical high-res promotion until every visible texture has at least a 64 or 128 preview resident.
- [x] Fix repromotion after zoom-out demotion so visible, preview-ready textures are allowed to climb back above the preview resident size even when another visible texture is still waiting for its first preview.
- [x] Treat a larger resident target as a promotion even if sparse committed-byte accounting is temporarily equal or stale.
- [x] Keep 64px preview as the first useful resident, but allow later distance/pressure demotion down to a real 1px mip floor instead of ever sampling missing texture data.
- [x] Fix sparse demotion to commit and upload the target lower mip range before changing GL sampling to that range, preventing black committed-but-empty sparse pages.
- [x] Prevent visible textures with missing projected-size metrics from collapsing to 1px and never re-promoting.
- [x] Keep visible normal, bump, height, alpha, mask, and opacity maps at a preview-size floor so 1px auxiliary mips cannot turn lighting or cutouts black.
- [x] Clamp normal visibility-policy demotion to the preview resident size for every texture role; reserve 1px resident targets for explicit VRAM-pressure fitting only.
- [ ] Consider adaptive preview decode concurrency based on CPU count, current frame time, and active import pressure.
- [ ] Keep total decode concurrency bounded so preview urgency does not steal the render thread or editor responsiveness.
- [x] Add a summary field for visible textures without resident preview data.
- [ ] Validate that the Sponza run no longer reaches a state with 37 visible textures and only 2 previews ready.

## Phase 4: Reuse Work Across Canceled Transitions

**Goal:** stop sparse transition churn from repeatedly paying CPU prep cost.

- [x] Add cancellation checkpoints before source/cache open, after cache read/source decode, before resize, before mip build, and before upload enqueue.
- [x] Add a short-lived resident-data reuse cache keyed by:
  - [x] authority path
  - [x] source timestamp/hash
  - [x] requested resident dimension
  - [x] mip-chain flag
  - [x] format (currently fixed by the RGBA8 cooked payload variant)
  - [ ] color space
  - [ ] page selection where relevant
- [x] Allow a superseding transition to reuse compatible prepared resident data from a canceled transition.
- [x] Track cancellation phase in `log_textures.txt`:
  - [x] before decode/cache read
  - [x] during decode/cache read
  - [x] after CPU prep
  - [x] during upload
  - [x] during finalization
- [ ] Validate that cancellation-heavy bump maps from the analysis no longer repeatedly decode or prepare identical data.

## Phase 5: Role-Aware Fallbacks And Null Texture Binding

**Goal:** make missing texture data visually safe and diagnosable.

- [x] Add role-aware fallback textures:
  - [x] albedo fallback that is visible but not black
  - [x] flat normal fallback
  - [x] neutral roughness fallback
  - [x] neutral metallic/specular fallback
  - [x] emissive-off fallback
- [x] Bind fallback textures explicitly when a preview path has no resident texture yet.
- [x] Investigate `LightProbeComponent.Preview.cs` and the `Texture0` sampler errors from the analysis.
- [x] Fix preview material paths that can leave a shader expecting `Texture0` while no texture is bound.
- [x] Add a texture-log event for fallback sampling caused by missing resident data, separate from shader binding errors.
- [x] Add draw-call-adjacent `Texture.BindingRisk` diagnostics for suspicious main-pass sampler bindings, including missing sampler uniforms, invalid GL ids, empty source mips, progressive upload/finalize state, invalid mip ranges, sparse sampling of uncommitted higher mips, and the current sparse resident/committed bases.
- [x] Mirror the existing opt-in material binding trace into `log_textures.txt` as `Texture.MaterialBinding`, including sampler, source path, dimensions, mip range, and sparse resident/committed state.
- [x] Capture a repro run and inspect `Texture.BindingRisk` plus upload validation entries before making the next behavioral change.
- [x] Fix the sparse-to-dense handoff found in the `21:31` repro: dense/tiered resident data must clear stale sparse residency before publishing a full mip chain.
- [x] Add `Texture.SparseStateClearedForDenseUpload` so future logs identify this handoff explicitly.
- [x] Recreate GL texture storage when leaving sparse residency for dense uploads, even when logical dimensions and level count match.
- [ ] If a future black-surface repro has no `Texture.UploadValidationFailed` and no sparse-to-dense handoff warning nearby, investigate material/shader/lighting paths outside texture streaming.

## Phase 6: Repair Texture Timing And VRAM Telemetry

**Goal:** make the next log capture answer why a texture was late.

- [x] Split the current upload timing into separate fields:
  - [x] `queueWaitMs`
  - [x] `cacheReadMs`
  - [x] `sourceDecodeMs`
  - [x] `cpuPrepMs`
  - [x] `activeUploadMs`
  - [x] `lifecycleMs`
- [x] Keep lifecycle timing for user-visible latency, but stop labeling full lifecycle time as active OpenGL execution time.
- [x] Fix tiered preview committed-byte accounting so 64x64 resident textures cannot report impossible values such as `committedBytes=3`.
- [x] Recompute target committed bytes after source dimensions are known.
- [x] Add per-texture stage to summaries:
  - [x] `noData`
  - [x] `fallback`
  - [x] `previewQueued`
  - [x] `previewResident`
  - [x] `promotionQueued`
  - [x] `promoted`
  - [x] `canceled`
  - [x] `failed`
- [x] Include cache path and original source path in slow CPU/cache events.

## Phase 7: Coordinate With Shadow And Render-Thread Work

**Goal:** prevent texture finalization from colliding with long shadow-atlas work.

- [x] Review the shared render-work budget between texture uploads/finalization and shadow atlas tile rendering.
- [x] Add or verify `Texture.DelayedByShadow` coverage for frames where shadow work consumes the budget first.
- [x] Ensure sparse finalization jobs yield when fences are not ready instead of waiting for multi-second stretches.
- [x] Add a run metric for texture work delayed by non-texture render jobs.
- [ ] Validate the Sponza startup run no longer shows 15-17 second waits in `TextureStreaming.FinalizeSparseTransitions`.

## Phase 8: Validation

**Goal:** prove the cached path fixes the user-visible symptoms and does not regress upload safety.

- [x] Run targeted builds:
  - [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Run targeted tests once the unrelated unit-test `Engine` type ambiguity is resolved:
  - [x] Attempted `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "ImportedTextureStreamingContractTests|ImportedTextureStreamingPhaseTests|RuntimeRenderingHostServicesTests" --no-restore`
  - [x] Attempted `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "ImportedTextureStreamingPhaseTests" --no-restore` after the visible-demotion policy fix.
  - [ ] Re-run after the unrelated test-project compile blockers are fixed:
    `CS0433 Engine exists in both XREngine.Runtime.Rendering and XREngine`, plus stale audio test references to removed `Audio2Face3DComponent` / `OVRLipSyncComponent` helpers.
- [ ] Run Sponza import with a cold cache and confirm source decode happens only on first import.
- [ ] Run Sponza import with a warm cache and confirm cached cooked texture data is used.
- [ ] Run the warm-cache path on a slower CPU or throttled decode profile.
- [ ] Compare against the May 1 analysis:
  - [ ] time to all visible previews resident
  - [ ] time to pending visible transitions drained
  - [ ] slow CPU prep events by phase
  - [ ] transition cancellation count
  - [ ] final promoted/preview texture counts
  - [ ] `Texture.UploadValidationFailed` count remains zero
  - [ ] `Texture.SparseStateClearedForDenseUpload` appears only when a texture legitimately crosses from sparse residency to dense/tiered upload
  - [ ] `Texture0` sampler binding errors are gone or explained by a separate non-streaming path
- [x] Update architecture docs if cache format, texture log fields, settings, or validation workflow changes.
- [ ] Merge the dedicated branch back into `main` after the TODO is complete and validated.
