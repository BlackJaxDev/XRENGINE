# Jax2031 Unity And Advanced Vulkan Parity

Date: 2026-10-08
Last updated: 2026-10-13
Status: Both prefab routes pass the focused Debug Advanced directional SH, local-light, Mochie probe, clearcoat reflection, backface-normal, texture-publication, and live-edit controls. Probe and local-light buffer lifetime checks fail. The saved harness state is preserved. Matched Unity visual parity, Release validation, and frame-time acceptance remain open.

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

## Merged Build And Skinning Snapshot Review, 2026-10-11

The baseline source is `d3a9c2fad`. The isolated Debug session
`jax-parity-merged` built with zero warnings and errors. Assimp again completed
the Unity prefab with 69 model components, 67 materials, and 28 Poiyomi-to-Uber
conversions. Advanced admitted Vulkan with `GpuIndirectZeroReadback` and no
submission downgrade. Viewed front and oblique captures still show bent arms,
detached geometry above the head, stretched triangles, and incorrect materials.

The Body audit reports 25,208 vertices and 72 bones. Its packed weights are valid,
and its bind-pose vertex displacement rounds to zero. The live expected palette
differs from the renderer palette. This is evidence to inspect palette ownership;
it does not prove that the coordinate conversion is correct.

Source review found that skeleton rebase, serialized transform rebinding, and
both prefab renderer remap paths changed `XRMesh.UtilizedBones` without updating
`XRMeshSkinningBufferState.UtilizedBones`. `XRMeshDeformationInputs` reads the
snapshot. Deep mesh cloning also assigned buffers before it filled the bone
table, which left the snapshot with an empty palette. The correction publishes
the completed table with the existing packed buffers and fills cloned palettes
before buffer assignment. The inverse-bind formula and native publication guard
remain unchanged for this comparison.

The corrected session `jax-parity-bindings` built with zero warnings and errors.
Its first run composed the same prefab. Diagnostics inspected 69 model components
and 101 mesh/LOD rows; all reported zero snapshot binding mismatches. However,
the collection loop stopped during scene growth. `get_time_state` recorded this
call sequence:

```text
GPUScene.Add
  -> VerifyUpdatingBufferSize
  -> EnsureLodTransitionBufferCapacity
  -> SyncLodTransitionBufferFromGpu
  -> XRDataBuffer.MapBufferData
  -> GenericRenderObject.EnsureApiWrapperForOwnerFirstUse
```

The error states that no active render owner was available. The screenshot request
then timed out. The stopped-loop palette audit cannot establish rendered output.
These GPUScene calls predate the correction. An earlier dispatch must set the GPU
write flag before later capacity growth reaches this path. Skipping the owner
check is unsafe: resizing from a stale CPU mirror can overwrite GPU-authored LOD
state. A render-owner preservation boundary is required.

An identical-binary restart in a new process completed publication and stayed
live. No capacity override or fallback was used. All 101 mesh/LOD rows again had
zero snapshot mismatches; 87 rows were skinned, with no detached palette bones.
Advanced was ready with the requested GPU submission strategy and no downgrade.
Viewed front and oblique captures still show the pose and detached-geometry
failures. The Body live palette difference remains about 1.960379. Snapshot
coherence is therefore not the sole cause. The environment image also changed
between launches, so these captures support qualitative geometry checks only.
Pin the environment before any pixel or material comparison.

The successful retry still logged an initial output-reservation retry and the
same temporary GPU deformation slice rejection seen in the baseline. Both named
sessions are stopped. The timing-dependent capacity failure remains open.

Existing tests ran with the filter `XRMeshSerializationTests`,
`GpuSkinningBufferCompressionTests`, and `SerializedSceneImporterTests` in an
isolated artifacts directory. The result was 21 passed and one failed.
`YamlSerializer_Reads_CookedBinaryWrappedRuntimeMeshPayload` returned a
`FailedCookedMesh` placeholder instead of the sample mesh. The clone and skinning
checks passed. No test source changed. The YAML failure needs a separate cause
check; a baseline test run has not established whether it predates this change.

Source review isolates a wrapper mismatch outside the binding change. The test
creates an unskinned triangle and does not call clone or rebind. The current
`CookedBinarySerializer` starts reference objects with marker `ReferenceDefinition`
(`62`), while `XRMeshYamlTypeConverter.TryExtractRuntimeCookedPayload` recognizes
only the legacy leading `CustomObject` marker (`24`). The complete wrapper then
reaches the runtime decoder, which does not accept marker `62`, and the converter
returns the placeholder. Repair must retain both the legacy wrapper and raw
runtime payload support. No serializer or test code changed in this work.

The merged material source now includes `AdvancedUberBaseSurfaceRecord`,
`AdvancedUberBaseSurfaceSourceEncoder`, and a source-contract field. However,
`EngineUberBaseNativeAdmission` resolves a WebGPU shader artifact. Vulkan does not
enforce that source contract or execute the browser Uber evaluator. Its native
opaque evaluator and masked visibility/shadow shaders still use standard material
semantics. Imported late transparency still lacks `AdvancedLatePassMetadata`.
The rendering todo now extends this shared data work instead of duplicating it.

The API broker still exposes deprecated model IDs. Route advice returned a
GPT-5.6 model despite a GPT-6 constraint. No paid worker was started. Native agents
reviewed the bind publication and material source paths.

## Coordinate And Renderer Construction Correction: 2026-10-11

The body palette still differed after snapshot publication was fixed. Source review
found that `ModelComponent` can create renderers before a node enters a world.
The importer then changed their inverse binds during metadata and prefab
composition. The renderer retained its earlier `RenderBone.InvBindMatrix` values.
Unity prefab conversion now suppresses runtime mesh construction
through composition and cooking. World attachment creates the final renderers.

Session `jax-parity-rebase` built with zero warnings and errors. The body audit
changed from a maximum live palette difference of 1.960379 to zero at six decimal
places. All 101 mesh/LOD rows reported coherent snapshots. Front and oblique
captures were viewed. Stretched hand geometry and pose errors remained. This
separates stale renderer bindings from the remaining coordinate problem.
Advanced Vulkan was ready with requested and resolved submission strategy 2 and
no downgrade. The session was stopped.

These runs set `XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR=1024` to avoid the known
scene-growth failure. This changes initial storage capacity. It does not change
active scene counts or select a CPU fallback. The result does not validate
dynamic scene growth. The environment image still varies across process starts.
The captures support qualitative geometry checks, not material or pixel parity.

Source inspection found two FBX dependencies with the same supported conversion
contract. The main model uses +X right, +Z up, and -Y front. The face-tracking debug
model uses +X right, +Y up, and +Z front. Both use metre units, global scale 1,
XYZ rotation, inheritance type 1, and no pivot or geometric offsets. Unity leaves
axis conversion on the hierarchy. The main FBX also contains one animation stack;
the debug model has none.

The Assimp result requires a further X reflection. The native result requires a
180-degree Y rotation. A root-only rotation cannot correct local joint axes.
The conversion changes geometry, local transforms, inverse binds, and bind-root
matrices together. Root normalization then excludes generated helpers from Unity
identities. Metadata pose replacement preserves each old inverse-bind/bone-bind
product. Unskinned geometry retains its world placement when metadata restores
scales that Assimp had baked into vertices. Mesh and submesh bounds are refreshed.

The first `jax-parity-coordinates` build passed with zero warnings and errors.
Its import reached unskinned metadata compensation and stopped at an exact affine
column check. Matrix inversion had left rounding in that column. The helper now
accepts an absolute error up to 1e-5, resets the column to affine form, and still
rejects projective or singular conversions. Its exceptions include the mesh and
matrix. The corrected build passed with zero warnings and errors.

Source animations still need path, axis, and bind conversion. Native raw clip
components are removed at the Unity boundary, and an explicit behavior diagnostic
keeps imports with source animation at `VisualPrefab` completion. The FBX remains
a tracked source dependency. This does not change standalone native FBX import.

A read-only review checked the row-vector conversion, root palette products,
unskinned owner transforms, explicit bounds, and geometry cache invalidation. It
found a stale `SubMesh.Bounds` cache and first-component-only clip removal. Both
were corrected before the successful rebuild.

The next run completed visual conversion but the startup `Animated` profile
required `VisualAndAvatarBehavior`. The explicit unsupported-animation diagnostic
correctly stopped that profile. The isolated visual profiles now use `Kind=Static`
with skinning enabled. This avoids avatar post-import behavior setup and does not
claim animation acceptance. Production settings and source assets were not changed.

Existing regression checks initially found one new scene-load failure: the generic
`.unity` entry no longer created its immediate runtime mesh list. The suppression
scope was narrowed to prefab conversion. The final existing checks report 21 passes
and the same cooked YAML wrapper failure described above. No test source changed.

The final static visual runs used the same binaries and the exact `.prefab` entry.
Assimp produced 69 model components, 67 materials, and 101 mesh/LOD audit rows.
Native produced 69 model components, 69 materials, and 107 mesh/LOD audit rows.
All 69 model node IDs agree between routes. Every audited row reported zero
snapshot binding mismatches and no detached palette bones. The body live palette
difference rounds to zero at six decimal places for both routes.

Native splits the body into chunks with 65535, 65535, and 6774 vertices. Its
mesh-local cluster offsets are intentionally non-identity. After bind deformation,
the combined body bounds agree with Assimp at approximately
`(-0.676, -0.005, -0.119)` to `(0.676, 1.249, 0.166)`. A non-identity native bind
palette alone is not evidence of a deformation defect.

Front and oblique captures were saved and viewed for each route. Both show a
T-pose without the long hand spikes from the earlier animated profile. Assimp
still shows an extra round object above the avatar; native does not show it in
these views. The source of this difference is not established. The native
`Yuno_Head_FT_Source` node reports both `IsActiveSelf=false` and
`IsActiveInHierarchy=false`. Audit presence does not establish visible submission.
The head source and `FT_Debug` have matching local bounds between routes, but
their submesh order differs. Material-slot identity and active-state comparison
remain required. The 67 versus 69 material count also needs a complete slot audit.

Both runs reported Advanced admission `Ready`, requested and resolved submission
strategy 2, no downgrade, and no terminal engine fault. Native logs still contain
the initial output-reservation retry and one GPU deformation output rejection
during publication. The later captures and admission state do not clear those
startup defects. The final session was stopped after the node-state check.

These results validate a static visual profile with skinning enabled. They do not
validate source animation, avatar post-import behavior, previous-frame palettes,
motion vectors, or the original animated startup profile. The environment differs
between launches. Shading remains visibly incorrect, and no frame-time acceptance
was measured.

## Source Animation Conversion Investigation (2026-10-11)

The source FBX contains one `Armature|ArmatureAction` stack. It spans 1.5833334
seconds, with 786 node bindings, 607 shape bindings, and 39 distinct source key
times. Shoulder and arm rotation changes provide the motion checks. The shape
curves in this action are constant zero; this action cannot prove animated face
expression parity.

Strict curve validation first rejected repeated `FullWeights` values and named
geometry-property curves. Inspection found one Shape per channel, with uniform
positive repeated weights. Some geometry properties mirror their same-name
shape channels. The parser now requires matching defaults and identical duplicate
curve times and values before it accepts these mirrors. It still rejects
progressive shapes, conflicting curves, unsupported interpolation, and ambiguous
targets. The exact source then passed the strict parser.

The first animated Assimp import found another naming difference. The
`Milltina_cloth_panties` mesh has six shape attachments. Assimp names them with
joined channel and Shape names, such as
`Option_Waist_wide.Option_Waist_wide`. The source channel and Shape each have the
single name `Option_Waist_wide`. The data is present. Animation binding now checks
the source connection and resolves one imported channel name on that geometry.
It does not remove arbitrary name suffixes or discard missing channels.

The native animated profile completed import and published 2,965 tracks. The first
seek request then lost its editor connection, and the isolated process stopped.
No captured log established the immediate process-exit cause. Review found that
the initial playback copy used a costly legacy serialization path which also
temporarily replaced source keyframe tracks and lost infinity and baked settings.
That copy path was replaced with independent typed tracks and fresh member
bindings. Curve endpoints now clamp, including shape curves that end before the
whole action. Later live runs completed seeks and playback with this copy path.

The first successful Assimp seek checks still showed the bind pose. The unit-world
`Animated` setup had added an empty `AnimStateMachineComponent` to the outer
import wrapper. That controller overwrote the child source clip's pose each frame.
Disabling only this controller gave the source clip sole pose ownership. The
wrapper `HumanoidComponent` remained active. This is a validation setup change;
it does not establish Unity controller blending or humanoid retargeting support.

The final Debug binary loaded the exact prefab with `NativeOnly` and `AssimpOnly`.
Each route published one 1.5833334-second generic clip with 2,965 property tracks:
three local TRS tracks for each of 786 nodes, plus 607 shape tracks. The source
graph remains separate from each component's mutable playback graph. A disposable
copy probe checked source key preservation, independent clocks, infinity modes,
baked tracks, method delegates, object identity, and member initializers.

At 0, 0.9583333, 1.2083333, and 1.5833334 seconds, the five sampled nodes
(`Shoulder_R`, `Shoulder_L`, `Arm_R`, `Arm_L`, and `Body`) had identical local,
world, and render transform components across routes. Front and oblique captures
showed the expected arm poses without the earlier long hand spikes. Capture
requests immediately after a seek could read the previous rendered pose. A
two-second settle interval before capture removed that ambiguity.

Both routes retained 69 model components. Assimp had 101 mesh/LOD audit rows;
native had 107. All rows had zero snapshot binding mismatches, detached bones,
missing palette bones, invalid influences, and non-finite values. The body live
palette difference rounded to zero at six decimal places. Some accessory palette
differences remain nonzero; this check does not establish complete accessory
deformation parity. Native mesh-local bind offsets are non-identity by design.

With a fixed camera, paused native output had zero velocity. Half-speed source
playback produced finite, nonzero velocity around the moving arms. Both routes
kept Advanced admission and GPU submission without a downgrade or terminal fault.
The capacity floor remained 1024, so this result does not clear scene-growth
failures. Both final runs logged an output-reservation failure before later
admission. Native also logged directional shadow render failures. Assimp logged a
shadow backend submission rejection. These rendering defects remain open.

The final Assimp run repeatedly rejected `Mat_Glow 5` from the Advanced late pass
because the material had no explicit eligibility metadata. It also rejected late
compute and raster after two canonical scene-source texture mismatches: `Black`
and `7517 geo baskets_Normal_OpenGL`, with 128 by 128 versus 64 by 64 dimensions.
These were live warnings, not teardown events. They remain evidence for material
publication and late-pass work. Both owned sessions were stopped after capture.

The final editor build had no warnings or errors. The existing
`AnimationClipComponentTests`, `AnimationClipSerializationTests`,
`NativeFbxImporterTests`, and `SerializedSceneImporterTests` passed: 46 tests,
zero failures. No test source changed. This selection does not include or clear
the earlier cooked YAML wrapper failure.

## Material Binding Investigation, 2026-10-11

The source inventory resolves all 74 FBX external material remaps and all 94
non-null outer-prefab material overrides. Two further override entries are null.
The combined inventory contains 103 distinct Unity materials and 106 texture
assets. All required source paths resolve inside the Unity project. The texture
inventory has 54 sRGB entries and 52 linear entries, including 25 normal maps.

The source FBX has 144 material-to-model connections across 68 models. Global
material object order differs from renderer slot order on `CLO LegWarmers` and
`H | Back Ponytail Cupkake`. Native import previously used global object order.
The corrected route reads connection order. Both routes retain source slot
identity across mesh chunks and prefab clones. Two Unity remap keys replace `/`
with `_`: `Bodysuit / socks / sleeves.001` and `Bra/Panties`. The importer now
accepts these unique connected aliases and rejects ambiguous names.

Material replacements previously took effect after producer texture loading.
Native import could also apply FBX surface values to the resolved Unity material.
Both routes now resolve replacements first. Preview callbacks and resident
uploads previously replaced authored sampling filters or reset the internal
format to linear eight-bit RGBA. They now retain authored sampling and select
the resident color format from effective import metadata.

The first live check with strict source-slot overrides stopped the Assimp import
because an override could not find source slot 0. No avatar was published in that
run. The failure is an import rejection, not a Vulkan material acceptance result.
The diagnostic run identified `C - Panties`: its faces use raw FBX slot 2,
but Unity overrides renderer index 0. Seven source models have unused material
connections. The saved Face and Garter overrides also distinguish the ordering:
Unity uses first polygon occurrence for drawable materials, not ascending raw
connection index. Sorting the raw indices would assign face and eyelash materials
to the wrong surfaces. The source slot and compact renderer index must remain
separate through chunking and cloning.

The next run rejected the nested `FT_UE_Debug` model during name lookup. Source
inspection ruled out the initial no-material hypothesis. The FBX contains one
model named `Debug`, one geometry, and one `Debug` material. Root normalization
renames that model to the asset stem. Slot resolution must read source names
before root normalization changes them.

The earlier `Black` and basket-normal canonical mismatches coincide with texture
promotion. Source metadata changed from 64 by 64 to 128 by 128 before the GPU
upload completed, with an interval of about 330 ms. No later mismatch was found
after streaming became idle in that run. This supports a publication-order
defect; it does not prove a persistent wrong source binding. The current Vulkan
upload path still needs a separate transaction that keeps logical source
metadata and native descriptor publication coherent. Keep the strict validator.

The corrected Assimp run completed with 101 mesh/LOD rows, including 30 active
rows and 21 active Unity materials. All active material paths resolve to the
source inventory. No active source-backed 2D sampling mismatch remained after
the audit accounted for bilinear mip filtering and sampler-specific texture
usage. Generated default samplers have no source path. They are not missing
source files. The converted HDR `_CubeMap` is a separate case: its importer
constructs a native cube and explicitly marks it linear. A direct comparison
with the original 2D importer sRGB flag does not validate that cube's shading.

RenderDoc captured an admitted Advanced Vulkan frame. Exported and viewed
clothing and accessory color textures used `R8G8B8A8_SRGB`; their matching normal
textures used `R8G8B8A8_UNORM`. Front, oblique, and face captures showed the avatar,
but the native output remained very dark with incorrect highlights. This confirms
GPU texture content and formats, not authored Uber shading or matched Unity
lighting. The capture retained GPU indirect submission without a terminal fault.

The final slot implementation passed 31 existing importer and toon-conversion
tests. An earlier broader selection had 45 passes and nine failures. All nine
failures were source-text assertions in `ImportedTextureStreamingContractTests`.
Their expected tokens were absent at the asserted paths in both Git HEAD and the
working tree; the implementations had moved to split files. No test source changed.

One native validation attempt lost its MCP listener after a session preference
change. The editor continued to render. The next attempt retained the startup
dispatch setting and used the same binary. This incident does not establish a
material import failure.

The final native run completed with 107 mesh/LOD rows, including 35 active rows.
Both routes have 19 active model nodes, 30 distinct active renderer slots, and
21 active Unity materials. Native has extra mesh chunks, not extra active
material assignments. All active material paths, raw source-slot sets, and
source-backed 2D texture settings agree. The all-node comparison matches 100 of
101 renderer-slot assignments. The one difference is inactive
`Yuno_Head_FT_Source`, renderer slot 3, raw slot 1 (`Emote`): native adds an
`_ImportedBaseColor` default sampler. This result does not accept every inactive
producer material or generated texture.

The final native RenderDoc capture also uses Vulkan and contains indirect draws
and compute dispatches. Exported and viewed clothing and body color textures use
`R8G8B8A8_SRGB`; clothing and accessory normal textures use
`R8G8B8A8_UNORM`. Both runs retain Advanced admission, requested and resolved
submission strategy 2, no downgrade, and no terminal fault. Front and oblique
images remain dark with incorrect colored highlights. Their environments and
animation times differ, so these images do not establish quantitative Unity
parity. Resource exports prove resident texture content and formats; they do not
prove that the native Uber evaluator consumes every authored feature.

The native streaming snapshot still had 21 pending transitions, with two active
decodes and 19 queued decodes. It had no active GPU upload or upload failure at
that sample. This check validates bindings and sampled resident textures, not a
fully settled streaming budget or promotion soak.

The final editor build has zero warnings and errors. Both RenderDoc capture
sessions and the owned editor session are closed. The capacity floor remains
1024. No Release frame-time or capacity-growth acceptance is claimed.

## Native Uber Execution Investigation (2026-10-11)

Native opaque and masked layouts now carry the supported Uber constants and
72 texture roles. They use source contract 11. Unsupported active operations
return a reason instead of entering the standard PBR path. The shared coverage
evaluator runs in visibility, directional shadows, and native shading. The
transparent horn material uses explicit sorted alpha metadata.

The first runtime checks exposed two shader compile errors: generated names
contained reserved consecutive underscores, and the mesh shader read an output
variable to supply a UV fallback. Both were corrected. Material preflight then
exposed real active glitter, theme, and decal features. These features were
implemented. Pipeline-owned lighting and shadow switches are not material
operation rejections. Active unsupported depth and procedural effects still
fail explicitly.

The source Face geometry has UV0 and UV1, but no UV2 or UV3. The iris and lash
materials select UV2 for one decal. The forward shader supplies zero for that
missing channel. Raster and compute now preserve that rule. UV2 and UV3 use
reserved words in the existing 64-byte packed vertex; the stride is unchanged.

An Assimp GPU capture contains 16 Uber rows with source contract 11, 72 texture
references, and separate opaque and masked layout hashes. Four indirect shading
dispatches have nonzero work. The late horn draw has 440 triangles and uses
premultiplied color blending: One, InvSrcAlpha, Add. Its exported target was
viewed. Both prefab routes reach Advanced admission with GPU indirect submission
and no terminal fault in these runs. Front and oblique images were captured and
viewed. This proves execution, not Unity image parity.

The images exposed shading differences. Source shader review found that active
Mochie materials use their own metallic and smoothness multipliers. Packed green
is smoothness and is inverted only by the source invert control. The old mapping
used generic controls and always inverted green. Native shading also added
custom specular for each light and applied probe specular after toon limits.
The canonical shader applies custom specular once with the primary directional
light and includes probe specular in toon ambient. Local-light attenuation also
differed. These findings define focused corrections; a low-resolution streamed
texture does not explain all of the shading difference.

The correction preserves the packed blue reflection mask and alpha direct
specular mask with independent source strengths. Generic Uber materials keep
their previous path. The editor rebuild completed with zero warnings and errors.
All 26 selected material database, scene identity, and source toon conversion
tests passed after this correction.

A fixed bind-pose run with the skybox disabled exposed automatic exposure as a
test-setting problem: the black background caused severe overexposure and bloom.
Use manual exposure 1 and disable bloom through the camera post-process tools
before comparing these runs. One capture during texture promotion contains only
late and post-process work. Its log rejects a canonical texture source whose
published 64-pixel extent changed to 128 pixels before residency publication.
Profile admission alone therefore does not prove that a specific frame executed
native shading. Keep this captured failure separate from the earlier valid
native dispatch evidence. Do not disable the source consistency check.

The corrected native FBX run uses fixed exposure and no bloom. Its GPU capture
contains 16 native Uber rows with 2312 words and 72 texture roles, four standard
rows, and four nonzero indirect shading dispatches. The horn draw has 440
triangles. Front and oblique images show a coherent T-pose, hair coverage, and
emission. They also show body/clothing intersections and a bright face. These
fail image acceptance. Check the final body and clothing morph state, clipping
masks, and lighting inputs before assigning those symptoms to a new shader fix.

The corrected Assimp run uses the same static settings, exposure 1, and no bloom.
It retains the tank coverage, but the front bangs are absent in both viewed
camera positions. The native route retains the bangs but shows the body through
the tank. Both routes have a bright face and strong clothing highlights. The
Assimp capture has four nonzero native indirect shading dispatches. Neither
route downgraded submission or reported a terminal fault. These runs isolate
backend-specific geometry or coverage differences from the earlier changing
environment and pose. Full Unity image parity and Release frame-time acceptance
remain open. All owned editor and RenderDoc sessions were closed after capture.

Two earlier Assimp attempts terminated during a legacy directional-shadow
buffer upload. One Windows event reports an access violation in
`VulkanBufferResourceService.UpdateFromVoidPtr`. A later identical route stays
live. Review found that `VkDataBuffer.PushData` obtains a raw CPU address without
a source-storage lease. Concurrent storage replacement is a possible cause,
but the failure did not record the buffer identity or source capacity. No
speculative buffer-lifetime fix was made. A repeat needs those diagnostics.

Existing material database and scene identity tests passed (15 checks). Eleven
of twelve selected deformation preparation tests passed. The failing
`CurrentPreviousArenaHandlesLodTopologyAndNewVisibility` expects valid velocity
without confirming produced owner history. It also fails alone. No test source
was changed. This result does not clear the older import test failures.

## Indexed Morphs And Hair Placement, 2026-10-11

The user reported that a blendshape needed to be set to 0% to prevent body
clipping. A live native import confirms the cause. The native parser sorted
numeric channel IDs instead of retaining the FBX connection order. It therefore
applied 100% to a blendshape whose saved weight was 0%. Correcting the weight
in the isolated editor removes the body intersection in front and oblique
views. No source prefab or material was changed.

`FbxDeformerParser` now retains geometry-to-deformer and deformer-to-channel
connection order. The source probe confirms all 607 channels across 24 geometry
objects use that order. The FBX build has no warnings or errors. All 15 existing
native FBX importer tests pass; no test source changed. A fresh native prefab
import preserves the saved blendshape weights, including the required 0%. Front,
oblique, and wide Vulkan views show the tank covering the body and hair on the
head. The native GPU capture has 16 Uber rows, 2312 constant words and 72 texture
roles per Uber row, and four nonzero indirect shading dispatches. Advanced GPU
submission has no downgrade or terminal fault. This confirms the body fix;
the bright face and strong material highlights still fail full image parity.

The Assimp `Base1` and `Base2` renderers are active with their sole morph at
100%, as in native FBX. Source target bounds and maximum morph deltas match.
Changing the Assimp `Base2` morph to zero does not restore the bangs. A wide
front view and a wide oblique view show the hair above the avatar. CPU source
and bind bounds are already at Y 2.4 to 2.6. The skin palette has no binding
mismatch or non-finite values. This rules out missing mesh data, the named
inactive bang alternatives, and a wrong hair morph index. The placement defect
must be corrected before shader coverage is assessed again.

Assimp's generated bone offset and bone world products reproduce the incorrect
full mesh-node placement. They cannot supply the correction. The serialized
FBX cluster product `Transform * TransformLink` retains the hair mesh's authored
local translation and excludes the duplicated parent placement. All 62 skinned
source geometries have unique names and agreement across weighted clusters.
Use this source bind frame at the Assimp geometry bake boundary. Do not clear
all translations or change the source asset.

The Assimp resolver applies that source bind through the existing base-vertex
and morph bake. The source bind is conjugated by the Z reflection when the
effective import steps use `MakeLeftHanded`. A paired Assimp probe confirms
that both vertices and node matrices use that reflection. All 62 source skins
pass the final finite, affine, and absolute-plus-relative matrix checks. No
source geometry has a nonidentity geometric transform. A build made before
the handedness correction was stopped and is not validation evidence.

Disposable evidence is in the task run's `mcp-output/animation-morph-*-baseline-*`,
`reports/morph-order-source.json`, `reports/morph-order-fixed.json`, and
`logs/morph-baselines/`. The broker route tool returned a deprecated model
despite the GPT-6 constraint, so no paid worker was started. A native read-only
review found no blocking issue in the connection-order correction.

The final editor build has zero warnings and errors. Fresh `AssimpOnly` and
`NativeOnly` runs both import the exact prefab. Front, oblique, and wide captures
were viewed. The bangs now stay on the head. The affected blendshape is set to
0%; the tank covers the body in both routes.
Four source-clip seek times were captured, with an additional oblique end pose.
Viewed samples retain the hair placement and tank coverage during arm deformation.
This check does not add acceptance for nonzero expression curves or motion vectors.

Both final GPU captures contain 16 Uber rows with source contract 11, 2312 words,
and 72 texture roles, plus four standard rows. Each capture has four nonzero
native indirect shading dispatches. Both profiles remain admitted with requested
and resolved submission strategy 2, no downgrade, and no terminal fault.
The captures are `renderdoc/morph-morph-assimp-final_frame10361.rdc` and
`renderdoc/morph-morph-native-final_frame13873.rdc` under the disposable task run.
Static audits use `mcp-output/animation-morph-*-final-*`; pose samples use
`mcp-output/animation-bind-assimp-*` and `animation-bind-native-*`.
All owned editor and RenderDoc sessions were closed.

Final logs are in `logs/morph-final/`. Both runs have zero Vulkan VUID messages
and one known startup output-reservation error. Texture publication remains
unresolved: Assimp records 42 source-mismatch stage warnings from 18:44:32 to
18:44:59; native records 28 from 18:48:37 to 18:48:54. Final captures contain
native shading work, but do not clear these transient failures. No source-bind fallback
diagnostic appears for this prefab. The bright face, strong highlights, and
surface patches remain image-parity issues; these importer fixes do not resolve
the shader or texture publication work.

The existing native FBX, serialized scene importer, and backend resolver selection
has 23 passes and one failure. `Resolve_AutoGltf_ProducesStableNativeThenAssimpSnapshot`
retains the version-1 Assimp candidate hash. The required Assimp version-2 cache
invalidation changes that hash; the candidate-order and repeat-resolution checks
pass before the stale literal assertion. No test source changed. The open test
update is in the import todo. Results are in `reports/prefab-bind-regressions.trx`.

## Surface Flicker And Source Shading Semantics, 2026-10-11

The user reported possible self z-fighting, but could not identify a specific
mesh. The new checks use the previous final binaries and the exact prefab.
They change only isolated runtime controls. No engine code, tests, source
materials, or prefab data changed during this investigation.

The first native run stopped at mesh publication with Windows access violation
`0xc0000005`. The faulting module is unknown. This does not prove the earlier
shadow-buffer lifetime hypothesis. A restart with directional shadows disabled
remains live. Keep this startup failure separate from the surface artifact.

### Moving Clothing Patches

Close native front views show moving white and pink patches on the tank and
shorts with a fixed camera and static pose. The patches remain with shadows
disabled. They also remain when the body is visibly absent, when tank detail
normals are disabled, and when the tank matcap is disabled. Glitter is disabled
in the source material; changing its inactive brightness does not remove them.

Setting only the tank `_DissolveEdgeWidth` from 0.334 to 0 removes its moving
patches. The shorts still show patches until their dissolve edge widths are
also set to zero. Front and oblique control captures were viewed. These are
diagnostic overrides, not a source-preserving fix.

The explicit Assimp route reproduces the same result. Disabling face detail
color restores skin shading. Setting tank edge width to zero removes tank
patches while shorts patches remain. Setting shorts edge width to zero then
removes those patches. Front and oblique captures were viewed. Restoring all
three source values brings back the white face and clothing patches. This
reversal excludes texture readiness as the cause of those control results.
The small fixed spot on the tank and bright accessory pixels remain unresolved.

The active Tank and Shorts source settings use Basic dissolve, amount 0, edge
width 0.334, noise pan `(3, 3)`, and pink edge emission 1.5. Poiyomi remaps the
amount with `da = mix(-edgeWidth, 1, saturate(amount) * mask)`. Its noise lies
strictly between 0 and 1. At amount 0, `da = -edgeWidth`; both clipping and edge
contribution are zero. Noise can pan without producing a visible edge.

The current conversion has two faults. `SerializedMaterialImporter` converts
the mode, but `BindExactSurfaceParameters` overwrites it with source value 1.
`NativeUberCoverage` then uses a geometric-distance branch and centered noise,
without the source endpoint remap. The canonical Uber evaluator shares the
semantic mismatch. The importer also aliases source dissolve amount to an
engine cutoff. Do not fix this by forcing cutoff 0.5: the source material has
its own alpha blend and cutoff rules, which must remain separate.

RenderDoc confirms that the patches are already in native compute color before
post-processing. At tank pixel `(988, 706)`, the visibility history has one
draw with two fragments at depths 0.8890498 and 0.8707766. Those depths represent
separate surfaces, not an equal-depth contest. The dissolve isolation is the
decisive result. This does not rule out every possible overlap on the avatar.
Disposable evidence includes `surface-native-matcap-off_frame15877.rdc`,
`surface-patch-pixel.json`, and `surface-native-compute-color.png` in the task run.

### Bright Face And Strong Highlights

`MAT FACE 3` imports detail intensity 1, brightness 1, white tint, multiply blend,
and a white fallback for an absent detail color texture. Both Uber evaluators
calculate `texture * tint + brightness`; the resulting factor is 2. Disabling
only face `_DetailTexIntensity` restores visible skin shading in the live view.
Detail normals remain enabled. Source Poiyomi uses multiplicative brightness
and a neutral gray fallback with its color-space factor. Preserve authored
brightness and tint in the correction; do not permanently disable the layer.

Two more source differences remain in code. The tank matcap has smoothness
0.225 on a 512-pixel texture. Poiyomi selects an explicit mip near 6.975; the
converter omits smoothness and native Uber uses derivative sampling. Source
replace/multiply modifies base color before final lighting. The engine replaces
already-lit color. Also, source detail normals use the green mask channel and
tangent-space normal blending. The engine uses red and can replace the base
world-space normal. These differences need separate corrections and matched
lighting acceptance. The current controls do not establish full highlight parity.

### Texture Publication And Validation Limits

The previous 28 native source-mismatch stage warnings come from four texture
transition timestamps, not 28 distinct upload failures. The canonical resource
runtime explicitly rejects a package whose texture source changed after capture
and retries on a later publication. These transient rejections do not explain
the continuous clothing patches.

There is still an ordering defect to address. `TryCreateSynchronizedImportedUploadPreparation`
calls `ApplyResidentDataForVulkanPublication` before upload and descriptor commit.
It changes logical mip data, format, and source generation while the old native
image can remain published. Cancellation can retain that disagreement. A safe
change must derive pending layout from resident data and coordinate logical,
descriptor, and canonical publication. Moving one metadata write alone cannot
make an already-captured package current. Keep source validation enabled.

Both routes retain Advanced admission, requested and resolved submission
strategy 2, no downgrade, and no terminal fault during the diagnostic controls.
The first Assimp attempt remains black after import because the early indexed
group finalizer compute pipeline remains in regeneration. An identical-binary
restart admits Advanced and completes the controls. This extends the shader
readiness investigation to an intermittent Debug case; it is not limited to
Release. No Release frame-time result was collected. The broker
route tool again returned deprecated `gpt-5.6-luna` despite the GPT-6 request;
no paid run was started. Native read-only reviews supplied the source comparisons.

The owned editor and RenderDoc sessions are stopped. Copied logs are under
`logs/surface-investigation/` in the task run. Native session
`xrengine_2026-10-11_20-21-48_pid3904` records 14 source-mismatch stage warnings
at two transition times. The blocked Assimp session
`xrengine_2026-10-11_20-31-23_pid21480` records none. The successful Assimp session
`xrengine_2026-10-11_20-37-46_pid36052` records 49 such warnings. Each records
one startup output-reservation error and zero Vulkan VUID messages. These
results do not clear the texture publication or startup readiness defects.

Assimp control evidence uses the `animation-surface-assimp-restart-` prefix
under `mcp-output/`. The front and oblique images are
`Screenshot_20261011_204052_387_bd06f1fa995f42e981669ae3e568070b.png` and
`Screenshot_20261011_204058_189_0b5a69882408429185be836e0389fdda.png`.
The restored source state is
`Screenshot_20261011_204128_459_e43cc024c1e44278b2d4a5dfb353f754.png`.
These images are diagnostic evidence, not accepted parity output. No build
or test was rerun for these documentation-only changes. `git diff --check`
passes.

## Source Dissolve And Detail Color Corrections (2026-10-11)

The importer now binds the converted dissolve mode after exact-name properties.
Source Basic dissolve has a distinct engine mode. It no longer aliases amount
to alpha cutoff. The shared shader helper preserves the source endpoint remap,
noise bounds, detail smoothing, edge hardness, continuous amount, and source
pan time scale. Native and canonical shading apply dissolved alpha before the
material cutout rule. An opaque source material does not become a forced cutout.
The native material schema includes the new source controls.

Detail color now converts source multiplicative brightness, tint, and the
project color-space factor into the engine multiplier. An absent texture uses
the source neutral gray factor. Engine additive brightness becomes zero. The
source detail intensity remains enabled. The architecture document owns these
conversion rules. Other dissolve modes and the browser lowering proof remain
outside this validation.

The final isolated Debug editor build has zero warnings and errors. The first
build found a missing namespace import in the new detail-color partial; that
error was corrected before either runtime check. No test source changed and no
unit tests ran during this feature validation.

Both explicit routes imported `jax2031 (1).prefab` through the same corrected
binary. The FBX remained its model-prefab dependency. Each route ran Vulkan with
Advanced Required and GPU indirect submission. Captures used a static pose,
shadows off, black background, manual exposure 1, and bloom off. Front and
oblique PNGs were viewed after texture readiness. Both retain the bangs and
tank coverage. Tank and shorts no longer have moving dissolve patches at saved
amount 0 and edge width 0.334. Runtime mode is 5. A temporary tank amount of 0.5
produces a patterned transition on both routes. It was restored to 0.

The face retains detail intensity 1 with normalized tint approximately 1 and
additive brightness 0. Skin shading is visible without disabling the layer.
This clears the absent-texture detail multiplier fault for this prefab. It does
not accept every detail texture, Gamma project, or final lighting response.
Strong highlights, bright accessory pixels, and the small fixed tank spot remain.

### Hair Flicker Control

The active `NEW HAIR` and `1 Hair 2 BANGS` materials have source glitter enabled
at brightness 2.7. Dissolve and detail color are disabled on these materials.
Two captures used the saved glitter value. Two more used only a temporary
brightness of 0. All captures kept the camera and pose fixed. The source value
was restored before stopping each route.

The broad moving bands disappear with glitter disabled on both routes. In the
hair rectangle from `(865, 440)` to `(1055, 555)`, mean absolute RGB byte change
between frames is 10.404 for native and 25.731 for Assimp with glitter enabled.
With glitter disabled it is 0 for native and 0.000183 for Assimp. Neither
control pair has a pixel with a channel change greater than 2. This identifies
glitter as the source of the measured bands. It does not rule out every possible
mesh overlap or edge aliasing issue elsewhere.

The current native glitter evaluator changes a coarse UV hash with time.
It lacks source sampling and mask rules. Native shading also adds that result
to color and emission, then adds emission to color again. The open glitter
code item covers source sampling, mask transforms, time, and contribution
ownership. Disabling glitter is a diagnostic control, not the permanent fix.

### Runtime Evidence And Limits

Both routes retain Advanced admission, requested and resolved submission
strategy 2, no downgrade, and no terminal fault. The owned editor is stopped.
Logs copied to `logs/material-semantics/` under the task run contain zero Vulkan
VUID messages and one known startup output-reservation error per route.
Native session `xrengine_2026-10-11_20-52-20_pid64584` has 49 source-mismatch
stage warnings. Assimp session `xrengine_2026-10-11_20-59-17_pid64092` has 56.
Texture publication and startup readiness remain unresolved.

Disposable evidence uses `animation-semantics-native-` and
`animation-semantics-assimp-` under `mcp-output/`. The native settled front image
is `Screenshot_20261011_205655_755_c51fdaf09181454a8c1280a5dff93207.png`.
The Assimp front image is
`Screenshot_20261011_210439_335_664cdfcac14f45a7886446e4780d91bd.png`.
Hair difference measurements are in `reports/material-semantics-hair-difference.json`.
The settled GPU captures are `semantics-native-settled_frame15622.rdc` and
`semantics-assimp-settled_frame28440.rdc` under `renderdoc/`.

GPU replay initially timed out. Targeted inspection succeeded after the editor
stopped. Both captures contain four nonzero indirect shading dispatches with
group counts 723, 4, 895, and 91. Assimp material rows include 16 native Uber
rows with source contract 11, 2324 words, and 72 texture roles. Four standard
rows remain separate. Compute color was exported and viewed for both captures;
the corrected tank and face are present before post-processing. The native
material-row query timed out, so no new native row-buffer acceptance is claimed.
Both RenderDoc sessions are closed.

The broker route request again returned a deprecated GPT-5.6 model despite the
GPT-6 request. No paid run started. Native workers supplied bounded source
review and implementation work. These Debug checks do not establish a Release
frame-time result, matched Unity lighting, production shadows, or MSAA parity.

## Source Glitter Correction: 2026-10-12

The importer now preserves the active source glitter controls. Both `NEW HAIR`
and `1 Hair 2 BANGS` reach runtime with frequency 2000, two layers, circle shape,
angle mode, angle range 3.9 degrees, contrast 640, and brightness 2.7. Tint RGB
is 0.09433961, normal blend is 0.536, and surface-color blend is 0.824. The old
frequency conversion reduced 2000 to 10. Its animated coarse hash caused broad
moving bands.

Native and canonical Uber now share `Uber/source_glitter.glsl`. Random cell
locations remain fixed. Source time changes orientation. The evaluator keeps
source layer, rotation, normal, mask, color, and blend rules. Add blending writes
emission once. The native and canonical final-color paths no longer add the same
glitter again. Replace blending changes base color before lighting and keeps the
source excess-brightness emission. Unsupported active options fail explicitly.
The architecture document lists the supported subset and derivative limits.

Source review corrected integer decoding for native mode and layer values,
rotation order, source luminance weights, native ramp derivatives, the layer
bound, and Replace behavior at brightness zero. The canonical glitter path also
compiles when stylized shading is disabled. The final isolated editor build has
zero warnings and errors. `glslangValidator -S frag` passes for expanded canonical
source with all features enabled and with glitter enabled but stylized shading
disabled. These are GLSL syntax checks, not canonical Vulkan runtime acceptance.
No tests were added, changed, or run during this feature validation.

Both explicit backends imported the exact `.prefab`, with the FBX as its model
base dependency. Native has 35 active mesh rows; Assimp has 30. Both have 21
active materials. Runs use Vulkan, Advanced Required, GPU indirect submission,
static pose, shadows off, black background, manual exposure 1, and bloom off.
Close, front, and oblique PNGs were captured and viewed. Bangs and tank coverage
remain. Broad animated glitter bands are absent. Fine glitter remains active.

Three enabled captures and two disabled captures used the same fixed camera.
Each pair has a four-second wait between captures. Only the two hair materials'
brightness changed for the disabled control. Brightness 2.7 was restored before
the final front and oblique captures and session shutdown.

| Route | Enabled pair mean RGB byte change | Disabled pair mean | Enabled versus disabled mean |
|---|---|---|---|
| Native FBX | 0.338, 0.343 | 0 | 0.736 |
| Assimp | 0.330, 0.326 | 0 | 0.770 |

The region is `(865, 440)` to `(1055, 555)`, as in the earlier control. Earlier
faulty enabled pairs measured 10.404 for native and 25.731 for Assimp. The new
small nonzero changes and enabled/disabled differences confirm active animated
glitter. They do not prove exact Unity pixels or every possible mesh overlap.

Both final profiles remain admitted with requested and resolved submission
strategy 2, no downgrade, and no terminal fault. Both owned sessions are stopped.
Copied logs under `logs/glitter/` contain zero Vulkan VUID messages and one
startup output-reservation error per route. Native session
`xrengine_2026-10-12_01-13-44_pid28220` has 35 source-mismatch stage warnings.
Assimp session `xrengine_2026-10-12_01-18-17_pid25168` has 42. Assimp warnings
precede the fixed-camera capture series. Native also reports a later mip change,
so these captures do not establish continuous texture-publication stability.
Texture publication and startup readiness remain open. No new RenderDoc replay
was needed for this isolated glitter control.

Disposable evidence is under the existing avatar parity run. MCP prefixes are
`animation-glitter-native-` and `animation-glitter-assimp-`. Reports are
`reports/glitter-hair-difference.json` and `reports/glitter-log-summary.json`.
Final front images are
`Screenshot_20261012_011738_509_7c23498f9c234a6dba844d0e73cf57b5.png` and
`Screenshot_20261012_012244_988_b57d5644792f4f9da4c1de873294422c.png`.

The glitter shadow mask still uses the engine lighting ramp. Poiyomi light-map
construction differs, including direct-light and SH inputs and texture-ramp
coordinates. Keep matched-lighting acceptance open. Strong skin highlights,
bright accessory pixels, and the fixed tank spot remain. The shared two-layer
circle evaluator checks 54 candidate sites per shaded sample. No Release frame
budget, production shadows, AA, or VR performance result is claimed.

## Surface Normals, Matcaps, And Accessory Reconstruction: 2026-10-12

Source review confirms that detail normals use the green mask channel. The
source scales detail tangent XY by strength times mask, reconstructs Z, and
combines it with the base tangent normal before one world-space transform.
The face mask has different red and green values. The skin detail texture also
sets green inversion in its importer metadata. The engine stored this flag but
did not apply it in either Uber decoder. The current correction binds this
metadata to the two normal samplers without changing shared texture bytes.
Backface normal flags and complete source lighting remain separate checks.

Replay of the earlier settled Assimp capture identifies 1,297 magenta pixels
with diagnostic `0x00b10000`: invalid reconstruction, reason 11,
`DegenerateTriangle`. The exported native compute color was viewed. The GPU
reconstruction reused a pixel-area epsilon for the world-space triangle cross
product. Small accessory triangles could pass visibility and fail shading.
The correction scales each transformed local edge independently, then scales
their cross product before normalization. This removes dependence on world
size and avoids translation cancellation. Zero and non-finite edges or crosses
still fail. The pixel-space area guard remains separate. The CPU tangent-frame
reference uses the same geometric-normal method.

The first final-binary import stopped on the inherited `T-Shirt` material. It
enables matcap hue shift but stores shift 0, speed 0, and shift mode 1. The new
unsupported-feature guard rejected this neutral state. The guard now accepts
that exact neutral case. Nonzero shift, nonzero speed, and hue selection remain
explicit failures. This does not implement animated material hue. The empty
black viewport from that failed import is not accepted as shading evidence.

The corrected Debug editor build has zero warnings and errors. Supplemental
canonical GLSL checks pass with all features, detail only, and matcap only.
The detail-only check found UV declarations under the unrelated surface
extension guard. Those declarations now belong to the detail feature.
No test source changed and no unit tests ran in this validation slice.

The final native run imports 21 active materials and 35 mesh/LOD material rows.
Tank matcap slot 1 retains smoothness 0.225; its slot 0 is disabled. Body, face,
and lashes retain detail-normal green inversion. Front and oblique captures
were viewed after texture promotion settled. The GPU capture has a nonzero
native indirect shading dispatch at event 250, with groups 91 by 1 by 1.
The exported compute color was viewed. The full 1920 by 1080 diagnostics image
has no rejection pixels, including no reason-11 pixels. This clears the
accessory reconstruction failure for this native view.

Temporary tank smoothness 1 and detail strength 0 controls change the tank
output. In the central tank region, mean RGB byte differences from the restored
image are approximately 0.455 and 0.072. The initial image review incorrectly
reported missing opaque groups. A later pixel audit found identical RGB values
throughout the arm, abdomen, and leg regions in these control captures. See
the live material edit audit below. The front and oblique views contain the
complete avatar.

Native session `xrengine_2026-10-12_02-14-04_pid39700` has zero Vulkan VUID
messages, one startup reservation error, and 28 source-mismatch stage warnings.
The final profile stays admitted, uses submission strategy 2 without downgrade,
and has no terminal fault. Strong skin and clothing highlights and the fixed
tank spot remain. These views use static pose and disabled shadows. They do
not establish matched Unity lighting, Release frame time, production AA, or VR.

Native disposable evidence uses MCP prefixes `animation-surface-native-accepted-`
and `animation-surface-native-control-`. The GPU report is
`reports/surface-native-diagnostics.json`; its color export is
`renderdoc/surface-native-after.png` in the existing avatar parity run.

The same binary imports Assimp with 21 active materials and 30 active mesh/LOD
rows. The initial front and oblique captures show the complete avatar with the
same saved matcap and normal controls. Both images were viewed. Changing Tank
slot-1 smoothness to 1 preserves the complete avatar after 15 seconds. Setting
its detail-normal strength to 0 changes the tank. The initial image review
incorrectly reported missing opaque groups after this edit and restoration.
The later pixel audit found identical RGB values throughout the arm, abdomen,
and leg regions in all three captures. Profile admission remains true,
submission stays at strategy 2, and no terminal fault appears. These images
do not establish a mesh-loss defect or a texture-publication cause.

Assimp session `xrengine_2026-10-12_02-22-53_pid49672` has zero Vulkan VUID
messages, one startup reservation error, and 35 source-mismatch stage warnings.
The last source-mismatch warning precedes the control series. Initial-view
evidence uses `animation-surface-assimp-final-`; control evidence uses
`animation-surface-assimp-control-`. The owned editor session was stopped.
The saved-state Assimp GPU capture has a nonzero native indirect dispatch at
event 245, with groups 91 by 1 by 1. Its 1920 by 1080 diagnostic image has zero
rejection pixels. The exported compute color was viewed. Evidence is
`reports/surface-assimp-diagnostics.json` and `renderdoc/surface-assimp-after.png`.
Both replay sessions were closed.

### Live Material Edit Audit

The 2026-10-12 follow-up rechecked the original control images before selecting
a code change. Both import routes have zero RGB differences in the sampled
arm regions and the full abdomen-and-leg rectangle between the sharp-matcap,
zero-detail, and restored captures. The tank changes. Small differences also
occur around the face and hair. The earlier missing-group diagnosis was
incorrect; no material-publication or shading-group fix is justified by those
images.

A fresh Assimp import preserves 21 active materials. Seventeen retain native
Uber identity; four use their existing standard semantics. Detail strength
`2 -> 0 -> 2` preserves the complete avatar in front and oblique views after
15-second waits. Vulkan Advanced remains admitted with
`GpuIndirectZeroReadback`, no downgrade, and no terminal fault. RenderDoc
comparison finds exactly one changed constant: material row 9, relative word
148, changes from `0x40000000` to zero. The material header and all other
material constants remain identical. This verifies a scalar update in the
native material path.

The clean native FBX import gives the same result without a shader reload.
It retains 21 active materials and native Uber preflight on the tank throughout
the sequence. RenderDoc again finds only row 9, word 148, changed from 2 to 0.
All other constants and the material header remain identical. Front and oblique
images were viewed for both routes. Small edge and face differences remain
between frames; no mesh disappears. The clean comparisons use static pose,
disabled shadows and bloom, and manual exposure 1. They do not establish full
Unity lighting parity, motion acceptance, or production frame time.

The isolated editor build completed with zero warnings and errors. Assimp
session `xrengine_2026-10-12_19-37-30_pid23700` and native session
`xrengine_2026-10-12_19-42-30_pid58116` each have zero Vulkan VUID messages and
one startup output-reservation error. They have 35 and 28 source-mismatch
warnings, respectively. The last warnings precede the scalar control captures.
Both routes retain Advanced admission, GPU indirect submission, and no terminal
fault at each control point. Owned editor and replay sessions were stopped.

The first native session stalled with a linked compute program whose compute
stage was unavailable. A manual shader reload restored admission but cleared
all 21 materials' engine semantic identities. Native Uber rows were retired
and their constant storage was zeroed. The resulting images are not valid
native Uber parity evidence. An apparent later HDR loss was also ruled out:
the late draw changes only 924 hair pixels in the inspected frame; sampled
body values stay unchanged. Do not use this discarded run as proof of a
material scalar fault.

The reload failure has a separate confirmed source path.
`ShaderSourceDependencyIndex.PublishPreparedRootRefreshes` notifies unchanged
source dependencies. `XRShader` emits `SourceChanged`, and
`XRMaterial.AuthoredShaderSourceChanged` clears engine semantics. The imported
native Uber identity is not restored. Internal Uber variant adoption also
calls `SetShader`, which clears that identity; this second path needs its own
runtime reproduction. Keep actual authored shader edits distinct from a
backend refresh when implementing a correction. The linked code todo owns
this work. No rendering code or tests changed during this audit.

### Shader Reload Identity Correction

The reload fix separates `XRShader.AuthoredSourceChanged` from the general
`SourceChanged` compile notification. Materials listen to the authored event.
Manual refresh captures each shader's dependency proof before any shared
`TextFile` refresh. A current root, include graph, search-directory state,
snippet version, and source owner permit backend regeneration without clearing
material identity. An absent or stale proof still clears eligibility. The file
proof uses the resolver's timestamp and length contract.

Compile-only refreshes retain the proven cache and declared cooked module.
Repeated reloads therefore keep the proof, even before another source resolve.
Actual source invalidation clears derived cooked fields without nested property
callbacks, then publishes the authored event before the general event.
At this checkpoint, generated Uber variants were excluded because their expanded
text did not prove the canonical include graph. The later adoption correction
below adds that proof. A temporary suppression scope was rejected because it
could hide a reentrant authored edit and break adoption revision checks.

The isolated final build completed with zero warnings and errors. No test
source changed. Assimp session `xrengine_2026-10-12_20-10-02_pid31992` retained
17 native Uber and four standard identities through two reloads and tank
detail strength `2 -> 0 -> 2`. Front and oblique captures retained the meshes.
Advanced admission and GPU indirect submission remained active, without a
downgrade or terminal fault. An in-memory root comment edit then cleared the
tank identity to `None`; restoring the original text did not restore eligibility.
The source file was not saved. The log has zero VUID messages, one startup
reservation error, and 35 texture source-mismatch warnings. The last warning
preceded the first control capture.

Native session `xrengine_2026-10-12_20-14-54_pid64080` used the same binaries
and passed the same reload and scalar sequence with all 21 identities intact.
Front and oblique images retained the meshes. The explicit dependency-change
notification then cleared tank identity to `None`. This negative control checks
the event path; it does not exercise a disk file watcher. The native log has
zero VUID messages, one startup reservation error, and 28 texture source-mismatch
warnings. Its last warning also preceded the first control capture. Both final
routes kept Advanced admission, GPU indirect submission, and no terminal fault
through the positive controls. All owned editor sessions were stopped.

Evidence is under `Build/_AgentValidation/20261008-090000-jax-avatar-parity/`:
`mcp-output/animation-reload-assimp-final-*`,
`mcp-output/animation-reload-native-final-*`, and
`reports/reload-pixel-audit.json`. The earlier `reload-native` series used an
intermediate build and captured ongoing texture promotion; use the final series
for image comparisons. These checks use a static pose, manual exposure, disabled
shadows and bloom, and a black background. They do not establish full Unity
lighting parity, animation acceptance, or a frame-time budget.

### Internal Generated-Variant Adoption

The failure was in material identity ownership. Internal generated fragment
replacement used the public authored replacement path. That path correctly
clears identity for user edits, but it also cleared valid native Uber identity
during engine adoption.

The builder now stamps canonical and generated source inputs, authored revisions,
resolved dependencies, source owner, and snippet version. Internal replacement
uses a one-use transition proof for the exact lists, source owner, identity, and
material revisions. The final check runs after event handlers attach. Adoption
must advance the shader revision once. The material also retains a canonical
source listener while a generated fragment is active. No callback restores an
old identity. Public replacement remains an authored edit.

Generated shader caches use weak canonical ownership. This avoids retaining every
canonical instance after repeated imports. Cache hits and snapshot recovery reject
stale provenance. The code review covered callback edits and fallback source paths.
Reentrant callback injection and snapshot round trips were not exercised live.

The final isolated build has zero warnings and errors. No test source changed.
Native session `xrengine_2026-10-12_20-40-50_pid23536` adopted a generated Tank
fragment with shader revision `4 -> 5` and native preflight still true. Two manual
reloads and detail strength `2 -> 0 -> 2` retained all 17 native Uber and four
standard identities. Viewed front and oblique images retained the visible meshes.
Strong highlights and the fixed tank spot remain.

The first profile read caught an empty frame package during publication. All four
later positive-control reads reported requested and resolved GPU indirect strategy
2, no downgrade, Advanced admission, and no terminal fault. In-memory generated
Tank source and canonical Bracelets source edits each cleared eligibility.
Restoring text did not restore identity. No source file was saved. The canonical
source is shared, so its negative control also invalidated dependent materials.
The native log has zero Vulkan VUID messages, one startup reservation error, 21
texture source-mismatch warnings, and 14 publication-change warnings. The last
source mismatch preceded the first image. Publication-change warnings also
occurred during shader refresh. These existing publication limits remain open.

Assimp session `xrengine_2026-10-12_20-45-28_pid51336` used the same final
binaries. Generated Tank adoption also advanced revision `4 -> 5`. The first
capture was black with `ShadeBackground.comp` pending regeneration. The first
manual reload restored Advanced admission. Both reloads and detail strength
`2 -> 0 -> 2` retained all 21 identities. All four later profile reads reported
strategy 2, no downgrade, and no terminal fault. Viewed front and oblique captures
retained the meshes after readiness recovered. The generated fragment remained
active after both reloads and scalar edits.

An explicit internal return to the canonical Tank fragment advanced revision
`5 -> 6` and kept native preflight true. Re-adoption then succeeded. Generated
Tank and canonical Bracelets source edits cleared eligibility; restoring the
original text did not restore it. The Assimp log has zero Vulkan VUID messages,
zero texture source-mismatch warnings, one startup reservation error, and seven
publication-change warnings during refresh. This run does not close the separate
shader-readiness defect. All owned editor sessions were stopped.

Evidence uses `mcp-output/animation-variant-native-final-*` and
`mcp-output/animation-variant-assimp-final-*` under
`Build/_AgentValidation/20261008-090000-jax-avatar-parity/`. Earlier
`variant-native-initial` evidence uses an intermediate build and is not final
acceptance evidence. The checks use the exact prefab, explicit backend selection,
static pose, manual exposure, disabled shadows and bloom, and a black background.
They do not establish matched Unity lighting, animation coverage, or frame time.

### Vulkan Shader Readiness Race

A diagnostic-only Assimp run reproduced the persistent wait on `Gtao.comp`.
Session `xrengine_2026-10-12_21-29-37_pid55100` reported a linked program at link
generation 1 with a nonzero pipeline layout, zero stage entries, and one shader.
That shader had a nonzero native module and entry point, `IsCompiled=true`,
`IsCompilePending=false`, and `StageFlags=None`. Both viewed camera captures
were black. The run was stopped after diagnosis without a manual reload.

This state identifies an adoption race, not a CPU shader compilation failure.
The asynchronous adopter released the dependency gate after destroying the old
module. `ApplyCompiledArtifact` then wrote stage flags before native creation.
Frame-swap invalidation could clear those flags between the two writes. Adoption
continued and published a ready module with zero stage flags. `BuildStageLookup`
skipped it, and the program published a linked interface with no stages. Later
readiness polls accepted that interface and could not repair it. The initial
clip-remap setup can queue an invalidation at the same source revision, so a
source-revision check alone cannot prevent this interleaving.

The fix holds the dependency gate across destruction and all module publication.
It selects the current module and drain scope after entering the gate. Both
synchronous and asynchronous publication use that boundary. The asynchronous
path checks task and input ownership under the gate before it publishes success
or failure. Deferred dependency work stays retryable. The link path also rejects
a missing native stage instead of publishing an empty stage table. Temporary
probe logging was removed after the cause was confirmed. No test source changed.

The broader nested warm shared-shader drain scope is outside this cold-publication
fix. This change preserves that existing queue contract. It does not claim a
review of all shared-shader replacement paths.

Probe evidence is under `Build/_AgentValidation/20261008-090000-jax-avatar-parity/`
with the `mcp-output/animation-readiness-assimp-probe-*` prefix. The exact log is
under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-212828-jax-shader-readiness-probe/logs/`.

The final isolated Debug build has zero warnings and errors. In session
`xrengine_2026-10-12_21-38-56_pid21420`, Assimp completed Advanced preparation in
5,783 ms without a manual shader reload. All 21 material identities remained
stable through tank detail strength `2 -> 0 -> 2`. Front and oblique captures
were viewed at each value. The avatar retained its visible meshes, bangs, and
tank coverage. Strong highlights and the fixed tank spot remain.

Two scalar-check diagnostics sampled an empty frame package while profile
admission remained true. Three later reads each reported a published package,
requested and resolved submission strategy 2, and no downgrade. No terminal
fault occurred. The stopped Assimp log has zero Vulkan VUID messages, zero
shader regeneration messages, one startup output-reservation error, and 42
source-mismatch stage warnings during texture promotion. The last mismatch
was at 21:41:17, before the viewed scalar-edit captures.

Final-build evidence uses the `mcp-output/animation-readiness-assimp-complete-*`
prefix under the task evidence root. Logs and the final build log are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-213744-jax-shader-readiness-complete/`.

The same binary then ran the exact prefab with `NativeOnly` in session
`xrengine_2026-10-12_21-44-26_pid67008`. The import log confirms
`ActualBackend=xrengine.native-fbx`. Advanced preparation completed in 6,052 ms
without a manual reload. All 21 material identities remained stable through
the same scalar sequence. All three sampled packages used requested and
resolved strategy 2 with no downgrade or terminal fault. Front and oblique
captures were viewed at each value and retained the visible meshes. The native
log has zero VUID, shader regeneration, or invalid-stage messages. It has one
startup reservation error and 35 source-mismatch stage warnings, last seen at
21:46:52. Evidence uses the `mcp-output/animation-readiness-native-complete-*`
prefix. The runs use a static pose, no shadows or bloom, and manual exposure 1.
Release, matched Unity lighting, and frame-time acceptance remain open.

A second Assimp startup with the same binary, session
`xrengine_2026-10-12_21-47-58_pid40288`, completed preparation in 5,712 ms without
a reload. It retained 21 material identities, admitted strategy 2 without a
downgrade, and had no terminal fault. Viewed front and oblique captures retained
the avatar. The log has zero VUID, shader regeneration, and invalid-stage
messages, one startup reservation error, and 42 texture source-mismatch stage
warnings. Evidence uses `mcp-output/animation-readiness-assimp-repeat-*`.
All editor sessions started for this investigation are stopped. No test source
was added or changed. These three final-binary startups validate the focused
Debug fix; they do not establish a long-running stability result.

### Texture Publication Review

`TryCreateSynchronizedImportedUploadPreparation` previously called
`ApplyResidentDataForVulkanPublication` before allocation and upload complete.
The call changed dimensions, mip arrays, format, mip range, and canonical
content generation. The wrapper lock and callback suppression do not protect
canonical metadata readers. Cancellation could also leave these changes behind.

The implementation now prepares a resident-data and sampler plan without changing
the live texture. It resolves the format through the existing color-space policy,
not the request's placeholder format. Sampler limits come from the pending mip
chain. Publication rechecks the source generation, prior dimensions and mip array,
and captured authored settings. Relevant texture setters and in-place resize use
the metadata writer lock. This prevents an authored edit from passing between the
check and the commit.

The completed descriptor, wrapper, and logical metadata now commit under the
streaming publication authority. Lock order is texture metadata, wrapper image,
then descriptor. The metadata epoch stays odd during the coupled commit.
Descriptor failure leaves logical metadata and content generation unchanged.
After descriptor success, notification-free `SetField` transfers update the mip
array, format, resident tracking, mip limits, and cleared sparse state. Canonical
content generation advances once. Published handles detach before retirement
and callbacks. The commit does not replay a generic mipmap-change callback that
can destroy the new image.

Review found that the existing preview cancellation callback records a terminal
generation failure. It cannot handle a changed sampler during preparation.
The upload service now retires that stale candidate and marks its ticket canceled.
It keeps the resident data and creates a fresh ticket under the same logical
generation and original scheduling context. The original job owns the terminal
callbacks across retries. Both completed-copy routes use this path. Actual
cancellation, retirement, or scheduling failure settles the owner once.

Keep strict canonical source validation and generation checks. A package made
before a successful publication is stale and must still retry. This transaction
fix removes premature exposure; it does not remove every source-mismatch retry.
The final isolated Debug build has zero warnings and errors. Live validation of
the final retry revision is recorded below. No test source changed.

Assimp session `xrengine_2026-10-12_22-13-59_pid71268` used the retry binary before
the final admission-failure repair.
The exact prefab completed with Advanced admitted and GPU indirect strategy 2
requested and resolved, with no downgrade. A pending promotion sample retained
64 by 64 logical metadata while the desired upload was 4096 by 4096.
`Accessories_Base_color 1` later published 4096 by 4096, 13 mips, and
`Srgb8Alpha8`. Its resident, published, and upload generations were all 2.
The controlled LodBias edit and restoration succeeded, but did not trigger a
stale-candidate retry in this run.

The settled Assimp session had 124 tracked textures, 126 final publications,
zero pending transitions or uploads, zero upload failures, and zero texture
validation failures. These are session totals. All 21 material identities stayed
stable through tank detail strength `2 -> 0 -> 2`. Front and oblique images were
viewed at each value. Visible meshes, bangs, and tank coverage remained. The
strong highlights and fixed tank spot remained too. The stopped log has zero
VUID and shader regeneration messages. It has one existing startup reservation
error and 35 source-mismatch stage warnings. The last source mismatch at 22:16:34
preceded the first settled capture at 22:16:47. Capture scheduling warnings are
separate from texture publication.

This build's logs and `build.log` are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-221243-jax-texture-final/`.
Assimp evidence uses the `mcp-output/animation-texture-assimp-final-*` prefix under
`Build/_AgentValidation/20261008-090000-jax-avatar-parity/`. Earlier
`texture-assimp-probe` evidence predates the service-local retry revision and is
not final acceptance evidence.

The same binary ran native session `xrengine_2026-10-12_22-17-56_pid43244`.
The import log confirms `ActualBackend=xrengine.native-fbx`. A short series of
LodBias edits made three Blackmatcap generation-2 upload candidates stale. Each
candidate retired, and a new ticket reused the logical generation. The texture
then published successfully. Streaming settled with 126 publications, zero
pending work, zero upload failures, and zero texture validation failures. Viewed
restored front and oblique captures retained the avatar. Evidence uses
`mcp-output/animation-texture-native-final-*`.

Review found a separate failure case in this revision. Canceling the old ticket
before replacement admission could hide a terminal error if admission failed
before a new ticket entered the ledger. The final repair keeps the old ticket
pending until that outcome is known. Rejected admission changes it directly to
`Failed` and reports `RendererTerminal`. Actual cancellation remains retryable.
The reviewed revision has no canceled-to-failed transition. Both completed-copy
routes preserve the failure result. This review found no remaining blocker.

The final rebuild, including rejected-admission handling, has zero warnings and
errors. Its logs and build output are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-222234-jax-texture-complete/`.
Assimp session `xrengine_2026-10-12_22-23-52_pid65452` completed the exact prefab
again. All three scalar checks and final profile reads retained Advanced
admission and strategy 2 without a downgrade or terminal fault. All 21 materials
retained their identities. All six front and oblique captures were viewed and
retained the meshes. The same highlights and fixed tank spot remain.

Final Assimp streaming has zero pending work, upload failures, or texture
validation failures. It has 126 final publications. Of 124 tracked textures,
57 have resident/published/upload generation 2, 12 have generation 1, and 55
have no requested generation. The sampler edit was restored. The log has zero
VUID messages and 49 source-mismatch stage warnings. The last mismatch at
22:26:29 preceded the first capture at 22:26:43. Evidence uses
`mcp-output/animation-texture-assimp-complete-*` under the task evidence root.

Native session `xrengine_2026-10-12_22-27-43_pid34708` used the same final binary.
The explicit native prefab route completed without fallback. Sampler edits made
four `ACC Metalic` generation-2 candidates stale. Publication tokens 62, 70, 82,
and 88 were canceled; token 100 published generation 2 at 22:30:04. The final
binding has 4096 by 4096 pixels, 13 mips, `Rgba8`, and restored LodBias 0.
The session settled with 126 publications, zero pending work, zero upload
failures, and zero texture validation failures. Its generation distribution
matches Assimp. All 21 identities remained stable through tank detail strength
`2 -> 0 -> 2`. All six front and oblique captures were viewed. The visible meshes
remained, with the same strong highlights and fixed tank spot.

The final native log has zero VUID messages and 35 source-mismatch stage warnings.
The last mismatch at 22:30:27 preceded the first capture at 22:30:41. Each final
route retains one existing startup reservation error. Neither has a terminal
fault or submission downgrade in the captured checks. Native evidence uses
`mcp-output/animation-texture-native-complete-*`. The checks use Debug, a static
pose, no shadows or bloom, and manual exposure 1. They validate publication and
live edits, not matched Unity lighting or frame time. Rejected replacement
admission and the recorded-transfer completion path were code-reviewed; these
runs exercised successful retries through the graphics upload batch. They do
not provide live fault-injection coverage for every failure path. No test source
changed. All editor sessions started for this texture check are stopped.

## Source Highlights And Fixed Tank Spot

The native baseline used the same final texture-publication binary. It passed
the tank detail-normal `2 -> 0 -> 2` control with all material identities and
meshes retained. The settled front view still had hard white shoulder circles,
wide skin highlights, and a small fixed patch below the left side of the tank.

RenderDoc identified a separate surface at the fixed patch. Pixel `(893, 665)`
had visibility draw 3 and primitive 16207. Nearby tank pixels had draw 14.
The draw resolves to material row 3; the tank resolves to row 9. Live Advanced
picking confirmed draw 3, primitive 16207, and scene node `Acc - Top Harness`.
Nearby tank picking confirmed `CLO Tank`, draw 14, and material row 9.
The picking API uses bottom-origin Y; image pixel Y 665 maps to its Y 414.
The native shading diagnostic target contained `0xff00` at every
pixel and no reconstruction or coverage rejection flags. This rules out a tank
texture mark and a rejected tank triangle as the cause of this patch.

The source harness uses `Goth Bunny-Straps` and `Metal.009 1`. The patch uses
the first material slot. The second material uses
`Standard_Double-sided.shader`, has no texture maps, and has smoothness
`0.8346456`. Its source specular highlights and reflections are enabled.
The generation-2 source path `//RootNode/root/Acc - Top Harness` resolves to:

- GameObject file ID `-8998589298071549428`.
- Transform file ID `-8980584500519708193`.
- SkinnedMeshRenderer file ID `4398915724156461118`.

The prefab explicitly sets this GameObject active and its first blendshape
weight to 0%. It has no renderer-enabled or transform override for these IDs.
The imported transform and weight match the saved source state. The user asked
to keep that state after this finding. The harness remains enabled; no source
asset or geometry was changed. A matched Unity view is still needed to decide
whether its visible intersection is also present in the source preview.

The locked Body and Tank shaders use Mochie BRDF shading. Their saved inactive
stylized-specular fields selected a hard toon highlight in Uber. That path also
used generic smoothness 1 instead of sampled Mochie smoothness. The source Body
rim uses inversion, power, brightness, and base-color mixing before lighting.
Uber instead added an ordinary white silhouette rim after lighting. These are
separate conversion errors from the harness patch.

The source correction shares the BRDF and rim math between canonical and native
Uber. It retains the source smoothness, GSAA, direct-light color limits,
second-lobe maximum, and pre-light rim blend. An independent source review
checked the active Body, Face, and Tank values against their locked shaders.

The first fresh run exposed an import regression in the new option checks.
The inactive alternate material `M_TransTop.002 1` enables `_GGXAnisotropics`.
Rejecting that control produced a generic material, and strict prefab validation
correctly rejected the import. The fix separates source-adapter enable from
packed-mask enable. Unsupported effects keep their existing native Uber
approximation and emit `POI0010`. The strict prefab validator remains unchanged.
All 21 active materials pass the supported-option scan. The source review still
identifies SH light selection, additive-light limits, and Face clearcoat as
separate parity gaps.

The native check used `jax-shading-fixed`, with logs in
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-230522-jax-shading-fixed/`.
Its editor build had zero warnings and errors. The actual model backend was
`xrengine.native-fbx`. Front and oblique images at tank detail strength
`2 -> 0 -> 2` retained the meshes and all 21 material identities. The hard white
tank circles and wide skin highlight bands were absent. Advanced remained
admitted with GPU submission strategy 2 and no downgrade or terminal fault.
Texture uploads settled without pending work or validation failures.

That session had zero Vulkan VUID messages and 28 source-mismatch stage warnings.
The last mismatch at 23:09:55 preceded the first capture at 23:10:25. Startup
retained one output reservation error and recovered. Seven shader reflection
warnings reported missing binding indices. No shader compile, regeneration, or
invalid-stage failure occurred. The later RenderDoc capture
`shading-native-final_frame11767.rdc` contained late and post-process work but no
native compute dispatch. It does not establish final native GPU execution.
The log shows a second GPU mesh-preparation pass from 23:11:43 through 23:11:58.
It progressed across meshes but deferred the native stages. Late passes can
retain the previous opaque color while this work is pending. No direct cause
from MCP picking was found. Profile admission alone cannot validate such a
frame. The repeat must require completed preparation and a nonzero native
dispatch. The earlier baseline capture remains valid only for patch ownership
and its own reconstruction result.

The final editor rebuild had zero warnings and errors. Assimp session
`xrengine_2026-10-12_23-17-25_pid55444` used the final binary in
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261012-231609-jax-shading-final/`.
The actual model backend was `assimp@2`. All six front and oblique scalar-check
images were viewed. The highlight correction, visible meshes, 21 material
identities, and saved harness state remained stable. Advanced used GPU strategy
2 without a downgrade or terminal fault. Texture work settled without failure.
The log had zero VUID messages, 35 source-mismatch stage warnings, and 14 GPU
mesh-preparation deferrals. The last mismatch at 23:19:41 and last preparation
deferral at 23:19:11 preceded the first capture at 23:20:21. One startup output
reservation error recovered. No shader compile or upload failure occurred.

Assimp capture `shading-assimp-gpu_frame14346.rdc` has a nonzero indirect native
shading dispatch at event 244 with groups `(91, 1, 1)`. All 2,073,600 diagnostic
pixels are `0xff00`, with no reconstruction or coverage rejection flags. The
exported native color was viewed and retains the corrected shading. The replay
script exceeded the CLI's 30-second response timeout on its first attempt;
the repeat completed with a 180-second request limit. This was a replay-tool
timeout, not an editor or Vulkan failure.

Native session `xrengine_2026-10-12_23-22-31_pid77844` used that same final binary
with the explicit native route. All six scalar-check images were viewed and
retained the corrected highlights, meshes, and 21 material identities. Streaming
settled without failure. Immediately before capture, preparation had no deferral,
34 draws, published GPU resources, and an executed aggregate dispatch. The scene
database epoch was 2 and topology generation was 34. Capture
`shading-native-gpu_frame14710.rdc` contains a nonzero indirect native shading
dispatch at event 250 with groups `(91, 1, 1)`. All 2,073,600 diagnostic pixels
are `0xff00`, without reconstruction or coverage rejection flags. The native
color export was viewed. Both final captures validate native shading execution.

The final native log records backend `xrengine.native-fbx@6`, zero VUID messages,
and zero shader or texture-upload failures. Its 28 mesh-preparation deferrals
end at 23:24:31. Its 28 source-mismatch stage warnings end at 23:24:56. Neither
recurs after the first screenshot at 23:25:23 or the final GPU capture. One
startup output reservation error recovers. Seven unresolved FBX texture
references remain import diagnostics; the composed prefab's active material
bindings use the resolved Unity materials.

These checks use Debug, static pose, Vulkan validation, GPU indirect submission,
FXAA, no shadows or bloom, and exposure 1. They do not establish matched Unity
lighting, Release frame time, production AA, or VR performance. No test source
changed. All editor and RenderDoc sessions started for this check are stopped.

## Source Light Selection, Face Clearcoat, And Backface Normals: 2026-10-13

The active source inventory has 17 Poiyomi materials and four Standard materials.
Body and the three face materials select source light color mode 2. The other
Poiyomi materials select mode 0. All 17 select source light direction mode 0 and
disable additional directional lights. Lashes disables backface normal flipping;
the other 16 Poiyomi materials enable it. The saved harness state stays enabled.

The engine supplies constant world ambient and octahedral probe textures. It does
not supply Unity SH coefficients. Constant ambient is sufficient to evaluate the
source mode 0 and mode 2 formulas for the current fixture. It does not reproduce
directional SH light selection under a matched Unity probe environment. Unity
vertex-light assignment is also a separate source contract.

Face enables a separate clearcoat layer. Its packed linear texture uses R for
the coat mask, G for smoothness, B for the reflection mask, and A for the direct
specular mask. The saved normal selection uses the vertex normal. The direct
strength is 0.907, reflection strength is 0.245, and smoothness is 0.882. The source
reflection calculation requires raw prefiltered radiance before its own Fresnel
and surface-reduction terms. Reusing engine BRDF-weighted reflection would apply
those terms twice.

The shared shader now covers source light color modes 0 and 2 and source diffuse
modes 0, 2, and 5. Wrapped and flat shading retain their own composition instead
of generic post-color adjustments. Source diffuse replacement preserves the
existing generic probe specular term. Clearcoat uses its own raw probe sample.
The source primary path remains active at zero light intensity. A zero-intensity
primary uses the source fallback direction and does not request a shadow sample.

Review found and corrected three shader issues before acceptance: incomplete
zero-primary ownership, clearcoat probe mip selection before GSAA filtering, and
loss of generic probe specular when replacing diffuse ambient. It also added
explicit handling for the active wrapped and flat materials. The importer states
that directional SH, Unity vertex-light selection, local diffuse/attenuation,
and Mochie IBL remain approximate.

The first C# build found missing namespace imports in the two new importer
partials. The next runtime found absent clearcoat UV and pan words in the native
material schema. Both were corrected. The supported coat adapter fixes those
coordinates to UV0 with no pan. The final isolated C# build has zero warnings and
zero errors. Canonical GLSL checks pass for all features, detail-only, and
matcap-only variants. No test source changed.

The final Assimp run imports the `.prefab` with actual backend `assimp@2`.
It retains 21 active materials, including 17 source lighting adapters. Face uses
coat values `(1, 0.882, 0.245, 0.907)` and a 1024 by 1024 linear RGBA wetness mask
with 11 mips. Lashes keeps `_FlipBackfaceNormals=0`; the other source materials
keep 1. Tank detail strength `2 -> 0 -> 2` retains the avatar and GPU submission.

The focused Assimp controls pass:

- Disabling coat changes 141 pixels in the checked lower-face region. Restoring
  it returns that region exactly.
- Disabling the hair backface flip changes its shading. The checked arm region
  remains pixel-identical, including alpha. Small hair differences after restore
  remain consistent with the active animated glitter.
- Under primary color `(1, 0.1, 0.02)` and intensity 0.4, Body light color modes
  0 and 2 differ. Restoring mode 2 returns the checked arm region exactly.
- With primary intensity 0, unlit intensity `1 -> 2` changes Body shading.
  Restoring 1 returns the checked arm region exactly.
- A second directional light leaves the checked Body and Tank regions unchanged
  under the saved suppression flag. Disabling Tank suppression changes its
  shading. Restoring suppression returns its checked region exactly.

Each control keeps all 21 identities and admitted GPU submission strategy 2,
with no downgrade or terminal fault. The validation scripts now restrict material
identity checks to the prefab subtree. Editor gizmo materials can enter the world
inventory and are not prefab materials. Temporary values and the added light are
restored or removed before final capture.

Assimp capture `renderdoc/light-assimp-gpu_frame24688.rdc` under the existing
`Build/_AgentValidation/20261008-090000-jax-avatar-parity/` evidence run executes
native indirect shading at event 244 with groups `(91, 1, 1)`. All 2,073,600
pixels have diagnostic value `0xff00`; no reconstruction or coverage rejection
occurs. The native color export was viewed.

Final Assimp PID 77396 has no VUID, shader compile, terminal, or texture upload
failure. One startup reservation error recovers. Fourteen mesh-preparation
deferrals end at 00:16:09; 35 source-mismatch warnings end at 00:16:26, before
settled images at 00:17:51 and the GPU capture at 00:25:20. One MCP query exceeds
90 seconds during startup. The same process later responds and completes the
checks. The logs do not identify the timeout cause. Seven unresolved source FBX
texture references remain separate import diagnostics; active composed material
bindings use Unity textures.

The final native FBX run has the same source control values for all 21 materials.
All focused controls pass with strategy 2, no downgrade, stable identities, and
no terminal fault. Disabling coat changes 143 lower-face pixels; restoring it
returns that region exactly. Hair flip changes shading without changing the
checked arm pixels. Body color-mode, zero-primary unlit intensity, and additional
directional suppression controls give the same outcomes as Assimp. Their checked
regions return exactly after restore. The checked arm and leg pixels do not
change during the Tank-only additional-light control. Active hair glitter gives
a small time-dependent difference after restoring the backface flag.

Native capture `renderdoc/light-native-gpu_frame22496.rdc` in the same evidence
run executes native shading at event 250 with groups `(91, 1, 1)`. All 2,073,600
pixels have value `0xff00`, with no reconstruction or coverage rejection. Its
native color export and final front and oblique images were viewed. The final
publication has 34 draws, database epoch 2, topology generation 34, and no CPU
readback requirement. Assimp has 29 draws and topology generation 31.

Final native PID 33560 uses `xrengine.native-fbx@6`. It has no VUID, shader
compile, terminal, or upload failure. One startup reservation failure recovers.
Twenty-eight mesh-preparation warnings end at 00:31:21. Thirty-five source
mismatch warnings end at 00:35:21; seven occur after the first settled screenshot,
but none occur after the final capture. Final texture state has zero pending,
uploading, or failed entries. Seven unresolved FBX texture references remain
separate import diagnostics. All owned editor and RenderDoc sessions are stopped.

Both runs use Debug, static pose, mono
1920 by 1080, Vulkan validation, FXAA, exposure 1, and no shadows or probes.
These checks do not accept positive probe reflection, matched Unity lighting,
Release frame time, production AA, or VR performance.

## Directional SH, Local Lights, And Probe Specular — 2026-10-13

World settings now supply seven signed SH vectors. Disabled SH uses constant
ambient; enabled zero coefficients stay black. The Advanced environment record
is 240 bytes and preserves the old 128-byte prefix. Capture copies the values
at world swap. Canonical and native Uber share source SH, local-light, and
Mochie probe equations. Local lights preserve enable, limit, gradient, color,
and shadow controls with engine attenuation and per-pixel assignment. Mochie
reflection uses raw prefiltered radiance, source Fresnel and exposure controls,
and imported tint. It replaces the prior probe term after overlays.

The isolated lighting build has zero warnings and errors. Canonical all-feature,
detail-only, and matcap-only GLSL checks pass. No test source changed. The existing
`AdvancedGlobalResourceContractTests.GlobalRecords_HaveStableStd430CompatiblePacking`
still expects a 128-byte environment record. The code todo records that stale
assertion and the required test-edit clearance.

Both exact prefab routes retain 21 active materials through Tank detail-normal
strength `2 -> 0 -> 2`. Authored positive and negative SH X terms, L2, enabled
black coefficients, local-light enable, Lambert, gradient, and monochromatic
controls produce distinct output. Front and oblique images were viewed.
GPU indirect submission stays admitted. A transient Assimp profile sample has
an empty frame package; the final GPU capture confirms native execution.

Assimp capture `renderdoc/sh-assimp-probe-gpu_frame26882.rdc` in the existing
evidence run executes native shading at event 246 with groups `(91, 1, 1)`.
Native capture `renderdoc/sh-native-gpu_frame43496.rdc` executes at event 250
with the same groups. Each has 2,073,600 diagnostic values of `0xff00`, with
no reconstruction or coverage rejection. Both native color exports were viewed.
The native capture has no accepted nonzero probe at this point.

The first Assimp probe control selects a valid 176-byte probe row with weight 1.
Its irradiance and prefilter handles resolve to nonzero RGBA16F textures with
32-square and 256-square extents and 6 and 9 mip levels. The exported studio
radiance was viewed. Face reflection-only coat changes 375 checked face pixels;
restoring it returns the checked region exactly. Tank reflection strength also
changes output, but that control changes diffuse energy. A separate tint control
is required to isolate the probe specular term.

The first SH/local script used incorrect transform argument names. That run is
not acceptance evidence. The corrected script uses `translation_x/y/z`.
Assimp SH restore does not match its initial image after repeated temporary-light
runs; that comparison remains inconclusive. Temporary lights now receive zero
intensity and deactivate before removal. An apparent missing-mesh preview was
also ruled out: the raw PNG has opaque alpha, the expected body and hair pixels,
and unchanged GPU draw and variant counters.

Probe validation found two separate producer issues. Initial `InitializeStatic`
can run through generic render-thread jobs without `AbstractRenderer.Current`.
This causes the explicit Vulkan resource-scope guard to fail before retry setup.
The correction defers static resource creation and pending-output resolution
until an active renderer accepts work. It uses the existing renderer-owner check
and keeps the explicit resource-scope guard. Configure an inactive probe, then
activate it before assigning the static environment texture. Inactive requests
can be discarded when automatic capture is disabled.

A separate topology lifetime defect remains. `ForwardLightProbeInstanceResources`
and `AdvancedRenderPipeline.ConsumePendingProbeTopology` stage imported-buffer
replacement, then dispose the old buffers before published bindings release them.
The first Assimp probe run records four disposed `XRDataBuffer` failures around
probe topology publication and removal. This is not a source BRDF failure. The
code todo owns buffer retirement; static producer scheduling does not repair it.

The corrected static producer build has zero warnings and errors. Native PID
24900 uses `xrengine.native-fbx` and reaches valid probe output on the second
poll, without manual initialization. Face reflection-only coat changes 382
checked face pixels, with a maximum channel difference of 85. Restore is exact.
The saved Tank mask gives no measurable tint response. A positive control
temporarily disables packed masks and sets reflection strength to 1. Black tint
then changes 34,316 checked Tank pixels, with the Face region unchanged. White
tint and final saved-value restores are exact in both checked regions.

The corrected native capture `renderdoc/sh-owner-native-probe-gpu_frame17817.rdc`
executes event 250 with groups `(91, 1, 1)` and no rejected pixels. Its 176-byte
probe row references irradiance handle 121 and prefilter handle 122. Nonzero
32-square irradiance and 256-square prefilter exports were viewed. The environment
row is 240 bytes. Front, oblique, and native compute-color output were viewed.
There is no Vulkan VUID, shader compile failure, texture upload failure, or
static resource-scope failure. One startup output reservation error recovers.
One frame-recording failure at 01:58:57 reports a disposed buffer during probe
publication; this remains a failed lifetime check even though later frames pass.

Fresh native SH restore is exact in the checked Face and Tank regions. Point
light and local Lambert, gradient, and monochromatic controls pass. The spot
control gives zero Tank contribution, including after `Transform.LookAt` sets
its verified forward vector to approximately `(-0.396, -0.330, 0.857)`. The spot
check remains open and must not be reported as passed.

The spot failure was traced to `AdvancedGlobalResourceCapture.CreateLight`.
It used `LightMeshMatrix`, which includes the cone volume's forward offset of
half its distance. The fixture emitter at `(0.3, 1.25, -0.65)` became
`(-0.29344, 0.75546, 0.63579)`. Its intended target was behind that displaced
origin, so the shader correctly returned zero cone attenuation. Light records
now use `Transform.RenderMatrix`, as the canonical light path does. The volume
matrix stays unchanged. The final isolated build has zero warnings and errors.
The fixture keeps the verified look-at orientation for repeated validation.

### Final Assimp Repeat

PID 36892 imports the exact prefab with `RequestedFbxBackend=Assimp` and
`ActualBackend=assimp`. It retains all 21 active material identities through
scalar edits and every lighting control. All checked Face and Tank regions
return exactly after SH, point, spot, probe tint, and coat restoration. The
point and corrected spot enable controls each change 34,328 Tank pixels while
the Face region stays unchanged. The L2 control changes 52 Face and 255 Tank
pixels. The unmasked Mochie tint control changes 34,319 Tank pixels; Face coat
reflection changes 376 Face pixels with Tank unchanged.

The spot capture `renderdoc/sh-final-assimp-env-spot_frame16942.rdc` and probe
capture `renderdoc/sh-final-assimp-probe-gpu_frame19132.rdc` each execute native
shading at event 245 with groups `(91, 1, 1)`. All 2,073,600 diagnostics equal
`0xff00`. Their native color exports were viewed. The captured spot record has
origin `(0.3, 1.25, -0.65)`, radius 3, and direction approximately
`(-0.396, -0.330, 0.857)`. The removed point row has intensity zero. The probe
row has valid irradiance and prefilter handles 121 and 122, and nonzero studio
convolution output. Those texture exports and final front and oblique images
were viewed.

The log has zero VUID, shader compile, or texture upload failures. Thirty-five
source-mismatch warnings occur from 02:11:38 to 02:12:02, before the measured
lighting controls. Two frame-recording failures remain; the last occurs at
02:14:58 and reports disposed `XRDataBuffer` access during probe publication.
Later frames recover. Recovery does not pass the probe lifetime check.

### Native Repeat And Late-Draw Buffer Failure

Native PID 31784 uses the same final binary. Scalar edits, SH, and point-light
controls pass before the backend failure. SH restoration is exact in Face and
Tank regions. Reversing SH X changes 6,473 Face and 33,995 Tank pixels; L2 changes
50 and 261 pixels. Point enable changes 34,328 Tank pixels with Face unchanged.
Point restore is exact. Gradient and monochromatic controls change Tank only.

At 02:23:57.964, the first spot-light addition coincides with a terminal
PresentNow transition. The transparent `Mat_Glow 5:Horns` draw rejects a retired
Vulkan buffer generation during descriptor update. This occurs before the first
probe initialization at 02:24:19, so it is separate from probe topology
publication. The later spot screenshots are byte-identical and are invalid
lighting evidence. Probe generation cannot complete while scene work is discarded.
The general timing query reports no terminal fault and the preparation query
still reports admission; those values do not establish successful presentation.
The native repeat is stopped, and its failed result is retained.

The first Spot component returns at 02:23:57.956; descriptor rejection follows
at .962. Point removal completed four seconds earlier. The unchanged
`EnsureForwardBuffer` path grows the first spot and spot-shadow metadata buffers
from one to 1,024 entries and immediately disposes their predecessors. These
buffers, at bindings 36 and 38, are the leading candidates. Captured canonical
draw bindings can retain the old native handles. The log does not identify the
rejected binding or resource owner, so this remains a bounded hypothesis.
Add those fields to the descriptor rejection diagnostic before selecting the
repair. The frame-slot number in the message is not a shader binding number.

### Final Native Spot And Probe Repeat

PID 5208 uses the same final binary and imports the exact prefab with actual
backend `xrengine.native-fbx`. This repeat creates one spot without prior point
creation or removal. It keeps the spot node alive and sets its intensity to zero
before the probe check. This sequence passes, but does not clear the earlier
intermittent lifetime failure.

Spot enable changes 34,328 Tank pixels, with Face unchanged. Reflection-only
Face clearcoat changes 380 Face pixels, with Tank unchanged. The unmasked Mochie
tint control changes 34,316 Tank pixels, with Face unchanged. All three controls
restore the checked Face and Tank regions exactly. The final saved material
values also restore exactly in those regions. Small full-image differences
remain outside the regions with active glitter.

The final probe capture `renderdoc/sh-repeat-native-probe-gpu_frame15807.rdc`
executes native shading at event 250 with groups `(91, 1, 1)`. All 2,073,600
diagnostic pixels equal `0xff00`. The selected probe has handles 121 and 122,
a 176-byte record, and nonzero 32-square irradiance and 256-square prefilter
output. The environment record is 240 bytes. Front, oblique, native compute
color, and exported probe textures were viewed. Pixel metrics are in
`reports/sh-repeat-native-controls.json` under the task evidence root.

The spot capture `renderdoc/sh-repeat-native-spot_frame14169.rdc` executes
native shading at event 249 with groups `(91, 1, 1)` and the same all-valid
diagnostic result. Its light record has the intended emitter position
`(0.3, 1.25, -0.65)` and direction approximately `(-0.396, -0.330, 0.857)`.
The native color export was viewed.

The log has zero Vulkan VUID, shader compile, or texture upload failures.
Twenty-eight source-mismatch warnings end at 02:30:46, before the controls.
One disposed-buffer command-record failure occurs at 02:31:38 during probe
publication. The desktop frame reports terminal failure and recreates the
swapchain; later changed screenshots and the captured native dispatch confirm
that rendering resumes. This differs from the persistent renderer pause in
PID 31784. It still fails the probe buffer lifetime check. All owned editor
and replay sessions are stopped after evidence review.

## Next Decision

Directional SH, local additive composition, and Mochie probe specular are
implemented and pass the focused controls on both prefab routes. Repair the
confirmed probe topology buffer lifetime and identify the retired buffer from
first local-light activation. Compare all lighting against a matched Unity
reference. Automatic SH projection and Unity per-renderer probe assignment remain
unsupported; engine probe convolution and local attenuation remain in use.
Keep the saved harness state and compare its visible intersection in that view.
The scalar-edit image audit does not support the earlier missing-group claim.
Texture metadata and native residency now publish together. The earlier extra
Assimp object was displaced hair; the source bind correction resolves it. Check
nonzero expression curves, accessory deformation,
animated bounds and shadows, and controller integration separately. Match the
Unity lighting and outfit for native Uber work. The linked code todos
own implementation work. The linked validation document owns visual, motion,
and performance checks.

The full-frame limits are 16.7 ms for 60 Hz, 11.1 ms for 90 Hz, and 8.3 ms for 120 Hz. These are target budgets, not measured results or avatar-only allowances. No numerical FPS claim is supported by this run.
