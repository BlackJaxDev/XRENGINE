# USD Import And Export TODO

Last Updated: 2026-10-06
Status: Planned
Design: [USD Import And Export Design](../../design/assets/usd-import-export-design.md)
Validation: [Asset Import Validation](../../testing/assets/asset-import-validation.md#usd)

## Current State

No USD code exists. `XREngine.Runtime.Core/Scene/Prefabs/XRPrefabSource.cs` lists `usd`, `usda`, `usdc`, and `usdz` as third-party extensions, and `ModelAssetImporter` sends them to the generic Assimp path. There is no `XREngine.Usd` project, no USD option type, no USD fixtures, no USD tests, and no USD benchmarks. An OpenUSD dependency needs owner approval and the dependency and license workflow before it lands.

## Open Code Items

### Project And Corpus

- [ ] Create an engine-neutral `XREngine.Usd` project with a contract type for the support matrix, module boundary, and fallback policy. Done when: the project builds and `ModelAssetPipeline` references it.
- [ ] Add a USD corpus (USDA, USDC, USDZ; static, skinned, blendshape, animated; references, payloads, variants; large and malformed files) with a manifest and golden summaries. `XREngine.UnitTests/TestData/Usd/usd-corpus.manifest.json`, `*.summary.json`. Done when: a corpus test loads the manifest.
- [ ] Add a baseline benchmark harness. `XREngine.Benchmarks/UsdBaselineHarness.cs`. Done when: it reports cold-open time, MB/s, allocations, peak memory, and parallel scaling.

### Containers And USDA

- [ ] Sniff `.usd` contents to select USDA or USDC. Done when: a test covers both encodings with the `.usd` extension.
- [ ] Add a USDZ central-directory reader that rejects compressed, encrypted, and misaligned entries, resolves the default layer, and slices it without extraction. Done when: tests cover each rejection and the default-layer slice.
- [ ] Add a low-allocation USDA tokenizer and a parser that fills the shared layer IR. Done when: the text corpus parses and malformed inputs fail with diagnostics.
- [ ] Add tokenizer and package microbenchmarks. `XREngine.Benchmarks`. Done when: the benchmarks run.

### USDC Structural Reader

- [ ] Parse the Crate bootstrap and TOC with bounds and overlap checks, then index `TOKENS`, `STRINGS`, `PATHS`, `FIELDS`, `FIELDSETS`, and `SPECS` into the IR from memory-mapped files or USDZ slices. Done when: the USDC corpus indexes without a full object graph.
- [ ] Add lazy path reconstruction and lazy `ValueRep` classification. Done when: a test indexes a file without path string allocation.
- [ ] Add corruption tests for bad TOC entries, overlapping sections, bad indices, bad payload offsets, and unknown versions. Done when: each case fails closed or routes to the fallback.

### Typed IR And Composition

- [ ] Decode scalars, vectors, matrices, tokens, strings, assets, numeric arrays, dictionaries, and time samples from USDA and USDC into one typed IR. Done when: text and binary forms of the same layer give equal IR summaries.
- [ ] Model sublayers, references, payloads, variant selections, default prim, and asset-resolution state, and add an asset resolver for relative, package-relative, and external paths. Done when: composition tests pass for the managed subset.
- [ ] Keep unsupported authored metadata, or record enough data to reopen the asset through OpenUSD. Done when: a test shows an unsupported field is kept or routed.

### Scene Import Bridge

- [ ] Map composed prims to `SceneNode` with explicit up-axis, unit-scale, and transform handling, and import mesh topology, normals, tangents, UV sets, color sets, material bindings, and subsets. Done when: static corpus import tests pass through `ModelAssetImporter`.
- [ ] Keep texture and material remap seeding through `ModelAssetImporter` and `XRPrefabSource`, and keep async mesh publication. Done when: a remap persistence test passes.
- [ ] Import `UsdSkel` skeletons, bindings, inverse bind data, weights, blendshapes, and time-sampled animation. Done when: tests for weight normalization, bind-pose stability, key order, instances, and visibility pass.

### Export

- [ ] Add an engine-neutral USD export document and a deterministic managed USDA writer for the supported subset. Done when: the managed reader reparses the writer output.
- [ ] Add USDC and USDZ export through OpenUSD interop. Done when: exports reopen through the interop reader.

### OpenUSD Interop

- [ ] Add a minimal OpenUSD interop assembly behind a backend boundary, and route unsupported Crate versions, schema or value cases, complex composition, and full-fidelity export to it. Done when: fallback routing tests pass. Needs owner approval first.

### Cutover

- [ ] Add format-specific dispatch for `.usd*` in `ModelAssetImporter`, and add USD options (composition policy, payload load policy, variant overrides, up-axis and unit policy, fallback controls). Done when: `.usd*` imports no longer use the generic Assimp path.
- [ ] Add malformed-file regression tests, a parallel multi-file stress test, and benchmark regression gates. Done when: the tests run in the unit test project.

## Decisions Needed

- [ ] Supported v1 subset for import and export. Owner: asset pipeline.
- [ ] Windows-first native packaging for OpenUSD, and license review. Owner: project owner.
- [ ] `UsdImportOptions` type, or USD settings in `ModelImportOptions`? Owner: asset pipeline.
- [ ] Policy for prototypes, instanceability, and variant selection during import. Owner: asset pipeline.
- [ ] Does stage import get its own asset type later, or does model and prefab import stay the only entry point? Owner: asset pipeline.

## Out Of Scope

- Arbitrary schema authoring beyond the asset subset.
- Source-style-preserving USDA export.
- A hand-written general-purpose Crate writer.
- Parity with every OpenUSD schema module without fallback.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/assets/usd-import-export-todo.md`

- [ ] Unsupported Crate versions, schema/value cases, or composition behaviors must fall back to OpenUSD interop or fail with actionable diagnostics. Do not silently guess.
- [ ] Supported USDA exports round-trip through OpenUSD tooling without semantic corruption.
- [ ] Decide validation oracles and tooling: `usdcat`, `usddumpcrate`, `sdfdump`, `usdzip`, `usdchecker`, plus any direct OpenUSD API-based differential checks.
- [ ] Implement a low-allocation USDA tokenizer handling headers, identifiers, strings, asset strings, braces, brackets, collections, numeric literals, and time-sample syntax required by the supported subset.
- [ ] `.usd` sniffing is deterministic and tested.
- [ ] Implement the full-fidelity USDC/USDZ export path through OpenUSD interop instead of hand-writing Crate/package output early.
- [ ] Propose the native dependency plan before landing any code: binaries, packaging, binding generation, update strategy, and Windows-first distribution story.
- [ ] Expand the corpus across exporters, DCC tools, file sizes, composition patterns, and malformed edge cases.
- [ ] Validate exported assets with `usdchecker`, `usdcat`, and at least one downstream consumer when practical.
- [ ] Remove obsolete assumptions or temporary fallbacks once the new path has proved itself.
- [ ] Full parity with every OpenUSD schema/domain module without fallback assistance.
