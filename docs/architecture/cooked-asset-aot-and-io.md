# Cooked Asset Serialization — AOT & I/O Design Decisions

[← Architecture index](README.md)

> Status: **Implemented** (AOT type references, registry-backed runtime cooked assets); **Deferred** (DirectStorage asset packer reads).

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
│       RuntimeBinaryV1 or generic BinaryV1 hydrate path   │
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
| `CookedAssetBlob` | `Core/Files/CookedAssetBlob.cs` | MemoryPack envelope: type reference + format tag + binary payload |
| `PublishedCookedAssetRegistry` | `Core/Files/PublishedCookedAssetRegistry.cs` | Explicit registry of published runtime asset serializers used by shipped builds |
| `XRAssetMemoryPackEnvelope` | `Core/Files/XRAsset.MemoryPack.cs` | Inner envelope used when CookedBinarySerializer falls through to MemoryPack for `XRAsset` subclasses |
| `AotRuntimeMetadata` | `Core/Engine/AotRuntimeMetadata.cs` | MemoryPack-serializable table of all known types, redirects, replication info |
| `AotRuntimeMetadataStore` | `Core/Engine/AotRuntimeMetadataStore.cs` | Lazy-loaded singleton that reads `AotRuntimeMetadata.bin` from the config archive |
| `CookedBinarySerializer` | `Core/Files/CookedBinary/CookedBinarySerializer.cs` | Module-dispatched binary serializer for the engine's general native wire format |
| `RuntimeCookedBinarySerializer` | `XREngine.Runtime.Rendering/Core/Files/RuntimeCookedBinarySerializer.cs` | Explicit runtime serializer used by registered published asset types |
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
(currently explicit runtime-rendering assets such as `XRMesh` and `XRTexture2D`, plus bounded animation assets such as
`AnimationClip`, `BlendTree1D`, `BlendTree2D`, `BlendTreeDirect`, and `AnimStateMachine`).
Published AOT runtime validates those types against `AotRuntimeMetadata.PublishedRuntimeAssetTypeNames`
before dispatching to the registered runtime serializer.

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

## 6. Open Items

- **Index-based resolution caching.** `AotRuntimeMetadataStore.ResolveType(int)`
  calls `Type.GetType()` on every invocation. If config blob loading ever
  becomes hot (~3 calls today), add a `Type?[]` cache sized to the metadata
  table.
- **`CookedAssetBlob.TypeName` field naming.** The field can now hold `"aot:5"`
  in addition to assembly-qualified names. Renaming is a MemoryPack versioning
  concern; deferred until the next envelope format bump.
- **Trimming annotations on `XRAssetMemoryPackAdapter`.** The `Serialize` and
  `Deserialize` methods lack `[RequiresUnreferencedCode]` attributes. All
  current call sites are already annotated, so this is low-risk but should be
  cleaned up for correctness.
