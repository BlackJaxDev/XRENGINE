# Rolling Ball

Rolling Ball is the shipping-path sample for XRENGINE. It is a small, complete
arcade game rather than an editor-only scene: tilt the course, guide the ball
across the bridge, avoid the bumpers, and reach the goal before time or lives
run out.

## Gameplay

- Fixed-step simulation at 120 Hz (PhysX on the existing desktop default,
  Jolt for the browser target): a dynamic spherical ball rolls against a
  kinematic compound course collider, with damping, speed limiting, bumpers,
  falling, lives, score, timer, pause, win, loss, and restart.
- A desktop-only VR headset/controller rig with a desktop mirror camera.
- Upright desktop follow camera parented to the ball, with smoothed yaw tracking
  toward the ball's velocity.
- SteamVR action manifest and generated Valve Index controller binding.
- Keyboard and gamepad controls for desktop development and QA.
- Asset-authored course, ball, camera, player rig, directional light, and HUD
  hierarchy saved in `Assets/Worlds/RollingBallWorld.asset`.
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
settings are created. That path and the existing `com.blackjax.monkeyballvr`
SteamVR application key and game-info ID remain stable so the new **Rolling
Ball** display name does not orphan saved controller bindings or application
state.

## World Asset

`Assets/Worlds/RollingBallWorld.asset` is the canonical editable XRENGINE world
asset. `.asset` is XRENGINE's XR-asset extension and is the extension consumed
by the cooker. The portable gameplay bootstrap loads that asset by path; it
does not construct the course geometry in code. The optional
`Samples/RollingBall.DesktopVR` host adds the VR rig and OpenVR startup settings
before desktop play. The authored world contains no VR nodes, so the same world
is usable in desktop and browser builds. Published builds convert it to a
strict `RuntimeBinaryV1` payload inside `GameContent.pak` using the game's
reflection-free cooked-world serializer. The serializer retains its original
version-5 signature and can read existing cooked worlds containing VR nodes;
the desktop host reuses those nodes rather than duplicating them.

## Development Build

From the repository root:

```powershell
dotnet build .\Samples\RollingBall\RollingBall.csproj `
  -c "Development Debug" `
  -p:Platform=AnyCPU
```

Use the VS Code task `Build-RollingBall-CookGameExe` to produce a cooked,
framework-dependent development build under `Samples/RollingBall/Build/Game`.

## Browser WebGPU publish

The project selects the saved `Worlds/RollingBallWorld.asset` through
`StartupScenePath`. The browser shader catalog is project-relative under
`Assets/Shaders/WebGPU`. Regenerate its checked-in, hash-addressed artifacts from
the seven explicit engine recipes with pinned Slang 2026.8 before publishing:

```powershell
pwsh Tools/Cook-RollingBallBrowserShaders.ps1
```

From a Windows source checkout with .NET 10.0.401, `wasm-tools`, and the
reviewed browser Jolt managed/native source prepared as described in
`docs/work/progress/rendering/browser-project-publishing.md`, use the production
Editor Build Project command:

```powershell
dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU
dotnet run --no-build --project XREngine.Editor/XREngine.Editor.csproj -c Release -p:Platform=AnyCPU -- --build-project Samples/RollingBall/RollingBall.xrproj --build-configuration Release --build-platform BrowserWebGPU --output-subfolder BrowserWebGPU
```

The staged publisher writes the static player to
`Samples/RollingBall/Build/BrowserWebGPU` only after world cooking, game assembly
audit, WebAssembly publish, content packaging, and launch configuration succeed.
Serve that directory over localhost HTTP or HTTPS for browser validation. This
recipe does not claim rendered game acceptance: the production WebGPU pipeline
still has named unsupported routes for authored effects and the scoreboard's
debug-shape pass until those are implemented and tested in a browser.

## Release Package

The canonical clean NativeAOT build, archive smoke test, and ZIP packaging
command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\Tools\Publish-RollingBall.ps1
```

On success it produces:

```text
Samples\RollingBall\Build\Publish\
  Binaries\RollingBall.exe
  Config\GameConfig.pak
  Content\GameContent.pak
  Content\CommonAssets.pak

Samples\RollingBall\Build\Packages\RollingBall-win-x64.zip
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
`Publish-RollingBall-NativeAOT-Package`. Tagged repository releases also run
this path and upload `rolling-ball-win-x64.zip`.

## Hardware Sign-Off

Automated smoke validation does not prove headset presentation, controller
poses, comfort, or frame pacing. Before calling a package release-ready, run
the matrix in
`docs/work/testing/xr/rolling-ball-release-matrix.md` on physical target
hardware.
