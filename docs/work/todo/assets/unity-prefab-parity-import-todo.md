# Unity Prefab Parity Import TODO

Last Updated: 2026-10-11
Status: Active
Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Rendering work: [Unity Poiyomi Advanced Parity TODO](../rendering/unity-poiyomi-advanced-parity-todo.md)

## Current State

The import entry is `jax2031 (1).prefab`, with `jax2031.fbx` as its model prefab base. Both explicit backends complete composition with the same 69 model node IDs. Runtime renderers and supported source FBX clips bind after prefab composition. Native FBX retains source morph channel order. Assimp uses serialized FBX skin bind frames during geometry baking. Both routes now retain the bangs and the intended body shape in viewed Vulkan captures. The active 30 renderer slots agree on 21 Unity materials and their source texture settings. Native Uber constants, shading, shared coverage, and explicit sorted alpha support are implemented. Active Mochie inputs use the source multipliers and independent direct specular and reflection masks. The linked validation document records the remaining image, deformation, streaming, and frame-time checks.

## Open Code Items

- [ ] Update the expected cache hash in `ModelImportBackendResolverTests.Resolve_AutoGltf_ProducesStableNativeThenAssimpSnapshot` for Assimp implementation version 2. Keep the candidate-order and repeat-resolution assertions. Done when the existing test passes with the new version and still checks deterministic resolution. Test source work needs user clearance after live validation, as required by `AGENTS.md`.

### Phase 5: Measured Cost Reduction

Use the linked validation plan to identify a specific cost before adding an implementation item. Shared material binding work stays in [Material Table And Texture Binding Ladder TODO](../rendering/optimization/material-table-and-texture-binding-ladder-todo.md). General avatar LOD work stays in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md).

## Decisions Needed

- Select the active outfit and expression for the Unity reference. Owner: avatar author.
- Select the desktop or VR frame budget and output resolution. Owner: application owner.

## Debugging And Validation

The linked investigation owns root matrices, hypotheses, failed imports, and build results. The linked validation document owns separate Assimp and native rows. Both rows import the Unity `.prefab` entry. A direct FBX import can supply diagnostic evidence, but cannot satisfy prefab acceptance.
