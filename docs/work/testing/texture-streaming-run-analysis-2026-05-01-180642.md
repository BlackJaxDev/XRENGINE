# Texture Streaming Run Analysis - 2026-05-01 18:06

Status: active investigation

Follow-up TODO: [Texture Streaming Cooked Cache TODO](../todo/texture-streaming-cooked-cache-todo.md)

This note analyzes the texture loading, streaming, and VRAM telemetry from the May 1 run where Sponza textures were still slow to become resident, some surfaces stayed visibly low resolution, and slower CPUs could leave surfaces black or placeholder-only for too long.

## Session

Session root:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/`

Primary files reviewed:

- `log_textures.txt`
- `log_general.txt`
- `log_rendering.txt`
- `log_opengl.txt`
- `editor_bootstrap.log`

No profiler trace files were present in this session directory, so this analysis is based on the runtime logs only.

## Executive Summary

- The run did not log any `Texture.UploadValidationFailed` events and `log_opengl.txt` did not show the earlier invalid sparse upload rectangle failure. The black/low-res symptom in this run is therefore more consistent with texture data not becoming resident quickly enough, preview/fallback binding gaps, or streaming policy churn than with rejected GL uploads.
- Preview residency was badly delayed. At `18:07:15.157` only 2 of 76 tracked textures had previews ready while all 76 were pending. All 76 previews were not ready until `18:07:52.032`, roughly 37 seconds later.
- Visible texture work did not fully drain until `18:08:17.362`, with 76 previews ready, 37 promoted, 39 still at preview, and 0 pending visible transitions. That is roughly 62 seconds after the first visible streaming burst.
- `log_textures.txt` recorded 228 `Texture.UploadSlow` events and 330 sparse transition cancellations. The cancellations were concentrated in bump maps, especially `sponza_column_b_bump.png`, `sponza_thorn_bump.png`, `background_bump.png`, and `lion_bump.png`.
- The current run did not include CPU decode timing in `log_textures.txt`. That was a diagnostic blind spot: slow raw image decode, clone, resize, and mip generation could make textures stay black without appearing in the dedicated texture log.
- The code now emits CPU-side texture preparation timing into `log_textures.txt` when decode, clone, resize, mip-build, or total resident-build time crosses the slow threshold. These events use `Texture.UploadSlow backend=CPU` and include `decodeMs`, `cloneMs`, `resizeMs`, `mipBuildMs`, `totalMs`, and `totalThresholdMs`.

## Follow-Up: 2026-05-01 21:31 Black After Demotion

The later session at `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_21-31-27_pid28976/` produced a different, more concrete failure signature than the original 18:06 run.

- `log_textures.txt` recorded six `Texture.UploadValidationFailed` events for textures that should have been promoting again: `sponza_ceiling_a_diff`, `sponza_floor_a_diff`, `vase_dif`, `vase_round`, `sponza_column_a_diff`, and `lion_bump`.
- Each failure tried to upload a `512x512` region to `mip=2`, while `log_opengl.txt` reported the allocated GL mip dimensions for that level as `256x256`.
- The same GL rows show `mipmapCount=11`, `StreamingLockMipLevel=1`, `sparseEnabled=True`, and `residentBase=1`. That is the important mismatch: a full dense mip chain was being uploaded while the old sparse resident base from a prior demotion was still active.
- After the failed uploads, summaries stayed stuck with `failed=6`, `pending=9`, `uploading=9`, `previewResident=39`, and `promotionQueued=4`, even though VRAM pressure was false and managed residency was only about 440 MB of a 20 GB budget.

Root cause: the texture crossed from sparse residency back into the dense/tiered progressive upload path without clearing sparse state. The GL uploader offsets mip indices by `residentBase` when sparse streaming is enabled, which is correct for sparse resident-relative mip arrays but wrong for a newly published full dense chain. That offset turned the 512x512 local mip into GL level 2, whose allocated dimensions were only 256x256, so validation rejected the upload and the transition never recovered.

Implemented fix:

- `XRTexture2D.ApplyResidentData` now treats dense/tiered resident data as a sparse-state boundary. It logs `Texture.SparseStateClearedForDenseUpload` when stale sparse state is present, clears sparse residency, and only then publishes the dense mip chain.
- `GLTexture2D.UpdateMipmaps` now recreates GL storage when leaving sparse storage even if logical dimensions and level count appear compatible, so dense uploads do not inherit sparse allocation/commit assumptions.
- Source-contract coverage now asserts both the sparse-state clear and the forced storage recreation path.

Next validation target: in the next repro run, `Texture.UploadValidationFailed` should remain at zero for promotion-after-demotion cases. If black surfaces persist with no upload validation failures, inspect the nearest `Texture.BindingRisk` rows and material/shader binding state instead of the mip upload path.

## Timeline Findings

| Time | Frame | Observation |
|---|---:|---|
| `18:07:00.149` | 60 | 59 textures tracked, no visible textures, no previews ready. |
| `18:07:12.320` | 3000 | 75 textures tracked, no visible textures, no previews ready. |
| `18:07:15.157` | 3600 | 76 tracked, 37 visible, only 2 previews ready, all 76 pending, max resident target 64. |
| `18:07:18.560` | 4200 | 9 previews ready, 6 promoted, 70 pending, max resident target 1024. |
| `18:07:27.734` | 6000 | 19 previews ready, 12 promoted, 64 pending. |
| `18:07:45.086` | 9000 | 64 previews ready, 33 promoted, 16 pending, max resident target 2048. |
| `18:07:52.032` | 10200 | All 76 previews ready, 33 promoted, 4 pending. |
| `18:08:17.362` | 14400 | 76 previews ready, 37 promoted, 39 at preview, 0 pending, resident memory about 446 MB. |
| `18:16:06.614` | 35760 | Peak logged queue/lifecycle time reached about 75.8 seconds. |
| `18:18:33.271` | 44400 | End-state summary still had 39 textures at preview and 37 promoted. |

The important user-visible issue is early starvation: the scene becomes visible before most textures have even their preview data resident. On a faster CPU this is a short-lived placeholder period; on a slower CPU it can look like textures are stuck black because raw source decode and residency preparation fall behind the renderable scene.

## Texture Log Findings

`log_textures.txt` contained 743 `Texture.VramSummary` rows, 228 `Texture.UploadSlow` rows, and 330 `Texture.TransitionCanceled` rows.

Slow upload event distribution:

| Backend | Slow events |
|---|---:|
| `OpenGL` | 88 |
| `GLTieredTextureResidencyBackend` | 86 |
| `GLSparseTextureResidencyBackend` | 54 |

Slow upload events by backend and resident size:

| Backend | Resident size | Slow events |
|---|---:|---:|
| `GLTieredTextureResidencyBackend` | 64 | 77 |
| `OpenGL` | 64 | 74 |
| `GLSparseTextureResidencyBackend` | 1024 | 44 |
| `OpenGL` | 1024 | 14 |
| `GLTieredTextureResidencyBackend` | 1024 | 9 |
| `GLSparseTextureResidencyBackend` | 512 | 5 |
| `GLSparseTextureResidencyBackend` | 2048 | 4 |
| `GLSparseTextureResidencyBackend` | 256 | 1 |

Worst queue/lifecycle timings observed:

| Texture | Backend | Resident | Logged time |
|---|---|---:|---:|
| `sponza_ceiling_a_diff` | `GLTieredTextureResidencyBackend` | 1024 | 75820.52 ms |
| `sponza_ceiling_a_diff` | `OpenGL` | 1024 | 74024.33 ms |
| `sponza_column_b_diff` | `GLTieredTextureResidencyBackend` | 1024 | 52038.86 ms |
| `sponza_fabric_diff` | `GLTieredTextureResidencyBackend` | 1024 | 48804.63 ms |
| `sponza_flagpole_diff` | `GLTieredTextureResidencyBackend` | 1024 | 39562.86 ms |
| `sponza_details_diff` | `GLTieredTextureResidencyBackend` | 1024 | 39278.19 ms |

These numbers should not be read as a single blocking GL call. In the progressive OpenGL path, the logged `executionMs` currently represents lifecycle time from queue entry through completion rather than only active driver upload time. That still matters for user-visible residency latency, but the diagnostic should eventually split queue age, CPU-prep time, active upload time, and progressive wall time.

Sparse transition cancellations were heavily concentrated:

| Source | Cancellations |
|---|---:|
| `sponza_column_b_bump.png` | 108 |
| `sponza_thorn_bump.png` | 89 |
| `background_bump.png` | 66 |
| `lion_bump.png` | 34 |
| `sponza_column_a_bump.png` | 15 |
| `spnza_bricks_a_bump.png` | 7 |

This points to policy churn or superseded transition targets. On a slower CPU, cancellation after decode is especially expensive: the engine pays the source decode cost, throws away or supersedes the work, and then queues another target for the same source.

## General And Rendering Log Findings

`log_general.txt` shows the texture streaming manager tracking 76 textures with 37 visible during the startup Sponza view. It also shows render-thread texture jobs waiting for long periods:

- `TextureStreaming.FinalizeSparseTransitions` waited about 17.3 seconds at `18:08:42.969`.
- Similar waits occurred around `18:09:00.553`, `18:09:18.363`, and `18:09:33.980`.
- `TextureStreaming.ScheduleSparseTransition` jobs later consumed 14-17 ms slices on the render thread.

`log_rendering.txt` shows shadow atlas work overlapping with this period. The shadow atlas repeatedly exceeded the 2 ms budget, with major stalls around `18:08:42` and `18:09:18` in the 17 second range. That means texture finalization was not the only render-thread pressure source; shadow rendering and sparse texture finalization were contending during the same visible hitch window.

`log_opengl.txt` confirms sparse texture support was active:

- `ARB_sparse_texture` and `ARB_sparse_texture2` were available.
- RGBA8 sparse page size was 128x128.
- Shared GL context was enabled.

It also logged 18 shader texture binding errors around `18:12:44` where sampler `Texture0` was not bound and a fallback texture was substituted. Those errors are probably separate from the imported Sponza streaming queue, but they can still produce black/fallback-looking preview surfaces. `LightProbeComponent.Preview.cs` is worth checking because a null preview texture path can leave a preview material using a shader that still expects `Texture0`.

## Decode Path Findings

The runtime imported texture source path still decodes source images directly for resident transitions:

- `ThirdPartyTextureStreamingSource.LoadResidentData` creates a `MagickImage` from the source path.
- `XRTexture2D.BuildResidentDataFromImage` clones, resizes, and optionally builds a mip chain.
- Preview and promotion transitions can revisit the same source image through separate residency requests.
- While active imported model work suppresses cache warmup, `AssetManager.ResolveTextureStreamingAuthorityPath` can choose the raw source path when a cache payload is missing or stale.

That architecture is correct enough for validation, but it is not fast enough as the long-term runtime path. Raw PNG/JPG decode is CPU-heavy and gets worse when it happens repeatedly under streaming pressure. It also makes the "first useful pixel" wait on full source decode before the engine can downsample to a tiny preview.

## Why Textures Can Stay Black Or Low-Res

The logs support several likely causes:

- Preview starvation: the first visible streaming burst had 37 visible textures, but only 2 previews were ready. Any material without a resident preview would show fallback data until CPU prep and upload completed.
- Decode backlog: both tiered and sparse streaming decode gates are capped at 2 concurrent decodes, while the manager can queue many visible textures quickly. This protects the CPU but stretches time-to-preview.
- Superseded sparse transitions: 330 cancellations mean the same sources were repeatedly changing or losing their pending target. Canceled work can delay stable residency and waste CPU decode time.
- Policy final state: the run ended with 39 textures still at preview and 37 promoted. Some of that is expected for non-visible or low-priority maps, but it means the current summary alone cannot prove every visible material reached its intended detail level.
- Sampler fallback: the later `Texture0` binding errors can produce black/fallback surfaces even when the streaming manager is otherwise healthy.
- Render-thread contention: long shadow-atlas and sparse-finalization waits can delay the moment prepared texture data becomes visible.

## How To Improve Texture Decode Speed

The biggest improvement is to stop treating raw source images as the normal runtime streaming format.

1. Build a cooked texture payload during import.
   - Store precomputed mip levels with offsets, dimensions, format, source timestamp/hash, color space, and texture role.
   - Runtime streaming should read exactly the requested mip range instead of opening the original PNG/JPG and resizing it.
   - For Windows-first OpenGL, start with CPU-readable RGBA/normal payloads if needed, then move toward GPU-native compressed blocks.

2. Move high-value textures to GPU-ready compression.
   - Use BC7 for color/albedo where quality matters.
   - Use BC5 for normal maps.
   - Use BC4 or packed formats for scalar masks when appropriate.
   - A DDS/KTX2-style payload avoids runtime ImageMagick decode and reduces VRAM/upload bandwidth.

3. Add a per-source resident-data cache.
   - Key it by source path, last write time/hash, requested resident dimension, mip-chain flag, format, and color-space options.
   - Reuse decoded/downsampled data between preview and promotion requests.
   - Keep canceled transition results briefly so a near-identical requeue can claim them instead of decoding again.

4. Split preview decode from promotion decode.
   - Give visible previews their own urgent lane and do not let 1024/2048 promotions run ahead while visible textures have no real preview.
   - Delay high-res promotions until every visible texture has at least a 64 or 128 preview resident.
   - Consider making preview concurrency adaptive: more parallelism on CPUs with idle cores, less when frame time or import work is already under pressure.

5. Make cancellation cheaper.
   - Check cancellation before opening the source image, before resize, before mip generation, and before enqueueing upload work.
   - If cancellation happens after CPU prep, preserve the prepared data for a short reuse window when the next target is compatible.

6. Use role-aware placeholders.
   - Albedo fallback should be visibly non-final but not black.
   - Normal fallback should be flat normal.
   - Roughness/metalness/specular fallbacks should be neutral role-correct values.
   - Preview materials should bind a fallback texture explicitly when their intended preview texture is null.

## Telemetry Changes Needed

Already implemented in this change:

- CPU resident-build timing now reaches `log_textures.txt`.
- Slow CPU events include decode, clone, resize, mip-build, total, and total-threshold timings.
- Total resident-build time can trigger `Texture.UploadSlow` even when no individual phase crosses its own threshold.

Still recommended:

- Split `executionMs` into `queueWaitMs`, `cpuPrepMs`, `activeUploadMs`, and `lifecycleMs`.
- Fix suspicious committed-byte accounting for tiered preview uploads. This run logged many tiered 64-resident events with `committedBytes=3`, which is not plausible for a 64x64 RGBA texture and likely comes from computing target bytes before source dimensions are known.
- Add per-texture current stage to summaries: `noData`, `fallback`, `previewQueued`, `previewResident`, `promotionQueued`, `promoted`, `canceled`.
- Add source decode queue age and source path/cache path to slow CPU events.
- Add cancellation phase: canceled before decode, during decode, after CPU prep, during upload, or during finalization.

## Recommended Next Work

1. Fix preview starvation first.
   - Visible textures should get a real preview before any non-critical high-res promotion work.
   - The first milestone should be "all visible previews resident within a few frames after visibility is detected."

2. Add cooked texture payloads.
   - This is the durable decode-speed fix.
   - Raw ImageMagick source decode should become an import/cache miss path, not the regular streaming path.

3. Cache resident CPU prep results across superseded transitions.
   - This directly targets the 330 cancellation events from this run.

4. Repair timing semantics.
   - Keep lifecycle timing for user-visible latency, but separate it from active upload timing so the next pass can distinguish CPU decode starvation from render-thread upload pressure.

5. Investigate `Texture0` fallback errors.
   - Check preview and light-probe materials for null texture binding paths.
   - This is likely a separate black-surface source from imported Sponza streaming, but it is visible enough to confuse texture-streaming validation.

6. Re-run this Sponza startup case on a slower CPU profile after the new CPU timing log is present.
   - Compare first-visible-to-all-preview-ready time.
   - Compare cancellation count.
   - Compare slow CPU prep events by source and phase.
   - Confirm there are still zero upload validation failures.
