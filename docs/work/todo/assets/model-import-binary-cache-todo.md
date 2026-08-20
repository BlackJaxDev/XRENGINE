# Model Import Binary Cache TODO

Last reconciled: 2026-08-20

Status: Active — Phases 1–3 complete; cooked payloads, hydration, and publication pending

Source design: [Model Import Binary Cache Design](../../design/assets/model-import-binary-cache-design.md)

Related docs:

- [Model import feature guide](../../../developer-guides/assets/model-import.md)
- [Texture Streaming Cooked Cache TODO](../COMPLETED/texture-streaming-cooked-cache-todo.md)
- [Meshlet import cooking and production readiness TODO](../rendering/gpu/meshlet-import-cooking-and-production-readiness-todo.md)
- [GPU meshlet zero-readback rendering design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Production rendering pipeline roadmap](../rendering/gpu/production-rendering-pipeline-roadmap.md)

Runtime implementation progress: all 12 Phase 1 tasks, all 12 Phase 2 tasks, and all 11 Phase 3 tasks complete. Checked Phase 0 items record pre-implementation reconciliation.

Meshlet closeout note (2026-08-20): the standalone `XRMesh` cooked payload and
shared meshlet-section slice now have cold/warm, deterministic identity,
changed-source/settings rejection, malformed-payload rejection, and
runtime-without-cooker evidence in the
[meshlet production closeout](../../investigations/rendering/meshlet-import-production-closeout-2026-08-20.md).
This does not activate or prove the general model/prefab binary-cache warm path.
That path remains blocked on the mesh-core/prefab-graph serializer/provider and
must not count standalone hydrations as model-cache hits.

## Outcome

Add a deterministic, versioned binary cache for imported model structure and cooked mesh data. A valid warm-cache load must avoid the original model parser, preserve project-authored bindings, and hydrate an `XRPrefabSource` that is structurally equivalent to a cold import.

This tracker is the implementation authority. The design document defines the format and behavioral contract; this file defines execution order and release evidence.

## V1 Scope

Required producer paths:

- Native glTF/GLB.
- Native FBX.
- Assimp fallback, including OBJ.
- Unity prefab import.

Required acceptance formats:

- `.gltf`
- `.glb`
- `.fbx`
- `.obj` with `.mtl`
- Unity `.prefab`

Other Assimp-supported extensions use the same backend descriptor and cache contract, but they are not v1 release blockers unless a deterministic fixture is added for them.

## Current Implementation Snapshot

The deterministic model binary container and defensive reader now exist. End-to-end warm hydration and publication do not: current imports still parse the source and intentionally skip publication until cooked semantic payloads land in later phases.

| Capability | Current state | Model-cache implication |
|---|---|---|
| Generic third-party cache routing | Uses shared typed read/write results with explicit cooperative and exclusive ownership | The model registration is exclusive, so model misses and rejections cannot reach generic YAML. |
| Standalone `XRMesh` cooked binary payload | Exists across `XRMesh.CookedBinary.cs`/`XRMesh.CookedMeshlets.cs` and is embedded by `XRMeshYamlTypeConverter.cs` | Extract reusable section codecs rather than nesting or duplicating the monolithic payload. |
| LOD generation and `SubMeshLOD` representation | Exists in `MeshOptimizerIntegration.cs` and `SubMeshLOD.cs` | Move requested generation into cold cooking and persist model-owned LOD tables/meshes. |
| CPU meshlet generation and serialization | Exists | Reuse the current cooked data and freshness contract. |
| GPUScene meshlet registration and cone data | Exists in `GPUScene.GpuMeshletDescriptor` | Cache remains CPU-owned; GPU upload data is rebuilt from cached CPU descriptors. |
| Runtime meshlet disk cache | Exists in `MeshletPayloadDiskCache.cs` | It becomes a repair/non-model secondary cache for model imports. |
| Generic manual reimport transaction | Exists in `AssetManager.ThirdPartyImport.cs` | Extend it to stage and publish a cache candidate while preserving generated asset identity. |
| Model-specific binary container/codec | A deterministic writer and defensive manifest/selective reader exist in `XREngine.Runtime.ModelingBridge/Importing/Caching/`; the exclusive engine codec recognizes the fixed magic and validates the manifest plus entry-source freshness | Live `XRPrefabSource` hydration and cache publication remain deliberately unavailable until cooked model sections exist. |
| Explicit cache rejection reasons and backend registry | Shared `CacheRejectReason`, immutable versioned descriptors, deterministic resolver snapshots, normalized producer reports, and the Unity adapter exist | The v1 preamble and mandatory dependency/manifest chunks persist the completed producer and dependency contracts. |
| Deterministic model-cache identity | Canonical import/cook projections, authored override snapshots, SHA-256-derived variant keys, origin-aware source identity, and bounded paths are integrated into `AssetManager` | The resolved path and compatibility hashes now feed the v1 container preamble without changing lookup identity. |

## Locked Architecture Decisions

The following decisions must remain true unless the design and this tracker are deliberately revised together.

- The model codec exclusively owns cache persistence for `XRPrefabSource`. Once it claims the asset type, a miss, rejection, or write failure must not fall through to generic YAML serialization.
- The shared third-party cache contract returns explicit results such as `Hit`, `Miss`, `Rejected(reason)`, and `WriteFailed`; `bool` is insufficient.
- Legacy YAML model caches are rejected as `LegacyFormat` and rebuilt from source. They are never hydrated as the new binary format.
- Cache identity records both the requested backend policy (including `Auto` and its ordered candidate set) and the actual producer/backend version selected for the cold import.
- Freshness covers the entry source plus structural dependencies. Examples include external glTF buffers, Unity prefab dependencies, OBJ/MTL files, and backend-reported sidecars. Texture payload bytes remain owned by the texture cache.
- Import-time geometry cooking is governed by a versioned `ModelCookSettings` block under `ModelImportOptions`, resolved per submesh with authored overrides from `MeshOptimizerSubMeshSettings`.
- LOD and meshlet generation happens after parsing and before cache publication. A warm cache never has to invoke the source parser to reconstruct cooked geometry.
- Shared mesh core, mesh skinning/bind, morph, and meshlet section codecs are extracted from the existing `XRMesh` cooked serializer. The standalone `XRMesh` format and the model container compose those same codecs; model-owned skeleton hierarchy and submesh-owned LOD tables use separate sections, and the model cache does not wrap a second monolithic `XRMesh` blob.
- For imported models, the model container’s meshlet chunk is primary. `MeshletPayloadDiskCache` is only a repair source or a cache for meshes that do not originate from a valid model container.
- Project-owned assets, material remaps, and imported-reference overrides are authoritative over cached imported defaults. A normal warm load never writes under `Assets/`.
- Animation data is stored by durable reference, not duplicated in the model container. Embedded textures must be durably published by the texture subsystem before a model cache can be marked complete.
- Cache publication uses unique adjacent temporary files, in-process keyed serialization, cross-process arbitration or race-safe publication, a post-write validation pass, and atomic replacement.
- Serialized collections use deterministic ordering and cache-local stable IDs. Only explicitly diagnostic fixed-header fields may vary between equivalent writes.
- Read-only warm loads succeed. If optional derived data is repaired in memory but cannot be republished, loading continues with the repaired payload and emits a warning.

## Primary Code Areas

- `XREngine.Data/Core/Assets/Caching/` — proposed shared cache result and rejection contracts.
- `XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs` — generic cache routing.
- `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs` — reimport transaction.
- `XRENGINE/Core/Engine/ModelCaching/` — proposed AssetManager adapter, hydration, and publication integration.
- `XREngine.Runtime.ModelingBridge/Importing/ModelImporter.cs` — producer selection and import routing.
- `XREngine.Runtime.ModelingBridge/Importing/ModelImportOptions.cs` — import and cook policy.
- `XREngine.Runtime.ModelingBridge/Importing/Caching/` — proposed backend descriptors, dependency reporting, and binary document/container implementation.
- `XRENGINE/Scene/UnityEditorImportBridge.cs` — Unity producer adapter at the upper-layer composition boundary.
- `XRENGINE/Scene/Prefabs/XRPrefabSource.cs` — hydrated imported-model state.
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs` — existing cooked mesh payload to refactor into shared sections.
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedMeshlets.cs` — existing meshlet section encoding.
- `XREngine.Runtime.Rendering/Serialization/XRMeshYamlTypeConverter.cs` — standalone mesh serialization integration.
- `XREngine.Runtime.Rendering/Serialization/Meshes/` — proposed shared mesh section codecs.
- `XREngine.Runtime.Rendering/Rendering/Meshlets/MeshOptimizerSettings.cs` — per-submesh authored overrides.
- `XREngine.Runtime.Rendering/Rendering/Meshlets/MeshOptimizerIntegration.cs` — existing LOD and meshlet cook implementation.
- `XREngine.Runtime.Rendering/Rendering/Models/Meshes/SubMeshLOD.cs` — runtime LOD representation.
- `XREngine.Runtime.Rendering/Objects/Meshes/MeshletPayloadDiskCache.cs` — secondary repair-cache precedence.
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/` — cached meshlet upload path and counters.
- `XREngine.Editor/` — cache status, reason, rebuild, and reimport UX.
- `XREngine.UnitTests/` — deterministic format, safety, integration, and concurrency coverage.

## Phase 0 — Contract and Baseline Lock

Documentation reconciliation is complete; runtime baselines still need to be captured.

- [x] Separate existing reusable foundations from unimplemented model-cache functionality.
- [x] Reconcile current source paths and module dependency direction.
- [x] Lock codec ownership, backend identity, dependency freshness, cook settings, mesh-section reuse, and cache-authority rules.
- [x] Lock race-safe publication and read-only repair behavior.
- [x] Identify deterministic native FBX, external-buffer glTF, embedded GLB, skinned/morph/animated glTF, and Unity prefab fixtures already in the test corpus.
- [ ] Add a deterministic OBJ/MTL cache-integration fixture.
- [ ] Capture cold-import baselines: parser calls, wall time, allocations, node/mesh/material counts, and structural hashes.
- [ ] Capture current second-load behavior so the new binary path can be compared against a known baseline.

Candidate existing fixtures:

- `XREngine.UnitTests/TestData/Fbx/synthetic-static-scene-ascii.fbx`
- `XREngine.UnitTests/TestData/Gltf/external-static-scene.gltf`
- `XREngine.UnitTests/TestData/Gltf/embedded-buffer-view-scene.glb`
- `XREngine.UnitTests/TestData/Gltf/skinned-morph-animated.gltf`
- `XREngine.UnitTests/TestData/UnityAvatarProject/Assets/SyntheticAvatar.prefab`

## Phase 1 — Shared Cache Decision Contract and Backend Registry

- [x] Extract the generic third-party cache codec contract into `XREngine.Data`.
- [x] Replace boolean reads/writes with typed results and `CacheRejectReason`.
- [x] Add exclusive codec ownership so handled types cannot silently fall through to YAML.
- [x] Route exclusive model lookup before the generic cache timestamp/YAML path and keep model validity inside the model codec.
- [x] Add a model codec registration for `XRPrefabSource`.
- [x] Define immutable backend descriptors: stable ID, implementation version, supported extensions, priority, and capability flags.
- [x] Make the ModelingBridge FBX/glTF/Assimp path resolve an ordered candidate list deterministically.
- [x] Register Unity prefab conversion as an `XRENGINE` adapter that emits the same normalized producer/dependency report without moving Unity-specific types into ModelingBridge.
- [x] Record the requested policy, candidate-list hash, actual producer ID, and actual producer version.
- [x] Require producers to report structural dependencies, stable source entity keys, and imported remap/reference keys.
- [x] Remove the independent glTF remap-key pre-parse from `XRPrefabSource`; seed keys from the successful producer report or warm manifest.
- [x] Add focused tests for exclusive ownership, legacy-format rejection, producer selection, and fallback ordering.

Implemented routing evidence:

- `XREngine.Data/Core/Assets/Caching/` owns the shared ownership, status, result, and rejection contracts.
- `ModelBinaryCacheCodec` exclusively claims `XRPrefabSource`, rejects pre-container entries as `LegacyFormat`, and suppresses YAML writes while the binary writer is unavailable.
- `AssetManager` permits generic YAML fallback only for unhandled codecs or cooperative `Miss` results; an exclusive result and every `Rejected` result return to source import.
- `AssetCacheTests` covers model registration, legacy rejection, no generic model-cache publication, and source fallback in the presence of a fresh legacy YAML entry.
- `ModelImportBackendRegistry` owns immutable descriptor snapshots ordered by descending priority and ordinal stable ID. Built-ins are `xrengine.native-gltf@1`, `xrengine.native-fbx@1`, and `assimp@1`.
- Resolver policy v1 normalizes FBX/glTF/other-format policies, preserves `Auto` as the requested policy, and hashes the ordered candidate IDs and implementation versions with SHA-256.
- `ModelImporter` executes the resolver snapshot in order and exposes the successful producer through both `LastBackendSelection` and `ModelImporterResult.BackendSelection`.
- Native glTF, native FBX, and Assimp emit immutable, normalized dependency/entity/reference metadata. Assimp discovers external glTF buffers/images and OBJ material libraries; native FBX uses stable FBX object IDs.
- `UnityModelImportProducerAdapter` registers `xrengine.unity-prefab@1` at the upper boundary and maps `UnityPrefabImportManifest` records without introducing Unity types into ModelingBridge.
- `XRPrefabSource` consumes the successful producer report to seed texture/material remap keys and no longer opens glTF independently for key discovery.

## Phase 2 — Paths, Fingerprints, and Legacy Transition

- [x] Define schema, payload, chunk, codec, cook-policy, and backend-version constants.
- [x] Define canonical serialization for all import and cook settings that affect output.
- [x] Exclude execution-only options and project-authoritative remap values from semantic cache identity.
- [x] Build a deterministic pre-lookup cook-override snapshot from project assets without parsing the model source.
- [x] Compute a 128-bit variant fingerprint from SHA-256 and encode it as 32 lowercase hexadecimal characters.
- [x] Partition cache roots for project Assets, engine-owned content, and external absolute sources.
- [x] Normalize source identity without depending on culture or process-specific path formatting.
- [x] Add a bounded hashed path fallback for long or invalid source-derived cache names.
- [x] Include requested backend policy, candidate-list hash, and cook settings in cache identity; record engine build identity for diagnostics only.
- [x] Detect old YAML model-cache entries and return `LegacyFormat`.
- [x] Document that legacy entries are rebuilt on demand and may be garbage-collected later.
- [x] Add path, fingerprint, locale, case, long-path, and legacy-transition tests.

Implemented identity and transition evidence:

- `ModelBinaryCacheVersions`, `ModelBinaryChunkVersions`, and `ModelImportBackendVersions` centralize every Phase 2 compatibility version, including the Unity adapter version.
- `ModelCacheCanonicalWriter`, `ModelImportCanonicalSettings`, and `ModelCookCanonicalSettings` use explicit field IDs, little-endian primitives, NFC UTF-8, invariant enum values, canonical zero, deterministic collection order, and finite-float validation.
- Import identity includes output-affecting import options, source/dependency resolution paths, model cook defaults, resolver policy, requested backend policy, ordered-candidate hash, schema/payload/codec/chunk policies, and the canonical authored-override snapshot. Execution scheduling, worker limits, progress callbacks, renderer scheduling, and project-authoritative remap values are excluded.
- `ModelCookOverrideSnapshotBuilder` traverses the existing generated project prefab deterministically and records only per-submesh policies that differ from model defaults. It never opens the third-party model source.
- `ModelCacheVariantFingerprintBuilder` retains the full SHA-256 digest and uses its first 128 bits as a 32-character lowercase path key. Assembly build identity is retained only as diagnostics and does not change the hash.
- `ModelCacheSourceIdentityResolver` applies the versioned Windows case policy, NFC normalization, portable separators, final-target classification when available, and separate Project/Engine/External identities.
- `ModelCachePathResolver` emits `Models/v<schema>/policy_p<path-policy>_r<resolver-policy>.../opts_<fingerprint>/...`; external, unsafe, reserved-device, and long source paths use a bounded hash-sharded fallback.
- `AssetManager` resolves the semantic model path before cache lookup, incorporating supplied import options and project-authored cook overrides. Current-location and previous generic-location YAML entries are handed to the exclusive model codec, rejected as `LegacyFormat`, and rebuilt from source without YAML hydration or migration.
- Legacy YAML entries are intentionally left in place after a successful source fallback. A later age-based cache garbage collector may remove entries from the obsolete layout once no supported build reads it; project assets and model sources are never candidates for that cleanup.
- Validation on 2026-07-29: `dotnet build .\XRENGINE\XREngine.csproj --no-restore` and `dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --no-dependencies` succeeded. The focused Phase 1/2 suite passed 25/25 tests across exclusive ownership, legacy transition, resolver ordering, all four producer families, native skin/morph/animation entity reporting, canonical settings, semantic exclusions, AssetManager-supplied settings and project cook overrides, locale/case stability, origin partitions, long/reserved paths, caller-variant containment, and collision resistance. Full `AssetCacheTests` passed 10/10 and full `NativeGltfImporterTests` passed 6/6. Existing Magick.NET advisory warnings remain unrelated.

## Phase 3 — Binary Container and Defensive Reader

- [x] Implement the fixed preamble, format identity, and version checks.
- [x] Implement a deterministic string pool with its own checksum.
- [x] Implement a bounded chunk table with explicit IDs, versions, flags, offsets, lengths, and checksums.
- [x] Add a mandatory `Dependencies` manifest chunk.
- [x] Define chunks for prefab structure, component directory/payloads, mesh core, skinning/skeleton, morph targets, LOD tables, meshlets, materials/references, animation references, and metadata.
- [x] Define required versus optional chunk behavior.
- [x] Validate checked arithmetic, file bounds, chunk-count limits, string limits, element limits, and non-overlapping ranges before allocation.
- [x] Validate checksums before deserializing chunk contents.
- [x] Add manifest-only and selective-chunk reads.
- [x] Keep v1 compression disabled while reserving explicit codec fields.
- [x] Add round-trip, deterministic-byte, truncation, checksum, overlap, absurd-count, and unknown-optional-chunk tests.

Implemented container evidence:

- `XREngine.Runtime.ModelingBridge/Importing/Caching/` owns the immutable v1 container model, deterministic writer, strict reader, and centralized read-limit policy.
- The packed little-endian layout uses a 308-byte preamble, 64-byte chunk entries, 16-byte alignment, and 20 explicit versioned chunk type IDs.
- The writer emits NFC-normalized ordinal string-pool and chunk ordering. xxHash3-64 independently protects the preamble, string pool, chunk table, dependency manifest, and decoded chunk bodies.
- The reader validates checked ranges, non-overlap, counts, hard resource ceilings, exact layout versions, strict UTF-8 strings, required/optional/unknown chunk policy, and the reserved uncompressed codec contract before exposing payload bytes.
- Manifest-only, selected-chunk, and full required-chunk publication-validation entry points share the same defensive validation path.
- The exclusive engine codec distinguishes the fixed binary magic from legacy YAML, validates binary manifests and entry-source length/timestamp/hash gates, then reports `CodecUnavailable` until later phases provide live hydration and publication payloads.
- `ModelBinaryContainerTests` covers fixed layout, round trip, deterministic bytes, selective reads, publication validation, truncation, every checksum layer, overlaps, absurd counts, malformed strings/layouts, required/optional unknown chunks, codec rejection, read-limit ceilings, and codec source gating.
- Validation on 2026-07-29: the ModelingBridge and XRENGINE no-dependency builds succeeded with zero errors. An isolated cache regression project passed 51/51 tests: all 16 Phase 3 container tests plus the Phase 1/2 model-cache, `AssetCacheTests`, and `NativeGltfImporterTests` suites. The full unit-test project is currently blocked by unrelated concurrent Vulkan test work referencing a missing `VkObject<>`; existing Magick.NET advisory warnings also remain unrelated.

## Phase 4 — Import-Time Cooking and Shared Mesh Section Codecs

- [x] Add versioned `ModelCookSettings` to `ModelImportOptions`. (Completed early as a Phase 2 identity prerequisite.)
- [x] Define deterministic defaults for LOD generation, meshlet generation, simplification, and repair policy. (Completed early as a Phase 2 identity prerequisite.)
- [ ] Resolve effective settings per submesh, honoring authored `MeshOptimizerSubMeshSettings` overrides.
- [ ] Extract reusable mesh core, mesh skinning/bind, morph, and meshlet section codecs from `XRMesh.CookedBinary.cs` and `XRMesh.CookedMeshlets.cs`.
- [ ] Add model-owned skeleton-hierarchy and submesh-owned LOD-table codecs alongside the shared mesh sections.
- [ ] Make standalone `XRMesh` serialization compose the shared section codecs without changing its ownership semantics.
- [ ] Make the model container compose the same codecs without nesting a full standalone `XRMesh` payload.
- [ ] Generate requested LODs and meshlets after import and before cache publication.
- [ ] Persist disabled/absent features explicitly so a warm load does not infer or regenerate them.
- [ ] Add section round-trip, compatibility, deterministic cooking, and standalone-mesh regression tests.

## Phase 5 — Meshlets and GPUScene Integration

Existing meshlet foundations are present; this phase integrates them with the model container.

Already available inputs, not phase completion:

- CPU meshlet descriptors and triangle/vertex remap payloads.
- Standalone cooked-mesh serialization with meshlet data and freshness metadata.
- A GPUScene-facing descriptor with bounds and cone-culling data.
- GPUScene registration/upload of precomputed CPU meshlet payloads.

Remaining work:

- [ ] Serialize model-owned meshlet sections and their cook metadata.
- [ ] Define model-container-primary and `MeshletPayloadDiskCache`-secondary precedence.
- [ ] Add a counter/assertion proving valid warm loads do not rebuild meshlets.
- [ ] Integrate optional meshlet repair without invoking the model source parser.
- [ ] Confirm cached CPU descriptors produce the same GPUScene upload payload as cold cooking.
- [ ] Add empty, disabled, multi-LOD, corrupt-optional, and warm-upload integration tests.

## Phase 6 — Prefab, Materials, and Referenced Subassets

- [ ] Introduce a cache-local `CookedModelDocument` independent of `AssetManager` and editor assemblies.
- [ ] Add an upper-layer `IImportedComponentCacheCodec` registry with stable keys, versions, required/optional policy, and bounded canonical payloads.
- [ ] Keep standard model components reference-based so component payloads do not duplicate model/submesh/material objects.
- [ ] Map supported Unity component records at the `XRENGINE` boundary without adding Unity-specific dependencies to ModelingBridge.
- [ ] Hydrate `XRPrefabSource` through normal mutation paths, using `SetField(...)` where `XRBase` state changes.
- [ ] Rebuild prefab hierarchy and component relationships from deterministic cache-local IDs.
- [ ] Restore mesh, material, skin, morph, and animation references without source parsing.
- [ ] Keep project-authored material assets and remaps authoritative over cached imported defaults.
- [ ] Ensure a warm load never rewrites generated project assets.
- [ ] Store animation references only; do not duplicate animation payload bytes.
- [ ] Require embedded texture publication to complete durably before publishing the model cache.
- [ ] Validate required animation/texture outputs through their owning subsystem for each requested hydration group.
- [ ] Reject only the affected requested group when a required referenced output cannot be resolved durably.
- [ ] Add cold-versus-warm structural equality and project-binding precedence tests.

## Phase 7 — Partial Hydration, Repair, and Hot-Path Behavior

- [ ] Read only the preamble, validated string pool, dependency manifest, and chunk table before touching heavy bodies.
- [ ] Support metadata-only, structure-only, and selected-mesh hydration.
- [ ] Use pooled buffers, spans, and bounded reads for hot binary paths.
- [ ] Coalesce nearby chunk reads without weakening validation.
- [ ] Repair optional LOD/meshlet sections from cached core geometry when policy permits.
- [ ] Keep repaired data in memory when a read-only cache cannot be republished.
- [ ] Fall back to source import only for required-data rejection or an explicitly non-repairable condition.
- [ ] Emit parser, cache-hit, bytes-read, chunks-read, repair, and cook counters.
- [ ] Verify no model parser or meshlet rebuild runs on a valid full warm load.

## Phase 8 — Atomic Publication, Concurrency, and Corruption Recovery

- [ ] Write to a unique adjacent path such as `<cache>.tmp.<pid>.<nonce>`.
- [ ] Flush data and metadata as supported, close the writer, reopen the candidate, and validate it with the production reader.
- [ ] Serialize same-key work in-process with a keyed semaphore.
- [ ] Add cross-process arbitration with a named mutex/file lock, or prove an equivalent race-safe unique-temp publication protocol.
- [ ] Recheck cache validity after acquiring publication ownership so duplicate producers can discard redundant work.
- [ ] Atomically replace an existing entry or move a new entry into place.
- [ ] Clean only validated orphan temp files matching the cache key and age policy.
- [ ] Quarantine or remove corrupt entries on a best-effort basis without making source import fail.
- [ ] Add same-process, cross-process, interrupted-write, read-during-replace, read-only, and corrupt-entry tests.

## Phase 9 — Manual Reimport, Identity, UX, and Documentation

- [ ] Define `ImportedEntityKey`: backend stable source ID when available, otherwise normalized node path plus entity kind and deterministic slot/ordinal.
- [ ] Stage a complete model-cache candidate during manual reimport.
- [ ] Match staged entities to existing generated assets by stable imported identity.
- [ ] Preserve project GUIDs, remaps, and authoritative bindings for matched entities.
- [ ] Preview additions, removals, remaps, and identity breaks before commit.
- [ ] Commit generated Assets and the cache candidate as one recoverable reimport transaction.
- [ ] Ensure cancellation or failure preserves the previous generated assets and previous valid cache.
- [ ] Show cache state, backend producer, rejection reason, dependency status, schema/settings versions, and repair state in the editor.
- [ ] Add rebuild, remove, inspect, and reimport/reconcile actions.
- [ ] Update user-facing import and cache documentation.

## Phase 10 — Validation and Closeout

- [ ] Run cold/warm integration tests for FBX, external glTF, embedded GLB, skinned/morph/animated glTF, OBJ/MTL, and Unity prefab.
- [ ] Prove valid warm loads invoke no source model parser.
- [ ] Prove valid warm loads invoke no LOD or meshlet builder when those chunks are present.
- [ ] Prove dependency edits invalidate the correct cache even when the entry source is unchanged.
- [ ] Prove cache bytes contain no duplicated animation or texture payloads.
- [ ] Prove equivalent imports produce byte-identical semantic sections.
- [ ] Run corruption, truncation, resource-limit, read-only, and concurrency suites.
- [ ] Benchmark cold import, full warm hydration, and partial hydration.
- [ ] Run the narrowest relevant solution/project builds and targeted unit tests.
- [ ] Update the design, user docs, and dependency documentation if implementation changed their contracts.
- [ ] Move this tracker to `docs/work/todo/COMPLETED/` only after all release criteria have evidence.

## Validation Matrix

| Area | Required evidence |
|---|---|
| Codec routing | Exclusive model ownership; no YAML fallback; typed miss/reject/write outcomes. |
| Identity | Stable path and fingerprint across processes, locales, backend fallback, and equivalent settings. |
| Freshness | Entry source plus external buffers, MTL/sidecars, Unity dependencies, and backend-reported structural inputs. |
| Format safety | Bounds, overlap, count, checksum, truncation, unknown-chunk, and version rejection tests. |
| Semantic parity | Cold and warm prefab structure, transforms, meshes, materials, skinning, morphs, and references match. |
| Payload ownership | No animation or texture payload duplication; project assets remain authoritative. |
| Meshlets/LODs | Present/disabled/repair cases; no valid-warm-load rebuild; equivalent GPU upload data. |
| Publication | Unique temp files, atomic replacement, same-process and cross-process races, crash leftovers, read-only operation. |
| Reimport | Stable generated GUIDs/bindings when identity matches; failure preserves the last valid state. |
| Performance | Parser-call counters at zero on hit; measured wall time, allocations, bytes, and chunk counts. |

## Release Criteria

- A valid cache hit hydrates the requested model state without opening the source through any model parser.
- Cache validity includes the actual backend producer, producer version, ordered resolver policy, all output-affecting settings, and structural dependencies.
- The container is deterministic, defensively parsed, forward-compatible at chunk boundaries, and safe against untrusted counts and offsets.
- Warm loads preserve project-authored material/remap authority and never write project assets.
- Imported LOD and meshlet data is generated before publication, restored without rebuilding, and uploaded through the existing GPUScene path.
- Animation and texture payload ownership remains with their dedicated systems.
- Publication is atomic and safe under same-process and cross-process races.
- Manual reimport preserves stable project identity where source identity still matches and cannot destroy the last valid state on failure.
- Read-only warm loading works, including in-memory optional-section repair.
- Targeted tests and benchmark evidence are recorded before the tracker is closed.
