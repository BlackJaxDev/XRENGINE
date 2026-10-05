# Vulkan Stall Remediation TODO

Updated: 2026-10-05. Runtime evidence cutoff: 2026-10-05.
Owner: Rendering, with Profiler, Runtime Core, serialization and ImGui Editor owners below.
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

The active Vulkan/OpenXR hardware regression takes priority. Start with cold
and post-Play pipeline-admission gaps in the hardware worklist below. Preserve
Advanced/CpuDirect Sponza and strict SinglePassStereo during isolation.

The desktop target is above 100 fresh FPS during camera motion, with one
directional light and no removed features. The latest interior route measures
92.09-92.46 FPS with the shadow lane versus 33.65-33.74 generically. The target
is unmet. Earlier exterior-route results do not close this gate.

After hardware isolation, resolve the invalid OpenGL control, run matched
shadow/temporal comparisons, and continue allocation/lifetime and CPU/GPU
attribution. The user waived replay of the unavailable original recording on
October 3. This is not a fix or a reproduced result; live symptom evidence and
user confirmation remain required.

## OpenXR Stereo And Desktop Regression Worklist

Owner: Rendering with the editor and runtime owners. Physical Vulkan/OpenXR
strict stereo is available. The user reports mostly black output, Sponza flicker
and old-frame jitter; sky-only views mostly work. Startup, bounded teardown,
desktop UI ownership and Play loop liveness have scoped passes in the
[hardware record](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md).
They do not close visual, performance or lifecycle acceptance.

- [ ] **Next slice: admission gaps.** Measure manifest changes and cursor resets
  through cold/warm startup and Play entry. Fresh Play copies restart a long
  plateau reporting `No compatible Vulkan render program is available yet`.
  Preserve bounded preparation, exact compatibility and XR deadlines before
  changing thread-local admission progress.
- [ ] Attribute the recorded 24-second `Renderer.RenderWindow` stall on one exit
  and the solid-magenta Sponza wall in a fresh third-entry eye preview.
- [ ] Validate interactive border drag-resize and recovery. Programmatic resize
  and live menu interaction pass; the native drag attempt did not execute.
  Programmatic checks do not cover the modal drag loop.
- [ ] Reduce XR memory to the 8 GB ceiling under the
  [editor memory reduction plan](optimization/editor-memory-reduction-todo.md).
  October 5 results are 13.5 GB private, 5.52 GB device-local, about 1.6 GB live
  managed objects and 48 MB/s idle allocation. The target remains unmet; keep
  process/GPU measures separate. Use that plan's current ordered work and open
  decisions, rather than repeating completed reductions. Evidence is in the
  [memory record](../../investigations/rendering/2026-10-04-editor-memory-retention.md).
- [ ] Remove the roughly 2.5-second startup black period at the stereo generation
  transition. A commit inside the OpenXR planner scope retires the live eye
  allocator and loses exposure history. Use the
  [stereo generation record](../../investigations/rendering/2026-10-04-openxr-stereo-flicker-and-target-duplication.md).
- [ ] Attribute warmed pacing, black/no-layer output and frame-data-slot refusals.
  Mixed-workload CPU dispatch is about 237 ms median, with about 39 ms in snapshot
  copy; these overlap and are not GPU time. Separate CPU, GPU completion,
  reservation and frame-data-slot ownership. Preview-off isolation removes
  forced reservation waits but not slot refusals. Do not hide failures with
  stale output, sequential fallback, disabled features or capacity increases.
- [ ] Correct the remaining 2-4 uniform-schema fallbacks, including skybox
  intensity/rotation ownership. Preserve content generation, per-view ownership,
  bounded storage and fresh output.
- [ ] Validate both eyes through head motion and Sponza/sky transitions: freshness,
  eye assignment, orientation, projection, history, exposure, flicker and ghosting.
  Obtain physical headset and comfort feedback; previews alone are insufficient.
- [ ] Repeat enable/disable, resize, visibility/focus changes, failure and normal
  teardown. Prove balanced image ownership, drained retired generations, bounded
  retention, no device loss and no silent sequential fallback.
- [ ] Exercise the separate OpenGL/OpenVR hardware path. Record backend-specific
  results before closing shared XR acceptance.

### S15b. Record Directional Cascade Casters On The Advanced Canonical Lane

Owner: Rendering. The [lane record](../../investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md)
contains scoped correctness, reload, retry, material, page and Play checks.
The lane remains **Active**. It reuses desktop sealed bins with a depth-only
atlas target; it does not use the earlier proposed separate family.

- [ ] Attribute the remaining interior-motion CPU/GPU cost before another
  performance change. The two failed motion windows stopped the remaining
  counterbalanced restarts. Obtain detailed CPU stage deltas and GPU history
  on the same route. A single recording/wait snapshot is not a distribution.
  The dense directional
  interval includes dependency stalls and excludes later render-scope closure;
  it is not exclusive shader cost.
- [ ] Complete counterbalanced stationary/motion A/B with an idle host, explicit
  matched shadows and the same accepted fixture. The October 4 interior result
  misses 100 FPS; SteamVR/Oculus background work limits performance admission.
  Do not substitute the earlier exterior route or claim the target passed.
- [ ] Preserve stationary, camera/light/object-motion shadow parity, per-cascade
  coverage, masked/custom-material eligibility and generic execution when the
  lane declines. Record all fresh frames and refresh-frame CPU tails. Keep HMD,
  multiview and unsupported moment encodings within their actual coverage.

### S16a. Black OpenGL Scene On The Measurement Host

Owner: OpenGL rendering. Upload ownership, query order, source reload, sparse
transitions, exposure, sampling and two-view HDR/depth parity have scoped results
in the [OpenGL record](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md).
Matched throughput acceptance remains blocked.

- [ ] Establish a correctly rendering control under the same admission gates.
  Rebuilt unchanged `9fee4b983` passes 0/100 interior samples and retains stale
  output after a camera cut, with canonical texture-source rejection. Do not
  patch its renderer or bypass admission and call it the original baseline.
  Disclose any replacement control's source delta.
- [ ] Repeat matched comparisons with corrected readiness and explicit requested
  shadow dimensions/readback before warmup. Require actual opaque/masked geometry,
  current reservations, advancing accepted receipts and Vulkan completed frames.
  Reject sky-only views and retained terminal faults. `NoStabilityGate` is diagnostic
  and cannot establish performance acceptance. Pin the environment map/lighting.
- [ ] Validate final display tonemapping, temporal sequences and the full
  cross-backend matrix. Raw mono depth and HDR parity after resize do not establish
  display parity. Earlier aborted or non-admitted captures are not valid speedups.
- [ ] Complete stereo/MSAA source reload, source-object replacement, driver-parallel
  overlap, sparse cancellation/device-failure and exposure/metering stereo gates.
  Keep row-major sampling aliasing and diagnostic latency limits explicit.

## S13i. Prove The Cumulative Fix On The Reported Workload

Owner: Rendering with Profiler. The September desktop fixture passed its scoped
comparison; that does not supersede **NOT PASSED** after the October 1 run and
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
  callbacks. Refresh these October 2 counts against the retained directional
  lane before choosing another dictionary change; they are historical entry evidence.
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
- [ ] Validate the family mutation/lifetime matrix on available production XR.
  The later hardware smoke does not establish this full gate, and emulated
  three-family results do not establish hardware correctness or utilization.

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
  successful test build and the October 4 selected run (86/97 passing). The final
  upload selection passed 31/32. Preserve cohort-specific failure classifications;
  do not report an old compile failure or combine these counts into one total.
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

Owner: Runtime Core. Tick optimization is **Deferred** under the
[September 27 measurements](../../investigations/rendering/2026-09-27-s14-core-update-owner.md).
Reopen when tick cost reaches 0.10 ms/update, pending application reaches 1.0 ms
within a second, a callback reaches 1.0 ms mean, or registration churn occurs
outside Play transitions. Run the following only after that trigger.

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

### S14e. Capture The Intermittent Play-Exit Exception

- [ ] If `playmode-transitions.log` records another exit failure, fix the named
  cause and repeat full probe runs. Recovery and stack logging are retained;
  about 40 exits without recurrence do not prove the cause fixed. See the
  [exit record](../../investigations/rendering/2026-09-27-s14e-exit-exception.md).

### S14g. Close The Steady-State Gap After Play Round Trips

- [ ] If short-lived output planner states grow, retire them when their output
  is destroyed. Current shadow-viewport states hold no textures and remain
  bounded by the 12-state cap. The superseded-generation resource leak is fixed;
  this conditional metadata follow-up is separate from the later XR memory gate.
  [Planner evidence](../../investigations/rendering/2026-10-03-s14g-post-play-cost.md).

## S15. Preserve Temporal Correctness And Resolve The Original Report

Run affected checks after each frame/view identity, admission or publication change.
The performance fixture defaults to FXAA; use an explicit TSR fixture for history
checks. Scoped motion/cut/resize, distinct-camera switching and immutable TAA/TSR
corrections are recorded in the
[temporal investigation](../../investigations/rendering/2026-10-03-s15-temporal-checks.md).

- [ ] Baseline and recapture stationary detail, controlled motion, disocclusion,
  camera cut, resize and pipeline/view switches with matching settings.
- [ ] View saved images/sequences from multiple positions. Correlate history/view/
  frame identity, jitter, previous/current matrices, velocity, depth and resets.
  Public diagnostics omit the actual temporal key and snapshot frame; separate
  asynchronous polls cannot certify the first frame after a camera/view switch
  or rejected-frame publication. Obtain exact post-admission/lifecycle bindings.
- [ ] Verify deferred TAA/TSR consumes the correct immutable pipeline snapshot.
  A `TsrOutputTexture` label or quiet log does not establish correct history.
- [ ] Give any isolated temporal defect its own fix and build/live/comparison gate.
  Do not mask it with reduced feedback/quality, disabled picking or hidden warnings.
- [ ] Resolve fine-edge TSR concerns and classify pipeline-replacement cold stalls.
  Attribute improvement on the reproducible live fixture and obtain the user's
  ghosting/performance confirmation. Original-recording replay is explicitly
  waived; do not reinstate that requirement or record the waiver as a fix.

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
- [ ] Report unsupported canonical command rejection reasons/counts instead of
  silently committing empty output; inspect `TryGetCanonicalCompatibilityReason`.
- [ ] Attribute the bounded 320 extra descriptor sets caused by per-frame
  auto-uniform arena-view identity on masked Sponza cascade casters.
- [ ] Rename phase-named publication telemetry types, MCP tools and environment
  variables by responsibility. Update docs and regenerate MCP documentation.
- [ ] Attribute later Play restore cost of 0.8-1.0 seconds versus the earlier
  0.25-0.33 seconds; investigate stair-stepped directional shadow boundaries.
- [ ] Attribute capture sequences to the rendered camera snapshot, not the live
  transform two frames ahead. Reconcile collect/render `TemporalHistoryValid` flags.
- [ ] Investigate exposure settling after repeated history resets, black upper
  sky with `klippad_sunrise_2_4k`, and per-launch environment-lighting variation.
  Compare only matched maps or an explicit procedural-sky fixture.

## S16. Integrated Acceptance And Closeout

Run only after individual gates pass. Compare original and previous-increment
baselines so cumulative cost transfers remain visible.

- [ ] Complete matched cold/cache-warm restart and warmed still/moving runs.
  Report p50/p95/p99/max, successful presents, lost timing samples, preparation,
  recording, waits, GPU cost and loading time separately.
- [ ] Complete repeated resize, shader/pipeline reload, mesh/index/texture admission,
  multi-view ownership, affected failure paths and teardown cycles.
  Preserve scoped root/include/in-memory reload and restoration results in the
  [shader reload record](../../investigations/rendering/2026-10-04-shader-root-reload.md).
  Wider source replacement, MSAA/stereo and failure/lifetime coverage remain open.
- [ ] Validate Play entry/exit, probe refresh, redraw/picking, attachment metadata
  and idle BVH diagnostics. Validate toolbar and camera settings on first/warm use.
- [ ] Validate affected OpenGL/shared and XR/stereo paths. Vulkan/OpenXR hardware
  is available but visual acceptance fails. Complete its worklist above and the
  separate OpenGL/OpenVR path. Inspect sequences and obtain user confirmation.
- [ ] Prove no new hot-path allocation, unbounded retention, queue starvation,
  unsafe disposal, silent fallback or missing required draws.
- [ ] After applicable explicit test clearance, complete focused regression checks
  and record results. Keep uncleared work pending.
- [ ] Update the result/investigation records and related owners only for proven
  coverage. Give each deferred/blocked item a reason and reopening condition.
  Restore temporary settings and record cleanup of owned sessions and captures.

Final acceptance requires all applicable gates and explicit exclusions. Close the
original CPU/TSR report only with reproduction/correction evidence and user confirmation.
