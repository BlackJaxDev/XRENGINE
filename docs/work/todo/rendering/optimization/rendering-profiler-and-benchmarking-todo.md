# Rendering Profiler And Benchmarking TODO

Last Updated: 2026-05-29
Owner: Rendering / Tooling
Status: Active
Target Branch: `rendering-profiler-benchmarking`

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Profiler Feature Docs](../../../../features/profiler.md)
- [Render Submission Performance Debug Plan](../../../design/rendering/render-submission-perf-debug-plan.md)
- [Production GPU-Driven Rendering Roadmap](../gpu/production-rendering-pipeline-roadmap.md)

## Goal

Make rendering performance conclusions reproducible. Counters should explain
what changed, sampled profilers should explain where CPU time went, GPU
timestamps should explain pass cost without perturbing production frames, and
benchmark manifests should make cache, build, backend, stereo, and validation
state explicit.

## Scope

- Per-frame renderer counters.
- Per-asset and cooked-variant counters.
- GPU-driven counters.
- VR counters.
- GPU timestamp policy.
- ETW, Superluminal, VTune, Nsight integration points.
- Benchmark harness rules and manifests.

## Non-Goals

- Do not make profiler instrumentation mandatory in production builds.
- Do not use synchronous GPU queries in measured production frames.
- Do not treat counters as a replacement for sampled CPU profiling.
- Do not compare Debug and Release numbers as architectural evidence.

## Phase 0 - Branch, Baseline, And Counter Audit

- [ ] Create dedicated branch `rendering-profiler-benchmarking`.
- [ ] Inventory existing profiler packets, JSON capture fields, log files, and
  editor profiler views.
- [ ] Inventory current render stats in `Engine.Rendering.Stats` and related
  runtime services.
- [ ] Inventory GPU timestamp query placement and readback behavior.
- [ ] Inventory benchmark scripts and environment variable parsing.
- [ ] Capture a current profile manifest for CPU direct and zero-readback
  avatar scenes.

Acceptance criteria:

- [ ] Missing counters and misleading counters are listed before new fields are
  added.

## Phase 1 - Renderer State Counters

- [ ] Add or validate counters for draw calls, multi-draw calls,
  indirect-count calls, shader program switches, pipeline switches, VAO binds,
  buffer binds by target, SSBO/UBO binds, texture binds or descriptor table
  changes, uniform calls, buffer upload bytes, barriers by kind, readback bytes,
  mapped-buffer reads, and fallback events.
- [ ] Split counters by pass where practical.
- [ ] Split counters by selected submission strategy.
- [ ] Add redundant-state-skip counters for CPU direct state caches.
- [ ] Add active texture-binding rung to every frame.
- [ ] Add active stereo mode to every frame.
- [ ] Add profile capture schema versioning.

Acceptance criteria:

- [ ] A frame capture can explain expensive state churn without opening a GPU
  debugger.

## Phase 2 - Scene And Asset Counters

- [ ] Add visible renderer count, visible submesh count, triangle count,
  material slot count, active material count, texture count, resident texture
  memory, texture upload jobs, and upload time.
- [ ] Add shader variant counts: requested, warming, linked, failed, loaded
  from disk cache, and generated this run.
- [ ] Add skinned renderer count, bone matrix upload bytes, blendshape weight
  upload bytes, and skinned/blendshape compute dispatch count.
- [ ] Add cooked variant identity and source asset identity to per-asset rows.
- [ ] Add avatar representation counters: source mesh, optimized LOD, meshlet,
  visibility buffer, cluster-virtualized, octahedral impostor, Gaussian splat.
- [ ] Add per-asset cost rows that can identify the observed high-material
  avatar as a distinct contributor.

Acceptance criteria:

- [ ] A frame capture can identify whether a slowdown is caused by a specific
  asset or by global renderer behavior.

## Phase 3 - GPU-Driven Counters

- [ ] Add culled command count, active bucket count, empty bucket skips, full
  bucket scans, material scatter dispatches, indirect command generation time,
  GPU cull time, GPU sort/compact time, and delayed draw count buffer values.
- [ ] Add `GpuCompactionOverflow` and more specific active-list/bucket/meshlet
  overflow counters.
- [ ] Add one-phase vs two-phase Hi-Z mode and phase draw counts.
- [ ] Add meshlet task records emitted, records culled by frustum/cone/Hi-Z,
  expansion overflow, and meshlet buffer bytes resident.
- [ ] Add visibility-buffer counters: visibility pass draws, classified pixels,
  active material tiles, classification overflow, reconstruction time, and
  material shading time.
- [ ] Ensure all delayed readbacks are marked as delayed diagnostics and do not
  affect current-frame render decisions.

Acceptance criteria:

- [ ] Zero-readback captures can prove both "no current-frame readbacks" and
  "compact active work".

## Phase 4 - VR Counters

- [ ] Add per-eye render time where available.
- [ ] Add active stereo mode per frame and per pass where different.
- [ ] Add reprojection/motion-smoothing event counters where runtime APIs expose
  them.
- [ ] Add VRS shading-rate distribution.
- [ ] Add motion-vector validity coverage.
- [ ] Add whole-frame XR budget and target refresh rate to the profile manifest.
- [ ] Add warning fields for two-pass fallback and validation/debug output in
  benchmark captures.

Acceptance criteria:

- [ ] VR captures cannot be mistaken for desktop mono captures.

## Phase 5 - Sampling Profiler Integration

- [ ] Keep profiler markers compatible with ETW, PerfView, `dotnet-trace`, and
  SpeedScope.
- [ ] Add named scopes around render submission, command collection handoff,
  material table updates, shader warmup, texture uploads, culling setup,
  indirect dispatch, and swap/present.
- [ ] Add Superluminal-friendly marker names that match engine profiler rows.
- [ ] Add VTune/Nsight correlation IDs where practical.
- [ ] Document a local workflow for capturing CPU profiles and matching them to
  engine frame IDs.
- [ ] Ensure marker creation does not allocate in hot paths.

Acceptance criteria:

- [ ] A CPU-bound frame can be traced from engine frame ID to sampled call stack
  and profiler counters.

## Phase 6 - GPU Timestamp Policy

- [ ] Make GPU timestamps opt-in for profile captures and disabled by default
  in production.
- [ ] Issue at most begin/end timestamps per pass by default.
- [ ] Allow dense timestamp mode only for diagnostics and mark it in manifests.
- [ ] Read timestamps with delayed, non-blocking policy.
- [ ] Add counters for timestamp query count and query readback bytes.
- [ ] Document that timestamp instrumentation can perturb small passes.

Acceptance criteria:

- [ ] GPU timestamp data explains pass cost without becoming a hidden benchmark
  variable.

## Phase 7 - Benchmark Harness Discipline

- [ ] Validate every environment variable override before launch.
- [ ] Fail loud on invalid enum values.
- [ ] Record build configuration, backend, GPU, driver, scene, camera, lights,
  viewport, render scale, stereo mode, validation/debug state, shader-cache
  state, texture-cache state, and GPU clock policy.
- [ ] Support warm-cache and cold-cache benchmark modes explicitly.
- [ ] Clear caches only for cold-start measurements.
- [ ] Capture enough frames after warmup for stable p50, p90, p99, and dropped
  frame counts.
- [ ] Separate startup, warmup, steady-state, and streaming phases in reports.
- [ ] Add optional GPU clock pinning instructions to benchmark docs, not as an
  automatic command.

Acceptance criteria:

- [ ] Benchmark reports are reproducible and do not rely on hidden cache or
  validation-layer state.

## Final Validation And Merge

- [ ] Run targeted profiler/unit/source-contract tests.
- [ ] Generate profile captures for CPU direct, zero-readback, meshlet if
  available, visibility-buffer if available, and VR/stereo if available.
- [ ] Update profiler feature docs if capture schema or workflow changes.
- [ ] Merge branch `rendering-profiler-benchmarking` back into `main` after
  implementation, validation, and documentation updates are complete.
