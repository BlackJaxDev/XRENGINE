# Humanoid Body/Root Parity and Compensation TODO

Last Updated: 2026-08-24
Owner: Animation / Avatar
Status: Complete; all phases are implemented and validated against the refreshed Unity v3 reference

Related evidence:

- `docs/work/investigations/avatar/humanoid-body-root-compensation-2026-08-24.md`
- `Assets/Walks/Sexy Walk.anim`
- `XREngine.UnitTests/TestData/SexyWalkHumanoidRawAudit.compact.json`
- Unity Manual, Root Motion: <https://docs.unity3d.com/Manual/RootMotion.html>
- Unity `HumanPoseHandler.GetHumanPose`: <https://docs.unity3d.com/ScriptReference/HumanPoseHandler.GetHumanPose.html>

## Goal

Establish measured Unity-to-XRENGINE parity for imported humanoid body data and
then implement only the body/hips compensation required by that evidence.
Unity humanoid `RootT`/`RootQ` data represents the retargetable Body Transform,
not an already-projected scene/model Root Transform. XRENGINE must therefore
keep these concepts distinct:

1. raw imported Unity body channels;
2. the converted, normalized humanoid body pose;
3. the final composed Hips local transform;
4. any temporal Root Transform delta published to the character/model root;
5. procedural IK or contact compensation applied after authored pose data.

Observability, deterministic evaluation, the Body-to-projected-root/Hips split,
loop accumulation, state-machine quaternion blending, avatar calibration, and
authored IK/contact handling are implemented. The fresh Unity v3 export from
this machine is the acceptance oracle; the resulting XRENGINE pose is also
validated in the live editor from multiple camera positions.

## Unity Contract To Preserve

- Unity documents the Body Transform as the humanoid center of mass and the
  Body Orientation as an avatar-relative average body orientation.
- Body Transform/Orientation are the humanoid clip's world-space curves;
  muscles and humanoid IK goals are relative to the body transform.
- Unity computes the movable Root Transform as a runtime projection of the Body
  Transform, controlled by clip import settings such as Bake Into Pose.
- `HumanPose.bodyPosition` is normalized by `Avatar.humanScale`. Do not assume
  XRENGINE's current average hips-to-feet bind distance is equivalent without
  measured reference evidence.
- Do not treat a first-sample-relative Hips offset as both the absolute humanoid
  body pose and a temporal model-root motion delta. Those are separate states.

## Confirmed Completed Behavior

- Scalar `RootT.*` and `RootQ.*` channels are staged in one evaluator-owned,
  allocation-free transaction. A complete sample is committed once, with
  neutral defaults, finite-value diagnostics, and quaternion normalization.
- Direct clips and state machines use complete Body samples. State-machine
  `RootQ` groups are blended as normalized quaternions rather than four
  unrelated scalars.
- The audit samples through a render-thread barrier, derives hierarchy state
  from current local matrices, and restores clocks, bindings, humanoid caches,
  transforms, dirty flags, projected-root state, and IK diagnostics exactly.
- The v3 profile records `humanScale = 0.8116461039`, 22 mapped roles, four
  twist chains, avatar body axes, and coupled muscle models. XRENGINE measures
  `39.370068` units per meter for a Body-motion scale of `31.954561`.
- Projected root XZ, calibrated Y, and yaw are independent validity channels.
  Absolute projected pose, consecutive delta, unwrapped loop pose, explicit
  target application, and external-consumer publication are distinct states.
- Hips and the other calibrated bones are evaluated once from their Unity
  neutral local pose and coupled-muscle models. Body/root projection no longer
  writes an incompatible intermediate Hips pose into hierarchy/render state.
- Across 81 fixed samples, full projected XYZ error is `0.017704` average /
  `0.132401` maximum engine units and projected rotation error is `0.000977` /
  `0.039565` degrees. Hips local error is `0.235714` / `0.932247` engine units
  and `0.005122` / `0.055953` degrees. The worst selected endpoint difference
  is about `1.2794` engine units (`3.25 cm`).
- Direct seek at `t=0.8` is bit-identical when repeated. Forward and reverse
  loops, replay, restart, clip replacement, direct/state-machine handoff, and
  lifecycle activation are stable without accumulated pose state.
- Authored Unity IK goals use calibrated body-to-world conversion, an explicit
  policy, status diagnostics, and one solver. The four intended goals apply;
  conversion error is at most `6e-6` engine units. Optional ground-plane
  compensation can target feet only or feet and hands, and Disabled preserves
  the authored result exactly.
- Fresh FXAA and TSR running sequences show one coherent, non-accumulating
  silhouette from opposing cameras through a loop. The Unity screenshot series
  contained uncleared old silhouettes, so numeric JSON and current-frame
  silhouettes—not those raster remnants—are the parity oracle.
- Ordinary skinned motion vectors are independently confirmed black while the
  mesh deforms. That renderer producer defect is isolated in the linked
  rendering investigation and is not a duplicate-pose or humanoid-compensation
  failure.

## Scope

- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/AnimationClipComponent.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs`
- `XREngine.Animation/Importers/UnityAnimImporter.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/Diagnostics/HumanoidPoseAudit*.cs`
- `XREngine.Runtime.Core/Scene/Transforms/TransformDiagnostic*.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/HumanoidIKSolverComponent.cs` after body parity
- `Tools/Unity/HumanoidPoseAuditExporter.cs`
- `Tools/Unity/HumanoidPoseAuditOverlay.cs`
- focused humanoid/importer tests after the live path is validated

## Non-Goals

- Do not claim exact private Mecanim equations; use public Unity contracts and
  measured reference output.
- Do not move the model/scene root merely because a clip contains Body
  Transform curves.
- Do not make ordinary limb muscles invent body translation.
- Do not add contact IK, replace RootT scale, replace RootQ composition, or
  expose a new public body-frame type until corrected parity evidence requires
  it.
- Do not interpret free-running screenshots as skeletal acceptance evidence;
  use paused deterministic samples.
- Track skinned-mesh temporal ghosting separately under rendering/velocity
  history.
- The local `Desktop/Misc/Mitsuki.fbx` is integration evidence, not a
  redistributable automated-test asset.

## Phase 0 - Trustworthy Observability

- [x] After each exact-time clip evaluation, derive a side-effect-free hierarchy
  snapshot from current local matrices before audit capture. Do not publish
  intermediate samples into live world/render transform caches.
- [x] Ensure audit sampling preserves and restores playback time, body/root
  baseline state, and the visible live pose.
- [x] Capture coordinate spaces explicitly from the local-matrix snapshot rather
  than relying on stale cached `WorldTranslation` values or converting transform
  types during diagnostics.
- [x] Record importer-mapped current/canonical Body, converted Body deltas,
  composed Hips local transform, and character-root local/world transforms as
  separately named fields. Unity's exporter separately records projected root
  and root-motion delta; XRENGINE has no published temporal root delta yet.
- [x] Re-run paused fixed-time samples at `t=0`, `0.8`, `1.6`, and `2.4` seconds.
- [x] Confirm Head/Hands/Feet positions vary and match live MCP transform queries
  after conversion into the same space.
- [x] Refresh the matching Unity reference export with the same avatar, clip,
  sample times, coordinate labels, avatar human scale, neutral pose, and all 95
  single-muscle response probes.

Acceptance criteria:

- [x] Repeating an audit does not change subsequent live playback or body/root
  baselines.
- [x] Derived endpoint positions agree with the live hierarchy at each sampled
  time.
- [x] Every reported transform names its source and coordinate space.

## Phase 1 - Transactional Unity Body-Channel Evaluation

- [x] Remove the implicit "Z commits RootT / W commits RootQ" contract.
- [x] Stage scalar RootT/RootQ values and dirty-component masks during property
  evaluation, then finalize each vector/quaternion exactly once after every
  animated member for the pose has been sampled.
- [x] Define stable defaults for missing components instead of reusing stale
  values from another sample or clip.
- [x] Normalize a completed RootQ only during finalization; reject or diagnose a
  non-finite/zero-length quaternion without silently corrupting the pose.
- [x] Define evaluator ownership and baseline behavior for play, replay, stop,
  clip replacement,
  deactivation, loop wrap, negative playback, direct seek, and exact-time audit
  evaluation.
- [x] Ensure a fixed-time result is independent of evaluation history and scalar
  member registration order.
- [x] Keep the transaction allocation-free in per-frame animation ticks.

Acceptance criteria:

- [x] The first completed Sexy Walk sample captures the full RootT and RootQ
  values shown by the raw audit.
- [x] Sampling time B directly produces the same body/Hips state as A-to-B and
  C-to-B evaluation paths.
- [x] Partial channels use documented neutral defaults and cannot fail to
  commit. The synthetic transaction fixtures cover missing and reordered
  components.

## Phase 2 - Body/Root Contract and Unity Parity Evidence

- [x] Implement and compile a reproducible Unity batch exporter for the avatar,
  clip, human scale, clip root settings, Body/Hips/root data, endpoints, muscles,
  and raw curves.
- [x] Run that exporter in a licensed Unity Editor and retain the reference JSON
  as disposable validation evidence plus a compact avatar-profile sidecar.

- [x] Compare, at identical fixed times, Unity raw/body values, XRENGINE imported
  values, composed bone-local rotations, selected limb endpoints, projected
  root, avatar scale, clip metadata, and the extract-only XRENGINE temporal
  delta for consecutive samples.
- [x] Complete mismatch isolation independently. All seven layers are measured
  and have separate diagnostics:
  1. scalar curve import and axis/handedness conversion;
  2. normalized body-position scale;
  3. RootQ coordinate conversion and multiplication order;
  4. body-to-Hips composition;
  5. muscle-to-bone retargeting;
  6. temporal Root Transform extraction;
  7. optional IK/contact compensation.
- [x] Document what `EstimateAnimatedMotionScale()` measures and compare it with
  Unity avatar human scale before retaining or replacing it.
- [x] Verify RootQ sign continuity without confusing `q` and `-q` with a pose
  discontinuity.
- [x] Define loop-pose behavior separately from root-delta accumulation. The
  within-cycle projected pose remains canonical-relative; a cached endpoint
  transform is exponentiated and composed per signed loop count for unwrapped
  placement, while the temporal delta compares consecutive unwrapped poses.

Acceptance criteria:

- [x] Each measured error is assigned to one conversion/composition layer;
  projected pose, unwrapped placement, temporal delta, Hips residual, authored
  IK, and optional contact compensation remain separately observable.
- [x] No scale, quaternion, IK, or body-frame change is justified only by visual
  preference or stale audit output.

## Phase 3 - Implement Only Demonstrated Corrections

The refreshed v3 export justified the completed corrections:

- avatar-specific neutral transforms, body axes, role/twist metadata, and
  coupled-muscle bone models;
- `humanScale * measured units-per-meter` Body-motion scaling;
- preservation and independent evaluation of Unity's XZ, Y, and orientation
  projection metadata;
- quaternion-aware complete Body samples in direct and state-machine playback;
- explicit root-placement ownership, signed loop accumulation, and authored IK
  conversion/contact policies.

With the matching v3 profile loaded, the 81-sample coupled comparison reduces
Hips local rotation error to `0.005122` degrees average / `0.055953` maximum.
Projected yaw is `0.000977` / `0.039565` degrees, and the maximum selected-bone
endpoint difference is approximately `3.25 cm`.

- [x] Keep raw imported body data immutable for diagnostics.
- [x] Expose projected root pose and consecutive temporal root delta as
  allocation-free component-valid outputs. Preserve and restore their exact
  state during diagnostic evaluation and invalidate temporal continuity at
  genuine playback discontinuities.
- [x] Apply the verified normalized Body Transform to the humanoid pose in one
  deterministic Hips/body composition step.
- [x] Keep absolute/in-place body pose state separate from temporal Root
  Transform deltas and model-root publication.
- [x] Define the exact Hips composition order: calibrated Unity neutral local
  transform, coupled authored-muscle residual, hierarchy propagation, then
  optional procedural IK/contact output.
- [x] Evaluate muscle deltas in the avatar's documented bind/body basis and let
  the composed parent body transform propagate them. Do not dynamically rotate
  the muscle basis a second time unless Unity parity evidence proves that is
  required.
- [x] Recompute hierarchy/render/skinning state after composed Hips changes. A
  single Scene-order coupled-pose publication avoids exposing an intermediate
  incompatible Hips transform.
- [x] Introduce a distinct runtime body-frame type only if private staged body
  state plus explicit root-motion output cannot represent the verified
  contract cleanly. `HumanoidProjectedRootPose` provides the distinct public
  placement value; no second mutable body-frame object is needed.

Acceptance criteria:

- [x] Mitsuki/Sexy Walk matches the refreshed Unity fixed-time body, Hips, and
  endpoint evidence within the recorded v3 tolerances.
- [x] Paused and running playback remain stable across loops, seeks, restarts,
  and clip replacement.

## Deferred Work After Body Parity

### IK and contacts

- [x] Decide whether authored Unity IK goals should be evaluated on this runtime
  path and enable them only with calibrated mappings.
- [x] Transform IK goals through the verified body frame and prevent duplicate
  custom-rig solving.
- [x] Keep authored body/root projection separate from post-pose IK body
  compensation; expose their diagnostics independently.
- [x] Add explicit, configurable foot/hand contact compensation only after
  ordinary clip playback matches Unity without it.

### Avatar metadata and retargeting quality

- [x] Formalize required/optional humanoid roles, avatar human scale, body axes,
  twist chains, stretch, feet spacing, and translation DoF in the avatar profile.
- [x] Warn on ambiguous Hips/Spine/UpperLeg mappings and non-finite body inputs.
- [x] Keep runtime role lookup dense and allocation-free.

### Diagnostics and rendering

- [x] Add deterministic zero-muscle and all-95-muscle `-1/+1` response probes
  to both Unity and XRENGINE audit schema 6, with exact diagnostic restoration.
- [x] Extend the audit comparer with worst-time body/Hips phase error and
  per-bone local translation error.
- [x] Extend overlays for body trajectory, projected root, Hips local transform,
  IK goals, and compensation source.
- [x] Create a separate rendering investigation for the observed skinned-mesh
  temporal ghosting/velocity-history failure.

## Completed Phase Sequence

1. [x] Prove the semantic `RootT` basis from refreshed Unity projected-root
   samples and the XRENGINE model-root/Hips-parent bases.
2. [x] Add explicit, allocation-free projected root pose and temporal root
   delta outputs. They default to extract-only and never overwrite external
   character placement.
3. [x] Apply the calibrated coupled Hips residual in the same final pose pass
   that exposes projected XZ/Y/yaw.
4. [x] Implement calibrated Y projection for the matching clip/profile while
   keeping XZ, Y, and yaw validity independently diagnosable.
5. [x] Validate fixed times and signed loop accumulation, then replace
   state-machine scalar RootQ blending with quaternion-aware Body groups.
6. [x] Validate authored IK/contact behavior and isolate the unrelated skinned
   motion-vector producer in a rendering investigation.

## Tests After Live Feature Validation

The corrected live Mitsuki path was validated before test work began. Focused
redistributable fixtures now cover:

- atomic RootT/RootQ evaluation and partial channels;
- reset, clip switch, exact seek, loop seam, reverse playback, and scrubbing;
- audit state preservation and transform propagation;
- isolated body translation/rotation;
- combined body motion and muscle pose;
- optional IK goals and explicit contact compensation.

Retain `Sexy Walk.anim` and local Mitsuki playback as integration coverage, but
do not make automated tests depend on the external FBX.

## Validation Workflow

For every runtime slice:

1. Build the editor through a uniquely named isolated MCP session.
2. Run Mitsuki with `Assets/Walks/Sexy Walk.anim` in the ImGui Unit Testing World.
3. Pause and capture identical camera views at `0`, `0.8`, `1.6`, and `2.4`
   seconds.
4. Inspect the saved PNGs and fixed-time transform/audit data.
5. Stop only the named session and inspect animation/rendering logs.
6. Record results in the linked investigation note before changing the next
   variable.

Narrow build/run validation before tests:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
pwsh Tools/Manage-McpEditorSession.ps1 Start -Name <unique-session>
pwsh Tools/Invoke-Mcp.ps1 -Session <unique-session> -Method ping
pwsh Tools/Manage-McpEditorSession.ps1 Stop -Name <unique-session>
```

Focused tests, only after live validation and explicit clearance:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Humanoid
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UnityAnimImporter
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~AnimationClipComponent
```

## Completion Checklist

- [x] Record before/after Unity comparison numbers in the linked investigation.
- [x] Update `docs/user-guide/animation.md` if public body/root behavior or editor
  controls change.
- [x] Update Unit Testing World settings/schema for the root application mode
  and direct/state-machine validation toggle, then regenerate root/server JSONC.
- [x] Add focused regression coverage only after live validation and the user's
  completion directive.
- [x] Verify no new compiler warnings or hot-path allocations in the transaction
  and diagnostic-scope implementation.
