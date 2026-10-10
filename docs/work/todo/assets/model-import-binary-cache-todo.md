# Model Import Binary Cache TODO

Last Updated: 2026-10-07
Status: Active
Architecture: [Model Import Binary Cache](../../../architecture/assets/model-import-binary-cache.md)  Design: [Model Import Binary Cache Design](../../design/assets/model-import-binary-cache-design.md)
Validation: [Asset Import Validation](../../testing/assets/asset-import-validation.md#model-import-binary-cache)
History: [Branch requirements and evidence](../../progress/assets/model-import-binary-cache-reconciliation-2026-10-07.md)

## Current State

The shared cache contracts are in `XREngine.Data/Core/Assets/Caching/`. The exclusive `ModelBinaryCacheCodec`, backend registry, producer reports, cache identity, `ImportedEntityKey`, `ModelCookSettings`, defensive container reader, and defensive container writer are in `XREngine.Runtime.ModelAssetPipeline/Importing/Caching/`. `ModelBinaryMeshletSectionCodec` and `ModelBinaryMeshletSectionService` exist and handle model-owned meshlet payloads. Shared mesh core, skinning, skeleton, morph, and LOD section codecs are still incomplete. A valid container still reports `CodecUnavailable`, so imports use the cold source path.

## Open Code Items

### Fixtures

- [ ] Add a deterministic OBJ and MTL cache-integration fixture. `XREngine.UnitTests/TestData/`. Done when: a cache test imports it and reports the MTL dependency.

### Cooking And Shared Mesh Sections

- [ ] Resolve effective cook settings per submesh with authored `MeshOptimizerSubMeshSettings` overrides. `ModelCookSettings`, `XREngine.Runtime.Rendering/Rendering/Meshlets/MeshOptimizerSettings.cs`. Done when: a unit test shows an override wins over the model default.
- [ ] Extract mesh core, skinning and bind, and morph section codecs from `XRMesh.CookedBinary.cs` and `XRMesh.CookedMeshlets.cs`. `XREngine.Runtime.Rendering/Objects/Meshes/`. Done when: standalone `XRMesh` serialization composes the codecs and its existing tests pass.
- [ ] Add model-owned skeleton-hierarchy and submesh-owned LOD-table codecs. `XREngine.Runtime.ModelAssetPipeline/Importing/Caching/`. Done when: round-trip tests pass.
- [ ] Make the model container compose the shared section codecs without a nested `XRMesh` payload. Done when: a container round trip restores mesh core, skin, morph, and LOD data.
- [ ] Generate requested LODs and meshlets after import and before publication, and persist disabled or absent features explicitly. `MeshOptimizerIntegration`, `SubMeshLOD`. Done when: a warm load with a disabled feature does not build it.
- [ ] Add section round-trip, compatibility, deterministic-cook, and standalone-mesh regression tests. Done when: the tests pass.

### Meshlets

- [ ] Add unit tests for `ModelBinaryMeshletSectionCodec` and `ModelBinaryMeshletSectionService`: empty, disabled, multi-LOD, corrupt optional section, duplicate key, secondary fill, and read-only repair. Done when: the tests pass.
- [ ] Add an assertion that a valid warm load does not rebuild meshlets. `ModelBinaryMeshletSectionTelemetry`. Done when: a test fails if a builder runs during a warm load.
- [ ] Add a test that cached CPU descriptors give the same GPUScene upload payload as a cold cook. `GPUScene.GpuMeshletDescriptor`. Done when: the payloads match.

### Prefab Hydration And Referenced Subassets

- [ ] Add a cache-local `CookedModelDocument` with no dependency on `AssetManager` or editor assemblies. Done when: the type builds in `XREngine.Runtime.ModelAssetPipeline`.
- [ ] Add an upper-layer `IImportedComponentCacheCodec` registry with stable keys, versions, required or optional policy, and bounded payloads. Map serialized source component records at the `XREngine.Editor` boundary. Done when: `XREngine.Runtime.ModelAssetPipeline` has no editor importer types and the registry round-trips a test component.
- [ ] Hydrate `XRPrefabSource` from the document through `SetField(...)`, and rebuild hierarchy, component, mesh, material, skin, morph, and animation references from cache-local IDs without source parsing. `XREngine.Runtime.Core/Scene/Prefabs/XRPrefabSource.cs`. Done when: a cold-versus-warm structural equality test passes.
- [ ] Keep project-authored materials and remaps authoritative, and never write generated project assets during a warm load. Done when: a project-binding precedence test passes.
- [ ] Store animation references only, and require durable embedded-texture publication before model-cache publication. Reject only the affected hydration group when a referenced output is missing. Done when: tests show no duplicated animation or texture bytes and a missing texture rejects only its group.

### Partial Hydration And Repair

- [ ] Read only the preamble, string pool, dependency manifest, and chunk table before heavy bodies, and support metadata-only, structure-only, and selected-mesh hydration. `ModelBinaryContainerReader`. Done when: a test counts chunk reads for each mode.
- [ ] Use pooled buffers, spans, and bounded reads, and coalesce nearby chunk reads without weaker validation. Done when: the hydration benchmark shows no per-chunk managed allocations.
- [ ] Fall back to source import only for required-data rejection or a non-repairable condition. Done when: a test with a corrupt optional section hydrates without a source parse.
- [ ] Emit parser-call, cache-hit, bytes-read, chunks-read, repair, and cook counters. Done when: a test reads zero parser calls on a valid warm load.

### Atomic Publication

- [ ] Write to a unique adjacent temporary path, flush, reopen, validate with the production reader, and replace atomically. Done when: an interrupted-write test leaves the previous entry valid.
- [ ] Serialize same-key work with a keyed semaphore and add cross-process arbitration or a proven race-safe protocol. Recheck validity after ownership. Done when: same-process and cross-process race tests pass.
- [ ] Clean only validated orphan temporary files that match the key and age policy, and quarantine corrupt entries without failing source import. Done when: read-during-replace, read-only, and corrupt-entry tests pass.

### Manual Reimport And Editor UX

- [ ] Stage a complete cache candidate during manual reimport, match entities to generated assets by `ImportedEntityKey`, and keep project GUIDs, remaps, and bindings. `XREngine.Editor/Importers/ThirdParty/AssetManager.ThirdPartyImport.cs`. Done when: a reimport test keeps GUIDs for matched entities.
- [ ] Preview additions, removals, remaps, and identity breaks, then commit assets and cache as one recoverable transaction. Done when: a cancel or failure test keeps the previous assets and cache.
- [ ] Show cache state, producer, rejection reason, dependency status, versions, and repair state in the editor. `XREngine.Editor/`. Done when: the inspector shows these fields for a cached model.
- [ ] Add rebuild, remove, inspect, and reimport or reconcile actions to the editor cache UI. `XREngine.Editor/`. Done when: each action uses the cache owner and reports its result without changing project-owned assets on cancel or failure.

## Decisions Needed

- [ ] Choose cross-process arbitration: named mutex, file lock, or a unique-temp race-safe protocol only. Owner: asset pipeline.

## Out Of Scope

- Texture payload storage. The texture cache owns it.
- Migration of legacy YAML model caches. The pipeline rebuilds them from source.
