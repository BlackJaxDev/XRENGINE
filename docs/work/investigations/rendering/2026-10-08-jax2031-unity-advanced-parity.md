# Jax2031 Unity And Advanced Vulkan Parity

Date: 2026-10-08
Last updated: 2026-10-11
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
image are approximately 0.455 and 0.072. Other opaque groups disappear in two
control captures and return after restoration. These captures do not establish
stable live material editing. Keep material publication and group transitions
open. The final front and oblique views contain the complete avatar.

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
its detail-normal strength to 0 omits other opaque groups. Restoring strength 2
does not restore those groups in the 15-second follow-up capture. Profile
admission remains true, submission stays at strategy 2, and no terminal fault
appears. This reproduces a separate live-edit failure. Its cause is not yet
established; do not attribute it to texture promotion without more evidence.

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

### Texture Publication Review

`TryCreateSynchronizedImportedUploadPreparation` currently calls
`ApplyResidentDataForVulkanPublication` before allocation and upload complete.
The call changes dimensions, mip arrays, format, mip range, and canonical
content generation. The wrapper lock and callback suppression do not protect
canonical metadata readers. Cancellation can also leave these changes behind.

The next implementation must prepare an immutable resident-data and sampler
plan without changing the live texture. Resolve the format through the existing
color-space policy. Do not use the upload request's placeholder format. Compute
sampler limits from the pending mip chain, not the current preview. Recheck
authored sampler, color-space, and usage inputs before publication.

Commit completed descriptor, wrapper, and logical metadata together under the
streaming publication authority. Acquire the texture metadata writer lock before
the wrapper image lock, then the descriptor lock. Keep the metadata epoch odd
for the coupled commit. Descriptor failure must leave logical metadata and
content generation unchanged. After descriptor success, use notification-free
`SetField` transfers for the mip array, format, resident tracking, mip limits,
and cleared sparse state. Advance canonical content generation once. Detach
published handles before retirement and callbacks. Do not replay a generic
mipmap-change callback that can destroy the new image.

Keep strict canonical source validation and generation checks. A package made
before a successful publication is stale and must still retry. This transaction
fix will remove premature exposure; it will not remove every source-mismatch
retry. No texture-publication code changed in this shading validation run.

## Next Decision

Compare the source lighting map under matched lighting. Preserve the source
backface tangent-basis flag and investigate the fixed tank spot. Check live
material edits that temporarily omit other opaque groups.
Keep texture publication coherent during mip promotion. The earlier extra
Assimp object was displaced hair; the source bind correction resolves it. Check
nonzero expression curves, accessory deformation,
animated bounds and shadows, and controller integration separately. Match the
Unity lighting and outfit for native Uber work. The linked code todos
own implementation work. The linked validation document owns visual, motion,
and performance checks.

The full-frame limits are 16.7 ms for 60 Hz, 11.1 ms for 90 Hz, and 8.3 ms for 120 Hz. These are target budgets, not measured results or avatar-only allowances. No numerical FPS claim is supported by this run.
