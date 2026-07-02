# Rendering Clean Performance Baseline Profile Contract TODO

Last Updated: 2026-07-01
Owner: Rendering / Editor
Status: Proposed
Target Branch: `rendering-clean-performance-baselines`

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/log_rendering.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/log_vulkan.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [Rendering Profiler Counter Audit](rendering-profiler-counter-audit.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Editor Profiler And UI Render Cost TODO](editor-profiler-ui-render-cost-todo.md)

## Goal

Create a repeatable performance profile contract so baseline captures measure
the engine, not diagnostics, validation, verbose logging, or profiler UI.

## Issue

The July 1 run was useful for triage, but it was not a clean performance
baseline:

- `VulkanProfile Active=Diagnostics`.
- ImGui profiler UI and dynamic text overlays were active and appeared as top
  render-thread costs.
- `log_vulkan.log` contained hundreds of `VulkanResourcePlanner` warnings.
- `log_rendering.log` contained hundreds of `ExposureUpdate`, `RenderDiag`,
  `OpenXR`, and GPU profiler publication lines.
- The profiler itself recorded many FPS-drop records.

Diagnostics mode is valuable while investigating, but it should not be the
default evidence for performance claims. Without a standard profile contract,
every optimization risks being measured against a moving target.

## Why This Matters

Performance work needs comparable numbers. A capture with validation layers,
debug labels, verbose warnings, profiler panels, cold shaders, and editor
overlays cannot be compared fairly to a capture without them.

The engine also needs diagnostic captures, but those captures should be labeled
as diagnostic and should report the known measurement overhead.

## Fix Direction

- Define named profile modes:
  `Diagnostics`, `DevelopmentProfile`, `CleanProfile`, and `ReleaseBenchmark`.
- Make every performance dump include a profile manifest:
  build configuration, backend, GPU, driver, validation layers, Vulkan profile,
  log verbosity, profiler UI state, editor UI state, mirror mode, XR runtime,
  stereo mode, shader cache state, texture cache state, and scene name.
- In `CleanProfile`, disable or minimize:
  profiler UI, dynamic text overlays, validation layers, verbose render graph
  warnings, per-frame diagnostic logs, debug labels where possible, and
  synchronous debug callbacks.
- In `Diagnostics`, keep the extra data but report that results are intrusive.
- Add a warmup policy: shader/pipeline warmup, texture residency warmup, and a
  fixed number of ignored startup frames before measurement.
- Store profile manifests next to generated frame logs or include them in the
  frame dumps.

## Phase 0 - Define The Modes

- [ ] Create dedicated branch `rendering-clean-performance-baselines`.
- [ ] Define `Diagnostics`, `DevelopmentProfile`, `CleanProfile`, and
  `ReleaseBenchmark` settings.
- [ ] Document which logging categories, validation layers, editor UI panels,
  overlays, and profiler features are enabled in each mode.
- [ ] Add a single visible line at startup reporting the active performance
  profile mode.

Acceptance criteria:

- [ ] A log file says whether it is suitable for clean performance comparison.
- [ ] Diagnostics captures and clean captures cannot be confused.

## Phase 1 - Capture Manifest

- [ ] Add a manifest section to CPU/GPU frame dumps.
- [ ] Include backend, GPU, driver, build configuration, runtime, stereo mode,
  mirror mode, scene, shader cache state, texture cache state, validation
  state, logging verbosity, and profiler UI state.
- [ ] Include active render settings that affect cost: AO, exposure, MSAA,
  TSR, bloom, motion vectors, debug overlays, and mesh strategy.
- [ ] Include the exact log session path.

Acceptance criteria:

- [ ] A frame dump can be reproduced without opening unrelated logs.
- [ ] Two captures can be compared only when their manifests are compatible, or
  the differences are explicit.

## Phase 2 - Clean Benchmark Flow

- [ ] Add a task or launch profile for clean desktop rendering performance.
- [ ] Add a task or launch profile for clean OpenXR/Monado performance where
  available.
- [ ] Disable profiler UI and dynamic overlays by default in clean profile.
- [ ] Warm shader and texture state before measurement.
- [ ] Record p50, p90, p95, p99, max, dropped-frame count, render-thread CPU,
  GPU pipeline time, and synchronization waits.

Acceptance criteria:

- [ ] A clean run produces stable, comparable numbers without manual editor
  setup.
- [ ] The run still reports failures visibly instead of silently falling back to
  CPU or disabling GPU paths.

## Phase 3 - Diagnostic Overhead Reporting

- [ ] Measure the overhead of Diagnostics mode relative to CleanProfile on a
  fixed scene.
- [ ] Add warnings when profiler UI, validation layers, or verbose logging are
  active during a claimed benchmark.
- [ ] Keep diagnostic captures useful by including the overhead context in the
  manifest.

Acceptance criteria:

- [ ] Performance reports can state whether they are clean benchmark evidence
  or diagnostic investigation evidence.
