# Humanoid Pose Audit

Use this workflow to compare Unity Mecanim humanoid playback against XRENGINE at matching sample times.

## What it exports

Both Unity and XRENGINE write the same core JSON shape:

- clip metadata: source, clip name, avatar name, duration, sample rate
- per-sample body pose: Unity-style body center pose (`HumanPose.bodyPosition` / `bodyRotation` on the Unity side, `RootT` / `RootQ` sample state on the engine side)
- per-sample muscles: Unity `HumanTrait.MuscleName` channel names with normalized values
- per-sample bones: local rotation, root-space position, world position for major humanoid bones

Unity exports two additional audit layers:

- top-level `MuscleDefaultRanges`: Unity `HumanTrait` default min/max values for every muscle
- top-level `DefaultMusclePose`: Unity's zero-muscle humanoid pose sampled from `HumanPoseHandler`, including per-bone bind-relative local rotations
- per-sample `RawCurves`: editor-evaluated float bindings from `AnimationUtility.GetCurveBindings`, including humanoid channels, `RootT` / `RootQ`, IK goal curves, and any other float curves on the clip

The engine-side comparison report summarizes:

- body position error
- body rotation error in degrees
- per-muscle absolute error
- per-bone local rotation error in degrees
- per-bone root-space position error in meters

The comparer canonicalizes equivalent Unity naming variants, so reports generated from older engine exports that used `.anim` curve attribute names such as `LeftHand.Index.Spread` can still be compared against Unity exports that use `Left Index Spread`.

## Unity export

Copy [HumanoidPoseAuditExporter.cs](/d:/Documents/XRENGINE/Tools/Unity/HumanoidPoseAuditExporter.cs) into a Unity project, attach it to a humanoid avatar root, assign:

- `Animator`
- `Clip`
- `OutputPath`
- optional `SampleRateOverride`

Then run the component context menu action `Export Humanoid Pose Audit`.

The script samples a hidden clone through Mecanim using `AnimationClipPlayable`, with playable IK and foot IK disabled, so the exported reference reflects raw humanoid playback rather than scene-side solver overrides.

The same export also records the clip's raw float bindings directly from Unity's editor curve API. That is the key diagnostic layer when the engine appears to match the `.anim` data but not Unity's sampled `HumanPose`: it lets you compare raw clip values and post-solve `HumanPose` values side by side in one JSON file.

The export also captures Unity's neutral humanoid base pose: the pose produced when every humanoid muscle is zero. This matters because Mecanim muscle playback is defined relative to that neutral pose, not necessarily the imported bind pose or authored T-pose.

## XRENGINE export and compare

Configure the unit-test world in [UnitTestingWorldSettings.json](/d:/Documents/XRENGINE/Assets/UnitTestingWorldSettings.json):

- `HumanoidPoseAuditEnabled`
- `HumanoidPoseAuditOutputPath`
- optional `HumanoidPoseAuditReferencePath`
- optional `HumanoidPoseAuditComparisonOutputPath`
- optional `HumanoidPoseAuditSampleRateOverride`

When enabled, the unit-test world attaches `HumanoidPoseAuditComponent` to the imported avatar root. On the first late tick after the `.anim` clip is ready, it:

1. evaluates the clip at deterministic sample times with `AnimationClipComponent.EvaluateAtTime`
2. forces the humanoid muscle pose to apply immediately
3. writes the XRENGINE export JSON
4. optionally compares it against the Unity reference JSON and writes a comparison report

If you want runtime muscle playback to use Unity's neutral humanoid base pose, populate `HumanoidNeutralPosePresets` from the Unity export script output and apply the desired `HumanoidComponent.NeutralPosePreset`. The component resolves Unity HumanBodyBones-style names onto the mapped avatar bones and stores per-bone neutral bind-relative rotations in `HumanoidSettings.NeutralPoseBoneRotations`. Subsequent muscle application composes muscle deltas on top of those neutral rotations instead of directly on top of bind pose.

## Typical loop

1. Export the Unity reference JSON for the target avatar and clip.
2. Set `HumanoidPoseAuditReferencePath` to that JSON in the unit-test world.
3. Run the editor unit-test world with the same `.anim` clip.
4. Inspect the generated comparison report to see whether the drift is in muscles, local limb rotations, or root-space endpoints.

## Live overlay

For in-editor visual comparison, add `HumanoidPoseAuditOverlayComponent` next to the avatar's `HumanoidComponent` and `AnimationClipComponent`, then assign the Unity reference JSON to `ReferencePath`.

At runtime the overlay:

- finds the nearest Unity audit sample for the current `AnimationClipComponent.PlaybackTime`
- reconstructs the Unity bone markers in the avatar's current root space from the audit `RootSpacePosition` data
- draws cyan reference points and a blue reference skeleton
- draws white actual engine bone points and colored error lines from engine bone positions to Unity reference positions

This is the fastest way to tell whether the remaining error is global or per-bone:

- if nearly every bone is offset in the same direction, suspect importer-space conversion or root-motion/body-space mismatch
- if only shoulders, upper legs, knees, or feet diverge, suspect bind-pose axis mapping or limb basis detection

Important: Unity humanoid `.anim` float curves are not guaranteed to equal the post-solve values returned by `HumanPose.muscles`. If the engine export matches the Unity `RawCurves` layer but still diverges from the Unity `Muscles` layer, the remaining issue is upstream of bone application: the imported humanoid channels are not yet in the same semantic space as Unity's sampled `HumanPose`.

Also important: zero muscle values do not imply bind pose. A consistent whole-limb offset with otherwise correct curve motion is usually a sign that the runtime is applying muscles from bind pose instead of Unity's neutral humanoid pose.
