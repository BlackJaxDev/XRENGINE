# Unity Prefab Parity Import TODO

Last Updated: 2026-10-19
Status: Active
Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)
Investigation: [Jax2031 Unity And Advanced Vulkan Parity](../../investigations/rendering/2026-10-08-jax2031-unity-advanced-parity.md)
Rendering work: [Unity Poiyomi Advanced Parity TODO](../rendering/unity-poiyomi-advanced-parity-todo.md)
Package validation: [Unity Avatar Package Validation](../../testing/assets/unity-avatar-package-validation.md)

## Current State

Both prefab routes compose supported model bases, source animation, saved
renderer state, and material bindings. Supported Poiyomi versions convert to
native Uber with explicit unsupported-feature diagnostics. Package composition
resolves declared VPM and local companion sources. Supported unskinned FBX
coordinate profiles normalize units; unsupported profiles fail preflight.
Partial import reports missing visual assets. The linked validation documents
own runtime, image, and performance results. Source-specific inventories and
reproduction details remain in local accessory notes.

## Open Code Items

- [ ] Update the expected cache hash in `ModelImportBackendResolverTests.Resolve_AutoGltf_ProducesStableNativeThenAssimpSnapshot` for Assimp implementation version 3. Keep the candidate-order and repeat-resolution assertions. Done when the existing test passes with the new version and still checks deterministic resolution. Test source work needs user clearance after live validation, as required by `AGENTS.md`.

### Package Composition

- [ ] Derive handedness for authored FBX tangent layers in `NativeFbxSceneImporter` and the FBX geometry parser. Done when source tangents retain the bitangent sign through UV flips and reflected transforms instead of always storing positive handedness. See the [tangent findings](../../investigations/assets/unity-avatar-package-import.md#geometry-and-material-input-differences).

### Phase 5: Measured Cost Reduction

Use the linked validation plan to identify a specific cost before adding an implementation item. Shared material binding work stays in [Material Table And Texture Binding Ladder TODO](../rendering/optimization/material-table-and-texture-binding-ladder-todo.md). General avatar LOD work stays in [Avatar Optimization Roadmap](../avatar/avatar-optimization-roadmap.md).

## Decisions Needed

- Select the active outfit and expression for the Unity reference. Owner: avatar author.
- Select the desktop or VR frame budget and output resolution. Owner: application owner.

## Debugging And Validation

The linked investigation owns root matrices, hypotheses, failed imports, and build results. The linked validation document owns separate Assimp and native rows. Both rows import the Unity `.prefab` entry. A direct FBX import can supply diagnostic evidence, but cannot satisfy prefab acceptance.
