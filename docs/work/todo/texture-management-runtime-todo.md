# Texture Management Runtime TODO

> Status: **active phased TODO**
> Scope: runtime texture residency, OpenGL upload safety, texture upload scheduling, VRAM telemetry, dedicated texture logs, and editor diagnostics.
> Source design: [Texture Management Runtime Design](../design/texture-management-runtime-design.md)

## Target Outcome

Texture management becomes a budgeted runtime service with stable material texture identity, validated GPU uploads, clear residency policy, and a dedicated texture log.

The v1 target is:

- No `GL_INVALID_VALUE` from texture mip uploads during imported-scene streaming.
- No stale queued upload may write into texture storage created for a different generation.
- Visible textures promote during import under a small budget instead of waiting for all import activity to finish.
- Large mips upload in bounded chunks; no single texture upload job can block the render thread for tens of milliseconds.
- Promotions, demotions, duplicate transitions, stale cancellations, VRAM pressure, and slow uploads are visible in `log_textures.txt`.
- Texture uploads and shadow atlas work cooperate under a shared render-work budget.

## Non-Negotiable Design Rules

- [x] Every `TexSubImage2D` call validates mip level, upload rectangle, allocated mip dimensions, format context, sparse state, and storage generation before touching the driver.
- [x] Queued texture work captures a storage generation and cancels or restarts when the generation changes.
- [x] Partially uploaded mips are never exposed for sampling.
- [x] Sparse textures never expose uncommitted pages through `GL_TEXTURE_BASE_LEVEL` / `GL_TEXTURE_MAX_LEVEL`.
- [x] Full immutable-storage recreation is a repair path only; normal promotion should use planned residency transitions.
- [x] Texture uploads have an internal time stop, not only an outer job budget.
- [x] Promotion is fast, demotion is conservative, and duplicate transitions are coalesced.
- [x] VRAM pressure decisions are logged with bytes reclaimed and the reason for each demotion.
- [x] `log_textures.txt` is the first place to look for texture residency, upload timing, queue wait, and VRAM diagnostics.
- [x] Texture logging has summary/slow/verbose modes so diagnostics do not become a hot-path allocation or IO problem.
- [x] Render-thread hot paths stay allocation-aware: no LINQ, captured lambdas, string formatting, or transient lists in per-frame texture scoring or upload submission after warmup.

## Primary Code Areas

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D*.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D*.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs`
- `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/ShadowRenderPipeline.cs`
- `XREngine.UnitTests/Rendering/*Texture*`
- `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`

## Phase 0: Branch And Baseline Capture

**Goal:** isolate the texture-management work and preserve a before/after diagnostic baseline.

### Tasks

- [x] Create a dedicated branch for this TODO, for example `texture-management-runtime`.
- [x] Preserve the May 1, 2026 Sponza logs as the initial baseline:
  - [x] `log_opengl.txt`
  - [x] `log_general.txt`
  - [x] `log_rendering.txt`
  - [x] `profiler-fps-drops.log`
  - [x] `profiler-render-stalls.log`
- [x] Add a short baseline note or linked audit summarizing:
  - [x] delayed texture promotion while `allowPromotions=False`
  - [x] slow `XRWindow.ProcessPendingUploads`
  - [x] whole-mip progressive upload stalls
  - [x] `GL_INVALID_VALUE` from `TexSubImage2D`
  - [x] repeated resident transitions
  - [x] slow shadow atlas tile contention
- [x] Identify the nearest targeted tests and source-contract tests for each phase.

### Exit Criteria

- [x] Branch exists.
- [x] Baseline symptoms and log paths are documented.
- [x] Phase validation commands are listed before more implementation begins.

## Phase 1: OpenGL Upload Safety

**Goal:** prevent invalid or stale texture uploads from reaching OpenGL.

### Tasks

- [x] Add `GLTexture2D` storage generation tracking.
- [x] Advance the storage generation on immutable allocation, resize invalidation, sparse logical allocation, GL object regeneration, and delete/reset paths.
- [x] Capture storage generation in progressive GL upload coroutines.
- [x] Cancel stale progressive GL upload work when the current generation differs from the captured generation.
- [x] Add allocated mip dimension resolution for 2D textures.
- [x] Validate `TexSubImage2D` mip level, offset, extent, allocated mip dimensions, and storage state before the driver call.
- [x] Emit diagnostics with texture name, binding id, upload rect, allocated base dims, allocated mip dims, allocated level count, mip count, format, sparse state, streaming lock mip, and generation.
- [x] Recreate immutable non-sparse storage when a full-push upload proves the current storage is too small.
- [x] Route validation failures into the future texture log once `log_textures.txt` exists.
- [ ] Add runtime validation against the Sponza repro to confirm the original `sponza_thorn_bump` `GL_INVALID_VALUE` no longer appears.
- [x] Audit `GLTexture2DArray`, `GLTextureCube`, and video/PBO upload paths for equivalent validation gaps.

### Exit Criteria

- [ ] No texture upload emits `GL_INVALID_VALUE` in the Sponza repro.
- [ ] Stale-generation cancellations are visible in diagnostics when forced by a resize/recreate test.
- [ ] Full-push repair happens only for non-sparse, non-external-memory immutable textures.
- [x] Unit/source-contract coverage protects the validation and generation-gating hooks.

### Validation

- [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
- [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter GLTexture2DContractTests --no-restore` once the unrelated duplicate `Engine` type issue is resolved.
- [ ] Editor Sponza import run with OpenGL debug logging enabled.

## Phase 2: Bounded Texture Upload Scheduler

**Goal:** make "progressive" uploads truly progressive by time and chunk, not only by bytes per outer job.

### Tasks

- [x] Introduce a texture-specific upload work item model:
  - [x] texture reference / weak reference
  - [x] upload kind: preview, promotion, repair, demotion, render-target init
  - [x] source kind: CPU pointer, cooked mip, sparse page, PBO
  - [x] mip/page range
  - [x] estimated bytes
  - [x] captured storage generation
  - [x] priority/deadline class
- [x] Replace runtime-managed whole-mip `PushMipLevel` promotion calls with chunked upload requests.
- [x] Use row chunking for supported CPU-pointer uploads.
- [x] Add a small-mip fast path for mips below a safe byte/time threshold.
- [x] Add per-chunk stopwatch checks so upload code yields when the texture budget is exhausted.
- [x] Keep partial scanline/page chunks hidden from sampling until the whole mip/page is complete.
- [x] Preserve the existing PBO/shared-context path where it helps, but fence exposure so render sampling never waits unnecessarily.
- [x] Record queue wait time and execution time for every upload work item.
- [x] Coalesce duplicate upload work for the same texture, generation, and target mip/page range.

### Exit Criteria

- [ ] No single texture upload work item exceeds the configured texture budget by more than one chunk.
- [ ] Large texture promotions advance over multiple frames without black mips or invalid sampling.
- [ ] Pending texture uploads are visible as a queue with count, bytes, and oldest wait.
- [ ] Sponza repro no longer shows `StartProgressiveCoroutine` jobs taking 30-100 ms.

### Validation

- [ ] Instrumented run with upload chunk timing.
- [ ] Sponza import with many large textures.
- [ ] Low-end texture budget run, for example 1-2 ms/frame, to force chunking behavior.

## Phase 3: Residency Policy, Coalescing, And Hysteresis

**Goal:** remove visible quality bounce and delayed promotion caused by overly coarse import-era policy.

### Tasks

- [x] Add pending-transition coalescing in `ImportedTextureStreamingManager`.
- [x] Treat identical target resident size, page selection, include-mip-chain flag, source version, and backend generation as a no-op.
- [x] Add promotion cooldown and demotion cooldown fields to streaming records.
- [x] Promote quickly when projected pixel span exceeds resident quality.
- [x] Demote only after a grace period below target quality.
- [x] Keep newly promoted visible textures pinned for a minimum lifetime.
- [x] Allow a small visible-promotion budget while imported model scopes are active.
- [x] Use recently bound material textures as fallback priority when no visibility snapshot exists yet.
- [x] Promote related material textures enough to avoid obvious albedo/normal/roughness detail mismatch.
- [x] Avoid demotion during active import unless VRAM pressure requires it.
- [x] Log pressure-driven demotions with bytes reclaimed and reason.

### Exit Criteria

- [ ] Visible textures begin promotion during import when bound or visible.
- [ ] Duplicate `ApplyResidentData` calls for unchanged residency disappear from logs.
- [ ] Newly promoted visible textures do not demote during the cooldown window.
- [ ] Texture quality changes are monotonic under stable camera/view conditions.

### Validation

- [ ] Sponza startup/import capture.
- [ ] Camera sweep across high-resolution imported materials.
- [ ] Low VRAM budget run that forces demotion.
- [ ] Unit tests for coalescing keys, cooldowns, promotion during import, and pressure demotion order.

## Phase 4: Dedicated Texture Log And Telemetry

**Goal:** make texture behavior diagnosable from one log file.

### Tasks

- [x] Add `Build/Logs/<configuration>_<tfm>/<platform>/<session>/log_textures.txt`.
- [x] Add rendering settings for texture logging:
  - [x] disabled
  - [x] summary
  - [x] slow-only
  - [x] verbose
- [x] Add allocation-free or pooled event formatting for hot paths.
- [x] Emit texture lifecycle events:
  - [x] `Texture.ImportPreviewQueued`
  - [x] `Texture.ImportPreviewReady`
  - [x] `Texture.VisibilityRecorded`
  - [x] `Texture.ResidencyDesired`
  - [x] `Texture.TransitionQueued`
  - [x] `Texture.TransitionCoalesced`
  - [x] `Texture.TransitionCanceled`
  - [x] `Texture.TransitionApplied`
  - [x] `Texture.UploadChunk`
  - [x] `Texture.UploadSlow`
  - [x] `Texture.StorageAllocated`
  - [x] `Texture.StorageRecreated`
  - [x] `Texture.UploadValidationFailed`
  - [x] `Texture.VramPressure`
  - [x] `Texture.VramSummary`
- [x] Include required fields:
  - [x] frame id
  - [x] texture name
  - [x] source path or asset id
  - [x] GL binding id
  - [x] logical dimensions
  - [x] resident dimensions or resident mip/page range
  - [x] mip/page upload range
  - [x] bytes uploaded or committed
  - [x] estimated committed VRAM
  - [x] queue wait time
  - [x] execution time
  - [x] storage generation
  - [x] backend name
  - [x] reason
- [x] Emit periodic summaries every 60 frames or on slow frames.
- [x] Mirror high-severity texture context into `log_opengl.txt` when OpenGL emits an error.
- [x] Keep slow thresholds configurable, with defaults:
  - [x] CPU decode/resize: 5 ms
  - [x] mip build: 5 ms
  - [x] render-thread upload chunk: 2 ms
  - [x] full transition: 8 ms
  - [x] queue wait: 100 ms
  - [x] storage recreate: always
  - [x] validation failure: always

### Exit Criteria

- [ ] `log_textures.txt` is created for Debug file-logging sessions.
- [ ] A single texture log explains promotions, demotions, skipped uploads, stale cancels, slow uploads, and VRAM pressure events.
- [ ] Summary/slow logging does not introduce per-frame allocations in hot paths.

### Validation

- [ ] Compare texture log against existing OpenGL/rendering/profiler logs for the same session.
- [ ] Verify no visible behavior change when texture logging is disabled.
- [ ] Verify verbose logging can be enabled temporarily from settings.

## Phase 5: Shared Render-Work Budget

**Goal:** prevent texture uploads, shadow atlas updates, shader compilation, and mesh uploads from starving each other.

### Tasks

- [x] Define a shared render-work budget coordinator.
- [x] Expose budget state:
  - [x] frame budget remaining
  - [x] startup boost state
  - [x] texture upload queue depth
  - [x] shadow atlas queue depth
  - [x] shader/mesh upload queue depth
  - [x] oldest queue wait
  - [x] last completed render age
- [x] Make texture uploads consult the shared budget before background promotions.
- [x] Make shadow atlas work defer low-priority tiles when urgent visible texture repair is pending.
- [x] Log when shadow work delays texture work.
- [x] Log when texture work delays shadow work.
- [x] Preserve startup boosts without allowing multi-second starvation.
- [x] Add budget counters to profiler summaries.

### Exit Criteria

- [ ] Shadow atlas tile bursts no longer coincide with unbounded texture upload queue waits.
- [ ] Texture promotions continue at a controlled rate while shadows are active.
- [ ] Slow-frame logs identify which subsystem consumed the budget.

### Validation

- [ ] Sponza scene with active shadow atlas updates.
- [ ] Stress scene with many shadow-capable lights and many imported textures.
- [ ] Compare max render stall and oldest texture queue wait before/after.

## Phase 6: Editor Diagnostics And Tooling

**Goal:** make texture residency and upload state visible without spelunking logs.

### Tasks

- [x] Add an ImGui texture streaming diagnostics panel.
- [x] Show tracked texture rows with:
  - [x] texture name/source path
  - [x] logical size
  - [x] resident size or mip/page range
  - [x] estimated committed bytes
  - [x] desired resident target
  - [x] priority score
  - [x] visibility state
  - [x] pending transition state
  - [x] oldest queue wait
  - [x] last upload duration
  - [x] backend
- [x] Add sortable columns for VRAM bytes, queue wait, upload time, and priority.
- [x] Add filters for visible, pending, slow, pressure-demoted, and validation-failed textures.
- [x] Add global summary counters to the rendering diagnostics area.
- [x] Add optional one-click "dump texture summary" action to write an immediate `Texture.VramSummary`.

### Exit Criteria

- [ ] The editor can identify the top VRAM textures and oldest pending uploads live.
- [ ] The panel remains usable with hundreds of tracked textures.
- [ ] Diagnostics are disabled or low-overhead by default in normal editor operation.

### Validation

- [ ] Open/close the panel during Sponza import.
- [ ] Verify no excessive allocations while panel is closed.
- [ ] Verify sorted/filtered views match `log_textures.txt` summaries.

## Phase 7: Cross-Backend And Asset Pipeline Follow-Up

**Goal:** keep the OpenGL work from hard-coding policy decisions that Vulkan and cooked texture assets will need later.

### Tasks

- [x] Define renderer-neutral residency and upload telemetry structs.
- [x] Audit OpenGL-only fields exposed above the backend layer.
- [x] Add Vulkan TODO hooks for image upload progress, sparse image residency, and generation-gated work.
- [x] Align cooked texture source metadata with upload scheduler needs:
  - [x] per-mip offsets
  - [x] page offsets when available
  - [x] source version/hash
  - [x] resident byte estimates by format
- [x] Decide how future BCn/neural texture compression reports resident bytes and upload chunks.
- [x] Update the sparse texture streaming plan if this TODO changes backend responsibilities.

### Exit Criteria

- [x] Streaming policy is renderer-neutral.
- [x] OpenGL details stay behind the OpenGL backend.
- [x] Future Vulkan and cooked-asset phases have clear extension points.

## Phase 8: Final Validation And Cleanup

**Goal:** prove the system fixes the logged regressions without hiding new stalls.

### Tasks

- [ ] Run Sponza import/startup repro and compare against the May 1, 2026 baseline.
- [ ] Run Unity/Poiyomi avatar import with albedo/normal/mask/ramp textures.
- [ ] Run a shadow-heavy scene while texture promotions are pending.
- [ ] Run a low-VRAM-budget demotion scenario.
- [ ] Verify no texture `GL_INVALID_VALUE` errors.
- [ ] Verify no upload job exceeds the configured texture budget by more than one chunk.
- [ ] Verify visible texture promotion starts during import.
- [ ] Verify duplicate transitions are coalesced.
- [ ] Verify `log_textures.txt` explains every promotion, demotion, stale cancel, skipped upload, validation failure, and pressure event.
- [x] Update stable docs if settings, log files, launch workflow, or editor diagnostics changed.
- [x] Remove temporary verbose diagnostics that are not guarded by settings.

### Exit Criteria

- [ ] The original texture pop-in, upload stalls, and GL upload error are fixed or have documented residual follow-ups.
- [x] Validation logs are linked from this TODO or a companion testing note.
- [x] Any remaining high-cost shadow atlas contention is tracked in the dynamic shadow atlas TODO.

### Validation Commands

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "GLTexture2DContractTests|ImportedTextureStreamingPhaseTests|RuntimeRenderingHostServicesTests" --no-restore
```

Current validation after implementation, 2026-05-01:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore` passes. Remaining warnings are existing NuGet advisories plus unrelated nullability/volatile warnings.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passes. Remaining warnings are existing NuGet advisories and pre-existing duplicate-type warnings from the modularization split.
- Targeted `dotnet test` is blocked before test execution by the known duplicate `Engine` type compile failure in unrelated unit-test files such as `SkinnedBoundsRecomputePolicyOverrideTests.cs`, `OctreeStatsTimingTests.cs`, and `VulkanTodoP2ValidationTests.cs`.

## Final Task

- [ ] Merge the dedicated texture-management branch back into `main` after all phases are complete, validation has passed, and any follow-up TODOs have been filed.
