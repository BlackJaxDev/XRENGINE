# Model Import Binary Cache

This page describes the finished parts of the model import binary cache: codec ownership, backend selection, cache identity, the binary container, and the optional meshlet section. For the user-facing cache layout and legacy transition, see [Model Import](../../developer-guides/assets/model-import.md#model-cache-identity-and-legacy-transition). The full format proposal is in the [Model Import Binary Cache Design](../../work/design/assets/model-import-binary-cache-design.md). Open work is in the [Model Import Binary Cache TODO](../../work/todo/assets/model-import-binary-cache-todo.md).

## Status

The container, reader, identity, backend registry, and the meshlet section codec and service are in code. Live `XRPrefabSource` hydration and cache publication are not. A valid container reports `CodecUnavailable` after the freshness gate, so imports still run the cold source path.

Code: `XREngine.Runtime.ModelAssetPipeline/Importing/Caching/`, shared contracts in `XREngine.Data/Core/Assets/Caching/`.

## Codec Ownership And Results

- `ModelBinaryCacheCodec` claims `XRPrefabSource` exclusively. A miss, rejection, or write failure never falls through to generic YAML serialization.
- Cache operations return typed results (`Hit`, `Miss`, `Rejected(reason)`, `WriteFailed`) with a `CacheRejectReason`. `AssetManager` allows generic YAML fallback only for unhandled codecs or a cooperative `Miss`.
- Legacy YAML model caches are rejected as `LegacyFormat` and rebuilt from source. They are never hydrated or migrated.

## Backend Registry And Producer Reports

- `ModelImportBackendRegistry` holds immutable `ModelImportBackendDescriptor` snapshots (stable ID, implementation version, extensions, priority, capabilities), ordered by descending priority and then ordinal ID. Built-ins are `xrengine.native-gltf@1`, `xrengine.native-fbx@1`, and `assimp@1`. `SerializedModelImportProducerAdapter` (`XREngine.Editor/Importers/Unity/`) registers `xrengine.unity-prefab` at the editor boundary, so ModelAssetPipeline has no Unity types.
- `ModelImportBackendResolver` normalizes FBX, glTF, and other-format policies, keeps `Auto` as the requested policy, and hashes the ordered candidate IDs and versions with SHA-256. `ModelAssetImporter` runs the candidates in order and exposes the successful producer through `ModelAssetImportResult.BackendSelection`.
- Each producer emits a `ModelImportProducerReport` with structural dependencies (`ModelImportDependency`), stable source entities (`ModelImportSourceEntity`), and reference keys (`ModelImportReferenceKey`). `XRPrefabSource` seeds texture and material remap keys from this report.
- `ImportedEntityKey` is the durable imported-entity identity: a producer key when one exists (`IsStable`), otherwise an explicit hierarchy and ordinal fallback. Values are NFC-normalized.

## Cache Identity

- `ModelImportCanonicalSettings` and `ModelCookCanonicalSettings` serialize output-affecting settings through `ModelCacheCanonicalWriter`: explicit field IDs, little-endian primitives, NFC UTF-8, invariant enum values, canonical zero, deterministic collection order, and finite-float checks.
- Identity includes import options, source and dependency resolution paths, model cook defaults, resolver policy, requested backend policy, the ordered-candidate hash, schema, payload, codec, and chunk policies, and the authored cook-override snapshot. It excludes worker limits, scheduling, progress callbacks, renderer scheduling, and project-authoritative remap values. Build identity is diagnostic only.
- `ModelCookOverrideSnapshotBuilder` reads per-submesh overrides from the generated project prefab without opening the third-party source.
- `ModelCacheVariantFingerprintBuilder` keeps the full SHA-256 digest and uses its first 128 bits as a 32-character lowercase path key.
- `ModelCacheSourceIdentityResolver` applies the versioned Windows case policy, NFC normalization, portable separators, and final-target resolution, with separate Project, Engine, and External origins (`ModelCacheSourceOrigin`).
- `ModelCachePathResolver` builds `Models/v<schema>/policy_p<path-policy>_r<resolver-policy>.../opts_<fingerprint>/...`. External, unsafe, reserved-device, and long paths use a bounded hash-sharded fallback.
- `ModelBinaryCacheVersions`, `ModelBinaryChunkVersions`, and `ModelImportBackendVersions` hold all compatibility versions.

## Binary Container

- Packed little-endian layout: a 308-byte preamble, a string pool, a chunk table of 64-byte entries, and 16-byte-aligned chunk bodies. `ModelBinaryChunkType` defines the chunk type IDs, including a mandatory `Dependencies` manifest.
- `ModelBinaryContainerWriter` writes NFC-normalized, ordinally sorted strings and chunks, so equivalent inputs give identical bytes.
- xxHash3-64 checksums protect the preamble, the string pool, the chunk table, the dependency manifest, and each decoded chunk body.
- `ModelBinaryContainerReader` checks ranges with checked arithmetic, non-overlap, counts, resource ceilings (`ModelCacheReadLimits`), exact layout versions, strict UTF-8, required, optional, and unknown chunk policy, and the reserved uncompressed codec field, before it exposes payload bytes. Checksums are verified before chunk contents are parsed.
- Manifest-only, selected-chunk, and full publication-validation reads share one validation path.
- v1 compression is off. The codec fields are reserved.

## Meshlet Section

The meshlet section is an optional chunk that stores cooked meshlet payloads for model submesh LODs.

- `ModelBinaryMeshletSectionCodec` stores one payload per stable model, submesh, and LOD key (`ModelBinaryMeshletSectionKey`). `CollectReferences` collects one explicit payload reference for every renderable LOD; a missing payload is a cold-publication error, not a request to build during warm load. `CreateChunk`, `Serialize`, `Deserialize`, and `Hydrate` never derive geometry, invoke a source parser, or call the mesh optimizer.
- `ModelBinaryMeshletSectionService.LoadAndPublish` stages the primary model-container entries without touching live meshes, so a later malformed entry cannot leave a partial section attached. A valid primary entry is authoritative. Secondary entries (for example `MeshletPayloadDiskCache`) fill only keys the primary did not provide. A repair callback receives cached core geometry only and never invokes a source parser. Duplicate keys become unresolved. A read-only cache keeps repaired data in memory and skips republication.
- `ModelBinaryOptionalSectionState` reports `Missing`, `Present`, or `Rejected`.
- `ModelBinaryMeshletSectionTelemetry` counts primary, secondary, repaired, and unmatched entries.
- GPU upload data is rebuilt from the cached CPU descriptors. The cache stays CPU-owned.

## Rules For Remaining Work

These rules hold for the pending hydration and publication work:

- Freshness covers the entry source plus structural dependencies (external glTF buffers, Unity prefab dependencies, OBJ and MTL files, sidecars). Texture payload bytes stay with the texture cache.
- LOD and meshlet generation happens after parsing and before publication. A warm load never runs the source parser to rebuild cooked geometry.
- Mesh core, skinning and bind, morph, and meshlet codecs are shared between the standalone `XRMesh` cooked format and the model container. The model container does not wrap a whole `XRMesh` blob.
- Project-owned assets, material remaps, and reference overrides win over cached imported defaults. A normal warm load never writes under `Assets/`.
- Animation data is stored by reference. Embedded textures are published durably by the texture subsystem before a model cache is complete.
- Publication uses unique adjacent temporary files, keyed in-process serialization, cross-process arbitration or a race-safe protocol, post-write validation, and atomic replacement.
