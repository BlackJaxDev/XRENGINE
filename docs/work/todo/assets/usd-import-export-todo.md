# USD Import And Export TODO

Last Updated: 2026-10-06
Status: Planned
Design: [USD Import And Export Design](../../design/assets/usd-import-export-design.md)
Validation: [Asset Import Validation](../../testing/assets/asset-import-validation.md#usd)

## Current State

No USD-specific importer or exporter exists. `XREngine.Runtime.ModelAssetPipeline/Importing/ModelPrefabSourceExtensions.cs` lists `usd`, `usda`, `usdc`, and `usdz` as model-source extensions. `ModelAssetImporter` still sends USD files through the generic Assimp path. There is no `XREngine.Usd` project, no USD option type, no USD fixtures, no USD tests, and no USD benchmarks. An OpenUSD dependency needs owner approval and the dependency and license workflow before it lands.

## Open Code Items

### Project And Corpus

- [ ] Create an engine-neutral `XREngine.Usd` project with a contract type for the support matrix, module boundary, and fallback policy. Done when: the project builds and `ModelAssetPipeline` references it.
- [ ] Add a USD corpus with USDA, USDC, USDZ, static, skinned, blendshape, animated, reference, payload, variant, large, and malformed files. `XREngine.UnitTests/TestData/Usd/usd-corpus.manifest.json`, `*.summary.json`. Done when: a corpus test loads the manifest and golden summaries.
- [ ] Add a baseline benchmark harness. `XREngine.Benchmarks/UsdBaselineHarness.cs`. Done when: it reports cold-open time, MB/s, allocations, peak memory, and parallel scaling.

### Containers And USDA

- [ ] Sniff `.usd` contents to select USDA or USDC. Done when: a test covers both encodings with the `.usd` extension.
- [ ] Add a USDZ central-directory reader that rejects compressed, encrypted, and misaligned entries, resolves the default layer, and slices it without extraction. Done when: tests cover each rejection and the default-layer slice.
- [ ] Add a low-allocation USDA tokenizer and parser for the supported subset, including headers, identifiers, strings, asset strings, braces, brackets, collections, numeric literals, and time-sample syntax. Done when: the text corpus parses and malformed inputs fail with diagnostics.
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

- [ ] Add an engine-neutral USD export document and a deterministic managed USDA writer for the supported subset. Done when: the managed reader reparses the writer output and OpenUSD tooling validates supported exports without semantic corruption.
- [ ] Add USDC and USDZ export through OpenUSD interop. Done when: exports reopen through the interop reader.

### OpenUSD Interop

- [ ] Add a minimal OpenUSD interop assembly behind a backend boundary, and route unsupported Crate versions, schema or value cases, complex composition, and full-fidelity export to it. Done when: fallback routing tests pass. Needs owner approval first.
- [ ] Propose the native dependency plan before landing code. Done when: binaries, packaging, binding generation, update strategy, and Windows-first distribution story have owner review.

### Cutover

- [ ] Add format-specific dispatch for `.usd*` in `ModelAssetImporter`, and add USD options for composition policy, payload load policy, variant overrides, up-axis and unit policy, and fallback controls. Done when: `.usd*` imports no longer use the generic Assimp path.
- [ ] Add malformed-file regression tests, a parallel multi-file stress test, and benchmark regression gates. Done when: the tests run in the unit test project.
- [ ] Remove obsolete assumptions or temporary fallbacks after the managed USD path proves itself. Done when: no dead fallback code remains in the USD import path.

## Decisions Needed

- [ ] Choose the supported v1 subset for import and export. Owner: asset pipeline.
- [ ] Choose Windows-first native packaging for OpenUSD and complete license review. Owner: project owner.
- [ ] Choose `UsdImportOptions`, or USD settings inside `ModelImportOptions`. Owner: asset pipeline.
- [ ] Choose the policy for prototypes, instanceability, and variant selection during import. Owner: asset pipeline.
- [ ] Decide whether stage import gets its own asset type later, or model and prefab import stay the only entry point. Owner: asset pipeline.
- [ ] Choose validation oracles and tooling: `usdcat`, `usddumpcrate`, `sdfdump`, `usdzip`, `usdchecker`, and direct OpenUSD API checks. Owner: asset pipeline.

## Out Of Scope

- Arbitrary schema authoring beyond the asset subset.
- Source-style-preserving USDA export.
- A hand-written general-purpose Crate writer.
- Full parity with every OpenUSD schema module without fallback.
