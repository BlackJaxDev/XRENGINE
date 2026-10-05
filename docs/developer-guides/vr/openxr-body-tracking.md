# OpenXR body tracking and spectator integration

The local VR player uses OpenXR for headset rendering and controller input. A headset, two controllers, and up to eight body trackers can drive eleven avatar targets: head, hands, hips, chest, feet, upper arms, and knees. Body trackers bind to slots by proximity during calibration. The player never needs to assign SteamVR body roles. When a slot has no usable tracker, its weight fades to zero. The source contract reserves estimator input, but this implementation does not generate estimated body poses for missing trackers.

The [OpenXR runtime guide](openxr-runtime.md) covers runtime selection, tracker transport, pose validity, smoke diagnostics, and the late connection policy. The [calibration guide](full-body-calibration.md) covers avatar setup, controller actions, capture, rollback, offsets, and frame order. [Body measurements](body-measurements.md) own avatar scale. The [spectator guide](spectator-camera.md) covers follow, visibility, desktop routing, output leases, and capture cadence. [Session continuity](calibration-session-continuity.md) describes the memory-only restoration contract.

## Ownership and coordinate contract

The runtime supplies current poses and a session-scoped physical tracker identity. A successful calibration alone assigns that identity to a body slot; neither a transport role nor collection order assigns a body part. The player copies one coherent predicted pose publication for the simulation tick. A single rig owner maintains stable concrete IK targets separately from raw VR devices. Each target stores one device-to-target offset in the engine's row-vector convention:

`targetWorld = deviceToTargetOffset * deviceWorld`

Trackers are matched one-to-one at capture, with a scale-aware distance cutoff. Upper-arm and knee candidates are measured against limb segments. Binding remains fixed until recalibration. A disconnected source briefly holds its last valid pose, then fades; only the same physical identity in the same provider generation can resume that binding. Retained poses never qualify for a new capture. Tracking-space meters remain separate from the player's measurement-driven avatar scale.

Calibration is a scene-owner transaction: the player previews a canonical T-pose with tracker labels and footprints, captures from a stationary coherent sample, or cancels back to the previous rig. Head geometry defines the eye offset. Controller grip-to-wrist offsets are selected by interaction profile; the shared defaults remain unqualified geometric estimates until measured on hardware. Chest, upper-arm, and knee constraints supplement the existing head, hand, hip, and foot endpoints. See the [calibration guide](full-body-calibration.md) for the player flow and priority rules.

The spectator is an independently owned monoscopic camera and output. Its follow state reads the calibrated body state after late animation, and its completed texture is leased to consumers. Desktop selection does not change headset cameras, tracking origin, VR input possession, or the headset audio listener. A recording encoder and calibration mirror are separate optional features.

## Validation status

The [software implementation record](../../work/progress/avatar/openxr-calibration-spectator-implementation-2026-09-30.md) documents the headless regression and software Vulkan results. These establish the implemented contracts but do not certify physical SteamVR tracker streaming, headset calibration controls, controller offset accuracy, spectator image quality, headset frame deadlines, or an OpenXR off/on toggle. Unknown provider generation or reference-space changes require recalibration rather than guessed rebasing.

Use the [hardware acceptance procedure](../../work/testing/avatar/openxr-calibration-spectator-validation.md) to qualify a named runtime, device set, graphics backend, and implementation commit. The [current validation evidence](../../work/investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md) records what this local pass could actually exercise; no headset, controllers, or body trackers were connected.
