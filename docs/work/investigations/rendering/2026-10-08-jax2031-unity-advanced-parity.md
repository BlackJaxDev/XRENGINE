# Jax2031 Unity And Advanced Vulkan Parity

Date: 2026-10-08
Status: Assimp prefab composition completes. Native publication is guarded. Visual parity and frame-time acceptance remain open.

Architecture: [Advanced Render Pipeline](../../../architecture/rendering/advanced-render-pipeline.md), [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md), [Uber Shader Varianting](../../../architecture/rendering/uber-shader-varianting.md)
Code work: [Unity Poiyomi Advanced Parity TODO](../../todo/rendering/unity-poiyomi-advanced-parity-todo.md)
Import work: [Unity Prefab Parity Import TODO](../../todo/assets/unity-prefab-parity-import-todo.md)
Validation: [Jax2031 Advanced Parity Validation](../../testing/rendering/jax2031-advanced-parity-validation.md)

## Scope And Evidence Limits

The source is `<unity-project-root>/Assets/Avatars/JAX/Mine/jax2031 (1).prefab`. The visual reference is the Unity image supplied by the user. The source inspection uses the saved prefab and material assets. It does not establish the current unsaved Unity scene state, preview lighting, or final active renderer set.

The initial review did not change engine code, tests, or Unity assets. It built and ran the current workspace, including changes that were present before the review. It used an isolated ImGui unit-testing editor session named `jax2031-parity-vulkan`. The implementation follow-up is recorded below. All owned sessions are stopped.

The [earlier avatar import review](../assets/unity-prefab-avatar-import-2026-07-29.md) uses a different avatar and OpenGL. Its geometry counts and visual result do not establish this prefab's Vulkan result.

## Source Asset Findings

| Source fact | Result |
|---|---|
| Prefab instances | Five. The sources include the avatar FBX, a physics setup, VRCFT, GoGoLoco, and InertiaBones. |
| Material overrides | 96 entries across slots 0 through 5. These entries are not draw calls or final slot counts. |
| Distinct non-null override materials | 29. All 29 material GUIDs resolve. Some materials belong to alternate outfits or hair. |
| Preserved original shader | 28 Poiyomi Pro materials and one Poiyomi Pro Grab Pass material. The referenced Pro source is 9.3.66. |
| FBX | `jax2031.fbx`, 128,525,772 bytes. The binary file does not provide renderer or geometry counts through a YAML inspection. |
| FBX import metadata | `globalScale: 1`, `fileIdsGeneration: 2`, `bakeAxisConversion: 0`, `preserveHierarchy: 0`, `useFileScale: 1`. Blend shape import is enabled. |
| Initial geometry cost | Unknown during the first review because import failed. The follow-up records model and material counts, but no accepted geometry or frame-time budget. |

The [existing parity corpus](../../testing/rendering/poiyomi-parity-validation.md) is pinned to Poiyomi Toon 9.3.64. It does not validate the Pro 9.3.66 source in this prefab.

The saved materials identify these features that can affect the supplied image:

| Visible area | Source evidence | Parity concern |
|---|---|---|
| Skin and face | `MAT_BODY 2` and `MAT FACE 3` enable shading and `_MochieBRDF`. The body enables rim lighting. The face enables glitter. | Toon light response, normal response, rim light, and specular response must survive native shading. |
| Eyes and lashes | The iris enables emission and a matcap. The lashes enable a matcap. | Eye color, highlight position, emission, and eyelash coverage need separate inspection. Queue 2449 alone does not prove masked coverage. |
| Hair and tail | Hair uses ramps, normal maps, and emission. Tail and bangs use emission strength 5. Bangs use source-alpha blending with depth writes. | Preserve strand coverage, two-sided appearance, normals, emission, and the mixed clipping/blending state. |
| Clothes and accessories | Several materials use `_MochieBRDF` and multiple matcaps. Some enable hue shift, decals, or dissolve. | Standard PBR alone cannot reproduce the authored layered highlights and texture effects. |

All inspected override materials have outlines disabled. Full outline support is not the first requirement for this reference. `BasicTee` has a Grab Pass source tag, but its saved refraction switch is disabled. The tag alone does not justify a refraction implementation for the pictured outfit.

Some hair materials retain `_AlphaToMask: 1`. The inspected locked hair shaders use `AlphaToMask Off`, and the current source field is `_AlphaToCoverage`. The old saved field is not evidence that those shaders use alpha-to-coverage. Disabled emission and other saved controls must also remain disabled during conversion.

## Initial Live Import Failure

The serialized prefab path selects Assimp in [SerializedSceneImporter.Models.cs](../../../../XREngine.Editor/Importers/SerializedSceneImporter.Models.cs), even when the generic model setting prefers the native FBX backend. The import uses the Unity metadata and performs the handedness conversion through the Assimp options.

The Debug log records this failure after about 151 seconds of import work:

```text
SourceVisualImportException: Assimp produced a non-identity synthetic RootNode while importing '<unity-project-root>/Assets/Avatars/JAX/Mine/jax2031.fbx'. The Unity generation-2 hierarchy cannot be flattened without applying an additional coordinate conversion.
```

`CollapseAssimpSyntheticRoot` rejects this case before the prefab hierarchy and material overrides can finish. The actual root matrix is not in the diagnostic. Its axis, unit, and pivot contribution therefore remains unknown. Removing the guard would risk a coordinate error, invalid bounds, wrong bind matrices, and unstable Unity file-ID mapping.

The underlying model import also reports 80 unresolved texture references. These references include old absolute paths stored inside the FBX. This does not prove that 80 final Unity material textures are missing: the 29 explicit override material GUIDs resolve. Final inherited slots and textures still need inspection after prefab expansion succeeds.

The final scene contains three nodes and two components: a directional light and a skybox. It contains no avatar model component. The profiler reports no skinned avatar geometry. The import failure is the first blocker for a valid visual comparison.

## Advanced Shader Contract Findings

The importer already converts many common Poiyomi modules to Uber. [SerializedMaterialImporter.cs](../../../../XREngine.Editor/Importers/SerializedMaterialImporter.cs) also uses `SourceToonFeatureLossNormalizer` for the Pro downgrade path. Pro-exclusive features can be removed with diagnostics. This is not a complete Pro parity contract.

The main gap is between that Uber material and Advanced native shading:

- [AdvancedGpuScenePublisher.MaterialTransitions.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.MaterialTransitions.cs) chooses a built-in layout from the render pass and state class. It does not prove that the material's Uber feature program has a native equivalent.
- [AdvancedGpuMaterialPublisher.cs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuMaterialPublisher.cs) translates the built-in opaque, forward opaque, masked, and mirror layouts. This bridge is not a native Uber evaluator.
- [MaterialBindingSourceEncoder.cs](../../../../XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingSourceEncoder.cs) reads standard surface semantics. Without semantic bindings, it assumes texture positions 0 through 2 are albedo, normal, and metallic/roughness. It reads `BaseColor` or `MatColor`, while the Poiyomi conversion writes `_Color`. This can also lose values before shading starts.
- [ShadeNativeOpaqueEvaluator.glslinc](../../../../Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaqueEvaluator.glslinc) evaluates the mirror path or standard PBR. It does not execute the converted Uber fragment modules.

An opaque or masked Uber draw can therefore be accepted as standard PBR. This is a static contract finding. The failed avatar import prevented observation of this behavior on the exact prefab. Vulkan compilation of a forward Uber shader does not close this native shading gap.

Coverage is another contract gap. [VisibilityRasterMasked.frag](../../../../Build/CommonAssets/Shaders/Advanced/Visibility/VisibilityRasterMasked.frag) and [DirectionalShadowRasterMasked.frag](../../../../Build/CommonAssets/Shaders/Advanced/Visibility/DirectionalShadowRasterMasked.frag) apply standard base alpha and cutoff. They do not evaluate the complete Uber alpha-mask or dissolve rules. Otherwise opaque materials can also require discard coverage. Coverage must be correct before the visibility depth write. A discard in later compute shading cannot reveal geometry that the depth write already hid.

The importer assigns transparency modes and render passes, but it does not assign `AdvancedLatePassMetadata`. [AdvancedLatePassEligibilityValidator.cs](../../../../XREngine.Runtime.Rendering/Rendering/Transparency/Advanced/AdvancedLatePassEligibilityValidator.cs) rejects ordinary late transparency without that metadata. Metadata permits admission; it does not create matching velocity and reactive shader variants or compatible geometry. The current permitted late path uses CPU draw submission and GPU shader execution. It needs its own cost measurement; this is separate from the native opaque zero-readback path.

Advanced already has aggregate GPU skinning and blend shape support. This foundation does not prove this avatar's bind pose, morph weights, motion vectors, or shader vertex effects. Compute surface reconstruction currently exposes UV0 and UV1. Additional UV channels and fragment-style sampling gradients need explicit support when an active Uber feature requires them.

## Initial Build And Runtime Results

| Check | Result |
|---|---|
| Isolated Release editor build | Passed with zero warnings and zero errors. |
| Isolated Debug editor build | Passed with zero warnings and zero errors. |
| Runtime request | Vulkan, Advanced required, `GpuIndirectZeroReadback`, desktop mono, 1920 by 1080. No submission downgrade was reported. |
| Hardware context | Local system includes an NVIDIA GeForce RTX 3090. Runtime capabilities report an NVIDIA Vulkan renderer. |
| Release Advanced admission | Blocked on `ShadeNativeOpaqueMsaa.comp`: `The compute shader is being regenerated.` The later snapshot still reports pending resources after 106.3 seconds and 18,893 preparation attempts. |
| Debug Advanced admission | The bound profile reaches `Admitted` / `Ready` after 5.37 seconds. A separate generic capability discovery result reports `MissingShaderFamily`; it does not describe the bound profile's admission state. |
| Startup logs | Debug records a required output reservation failure before the later bound profile becomes ready. This run is not an error-free production acceptance result. |
| Visual captures | Front and oblique Release readbacks succeed, but both viewed PNGs are black. No avatar exists in the scene. |
| Avatar performance | Not measured. Empty-scene profiler output is not an avatar FPS result. |

The Release pending-resource cause is unresolved. It is a separate readiness failure from the FBX root failure. A warmed Release run must reach stable admission before a frame-time result can be used.

## Implementation Follow-Up

The requested entry remains the Unity `.prefab`. The implementation does not replace it with a direct FBX import. Source-project resolution composes the FBX model base, nested prefabs, stripped references, and overrides.

The diagnostic run identified an identity outer wrapper and a synthetic root with a positive 90-degree X rotation and 49 direct children. Its matrix, rounded to the measured basis, is:

```text
1  0  0  0
0  0  1  0
0 -1  0  0
0  0  0  1
```

`SerializedSceneImporter.ModelRoots.cs` now redistributes this basis with `childLocalNew = childLocalOld * rootBasis`, then makes the root identity. It validates all child matrices before mutation. It rejects non-finite, singular, or sheared results and retains guards for unsupported root shapes, root skin influences, and explicit bounds that use the changed basis. The operation preserves authored descendant world transforms without applying another conversion to vertices, morphs, or winding.

The metadata parser now reads the `meshes` mapping before legacy root-level fields. The exact asset's `sortHierarchyByName: 1` was previously ignored. Both prefab entry points now pass the requested backend through `SourceProjectImportContext`. Unit-world `NativeOnly` and `AssimpOnly` select explicit backends. Actual producer identity is logged after model import. The settings schema was regenerated.

| Check | Follow-up result |
|---|---|
| Debug editor build | Passed with zero warnings and zero errors. |
| Exact prefab with `AssimpOnly` | Completed composition. Log reports 69 model components, 67 materials, 28 Poiyomi-to-Uber conversions, and 28 Pro downgrades. These counts are not draw counts. |
| Root normalization | Applied the measured basis to 49 direct children. Nested `FT_UE_Debug.fbx` also used Assimp under the same prefab request. |
| Scene publication | Scene statistics report 1,044 nodes, 973 active nodes, and 168 components after import. |
| Vulkan admission | Advanced reports `Admitted` / `Ready`. Requested and resolved `GpuIndirectZeroReadback` agree without a submission downgrade. |
| Viewed output | Front and oblique captures show the avatar. Detached geometry, stretched triangles, a pose difference, and large shading differences remain. The captures do not establish coordinate, deformation, or material parity. |
| Publication diagnostics | One recorded visibility submission retry reports no exact GPU deformation output with `offsetsFit=False` and `forceCpuDiagnostic=True`. This appeared during model publication. Its relation to the persistent visual defects is not established. |
| Exact prefab with `NativeOnly` | Selected `xrengine.native-fbx@5` as the only candidate and completed the FBX parse. The returned root contains `FbxImportedContentBasis` with a negative 90-degree X rotation. Publication stops at an explicit Unity normalization guard. No Assimp fallback occurred. |
| Existing targeted tests | 34 passed, zero failed, zero skipped. The filter covers model coordinate options, source-project context, unit-world model settings, and serialized scene import. No tests were added or changed. |

The initial root rejection is removed for this Assimp case. Native FBX has different data conventions. It creates a content-basis helper, does not consume the Assimp handedness flags, and retains mesh-local cluster offsets. The existing Unity skeleton rebase replaces palette entries with bone inverse-world matrices. That would discard the native cluster offsets, so native publication remains guarded until the shared conversion is complete.

The source review also found risks after the root conversion. FBX and Unity skeleton metadata have different X signs for the named legs and eyes. Some unskinned hair nodes contain scales near 0.01, which can be applied twice if Assimp bakes scale into vertices and metadata restores it on the node. The FBX has 859 authored models, while the skeleton metadata has 863 entries and 18 parent mismatches. These findings require landmark and pose comparison. They are not proof of the cause of every artifact in the capture. Actual FBX hierarchy must remain the source of object identity.

## Next Decision

Resolve the coordinate, metadata-pose, and bind-palette contracts before treating the visible avatar as a valid shader reference. Compare the same prefab through both explicit backends. Then select the active outfit and shader features for the native Uber work. The linked code todos own implementation work. The linked validation document owns visual, motion, and performance checks.

The full-frame limits are 16.7 ms for 60 Hz, 11.1 ms for 90 Hz, and 8.3 ms for 120 Hz. These are target budgets, not measured results or avatar-only allowances. No numerical FPS claim is supported by this run.
