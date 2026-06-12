# Model Import

Model imports can route through a native format-specific importer or the older Assimp compatibility path.

## Unity scene import

- `.unity` files now import into `XRScene` assets through the standard third-party asset pipeline.
- `.prefab` files now import into `XRPrefabSource` assets through the same Unity YAML importer.
- `GameObject` documents map to `SceneNode`, and `Transform` / `RectTransform` documents map to the engine's default `Transform` type.
- Root ordering is preserved through Unity `SceneRoots`, authored child order, and prefab-instance `m_RootOrder` overrides.
- Prefab instances are expanded by resolving prefab GUIDs through the source Unity project's `.meta` files, then applying scene-level name, active-state, layer, and local transform overrides.
- Supported Unity component documents now map into engine components for `Camera`, `Light`, `MeshFilter` + `MeshRenderer`, and `SkinnedMeshRenderer`.
- Renderer meshes resolve from Unity built-in primitives, serialized Unity `.asset` mesh files, or third-party model assets by matching imported node paths.
- Scene and prefab transforms use a direct Unity left-handed to engine right-handed conversion by flipping the local `Z` axis. This path does not apply the extra Assimp-facing compensation used by the `.anim` importer.

Current limitations:

- Non-hierarchy scene settings such as `RenderSettings`, `LightmapSettings`, `NavMeshSettings`, and `OcclusionCullingSettings` are currently skipped.
- Material import covers common Unity `_BaseColor` / `_Color` tint and `_BaseMap` / `_MainTex` texture paths. Unity 2022 materials using Poiyomi Toon 9.3 or lilToon are additionally converted to the engine Uber shader path. See [Unity Conversion Integrations](unity-conversion-integrations.md) for the shader mapping scope.
- Serialized Unity mesh assets currently import the common uncompressed vertex layouts used for positions, normals, tangents, colors, and UV0. More exotic compressed or multi-stream layouts may still need fallback handling.

## Default routing

- The ImGui editor exposes `Tools > Import External Files...` and `Tools > Import External Folder...` to copy external sources into the project `Assets/` tree and optionally run the registered third-party import pipeline immediately. Folder import is the preferred path for Unity exports because it preserves `.meta` files and referenced material, shader, and texture dependencies.
- Selecting an importable standalone texture file such as `.png`, `.jpg`, `.tga`, `.exr`, or `.hdr` in the ImGui Asset Explorer automatically prepares its generated `XRTexture2D` `.asset` beside the source and shows that asset's texture preview in the same inspector panel.
- `.fbx` files route through the native `XREngine.Fbx` importer by default. `FbxBackend = Auto` attempts native import first and falls back to Assimp if the native path rejects the asset or throws. See [Native FBX Import And Export](native-fbx-import-export.md) for the supported subset, parser/exporter rules, and remaining hardening work.
- `.gltf` and `.glb` files route through the native fastgltf-backed path by default.
- `.anim` files route to `AnimationClip` through the Unity YAML animation importer.
- `.unity`, `.prefab`, and `.mat` files route through the Unity YAML scene, prefab, and material importers.
- Other third-party model formats still import through Assimp.

For Unity-specific conversion behavior, including `.anim` caveats and Poiyomi/lilToon material mapping, see [Unity Conversion Integrations](unity-conversion-integrations.md).

## Model debug overlays

`ModelComponent.RenderUtilizedBoneDiamonds` draws a semi-transparent, camera-view-lit diamond mesh overlay for every transform referenced by the component's mesh bone bindings. The overlay is per model component and reuses one shared gray diamond mesh/material rather than changing global transform debug rendering.

Compatibility overrides stay explicit and per format:

- `ModelImportOptions.FbxBackend = Assimp` forces the older FBX path. Legacy YAML may still spell this as `AssimpLegacy`.
- `ModelImportOptions.GltfBackend = Assimp` forces the older glTF path. Legacy YAML may still spell this as `AssimpLegacy`.
- Assimp `PostProcessSteps` still apply to non-native formats and the explicit `Assimp` compatibility modes.
- `FbxPivotPolicy` and `CollapseGeneratedFbxHelperNodes` remain FBX-specific controls.

The unit-testing world exposes the same high-level policy per startup import through `ModelsToImport[*].ImporterBackend` in `Assets/UnitTestingWorldSettings.jsonc`:

- `PreferNativeThenAssimp` uses a native importer when the format has one available and falls back to Assimp before scene publication if the native path rejects the asset.
- `AssimpOnly` forces the compatibility path for both FBX and glTF startup imports.
- Today the native format-specific path exists for FBX, glTF, and GLB.

## Native glTF path

The glTF path uses the native `FastGltfBridge.Native.dll` bridge for container parsing, coarse accessor and buffer-view copies, and local external-buffer loading, while scene, material, texture, and publication orchestration stays in managed code.

Supported V1 subset:

- scene hierarchy, default-scene selection, and TRS or baked-matrix node transforms
- meshes with multiple primitives, multiple UV/color sets, sparse accessors, normalized integer conversion, and generated indices when primitives omit them
- skinning, inverse bind matrices, morph targets, default weights, and translation, rotation, scale, and weight animation channels
- external buffers, embedded GLB BIN chunks, data URIs, URI-backed images, and buffer-view-backed images
- extras and unknown extension payload retention in the managed document layer
- existing texture and material remap seeding and replacement workflows

Extension support:

- supported: `KHR_materials_unlit`, `KHR_mesh_quantization`, `EXT_meshopt_compression`, `EXT_texture_webp`
- partial: `KHR_texture_transform` texCoord override only
- unsupported with a diagnostic that points to `Assimp`: `KHR_draco_mesh_compression`, `KHR_texture_basisu`, and `KHR_texture_transform` offset, scale, and rotation

Resource and fallback rules:

- native glTF accepts only local relative URIs and data URIs; network and other non-local URIs are rejected deterministically
- `GltfBackend = Auto` falls back cleanly to Assimp if the native path fails or rejects the asset
- `GltfBackend = Assimp` skips the native path entirely. Legacy YAML may still spell this as `AssimpLegacy`.

For importer and exporter tracing, set `XRE_FBX_LOG` before launching the editor, tests, or tools:

- `XRE_FBX_LOG=info` for stage-level summaries
- `XRE_FBX_LOG=verbose` (or `1`) for detailed per-stage and per-asset trace lines
- `XRE_FBX_LOG=warn` or `error` to log only problems

Enabled FBX trace lines flow through the engine `Assets` log category, so they appear in the editor console's `Assets` tab and in `Build/Logs/.../log_assets.log` when file logging is enabled. There is no separate glTF trace env var today; native glTF warnings, unsupported-extension diagnostics, and Auto-to-Assimp fallback messages also surface through the normal asset-import logging path.

The remaining import settings apply across native and compatibility paths unless noted otherwise:

- `ProcessMeshesAsynchronously`: runs mesh conversion work on background jobs instead of finishing the whole import inline.
- `NativeFbxMeshBuildMaxDegreeOfParallelism`: caps the native FBX mesh build stage. `0` uses an editor-friendly automatic cap; positive values force an exact maximum worker count.
- `GenerateMeshRenderersAsync`: leaves `XRMeshRenderer.GenerateAsync` off by default globally, but allows imported model renderers to opt into async renderer generation.
- `SplitSubmeshesIntoSeparateModelComponents`: creates one `ModelComponent` per imported submesh instead of grouping a source node's submeshes into one model component.
- `GenerateSceneNodesPerSubmesh`: creates individual child scene nodes for split submesh model components. This implies split submesh components.
- `SeparateMeshIslands`: analyzes imported triangle submeshes for disconnected geometric islands and emits each island as its own submesh while preserving the source material.
- `BatchSubmeshAddsDuringAsyncImport`: controls how finished submeshes are published when async import is enabled.

The unit-testing world exposes per-model `PostImportFlags` alongside Assimp `ImportFlags`. It accepts comma-separated `ModelPostImportFlags` names:

- `GenerateCoacdCollidersPerSubmesh`: for static imports, forces split submesh components and adds auto-generated CoACD colliders.
- `SplitSubmeshesIntoSeparateModelComponents`: forwards to `ModelImportOptions.SplitSubmeshesIntoSeparateModelComponents`.
- `SeparateMeshIslands`: forwards to `ModelImportOptions.SeparateMeshIslands`.
- `GenerateIndividualSceneNodesPerSubmesh`: forwards to `ModelImportOptions.GenerateSceneNodesPerSubmesh` and implies split submesh components.
- `PutAllCoacdCollidersIntoOneStaticRigidBodyComponent`: when CoACD generation is enabled, attaches all generated collider shapes to one static rigid body on the imported model root instead of creating one static rigid body per model component.

Without `PutAllCoacdCollidersIntoOneStaticRigidBodyComponent`, `GenerateCoacdCollidersPerSubmesh` adds a `StaticRigidBodyComponent` sibling to each imported model component. After the submesh data is ready, each rigid body runs CoACD and attaches one PhysX convex shape per generated hull.

`BatchSubmeshAddsDuringAsyncImport = true` preserves the old behavior. Imported nodes appear in the scene quickly, but each node's submeshes are withheld until that node's async mesh work is complete, then published together.

`BatchSubmeshAddsDuringAsyncImport = false` streams submeshes into the scene as they become ready. Publication still happens on the swap thread rather than directly from worker threads, and the importer preserves source order by only releasing the next contiguous ready submeshes.

`ProcessMeshesAsynchronously` can still inherit the engine-wide async import preference.

`NativeFbxMeshBuildMaxDegreeOfParallelism` is a per-import setting for the native FBX backend. Its default `0` leaves worker selection to the importer, which intentionally keeps the cap conservative so background imports do not starve editor input and rendering. Raise it only when batch-import throughput matters more than interactive responsiveness.

`GenerateMeshRenderersAsync` is a per-import setting on `ModelImportOptions`. Its current default is `true`.

`SplitSubmeshesIntoSeparateModelComponents` is a per-import setting on `ModelImportOptions`. Its default is `false`, preserving the current "one imported model component per source node" layout.

`GenerateSceneNodesPerSubmesh` is a per-import setting on `ModelImportOptions`. Its default is `false`; when enabled, split submesh components are placed under generated child scene nodes instead of being attached directly to the source scene node.

`SeparateMeshIslands` is a per-import setting on `ModelImportOptions`. Its default is `false`; when enabled, the importer splits disconnected triangle islands inside each material/grouped submesh before publishing model components. Island connectivity is based on exact vertex positions, so FBX/Assimp meshes with per-face duplicated vertices still group connected surfaces together.

`BatchSubmeshAddsDuringAsyncImport` is a per-import setting on `ModelImportOptions`. Its default is batched publication.

Model import settings also persist per-import replacement maps for textures and materials:

- `TextureRemap`: maps source texture paths from the imported file to finalized `XRTexture2D` assets.
- `MaterialRemap`: maps imported material names to finalized `XRMaterial` assets.

On the first successful model import, these dictionaries are automatically seeded with any discovered texture-path and material-name keys from the source model. New entries are written with null values so reimport settings can be filled in later without manually copying keys out of the model.

Older cached import-option files that still store path-based remaps continue to load as legacy fallbacks, but resaving import settings writes the asset-based remap form.

## On-disk externalization layout

When a third-party model (e.g. `Mitsuki.fbx`) is imported to a native prefab `Mitsuki.asset`, embedded sub-assets are externalized into a sibling folder named after the root asset. The externalizer runs in three phases — **Discover**, **PreAssign & Touch**, then **Topological Write (leaves-first)** — so that every nested sub-asset already has a real file path and a zero-byte placeholder on disk before any container serializes. This lets the YAML reference-emission gate in `XRAssetYamlConverter` / `AnimationClipYamlTypeConverter` emit compact `{ID: <guid>}` references for externalized sub-assets instead of inlining their full content.

```text
<import-folder>/
    Mitsuki.asset                    # XRPrefabSource (root)
    Mitsuki/
        Textures/*.asset             # XRTexture (leaves — written first)
        Materials/*.asset            # XRMaterialBase
        SubMeshes/*.asset            # SubMesh
        Meshes/*.asset               # XRMesh
        Models/*.asset               # Model
        Animations/*.asset           # AnimationClip (containers — written last)
```

If any sub-asset serialization fails mid-import, placeholder files that were never overwritten are deleted during cleanup so the on-disk folder does not accumulate empty `.asset` stubs.

During third-party import externalization, generated `XRMesh` assets serialize their runtime mesh buffers as raw streams inside the cooked mesh payload. The outer YAML `DataSource` payload is already Zstd-compressed, so this avoids expensive per-buffer LZMA work while keeping the written `.asset` files compressed. Existing mesh assets that were written with LZMA buffer streams remain readable.

Third-party-to-native conversion suppresses render API wrapper creation and `ModelComponent` runtime renderable mesh construction while the source model is being converted. The saved prefab template still contains the authored `Model`/`SubMesh` references; runtime `RenderableMesh` objects are rebuilt when the prefab hierarchy is deserialized or instantiated. Conversion logs one timing line each for source import, externalized sub-asset write, root serialization, and total completion. Individual sub-asset/cache writes only log timing details when they exceed 100 ms.

Save & Reimport reports determinate editor job progress: settings save, source/model import, embedded sub-asset externalization, root serialization, and finalization. Model imports forward per-mesh importer progress when available, so the UI can show a moving percentage during the longest stage instead of an indeterminate spinner.
