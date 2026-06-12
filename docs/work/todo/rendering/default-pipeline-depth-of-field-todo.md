# Default Pipeline Depth Of Field TODO

Last Updated: 2026-06-05
Owner: Rendering
Status: Active
Target Branch: `rendering-dof-default-pipeline`

## Goal

Upgrade the default render pipeline depth-of-field path from the current
full-resolution one-pass Poisson gather into a faster, more correct, and more
featureful post-process stage.

The first production milestone should deliver:

- a dedicated signed circle-of-confusion texture,
- reversed-depth-correct physical and artist CoC math,
- explicit stereo behavior,
- half-resolution near/far separated blur,
- full-resolution composite with edge-aware foreground handling,
- debug views and profiler counters that explain cost and quality.

## Current Implementation Notes

- `DefaultRenderPipeline.CommandChain.cs` runs bloom, then motion blur / DoF,
  then temporal accumulation.
- `CreateDepthOfFieldPassCommands()` copies `ForwardPassFBO` into
  `DepthOfFieldCopyFBO`, then renders `DepthOfFieldFBO` back into
  `ForwardPassFBO`.
- `DepthOfField.fs` is a mono full-resolution gather shader using 12 Poisson
  taps. It recomputes CoC from depth for the center pixel and again for every
  blur tap (13 `ComputeCoC` calls per output pixel, including the full physical
  branch on every tap).
- `DepthOfFieldSettings` already exposes artist, physical, and target-transform
  focus modes, plus focus range, aperture, max CoC, bokeh radius, near blur,
  and physical CoC reference.
- The DoF texture has a stereo texture-array creation path, but the current
  shader is mono `sampler2D`. Stereo support must be made real or explicitly
  gated off.
- Physical-mode depth linearization in `DepthOfField.fs` hardcodes a normal-Z
  GL mapping (`depthSample = 2.0 * depth - 1.0`) and must be reconciled with
  `XRMath.DepthToDistance(depth, nearZ, farZ, IsReversedDepth)`.
- Artist mode has a separate reversed-depth bug: the CPU side computes
  `FocusDepth` via `camera.DistanceToDepth` (depth-mode aware), but the shader
  gates near blur with a raw `signedDepth = depth - FocusDepth; signedDepth < 0`
  test. Under reversed Z (near=1, far=0) that sign flips, so the `NearBlur`
  skip suppresses the wrong side. The shader needs an explicit near/far sign.
- The shader writes `OutColor.a = 1.0`; revisit once a composite stage exists so
  alpha/transparency is handled intentionally.

Relevant files:

- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.CommandChain.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.*.cs`
- `XREngine.Runtime.Rendering/Rendering/Camera/DepthOfFieldSettings.cs`
- `Build/CommonAssets/Shaders/Scene3D/DepthOfField.fs`
- `docs/architecture/rendering/default-render-pipeline-notes.md`

## Invariants

- DoF must not run in light-probe or scene-capture passes unless an explicit
  capture policy is added.
- Reversed depth and normal depth must produce equivalent focus behavior,
  including the sign of the near-blur gate, not just the focus plane.
- Foreground blur must not smear focused/background color across silhouettes.
- Stereo/VR behavior must be explicit. If unsupported, the stage should report
  why it is disabled instead of silently sampling mono textures incorrectly.
- The implementation should avoid per-frame heap allocations in render hot
  paths; validate with the `Report-NewAllocations` task.
- DoF quality presets must be measurable with GPU pipeline timings.
- Existing camera post-process schema behavior must remain editor-friendly and
  backed by `DepthOfFieldSettings`.
- Half-resolution stages need their own texel sizes; do not reuse the single
  full-resolution `TexelSize` uniform across the downsample/blur/composite
  chain.

## Phase 0 - Branch, Baseline, And Contracts

- [ ] Create dedicated branch `rendering-dof-default-pipeline`.
- [ ] Resolve the Follow-Up Questions below. Pass order (vs. bloom and
  TAA/TSR), VR default, and aperture coupling all determine the contracts this
  phase locks down, so answer them before implementation rather than after.
- [ ] Decide the stereo policy now (real `sampler2DArray` variant vs. gated-off
  diagnostic). This drives whether the Phase 2 CoC and Phase 3 half-res
  textures are 2D or array-shaped, so it cannot wait until Phase 1.
- [ ] Record current DoF behavior with screenshots or captures for:
  disabled DoF, artist DoF, physical DoF, near blur off, near blur on, and
  target-transform focus.
- [ ] Capture GPU timings for the current full-resolution DoF pass at 1080p,
  1440p, and the unit-testing internal resolution, including the per-tap CoC
  recompute cost, using profiler logs under `Build/Logs`.
- [ ] Record whether DoF currently compiles/renders in mono, stereo, OpenVR, and
  OpenXR paths.
- [ ] Add a short note to
  `docs/architecture/rendering/default-render-pipeline-notes.md` documenting
  the intended DoF pass order relative to bloom, motion blur, TAA, TSR, and
  post-process tonemapping.
- [ ] Add shader-contract tests in `XREngine.UnitTests/` that lock down the
  required DoF samplers and uniforms before refactoring.

Acceptance criteria:

- [ ] Baseline captures and timing notes are attached to the work item or
  stored under `Build/Logs`.
- [ ] The pass-order and stereo policy are documented and the Follow-Up
  Questions are answered before implementation begins.

## Phase 1 - Correctness And Gating

- [ ] Replace the hardcoded `2.0 * depth - 1.0` linearization in
  `DepthOfField.fs` with logic mirroring
  `XRMath.DepthToDistance(depth, nearZ, farZ, IsReversedDepth)`, binding
  `IsReversedDepth` (or an equivalent depth-mode flag) to the shader.
- [ ] Fix the artist-mode near-blur sign bug: the `signedDepth = depth -
  FocusDepth; signedDepth < 0` near-blur gate is inverted under reversed Z.
  Drive the near/far sign from the camera depth mode so `NearBlur` skips the
  correct side in both normal and reversed Z.
- [ ] Verify artist mode uses the same `DistanceToDepth` mapping as the active
  camera (CPU already does; confirm the shader agrees).
- [ ] Verify physical mode converts depth to linear distance correctly for both
  normal and reversed depth.
- [ ] Implement the stereo policy chosen in Phase 0 (real `sampler2DArray`
  variant or gated-off diagnostic).
- [ ] Ensure `DefaultRenderPipeline` and `DefaultRenderPipeline2` use the same
  gating behavior.
- [ ] Add tests for disabled/capture/light-probe/stereo DoF gates, plus a
  regression test that asserts the near-blur sign is correct in both depth
  modes.

Acceptance criteria:

- [ ] Artist and physical focus planes match the active camera in normal and
  reversed depth modes.
- [ ] Near blur affects the foreground (not the background) in both depth
  modes.
- [ ] Stereo behavior matches the Phase 0 decision and is intentional.

## Phase 2 - Dedicated CoC Texture

- [ ] Add a signed CoC texture, preferably `R16F`, sized to internal
  resolution.
- [ ] Add a `DepthOfFieldCoC.fs` pass that writes signed CoC:
  negative for near blur, positive for far blur, zero for in-focus pixels.
- [ ] Move physical/artist CoC calculation out of the gather shader.
- [ ] Precompute physical camera coefficients on the CPU where practical, so
  the shader avoids repeated focal-length/aperture math.
- [ ] Add `MaxCoC`, focus distance, focus range, near blur, depth mode, and
  physical camera bindings to the CoC pass.
- [ ] Add a CoC debug visualization mode.

Acceptance criteria:

- [ ] Gather/composite shaders sample CoC instead of recomputing it per tap.
- [ ] CoC debug output clearly distinguishes near, far, and focused pixels.

## Phase 3 - Half-Resolution Near/Far Blur Chain

- [ ] Add half-resolution DoF color and CoC textures.
- [ ] Add a CoC-aware downsample pass that preserves foreground blur and max far
  blur without leaking background depth across foreground silhouettes.
- [ ] Split blur into near and far buffers or equivalent signed-CoC layers.
- [ ] Add a toggle (quality preset or cvar) that keeps the legacy
  full-resolution gather selectable, so the new chain can be A/B compared and
  validated in Phase 6 before the old path is removed.
- [ ] Replace the current one-pass full-resolution gather with a half-resolution
  blur chain.
- [ ] Add full-resolution composite that uses source color, near blur, far blur,
  signed CoC, and depth discontinuity checks.
- [ ] Handle composite alpha intentionally (the legacy shader forces
  `OutColor.a = 1.0`).
- [ ] Keep tiny CoC pixels on the source color fast path.
- [ ] Gate expensive large-radius blur by tile classification or max-CoC
  thresholds.
- [ ] Reuse existing pipeline resource lifetime/transient patterns where
  possible.

Acceptance criteria:

- [ ] DoF cost scales primarily with half-resolution blur work, not full-screen
  full-resolution gathers.
- [ ] Foreground silhouettes do not visibly bleed background color.
- [ ] In-focus pixels remain sharp.

## Phase 4 - Post-FX Ping-Pong Cleanup

- [ ] Replace DoF's copy-then-render-back pattern with a generic post-FX
  ping-pong target sequence if it fits the existing pipeline architecture.
- [ ] Evaluate whether motion blur and DoF can share the same ping-pong
  infrastructure.
- [ ] Preserve the current pass order unless Phase 0 explicitly changes it.
- [ ] Update `docs/developer-guides/rendering/render-pipelines/default-render-pipeline.xrs` after the pass graph
  changes.

Acceptance criteria:

- [ ] DoF no longer performs an avoidable full-resolution blit solely to prevent
  read/write feedback.
- [ ] Motion blur and DoF ordering remains obvious in the command chain.

## Phase 5 - Feature Expansion

- [ ] Add quality presets:
  `Low`, `Medium`, `High`, and `Cinematic`.
- [ ] Add bokeh shape controls:
  blade count, blade rotation, roundness, anamorphic stretch, and cat-eye edge
  falloff.
- [ ] Add highlight bokeh controls:
  threshold, boost, clamp, and optional chromatic fringe.
- [ ] Add autofocus controls:
  center-depth focus, selected-object focus, focus smoothing speed, rack-focus
  duration, focus dead zone, and focus distance readout.
- [ ] Integrate physical DoF aperture/focal length/sensor size with
  `XRPhysicalCameraParameters` and physical exposure settings.
- [ ] Add optional temporal/stochastic sampling for high-quality bokeh, with
  history rejection compatible with TAA/TSR.
- [ ] Add editor debug views for CoC, signed CoC, near blur, far blur, focus
  plane, tile classification, and sample count/cost.

Acceptance criteria:

- [ ] The stage supports both practical realtime presets and cinematic authoring
  controls.
- [ ] Feature controls are visible only when relevant in the post-process
  schema.

## Phase 6 - Validation

- [ ] Run targeted shader contract tests.
- [ ] Build the editor:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore /p:UseSharedCompilation=false /clp:ErrorsOnly`.
- [ ] Validate mono editor rendering with DoF disabled and enabled.
- [ ] Validate physical and artist modes against known focus distances.
- [ ] Validate near-blur disabled and enabled.
- [ ] Validate target-transform focus.
- [ ] Validate TAA and TSR interaction, including camera jitter and history
  stability.
- [ ] Validate bloom ordering with bright highlights and bokeh highlights.
- [ ] Validate stereo or verify the documented stereo diagnostic path.
- [ ] Compare GPU timings against the Phase 0 baseline.
- [ ] Run `Report-NewAllocations` and confirm no new per-frame hot-path
  allocations were introduced.
- [ ] Merge `rendering-dof-default-pipeline` back into `main` after validation,
  then remove the legacy gather toggle if it is no longer needed.

Acceptance criteria:

- [ ] New DoF implementation is measurably faster than the old full-resolution
  gather at equivalent visible quality (record a numeric target, e.g. a
  percentage improvement at 1440p, against the Phase 0 baseline timings).
- [ ] Debug views explain incorrect focus, edge bleeding, and high GPU cost.
- [ ] No new compiler warnings or shader-link diagnostics are introduced.

## Follow-Up Questions

These are Phase 0 blockers, not deferred work; they shape the pass-order and
camera contracts and must be answered before implementation begins.

- Should DoF run before or after bloom for cinematic bokeh highlights, or should
  it consume a pre-thresholded highlight buffer separately?
- Should DoF run before temporal accumulation, as it does now, or should high
  quality modes move after TAA/TSR to avoid history instability?
- Should VR default to DoF disabled even after stereo support exists, given
  comfort and performance concerns?
- Should physical DoF aperture always mirror physical exposure aperture, or
  should artists be able to decouple them per camera?
