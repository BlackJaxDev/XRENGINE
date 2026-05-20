# Texture Runtime Streaming Validation

Status: active validation ledger
Last Updated: 2026-05-19

This testing note replaces the validation portions of the completed texturing implementation TODOs:

- [Texture Management Runtime TODO](../todo/texturing/texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](../todo/texturing/texture-streaming-cooked-cache-todo.md)
- [Texture Streaming Consolidation TODO](../todo/texturing/texture-streaming-consolidation-todo.md)

Architecture and future implementation work now lives in:

- [Texture Runtime, Streaming, And Virtual Texturing Design](../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)

## Purpose

Prove that the implemented v1 texture runtime is stable before enabling finer sparse-page residency or full virtual texturing. This document tracks scene runs, log evidence, test blockers, and closeout criteria for the current streamer.

This is intentionally a testing document, not an implementation roadmap. Feature follow-ups such as full SVT, Vulkan parity, bindless deferred texturing, runtime virtual textures, neural compression, color-space metadata, texture-role metadata, and adaptive decode policy belong in the canonical TODO.

## System Under Test

The implemented v1 texture runtime includes:

- `ImportedTextureStreamingManager` as the frame-level coordinator.
- `TextureStreamingRegistry` for weak records, usage, material binding observations, snapshots, and compaction.
- `TextureResidencyPolicy` for desired residency, priority, fairness, role multipliers, cooldowns, pressure fitting, and promotion fade.
- `TextureTransitionQueue` for pending transition replacement, cancellation, stale repair, and lifecycle state.
- `TextureUploadScheduler` for priority queueing, duplicate coalescing, generation cancellation, budget gates, and telemetry.
- `TextureResidencyState` for mutable `XRTexture2D` sparse runtime fields while preserving `XRBase.SetField(...)` mutation semantics.
- `GLTieredTextureResidencyBackend` as the dense fallback path.
- `GLSparseTextureResidencyBackend` as the OpenGL sparse mip residency path for page-aligned `Rgba8` textures.
- Metadata-first cooked texture streamability checks and mip-addressable `XRTS` payloads.
- `log_textures.txt` and the ImGui texture streaming diagnostics panel.

Known current limits:

- Partial sparse page residency is scaffolded but disabled by policy.
- Cooked payloads are mip-addressable, not page-addressable.
- Unit-test execution is blocked by unrelated duplicate `Engine` type compile errors in the unit-test project.
- Full cold/warm imported-scene validation still needs fresh runs.

## Historical Baselines

### May 1 Runtime Baseline

Session root:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_15-37-23_pid47288/`

Compare future runs against:

- `log_opengl.txt`
- `log_general.txt`
- `log_rendering.txt`
- `profiler-fps-drops.log`
- `profiler-render-stalls.log`

Baseline symptoms:

- Imported-scene texture promotion stayed delayed while import scopes forced `allowPromotions=False`.
- `XRWindow.ProcessPendingUploads` appeared as a render-thread stall during startup.
- Runtime-managed progressive uploads still pushed whole mips often enough to produce multi-frame stalls.
- OpenGL emitted `GL_INVALID_VALUE` from `TexSubImage2D` during imported texture residency changes, including the Sponza bump-map repro path.
- Resident transitions repeated for textures whose target residency had not materially changed.
- Shadow atlas tile rendering and texture upload bursts contended during startup.

Full baseline note: [Texture Management Runtime Baseline - 2026-05-01](texture-management-runtime-baseline-2026-05-01.md).

### May 1 Streaming Analysis

Session root:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_18-06-42_pid6412/`

Key observations:

- No `Texture.UploadValidationFailed` events were logged in this run.
- `log_opengl.txt` did not show the earlier invalid sparse upload rectangle failure.
- Preview residency was delayed: at `18:07:15.157`, only 2 of 76 tracked textures had previews ready while all 76 were pending.
- All 76 previews were not ready until `18:07:52.032`, roughly 37 seconds later.
- Visible texture work did not fully drain until `18:08:17.362`.
- The run logged 228 `Texture.UploadSlow` rows and 330 `Texture.TransitionCanceled` rows.
- Sparse transition cancellations were concentrated in bump maps, especially `sponza_column_b_bump.png`, `sponza_thorn_bump.png`, `background_bump.png`, and `lion_bump.png`.
- Texture finalization and shadow atlas work overlapped in the same hitch window.

Full analysis: [Texture Streaming Run Analysis - 2026-05-01 18:06](texture-streaming-run-analysis-2026-05-01-180642.md).

### May 1 Sparse-To-Dense Follow-Up

Session root:

`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-01_21-31-27_pid28976/`

Observed failure:

- Six `Texture.UploadValidationFailed` events appeared during promotion-after-demotion.
- Uploads tried to write `512x512` data to `mip=2` while the allocated GL level was `256x256`.
- The dense upload path inherited stale sparse residency state, so mip indices were offset by the prior sparse `residentBase`.

Implemented fix:

- `XRTexture2D.ApplyResidentData` clears sparse state when publishing dense/tiered resident data.
- `GLTexture2D.UpdateMipmaps` recreates GL storage when leaving sparse storage even if logical dimensions and level count appear compatible.
- `Texture.SparseStateClearedForDenseUpload` identifies the boundary in future logs.

Validation target:

- Promotion-after-demotion should leave `Texture.UploadValidationFailed` at zero.
- `Texture.SparseStateClearedForDenseUpload` should appear only for legitimate sparse-to-dense handoffs.
- If black surfaces persist with no upload validation failure, inspect `Texture.BindingRisk` and material/shader binding state.

## Validation Commands

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "GLTexture2DContractTests|ImportedTextureStreamingContractTests|ImportedTextureStreamingPhaseTests|RuntimeRenderingHostServicesTests" --no-restore
```

Known blocker:

- Targeted `dotnet test` currently fails before test execution because unrelated unit-test files hit duplicate `Engine` type compile errors between `XREngine.Runtime.Rendering` and `XREngine`.

## Core Validation Checklist

- [ ] Run `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`.
- [ ] Run `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`.
- [ ] Re-run targeted unit tests after the duplicate `Engine` test-project blocker is fixed.
- [ ] Capture a fresh cold-cache Sponza startup run.
- [ ] Capture a fresh warm-cache Sponza startup run.
- [ ] Link the resulting `log_textures.txt`, `log_opengl.txt`, `log_rendering.txt`, `log_general.txt`, profiler FPS-drop log, and profiler render-stall log.
- [ ] Confirm `log_textures.txt` is created for Debug file-logging sessions.
- [ ] Confirm no texture upload emits `GL_INVALID_VALUE` in `log_opengl.txt`.
- [ ] Confirm `Texture.UploadValidationFailed` remains zero in normal scene runs.
- [ ] Confirm stale-generation cancellation is visible when forced by a resize/recreate test.
- [ ] Confirm non-sparse full-push storage repair happens only for non-sparse, non-external-memory immutable textures.
- [ ] Confirm no single texture upload work item exceeds the configured texture budget by more than one chunk.
- [ ] Confirm large texture promotions advance over multiple frames without black mips or invalid sampling.
- [ ] Confirm pending texture uploads are visible with count, bytes, and oldest wait.
- [ ] Confirm `StartProgressiveCoroutine`-style texture jobs no longer take 30-100 ms render-thread slices.

## Cold-Cache Sponza Run

Record:

- [ ] Session root.
- [ ] Commit SHA or branch.
- [ ] GPU, driver, CPU, and memory.
- [ ] Raw source decode count.
- [ ] Cache miss count.
- [ ] Cache write count.
- [ ] First visible preview time.
- [ ] All visible previews resident time.
- [ ] Pending visible transition drain time.
- [ ] Upload validation failure count.
- [ ] Final promoted/preview texture counts.

Pass criteria:

- [ ] First import writes streaming-usable cached `XRTexture2D` assets with enough resident data for preview and promotion.
- [ ] Visible textures begin promotion during import when bound or visible.
- [ ] The run does not reach a stable visible state with dozens of visible textures and only a handful of previews ready.
- [ ] Black placeholder surfaces are limited to true failure paths, not normal startup streaming.
- [ ] `Texture.CacheWrite` and cache fallback events explain all source decode paths.

## Warm-Cache Sponza Run

Record:

- [ ] Session root.
- [ ] Commit SHA or branch.
- [ ] Cache hit count.
- [ ] Slow cache read count.
- [ ] Worst `cacheReadMs`.
- [ ] Worst `cacheParseMs`.
- [ ] Promotion queue wait.
- [ ] Render-thread upload chunk timings.
- [ ] Time to all visible previews resident.
- [ ] Time to pending visible transitions drained.
- [ ] Slow CPU prep events by phase.
- [ ] Transition cancellation count.
- [ ] Final promoted/preview texture counts.

Pass criteria:

- [ ] A second import of the same source chooses `AssetTextureStreamingSource`.
- [ ] Warm-cache streaming does not construct `MagickImage` from the original source path except for missing, stale, unreadable, or non-streamable cache fallback.
- [ ] Cache usability checks read only the manifest/header and do not hydrate resident mip blobs.
- [ ] `Texture.CacheReadSlow` parse times are materially lower after metadata-first source loading.
- [ ] `cacheReadMs` and `cacheParseMs` clearly separate file I/O, manifest parse, mip blob copy, CPU conversion, and GPU upload once that telemetry split lands.

## Residency And Policy Checks

- [ ] Duplicate `ApplyResidentData` calls for unchanged residency disappear from logs.
- [ ] Newly promoted visible textures do not demote during the cooldown window.
- [ ] Texture quality changes are monotonic under stable camera/view conditions.
- [ ] Low-VRAM-budget runs log pressure-driven demotions with bytes reclaimed and reasons.
- [ ] Visible normal, bump, height, alpha, mask, and opacity maps stay at a preview-size floor unless explicit VRAM pressure requires a 1px target.
- [ ] Cancellation-heavy bump maps no longer repeatedly decode or prepare identical resident data after compatible superseded transitions.
- [ ] `Texture.SparseStateClearedForDenseUpload` appears only when a texture legitimately crosses from sparse residency to dense/tiered upload.
- [ ] If a future black-surface repro has no `Texture.UploadValidationFailed` and no sparse-to-dense warning nearby, file the issue against material, shader, lighting, or non-streaming binding paths.

## Render-Work And Diagnostics Checks

- [ ] Shadow atlas tile bursts no longer coincide with unbounded texture upload queue waits.
- [ ] Texture promotions continue at a controlled rate while shadows are active.
- [ ] `Texture.DelayedByShadow` identifies frames where shadow work consumes the shared render-work budget first.
- [ ] `TextureStreaming.FinalizeSparseTransitions` no longer waits for 15-17 second spans during Sponza startup.
- [ ] Slow-frame logs identify which subsystem consumed the render-work budget.
- [ ] The ImGui texture streaming diagnostics panel identifies the top VRAM textures and oldest pending uploads live.
- [ ] The diagnostics panel remains usable with hundreds of tracked textures.
- [ ] The panel is disabled or low-overhead by default in normal editor operation.
- [ ] Summary/slow texture logging does not introduce per-frame allocations in hot paths.
- [ ] `log_textures.txt` line schema is stable in a fresh editor run or any breaking change is documented.
- [ ] `Texture.BindingRisk` entries are either expected non-streaming paths or tracked follow-ups.
- [ ] `Texture0` sampler binding errors are gone or explained by a separate non-streaming path.

## Allocation Audit

Run the allocation reporting tool against these paths and capture the before/after delta in `docs/work/audit/`:

- [ ] Registry snapshot collection.
- [ ] Usage recording.
- [ ] Policy scoring.
- [ ] Transition queueing.
- [ ] Scheduler submit/execute.
- [ ] OpenGL upload chunks.
- [ ] Texture diagnostics panel while closed.
- [ ] Texture diagnostics panel while open with hundreds of tracked textures.

Hot-path validation should flag new LINQ, captured lambdas, string formatting, boxing, transient lists, and avoidable heap allocations.

## Run Record Template

Copy this section for each new validation run.

### YYYY-MM-DD Scenario Name

Session root:

`Build/Logs/...`

Build:

- Branch:
- Commit:
- Configuration:
- Renderer:
- GPU and driver:
- CPU and memory:
- Cache state: cold or warm

Logs:

- [ ] `log_textures.txt`
- [ ] `log_opengl.txt`
- [ ] `log_rendering.txt`
- [ ] `log_general.txt`
- [ ] `profiler-fps-drops.log`
- [ ] `profiler-render-stalls.log`

Metrics:

| Metric | Value |
|---|---:|
| First visible preview time | |
| All visible previews resident time | |
| Pending visible transition drain time | |
| Cache hits | |
| Cache misses | |
| Raw source decodes | |
| Transition cancellations | |
| Upload validation failures | |
| Texture `GL_INVALID_VALUE` errors | |
| Worst queue wait | |
| Worst active upload chunk | |
| Worst sparse finalization wait | |
| Final promoted textures | |
| Final preview textures | |

Result:

- [ ] Pass
- [ ] Fail
- [ ] Inconclusive

Notes:

- TBD

## Closeout Criteria

This validation line can be closed when:

- [ ] Runtime and editor builds pass.
- [ ] Targeted texture tests pass or remaining failures are proven unrelated and tracked elsewhere.
- [ ] Cold-cache and warm-cache Sponza runs are linked with metrics.
- [ ] No texture upload emits `GL_INVALID_VALUE`.
- [ ] `Texture.UploadValidationFailed` remains zero in normal runs.
- [ ] Visible textures receive previews quickly enough that normal startup does not show widespread black or placeholder surfaces.
- [ ] Warm-cache imports use cooked texture payloads instead of raw source decode.
- [ ] Sparse-to-dense handoff logs appear only for legitimate handoffs.
- [ ] Texture/shadow shared-budget behavior is explained by logs.
- [ ] Texture diagnostics remain low overhead when disabled.
- [ ] Remaining non-v1 work is tracked only in the canonical roadmap.
