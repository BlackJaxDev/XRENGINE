# 10 - Validation, Performance, Cutover, And Retirement TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [09 - Stereo, XR, Capture, And Editor Integration](09-stereo-xr-capture-and-editor-integration-todo.md)
Completes: [Advanced Render Pipeline Architectural Refactor](00-advanced-render-pipeline-refactor-todo.md)

## Goal

Prove the advanced architecture is correct, stable, and faster on the workloads
it targets; make it the production default; and remove the duplicated
V2/legacy architecture that should not survive the v1 cutover.

## Validation Matrix

Use reproducible settings and record:

- commit, dirty-worktree exclusions, build configuration, backend, driver, GPU,
  CPU, OS, resolution, render scale, HDR, VSync, AA/upscaler, shadows, GI,
  capture/profiler overhead, and scene revision;
- CPU frame, update, animation, extraction, render-thread, command-recording,
  submission, and present timing;
- total GPU frame and named stage timing;
- p50, p95, p99, hitch count, warmup, sample count, and run duration;
- draw/dispatch/barrier/pipeline-bind counts, visible pixels, active kernels,
  material work, deformation work, late recovery, readback bytes, and managed
  allocations.

Document 01 captured a composite original-pipeline seed reference spanning
static, moving, skeletal, material, transparency, post, and emulated-stereo
content. It is an orientation baseline only. The checklist below still requires
the exact deterministic named cohorts, identical original/advanced cameras,
production GPU timing, longer tail sampling, and OpenXR RVC runtime evidence.

## TODO

### 1. Deterministic Test Scenes

- [ ] Add or finalize `Empty`, `OpaqueDense`, `MaterialDiverse`,
  `MaskedCoverage`, `Skeletal1`, `Skeletal8`, `Skeletal32`,
  `SkeletalCrowd`, `Overdraw`, `Occlusion`, `ClusteredLights`,
  `ShadowStress`, `Transparency`, `PostProcess`, `MixedSpecial`,
  `StereoAsymmetric`, and `CaptureConsumers` cohorts.
- [ ] Pin cameras, animations, light state, assets, render settings, random
  seeds, and warmup.
- [ ] Keep source assets legal and repository-appropriate.
- [ ] Generate unit-testing settings/schema through canonical tools after
  settings changes.

### 2. Automated Correctness

- [ ] Run focused record-layout, resource-lifecycle, strategy-resolution,
  visibility-payload, reconstruction, derivative, classification, material,
  light, shadow, skinning, motion, stereo, capture, and post-process tests.
- [ ] Add source-contract tests for forbidden advanced-path dependencies:
  classic GBuffer, deferred light accumulation, light-combine, ordinary opaque
  forward, V2 type names, and same-frame production readback.
- [ ] Add command-tree/resource-layout tests for every feature profile.
- [ ] Add shader layout and cache-key tests for OpenGL and Vulkan.
- [ ] Add deterministic overflow and required-mode failure tests.
- [ ] Build the editor, server, and VR client where shared contracts changed.

### 3. Visual Validation

- [ ] Capture original-pipeline references and advanced output from identical
  cameras and settings.
- [ ] Define per-feature image tolerances; do not use one permissive global
  tolerance.
- [ ] Inspect final output plus visibility, depth, reconstructed attributes,
  velocity, material work, shadow, AO, GI, transparency, and temporal debug
  targets.
- [ ] Use the isolated MCP editor-session workflow and inspect saved PNGs.
- [ ] Capture more than one camera position for every suspected artifact.
- [ ] Use RenderDoc when screenshots/logs do not identify the failing
  pass/resource.
- [ ] Record durable findings in
  `docs/work/investigations/rendering/` until resolved.

### 4. Performance Gates

- [ ] Establish matched original-versus-advanced Release baselines with VSync
  disabled.
- [ ] Evaluate both the 8.33 ms 120 Hz budget and the canonical desktop
  high-refresh target; do not report average FPS alone.
- [ ] Require stable p95/p99 and moving-camera results, not only a favorable
  static p50.
- [ ] Demonstrate bounded aggregate deformation and submission scaling for the
  skeletal cohorts.
- [ ] Demonstrate material work scaling with visible kernel coverage rather
  than registered material count.
- [ ] Demonstrate two-phase visibility benefit on high-occlusion scenes and
  bounded overhead on low-occlusion scenes.
- [ ] Confirm zero production same-frame readback bytes.
- [ ] Confirm zero warmed managed allocations in per-frame rendering hot paths.
- [ ] Confirm command reuse misses name only topology, capacity, binding,
  shader, or resource-generation changes.
- [ ] Reject promotion if a claimed GPU optimization merely moves a larger cost
  to CPU recording, synchronization, descriptors, or tail latency.

### 5. Stability And Lifecycle

- [ ] Run long camera-motion, animation, resize, feature-toggle, shader-reload,
  asset-streaming, editor-interaction, and pipeline-switch sessions.
- [ ] Verify no routine device-wide idle, resource churn, unbounded retired
  generations, descriptor leak, stale history, or command-rerecord storm.
- [ ] Validate device loss/recovery and swapchain recreation where supported.
- [ ] Validate empty, missing-resource, shader-pending, capacity-overflow, and
  backend-capability failure paths.
- [ ] Inspect OpenGL, Vulkan, rendering, profiler, and shutdown logs while
  separating steady-state issues from teardown-only noise.

### 6. Production Cutover

- [ ] Make `AdvancedRenderPipeline` the desktop and applicable offscreen
  default only after its gates pass; promote the RVC-owned OpenXR eye path only
  after the matching XR gates pass.
- [ ] Replace development selectors with the final pipeline-kind setting and
  documented launch/config behavior.
- [ ] Update generated settings, schemas, editor defaults, launch profiles, and
  unit-testing-world setup.
- [ ] Remove every remaining `DefaultRenderPipeline2`, `Default2`, pipeline-V2
  environment variable, diagnostic label, source-path assertion, and
  documentation instruction.
- [ ] Update `README.md`, `docs/README.md`, runtime overview, rendering
  architecture, material authoring, pipeline authoring, MCP, benchmark, and
  launch documentation.
- [ ] Regenerate MCP docs if tool names or settings change.

### 7. Legacy Retirement

- [ ] Delete deferred/forward resources, shaders, commands, settings, and
  tests that are unreachable after the advanced cutover.
- [ ] Delete the original `DefaultRenderPipeline` after every required
  production/capture/XR consumer has migrated.
- [ ] If immediate deletion is blocked by a named required consumer, rename it
  to `LegacyDefaultRenderPipeline`, keep it opt-in, record the exact blocker
  and owner in the closeout doc, and set a dated deletion gate.
- [ ] Do not preserve both architectures by continuing symmetric feature work.
- [ ] Move completed/superseded todo material to the repository's historical
  convention and update canonical links.
- [ ] Update dependency-free legal/product language only where renderer naming
  changed; do not alter licensing policy.

### 8. Closeout

- [ ] Create a progress closeout under `docs/work/progress/rendering/` with
  architecture summary, feature matrix, validation commands, images/captures,
  performance tables, remaining risks, and legacy deletion status.
- [ ] Confirm `Build/_AgentValidation/` contains no more than ten immediate run
  folders and remove unneeded disposable evidence.
- [ ] Confirm tracked docs do not depend on ignored evidence for required
  behavior.
- [ ] Mark this series complete only after no required work remains.

## Final Acceptance Criteria

- [ ] `AdvancedRenderPipeline` is the desktop production default and
  `RvcRenderPipeline` owns production OpenXR eye output.
- [ ] Compatible opaque and masked rendering uses visibility plus native
  material/lighting shading.
- [ ] The advanced production graph contains no classic GBuffer, deferred light
  accumulation, ordinary opaque Forward+, or light-combine.
- [ ] Static, moving, material-diverse, skeletal, transparent, stereo, XR, and
  capture cohorts meet their recorded correctness and performance budgets.
- [ ] Production modes have zero same-frame GPU readback and zero warmed
  managed hot-path allocations.
- [ ] `DefaultRenderPipeline2` is absent.
- [ ] The original default pipeline is deleted or has one explicit bounded
  legacy blocker with a dated removal gate.
