# Model Import

Model imports have two separate runtime policies:

- `ProcessMeshesAsynchronously`: runs mesh conversion work on background jobs instead of finishing the whole import inline.
- `BatchSubmeshAddsDuringAsyncImport`: controls how finished submeshes are published when async import is enabled.

`BatchSubmeshAddsDuringAsyncImport = true` preserves the old behavior. Imported nodes appear in the scene quickly, but each node's submeshes are withheld until that node's async mesh work is complete, then published together.

`BatchSubmeshAddsDuringAsyncImport = false` streams submeshes into the scene as they become ready. Publication still happens on the swap thread rather than directly from worker threads, and the importer preserves source order by only releasing the next contiguous ready submeshes.

`ProcessMeshesAsynchronously` can still inherit the engine-wide async import preference.

`BatchSubmeshAddsDuringAsyncImport` is a per-import setting on `ModelImportOptions`. Its default is batched publication.

Model import settings also persist per-import replacement maps for textures and materials:

- `TextureRemap`: maps source texture paths from the imported file to finalized `XRTexture2D` assets.
- `MaterialRemap`: maps imported material names to finalized `XRMaterial` assets.

On the first successful model import, these dictionaries are automatically seeded with any discovered texture-path and material-name keys from the source model. New entries are written with null values so reimport settings can be filled in later without manually copying keys out of the model.

Older cached import-option files that still store path-based remaps continue to load as legacy fallbacks, but resaving import settings writes the asset-based remap form.