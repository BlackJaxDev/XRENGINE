# Native FBX Import And Export TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md), [Model Import](../../../developer-guides/assets/model-import.md)
Validation: [Asset Import Validation](../../testing/assets/asset-import-validation.md#native-fbx)

## Current State

The native FBX backend in `XREngine.Fbx/` is the default `.fbx` route through `NativeFbxSceneImporter`. It reads binary and ASCII files and imports hierarchy, meshes, materials, texture references, skeletons, skinning, blendshapes, and animation stacks. `FbxBinaryWriter` exports binary files. No ASCII writer exists. Assimp stays available through `ModelImportOptions.FbxBackend = Assimp`. The test corpus in `XREngine.UnitTests/TestData/Fbx/` holds ASCII fixtures only. Prefab sub-asset externalization (leaves-first write order, placeholder files, abort cleanup) is in `XREngine.Editor/Importers/ThirdParty/AssetManager.ThirdPartyImport.cs`.

## Open Code Items

### Corpus And Fixtures

- [ ] Add binary FBX 7400 and 7500 fixtures for static meshes, skinned animation, blendshapes, embedded textures, large files, malformed files, and transform semantics. `XREngine.UnitTests/TestData/Fbx/`, `fbx-corpus.manifest.json`. Done when: the manifest lists binary fixtures and `FbxPhase0CorpusTests` loads them.
- [ ] Add a structural-scan test over the binary corpus. `FbxPhase1StructuralParserTests`. Done when: the test passes for every binary fixture.
- [ ] Add binary round-trip tests (FBX to internal to FBX to reader) over the binary corpus. `FbxPhase5BinaryExportTests`. Done when: the tests pass for every binary fixture.
- [ ] Add a determinism test for export. `FbxBinaryWriter`. Done when: two exports of the same input give identical bytes.

### Performance And Allocation

- [ ] Add macro benchmarks that compare native FBX import with Assimp FBX import (wall time, MB/s, allocation count, peak memory, parallel scaling). `XREngine.Benchmarks/FbxPhase*Harness.cs`. Done when: the benchmark reports both backends for the static corpus.
- [ ] Remove LINQ, boxing, capturing delegates, per-node and per-property allocations, and large object heap churn from parser and importer hot paths. `XREngine.Fbx/`, `NativeFbxSceneImporter`. Done when: the allocation benchmark shows no per-node or per-property allocations outside output buffers and pooled scratch.

### Import Behavior

- [ ] Keep or replace the Assimp behaviors for mesh splitting, async mesh publication, and generated renderer async flags. `NativeFbxSceneImporter`. Done when: the native path sets the same flags or the guide documents the new behavior.
- [ ] Add a test for material and texture remap persistence across native FBX import and reimport. `NativeFbxImporterTests`. Done when: remaps survive a reimport.
- [ ] Remove obsolete Assimp FBX workarounds. `ModelAssetImporter`. Done when: no FBX-specific Assimp workaround code remains.

### Rigging And Animation Tests

- [ ] Add tests for weight normalization, bind-pose stability, animation key order, and root-motion transforms. `XREngine.UnitTests/Core/FbxPhase*Tests.cs`. Done when: the tests pass on the skinned fixtures.
- [ ] Add semantic checks for vertex AABB plausibility, normal length, UV range, acyclic joint parents, and monotonic key times. `NativeFbxSceneImporter` or a test helper. Done when: a malformed fixture fails each check with a diagnostic.
- [ ] Add differential tests against a reference parser (ufbx first, OpenFBX second) for the rigging and animation subset. Done when: imported transforms and animation outputs match the reference within tolerance.

### Prefab Sub-Asset Externalization Tests

- [ ] Add an FBX reimport regression test. `AssetManager.ThirdPartyImport.cs`. Done when: the root prefab YAML has no inline `AnimationClip`, `XRMaterial`, `XRTexture2D`, or `XRMesh` blocks, each reference resolves to one file, and a reload gives the same mesh, material, animation, and texture counts.
- [ ] Add a shared-asset deduplication test. Done when: two prefabs that share a texture write it once.
- [ ] Add a failure-rollback test. Done when: a simulated `IOException` during placeholder creation leaves no placeholder files.
- [ ] Find other `XRAsset` types (font glyph sets, shaders) that inline instead of writing a reference. `XRAssetYamlTypeConverter`. Done when: each type writes a reference when `ShouldWriteReference` is true.

### ASCII Writer (Deferred)

- [ ] Add an ASCII writer with the magic comment, version string, `Name:` nodes, property lists, balanced braces, scalar and `*count { a: ... }` arrays, and a `Connections` block in deterministic order. `XREngine.Fbx/`. Done when: the ASCII reader reparses its output for the corpus.

## Decisions Needed

- [ ] Does ASCII write mode emit one canonical style, or keep the source style in debug builds? Owner: asset pipeline.
- [ ] When can Assimp stop being a supported FBX route for normal development? Owner: asset pipeline.

## Out Of Scope

- Source-style-preserving ASCII export and comment preservation.
- Rare constraints, advanced control rigs, and deformers beyond skinning and blendshapes.
- Layered material stacks beyond the supported material path.
- Full parity with every Autodesk SDK corner case.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/assets/fbx-import-export-todo.md`

- [ ] Current engine behaviors that matter to users remain intact or are replaced by clearly better semantics with docs updates.
- [ ] Prove the binary structural scan succeeds on the binary corpus once the planned 7400 and 7500 fixtures are sourced.
- [ ] Expand the corpus across exporters, DCC tools, file sizes, and edge cases.
- [ ] Finish tokenization and structural validation benchmarking and allocation audits.
- [ ] Emit `Name:` nodes, comma-separated property lists, balanced braces, and deterministic ordering.
- [ ] Decide whether ASCII write mode normalizes to a single canonical style or preserves source style only in special round-trip/debug builds.
- [ ] Source-style-preserving ASCII export, including comment preservation and minimal-diff round-tripping.
- [ ] Exotic deformers beyond the current skinning and blendshape subset.
