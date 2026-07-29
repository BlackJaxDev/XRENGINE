# MonkeyBall VR Shipping Implementation

Date: 2026-07-28

## Completed

- Replaced the passive bobbing-ball sample path with a deterministic playable
  arcade loop: course tilt, rolling, bumpers, bounds/fall recovery, lives,
  timer, scoring, pause, win/loss, restart, HUD, VR rig, and desktop controls.
- Added a public `IGameLaunchBootstrap` contract and a MonkeyBall
  implementation. Generated launchers instantiate this type directly so the
  custom VR startup settings, game state, world, and components remain rooted
  under NativeAOT.
- Restored runtime rendering/audio/input host bootstrap installation in
  generated game launchers.
- Made AOT content cooking strict: source/project/debug files and root
  launcher-only startup/state assets are excluded; both `__assetType` and
  `__type` hints are recognized; uncookable or non-`RuntimeBinaryV1` assets
  fail the publish instead of falling back to authoring YAML.
- Required config, content, and common-assets archives at startup and in the
  publish validator.
- Made IL2xxx/IL3xxx warnings and a missing smoke completion marker hard
  failures by default.
- Added a canonical MonkeyBall NativeAOT ZIP packager, corrected VS Code tasks,
  and included the game package in tagged Windows releases.
- Added targeted launcher/cooker regression coverage and a physical XR release
  matrix.
- Added an AOT-safe explicit component factory path and moved VR-headset
  resource setup until after scene attachment.
- Replaced the SharpFont-backed text HUD with a pooled procedural
  seven-segment HUD so game-world construction is NativeAOT-safe.
- Explicitly registered Silk.NET's selected window and input platforms so
  NativeAOT launchers retain both GLFW/SDL windowing and input backends.
- Made Release startup failures visible on standard error and in
  `startup-failure.log`, and return a failing process exit code.
- Preserved repository-managed native engine dependencies under their
  runtime-relative `runtimes\win-x64` path while excluding other platform
  RIDs and symbols.
- Removed redundant managed game/engine build trees from NativeAOT packages
  and added a runtime-shader
  common-assets mode for procedural games. It keeps the full shader/include
  tree but excludes multi-gigabyte optional engine content.

## Validation

- `dotnet build Samples/MonkeyBallVR/MonkeyBallVR.csproj -c "Development Debug" -p:Platform=AnyCPU`
  — passed with 0 warnings and 0 errors.
- `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Debug -p:Platform=AnyCPU`
  — passed with 0 warnings and 0 errors.
- Targeted `XREngine.UnitTests.Editor` cooker/launcher tests — 4 passed.

Current validation after the complete shipping-path implementation:

- Release editor build - passed with 0 errors; its warnings are repeated NuGet
  audit findings for Magick.NET 14.14.0.
- Targeted `XREngine.UnitTests.Editor` cooker, launcher, and packaging tests -
  7 passed.
- NativeAOT diagnostic publish - produced the executable, all three archives,
  and `MonkeyBallVR-win-x64.zip`.
- NativeAOT `--aot-smoke` - passed after directly rooting game components and
  replacing the SharpFont HUD. It constructed the MonkeyBall runtime world and
  reported 34,897 metadata types, 11 registered asset types, one content asset,
  and 450 runtime-shader common assets.
- Standalone launch - `MonkeyBallVR.exe` opened a responsive `MonkeyBall VR`
  window using the packaged configuration, content, shaders, and native PhysX
  dependency.
- Optimized publish payload - 435,246,899 bytes across 72 files; the compressed
  Windows package is 178,928,763 bytes. It contains only the `win-x64` runtime
  tree; `CommonAssets.pak` remains 603,666 bytes.
- Package hygiene - no managed source/project files, PDBs, publish logs, or AOT
  warning reports are present in the ZIP.

## Remaining Release Gates

- The generated launcher closure still emits existing IL2xxx/IL3xxx analyzer
  warnings from editor/dev reflection surfaces, runtime cooked-asset fallback
  paths, and third-party internals. The latest diagnostic publish reports 407
  warnings. The strict publisher correctly refuses a release until these are
  removed; `-AllowAotWarnings` is diagnostic only.
- Magick.NET-Q16-HDRI-AnyCPU 14.14.0 has current NuGet audit findings. A
  dependency upgrade requires owner approval and dependency/license
  regeneration under repository policy.
- Physical headset/controller, comfort, frame-pacing, code signing, and store
  certification require release-owner hardware and credentials.

## External Sign-Off

Code signing certificates, store credentials/listing material, and physical
headset/controller testing are release-owner inputs and are not fabricated by
the build. Track hardware results in
`docs/work/testing/xr/monkeyball-vr-release-matrix.md`.
