# Compact Zero-Readback Rendering TODO

Last Updated: 2026-05-29
Owner: Rendering
Status: Active
Target Branch: `rendering-compact-zero-readback`

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Zero-Readback GPU-Driven Rendering Plan](../../../design/rendering/zero-readback-gpu-driven-rendering-plan.md)
- [Production GPU-Driven Rendering Roadmap](../gpu/production-rendering-pipeline-roadmap.md)
- [Occlusion And Meshlet Execution TODO](../occlusion-and-meshlet-execution-todo.md)

## Goal

Make strict zero-readback rendering fast by replacing broad scans with compact
active work. No current-frame CPU readback is allowed, but zero readback alone
is not success: the path must compact, cull, batch barriers, handle overflow,
and report its true cost.

## Scope

- Active draw/material/bucket list generation.
- Subgroup/wave prefix-sum compaction.
- Overflow handling and diagnostics.
- One-phase and two-phase Hi-Z integration.
- Barrier and synchronization batching.
- Indirect-count draw/dispatch submission.
- Delayed diagnostics that do not affect the current frame.

## Non-Goals

- Do not remove `GpuIndirectInstrumented`; it remains the diagnostic path.
- Do not make CPU direct slower to make zero-readback look better.
- Do not rely on CPU-visible counters for current-frame draw decisions.
- Do not hide fallback draws in strict zero-readback profiles.

## Phase 0 - Branch, Baseline, And Audit

- [ ] Create dedicated branch `rendering-compact-zero-readback`.
- [ ] Capture Release baselines for `CpuDirect`, `GpuIndirectInstrumented`, and
  `GpuIndirectZeroReadback` on low-object, high-object, occluded, material-diverse,
  and skinned-avatar scenes.
- [ ] Record active zero-readback draw path, material path, Hi-Z mode, and
  readback bytes.
- [ ] Inventory every CPU readback helper reachable from
  `GpuIndirectZeroReadback`.
- [ ] Inventory every full material, bucket, tier, object, or meshlet scan in
  the production zero-readback path.
- [ ] Inventory every memory barrier issued by the cull, scatter, compact, and
  draw stages.

Acceptance criteria:

- [ ] The audit distinguishes production zero-readback work from instrumented
  diagnostic work.

## Phase 1 - Active-List Data Model

- [ ] Define active draw, active material, active state-class, and active bucket
  buffers with explicit capacities and ownership.
- [ ] Ensure GPU culling writes compact active draw IDs rather than preserving
  upper-bound scene-size lists as real work.
- [ ] Ensure material scatter consumes active draw IDs, not all possible draws.
- [ ] Ensure bucket draw submission consumes active buckets, not all theoretical
  material slots.
- [ ] Add static-scene reuse or short-circuit rules where camera, topology, BVH,
  material table, and visibility inputs are unchanged.
- [ ] Keep diagnostic full scans behind explicit flags and profiler labels.

Acceptance criteria:

- [ ] Production zero-readback work scales with active draws/buckets, not scene
  maxima or material table capacity.

## Phase 2 - Subgroup Prefix-Sum Compaction

- [ ] Implement subgroup/wave partitioned prefix sums for stream compaction.
- [ ] Use one workgroup-level atomic to reserve each group's output span.
- [ ] Derive per-lane output slots from subgroup prefix sums.
- [ ] Remove per-survivor `atomicAdd` compaction from production paths.
- [ ] Provide a fallback compaction shader for hardware without subgroup
  arithmetic; mark it as lower rung in profiler output.
- [ ] Add shader tests or source-contract checks that production compaction
  does not use per-element atomics.
- [ ] Add counters for input count, survived count, group count, atomics issued,
  and compaction time.

Acceptance criteria:

- [ ] Compaction cost is dominated by active data movement, not serialized
  per-element atomics.

## Phase 3 - Overflow Contract

- [ ] Size active-list and indirect-output buffers from historical visible
  counts with a configurable safety multiplier.
- [ ] Add explicit capacity checks to every shader that writes active-list or
  indirect-command output.
- [ ] Clamp written counts on overflow.
- [ ] Increment `GpuCompactionOverflow` or a more specific overflow counter.
- [ ] Emit a conservative fallback where possible, such as parent cluster,
  coarser LOD, or next-frame CPU-direct fallback.
- [ ] Resize buffers across frames, never mid-frame.
- [ ] Surface overflow in editor diagnostics and profile capture JSON.
- [ ] Add tests for empty input, exact-capacity input, overflow input, and
  resize-on-next-frame behavior.

Acceptance criteria:

- [ ] Visible work is never silently truncated without a diagnostic.
- [ ] Overflow cannot corrupt adjacent buffers or indirect-count arguments.

## Phase 4 - One-Phase And Two-Phase Hi-Z

- [ ] Define a single render-graph contract for last-frame Hi-Z and
  current-frame Hi-Z consumers.
- [ ] Implement phase 1: cull against last-frame Hi-Z, draw accepted work.
- [ ] Rebuild current-frame Hi-Z after phase 1 depth is available.
- [ ] Implement phase 2: recull the rejected or uncertain set against
  current-frame Hi-Z, then draw newly accepted work.
- [ ] Report one-phase vs two-phase mode and draw counts for each phase.
- [ ] Keep one-phase available for editor diagnostics and cases where measured
  cheaper.
- [ ] Add stereo-safe Hi-Z handling: per-eye Hi-Z chain, array view variant, or
  explicit conservative fallback with warning.
- [ ] Add tests for missing depth source, stale depth source, stereo depth
  source, dirty bypass, and no-depth fallback.

Acceptance criteria:

- [ ] Hi-Z failures are visible as skipped reasons, not silent pass-through.
- [ ] Two-phase mode improves heavily occluded scenes without regressing simple
  scenes beyond the acceptance threshold.

## Phase 5 - Barrier And Synchronization Batching

- [ ] Group compute dispatches that produce SSBO or indirect-command writes
  before issuing barriers.
- [ ] Replace per-dispatch broad barriers with resource/stage-specific grouped
  barriers.
- [ ] Ensure indirect command buffers receive `GL_COMMAND_BARRIER_BIT` or
  backend equivalent only when needed.
- [ ] Ensure shader storage writes receive `GL_SHADER_STORAGE_BARRIER_BIT` or
  backend equivalent only when needed.
- [ ] Avoid synchronous `glGet*`, query waits, blocking maps, and fence waits in
  production hot paths.
- [ ] Make GPU timestamp query density opt-in and pass-level by default.
- [ ] Add counters for barriers by kind and stalls by reason.

Acceptance criteria:

- [ ] GPU traces show fewer bubbles from redundant barriers.
- [ ] Production frames contain no blocking sync calls required for current
  zero-readback draw decisions.

## Phase 6 - Indirect-Count Submission

- [ ] Ensure OpenGL uses `ARB_indirect_parameters` / multi-draw indirect count
  where available.
- [ ] Ensure Vulkan uses `vkCmdDrawIndexedIndirectCount` or equivalent strategy.
- [ ] Ensure DX12 path design maps to `ExecuteIndirect` when implemented.
- [ ] Validate count-buffer reset happens on GPU before command generation.
- [ ] Validate zero-count draws are skipped or harmless without CPU inspection.
- [ ] Ensure unsupported backend capability falls back explicitly and reports
  the selected strategy.
- [ ] Add source-contract checks preventing CPU draw-count readback in
  `GpuIndirectZeroReadback`.

Acceptance criteria:

- [ ] The CPU submits stable pass-level indirect calls and does not inspect the
  generated draw count for the current frame.

## Phase 7 - Delayed Diagnostics

- [ ] Keep diagnostic count readback in `GpuIndirectInstrumented` or delayed
  profiler paths only.
- [ ] Read delayed counters at least one frame later and never gate current draw
  submission on them.
- [ ] Publish active draw count, active bucket count, empty bucket skips, full
  bucket scans, indirect command count, Hi-Z phase counts, and overflow counts.
- [ ] Include readback bytes in every profiler frame and profile capture.
- [ ] Add assertions that strict zero-readback profiles have zero current-frame
  readback bytes.

Acceptance criteria:

- [ ] Diagnostics explain what the GPU path did without changing what the
  current frame renders.

## Final Validation And Merge

- [ ] Run targeted GPU indirect, material table, Hi-Z, and profiler tests.
- [ ] Run Release baselines after implementation on low-count, high-count,
  occluded, material-diverse, and skinned-avatar scenes.
- [ ] Capture at least one GPU trace showing barrier and compaction behavior.
- [ ] Update the engine optimization roadmap with final results.
- [ ] Merge branch `rendering-compact-zero-readback` back into `main` after
  implementation, validation, and documentation updates are complete.
