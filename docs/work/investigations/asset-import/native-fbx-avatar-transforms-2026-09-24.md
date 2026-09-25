# Native FBX avatar scale and orientation

- Status: fixed and verified through the live desktop import path.
- Input: private `jax2031.fbx`, selected by the current Unit Testing World settings.
- Symptom: some meshes surround a small upright avatar with oversized, rotated,
  stretched geometry. The user initially described the asset as a prefab; the
  configured Unity prefab entry is disabled.

## Investigation

Three issues were confirmed:

1. `BuildSceneNodes` assumed FBX objects were ordered parent first. This asset
   declares children before their parents, causing a `KeyNotFoundException` and
   fallback to Assimp. The fallback loaded a roughly hundred-unit skeleton and
   reproduced the oversized geometry in front and oblique Vulkan captures.
   Native scene construction now traverses the actual hierarchy parent first,
   preserving sibling order and capturing complete world bind transforms.
2. The native importer interpreted serialized cluster `Transform` as a mesh
   world matrix, reused the first cluster's value for every bone, and multiplied
   by inverse `TransformLink` again. Serialized `Transform` is already a
   mesh-to-bone inverse bind. The parser now preserves that value, and each bone
   uses its own cluster matrix. The common axis/unit conversion stays in the
   imported content hierarchy. The binary exporter already writes the raw
   matrix and needs no format change.
3. `C_Pants` has 89 unweighted control points, all referenced by polygons; 66
   polygons join weighted and unweighted vertices. The shader's zero-weight
   branch emitted raw positions reaching 75.6 source units instead of their
   authored meter-space placement. Hiding only `C_Pants` removed the remaining
   spikes. Import now supplies a rigid mesh-node influence whose inverse bind is
   `authoredMeshBind * inverse(meshNodeBind)`. The authored mesh bind is
   reconstructed from `Transform * TransformLink * importWorld`, preserving
   placement even when the current mesh hierarchy differs from the bind pose.

The serialized convention is independently documented in the
[Blender FBX exporter](https://github.com/blender/blender-addons/blob/main/io_scene_fbx/export_fbx_bin.py#L1699)
and implemented by the
[ufbx reader](https://github.com/ufbx/ufbx/blob/master/ufbx.c#L13075).

An independent audit of all 69 skinned mesh nodes found that every cluster in
each mesh reconstructs the same authored mesh bind world, within 0.0000322 source
units. Correct per-cluster skinning places the Body at Y -0.0049 to 1.2029 meters,
Face at Y 1.145 to 1.346 meters, and boots at Y -0.033 to 0.175 meters. The missing
pants influences belong around Y 0.744 to 0.790 meters. Both tails have complete
influences and no unexplained downward bounds.

Native FBX producer version advances to 5 to invalidate prior cached output.

The existing `NativeFbxSkinWeights_UseImportedMeshBindPoseInsteadOfClusterTransformMatrix`
test still calls an older three-argument private method signature. It does not
match either the pre-fix four-argument signature or the new five-argument
signature. No tests were added, modified, or run during this runtime repair.

## Validation setup

- Named isolated session: `avatar-transforms-0924`.
- Preserve the configured Vulkan backend and advanced pipeline for reproduction.
- Use a private copy of startup settings through
  `XRE_UNIT_TEST_WORLD_SETTINGS_PATH`; shared user settings remain untouched.
- Desktop-only isolation clears `VR.EditorToggleRuntime` in that private copy
  after baseline reproduction. The initial toggle-ready setup created a
  `VRHeightScaleComponent` and applied a 1.8 root scale. The plain desktop setup
  retains the source's root scale of 1.
- Evidence root:
  `Build/_AgentValidation/00000000-000000-shared/avatar-transforms-20260924/`.
  The normal retention script failed on a locked older editor artifact; the
  separate cleanup attempt was policy-blocked. Evidence uses the shared root to
  avoid exceeding the immediate-directory limit.
- The broker advertised only deprecated model IDs and recommended a deprecated
  model despite the GPT-6 constraint. No paid worker was launched or substituted.

## Validation results

The final isolated editor build passed with zero warnings and zero errors in
59.96 seconds. The final run used the Vulkan backend and the native FBX producer
at version 5. It loaded 2,697 scene nodes, 83 materials, and 109 render meshes in
25.765 seconds without an Assimp fallback. Its log explicitly confirms the new
89-control-point rigid influence repair, excluding reuse of an intermediate
import result. The animated Unit Testing World route calls `ScheduleImportJob`
and imports directly; it does not read or write a cooked model payload. A warm
model-cache reload was therefore not part of this reproduction.

Front, oblique, and rear captures were viewed with all imported meshes enabled,
including `C_Pants`. The avatar is upright, its body/clothing share meter-scale
placement, and the oversized/spiking geometry is gone. Live skin-culling
diagnostics and the independent source audit agree with those bounds. The owned
editor session was stopped after capture and its logs inspected. Independent
code review found no blocking issue in traversal, bind math, influence copying,
culling, or transform-reference rebinding. User confirmation is not yet recorded.

Evidence paths below are relative to the evidence root above:

- `mcp-captures/final-front/Screenshot_20260924_183813_290_d8aa1b6176134bc691804b7782ddf2c4.png`
- `mcp-captures/final-oblique/Screenshot_20260924_183916_074_52c311f8bd404c6987d089f477471ad6.png`
- `mcp-captures/final-rear/Screenshot_20260924_184243_209_d3d3c5ea586344c286acc16dfa0c85b2.png`
- `logs/final-build.log`, `logs/final-log_meshes.log`,
  `logs/final-log_rendering.log`, and `logs/final-log_animation.log`.

## Remaining limitations

The rigid influence preserves the bind placement and follows mesh-node/ancestor
movement. It does not invent missing skeletal weights. Mixed weighted/unweighted
triangles can still stretch under later skeletal animation; authoring proper
weights in the source asset is the appropriate repair for that case.

At the end of this transform repair, the configured startup Unity animation was blocked by an existing invalid
humanoid-definition diagnostic (LeftUpperLeg mapping confidence 74%). Its
playback time is zero and `IsPlaying` is false, ruling it out as the remaining
bind-pose spike source. Animation mapping and missing source textures are
separate from this import-transform repair. The subsequent
[mapping and import-diagnostics repair](avatar-mapping-and-import-diagnostics-2026-09-24.md)
fixes that detection failure and validates startup playback, and publishes missing
texture references to the Missing Assets dialog after model processing.

The Vulkan run also logged transient render-resource publication diagnostics,
including one `XRFrameBuffer` CPU-construction publication exception. Rendering
recovered and all final captures succeeded. Those renderer diagnostics were not
changed as part of this asset importer repair.
