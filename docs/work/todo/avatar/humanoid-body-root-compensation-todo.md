# Humanoid Body/Root Parity and Compensation TODO

Last Updated: 2026-08-24
Owner: Animation / Avatar
Status: Paused after Unity reference export, avatar-response calibration, and measured motion-scale validation; projected root/Hips decomposition remains open

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

Observability and deterministic evaluation are now implemented. Unity reference
evidence has also proved the avatar scale and most per-bone muscle response
corrections. The remaining consequential work is the Body-to-projected-root and
Hips-residual split; contact compensation remains deferred until that split is
correct.

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

## Confirmed Current Behavior and Remaining Gaps

- The imported Unity `.anim` path routes scalar `RootT.*` and `RootQ.*` curves
  to `HumanoidComponent.SetRootPosition*` and `SetRootRotation*`.
- The former "Z commits RootT / W commits RootQ" behavior was a real defect. It
  produced incomplete first samples. RootT/RootQ are now staged in one
  evaluator-owned transaction and committed once after all animated members.
- Direct clips cache their complete time-zero RootT/RootQ sample and pass that
  canonical reference on every evaluation. Missing channels start from neutral
  values, invalid values are diagnosed, and RootQ is normalized only at commit.
- The audit now samples at a render-thread barrier, composes hierarchy spaces
  from local matrices, suppresses temporary world-dirty publication, restores
  the exact clip clock/binding state, humanoid caches, transform states, and
  dirty flags, then asserts that restoration was bit-exact.
- The corrected 81-sample/25 Hz report has changing Head/Hand/Foot positions.
  At `t=1.6`, the audit Hips and live MCP Hips agree exactly; Head world position
  agrees to approximately `3e-7` engine units.
- Direct B, A-to-B, C-to-B, reset-to-B, stop/play-to-B, and deactivate/reactivate
  paths produce the same Hips translation and rotation at `t=1.6`.
- All 81 imported Body and composed Hips samples remained identical after the
  ownership/lifecycle work. The loop endpoint is continuous within the source
  curve's small seam error.
- Paused fixed-time poses are coherent. Running multiple-silhouette ghosting is
  a separate temporal rendering/velocity-history problem, not evidence of a
  duplicated skeleton or body transform.
- Both audit-enabled and audit-disabled controls emit 40 startup
  `[SkinExplode]` threshold warnings during ordinary playback. The avatar stays
  visually coherent; the fixed 50-unit diagnostic threshold is not a valid
  failure signal for this centimeter-scale skeleton and belongs to rendering
  diagnostics, not this body transaction.
- The Unity exporter now records `HumanPose`, Animator Body, projected root,
  root-motion delta, Hips, endpoints, all 95 muscle probes, `humanScale`, avatar
  description settings, and serialized clip root settings. A licensed Unity
  2022.3 batch export completed successfully without touching the user's open
  Unity project.
- Unity's reference avatar reports `humanScale = 0.8143980503`. Matching bind
  vectors measure `39.370064` XRENGINE units per Unity meter, so the verified
  Body-motion scale is `32.062904`; the old hips-to-feet estimate was about
  `25.48` and has been replaced whenever a Unity avatar profile is present.
- A compact avatar profile now preserves the neutral pose and measured
  negative/positive response of every muscle on every affected humanoid bone.
  Runtime profile overrides reduce the dominant arm/hand/neck errors from tens
  of degrees to mostly sub-degree values, while unprofiled/missing roles retain
  the geometry solver.
- Unity humanoid clip root-projection metadata is now retained on imported
  clips instead of being discarded.
- The current startup path disables `HumanoidIKSolverComponent`, so this repro
  does not establish foot-contact or IK parity.
- State-machine RootQ blending still operates on scalar components, no temporal
  projected Root Transform is published yet, and Hips is intentionally not
  profile-overridden until projected root yaw and Hips-local residual are
  committed together.
- Raw `RootT`/`RootQ` curve import is numerically exact, but the current
  semantic `RootT` mapping swaps Unity Y/Z before composing directly onto Hips.
  That basis is now a measured open question and must be resolved with the
  projected-root evidence, not changed in isolation.

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
  commit. Synthetic fixture coverage remains deferred under the repository test
  policy.

## Phase 2 - Body/Root Contract and Unity Parity Evidence

- [x] Implement and compile a reproducible Unity batch exporter for the avatar,
  clip, human scale, clip root settings, Body/Hips/root data, endpoints, muscles,
  and raw curves.
- [x] Run that exporter in a licensed Unity Editor and retain the reference JSON
  as disposable validation evidence plus a compact avatar-profile sidecar.

- [x] Compare, at identical fixed times, Unity raw/body values, XRENGINE imported
  values, composed bone-local rotations, selected limb endpoints, projected
  root, avatar scale, and clip metadata. Temporal XRENGINE root delta remains
  unavailable because it has not yet been implemented.
- [ ] Complete mismatch isolation independently. Items 1-5 are measured; items
  6-7 remain open because projected root output and IK/contact are not yet live:
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
- [ ] Define loop-pose behavior separately from root-delta accumulation.

Acceptance criteria:

- [x] Each currently measured error is assigned to one
  conversion/composition layer; temporal root and IK layers remain explicitly
  unimplemented rather than conflated with the pose.
- [x] No scale, quaternion, IK, or body-frame change is justified only by visual
  preference or stale audit output.

## Phase 3 - Implement Only Demonstrated Corrections

The reference export justified three corrections that are now implemented:

- avatar-specific neutral rotations and measured per-muscle bone responses;
- `humanScale * measured units-per-meter` Body-motion scaling;
- preservation of Unity's clip root-projection metadata.

With the compact Mitsuki profile loaded, full local-quaternion comparison over
81 Sexy Walk samples reduced the worst previously dominant errors to: lower
arms at most `0.06` degrees, hands `1.18`, neck `1.12`, right upper arm `6.75`,
left upper arm `13.55`, and legs/feet `0.91`-`11.00`. Hips remains
`12.40` degrees average / `23.46` maximum because its measured local response
has deliberately not replaced the current full-Body rotation before projected
root yaw exists.

- [x] Keep raw imported body data immutable for diagnostics.
- [ ] Apply the verified normalized Body Transform to the humanoid pose in one
  deterministic Hips/body composition step.
- [ ] Keep absolute/in-place body pose state separate from temporal Root
  Transform deltas and model-root publication.
- [ ] Define the exact Hips composition order: avatar bind/neutral state,
  converted body translation/rotation, Hips muscle channels if supported, and
  later procedural body/IK offsets.
- [ ] Evaluate muscle deltas in the avatar's documented bind/body basis and let
  the composed parent body transform propagate them. Do not dynamically rotate
  the muscle basis a second time unless Unity parity evidence proves that is
  required.
- [ ] Recompute hierarchy/render/skinning state after composed Hips changes.
- [ ] Introduce a distinct runtime body-frame type only if private staged body
  state plus explicit root-motion output cannot represent the verified
  contract cleanly.

Acceptance criteria:

- [ ] Mitsuki/Sexy Walk matches the refreshed Unity fixed-time body, Hips, and
  endpoint evidence within agreed tolerances.
- [ ] Paused and running playback remain stable across loops, seeks, restarts,
  and clip replacement.

## Deferred Work After Body Parity

### IK and contacts

- [ ] Decide whether authored Unity IK goals should be evaluated on this runtime
  path and enable them only with calibrated mappings.
- [ ] Transform IK goals through the verified body frame and prevent duplicate
  custom-rig solving.
- [ ] Keep authored body/root projection separate from post-pose IK body
  compensation; expose their diagnostics independently.
- [ ] Add explicit, configurable foot/hand contact compensation only after
  ordinary clip playback matches Unity without it.

### Avatar metadata and retargeting quality

- [ ] Formalize required/optional humanoid roles, avatar human scale, body axes,
  twist chains, stretch, feet spacing, and translation DoF in the avatar profile.
- [ ] Warn on ambiguous Hips/Spine/UpperLeg mappings and non-finite body inputs.
- [ ] Keep runtime role lookup dense and allocation-free.

### Diagnostics and rendering

- [x] Add deterministic zero-muscle and all-95-muscle `-1/+1` response probes
  to both Unity and XRENGINE audit schema 6, with exact diagnostic restoration.
- [ ] Extend the audit comparer with worst-time body/Hips phase error and
  per-bone local translation error.
- [ ] Extend overlays for body trajectory, projected root, Hips local transform,
  IK goals, and compensation source.
- [ ] Create a separate rendering investigation for the observed skinned-mesh
  temporal ghosting/velocity-history failure.

## Next Resume Point

Do not change more limb tuning first. Resume at the Body/root boundary:

1. Prove the semantic `RootT` basis from Unity projected-root samples and the
   XRENGINE model-root/bind basis. The current source-Y/source-Z swap is
   numerically faithful to the old importer but appears semantically suspect.
2. Add explicit, allocation-free outputs for projected root pose and temporal
   root delta. Default to extract-only so animation never overwrites external
   character placement; make scene-root application an explicit locomotion
   policy.
3. Apply the measured Hips muscle response and residual Body tilt/roll in the
   same atomic composition that extracts projected yaw. Do not remove yaw from
   Hips one frame before the root receives it.
4. Implement Y projection from the clip's `HeightFromFeet`/Bake settings using
   measured foot-bottom data; keep XZ, yaw, and Y policies independently
   diagnosable.
5. Validate fixed times and loop wrap, then replace state-machine scalar RootQ
   blending with complete quaternion-aware Body samples.
6. Only after that, address authored IK/contact behavior and the separate
   temporal-rendering ghosting issue.

## Tests After Live Feature Validation

Per repository policy, do not add or revise regression tests until the corrected
live Mitsuki path has been validated and the user explicitly clears test work.
Then add a redistributable synthetic avatar/clip corpus covering:

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
- [ ] Update `docs/user-guide/animation.md` if public body/root behavior or editor
  controls change.
- [ ] Update Unit Testing World settings/schema if new toggles are introduced.
- [ ] Add focused regression coverage only after live validation and user
  approval.
- [x] Verify no new compiler warnings or hot-path allocations in the transaction
  and diagnostic-scope implementation.
