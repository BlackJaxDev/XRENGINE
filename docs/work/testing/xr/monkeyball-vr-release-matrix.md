# MonkeyBall VR Release Matrix

Last updated: 2026-07-28

This is the mandatory manual sign-off after the automated NativeAOT publish
and `--aot-smoke` gates pass. Record the package hash, GPU driver, runtime
version, headset firmware, and tester beside each result.

## Package Identity

- Git commit:
- Package path:
- SHA-256:
- Windows version:
- GPU / driver:

## SteamVR / OpenVR

| Device | Launch and present | Head pose | Stick tilt | Reset/pause | 90 Hz pacing | 20-minute comfort | Result / notes |
|---|---:|---:|---:|---:|---:|---:|---|
| Valve Index + Index controllers | Pending | Pending | Pending | Pending | Pending | Pending | |
| SteamVR-compatible headset + alternate controllers | Pending | Pending | Pending | Pending | Pending | Pending | |

## OpenXR

| Runtime / device | Launch and present | Head pose | Controller input | Reset/pause | 90 Hz pacing | Result / notes |
|---|---:|---:|---:|---:|---:|---|
| SteamVR OpenXR | Pending | Pending | Pending | Pending | Pending | |
| Windows/OpenXR runtime available to release QA | Pending | Pending | Pending | Pending | Pending | |

The game currently uses the explicit SteamVR action manifest for action-based
input. OpenXR rows validate runtime selection, presentation, tracking, and the
keyboard/gamepad fallback until equivalent OpenXR action bindings are added.

## Desktop And Recovery

- [ ] Keyboard tilt, reset, and pause work in the mirror window.
- [ ] XInput tilt, reset, and pause work.
- [ ] Removing `GameConfig.pak` produces a clear non-zero launch failure.
- [ ] Removing `GameContent.pak` produces a clear non-zero launch failure.
- [ ] Removing `CommonAssets.pak` produces a clear non-zero launch failure.
- [ ] Repeated win, loss, fall, pause, and restart cycles remain stable.
- [ ] No unexpected editor, loose-source, or authoring YAML files are present
  in the package.
- [ ] Logs contain no unhandled exception, GPU validation error, or sustained
  frame-pacing regression.

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md#game-runtime-smoke).

## Imported Checks

### From monkeyball-vr-final-build-runtime-todo.md

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Direct user sign-off of the repackaged ZIP | Run `Publish-VRMonkeyBall-NativeAOT-Package`. Extract `Samples/MonkeyBallVR/Build/Packages/MonkeyBallVR-win-x64.zip`. Play the desktop game: hold W/A/S/D and the arrow keys, press R, and watch the sun shadows. | The ball rolls under physics. Each key pair gives the same tilt. The stage pivots on the ball. The camera stays upright and follows the ball. R resets the ball. Moving shadows are visible. | Pending | 2026-07-29 (automated gates passed; user sign-off open) |

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/games/monkeyball-vr-final-build-runtime-todo.md`

- [ ] The repackaged ZIP receives direct user sign-off.
