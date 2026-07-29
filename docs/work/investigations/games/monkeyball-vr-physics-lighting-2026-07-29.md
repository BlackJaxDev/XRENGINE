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

## Status

Resolved in the current packaged build. Physical headset/controller behavior,
comfort, and frame pacing still require the hardware release matrix.
