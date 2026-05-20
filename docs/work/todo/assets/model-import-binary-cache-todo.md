# Model Import Cooked Asset Cache TODO

Status: active

Source design: [Model Import Cooked Asset Cache Design](../../design/assets/model-import-binary-cache-design.md)

Related docs:

- [Model import feature guide](../../../features/model-import.md)
- [Texture streaming cooked cache TODO](../texturing/texture-streaming-cooked-cache-todo.md)
- [GPU meshlet zero-readback rendering design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Production rendering pipeline roadmap](../rendering/gpu/production-rendering-pipeline-roadmap.md)

## Goal

Make third-party model imports use a disposable cooked engine-native `.asset` cache after the first source import. Fresh caches should avoid reparsing `.fbx`, `.gltf`, `.glb`, `.obj`, and similar source files, and should avoid regenerating mesh data, LODs, and meshlets during warm loads.

The cache is a runtime/import acceleration artifact, not a replacement for user-owned source files or editable generated project assets. Like textures, model caches should be cooked `.asset` files under `Cache/`, not a separate model-specific extension.

## Non-Negotiable Rules

- [ ] Create a dedicated branch before implementing the TODO, for example `model-import-binary-cache`.
- [ ] Keep project `.asset` files under `Assets/` authoritative for user-editable generated assets; cooked `.asset` cache files under `Cache/` only replace the third-party source importer on fresh warm loads.
- [ ] Never write animation clip data into the model cache asset. Animation cache ownership remains separate.
- [ ] Never write texture payload bytes into the model cache or texture cache root from the model importer; embedded source textures must flow through the texture importer API.
- [ ] Treat `engineVersion` as diagnostic only. It must not invalidate caches.
- [ ] Reject incompatible caches by `schemaVersion`, `payloadVersion`, per-chunk versions, or explicit freshness inputs.
- [ ] Produce deterministic output for identical source, settings, and backend versions, modulo the small timestamp/UUID header region documented by the implementation.
- [ ] Use `SetField(...)` when hydrating or mutating any `XRBase`-derived type.
- [ ] Keep cache reads, GPUScene registration, and render-adjacent hydration allocation-conscious: no LINQ, captured closures, boxing, string concatenation, or avoidable heap churn in hot paths.
- [ ] Add or upgrade dependencies only after approval and the dependency/license regeneration workflow.
- [ ] New code must compile without new warnings.

## Success Criteria

- [ ] Fresh model caches are preferred over original third-party source paths during normal loads.
- [ ] Manual reimport forces source parse and atomically replaces cache output.
- [ ] Manual reimport preserves GUIDs for matched entities and reports identity breaks for unmatched entities before committing.
- [ ] Cache freshness accounts for source length/timestamp/hash policy, import options, importer backend, LOD settings, meshlet settings, material policy, `schemaVersion`, and `payloadVersion`.
- [ ] Warm-cache loads reconstruct hierarchy, meshes, LODs, meshlets, morph targets, materials, texture references, and skeletons without opening the original model file.
- [ ] Stale, incompatible, partial, or unreadable caches fall back to source import with a single clear `CacheRejectReason`.
- [ ] Stale `LodTables` and `Meshlets` chunks can be repaired in-process from valid mesh chunks without parsing the source model.
- [ ] Cache writes are atomic on Windows with same-directory temp files, validation before swap, and orphan temp cleanup.
- [ ] Cache reads succeed on read-only filesystems; failed repair writes do not abort the load.
- [ ] Identical source plus settings plus backend version produces byte-identical cache files, aside from documented header-only variable fields.

## Primary Code Areas

- `XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs`
- `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs`
- `XRENGINE/Core/ModelImporter.cs`
- `XRENGINE/Scene/Prefabs/XRPrefabSource.cs`
- `XREngine.Runtime.Rendering/Rendering/Models/Meshes/ModelImportOptions.cs`
- `XREngine.Runtime.Rendering/Rendering/Models/Meshes/SubMeshLOD.cs`
- `XREngine.Runtime.Rendering/Rendering/Meshlets/Meshlet.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs`
- `XREngine.UnitTests/`
- `docs/features/model-import.md`

## Phase 0: Branch, Baseline, And Scope Lock

**Goal:** isolate the work and make the current importer/cache behavior measurable before cooked cache changes start.

- [ ] Create a dedicated branch, for example `model-import-binary-cache`.
- [ ] Confirm the v1 source-format scope and note any formats that will stay source-import-only at first.
- [ ] Capture current cold import and warm load behavior for representative FBX and glTF/GLB assets.
- [ ] Record current cache path behavior for game assets, engine assets, external paths, and import-option freshness.
- [ ] Identify the smallest committed or generated test fixtures needed for source-contract tests.
- [ ] Decide where cooked cache reader/writer code lives so `AssetManager` does not become the format implementation.
- [ ] Document the implementation-owned variable fields that are allowed to differ in deterministic-output tests.

### Exit Criteria

- [ ] Branch exists.
- [ ] Baseline behavior and test fixture plan are recorded.
- [ ] Reader/writer module boundary is agreed before binary format code lands.

## Phase 1: Importer Backend Versioning

**Goal:** make cache identity depend on the importer implementation that produced the payload.

- [ ] Add stable monotonic `BackendVersion` constants for each third-party model importer path: Assimp, native glTF/GLB, FBX, Unity prefab, and any other active source backend.
- [ ] Expose active importer name and backend version through a registry queried by source extension and import options.
- [ ] Fold meshoptimizer native-lib version into the relevant backend/settings freshness inputs.
- [ ] Add tests proving backend name and version changes invalidate the model cache.
- [ ] Add owner guidance near each backend version constant explaining when to bump it.

### Exit Criteria

- [ ] Cache lookup can ask which importer backend owns a source model before parsing the source.
- [ ] Backend version changes are covered by source-contract tests.

## Phase 2: Cache Contract, Path, And Logging

**Goal:** make model cache identity and rejection explainable before payload hydration begins.

- [ ] Add `schemaVersion`, `payloadVersion`, and per-chunk version constants with documented bump rules.
- [ ] Keep model cache path resolution on the existing cooked `.asset` cache convention.
- [ ] Add a model cache variant key such as `Models/v<schemaVersion>/importer_<backend-key>/opts_<8hex>/...`.
- [ ] Keep settings detail in the header; do not encode individual settings directly into path segments.
- [ ] Implement long-path handling and hashed fallback for cache paths that would exceed Windows path limits.
- [ ] Add `CacheRejectReason` and model cache log event constants.
- [ ] Add source-contract tests for game, engine, external, long-path, and variant-key cache resolution.
- [ ] Update `docs/features/model-import.md` for cache path shape and long-path fallback behavior.

### Exit Criteria

- [ ] Model source paths resolve to deterministic cooked `.asset` cache paths under `Cache/`.
- [ ] Cache miss/stale/incompatible decisions can emit one reason enum value.
- [ ] Docs describe user-visible cache path behavior.

## Phase 3: Binary Manifest And Freshness Gate

**Goal:** write and read manifest-only cooked model cache assets with early rejection from the preamble.

- [ ] Define the fixed preamble with little-endian scalar reads, aligned offsets, freshness fields, and whole-file checksum.
- [ ] Define the length-prefixed UTF-8 string pool.
- [ ] Define the chunk table with type, version, offset, sizes, checksum, compression kind, and flags.
- [ ] Implement manifest-only cache writes after successful source import.
- [ ] Implement preamble-only rejection for schema, payload, source length, source timestamp, source hash, importer backend, import options, meshoptimizer settings, and material policy.
- [ ] Implement source hashing policy for unreliable timestamps, explicit hash opt-in, and backwards-moving timestamps.
- [ ] Add manual reimport routing that skips cache reads and forces source import.
- [ ] Add tests for fresh manifest hit, stale source fallback, version rejection, import-option invalidation, backwards timestamp hashing, and manual reimport skip.

### Exit Criteria

- [ ] The engine can create and reject manifest-only cooked model `.asset` files without reading chunk payloads.
- [ ] Manual reimport bypasses cache reads.

## Phase 4: Mesh, Morph, Skeleton, And LOD Payloads

**Goal:** make warm cache reads reconstruct model geometry and LOD chains without opening source files.

- [ ] Serialize engine-native mesh payload identity and metadata.
- [ ] Serialize vertex streams for positions, normals, tangents, UVs, colors, bone influences, and any engine-required attributes.
- [ ] Serialize index streams, primitive topology, bounds, and source diagnostic identity.
- [ ] Serialize morph target deltas in a dedicated `MorphTargets` chunk.
- [ ] Serialize skeleton bind pose, joint hierarchy, and inverse bind matrices in a dedicated `Skeletons` chunk.
- [ ] Serialize `SubMeshLOD` chains with LOD 0 referencing the source mesh payload and LOD 1+ using distinct mesh payloads.
- [ ] Include LOD settings hash, source mesh hash, simplification backend/version, authored LOD identity, and distance policy in freshness.
- [ ] Load cached `XRMesh` and `SubMeshLOD` data without source model access.
- [ ] Add tests for mesh streams, morph target round trip, skeleton round trip, LOD reconstruction, and generated LOD determinism.

### Exit Criteria

- [ ] Static and skinned mesh payloads hydrate from cache.
- [ ] LOD chains hydrate from cache and do not duplicate LOD 0 mesh payload bytes.

## Phase 5: Meshlet Payloads And GPUScene Integration

**Goal:** move meshlet generation out of warm render startup and provide cone-culling data to GPUScene.

2026-05-19 dependency note: the meshlet production tracker now provides an `XRMesh`-level `MeshletPayload` contract with CPU descriptors, cone data, freshness hashes, disabled-generation manifests, and runtime cooked-binary round trip. This phase still owns the disposable model-cache chunk/container integration, GPUScene registration handoff, GPU `Meshlet` layout/shader coordination, and model-cache counter coverage.

- [ ] Add a serialized CPU meshlet descriptor distinct from the GPU `Meshlet` struct.
- [ ] Extend the GPU `Meshlet` layout with cone axis, cutoff, and apex or a documented compressed equivalent.
- [ ] Coordinate the GPU buffer layout change with shaders, indirect-buffer writers, pools, allocators, and payload versioning.
- [ ] Serialize meshlet descriptors, vertex-reference indices, triangle-local indices, bounds, cones, settings, and meshoptimizer stats.
- [ ] Represent `MeshletGenerationSettings.Enabled == false` as empty chunks plus an explicit manifest flag.
- [ ] Load meshlet payloads into GPUScene registration data without calling `MeshletGenerator.Build` during warm-cache startup.
- [ ] Add counter-based tests proving warm-cache render startup does not rebuild meshlets.

### Exit Criteria

- [ ] Warm-cache render startup consumes cached meshlets.
- [ ] GPUScene has meshlet cone data available for task-shader cone culling.

## Phase 6: Prefab, Material, Texture Reference, And Sub-Asset Reconstruction

**Goal:** rebuild the imported prefab graph and generated asset references from binary chunks while preserving user-editable project assets.

- [ ] Serialize and hydrate `Manifest`, `PrefabGraph`, `Models`, `SubMeshes`, `Materials`, `TextureReferences`, and `ColliderHints`.
- [ ] Reconstruct `XRPrefabSource`, `Model`, `SubMesh`, `XRMesh`, `XRMaterial`, skeleton references, and generated asset references via `SetField(...)`.
- [ ] Preserve externalized project asset paths and references.
- [ ] Keep material and texture remap dictionaries seeded without flapping import-option timestamps.
- [ ] Ensure animation references point to animation-cache outputs or generated animation assets without storing clip data in the model cache.
- [ ] Add structural-equality tests comparing a cached prefab with a fresh source import, modulo documented timestamp/UUID fields.
- [ ] Add tests preserving user material and texture remap assignments.

### Exit Criteria

- [ ] Prefab reconstruction from cache is structurally equivalent to source import.
- [ ] User-authored generated assets and remaps remain authoritative.

## Phase 7: Partial Hydration, Repair, And Performance

**Goal:** avoid unnecessary IO and recover repairable stale chunks without reparsing source models.

- [ ] Read preamble, string pool, and chunk table before any payload hydration.
- [ ] Hydrate hierarchy/manifest data separately from heavy mesh and meshlet data.
- [ ] Read chunk bodies into pooled buffers and expose them through `Span<T>` / `ReadOnlySpan<T>` parsers.
- [ ] Add lazy or grouped prefetch for mesh, morph, skeleton, LOD, and meshlet payloads.
- [ ] Implement per-chunk repair policy for stale or missing `LodTables` and `Meshlets`.
- [ ] Treat required chunk failures as full cache misses with source fallback.
- [ ] Treat missing or stale `ColliderHints` as optional with a warning.
- [ ] Add telemetry for cache read/write time, slow reads/writes, bytes read/written, mesh count, LOD count, and meshlet count.
- [ ] Benchmark cold import, warm cache load, manual reimport, stale LOD repair, and stale meshlet repair.
- [ ] Validate warm-cache model load avoids third-party parser DLLs for cached formats.

### Exit Criteria

- [ ] Warm cache reads hydrate only the data needed by the caller.
- [ ] Repairable LOD and meshlet chunks regenerate from cached meshes without source parse.
- [ ] Performance logs show cold vs warm import benefit and allocation profile.

## Phase 8: Atomic Writes, Concurrency, And Corruption Hardening

**Goal:** make the cache disposable but reliable under crashes, concurrent imports, and bad files.

- [ ] Write temp files adjacent to final cache files as `<cache>.tmp`.
- [ ] Flush temp writes to disk before replacement.
- [ ] Reopen and validate temp files before atomic swap.
- [ ] Use `File.Replace` when an existing cache exists and `File.Move` with overwrite when no prior cache exists.
- [ ] Serialize in-process writers with a per-cache-path mutex.
- [ ] Tolerate cross-process races with last-valid-writer-wins semantics.
- [ ] Sweep orphan model-cache `*.asset.tmp` files older than the grace period during AssetManager startup.
- [ ] Add corruption tests for truncated files, bad preamble bits, bad chunk bits, bad string pool offsets, checksum mismatch, and cross-version rejection.
- [ ] Add interrupted-write tests proving valid prior caches remain usable.
- [ ] Add tests for read-only filesystem reads and failed repair writes.
- [ ] Add tests for symlinked paths, Windows case differences, FAT timestamp granularity, network share skew, and import-option invalidation.

### Exit Criteria

- [ ] Atomic write and orphan recovery behavior is covered by tests.
- [ ] Corrupt or partial caches never silently hydrate invalid engine objects.

## Phase 9: Editor UX, Reconcile, And Docs

**Goal:** expose cache state clearly and give users safe reimport/reconcile controls.

- [ ] Show cache status: hit, miss, stale, incompatible, unreadable.
- [ ] Show source timestamp, cache timestamp, truncated source hash, schema version, payload version, importer backend, LOD summary, and meshlet summary.
- [ ] Add "Reimport From Source".
- [ ] Add "Delete Cache And Reimport".
- [ ] Add "Open Generated Asset".
- [ ] Add "Reveal Cache File".
- [ ] Add "Reconcile Cache" for manual orphan cleanup.
- [ ] Implement reconcile over `Cache/` and `Cache/Engine/`, skipping `Cache/External/` unless explicitly requested.
- [ ] Prompt before deleting orphan or obsolete cache entries and write a summary log.
- [ ] Update user-facing model import docs for cooked cache behavior, manual reimport, cache status, and reconcile.

### Exit Criteria

- [ ] Artists can tell whether they are looking at source-owned content, generated project assets, or disposable cache output.
- [ ] Manual orphan cleanup is available and documented.

## Phase 10: Validation And Closeout

**Goal:** prove the feature is correct, deterministic, and faster before merging back.

- [ ] Run targeted source-contract and unit tests for cache path resolution, freshness, rejection, manual reimport, mesh payloads, LODs, meshlets, morph targets, skeletons, remaps, and no-animation/no-texture-payload guarantees.
- [ ] Run resilience tests for corruption, orphan temp cleanup, concurrent writers, read-only filesystems, symlinks, Windows case differences, timestamp granularity, and cross-volume temp rejection.
- [ ] Run integration tests for cold and warm FBX import.
- [ ] Run integration tests for cold and warm glTF/GLB import.
- [ ] Verify stale meshlet chunks repair without source parse.
- [ ] Verify stale LOD chunks repair without source parse.
- [ ] Verify forced reimport preserves generated asset graph on failure.
- [ ] Verify deterministic cache output across two clean imports.
- [ ] Benchmark large static model warm-cache load time, B1 startup with fresh cache versus no cache, cache read allocations, cache write time, and meshlet generation time moved out of render startup.
- [ ] Run the narrowest useful build or test command if full test execution is blocked.
- [ ] Report any unrelated build/test failures instead of hiding them in this tracker.
- [ ] Merge the dedicated branch back into `main` after the TODO is complete and validated.

## Suggested Test Names

- [ ] `ModelCache_VariantKey_IncludesImporterMeshletAndLodSettings`
- [ ] `ModelCache_FreshCache_LoadsInsteadOfSourceImport`
- [ ] `ModelCache_EngineVersionChange_DoesNotInvalidateCache`
- [ ] `ModelCache_SchemaVersionChange_InvalidatesCache`
- [ ] `ModelCache_PayloadVersionChange_InvalidatesCache`
- [ ] `ModelCache_SourceNewer_FallsBackToSource`
- [ ] `ModelCache_SourceTimestampBackwards_TriggersContentHash`
- [ ] `ModelCache_ImportOptionsNewer_FallsBackToSource`
- [ ] `ModelCache_ManualReimport_SkipsCacheReadAndReplacesCache`
- [ ] `ModelCache_ManualReimport_PreservesGuidsForMatchingEntities`
- [ ] `ModelCache_ManualReimport_LogsIdentityBreakForRenamedEntities`
- [ ] `ModelCache_UnreadableCache_DeletesOrQuarantinesAndImportsSource`
- [ ] `ModelCache_WholeFileChecksumMismatch_RejectsCache`
- [ ] `ModelCache_ChunkChecksumMismatch_TriggersPerChunkRepairOrFallback`
- [ ] `ModelCache_MeshPayload_ReconstructsXRMeshStreams`
- [ ] `ModelCache_LodPayload_ReconstructsSubMeshLods`
- [ ] `ModelCache_MeshletPayload_ReconstructsDescriptorsAndConeData`
- [ ] `ModelCache_MorphTargets_RoundTripDeltas`
- [ ] `ModelCache_Skeletons_RoundTripBindPose`
- [ ] `ModelCache_TextureRemaps_PreserveUserAssignments`
- [ ] `ModelCache_MaterialRemaps_PreserveUserAssignments`
- [ ] `ModelCache_DoesNotWriteAnimationData`
- [ ] `ModelCache_DoesNotWriteIntoTextureCacheRoot`
- [ ] `ModelCache_DeterministicOutput_TwoCleanImportsByteIdentical`
- [ ] `ModelCache_ReadOnlyFilesystem_WarmReadSucceeds`
- [ ] `ModelCache_ConcurrentWriters_SerializeViaMutex`
