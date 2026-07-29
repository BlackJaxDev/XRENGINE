# MonkeyBall VR

MonkeyBall VR is the shipping-path sample for XRENGINE. It is a small, complete
arcade game rather than an editor-only scene: tilt the course, guide the ball
across the bridge, avoid the bumpers, and reach the goal before time or lives
run out.

## Gameplay

- Native PhysX simulation at 90 Hz: a dynamic spherical ball rolls against a
  kinematic compound course collider, with damping, speed limiting, bumpers,
  falling, lives, score, timer, pause, win, loss, and restart.
- VR headset/controller rig with a desktop mirror camera.
- Upright desktop follow camera parented to the ball, with smoothed yaw tracking
  toward the ball's velocity.
- SteamVR action manifest and generated Valve Index controller binding.
- Keyboard and gamepad controls for desktop development and QA.
- Asset-authored course, ball, camera, player rig, directional light, and HUD
  hierarchy saved in `Assets/Worlds/MonkeyBallWorld.asset`.
- A standalone 2048x2048 directional shadow map: the saved light disables both
  cascades and shared-atlas allocation, and the desktop camera requests the
  non-cascaded directional path.
- A runtime-shader `CommonAssets.pak`; it contains the complete engine shader
  tree required to render while excluding XRENGINE's multi-gigabyte model,
  texture, font, and test-asset library.

## Controls

| Action | VR / gamepad | Keyboard |
|---|---|---|
| Tilt course | Left stick | WASD or arrow keys |
| Reset/restart | A / face-down | R |
| Pause | Menu | Escape |

Tilt input is camera-relative. The course rotates about the ball's current
ground position, so steering remains consistent as the follow camera turns and
does not orbit the course around the world origin.

The SteamVR binding is generated under
`%LOCALAPPDATA%\MonkeyBallVR\SteamVR\bindings_knuckles.json` when startup
settings are created.

## World Asset

`Assets/Worlds/MonkeyBallWorld.asset` is the canonical editable XRENGINE world
asset. `.asset` is XRENGINE's XR-asset extension and is the extension consumed
by the cooker. The standalone bootstrap loads that asset by path; it does not
construct the scene hierarchy or course geometry in code. Published builds
convert it to a strict `RuntimeBinaryV1` payload inside `GameContent.pak` using
the game's reflection-free cooked-world serializer.

## Development Build

From the repository root:

```powershell
dotnet build .\Samples\MonkeyBallVR\MonkeyBallVR.csproj `
  -c "Development Debug" `
  -p:Platform=AnyCPU
```

Use the VS Code task `Build-VRMonkeyBall-CookGameExe` to produce a cooked,
framework-dependent development build under `Samples/MonkeyBallVR/Build/Game`.

## Release Package

The canonical clean NativeAOT build, archive smoke test, and ZIP packaging
command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\Publish-MonkeyBallVR.ps1
```

On success it produces:

```text
Samples\MonkeyBallVR\Build\Publish\
  Binaries\MonkeyBallVR.exe
  Config\GameConfig.pak
  Content\GameContent.pak
  Content\CommonAssets.pak

Samples\MonkeyBallVR\Build\Packages\MonkeyBallVR-win-x64.zip
```

The publish fails when an archive is missing, an asset cannot be converted to
`RuntimeBinaryV1`, the launcher bootstrap is absent or ambiguous, an
IL2xxx/IL3xxx warning is emitted, or `--aot-smoke` does not complete.

NativeAOT output contains the self-contained executable and the native
dependencies emitted by `dotnet publish`. The packager also preserves the
engine's repository-managed `runtimes\win-x64` tree at its runtime-relative
path; managed game and engine build trees and other platform RIDs
are deliberately excluded.

`-AllowAotWarnings` exists only to collect local diagnostic packages and must
not be used for a release.

The matching VS Code task is
`Publish-VRMonkeyBall-NativeAOT-Package`. Tagged repository releases also run
this path and upload `monkeyball-vr-win-x64.zip`.

## Hardware Sign-Off

Automated smoke validation does not prove headset presentation, controller
poses, comfort, or frame pacing. Before calling a package release-ready, run
the matrix in
`docs/work/testing/xr/monkeyball-vr-release-matrix.md` on physical target
hardware.
