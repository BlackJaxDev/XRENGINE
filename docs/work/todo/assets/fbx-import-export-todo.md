# Native FBX Import/Export TODO

Active follow-up tracker for native FBX import/export hardening. Completed design and shipped behavior now live in [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md). This file intentionally tracks only remaining work and deferred work.

## Current Status

The native FBX backend is the default `.fbx` import route. Static meshes, authored hierarchy, materials, texture references, skeletons, skinning, blendshapes, animation stacks, binary import, ASCII import, and binary export are implemented for the supported v1 subset.

Assimp remains available through `ModelImportOptions.FbxBackend = Assimp` while the native path finishes corpus expansion, performance validation, differential rigging checks, and compatibility cleanup.

## Remaining Acceptance Gates

- [ ] Native FBX import beats the current Assimp FBX path on a representative XRENGINE corpus.
- [ ] Full-file import scales across multiple files in parallel without shared-state crashes or global locks.
- [ ] Parser/tokenizer allocation profile is near-zero outside final output buffers and intentionally pooled scratch storage.
- [ ] Round-trip validation passes for the supported subset: FBX -> internal -> FBX -> reference importer.
- [ ] Current engine behaviors that matter to users remain intact or are replaced by clearly better semantics with docs updates.
- [ ] The native path is stable enough to be the only supported FBX path for normal development workflows.
- [ ] Remaining unsupported FBX features are explicit backlog items, not unknowns.

## Corpus And Format Validation

- [ ] Source and check in representative binary FBX 7400 and 7500 fixtures for static meshes, skinned animation, blendshapes, embedded textures, large files, malformed files, and transform semantics.
- [ ] Prove the binary structural scan succeeds on the binary corpus once the planned 7400 and 7500 fixtures are sourced.
- [ ] Expand the corpus across exporters, DCC tools, file sizes, and edge cases.
- [ ] Preserve embedded-texture and external-texture workflows at parity with current engine behavior where practical.
- [ ] Validate exported files with ImHex FBX pattern assertions.
- [ ] Validate exported files with at least one external reference parser, with ufbx preferred and OpenFBX secondary.

## Performance And Allocation Hardening

- [ ] Keep heavy array decode parallel and pool-backed.
- [ ] Finish tokenization and structural validation benchmarking and allocation audits.
- [ ] Add macro benchmarks comparing native FBX import against the current Assimp FBX import.
- [ ] Gate the native importer against the initial static corpus so regressions in wall time, MB/s, allocation count, peak memory, and parallel scaling are caught early.
- [ ] Re-audit parser and importer hot paths for LINQ, boxing, capture-heavy delegates, per-node allocations, per-property allocations, and large object heap churn.
- [ ] Treat any hot-path allocation regressions discovered during implementation as bugs.

## Import Behavior Follow-Ups

- [ ] Preserve or intentionally replace current import behaviors for mesh splitting, async mesh publication, and generated renderer async flags.
- [ ] Prove material and texture remap persistence still works across native FBX import and reimport.
- [ ] Remove obsolete Assimp FBX workarounds once the native path has proved itself.
- [ ] Document any intentional deviations from current Assimp FBX behavior.

## Rigging, Animation, And Semantic Checks

- [ ] Validate imported transforms and animation outputs against reference parsers on humanoid and non-humanoid assets.
- [ ] Add tests for weight normalization, bind-pose stability, animation key ordering, and root-motion-related transform correctness.
- [ ] Add semantic sanity checks for vertex AABB plausibility, normal vector length, UV ranges, skeleton joint parent-index acyclicity, and monotonic animation keyframe timestamps.
- [ ] Add differential tests covering the supported rigging and animation subset.

## Binary Export Follow-Ups

- [ ] Prove export output is deterministic for identical inputs.
- [ ] Expand round-trip tests across real binary 7400/7500 files once the binary corpus lands.
- [ ] Keep binary writer behavior deterministic and bounds-checked even where unsafe or pooled code is introduced later.
- [ ] Keep binary array compression as an explicit export option rather than silently changing default output shape.

## Deferred ASCII Writer Requirements

- [ ] When the ASCII writer path lands, emit the leading ASCII magic comment and stable version string.
- [ ] Emit `Name:` nodes, comma-separated property lists, balanced braces, and deterministic ordering.
- [ ] Support both scalar property emission and `*count { a: ... }` array emission where that representation is required.
- [ ] Serialize `Connections` blocks and connection entries in a form the ASCII reader can reparse deterministically.
- [ ] Decide whether ASCII write mode normalizes to a single canonical style or preserves source style only in special round-trip/debug builds.

## Deferred Until After Core Import/Export Is Stable

- [ ] Source-style-preserving ASCII export, including comment preservation and minimal-diff round-tripping.
- [ ] Rare constraints and advanced control rigs.
- [ ] Exotic deformers beyond the current skinning and blendshape subset.
- [ ] Obscure or layered material stacks beyond the supported material path.
- [ ] Full parity with every Autodesk SDK corner case.
- [ ] Aggressive exporter feature surface beyond what can be validated with confidence.

## Useful Touchpoints

- `docs/developer-guides/assets/native-fbx-import-export.md`
- `docs/developer-guides/assets/model-import.md`
- `XREngine.Fbx/`
- `XRENGINE/Core/ModelImporter.cs`
- `XRENGINE/Core/NativeFbxSceneImporter.cs`
- `XRENGINE/Models/Meshes/ModelImportOptions.cs`
- `XREngine.UnitTests/TestData/Fbx/fbx-corpus.manifest.json`
- `XREngine.UnitTests/Core/FbxPhase*Tests.cs`
- `XREngine.UnitTests/Rendering/NativeFbxImporterTests.cs`
- `XREngine.Benchmarks/FbxPhase*Harness.cs`
