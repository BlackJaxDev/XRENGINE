# Model Import

Model imports have two separate runtime policies:

- `.fbx` files now route through the native `XREngine.Fbx` importer by default via `ModelImporter`'s format-specific dispatch. Non-FBX formats still import through Assimp.
- `ModelImportOptions.FbxBackend = AssimpLegacy` is the explicit compatibility override if a particular FBX still needs the older Assimp path.
- Assimp `PostProcessSteps` flags still apply to non-FBX imports and the `AssimpLegacy` FBX mode, but the default native FBX path uses explicit FBX-native settings such as `FbxPivotPolicy` instead.
- `CollapseGeneratedFbxHelperNodes` only affects the legacy Assimp FBX path; the native FBX importer preserves the authored FBX model hierarchy directly.

The unit-testing world exposes the same policy per startup import through `ModelsToImport[*].ImporterBackend` in `Assets/UnitTestingWorldSettings.jsonc`:

- `PreferNativeThenAssimp` uses a native importer when the format has one available and falls back to Assimp otherwise.
- `AssimpOnly` forces the older Assimp compatibility path for that import.

Today the native format-specific path exists only for FBX, so non-FBX imports still land on Assimp in either mode unless and until native importers are added for those formats.

For importer/exporter tracing, set `XRE_FBX_LOG` before launching the editor, tests, or tools:

- `XRE_FBX_LOG=info` for stage-level summaries
- `XRE_FBX_LOG=verbose` (or `1`) for detailed per-stage and per-asset trace lines
- `XRE_FBX_LOG=warn` or `error` to log only problems

Enabled FBX trace lines flow through the engine `Assets` log category, so they appear in the editor console's `Assets` tab and in `Build/Logs/.../log_assets.txt` when file logging is enabled.

- `ProcessMeshesAsynchronously`: runs mesh conversion work on background jobs instead of finishing the whole import inline.
- `GenerateMeshRenderersAsync`: leaves `XRMeshRenderer.GenerateAsync` off by default globally, but allows imported model renderers to opt into async renderer generation.
- `SplitSubmeshesIntoSeparateModelComponents`: creates one `ModelComponent` per imported submesh instead of grouping a source node's submeshes into one model component.
- `BatchSubmeshAddsDuringAsyncImport`: controls how finished submeshes are published when async import is enabled.

The unit-testing world also exposes a per-model JSON toggle, `GenerateCoacdCollidersPerSubmesh`. When enabled for a static model import, the importer forces `SplitSubmeshesIntoSeparateModelComponents = true` for that import and adds a `StaticRigidBodyComponent` sibling to each imported submesh component. After the submesh data is ready, the rigid body runs CoACD and attaches one PhysX convex shape per generated hull.

`BatchSubmeshAddsDuringAsyncImport = true` preserves the old behavior. Imported nodes appear in the scene quickly, but each node's submeshes are withheld until that node's async mesh work is complete, then published together.

`BatchSubmeshAddsDuringAsyncImport = false` streams submeshes into the scene as they become ready. Publication still happens on the swap thread rather than directly from worker threads, and the importer preserves source order by only releasing the next contiguous ready submeshes.

`ProcessMeshesAsynchronously` can still inherit the engine-wide async import preference.

`GenerateMeshRenderersAsync` is a per-import setting on `ModelImportOptions`. Its current default is `true`.

`SplitSubmeshesIntoSeparateModelComponents` is a per-import setting on `ModelImportOptions`. Its default is `false`, preserving the current "one imported model component per source node" layout.

`BatchSubmeshAddsDuringAsyncImport` is a per-import setting on `ModelImportOptions`. Its default is batched publication.

Model import settings also persist per-import replacement maps for textures and materials:

- `TextureRemap`: maps source texture paths from the imported file to finalized `XRTexture2D` assets.
- `MaterialRemap`: maps imported material names to finalized `XRMaterial` assets.

On the first successful model import, these dictionaries are automatically seeded with any discovered texture-path and material-name keys from the source model. New entries are written with null values so reimport settings can be filled in later without manually copying keys out of the model.

Older cached import-option files that still store path-based remaps continue to load as legacy fallbacks, but resaving import settings writes the asset-based remap form.