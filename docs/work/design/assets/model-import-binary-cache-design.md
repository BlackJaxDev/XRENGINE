# Model Import Binary Cache Design

Last reconciled: 2026-07-29

Status: Implementation in progress — producer contracts, deterministic identity/path resolution, and the defensive binary container are complete; cooked payloads, hydration, and publication are pending

Scope: Engine-native cooked `.asset` caches for third-party model imports, including imported hierarchy, cooked mesh sections, LODs, and meshlets.

Related docs:

- [Implementation tracker](../../todo/assets/model-import-binary-cache-todo.md)
- [Model import feature guide](../../../developer-guides/assets/model-import.md)
- [Texture management runtime design](../texturing/texture-management-runtime-design.md)
- [Texture streaming cooked cache TODO](../../todo/COMPLETED/texture-streaming-cooked-cache-todo.md)
- [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Production rendering pipeline roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## 1. Summary

The first load of a third-party model parses the source, normalizes it into engine-owned data, performs requested geometry cooking, and publishes a deterministic binary cache. A compatible warm load hydrates the model from that cache without invoking the source-format parser or rebuilding valid LOD and meshlet data.

The cache contains:

- imported prefab hierarchy and component relationships;
- deterministic imported-entity identities;
- engine-native mesh core, skinning, skeleton, and morph sections;
- imported or generated LOD tables and their mesh payloads;
- CPU meshlet descriptors, index streams, bounds, and cone data;
- imported material defaults and references to project-owned assets;
- references to animation and texture outputs owned by their respective subsystems;
- source, dependency, backend, import-policy, and cook-policy metadata.

The cache does not replace the source file or project-authored `.asset` files. It is generated, disposable runtime/import acceleration data under `Cache/`.

## 2. Goals

- Avoid source-parser work on valid warm loads.
- Avoid LOD and meshlet regeneration when compatible cooked sections exist.
- Hydrate `XRPrefabSource` and `XRMesh` through engine-native binary sections.
- Preserve project-authored materials, remaps, GUIDs, and other editable bindings.
- Make every cache decision deterministic and explainable with one rejection reason.
- Include the actual producer, resolver policy, structural dependencies, and all output-affecting settings in compatibility checks.
- Permit bounded partial hydration and optional-section repair without opening the model source.
- Publish atomically under same-process and cross-process races.
- Treat malformed cache files as untrusted input and reject them before unsafe allocation.
- Produce byte-identical semantic output for identical inputs and versions.

## 3. Non-Goals

- Replacing YAML for project-authored assets.
- Making the cache the sole copy of user-authored state.
- Storing animation clip payloads or texture image payloads in the model cache.
- Inventing new simplification or meshlet algorithms.
- Silently treating a corrupt required section as valid.
- Requiring every Assimp-supported extension to have a dedicated v1 acceptance fixture.
- Performing cache writes on render or per-frame hot paths.
- Preserving legacy model-cache YAML as a readable model-cache format.

## 4. Current Baseline

The repository now has the model-specific deterministic container, defensive reader, and manifest/source compatibility gate. It does not yet have cooked semantic section payloads, live `XRPrefabSource` hydration, or cache publication.

| Existing capability | Location | Reuse or required change |
|---|---|---|
| Third-party cache path and load routing | `XRENGINE/Core/Engine/Loading/AssetManager.Loading.SerializationAndCache.cs` | Typed decisions and exclusive ownership are implemented; binary model hydration and publication remain. |
| Generated asset reimport transaction | `XRENGINE/Core/Engine/AssetManager.ThirdPartyImport.cs` | Extend to stage and publish the binary cache while preserving imported identity. |
| Model backend routing | `XREngine.Runtime.ModelingBridge/Importing/ModelImporter.cs` and `Importing/Caching/` | Stable descriptors, deterministic resolver snapshots, candidate hashing, actual-producer reporting, dependencies, entity keys, and imported reference keys are implemented. |
| Import options | `XREngine.Runtime.ModelingBridge/Importing/ModelImportOptions.cs` | Versioned model cook policy and canonical semantic projection are implemented; Phase 4 still resolves and executes effective per-submesh cooking. |
| Model cache identity/path | `XREngine.Runtime.ModelingBridge/Importing/Caching/` and `XRENGINE/Core/Engine/ModelCaching/` | Versioned canonical settings, authored override snapshots, SHA-256 variants, source-origin identity, bounded paths, and legacy-location probing are implemented. |
| Model binary container | `XREngine.Runtime.ModelingBridge/Importing/Caching/` | Deterministic v1 writer and defensive manifest/selective reader are implemented; semantic cooked chunks, hydration, and publication remain. |
| Standalone cooked `XRMesh` payload | `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs` and `XRMesh.CookedMeshlets.cs` | Extract reusable section codecs; do not duplicate its monolithic payload inside the model container. |
| YAML `XRMesh` bridge | `XREngine.Runtime.Rendering/Serialization/XRMeshYamlTypeConverter.cs` | Continue composing the standalone mesh format from the shared sections. |
| Per-submesh LOD/meshlet overrides | `XREngine.Runtime.Rendering/Rendering/Meshlets/MeshOptimizerSettings.cs` | Use as authored overrides when resolving import-time cook settings. |
| LOD generation and `SubMeshLOD` runtime representation | `XREngine.Runtime.Rendering/Rendering/Meshlets/MeshOptimizerIntegration.cs` and `XREngine.Runtime.Rendering/Rendering/Models/Meshes/SubMeshLOD.cs` | Run deterministically during cold cooking and persist model-owned LOD tables/meshes. |
| Runtime meshlet disk cache | `XREngine.Runtime.Rendering/Objects/Meshes/MeshletPayloadDiskCache.cs` | Retain as a non-model or repair cache; it is not primary for a model-cache hit. |
| GPU meshlet registration | `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/` | Feed it cached CPU descriptors through the existing upload path. |

The generic codec contract now uses typed read/write results. Animation and texture codecs retain cooperative fallback for legacy representations, while the registered model codec is exclusive: a model cache miss or rejection triggers source import and cannot deserialize or serialize a complete prefab graph through generic YAML.

## 5. Architecture and Module Boundaries

The implementation is split by dependency direction:

```text
XREngine.Data
    generic cache decision/result contracts
            |
            v
XREngine.Runtime.Rendering
    shared mesh section codecs and cook payloads
            |
            v
XREngine.Runtime.ModelingBridge
    backend descriptors, dependency reports,
    CookedModelDocument, binary reader/writer
            |
            v
XRENGINE
    AssetManager adapter, XRPrefabSource hydration,
    cache publication and reimport transaction
            |
            v
XREngine.Editor
    status, inspection, rebuild, and reconcile UI
```

Ownership:

- `XREngine.Data/Core/Assets/Caching/`
  - cache disposition/result types;
  - `CacheRejectReason`;
  - the generic exclusive-codec contract.
- `XREngine.Runtime.Rendering/Serialization/Meshes/`
  - shared mesh core, skinning/bind, morph, and meshlet section codecs;
  - no AssetManager or model-container policy.
- `XREngine.Runtime.ModelingBridge/Importing/Caching/`
  - backend descriptors and resolver snapshots;
  - structural dependency records;
  - `CookedModelDocument`;
  - format constants, chunk codecs, reader, and writer.
- `XRENGINE/Core/Engine/ModelCaching/`
  - AssetManager registration;
  - source/cached hydration orchestration;
  - imported component codec registry and Unity adapter;
  - project-binding resolution;
  - publication, repair, and manual reimport integration.

The binary reader/writer must not depend on `AssetManager`, editor types, or live GPU resources. Its input and output are immutable engine-facing documents and bounded byte sections.

### 5.1 Exclusive Cache Codec Contract

A codec first declares how it participates in a requested asset type. Ownership and cache outcome are separate:

```text
ownership: NotHandled | Cooperative | Exclusive
read:      Hit(document) | Miss | Rejected(reason)
write:     Written | Skipped(reason) | Failed(reason, exception)
```

Required behavior:

- `NotHandled` permits the generic asset pipeline to continue.
- `Cooperative` permits generic cache fallback only after `Miss`; `Rejected` still returns to source import.
- `Exclusive` prevents generic YAML cache read or write fallback.
- Exclusive codec lookup runs before the generic cache-file timestamp/YAML path; the model codec owns header/dependency freshness and does not delegate validity to `IsCacheAssetFresh`.
- `Miss` and `Rejected` return control to source import.
- An ordinary cold source load can still succeed when cache publication fails; the failure is visible in diagnostics. Explicit manual reimport uses the stronger transaction in section 15.
- A legacy YAML cache at a model-cache path returns `Rejected(LegacyFormat)`.

This contract is shared because animation, texture, and future binary cache codecs need the same unambiguous routing semantics.

### 5.2 Producer Contract

Each model producer exposes an immutable descriptor:

- stable backend ID;
- monotonic implementation version;
- supported extensions and capabilities;
- deterministic priority;
- whether it can supply stable source entity IDs;
- dependency-discovery behavior.

Each completed cold import returns:

- the actual descriptor used;
- a normalized `CookedModelDocument`;
- all structural dependencies consulted;
- stable source entity IDs where the format provides them;
- imported material, texture, animation, and other remap/reference keys;
- importer diagnostics.

The requested policy and actual producer are different values. For example, `Auto` is the requested policy; `NativeGltf` or `Assimp` is the actual producer.

The implemented ModelingBridge registry uses stable built-in identities `xrengine.native-gltf@1`, `xrengine.native-fbx@1`, and `assimp@1`; the upper adapter registers `xrengine.unity-prefab@1`. Resolver policy v1 snapshots eligible descriptors in descending priority and then ordinal stable-ID order, computes a SHA-256 hash over the ordered IDs and versions, and preserves requested policy separately. `ModelImporter` follows that snapshot in order and returns the successful descriptor plus normalized dependencies, source-entity keys, and imported reference keys through `ModelImporterResult`.

Native glTF, native FBX, and Assimp implementations live behind the ModelingBridge contract. Unity prefab conversion remains an `XRENGINE` adapter because it depends on the editor/Unity bridge; the composition root registers that adapter and maps its import manifest into the same normalized dependency and entity-key records. Unity-specific runtime types must not be pushed into ModelingBridge.

Key discovery comes from the successful producer report. `XRPrefabSource` must not independently pre-parse glTF merely to seed remap dictionaries; that duplicates cold-parser work and would undermine the cache boundary. The upper adapter seeds missing keys from either the cold producer report or the warm cache manifest.

## 6. Authority and Payload Ownership

Authority is resolved in this order:

1. Project-authored assets and explicit remaps.
2. Compatible imported defaults/references from the model cache.
3. Defaults reconstructed by a forced cold import.

The cache accelerates only the source-import computation. Loading a normal project `.asset` never consults the model cache.

| Data | Durable owner |
|---|---|
| Original FBX/glTF/GLB/OBJ/prefab | User/source control |
| Editable materials, meshes, remaps, generated GUIDs | Project `Assets/` |
| Imported hierarchy and cooked model geometry | Model cache |
| Animation clip payload | Animation asset/cache subsystem |
| Texture image payload and streaming data | Texture asset/cache subsystem |
| GPU buffers | Runtime GPUScene/resource ownership |

The model cache may store animation and texture references, source keys, sampler/material interpretation, and imported defaults. It must not store animation clip bytes or image payload bytes.

### 6.1 Normal Warm Load

1. Resolve source type, import options, cook settings, and requested backend policy.
2. Resolve the deterministic candidate list and variant fingerprint.
3. Locate the cache.
4. Validate the fixed entry-source gate.
5. Read and validate the dependency manifest and chunk directory.
6. Hydrate requested sections.
7. Apply project-authored bindings/remaps over cached imported defaults.
8. Register cached meshlet data with GPUScene when needed.

A successful warm load writes neither the model source nor project `Assets/`.

### 6.2 Cold Import

1. Resolve backend candidates.
2. Invoke candidates in deterministic order until one succeeds.
3. Record the actual producer and every structural dependency used.
4. Normalize the result into `CookedModelDocument`.
5. Resolve effective cook settings per submesh.
6. Generate requested LOD and meshlet payloads.
7. Durably publish referenced animation/texture outputs through their owning subsystems.
8. Publish project generated assets when the workflow calls for externalization.
9. Build, validate, and atomically publish the model-cache candidate.

Failure to publish the cache must not turn a successful in-memory source import into a failed asset load. It is a visible cache-write failure.

## 7. Cache Identity and Freshness

### 7.1 Resolver and Producer Identity

The lookup identity includes:

- requested backend policy;
- ordered candidate backend IDs and versions;
- resolver-policy version;
- source extension/category;
- canonical import options;
- canonical effective cook policy;
- schema and payload versions;
- other output-affecting policy versions.

The header additionally records the actual producer ID and version. A warm load rejects the cache when:

- the candidate-list/resolver hash changed;
- the recorded producer is no longer an eligible candidate;
- the producer version changed;
- an output-affecting settings hash changed.

This handles `Auto` correctly. The path does not guess which fallback producer would win before a cold import; it is keyed by the requested policy and deterministic candidate snapshot, while the file records the producer that actually succeeded.

Candidate eligibility includes deterministic environment/capability inputs that can change which producer is usable. The fixed preamble compares the producer-key hash; after the string pool is validated, the reader also compares the complete stable producer ID.

### 7.2 Variant Fingerprint

Canonical settings are serialized with:

- explicit field order and field IDs;
- little-endian numeric values;
- invariant UTF-8 strings;
- normalized enum values;
- deterministic map/set ordering;
- no runtime hash codes or culture-sensitive formatting.

SHA-256 is computed over the canonical bytes. The cache path uses the first 128 bits, encoded as 32 lowercase hexadecimal characters. The complete 128-bit canonical hashes required for compatibility remain in the header/manifest.

The canonical import projection includes only values that change imported semantic output. Execution-only values such as progress callbacks, worker parallelism, and asynchronous scheduling are excluded and must not affect output. Project-authoritative material/texture remap values are also excluded because they are applied after hydration; their durable asset IDs/paths remain in the project binding layer. Unity project-root selection and any other option that changes source/dependency resolution are included.

The model cache does not use the import-options file timestamp as semantic freshness. It hashes the canonical projection. Re-saving identical options or replaying already-seeded remap keys therefore cannot invalidate the model cache.

Phase 2 implements this projection with explicit field IDs, little-endian primitive encoding, NFC UTF-8, finite-float validation, and deterministic collection order. The fingerprint includes schema, payload, container, chunk, hashing, source-identity, cache-path, import-projection, cook-policy, deterministic-ordering, and backend/resolver versions. It retains the full SHA-256 digest for diagnostics and uses the first 128 bits for the path. Engine assembly build identity is recorded separately and deliberately excluded from the semantic bytes.

Before lookup, the upper `AssetManager` adapter reads the existing generated project prefab, when one exists, and builds a sorted `ModelCookOverrideSnapshot` from authored per-submesh settings that differ from model defaults. This read does not open the third-party source. If the authoritative project prefab cannot be read, cache lookup is disabled for that import rather than selecting a potentially incorrect variant.

### 7.3 Source and Dependency Freshness

Freshness has two stages.

Stage 1 is a fixed preamble gate for the entry source:

- source length;
- source last-write UTC;
- optional source content hash and hash mode;
- schema/payload compatibility;
- variant and resolver hashes;
- actual producer compatibility.

Stage 2 validates the mandatory `Dependencies` chunk before reading heavy model sections.

Each dependency record contains:

- normalized identity;
- relationship kind;
- required/optional flag;
- length and last-write UTC at import;
- optional content hash and hash mode;
- producer-specific stable dependency key, when available.

Dependency relationship kinds distinguish structural source inputs from referenced outputs owned by another subsystem. Structural inputs participate in model-cache freshness. Referenced-output records identify the owner, imported key, durable binding hint, required hydration group, and owner payload/version contract.

Structural dependencies include at least:

- external glTF buffers;
- OBJ material libraries and structural sidecars;
- Unity prefab dependencies that affect the imported graph or geometry;
- backend-reported files that affect model, material interpretation, or hierarchy.

Referenced texture files can be recorded for handoff and diagnostics, but their pixel-payload freshness remains owned by the texture subsystem. A texture-only payload change should not force model geometry parsing when the texture subsystem can update independently.

A missing or changed required structural dependency rejects the model cache. A missing optional dependency follows the producer-defined compatibility rule recorded in the manifest.

Before hydrating a group that needs an animation or texture output, the upper-layer adapter asks that owning subsystem to resolve and validate the referenced output. If it cannot satisfy a required output from durable project/cache state, the model cache is rejected for that requested group and source import may republish it. Geometry-only partial hydration does not require unrelated texture or animation outputs.

### 7.4 Hashing Policy

The cheap freshness tuple is `(length, last-write-utc)`. A streamed content hash is recorded and checked when:

- the source/dependency is on a timestamp-unreliable path;
- import options require content hashing;
- timestamps move backwards;
- the filesystem’s timestamp granularity cannot reliably distinguish the observed edit;
- a producer marks the dependency content-critical.

The hash algorithm and version are explicit fields. Changing the hashing policy version invalidates the relevant identity rather than silently reinterpreting old metadata.

Every rejected lookup reports exactly one primary `CacheRejectReason`; diagnostics may include additional contributing details.

## 8. Cache Paths

Model caches use the existing generated cache root and `.asset` convention:

```text
<cache-root>/
    Models/
        v<schema-version>/
            policy_<resolver-key>/
                opts_<32-hex-variant>/
                    <source-relative-or-hashed-path>/
                        <source-name>.asset
                        <source-name>.asset.tmp.<pid>.<nonce>
```

Path rules:

- Project sources, engine-owned sources, and external absolute sources remain in distinct origin partitions.
- Project and engine sources use stable root-relative identities.
- External sources use a canonical absolute identity hashed into the cache tree.
- Canonicalization uses `Path.GetFullPath`, normalized `/` separators, Unicode normalization form C, and the platform resolver's versioned case policy; it never depends on the process current directory or current culture.
- Existing symlink/junction sources are classified by their resolved final target. If final-target resolution is unavailable, the resolver records that fallback in its versioned policy hash.
- Source-derived names are sanitized only for display; identity comes from canonical bytes.
- A deterministic hashed fallback collapses long source-relative paths.
- Long-path handling uses the repository’s Windows-first filesystem conventions.
- Individual import settings never become free-form path segments.

Temporary paths are unique and adjacent to the final file. A fixed `<cache>.tmp` name is forbidden because independent processes would collide before atomic replacement.

Legacy YAML entries in a matching model-cache location are rejected as `LegacyFormat` and rebuilt on demand. They are not migrated by deserializing the old cached prefab.

The transition probes both the current deterministic model-cache location and the previous generic third-party-cache location. An entry found in either layout is classified by the exclusive model codec, never passed to the generic YAML deserializer, and source import proceeds. The obsolete file is retained so fallback cannot turn a successful load into a destructive cache operation; a later age-based garbage collector may remove old-layout entries after the compatibility retention window.

## 9. Binary Container

The model codec owns the file bytes directly. The `.asset` suffix is the repository's generated-cache convention; the file is not wrapped in generic YAML or a second serialized `XRAsset` envelope. The fixed magic distinguishes it from legacy entries.

Phase 3 implements this container in `XREngine.Runtime.ModelingBridge/Importing/Caching/`, including deterministic writes, manifest-only and selective reads, full required-chunk publication validation, and bounded malformed-input rejection. The engine-facing codec validates the manifest and entry-source freshness but intentionally returns `CodecUnavailable` after that gate until later phases can hydrate a live prefab; publication is likewise deferred until cooked semantic chunks exist.

The file is little-endian and contains:

1. a fixed-size preamble;
2. a length-prefixed UTF-8 string pool;
3. a fixed-size chunk table;
4. aligned chunk bodies.

All offsets are absolute from the beginning of the file. Every multiplication and addition used for ranges is checked before allocation or seeking.

Region and chunk offsets are absolute `u64` values. String references are `u32` byte offsets relative to `stringPoolOffset`; zero means null. The pool begins with a reserved zero entry, stores `u32` byte length plus strict UTF-8 bytes, rejects invalid UTF-8/NUL data, deduplicates by ordinal value, and is emitted in deterministic ordinal order.

### 9.1 Fixed Preamble

The v1 preamble contains only fixed-size fields:

```text
magic                       : u8[16] = "XRE_MODEL_CACHE\0"
schemaVersion               : u32
payloadVersion              : u32
preambleSize                : u32
flags                       : u32
fileSize                    : u64
headerChecksum              : u64
stringPoolOffset            : u64
stringPoolLength            : u64
chunkTableOffset            : u64
chunkTableLength            : u64
chunkTableChecksum          : u64
stringPoolChecksum          : u64
chunkCount                  : u32
chunkEntrySize              : u32
entrySourceLength           : u64
entrySourceLastWriteUtc     : i64   // UTC .NET ticks
entrySourceHash             : u64
entrySourceHashMode         : u32
assetType                   : u32
requestedPolicyHash         : u8[16]
backendResolutionHash       : u8[16]
actualBackendKeyHash        : u8[16] // stable descriptor key
actualBackendVersion        : u32
actualBackendName           : u32   // diagnostic string-pool offset
variantFingerprint          : u8[16]
importOptionsHash           : u8[16]
modelCookSettingsHash       : u8[16]
dependencyManifestHash      : u8[16]
dependencyCount             : u32
materialPolicyVersion       : u32
sourceIdentity              : u32   // string-pool offset
engineBuildIdentity         : u64   // deterministic diagnostic build key
reserved                    : u8[32]
```

`engineBuildIdentity` is diagnostic and does not invalidate a cache. Every engine behavior change that affects output must instead bump a format, codec, producer, resolver, or policy version.

The implemented v1 preamble is exactly 308 packed bytes and each chunk entry is exactly 64 bytes. The header checksum field starts at byte 40 and is zeroed while hashing the preamble. These offsets and the checksum-zeroing rule are constants verified by layout tests. Readers require `preambleSize` and `chunkEntrySize` to match the supported schema. Header, table, string-pool, dependency-manifest, and chunk checksums use an explicitly versioned xxHash3-64 contract; identity fingerprints use truncated SHA-256.

All serialized IDs, enums, flags, and type keys are explicit format constants. They are never implicit CLR enum ordinals, assembly-qualified names, or runtime type hashes.

### 9.2 Chunk Entry

Each entry contains:

```text
type              : u32
version           : u32
flags             : u32
codec             : u32
instanceId         : u64
offset            : u64
storedLength      : u64
decodedLength     : u64
decodedChecksum   : u64
elementCount      : u64
```

Rules:

- v1 writes `codec = None`; compression is reserved for a future measured change.
- The checksum covers decoded canonical bytes.
- Unknown required chunks reject the cache.
- Unknown optional chunks are skipped after their ranges are validated.
- The chunk key is `(type, instanceId)`. Singleton chunks use instance ID zero; mesh/submesh-scoped chunks use their deterministic cache-local ID.
- Duplicate chunk keys reject the cache.
- Entries are emitted in type-then-instance order.
- Chunk ranges may not overlap the preamble, string pool, table, or another chunk.
- Zero-length chunks are permitted only where the chunk contract explicitly allows them.

### 9.3 V1 Chunks

| Chunk | Requirement |
|---|---|
| `Dependencies` | Always required and validated before heavy chunks. |
| `Manifest` | Required; feature flags, counts, stable IDs, and section presence. |
| `PrefabGraph` | Required. |
| `ComponentDirectory` | Required when components exist; stable codec keys, versions, flags, owners, and payload instance IDs. |
| `ComponentPayloads` | One bounded instance per declared imported component payload. |
| `Models` | Required when the manifest declares model nodes. |
| `SubMeshes` | Required when renderable submeshes exist. |
| `MeshDirectory` | Required when meshes exist; maps cache-local IDs to mesh sections. |
| `MeshCoreStreams` | One instance per declared mesh payload. |
| `Skinning` | One instance per declared skinned mesh. |
| `Skeletons` | One instance per declared skeleton. |
| `MorphTargets` | One instance per declared morph-bearing mesh. |
| `LodTables` | One instance per applicable submesh; otherwise manifest records disabled/absent. |
| `Meshlets` | One instance per applicable mesh/LOD payload; otherwise manifest records disabled/absent. |
| `Materials` | Required for declared imported material defaults. |
| `TextureReferences` | Required for declared texture references; contains no image bytes. |
| `AnimationReferences` | Required for declared animation outputs; contains no clip bytes. |
| `ImportedEntityTable` | Required; stable reimport keys and project-binding hints. |
| `ColliderHints` | Optional. |
| `Diagnostics` | Optional and never required for hydration. |

There is no animation-payload or texture-payload chunk.

### 9.4 Defensive Read Limits

All limits live in one immutable `ModelCacheReadLimits` policy with conservative defaults and hard ceilings. Before allocating, the reader validates:

- maximum cache file size;
- string-pool size and individual string length;
- chunk count and table length;
- per-chunk stored/decoded sizes;
- aggregate decoded-byte budget;
- node, model, submesh, mesh, vertex, index, bone, morph, LOD, and meshlet counts;
- conversion to CLR collection/index types.

Limit violations return `ResourceLimitExceeded`. Invalid or overlapping ranges return their specific rejection reasons. The implementation must never use attacker-controlled counts in unchecked arithmetic.

Checksums are hierarchical to preserve partial hydration:

- `headerChecksum` validates the fixed preamble;
- `chunkTableChecksum` validates the directory;
- `stringPoolChecksum` validates all referenced strings before they are decoded;
- each selected chunk validates its decoded bytes.

A publication validation pass opens and validates every required chunk. A normal partial read need not stream unrelated chunk bodies merely to compute a whole-file checksum.

## 10. Cooked Model and Shared Mesh Sections

`CookedModelDocument` is the normalized bridge between source producers and persistence. It uses cache-local integer IDs and immutable ordered collections. It contains no `AssetManager`, editor, `FileStream`, or GPU handles.

### 10.1 Shared Mesh Codecs

The current standalone `XRMesh` cooked payload already serializes core streams, indices, skinning/bones, bind data, morph targets, and meshlets across `XRMesh.CookedBinary.cs` and `XRMesh.CookedMeshlets.cs`. The implementation must extract those responsibilities into focused section codecs.

```text
XRMesh standalone binary
    standalone header
    shared mesh core section
    shared mesh skinning/bind section
    shared morph section
    shared meshlet section

Model binary container
    model header/table
    same shared mesh sections
    model-owned skeleton hierarchy section
    submesh-owned LOD table section
```

The model container must not serialize a complete standalone `XRMesh` blob and then add separate morph/skeleton/meshlet chunks around it. That would duplicate bytes and create two version authorities for the same data.

Each shared mesh section has its own explicit codec version. The standalone mesh payload version and model chunk versions declare which shared codec versions they compose. Skeleton hierarchy and LOD-table codecs remain model/submesh concerns because they are not owned by a standalone `XRMesh`.

### 10.2 Model Cook Settings

`ModelImportOptions` gains a versioned `ModelCookSettings` block covering every import-time geometry transformation whose output enters the cache:

- whether LOD generation is enabled;
- LOD count/reduction/error/distance policy;
- whether meshlet generation is enabled;
- meshlet limits and generation policy;
- optimizer/simplifier implementation versions;
- validation and optional-repair policy;
- deterministic ordering/version policy.

The effective policy for a submesh is:

1. explicit authored per-submesh override from `MeshOptimizerSubMeshSettings`;
2. model-level `ModelCookSettings`;
3. versioned engine default.

The canonical effective policy, not only the model-level object, contributes to output identity. Equivalent resolved settings must hash identically.

Before cache lookup, the upper-layer adapter builds a `ModelCookOverrideSnapshot` from authoritative generated project assets keyed by `ImportedEntityKey`. Its canonical hash contributes to the variant fingerprint, so changing an authored per-submesh cook override selects/rebuilds the right variant without parsing the source. On first import the snapshot is empty; on reimport matched entities inherit their existing overrides and unmatched entities use model defaults.

### 10.3 LOD Payload

- LOD 0 references the imported source mesh payload and is not duplicated.
- LOD 1+ references distinct simplified mesh payload IDs.
- Imported authored LODs record their producer/source identity.
- Generated LODs record effective settings, source mesh semantic hash, and simplifier version.
- Invalid generated LODs are omitted with diagnostics; the manifest cannot claim an omitted payload.

### 10.4 Meshlet Payload

The serialized representation is CPU-owned and based on the existing CPU meshlet descriptor/remap data. It includes:

- mesh and LOD payload ID;
- descriptor ranges;
- source-vertex remap indices;
- triangle-local indices;
- bounds;
- cone axis, cutoff, and apex data;
- effective generation settings and payload freshness metadata.

`GPUScene.GpuMeshletDescriptor` already provides the GPU-facing cone representation. The cache loader expands or uploads cached CPU descriptors through the existing GPUScene path. The older/alternate `Meshlet` type is not the cache schema authority and does not need to be expanded solely for this feature.

For a model originating from a valid model container:

1. use the model container’s meshlet section;
2. if the optional section is repairable, consult `MeshletPayloadDiskCache` or rebuild from cached core geometry according to policy;
3. use `MeshletPayloadDiskCache` as primary only for meshes outside model-container ownership.

A valid meshlet section must not trigger `MeshletGenerator.Build`.

### 10.5 Prefab Graph and Component Codecs

`PrefabGraph` stores deterministic node IDs, parent IDs, sibling order, names, enabled/layer state, transform kind, and canonical local-transform data. Model/submesh/mesh relationships use cache-local IDs rather than object references.

Imported component state uses a registry rather than generic reflection or YAML:

- `IImportedComponentCacheCodec` is registered at the `XRENGINE` composition boundary.
- Each codec has a stable type key, monotonic payload version, required/optional policy, and bounded canonical reader/writer.
- Standard model-component records reference the model/submesh/material tables instead of embedding those objects.
- Unity-specific adapters may contribute component records without introducing Unity/XRENGINE type dependencies into ModelingBridge.
- The lower container treats registered extension payloads as bounded canonical bytes; the upper adapter owns live component construction and mutation.
- A missing or incompatible required component codec rejects the requested prefab hydration. Unknown optional component records are skipped with diagnostics.
- Components intentionally ignored by a source converter are recorded in import diagnostics, not invented as empty cache records.

The v1 fixture corpus defines the minimum required component-codec set. Cold and warm imports must report the same supported/ignored component inventory.

## 11. Determinism and Stable Identity

Determinism rules:

- Traverse hierarchy in source-defined order when stable; otherwise use an explicit normalized sort key.
- Sort dictionaries and sets before serialization.
- Canonicalize floats according to the section codec contract, including signed zero and non-finite-value rejection/normalization.
- Do not serialize CLR hash codes, object addresses, random GUIDs, local time, or collection iteration accidents.
- Allocate cache-local IDs deterministically from canonical traversal order.
- Keep diagnostics that may vary out of semantic section bytes.

Equivalent cold imports must produce byte-identical semantic sections. If the file includes diagnostic build identity, deterministic tests compare after zeroing only the documented diagnostic field; no broad “ignore header” exception is allowed.

### 11.1 Imported Entity Keys

Manual reimport identity uses:

1. a stable producer/source entity ID when available;
2. otherwise normalized node path + entity kind + deterministic slot/ordinal.

Names alone are insufficient. Each cache entry records its `ImportedEntityKey`, cache-local ID, source diagnostic name, and project-binding hint.

Project-binding hints are deterministic imported keys and normalized project-relative paths, not project GUID ownership. Live GUIDs and user remap values remain in project assets/import options and are resolved by the upper-layer adapter. This keeps the cache deterministic while still allowing manual reimport to preserve existing project identity.

Renames, removals, splits, merges, or ambiguous matches are identity breaks. They must be shown before a manual reimport commits if they would replace project GUIDs or bindings.

## 12. Project Assets, Materials, Textures, and Animations

The generated project tree remains editable and authoritative:

```text
<import-folder>/
    Model.asset
    Model/
        Textures/*.asset
        Materials/*.asset
        SubMeshes/*.asset
        Meshes/*.asset
        Models/*.asset
        Animations/*.asset
```

Cache hydration restores imported defaults and binding hints, then resolves existing project assets/remaps over them.

Animation, texture, material, and mesh references stored in semantic cache sections use deterministic imported keys and normalized binding hints. Randomly generated project GUIDs are not copied into semantic cache bytes.

Warm reads may seed missing remap keys only when the cached key is absent from live import options. Replaying an already-present key must not mark import options changed or touch their timestamp.

Animation rules:

- store durable output references and imported animation identity only;
- never store clip/keyframe payload bytes;
- do not mark the model cache complete until required referenced animation outputs are durably available.
- validate required animation outputs through the animation subsystem before hydrating a group that needs them.

Texture rules:

- store texture source keys, material interpretation, sampler metadata, and durable asset/cache references;
- never store image payload bytes;
- hand embedded or external texture data to the texture subsystem through its public import API;
- do not mark the model cache complete until required embedded-texture outputs are durably available;
- validate required texture outputs through the texture subsystem before hydrating a group that needs them;
- never write directly into the texture cache’s internal paths.

## 13. Read and Repair Behavior

Required read order:

1. Open final file read-only.
2. Validate preamble size, magic, versions, fixed freshness fields, and header checksum.
3. Validate string-pool and chunk-table ranges before reading them.
4. Validate chunk table checksum and every range/count.
5. Read and validate `Dependencies`.
6. Read `Manifest`.
7. Hydrate only the requested content groups.

Required-section failure rejects the cache and falls back to source import. Optional-section behavior is explicit:

| Section | Failure behavior |
|---|---|
| `LodTables` | Repair from valid cached core mesh data when policy allows; otherwise source fallback if required by the requested load. |
| `Meshlets` | Repair from valid cached mesh/LOD data when policy allows; otherwise continue without meshlets only when the requested rendering policy permits it. |
| `ColliderHints` | Continue without hints and warn. |
| `Diagnostics` | Ignore. |

Repair never invokes the source parser. A successful repair first updates the in-memory document. Republishing is best-effort:

- writable cache: publish a replacement containing the repaired section;
- read-only cache: keep the repaired data in memory and warn;
- publication race lost: accept the winner if it validates, otherwise continue with the in-memory repair.

Failure to write repaired optional data must not discard already valid required cached data or force a source parse.

Readers use independent file handles and permit deletion/replacement sharing appropriate for atomic publication. Existing readers may finish against the old immutable file; new readers see the new file.

## 14. Atomic Publication and Concurrency

Publication sequence:

1. Acquire an in-process keyed semaphore for the normalized final cache path.
2. Acquire a cross-process named mutex derived from a cryptographic hash of that path.
3. Recheck whether another producer already published a compatible entry.
4. Snapshot the immutable `CookedModelDocument`.
5. Write a unique adjacent candidate: `<cache>.tmp.<pid>.<nonce>`.
6. Flush the stream to disk where supported and close it.
7. Reopen the candidate with the production reader and validate all required chunks and checksums.
8. Atomically replace the existing file, or atomically move into place when no destination exists.
9. Release cross-process and in-process ownership.

The cache file is never mutated in place. Last-writer-wins is acceptable only after writers are isolated by unique candidates and each published candidate is valid for the same cache identity.

Failure behavior:

- Preserve the old valid cache until replacement succeeds.
- Delete the current process’s failed candidate on a best-effort basis.
- Never delete another live writer’s temp file.
- Quarantine/remove a corrupt final entry only on a best-effort basis; source import must still proceed.

Orphan cleanup matches the exact model-cache temp pattern and removes only files older than a grace period after confirming no owning lock is active. It does not prefer temp files over a final entry.

Cache writing and model cooking run on import workers, never the render thread.

## 15. Manual Reimport Transaction

Manual reimport always bypasses cache reads and parses the source.

Transaction:

1. Build a complete new `CookedModelDocument` and dependency report.
2. Resolve imported entity keys against the existing generated asset tree.
3. Stage generated project assets while preserving matched GUIDs/bindings.
4. Stage and fully validate a model-cache candidate.
5. Present identity breaks and destructive diffs for confirmation.
6. Commit the generated asset tree and cache publication through the extended reimport transaction.
7. Roll back to the prior generated tree and prior valid cache if commit fails.

The cache candidate is not published before referenced animation/texture outputs and staged generated assets meet their durability barriers.

An unmatched or ambiguous entity emits `Model.ReimportIdentityBreak` with the old and new keys, affected asset identity, and reason. Reference-breaking reimport is never silent.

## 16. Hydration and Runtime Constraints

The loader constructs engine-native objects directly from `CookedModelDocument`; it does not rebuild a duplicate third-party scene graph.

- `XRBase`-derived state is populated through normal property/`SetField(...)` mutation paths.
- Large byte buffers come from pools and are returned promptly.
- Section readers operate on `ReadOnlySpan<byte>` or equivalent bounded views.
- No LINQ, captured closures, boxing, or avoidable allocations are introduced in GPUScene registration or other per-frame paths.
- Cache I/O and decoding occur on asset-loading workers.
- GPU resources are created only by their owning runtime systems.
- No new compiler warnings are permitted.

Partial hydration groups include:

- metadata/dependency inspection;
- prefab structure and imported bindings;
- selected mesh core;
- skinning/morph data;
- LOD/meshlet render data.

Grouping is an I/O optimization, not permission to skip dependency or range validation.

## 17. Diagnostics and UX

Primary events:

- `Model.CacheHit`
- `Model.CacheMiss`
- `Model.CacheRejected`
- `Model.CacheFallbackToSource`
- `Model.CacheRead`
- `Model.CacheWrite`
- `Model.CacheRepair`
- `Model.CachePublishRace`
- `Model.CacheOrphanTempSwept`
- `Model.CacheManualReimport`
- `Model.ReimportIdentityBreak`

`CacheRejectReason` includes:

```text
None
FileMissing
LegacyFormat
SchemaVersionMismatch
PayloadVersionMismatch
HeaderChecksumMismatch
ChunkTableChecksumMismatch
StringPoolChecksumMismatch
DependencyManifestChecksumMismatch
ReferencedOutputMissing
ReferencedOutputIncompatible
EntrySourceMissing
SourceLengthMismatch
SourceTimestampMismatch
SourceHashMismatch
DependencyMissing
DependencyLengthMismatch
DependencyTimestampMismatch
DependencyHashMismatch
RequestedBackendPolicyMismatch
BackendResolutionPolicyMismatch
ImporterBackendMismatch
ImporterBackendVersionMismatch
ImportOptionsHashMismatch
ModelCookSettingsHashMismatch
MaterialPolicyVersionMismatch
RequiredChunkMissing
UnknownRequiredChunk
RequiredComponentCodecMissing
ComponentCodecVersionMismatch
ChunkChecksumMismatch
ChunkVersionMismatch
InvalidChunkRange
OverlappingChunkRange
ResourceLimitExceeded
AssetTypeMismatch
CodecUnavailable
SerializationFailed
Unreadable
```

One reason is primary; structured details identify the dependency, chunk, expected value, and actual value where relevant.

The editor exposes:

- hit/miss/rejected/repaired state;
- primary rejection reason and dependency detail;
- requested policy, candidate snapshot, and actual producer/version;
- schema, payload, section-codec, and cook-policy versions;
- source and dependency freshness details;
- mesh/LOD/meshlet summaries;
- cache path and size;
- reimport-from-source;
- rebuild/remove cache;
- inspect manifest;
- reconcile orphan/stale caches;
- open generated asset and reveal cache file.

Reconcile is manual in v1. It identifies model entries by path partition plus header magic/type, asks before deletion, and treats external-source entries conservatively.

## 18. Implementation Sequence

The [implementation tracker](../../todo/assets/model-import-binary-cache-todo.md) is authoritative for phase status and validation evidence. The dependency order is:

1. exclusive cache decision contract and backend registry;
2. identity, paths, resolver snapshot, and dependency reporting;
3. defensive container and manifest;
4. shared mesh section codecs and import-time cooking;
5. prefab/material/reference hydration;
6. optional repair and partial hydration;
7. atomic concurrency, manual reimport identity, and editor UX;
8. integration, corruption, determinism, and performance evidence.

Stages 1–3 are implemented. Work proceeds from shared mesh sections and import-time cooking; completing the container does not imply that warm hydration or publication is active.

Do not implement hydration on top of the current boolean codec/YAML fallback. Do not add model-specific copies of the existing cooked mesh encoders.

## 19. Validation Requirements

The test suite must cover:

- exclusive codec ownership and absence of YAML fallback;
- legacy YAML rejection;
- path/fingerprint stability across process, locale, case, and long-path variants;
- requested `Auto` policy, ordered candidate changes, fallback producer identity, and producer-version changes;
- entry source and structural dependency invalidation;
- deterministic container and section bytes;
- fixed-layout, bounds, overlap, integer-overflow, resource-limit, truncation, checksum, and unknown-chunk handling;
- standalone `XRMesh` compatibility after shared-section extraction;
- cold/warm equality for hierarchy, transforms, mesh streams, materials, skinning, morphs, LODs, and references;
- zero model-parser calls on a valid warm hit;
- zero LOD/meshlet builder calls when valid sections exist;
- CPU-to-GPU meshlet upload equivalence;
- project remap/binding authority and no warm-load writes under `Assets/`;
- absence of animation and texture payload bytes;
- read-only warm loads and in-memory repair;
- same-process and cross-process writers, interrupted writes, and readers during replacement;
- manual reimport GUID preservation, identity-break preview, cancellation, and rollback;
- cold import, warm full hydration, and partial hydration performance/allocation baselines.

Required v1 integration formats are FBX, external-buffer glTF, embedded GLB, skinned/morph/animated glTF, OBJ/MTL, and Unity prefab.

## 20. Acceptance Criteria

- A valid warm hit never invokes a model source parser.
- A valid warm hit never rebuilds compatible LOD or meshlet sections.
- Cache identity covers requested resolver policy, ordered candidates, actual producer/version, canonical import/cook settings, format/codec versions, and structural dependencies.
- The defensive reader rejects malformed offsets, counts, overlaps, checksums, versions, and resource-limit violations before unsafe allocation.
- Cold and warm hydration are structurally equivalent for every required v1 fixture.
- Project-authored assets and remaps remain authoritative, and warm loads do not write project assets.
- The model cache contains no animation clip or texture image payloads.
- Shared mesh section codecs are the single binary authority used by standalone `XRMesh` and model-container persistence.
- Cached CPU meshlet data reaches the existing GPUScene path with equivalent descriptors and no rebuild.
- Optional repair works without source parsing; a failed repair write on read-only media does not abort the load.
- Publication preserves the previous valid entry through crashes and same-process/cross-process races.
- Manual reimport preserves matched project identity, previews breaks, and rolls back on failure.
- Rejections and fallbacks report one actionable primary reason.
- Targeted correctness, resilience, determinism, integration, and performance evidence is complete before the tracker is closed.
