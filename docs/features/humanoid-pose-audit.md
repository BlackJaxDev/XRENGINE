# Humanoid Pose Audit

Use this workflow to compare Unity Mecanim humanoid playback against XRENGINE at matching sample times.

## What it exports

Both Unity and XRENGINE write the same JSON shape:

- clip metadata: source, clip name, avatar name, duration, sample rate
- per-sample body pose: body position and body rotation
- per-sample muscles: Unity muscle names with normalized values
- per-sample bones: local rotation, root-space position, world position for major humanoid bones

The engine-side comparison report summarizes:

- body position error
- body rotation error in degrees
- per-muscle absolute error
- per-bone local rotation error in degrees
- per-bone root-space position error in meters

## Unity export

Copy [HumanoidPoseAuditExporter.cs](/d:/Documents/XRENGINE/Tools/Unity/HumanoidPoseAuditExporter.cs) into a Unity project, attach it to a humanoid avatar root, assign:

- `Animator`
- `Clip`
- `OutputPath`
- optional `SampleRateOverride`

Then run the component context menu action `Export Humanoid Pose Audit`.

The script samples a hidden clone through Mecanim using `AnimationClipPlayable`, with playable IK and foot IK disabled, so the exported reference reflects raw humanoid playback rather than scene-side solver overrides.

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

## Typical loop

1. Export the Unity reference JSON for the target avatar and clip.
2. Set `HumanoidPoseAuditReferencePath` to that JSON in the unit-test world.
3. Run the editor unit-test world with the same `.anim` clip.
4. Inspect the generated comparison report to see whether the drift is in muscles, local limb rotations, or root-space endpoints.
