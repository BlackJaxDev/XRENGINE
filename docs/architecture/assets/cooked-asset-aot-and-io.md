# Cooked Asset Serialization — AOT & I/O Design Decisions

[Architecture index](../README.md)

> Status: **Implemented** (AOT type references, registry-backed runtime cooked assets, long-lived archive handles, payload leases, fixed envelope); **Deferred** (DirectStorage asset packer reads).

## 1. Problem Statement

Published (cooked) builds need to deserialize assets without relying on runtime
reflection to resolve types. The engine targets native AOT compilation and IL
trimming, which strip metadata that `Type.GetType()` depends on. At the same
time, the asset packer's I/O layer needs a clear design point for when (and
whether) to integrate DirectStorage.

This document records the decisions made, alternatives considered, and rationale
for the current design.

## 2. Subsystem Overview

```
┌──────────────────────────────────────────────────────────┐
│                   Build Pipeline (Editor)                 │
│                                                          │
│  CookContent()                                           │
│    ├─ YAML .asset files → CookedBinarySerializer         │
│    │    → CookedAssetBlob (AQN string type ref)          │
│    │    → AssetPacker.Pack() → content.archive            │
│    │                                                     │
│  GenerateConfigArchive()                                 │
│    ├─ GameStartupSettings ─┐                             │
│    ├─ EditorPreferences  ──┼─ CookedAssetBlob            │
│    ├─ UserSettings ────────┘    (AOT index when native)  │
│    ├─ AotRuntimeMetadata.bin                             │
│    └─ AssetPacker.Pack() → config.archive                │
└──────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────┐
│                Published Runtime (Client)                 │
│                                                          │
│  AotRuntimeMetadataStore                                 │
│    ← loads AotRuntimeMetadata.bin from config.archive    │
│                                                          │
│  CookedAssetReader.LoadAsset()                           │
│    ← reads CookedAssetBlob from archive                  │
│    ← CookedAssetTypeReference.Resolve() resolves type    │
│    ← runtime format dispatch chooses registry-backed     │
│       RuntimeBinaryV1; non-AOT generic BinaryV1/V2       │
│                                                          │
│  CookedBinarySerializer (MemoryPack path)                │
│    └─ XRAssetMemoryPackAdapter.Deserialize()             │
│       ← CookedAssetTypeReference.Resolve() resolves type │
│       ← CookedBinarySerializer.Deserialize() hydrates    │
└──────────────────────────────────────────────────────────┘
```

### Key types

| Type | Location | Role |
|------|----------|------|
| `CookedAssetTypeReference` | `Core/Files/CookedAssetTypeReference.cs` | Shared encode/resolve for type references in cooked envelopes |
| `CookedAssetEnvelope` | `Core/Files/CookedAssetEnvelope.cs` | Fixed binary envelope: magic, version, format, type reference, payload offset and length |
| `CookedAssetBlob` | `Core/Files/CookedAssetBlob.cs` | Authoring-side value (type reference, format, payload) written through the envelope during cooking |
| `PublishedArchiveHandle` | `Core/Files/AssetPacker/PublishedArchiveHandle.cs` | Long-lived mapped view of one archive with parsed tables and thread-safe lookups |
| `PublishedArchiveRegistry` | `Core/Files/AssetPacker/PublishedArchiveRegistry.cs` | One open handle per archive; maps the config, game content, and common-assets roots |
| `CookedPayloadLease` / `CookedPayloadOwner` | `Core/Files/CookedPayload/` | Stack-confined and heap-owned access to payload bytes without transient arrays |
| `PublishedCookedAssetReader` | `Core/Files/PublishedCookedAssetReader.cs` | Reflection-free reader for `RuntimeBinaryV1`; the only reader a published runtime uses |
| `PublishedCookedAssetRegistry` | `Core/Files/PublishedCookedAssetRegistry.cs` | Explicit registry of published runtime asset serializers used by shipped builds |
| `XRAssetMemoryPackEnvelope` | `Core/Files/XRAsset.MemoryPack.cs` | Inner envelope used when CookedBinarySerializer falls through to MemoryPack for `XRAsset` subclasses |
| `AotRuntimeMetadata` | `Core/Engine/AotRuntimeMetadata.cs` | MemoryPack-serializable table of all known types, redirects, replication info |
| `AotRuntimeMetadataStore` | `Core/Engine/AotRuntimeMetadataStore.cs` | Lazy-loaded singleton that reads `AotRuntimeMetadata.bin` from the config archive |
| `CookedBinarySerializer` | `Core/Files/CookedBinary/CookedBinarySerializer.cs` | Module-dispatched binary serializer for the engine's general native wire format |
| `RuntimeCookedBinarySerializer` | `XREngine.Data/Core/Files/RuntimeCookedBinarySerializer.cs` | Explicit runtime serializer used by registered published asset types |
| `AssetPacker` | `Core/Files/AssetPacker/AssetPacker.cs` | Archive pack/repack/compact/read for cooked content and config |
| `DirectStorageIO` | `Core/Files/DirectStorageIO.cs` | Windows DirectStorage abstraction for CPU and GPU I/O |

## 3. AOT Type Reference Encoding

### Decision

Cooked asset envelopes store a **type reference string** that encodes the
runtime type of the serialized object. Two formats exist:

| Format | Example | When used |
|--------|---------|-----------|
| Assembly-qualified name | `"XREngine.GameStartupSettings, XRENGINE, ..."` | Default; always works |
| AOT index | `"aot:42"` | Native AOT builds, config archive blobs only |

`CookedAssetTypeReference.Encode(Type, AotRuntimeMetadata?)` produces the
reference. When an `AotRuntimeMetadata` is passed and the type is found in the
known-types table, it emits the compact `"aot:{index}"` form. Otherwise it falls
back to the assembly-qualified name.

`CookedAssetTypeReference.Resolve(string?, Type?)` decodes either form. The
`"aot:"` prefix triggers integer-indexed lookup in `AotRuntimeMetadataStore`;
plain strings go through the existing `ResolveType(string)` path which tries
`Type.GetType()` first, then searches the metadata table by name or
short-name match.

### Why only config blobs use AOT indices

The build pipeline runs in this order:

1. **`CookContent()`** — iterates all `.asset` files, serializes each into a
   `CookedAssetBlob`, and packs them into `content.archive`.
2. **`GenerateConfigArchive()`** — serializes startup settings, builds AOT
   metadata, and packs everything into `config.archive`.

AOT metadata is generated in step 2 because it requires scanning all loaded
assemblies (including the compiled game assembly from step 1). Content blobs are
written in step 1, before metadata exists, so they use assembly-qualified name
strings.

Runtime-cooked published assets are now distinguished by `CookedAssetFormat.RuntimeBinaryV1`.
The build pipeline selects that format only for types registered in `PublishedCookedAssetRegistry`
(currently explicit runtime-rendering assets such as `XRMesh`, `XRTexture2D`, and `SubMesh`, plus bounded animation assets such as
`AnimationClip`, `BlendTree1D`, `BlendTree2D`, `BlendTreeDirect`, and `AnimStateMachine`).
Published AOT runtime validates those types against `AotRuntimeMetadata.PublishedRuntimeAssetTypeNames`
before dispatching to the registered runtime serializer.

Generic editor/dev cooks now use `BinaryV2`. Reference definitions and uses in
the payload retain one object for repeated references in an authored graph,
including inline pipeline assets shared by cameras. The reader still accepts
legacy `BinaryV1` for ordinary desktop compatibility, but those older bytes
cannot recover aliases already duplicated during cooking. Browser world/scene
cook audits and runtime catalog reads require the new generic format and report
that such an older derived cache must be recooked. Explicit registered
`RuntimeBinaryV1` serializers retain their own formats and reference contracts;
this version change does not rewrite source YAML or existing user cache files.

Within a generic payload, a reference definition writes a marker and 32-bit
index before the original value marker; a later use writes a reference marker
and that index. Indices follow object identity in traversal order, never asset
IDs. Reflected objects and collections register before reading their children,
so supported cycles resolve. Opaque custom serializers and MemoryPack envelopes
remain independent payload scopes and own any identity inside them.

Browser content cooking uses the same registered serializer for payload bytes. A serializer owner can also
register a dependency declaration that lists the separate `.asset` files referenced by those bytes.
Declarations must use the same portable `game://`, `engine://`, `/game/`, or `/engine/` identity
that the serializer places in its payload. The cooker does not rewrite serializer-owned bytes.
An explicitly empty declaration means the serializer embeds its complete graph; an absent declaration
means browser cooking fails instead of silently omitting dependencies. The browser cooker resolves each
declaration under the game or engine asset root, recursively cooks it under a `/game/` or `/engine/`
catalog identity, and rejects missing files, unsupported asset types, cycles, and dependencies outside
those roots. The dependency declaration is catalog metadata and does not change the cooked payload format.
The canonical generic cooked serializer embeds object-valued members; browser cooking admits the exact
base `XRWorld` through that path and separately declares its skybox file setting. Game-defined world
serializers must declare any external file references they emit. Path-valued references in world payloads
must already be portable, since packaging cannot rewrite a serializer-owned payload safely.

The current policy boundary is deliberate: only assets with explicit bounded payloads are registered.
Custom handlers that still depend on open-ended reflection or unrestricted runtime type activation remain on the generic
editor/dev cooked-binary path until they gain an explicit published-runtime serializer.

### Alternative considered: generate metadata before content

We considered reordering the pipeline to build metadata first, allowing all
cooked blobs (content + config) to use `"aot:N"` indices. This was rejected:

- **Negligible size savings.** An AQN string is ~100 bytes in a blob that is
  10–100 KB. Compression in the archive further reduces the delta.
- **Fragile coupling.** Content blobs would depend on a separate metadata index
  table. Partial rebuilds, cache corruption, or archive version skew would
  produce inscrutable type-resolution failures. AQN strings are self-describing.
- **Inconsistent encoding.** AOT metadata is only built when
  `PublishLauncherAsNativeAot` is true. Non-AOT published builds would still use
  AQN strings, creating two code paths to test for content deserialization.
- **Pipeline parallelization.** A future improvement is to cook content in
  parallel. Depending on a single pre-computed metadata table adds a
  serialization point.

### Resolution fallback chain

When `Resolve()` is called at runtime:

1. **`"aot:"` prefix** → parse index → `AotRuntimeMetadataStore.ResolveType(int)`
2. **AQN string** → `XRTypeRedirectRegistry.RewriteTypeName()` (handle renames)
   → `AotRuntimeMetadataStore.ResolveType(string)` (metadata table lookup)
   → if not AOT build: `AppDomain.CurrentDomain.GetAssemblies()` scan
3. **Fallback** → return `expectedType` if provided, else `null`

The `expectedType` parameter lets callers like `LoadAsset<T>()` succeed even
when the stored type reference is stale or unresolvable, as long as the payload
is compatible.

## 4. DirectStorage Integration

### Decision: keep DirectStorage at the GPU upload layer only

DirectStorage (`Core/Files/DirectStorageIO.cs`) is currently used for:

- Vulkan buffer staging (`VkDataBuffer`)
- Vulkan texture staging (`VkImageBackedTexture`)
- CPU-side texture loading (`XRTexture2D`)

It is **not** used by `CookedBinarySerializer` or `AssetPacker`.

### Alternative considered: DirectStorage-backed archive reads

We considered adding `DirectStorageIO.ReadRange()` to the asset packer's
`GetAsset()` path for zero-copy ranged reads from archive files. This was
deferred:

- **Wrong bottleneck.** Asset loading is dominated by decompression (LZ4/Zstd)
  and deserialization (CookedBinarySerializer field-by-field hydration), not
  raw I/O. NVMe sequential reads already saturate the pipeline.
- **CPU destination.** The packer delivers bytes to the CPU for deserialization.
  DirectStorage's value is bypassing the CPU entirely for GPU uploads. Reading
  into managed `byte[]` for CPU processing negates its advantage.
- **Platform constraint.** DirectStorage requires Windows 10 21H2+ with
  compatible NVMe drivers. Adding it to the generic packer read path introduces
  a mandatory fallback path and testing matrix.
- **Premature optimization.** No profiling data shows I/O as a bottleneck in
  asset loading. The optimization should be data-driven.

### When to revisit

DirectStorage integration at the packer/loader level becomes worthwhile if:

1. **GPU-direct asset loading** is added (e.g., loading compressed textures
   directly from archive → GPU memory without CPU staging).
2. **Profiling data** shows I/O wait as a significant fraction of total
   asset load time.
3. **Batch loading** patterns emerge where dozens of assets are needed
   simultaneously (DirectStorage's `ReadBatch` shines here).

The correct integration point would be a new `AssetPacker.GetAssetGpuDirect()`
API that returns a DirectStorage fence/future rather than a `byte[]`, usable
by the Vulkan/DX12 texture upload path.

## 5. File Layout

```
Core/Files/
├── AssetPacker/
│   ├── AssetPacker.cs              # Pack/Repack/Compact/GetAsset
│   ├── ArchiveInfo.cs
│   ├── ArchiveEntryInfo.cs
│   ├── FooterInfo.cs
│   ├── StringCompressor.cs
│   └── TocEntryData.cs
├── CookedBinary/
│   ├── CookedBinarySerializer.cs   # Core read/write/size
│   ├── CookedBinarySerializer.Schema.cs
│   ├── CookedBinarySharedValueTracker.cs  # Write/size/schema side of shared values
│   ├── CookedBinarySharedValueTable.cs    # Read side of shared values
│   ├── CookedBinaryTypeMarker.cs
│   ├── IPostCookedBinaryDeserialize.cs
│   └── Modules/
│       ├── Core/       (Registry, Primitive, ByteArray, DataSource, ...)
│       ├── Collections/ (Array, Dictionary, HashSet, List)
│       └── Custom/     (AnimationClip, BlendTree, AnimStateMachine, ...)
├── CookedAssetBlob.cs              # Outer envelope (MemoryPack)
├── CookedAssetTypeReference.cs     # Encode/Resolve shared helper
├── DirectStorageIO.cs              # Windows DirectStorage abstraction
└── XRAsset.MemoryPack.cs           # Inner MemoryPack envelope for XRAsset
```

## Runtime catalog object ownership

A bound runtime catalog owns the engine objects materialized by its synchronous
asset-deserialization transactions. Each transaction retains the exact allocation
ledger from `ObjectCachePublicationScope`, including nested render-object batches
and constructor defaults that deserialization later replaces. Successful publication
transfers that ledger to an `ObjectCacheOwnership` lease. Ownership commits omit
fully destroyed temporary allocations; ordinary publication remains strict and rejects
destroyed members. A destruction exception for an enlisted object aborts its owning
transaction, including exceptions from a terminal destruction observer. A failed, duplicate, cancelled,
or stale load aborts its unpublished batch; a post-publication failure releases its
whole lease rather than only the asset root.

External dependencies are loaded before the referring asset's transaction and remain
shared for the source's lifetime. References to objects or assets cached before the
source loaded them are borrowed. Source teardown removes only its own cached roots
and destroys only its recorded allocations after world teardown; it does not make
`RuntimeWorld.Dispose` recursively destroy reusable authored worlds, and global
object discovery remains a strong registry.

Publication scopes are synchronous and thread-affine. Ordinary nested scopes join
their parent's batch. An independent scope owns a separate batch and restores the
enclosing scope on completion. Process-lifetime lazy factories, such as shared UI
quad geometry and the render window registry, use independent publication without
transferring an ownership lease, so the first requesting catalog cannot claim them.
A factory that publishes shared state must establish that independent lifetime
boundary rather than attach process resources to a transient construction batch.

Browser startup also owns its newly constructed settings projections, world-host
objects, and viewport allocations through bounded synchronous batches. Shutdown
first quiesces gameplay and rendering, restores borrowed engine settings, and then
releases these session batches. Destruction is immediate at the stopped ownership
boundary, including draining queued child destruction. Failed owned destruction
retains its ledger and source/session services for a later stop retry; a replacement
source cannot be admitted while that ownership remains.

## 6. Shared Values

Generic BinaryV2 serialization without a sharing callback preserves object
identity with graph definitions and references (markers 62/63 with fixed-width
identities). Supported cyclic references are registered before their contents
are read. A serialization that sets
`CookedBinarySerializationCallbacks.ShareReference` writes each selected
value once: its first complete occurrence as a `SharedDefinition` (marker 64,
a 7-bit identity, then the value in its own encoding) and later occurrences as
a `SharedReference` (marker 65 and the identity). An occurrence met while the
value itself is still being written, a cycle, is written by value as before.
The size pass and the schema inspector make the same decisions as the writer,
and the reader registers each definition after reading it; a reference to an
unknown identity fails the read, and a definition met where its occurrence is
skipped is still read in full. The option applies to `Serialize`,
`CalculateSize` and `Deserialize` alike; custom serializers write and size
their payloads through independent callback-free scopes. They do not use the
enclosing snapshot's selected-sharing table. Cooked
assets on disk do not use it; play-mode snapshots do.

## Portable Source Paths

A cook must not ship a machine path. The content cook therefore writes the
source paths of cooked assets at write time, without changing any object:

- `ProjectBuilder.CreateCookedBlobFromYaml` opens a cook scope
  (`CookedSourcePathProjection.Begin`) around the serialization of each asset
  file. Inside the scope, the asset-header step of each cooked serializer
  writes `XRAsset.OriginalPath` as a `game://` or `engine://` reference
  (`CookedSourcePathProjection.ProjectOriginalPath`) and writes no
  `XRAsset.FilePath` (`ProjectFilePath`). The steps are
  `RuntimeCookedBinarySerializer.WriteBaseObject` and
  `CalculateBaseObjectSize`, the reflective `OriginalPath` member of
  `CookedBinarySerializer` (`MemberMetadata.GetSerializedValue`, for the size,
  write, and schema passes), the `SubMeshCookedCodec` header, and the serialized
  models of animation clips, blend trees, and state machines. The size pass and
  the write pass use the same projection, and the scope caches each result, so
  the sizes and the bytes agree.
- The projection covers every asset that the cook writes into the bytes: the
  root, embedded assets, the items of a material texture list, a texture that a
  source image backs, and live assets that the copy references in full (for
  example a material `.asset` that a prefab references by `{ID, Path}`). Live
  editor assets do not change.
- The cooked `FilePath` is empty. Do not write it as `game://`: the
  `FilePath` setter calls `Path.GetFullPath`, and every reader treats it as a
  file path. At runtime, `ImportedTextureStreamingManager.RegisterTexture`
  sets an empty `FilePath` from the resolved `OriginalPath`, so the streaming
  record points at a file that exists.
- An absolute source path outside every asset root is cleared: the cooked
  bytes hold no value for it, and the publish lists the asset file, the asset,
  and the path in its warning. The cook continues. A relative value (not a
  machine path) is written as it is.
- After the cook, `CookedOutputPathGuard` strips machine paths from the cooked
  copy in the build output. It never changes a source file. It finds the asset
  roots and the user profile folder of the cook process (with both separator
  forms and the escaped form), drive-letter paths, user profile paths, and UNC
  paths, as UTF-8 and UTF-16 text. It overwrites the folder part of each path
  with underscores and keeps every separator and the file name, for example
  `C:\Users\alice\Proj\wood.png` becomes `__\_____\_____\____\wood.png`.
  The length stays the same, so the file layout and every length prefix stay
  valid, and an importer can still find a texture by its file name (the FBX
  importer tries the file name in the model folder and its `textures` folder).
  The folder part always covers the user name of a user profile path. A leading
  UNC backslash run becomes underscores too. The strip records each full path
  in the publish warning. A separator can be a slash, a
  backslash, or an escaped backslash (two backslashes, as in JSON). A path part
  can hold non-ASCII characters: in UTF-8 each must be a complete UTF-8
  sequence, and in UTF-16 any character except a control character, a reserved
  path character, or a lone surrogate counts.
- The rules depend on the file (`EMachinePathScanProfile`):
  - A cooked asset file or `lod/` entry: drive paths with three or more parts,
    user profile paths, and UNC paths.
  - A copied text file (a byte order mark, or a first 8 KiB with no zero byte
    and valid UTF-8): drive paths with one or more parts (for example
    `E:\Textures\wood.png` in an `.mtl` file) and UNC paths. A drive letter must
    not follow a letter or a digit. A UNC path must not follow a path character
    or a backslash, so an escaped relative path such as
    `Textures\\Wall\\Brick\\albedo.png` is not a server name.
  - A copied binary file: user profile paths, and file paths with two or more
    folder parts, one separator style, plain name characters, and a file name
    with an extension of two to five characters that starts with a letter. Raw
    pixel and sample data (TGA, RLE HDR) holds long printable runs with many
    backslashes, and these rules do not match them. A drive path with fewer than
    two folder parts in binary media is not found.
- The path range: in text, it stops before a quote, a comma, a semicolon, an
  equals sign, or a closing brace. A backslash run of odd length before a quote
  ends in an escape (for example `\"` in JSON), so it is not part of the path.
  In a text range with escaped separators (`\\`), only backslash pairs are
  separators: an odd backslash is an escape and stays, a `\u`, `\x`, or `\U`
  escape gets the code of the underscore, and another escaped character stays.
  So JSON and double-quoted YAML stay valid. In binary data, the folder part
  always ends inside the matched path and inside the path token (the leading
  separators and plain name characters), so the strip never writes past the
  file name. A 32-bit length before the path (FBX string properties, EXR
  attributes, MemoryPack strings, RIFF chunk sizes) gives only the report text:
  such a string can hold more lines or binary fields, and a false prefix can
  cover unrelated data. A PNG file gets new checksums for the chunks that
  change.
- Compressed content of a known container that can hold text is inflated and
  scanned: PNG `zTXt` chunks and `iTXt` chunks with the compression flag, every
  ZIP entry, and GZIP data. A path there fails the publish, because the strip
  cannot rewrite it in place, and the error names the file and the place. A
  container that the guard cannot inflate (7z, RAR, XZ, Zstandard, BZip2, a ZIP
  entry with LZMA, BZip2, Zstandard, or encryption, or damaged data) fails the
  publish too. The BZip2 signature also needs the block-size digit and the block
  magic, so a text file that starts with `BZh` is not a container. The
  inflation has limits (`CompressedInspectionBudget`): 2 GiB and 2 minutes for
  each item, and 4 GiB and 10 minutes for each file. Past a limit, the publish
  fails and names the file and the limit. So a path in compressed data never
  passes silently, and a decompression bomb cannot stall a publish. Nested
  containers (a ZIP inside a ZIP) are not opened.
- A path that cannot be stripped safely fails the publish, and the error names
  the file: a visible path in a compressed or checksummed container, a path in
  a critical PNG chunk, or a path that is still found after the strip.
- The strip covers every file that a publish ships: the cooked asset files, the
  `lod/` entries, and the copied non-asset files of the full and the
  incremental content cook; every file of `CommonAssets.pak` (the full pack
  scans the engine asset folder, writes a stripped copy of each file with a hit
  to the intermediate folder, and packs the copy under the source path and
  timestamp through `AssetPacker.Pack(..., replacementFiles)`; the incremental
  repack strips each changed file; the runtime shader pack strips its staging
  copy); the config archive and the browser cooks (`WriteGuardedCookedAsset`);
  and on the browser route the staged content sources, the font license
  notices, and the native world of a shared world package.
- An incremental archive build records the strip version in
  `<intermediate>/Build/<delta folder>.strip-ledger`, with the archive length
  and timestamp. When the record is missing, has another version
  (`CookedOutputPathGuard.StripVersion`), or does not match the archive, the
  build packs the archive again in full, so every entry passes the current
  strip once. The record also lists the stripped paths of the archive entries.
  A repack keeps the earlier paths of the entries that it does not write again
  and lists them in the warning as "stripped in an earlier build", also when
  the build changes nothing.
- The publish writes one warning that lists each file and each removed path or
  secret (`PublishPathRedactionLog`). It goes to the engine log, to standard
  error, and to the trace listeners, so a Release editor shows it, and the
  completion text of the build job names the report. The same list goes to
  `<intermediate>/Build/Logs/publish-report.txt`. The intermediate folder is
  never part of a publish output tree, because the report holds the removed
  paths. Each build deletes the old report first, and a build that removes
  nothing writes a report that says so.
- The guard reads each file in 4 MiB chunks that overlap by 4 KiB, so a file of
  any size is checked with a fixed buffer.
- A cook does not ship YAML sources. An asset file with no `__assetType` or
  `__type` hint, or an asset whose cook fails, fails the content cook with an
  error that names the file. A YAML source can hold machine paths inside
  compressed payloads (for example the `CookedBinary` texture envelopes) that
  the guard cannot read, so the cook does not copy it.
- Before the serialization, `CookedCopyGraph.ReleaseLiveAssets` refreshes the
  asset graph of the deserialized copy with live assets as foreign, so the copy
  does not claim a live editor asset. A refresh failure fails the cook.
- Code that reads a cooked `OriginalPath` for a file system call resolves it
  first with `AssetReferencePath.TryResolveFileSystemPath`: the texture
  streaming restore, the texture cache fallback, the texture streaming cache
  codec, and the font layout recovery (`FontGlyphSet`).
- The config archive (`ProjectBuilder.WriteConfigArchiveAssets`) and the
  browser dependency and font cooks write each asset through
  `WriteGuardedCookedAsset`: the cook scope, the list of cleared items, and the
  strip.
  The settings assets use registered MemoryPack codecs that do not pass through
  the projection, so `PublishedConfigAssets` writes a MemoryPack copy of each
  settings asset with no `FilePath` and a projected `OriginalPath`. The live
  settings do not change. An absolute `GameStartupSettings.TexturesFolder` is
  cleared in the copy and listed in the warning.
- A published archive holds no secret. A secret is a settings member marked
  with `[Secret]` (`SecretAttribute`). Its environment variable companion
  (`<Name>EnvVar`) is cleared with it. One step, `PublishedSecrets.Strip`,
  clears them on every copy that a publish writes: the config archive copies,
  the deserialized copy of each content asset (full and incremental), and the
  browser startup settings. The MemoryPack codecs write the public plaintext
  members, so the strip must run before the write. The marked members are the
  MCP token and the four assistant API keys of `EditorPreferences` (also
  `[Password]` for the UI), and `MultiplayerSessionToken`,
  `MultiplayerAdmissionSecret`, `MultiplayerAccountId`,
  `MultiplayerReservationId`, and `MultiplayerClientId` of
  `GameStartupSettings`. `EditorPreferencesOverrides.McpServerAuthTokenOverride`
  is marked too; no route ships the overrides asset today. Mark each new secret
  member with `[Secret]`.
- The strip clears only top-level secrets. `PublishedSecrets.ThrowIfNestedSecret`
  walks the declared types of the serialized members of the asset type (YAML
  members, and MemoryPack members of a MemoryPack type). If it reaches a
  `[Secret]` member of a nested type, the publish fails and names the member
  and the path to it. The walk enters the element type of arrays and the
  generic arguments of every generic type (also engine collections such as
  `EventList<T>`). It follows declared types only, so it does not see a secret
  on a derived type that a base-typed member holds at run time.
- At write time, inside a cook scope, the reflective cooked serializer writes
  every `[Secret]` member that has a value as its default value
  (`CookedSourcePathProjection.ProjectSecretValue`) and lists it in the publish
  warning. This covers a secret on any nested object, list item, or derived
  type that the serializer writes, and it does not change the object. The
  registered MemoryPack codecs do not pass through this step; the settings
  assets that use them are stripped as a copy.
- The content cook strips only a copy. If the YAML load gives the live editor
  instance of an asset whose type has secrets, the cook fails, because a strip
  would clear the live secrets and a write without the strip would ship them.
- `MultiplayerAdmissionSecret` also has `[YamlIgnore]`: it stays in memory only.
  A launched game gets it from the realtime join handoff (see the networking
  guide).

## Mesh LOD Payloads

`SubMesh` has a registered `RuntimeBinaryV1` codec, `SubMeshCookedCodec`, with its own codec version.
Version 2 holds three parts:

1. The sub-mesh fields that do not depend on a LOD: the asset ID, name, and original path, the imported entity
   identity, the bounds and culling bounds, the root bone and root transform (as serialized reference IDs and names,
   the same form that a cooked mesh uses for its bones), and the GPU BVH flags. The mesh optimizer settings and the
   LOD materials keep the generic cooked encoding of the older payload, in one block, so a material that more than one
   LOD uses stays one object.
2. A LOD table with one descriptor for each LOD (`SubMeshLodDescriptor`): the distance and projected-radius values,
   the flags (auto generated, inline, skinning, blendshapes, blendshapes stripped), the generation values, the bounds,
   the vertex, index, bone, and blendshape counts, the bone set hash and the blendshape name hash (xxHash3-64), the
   content key, the payload length, the payload checksum (xxHash3-64), the mesh codec version, a reserved zero, the
   stable sort key, and the material index.
3. The payload of the coarsest LOD, inline.

Each other LOD is a separate archive entry `lod/<32 lowercase hex digits of the content key>` with a normal cooked
`XRMesh` envelope (`RuntimeBinaryV1`), so its meshlet payload moves with it. The content key is the first 128 bits of
SHA-256 over the mesh codec version (32-bit little endian) and the entry bytes. Only the cook computes it. Equal
payloads have one entry. A sub-mesh with one LOD writes it inline and has no separate entry.

The writer puts the separate entries in the `SubMeshLodPayloadCookSink` of the thread. `ProjectBuilder` opens a sink on
the cooked content root, and on each staging folder of an incremental repack. The sink records the `lod/` entries that
each cooked file refers to (`RecordReference`, `TakeReferencedEntries`). A staged full pack of an archive writes these
records to a ledger next to the delta folder (`SubMeshLodEntryLedger`, `<intermediate>/Build/<delta folder>.lod-entries.txt`).
An incremental repack updates the ledger for the files that it cooks again or removes, and removes each `lod/` entry
that no file in the ledger refers to. The ledger holds the write time of its archive. If the ledger is missing or
describes another version of the archive, the repack keeps all `lod/` entries until the next full pack. With no sink open, the registered codec declines a sub-mesh with more than one LOD
(`SubMeshCookedCodec.Accepts`, through the accept predicate of `PublishedCookedAssetRegistry.Register`), so the cook
writes it with the generic cooked serializer with every LOD inline, the same fallback as a sub-mesh inside a model. A
browser cook, which opens no sink, takes this path; `PublishedCookedAssetRegistry.Accepts` tells such cookers that the
generic walk applies. The codec declares an empty dependency set, because its payload embeds the materials and meshes.
A cook of a LOD that is a separate payload and not resident (a sub-mesh from a `ProxyFirst` load) fails with an error that
names the sub-mesh, because the bytes are not in memory. A sink keeps the description of each mesh that it met (weakly),
so a mesh is serialized one time even though a generic stream has a size pass and a write.
`ProjectBuilder.TryWriteCookedAssetFile` reports a cook failure as an error for an asset whose cook needs the split
sub-mesh format: a sub-mesh, a model, or a world whose loaded graph has a sub-mesh. Any other asset keeps the older
fallback to its YAML source, with one warning that names the asset.

### Sub-meshes in a generic stream

A `Model` (and any other generic payload) writes its sub-meshes as nested reflected objects. The generic serializer calls
the nested-object hook `ICookedBinaryWriteScope.BeginCookedBinaryWrite` before it reads the members of an object for a
size pass, a write, or a schema. While a `SubMeshLodPayloadCookSink` is open, the `SubMesh` hook gives a split view of its
LODs: a copy of each LOD with its descriptor (`SubMeshLOD` carries it through the private `CookedBinaryDescriptor`
member), the coarsest LOD with its mesh inline, and each other LOD with no mesh; those meshes go to the sink as
`lod/<key>` entries. The copies keep their material references, so a material that more than one LOD or sub-mesh uses is
one object of the reference table of the enclosing stream, as before. A sub-mesh that two references of the stream use
is still written one time. With no sink (play-mode snapshots, clones, and the browser cooker), the hook does nothing: the
stream writes the live LODs inline with any descriptors they have, so a LOD that is not resident stays not resident after
a round trip, and the browser cooker's meshlet and material walk sees every LOD mesh and material.

Each nested sub-mesh writes its own version marker, the private `CookedBinaryCodecVersion` member: 2 in a sink scope,
or the current `SubMesh.CookedCodecVersion` in a snapshot (0 for an editor sub-mesh). After the read, a sub-mesh with no
marker is version 1, the older form with all LODs inline. A version 2 sub-mesh follows the load policy for its separate
LODs, as the registered codec does.

NativeAOT: the registered codec and the generic stream both write the LOD materials with the reflective cooked serializer.
A NativeAOT cook therefore rejects a standalone `SubMesh` asset at cook time with an error that names the cause, and it
rejects a `Model` because the model is a generic payload.

The reader follows `SubMeshLodPayloads.LoadPolicy`. The default, `ELodPayloadLoadPolicy.All`, reads each separate entry
during the load, so a normal world load gives every LOD as before. `ProxyFirst` reads only the inline LOD; a host sets it
for its loads with `SubMeshLodPayloads.UseLoadPolicy`. A LOD that is not read keeps its descriptor and has no mesh
(`SubMeshLOD.IsPayloadDeferred`). `SubMeshLodPayloads.TryLoadLod` reads one entry later, through the registered
`ISubMeshLodPayloadSource` instances (the published game content and common assets archives by default, and
`DirectoryLodPayloadSource` for a loose cooked folder). It checks the length and the checksum before it parses, and the
bone set hash and the blendshape name hash after. A missing entry, a bad length or checksum, an unknown mesh codec
version, or a hash that does not agree rejects that LOD only: it stays not resident, the other LODs stay, and one warning
names the sub-mesh, the LOD, and the reason. The runtime never cooks again.

Version rule: the writer writes only version 2. The reader accepts version 2 in a `RuntimeBinaryV1` envelope and rejects
a higher version. In a generic envelope (`BinaryV1` or `BinaryV2`) each sub-mesh has its own marker, and a sub-mesh with no
marker is version 1: the generic reader loads it with all LODs inline, and `SubMesh.CookedCodecVersion` is 1. No tool
rewrites archives in place; a new cook writes version 2. The version 1 reader stays until engine v1.

## Open Items

- **Index-based resolution caching.** `AotRuntimeMetadataStore.ResolveType(int)`
  calls `Type.GetType()` on every invocation. If config blob loading ever
  becomes hot (~3 calls today), add a `Type?[]` cache sized to the metadata
  table.
- **Trimming annotations on `XRAssetMemoryPackAdapter`.** The `Serialize` and
  `Deserialize` methods lack `[RequiresUnreferencedCode]` attributes. All
  current call sites are already annotated, so this is low-risk but should be
  cleaned up for correctness.

## 7. Archive Handles, Payload Leases, And The Envelope

### Archive handles

A published runtime opens each archive once. `PublishedArchiveRegistry` maps the
three content roots (config, game content, common assets) to
`PublishedArchiveHandle` instances. A handle memory-maps the file and parses the
header, footer, string dictionary, and table of contents once; lookups for all
three modes (hash buckets, sorted-by-hash, linear) are then thread-safe and
allocation-free. Handles close when a root is reconfigured, when
`XRRuntimeEnvironment.ConfigurePublishedPaths` changes the config archive, or
when `AssetManager` is disposed. Using a disposed handle throws
`ObjectDisposedException` naming the archive.

`AssetPacker.GetAsset`, `GetAssetPaths`, `ReadArchiveInfo`, `DecompressEntry`,
and `AssetArchiveReader` remain as one-shot tooling APIs. Each opens a temporary
handle, so the format has one set of magic, version, footer, and TOC
definitions.

### Payload leases

`PublishedArchiveHandle.TryReadAsset` returns a `CookedPayloadLease`:

- A stored entry (`CompressionCodec.Stored`) is a span over the mapping. Nothing
  is copied.
- A compressed entry is decompressed by
  `Compression.Decompress(ReadOnlySpan<byte>, CompressionCodec, Span<byte>)`
  into lease-owned storage. Storage through 65,536 bytes comes from a bounded
  pool of managed buffers; larger storage is native memory
  balanced through `NativeMemoryPressureTracker`, so temporary payloads never
  reach the large object heap.

A lease is a `ref struct` and cannot be captured by a closure or an awaited
continuation. Its small shared ownership object makes copied leases observe the
same disposal/transfer state, preventing double returns and double frees. Stored
leases retain the archive mapping even when its registry root is replaced;
closing the handle forbids new reads and defers unmapping until the final lease
or owner releases it. The managed bucket ceiling prevents a request just below
the CLR LOH threshold from rounding up to a 128 KiB managed array. When bytes must
cross a job boundary, `TransferToOwner()` moves them into a
`CookedPayloadOwner`, a `MemoryManager<byte>` that exposes `ReadOnlyMemory<byte>`
over pooled, native, or mapped storage. Transfers do not copy mapped payloads.
Pinned owner memory remains alive until its final pin is released, even if
`Dispose` was requested meanwhile. `CookedPayloadOwner.MapFile` maps a whole
file the same way; texture streaming uses it to parse cache files and decode
source images without a managed array. `CookedPayloadBufferPool.Statistics`
reports rents, misses, native rents, retained bytes, and high-water marks.

### Envelope

Cooked assets are wrapped in a fixed little-endian header instead of a
MemoryPack struct:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Magic `XRCA` |
| 4 | 2 | Envelope version (2) |
| 6 | 1 | `CookedAssetFormat` |
| 7 | 1 | Reserved |
| 8 | 2 | Type reference length in UTF-8 bytes |
| 10 | 2 | Reserved |
| 12 | 4 | Payload offset from the start of the envelope |
| 16 | 4 | Payload length |
| 20 | n | Type reference, padded so the payload starts on an 8-byte boundary |

The reader parses the header from a span and slices the payload in place. The
type reference is the `CookedAssetTypeReference.Encode` string (an
assembly-qualified name or an `aot:` metadata index); the header field is named
*type reference*, which resolves the earlier `TypeName` naming item.

### Version gates

The archive format is version 5 and the envelope is version 2. Archives or
envelopes from earlier versions are rejected with one diagnostic that names the
file and tells the user to re-cook the content. There is no compatibility shim.

### Registry delegates and the reader split

`PublishedCookedAssetRegistry` delegates are span-based: the deserializer takes
`ReadOnlySpan<byte>` and the serializer writes to `IBufferWriter<byte>`. Full-name
resolution uses a dictionary maintained at registration.

`PublishedCookedAssetReader` handles `RuntimeBinaryV1` only and carries no
`RequiresUnreferencedCode` or `RequiresDynamicCode` annotations. `CookedAssetReader`
is the authoring and development reader: it also accepts reflective `BinaryV1`
payloads and keeps the annotations. `AssetManager` published loads use only the
published reader.
