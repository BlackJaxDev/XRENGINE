# MonkeyBall VR Gameplay And World Asset

Date: 2026-07-29

## Implemented

- Replaced runtime world construction with the saved
  `Samples/MonkeyBallVR/Assets/Worlds/MonkeyBallWorld.asset`.
- Authored the course, ball, root-level desktop camera, player pawn, headset,
  controllers, trackers, procedural scoreboard, and directional light as scene
  nodes and components in that world.
- Added a reflection-free `MonkeyBallWorldAsset` cooked serializer and module
  registration. The game bootstrap now loads `Worlds/MonkeyBallWorld.asset`
  through `AssetManager` and fails clearly if the cooked asset is unavailable.
- Made the desktop camera remain upright while its yaw exponentially
  interpolates toward the ball's horizontal velocity heading.
- Moved desktop camera follow to `Late/Scene` and made it consume the ball's
  presented world pose. Keeping the authored camera outside the spinning ball
  hierarchy prevents a world/render parent-matrix mismatch from moving the
  camera boom while the ball rolls.
- Authored and cooked the ball rigid-body transform with
  `InterpolationMode: Interpolate`; interpolated presentation is synchronized
  between the physics and normal-update threads.
- Set the game's fixed physics cadence to 120 Hz in bootstrap, startup config,
  game config, and world settings while preserving 90 Hz update/render targets.
- Made WASD and arrow-key input equivalent and camera-relative.
- Made the course rotate about the ball's full current 3D position instead of
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

- Latest MonkeyBall/input/asset/launcher/physics timing gate: 40 passed,
  0 failed.
- The canonical publisher forced a Release editor/tooling rebuild before
  NativeAOT launcher generation.
- Renderer source/launcher-input SHA-256 pairs matched:
  - Core:
    `C6A63A708AB6A523BDC7E6AA30D1D95E47DFD300683B3BD8DD39A700899F0AB3`.
  - OpenGL:
    `670F73F9C0AA69E36D8788155BFCDC2C46163B2B0C5587E187163A055FDD1EA8`.
  - Vulkan:
    `F0C4E4830C1142D45D0B042FB69AC3DB951C136386DC90E100BD4B79938DDEC5`.
- Authoring validation:
  - YAML world: 30,950 bytes.
  - YAML and cooked serializer round trips both preserved the required graph,
    physics bodies/colliders, camera mode, fixed exposure, material submission
    flags, standalone directional-light flags, and ball interpolation mode.
  - Final `GameContent.pak`: 1,684 bytes and contains the binary v5 world
    payload rather than loose YAML.
  - Authored and cooked worlds each contain all four 1,024-triangle procedural
    spheres with their CPU-submission exclusion flags intact.
- NativeAOT diagnostic publish and expanded `--aot-smoke`: passed with 300
  normal ticks and 398 physics steps at the 120 Hz fixed cadence.
- Extracted ZIP smoke: native exit code 0 with 300 normal ticks and 399 physics
  steps, `engineFixedHz=120.00047`, and `ballInterpolation=Interpolate`.
- Immutable ZIP contents:
  - 109 entries.
  - 632,079,146 uncompressed bytes.
  - 254,213,804-byte ZIP.
  - SHA-256:
    `AA5E30F1DC0A4DFFD43B61EDF819232A50A85BC0D4DD233C0A9809C45130A367`.
  - Executable SHA-256:
    `9147B747A8EDE5632C130A659A977A033CAF734F4C2A4907F2264AF0153DCE1B`.
  - ZIP contains the executable, both cooked content archives,
    `libmagicphysx.dll`, `openvr_api.dll`, `OVRLipSync.dll`, and the verified
    renderer hash manifest.
- Final direct runtime diagnostics:
  - Real W and Up Arrow window messages emitted pressed/released callbacks and
    held/zero tilt.
  - After camera yaw reached -1.52 radians, W mapped to world tilt
    `(0.998, -0.056)` and accelerated the ball above 1.2 m/s.
  - Up Arrow drove the stage near its authored 12-degree target and the ball
    to 0.857 m/s within two seconds.
  - Camera distance stayed approximately 6.041523 m. Sampled render-pair
    offset error was no greater than 0.00000143 m, right-axis Y error was no
    greater than 0.000000008, and velocity alignment reached 0.99.
  - Earlier high-frequency diagnostics bounded the rolling ball body's own Y
    motion to 2.74 mm, confirming the much larger apparent bounce came from
    camera presentation rather than PhysX contact motion.
  - Smoke and direct stderr contained zero PhysX in-flight access errors.
- 2026-07-29 shadow-validation package capture:
  - `monkeyball-final-hotkey-v2_frame241.rdc` replays as OpenGL.
  - Dedicated resources 94/95 are 2048x2048 D24/R16_FLOAT with nonuniform
    caster data.
  - Directional-light EID 674 binds `ShadowMap` texture 95 and its exported
    accumulation shows lit and shadowed regions.
  - Reports and PNG exports are retained under
    `Build/_AgentValidation/20260729-monkeyball-gameplay/runtime-gates/`.

## Remaining Release Gates

- Direct user acceptance of the launched final package is still required.
- The NativeAOT analyzer still reports 899 IL2xxx/IL3xxx
  warnings. `-AllowAotWarnings` was used only for this diagnostic package; the
  strict release publisher correctly continues to reject warning-bearing
  output.
- Magick.NET 14.14.0 still has existing NuGet audit findings. Dependency
  changes require owner approval and dependency/license regeneration.
- Physical headset/controller, comfort, frame-pacing, code-signing, and store
  sign-off remain release-owner tasks.
