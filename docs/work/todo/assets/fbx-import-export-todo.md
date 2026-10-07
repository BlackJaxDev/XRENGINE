# Native FBX Import And Export TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md), [Model Import](../../../developer-guides/assets/model-import.md)
Validation: [Asset Import Validation](../../testing/assets/asset-import-validation.md#native-fbx)

## Current State

The native FBX backend in `XREngine.Fbx/` is the default `.fbx` route through `NativeFbxSceneImporter`. It reads binary and ASCII files and imports hierarchy, meshes, materials, texture references, skeletons, skinning, blendshapes, and animation stacks. `FbxBinaryWriter` exports binary files. No ASCII writer exists. Assimp stays available through `ModelImportOptions.FbxBackend = Assimp`. The test corpus in `XREngine.UnitTests/TestData/Fbx/` holds ASCII fixtures only. Prefab sub-asset externalization is in `XREngine.Editor/Importers/ThirdParty/AssetManager.ThirdPartyImport.cs`.

## Open Code Items

### Corpus And Fixtures

- [ ] Add binary FBX 7400 and 7500 fixtures for static meshes, skinned animation, blendshapes, embedded textures, large files, malformed files, and transform semantics. `XREngine.UnitTests/TestData/Fbx/`, `fbx-corpus.manifest.json`. Done when: the manifest lists binary fixtures and `FbxPhase0CorpusTests` loads them.
- [ ] Expand the corpus across exporters, DCC tools, file sizes, and edge cases. `XREngine.UnitTests/TestData/Fbx/`. Done when: the manifest lists the new assets and the corpus tests cover them.
- [ ] Add a structural-scan test over the binary corpus. `FbxPhase1StructuralParserTests`. Done when: the test passes for every binary fixture.
- [ ] Add binary round-trip tests over the binary corpus. `FbxPhase5BinaryExportTests`. Done when: FBX to internal to FBX to reader passes for every binary fixture.
- [ ] Add a determinism test for export. `FbxBinaryWriter`. Done when: two exports of the same input give identical bytes.

### Performance And Allocation

- [ ] Add macro benchmarks that compare native FBX import with Assimp FBX import. `XREngine.Benchmarks/FbxPhase*Harness.cs`. Done when: the benchmark reports wall time, MB/s, allocation count, peak memory, and parallel scaling for both backends.
- [ ] Finish tokenization and structural-validation allocation audits. `XREngine.Fbx/`, `NativeFbxSceneImporter`. Done when: the allocation benchmark identifies parser and importer allocations by category.
- [ ] Remove LINQ, boxing, capturing delegates, per-node and per-property allocations, and large object heap churn from parser and importer hot paths. `XREngine.Fbx/`, `NativeFbxSceneImporter`. Done when: the allocation benchmark shows no per-node or per-property allocations outside output buffers and pooled scratch.

### Import Behavior

- [ ] Keep or replace current engine behaviors for mesh splitting, async mesh publication, generated renderer async flags, material remaps, and texture remaps. `NativeFbxSceneImporter`, `ModelAssetImporter`. Done when: the native path preserves the behavior or the guide documents a better supported behavior.
- [ ] Add a test for material and texture remap persistence across native FBX import and reimport. `NativeFbxImporterTests`. Done when: remaps survive a reimport.
- [ ] Remove obsolete Assimp FBX workarounds. `ModelAssetImporter`. Done when: no FBX-specific Assimp workaround code remains.

### Rigging And Animation Tests

- [ ] Add tests for weight normalization, bind-pose stability, animation key order, and root-motion transforms. `XREngine.UnitTests/Core/FbxPhase*Tests.cs`. Done when: the tests pass on the skinned fixtures.
- [ ] Add semantic checks for vertex AABB plausibility, normal length, UV range, acyclic joint parents, and monotonic key times. `NativeFbxSceneImporter` or a test helper. Done when: a malformed fixture fails each check with a diagnostic.
- [ ] Add differential tests against a reference parser, using ufbx first and OpenFBX second. Done when: imported transforms and animation outputs match the reference within tolerance for the rigging and animation subset.

### Prefab Sub-Asset Externalization Tests

- [ ] Add an FBX reimport regression test. `XREngine.Editor/Importers/ThirdParty/AssetManager.ThirdPartyImport.cs`. Done when: the root prefab YAML has no inline `AnimationClip`, `XRMaterial`, `XRTexture2D`, or `XRMesh` blocks, each reference resolves to one file, and a reload gives the same mesh, material, animation, and texture counts.
- [ ] Add a shared-asset deduplication test. Done when: two prefabs that share a texture write it once.
- [ ] Add a failure-rollback test. Done when: a simulated `IOException` during placeholder creation leaves no placeholder files.
- [ ] Find other `XRAsset` types, such as font glyph sets and shaders, that inline instead of writing a reference. `XRAssetYamlTypeConverter`. Done when: each type writes a reference when `ShouldWriteReference` is true.

### ASCII Writer

- [ ] Add an ASCII writer with the magic comment, version string, `Name:` nodes, property lists, balanced braces, scalar and `*count { a: ... }` arrays, and a `Connections` block in deterministic order. `XREngine.Fbx/`. Done when: the ASCII reader reparses its output for the corpus.

## Decisions Needed

- [ ] Decide whether ASCII write mode emits one canonical style, or keeps the source style in debug builds. Owner: asset pipeline.
- [ ] Decide when Assimp can stop being a supported FBX route for normal development. Owner: asset pipeline.

## Out Of Scope

- Source-style-preserving ASCII export and comment preservation.
- Rare constraints, advanced control rigs, and deformers beyond skinning and blendshapes.
- Layered material stacks beyond the supported material path.
- Full parity with every Autodesk SDK corner case.
