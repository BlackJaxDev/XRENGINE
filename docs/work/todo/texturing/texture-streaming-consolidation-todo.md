# Texture Streaming Consolidation TODO

Status: implementation pass complete; editor scene validation and merge remain

Source context:
- [Texture Management Runtime TODO](texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](texture-streaming-cooked-cache-todo.md)
- [Texture Management Runtime Design](../../design/texturing/texture-management-runtime-design.md)
- Latest inspected run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-05-02_21-44-39_pid4156/`

## Goal

Consolidate texture streaming into a small set of explicit runtime services instead of a large manager and several texture partial classes sharing policy, source I/O, cache parsing, upload scheduling, residency mutation, OpenGL mechanics, and diagnostics.

The cleanup should preserve the behavior already achieved by the texture-management and cooked-cache work while making the v1 architecture easier to reason about, test, and extend to Vulkan.

## Target Outcome

- `ImportedTextureStreamingManager` becomes an orchestration facade, not the owner of every streaming concern.
- Texture source loading, residency policy, registry state, transition queueing, upload scheduling, and renderer backends each live in focused files.
- There is one texture upload scheduler for priority, coalescing, frame budget, queue wait, storage-generation cancellation, and telemetry.
- Cooked texture cache reads are metadata-first and read only the mip/page blobs needed for the requested resident target.
- `XRTexture2D` keeps stable material-visible identity and logical texture description; mutable residency state is owned by explicit runtime/backend objects.
- OpenGL-specific sparse and tiered residency code is no longer embedded in renderer-neutral policy code.
- Tests cover behavior through fake sources/backends/schedulers instead of mostly checking source text.

## Implementation Closeout - 2026-05-04

The consolidation implementation landed on the existing `texture-management-runtime` branch per owner direction.

Runtime service boundaries now are:

- `ImportedTextureStreamingManager` is the frame-level coordinator.
- `TextureStreamingSourceFactory`, `AssetTextureStreamingSource`, `ThirdPartyTextureStreamingSource`, and `TextureStreamingResidentDataReuseCache` own source/cache loading concerns.
- `TextureResidencyPolicy` owns pure residency decisions, priority, fairness, role multipliers, sparse page selection, and promotion-fade math.
- `TextureStreamingRegistry` owns weak texture records, usage recording, material binding recording, compaction, and snapshot collection.
- `TextureTransitionQueue` owns pending transition replacement, stale-transition repair, cancellation, and clear/reset state.
- `TextureUploadScheduler` owns progressive upload queue state, duplicate coalescing, priority ordering, active-slot gating, frame byte budgeting, and queue-wait telemetry.
- `TextureResidencyState` centralizes mutable `XRTexture2D` sparse residency fields while preserving `XRBase.SetField(...)` mutation semantics on the public properties.
- OpenGL tiered and sparse residency backends live in `OpenGLTextureResidencyBackends.cs` behind `ITextureResidencyBackend`; the interface exposes neutral `SupportsSparseResidency` rather than OpenGL-specific capability fields.
- Cooked texture usability now reads the streamable manifest/header through `TryReadTextureStreamingManifestFromTextureAssetFileBytes(...)` instead of hydrating resident mip blobs.

Validation performed:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore` passes with 0 errors. Remaining warnings are existing NuGet advisories plus unrelated nullability/volatile diagnostics in non-texture paths.
- Targeted unit-test execution is still blocked before test execution by the unrelated unit-test project compile issue where `Engine` exists in both `XREngine.Runtime.Rendering` and `XREngine`.

## Non-Goals

- Do not change texture quality policy, cache format, or runtime behavior in the first file-split phase.
- Do not add or upgrade compression dependencies as part of this cleanup.
- Do not remove the tiered fallback backend; sparse remains preferred, tiered remains required.
- Do not require Vulkan feature parity before the OpenGL path is simplified, but avoid new OpenGL-only policy fields above the backend layer.
- Do not run residency policy or upload scheduling on dedicated server, pose server, or other headless roles; the consolidated services must opt in via `RuntimeRenderingHostServices` and stay no-op without a renderer.

## Behavioral Invariants To Preserve

These are subtle behaviors already in place that the refactor must not regress. Each phase that touches the relevant code must explicitly verify the invariant still holds.

- Imported streaming usage ignores non-main passes (scene captures, mirrors, light probes, shadow passes); only main-view sampling counts as visible usage.
- Promotion-pop reduction via transient `XRTexture2D.LodBias` decay survives the residency-state move (Phase 5): when resident size increases, bias starts at the mip delta and decays back to the texture's baseline bias over a few frames.
- Embedded `XRTexture2D` YAML round-trip continues to use `XRAssetYamlConverter` + `XRTexture2DYamlTypeConverter`'s `CookedBinary` envelope so font atlases and other embedded textures preserve atlas payload and original-path round-trips.
- `XRTexture2D` material-visible identity (the reference materials hold) is stable across sparse/dense transitions and across residency-state extraction.
- Storage-generation cancellation: queued upload work captures the storage generation at submission and drops if the generation has changed when it executes.
- Partially uploaded mips/pages are never exposed for sampling, including across the new scheduler boundary.
- Sparse textures never expose uncommitted pages through `GL_TEXTURE_BASE_LEVEL` / `GL_TEXTURE_MAX_LEVEL`.

## Primary Code Areas

- `XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D*.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D*.cs`
- `XREngine.Runtime.Rendering/Runtime/TextureUploadWorkItem.cs`
- `XREngine.Runtime.Rendering/Runtime/TextureRuntimeTelemetry.cs`
- `XREngine.Runtime.Rendering/Runtime/RenderWorkBudgetCoordinator.cs`
- `XREngine.Runtime.Rendering/Runtime/TextureRuntimeDiagnostics.cs`
- `XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs`
- `XREngine.UnitTests/Rendering/*Texture*`
- `docs/work/design/texturing/texture-management-runtime-design.md`
- `docs/work/design/texturing/sparse-texture-streaming-plan.md`

## Phase 0: Branch And Baseline

**Goal:** isolate the consolidation and preserve evidence for behavior and performance comparisons.

- [x] Keep this consolidation on the existing `texture-management-runtime` branch per owner direction.
- [ ] Preserve or link the current texture-streaming run logs:
  - [ ] `log_textures.txt`
  - [ ] `log_rendering.txt`
  - [ ] `log_opengl.txt`
  - [ ] `log_general.txt`
- [ ] Record current file sizes and ownership hotspots:
  - [ ] `ImportedTextureStreamingManager.cs`
  - [ ] `XRTexture2D.cs`
  - [ ] `XRTexture2D.ImportedStreaming.cs`
  - [ ] `XRTexture2D.SparseStreaming.cs`
  - [ ] `XRTexture2D.StreamingPayload.cs`
  - [ ] `GLTexture2D.Upload.cs`
  - [ ] `GLTexture2D.SparseStreaming.cs`
  - [ ] `GLTexture2D.SparseStreaming.Async.cs`
- [ ] Record current warm-cache symptoms from `log_textures.txt`, especially `Texture.CacheReadSlow` parse times and queue waits.
- [x] Identify the targeted validation set before refactoring:
  - [x] `ImportedTextureStreamingPhaseTests`
  - [x] `ImportedTextureStreamingContractTests`
  - [x] `GLTexture2DContractTests`
  - [x] `RuntimeRenderingHostServicesTests`
  - [x] warm-cache editor Sponza run
  - [x] cold-cache editor Sponza run

## Phase 1: Split Files Without Behavior Changes

**Goal:** make ownership visible before changing architecture.

- [x] Extract streaming source types from `ImportedTextureStreamingManager.cs`:
  - [x] `ITextureStreamingSource`
  - [x] `TextureStreamingSourceFactory`
  - [x] `AssetTextureStreamingSource`
  - [x] `ThirdPartyTextureStreamingSource`
  - [x] `TextureStreamingResidentDataReuseCache`
- [x] Extract concurrency helpers that are not texture-policy specific:
  - [x] `PriorityAsyncSemaphore`
- [x] Extract neutral residency contracts and telemetry-facing records:
  - [x] `TextureStreamingResidentData`
  - [x] `ImportedTextureStreamingUsage`
  - [x] `ImportedTextureStreamingTelemetry`
  - [x] `ImportedTextureStreamingTextureTelemetry`
  - [x] `ITextureResidencyBackend`
- [x] Move `GLTieredTextureResidencyBackend` out of the manager into an OpenGL texture-streaming backend file.
- [x] Move `GLSparseTextureResidencyBackend` out of the manager into an OpenGL texture-streaming backend file.
- [x] Keep the existing `XRTexture2D` static facade methods intact during the split.
- [x] Run formatting and targeted build after the pure move phase.

## Phase 2: Extract Policy And Registry

**Goal:** make residency decisions deterministic and testable without renderer or disk dependencies.

- [x] Create a `TextureResidencyPolicy` service or static module for pure decisions:
  - [x] desired resident size
  - [x] page selection
  - [x] sampler-role multipliers
  - [x] priority score
  - [x] promotion and demotion cooldown decisions
  - [x] pressure demotion order
  - [x] fairness group key
- [x] Create a `TextureStreamingRegistry` for tracked texture records, weak references, usage recording, material binding recording, and snapshot collection.
- [x] Create a `TextureTransitionQueue` for pending transition state, coalescing, cancellation, stale-transition repair, and lifecycle timing.
- [x] Define service ownership and lifetime: manager-owned policy/registry/transition queue with the upload scheduler as the shared runtime singleton; execution still gates through `RuntimeRenderingHostServices` and renderer backends.
- [x] Document the thread-safety contract for each new service (which members are main-thread, render-thread, worker-thread, or free-threaded) at the type level.
- [x] Keep `ImportedTextureStreamingManager` as the frame-level coordinator:
  - [x] collect snapshots
  - [x] ask policy for desired residency
  - [x] enqueue transition intents
  - [x] publish telemetry
  - [x] dump summaries
- [x] Add behavior tests for policy, registry, transition queue, and scheduler decisions.
- [x] Remove or retarget source-contract assertions that became fragile after extraction where behavior tests cover the same invariant.

## Phase 3: Unify Upload Scheduling

**Goal:** remove duplicate progressive-upload machinery and make all GPU texture uploads pass through one budgeted scheduler.

- [x] Introduce a `TextureUploadScheduler` runtime service that owns:
  - [x] `TextureUploadWorkItem` queueing
  - [x] priority ordering
  - [x] duplicate coalescing
  - [x] storage-generation capture and cancellation
  - [x] queue wait telemetry
  - [x] execution time telemetry
  - [x] per-frame byte and time budgeting
  - [x] urgent visible repair lane
- [x] Route runtime-managed `XRTexture2D` progressive uploads through the scheduler.
- [x] Route `GLTexture2D.Upload.cs` progressive/chunked uploads through the same scheduler.
- [x] Keep OpenGL upload primitives on `GLTexture2D`, but remove scheduler policy from the GL texture object.
- [x] Make sparse shared-context promotions expose upload progress through the same telemetry model.
- [x] Ensure render-target initialization and video/PBO paths either use the scheduler or explicitly document why they are outside imported texture streaming.
- [x] Validate that partially uploaded mips/pages remain hidden from sampling until complete.

## Phase 4: Make Cooked Sources Metadata-First

**Goal:** remove slow warm-cache parse work by making streaming cache reads directly addressable.

- [x] Define a compact texture streaming source manifest that can be read without hydrating resident mip blobs:
  - [x] source width and height
  - [x] logical mip count
  - [x] pixel format and sized internal format
  - [x] per-mip offsets and lengths
  - [x] preview mip index
  - [x] source version or freshness token
  - [ ] color space
  - [ ] texture role where available
- [x] Make `IsTextureStreamingAssetUsable` read only the header/manifest.
- [x] Make `AssetTextureStreamingSource.LoadResidentData` read only the selected mip range.
- [x] Avoid full resident mip hydration in the asset-usability hot path.
- [x] Keep backward compatibility with existing cache variants only long enough to regenerate stale/non-streamable cache files.
- [x] Bump the cooked texture cache version so existing entries are invalidated cleanly, and document the bump in the cache header and in `docs/work/design/`.
- [x] Add a focused manifest test that fails if usability regresses into resident mip hydration.
- [ ] Update cache logs so `cacheReadMs` and `cacheParseMs` clearly separate file I/O, manifest parse, and mip blob copy.

## Phase 5: Centralize Residency State

**Goal:** prevent sparse/dense handoff bugs by making residency mutation explicit and atomic.

- [x] Introduce a `TextureResidencyState` or `TextureResidencyHandle` for mutable runtime residency fields:
  - [x] sparse enabled flag
  - [x] logical dimensions
  - [x] logical mip count
  - [x] resident base mip
  - [x] committed base mip
  - [x] sparse level count
  - [x] committed bytes
  - [x] resident page selection
  - [x] storage generation
- [x] Replace scattered sparse property mutation fields with a central residency-state object and backend transition handoff paths.
- [x] Keep `XRTexture2D` as stable identity, logical description, sampler defaults, and serialization surface.
- [x] Preserve `XRBase` mutation semantics by using `SetField(...)` for any remaining `XRTexture2D` state exposed as properties.
- [x] Make sparse-to-dense and dense-to-sparse transitions call explicit handoff paths.
- [x] Add tests/source-contract coverage for sparse-to-dense and dense-to-sparse handoff state.
- [x] Provide YAML compatibility by keeping the serialized `XRTexture2D` property surface stable while moving backing runtime fields into `TextureResidencyState`.
- [x] Add an embedded `XRTexture2D` YAML round-trip test that asserts `CookedBinary` payload and original-path behavior are preserved after the residency-state extraction.

## Phase 6: OpenGL Backend Cleanup

**Goal:** make OpenGL residency code a backend implementation detail with narrow, validated upload primitives.

- [x] Move OpenGL texture-streaming backend files under a coherent OpenGL folder.
- [x] Keep `GLTexture2D.Storage.cs` focused on storage allocation, storage generation, and validation helpers.
- [x] Keep `GLTexture2D.Upload.cs` focused on validated primitive uploads and row/page chunk upload helpers.
- [x] Keep `GLTexture2D.SparseStreaming*.cs` focused on sparse commit/uncommit, shared-context execution, fence exposure, and sparse sampling ranges.
- [x] Remove scheduler policy decisions from `GLTexture2D`.
- [x] Ensure every `TexSubImage2D` path uses the same validation helper.
- [ ] Audit hot render-thread paths after the split with `Report-NewAllocations` for new allocations, LINQ, captured lambdas, string formatting, and boxing.
- [x] Vulkan-readiness gate: review the `ITextureResidencyBackend` surface and confirm no OpenGL types (GL handles, GLEnums, sparse-page formats) leak above the backend interface; add a stub Vulkan backend or a documented TODO if any leak remains.
- [ ] Delete or archive obsolete partial-class files once their content has migrated; current partials still hold active serialization/import/sparse state surface and are not empty stubs.

## Phase 7: Tests, Tooling, And Docs

**Goal:** make the consolidated system safe to keep changing.

- [x] Replace brittle source-shape tests with behavior tests where practical.
- [x] Add source/cache tests for cache hit, cache miss, fallback, cancellation, and resident-data reuse.
- [x] Add backend/transition tests for sparse/tiered selection and transition coalescing.
- [x] Add scheduler tests for priority, urgent repair, duplicate coalescing, generation cancellation, and budget yielding.
- [x] Add cooked-cache manifest tests for header-only usability and selected-mip reads.
- [ ] Confirm `log_textures.txt` line schema is stable in a fresh editor run, or document any breaking changes for external diagnostics tooling.
- [x] Decide which of the new telemetry counters are permanent vs. debug-only; debug-only detail remains gated behind summary/slow/verbose modes.
- [x] Update the editor texture-diagnostics overlay/panel to read the new service surface where exposed by telemetry.
- [ ] Run `Report-NewAllocations` against the new hot paths (registry snapshot, scheduler submit/execute, policy scoring) and capture the before/after delta in `docs/work/audit/`.
- [x] Update texture-management docs to describe the new service boundaries.
- [x] Update sparse texture streaming docs if backend responsibilities or transition contracts change.
- [x] Update `docs/work/README.md` status links as phases complete.

## Phase 8: Validation

**Goal:** prove the cleanup did not regress startup texture behavior, render-thread safety, or diagnostics.

- [ ] Run targeted builds:
  - [x] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Run targeted tests:
  - [x] Attempted `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "ImportedTextureStreamingPhaseTests|ImportedTextureStreamingContractTests|GLTexture2DContractTests|RuntimeRenderingHostServicesTests" --no-restore`; blocked before test execution by unrelated duplicate `Engine` compile errors in the unit-test project.
- [ ] Run cold-cache Sponza startup and record:
  - [ ] cache misses and writes
  - [ ] raw decode count
  - [ ] first visible preview time
  - [ ] all visible previews resident time
  - [ ] pending visible transition drain time
  - [ ] upload validation failure count
- [ ] Run warm-cache Sponza startup and record:
  - [ ] cache hit count
  - [ ] slow cache parse count
  - [ ] worst `cacheParseMs`
  - [ ] promotion queue wait
  - [ ] render-thread upload chunk timings
- [ ] Confirm `Texture.UploadValidationFailed` remains zero in normal runs.
- [ ] Confirm no `GL_INVALID_VALUE` texture upload errors appear in `log_opengl.txt`.
- [ ] Confirm `Texture.BindingRisk` entries are either expected non-streaming paths or tracked follow-ups.
- [ ] Confirm `Texture.CacheReadSlow` parse times are materially lower after the metadata-first source work.

Current validation after consolidation, 2026-05-04:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore` passes with 0 errors. Remaining warnings are existing NuGet advisories plus unrelated nullability/volatile diagnostics in non-texture paths.
- Targeted `dotnet test` is blocked before test execution by unrelated `CS0433` duplicate `Engine` type errors in existing unit-test files.

## Phase 9: Wrap-Up Housekeeping

**Goal:** close out the texture-management line of work cleanly so future contributors land in one canonical place.

- [x] Mark `texture-management-runtime-todo.md` as superseded/complete with a back-link to this consolidation doc.
- [x] Mark `texture-streaming-cooked-cache-todo.md` as superseded/complete with a back-link to this consolidation doc.
- [ ] Update `/memories/repo/texture-streaming.md` with the final service boundaries (registry, policy, transition queue, upload scheduler, residency backends) and any invariant gotchas discovered during the refactor.
- [x] Update `docs/work/design/texturing/texture-management-runtime-design.md` and `docs/work/design/texturing/sparse-texture-streaming-plan.md` to reflect the post-consolidation architecture, or supersede them with a single "texture streaming v1" design doc.
- [x] Confirm `docs/work/README.md` shows all three texturing TODOs in their final state.
- [ ] Merge the current `texture-management-runtime` branch back into `main` after the TODO is complete and validated.
