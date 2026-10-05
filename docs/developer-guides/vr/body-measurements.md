# Player body measurements and avatar scale

Body measurements are persisted on the player's `UserSettings` asset, not on the unit-testing world. Set them through the user-settings inspector or the application's player-settings UI. They are independent of tracker identity, tracker roles, and headset pose.

| Property | Default | Contract |
|---|---|---|
| `BodyMeasurementMode` | `Height` | `Height` or `ArmSpan` |
| `PlayerHeight` | Unset | Standing height in meters; supported range 0.5–2.75 m |
| `PlayerArmSpan` | Unset | Fingertip-to-fingertip span with horizontal arms; supported range 0.5–3.5 m |
| `StandingHeightToEyeHeightRatio` | 0.936 | Configurable approximation, range 0.8–1.0; not a measurement of the individual |
| `HeightCalibrationWarningToleranceMeters` | 0.12 | Finite range 0.01–0.5 m; warning only, never updates the entered value |

`VRHeightScaleComponent` is the single avatar-scale owner. It reads the selected explicit measurement and applies an absolute uniform scale to the avatar root:

- Height: entered standing height × eye-height ratio ÷ canonical model eye height
- Arm span: entered arm span ÷ canonical model arm span

Canonical geometry is measured in unscaled model-root units from the captured bind hierarchy, independent of live pose, current root scale, and tracking-space scale. The avatar root is the authored floor origin for eye-height measurement. Invalid or missing settings, non-finite geometry, and invalid denominators refuse the update and return a specific notice. There is no silent 1.8-meter default. Changing a measurement queues a scene-owner update rather than writing transforms from a settings/UI callback; repeating an update never multiplies the previously applied scale.

Calibration and IK must not write avatar scale. Tracker and headset coordinates remain in the runtime's metric basis; scaling the avatar does not modify the playspace or IPD. A capsule's explicit `FootTransform` can be repositioned by the scale owner, but an arbitrary parent is never inferred to be that foot transform.

## Canonical pose and arm span

`SetCanonicalCalibrationPose` restores bind geometry and straightens mapped arm segments into a horizontal T-pose, including A-pose imports. The canonical measurement adds the shoulder span, straightened shoulder/arm/forearm lengths, and longest wrist-to-fingertip chain for each hand. It does not mistake the narrower width of an A-pose for arm span.

A mapped distal finger's named `tip` or `end` child supplies its endpoint. If that endpoint is absent, the remaining fingertip length is estimated as 70% of the intermediate-to-distal segment. If complete finger chains are absent, each hand is estimated as 11% of model eye height beyond the wrist. These are explicit heuristics: `AvatarBodyMeasurements.UsesEstimatedHandLength` and `Notice` report them. They are not claimed as measured hand geometry.

Eye offsets derived from weighted eye vertices are converted into head-local coordinates before scaling; non-origin or rotated avatar placement does not become part of the offset. Empty eye-geometry samples cannot divide by zero.

## Calibration feedback

`IRuntimeVrHeightScaleComponent.TryApplyPlayerMeasurements(out notice)` reports unset or invalid measurements and any arm-span estimate. The calibration flow must present the notice rather than silently replacing the player's setting.

In height mode, `GetCaptureMeasurementWarning(trackedEyeHeightMeters)` compares floor-relative headset eye height against entered standing height × ratio. A disagreement warns the player to stand straight and check the measurement/floor origin. The setting is never inferred from the headset or corrected automatically. Arm-span mode does not use this height warning.

The headless `VrBodyMeasurementTests` cover persistence, unset/range/finite checks, both scaling formulas, height warnings, A-pose normalization, fingertip and estimated-hand measurements, repeat scaling, setting changes, and preservation of the tracking parent. These deterministic checks do not certify headset floor calibration or a real avatar's eye/hand proportions.
