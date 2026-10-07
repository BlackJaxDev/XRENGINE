# Asset Import Validation

Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Unity Conversion Integrations](../../../developer-guides/assets/unity-conversion-integrations.md), [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md), [Model Import Binary Cache](../../../architecture/assets/model-import-binary-cache.md)  Code todos: [Native FBX import/export](../../todo/assets/fbx-import-export-todo.md), [Model import binary cache](../../todo/assets/model-import-binary-cache-todo.md), [USD import/export](../../todo/assets/usd-import-export-todo.md)

Related validation: [glTF import validation](gltf-import.md).

## Setup

Use `Build-Editor` before editor import checks. Use the launch profiles `Editor (Default World)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing World, Validation Layers)` when a check needs the editor. Use `Generate-UnitTestingWorldSettings` after unit-testing world settings change. Use `Cook-CommonAssets-Archive (Manual Slow)` when a check needs cooked common assets.

Set `XRE_WORLD_MODE=UnitTesting` for unit-testing-world checks. Set `XRE_VULKAN_VALIDATION=1` and `XRE_GL_DEBUG=1` when a graphics backend check needs validation layers. Private fixture roots, such as the Unity avatar corpus, stay outside the repository. An opt-in check must skip with a clear reason when the corpus is absent.

Validate OpenGL first. Then run the narrowest useful Vulkan check. Do not start the editor from this document cleanup.

## Checks

### Unity Prefab Import

Architecture link: [Unity Conversion Integrations](../../../developer-guides/assets/unity-conversion-integrations.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Unity avatar OpenGL smoke | Import the private avatar fixture, place it through the standard external-file UI path, and view it from front, oblique, and rear camera positions on OpenGL. | The avatar is upright. Materials, textures, rig, blendshapes, and animation bindings load. | Passed | 2026-08-04. |
| Unity avatar Vulkan smoke | Import the private avatar fixture, place it through the standard external-file UI path, and capture it from front, oblique, and rear camera positions on Vulkan. | The capture is upright and matches OpenGL, including forward-masked materials. | Open | 2026-07-31: placement and texture streaming worked, but screenshot readback was vertically inverted and forward-masked passes were wrong. See the [Unity import investigation](../../investigations/assets/unity-prefab-avatar-import-2026-07-29.md). |
| Source and serialized asset name coverage | Import a prefab with model, material, animation, nested prefab, and external asset references. | `SourceProjectImportContext`, `SourceAssetResolver`, and `Serialized*` importers resolve the same dependencies as the authored project. | Open | Last evidence: none. |

### Prefab Sub-Asset Externalization

Architecture link: [Model Import](../../../developer-guides/assets/model-import.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Root prefab sub-asset externalization | Reimport a model or FBX prefab that creates meshes, materials, textures, and animation clips. Inspect the root prefab YAML and reload it. | The root prefab has no inline `AnimationClip`, `XRMaterial`, `XRTexture2D`, or `XRMesh` blocks. Each reference resolves to one file, and reload preserves counts. | Open | Last evidence: none. |
| Shared generated asset deduplication | Import two prefabs that share a generated texture or material. | The shared asset is written once and both prefabs resolve to it. | Open | Last evidence: none. |
| Failure rollback | Simulate an `IOException` during placeholder creation. | No placeholder files remain and the previous asset state stays valid. | Open | Last evidence: none. |

### Native FBX

Architecture link: [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| ASCII corpus import | Run the focused FBX corpus tests after changing FBX import behavior. | ASCII fixtures import through the native path and keep the expected hierarchy and summaries. | Open | Last evidence: none. |
| Binary corpus structural scan | Run the structural scan over binary FBX 7400 and 7500 fixtures after the fixtures land. | Every binary fixture parses or fails with a deterministic diagnostic. | Blocked by missing binary fixtures | Last evidence: none. |
| Binary round trip | Import binary fixtures, export with `FbxBinaryWriter`, and re-read the exported file. | The exported file is deterministic and preserves the supported subset. | Open | Last evidence: none. |
| Native versus Assimp benchmark | Run the FBX benchmark harness against representative static and animated assets. | The report compares native FBX and Assimp for wall time, MB/s, allocations, peak memory, and parallel scaling. | Open | Last evidence: none. |
| Hot-path allocation audit | Run the parser and importer allocation benchmark. | Parser and importer hot paths do not allocate per node or per property outside output buffers and pooled scratch. | Open | Last evidence: none. |
| Rigging and animation semantics | Run tests for weight normalization, bind-pose stability, animation key order, root motion, and semantic validation. | Skinned fixtures pass. Malformed fixtures fail with actionable diagnostics. | Open | Last evidence: none. |

### USD

Architecture link: [USD Import And Export Design](../../design/assets/usd-import-export-design.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| USD corpus manifest | Run the future USD corpus loader against USDA, USDC, and USDZ fixtures. | The manifest loads and identifies valid, large, referenced, variant, and malformed assets. | Blocked by missing USD implementation | Last evidence: none. |
| USDA tokenizer and package benchmark | Run the future USDA tokenizer and USDZ package microbenchmarks. | The report includes cold-open time, MB/s, allocations, peak memory, and parallel scaling. | Blocked by missing USD implementation | Last evidence: none. |
| `.usd` sniffing | Import text and Crate files that both use the `.usd` extension. | Dispatch is deterministic and covered by tests. | Blocked by missing USD implementation | Last evidence: none. |
| OpenUSD oracle validation | Validate exported assets with `usdchecker`, `usdcat`, and at least one downstream consumer when practical. | Supported USDA exports round-trip without semantic corruption. Full-fidelity paths use OpenUSD interop. | Blocked by missing USD implementation and dependency approval | Last evidence: none. |
| Unsupported-case fallback | Import unsupported Crate versions, schemas, values, or composition cases. | The importer routes to OpenUSD interop or fails with an actionable diagnostic. It does not guess silently. | Blocked by missing USD implementation and dependency approval | Last evidence: none. |

### Model Import Binary Cache

Architecture link: [Model Import Binary Cache](../../../architecture/assets/model-import-binary-cache.md).
Historical requirements and phase evidence: [model-cache branch record](../../progress/assets/model-import-binary-cache-reconciliation-2026-10-07.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Valid warm load | Import a cached model after a complete container and hydration path exist. | The importer uses the cache and makes zero source parser calls. | Blocked by incomplete hydration path | Last evidence: none. |
| Meshlet warm load | Warm-load a model with cached meshlet sections. | `ModelBinaryMeshletSectionTelemetry` shows no meshlet builder execution, and GPUScene descriptor payloads match a cold cook. | Open | Last evidence: none. |
| Partial hydration | Read metadata-only, structure-only, and selected-mesh modes. | Only the preamble, string pool, dependency manifest, chunk table, and requested bodies are read. | Open | Last evidence: none. |
| Optional-section repair | Corrupt an optional meshlet section and load from the cache. | Required data hydrates. Repair uses cached core geometry and does not parse the source file. | Open | Last evidence: none. |
| Atomic publication | Interrupt a cache write and read during replacement. | The previous entry remains valid. Corrupt entries quarantine without blocking source import. | Open | Last evidence: none. |
| Manual reimport transaction | Reimport a generated model asset after cache staging. | GUIDs, remaps, project-authored materials, and bindings remain authoritative; cancel or failure keeps the previous assets and cache. | Open | Last evidence: none. |
| Editor cache inspection | Open a cached model in the editor after inspector work lands. | The editor shows cache state, producer, rejection reason, dependency status, versions, and repair state. | Open | Last evidence: none. |
| Cold and second-load baseline | Record parser calls, wall time, allocations, node, mesh, and material counts, structural hashes, and second-load behavior before full cache hydration. | The later warm path has a reproducible baseline with the same fixture and settings. | Open | Last evidence: none. |
| Import format matrix | Cold-load and warm-load FBX, external glTF, embedded GLB, skinned, morph, and animated glTF, OBJ with MTL, and a Unity prefab after hydration exists. | Structure, bindings, skin, morph, animation references, and dependency invalidation match. A valid warm load makes zero source parser calls. | Blocked by incomplete hydration path | Last evidence: none. |
| Deterministic semantic bytes | Cook equivalent imported models across repeat runs and compare semantic sections. | Equivalent inputs produce byte-identical semantic sections; diagnostic header fields may differ only where the format permits. | Blocked by incomplete cooked sections | Last evidence: none. |
| Cold, warm, and partial benchmarks | Measure cold import, full warm hydration, and each partial-hydration mode with matched settings. | Record parser calls, wall time, allocations, bytes read, and chunks read; no valid warm load rebuilds a present LOD or meshlet. | Blocked by incomplete hydration path | Last evidence: none. |
| Editor cache actions | Exercise rebuild, remove, inspect, and reimport or reconcile in the editor. | Each action reports the current owner/result; cancel and failure preserve project assets and the previous valid cache. | Open | Last evidence: none. |

### glTF

Architecture link: [Model Import](../../../developer-guides/assets/model-import.md). See [glTF import validation](gltf-import.md) for the delivered fastgltf baseline and future glTF checks.

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Unity avatar Vulkan smoke | Vertically inverted screenshot readback and incorrect forward-masked rendering. | [Unity prefab avatar import investigation](../../investigations/assets/unity-prefab-avatar-import-2026-07-29.md) |
