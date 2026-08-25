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
XZ, Y, or yaw. Each channel is enabled only when the clip import policy and the
loaded avatar calibration support it; unsupported or mismatched calibration is
reported as an invalid channel instead of a guessed value.

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
clip projection metadata, while explicit target application remains owned by a
direct `AnimationClipComponent`.

## Authored Humanoid IK Goals

Animation-driven foot and hand goals require a `HumanoidIKSolverComponent` and
are calibration-gated by default. `EHumanoidIKGoalPolicy` can instead ignore
all authored goals or apply them without requiring calibration. Runtime
diagnostics identify goals that were ignored, skipped as uncalibrated, applied
as authored, or applied with contact correction.

Contact correction is an explicit post-pose option. It can be disabled, clamp
feet to a configured ground plane, or clamp feet and hands. Disabling it
preserves the authored body-relative goal exactly; it never changes the Body or
projected-root calculation.

## Deeper Docs

- [Animation API](../developer-guides/animation/animation-api.md)
- [VR Development](vr-development.md)
- [Component System](components.md)
