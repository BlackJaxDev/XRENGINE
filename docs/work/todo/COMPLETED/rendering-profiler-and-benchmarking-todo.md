# Rendering Profiler And Benchmarking TODO

Last Updated: 2026-05-29
Owner: Rendering / Tooling
Status: Implementation Complete; Merge Deferred
Target Branch: `rendering-profiler-benchmarking`

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Profiler Feature Docs](../../../../developer-guides/diagnostics/profiler.md)
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

- [x] Create dedicated branch `rendering-profiler-benchmarking`.
- [x] Inventory existing profiler packets, JSON capture fields, log files, and
  editor profiler views.
- [x] Inventory current render stats in `Engine.Rendering.Stats` and related
  runtime services.
- [x] Inventory GPU timestamp query placement and readback behavior.
- [x] Inventory benchmark scripts and environment variable parsing.
- [x] Capture a current profile manifest for CPU direct and zero-readback
  avatar scenes.

Acceptance criteria:

- [x] Missing counters and misleading counters are listed before new fields are
  added.

## Phase 1 - Renderer State Counters

- [x] Add or validate counters for draw calls, multi-draw calls,
  indirect-count calls, shader program switches, pipeline switches, VAO binds,
  buffer binds by target, SSBO/UBO binds, texture binds or descriptor table
  changes, uniform calls, buffer upload bytes, barriers by kind, readback bytes,
  mapped-buffer reads, and fallback events.
- [x] Split counters by pass where practical.
- [x] Split counters by selected submission strategy.
- [x] Add redundant-state-skip counters for CPU direct state caches.
- [x] Add active texture-binding rung to every frame.
- [x] Add active stereo mode to every frame.
- [x] Add profile capture schema versioning.

Acceptance criteria:

- [x] A frame capture can explain expensive state churn without opening a GPU
  debugger.

## Phase 2 - Scene And Asset Counters

- [x] Add visible renderer count, visible submesh count, triangle count,
  material slot count, active material count, texture count, resident texture
  memory, texture upload jobs, and upload time.
- [x] Add shader variant counts: requested, warming, linked, failed, loaded
  from disk cache, and generated this run.
- [x] Add skinned renderer count, bone matrix upload bytes, blendshape weight
  upload bytes, and skinned/blendshape compute dispatch count.
- [x] Add cooked variant identity and source asset identity to per-asset rows.
- [x] Add avatar representation counters: source mesh, optimized LOD, meshlet,
  visibility buffer, cluster-virtualized, octahedral impostor, Gaussian splat.
- [x] Add per-asset cost rows that can identify the observed high-material
  avatar as a distinct contributor.

Acceptance criteria:

- [x] A frame capture can identify whether a slowdown is caused by a specific
  asset or by global renderer behavior.

## Phase 3 - GPU-Driven Counters

- [x] Add culled command count, active bucket count, empty bucket skips, full
  bucket scans, material scatter dispatches, indirect command generation time,
  GPU cull time, GPU sort/compact time, and delayed draw count buffer values.
- [x] Add `GpuCompactionOverflow` and more specific active-list/bucket/meshlet
  overflow counters.
- [x] Add one-phase vs two-phase Hi-Z mode and phase draw counts.
- [x] Add meshlet task records emitted, records culled by frustum/cone/Hi-Z,
  expansion overflow, and meshlet buffer bytes resident.
- [x] Add visibility-buffer counters: visibility pass draws, classified pixels,
  active material tiles, classification overflow, reconstruction time, and
  material shading time.
- [x] Ensure all delayed readbacks are marked as delayed diagnostics and do not
  affect current-frame render decisions.

Acceptance criteria:

- [x] Zero-readback captures can prove both "no current-frame readbacks" and
  "compact active work".

## Phase 4 - VR Counters

- [x] Add per-eye render time where available.
- [x] Add active stereo mode per frame and per pass where different.
- [x] Add reprojection/motion-smoothing event counters where runtime APIs expose
  them.
- [x] Add VRS shading-rate distribution.
- [x] Add motion-vector validity coverage.
- [x] Add whole-frame XR budget and target refresh rate to the profile manifest.
- [x] Add warning fields for two-pass fallback and validation/debug output in
  benchmark captures.

Acceptance criteria:

- [x] VR captures cannot be mistaken for desktop mono captures.

## Phase 5 - Sampling Profiler Integration

- [x] Keep profiler markers compatible with ETW, PerfView, `dotnet-trace`, and
  SpeedScope.
- [x] Add named scopes around render submission, command collection handoff,
  material table updates, shader warmup, texture uploads, culling setup,
  indirect dispatch, and swap/present.
- [x] Add Superluminal-friendly marker names that match engine profiler rows.
- [x] Add VTune/Nsight correlation IDs where practical.
- [x] Document a local workflow for capturing CPU profiles and matching them to
  engine frame IDs.
- [x] Ensure marker creation does not allocate in hot paths.

Acceptance criteria:

- [x] A CPU-bound frame can be traced from engine frame ID to sampled call stack
  and profiler counters.

## Phase 6 - GPU Timestamp Policy

- [x] Make GPU timestamps opt-in for profile captures and disabled by default
  in production.
- [x] Issue at most begin/end timestamps per pass by default.
- [x] Allow dense timestamp mode only for diagnostics and mark it in manifests.
- [x] Read timestamps with delayed, non-blocking policy.
- [x] Add counters for timestamp query count and query readback bytes.
- [x] Document that timestamp instrumentation can perturb small passes.

Acceptance criteria:

- [x] GPU timestamp data explains pass cost without becoming a hidden benchmark
  variable.

## Phase 7 - Benchmark Harness Discipline

- [x] Validate every environment variable override before launch.
- [x] Fail loud on invalid enum values.
- [x] Record build configuration, backend, GPU, driver, scene, camera, lights,
  viewport, render scale, stereo mode, validation/debug state, shader-cache
  state, texture-cache state, and GPU clock policy.
- [x] Support warm-cache and cold-cache benchmark modes explicitly.
- [x] Clear caches only for cold-start measurements.
- [x] Capture enough frames after warmup for stable p50, p90, p99, and dropped
  frame counts.
- [x] Separate startup, warmup, steady-state, and streaming phases in reports.
- [x] Add optional GPU clock pinning instructions to benchmark docs, not as an
  automatic command.

Acceptance criteria:

- [x] Benchmark reports are reproducible and do not rely on hidden cache or
  validation-layer state.

## Final Validation And Merge

- [x] Run targeted profiler/unit/source-contract tests.
- [x] Generate profile captures for CPU direct, zero-readback, meshlet if
  available, visibility-buffer if available, and VR/stereo if available.
- [x] Update profiler feature docs if capture schema or workflow changes.
- [ ] Merge branch `rendering-profiler-benchmarking` back into `main` after
  implementation, validation, and documentation updates are complete.

Merge note:

- Deferred. The working tree contains unrelated pre-existing dirty files and
  untracked submodule directories, so merging into `main` from this session
  would risk carrying unrelated work. The branch is already
  `rendering-profiler-benchmarking`.

## Validation Evidence

- Branch: `rendering-profiler-benchmarking`.
- Counter audit: `docs/work/todo/rendering/optimization/rendering-profiler-counter-audit.md`.
- Script parse: `Tools/Measure-GameLoopRenderPipeline.ps1` parses as a
  PowerShell scriptblock.
- Build: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -c Release --no-restore`
  passed with 0 warnings and 0 errors.
- Profiler protocol tests:
  `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter ProfilerProtocolTests --no-restore`
  passed 13/13.
- Additional check:
  `ProfilerProtocolTests|RuntimeRenderingHostServicesTests` passed the profiler
  tests but still has one non-profiler CPU scene-culling env-cache assertion
  failure in `EffectiveCpuSceneCullingStructure_UsesRuntimeRenderingHostServicesAndEnvOverride`.
  That path expects an environment variable changed after
  `EffectiveSettingsEnvOverrides` has already cached startup env values.
- Full avatar strategy comparison summary:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-05-29_01-11-26/summary.json`.
- Backend/GPU metadata smoke summary:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-05-29_01-17-19/summary.json`.
- Emulated stereo attempt summary:
  `Build/Logs/speed-profiles/game-loop-render-pipeline/2026-05-29_01-12-32/summary.json`.

Observed capture facts:

- CPU direct avatar/no-lights capture recorded 29 p50 draw calls, 29 p50 visible
  renderers, heavy shader/uniform/texture churn, and zero steady-state GPU
  readback bytes after the backend metadata fix.
- GPU zero-readback avatar/no-lights capture recorded zero current-frame
  readback bytes, zero mapped-buffer reads, delayed diagnostic readbacks, full
  bucket scans, and fallback-event counters.
- Meshlet zero-readback was selectable but the active sample strategy fell back
  to `GpuIndirectZeroReadback`; the capture now exposes that fallback instead
  of hiding it.
- Visibility-buffer capture was not generated because no selectable
  visibility-buffer render strategy exists in the current runtime.
- Emulated VR/stereo launch recorded the target 90 Hz budget in the manifest,
  but the active frame samples remained `mono`; true stereo runtime activation
  was not available in this local run.
- Backend/GPU manifest fields now resolve to `OpenGL`,
  `NVIDIA GeForce RTX 3090/PCIe/SSE2`, and `NVIDIA Corporation`; frame samples
  also report `active_render_backend = OpenGL`.
- Benchmark manifests expose validation/debug state; the local captures reported
  validation/debug output enabled, so the benchmark hazard is visible in the
  captured metadata instead of hidden.
