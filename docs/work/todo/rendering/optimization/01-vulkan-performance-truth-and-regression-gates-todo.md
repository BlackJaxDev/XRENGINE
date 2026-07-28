# 01 - Vulkan Performance Truth And Regression Gates TODO

Last Updated: 2026-07-28
Owner: Rendering / Vulkan / Profiling
Status: Complete
Sequence: 01 of 08
Predecessor: None
Blocks: [02 - Vulkan Primary Reuse Correctness](02-vulkan-primary-reuse-correctness-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)

Related trackers:

- [Rendering Clean Performance Baseline Profile Contract](rendering-clean-performance-baseline-profile-contract-todo.md)
- [Rendering Profiler Counter Audit](rendering-profiler-counter-audit.md)
- [Vulkan Primary Command Recording Fast Path](vulkan-primary-command-recording-fast-path-todo.md)
- [Editor Profiler And UI Render Cost](editor-profiler-ui-render-cost-todo.md)

## Completion Outcome

The measurement and regression-gate infrastructure is complete. Completion
means the harness can produce trustworthy pass/fail evidence; it does not mean
the current renderer meets the promotion budgets. The first canonical Quick
capture correctly returned a nonzero exit code and
`NonPromotableQuickRun`/`Fail` because it observed current-frame GPU readback,
3,442,176 bytes of command-buffer-recording allocations, and a 58.887 ms
desktop render p95 against the 5.00 ms budget.

Implemented entry points:

- `Tools/Benchmarks/Invoke-VulkanPerf.ps1` owns isolated build, launch,
  capture, manifest creation, retention, and evaluator invocation.
- `XREngine.Benchmarks --vulkan-perf` owns typed parsing, compatibility,
  percentile/variance, baseline, budget, classification, and exit-code logic.
- `Tools/Benchmarks/Measure-VulkanProfileOverhead.ps1` measures all four
  observer modes against `ReleaseBenchmark`.
- `XREngine.Benchmarks/VulkanPerformance/vulkan-performance-cohorts.json`
  is the tracked contract for presets, modes, windows, gate environment, and
  eight desktop/RVC cohorts.

Accepted baselines are never replaced by a normal run. `--accept-baseline`
requires an explicit baseline path and now also rejects an invalid or failing
candidate before writing it. OpenXR/foveation absence, missing required views,
fallbacks, readbacks, unstable workload identity, intrusive profile state, and
manifest mismatches are explicit failures rather than substitutions.

Canonical ownership:

- This document owns the measurement contract and completion gate.
- The linked baseline contract is a superseded source of requirements, the
  counter audit is historical evidence, and the editor-profiler tracker is a
  technical child for reducing observer overhead.

## Sequential Execution Contract

- This is the only entry point for the eight-workstream performance program.
- Do not start workstream 02 until every exit-gate item in this document is
  checked, supporting evidence is recorded, and this status is `Complete`.
- A benchmark failure is valid evidence. A missing, ambiguous, or intrusive
  measurement is not.
- Later workstreams may add metrics, but they must not redefine this baseline
  without updating the manifest and recapturing affected comparisons.

## Goal

Establish trustworthy, repeatable performance evidence and automated regression
gates before changing the renderer. The harness must separate render-thread CPU
preparation, Vulkan command encoding, waits, GPU execution, collection,
streaming, allocations, and readbacks.

This workstream establishes two distinct whole-frame promotion budgets:

| Promotion lane | Required measured workload | Target |
| --- | --- | ---: |
| Desktop-only | One desktop render using the canonical Deferred and Uber static/moving cohorts | At least 200 Hz; render p95 <= 5.00 ms |
| Vulkan RVC zero-readback | Vulkan `GpuIndirectZeroReadback` with, at minimum, one desktop render plus both RVC eye renders in the same frame | At least 120 Hz; whole-frame render p95 <= 8.33 ms |

The RVC target applies with foveation disabled or enabled and is not a per-eye
or per-render allowance. More than three renders or internal RVC views do not
relax the 8.33 ms whole-frame budget. This workstream establishes measurement
and failure gates; it does not require the existing renderer to pass them yet.

## Starting Evidence

- Deferred CPU-direct measured 9.36 ms p50 and 13.84 ms p95 render time.
- Deferred zero-readback FullBucketScan measured 24.97 ms p50 and 30.15 ms
  p95 while GPU time was only 2.93 ms p50.
- `VulkanFrame.RecordCommandBuffer` currently includes frame-data refresh and
  other preparation, so its name overstates actual command encoding.
- Dense GPU timestamps forced primaries dirty and therefore could not be used
  for CPU-path comparisons.
- Several counters describe requested work or accumulated candidates rather
  than work actually executed.

## Scope

- Define canonical, isolated Deferred and Uber benchmark scenes.
- Define static-camera and deterministic moving-camera sequences.
- Capture build, hardware, driver, settings, cache, validation, logging,
  resolution, and scene manifests.
- Provide a one-command quick/compare/gate benchmark path for rebuilding the
  Release editor, capturing a selected cohort, and evaluating the result.
- Preserve named `Diagnostics`, `DevelopmentProfile`, `CleanProfile`, and
  `ReleaseBenchmark` modes so a result states whether it is suitable for
  comparison.
- Attribute CPU time by thread and stage, including time outside
  `Vulkan.Frame.Total`.
- Attribute GPU time by pass without changing command-buffer reuse behavior.
- Count allocations, readback bytes and mappings, queue/fence waits, resource
  publications, primary/secondary records and reuse, and collect/render waits.
- Report exact primary dirty and reuse-rejection reasons, including expected
  and actual image-entry state.
- Add repeatable desktop 200+ Hz and Vulkan RVC zero-readback 120 Hz budget
  checks using p50, p95, p99, and worst-frame evidence.

## Non-Goals

- Repairing primary reuse.
- Replacing zero-readback material submission.
- Moving preparation to the collect-visible thread.
- Optimizing individual render passes or occlusion modes.
- Treating a short quick run as promotion evidence.
- Reimplementing the editor lifecycle or whole-frame Vulkan workload as an
  in-process BenchmarkDotNet microbenchmark.

## Benchmark Execution Path

Implement this path as the first Phase 0 deliverable, before renderer changes
begin:

- Keep editor-process launch, environment isolation, stability detection,
  capture, shutdown, and raw summary generation in
  `Tools/Measure-VulkanFrameLoop.ps1` and
  `Tools/Measure-GameLoopRenderPipeline.ps1`.
- Add a thin `Tools/Benchmarks/Invoke-VulkanPerf.ps1` entry point that can build
  the Release editor and invoke the existing capture harness with a named
  preset and cohort.
- Add an `XREngine.Benchmarks --vulkan-perf` command for typed manifest/result
  parsing, compatibility checks, statistics, baseline comparison, budget
  evaluation, and process exit codes. Do not add a project reference from the
  benchmark project to the editor or run the renderer in the benchmark
  process.
- Keep canonical cohort definitions, required output/view-family identities,
  budgets, and preset settings in tracked machine-readable configuration.
  Keep measured machine results under the current
  `Build/_AgentValidation/<run>/` evidence root and reference accepted evidence
  from the investigation document.
- Require an explicit baseline path for comparison. Replacing an accepted
  baseline must require a separate explicit `AcceptBaseline` action; a normal
  run must never normalize a regression by overwriting its comparison input.

Named presets:

| Preset | Purpose | Minimum behavior | Promotion evidence |
| --- | --- | --- | --- |
| `Quick` | Tight feedback after a code change | Release, warm cache, one selected canonical cohort, one short repetition, stability and validity gates enabled | No; report `NonPromotableQuickRun` explicitly |
| `Compare` | Matched before/after evaluation | Release, warm cache, at least three repetitions of each selected cohort, compatible manifests, variance and absolute/relative deltas | Yes, for the selected cohorts when full capture windows are used |
| `Gate` | Workstream completion and regression gate | Full canonical desktop and available RVC matrix, at least three repetitions, full capture windows, absolute budgets and baseline deltas | Yes |

The quick preset may shorten warmup and capture durations, but it must not
disable workload-stability checks, manifest compatibility checks, zero-readback
proof, required-render-count checks, fallback detection, or capture-validity
checks. It may report absolute budget status for feedback, but its overall
result must never be `PromotionPass`.

## Phase 0 - Freeze The Benchmark Contract

- [x] Implement the one-command benchmark execution path and the
  `Quick`/`Compare`/`Gate` presets defined above.
- [x] Add fixture-driven tests for manifest compatibility, percentile and
  variance calculations, absolute budgets, baseline deltas, invalid-capture
  rejection, and exit codes without requiring a GPU.
- [x] Select the target GPU, driver, display mode, resolution, and operating
  system used for the primary gate.
- [x] Create one Deferred-only and one Uber-only scene with identical geometry,
  camera framing, lights, and post-process settings where applicable.
- [x] Record static and deterministic moving-camera scripts.
- [x] Define Vulkan RVC zero-readback cohorts that render, at minimum, the
  desktop output and both eye outputs in every measured frame.
- [x] Capture RVC cohorts with foveation disabled and enabled where the runtime
  supports foveation; unsupported modes must be reported explicitly and cannot
  be silently substituted.
- [x] Define cold-start, warmup, steady-state, and streaming-churn capture
  windows.
- [x] Define clean and diagnostic profiles and list the expected overhead of
  each profile.
- [x] Define the exact behavior of `Diagnostics`, `DevelopmentProfile`,
  `CleanProfile`, and `ReleaseBenchmark`, including validation, debug labels,
  logging, profiler panels, ImGui, and dynamic-text overlays.
- [x] Store a machine-readable manifest beside every result.
- [x] Include source commit, dirty-worktree state, executable hash, backend,
  GPU, driver, build configuration, scene and camera identity, lights, viewport
  extent and render scale, mesh strategy, stereo and mirror mode, active render
  features, validation/debug state, profiler UI and collection toggles,
  shader/texture cache state, GPU clock policy where available, and the exact
  log-session path in that manifest.
- [x] Provide canonical clean desktop and available OpenXR benchmark launch
  tasks that do not depend on manually edited editor preferences.

Acceptance criteria:

- [x] A developer can rebuild and run one selected quick cohort from a clean
  shell with one command, and receives a result path plus a meaningful exit
  code.
- [x] Quick results are visibly non-promotable and cannot overwrite an accepted
  baseline.
- [x] Comparing incompatible manifests fails with the exact mismatched fields
  instead of producing a performance delta.
- [x] A run can be reproduced without inheriting editor-global preferences.
- [x] Deferred/Uber comparisons differ only in declared render-path settings.
- [x] Every result identifies whether it is valid for performance comparison.

## Phase 1 - Correct Stage Attribution

- [x] Split command encoding from frame-op construction, descriptor/uniform
  refresh, resource planning, submission, and presentation.
- [x] Measure render-thread work before and after `Vulkan.Frame.Total`.
- [x] Attribute queue-lock acquisition and fence waits separately.
- [x] Attribute collect-visible active work and backpressure wait separately.
- [x] Attribute software occlusion selection, sort, raster, query, and Hi-Z
  phases separately.
- [x] Split actual Vulkan encoding into op dispatch, context/pass transitions,
  barrier planning and emission, descriptor publication and binding, pipeline
  and mesh binding, draw/dispatch calls, uploads, secondary execution, and
  debug-label work where those costs are material.
- [x] Report actual primary and secondary encoding counts rather than only
  scheduler decisions.
- [x] Prove that normal GPU timings do not force a dirty primary.
- [x] Attribute profiler data ingestion, aggregation, graph/table preparation,
  formatted-text work, ImGui draw, dynamic-text recording, and Vulkan overlay
  recording separately when editor diagnostics are enabled.

Acceptance criteria:

- [x] Mutually exclusive CPU stages reconcile to the measured render-thread
  frame within a documented tolerance.
- [x] A clean primary reuse reports zero command encoding while still exposing
  any per-frame refresh cost.
- [x] Every dirty decision reports one or more precise, testable causes.

## Phase 2 - Allocation, Readback, And Work Counters

- [x] Count render-thread allocations and bytes by stage.
- [x] Count GPU-to-CPU mappings, bytes, and waits by feature.
- [x] Count configured capacity, candidates examined, commands emitted,
  commands executed, and objects culled as distinct values.
- [x] Count Vulkan frame ops and render commands by pass and operation kind.
- [x] Count context/pass changes, barriers by kind, descriptor/pipeline/mesh
  binds, avoided redundant binds, draw/dispatch calls, uploads, overlay command
  count, visible profiler rows, graph samples, formatted strings, and glyph or
  quad counts where applicable.
- [x] Count render-thread jobs by source, duration, queue delay, and over-budget
  duration.

Acceptance criteria:

- [x] A claimed zero-readback run proves zero readback mappings and bytes.
- [x] A claimed zero-allocation hot path proves zero steady-state allocations.
- [x] Candidate counts cannot be mistaken for actual draw or cull counts.

## Phase 3 - Repeatability And Automated Gates

- [x] Run at least three warm repetitions of each canonical cohort.
- [x] Publish p50, p95, p99, maximum, missed-5.00-ms desktop-frame count,
  missed-8.33-ms RVC-frame count, and sample count for every top-level and
  stage metric.
- [x] Define and enforce an acceptable run-to-run variance threshold before
  using small deltas as evidence.
- [x] Add regression comparisons for static and moving Deferred and Uber
  cohorts.
- [x] Make the `Compare` and `Gate` commands return nonzero for invalid
  captures, incompatible manifests, threshold failures, silent fallbacks,
  readback violations, or insufficient required renders.
- [x] Make the desktop-only 200+ Hz gate fail visibly when render p95 exceeds
  5.00 ms.
- [x] Make the Vulkan RVC zero-readback 120 Hz gate fail visibly when
  whole-frame render p95 exceeds 8.33 ms, fewer than three required renders
  execute, any current-frame GPU readback occurs, or a silent fallback is used.
- [x] Keep intrusive diagnostic captures out of pass/fail performance totals.
- [x] Measure each diagnostic/profile mode against `ReleaseBenchmark` and
  record observer overhead rather than assuming it is negligible.

Acceptance criteria:

- [x] Three consecutive unchanged runs fall within the declared variance
  threshold or the environmental source of variance is resolved.
- [x] The gate distinguishes CPU-bound, GPU-bound, wait-bound, and mixed
  failures.
- [x] Baseline failures are recorded as baseline failures, not normalized away.

## Exit Gate

- [x] All canonical cohorts have manifests and repeatable evidence.
- [x] CPU stages reconcile, GPU timing is non-intrusive, and exact dirty reasons
  are available.
- [x] Allocation, readback, queue/fence, collection, and render-job counters are
  trustworthy.
- [x] Automated 5.00 ms desktop and 8.33 ms RVC zero-readback p50/p95/p99
  reporting exists and detects a seeded regression in either lane.
- [x] Required targeted tests and Release build/run validation pass.
- [x] Results and evidence paths are recorded in the investigation document.
- [x] This document is marked `Complete`.

Only after this gate is complete may work begin on
[02 - Vulkan Primary Reuse Correctness](02-vulkan-primary-reuse-correctness-todo.md).
