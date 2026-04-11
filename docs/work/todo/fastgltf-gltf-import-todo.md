# fastgltf-Backed glTF Import TODO

Created: 2026-04-10
Completed: 2026-04-10

This tracker is complete. Canonical runtime docs now live in `docs/features/model-import.md`, `docs/features/unit-testing-world.md`, and `docs/features/native-dependencies.md`. The implementation ships a fastgltf-backed native import path for `.gltf` and `.glb`, keeps Assimp as the explicit compatibility fallback, and validates the delivered behavior against a committed corpus, golden summaries, benchmarks, smoke tests, and focused unit tests.

## Final State

- `XRENGINE/Core/ModelImporter.cs` routes `.gltf` and `.glb` through the native path by default and falls back to Assimp only when `GltfBackend = Auto` and the native importer rejects the asset.
- `XRENGINE/Models/Meshes/ModelImportOptions.cs` exposes `GltfBackend` alongside the existing FBX backend selection.
- `XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs` translates `AssimpOnly` into both `FbxBackend = AssimpLegacy` and `GltfBackend = AssimpLegacy`.
- The native bridge lives in `Build/Native/FastGltfBridge`, vendors fastgltf v0.9.0 plus simdjson v3.12.3, and stages `FastGltfBridge.Native.dll` under `XREngine.Gltf/runtimes/win-x64/native`.
- `XREngine.Gltf` owns managed JSON parsing, GLB JSON-chunk bounds validation, extras and unknown-extension retention, native-handle lifetime, and batched accessor and buffer-view copy helpers.
- The committed corpus, manifest, and golden summaries live under `XREngine.UnitTests/TestData/Gltf/`.
- `Build/Reports/gltf-phase0-performance.json` captures the baseline native-versus-Assimp benchmark snapshot.

## Recommended V1 Scope

- [x] Import `.gltf` and `.glb` through a fastgltf-backed native path.
- [x] Support the model/prefab-relevant subset first: scene hierarchy, transforms, meshes, materials, textures, samplers, skins, morph targets, and animation clips.
- [x] Support external buffers, embedded binary chunks, and data URIs.
- [x] Support sparse accessors, normalized integer conversion, matrix/TRS node transforms, and multiple UV/color sets.
- [x] Preserve unknown `extras` and unsupported extension payloads in a stable form when practical so future higher-level features are not blocked.
- [x] Keep existing material-factory hooks, transparency policies, and remap seeding behavior intact.
- [x] Explicitly defer glTF export, HTTP and network URI fetches, and editor-facing glTF-specific polish until the import path is stable.

## Non-Negotiable Implementation Rules

- [x] No per-vertex, per-index, or per-accessor P/Invoke chatter. Cross the native boundary in coarse batches.
- [x] Do not share a single `fastgltf::Parser` across threads. Use one parser per import worker or a parser pool with strict thread ownership.
- [x] The native boundary is a narrow C ABI with explicit ownership and destruction rules; C++ object graphs stay native.
- [x] Do not silently mix partial native results with partial Assimp results. Fallback happens before scene publication and produces actionable diagnostics.
- [x] Keep `PreferNativeThenAssimp` and `AssimpOnly` as the high-level user-facing policy. No extra top-level backend mode was added.
- [x] No hidden network fetches or path guessing for external resources. URI resolution stays deterministic and local-path constrained.
- [x] Treat decode-path allocation regressions as bugs. The implementation avoids LINQ, boxing, and capture-heavy conversion loops in hot decode paths.
- [x] Do not enable deprecated or draft fastgltf extension toggles by default.
- [x] The dependency and native-packaging change was approved by the tracker execution request and is paired with the dependency and license inventory refresh.

## Success Criteria

- [x] A representative glTF corpus imports with deterministic hierarchy, geometry, skinning, material, and animation summaries.
- [x] The native glTF path materially improves wall time and allocations versus Assimp on the representative `large-production-scene` workload, while smaller synthetic fixtures remain baseline coverage assets rather than optimization targets.
- [x] `PreferNativeThenAssimp` routes `.gltf` and `.glb` through the native path automatically.
- [x] `AssimpOnly` remains a clean compatibility escape hatch for glTF, not just FBX.
- [x] Native bridge smoke tests catch missing or stale DLL exports before runtime debugging starts.
- [x] User-facing docs are updated now that glTF is a visible native import path.

## Phase 0: Approval Gate, Ownership Boundary, Corpus, and Benchmark Plan

- [x] Get approval for the dependency and native-packaging path before adding fastgltf or simdjson to the repository.
- [x] Decide how fastgltf is brought in: vendored source snapshot under `Build/Native/FastGltfBridge/vendor`.
- [x] Decide the core boundary up front: `Build/Native/FastGltfBridge` for native parsing plus a managed `XREngine.Gltf` integration layer, keeping scene assembly owned by the engine.
- [x] Decide whether external buffers and images are loaded natively with fastgltf options or resolved by managed code and passed back through a controlled adapter path. Decision: native loads local external buffers, managed code owns images and data URI decode.
- [x] Decide whether image decoding stays on the existing engine texture path or uses fastgltf and native image decode for any subset of sources. Decision: stay on the existing managed texture path.
- [x] Build a committed corpus under `XREngine.UnitTests/TestData/Gltf/` covering `.gltf`, `.glb`, embedded buffers, external buffers, data URIs, sparse accessors, skins, morphs, animations, malformed files, and a large production-style scene.
- [x] Add golden summaries for scene counts, mesh and material counts, skeleton summaries, and animation summaries.
- [x] Define performance metrics up front: wall time, allocation count, peak memory, and parallel scaling versus Assimp.
- [x] Decide whether development and test builds should run `fastgltf::validate` for stricter spec checking. Decision: keep it disabled for now and rely on corpus validation plus importer diagnostics.

Phase 0 target artifacts:

- `Build/Native/FastGltfBridge/FastGltfBridge.vcxproj`
- `XREngine.UnitTests/TestData/Gltf/gltf-corpus.manifest.json`
- `XREngine.UnitTests/TestData/Gltf/*.summary.json`
- `XREngine.Benchmarks/GltfPhase0BaselineHarness.cs`

### Exit Criteria

- [x] The dependency path is approved.
- [x] The native and managed boundary is chosen.
- [x] The glTF corpus and benchmark contract are committed.
- [x] External resource ownership is defined.

## Phase 1: Native Bridge Skeleton

- [x] Create `Build/Native/FastGltfBridge/FastGltfBridge.vcxproj` using the repo's existing native bridge conventions.
- [x] Compile fastgltf in a minimal configuration for repo use: no examples, no docs, and no upstream tests in the normal engine build.
- [x] Decide whether to keep fastgltf's custom memory pool enabled by default. Decision: keep it enabled.
- [x] Export a narrow C ABI for open and parse asset, free asset, query last error, and copy accessor-backed and buffer-view-backed data into caller-owned buffers.
- [x] Keep native structs POD-friendly and versionable.
- [x] Add DLL export smoke tests alongside `XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs`.
- [x] Decide runtime staging: stage under `XREngine.Gltf/runtimes/win-x64/native` and copy into consuming outputs.

### Exit Criteria

- [x] The bridge builds on the supported Windows toolchain.
- [x] Managed code can load the DLL and verify exports.
- [x] The bridge can parse `.gltf` and `.glb` fixtures and return structured data without crashing.

## Phase 2: Managed Interop Layer and Engine-Neutral IR

- [x] Create a managed wrapper layer in `XREngine.Gltf` that owns native handles with strict disposal semantics.
- [x] Define an engine-neutral glTF IR for nodes, meshes, primitives, accessors, materials, textures, skins, animations, and retained `extras` and extension payloads.
- [x] Translate fastgltf and native parse errors into actionable engine diagnostics.
- [x] Keep this layer unit-testable without a live scene or world.
- [x] Avoid copying data twice without measurement; native-to-managed copies are explicit and batched.

### Exit Criteria

- [x] Managed tests can parse and inspect glTF metadata without going through `ModelImporter`.
- [x] Lifetime and error handling are deterministic under repeated open and close cycles.

## Phase 3: Geometry and Buffer Decode

- [x] Support `POSITION`, `NORMAL`, `TANGENT`, `TEXCOORD_n`, `COLOR_n`, `JOINTS_n`, `WEIGHTS_n`, indices, and morph target attributes.
- [x] Use fastgltf accessor tools for sparse accessors and normalized and type-converting reads instead of hand-written conversion code wherever practical.
- [x] Support primitives without indices and meshes with multiple primitives.
- [x] Validate buffer view ranges, strides, and component types before decode.
- [x] Decide whether decoded vertex and index data lands first in engine-owned arrays or directly in pre-sized staging buffers. Decision: decode into explicit engine-owned managed arrays.
- [x] Benchmark large JSON, base64-heavy, and GLB assets against Assimp.

### Exit Criteria

- [x] Static mesh geometry imports correctly for the committed corpus.
- [x] Accessor edge cases are covered by tests.
- [x] The geometry path has a measured allocation profile.

## Phase 4: Scene, Materials, and Texture Integration

- [x] Map default scene selection, node hierarchy, and TRS or matrix transforms into the existing `SceneNode` workflow.
- [x] Map glTF material semantics explicitly: metallic-roughness, normal, occlusion, emissive, alpha mode, alpha cutoff, double-sidedness, and sampler wrap and filter state.
- [x] Preserve current material factory hooks (`Deferred`, `Forward`, `Uber`) instead of hardcoding glTF-native render materials.
- [x] Preserve material and texture remap seeding through the current `ModelImporter` asset workflow.
- [x] Keep async mesh processing and publication-order behavior intact.
- [x] Handle URI, data-URI, and buffer-view-backed image sources deterministically.

### Exit Criteria

- [x] Imported glTF materials behave at parity with current engine expectations for the supported subset.
- [x] Texture-remap and material-remap seeding still works after native glTF import.
- [x] Async import does not reorder or duplicate scene publication.

## Phase 5: Skinning, Animation, and Morph Targets

- [x] Support skins, joint lists, skeleton roots, and inverse bind matrices.
- [x] Support animation samplers and channels for translation, rotation, scale, and morph weights.
- [x] Support morph target default weights and animated weights.
- [x] Verify quaternion normalization, tangent handedness, and joint-weight normalization behavior against corpus expectations.
- [x] Add focused tests for mixed static and skinned scenes, morph-only scenes, and animated skinned scenes.

### Exit Criteria

- [x] The committed skinned and animated corpus imports without structural regressions.
- [x] Skinning and animation summaries remain deterministic across repeated imports.

## Phase 6: Extension Matrix and Compatibility Strategy

- [x] Publish an explicit V1 extension support matrix instead of claiming blanket parity.
- [x] Evaluate `KHR_texture_transform`, `KHR_materials_unlit`, `KHR_mesh_quantization`, `EXT_meshopt_compression`, and `KHR_texture_basisu` based on the committed corpus and engine needs.
- [x] Decide the story for `KHR_draco_mesh_compression`. Decision: unsupported with a diagnostic plus an `AssimpLegacy` compatibility escape hatch.
- [x] Preserve unknown extension payloads without crashing, and log when ignored extensions can materially change results.
- [x] Keep deprecated and draft fastgltf options off unless the corpus proves they are needed and approval is granted.

### Exit Criteria

- [x] Supported extensions are documented.
- [x] Unsupported extensions fail with diagnostics rather than silent corruption.

## Phase 7: Import Routing, Rollout, and Docs

- [x] Generalize native import dispatch in `XRENGINE/Core/ModelImporter.cs` so `.gltf` and `.glb` participate in native routing.
- [x] Generalize the current FBX-only compatibility override surface so `AssimpOnly` also works for glTF.
- [x] Avoid introducing a glTF-specific editor and backend toggle.
- [x] Add focused tests around unit-testing-world and import-policy behavior so backend preference behaves correctly for glTF.
- [x] Update `docs/features/model-import.md` now that glTF is a native path.
- [x] Update `docs/features/unit-testing-world.md` now that the backend policy text and validation guidance changed.
- [x] Skip a glTF-specific trace env var for now and document the existing logging behavior instead.

### Exit Criteria

- [x] Native glTF routing is live behind the existing import policy.
- [x] Documentation matches runtime behavior.
- [x] Compatibility fallback remains available.

## Delivered Corpus

- `external-static-scene`: external buffers, external images, multiple UV and color sets, deterministic remap keys, compatibility fallback
- `data-uri-unlit`: data URIs, `KHR_materials_unlit`, supported `KHR_texture_transform` texCoord override subset
- `skinned-morph-animated`: skinning, default-scene selection, translation and rotation animation coverage
- `morph-sparse-extras`: sparse accessors, morph targets, retained extras, and unknown extension payload preservation
- `embedded-buffer-view-scene`: GLB container parsing, embedded BIN payloads, buffer-view-backed image reads, baked matrix transforms
- `large-production-scene`: representative production-style benchmark workload
- `malformed-truncated-glb`: deterministic malformed-container rejection

## Validation Performed

- `Build-Editor`
- `Generate-UnitTestingWorldSettings`
- `dotnet run --project .\XREngine.Benchmarks -- --gltf-phase0-report`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~Gltf"`

Focused unit-test result at completion: 11 glTF tests passed, 0 failed.

Benchmark snapshot at completion:

- `large-production-scene`: native 1651.16 ms vs Assimp 1862.30 ms, native allocations 301,527,712 B vs Assimp 350,272,824 B, native peak working set 599,310,336 B vs Assimp 779,182,080 B
- smaller synthetic fixtures remain useful for regression coverage, but they are not the optimization target for wall-time wins

## Notes

- The important architectural question was not whether fastgltf was fast enough in isolation; it was whether the native boundary stayed narrow enough that interop overhead and ownership bugs did not erase the win.
- Prefer explicit glTF semantics over Assimp-era heuristics when the two conflict.
- glTF export remains intentionally deferred to a separate future tracker.