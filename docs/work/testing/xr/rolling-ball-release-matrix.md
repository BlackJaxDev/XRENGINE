# Rolling Ball Release Matrix

Scope: Validate the packaged Rolling Ball game after automated NativeAOT publish and `--aot-smoke` gates pass. This matrix covers package identity, desktop recovery, SteamVR/OpenVR, OpenXR fallback behavior, and final user sign-off.

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md#game-runtime-smoke), [Rolling Ball README](../../../../Samples/RollingBall/README.md). Code todos: none.

## Setup

Use `Build-RollingBall-GenerateGameProject` to generate the game project. Use `Build-RollingBall-CookGameExe` to cook the game executable. Use `Publish-RollingBall-NativeAOT-Package` to produce the packaged ZIP. Record the git commit, package path, SHA-256, Windows version, GPU and driver, runtime version, headset firmware, tester, and package hash beside each result. Do not record local user profile paths.

## Checks

### Package identity

Architecture: [AOT Final Game Builds](../../../developer-guides/runtime/aot-final-game-builds.md#game-runtime-smoke).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Package metadata | Publish the package and record commit, package path, SHA-256, Windows version, GPU, driver, runtime version, headset firmware, and tester. | The package has complete identity data for release comparison. | Open | Last evidence: none. |
| No authoring files | Inspect the package contents. | No unexpected editor, loose-source, or authoring YAML files are present in the package. | Open | Last evidence: none. |
| Missing `GameConfig.pak` recovery | Remove `GameConfig.pak` and launch the package. | The game exits with a clear non-zero launch failure. | Open | Last evidence: none. |
| Missing `GameContent.pak` recovery | Remove `GameContent.pak` and launch the package. | The game exits with a clear non-zero launch failure. | Open | Last evidence: none. |
| Missing `CommonAssets.pak` recovery | Remove `CommonAssets.pak` and launch the package. | The game exits with a clear non-zero launch failure. | Open | Last evidence: none. |
| Log cleanliness | Run the packaged game through the release smoke path. | Logs contain no unhandled exception, GPU validation error, or sustained frame-pacing regression. | Open | Last evidence: none. |

### Desktop play and recovery

Architecture: [Rolling Ball README](../../../../Samples/RollingBall/README.md).

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Keyboard controls | In the mirror window, hold W/A/S/D and arrow keys, then press R and pause. | Keyboard tilt, reset, and pause work. The ball rolls under physics, the stage pivots on the ball, and the camera stays upright and follows the ball. | Open | 2026-07-29 automated gates passed; user sign-off remained open. |
| XInput controls | Use an XInput controller for tilt, reset, and pause. | XInput tilt, reset, and pause work. | Open | Last evidence: none. |
| Repeated recovery cycles | Repeat win, loss, fall, pause, and restart cycles. | The game remains stable across repeated cycles. | Open | Last evidence: none. |
| Moving shadows | Play the desktop game and observe the sun shadows. | Moving shadows are visible. | Open | 2026-07-29 automated gates passed; user sign-off remained open. |
| Direct user sign-off | Extract the published ZIP and play the desktop game. | The user accepts the repackaged ZIP directly. | Open | 2026-07-29 automated gates passed; user sign-off remained open. |

### SteamVR and OpenVR

Architecture: [Rolling Ball README](../../../../Samples/RollingBall/README.md).

| Device | Launch and present | Head pose | Stick tilt | Reset/pause | 90 Hz pacing | 20-minute comfort | Result / notes |
|---|---:|---:|---:|---:|---:|---:|---|
| Valve Index with Index controllers | Pending | Pending | Pending | Pending | Pending | Pending | |
| SteamVR-compatible headset with alternate controllers | Pending | Pending | Pending | Pending | Pending | Pending | |

### OpenXR runtime fallback

Architecture: [OpenXR Runtime](../../../developer-guides/vr/openxr-runtime.md).

OpenXR rows validate runtime selection, presentation, tracking, and keyboard/gamepad fallback until equivalent OpenXR action bindings are added. The game currently uses the explicit SteamVR action manifest for action-based input.

| Runtime / device | Launch and present | Head pose | Controller input | Reset/pause | 90 Hz pacing | Result / notes |
|---|---:|---:|---:|---:|---:|---|
| SteamVR OpenXR | Pending | Pending | Pending | Pending | Pending | |
| Windows/OpenXR runtime available to release QA | Pending | Pending | Pending | Pending | Pending | |

## Hardware Matrix

| Runtime or device | Purpose | Status |
|---|---|---|
| Valve Index with Index controllers | Primary SteamVR/OpenVR sign-off. | Pending |
| SteamVR-compatible headset with alternate controllers | Alternate controller sign-off. | Pending |
| SteamVR OpenXR | OpenXR fallback and presentation sign-off. | Pending |
| Windows/OpenXR runtime available to release QA | Vendor-runtime fallback sign-off. | Pending |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
