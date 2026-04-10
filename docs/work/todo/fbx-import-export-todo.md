# Native FBX Importer/Exporter TODO

Active tracker for replacing the current Assimp-driven FBX path with an engine-owned C# importer/exporter optimized for low allocations, deterministic behavior, and high parallel throughput.

## Goal

Build a native FBX pipeline that:

- imports modern binary FBX quickly without per-node/property heap churn,
- exports a deterministic binary FBX subset suitable for round-trip asset workflows,
- preserves current engine import workflow strengths such as async mesh processing and scene publication, and
- gives us explicit control over FBX-specific semantics instead of routing them through Assimp quirks.

This tracker assumes the current FBX entry point stays anchored in the existing model-import workflow while we replace the FBX parsing/serialization core underneath it.

## Current State

- `XRENGINE/Core/ModelImporter.cs` imports FBX through Assimp and currently owns scene assembly, node normalization, async mesh processing, and material/texture creation.
- `XRENGINE/Models/Meshes/ModelImportOptions.cs` exposes Assimp post-process flags plus FBX-native controls such as `FbxPivotPolicy`, `CollapseGeneratedFbxHelperNodes`, and the `FbxBackend` routing override.
- Current behavior depends on Assimp's FBX transform handling, helper-node insertion/removal, multithreading settings, and mesh/material decoding.
- We are pre-ship, so internal API cleanup is acceptable if it produces a cleaner v1 path.

## Recommended V1 Scope

- [ ] Import binary FBX 7.4 and 7.5 as the required path.
- [ ] Export binary FBX 7.4 and 7.5 for the subset we can round-trip with confidence.
- [ ] Support static meshes, node hierarchy, materials, texture references, skeletons, skinning, and animation curves/stacks.
- [ ] Preserve embedded-texture and external-texture workflows at parity with current engine behavior where practical.
- [ ] Treat ASCII FBX as a compatibility/debug path rather than a first-class performance path, but implement its grammar carefully enough that we can read and write it correctly when that path is enabled.
- [ ] Explicitly defer rare constraints, exotic deformers, obscure material stacks, and full SDK-behavior parity until the core path is solid.

## Non-Negotiable Implementation Rules

- [ ] No per-node or per-property managed allocations in parser hot paths.
- [ ] No LINQ, boxing, or capture-heavy delegates in scan/decode loops.
- [ ] Use `ReadOnlySpan<byte>`, `BinaryPrimitives`, pooled buffers, and `ref struct` readers in the binary parser.
- [ ] Keep the importer two-phase: fast structural scan first, parallel heavy-array decode second.
- [ ] Keep parser/writer behavior deterministic and bounds-checked even when using unsafe code.
- [ ] Treat any hot-path allocation regressions discovered during implementation as bugs.
- [ ] Do not add a shipping dependency on Autodesk's SDK; use external parsers or SDK tooling only as validation oracles if needed.

## Success Criteria

- [ ] Native FBX import beats the current Assimp FBX path on a representative XRENGINE corpus.
- [ ] Full-file import scales across multiple files in parallel without shared-state crashes or global locks.
- [ ] Parser/tokenizer allocation profile is near-zero outside final output buffers and intentionally pooled scratch storage.
- [ ] Round-trip validation passes for the supported subset: FBX -> internal -> FBX -> reference importer.
- [ ] Current engine behaviors that matter to users remain intact or are replaced by clearly better semantics with docs updates.

## Phase 0: Scope, Ownership, and Validation Harness

- [x] Confirm the supported versions for v1: binary 7400/7500 required, older binaries best-effort, ASCII optional.
- [x] Confirm the supported feature subset for v1 import and export.
- [x] Decide the low-level code boundary: create an engine-neutral FBX core layer/assembly instead of baking format parsing directly into `ModelImporter`.
- [x] Define the boundary between the FBX core and engine integration: parser/writer/semantic graph vs `SceneNode`, `XRMesh`, `XRMaterial`, and import-asset orchestration.
- [x] Build a representative FBX corpus covering static meshes, skinned meshes, blendshapes, animation clips, embedded textures, large files, and malformed files.
- [x] Add golden-output fixtures for hierarchy, mesh counts, material counts, bone counts, and animation summaries.
- [x] Define performance metrics up front: wall time, MB/s, allocation count, peak memory, and parallel scaling.
- [x] Decide whether built-in deflate is sufficient for array decompression or whether a faster permissively licensed dependency is justified.
- [x] If any dependency is added, run the dependency/license workflow in the same change.

Phase 0 artifacts:

- `XREngine.Fbx/FbxPhase0Contracts.cs` locks the support matrix, benchmark metrics, compression choice, and the engine-neutral/core-vs-engine integration boundary.
- `XREngine.UnitTests/TestData/Fbx/fbx-corpus.manifest.json` is the committed corpus contract, including checked-in ASCII semantic baselines, synthetic malformed fixtures, and planned binary coverage still to be sourced.
- `XREngine.UnitTests/TestData/Fbx/*.summary.json` are the current golden summaries for the checked-in importable fixtures.
- `XREngine.Benchmarks/FbxPhase0BaselineHarness.cs` and `dotnet run --project XREngine.Benchmarks -- --fbx-phase0-report` regenerate the golden summaries from the current Assimp baseline.

Current checked-in corpus reality:

- The repo currently ships four large ASCII FBX Sponza fixtures that are good semantic baselines but not the binary tokenizer corpus we ultimately need.
- The required binary 7400/7500 tokenizer, skinned-animation, and embedded-texture fixtures are now explicit planned manifest entries instead of undocumented gaps.
- No new dependency was added in Phase 0, so the dependency/license regeneration workflow was not required for this change.

### Exit Criteria

- [x] We have an agreed support matrix.
- [x] We have a committed corpus and benchmark plan.
- [x] We have a chosen module boundary that will not force a rewrite midstream.

## File Format Ground Truth

The format rules below are the minimum container-level ground truth the native importer/exporter must honor. These come from converging reverse-engineered sources and are intended to stop us from baking SDK folklore or Assimp quirks into the new implementation.

### Binary container rules

| Item | Required handling |
|---|---|
| Binary header | Read a 27-byte header. Validate the binary magic `Kaydara FBX Binary \0\x1A`, parse the next byte as endianness (`0` little-endian, `1` big-endian), and read the version with that endianness. Do not hardcode little-endian just because most files are LE. |
| Node header `< 7500` | Read 13 bytes: `u32 endOffset`, `u32 propertyCount`, `u32 propertyListLen`, `u8 nameLen`. |
| Node header `>= 7500` | Read 25 bytes: `u64 endOffset`, `u64 propertyCount`, `u64 propertyListLen`, `u8 nameLen`. |
| Node name | Read `nameLen` raw bytes immediately after the node header. Higher layers can sanitize or decode strings later. |
| Offsets | Treat `endOffset` as an absolute file offset and assert the read cursor lands there after the child list is parsed. |
| Sentinel | Use strict all-zero header detection for sentinels. The sentinel size must match the version split: 13 bytes before 7500, 25 bytes at or after 7500. |
| Footer | Binary reader should tolerate missing or truncated footer data in lenient mode, and in strict mode validate the observed footer layout after the top-level sentinel: 16 opaque bytes, 4 zero bytes, padding to the next 16-byte boundary (write 16 bytes if already aligned), `u32 version`, 120 zero bytes, then the 16-byte terminal magic. Binary writer can emit a deterministic 16-byte prefix, but the reader should not require a single universal value there because checked real-world 7400 files vary. Strict validation should still flag header/footer version mismatches. |
| Writer footer prefix | `FA BC AB 09 D0 C8 D4 66 B1 76 FB 83 1C F7 26 7E` is the deterministic prefix currently emitted by the writer. Treat the leading 16 footer bytes as opaque when reading. |
| Terminal magic constant | `F8 5A 8C 6A DE F5 D9 7E EC E9 0C E3 75 8F 29 0B` (confirmed by Blender writer, ImHex pattern, and FbxWriter). |
| Footer alignment detail | The padding between the footer ID block and the version word is computed as: write 4 zero bytes, then write 1–16 zero bytes to reach the next 16-byte boundary (if already aligned, write a full 16 bytes). Some writers (e.g., FbxWriter) simplify this to a constant 20 bytes, which only matches when computed padding is 16; treat constant-20 as non-portable. |

### Binary property codes and array rules

| Code | Required interpretation |
|---|---|
| `Z` | Signed byte / int8 primitive. |
| `Y` | `int16` primitive. |
| `B` | Boolean primitive stored as a byte. |
| `C` | Char / byte primitive. Do not treat it as boolean by default; only coerce with schema-aware tolerant handling if needed. |
| `I` | `int32` primitive. |
| `F` | `float32` primitive. |
| `D` | `float64` primitive. |
| `L` | `int64` primitive. |
| `S` | Length-prefixed string: `u32 length` followed by raw bytes, with no implicit terminator. |
| `R` | Length-prefixed raw blob: `u32 length` followed by raw bytes. |
| `b`, `c`, `i`, `l`, `f`, `d` | Array properties. Each array starts with `u32 arrayLength`, `u32 encoding`, `u32 compressedLength`, then `compressedLength` bytes of payload. `encoding == 0` means raw data, `encoding == 1` means zlib-wrapped deflate. Reject any other encoding value. |

Array validation requirements:

- `decodedLength` must equal `arrayLength * elementSize` for both raw and decompressed arrays.
- For `encoding == 0` (raw), also validate that `compressedLength == arrayLength * elementSize` (the "compressed" length is the raw payload length).
- Decompress with a zlib-capable path (RFC 1950 zlib wrapper around RFC 1951 deflate), not raw deflate-only assumptions. Prefer `System.IO.Compression.ZLibStream` over `DeflateStream`.
- Boolean arrays should normalize non-zero bytes to `true` after decode (consistent with ufbx post-processing behavior).

### ASCII grammar rules

- Treat semicolon-prefixed text as a line comment.
- Parse the leading magic comment such as `; FBX 7.4.0 project file` when present and use it to derive the version for ASCII files.
- Parse nodes in the form `Name: <properties...> { <children...> }`.
- Treat `Name:` as a first-class token; properties that follow are comma-separated scalars, quoted strings, bare words, or nested array forms.
- Support both inline numeric lists and the newer `*count { a: ... }`-style array blocks.
- Tolerate exporter quirks like optional leading commas in entries such as `Content: , "base64-string"`.
- Support quoted base64 payloads for embedded binary content in ASCII when that content path is present.
- Treat `Connections` as a normal node block and support child connection entries such as `Connect: "OO", ...` while remaining tolerant of equivalent observed variants.
- Be aware that some exporters emit `C:` instead of `Connect:` as the connection child token, and object identifiers may be numeric (int64 IDs) rather than name-strings depending on FBX version. Treat `Connect:` as one observed variant, not the only possibility.
- Support `Relations:` blocks in older ASCII files (observed in FBX 6.1.0 Blender exports); these declare object relation entries alongside or instead of `Connections:`.
- Writer output must keep braces balanced, strings properly quoted, and property ordering deterministic.

### Structural validation invariants

- Property parsing must consume exactly `propertyListLen` bytes for the current node.
- Child parsing must end exactly at `endOffset` for the current node.
- Sentinel detection must use the correct header width for the current version.
- Array `encoding` is valid only for `0` or `1`.
- Binary footer parsing in strict mode should warn or fail on header/footer version mismatches.
- ASCII parsing must reject unbalanced braces, unterminated strings, and malformed array-count syntax.
- Reader logic should have strict and tolerant modes; strict mode enforces invariants, tolerant mode may coerce a small set of known ambiguities like `C`-as-bool when schema knowledge exists.

## Performance Architecture Notes

These cross-cutting patterns apply across multiple phases and come from converging recommendations in both research reports.

### Three-stage parsing pipeline

The recommended parsing architecture has three distinct stages:

1. **Stage A (mostly sequential, very fast): structural scan** — Traverse node records, validate `endOffset`, compute property spans, and record "work items" for heavy arrays. Use `endOffset` to skip entire subtrees that the caller does not need (e.g., skip animations for static-only import).
2. **Stage B (highly parallel): decode heavy arrays** — For each array property: if compressed, inflate into a pooled buffer; then reinterpret as typed data. Each array is independent and can be decoded on its own thread.
3. **Stage C (parallel or pipelined): semantic builds** — Build meshes, skinning, and animation tracks from decoded arrays using partitioned work (per mesh, per deformer, per animation stack). Prefer thread-local accumulators and reduce steps to avoid lock contention; `Parallel.ForEach` supports thread-local state patterns.

### Memory layout for hot consumers

- Prefer **structure-of-arrays (SoA)** for vertex attributes (positions/normals/UVs separate) to improve cache locality and vectorization for renderers and physics.
- Use **blittable structs** when AoS representation is needed (e.g., packed tangent frames) and zero-copy interop to native or GPU upload is required.
- Use `StructLayoutAttribute` with explicit `Pack` for native interop alignment guarantees.

### Allocation discipline extras

- **ArrayPool<T>**: Rent/return buffers for decompression outputs, UTF-8 scratch, and temporary arrays. Microsoft docs explicitly call out GC pressure reduction for frequently created/destroyed arrays.
- **Large Object Heap (LOH) avoidance**: Watch for allocations ≥ 85,000 bytes that land on the LOH and cause fragmentation. Pool large buffers and chunk oversized arrays rather than allocating fresh.
- **String pooling for node names**: Intern frequently-repeated node names into a pool (similar to ufbx's approach) instead of allocating per-node strings.
- **System.IO.Pipelines**: Consider as an alternative IO strategy for streaming large files without reading the entire file into memory.
- **Memory-mapped file IO**: Map large files as addressable ranges for random-access scanning without loading the whole file.

### Validation oracles and tooling

When validating our parser/writer outputs, the following external tools and references are available:

| Tool | Use |
|---|---|
| `ufbx` (C, MIT/Public Domain) | Highest-coverage open-source loader; binary + ASCII; extensive fuzzing; use as primary differential-test oracle. |
| OpenFBX (C++, MIT) | Lightweight importer covering geometry, skeletons, animations, materials, lights/cameras; secondary oracle. |
| Assimp (C++, BSD-3-Clause) | Multi-format; current engine baseline; useful for migration comparison but not FBX-optimized. |
| `fbxcel` / `fbxcel-dom` (Rust, Apache-2.0/MIT) | Clean pull-parser design reference for binary 7.4/7.5 versioned parsing. |
| ImHex + FBX pattern | Interactive binary structure validation; assert offsets, sentinels, footer layout against real files. |
| Kaitai Struct | Rapid "format-as-code" prototyping with diagrams; no canonical FBX .ksy but useful for custom validation. |
| Autodesk FBX Converter | Archived conversion/inspection utility; useful for round-trip baseline testing. |
| Khronos glTF Validator | Cross-check by converting FBX→glTF and validating structural correctness of exported geometry/materials. |

### Profiling toolchain

| Goal | Tool |
|---|---|
| Reproducible micro/macro benchmarks | BenchmarkDotNet (already in `XREngine.Benchmarks`) |
| Cross-platform tracing | `dotnet-trace` (EventPipe-based) |
| Runtime allocation/GC counters | `dotnet-counters` |
| GC heap snapshots | `dotnet-gcdump` |
| Deep Windows analysis | PerfView |

### Specific benchmark targets

Microbenchmarks (BenchmarkDotNet):
- ReadInt32/ReadUInt64 loops on spans vs unsafe pointer loops.
- Property record parsing per token.
- Zlib inflate throughput and allocation profile (pooled vs fresh arrays).

Macrobenchmarks (real files):
- Throughput (MB/s) and wall time per file.
- Allocation rate and peak working set (watch LOH allocations for large buffers).
- Scaling: N files parsed in parallel; 1 huge file with internal parallel array decode.
- Differential correctness: compare mesh/anim outputs against ufbx/OpenFBX/Assimp.

## Phase 1: Container Readers and Structural Scan

### Binary reader

- [x] Implement a low-allocation binary reader for the 27-byte FBX header, including magic validation, endianness handling, and version decode.
- [x] Support the version split at 7500 exactly: 13-byte node headers and sentinels before 7500, 25-byte node headers and sentinels at or after 7500.
- [x] Parse primitive property types without materializing strings or arrays unless requested.
- [x] Support the full container-level property code set: `Z`, `Y`, `B`, `C`, `I`, `F`, `D`, `L`, `S`, `R`, and arrays `b`, `c`, `i`, `l`, `f`, `d`.
- [x] Parse array property headers (`arrayLength`, `encoding`, `compressedLength`) and capture decode work items instead of inflating immediately.
- [x] Use `endOffset` to support subtree skipping: when the caller does not need a subtree (e.g., skip animations for static-only import), jump directly to `endOffset` instead of parsing children.
- [x] Decode `encoding == 1` arrays as zlib-wrapped deflate (use `ZLibStream`, not raw `DeflateStream`) and validate the decoded length against `arrayLength * elementSize`.
- [x] For `encoding == 0` raw arrays, validate `compressedLength == arrayLength * elementSize`.
- [x] Add structural validation for `endOffset`, `propertyListLen`, strict all-zero sentinel handling, and impossible order/size states.
- [x] Parse the binary footer in tolerant and strict modes, then mirror the observed footer layout in the writer later.

### ASCII reader

- [x] Implement an ASCII tokenizer that handles semicolon comments, quoted strings, `Name:` tokens, commas, braces, and numeric literals.
- [x] Parse node trees in the form `Name: <properties...> { <children...> }`.
- [x] Support inline numeric lists and modern `*count { a: ... }` array blocks.
- [x] Support `Connections { Connect: ... }` style blocks and tolerate equivalent observed variants (including `C:` as a connection child token and numeric object IDs).
- [x] Support `Relations:` blocks for older ASCII file compatibility (observed in FBX 6.1.0 Blender exports).
- [x] Support quoted base64 content for embedded binary payloads in ASCII files when that path is encountered.
- [x] Derive the ASCII version from the leading `; FBX x.y.z project file` comment when present.
- [x] Add malformed ASCII fixtures for unbalanced braces, unterminated strings, and broken array blocks.

### Shared work

- [x] Decide file IO strategy for large files: whole-file read for smaller files, memory-mapped access for the checked-in giant ASCII corpus, and keep streaming as a future option if we outgrow the current structural-scan model.
- [x] Add corruption tests that prove the reader fails closed on bad offsets, truncated arrays, and invalid sentinels.
- [x] Add tokenizer microbenchmarks to `XREngine.Benchmarks`.

### Exit Criteria

- [ ] The binary structural scan succeeds on the binary corpus once the planned 7400 and 7500 fixtures are sourced.
- [x] The ASCII structural scan succeeds on the current checked-in ASCII corpus and malformed ASCII fixtures.
- [ ] Tokenization and structural validation are benchmarked and allocation-audited.
- [x] Corrupt files fail with actionable diagnostics instead of undefined behavior.

## Phase 2: FBX Semantic Graph and Transform Semantics

- [x] Build a typed semantic layer over raw node/property records for `Objects`, `Connections`, `Definitions`, `Takes`, and global settings.
- [x] Implement ID mapping and connection traversal without repeated dictionary churn in hot paths.
- [x] Normalize axis system, unit scale, geometric transforms, pivots, pre/post rotations, and bind-pose-related transform semantics.
- [x] Replace the current `PreservePivots` and `RemoveAssimpFBXNodes` behavior with explicit native policies rooted in actual FBX semantics.
- [x] Define an engine-neutral intermediate representation for nodes, meshes, materials, textures, skins, clusters, blendshapes, and animation curves.
- [x] Add targeted differential tests against at least one reference parser/oracle for transform hierarchy and connection graphs.

### Exit Criteria

- [x] The semantic graph can explain the same scene topology that the current importer expects to assemble.
- [x] FBX pivot/helper-node behavior is native and documented rather than inherited from Assimp.
- [x] Transform and connection semantics are validated on non-trivial corpus assets.

## Phase 3: Geometry, Materials, and Scene Import Integration

- [x] Import node hierarchies into the current scene assembly path without regressing async publication behavior.
- [x] Decode positions, indices, normals, tangents, UV channels, color channels, and primitive topology into engine-native mesh buffers.
- [ ] Keep heavy array decode parallel and pool-backed.
- [ ] Preserve or intentionally replace current import behaviors for mesh splitting, async mesh publication, and generated renderer async flags.
- [x] Port material extraction, texture lookup, texture remap seeding, and material remap seeding off the Assimp path.
- [x] Preserve current transparency inference behavior where it is still correct, then tighten format-specific behavior later if needed.
- [x] Add targeted unit tests for static-model import parity on representative FBX assets.
- [ ] Add macro benchmarks comparing native FBX import vs current Assimp FBX import.

### Current status

- The Phase 3 native path has now been promoted into the default `.fbx` import route via format-specific dispatch in `ModelImporter`; `ModelImportOptions.FbxBackend = AssimpLegacy` remains the compatibility escape hatch for the older Assimp FBX path.
- Static native import currently covers authored node hierarchy, static mesh geometry, material extraction, texture/video file-path resolution, and reuse of the existing material/texture remap hooks through `ModelImporter`.
- The remaining Phase 3 work is focused on performance hardening and behavior parity gaps rather than first-use functionality.

### Exit Criteria

- [x] Static-mesh FBX import is functional end-to-end through the engine's normal import flow.
- [ ] Native FBX import meets or beats the existing Assimp path on the initial static corpus.
- [ ] Material and texture remap persistence still works.

## Phase 4: Skinning, Blendshapes, and Animation Import

- [x] Import skeleton hierarchy, bind poses, inverse bind matrices, clusters, and per-vertex skin weights.
- [x] Import blendshape channels and deltas for the supported subset.
- [x] Import animation stacks, layers, curve nodes, and scalar/vector/quaternion curves needed for the supported asset set.
- [x] Decide and document the evaluation/baking policy for animation data we store internally.
- [ ] Validate imported transforms and animation outputs against reference parsers on humanoid and non-humanoid assets.
- [ ] Add tests for weight normalization, bind-pose stability, animation key ordering, and root-motion-related transform correctness.
- [ ] Add semantic sanity checks: vertex AABB bounds plausibility, normal vector length validation (should be ~1.0), UV range checks, skeleton joint parent-index acyclicity, and monotonic animation keyframe timestamps.

Current native import policy for the supported Phase 4 subset:

- Skin clusters are attached directly to imported `SceneNode` transforms and per-control-point weights are normalized before `XRMesh` skinning buffers are rebuilt.
- Blendshape channel deltas are converted into absolute per-vertex targets in engine space and default deform percentages are applied as normalized `ModelComponent` blendshape weights.
- FBX animation stacks are imported as generic `AnimationClip`s attached to the imported root node. Translation and scale stay as scalar property curves, blendshape `DeformPercent` curves are normalized to `0..1`, and Euler rotation curves are baked into quaternion component tracks at the union of source key timestamps.

### Exit Criteria

- [x] Skinned meshes and animation-heavy files import correctly through the native path.
- [ ] We can explain any intentional deviations from current Assimp FBX behavior.
- [ ] Differential tests cover the supported rigging and animation subset.

## Phase 5: Binary Exporter Core

- [x] Implement a deterministic binary FBX writer for the supported 7.4/7.5 subset.
- [x] Serialize the binary header correctly, including the endianness byte and version word.
- [x] Serialize node/property records, version-correct sentinels, and the observed footer layout correctly.
- [x] Emit stable object IDs and connection graphs.
- [x] Export hierarchy, transforms, meshes, materials, texture references, skins, blendshapes, and animation data for the supported subset.
- [x] Decide whether binary array compression is enabled by default or exposed as an export option.
- [x] Add structural writer tests that re-parse exported files with our own reader before involving external tools.
- [x] Add round-trip tests against at least one external reference parser.

Phase 5 artifacts:

- `XREngine.Fbx/FbxBinaryWriterModel.cs`, `XREngine.Fbx/FbxBinaryWriter.cs`, and `XREngine.Fbx/FbxBinaryExporter.cs` now serialize deterministic binary 7400/7500 FBX from the semantic/geometry/deformer/animation documents, preserve stable object IDs and connections, and expose array compression as an export option instead of forcing it on by default.
- `XREngine.UnitTests/Core/FbxPhase5BinaryExportTests.cs` now covers compressed 7400 writer reparse, big-endian 7500 writer reparse, internal round-trip export/import of the supported synthetic Phase 4 subset, and external Assimp import compatibility.
- External-reader compatibility required two format-specific fixes in the exporter: `Properties70` numeric values must be emitted from FBX property metadata rather than the original ASCII token shape, and FBX `int` properties must be written as 32-bit `I` values instead of 64-bit `L` values for readers like Assimp to accept the document.

### Deferred ASCII writer requirements

- [ ] When the ASCII writer path lands, emit the leading ASCII magic comment and stable version string.
- [ ] Emit `Name:` nodes, comma-separated property lists, balanced braces, and deterministic ordering.
- [ ] Support both scalar property emission and `*count { a: ... }` array emission where that representation is required.
- [ ] Serialize `Connections` blocks and connection entries in a form the ASCII reader can reparse deterministically.
- [ ] Decide whether ASCII write mode normalizes to a single canonical style or preserves source style only in special round-trip/debug builds.

### Exit Criteria

- [x] Supported assets round-trip through native import/export without structural corruption.
- [ ] Export output is deterministic for identical inputs.
- [x] External validation can consume exported files successfully.

Current validation note:

- The targeted `FbxPhase5BinaryExportTests` source compiles cleanly, but a normal build-backed `dotnet test` run is currently blocked by unrelated preexisting compile errors in `XRENGINE/Rendering/DLSS/StreamlineNative.cs` and `XRENGINE/Rendering/XeSS/IntelXessNative.cs`.
- The external-reader compatibility fix was therefore verified through the same exporter code path in a standalone diagnostic run that wrote the synthetic Phase 4 subset and imported it with Assimp successfully.

## Phase 6: Engine Cutover and Import Option Cleanup

- [x] Introduce a format-specific dispatch path so `.fbx` no longer has to flow through Assimp once the native path is ready.
- [x] Keep Assimp available for non-FBX formats unless and until we intentionally replace those too.
- [x] Decide whether `ModelImportOptions` keeps compatibility shims or is cleaned up aggressively for a better pre-v1 API.
- [x] Replace Assimp `PostProcessSteps` dependence for FBX with explicit engine-native options where possible.
- [x] Migrate FBX-specific options from Assimp terminology to native terminology rooted in actual importer behavior.
- [x] Preserve editor, asset-pipeline, and MCP import workflows while swapping the backend.
- [x] Update docs such as `docs/features/model-import.md` once the native path is user-visible.

Phase 6 cutover notes:

- `ModelImporter` now routes `.fbx` files to the native importer by default even when the caller does not explicitly populate `ModelImportOptions`; the Assimp path remains the default only for non-FBX formats.
- `ModelImportOptions` now exposes `FbxPivotPolicy` and `CollapseGeneratedFbxHelperNodes` as the FBX-facing controls, while hidden YAML compatibility setters continue to accept the legacy `PreservePivots` and `RemoveAssimpFBXNodes` names from previously cached import settings.
- `FbxImportBackend.Auto` is now the default import mode, and `FbxImportBackend.AssimpLegacy` is the explicit compatibility override for the legacy FBX path.

Current validation note:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~NativeFbxImporterTests --no-build` passes 4 focused tests, including coverage for the null-`ImportOptions` default-native dispatch path and the legacy YAML alias shims.

### Exit Criteria

- [x] `.fbx` imports use the native backend by default.
- [x] Current workflows still work without hidden Assimp dependencies for FBX.
- [x] User-visible docs describe the new behavior accurately.

## Phase 7: Hardening, Corpus Expansion, and Regression Gates

- [ ] Expand the corpus across exporters, DCC tools, file sizes, and edge cases.
- [x] Add malformed-file regression tests and, if worthwhile, a lightweight fuzzing harness for parser hardening.
- [x] Add sustained multi-file parallel stress tests.
- [x] Add benchmark regression gates so parser or decode slowdowns are caught early.
- [x] Run allocation audits against touched importer/exporter code and eliminate nearby low-risk issues; specifically audit for LOH allocations (≥85KB) in parser hot paths.
- [ ] Remove obsolete Assimp FBX workarounds once the new path has proved itself.
- [ ] Validate exported files with ImHex FBX pattern assertions and at least one external reference parser (ufbx preferred, OpenFBX secondary).

Phase 7 hardening slice currently landed:

- `XREngine.UnitTests/TestData/Fbx/` now includes two small checked-in deterministic ASCII fixtures: `synthetic-static-scene-ascii.fbx` for static/material coverage and `synthetic-phase4-skinned-animation-ascii.fbx` for the supported skinned/blendshape/animation subset. Both are wired into `fbx-corpus.manifest.json` and have committed golden summaries.
- `XREngine.UnitTests/Core/FbxPhase7HardeningTests.cs` now covers malformed binary array encodings, decoded-length mismatch failure behavior, and repeated parallel full-roundtrip validation over the checked-in performance-baseline corpus.
- `XREngine.Benchmarks/FbxPhase7RegressionHarness.cs` now emits `Build/Reports/fbx-phase7-regression.json` and fails conservatively when the checked-in baseline fixtures exceed per-asset wall-time or allocation budgets.
- The parser allocation sweep removed an extra compressed-payload copy by streaming zlib decode from `FbxSourceSliceStream`, and the geometry parser now decodes typed numeric arrays directly into their final buffers via `FbxArrayDecodeHelper` instead of materializing an intermediate byte array first.
- `XREngine.Fbx/FbxSemanticParser.cs` now normalizes observed binary object-name strings from the `Name\0\u0001Family` form into canonical `Family::Name` before building semantic/intermediate objects, so imported scene nodes and blendshape channels no longer surface control-byte artifacts like `??Model` or `??SubDeformer` in their names.

Current validation note:

- Focused validation on 2026-04-07 passed the targeted `NativeFbxImporterTests`, `FbxPhase5BinaryExportTests`, and `FbxPhase7HardeningTests` slice (11 tests total), and the Phase 7 regression harness currently stays within budget for both checked-in synthetic baseline assets.

### Exit Criteria

- [ ] The native path is stable enough to be the only supported FBX path for normal development workflows.
- [x] Performance and correctness regressions are measurable and caught automatically.
- [ ] Remaining unsupported FBX features are explicit backlog items, not unknowns.

## Suggested Initial File/Project Touchpoints

- `XRENGINE/Core/ModelImporter.cs`
- `XRENGINE/Models/Meshes/ModelImportOptions.cs`
- `XREngine.UnitTests/`
- `XREngine.Benchmarks/`
- new engine-neutral FBX core parser/writer project or namespace

## Deferred Until After Core Import/Export Is Stable

- [ ] Source-style-preserving ASCII export, including comment preservation and minimal-diff round-tripping.
- [ ] Rare constraints and advanced control rigs.
- [ ] Full parity with every Autodesk SDK corner case.
- [ ] Aggressive exporter feature surface beyond what we can validate with confidence.