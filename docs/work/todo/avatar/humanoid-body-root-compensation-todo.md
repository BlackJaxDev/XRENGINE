# Humanoid Body/Root Compensation TODO

Last Updated: 2026-06-08
Owner: Animation / Avatar
Status: Active
Target Branch: `humanoid-body-root-compensation`

Research source:

- User-provided `deep-research-report.md` on Unity-style humanoid animation.

## Goal

Fix and improve the humanoid animation path so imported Unity humanoid clips and
runtime humanoid animator values produce stable body-space motion: the hips/body
frame translates and rotates correctly when Unity-authored body/root curves,
limb muscles, twist channels, IK goals, or layered humanoid animation require
compensation. The result should feel like Unity Mecanim playback: clips are not
just local bone curves, and limb motion is evaluated relative to a canonical
body transform rather than directly copied onto arbitrary skeleton joints.

## Research Takeaways

- Unity Humanoid is a layered system: Avatar mapping, body-space/muscle-space
  clip data, controller/layer blending, optional procedural constraints and IK,
  root motion extraction, then skinning.
- Unity stores a Humanoid clip around a Body Transform/Body Orientation. Muscle
  curves and IK goals are body-relative; the runtime Root Transform is projected
  from the body trajectory according to clip root settings.
- The correct mental model is not "make the hips chase animated endpoints." The
  safer model is "evaluate a canonical body frame, evaluate muscles and goals
  relative to it, then solve mapped bones/IK in a known order."
- Built-in Unity IK is a late IK pass for Humanoid Avatars, not a universal
  full-body solver during ordinary clip sampling.

## Current Reality

- `HumanoidComponent` stores normalized muscle values, raw humanoid values, and
  applies a full muscle pose once per frame in `ApplyMusclePose`.
- Limb muscles already use a bind-pose body basis for arm/leg axes.
- Imported Unity `.anim` humanoid root curves route `RootT.*` and `RootQ.*` to
  `HumanoidComponent.SetRootPosition*` and `SetRootRotation*`.
- `SetRootPosition` and `SetRootRotation` currently apply bind-relative offsets
  directly to `Hips`.
- `HumanoidPoseAudit*` infrastructure already compares body position, body
  rotation, muscles, and bone root-space/world-space samples against Unity data.
- `UnityHumanoidRawCurveRegressionTests` verifies raw muscle curve replay but
  does not yet validate final Unity-like body/hip compensation across the full
  clip.

## Scope

- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs`
- `XREngine.Animation/Importers/UnityAnimImporter.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/IK/HumanoidIKSolverComponent.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/Diagnostics/HumanoidPoseAudit*.cs`
- `Tools/Unity/HumanoidPoseAuditExporter.cs`
- `Tools/Unity/HumanoidPoseAuditOverlay.cs`
- `XREngine.UnitTests/Animation/*Humanoid*`
- `Assets/Walks/Sexy Walk.anim`
- `XREngine.UnitTests/TestData/SexyWalkHumanoidRawAudit.compact.json`

## Non-Goals

- Do not invent a hidden CPU fallback for explicitly requested GPU/accelerated
  animation paths.
- Do not claim exact Unity native Mecanim equations; use measured parity and
  documented body-space behavior as the contract.
- Do not make limb-only muscle channels silently rewrite locomotion unless a
  documented body-compensation mode or IK/contact solve requested it.
- Do not overwrite the scene root transform when the clip should only animate
  the humanoid body/hips inside the character root.
- Do not expand this into a full controller rewrite unless required by
  body/root ordering.

## Phase 0 - Branch, Baseline, And Repro

- [ ] Create dedicated branch `humanoid-body-root-compensation`.
- [ ] Save the research report or a summarized stable design note under
  `docs/work/design/animation/` if the external report is needed long-term.
- [ ] Capture the current XREngine output for `Assets/Walks/Sexy Walk.anim`
  with `HumanoidPoseAuditComponent`.
- [ ] Capture or refresh the matching Unity reference with
  `Tools/Unity/HumanoidPoseAuditExporter.cs`.
- [ ] Add a compact fixture that includes body position, body rotation,
  root-space hips, hands, feet, knees, elbows, and the raw muscle channels most
  involved in hip compensation.
- [ ] Document the exact coordinate-space convention for Unity `RootT`, `RootQ`,
  IK goal curves, engine hips local space, engine root space, and world space.

Acceptance criteria:

- [ ] The repo has a deterministic failing or skipped parity fixture showing the
  current hip/body drift, missing compensation, or incorrect body rotation.
- [ ] The expected body/root-space values are inspectable without launching the
  editor.

## Phase 1 - Body Frame Contract

- [ ] Introduce an explicit runtime body frame value separate from scene root and
  hips local transform.
- [ ] Define whether the body frame is represented by hips, a pelvis/chest
  derived frame, or a future COM estimate for each Avatar profile.
- [ ] Store current and previous body position/rotation in normalized humanoid
  body space.
- [ ] Keep raw imported `RootT`/`RootQ` values separate from converted runtime
  body frame values.
- [ ] Make root/body setter APIs apply atomically after all scalar components for
  a sample have been evaluated, rather than depending on `Z` or `W` being the
  last animated component.
- [ ] Add reset and transition semantics for clip starts, clip switches, loops,
  and state-machine blend interruptions.
- [ ] Ensure body-frame storage does not allocate in per-frame animation ticks.

Acceptance criteria:

- [ ] Sampling a clip at a fixed time produces the same body frame regardless of
  scalar member evaluation order.
- [ ] Resetting or switching clips cannot leak the previous root/body baseline.

## Phase 2 - RootT/RootQ Import And Conversion

- [ ] Audit the current `RootT.*` component remap and scaling against Unity
  reference exports.
- [ ] Audit the current `RootQ.*` quaternion conversion, normalization, and sign
  continuity across adjacent keys.
- [ ] Preserve authored tangents and cadence for body/root curves the same way
  scalar transform curves are preserved.
- [ ] Add tests for clips that contain only partial `RootT` or `RootQ` channels.
- [ ] Add tests for loop seams, negative playback speed, and exact end-frame
  sampling.
- [ ] Add diagnostics when a Unity humanoid clip contains body/root curves but no
  mapped `HumanoidComponent` exists at runtime.

Acceptance criteria:

- [ ] Imported Unity root/body curves replay within tolerance against the Unity
  body pose fixture.
- [ ] Missing channels use stable defaults instead of retaining stale values.

## Phase 3 - Hip Application And Muscle Pose Composition

- [ ] Replace direct "RootT/RootQ writes hips immediately" behavior with a single
  composition step that combines bind pose, neutral pose, body/root delta, and
  muscle rotation in deterministic order.
- [ ] Define the exact composition order for hips: bind local transform, neutral
  offset, body translation, body rotation, hips-specific muscle channels if
  added later, IK/body offsets.
- [ ] Make torso, arm, leg, twist, and finger muscles evaluate relative to the
  current body frame.
- [ ] Validate that twist muscles distribute only through mapped twist/helper
  chains or the intended primary bone when helpers are absent.
- [ ] Add a configurable body-compensation policy for cases where IK/contact
  goals should move the hips/body to keep feet or hands stable.
- [ ] Keep the default policy aligned with Unity-like clip playback: body/root
  curves drive body motion, while ordinary limb muscles do not invent body
  translation by themselves.
- [ ] Recompute render matrices and skinning dirty state after the composed hip
  transform changes.

Acceptance criteria:

- [ ] Arm, leg, and twist muscle playback no longer fights or overwrites the
  imported body/root transform.
- [ ] Hips root-space translation and rotation match Unity fixture tolerance for
  the selected test clip.
- [ ] Twist/helper bones improve deformation without changing locomotion unless
  body-compensation policy requests it.

## Phase 4 - IK, Contacts, And Evaluation Order

- [ ] Define the humanoid evaluation order as: clip sampling, state/layer/mask
  blending, body-frame composition, muscle pose, procedural constraints,
  humanoid IK, root-motion extraction/publication, model-space solve, skinning.
- [ ] Make animated Unity IK goals body-relative and transform them through the
  composed body/hips frame.
- [ ] Ensure IK goals do not double-solve limbs already rewritten by a later
  custom rig pass.
- [ ] Add foot-contact and hand-goal tests that verify hips/body compensation is
  explicit and deterministic.
- [ ] Add a debug flag to show when IK moved the hips/body versus when clip
  body/root curves did.
- [ ] Ensure culling/offscreen animation policies do not skip required body/root
  updates for visible skinned output.

Acceptance criteria:

- [ ] Humanoid IK target playback uses the same body frame and scale as root/body
  motion.
- [ ] Contact compensation can be enabled, disabled, and tested without changing
  ordinary clip playback semantics.

## Phase 5 - Avatar Metadata And Retargeting Quality

- [ ] Promote the current name/spatial bone mapping into an explicit Avatar
  profile contract for required humanoid roles, optional roles, axis mappings,
  twist chains, stretch settings, and translation DoF.
- [ ] Persist or derive `restBodyFrame` for each avatar from mapped hips, spine,
  shoulders, legs, and feet.
- [ ] Validate required roles before importing a clip as Unity humanoid muscle
  data.
- [ ] Add warnings for ambiguous hips/spine/upper-leg mappings that would make
  body compensation unreliable.
- [ ] Add editor UX for inspecting body axes, hips body frame, raw RootT/RootQ,
  and final composed hips transform.
- [ ] Keep lookup tables dense for runtime use; avoid per-frame name/path search.

Acceptance criteria:

- [ ] Avatar setup explains why a rig can or cannot use humanoid body/root
  compensation.
- [ ] Runtime evaluation no longer relies on fragile string lookup in hot paths.

## Phase 6 - Diagnostics And Unity Parity Harness

- [ ] Extend `HumanoidPoseAuditReport` to include composed body frame, composed
  hips local transform, root-motion delta, IK body offset, and body-compensation
  source.
- [ ] Extend `HumanoidPoseAuditComparer` with per-bone local translation error
  and hips/body phase error.
- [ ] Add overlay visualization for body trajectory, root projection, hips local
  transform, foot contacts, IK goals, and twist-chain saturation.
- [ ] Add a minimal synthetic fixture where only `RootT`/`RootQ` changes.
- [ ] Add a minimal synthetic fixture where only upper-leg/arm twist changes.
- [ ] Add a fixture combining body motion, arm swing, leg swing, and foot IK.
- [ ] Make failures print the worst sample time, channel, expected value, actual
  value, and likely coordinate space.

Acceptance criteria:

- [ ] A failing parity run points to the body/root, muscle, IK, or mapping layer
  instead of just reporting a final pose mismatch.

## Phase 7 - Tests And Runtime Validation

- [ ] Add focused unit tests for atomic body-frame setters.
- [ ] Add unit tests for body frame reset on stop, deactivate, clip switch, and
  state-machine transition.
- [ ] Add tests for limb muscles evaluated relative to a rotated body frame.
- [ ] Add tests for hip/body compensation policy enabled and disabled.
- [ ] Add importer tests for `RootT`, `RootQ`, IK goal curves, partial curves,
  authored tangents, and loop seams.
- [ ] Add pose-audit parity tests using the compact Unity fixture.
- [ ] Add editor unit-testing world toggle or scenario for visual hip/body
  compensation playback.
- [ ] Run a narrow editor validation with the ImGui editor and the Unit Testing
  World after runtime changes land.

Acceptance criteria:

- [ ] Focused humanoid/importer tests pass.
- [ ] Unity reference comparison stays within agreed tolerances for body frame,
  hips root-space transform, limb endpoints, and raw muscle values.
- [ ] No new compiler warnings are introduced.

## Validation Commands

Expected focused commands:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Humanoid
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UnityAnimImporter
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~AnimationClipComponent
```

Broader validation after implementation:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj -- --unit-testing
```

## Final Validation And Merge

- [ ] Record before/after Unity pose-audit comparison numbers in this document
  or a linked validation note.
- [ ] Update `docs/user-guide/animation.md` if public humanoid/root-motion behavior or
  editor controls change.
- [ ] Update any unit-testing world settings/schema if new toggles are added.
- [ ] Merge branch `humanoid-body-root-compensation` back into `main` after
  implementation, validation, and documentation updates are complete.
