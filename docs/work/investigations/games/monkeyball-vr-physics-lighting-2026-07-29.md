# MonkeyBall VR Physics And Lighting Regression

Date: 2026-07-29

## Problem

The first asset-authored NativeAOT package launched, but user validation showed
no convincing physics response and a flat/unlit-looking scene.

## Root Causes

Three independent problems overlapped:

1. The original sample moved the ball and course as gameplay transforms rather
   than using engine physics. The saved world therefore did not contain a
   dynamic ball body or a kinematic compound course body to simulate.
2. The desktop camera's newly-created render pipeline retained GPU auto
   exposure. In this small, bright test scene that washed out most of the
   directional-light response and made the course look flat.
3. The production meshlet path did not call
   `RenderCPUNonMeshAndExcluded`. Procedural sphere renderers deliberately
   excluded from GPU-indirect submission—the ball, bumpers, and goal
   marker—were silently omitted whenever the meshlet strategy was selected.

A packaging complication initially hid the renderer fix: the generated
NativeAOT launcher referenced absolute assemblies in the canonical Release
editor output, and that output still held an older rendering assembly. The
Release editor output had to be rebuilt before republishing.

## Fix

- Authored a `DynamicRigidBodyComponent` sphere for the ball and a kinematic
  `DynamicRigidBodyComponent` with compound course colliders in
  `MonkeyBallWorld.asset`.
- Moved course tilt to `PrePhysics`, calculated it in camera-relative space,
  pivoted the kinematic target around the current ball position, and read ball
  position/velocity back from the live physics actor.
- Configured the desktop camera's private render pipeline with fixed exposure
  (`AutoExposure = false`, `Exposure = 1`) and the non-cascaded directional
  shadow mode.
- Preserved `ExcludeFromGpuIndirect` in cooked material render options.
- Added the missing mixed CPU/GPU submission call to the meshlet render pass,
  matching the traditional GPU path.
- Rebuilt the exact Release editor assemblies consumed by launcher generation,
  then republished and reran the NativeAOT smoke test.

## Evidence

- RenderDoc 1.44 and `rdc-cli` passed `rdc doctor`, including replay support
  and Vulkan layer registration.
- Before the meshlet fix, the relevant G-buffer pass contained only one
  12-triangle box draw. After the fix, the meshlet pass included its CPU-owned
  submission path.
- Inspection of both authored and cooked worlds found four procedural sphere
  renderers. Every sphere retained a 1,024-triangle mesh and
  `ExcludeFromGpuIndirect = true` on both component and renderer state. The
  cooked world payload is 2,781 bytes inside `GameContent.pak`.
- A direct capture of the final packaged desktop window shows the amber ball,
  red bumpers, green goal marker, blue course, and clear directional shading.
- After reset, a sustained W input was captured at 0.6, 1.2, and 1.8 seconds.
  The follow camera kept the ball framed while the course and bumpers moved
  relative to it, demonstrating live kinematic tilt and ball motion. Up Arrow
  and W produced byte-identical held-input frames, confirming equivalent key
  mapping.
- The final executable completed `--aot-smoke` and remains responsive when
  launched from `Samples/MonkeyBallVR/Build/Publish/Binaries/MonkeyBallVR.exe`.

## Final Packaged Acceptance

The definitive package was rebuilt after every gameplay/runtime fix. The
publisher now forces a canonical Release editor/tooling rebuild and verifies
the SHA-256 of the core, OpenGL, and Vulkan renderer assemblies copied into
the NativeAOT launcher. The matching hashes are embedded in
`Metadata/RenderingAssemblyHashes.json` inside the ZIP.

The final validation sequence passed in the required order:

1. 32 focused tests passed.
2. The canonical NativeAOT publish completed with the expanded live smoke.
3. The extracted ZIP executable returned exit code 0 from that smoke.
4. A focused direct executable received real W and extended Up Arrow window
   messages and emitted pressed/released input callbacks.
5. Runtime diagnostics captured stage, ball, camera, physics, and reset state.
6. RenderDoc captured and replayed the exact packaged executable.

Key numeric results:

- NativeAOT smoke: 300 normal ticks, 299 physics steps, all gameplay and
  standalone-shadow acceptance flags passed.
- Direct W after camera yaw changed to -1.52 radians produced camera-relative
  world tilt `(0.998, -0.056)`, stage rotation near the authored 12-degree
  target, and ball speed above 1.2 m/s.
- Direct Up Arrow produced `tilt=0,1`, drove the stage near 12 degrees, and
  accelerated the ball to 0.857 m/s within two seconds.
- Camera offset stayed approximately 6.0415 m, right-axis Y stayed effectively
  zero, and velocity-facing alignment reached 0.99.
- No packaged smoke or direct run emitted a PhysX
  `not allowed while simulation is running` diagnostic. Physics velocity and
  sleep reads now use post-fetch caches; mutations are routed to the physics
  thread.
- RenderDoc resource 94 is a dedicated 2048x2048 D24 depth target and resource
  95 is its 2048x2048 R16_FLOAT receiver. Directional-light EID 674 binds the
  standalone `ShadowMap` and its exported accumulation contains distinct lit
  and shadowed regions.

## Rolling Camera Jitter Follow-Up (2026-07-30)

The large apparent up/down bounce while rolling was a camera presentation
defect, not a large PhysX contact oscillation. High-frequency diagnostics from
the affected build put the rolling ball body's Y position between 0.647334 m
and 0.650078 m, a 2.74 mm range, with vertical speed no greater than
0.00131 m/s. The visible displacement was much larger.

The desktop camera was a child of the spinning `RigidBodyTransform`. Gameplay
set a compensating world pose from the ball's world matrix, but rendering
recomposed that local pose against the ball's independently published
interpolated render matrix. Even a one-sample rotation mismatch is amplified by
the camera's roughly 6 m boom.

The fix:

- Moves the desktop camera to a root-level authored scene node so it cannot
  inherit the ball mesh's rolling rotation.
- Updates camera follow in `Late/Scene`, after normal rigid-body presentation,
  and derives its position from the ball's presented world pose.
- Validates the actual ball and camera `RenderTranslation`/`RenderRotation`
  pair, not only gameplay world transforms.
- Authors and cooks the ball `RigidBodyTransform` with
  `InterpolationMode: Interpolate`.
- Keeps interpolated/extrapolated rigid-body presentation on the ordered normal
  update tick and synchronizes physics/presentation state; only discrete mode
  publishes directly from the fixed thread.
- Runs physics at 120 Hz (`1/120 s`) while update and rendering remain 90 Hz.

Validation passed in sequence:

- 20 focused camera, asset, timing, and gameplay tests.
- 40 broader MonkeyBall/input/launcher/cooked-asset/physics timing tests.
- Canonical NativeAOT cook/publish smoke: 300 normal ticks, 398 physics steps.
- Direct published executable smoke: 300 normal ticks, 399 physics steps.
- Extracted final ZIP smoke: native exit code 0, 300 normal ticks, 399 physics
  steps.
- Packaged diagnostics reported `engineFixedHz=120.00047` and
  `ballInterpolation=Interpolate`.
- Across the sampled published render pairs, camera offset error was at most
  0.00000143 m and camera right-axis Y error was at most 0.000000008.

Final immutable artifacts:

- Executable SHA-256:
  `9147B747A8EDE5632C130A659A977A033CAF734F4C2A4907F2264AF0153DCE1B`.
- ZIP SHA-256:
  `AA5E30F1DC0A4DFFD43B61EDF819232A50A85BC0D4DD233C0A9809C45130A367`.

## Status

Resolved in the current packaged build. Direct user sign-off and physical
headset/controller, comfort, frame-pacing, signing, and store checks remain
release-owner gates.
