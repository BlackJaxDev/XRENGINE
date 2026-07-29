# MonkeyBall VR Gameplay And World Asset

Date: 2026-07-29

## Implemented

- Replaced runtime world construction with the saved
  `Samples/MonkeyBallVR/Assets/Worlds/MonkeyBallWorld.asset`.
- Authored the course, ball, ball-child desktop camera, player pawn, headset,
  controllers, trackers, procedural scoreboard, and directional light as scene
  nodes and components in that world.
- Added a reflection-free `MonkeyBallWorldAsset` cooked serializer and module
  registration. The game bootstrap now loads `Worlds/MonkeyBallWorld.asset`
  through `AssetManager` and fails clearly if the cooked asset is unavailable.
- Made the desktop camera remain upright while its yaw exponentially
  interpolates toward the ball's horizontal velocity heading.
- Made WASD and arrow-key input equivalent and camera-relative.
- Made the course rotate about the ball's current ground position instead of
  the world origin.
- Replaced transform-only ball motion with engine physics: the ball is a
  dynamic PhysX sphere and the tilting course is a kinematic compound rigid
  body driven before each physics step.
- Added per-light `UseShadowAtlas` authoring state. The MonkeyBall sun uses a
  dedicated 2048x2048 shadow map with cascades, contact shadows, and shared
  atlas allocation disabled. The desktop camera requests non-cascaded
  directional shadows.
- Disabled GPU auto exposure for the desktop camera and selected a fixed
  exposure so the authored directional-light response remains visible.
- Declared the camera and light runtime defaults to the YAML serializer so
  non-cascaded/false overrides survive round trips.
- Excluded the runtime `ShadowMap` framebuffer graph from YAML authoring data.
- Preserved material `ExcludeFromGpuIndirect` state in the cooked world and
  fixed the meshlet pass to render CPU-owned/excluded meshes alongside GPU
  meshlet draws.
- Made standalone packaging copy the optional repository-managed native host
  libraries that are present for the selected runtime.

XRENGINE's canonical XR asset extension is `.asset`; this is the extension
recognized and converted by the content cooker. The world is therefore named
`MonkeyBallWorld.asset`, not `.xrasset`.

## Validation

- MonkeyBall focused build: 0 warnings, 0 errors.
- Rendering focused Release build: 0 warnings, 0 errors.
- Exact Release editor build consumed by NativeAOT launcher generation:
  0 warnings, 0 errors.
- Targeted asset, camera/light, shape-mesh, world-settings, and native-host
  packaging tests: 11 passed, 0 failed.
- A later rebuild of the test assembly is currently blocked by unrelated
  existing edits in `UnitTestingWorldModelImportSettingsTests.cs` that refer
  to removed `UnitBox*` settings. The new meshlet mixed-submission contract
  test is present but cannot execute until that pre-existing compile break is
  resolved.
- Authoring validation:
  - YAML world: 30,277 bytes.
  - YAML and cooked serializer round trips both preserved the required graph,
    physics bodies/colliders, camera mode, fixed exposure, material submission
    flags, and standalone directional-light flags.
  - Cooked world blob: 2,781 bytes inside `GameContent.pak` and does not begin
    with YAML.
  - Authored and cooked worlds each contain all four 1,024-triangle procedural
    spheres with their CPU-submission exclusion flags intact.
- NativeAOT diagnostic publish and `--aot-smoke`: passed.
- Immutable ZIP contents:
  - 85 entries.
  - 477,647,670 uncompressed bytes.
  - 195,021,791-byte ZIP.
  - SHA-256:
    `E3018BB0708E93C83DE0DD5560BD42F44C9415BA7EE560C05E539B623750A162`.
  - Executable SHA-256:
    `392F93495168D5A267B87EFE5F113232BB328F75C5EF43D656506540690F9FD9`.
  - ZIP contains the executable, both cooked content archives,
    `openvr_api.dll`, and `OVRLipSync.dll`.
- Standalone packaged launch opened a responsive `MonkeyBall VR` window.
  Direct window captures confirm the complete lit scene. Sustained W input
  captures at 0.6, 1.2, and 1.8 seconds confirm course/ball motion while the
  upright follow camera keeps the ball framed; W and Up Arrow produced the
  same held-input image.
- RenderDoc isolated the missing procedural geometry to the meshlet
  mixed-submission path. Captures and visual comparisons are retained under
  `Build/_AgentValidation/20260729-monkeyball-gameplay/`.

## Remaining Release Gates

- The NativeAOT analyzer still reports the existing 407 IL2xxx/IL3xxx
  warnings. `-AllowAotWarnings` was used only for this diagnostic package; the
  strict release publisher correctly continues to reject warning-bearing
  output.
- Magick.NET 14.14.0 still has existing NuGet audit findings. Dependency
  changes require owner approval and dependency/license regeneration.
- Physical headset/controller, comfort, frame-pacing, code-signing, and store
  sign-off remain release-owner tasks.
