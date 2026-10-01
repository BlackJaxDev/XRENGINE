# OpenXR body calibration and spectator validation

This procedure qualifies the integrated local-player behavior on a named hardware and runtime configuration. The [integration guide](../../../developer-guides/vr/openxr-body-tracking.md) describes the implementation; the [current local evidence](../../investigations/avatar/openxr-calibration-spectator-validation-2026-10-01.md) records the available validation without connected XR devices. Software tests and renderer compilation do not count as hardware acceptance.

## Record the configuration and evidence

For each run, record the implementation commit; headset, controller and tracker models and tracker count; GPU and driver; Windows version; SteamVR and OpenXR runtime versions; active runtime manifest; tracker extension revision; graphics backend; stereo view mode; and any application settings that affect calibration or spectator output. Keep logs, normalized smoke summaries, test results, and short visual evidence with the run. Record failures and unsupported behavior by configuration. Update the [SteamVR hardware record](../openxr-steamvr-hardware-validation.md) with the same configuration and findings.

Run the repository's SteamVR OpenXR smoke runner with the active runtime. Select `SequentialViews` in the existing rendering setting for the initial isolation pass; it is not a smoke-runner flag. Repeat the primary Vulkan lane and the separate OpenGL diagnostic lane:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer Vulkan
powershell -ExecutionPolicy Bypass -File Tools\OpenXR\Run-OpenXrSteamVrSmoke.ps1 -UseActiveRuntime -Renderer OpenGL
```

Explicit `VR.Mode=OpenXR` must expose failure rather than silently switching to OpenVR. Use the [OpenXR runtime guide](../../../developer-guides/vr/openxr-runtime.md) for runtime and loader diagnostics. Consume the existing headset sequential and single-pass stereo evidence where it matches the tested configuration; otherwise record the missing lane.

## Tracker transport decision

Start with all intended trackers connected. On SteamVR's OpenXR runtime, capture extension revision, enumeration, action bindings, current pose activity and validity, identity, and sample times for every tracker. Repeat with default and duplicate SteamVR role mappings, a disabled tracker, occlusion, power cycling, and reconnection. Check that each physical tracker appears once, streams through its persistent-path subaction, and keeps its identity across role changes. A nonempty enumeration alone is insufficient evidence of pose streaming. A tracker discovered after action attachment must report that a player-initiated VR restart is required.

If any connected tracker cannot stream independent of its role mapping, record the runtime/version and exact enumeration, binding, and pose failure. Propose a separately reviewed SteamVR tracker provider only after establishing that blocker. Such a provider would need explicit reference-space, timestamp, identity, and lifecycle reconciliation while headset rendering and controller input remain on OpenXR. Do not silently switch providers or ask players to assign body roles.

## Calibration and body behavior

On every available supported controller interaction profile, exercise open, head-tilt refusal, stationary capture with both triggers or both select buttons, cancel, retry, and recalibration. Confirm each gesture is available while the desktop editor camera has focus, requires no keyboard, and has no mute side effect. Check that eye-level messages, tracker labels, markers, footprints, and the T-pose are readable from the headset. Missing or stale headset/controller samples must refuse capture and leave the last committed rig intact. A changed avatar must request recalibration.

Use a six-point configuration and the largest available tracker set, up to eleven points. Include trackers in their default SteamVR state and trackers swapped between sessions. Confirm proximity assigns each tracker to at most one slot and leaves distant extras unassigned; crossing feet after capture must not rebind them. Check head, hand, hip, foot, chest, upper-arm, and knee motion, repeated calibration, and the absence of origin snaps, duplicate targets, or accumulated offsets. Compare standing, independent head glances, body turns, crouching, either foot lifted, and room-scale walking. Height and arm-span settings must drive avatar scale without changing tracking-space meters; an unset or implausible measurement must present a notice, not infer a new value.

Qualify the grip-to-wrist preset on each physical controller profile against the avatar's wrist placement. Record the controller model, interaction-profile path, measured grip-to-wrist translation and rotation, the source of that measurement, and observed hand alignment across several wrist poses. The currently shared geometric default is an unqualified estimate; active-profile selection and overrides alone do not qualify a hardware preset.

Exercise tracker occlusion, loss, recovery, power cycling, and a different physical tracker replacing a bound one. The last target may hold briefly and fade, but it must not snap to the origin or silently inherit the replacement. Exercise controller loss, headset removal, dashboard focus, recenter, teleport, runtime/session restart, and a change of avatar. Verify current validity, input routing, event resets, and explicit recalibration when the reference-space relationship is unknown.

Toggle OpenXR off and on in the editor and recreate the VR pawn. Record whether provider generation and reference-space basis are unchanged. Restore committed body bindings and offsets only when the same avatar, measurement, provider generation, reference basis, and physical tracker identities still match. Missing trackers leave their slots empty; another tracker must never inherit one. If the relationship changed or cannot be established, require recalibration and record that outcome. A successful same-generation synthetic restore does not establish hardware toggle continuity.

## Spectator image and performance

With headset rendering active, inspect the first-person headset image and third-person desktop or completed render-texture output together. Check full-avatar visibility, follow direction and body yaw independent of head glances, obstruction recovery, teleport and camera-mode resets, output orientation, aspect ratio, color space, post-processing, and per-view visibility. Check Vulkan and OpenGL separately. Verify that switching desktop cameras, changing spectator cadence, acquiring or holding a completed output lease, and desktop focus never take VR input possession, change the tracking origin, alter eye cameras, or move the headset audio listener. A slower spectator cadence may reuse a completed frame but must not delay headset submission.

On the named system, measure headset frame time, missed deadlines, allocations, and spectator CPU/GPU cost with the spectator disabled and enabled. Record measured values and scene settings; source inspection or synthetic fence tests alone do not establish performance. The acceptance result requires the complete named configuration, behavioral observations, logs, and visual evidence, with each untested item stated explicitly.

An encoder or external capture integration is optional. If added, separately validate bounded queues, GPU readback lifetime, frame timestamps, audio alignment, and behavior when encoding falls behind; a render texture alone is not a video recording. An eye-level spectator mirror during calibration is also optional and needs headset visual and input validation if added.
