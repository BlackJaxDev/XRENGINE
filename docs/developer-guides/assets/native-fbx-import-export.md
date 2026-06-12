# Native FBX Import And Export

Native FBX support is the engine-owned FBX path behind XRENGINE's model import and export workflow. It replaces the old Assimp FBX route for normal `.fbx` imports, while keeping Assimp available as an explicit compatibility backend for assets that still need it during the v1 hardening window.

The implementation lives in two layers:

- `XREngine.Fbx` owns container parsing, semantic FBX documents, binary writing, corpus contracts, and benchmark harnesses.
- `XRENGINE` bridges the engine-neutral FBX documents into `SceneNode`, `XRMesh`, `XRMaterial`, `XRTexture`, animation clips, remap persistence, and editor import workflows.

## User-Facing Behavior

- `.fbx` imports route through the native `XREngine.Fbx` importer by default.
- `ModelImportOptions.FbxBackend = Auto` uses the native importer first and may fall back to Assimp if the native path rejects the asset or fails before publication.
- `ModelImportOptions.FbxBackend = Assimp` forces the older Assimp path. Cached YAML may still spell this as `AssimpLegacy`; the loader keeps that alias for compatibility.
- Assimp remains the default path for non-FBX model formats that do not have their own native importer.
- `FbxPivotPolicy` and `CollapseGeneratedFbxHelperNodes` are the FBX-specific transform controls. They replace the older Assimp-shaped pivot/helper-node settings, while hidden YAML aliases keep old import settings readable.
- `NativeFbxMeshBuildMaxDegreeOfParallelism` caps native FBX mesh-build workers. `0` uses the importer's editor-friendly automatic cap.
- `ProcessMeshesAsynchronously`, `GenerateMeshRenderersAsync`, `SplitSubmeshesIntoSeparateModelComponents`, `GenerateSceneNodesPerSubmesh`, `SeparateMeshIslands`, and `BatchSubmeshAddsDuringAsyncImport` continue to apply through the normal model import settings.

For importer and exporter tracing, set `XRE_FBX_LOG` before launching the editor, tests, or tools:

- `XRE_FBX_LOG=info` logs stage-level summaries.
- `XRE_FBX_LOG=verbose` or `XRE_FBX_LOG=1` logs detailed per-stage and per-asset traces.
- `XRE_FBX_LOG=warn` or `XRE_FBX_LOG=error` limits output to problems.

Enabled trace lines use the engine `Assets` log category, so they appear in the editor console's `Assets` tab and in `Build/Logs/.../log_assets.log` when file logging is enabled.

## Supported V1 Scope

The required binary interchange target is FBX 7.4 and 7.5. Older binary FBX files are best-effort import inputs, not blocking export targets. ASCII FBX is supported as a compatibility and debug import path; ASCII export is intentionally deferred.

Native import covers:

- authored node hierarchy and transforms,
- static mesh geometry,
- positions, indices, normals, tangents, UVs, colors, and primitive topology,
- materials, texture references, embedded texture payloads where supported, texture remap seeding, and material remap seeding,
- skeletons, bind poses, clusters, inverse bind data, and normalized skin weights,
- blendshape channels and vertex deltas,
- animation stacks, layers, curve nodes, and imported animation clips for the supported subset.

Native binary export covers the supported 7.4/7.5 subset:

- hierarchy, transforms, meshes, materials, and texture references,
- skins, blendshapes, and animation data,
- stable object IDs and connection graphs,
- deterministic binary node/property output,
- optional binary array compression through `FbxBinaryExportOptions.ArrayEncodingMode`.

The native path does not add a shipping dependency on Autodesk's FBX SDK. External tools such as ufbx, OpenFBX, Assimp, Autodesk FBX Converter, ImHex, and Blender are validation oracles, not runtime dependencies.

## Parser Architecture

The importer is organized as a staged pipeline:

1. Structural scan: traverse FBX node records, validate offsets and property spans, identify child ranges, and collect heavy array decode work.
2. Heavy array decode: decode raw or zlib-compressed arrays into typed buffers, using pool-backed scratch where practical.
3. Semantic build: build FBX objects, connections, geometry, deformers, materials, textures, animation stacks, and the engine-neutral scene document consumed by `ModelImporter`.

The binary reader is built around `ReadOnlySpan<byte>`, `BinaryPrimitives`, bounds-checked cursor movement, and version-aware record sizes. Parser hot paths avoid LINQ, boxing, and capture-heavy delegates.

## Binary FBX Rules

The binary reader validates the 27-byte FBX header, the `Kaydara FBX Binary` magic, the endianness byte, and the version word. Version handling controls node header width:

| Version | Node header |
|---|---|
| `< 7500` | `u32 endOffset`, `u32 propertyCount`, `u32 propertyListLen`, `u8 nameLen` |
| `>= 7500` | `u64 endOffset`, `u64 propertyCount`, `u64 propertyListLen`, `u8 nameLen` |

Node names are read as raw bytes and decoded by higher layers. `endOffset` is treated as an absolute file offset. Sentinels are detected as strict all-zero headers using the version-correct sentinel width: 13 bytes before 7500 and 25 bytes at or after 7500.

The reader supports tolerant and strict footer modes. Strict mode validates the observed footer layout after the top-level sentinel: 16 opaque bytes, 4 zero bytes, padding to the next 16-byte boundary, a `u32` version, 120 zero bytes, and the 16-byte terminal magic. The writer emits the deterministic footer prefix `FA BC AB 09 D0 C8 D4 66 B1 76 FB 83 1C F7 26 7E`, but the reader treats the leading 16 bytes as opaque because real-world files vary there.

The terminal magic is:

```text
F8 5A 8C 6A DE F5 D9 7E EC E9 0C E3 75 8F 29 0B
```

Supported property codes:

| Code | Meaning |
|---|---|
| `Z` | signed byte / int8 |
| `Y` | int16 |
| `B` | boolean byte |
| `C` | char / byte, not boolean unless schema-aware tolerant handling says so |
| `I` | int32 |
| `F` | float32 |
| `D` | float64 |
| `L` | int64 |
| `S` | `u32` length-prefixed string bytes |
| `R` | `u32` length-prefixed raw bytes |
| `b`, `c`, `i`, `l`, `f`, `d` | typed array properties |

Array properties begin with `u32 arrayLength`, `u32 encoding`, and `u32 compressedLength`. `encoding == 0` is raw payload data and must have `compressedLength == arrayLength * elementSize`. `encoding == 1` is zlib-wrapped deflate and is decoded with a zlib-capable path. Other encoding values are rejected. Decoded byte length must equal `arrayLength * elementSize`, and boolean arrays normalize non-zero bytes to `true`.

## ASCII FBX Rules

The ASCII reader exists for compatibility, debugging, and semantic baselines. It handles:

- semicolon comments,
- leading comments such as `; FBX 7.4.0 project file` for version detection,
- `Name: <properties...> { <children...> }` node forms,
- quoted strings, bare words, numeric literals, commas, and balanced braces,
- inline numeric lists and `*count { a: ... }` array blocks,
- optional leading commas in entries such as `Content: , "base64-string"`,
- quoted base64 embedded content,
- `Connections` blocks with `Connect:` and observed aliases such as `C:`,
- numeric object IDs as well as name strings,
- older `Relations:` blocks observed in FBX 6.1-era ASCII exports.

Malformed ASCII input is expected to fail closed on unbalanced braces, unterminated strings, malformed array-count syntax, and broken array blocks.

## Semantic Import

The semantic layer sits above raw nodes and properties. It builds typed views for `Objects`, `Connections`, `Definitions`, `Takes`, and global settings; normalizes object-name forms such as `Name\0\x01Family` into `Family::Name`; and maps connection graphs without repeated dictionary churn in hot paths.

Transform import is native FBX behavior rather than inherited Assimp behavior. The importer handles axis system, unit scale, geometric transforms, pivots, pre/post rotations, bind-pose-related transforms, and helper-node policy through explicit options.

The engine-neutral intermediate representation includes nodes, meshes, materials, textures, skins, clusters, blendshapes, animation stacks, layers, curve nodes, and animation curves. `ModelImporter` then maps that representation into the normal engine scene assembly path so async publication, submesh splitting, material remaps, texture remaps, and asset externalization continue to use the existing workflow.

Skin clusters attach directly to imported `SceneNode` transforms. Per-control-point weights are normalized before `XRMesh` skinning buffers are rebuilt. Blendshape channel deltas are converted into absolute per-vertex targets in engine space, and default deform percentages become normalized `ModelComponent` blendshape weights.

Animation stacks import as generic `AnimationClip`s attached to the imported root node. Translation and scale stay as scalar property curves. Blendshape `DeformPercent` curves normalize to `0..1`. Euler rotation curves are baked into quaternion component tracks at the union of source key timestamps.

## Binary Export

`FbxBinaryWriter` serializes binary nodes and properties, including version-correct headers, sentinels, footer data, and raw or zlib-compressed array payloads. `FbxBinaryExporter` builds a binary FBX document from the semantic, geometry, deformer, and animation documents.

`FbxBinaryExportOptions` controls:

- `BinaryVersion`, defaulting to 7400,
- `BigEndian`,
- `IncludeFooter`,
- `ArrayEncodingMode`,
- `IncludeGlobalSettings`,
- `IncludeDefinitions`,
- `IncludeTakes`.

Exporter compatibility fixes that are part of the current feature contract:

- `Properties70` numeric values are emitted from FBX property metadata, not the original ASCII token shape.
- FBX `int` properties are written as 32-bit `I` values, not 64-bit `L` values, so readers such as Assimp accept the document.

## Validation Assets And Tests

The committed FBX corpus contract lives in `XREngine.UnitTests/TestData/Fbx/fbx-corpus.manifest.json`. Current checked-in fixtures include large ASCII Sponza semantic baselines, deterministic synthetic static/material ASCII fixtures, a synthetic skinned/blendshape/animation ASCII fixture, malformed fixtures, and golden summary JSON files. Binary 7400/7500 corpus expansion remains active follow-up work.

Relevant focused tests include:

- `FbxPhase0CorpusTests`
- `FbxPhase1StructuralParserTests`
- `FbxPhase2SemanticGraphTests`
- `FbxPhase3GeometryImportTests`
- `FbxPhase5BinaryExportTests`
- `FbxPhase7HardeningTests`
- `NativeFbxImporterTests`

Benchmark and report harnesses include:

- `dotnet run --project XREngine.Benchmarks -- --fbx-phase0-report`
- `XREngine.Benchmarks/FbxPhase7RegressionHarness.cs`, which emits `Build/Reports/fbx-phase7-regression.json`

Focused validation has covered native default dispatch, legacy YAML alias loading, static hierarchy/material import, skinning/blendshape/animation import, binary writer reparse, synthetic supported-subset round-trip, external Assimp readability for exported binary FBX, malformed array handling, and repeated parallel full-roundtrip validation over checked-in synthetic baselines.

## Current Limitations

The native path is the default FBX route, but hardening work remains before the old Assimp FBX path can be removed from normal development workflows. The active follow-up tracker is [Native FBX Import/Export TODO](../../work/todo/assets/fbx-import-export-todo.md).

The main remaining gaps are:

- broader binary 7400/7500 fixture coverage,
- macro benchmarks proving native import meets or beats the Assimp path on representative assets,
- deeper humanoid and non-humanoid rigging/animation differential tests,
- material and texture remap persistence validation across the supported corpus,
- deterministic export verification for identical inputs,
- ImHex and external parser validation for exported files,
- removal of obsolete Assimp FBX workarounds,
- source-style-preserving ASCII export and advanced/rare FBX feature parity.

## Related Docs

- [Model Import](model-import.md)
- [Unit Testing World](../testing/unit-testing-world.md)
- [Native FBX Import/Export TODO](../../work/todo/assets/fbx-import-export-todo.md)
