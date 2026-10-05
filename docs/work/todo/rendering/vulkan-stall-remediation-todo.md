# Vulkan Stall Remediation TODO

Updated: 2026-10-05. Runtime evidence cutoff: 2026-10-02.
Owner: Rendering, with Profiler, Runtime Core and ImGui Editor owners below.
Status: **Cumulative acceptance NOT PASSED. The original CPU/TSR report is open.**

This checklist contains remaining work and conditions for reopening deferred
work. Completed results and historical status reports are in the
[result record](../../progress/rendering/vulkan-stall-remediation-results.md).
Architecture is in [scene preparation and publication](../../../architecture/rendering/vulkan-scene-preparation-and-publication.md)
and [editor background preparation](../../../architecture/editor/background-preparation.md).

Follow the [validation protocol](../../testing/rendering/vulkan-stall-validation.md)
for every change. Keep only one implementation item active. Define its measured
entry condition and acceptance before editing. Validate each increment through
its exact build and live path before starting the next. Test clearance remains
separate; this document does not grant it.

The [frame-loop master](vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
owns integrated renderer promotion. Keep the
[hardening](vulkan-core-hardening-and-device-loss-todo.md),
[resource lifecycle](render-pipeline-resource-lifecycle-todo.md), and
[AA](advanced-pipeline-antialiasing-todo.md) gates with their existing owners.
Do not close broader contracts from a narrow stall result.

## Next Work

Start with pool retention across scene unload and submission-contract sealing
under the cumulative gate below. Preserve the measured allocation improvements.
Sealed binding snapshots remain the largest measured allocation owner, but
reuse requires proof of program-borrow lifetime and content identity.

CPU tails, the unexplained outer-dispatch interval, GPU attribution and visual
correctness remain separate gates. The original 153-165 ms report lacks its
original logs; a different fixture cannot prove that report fixed.

## S13i. Prove The Cumulative Fix On The Reported Workload

Owner: Rendering with Profiler. **NOT PASSED** after the October 1 run and
subsequent focused allocation changes. Use the
[cumulative investigation](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md)
and [CPU attribution record](../../investigations/rendering/2026-10-01-cpu-stall-attribution.md).

### Residual Allocation And Lifetime Work

- [ ] **Pool retention.** Validate scene unload and repeated scene replacement.
  Pooled desktop operations retain their last resource/context references up to
  historical per-frame demand. Prove bounded retained memory and timely release.
  Keep receipt-owned, captured, ordered-batch and OpenXR work excluded unless
  every borrow is proven to end before reuse.
- [ ] **Temporary construction.** Attribute submission-contract sealing
  (about 69 KB/present in the October 1 capture). Classify the remaining
  resource-use array growth before changing it; only nine samples remained
  after desktop pooling. Preserve complete dependency closure and ownership.
  Do not double-count nested allocation totals.
- [ ] **Sealed binding snapshots.** Measure required contents and copy frequency
  (about 1.077 MB/present in the latest window). Establish program-borrow
  retirement and content generations before reuse. `ApplyBindingSnapshot`
  retains snapshots beyond recording; frame-data signatures use snapshot
  identity. Frame-slot retirement alone is insufficient. Do not label ordinary
  snapshots as immutable binding artifacts to bypass copies.
- [ ] **Shadow consumers, conditional on attribution.** Of 396 recorded binding
  fallbacks, 393 were shadow passes with per-mesh generated programs. Determine
  whether typed shadow data can replace specific dictionary consumers. Preserve
  cascade/face matrices, caster masks, material values, textures and arbitrary
  callbacks. Existing typed state does not yet replace these consumers.
- [ ] **Gate each retained increment.** Freeze control and candidate binaries.
  Repeat stationary and uninterrupted-motion comparisons with matched observers.
  Report sampled bytes per completed present and GC tails. Preserve zero-sample
  refresh/layout paths; validate real mutations, relinks, snapshot ownership
  and retention. Record excluded or incomplete runs. Source-reference sealed-copy
  sharing was reverted for lack of benefit; require new reuse evidence to reopen it.

### CPU Stall And Observer Work

- [ ] Correlate GC suspension and successful-present intervals with aligned
  scheduling and file-I/O evidence. Explain the roughly 1.9-second outer-dispatch
  gap and the later 567.52 ms GC-only suspension. Do not attribute all jitter
  to GC or Vulkan validation.
- [ ] Use a constant-speed camera path, or account for authored easing. Obtain
  displayed-motion evidence and user confirmation before closing the report.
- [ ] Repair unavailable camera-state and default-zero lifetime/lease series.
  Repeat observer overhead and retention admission on a frozen binary. Missing
  fields do not prove zero cost, disabled effects or empty leases.
- [ ] Keep CPU-observer-off controls and measured outliers. Establish replacement
  source-row and previous-increment comparison evidence where historical
  workspace/binary evidence is unavailable.

### Cumulative Acceptance

- [ ] Repeat the matched matrices against both the original current-source
  baseline and the previous validated increment. Use at least three matched
  pairs and 60-second warmed windows unless a justified alternative is declared
  before capture. Keep Debug/debugger evidence separate from Release claims.
- [ ] Report dirty causes/callbacks, registration rebuilds, allocations/GC, real
  dirty/upload bytes, family preparation calls, scans, lock wait/hold, publication
  latency, Vulkan preparation/encoding, successful-present intervals and queue/
  lease retention. Compare all-frame p50/p95/p99/max and adjacent stages.
- [ ] Match scene content, native/canonical draw coverage, AA, resolution and
  executed feature state. Inspect stationary, motion and disocclusion sequences.
  Resolve the failed OpenGL image/readback check. Complete temporal and multi-view
  gates below, with explicit hardware limits. Missing draws, stale output,
  reduced quality or silent CPU fallback cannot count as improvement.
- [ ] Complete the separate [Advanced GPU attribution item](optimization/advanced-pipeline-gpu-attribution-todo.md).
  Preserve coarse query identities/coverage. Validate a dense observer separately
  and compare one effect at a time. Report CPU and GPU costs independently.
- [ ] If attribution identifies cold canonical PSO admission or required texture
  finalization, create a separate child under the existing pipeline/upload owner.
  Validate pending/failure/stale completion, transfer-before-binding and retirement.
- [ ] Record retained diffs, each child disposition, remaining CPU/GPU latency
  and unexplained intervals. Pass cumulative tails, resource retention and
  adjacent-stage budgets before accepting the cumulative change.

## Remaining Fixture And Regression Coverage

These cases were not established by the recorded scoped passes. Provide a
suitable fixture, or record an explicit supported-scope disposition. Do not
silently convert an unexercised case into a pass.

### S13c. Retain Logical Mesh/LOD Registration By Real Mutation Identity

Owner: GPUScene. [Evidence and dependency key](../../investigations/rendering/2026-09-26-s13c-registration-retention.md).

- [ ] Complete the registration matrix for multiple LODs, shared meshes/submeshes,
  threshold edits, active LOD changes, streaming completion/eviction, atlas
  relocation, geometry replacement and removal/re-addition. The recorded fixture
  used single-LOD renderables with streaming off.
- [ ] Exercise failed registration and retry, supersession, repeated create/destroy
  cycles and atlas-slot reuse. Preserve prior accepted registration on failure;
  verify reference counts, retirement, IDs, LOD selection and every consuming slot.
- [ ] Retain the zero-allocation, zero-rebuild, zero-atlas-ensure and zero redundant
  table-write contract on exact hits, including transform-only motion.

### S13d. Update Material/Draw Auxiliary State Only When Its Inputs Change

Owner: GPUScene. [Evidence and column dependencies](../../investigations/rendering/2026-09-26-s13d-auxiliary-state.md).

- [ ] Exercise instance-count changes, live material override swap/removal,
  texture/sampler replacement and skinning/deformation on a suitable fixture.
  Verify affected rows and images, stable unrelated identities and ID reuse.
- [ ] Exercise growth beyond initial capacity and retry after failed registration.
  These cases were reasoned about, but not run. Verify initial population,
  rotating destinations and accepted per-stream revisions.
- [ ] Attribute repeated state-class writes where several materials share the
  last-writer class row. Change that path only if a measured residual cost and
  a correct ownership model justify it.

### S13e. Prepare Shared Advanced Scene State Once Per Compatible Family

Owner: Vulkan resource/command preparation.
[Evidence and compatibility key](../../investigations/rendering/2026-09-26-s13e-family-preparation.md).

- [ ] Exercise window resize and MSAA sample-count changes. Confirm incompatible
  inputs refresh family state, required uploads remain ordered, and lease/native
  counts settle after churn. Count failures and retries separately.
- [ ] Validate affected production XR hardware paths when available. Emulated
  three-family results do not establish hardware XR correctness or utilization.

### Additional Validation Limits

- [ ] Cover shared-extraction forced growth/failure, duplicate published geometry
  keys and long-duration structural/material churn. Preserve fresh accepted mesh
  output and both copy boundaries. Keep the separate AA fresh-output/streaming/
  framebuffer and OpenGL transition gates with the AA owner.
- [ ] Resolve deterministic late-result rejection and the recorded cold upload-
  retry recovery limit before extending readiness coverage or introducing reuse.
  [Readiness evidence](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md)
  defines the current scope.
- [ ] When applicable targeted tests run, record the current build and failure
  status. Separate the historical 33 lifecycle/VR source-contract failures and
  five readiness source-text failures from new regressions. The older missing
  `IAdvancedGlobalIlluminationProvider` compile blocker was superseded by a later
  successful test build; do not report it as a current failure without evidence.
- [ ] Obtain explicit clearance for any new test work after its live feature
  validation. Prior focused closeout clearance is not blanket approval.

## Deferred Work: Conditions For Reopening

These are conditional follow-ups. No implementation is active under these items.

### S12. Improve Shared Advanced Preparation Safely

The distinct-runtime-world gate is **Not Applicable** under the explicit
[September 25 decision](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md#september-25-distinct-world-disposition).
Reopen if Advanced rendering supports different worlds in multiple windows/views,
world load or Play creates a new runtime host, or `ReloadFromAsset` is implemented.

- [ ] On that trigger, exercise distinct runtime owners across frames with retained
  GPU consumers, static/deformation generations, topology replacement and teardown.
  Prove bounded generations and completion-gated reuse. Same-owner snapshot/restore
  and first-wins same-frame publication do not prove this lifetime case. Do not
  add per-world caches without measured need.

### S13f. Retain Plan-Derived Operation Metadata

Reopen on material operation/family growth, scan allocation, repeated cost above
0.05 ms/presentation, or separately attributed structural demand cost.
[Measured deferral](../../investigations/rendering/2026-10-01-advanced-operation-metadata.md).

- [ ] First audit existing sealed-plan, manifest and admitted-frame reuse. If the
  entry condition holds, define a complete structural key: sealing revision,
  graph/planner generation, order/count, target backing and relevant view/AA state.
- [ ] Keep current availability, frame-slot leases, output/reservation identity
  and producer readiness live. Validate pass/order changes, multiple families,
  progressive admission, deferred/retried/superseded plans and malformed/stale
  rejection. Prove coverage and ordering without increased retention or encoding cost.

### S13g. Bound Warmed Pipeline-Readiness Work

Broader reuse is deferred. Reopen only for measured costly evaluation that remains
following the allocation correction.
[Measured disposition](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md).

- [ ] Prove every mutation producer advances an exact dependency-generation key
  before consumption. Cover generated source, shader edits, layout/device
  recreation and capability changes. File events alone may be insufficient.
- [ ] Validate unchanged polling, unrelated/dependent edits, rapid reload, failed
  compilation, Pending recovery, cancellation/stale completion and replacement.
  Keep pending-to-ready progress observable with an unchanged plan. Preserve
  zero foreground joins and exact retirement; never freeze Pending behind a cache.

### S13h. Change Synchronization Only For A Measured Remaining Bottleneck

Reopen only with a measured residual critical path and adequate concurrent-lifetime
proof. Prior approximate acquisition costs were below 0.10 ms/present.
[Measured deferral and ownership review](../../investigations/rendering/2026-10-01-remaining-synchronization-gates.md).

- [ ] Measure GPUScene mutation-lock and Vulkan Advanced storage-gate wait/hold
  separately, with contender/owner identity, publication time and worker use.
  Long held work is not contention; sparse snapshots do not prove parallel use.
- [ ] Select one owner. Review snapshot ownership, lock order, generation recheck,
  commit/rollback, bounded retry/backpressure and retirement before a change.
  Preserve shared arena, rollback and preparation-scratch ownership.
- [ ] If parallel recording is justified, use worker/frame-slot-owned command
  and descriptor pools, existing workers and suitable batch sizes. Preserve
  external Vulkan synchronization and deterministic publication order.
- [ ] Exercise actual concurrent mutation, competing views/families, delayed
  completion, cancellation, resize, teardown and failures. Prove complete output,
  bounded progress and no deadlock, race, use-after-free or partial publication.
  Confirm another stage, retry queue or worker backlog did not absorb the cost.

### Other Measured Deferrals

- [ ] Reopen warmed camera metadata caching only if repeated cost reaches its
  1.0 ms entry threshold. Retain script-generation and unload correctness.
- [ ] Reopen debug/light-volume sharing or procedural fullscreen drawing only
  after separate duplication/cost evidence. Define topology, shader/view
  compatibility, consumer ownership and retirement before extending sharing.
- [ ] Reopen historical backend divergence only with a reproduced differing
  publication/notification/collection event and matched accepted output. Preserve
  the recorded Not Reproducible disposition until then.

## S14. Address The Actual Core Update Owner

Owner: Runtime Core. Start at `RuntimeWorldLifecycle` Normal/Late callbacks.

- [ ] Profile actual callbacks, tick order, pending registration drain and callback
  identity. Do not use the unrelated legacy list or XREvent indices as world IDs.
- [ ] If registration dominates, preserve ordered dispatch and define a coherent
  batch boundary, lock-owned snapshot sizing and activation/deactivation semantics
  before changing membership cost or adding a cap.
- [ ] Validate duplicate registration, add/remove order, callback-time changes,
  bulk activation, Play transitions and teardown. Compare final membership and
  callback sequences as well as time.
- [ ] If another callback/wait dominates, assign its exact owner. Verify probe/
  physics consumers, worker dependency and timer debt. Do not drop simulation
  steps or move app-thread publication from a broad world-update label alone.

Gate: preserve ordering and Play behavior while improving the measured cause.
Defer tick optimization if GC/descheduling or negligible update cost explains it.

## S15. Preserve Temporal Correctness And Resolve The Original Report

Run affected checks after each frame/view identity, admission or publication change.

- [ ] Baseline and recapture stationary detail, controlled motion, disocclusion,
  camera cut, resize and pipeline/view switches with matching settings.
- [ ] View saved images/sequences from multiple positions. Correlate history/view/
  frame identity, jitter, previous/current matrices, velocity, depth and resets.
- [ ] Verify deferred TAA/TSR consumes the correct immutable pipeline snapshot.
  A `TsrOutputTexture` label or quiet log does not establish correct history.
- [ ] Give any isolated temporal defect its own fix and build/live/comparison gate.
  Do not mask it with reduced feedback/quality, disabled picking or hidden warnings.
- [ ] Record whether the original long-recording trigger reproduces, which exact
  change explains improvement, and the user's ghosting/performance confirmation.
  Keep unavailable original evidence unresolved.

Gate: classify each case as validated, failing or unverified. A failing or
unverified temporal result blocks closure of the original report.

## Separate Issues Found During Validation

These are carried forward from the closeout record. Reproduce their current
state and assign a separate owner; they are not proven current stall causes.

- [ ] Resolve world snapshot/restore calls that do not return after long settles.
- [ ] Review YAML `OmitDefaults` dropping `false` on true-initialized booleans
  without `[DefaultValue(true)]`.
- [ ] Resolve shared material GUIDs in `duplicate_scene_node` clones.
- [ ] Account for the roughly 7.5-second hover-highlight dirty traffic in stationary
  automation. Record any suppression in both comparison conditions.
- [ ] Review the bounded 1,024-entry deferred presentation ring retaining the
  prior renderer generation until overwrite.
- [ ] Track missing replacement-GI contract coverage with its owner and test policy.

## S16. Integrated Acceptance And Closeout

Run only after individual gates pass. Compare original and previous-increment
baselines so cumulative cost transfers remain visible.

- [ ] Complete matched cold/cache-warm restart and warmed still/moving runs.
  Report p50/p95/p99/max, successful presents, lost timing samples, preparation,
  recording, waits, GPU cost and loading time separately.
- [ ] Complete repeated resize, shader/pipeline reload, mesh/index/texture admission,
  multi-view ownership, affected failure paths and teardown cycles.
- [ ] Validate Play entry/exit, probe refresh, redraw/picking, attachment metadata
  and idle BVH diagnostics. Validate toolbar and camera settings on first/warm use.
- [ ] Validate affected OpenGL/shared and XR/stereo paths. Missing hardware blocks
  the corresponding claim. Inspect temporal sequences and obtain user confirmation.
- [ ] Prove no new hot-path allocation, unbounded retention, queue starvation,
  unsafe disposal, silent fallback or missing required draws.
- [ ] After applicable explicit test clearance, complete focused regression checks
  and record results. Keep uncleared work pending.
- [ ] Update the result/investigation records and related owners only for proven
  coverage. Give each deferred/blocked item a reason and reopening condition.
  Restore temporary settings and record cleanup of owned sessions and captures.

Final acceptance requires all applicable gates and explicit exclusions. Close the
original CPU/TSR report only with reproduction/correction evidence and user confirmation.
