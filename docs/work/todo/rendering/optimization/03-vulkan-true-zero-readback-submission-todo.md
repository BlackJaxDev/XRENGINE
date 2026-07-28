# 03 - True GPU-Driven Zero-Readback Submission TODO

Last Updated: 2026-07-28
Owner: Rendering / Vulkan / GPU-Driven Submission
Status: Blocked By Workstream 02
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

- [ ] Capture configured material slots, active materials, tiers, render
  passes, emitted frame ops, and executed indirect commands.
- [ ] Sweep object and material counts independently to confirm scaling.
- [ ] Attribute command-building time outside the Vulkan frame and refresh time
  inside primary reuse.
- [ ] Add tests that fail if a zero-readback strategy maps an active/count
  buffer during current-frame submission.

Acceptance criteria:

- [ ] The baseline shows exactly where work scales with configured capacity
  instead of active work.
- [ ] Readback-assisted strategies cannot be labeled `ZeroReadback`.

## Phase 1 - Define GPU-Owned Active Work

- [ ] Define the compact GPU representation for active pass, material,
  variant, and tier work.
- [ ] Define indirect-count production and consumption without CPU inspection.
- [ ] Define bounded capacity, overflow behavior, and visible diagnostics.
- [ ] Use subgroup/workgroup compaction without a production per-survivor
  atomic bottleneck, with a declared lower capability rung where subgroup
  arithmetic is unavailable.
- [ ] Clamp every GPU-produced active/count output on overflow, preserve visible
  conservative work where possible, and grow capacity only at a safe later
  frame boundary.
- [ ] Cover opaque, masked, transparent where supported, overrides,
  depth/normal, shadow, and other required variants.
- [ ] Define stable descriptor/material-table ownership across in-flight frames.
- [ ] Define a zero-readback interface for an optional GPU-produced visibility
  input so workstream 07 can add or bypass Hi-Z without changing the compact
  submission contract. Do not implement or tune Hi-Z in this workstream.

Acceptance criteria:

- [ ] Every required render variant has a zero-readback path or is explicitly
  declared unsupported with a visible failure.
- [ ] Capacity and overflow cannot cause an out-of-bounds dispatch or a silent
  CPU fallback.

## Phase 2 - Remove CPU Fan-Out And Refresh

- [ ] Replace `FullBucketScan` as the default submission mechanism.
- [ ] Remove current-frame active-list and material-count mappings.
- [ ] Emit a bounded command structure whose CPU size scales with pass groups,
  not material capacity times tiers.
- [ ] Move changing per-frame values into GPU-visible data that does not
  require visiting every reusable frame op.
- [ ] Keep delayed counters or debug snapshots off the current-frame critical
  path.
- [ ] Batch resource-specific compute-to-indirect barriers instead of emitting
  broad or per-dispatch synchronization, and consume generated counts with the
  backend indirect-count API.
- [ ] Select a reported material/texture binding rung by runtime capability;
  any coarse bucket fallback must consume compact active work rather than full
  material-table capacity.
- [ ] Remove or rename settings whose advertised zero-readback behavior is no
  longer accurate.

Acceptance criteria:

- [ ] Steady-state current-frame draw selection reports zero readback mappings,
  bytes, and waits.
- [ ] Clean primary reuse performs zero O(all frame ops) descriptor/frame-data
  refresh.
- [ ] No render-thread loop enumerates every configured material slot and tier.

## Phase 3 - Correctness And Performance Validation

- [ ] Validate material changes, streamed texture publication, overrides,
  depth/normal prepass, shadows, transparent content, and capacity overflow.
- [ ] Validate empty, exact-capacity, overflow, delayed-diagnostic, and
  optional-visibility-input bypass cases inherited from the technical children.
- [ ] Compare CPU-direct and GPU-driven output for deterministic scenes.
- [ ] Run static and moving canonical Deferred and Uber cohorts.
- [ ] Run the RVC acceptance workload with at least one desktop render and both
  RVC eye renders in every measured frame, with foveation disabled and enabled
  where supported.
- [ ] Compare stage p50/p95/p99, render-thread allocations, frame-op count,
  readbacks, and GPU time against workstreams 01 and 02.
- [ ] Verify primary reuse remains correct.

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

- [ ] The default GPU-driven path satisfies the zero-readback contract.
- [ ] Required compact-zero-readback child gates for active lists, compaction,
  overflow, barrier batching, indirect-count, and delayed diagnostics are
  complete. The child's Hi-Z phase remains blocked until workstream 07.
- [ ] The selected material binding rung is capability-probed, reported, and
  proven to preserve compact active-work scaling.
- [ ] FullBucketScan is removed from steady production submission or retained
  only as an explicitly named diagnostic fallback.
- [ ] CPU work scales with active pass groups rather than configured material
  capacity.
- [ ] Primary reuse, validation, focused tests, Release build, and canonical
  performance cohorts pass.
- [ ] Evidence and any unsupported variants are recorded in the investigation.
- [ ] This document is marked `Complete`.

Only after this gate is complete may work begin on
[04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md).
