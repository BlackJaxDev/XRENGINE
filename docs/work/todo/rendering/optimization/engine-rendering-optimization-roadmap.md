# Engine Rendering Optimization Roadmap

Last Updated: 2026-08-01
Owner: Rendering
Status: Active; Workstreams 03-05 Pre-06 Gate Open, Workstream 06 Blocked

## Purpose

This is the umbrella index for renderer performance work. The numbered Vulkan
workstreams own the current desktop 200+ Hz and RVC 120 Hz program; focused
TODOs own backend-neutral, VR, material, and future-renderer work.

The production targets remain:

- desktop-only render p95 at or below 5.00 ms; and
- Vulkan `GpuIndirectZeroReadback` whole-frame RVC render p95 at or below
  8.33 ms, including the desktop and both eye renders.

Canonical contracts:

- [Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md)
- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Production GPU-Driven Rendering Roadmap](../gpu/production-rendering-pipeline-roadmap.md)

## Current Vulkan Sequence

| # | Status | Remaining owner |
| --- | --- | --- |
| 01 | Complete | Measurement, attribution, manifests, and automated gates are recorded in the [framerate investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md). |
| 02 | Complete | Primary state, invalidation, and stable reuse are recorded in the same [investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md). |
| 03 | Implementation complete; pre-06 validation open | Run scaling, parity, overflow, mutation, allocation, crossover, and desktop/RVC acceptance in the [combined 03-05 gate](../../../testing/rendering/03-05-optimization-validation-todo.md#workstream-03-validation). |
| 04 | Implementation closure and validation open | Finish producer-side binding/data preparation and legacy cutover, then pass publication, parity, lifetime, latency, and overlap acceptance in the [combined 03-05 gate](../../../testing/rendering/03-05-optimization-validation-todo.md#workstream-04-completion-and-validation). |
| 05 | Implementation complete; pre-06 validation open | Prove real worker overlap, zero allocation, parity, fallback truth, and lifecycle safety in the [combined 03-05 gate](../../../testing/rendering/03-05-optimization-validation-todo.md#workstream-05-validation). |
| 06 | Blocked by the 03-05 gate | Remove unjustified prepasses, copies, replays, barriers, and disabled-feature GPU work after the [combined gate](../../../testing/rendering/03-05-optimization-validation-todo.md) is complete. Source: [Forward+ and render-graph cost](06-forward-prepass-and-render-graph-cost-todo.md). |
| 07 | Blocked by 06 | Measure and retain, gate, or retire CPU software, CPU query, and GPU Hi-Z occlusion modes. Source: [occlusion systems](07-occlusion-systems-performance-todo.md). |
| 08 | Blocked by 07 | Bound shadows, streaming publication, queue waits, render-thread jobs, and final tail latency. Source: [render tail latency](08-render-tail-latency-shadows-streaming-jobs-todo.md). |

The earlier `Implementation Complete; Acceptance Deferred` exception for this
boundary is retired. Workstream 06 may proceed only after the combined 03-05
gate is marked `Complete`. Targeted tests, narrow builds, and implementation
smokes remain mandatory during each workstream.

## Active Vulkan Command-Execution Architecture Work

The [Vulkan Command Recording Architecture Optimization TODO](vulkan-command-recording-architecture-optimization-todo.md)
is the active implementation tracker for the reopened workstream-04 binding
and data handoff and the follow-on stable command-execution architecture. It
extends workstream 05's completed worker mechanics; it does not replace or
reorder numbered workstreams 06-08.

- [x] Implement linked binding schemas, frequency-owned payloads, prepared mesh
  state, typed primary plans, recorded artifacts, worker arenas,
  command-buffer-local image journals, typed eligibility, and the initial
  secondary-family expansion.
- [ ] Close the remaining Phase-1 producer-boundary, bounded-storage,
  equivalence, and legacy-cutover items through Phase 0 of the
  [combined 03-05 gate](../../../testing/rendering/03-05-optimization-validation-todo.md#phase-0---close-workstream-04-implementation).
- [ ] Run the remaining architecture acceptance and hardware matrix through the
  workstream-04 and workstream-05 sections of that same gate.

## Checked Off

- [x] Establish reproducible benchmark cohorts, manifests, stage attribution,
  allocation/readback counters, observer-overhead reporting, and automated
  5.00 ms desktop / 8.33 ms RVC gates.
- [x] Repair Vulkan primary image-state merging and invalidation so eligible
  stable CPU-direct frames reuse primary and secondary command buffers.
- [x] Land the bounded Vulkan zero-readback submission architecture: fixed
  GPU-owned tier groups, active-work compaction, indirect-count submission,
  overflow clamping/diagnostics, specific barriers, and no production
  full-bucket scan or current-frame selection readback.
- [x] Select and report the bounded Vulkan material-binding rung through
  runtime capability probing; unsupported required variants fail visibly.
- [x] Land the bounded double-buffered collect-visible package foundation for
  pass ordering, selection identity, revisions, and package freshness.
- [x] Land persistent Vulkan command-recording workers with per-worker command
  pools, deterministic merge order, bounded waits, failure quarantine, and
  truthful activation telemetry.

The two fully checked implementation checklists for workstreams 01 and 02 were
removed after their durable outcome and validation evidence were consolidated
in the framerate investigation. Their completed contracts remain summarized
above.

## Remaining Numbered Work

- [ ] Complete the [workstreams 03-05 validation gate](../../../testing/rendering/03-05-optimization-validation-todo.md),
  including workstream-04 implementation closure, and unblock workstream 06.
- [ ] Implement workstream 06 and justify every remaining geometry replay,
  copy, transition, barrier, and optional full-resolution producer with
  RenderDoc and matched timing evidence.
- [ ] Implement workstream 07 and give each occlusion mode an evidence-backed
  production, opt-in, diagnostic-only, or retired disposition.
- [ ] Implement workstream 08, attribute every tail spike, and meet the final
  desktop and RVC budgets without hidden fallback or current-frame readback.
- [ ] Run the [01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)
  after workstreams 06-08 are implementation complete, consuming the accepted
  03-05 manifests as the pre-06 baseline.
- [ ] Synchronize final evidence and status into this roadmap and the
  [Vulkan Core Hardening And Device-Loss TODO](../vulkan-core-hardening-and-device-loss-todo.md).

## Remaining Broader Lanes

These lanes do not override the numbered Vulkan order.

| Lane | Remaining work |
| --- | --- |
| CPU direct | Finish the cross-backend constant/upload/state-cache and warmup boundaries not already owned by workstreams 01, 02, and 04. See [CPU Direct Fast Path](cpu-direct-fast-path-todo.md). |
| Compact zero-readback | Complete deferred validation plus the workstream-07 Hi-Z phases. See [Compact Zero-Readback Rendering](compact-zero-readback-rendering-todo.md). |
| Material and texture binding | Generalize the bounded Vulkan rung into pass-declared layouts, cross-backend array/bindless/coarse behavior, sparse/virtual interfaces, dirty updates, and prewarm. See [Material Table And Texture Binding Ladder](material-table-and-texture-binding-ladder-todo.md). |
| Default pipeline GPU cost | Execute the workstream-06-owned pass, quality-scaling, GTAO, lighting, and post-process measurements. See [Default Pipeline GPU Hotspots](default-pipeline-gpu-hotspots-todo.md). |
| Future visibility renderer | Complete the ordered [Advanced Render Pipeline Architectural Refactor](../architectural-refactor/00-advanced-render-pipeline-refactor-todo.md); the old standalone visibility-buffer and Deferred+ TODOs are superseded. |
| XR | Complete the stereo-mode, per-eye temporal/motion, VRS/foveation, reprojection, and whole-frame budget contract. See [VR Rendering Performance Contract](vr-rendering-performance-contract-todo.md). |
| Avatar and cooked assets | Publish representation and cooked-variant identity through material, streaming, prewarm, submission, and profiler paths. See the [Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md). |
| Profiling tools | Bound visible editor-profiler cost and finish presentation-independent component profiling. See [Editor Profiler And UI Render Cost](editor-profiler-ui-render-cost-todo.md) and [Vulkan Headless MCP Component Profiling](vulkan-headless-mcp-component-profiling-todo.md). |

## Invariants

- CPU direct remains the correctness baseline and an explicit fallback; a
  requested accelerated strategy never enters it silently.
- `GpuIndirectZeroReadback` never reads current-frame visibility, counters,
  ranges, or query results needed for submission.
- GPU-driven cost scales with active work, not configured capacity.
- Stable Vulkan command topology is reused across data-only changes.
- Measured frames are warmed; late shader, pipeline, asset, and texture work is
  attributed rather than hidden.
- Render-submission hot paths avoid steady-state heap allocation.
- Every performance claim has counters, a reproducible manifest, and matched
  CPU/GPU evidence.
- VR results state the active stereo mode and use the whole submitted XR frame
  budget.

## Final Closeout

- [ ] Pass focused deterministic tests, Release builds, Vulkan validation, and
  required RenderDoc inspections.
- [ ] Pass static, moving, mutation, streaming, resize, device-loss, shutdown,
  and repeated-start stress relevant to the changed workstreams.
- [ ] Prove zero current-frame readback, accepted steady-state allocation
  budgets, stable reuse, deterministic output, and no silent fallback.
- [ ] Pass the canonical desktop and available RVC Gate cohorts with the
  required repetitions and variance limits.
- [ ] Record the accepted evidence, hardware/runtime manifest, risks, and any
  explicit v1 deferral before promoting Vulkan Phase 5.2.
