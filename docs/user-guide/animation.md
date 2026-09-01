# Animation

[Back to user guide](README.md)

Use animation features to drive clips, state machines, humanoid rigs, blendshapes, and IK targets. For the runtime system and code-facing details, see [Animation API](../developer-guides/animation/animation-api.md).

## Common Workflows

- Play a single authored clip with an animation clip component.
- Drive gameplay or character state through an animation state machine.
- Use humanoid components to map a skeleton, apply neutral poses, and expose IK targets.
- Use VR IK components to map headset, hand, and tracker poses onto a character.
- Use blendshape parameters for face tracking or procedural expressions.

## Authoring Notes

Animation assets are reusable. Clips can target arbitrary properties, not only skeletal transforms. Blend trees and state machines combine clips and procedural values with parameter-driven transitions.

For editor clip inspection, use the animation clip editor panel where available.

## Humanoid Projected Root Output

Imported Unity humanoid clips expose a projected root pose through
`HumanoidComponent.CurrentProjectedRootPose` and its consecutive change through
`CurrentRootMotionDelta`. Check `EHumanoidProjectedRootChannels` before reading
XZ, Y, or yaw. Each channel is enabled only when the clip import policy and
finalized avatar definition support it; unsupported or mismatched avatar data
is rejected instead of guessed.

`AnimationClipComponent.RootMotionApplicationMode` controls ownership:

- `ExtractOnly` leaves placement unchanged while exposing the values;
- `ApplyToExplicitTarget` applies the unwrapped projected pose relative to the
  named `RootMotionTarget` pose captured for the playback epoch;
- `ExternalConsumer` publishes `RootMotionEvaluated` and leaves placement to
  the subscriber.

The runtime never guesses a Hips, Armature, or scene-root target. Looping uses a
cached endpoint transform and signed cycle count, so forward and reverse loops
accumulate separately from the canonical-relative within-cycle pose. Playback
starts, exact seeks, and other discontinuities begin a new temporal epoch.
State-machine Body samples use quaternion-aware RootQ blending and compatible
clip projection metadata. Explicit target application remains an explicit
`AnimationClipComponent` choice; state-machine output can instead be consumed
through the same projected-root publication path.

## Humanoid Body Frame and Avatar Data

Humanoid muscles do not treat Hips as the Body pivot. The final muscle pose can
move the skeleton's weighted center and change its hip/shoulder orientation.
XRENGINE recenters and reorients Hips in component/model-root space so the
requested Body frame is retained. This compensation is part of pose solving;
it is not scene locomotion, physics motion, contact correction, or a new
RootT/RootQ sample.

Avatar definitions now contain explicit `BodyDefinition` data: a model ID,
algorithm version, weighted skeletal segments, and orientation landmarks. If
an older finalized definition lacks this additive data, refresh the avatar
definition and review/reconfirm it before playback. Runtime playback never
silently fabricates missing Body data. The supplied
`XRE.PublicHumanoidMassHierarchy.v1` preset is the ratified public-hierarchy
model for the focused Unity 2022.3 contract. It does not claim private Mecanim
constants or bit-exact parity.

The Body solve applies to all 95 muscles, not a named subset of arm or torso
controls. Some controls can leave the mass center unchanged. The current
acceptance record includes fresh Unity comparisons and still reports differences
in the default mass model and native joint poses; a successful native solve is
not yet proof that a character matches Unity.

`CurrentBodyFrameDiagnostic` describes the last successfully committed pre-IK
Body solve, including provisional, requested, compensated, and final Hips
data. It is useful for inspection and troubleshooting; a rejected frame leaves
this diagnostic and the previously committed pose/root intact.

## Authored Humanoid IK Goals

Animation-driven foot and hand goals require a `HumanoidIKSolverComponent` and
are calibration-gated by default. `EHumanoidIKGoalPolicy` can instead ignore
all authored goals or apply them without requiring calibration. Runtime
diagnostics identify goals that were ignored, skipped as uncalibrated, applied
as authored, or applied with contact correction.

For accepted imported humanoid frames, processing is atomic: final blended
muscles and translation DoF are solved through FK, the Body is aligned and
projected, the pose/root result is committed, and then authored IK consumes the
committed Body frame rather than treating Hips as Body. A rejected input leaves
the prior pose, root output, and IK frame in place.

Contact correction is an explicit post-pose option. It can be disabled, clamp
feet to a configured ground plane, or clamp feet and hands. Disabling it
preserves the authored body-relative goal exactly; it never changes the Body or
projected-root calculation.

## Deeper Docs

- [Animation API](../developer-guides/animation/animation-api.md)
- [Phase 9A Body-frame investigation](../work/investigations/avatar/humanoid-body-frame-compensation-2026-08-31.md)
- [VR Development](vr-development.md)
- [Component System](components.md)
