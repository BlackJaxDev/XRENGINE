# Texture Streaming Consolidation TODO

Status: proposed phased TODO

Source context:
- [Texture Management Runtime TODO](texture-management-runtime-todo.md)
- [Texture Streaming Cooked Cache TODO](texture-streaming-cooked-cache-todo.md)
- [Texture Management Runtime Design](../design/texture-management-runtime-design.md)
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
- `docs/work/design/texture-management-runtime-design.md`
- `docs/work/design/sparse-texture-streaming-plan.md`

## Phase 0: Branch And Baseline

**Goal:** isolate the consolidation and preserve evidence for behavior and performance comparisons.

- [ ] Create a dedicated branch for this TODO, for example `texture-streaming-consolidation`.
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
- [ ] Identify the targeted validation set before refactoring:
  - [ ] `ImportedTextureStreamingPhaseTests`
  - [ ] `ImportedTextureStreamingContractTests`
  - [ ] `GLTexture2DContractTests`
  - [ ] `RuntimeRenderingHostServicesTests`
  - [ ] warm-cache editor Sponza run
  - [ ] cold-cache editor Sponza run

## Phase 1: Split Files Without Behavior Changes

**Goal:** make ownership visible before changing architecture.

- [ ] Extract streaming source types from `ImportedTextureStreamingManager.cs`:
  - [ ] `ITextureStreamingSource`
  - [ ] `TextureStreamingSourceFactory`
  - [ ] `AssetTextureStreamingSource`
  - [ ] `ThirdPartyTextureStreamingSource`
  - [ ] `TextureStreamingResidentDataReuseCache`
- [ ] Extract concurrency helpers that are not texture-policy specific:
  - [ ] `PriorityAsyncSemaphore`
- [ ] Extract neutral residency contracts and telemetry-facing records:
  - [ ] `TextureStreamingResidentData`
  - [ ] `ImportedTextureStreamingUsage`
  - [ ] `ImportedTextureStreamingTelemetry`
  - [ ] `ImportedTextureStreamingTextureTelemetry`
  - [ ] `ITextureResidencyBackend`
- [ ] Move `GLTieredTextureResidencyBackend` out of the manager into an OpenGL texture-streaming backend file.
- [ ] Move `GLSparseTextureResidencyBackend` out of the manager into an OpenGL texture-streaming backend file.
- [ ] Keep the existing `XRTexture2D` static facade methods intact during the split.
- [ ] Run formatting and targeted build after the pure move phase.

## Phase 2: Extract Policy And Registry

**Goal:** make residency decisions deterministic and testable without renderer or disk dependencies.

- [ ] Create a `TextureResidencyPolicy` service or static module for pure decisions:
  - [ ] desired resident size
  - [ ] page selection
  - [ ] sampler-role multipliers
  - [ ] priority score
  - [ ] promotion and demotion cooldown decisions
  - [ ] pressure demotion order
  - [ ] fairness group key
- [ ] Create a `TextureStreamingRegistry` for tracked texture records, weak references, usage recording, material binding recording, and snapshot collection.
- [ ] Create a `TextureTransitionQueue` for pending transition state, coalescing, cancellation, stale-transition repair, and lifecycle timing.
- [ ] Define service ownership and lifetime: `RuntimeRenderingHostServices` constructs and disposes the policy, registry, transition queue, and upload scheduler in a documented order relative to the GL context.
- [ ] Document the thread-safety contract for each new service (which members are main-thread, render-thread, worker-thread, or free-threaded) at the type level.
- [ ] Keep `ImportedTextureStreamingManager` as the frame-level coordinator:
  - [ ] collect snapshots
  - [ ] ask policy for desired residency
  - [ ] enqueue transition intents
  - [ ] publish telemetry
  - [ ] dump summaries
- [ ] Add behavior tests for policy decisions using fake records and fake backends.
- [ ] Remove source-contract assertions that become fragile after the extraction where behavior tests cover the same invariant.

## Phase 3: Unify Upload Scheduling

**Goal:** remove duplicate progressive-upload machinery and make all GPU texture uploads pass through one budgeted scheduler.

- [ ] Introduce a `TextureUploadScheduler` runtime service that owns:
  - [ ] `TextureUploadWorkItem` queueing
  - [ ] priority ordering
  - [ ] duplicate coalescing
  - [ ] storage-generation capture and cancellation
  - [ ] queue wait telemetry
  - [ ] execution time telemetry
  - [ ] per-frame byte and time budgeting
  - [ ] urgent visible repair lane
- [ ] Route runtime-managed `XRTexture2D` progressive uploads through the scheduler.
- [ ] Route `GLTexture2D.Upload.cs` progressive/chunked uploads through the same scheduler.
- [ ] Keep OpenGL upload primitives on `GLTexture2D`, but remove scheduler policy from the GL texture object.
- [ ] Make sparse shared-context promotions expose upload progress through the same telemetry model.
- [ ] Ensure render-target initialization and video/PBO paths either use the scheduler or explicitly document why they are outside imported texture streaming.
- [ ] Validate that partially uploaded mips/pages remain hidden from sampling until complete.

## Phase 4: Make Cooked Sources Metadata-First

**Goal:** remove slow warm-cache parse work by making streaming cache reads directly addressable.

- [ ] Define a compact texture streaming source manifest that can be read without hydrating a full `XRTexture2D`:
  - [ ] source width and height
  - [ ] logical mip count
  - [ ] pixel format and sized internal format
  - [ ] per-mip offsets and lengths
  - [ ] preview mip index
  - [ ] source version or freshness token
  - [ ] color space
  - [ ] texture role where available
- [ ] Make `IsTextureStreamingAssetUsable` read only the header/manifest.
- [ ] Make `AssetTextureStreamingSource.LoadResidentData` read only the selected mip range.
- [ ] Avoid UTF-8 YAML conversion and full asset deserialization in the hot streaming path.
- [ ] Keep backward compatibility with existing cache variants only long enough to regenerate stale/non-streamable cache files.
- [ ] Bump the cooked texture cache version so existing entries are invalidated cleanly, and document the bump in the cache header and in `docs/work/design/`.
- [ ] Add a cache parse benchmark or focused test that fails if warm-cache resident reads regress into full payload hydration.
- [ ] Update cache logs so `cacheReadMs` and `cacheParseMs` clearly separate file I/O, manifest parse, and mip blob copy.

## Phase 5: Centralize Residency State

**Goal:** prevent sparse/dense handoff bugs by making residency mutation explicit and atomic.

- [ ] Introduce a `TextureResidencyState` or `TextureResidencyHandle` for mutable runtime residency fields:
  - [ ] sparse enabled flag
  - [ ] logical dimensions
  - [ ] logical mip count
  - [ ] resident base mip
  - [ ] committed base mip
  - [ ] sparse level count
  - [ ] committed bytes
  - [ ] resident page selection
  - [ ] storage generation
- [ ] Replace scattered sparse property mutation paths with backend-owned transition application.
- [ ] Keep `XRTexture2D` as stable identity, logical description, sampler defaults, and serialization surface.
- [ ] Preserve `XRBase` mutation semantics by using `SetField(...)` for any remaining `XRTexture2D` state exposed as properties.
- [ ] Make sparse-to-dense and dense-to-sparse transitions call one explicit handoff path.
- [ ] Add tests that force sparse-to-dense and dense-to-sparse transitions without stale state surviving.
- [ ] Provide YAML migration for any `XRTexture2D` fields that move into `TextureResidencyState` so previously serialized assets still load (test with at least one embedded font-atlas asset and one cooked scene texture).
- [ ] Add an embedded `XRTexture2D` YAML round-trip test (font-atlas case) that asserts `CookedBinary` payload and original-path are preserved after the residency-state extraction.

## Phase 6: OpenGL Backend Cleanup

**Goal:** make OpenGL residency code a backend implementation detail with narrow, validated upload primitives.

- [ ] Move OpenGL texture-streaming backend files under a coherent OpenGL folder.
- [ ] Keep `GLTexture2D.Storage.cs` focused on storage allocation, storage generation, and validation helpers.
- [ ] Keep `GLTexture2D.Upload.cs` focused on validated primitive uploads and row/page chunk upload helpers.
- [ ] Keep `GLTexture2D.SparseStreaming*.cs` focused on sparse commit/uncommit, shared-context execution, fence exposure, and sparse sampling ranges.
- [ ] Remove policy decisions from `GLTexture2D`.
- [ ] Ensure every `TexSubImage2D` path uses the same validation helper.
- [ ] Audit hot render-thread paths after the split for new allocations, LINQ, captured lambdas, string formatting, and boxing.
- [ ] Vulkan-readiness gate: review the `ITextureResidencyBackend` surface and confirm no OpenGL types (GL handles, GLEnums, sparse-page formats) leak above the backend interface; add a stub Vulkan backend or a documented TODO if any leak remains.
- [ ] Delete or archive obsolete partial-class files (e.g., `XRTexture2D.SparseStreaming.cs`, `XRTexture2D.StreamingPayload.cs`, `XRTexture2D.ImportedStreaming.cs`) once their content has migrated; do not leave empty stub partials behind.

## Phase 7: Tests, Tooling, And Docs

**Goal:** make the consolidated system safe to keep changing.

- [ ] Replace brittle source-shape tests with behavior tests where practical.
- [ ] Add fake-source tests for cache hit, cache miss, fallback, cancellation, and resident-data reuse.
- [ ] Add fake-backend tests for sparse/tiered selection and transition coalescing.
- [ ] Add scheduler tests for priority, urgent repair, duplicate coalescing, generation cancellation, and budget yielding.
- [ ] Add cooked-cache manifest tests for header-only usability and selected-mip reads.
- [ ] Confirm `log_textures.txt` line schema is stable, or document any breaking changes for external diagnostics tooling.
- [ ] Decide which of the new telemetry counters are permanent vs. debug-only (gate the debug-only counters behind summary/slow/verbose modes) before merge.
- [ ] Update the editor texture-diagnostics overlay/panel to read the new service surface, or add an explicit "deferred" follow-up note linking back to this TODO.
- [ ] Run `Report-NewAllocations` against the new hot paths (registry snapshot, scheduler submit/execute, policy scoring) and capture the before/after delta in `docs/work/audit/`.
- [ ] Update texture-management docs to describe the new service boundaries.
- [ ] Update sparse texture streaming docs if backend responsibilities or transition contracts change.
- [ ] Update `docs/work/README.md` status links as phases complete.

## Phase 8: Validation

**Goal:** prove the cleanup did not regress startup texture behavior, render-thread safety, or diagnostics.

- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Run targeted tests:
  - [ ] `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "ImportedTextureStreamingPhaseTests|ImportedTextureStreamingContractTests|GLTexture2DContractTests|RuntimeRenderingHostServicesTests" --no-restore`
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

## Phase 9: Wrap-Up Housekeeping

**Goal:** close out the texture-management line of work cleanly so future contributors land in one canonical place.

- [ ] Mark `texture-management-runtime-todo.md` as superseded/complete with a back-link to this consolidation doc.
- [ ] Mark `texture-streaming-cooked-cache-todo.md` as superseded/complete with a back-link to this consolidation doc.
- [ ] Update `/memories/repo/texture-streaming.md` with the final service boundaries (registry, policy, transition queue, upload scheduler, residency backends) and any invariant gotchas discovered during the refactor.
- [ ] Update `docs/work/design/texture-management-runtime-design.md` and `docs/work/design/sparse-texture-streaming-plan.md` to reflect the post-consolidation architecture, or supersede them with a single "texture streaming v1" design doc.
- [ ] Confirm `docs/work/README.md` shows all three texturing TODOs in their final state.
- [ ] Merge `texture-streaming-consolidation` back into `main` after the TODO is complete and validated.
