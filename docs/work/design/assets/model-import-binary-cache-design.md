# Model Import Cooked Asset Cache Design

Last Updated: 2026-05-18
Status: design proposal
Scope: engine-native cooked `.asset` cache files for third-party model imports, including generated meshlet and LOD data.

Related docs:

- [Model import feature guide](../../../developer-guides/assets/model-import.md)
- [Texture management runtime design](../texturing/texture-management-runtime-design.md)
- [Texture streaming cooked cache TODO](../../todo/texturing/texture-streaming-cooked-cache-todo.md)
- [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Production rendering pipeline roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)

## 1. Summary

Third-party model imports should follow the texture-cache pattern: the original source file is parsed on the first import, but a fresh cooked engine-native `.asset` cache becomes the preferred runtime/import authority afterward.

For models, the cache should be more than a YAML prefab snapshot. It should be a cooked engine-native asset binary that includes:

- imported scene hierarchy data needed to reconstruct the `XRPrefabSource`
- model, submesh, material, texture-reference, skeleton, and collider metadata
- `XRMesh` vertex/index streams in the engine layout
- morph target deltas
- generated `SubMeshLOD` data
- generated meshlet descriptors and meshlet index streams
- meshoptimizer statistics and generation settings
- source and import-option freshness metadata

Animation clips embedded in third-party model files are intentionally **not** cached by the model cache asset. They are handled by a separate animation cache covered in its own design. The broader pattern is that every third-party asset type gets a cooked engine-native `.asset` cache; this document covers only the model payload.

The user keeps the original `.fbx`, `.gltf`, `.glb`, `.obj`, or other source file. Manual reimport remains available and intentionally replaces the cache. Normal loads should read the cache in place of the original source whenever the cache is fresh and compatible.

## 2. Goals

- Avoid reparsing large third-party model files on warm loads.
- Avoid regenerating LODs and meshlets on warm loads.
- Store model import results in a compact cooked `.asset` binary format that can be parsed faster than YAML and third-party formats.
- Preserve source-file authority for manual reimport, diffing, and artist workflows.
- Treat fresh cache data as the default load authority for runtime and editor startup.
- Keep cache freshness deterministic and debuggable.
- Make cache keys include source identity, source timestamp, importer backend, import options, mesh optimizer settings, cache schema version, and runtime payload version.
- Allow manual reimport to force source parse and atomically replace cache output.
- Keep externalized authoring assets and cache assets distinct by location and authority: project `.asset` files under `Assets/` are user/project content; cooked `.asset` files under `Cache/` are generated and disposable.
- Produce **deterministic** cache output: identical source + settings + backend version must yield byte-identical cache contents modulo a small set of well-known timestamp/UUID fields recorded in a single header region. Determinism is a tested property, not a hope.

## 3. Non-Goals

- This design does not remove the existing YAML asset format for project-authored assets.
- This design does not remove externalized sub-assets beside generated prefab assets.
- This design does not require every third-party format to use the cooked `.asset` cache on day one.
- This design does not invent a new mesh simplification or meshlet generation library.
- This design does not make stale or unreadable caches fatal. They fall back to source import unless the caller explicitly requested cache-only validation.
- This design does **not** cache animation clips. Animations extracted from model files are owned by a separate animation cooked `.asset` cache. The model importer must extract animation data through the existing source-import path (or delegate to the animation importer) but must not persist clip data inside a model cache file.
- This design does not specify caches for textures, audio, or other third-party asset types. Those follow the same authority pattern but are covered by their own designs.

## 4. Current Baseline

`AssetManager` already has a third-party cache mechanism:

- `GameCachePath` defaults to `<ProjectRoot>/Cache`.
- `TryResolveCacheDirectory` mirrors game/engine asset-relative source paths under the cache root and hashes external paths under `Cache/External`.
- `TryResolveCachePath` maps a source path and asset type to a cached `.asset`.
- `IsCacheAssetFresh` compares cache timestamp against source timestamp and import-option timestamp.
- `ResolveDefaultCacheVariantKey` already uses a texture streaming payload key for `XRTexture2D`.
- `ReimportThirdPartyFile` and `ReimportThirdPartyFileAsync` force source import with overwrite.

Textures already use this policy as a runtime streaming authority once a fresh cooked cache exists. Models should use the same authority model, but with a model-specific binary payload and variant key.

## 5. Terminology

| Term | Meaning |
| --- | --- |
| Source model | The user-owned third-party file, such as `.fbx`, `.gltf`, or `.glb`. |
| Generated asset | The project `.asset` created beside the source model, usually an `XRPrefabSource` plus externalized sub-assets. |
| Cooked cache asset | Disposable engine-native `.asset` cache stored under `<ProjectRoot>/Cache`, preferred for warm loads of the third-party source. |
| Cache authority | A fresh cache is loaded instead of reparsing the source model; project `.asset` files always win over the cache (see section 13). |
| Manual reimport | User action that forces source parse and replaces generated asset and cache. |
| Schema version | Version of the header and chunk-table layout. Bumped when fixed-size header or directory format changes. Old caches are rejected. |
| Payload version | Version of chunk binary layouts (vertex stream encoding, meshlet descriptor, etc.). Bumped on any chunk format change. Old caches are rejected. |
| Engine version | Informational only. Never used for invalidation; everything that should invalidate the cache must be reflected in `schemaVersion`, `payloadVersion`, importer/backend version, or settings hashes. |
| Variant key | Stable cache subdirectory segment for import options that change cache output. |
| Backend version | Stable monotonic integer exposed by each third-party importer (Assimp, glTF, FBX, Unity prefab, etc.). Bumped when the importer's output changes. |

## 6. Cache Authority Rules

The cooked cache asset short-circuits **only** the third-party importer step. Loading a project-authored `.asset` (including externalized sub-assets such as `Materials/*.asset`, `SubMeshes/*.asset`, `Meshes/*.asset`) always goes through the normal asset loader and never consults the model cache. Project assets are the authority for anything a user can edit; the cache is the authority only for what the importer would otherwise have to recompute from the source model.

When loading a third-party model source path:

1. Resolve asset type and import options as today.
2. Compute the model cache variant key.
3. Resolve the cache path.
4. If cache exists, is fresh, schema-compatible, payload-compatible, and complete, load it.
5. If cache is missing, stale, incompatible, or unreadable, import from source.
6. After successful source import, write a new cooked cache asset.
7. If the user explicitly chooses manual reimport, skip cache read, parse source, overwrite generated project assets, and atomically replace cache.

### 6.1 Versioning

Three independent version numbers govern cache compatibility:

| Version | Scope | Mismatch behavior |
| --- | --- | --- |
| `schemaVersion` | Layout of the header, string pool, and chunk table. | Full reject; file is unreadable in a structured way. |
| `payloadVersion` | Layout of chunk *contents* across the whole file (e.g. struct shapes). | Full reject. |
| per-chunk `version` | Layout of a single chunk's contents. | Per-chunk reject; loader may treat the chunk as missing and apply the §6.3 repair policy. |

Engine version is recorded for diagnostics only and is **not** a freshness input. Bumping the engine version must not invalidate caches; only bumping `schemaVersion`, `payloadVersion`, or a per-chunk `version` does.

### 6.2 Freshness Inputs

Freshness is decided from data accessible without parsing chunk payloads:

- source file length
- source last-write UTC timestamp
- optional source content hash (see §6.4) for external or unreliable timestamp cases
- import options timestamp
- importer backend and backend version
- `schemaVersion` and `payloadVersion`
- meshoptimizer native version when available
- meshlet generation settings hash
- LOD generation settings hash
- coordinate conversion settings hash
- material/texture remap input hash
- shader/material import policy version

The normalized source path is recorded in the header for diagnostics, but it is **not** a freshness input. Cache identity is keyed off the cache file's *location* (resolved from the source path at lookup time). This lets projects move or be renamed without invalidating every cache.

Cache freshness must be explainable in logs. Every cache miss carries a single `reason` enum value (see §18) identifying which check failed.

### 6.3 Per-Chunk Repair Policy

A cache may be header-fresh but have an individual chunk that is missing, stale (older per-chunk `version`), checksum-failed, or generated from now-invalidated settings. The loader applies the following policy per chunk:

| Chunk | Policy on failure |
| --- | --- |
| `Manifest`, `PrefabGraph`, `Models`, `SubMeshes` | Required. Failure escalates to full cache miss → reimport from source. |
| `Meshes`, `MeshVertexStreams`, `MeshIndexStreams` | Required. Failure escalates to full miss. |
| `MorphTargets` | Required if any SubMesh declares morph targets; otherwise absent is valid. |
| `Skeletons` | Required if any mesh is skinned. |
| `LodTables` | Regenerable from cached `Meshes`. Failure triggers in-process LOD regeneration via meshoptimizer; cache is rewritten on success. No source-model parse. |
| `Meshlets` | Regenerable from cached `Meshes` + LODs. Same recovery as `LodTables`. |
| `Materials`, `TextureReferences` | Failure escalates to full miss (material identity is too entangled with the prefab graph to repair in isolation in v1). |
| `ColliderHints` | Optional. Absent or stale → loader proceeds without collider hints and logs a warning. |
| `Diagnostics` | Never required; never read by the loader. Forensic only. |

Partial repair must write the repaired chunks atomically via the §15 swap rules. If repair fails, the loader falls back to source import.

### 6.4 Source Hashing Policy

By default, freshness uses `(length, last-write-utc)`. A content hash is computed only when:

- the source lives on a network share or path classified as "timestamp-unreliable";
- the user explicitly opts in via import options; or
- the timestamp moved *backwards* relative to the recorded value (suggesting source-control checkout).

When hashing is required, the hash is computed once at import time, stored in the header (`sourceHash`, `sourceHashMode`), and only recomputed on subsequent loads if the cheap `(length, mtime)` check disagrees. The hash algorithm is xxHash3-64 streamed over the file; this is non-cryptographic and chosen for throughput.

## 7. Cache Path And Variant Key

Use the existing cache root with the variant key forming the directory bucket. There is one tree (not a `Models_v1/` bucket *plus* a variant key — they are the same thing):

```text
<ProjectRoot>/Cache/
    Models/
        v<schemaVersion>/
            importer_<backend-key>/
                opts_<hash>/
                    <relative-source-path>/
                        <source-name>.asset
                        <source-name>.asset.tmp     (only during writes)
```

Model caches use the same `.asset` extension as other engine-native cached third-party assets. The cache is distinguished from editable project assets by location (`Cache/` versus `Assets/`), variant key, asset type metadata, and the internal payload magic/version fields. Do not introduce a model-specific public file extension for v1.

`opts_<hash>` is a single short stable hash over the union of: meshlet generation settings, LOD generation settings, material/texture policy version, coordinate conversion settings, and any other settings that change cache output. Full metadata for these settings is stored in the cache header so a cache file remains self-describing without consulting the variant directory name.

Do not place individual settings directly in the path.

Windows path length. The fully resolved cache path is bounded by:

- `<ProjectRoot>` length (user-controlled).
- A fixed `Cache/Models/v<schemaVersion>/importer_<backend-key>/opts_<8hex>/` prefix (~60 chars worst case).
- The source-relative path mirrored under the variant directory.
- The cache file name `<source-name>.asset`.

The implementation enables Windows long-path support (`\\?\` prefix and the `LongPathsEnabled` registry/manifest opt-in) for all cache file IO. If long paths are unavailable on a target machine, the cache path resolver falls back to collapsing `<relative-source-path>` into a single hashed segment to stay under MAX_PATH. The fallback strategy is logged once at AssetManager startup and the choice is documented in `docs/developer-guides/assets/model-import.md`.

## 8. Cooked Asset Binary Shape

Use a three-region binary payload inside the cooked cache `.asset`:

1. **Fixed preamble** of fixed-size scalars (magic, versions, sizes, offsets, and the source freshness tuple). All scalars are little-endian, naturally aligned, packed to a 16-byte boundary.
2. **String pool** of length-prefixed UTF-8 strings referenced by offset from preamble and chunks (source path, importer backend name, etc.).
3. **Chunked payloads** indexed by a chunk table.

The format is little-endian and assumes natural alignment of scalar fields. A future big-endian port must bump `schemaVersion` rather than attempting in-place byte-swap.

### 8.1 Preamble

Fixed-size, no variable-length fields. All offsets are byte offsets from the start of the file.

```text
magic                    : u8[16]   = "XRE_MODEL_CACHE\0"
schemaVersion            : u32
payloadVersion           : u32
engineVersion            : u32      (diagnostic only; not a freshness input)
preambleSize             : u32      (== sizeof(preamble), aligned)
stringPoolOffset         : u64
stringPoolSize           : u64
chunkTableOffset         : u64
chunkCount               : u32
fileSize                 : u64      (expected total file size at write time)
fileChecksum             : u64      (xxHash3-64 over the whole file with this field zeroed)
sourceLength             : u64
sourceLastWriteUtc       : i64      (.NET ticks)
sourceHashMode           : u8       (0=none, 1=xxHash3-64)
sourceHash               : u64
assetType                : u32      (enum)
importerBackend          : u32      (string pool offset)
importerBackendVersion   : u32
sourcePathNormalized     : u32      (string pool offset; diagnostic only)
importOptionsHash        : u64
meshOptimizerSettingsHash: u64
materialPolicyVersion    : u32
flags                    : u32
reserved                 : u8[32]
```

The loader must be able to reject the cache from the preamble alone when versions or source freshness do not match, without reading the string pool or chunk table.

### 8.2 Chunks

Initial chunks:

- `Manifest`
- `PrefabGraph`
- `Models`
- `SubMeshes`
- `Meshes`
- `MeshVertexStreams`
- `MeshIndexStreams`
- `MorphTargets`
- `LodTables`
- `Meshlets`
- `Materials`
- `TextureReferences`
- `Skeletons`
- `ColliderHints`
- `Diagnostics`

There is **no** `Animations` chunk. Animation clips extracted from the source model are persisted by the separate animation cache.

Each chunk table entry has:

- type (u32 enum)
- version (u32, per-chunk)
- offset (u64)
- compressed size (u64)
- uncompressed size (u64)
- checksum (u64, xxHash3-64 over **uncompressed** bytes)
- compression kind (u8: 0=none, 1=lz4, 2=zstd)
- flags (u32)

Checksum scope is documented as uncompressed bytes so a checksum mismatch unambiguously indicates payload corruption rather than codec mismatch. A whole-file checksum lives in the preamble (§8.1, `fileChecksum`) and is the first integrity gate; chunk checksums are validated on demand during hydration.

**Compression decision for v1**: no compression. nvCOMP is already a repo-managed dependency for GPU paths, but pulling it into the asset-import CPU path adds complexity without measured benefit. If profiling later shows a win, LZ4 via an MIT/BSD-licensed package is the preferred candidate; any addition must go through the dependency-generation process in `AGENTS.md`.

**Diagnostics chunk**: an opaque, optional, free-form key/value blob used for forensic logging (importer warnings, meshoptimizer stats verbatim, etc.). The loader never reads it during normal hydration.

## 9. Mesh Payload

The cache stores engine-native mesh data, not source-format accessors. The `Meshes`/`MeshVertexStreams`/`MeshIndexStreams` chunks contain:

- positions
- normals
- tangents
- UV sets
- color sets
- bone influence data (per-vertex; references a skeleton ID resolved from the `Skeletons` chunk)
- index buffer
- primitive topology after import conversion
- mesh bounds
- source node/submesh identity (for diagnostic round-tripping)
- skeleton ID (when skinned)

Morph target deltas live in the dedicated `MorphTargets` chunk and are referenced by mesh ID. They are stored separately because morphs are large, optional, and may be hydrated lazily.

The `Skeletons` chunk owns the bind pose, joint hierarchy, and inverse bind matrices. Meshes reference skeletons by ID; multiple meshes may share a skeleton.

The runtime must be able to create `XRMesh` and atlas-ready mesh data without reopening source buffers.

## 10. LOD Payload

The cache stores the generated or imported LOD chain per logical submesh:

```text
LogicalMesh
    lodCount
    lod[0].meshPayloadId   // LOD 0 references the source mesh's payload id; no duplication
    lod[0].maxVisibleDistance
    lod[0].generationSource
    lod[1].meshPayloadId   // distinct payload id; simplified mesh stored in Meshes chunk
    ...
```

LOD 0 is always the source mesh and shares its `meshPayloadId` with the corresponding `Meshes` entry; it is never duplicated. LODs 1+ are distinct payloads written into the same `Meshes`/`MeshVertexStreams`/`MeshIndexStreams` chunks.

LOD payload freshness includes:

- `MeshLodGenerationSettings` hash
- source mesh hash
- simplification backend/version
- imported authored LOD identity when present
- distance policy

Generated LODs must be deterministic for a given source mesh and settings — this is tested in §20. If a generated LOD fails validation, the cache writer must omit that LOD and record diagnostics rather than writing corrupt partial data. Per §6.3, a `LodTables` chunk that is stale but whose mesh inputs are still valid is regenerated in-process without falling back to source import.

## 11. Meshlet Payload

The cache stores meshlets for every cached LOD mesh that participates in meshlet rendering:

```text
MeshletSet
    meshPayloadId
    settingsHash
    maxVertices
    maxTriangles
    descriptorOffset
    descriptorCount
    vertexIndexOffset
    vertexIndexCount
    triangleByteOffset
    triangleByteCount
    stats
```

Each descriptor stores:

- bounding sphere
- vertex-reference range
- triangle-local-index range
- vertex count
- triangle count
- cone axis/cutoff
- cone apex or compressed equivalent

This document **owns** the cooked meshlet descriptor schema. The [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md) consumes that schema and must not redefine descriptor field shapes independently. Layout changes here require a bump to the `Meshlets` chunk `version` and a coordinated update in the meshlet renderer doc.

The cache distinguishes two descriptor forms:

- **Serialized CPU descriptor** (`MeshletDescriptor`, new type): everything needed to rebuild the GPU descriptor on load — bounding sphere, vertex-reference range, triangle-local-index range, vertex/triangle counts, cone axis, cone cutoff, and cone apex (or compressed equivalent). This is what lives in the `Meshlets` chunk and is versioned by the chunk `version`.
- **GPU descriptor** (`Meshlet` struct in [`Meshlets/Meshlet.cs`](XREngine.Runtime.Rendering/Rendering/Meshlets/Meshlet.cs)): the packed form uploaded to the GPU and consumed by task/mesh shaders. The runtime builds this from the serialized CPU descriptor at load time using a per-thread scratch buffer.

Keeping the two representations separate means a future GPU layout change (e.g. bit-packing, splitting cone data into a side buffer) does not require bumping the cache chunk `version` unless the CPU descriptor changes.

The current runtime `Meshlet` struct stores sphere and ranges but not cone payload. Extending it with cone axis + cutoff and cone apex is part of this design and must be coordinated with every shader, indirect-buffer writer, pool, and allocator that consumes `Meshlet` (see Phase 4). This is a breaking GPU buffer layout change; `payloadVersion` bumps so existing caches are invalidated and rebuilt. The change is reflected in the renderer doc's §5.2.

Meshlet payload freshness includes:

- `MeshletGenerationSettings` hash
- mesh payload hash
- meshoptimizer native version/export availability
- vertex/index format assumptions
- descriptor layout version (== `Meshlets` chunk `version`)

Per §6.3, a stale `Meshlets` chunk can be regenerated in-process from cached meshes.

## 12. Materials And Texture References

The cooked cache asset stores enough imported material data to rebuild material assets or map to existing project assets:

- source material name
- resolved engine material type
- scalar/vector properties
- texture slots by source texture key
- alpha/opacity interpretation
- shader/material policy version
- remap keys seeded from the source model

Texture payload bytes do not belong in the model cache asset. Texture references store:

- user remap target asset, when configured
- generated/imported texture asset path
- original source texture key for future remap repair

Texture streaming cache remains owned by the texture system.

Remap-seeding rule on warm cache reads (interaction with [`XRPrefabSource.Import3rdParty`](XRENGINE/Scene/Prefabs/XRPrefabSource.cs#L154-L200)): the source importer today writes back into `ModelImportOptions.TextureRemap` / `MaterialRemap` and sets `importOptionsChanged` when a new key is seeded. Warm cache reads must replay that seeding **only if** the seeded keys differ from what is already in `ModelImportOptions`; otherwise `importOptionsChanged` must stay `false` so import-options timestamps do not flap and cause the next load to think the cache is stale. Keys that the cache knows about but the live options do not are added with `null` values, matching the source-import behavior. The model importer is **forbidden** from writing into the texture cache root (`Cache/Textures/...` or whatever the texture system uses); any embedded texture data extracted from source models must be handed off to the texture importer through its public API.

## 13. Generated Asset Relationship

There are two outputs after source import:

1. Generated project asset tree under `Assets`, visible and user-editable.
2. Binary cache under `Cache`, generated and disposable.

The generated asset tree keeps the current externalization layout:

```text
<import-folder>/
    Model.asset
    Model/
        Textures/*.asset
        Materials/*.asset
        SubMeshes/*.asset
        Meshes/*.asset
        Models/*.asset
        Animations/*.asset      // produced by source import; cached by the animation cache, not this cache
```

Animation `.asset` files under the generated tree are owned by the animation import/cache subsystem. The model cache does not produce, read, or invalidate them.

The cooked cache asset accelerates future loads/imports. It should not be the only copy of user-authored overrides. User edits belong in project assets or import options, not inside cache-only state.

## 14. Manual Reimport

Manual reimport is the explicit way to replace cache output:

- User invokes reimport on source model.
- AssetManager sets a force-source-import flag.
- Cache read is skipped.
- Source importer runs.
- Generated project asset tree is updated through the existing externalization flow.
- Binary cache is written to a temporary path.
- Cache manifest and payload are atomically swapped into place (see §15).
- Old cache is deleted only after the new cache validates.

Manual reimport **guarantees** asset GUID stability for the following identity classes when the underlying source entity can be matched:

- generated `Model`/`SubMesh`/`XRMesh`/`Material` assets keyed by source-node path + source-name tuple
- texture remap entries keyed by source texture key
- material remap entries keyed by source material name
- user-authored import options

If an entity in the new source has no match (renamed, removed, or split), the importer:

1. Logs a `Model.ReimportIdentityBreak` event with the affected GUIDs and source entity paths.
2. Surfaces the break in the editor reimport UI before committing.
3. Allows the user to confirm or cancel the reimport.

Reference-breaking reimport is never silent.

If reimport fails, existing generated assets and existing cache remain usable.

## 15. Cache Writes

Cache writes must be atomic on Windows with the following boundary conditions:

1. The temp file is `<cache>.tmp` in the **same directory** as the final cache file. Cross-volume renames are not atomic on NTFS; placing the temp adjacent to the destination guarantees `File.Replace`/`MoveFileEx` is atomic.
2. Write payload to `<cache>.tmp`.
3. Flush to disk (`FileStream.Flush(flushToDisk: true)`).
4. Close the temp file.
5. Reopen the temp file and validate: preamble, whole-file checksum, chunk table, and one round-trip chunk checksum sample.
6. Atomically replace the existing cache via `File.Replace` (or `File.Move` with overwrite when no prior file exists).
7. Emit cache-write diagnostics.

Writer serialization:

- Only **one** writer per (resolved) cache path may be active at a time. Concurrent imports of the same source acquire a per-cache-path mutex; the second writer waits for the first to finish and then re-checks freshness (it may now be a hit).
- Cross-process collisions are tolerated via `File.Replace` last-writer-wins semantics; both outputs are valid if both passed validation in step 5.

Orphan recovery:

- On AssetManager startup, sweep `Cache/Models/**/*.asset.tmp` and delete any orphan temp files older than a small grace period (e.g. 10 minutes). This handles crashes mid-write.
- An orphan `.tmp` is never preferred over an existing `.asset`; it is treated as a write that did not complete.

Thread placement:

- Cache writes never run on the render thread.
- Cache writes run on the same import worker that performed the source parse, after the generated project asset tree has been flushed.
- Any GPU-generated payload needed for future model caches must use explicit async readback and cannot be part of the zero-readback render path.

For meshlet and LOD generation, prefer CPU meshoptimizer generation during import/cache write.

## 16. Cache Reads

Warm cache reads support partial hydration:

- read preamble first; reject early on version/freshness mismatch
- validate whole-file checksum on first access (lazy: skip in release fast-path when the file has not been modified since last successful validation)
- read string pool and chunk table
- hydrate `Manifest`, `PrefabGraph`, `Models`, `SubMeshes` for editor display
- hydrate `Meshes`/`MeshVertexStreams`/`MeshIndexStreams` on demand or by prefetch group
- hydrate `MorphTargets`, `Skeletons`, `LodTables`, and `Meshlets` before `GPUScene` registration for renderable meshes
- the cache contains no animation data; animation hydration is owned by the animation cache and is independent of model cache reads

Thread placement:

- Cache reads run on the asset loading worker(s), never on the render thread.
- Read-only access from multiple threads is supported via independent `FileStream` handles; the file is opened with `FileShare.Read` only.
- Cache reads must succeed on a read-only filesystem; failures to write back repaired chunks (§6.3) log a warning and fall through to source import without aborting the load.

The loader avoids allocating a full duplicate source-model graph and constructs engine-native objects directly from cache chunks.

Hot-path allocation rules (per AGENTS.md). Any code added by this feature that runs during render submission, visible collection, fixed update, per-frame update, or `GPUScene` registration must not heap-allocate:

- Read chunk bodies into pooled byte buffers (`ArrayPool<byte>.Shared` or a dedicated pool) and release on hydrate completion.
- Use `Span<T>` / `ReadOnlySpan<T>` over those buffers; do not materialize intermediate arrays.
- No LINQ, no captured closures, no `foreach` over non-struct enumerators.
- Reuse per-thread scratch buffers when expanding CPU `MeshletDescriptor` entries into GPU `Meshlet` structs.

Cold import-time code (the writer, manual reimport, reconcile sweep) may allocate freely.

`XRBase` mutation rule (per AGENTS.md). All reconstructed types that derive from `XRBase` — including [`SubMeshLOD`](XREngine.Runtime.Rendering/Rendering/Models/Meshes/SubMeshLOD.cs#L5-L77), `XRMaterial`, [`XRPrefabSource`](XRENGINE/Scene/Prefabs/XRPrefabSource.cs#L89), `XRMesh`, and `MeshletGenerationSettings` — must be populated via `SetField(...)`, not direct backing-field assignment, so change notification and invalidation behave correctly. Add this to the code review checklist for the loader.

No new compiler warnings. Per AGENTS.md, the cache reader/writer must compile clean. Low-risk warnings in touched files should be fixed in the same change.

## 17. Editor UX

The editor exposes:

- cache status: hit, miss, stale, incompatible, unreadable
- source timestamp and cache timestamp (informational)
- source content hash (truncated)
- cache schema and payload versions
- importer backend + backend version used to write cache
- meshlet and LOD generation summaries
- "Reimport From Source"
- "Delete Cache And Reimport"
- "Open Generated Asset"
- "Reveal Cache File" for diagnostics
- "Reconcile Cache" — sweeps `Cache/` and removes orphan entries whose source file is gone or whose variant key no longer matches any current importer settings (see section 17a).

Do not hide the source path. Artists need to know whether they are looking at source-owned content or generated cache output.

## 17a. Orphan Garbage Collection

Deleting or renaming a source file leaves stale cache entries under `Cache/`. v1 ships a manual reconcile action; automatic GC is out of scope.

The manual reconcile pass:

1. Enumerates model-cache `*.asset` files under `Cache/` and `Cache/Engine/` (skips `Cache/External/` unless explicitly requested — those entries reference paths outside the workspace and may be valid on another machine). Reconcile identifies model caches by cache-tree location and header magic/type, not by a bespoke extension.
2. For each entry, reads the header (no chunk decode required).
3. Marks for deletion if: source file from `sourcePathNormalized` does not exist, or `sourceHash` cannot be matched to a current importable file, or `schemaVersion` / `payloadVersion` is below the current minimum.
4. Prompts before deleting and writes a summary log.

## 18. Logging And Telemetry

Use the assets log category for model cache events:

- `Model.CacheHit`
- `Model.CacheMiss`
- `Model.CacheStale`
- `Model.CacheIncompatible`
- `Model.CacheFallbackToSource`
- `Model.CacheWrite`
- `Model.CacheRead`
- `Model.CacheReadSlow`
- `Model.CacheWriteSlow`
- `Model.CacheManualReimport`
- `Model.CacheMeshletPayloadMissing`
- `Model.CacheLodPayloadMissing`
- `Model.CacheChunkRepaired`
- `Model.CacheOrphanTempSwept`
- `Model.ReimportIdentityBreak`

Every miss/stale/incompatible event carries a `reason` enum value identifying the single check that failed:

```text
CacheRejectReason {
    None,
    FileMissing,
    SchemaVersionMismatch,
    PayloadVersionMismatch,
    WholeFileChecksumMismatch,
    SourceLengthMismatch,
    SourceTimestampMismatch,
    SourceHashMismatch,
    ImporterBackendMismatch,
    ImporterBackendVersionMismatch,
    ImportOptionsHashMismatch,
    MeshOptimizerSettingsHashMismatch,
    MaterialPolicyVersionMismatch,
    RequiredChunkMissing,
    ChunkChecksumMismatch,
    ChunkVersionMismatch,
    Unreadable,
}
```

Include in events:

- source path
- cache path
- `reason`
- source timestamp
- import options timestamp
- payload version
- importer backend
- mesh count
- LOD count
- meshlet count
- read/write milliseconds
- bytes read/written

## 19. Implementation Plan

### Phase 0: Importer Backend Versioning (prerequisite)

- Add a stable `BackendVersion` constant to each third-party importer (Assimp, glTF, FBX, Unity prefab). Treat it as a monotonic integer that the importer owner bumps when output changes.
- Expose the active importer's name + version through a registry the cache layer can query by source extension.
- Add a `meshoptimizer` native-lib version probe; fold its value into the FBX/glTF/Assimp backend versions rather than checking it independently.

### Phase 1: Cache Contract And Variant Key

- Add `schemaVersion`, `payloadVersion`, and `engineVersion` constants. Document the bump rules from section 5.
- Keep `TryResolveCachePath` on the existing cooked `.asset` cache convention; model caches use the same extension as texture caches.
- Add `ResolveDefaultCacheVariantKey` branch for model asset types returning `Models/v<schemaVersion>/importer_<backend-key>/opts_<8-hex-hash>` or the equivalent existing cache-variant directory shape.
- Add source-contract tests for cache path selection (game/engine/External cases) and freshness.
- Add model cache log events as constants.

### Phase 2: Binary Manifest

- Define the header and chunk table.
- Write manifest-only cache files from successful imports.
- Validate stale/incompatible cache rejection.
- Add manual reimport path that skips cache read.

### Phase 3: Mesh And LOD Payloads

- Serialize engine-native `XRMesh` streams.
- Serialize `SubMeshLOD` chains and LOD generation settings.
- Load cached meshes/LODs without opening source model files.
- Add tests for warm-cache LOD reconstruction.

### Phase 4: Meshlet Payloads

- Add a serialized CPU `MeshletDescriptor` distinct from the GPU `Meshlet` struct.
- Extend the GPU `Meshlet` struct ([`Meshlets/Meshlet.cs`](XREngine.Runtime.Rendering/Rendering/Meshlets/Meshlet.cs)) with cone axis + cutoff and cone apex (or compressed equivalent). Coordinate the GPU buffer layout change with every shader, indirect-buffer writer, pool, and allocator that consumes `Meshlet`. Bump `payloadVersion`.
- Serialize CPU meshlet descriptors, vertex-reference indices, triangle-local indices, bounds, cones, and stats.
- Handle the `MeshletGenerationSettings.Enabled == false` case (empty chunks + manifest flag, see section 11).
- Load meshlet payloads into `GPUScene` registration data via the CPU→GPU descriptor expansion.
- Add tests asserting `MeshletGenerator.Build` is **not** called during warm-cache render startup (counter-based assertion).

### Phase 5: Prefab And Sub-Asset Reconstruction

- Reconstruct `XRPrefabSource`, `Model`, `SubMesh`, `XRMesh`, materials, and skeleton references from binary chunks.
- Animation references point to the animation cache outputs; this phase does not reconstruct clip data.
- Preserve externalized project asset paths and references.
- Keep remap dictionaries seeded and stable.
- Validate prefab structural equality against a fresh source import (see §20).

### Phase 6: Partial Hydration And Performance

- Read mesh/meshlet blobs on demand.
- Add cache read timing telemetry.
- Validate warm model load avoids third-party parser DLLs for cached formats.
- Benchmark cold import, warm cache load, manual reimport, and stale cache repair.

### Phase 7: Hardening

- Atomic write/replace with same-directory tmp.
- Orphan `.tmp` sweep on startup.
- Per-cache-path writer mutex.
- Corruption tests (truncated file, flipped bits in preamble, chunk, and string pool).
- Interrupted-write tests.
- Source timestamp edge cases (backwards-moving timestamps, FAT 2-second granularity, network share skew).
- Symlinked source paths.
- Windows case-difference source paths.
- Import option change invalidation.
- Cross-version rejection.
- Per-chunk repair flows (`LodTables`, `Meshlets`).
- Read-only filesystem read path.
- Large model memory-pressure validation.
- Deterministic-output validation (bit-identical cache from identical inputs across two clean imports).

## 20. Test Plan

Unit/source-contract tests:

- `ModelCache_VariantKey_IncludesImporterMeshletAndLodSettings`
- `ModelCache_FreshCache_LoadsInsteadOfSourceImport`
- `ModelCache_EngineVersionChange_DoesNotInvalidateCache`
- `ModelCache_SchemaVersionChange_InvalidatesCache`
- `ModelCache_PayloadVersionChange_InvalidatesCache`
- `ModelCache_SourceNewer_FallsBackToSource`
- `ModelCache_SourceTimestampBackwards_TriggersContentHash`
- `ModelCache_ImportOptionsNewer_FallsBackToSource`
- `ModelCache_ManualReimport_SkipsCacheReadAndReplacesCache`
- `ModelCache_ManualReimport_PreservesGuidsForMatchingEntities`
- `ModelCache_ManualReimport_LogsIdentityBreakForRenamedEntities`
- `ModelCache_UnreadableCache_DeletesOrQuarantinesAndImportsSource`
- `ModelCache_PreambleRejectsWrongSchemaVersion`
- `ModelCache_PreambleRejectsWrongPayloadVersion`
- `ModelCache_WholeFileChecksumMismatch_RejectsCache`
- `ModelCache_ChunkChecksumMismatch_TriggersPerChunkRepairOrFallback`
- `ModelCache_MeshPayload_ReconstructsXRMeshStreams`
- `ModelCache_LodPayload_ReconstructsSubMeshLods`
- `ModelCache_MeshletPayload_ReconstructsDescriptorsAndConeData`
- `ModelCache_MorphTargets_RoundTripDeltas`
- `ModelCache_Skeletons_RoundTripBindPose`
- `ModelCache_TextureRemaps_PreserveUserAssignments`
- `ModelCache_MaterialRemaps_PreserveUserAssignments`
- `ModelCache_DoesNotWriteAnimationData`
- `ModelCache_DoesNotWriteIntoTextureCacheRoot`
- `ModelCache_DeterministicOutput_TwoCleanImportsByteIdentical`
- `ModelCache_StringPoolOffsets_StableAcrossRebuilds`

Resilience tests:

- `ModelCache_TruncatedFile_RejectsAndFallsBack`
- `ModelCache_OrphanTempFile_SweptOnStartup`
- `ModelCache_OrphanTempFile_NotPreferredOverValidCache`
- `ModelCache_ConcurrentWriters_SerializeViaMutex`
- `ModelCache_ReadOnlyFilesystem_WarmReadSucceeds`
- `ModelCache_ReadOnlyFilesystem_WriteFailureDoesNotAbortLoad`
- `ModelCache_SymlinkedSource_ResolvesAndCachesCorrectly`
- `ModelCache_WindowsCaseDifferentSourcePath_MapsToSameCache`
- `ModelCache_FATTimestampGranularity_NotFalsePositiveStale`
- `ModelCache_CrossVolumeTempRejected_TempStaysAdjacentToDestination`

Integration tests:

- cold FBX import writes cooked `.asset` cache
- warm FBX load reads cooked `.asset` cache and does not parse source
- cold glTF/GLB import writes cooked `.asset` cache
- warm glTF/GLB load reads cooked `.asset` cache and does not parse source
- cache with stale meshlet chunk regenerates meshlets in-process without source parse
- cache with stale LOD chunk regenerates LODs in-process without source parse
- forced reimport preserves generated asset graph on failure
- prefab reconstructed from cache is structurally equal to prefab from a fresh source import (modulo recorded timestamp/UUID fields)

Performance validation:

- large static model warm-cache load time
- B1 startup with fresh cache versus no cache
- cache read allocations
- cache write time
- meshlet generation time moved out of render startup

## 21. Acceptance Criteria

- Fresh model caches are preferred over original third-party source paths during normal loads.
- Manual reimport forces source parse and atomically replaces cache.
- Manual reimport preserves GUIDs for matched entities and logs/surfaces identity breaks for unmatched ones.
- Cache freshness accounts for source, import options, importer backend, LOD settings, meshlet settings, and `schemaVersion`/`payloadVersion`. Engine version is recorded but does not invalidate caches.
- Warm-cache loads reconstruct model hierarchy, meshes, LODs, meshlets, morph targets, and skeletons without opening the original model file.
- Meshlet cone data is available to `GPUScene` for task-shader cone culling.
- Stale, incompatible, partial, or unreadable caches fall back to source import with clear diagnostics carrying a single `CacheRejectReason`.
- Stale `LodTables`/`Meshlets` chunks are repaired in-process from valid mesh chunks without falling back to source import.
- Cache writes are atomic on Windows: temp files live adjacent to their destination, are validated before swap, and orphans are swept on startup.
- Cache reads succeed on read-only filesystems; failed repair writes do not abort the load.
- Identical source + settings + backend version produce byte-identical cache files (modulo a defined small set of recorded fields). This is verified by test.
- Animation clip data is never written by the model cache; texture payload bytes are never written into the texture cache by the model importer.
- Existing texture cache behavior remains separate and unchanged.
