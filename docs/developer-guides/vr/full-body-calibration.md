# Local VR body calibration

The local player owns raw tracking sources separately from the solver's concrete body targets. A calibrated target has one offset, stored on its owned child transform. In the engine's row-vector convention:

`targetWorld = deviceToTargetOffset * deviceWorld`

The same component owns eleven slots: head, hands, hips, chest, feet, upper arms and knees. The serialized names `LeftElbow` and `RightElbow` mean upper-arm tracker poses; they are not positional elbow hints. Chest and upper-arm orientation constraints yield to reachable head, hip, wrist and foot endpoints. See the [solver investigation](../../work/investigations/avatar/tracked-body-solver-2026-09-30.md).

## Configure an avatar

`BootstrapVrAvatarFactory.ConfigureImportedAvatar` is the shared setup path for an already imported humanoid. The unit-testing world's imported-avatar path uses it. New character and flying VR pawns can instantiate the explicitly selected `UserSettings.VrAvatarPrefabPath`. The prefab must have a mapped root `HumanoidComponent`; existing scene avatars are never silently cloned or taken from another owner.

An unset prefab leaves headset/controller input available and reports that an avatar must be selected. Choose either that configured prefab or the legacy animated-model import for the player, rather than importing two player avatars. No default avatar asset is fabricated.

Enter standing height or fingertip arm span in the persisted [body measurement settings](body-measurements.md). Unset or invalid values are reported; calibration does not derive scale from the headset. Avatar scaling does not scale tracking-space meters or IPD.

## Calibrate entirely in VR

1. Open calibration with the dedicated controller binding. No automatic calibration starts on activation, and mute never opens or captures calibration
2. Stand inside the canonical T-pose. Live markers show the one-to-one proximity assignment; distant extra trackers remain unassigned. Match the floor footprints and keep the head level
3. Hold still briefly, then pull both triggers together. Simple controllers use both select buttons. The headset, controllers and selected trackers must have one valid coherent sample
4. Cancel to restore the previous source tuples, target identities, poses and solver activation without running another calibration

Controller bindings:

| Profile | Open | Cancel | Capture |
|---|---|---|---|
| Valve Index | Left A | Right B | Both triggers |
| Oculus Touch | Left Y | Right B | Both triggers |
| HTC Vive | Left menu | Right menu | Both triggers |
| Microsoft Motion | Left menu | Right menu | Both triggers |
| Khronos simple | Left menu | Right menu | Both select buttons |

Vive/Motion quick menu uses left trackpad click, so it does not also open calibration. Calibration action handlers use press edges and register independently of desktop pawn possession. Their requests execute on the simulation owner. An eye-level world canvas shows status and refusal reasons without requiring the player to look down. Actual controller ergonomics and headset readability still require hardware acceptance.

The stationary window defaults to 0.15 seconds, permits at most a 0.25-second sample gap, and checks every selected device at 0.15 m/s and 15 degrees/s. The exact copied publication used for capture must end that window. A stale, repeated, invalid, fast-moving or changed-identity sample cannot manufacture a stationary interval. `HeadTiltToleranceDegrees` defaults to 10 degrees and does not reject ordinary yaw.

## Head and controller offsets

The head offset comes from the avatar's eye geometry and head bind orientation, independent of the headset orientation at capture. Rotated head bones are covered by an eye-position reconstruction regression.

Active OpenXR interaction-profile paths are cached at input/profile discovery. `VrControllerWristOffsets` maps profile paths to per-player grip-to-wrist translations. Explicit `LeftControllerOffset`/`RightControllerOffset` matrices remain available for application overrides. The five supported profiles currently share an explicitly **unqualified geometric starting estimate**: wrist 6 cm behind and 2 cm below the palm grip, in meters. This is an engine geometry assumption, not vendor-provided or measured hardware calibration. Invalid overrides are rejected in favor of that labeled default. Device-specific tuning remains open.

Feet and body trackers use the displayed bone pose exactly; there is no foot-yaw inference, mount-orientation assumption, forced foot height or calibration-time scale multiplier. Legacy settings for those removed paths were removed after a repository search found no serialized instances.

## Frame and coordinate contract

1. The runtime publishes predicted poses in reference-space meters, with current validity, session generation, reference-space version, sample time and snapshot ID
2. The scene owner reconciles discovered physical identities. The player copies one publication into reusable storage and freezes all its raw predicted device sources for the simulation tick
3. Playspace/locomotion uses explicit rig references and calibrated target positions. The device-to-body offset is applied once
4. Animation restores/evaluates its base pose. Late IK consumes stable calibrated children and current source weights, then publishes the solved pose
5. Spectator follow runs after late animation using the same calibrated body state

Runtime reference space is transformed by the playspace into world space. Avatar root scale belongs only to the measurement component. Raw device transforms remain metric VR transforms. The target child stores the sole device-to-target offset. The rendered skeleton deliberately uses the simulation pose; late-located headset/controller render transforms are independent, and no render-thread callback writes live avatar bones.

During a temporary loss, targets retain the last usable device pose, hold for 0.1 seconds and fade to zero over 0.2 seconds by default. The same physical source fades back. A changed identity or provider generation is rejected immediately rather than inheriting the previous mount offset. Retained transforms are never accepted as calibration samples. The per-slot source contract includes tracker, estimator and none; estimator generation remains a separate feature.

`RuntimeVrDiscontinuityServices` shares teleport, snap-turn, recenter, avatar, session and camera-mode events. Existing character-controller reset teleports forward through the local player's movement event. Optional `VRPlayerInputSet.SnapTurningEnabled` uses a configurable 45-degree default and publishes after applying body yaw; continuous turning remains the default. Games performing their own teleport/snap-turn writes must publish the same event after applying the new basis. Unknown recenter/session relationships require recalibration.

## Spectator and continuity

`SpectatorEnabled` enables independent follow. By default it selects the spectator camera on the local desktop viewport only, retaining the previous camera, including an empty viewport. Disabling/replacing it restores the exact prior selection only if the spectator still owns that selection. It never changes pawn possession, controller routing or audio ownership. `ShowSpectatorOnDesktop=false` leaves the offscreen producer enabled instead. See [spectator controls and output ownership](spectator-camera.md).

The [session store](calibration-session-continuity.md) retains only body binding identities and offsets outside temporary rigs. Head/controller fixed geometry is recreated using current configuration. Same-session, same-avatar, same-measurement, same-reference-basis rig recreation restores exact physical IDs; missing body trackers remain empty and can be restored when those same IDs reappear. Unknown provider-generation/reference-space transitions still require recalibration. This does not certify an actual VR off/on toggle across a new OpenXR session.

## Qualification

See the [implementation and validation record](../../work/progress/avatar/openxr-calibration-spectator-implementation-2026-09-30.md). Headless tests and software Vulkan checks do not qualify a headset, SteamVR role-independent tracker streaming, actual spectator visuals, controller ergonomics, or frame-deadline cost.
