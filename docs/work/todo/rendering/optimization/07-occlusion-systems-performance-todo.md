# 07 - Occlusion Systems Performance TODO

Last Updated: 2026-07-28
Owner: Rendering / Visibility / Vulkan
Status: Blocked By Workstream 06
Sequence: 07 of 08
Predecessor: [06 - Forward+ Prepass And Render-Graph Cost](06-forward-prepass-and-render-graph-cost-todo.md)
Blocks: [08 - Render Tail Latency](08-render-tail-latency-shadows-streaming-jobs-todo.md)

Canonical ownership: this document owns execution order, end-to-end
performance evidence, mode disposition, and promotion/default policy for every
occlusion system. The related trackers below remain the owners of their
algorithm, architecture, and correctness matrices; they do not independently
promote a mode or waive this workstream's exit gate.

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md)

Related trackers:

- [GPU-Driven Occlusion Culling Architecture](../gpu/gpu-driven-occlusion-culling-architecture-todo.md)
- [Compact Zero-Readback Rendering](compact-zero-readback-rendering-todo.md)
- [CPU Async Query Camera Motion](../cpu-async-query-camera-motion-todo.md)
- [Masked Software Occlusion Culling](../masked-software-occlusion-culling-todo.md)

Child-tracker disposition:

- GPU-driven architecture owns persistent visibility, two-phase Hi-Z,
  BVH/meshlet integration, and stereo data contracts.
- Compact zero-readback Phase 4 owns the focused one-/two-phase Hi-Z render
  graph and indirect-submission implementation used here.
- CPU async query owns temporal-query correctness, camera-motion, hierarchy,
  and physical-eye validation.
- Masked software occlusion owns scalar/SIMD implementation, selector
  correctness, visualization, and stereo validation.
- The effectiveness thresholds, production/diagnostic/retired decision, and
  user-facing promotion decision for all three modes are made only here.

## Sequential Execution Contract

- Do not start this workstream until workstream 06 is marked `Complete`.
- Evaluate GPU Hi-Z, CPU software, and CPU query occlusion as separate systems;
  one mode's cadence or reuse behavior is not evidence for another.
- Do not start workstream 08 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

## Goal

Make each retained occlusion mode produce a measured net frame-time benefit in
the scene class where it is enabled, with negligible overhead when ineffective.
Modes that cannot meet that contract must remain disabled, be clearly
diagnostic/experimental, or be retired.

## Starting Evidence

- Disabled occlusion already missed even the historical 120 Hz comparison
  threshold, so occlusion is not the universal baseline cause and cannot
  explain the miss against the stricter 200+ Hz desktop target.
- CPU software occlusion increased Deferred CPU-direct from 9.36 to 15.89 ms
  p50 while performing four final AABB tests and zero culls.
- CPU query async measured 4.70 ms p50 but 13.86 ms p95, tested two objects,
  culled zero, and concentrated query records on one cadence frame.
- GPU Hi-Z increased Deferred GPU-driven rendering from 24.97 to 182.04 ms p50;
  command preparation reached 145.85 ms and GPU time 17.49 ms.
- The Hi-Z pyramid is RGBA32F and incurs initialization, per-mip
  dispatch/barrier, refinement, and count-copy work.
- Existing phase counters can be mistaken for actual draw or cull counts.

## Common Effectiveness Contract

For each mode:

- measure total added CPU and GPU cost, work removed, actual objects/draws
  culled, and end-to-end frame delta;
- compare p50, p95, and p99, not median alone;
- avoid current-frame readback and render-thread stalls;
- bypass cheaply when candidate count or expected benefit is too low;
- preserve conservative visibility and expose false-positive/false-negative
  diagnostics.

## Phase 0 - Correct Counters And Scenarios

- [ ] Separate candidates, occluders, tested bounds, rasterized triangles,
  queries issued/resolved, Hi-Z invocations, indirect commands, and actual
  culls.
- [ ] Attribute selection, sorting, rasterization, query, pyramid, refinement,
  barriers, copies, and readback separately.
- [ ] Export the existing CPU software begin, occluder-selection/raster,
  selected/rasterized occluder, closed-tile, and AABB-test counters to the
  profile capture and profiler packet; `cpu_soc_tested`/`cpu_soc_culled` alone
  are not an effectiveness or cost measurement.
- [ ] Make Hi-Z telemetry distinguish invocation count from frame count and
  candidate count from actual phase-one/phase-two indirect draws. Report when
  phase-two counts are unavailable because the production path has no CPU
  readback.
- [ ] Create open, moderate-occlusion, occluder-heavy, masked-geometry, static,
  and deterministic moving-camera scenarios.
- [ ] Capture disabled baselines after workstream 06.

Acceptance criteria:

- [ ] A mode cannot report success from cadence reuse or candidate counts when
  it culled no work.
- [ ] Each scenario has stable expected visibility results.

## Phase 1 - CPU Software Occlusion Decision

- [ ] Bound candidate selection, sort, and triangle-raster work.
- [ ] Remove eligible pure work from the render thread or prove why it must
  remain there.
- [ ] Add an early bypass based on candidate/occluder count and prior benefit.
- [ ] Validate masked, near-plane, large-bound, and moving-camera conservatism.
- [ ] Measure net benefit in the occluder-heavy scenario and overhead in the
  open scenario.

Acceptance criteria:

- [ ] CPU software mode has positive p95 frame-time benefit in its target
  scenario and at most 0.25 ms p95 overhead when bypassed, or it is explicitly
  retired/kept diagnostic-only.

## Phase 2 - CPU Query Async Decision

- [ ] Separate query-result benefit from primary-record cadence.
- [ ] Define query latency, refresh cadence, stale-result, and camera-motion
  policies.
- [ ] Avoid CPU waits and current-frame result dependency.
- [ ] Verify query frames do not create unacceptable p95/p99 spikes.
- [ ] Measure actual draw work removed and net end-to-end benefit.

Acceptance criteria:

- [ ] CPU query mode improves p95, not only p50, in its target scenario and has
  bounded tail latency, or it is explicitly retired/kept diagnostic-only.

## Phase 3 - GPU Hi-Z Decision

- [ ] Remove CPU frame-op fan-out already addressed by workstream 03 from the
  Hi-Z path.
- [ ] Use a minimal depth format and persistent resources appropriate to the
  algorithm.
- [ ] Bound pyramid construction, per-mip barriers, refinement, and count-copy
  work.
- [ ] Consume visibility entirely on GPU without current-frame readback.
- [ ] Add a cheap bypass for insufficient candidates or expected occlusion.
- [ ] Validate one-phase/two-phase behavior and actual indirect draw counts.
- [ ] Complete and validate Phase 4 of
  [Compact Zero-Readback Rendering](compact-zero-readback-rendering-todo.md)
  under this workstream, not during workstream 03.

Acceptance criteria:

- [ ] GPU Hi-Z has zero current-frame readback and positive p95 end-to-end
  benefit in the occluder-heavy GPU-driven scenario.
- [ ] Ineffective-case overhead is within the accepted budget and never
  approaches the measured 145.85 ms CPU preparation failure.
- [ ] If these criteria cannot be met, GPU Hi-Z remains disabled by default and
  is clearly marked experimental or is removed.

## Phase 4 - Promotion Policy

- [ ] Define explicit selection thresholds and hysteresis for every retained
  adaptive mode.
- [ ] Keep a user-forced diagnostic mode for reproducible testing.
- [ ] Record why each mode is production, opt-in, diagnostic-only, or retired.
- [ ] Run static/moving and open/occluded canonical performance matrices.

Acceptance criteria:

- [ ] Every enabled-by-default mode demonstrates positive net p95 benefit.
- [ ] Disabled/bypassed occlusion has negligible measured overhead.
- [ ] Mode transitions do not break primary reuse or create tail spikes.

## Exit Gate

- [ ] CPU software, CPU query, and GPU Hi-Z each have a documented disposition
  supported by end-to-end evidence.
- [ ] Retained production modes meet the common effectiveness contract.
- [ ] GPU Hi-Z remains off by default unless its full promotion gate passes.
- [ ] The compact zero-readback Hi-Z child phase and GPU-driven architecture
  requirements needed by every retained Hi-Z mode are complete.
- [ ] Visibility correctness, Release build, focused tests, validation layers,
  and canonical performance cohorts pass.
- [ ] Evidence and scenario-specific selection policy are recorded.
- [ ] This document is marked `Complete`.

Only after this gate is complete may work begin on
[08 - Render Tail Latency](08-render-tail-latency-shadows-streaming-jobs-todo.md).
