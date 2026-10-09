# Unity Prefab Parity Import TODO

Last Updated: 2026-10-08
Status: Active
Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Rendering work: [Unity Poiyomi Advanced Parity TODO](../rendering/unity-poiyomi-advanced-parity-todo.md)

## Current State

The import entry is `jax2031 (1).prefab`, with `jax2031.fbx` as its model prefab base. Backend selection now reaches referenced model prefabs. `NativeOnly` disables fallback. Assimp root normalization lets the exact prefab complete composition with 69 model components and 67 materials. The live Vulkan output still has deformed and detached geometry and shading differences. Native parsing succeeds, but publication stops at an explicit guard because its coordinate and cluster-bind contracts are not yet compatible with Unity composition. The linked guide owns the completed backend and normalization behavior.

## Open Code Items

### Phase 2: Model Base Coordinates And Identity

- [ ] Support the remaining guarded Assimp root shapes in `SerializedSceneImporter.ModelRoots.cs` and `SerializedSceneImporter.Models.cs`. Done when: a non-identity root with one authored child, a root skin influence, or explicit root-relative bounds has a defined normalization that preserves geometry and bounds. Keep unsupported cases explicit until that contract exists.
- [ ] Normalize native FBX hierarchy and coordinate data at the same boundary. Done when: native content-basis nodes do not change Unity object IDs, and mesh-local cluster binds remain valid after the imported skeleton is applied. Preserve current and previous deformation data.
- [ ] Replace the inverse-world-only skeleton rebase in `SerializedSceneImporter.ModelSkeleton.cs` and `XRMesh.Skinning.cs` with a palette-preserving conversion. Done when: each new palette and bone-bind product preserves the old product, including native mesh-local cluster offsets, without changing influence order.
- [ ] Preserve unskinned mesh data and culling bounds when applying Unity skeleton metadata. `SerializedSceneImporter.ModelSkeleton.cs` and the model mesh conversion boundary. Done when: an authored scale or basis is applied once to positions, morph vectors, normals, tangents, and bounds.
- [ ] Define the FBX-to-Unity-to-engine axis and unit conversion for both routes. `SerializedSceneImporter.Models.cs`, `NativeFbxSceneImporter.cs`, and the Assimp model boundary. Done when: source and Unity joint landmarks agree before pose replacement, winding stays correct, and unsupported settings fail with a diagnostic.

### Phase 3: Prefab Composition And Material Bindings

- [ ] Correct any final material or texture binding that still depends on a stale embedded FBX path. `SerializedSceneImporter.Models.cs`, renderer override code, and `SerializedMaterialImporter`. Done when: required active slots resolve through the Unity project and preserve effective texture import metadata.

### Phase 4: Native Advanced Material Execution

Complete the open implementation items in the linked rendering todo. This includes native Uber data and shading, visibility and shadow coverage, and explicit late transparency support. Do not treat forward shader compilation as proof of native Advanced execution.

### Phase 5: Measured Cost Reduction

Use the linked validation plan to identify a specific cost before adding an implementation item. Shared material binding work stays in [Material Table And Texture Binding Ladder TODO](../rendering/optimization/material-table-and-texture-binding-ladder-todo.md). General avatar LOD work stays in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md).

## Decisions Needed

- Select the active outfit and expression for the Unity reference. Owner: avatar author.
- Select the desktop or VR frame budget and output resolution. Owner: application owner.

## Debugging And Validation

The linked investigation owns root matrices, hypotheses, failed imports, and build results. The linked validation document owns separate Assimp and native rows. Both rows import the Unity `.prefab` entry. A direct FBX import can supply diagnostic evidence, but cannot satisfy prefab acceptance.
