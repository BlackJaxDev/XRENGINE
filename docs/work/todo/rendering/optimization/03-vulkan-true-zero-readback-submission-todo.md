# 03 - True GPU-Driven Zero-Readback Submission TODO

Last Updated: 2026-07-29
Owner: Rendering / Vulkan / GPU-Driven Submission
Status: Implementation Complete; Acceptance Deferred To 01-08 Closeout
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
- Owner sequencing change (2026-07-29): the remaining long-form validation is
  retained in the
  [01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md).
  Workstream 04 may begin because this implementation is complete. No unchecked
  acceptance item is waived or promoted.

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
- Eliminating generic frame-data-refresh allocations after the zero-readback
  submission data is prepared; that is workstream 04.
- Generic primary/secondary command-encoding allocation and parallel command
  recording; that is workstream 05.
- Forward prepass pass-count optimization; that is workstream 06.
- Final whole-renderer desktop 5.00 ms and RVC 8.33 ms promotion; that is
  workstream 08.

## Acceptance Evidence Contract

Workstream 03 closes only from reproducible `Gate` evidence, not from a
`Quick` capture, a single favorable window, or source inspection alone:

- Use `ReleaseBenchmark`, the canonical workstream 01 hardware/settings
  identity, warm caches, the stability gate, three 60-second repetitions, and
  manifest-compatible CPU-direct, diagnostic FullBucketScan, and production
  zero-readback runs.
- The maximum run-to-run range is 7.5%. A comparison with greater variance is
  invalid and must be repeated after the environmental cause is removed.
- The accepted regression threshold is 5%. For low-count overhead comparisons,
  the allowed p95 delta is the greater of 0.25 ms or 5% of the matched
  CPU-direct value. There is no additional discretionary "GPU-driven
  overhead."
- Submission-CPU evidence comprises
  `render_outside_vulkan_frame_ms`,
  `vulkan_cpu_frame_op_preparation_ms`,
  `vulkan_cpu_resource_planning_ms`,
  `vulkan_cpu_frame_data_refresh_ms`,
  `vulkan_cpu_primary_command_encoding_ms`, and
  `vulkan_cpu_secondary_recording_ms`. Report each metric independently; do
  not add overlapping percentile values into a synthetic total.
- The workstream-03 allocation gate covers allocations attributable to compact
  active-work generation, material-table/pass-group preparation, zero-readback
  selection, and submission. Generic `frame_data_refresh` allocations are
  recorded here and handed to workstream 04; generic primary/secondary
  command-encoding allocations are recorded here and handed to workstream 05.
  An allocation traced to workstream-03-owned code remains a blocker even when
  a broader stage counter initially reports it.
- Diagnostic validation-layer, delayed-readback, and RenderDoc captures are
  correctness evidence only and are excluded from performance totals.
- An unavailable RVC runtime, required view, image comparison, or GPU capture
  leaves this workstream `Blocked`; it does not convert the corresponding gate
  to pass. An unsupported foveation mode may be `NotApplicable` only when the
  manifest reports that capability result explicitly.
- The desktop 5.00 ms and RVC 8.33 ms results must be recorded here, but their
  final whole-renderer promotion is owned by workstream 08. Workstream 03
  instead gates the relative cost and scaling of the submission path so later
  graph, scheduling, occlusion, and tail work can proceed. Missing an absolute
  whole-frame target alone does not keep workstream 03 open after every
  workstream-03-local correctness, relative-cost, and scaling gate passes.

## Phase 0 - Characterize Fan-Out And Scaling

- [x] Capture configured material slots, active materials, tiers, render
  passes, emitted frame ops, and executed indirect commands.
- [ ] Run a matched scaling matrix that holds active work fixed while configured
  object/material capacity is increased through 1x, 4x, and 16x, then holds
  capacity fixed while active object/material count is increased through 1x,
  4x, and 16x.
- [x] Attribute command-building time outside the Vulkan frame and refresh time
  inside primary reuse.
- [x] Add tests that fail if a zero-readback strategy maps an active/count
  buffer during current-frame submission.

Acceptance criteria:

- [x] The baseline shows exactly where work scales with configured capacity
  instead of active work.
- [x] Readback-assisted strategies cannot be labeled `ZeroReadback`.
- [ ] With active work fixed, increasing configured capacity does not change
  CPU pass-group submissions, reusable frame-op count, current-frame mappings,
  or full-scan count, and every submission-CPU p95 remains within the
  low-count overhead allowance.
- [ ] With capacity fixed, GPU input/survivor/executed counts follow the
  expected active-work increase while CPU submission remains bounded by the
  fixed pass-group/tier topology.

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
  depth/normal prepass, shadows, supported transparent content, and explicit
  rejection of unsupported transparent/forward semantics.
- [ ] Validate empty, exact-capacity, overflow, delayed-diagnostic, and
  optional-visibility-input bypass cases inherited from the technical children.
- [x] Compare CPU-direct and GPU-driven output from identical frame inputs for
  deterministic Deferred and Uber opaque/masked scenes, with temporal jitter
  and stochastic effects disabled.
- [x] Run static and moving canonical Deferred and Uber cohorts.
- [ ] Run every canonical RVC cohort available on the workstream 01 target
  environment. Every measured desktop frame must contain a freshly rendered
  desktop output. Every frame that submits an XR projection layer must contain
  both freshly rendered eye views, and each retained capture must observe both
  eyes at the runtime-owned cadence; run foveation off and every supported
  enabled mode.
- [x] Compare stage p50/p95/p99, render-thread allocations, frame-op count,
  readbacks, and GPU time against workstreams 01 and 02.
- [ ] Verify primary reuse remains correct.
- [x] Capture and inspect at least one RenderDoc frame containing compaction,
  the compute-to-indirect barrier, indirect-count consumption, and a compact
  material draw.
- [x] Run the focused zero-readback/material-scatter/overflow/primary-reuse
  tests and build the Release editor.

Acceptance criteria:

- [x] Object/material/selection identity and the finite-depth coverage mask are
  bit-exact between CPU-direct and zero-readback references. In deterministic
  non-temporal regions, linear-color RMSE is at most 0.5/255 and no channel
  differs by more than 2/255. A seeded omitted draw or material-row swap makes
  the comparison fail.
- [ ] Opaque, masked, override, depth/normal, shadow, and supported transparent
  draw/material counters match the CPU-direct reference. A required
  unsupported variant produces a counted hard diagnostic and a
  non-promotable result before it can become silently missing geometry.
- [ ] Material edits and streamed texture publication become visible at their
  declared safe frame boundary with no stale row/descriptor use, fallback
  event, current-frame readback, or primary invalidation caused only by data
  publication.
- [ ] Empty and exact-capacity inputs execute without overflow. Overflow clamps
  every count, preserves the declared conservative visible work or rejects the
  frame explicitly, increments the delayed overflow diagnostic, leaves guard
  regions intact, and causes no validation error or out-of-bounds indirect
  argument.
- [ ] Delayed diagnostics and optional-visibility bypass do not change
  current-frame mappings, waits, dispatch topology, draw counts, or output.
- [ ] In every canonical desktop cohort, each zero-readback submission-CPU p95
  is no more than the matched CPU-direct p95 plus the low-count overhead
  allowance.
- [ ] In at least one retained high-count or material-diverse production
  cohort, zero-readback `render_dispatch_ms` p95 is at least 5% lower than both
  matched CPU-direct and diagnostic FullBucketScan. The crossover must remain
  after three repetitions and cannot rely on missing/unsupported work.
- [ ] Every stable sample reports zero GPU readback bytes, mappings, and waits;
  zero full scans and forbidden fallback events; and zero managed allocation
  attributable to workstream-03-owned compaction, material-table/pass-group
  preparation, active-work selection, and submission. Record frame-data-refresh
  and primary/secondary command-encoding allocations as explicit workstream
  04/05 handoffs; they block this criterion only when attribution identifies
  workstream-03-owned code.
- [ ] Eligible static frames retain at least 99% primary reuse after
  per-swapchain-image initialization. Camera motion alone does not force a
  primary record unless a recorded structural dependency changes.
- [ ] Every RVC sample reports a fresh desktop render. Every submitted XR frame
  reports both fresh eye renders, no submitted frame reports only one eye, and
  the retained capture proves both eyes recur at the runtime-owned cadence.
  All samples report zero current-frame readback and zero silent fallback;
  skipped/reused eye output cannot masquerade as a render. Record p50/p95/p99
  and the 8.33 ms result; workstream 08 owns final RVC 120 Hz promotion.
- [ ] The GPU trace shows bounded compaction, resource/stage-specific
  compute-to-indirect synchronization, clamped indirect counts, and no
  per-material CPU submission hidden between compact generation and draws.

## Exit Gate

- [x] The default GPU-driven path satisfies the zero-readback contract.
- [ ] The workstream-03 portions of the compact-zero-readback child are proven:
  active-list/pass-group scaling, bounded non-per-survivor-atomic compaction,
  empty/exact/overflow safety, barrier batching, indirect-count submission, and
  non-critical-path delayed diagnostics. The child's subgroup-optimized rung
  may remain a reported future capability rung, and its Hi-Z phase remains
  blocked until workstream 07.
- [x] The selected material binding rung is capability-probed, reported, and
  proven to preserve compact active-work scaling.
- [x] FullBucketScan is removed from steady production submission or retained
  only as an explicitly named diagnostic fallback.
- [ ] The 1x/4x/16x matrix proves CPU work scales with bounded active pass
  groups rather than configured object/material capacity.
- [ ] Deterministic image/identity parity, mutation/streaming, required variant,
  empty/exact/overflow, delayed-diagnostic, and visibility-bypass gates pass.
- [ ] The relative submission-CPU and retained production-crossover gates pass
  under the acceptance evidence contract.
- [ ] Primary reuse, workstream-03-owned zero allocations, zero
  readbacks/fallbacks, focused tests, Release build, Vulkan validation, and
  RenderDoc inspection pass. Canonical desktop and required RVC `Gate` captures
  are valid and satisfy every workstream-03-local check. Frame-data-refresh and
  generic command-encoding allocations are recorded as workstream 04/05
  handoffs, and the absolute frame-budget result is carried to workstream 08.
- [x] Evidence and any unsupported variants are recorded in the investigation.
- [ ] Every unchecked Phase 0-3 task and acceptance criterion above is checked,
  with exact report/capture/test paths recorded in the investigation.
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

The infrastructure blockers are now resolved under
`Build/_AgentValidation/20260728-workstream03-acceptance/`:

- Monado is built and staged as a process-scoped OpenXR runtime. The clean smoke
  run created the instance/system/session/swapchains, submitted both eyes,
  retained zero per-frame allocations, completed teardown, and stopped only
  its owned service (`openxr-smoke-pass2/reports/openxr-smoke-summary.json`).
- The canonical RVC benchmark now owns the Monado service and runtime manifest.
  The Deferred/foveation-off Quick capture passed its 5-second stability gate,
  retained 306 samples, and reported a fresh desktop output in 306/306 samples
  plus both fresh eye outputs together in 124 submitted XR frames. It reported
  zero capture-window readback bytes/mappings, full scans, forbidden fallbacks,
  VUIDs, and submission rejections
  (`rvc-quick-deferred-off-pass5/reports/rvc-deferred-foveation-off/summary.json`).
  The corrected capture-level evaluator classifies that evidence as
  `NonPromotableQuickRun` (`evaluation-fixed.json`).
- RenderDoc captured a 65,775,716-byte Vulkan frame from the exact production
  cohort (`renderdoc/ws03-zero-readback-explicit.rdc`). Replay contained 566
  events, 19 compute dispatches, 177 draws, and the 40-command
  `vkCmdDrawIndexedIndirectCount` compact material submission. Exported final
  and G-buffer targets contain scene geometry
  (`ws03-explicit-final-pass.png`, `ws03-explicit-gbuffer.png`), and replay
  completed with no high-severity messages.
- The Release editor build completed with zero warnings/errors after the runtime
  and telemetry fixes. Focused OpenXR/frame-output regressions and the
  GPU-free Vulkan evaluator fixtures pass.

### Acceptance closeout snapshot (paused 2026-07-28)

The implementation and most diagnostic infrastructure are complete, but the
workstream is not accepted and workstream 04 remains blocked. The current
evidence root is
`Build/_AgentValidation/20260728-workstream03-acceptance/`.

Completed evidence since the implementation summary above:

- Deterministic Deferred and Uber CPU-direct/zero-readback comparisons are
  exact for object, material, selection, finite-depth coverage, and linear
  color (RMSE 0 and maximum channel difference 0). Seeded omitted-draw and
  material-row-swap controls fail as required. Reports:
  `reports/deferred-cpu-gpu-parity.json`,
  `reports/deferred-parity-negative-control.json`,
  `reports/uber-cpu-gpu-parity.json`, and
  `reports/uber-parity-negative-control.json`.
- The clean production desktop probe
  `desktop-quick-zero-clean/reports/desktop-deferred-static/summary.json`
  retained 1,240 samples and reported render p95 19.743 ms, 100% eligible
  primary reuse, zero current-window readback/mapping/full-scan/fallback, and
  zero workstream-03-owned managed allocation.
- The Quick 1x/4x/16x matrix proves the intended topology directionally:
  configured-capacity growth held active commands at 267 and CPU pass groups
  at 3; active-work growth produced 267/1,035/4,107 commands while pass groups
  remained 3. Every run reported zero current-frame readback, full scans,
  forbidden fallback, and workstream-03-owned allocation. Quick evidence is
  useful diagnosis but cannot satisfy the Gate-only acceptance contract.
- The retained high-count Quick comparison used at least 4,096 commands.
  Zero-readback render p95 was 343.854 ms versus 1,245.364 ms CPU-direct and
  518.635 ms diagnostic FullBucketScan. This establishes a strong candidate
  crossover, but it still requires three Gate repetitions.
- `Tools/Benchmarks/Compare-VulkanPhase3Acceptance.ps1` now evaluates the two
  scaling axes separately, the retained crossover, canonical Gate invariants,
  absolute-budget handoffs, and matched desktop CPU-direct submission metrics.
- The first formal scaling attempt under `formal-scaling-gate/` completed all
  18 captures with run ranges below 7.5%, but is invalid as acceptance evidence:
  the engine's three-capture retention policy removed earlier frame streams
  before evaluation, and an explicitly selected Gate subset was incorrectly
  treated as the full canonical Gate. The harness now copies each repetition's
  `profiler-render-stats.ndjson` and `profiler-capture-manifest.json` into its
  cohort report directory and records `GateScope=Selected` for explicit Gate
  subsets. Evaluator fixtures cover this behavior.
- Focused Phase 3 regression evidence is
  `reports/test-results/phase3-final-regression/phase3-final-regression.trx`
  (28 passed) and
  `reports/test-results/phase3-harness-fix/phase3-harness-fix.trx`
  (6 passed). The Vulkan evaluator self-test passes 11 fixtures.
- The persistence re-check under `frame-stream-persistence-probe2/` proves the
  durable frame stream and capture manifest reopen correctly; there are no
  `MissingFrameStream`, `MissingCaptureManifest`, or `MissingLogDirectory`
  issues.

The current blocking result is
`frame-data-reuse-diagnostic/reports/evaluation.json`. Its 1,134-sample Quick
capture still satisfies the local zero-readback path (zero workstream-03-owned
allocation, readback bytes, mappings, full scans, and forbidden fallbacks), but
only reuses 1,061 of 1,129 eligible primaries (93.98%) and records 68 instead of
meeting the 99% floor. The immediately preceding retained stream shows the
same clustered failure as decision mask 66 (`Recorded | FrameData`) with a
constant descriptor generation and no pending pipeline. Enabling
`XRE_VULKAN_FRAME_DATA_REUSE_DIAG=1` alone did not emit the detailed blocker;
the next repro must also enable `XRE_VULKAN_RECORDING_DIAG=1`, which populates
the full `DescribePrimaryReuseMiss` reason. The diagnostic evaluation also
reports two `CpuStageReconciliationFailed` samples; those must be explained or
fixed before formal evidence is accepted. The guarded wrapper restored the
Windows Balanced power plan after every run.

### Ordered next steps

The steps below are deferred intact to the
[01-08 Acceptance Closeout](../../../testing/rendering/01-08-optimization-acceptance-closeout.md)
and run after workstreams 01-08 are implementation complete.

1. Reproduce only `phase3-capacity-1x-active-fixed` with the Quick preset and
   both `XRE_VULKAN_FRAME_DATA_REUSE_DIAG=1` and
   `XRE_VULKAN_RECORDING_DIAG=1`. Inspect the retained frame stream and source
   Vulkan log for the first mask-66 transition. Fix the frame-data refresh
   blocker at its source; do not lower the 99% threshold or classify eligible
   frames as ineligible to hide the miss. Resolve the two CPU-stage
   reconciliation discrepancies in the same narrow probe.
2. Repeat that Quick cohort until every stable window reports at least 99%
   eligible primary reuse, zero workstream-03-owned allocation, and zero
   readback/mapping/full-scan/forbidden-fallback. Then rerun the focused Phase 3
   tests, the 11 evaluator fixtures, and the Release editor build.
3. Retain reproducible runtime evidence for the still-open mutation/streaming,
   required-variant, empty/exact/overflow, delayed-diagnostic, and optional
   visibility-bypass criteria. Automated tests already cover their contracts;
   the unchecked boxes require the runtime/counter/output proof described
   above, including explicit unsupported-forward rejection.
4. Run a new selected `Gate` scaling matrix (do not reuse
   `formal-scaling-gate/`) with three 60-second repetitions for:
   `phase3-capacity-1x-active-fixed`,
   `phase3-capacity-4x-active-fixed`,
   `phase3-capacity-16x-active-fixed`,
   `phase3-active-1x-capacity-fixed`,
   `phase3-active-4x-capacity-fixed`, and
   `phase3-active-16x-capacity-fixed`. Use the guarded High-performance wrapper
   and verify it restores the original power plan. Run the Phase 3 comparator
   against the retained result.
5. Run a new selected `Gate` crossover for
   `phase3-high-count-zero-readback`, `phase3-high-count-cpu-direct`, and
   `phase3-high-count-full-scan`. The three strategies must use the same
   settings SHA, each must retain at least 4,096 backend commands, each p95
   range must be at most 7.5%, and zero-readback must remain at least 5% faster
   than both references.
6. Run the full canonical `Gate`: desktop Deferred/Uber static/moving plus RVC
   Deferred/Uber with foveation Off and Fixed. Monado and Fixed foveation are
   available, so none of these eight cohorts may be skipped. Run the four
   matched CPU-direct `primary-reuse-*` cohorts and feed both run roots to the
   comparator. Record the desktop 5.00 ms and RVC 8.33 ms outcomes as
   workstream-08 handoffs; missing those absolute targets alone is not a WS03
   blocker.
7. If the primary-reuse fix changes recording, descriptor publication, or
   synchronization, repeat StandardValidation and the focused RenderDoc
   inspection. Otherwise retain the already valid zero-VUID validation run and
   `renderdoc/ws03-zero-readback-explicit.rdc` evidence.
8. Only after the comparator and evaluator report all WS03-local checks passed,
   update the investigation with exact report paths, check every remaining box
   in this document, mark it `Complete`, unblock workstream 04, and preserve the
   generic frame-data-refresh/command-encoding and absolute-budget handoffs to
   workstreams 04, 05, and 08.

Until those steps pass, do not mark workstream-03 acceptance complete or
promote zero-readback submission. The implementation sequence may proceed to
[04 - Next-Frame Preparation And Collect-Visible Handoff](04-next-frame-preparation-and-collect-visible-handoff-todo.md)
under the 2026-07-29 owner-authorized deferral.
