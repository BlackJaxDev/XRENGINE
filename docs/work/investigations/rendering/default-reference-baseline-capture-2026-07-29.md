# Default Reference Baseline Capture - 2026-07-29

Status: Baseline captured; visual defect remains open
Subsystem: Rendering

## Problem

Document 01 needed original `DefaultRenderPipeline` images and timing evidence
before the desktop renderer diverges further. The reference must cover static,
moving, skeletal, material-diverse, transparency, stereo, and post-processing
content without changing original-pipeline behavior to make the images look
better.

## Reproduction

- Release editor, OpenGL, 1920x1080, render scale 1.0, VSync off, TAA.
- Composite unit-testing world with Sponza, the repository skinned/morph glTF,
  an unlit forward glTF, dynamic water, decal, procedural sky, atmosphere,
  volumetric fog, and deterministic moving point/spot lights.
- Isolated MCP session: `advanced-refactor-default-baseline`.
- Evidence root:
  `Build/_AgentValidation/renderer-root-trace/baseline-default-20260729/`.

## Findings

1. The first captures were almost entirely white because the active
   `FlyingCameraPawnComponent` continued translating after an MCP transform
   update. Render state showed the camera hundreds of units outside the scene
   and only six submitted commands.
2. Disabling the pawn component after setting its transform pinned the camera.
   The intended workload then reported up to 394 commands depending on the
   view, and Sponza geometry changed correctly across two camera positions.
3. The original output remained heavily overexposed/washed out after the camera
   fix. Disabling atmosphere alone did not remove the defect. Disabling fog
   reduced the wash but did not restore normal material response. General
   geometry references therefore use atmosphere/fog/water-disabled variants,
   while a separate full-composite image preserves the current post and
   transparency result.
4. The skeletal/material isolation retained one deferred `Hero Material` mesh
   and one masked/forward `Inline Unlit Material` mesh. Its output is also
   visibly distorted/overexposed and is preserved as the original result.
5. Emulated single-pass stereo produced an original-pipeline eye submission
   with shared visibility. It is useful as a stereo reference, but it is not a
   substitute for later OpenXR runtime evidence from independently owned RVC
   eye pipelines.
6. Retained GPU timing history was unavailable for these OpenGL runs. CPU
   frame-output percentiles and CPU frame dumps were captured instead.

## Evidence

- `desktop-default-composite-view-a.png`: pinned interior reference at
  `(0, 2.5, -5)`, yaw `90`.
- `desktop-default-composite-view-b.png`: second pinned view at
  `(2, 2.5, -3)`, yaw `-90`.
- `desktop-default-skeletal-material-view.png`: isolated animated/material
  reference.
- `desktop-default-post-transparency-view.png`: full atmosphere, fog, water,
  temporal, and post chain.
- `stereo-default-emulated-single-pass-preview.png`: emulated single-pass
  stereo preview.
- Exact timing ranges and host configuration are recorded in
  `Build/_AgentValidation/renderer-root-trace/baseline-default-20260729/reports/default-reference-baseline.md`.

## Next Investigation Steps

- Build the deterministic named cohorts in document 10 instead of relying on
  one composite scene for promotion decisions.
- Capture final output and intermediate HDR, exposure, atmosphere, fog, depth,
  velocity, and temporal targets from identical pinned cameras.
- Use RenderDoc if the per-target captures do not identify where the exposure
  or compositing defect begins.
- Record matched original/advanced GPU timings and OpenXR RVC eye output on a
  known runtime before production promotion.
