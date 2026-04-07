# USD Import/Export TODO

Created: 2026-04-07

Active tracker for adding engine-owned USD import/export support to the current model/prefab pipeline. The intended architecture is not a blind one-to-one clone of the FBX path: USD needs an engine-owned managed fast path for common layer/package cases plus a native OpenUSD fallback for the composition breadth and writer fidelity that would otherwise be expensive to recreate incorrectly.

## Goal

Build a USD pipeline that:

- imports `.usda`, `.usdc`, `.usd`, and `.usdz` into the current prefab/model workflow with deterministic hierarchy, material, and animation behavior,
- exports a deterministic supported subset with a managed USDA writer and a validated full-fidelity path for USDC/USDZ,
- preserves current engine strengths such as async mesh processing, scene publication, and material/texture remap seeding, and
- makes USD-specific semantics explicit instead of flattening everything into Assimp-style assumptions or lossy stage snapshots.

This tracker assumes the current import entry point stays anchored in the existing `ModelImporter` and `XRPrefabSource` workflow while we add a USD-specific parsing, stage-resolution, and export core underneath it.

## Current State

- `XRENGINE/Scene/Prefabs/XRPrefabSource.cs` already lists `usd`, `usda`, `usdc`, and `usdz` as supported third-party extensions.
- `XRENGINE/Core/ModelImporter.cs` currently has format-specific native dispatch only for `.fbx`; non-FBX formats still route through the generic Assimp path.
- There is no `XREngine.Usd` project, no USD-specific import/export option surface, no committed USD fixture corpus, no USD unit tests, and no USD benchmarks.
- `docs/features/model-import.md` currently documents the native FBX path and Assimp for non-FBX formats; it does not describe any native USD behavior yet.
- Any OpenUSD dependency, binding generator, native packaging, or submodule addition would be a risky operation and requires proposal + approval before landing, along with the dependency/license workflow in `AGENTS.md`.

## Recommended V1 Scope

- [ ] Import `.usd`, `.usda`, `.usdc`, and `.usdz` through an engine-owned USD path instead of relying on generic non-FBX import behavior.
- [ ] Treat `.usd` as a sniffed container extension rather than assuming text or binary from the filename alone.
- [ ] Support the model/prefab-relevant scene subset first: transforms, hierarchy, meshes, materials, texture references, instancing, skeletons/skinning, blendshapes, and time-sampled animation.
- [ ] Support enough composition to import real assets: default layer resolution, sublayers, references, payloads, asset paths, and variant selections required by the target corpus.
- [ ] Use a managed fast path for USDA reading/writing, USDZ package indexing, and USDC structural indexing/selective decode.
- [ ] Use an OpenUSD interop fallback for unsupported Crate versions, complex composition cases, broad schema/value coverage, and full-fidelity USDC/USDZ export.
- [ ] Keep model/prefab import semantics aligned with current engine workflows, but allow API cleanup where USD-specific options deserve cleaner pre-v1 names.
- [ ] Explicitly defer full arbitrary schema authoring, source-style-preserving USDA round-tripping, and full hand-written Crate writer parity until the core path is proven.

## Non-Negotiable Implementation Rules

- [ ] No per-spec, per-field, or per-token managed allocations in parser hot paths.
- [ ] No LINQ, boxing, or capture-heavy delegates in scan/decode loops.
- [ ] Use `ReadOnlySpan<byte>`, `BinaryPrimitives`, pooled buffers, and `ref struct` or similarly allocation-free readers for USDA/USDC hot paths.
- [ ] Use memory-mapped or similarly zero-copy-friendly access for large Crate files and embedded USDZ layer slices where practical.
- [ ] Keep parsing layered: container sniffing/package index first, structural parse second, semantic stage/build third.
- [ ] Unsupported Crate versions, schema/value cases, or composition behaviors must fall back to OpenUSD interop or fail with actionable diagnostics. Do not silently guess.
- [ ] Any native OpenUSD dependency addition, packaging, or upgrade requires approval first and must run the dependency/license refresh in the same change.
- [ ] Treat any hot-path allocation regressions discovered during implementation as bugs.

## Success Criteria

- [ ] A representative USD corpus imports with deterministic hierarchy, mesh/material, skeleton, and animation summaries.
- [ ] Supported USDA exports round-trip through OpenUSD tooling without semantic corruption.
- [ ] Supported USDC/USDZ exports validate through the chosen OpenUSD-backed writer path and reopen cleanly.
- [ ] Cold-open indexing of large USDC assets is near-zero allocation outside output buffers and intentionally pooled scratch storage.
- [ ] Multi-file USD import scales in parallel without global-lock bottlenecks or shared-state crashes.
- [ ] Current prefab/model workflows remain intact or improve, with docs updated when the new path becomes user-visible.

## Phase 0: Scope, Ownership, Dependency Gate, and Validation Harness

- [ ] Confirm the supported V1 subset for import and export: scene/model features, composition arcs, animation scope, and export formats.
- [ ] Decide the low-level code boundary: create an engine-neutral `XREngine.Usd` core instead of embedding USD parsing directly into `ModelImporter`.
- [ ] Define the boundary between the USD core and engine integration: layer/package parsing, stage resolution, value decoding, and export documents vs `SceneNode`, `XRMesh`, `XRMaterial`, and import orchestration.
- [ ] Decide the interop boundary up front: what must stay managed, what explicitly routes through OpenUSD, and what remains deferred.
- [ ] Decide the Windows-first native packaging strategy for OpenUSD before any dependency lands.
- [ ] Build a representative USD corpus covering USDA, USDC, USDZ, static meshes, skinned meshes, blendshapes, animation clips, referenced layers, payloads, variants, large files, and malformed files.
- [ ] Add golden-output fixtures for layer metadata, prim counts, mesh/material counts, skeleton summaries, and animation summaries.
- [ ] Define performance metrics up front: cold-open wall time, hot query throughput, MB/s, allocation count, peak memory, and parallel scaling.
- [ ] Decide validation oracles and tooling: `usdcat`, `usddumpcrate`, `sdfdump`, `usdzip`, `usdchecker`, plus any direct OpenUSD API-based differential checks.
- [ ] If any new dependency is added, get approval first, then run the dependency/license workflow in the same change.

Phase 0 target artifacts:

- `XREngine.Usd/UsdPhase0Contracts.cs` to lock the support matrix, module boundary, fallback policy, and benchmark metrics.
- `XREngine.UnitTests/TestData/Usd/usd-corpus.manifest.json` for the committed corpus contract.
- `XREngine.UnitTests/TestData/Usd/*.summary.json` for golden summaries.
- `XREngine.Benchmarks/UsdPhase0BaselineHarness.cs` for baseline import/open reports.

Current checked-in corpus reality:

- There is no committed USD corpus in the repository yet.
- There is no native USD benchmark or summary-generation harness yet.
- There is no native or interop USD module to validate yet, so Phase 0 must establish the entire baseline instead of refining an existing subsystem.

### Exit Criteria

- [ ] We have an agreed support matrix and fallback policy.
- [ ] We have a committed corpus and benchmark plan.
- [ ] We have a chosen module boundary that will not force a rewrite midstream.
- [ ] Any dependency-risk path has an explicit proposal before implementation begins.

## File Format Ground Truth

The rules below are the minimum format and semantic ground truth the USD path must honor. USD is not just a file extension family; it is a layered scene description system. We must not flatten the design into a generic mesh file reader and call it done.

### Extension and container rules

| Item | Required handling |
|---|---|
| `.usda` | UTF-8 text layer. Detect via the `#usda` header and parse as text, not as a generic JSON-like document. |
| `.usdc` | Crate binary layer. Validate the Crate bootstrap and TOC before reading any section tables. |
| `.usd` | Sniff actual encoding. `.usd` may be USDA or USDC; never trust the extension alone. |
| `.usdz` | Treat as a package format first, not as a standalone layer. Resolve the default layer entry and then hand off to the USDA or USDC parser. |
| Package-relative assets | Keep asset resolution explicit and deterministic for packaged and unpackaged layers. |

### USDA text rules

- Parse USDA as UTF-8 text beginning with a `#usda <version>` header.
- Support layer metadata, prim specs, property specs, dictionaries, arrays, and time-sampled values needed by the supported subset.
- Treat asset-valued strings distinctly from ordinary strings and preserve the required asset delimiter syntax when writing.
- Keep the lexer/tokenizer slice-based and low-allocation; defer string materialization until semantic consumers need it.
- Writer output must be deterministic in ordering and formatting for the supported subset even if we do not preserve source style.

### USDC Crate rules

| Item | Required handling |
|---|---|
| Bootstrap header | Validate the Crate ident (`PXR-USDC`), version bytes, and TOC offset before parsing anything else. |
| Table of contents | Read named sections and validate that section spans are in bounds and non-overlapping before decoding them. |
| Required structural sections | Support `TOKENS`, `STRINGS`, `FIELDS`, `FIELDSETS`, `PATHS`, and `SPECS` as the core indexing surface. |
| Spec records | Treat specs as a path index + field-set index + spec-type tuple. |
| Field records | Treat fields as field-name token index + `ValueRep`. |
| `ValueRep` | Decode flags, type tag, and payload explicitly. Validate any payload offsets before dereferencing them. |
| Path storage | Respect path-tree encoding and reconstruct full paths lazily instead of eagerly allocating strings for every path. |
| Versioning | Gate behavior on known Crate versions. Unknown or newer versions must route to OpenUSD interop or fail closed. |
| Endianness | Keep the fast path little-endian, but make endianness handling explicit rather than relying on undefined struct reinterpretation assumptions. |

`ValueRep` decoding requirements for the structural reader:

- Bit 63: array flag.
- Bit 62: inlined-value flag.
- Bit 61: compressed-value flag.
- Bit 60: array-edit flag.
- Bits 48-55: type tag.
- Bits 0-47: payload.

### USDZ package rules

| Item | Required handling |
|---|---|
| Zip encoding | Reject compressed or encrypted entries for the engine-owned USDZ fast path. |
| Alignment | Validate that each embedded file's data begins at a 64-byte multiple from the package start. |
| Default layer | Treat the first file in the package as the default layer entry. |
| Layer slicing | Parse the embedded USDA/USDC entry directly from the package slice instead of extracting to a temp directory when the fast path is used. |
| Allowed contents | Expect USD layers and referenced resources such as textures or audio, but validate what we actually consume. |

### Layer and composition semantics

- USD serializes layers and specs, not a precomposed stage snapshot.
- Import code must define when it is reading a single layer vs resolving a composed stage from a root layer or package.
- Default prim, sublayers, references, payloads, asset paths, variant selections, and time samples are first-class semantics, not optional trivia.
- Unknown metadata and unsupported schema/value cases must either be preserved in an intermediate form or handed to the OpenUSD fallback. Do not silently discard authored data.
- Asset-resolution policy for external references, package-relative references, and search paths must be explicit and testable.

### Structural and semantic validation invariants

- USDA parser rejects malformed headers, unterminated strings, broken collections, and malformed time-sample/value syntax.
- Crate bootstrap, TOC, and all dereferenced payload offsets must stay in file bounds.
- Table indices for tokens, strings, paths, fields, field sets, and specs must be range-checked before use.
- USDZ fast path rejects compressed/encrypted entries and misaligned embedded data.
- Stage-resolution code must never partially apply composition arcs without a recorded fallback or diagnostic.
- Strict and tolerant modes may differ in how they recover, but they must share the same bounds checks.

## Performance Architecture Notes

### Two-path architecture

The recommended architecture is:

1. Managed fast path for USDA, USDZ indexing, and USDC structural indexing/selective decode.
2. OpenUSD interop fallback for unsupported versions, complex composition cases, broad value/schema coverage, and full-fidelity USDC/USDZ export.

This lets us own the hot cases without re-implementing the entire OpenUSD runtime badly.

### Shared layer IR

The managed path should build a table-driven intermediate representation rather than a per-prim object graph during parsing.

Suggested tables:

- `TokenTable`
- `StringTable`
- `PathTable`
- `SpecTable`
- `FieldTable`
- `FieldSetTable`
- `LayerMetadataTable`
- optional `CompositionArcTable` and `TimeSampleTable` once the supported subset grows past pure structural indexing

Goals of the IR:

- cheap indexing and iteration,
- lazy path reconstruction,
- lazy value decode,
- explicit storage of unresolved or fallback-routed authored data,
- deterministic export input for the USDA writer and OpenUSD-backed export bridge.

### Parsing pipeline

Recommended stages:

1. Container detection: sniff USDA vs USDC vs USDZ and resolve the default layer when packaged.
2. Structural parse/index: tokenize USDA or index Crate tables without building engine objects.
3. Value decode/composition: decode requested values, resolve composition arcs for the supported subset, and build a composed stage view or fallback request.
4. Engine bridge: map composed USD data into `SceneNode`, `XRMesh`, `XRMaterial`, animation clips, and prefab metadata.

### Allocation discipline extras

- Use `ArrayPool<T>` for tokenizer buffers, zip-central-directory parsing, decompression scratch, and temporary decode buffers.
- Avoid allocating strings for every token/path on load; decode lazily.
- Prefer memory-mapped reads for large Crate/package files and streamed writes for USDA output.
- Keep per-file caches thread-local or stripe them to avoid lock contention in parallel import workloads.
- Treat any large-object-heap churn in parse/decode hot paths as a regression to fix, not a warning to ignore.

### Validation oracles and tooling

| Tool | Use |
|---|---|
| `usdcat` | Convert between encodings, compact layers, and compare round-trip output. |
| `usddumpcrate` | Inspect Crate structure during parser validation. |
| `sdfdump` | Inspect layer/spec structure and metadata. |
| `usdzip` | Validate package behavior and author reference packages for tests. |
| `usdchecker` | Validate authored/exported assets. |
| OpenUSD runtime/API | Differential oracle for composition behavior, schema/value coverage, and writer output. |

### Specific benchmark targets

- Cold-open index time for large USDC assets.
- Hot query throughput for spec/field enumeration and value decode.
- USDA parse/write throughput and allocation profile.
- USDZ package open without extraction.
- End-to-end model/prefab import time on representative assets.
- Parallel scaling across many independent files.

## Phase 1: Container Detection, USDA Reader, and USDZ Package Reader

- [ ] Implement format sniffing for `.usd` so the parser selects USDA vs USDC from file contents.
- [ ] Implement a USDZ central-directory reader/validator that rejects compressed or encrypted fast-path inputs.
- [ ] Resolve the default layer from USDZ and support direct slicing into the embedded USDA/USDC file.
- [ ] Implement a low-allocation USDA tokenizer handling headers, identifiers, strings, asset strings, braces, brackets, collections, numeric literals, and time-sample syntax required by the supported subset.
- [ ] Parse USDA layer metadata, prim specs, property specs, and field values into the shared IR instead of directly creating engine objects.
- [ ] Add malformed USDA/USDZ fixtures for truncated headers, malformed asset strings, broken collections, compressed package entries, and misaligned package contents.
- [ ] Add tokenizer/package microbenchmarks to `XREngine.Benchmarks`.

### Exit Criteria

- [ ] `.usd` sniffing is deterministic and tested.
- [ ] USDZ default-layer resolution works on the committed corpus without temp extraction.
- [ ] USDA structural parsing succeeds on the initial text corpus.
- [ ] Malformed USDA/USDZ inputs fail closed with actionable diagnostics.

## Phase 2: USDC Structural Index Reader

- [ ] Implement Crate bootstrap parsing with explicit ident/version/TOC validation.
- [ ] Implement TOC parsing and bounds-check all section spans before decode.
- [ ] Parse `TOKENS`, `STRINGS`, `PATHS`, `FIELDS`, `FIELDSETS`, and `SPECS` into the shared IR.
- [ ] Keep large-file access compatible with memory-mapped USDC files and embedded USDC slices from USDZ packages.
- [ ] Implement lazy path reconstruction and lazy `ValueRep` classification.
- [ ] Add corruption tests for invalid TOC entries, overlapping sections, bad indices, bad payload offsets, and unsupported version gates.
- [ ] Add cold-open index benchmarks for representative Crate files.

### Exit Criteria

- [ ] The structural reader can index committed USDC fixtures without materializing a full object graph.
- [ ] Invalid or unsupported Crate inputs fail cleanly or route to the fallback path deterministically.
- [ ] Cold-open and indexing allocations are benchmarked and audited.

## Phase 3: Typed Layer IR, Value Decode, and Composition Primitives

- [ ] Extend the IR to cover authored value types required by the supported subset: scalars, vectors, matrices, tokens, strings, assets, numeric arrays, dictionaries, and time samples.
- [ ] Implement lazy `ValueRep` decode for the prioritized USDC types instead of decoding everything eagerly.
- [ ] Implement USDA value decoding against the same type model so text and binary paths converge into the same IR.
- [ ] Add explicit modeling for composition primitives: sublayers, references, payloads, variant selections, default prim, and asset resolution state.
- [ ] Define the supported managed-composition subset vs the cases that must route to OpenUSD.
- [ ] Implement an asset resolver policy for relative paths, package-relative assets, and external references.
- [ ] Preserve unsupported authored metadata or record enough fallback information to reopen the same asset through OpenUSD.
- [ ] Add differential tests comparing IR summaries and selected values against OpenUSD tooling/runtime output.

### Exit Criteria

- [ ] USDA and USDC converge into one typed IR for the supported subset.
- [ ] Composition behavior for the supported subset is explicit, deterministic, and tested.
- [ ] Unsupported composition/value/schema cases are routed or reported instead of silently dropped.

## Phase 4: Scene Import Bridge and Model/Prefab Integration

- [ ] Map composed USD prims into the current scene assembly path without regressing async publication behavior.
- [ ] Import authored hierarchy and Xform semantics into `SceneNode` with explicit up-axis, unit-scale, and local-vs-world transform handling.
- [ ] Import mesh topology, positions, normals, tangents, UV sets, color sets, material bindings, and subset-based assignments into engine-native mesh/material structures.
- [ ] Decide the supported schema subset for the first bridge pass, likely including `Xform`, `Mesh`, `Material`, `Shader`, and the binding metadata needed by the corpus.
- [ ] Preserve or intentionally replace current remap seeding behavior for textures and materials through `ModelImporter` and `XRPrefabSource`.
- [ ] Keep heavy mesh decode compatible with the engine's async mesh processing and publication controls.
- [ ] Add targeted unit tests for static scene/model import parity on representative USD assets.
- [ ] Add macro benchmarks comparing native USD import vs the current generic non-FBX path where that comparison is meaningful.

### Exit Criteria

- [ ] Static USD scene/model import is functional end-to-end through the engine's normal import flow.
- [ ] Material and texture remap persistence still works.
- [ ] Async mesh processing behavior remains correct and benchmarked.

## Phase 5: Rigging, Animation, Instancing, and Richer Scene Semantics

- [ ] Import `UsdSkel` or equivalent supported rigging data: skeletons, bindings, inverse bind data, weights, and blendshape channels for the target corpus.
- [ ] Import time-sampled transform/property animation and decide the evaluation/baking policy for the engine's animation storage.
- [ ] Decide the supported policy for prototypes/instanceability and how instances map onto engine scene nodes/components.
- [ ] Decide the supported policy for variant selections during import and how user-selected variants are surfaced in import options if needed.
- [ ] Add tests for weight normalization, bind-pose stability, animation key ordering, instance reconstruction, and authored visibility/purpose handling where relevant.
- [ ] Add differential checks against OpenUSD for skinned/animated assets and variant/reference-heavy assets.

### Exit Criteria

- [ ] Skinned and animation-heavy USD assets import correctly through the supported native/fallback path.
- [ ] Instance and variant behavior is explicit rather than accidental.
- [ ] Differential tests cover the supported rigging and animation subset.

## Phase 6: Export Pipeline

- [ ] Define an engine-neutral USD export document model that can feed both the managed USDA writer and the OpenUSD-backed binary/package writer path.
- [ ] Implement a deterministic managed USDA writer for the supported subset.
- [ ] Export layer metadata, default prim, authored hierarchy, transforms, meshes, materials, texture references, skeletons, blendshapes, and animation data for the supported subset.
- [ ] Decide the supported export behavior for references, payloads, variants, package-relative assets, and external texture paths.
- [ ] Implement the full-fidelity USDC/USDZ export path through OpenUSD interop instead of hand-writing Crate/package output early.
- [ ] Add structural reparse tests for USDA output through the managed reader before involving external tools.
- [ ] Add round-trip tests through OpenUSD tools/runtime for supported USDA, USDC, and USDZ export cases.

### Deferred managed binary-writer requirements

- [ ] Only consider a hand-written USDC writer after the managed reader, IR, and USDA writer are stable and benchmark-backed.
- [ ] If a managed Crate writer is later justified, scope it to a narrow, explicitly version-gated subset first.
- [ ] Do not take on a full hand-written USDZ writer beyond package assembly until the dependency/fidelity tradeoffs are proven.

### Exit Criteria

- [ ] Supported USDA exports are deterministic and reopen cleanly.
- [ ] Supported USDC/USDZ exports validate through OpenUSD-backed tooling.
- [ ] Export output is structurally sound and semantically round-trippable for the supported subset.

## Phase 7: OpenUSD Interop, Native Packaging, and Fallback Routing

- [ ] Propose the native dependency plan before landing any code: binaries, packaging, binding generation, update strategy, and Windows-first distribution story.
- [ ] Confirm license compatibility for OpenUSD and any binding-generation/runtime dependencies before integration.
- [ ] Build the minimal interop surface needed for fallback import/export rather than exposing the entire OpenUSD API blindly.
- [ ] Isolate interop behind a separate assembly or backend boundary so the managed fast path stays independently testable.
- [ ] Route unsupported Crate versions, unsupported schema/value cases, complex composition requests, and full-fidelity USDC/USDZ export through the interop path.
- [ ] Add focused smoke tests that prove fallback routing works and that native packaging is discoverable in editor/server/client workflows.
- [ ] If dependencies land, run the dependency/license workflow and commit refreshed dependency docs in the same change.

### Exit Criteria

- [ ] Native packaging and fallback routing work on the supported Windows workflow.
- [ ] The managed fast path remains usable without turning every USD import into a native interop call.
- [ ] Dependency/license requirements are satisfied and documented.

## Phase 8: Engine Cutover, Import Options, and Workflow Cleanup

- [ ] Introduce format-specific dispatch so USD no longer relies on the generic non-FBX path once the native/fallback USD path is ready.
- [ ] Decide whether USD gets its own `UsdImportOptions` type or a clean extension of `ModelImportOptions` with explicit USD-native settings.
- [ ] Surface explicit USD-native options where needed: composition policy, payload load policy, variant selection overrides, up-axis/unit policy, and fallback controls.
- [ ] Preserve editor, asset-pipeline, and prefab workflows while swapping the backend.
- [ ] Update `docs/features/model-import.md` and any related workflow docs once USD support becomes user-visible.
- [ ] Decide whether broader scene/stage import deserves a dedicated asset type later, or whether model/prefab import remains the only supported entry point for v1.

### Exit Criteria

- [ ] `.usd*` imports use the new USD path by default.
- [ ] Current workflows still work without hidden dependence on the old generic path.
- [ ] User-visible docs describe the new behavior accurately.

## Phase 9: Hardening, Corpus Expansion, and Regression Gates

- [ ] Expand the corpus across exporters, DCC tools, file sizes, composition patterns, and malformed edge cases.
- [ ] Add malformed-file regression tests and a lightweight fuzzing harness for USDA/USDC/USDZ parser hardening if worthwhile.
- [ ] Add sustained multi-file parallel stress tests.
- [ ] Add benchmark regression gates so parser, index, or writer slowdowns are caught early.
- [ ] Run allocation audits against touched importer/exporter code and eliminate nearby low-risk issues.
- [ ] Validate exported assets with `usdchecker`, `usdcat`, and at least one downstream consumer when practical.
- [ ] Remove obsolete assumptions or temporary fallbacks once the new path has proved itself.

### Exit Criteria

- [ ] The USD path is stable enough for normal development workflows.
- [ ] Performance and correctness regressions are measurable and caught automatically.
- [ ] Remaining unsupported USD features are explicit backlog items, not unknowns.

## Suggested Initial File/Project Touchpoints

- `XRENGINE/Core/ModelImporter.cs`
- `XRENGINE/Models/Meshes/ModelImportOptions.cs`
- `XRENGINE/Scene/Prefabs/XRPrefabSource.cs`
- `XREngine.UnitTests/`
- `XREngine.Benchmarks/`
- new engine-neutral USD core parser/writer project or namespace

## Deferred Until After Core Import/Export Is Stable

- [ ] Full arbitrary schema authoring beyond the supported asset subset.
- [ ] Source-style-preserving USDA export and comment-preserving round-tripping.
- [ ] A fully hand-written general-purpose Crate writer.
- [ ] Full parity with every OpenUSD schema/domain module without fallback assistance.