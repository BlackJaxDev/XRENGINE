# 03 - True GPU-Driven Zero-Readback Submission TODO

Last Updated: 2026-07-28
Owner: Rendering / Vulkan / GPU-Driven Submission
Status: Implementation Complete; Promotion Blocked By Validation
Sequence: 03 of 08
Predecessor: [02 - Vulkan Primary Reuse Correctness](02-vulkan-primary-reuse-correctness-todo.md)
Blocks: [04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md)

Primary evidence:

- [Vulkan Framerate Root-Cause Investigation](../../../investigations/rendering/vulkan-framerate-root-cause-2026-07-28.md)

Related trackers:

- [Compact Zero-Readback Rendering](compact-zero-readback-rendering-todo.md)
- [Material Table And Texture Binding Ladder](material-table-and-texture-binding-ladder-todo.md)
- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)

Technical children:

- [Compact Zero-Readback Rendering](compact-zero-readback-rendering-todo.md)
  owns compaction, overflow, barrier batching, indirect-count, and
  delayed-diagnostic algorithms for this workstream. Its Hi-Z phase is deferred
  to workstream 07.
- [Material Table And Texture Binding Ladder](material-table-and-texture-binding-ladder-todo.md)
  owns the runtime binding-rung and material-table representation.
- This document remains the canonical production contract and completion gate;
  a child tracker cannot promote the zero-readback path independently.

## Sequential Execution Contract

- Do not start this workstream until workstream 02 is marked `Complete`.
- Preserve the corrected primary-reuse behavior while changing submission.
- Do not start workstream 04 until every exit-gate item here is checked,
  evidence is recorded, and this status is `Complete`.

## Goal

Make the default GPU-driven path genuinely zero-readback and proportional to
active GPU work. Remove CPU material-slot-by-tier fan-out, CPU enumeration of
GPU-produced active lists, and O(all frame ops) refresh work from clean primary
reuse.

## Starting Evidence

- Deferred `GpuIndirectZeroReadback` with `FullBucketScan` measured 24.97 ms
  p50 render time, 13.45 ms Vulkan frame time, 11.60 ms record/preparation,
  and only 2.93 ms GPU time.
- It scanned 77,760 candidate buckets over 480 samples: exactly 162 scans per
  frame.
- Clean primary reuse still spent 9.67 ms refreshing all frame ops.
- ActiveBucketList read about 556 bytes through three mappings per sampled
  frame.
- MaterialTable and BindlessMaterialTable read about 536 bytes through two
  mappings per frame.
- Overrides and depth/normal variants can fall back to FullBucketScan.

## Zero-Readback Contract

A conforming path:

- performs no GPU-to-CPU mapping, count readback, fence wait, or CPU
  enumeration to decide the current frame's material or draw work;
- does not loop configured material capacity times atlas tiers on the render
  thread;
- keeps active counts, compaction, and indirect dispatch GPU-owned;
- represents overrides and depth/normal variants without silently returning to
  FullBucketScan;
- retains bounded delayed diagnostics that cannot affect current-frame
  submission.

## Non-Goals

- Moving all backend preparation to collect-visible; that is workstream 04.
- Parallel command recording; that is workstream 05.
- Forward prepass pass-count optimization; that is workstream 06.

## Phase 0 - Characterize Fan-Out And Scaling

- [x] Capture configured material slots, active materials, tiers, render
  passes, emitted frame ops, and executed indirect commands.
- [ ] Sweep object and material counts independently to confirm scaling.
- [x] Attribute command-building time outside the Vulkan frame and refresh time
  inside primary reuse.
- [x] Add tests that fail if a zero-readback strategy maps an active/count
  buffer during current-frame submission.

Acceptance criteria:

- [x] The baseline shows exactly where work scales with configured capacity
  instead of active work.
- [x] Readback-assisted strategies cannot be labeled `ZeroReadback`.

## Phase 1 - Define GPU-Owned Active Work

- [x] Define the compact GPU representation for active pass, material,
  variant, and tier work.
- [x] Define indirect-count production and consumption without CPU inspection.
- [x] Define bounded capacity, overflow behavior, and visible diagnostics.
- [x] Use subgroup/workgroup compaction without a production per-survivor
  atomic bottleneck, with a declared lower capability rung where subgroup
  arithmetic is unavailable.
- [x] Clamp every GPU-produced active/count output on overflow, preserve visible
  conservative work where possible, and grow capacity only at a safe later
  frame boundary.
- [x] Cover opaque, masked, transparent where supported, overrides,
  depth/normal, shadow, and other required variants.
- [x] Define stable descriptor/material-table ownership across in-flight frames.
- [x] Define a zero-readback interface for an optional GPU-produced visibility
  input so workstream 07 can add or bypass Hi-Z without changing the compact
  submission contract. Do not implement or tune Hi-Z in this workstream.

Acceptance criteria:

- [x] Every required render variant has a zero-readback path or is explicitly
  declared unsupported with a visible failure.
- [x] Capacity and overflow cannot cause an out-of-bounds dispatch or a silent
  CPU fallback.

## Phase 2 - Remove CPU Fan-Out And Refresh

- [x] Replace `FullBucketScan` as the default submission mechanism.
- [x] Remove current-frame active-list and material-count mappings.
- [x] Emit a bounded command structure whose CPU size scales with pass groups,
  not material capacity times tiers.
- [x] Move changing per-frame values into GPU-visible data that does not
  require visiting every reusable frame op.
- [x] Keep delayed counters or debug snapshots off the current-frame critical
  path.
- [x] Batch resource-specific compute-to-indirect barriers instead of emitting
  broad or per-dispatch synchronization, and consume generated counts with the
  backend indirect-count API.
- [x] Select a reported material/texture binding rung by runtime capability;
  any coarse bucket fallback must consume compact active work rather than full
  material-table capacity.
- [x] Remove or rename settings whose advertised zero-readback behavior is no
  longer accurate.

Acceptance criteria:

- [x] Steady-state current-frame draw selection reports zero readback mappings,
  bytes, and waits.
- [x] Clean primary reuse performs zero O(all frame ops) descriptor/frame-data
  refresh.
- [x] No render-thread loop enumerates every configured material slot and tier.

## Phase 3 - Correctness And Performance Validation

- [ ] Validate material changes, streamed texture publication, overrides,
  depth/normal prepass, shadows, transparent content, and capacity overflow.
- [ ] Validate empty, exact-capacity, overflow, delayed-diagnostic, and
  optional-visibility-input bypass cases inherited from the technical children.
- [ ] Compare CPU-direct and GPU-driven output for deterministic scenes.
- [x] Run static and moving canonical Deferred and Uber cohorts.
- [ ] Run the RVC acceptance workload with at least one desktop render and both
  RVC eye renders in every measured frame, with foveation disabled and enabled
  where supported.
- [x] Compare stage p50/p95/p99, render-thread allocations, frame-op count,
  readbacks, and GPU time against workstreams 01 and 02.
- [x] Verify primary reuse remains correct.

Acceptance criteria:

- [ ] Image comparisons and draw/material counters show no missing or
  mis-materialed geometry.
- [ ] The zero-readback path meets the CPU-stage budget established in
  workstream 01 and is not promoted if it is slower than CPU-direct beyond the
  declared variance and accepted GPU-driven overhead.
- [ ] The RVC workload proves zero current-frame readback, no silent fallback,
  and at least three executed renders. Record its whole-frame 8.33 ms result;
  final RVC 120 Hz promotion remains owned by workstream 08 after the
  intervening graph, scheduling, and tail-cost workstreams complete.
- [ ] Steady-state render-thread allocations are zero.

## Exit Gate

- [x] The default GPU-driven path satisfies the zero-readback contract.
- [ ] Required compact-zero-readback child gates for active lists, compaction,
  overflow, barrier batching, indirect-count, and delayed diagnostics are
  complete. The child's Hi-Z phase remains blocked until workstream 07.
- [x] The selected material binding rung is capability-probed, reported, and
  proven to preserve compact active-work scaling.
- [x] FullBucketScan is removed from steady production submission or retained
  only as an explicitly named diagnostic fallback.
- [x] CPU work scales with active pass groups rather than configured material
  capacity.
- [ ] Primary reuse, validation, focused tests, Release build, and canonical
  performance cohorts pass.
- [x] Evidence and any unsupported variants are recorded in the investigation.
- [ ] This document is marked `Complete`.

## Implementation And Validation Result

The production implementation is complete for the bounded Vulkan
material-table rung:

- `BindlessMaterialTable` is the engine and benchmark default.
- The GPU compacts into three fixed atlas-tier groups with a portable
  64-lane workgroup prefix scan and one clamped reservation per group/tier.
- Vulkan consumes the three GPU counts through indirect-count draws; the
  render thread does not map or enumerate current-frame active work.
- Full-capacity and active-list readback paths are explicitly named
  diagnostics.
- Descriptor-indexed binding and the `WorkgroupPrefixScan64` lower-capability
  compaction rung are reported with selection reasons.
- Forward opaque/masked depth-normal prepass, override rows, and normal/alpha
  texture evaluation have compact variants. Exact transparency and arbitrary
  forward shader semantics remain explicitly unsupported, counted, warned,
  and skipped without a CPU/full-scan fallback.

Release short-window evidence is under
`Build/_AgentValidation/20260728-vulkan-framerate-root-cause/workstream-03-*`.
All four desktop captures reported `Bindless`, three pass groups, zero
capture-window readback bytes/mappings, zero full scans, zero fallback events,
zero forbidden fallback events, and zero VUID/submission rejection:

| Cohort | Samples | Render p50 / p95 / p99 | Vulkan frame p50 / p95 | Primary reuse |
| --- | ---: | ---: | ---: | ---: |
| Deferred static | 740 | 7.444 / 24.226 / 37.839 ms | 4.048 / 12.581 ms | 97.84% |
| Deferred moving | 253 | 14.840 / 21.047 / 53.308 ms | 7.458 / 11.528 ms | 96.84% |
| Uber static | 186 | 21.384 / 29.349 / 92.268 ms | 9.296 / 15.425 ms | 95.16% |
| Uber moving | 175 | 23.688 / 33.131 / 70.111 ms | 10.456 / 15.525 ms | 97.71% |

An earlier cleaner Deferred-static probe reached 5.343 ms render p50,
3.020 ms Vulkan-frame p50, and 99.93% primary reuse. The starting
full-capacity zero-readback result was 24.97 ms render p50. The matched
Uber-static CPU-direct check remained materially faster at 9.159 ms p50 than
the compact GPU path's 21.384 ms, however, and the short captures retained
recording/refresh allocations. The canonical stability gate also timed out
because texture upload/retirement activity never became quiescent.

The OpenXR/RVC runtime was unavailable, so the required three-render eye-submit
workload could not be measured. `rdc doctor` passed, but the automated capture
target disconnected before producing an `.rdc`. Because performance,
allocation, image-comparison, RVC, and GPU-trace gates remain open, this
workstream is not promoted and workstream 04 remains blocked.

Only after this gate is complete may work begin on
[04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md).
