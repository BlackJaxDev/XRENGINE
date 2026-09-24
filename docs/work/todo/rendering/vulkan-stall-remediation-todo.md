# Vulkan Stall Remediation TODO

Last Updated: 2026-09-24
Owner: Rendering, with Profiler, Runtime Core, and ImGui Editor owners per item
Status: S00/S00a/S01/S02/S03/S04/S05/S06/S07/S08/S09/S10/S11 Validated; S12 Active for merged lifetime gates; S13a/S13b Blocked with validation gates open; S13c-S13i Pending
Execution: One fix at a time, with a mandatory validation gate after each fix

## Purpose And Ownership

Address the priorities in the [September 17 stall research](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#L121)
without turning source-level candidates into assumed root causes. This is the
execution checklist for that investigation, not authorization to implement every
possible optimization regardless of measurements.

The [Vulkan frame-loop master](vulkan-core-frame-loop-and-resident-rendering-master-todo.md)
continues to own architectural contracts and integrated promotion. Reuse its
existing machinery and cross-reference completed work instead of creating a
second pipeline queue, cache, telemetry system, or publication model. Related
contracts remain in the [hardening ledger](vulkan-core-hardening-and-device-loss-todo.md)
and [resource-lifecycle checklist](render-pipeline-resource-lifecycle-todo.md).

This document schedules future work only. Creating it does not authorize test
changes, dependency upgrades, storage migrations, or changes to launch flows.
Keep durable results in the linked investigation and concise gate status here.

## Evidence That Must Not Be Lost

- The original report describes repeated 153-165 ms CPU recording and TSR
  ghosting. Its original logs are unavailable locally; causal attribution is open.
- The resumed probe instead has warmed sampled recording of 27.1-42.6 ms, GPU
  time of 17.81-24.85 ms, and output of 15-20 Hz. These are sparse overlay samples,
  not all-frame percentiles or a demonstrated reproduction of the original issue.
- Seven 500-ms watchdog detections include visibility preparation, UI, upload and
  Vulkan recording scopes. An active leaf observation does not explain an entire
  no-completed-render interval.
- `GetHottestPath` pairs a descendant path with its root duration. Neither the
  reported 1,285.837 ms mesh endpoint nor 92.077 ms update endpoint measures the
  named method's exclusive cost.
- The resumed probe presented `TsrOutputTexture`. That proves participation in
  the presentation path, not correct history identity or absence of ghosting.
- The historical repaired Release matrix failed motion p99 observer overhead.
  A superseding current-HEAD matrix at revision `a1f9408e5` completed three
  alternating profiler-off/on pairs with the same workload identity, verified
  camera motion, admitted images, 99.625-99.643% motion GPU coverage, zero
  diagnostic loss, and passing retention/backlog gates. Paired motion p99 deltas
  were -1.00%, +3.94%, and +6.43%, below the 13.67% disabled-run spread.
  S00 is Validated; this does not make the measured motion smooth.
- The current matrix confirms a separate periodic motion hitch with either
  profiler state: frames above 55 ms recur every 0.282-0.301 seconds median,
  usually four render frames apart with adjacent-frame bursts. Render time
  correlates 0.98-1.00 with CPU Vulkan recording and only 0.03-0.10 with GPU
  time. Slow frames add about 28.5 ms in primary recording, dominated by the
  primary operation loop, prewarm, packet lowering, and `vkEndCommandBuffer`.
  This is entry evidence for S02, not observer overhead.
- S02 localized recurring warmed allocation to generated value equality in the
  Advanced visibility raster lane. Explicit handle/scalar identity checks removed
  245,232 bytes from the median Advanced visibility operation (337,176 -> 58,376)
  while preserving the warmed 393-draw workload. A matched profile-capture A/B
  reduced primary-recording median from 16.601 to 7.097 ms and p95 from 58.656
  to 36.934 ms. Rare whole-frame tails and the 18-25 ms GPU workload remain.
- Controlled TSR motion produced ready history and finite nonzero velocity, but
  the viewed path was not discriminating enough to resolve the reported ghosting.
  Frame-authoritative profiler telemetry now reports the active TSR mode instead
  of a launch-cached FXAA value; that telemetry fix is not a visual TSR fix.
- Existing native caches, background compilation, resident-table reuse, family
  leases and transactional resource generations must be audited and reused.
  Unchecked historical backlog entries do not prove those mechanisms are absent.

### September 22-23 Publication And Recording Follow-Up

The [frame-rate investigation](../../investigations/rendering/2026-09-22-framerate-cpu-gpu-attribution.md)
is a separate Debug reproduction, not a replacement for the earlier Release
gates. Its stationary profiler-off window had median present interval 121.594 ms,
Vulkan CPU time 40.074 ms, primary recording 28.503 ms, nested encoding 8.666 ms,
and coarse GPU time 15.423 ms. Completed render-pass swap scopes sampled
42.272-65.660 ms; those samples are not a distribution or exclusive leaf costs.
The two 20-second profiler-on/off windows are diagnostic entry evidence only.
They do not establish debugger overhead, Release performance, or effect-level GPU
cost. Do not subtract independent medians to claim an attributed recording leaf.

The subsequent source audit identified a concrete publication-to-dirty feedback
path and recurring LOD/material registration work before the unchanged-data
check. It also identified repeated Vulkan family/stage preparation as a candidate.
The September 23 experiment below subsequently measured the identity feedback
and enclosing GPU-scene update callbacks. Attribution inside those callbacks and
the repeated family/stage preparation candidate remain open. S13a-S13i own that
verification and any justified fixes. Successful S02 equality remediation and
S12's inexpensive measured extractor lock do not validate these other paths.

### September 23 Collect Wait: Measured Cause And Remaining Questions

Durable record: [Vulkan render<-collect investigation](../../investigations/rendering/2026-09-23-vulkan-render-collect-wait.md).
This is diagnostic entry evidence, not a retained fix or a completed S13 gate.
The observed workload used AdvancedRenderPipeline, CpuDirect, TSR, 1920x1080
output / 1286x723 internal resolution and 393 canonical resident draws. The
Release backend comparison reused one binary with only the render API changed;
the CPU profiler was active, the debugger detached, and Vulkan validation off.

| Observation | Recorded result | What it establishes |
| --- | --- | --- |
| Debug Vulkan without debugger | Twenty wait samples mostly 61-73 ms; completed command-swap scopes about 55-62 ms. | The long wait reproduces without an attached debugger; it does not quantify the debugger's additional cost. |
| Release Vulkan baseline | Twenty wait samples about 49-59 ms; completed command-swap scopes about 48-62 ms. | Optimized code still has the problem. |
| Release OpenGL comparison | Twenty wait samples about 2.6-3.3 ms with one zero; completed command-swap scopes about 0.5 ms. | Backend-associated behavior differs; equal resident counts alone do not prove equal collected/accepted work or image correctness. |
| Temporary callback timing | One Vulkan swap contained 393 callbacks; sampled callback totals were about 48-51 ms. OpenGL histories contained 4-6 callbacks across multiple frames. | Repeated `GPUScene.TryUpdateMeshCommand` calls own the large enclosing elapsed interval. OpenGL counts must not be compared as single-frame totals. |
| Temporary identity-notification exclusion | Twenty warmed Vulkan wait samples 3.0-5.7 ms, mean 3.51 ms; sampled scene-command swap about 0.001 ms with the 393 callbacks absent. | Excluding publication identity from generic dirtiness removes the measured trigger. This is a causal experiment, not mutation/history validation. |

The code path is `PublishSourceDrawIdentities` ->
`RenderCommandMesh3D.PublishCanonicalDrawIdentities` -> `SetField` ->
`RenderCommand.OnPropertyChanged` -> dirty queue -> `SwapBuffers` callback ->
`VisualScene3D.OnRenderableSwapBuffers` -> `GPUScene.TryUpdateMeshCommand`.
The temporary candidate excluded only the caller-member notification
`nameof(PublishCanonicalDrawIdentities)` through `IsRenderStateDirtyProperty`;
both identity snapshots and their `SetField` notifications were still published.
That candidate and the per-callback profiler scopes were removed. S13b must
review and validate a permanent implementation; do not assume the experiment
left a fix in the checkout or blindly reinstate it as a completed phase.

`render<-collect` is elapsed waiting for a freshly published collect generation.
The collect thread must finish command swapping before releasing that wait.
`collect<-render` is previous-render backpressure and can overlap other work;
do not add the two counters or subtract unrelated sampled medians. The narrow
frame-package publication counter excludes the later command-swap callbacks,
so its small value does not contradict the large generation wait.

The callback timer includes acquisition, held-body work and any descheduling;
it does not separately measure a dictionary, registration routine or lock wait.
Their shares remain S13a evidence, and any residual optimization belongs to
S13c/S13d/S13h only after S13b is measured. About 3 ms of Advanced publication
remained in both backends and in the exclusion experiment. Separate Vulkan
recording costs and GPU time are not fixed by this experiment. The original
attached-debugger screenshot's exact split, full-window tails, visual/temporal
correctness and the reason for OpenGL's smaller queue remain unverified.

## Mandatory One-By-One Protocol

**Only one implementation item may be Active. Do not begin the next fix until
the current fix has passed its focused build, live behavior, performance and
applicable regression checks, with evidence recorded.** A section containing
multiple independent changes is not an exemption: split them into child items
and validate each child before starting the next.

Read-only evidence gathering may run independently. Overlapping edits, runtime
mutations and A/B measurements must remain serialized. Do not combine readiness,
invalidation, resource publication and UI changes in one unvalidated patch.

For every item below, complete this gate:

- [ ] Record the entry evidence, exact owning path, one falsifiable hypothesis,
  affected workloads, smallest coherent change, and a check that could reject it.
- [ ] Define acceptance before editing: correctness invariants, target metric,
  numeric budget/tolerance, sample count, observation window and observer overhead.
  Use measured baseline variability and existing master contracts; do not choose
  a convenient threshold after seeing the result.
- [ ] Record ownership/thread affinity, source/configuration generations,
  cancellation semantics, lock order, publication and retirement dependencies.
  Require an explicit design review before changing concurrent/native lifetimes.
- [ ] Implement only this item. Identify this exact source diff and the binaries
  used for validation; a build from before the edit is not evidence for the fix.
- [ ] Immediately run the narrowest useful check. Run the owning project build
  with no new warnings, then the relevant isolated editor path. A successful
  build, quiet log or `git diff --check` alone cannot close a runtime item.
- [ ] Exercise the original trigger plus the item's cold/warm, pending/failure,
  mutation and lifetime cases below. Actually view saved images when rendering
  or UI behavior is involved; tool success is not image validation.
- [ ] Compare baseline and changed captures under matching conditions. Confirm
  the targeted mechanism changed, performance met the predeclared budget and
  neighboring stages did not absorb the removed cost or accumulate backlog.
- [ ] Check affected regressions, warnings, allocation/retention growth and
  teardown separately from steady-state behavior. Explain every new diagnostic.
- [ ] Record pass/fail, evidence, remaining risks and user feedback. Only then
  mark the item Validated and select the next item.

### Failure And Deferral Rules

- On failure, stop progression. Repair the same item and rerun its checks. If the
  hypothesis was falsified, record that result and reassess the nearest owner.
  Do not build another fix on top of a known failing change.
- A flaky or ambiguous result is not a pass. Repeat with a discriminating capture;
  if evidence remains insufficient, mark Blocked and report the blocker.
- If a candidate's entry condition is disproved, mark Deferred/Not Applicable
  with evidence and the condition for reopening it. Do not mark it Fixed.
  Advancing past an unvalidated behavioral change requires an explicit scope
  decision; do not silently waive a failed gate.
- Any rollback must be scoped to this item's own edits. Preserve unrelated user
  work. Do not reset the worktree or switch branches to obtain an A/B baseline.
- A change to instrumentation or measurement settings requires a comparable
  baseline before accepting performance results from subsequent items.
- A reordering requires a written evidence-based reason and satisfied dependency
  gates. The measured steady-state owner may take priority over cold-only work;
  this must not become simultaneous implementation.

### Status And Test Clearance

Use Pending, Active, Blocked, Validated, Closed, or Deferred/Not Applicable. Checked
implementation boxes are not closure. Validated means the item's live gates
passed; Closed additionally means its required follow-ups and applicable test
work/user confirmation are resolved. Record test clearance separately.

Do not add or modify regression tests until live feature validation succeeds and
the user explicitly clears test work, as required by repository policy. Existing
tests may support diagnosis or verify a sound change when applicable, but must
not replace live validation. A clearance request or this TODO is not clearance.
Pending test approval remains visible and does not permit claiming full closure.
Once cleared, add only focused deterministic coverage in the existing test project
and rerun the corresponding focused checks before closing the item.

## Execution Ledger

All items start Pending. Conditional items require an explicit disposition if
their entry condition is not met. Every row, including a split child, has its own
gate record. No item is complete merely because this checklist was written.

| ID | Item | Entry dependency | Initial status |
| --- | --- | --- | --- |
| S00 | [Comparable baseline and evidence manifest](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s00-gate-record-comparable-baseline-and-evidence-manifest) | None | Validated (current-HEAD three-pair matrix passes observer, coverage, loss, retention, and backlog gates) |
| S00a | Export existing coarse GPU timing provenance | S00 instrumentation prerequisite | Validated (GPU identity and symmetric clean profiler toggle proven live) |
| S01 | [Correct profiler duration/identity reporting](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s01-gate-record-correct-profiler-duration-and-identity-reporting) | S00 | Validated (normal, linked async/parallel, and lifecycle identity proven) |
| S02 | Attribute warmed recording and waits | S01 | Validated (boxed stable-bin identity comparisons identified and removed; matched live A/B passed) |
| S03 | Nonblocking Advanced pipeline readiness | S02, confirmed cold-path trigger | Validated (cold/reload phases measured; zero attributed foreground joins; explicit unavailable capability rejected without fallback) |
| S04 | [Dependency-scoped compile invalidation](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s04-gate-record-dependency-scoped-compile-invalidation) | S03, lifetime design review | Validated (existing detailed gate below; scoped invalidation and retirement passed) |
| S05 | [Cache publication and foreground native creation](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s05-gate-record-bounded-cache-and-native-creation-work) | S04, measured remaining cost | Validated (existing detailed gate below; measured native/cache paths and rejected-cache recovery passed) |
| S06 | [Bounded initial resource materialization](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s06-gate-record-bounded-initial-resource-materialization) | S02; default after S05 disposition | Validated |
| S07 | [CPU mesh preparation and wrapper publication](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s07-gate-record-separate-mesh-cpu-data-and-wrapper-publication) | S06, measured construction cost | Validated |
| S08 | [Index preparation before draw admission](../../investigations/rendering/2026-09-21-s08-index-preparation.md) | S07 disposition, measured join | Validated (PR #75 merged; normal Vulkan admission requests/polls exact-revision index preparation without joining) |
| S09 | [Shared immutable helper geometry](../../investigations/rendering/2026-09-21-s09-shared-helper-geometry.md) | S07-S08 dispositions, measured duplication | Validated (fullscreen helpers share one leased CPU mesh per topology while retaining per-consumer renderer/material/stereo state) |
| S10 | [Toolbar icon preparation](../../investigations/rendering/2026-09-21-s10-toolbar-icon-preparation.md) | S02; default after S09 disposition | Validated (CPU preparation is off draw; bounded owner publication reaches 12/12 on Vulkan and OpenGL) |
| S11 | [Camera inspector metadata/discovery](../../investigations/rendering/2026-09-22-s11-camera-inspector-discovery.md) | S10 disposition, measured cost | Validated (cold-path timing, live picker/undo/retry and script generation/lifetime gates passed) |
| S12 | [Shared Advanced extraction/publication](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md) | S02; default after S11 disposition | Active for merged lifetime gates (local reachable gate passed; incoming distinct-world lifetime validation remains open; preserve both parent evidence sets) |
| S13 | Recurring publication/recording/source preparation; parent of S13a-S13i | S02; after S12 disposition | Pending |
| S13a | [Current workload, leaf attribution, backend divergence and acceptance budgets](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md) | S12 validated or explicitly dispositioned under the protocol | **Blocked**: observation handoff and causal identity-feedback evidence are recorded, but S12's distinct-world lifetime gate, exact-final-binary observer/retention matrix, attached debugger, correlated callback CPU/GC/scheduling attribution, and historical backend-divergence disposition remain open |
| S13b | [Separate publication identity from command dirtiness](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md) | S13a; confirmed identity-only dirty callbacks | **Blocked**: corrected local descriptor identity passes targeted camera retention and texture rehydration passes two live renderer restarts; validation-layer compatibility, broader retention, complete mutation/temporal, multi-view, failure/retry, matched-performance, and applicable test-clearance gates remain open |
| S13c | Generation-based logical mesh/LOD registration | S13b disposition; measured recurring registration | Pending |
| S13d | Mutation-scoped material/state/auxiliary updates | S13c disposition; measured redundant writes/resolution | Pending |
| S13e | Prepare compatible Advanced scene state once per family | S13d disposition; measured repeated preparation | Pending |
| S13f | Retain plan-derived operation metadata | S13e disposition; measured repeated plan scans | Pending |
| S13g | Bound warmed pipeline-readiness validation | S13f disposition; measured repeated readiness work | Pending |
| S13h | Shorten or partition a measured serialized critical section | S13g disposition; residual critical-path evidence and lifetime review | Pending |
| S13i | Cumulative publication/recording reproduction gate | All S13 child dispositions; applicable S15 checks | Pending |
| S14 | Actual Core update callbacks/registration | S02; default after S13 disposition | Pending |
| S15 | Temporal correctness and original-regression decision | Baseline plus each affected runtime gate | Pending |
| S16 | Integrated acceptance and closeout | All applicable prior gates | Pending |

### September 24 S13a/S13b closure audit

Neither item meets this TODO's Validated or Closed definition. The September 23
direction allowed the completed S13a observation to hand off to S13b; it did
not waive S12 or S13a's unfinished gate. Do not promote S13c, S13i, or S16
from the current evidence.

- S13a's 393-draw identity-feedback mechanism and registration leaf are
  established. Three earlier observer pairs passed the positive-overhead
  tolerance but all failed native/descriptor retention and preceded later
  instrumentation corrections. A September 24 180-second warmup produced one
  flat 60-second stationary resource window. An independent 180-second warmup
  run gained 1,961 native resources and 1,955 descriptor sets by the endpoint
  **after** its 60-second stationary and 60-second motion phases. Later live
  evidence traced large motion increments to retained mesh descriptor variants.
  The later probe-free 180-second-warmup run was flat for its 60-second
  stationary window, then gained 1,641 native resources and 1,625 descriptor
  sets after the controlled view transition. That binary failed motion
  retention. The later local-descriptor identity correction passes its targeted
  camera matrix; it does not supply the full S13a observer/attribution matrix.
  The [gate record](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md#september-24-closeout-audit)
  preserves the exact binary and observer limits.
- S13b's publication-only feedback filter, callback boundary guard, primitive
  reconciliation, material-header normalization, and exact-primitive
  preparation fixes have passed focused live Vulkan checks. A final read-only
  lifetime review found and repaired retained sidecar/scratch references and
  protected sidecar inspection with a scoped publication lease. Shared material
  retirement cleared old image/view/sampler backlogs in the isolated Vulkan run,
  but the mesh full-key cache kept older local allocation variants. A local
  binding-payload probe then found 918 duplicate variants caused by unconsumed
  snapshot resources. The corrected identity produced zero duplicate variants
  in the probe and flat mesh allocations through the probe-free camera matrix.
  A positive renderer-restart check subsequently failed texture upload readiness.
  The corrected recovery now claims and publishes a newer upload generation on
  the replacement device; two consecutive restarts and A/B/A view sequences
  passed with advancing presents, unchanged return-view mesh allocations, and
  zero retirement backlog. Cumulative validation exposed two startup errors
  involving descriptor-heap structures unknown to the installed layer; they
  remain visible, with no additional rendering/recovery errors observed.
  The complete
  mutation/temporal matrix and matched final-source performance pairs remain
  unverified; broader retention and lifecycle gates remain open. The
  [S13b gate record](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md)
  lists the observed cases and limits.
- Regression-test work is still uncleared under the repository testing policy.
  No new regression tests were written or run for these live integration fixes.
  Closure requires successful live gates first, then explicit test clearance
  and applicable focused checks or an explicit documented scope disposition.

### September 24 handoff: remaining closeout work

**Current disposition:** S13a and S13b remain **Blocked**. The user selected
continued validation, not a scope waiver. This handoff ends the current work
session; it does not close the remediation items. The named isolated editor
`descriptor-local-0924` is stopped. No further run, SDK change, commit, or
regression-test work is implied by this documentation update.

**Preserve completed results:** the local descriptor-identity correction removed
918 duplicate physical-payload variants in the diagnostic comparison; its
probe-free repeated/unseen-camera run kept mesh allocations flat. Shared
material retirement cleared the observed old image/view/sampler backlog. The
texture rehydration correction passed two manually reviewed renderer restarts,
reaching 7,891 and 6,678 completed presents with zero retirement backlog. These
are scoped live results, not the complete acceptance matrix. Do not rerun the
root-cause investigation without new contradictory evidence.

The later Python collection produced **21 screenshots** and raw telemetry on a
newer build. Those screenshots and logs are **not yet reviewed**. Its automated
statuses do not supersede the earlier reviewed results or establish new passes.
See the [collection record](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#automated-evidence-collection-awaiting-review)
and [harness instructions](../../testing/rendering/vulkan-lifecycle-evidence-harness.md).

| Remaining issue or gate | Work required | Evidence needed to resolve it |
| --- | --- | --- |
| Collected screenshots/logs await review | Review the 14 camera/shader/restart observations and seven warmup/transform/activation/material observations. Correlate each image with frame progress, current Advanced output, resource counts, and cumulative messages. Verify mutations actually changed and restored the intended state. Separate startup/teardown messages, historical readiness retries, and capture-induced allocations from steady rendering. | A durable per-case disposition with exact binary identity, meaningful resource deltas, and image/log findings. Fix and rerun only failing or inconclusive cases. |
| World snapshot fails before restoration | `snapshot_world_state` throws inside `AssetManager.Serializer.Serialize(world)`. The MCP wrapper discards the exception details from its response and sends them only to `Debug.LogError`, whose body is compiled out in this Release configuration. Expose bounded, sanitized exception type/inner-exception/property context through the MCP response or a Release-capable diagnostic sink. Rerun only the snapshot operation, identify the actual offending object/property, and correct its serialization contract. Do not guess the cause from the generic message. | The actual exception and responsible member are recorded; snapshot and restore succeed with scene integrity and continued rendering. Current evidence establishes a serialization failure, not a Vulkan lifetime failure. |
| S12 distinct-world lifetime dependency | After snapshot diagnostics are resolved, exercise genuinely different world instances/assets and trace world, publication, view, and accepted-package ownership through replacement. Verify old publication/scratch/descriptor references are released after their legitimate owners finish. Account for undo/snapshot references explicitly. A YAML roundtrip preserving IDs and undo references cannot substitute for this case. | Distinct old/new world identity, correct new-world rendering, and bounded retirement/reference retention under the S12 gate. Update the S12 owner record and its S13a dependency disposition. |
| Two startup Vulkan validation errors | The installed layer reports unknown descriptor-heap properties/features structures (`1000135008`/`1000135009`) and an unknown `VK_EXT_descriptor_heap` extension. Confirm compatibility using a validation layer that understands the enabled extension; any SDK/layer installation or upgrade requires the applicable tooling approval. Rerun standard and synchronization validation, including device creation and teardown. Preserve errors rather than suppressing them to obtain a pass. | Clean applicable validation or an explicitly approved, evidenced compatibility disposition. The earlier steady rendering/recovery run added no errors beyond these two startup reports; it was not a zero-error run. |
| Complete S13b mutation and temporal behavior | Extend the reviewed positive transform/activation/scalar-material cases to the declared geometry/primitive, material-state, texture-generation, visibility, and temporal-history cases. Verify both real changes and unchanged/publication-only updates. Include in-place native arena-buffer replacement; shader reload and renderer restart do not cover that race. Track the existing dark/high-contrast and temporal image-quality limits separately. | Correct accepted identities, descriptor payloads, invalidation/history behavior, visible outputs, and no unintended recurring dirty callbacks or retained owners for each applicable case. |
| Multi-view and upload failure/retry coverage | Exercise simultaneous views with distinct visibility/state, then bounded upload cancellation, failed admission, failed successor upload, competing transitions, and replacement during pending work. Retain exact generation/ticket evidence. | No cross-view stale state, premature native readiness, unbounded retry loop, double retirement, or leaked owner. Successful recovery and explicit terminal failure occur in their intended cases. |
| S13a final-binary observer/retention matrix | Freeze the final source, binary hashes, settings, workload and observation budgets. Run the predeclared three observer pairs, keeping stationary and moving intervals separate. Capture usable GPU timing coverage and the attached-debugger condition when available. Concurrent pipeline changes and historical binaries prevent isolated performance claims from the current mixed evidence. | All paired overhead/retention budgets pass on the same final binary; debugger availability and any unavailable evidence are explicitly recorded. |
| Callback attribution and historical backend divergence | Correlate callback spans with CPU execution, GC pauses, scheduler-ready and blocked time. Complete callback-to-publication/view ownership joins on Vulkan and OpenGL. Reproduce the first differing historical event under controlled conditions, or obtain an explicit scope disposition if it cannot be reproduced. Historical missing binary manifests remain unknown; do not reconstruct them by assumption. | Residual wall time and the first differing event have evidence-backed causal dispositions that satisfy S13a. No scope waiver is currently authorized. |
| Regression-test clearance and coverage | Complete relevant live feature validation first, then obtain the user's explicit clearance for test work under `AGENTS.md`. Add/run focused deterministic coverage for descriptor identity/invalidation, exact-lifetime retirement, and rehydration generation/admission/cancellation behavior. Keep live-only rendering cases in the runtime matrix. | Recorded clearance and passing applicable checks, with any remaining hardware/runtime cases explicitly tracked. The Python evidence collector does not grant this clearance or replace regression tests. |

**Resume order:** review the existing packet first; recover the missing snapshot
exception and repair that path; complete world/lifetime, validation-layer, and
mutation/failure/multi-view gaps; freeze the resulting binary for the observer
and attribution gates; then obtain test clearance and complete focused coverage.
Record each result in the existing S12/S13a/S13b owner documents. Promote an item
only when its declared acceptance conditions are met. S13c-S13h are later
optimization work, and S13i/S15/S16 provide cumulative acceptance; none
automatically resolves or waives the blockers above.

Disposable packet entry point:
`Build/_AgentValidation/20260924-102959-s13-closeout/reports/python-lifecycle-review.md`.
It links `python-lifecycle-ready` and `python-lifecycle-mutations`; both used the
same DLL hashes recorded in the collection record. If these ignored files are
cleaned up before review, recollect with the documented harness rather than
claiming the missing evidence passed. Preserve required conclusions in tracked
documentation after review.

## S00. Establish A Comparable Baseline

- [x] Preserve the original report separately from the resumed probe. Recover the
  original logs/configuration if available; otherwise label reproduction status
  unknown and never use a different scene to declare the original issue fixed.
- [x] Record source revision plus local diff, build configuration, SDK/runtime,
  device/driver, Vulkan validation settings, present mode, profiler/logging mode,
  backend/submission mode, scene content, camera path and viewport dimensions.
- [x] Define cold process, persisted-cache warm restart, warmed stationary and
  controlled-motion workloads. Record actual scene readiness, accepted content
  and cache state; elapsed time alone does not prove warm-up is complete.
- [x] Capture all-frame counts and CPU/GPU distributions, recording/resource
  preparation child timings, queue delay, successful presentations, missing GPU
  samples, stall thresholds and dropped/suppressed diagnostic events.
- [x] Use at least three matched runs per performance condition and a warmed
  window of at least 60 seconds by default. Record any justified alternative
  before comparison. Define stage/tail-latency and retained-memory budgets here.
- [x] Compare instrumented and minimal-observer configurations. Do not mix Debug,
  Release, profiler modes or validation-layer settings in one claimed speedup.

Gate: evidence is reproducible and budgets are recorded. If the 153-165 ms
regression cannot be reproduced, retain that unresolved result while addressing
independently confirmed mechanisms. Do not delete user caches to force cold runs.
Status: Validated. S00a's clean profiler toggle prerequisite and the superseding
current-HEAD Release matrix pass. Three matched 60-second off/on pairs retained
one workload identity, verified camera motion, exceeded 99% coarse-GPU coverage,
and passed exact diagnostic-loss, settled managed/private-memory, native-resource,
descriptor-set, and required-job/retire backlog gates. Paired motion p99 changes
of -1.00%, +3.94%, and +6.43% remain within the 13.67% disabled-run spread;
positive paired mean changes are at most +0.116 ms. Preserve the original CPU
regression, image corruption, TSR ghosting, and newly quantified periodic
recording hitch as unresolved. S01 may proceed; S02 remains blocked on S01 (see
[S00 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s00-gate-record-comparable-baseline-and-evidence-manifest)).

## S01. Correct Profiler Attribution

Anchor: [Engine.CodeProfiler.cs](../../../../XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.CodeProfiler.cs#L1551).

- [x] Define root-inclusive, selected-child-inclusive, synchronous self-time and
  active-scope elapsed fields with matching name, kind, frame/thread and timestamp.
- [x] Keep wall-clock self-time distinct from on-CPU execution. Treat overlapping
  cross-thread spans, incomplete roots and stale snapshots explicitly.
- [x] Update affected log/export/UI consumers so a new leaf duration cannot be
  misread as the old root field. Reuse the shared telemetry contract and document
  changed field meanings; avoid an unrelated profiler rewrite.
- [x] Validate a captured completed hierarchy with a larger parent and smaller
  child, sibling work, explicit waiting, and an active/incomplete scope. Recompute
  the expected fields from captured spans rather than trusting the formatted log.
- [x] Validate concurrent thread/frame identity and enabled/disabled profiling;
  confirm no new per-frame allocations or unacceptable observer overhead.

Gate: labels and values describe the same scope, self-time accounting is coherent,
and historical root-only values remain clearly identified. Do not add a new test
method or synthetic test suite before test clearance; use captured runtime spans.
Status: Validated. Explicit scope, parent,
producer-thread, logical-thread, session-epoch, publication and frame identity are
implemented and propagated through dumps, packets and UI. A 1,395-node live dump
had no duplicate IDs, missing parents, hierarchy mismatches, logical-thread
mismatches, invalid intervals, children outside parent intervals or incomplete
published nodes. A profiler off/on cycle advanced epochs 2 -> 3 -> 4 and surfaced
13 then 24 rejected stale completions without attaching them to the new session.
Focused `InvokeAsync` and `InvokeParallel` validation now proves linked listener
parentage, logical/producer thread identity, timestamp containment, completion,
publication deferral and synchronous self-time exclusion. These APIs remain
explicit opt-ins for independent, thread-safe listeners; no existing engine
event has a sufficiently broad thread-safety contract for automatic conversion.
See the
[S01 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s01-gate-record-correct-profiler-duration-and-identity-reporting).

## S02. Attribute The Actual Warmed Bottleneck

Anchors: [primary recording](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs)
and the [profiler guide](../../../developer-guides/diagnostics/profiler.md).

- [x] Capture a completed warmed long frame, with coarse recording children and
  operation kind/count/bytes. Add only the missing bounded instrumentation and
  validate that instrumentation as its own change before optimization.
- [x] Correlate lock/worker waits, native calls, allocation/GC pauses, JIT,
  scheduling, queue pressure and frame/output/generation identities.
- [x] Use managed wait/contention/GC evidence and, when needed, Windows ETW native
  stacks/context switches. EventPipe stack sampling alone is not on-CPU evidence.
  Check installed tool versions; operator-run elevation may be required for ETW.
- [x] Identify the critical-path owner rather than summing overlapping CPU/GPU
  intervals or assigning downstream `WaitForRender` time to useful update work.
- [x] For detailed captures, satisfy the existing hardening contract: account for
  at least 99% of the root interval and expose unattributed gaps of 50 us or more.
  If collection cannot support that, retain an explicit attribution blocker.
- [x] Select the next measured fix and record whether it affects startup, reload,
  warm recording, GPU work, or multiple regimes. Separate the warmed 18-25 ms GPU
  workload from CPU remedies; do not promise 60 Hz from a CPU-only change.

Gate: enough correctly attributed evidence exists to choose a bounded change.
If waiting/GC dominates, reject an unsupported algorithmic optimization and route
the next item to that owner with the same one-by-one protocol.
Status: Validated. Sparse and dense managed-allocation captures first separated
profile-row observer cost from render-thread work. Operation-family and raster
substage counters then identified deterministic 528-byte closure-validation and
96-byte stable-bin-lowering allocations for each of 393 records. Both came from
generated record-struct equality traversing nested Silk.NET Vulkan handle values;
explicit scalar/handle comparisons preserve the same identities without boxing.
The final detailed window had zero bytes in validation, lowering, binding and
CPU-direct draw substages, at least 99.942% frame attribution, and no unattributed
gap above 7 us. The matched labels-off/profiler-off CleanProfile capture improved
CPU recording and GC distributions without changing CpuDirect workload identity.
This closes attribution and the bounded warm-recording correction, not the
original unreproduced 153-165 ms report, rare whole-frame tails, TSR correctness,
or the independent GPU budget. S03 remains Pending because no recurring cold
pipeline-readiness trigger was observed. See the
[S02 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s02-gate-record-warmed-recording-allocation-attribution).

## S03. Make Advanced Readiness Nonblocking

Anchors: [Advanced capabilities](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.AdvancedPipelineCapabilities.cs#L407),
[visibility programs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.cs#L31),
and [program linking](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Linking.cs#L23).

- [x] Measure source compilation, linking, native pipeline work and foreground
  joins separately during the first required Advanced family and after reload.
- [x] Inventory the complete required family: early/late/native compute, opaque/
  masked raster and supported view/mesh variants. Establish preparation ownership
  outside readiness polling, reusing existing queues and prewarm records.
- [x] Implement explicit Missing/Pending/Ready/Failed transitions. Polling must
  not synchronously link, compile or wait, and repeated polls must not duplicate
  requests or reset progress. Avoid changing only the async boolean while the
  enclosing synchronous-preparation scope still overrides it.
- [x] Publish only a complete compatible family. Preserve previous output only
  where its contract permits; otherwise report pending/loading. Never silently
  omit required draws or substitute a CPU/backend fallback.
- [x] Validate cold miss, warm hit, delayed completion, unavailable capability,
  compile failure, shader reload, repeated polling and exact frame admission.
  A queued compile must eventually publish or report failure, not remain pending
  forever; shutdown must not strand preparation jobs.

Gate: readiness does no foreground compilation/join, outcomes remain correct,
target stalls improve within budget, and missing/failed work remains visible.
Split preparation and admission changes into child gates if independently staged.

Status: Validated. The generation-owned family preparation task, explicit readiness
states, nonblocking shader artifact polling, complete-family publication, reload
identity handling, failed-revision recovery, superseded-task draining, and
idempotent shutdown are implemented. Cold, warm, delayed double-reload, injected
compile failure, same-process recovery, exact admission, repeated polling, and
shutdown passed in isolated Vulkan editor sessions. The authoritative cold run
transitioned from `PendingResources` to `Admitted` in 3,904.05 ms wall time with
1,562.59 ms summed source work, 10.23 ms linking, 3.17 ms native compute pipeline
creation and zero S03-attributed foreground joins. Reload completed in 1,664.39 ms
with 2,169.78 ms summed parallel source work, 9.55 ms linking, 0.99 ms native work
and zero attributed joins. `XRE_VK_ADVANCED_FORCE_UNAVAILABLE=1` is a validation-only
renderer-restart override; it produced `Unsupported`/`Rejected`, no preparation,
no execution admission and no downgrade. Target-specific native graphics pipeline
creation remains S05; global invalidation maintenance and mutation-scope localization
remain S04. The independent canonical texture `SourceMismatch` still prevents a
visual-quality pass and is not an S03 readiness failure. See [S03 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s03-gate-record-nonblocking-advanced-readiness).

## S04. Localize Compile Invalidation Safely

Anchor: [VulkanPipelineCompileQueue.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs#L624).

- [x] Measure mutation reason, affected owners, global invalidations, drained jobs,
  publication waits and stale completions. Document lock order and all dependency
  lifetimes before replacing the global mutation gate.
- [x] Distinguish additive cold program creation from shader/interface replacement,
  layout destruction, renderer shutdown and device-wide invalidation.
- [x] Introduce only the missing owner/dependency generation checks and immutable
  retention. Reuse existing leases and preserve necessary device-wide barriers.
- [x] Reject stale results and release each result/dependency exactly once.
  Abandoning a managed task cannot cancel native compilation; retirement must
  wait for both compiler and GPU users, including cache-publication users.
- [x] Validate an unrelated new program while other jobs are pending, replacement
  while an old job is queued/running/completed-unpublished, repeated reload,
  window/renderer teardown and the available isolated recovery path.
- [x] Inspect drain counts, compile progress, stale-result disposal, deadlocks,
  live native handles and retirement backlog across repeated cycles. Do not induce
  a machine-wide GPU reset merely to exercise device loss.

Gate: unrelated additive work does not invalidate/drain unaffected owners, all
required dependencies remain retained, and memory/backlogs settle after use.
Unvalidated lifetime behavior blocks advancement even if frame time improves.

Status: Validated. Additive links and first shader-module creation now retain the
existing dependency lease without advancing the device generation. Program/layout
and shader-module replacement use exact dependency scopes; only deliberate
device-wide mutation advances the global generation and clears all completion
caches. Scoped mutation rejects pre-replacement requests at enqueue and worker
entry, drains only matching native compilers, removes matching terminal results,
destroys unadopted compute pipelines immediately, and defers shared graphics
pipeline retirement until GPU use completes.

The authoritative cold run recorded 38 additive links, 62 scoped mutations, zero
global invalidations, zero drained jobs and zero publication waits. Four shader
reload cycles remained admitted with both compile queues bounded at zero; the
longer cycle reached 948 scoped mutations with one affected compute drain, 84
stale completions and 54 exact stale disposals, while global invalidations and
publication waits stayed at zero. A final isolated reload advanced 12 captured
frames and settled graphics/compute compile queues, pipeline/layout retirement,
and total retirement backlog to zero with no quarantined failure or
`vkDeviceWaitIdle`. Owned sessions stopped cleanly and the final logs contained
no matching validation, watchdog, compile/quiesce, disposal, deadlock or
double-destroy failure. No machine-wide reset was induced. See the
[S04 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s04-gate-record-dependency-scoped-compile-invalidation).

## S05. Bound Remaining Cache And Native-Creation Work

Anchor: [VulkanPipelineCache.cs](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs).

- [x] Measure foreground native creation, foreground/background cache locks,
  merge, capture and persistence separately. Retain existing isolated caches.
- [x] If foreground misses matter, implement capability-gated cache-only probes
  with queued preparation and Pending results, never an immediate blocking retry.
  Check `pipelineCreationCacheControl`; even a cache hit is not a hard latency bound.
- [x] Separately, if publication/capture contention matters, schedule or batch it
  without weakening merge, destruction, shutdown or persistence synchronization.
  Treat this as another child fix with its own validation, not the same A/B.
- [x] Validate persisted/runtime hits, misses, unsupported capability, publication
  overlapping other jobs, missing/rejected cache data and orderly teardown using
  isolated inputs. Do not alter the cache storage format without approval.
- [x] Verify needed entries survive restart, compile progress is not starved,
  cache memory stays bounded and foreground cost is not merely moved into another
  foreground lock. Keep the conservative worker count unless measured evidence
  justifies a separate concurrency change.

Gate: each measured source of blocking meets its budget without losing cache or
lifetime correctness. If absent from the trace, defer the candidate with evidence.

Status: Validated. Device-lifetime telemetry now separates foreground/background
native creation and cache-host waits from cache-only probe outcomes, merge,
capture and persistence costs. The measured paths did not justify new foreground
queue semantics or publication scheduling: the stress cohort's five foreground
creates totaled 3.85 ms with a 3.042 ms maximum; foreground host waits totaled
0.0336 ms with a 0.001 ms maximum; 250 merges totaled 1.1972 ms with a 0.1936 ms
maximum; and one 927,965-byte autosave captured in 0.393 ms and wrote in 0.5293 ms.

Persisted cache headers are now validated against Vulkan header version, selected
vendor/device and pipeline-cache UUID before driver use. Malformed, mismatched or
driver-rejected initial data recreates empty foreground/background caches rather
than disabling caching for the process. A mismatched isolated header produced
exactly one rejection and recovery, `warmBytes=0`, bounded worker creation, zero
pending queues, then a valid 927,965-byte replacement cache. Cold, warm, three
reloads, autosave, rejected-data recovery, and the validation-only unsupported
`pipelineCreationCacheControl` branch all completed. The unsupported cohort
reported the feature disabled, zero cache-only probes, zero foreground creates,
eight background creates totaling 4.5444 ms and settled queues. All owned sessions
stopped cleanly, failure scans were empty, and worker count remains unchanged. See
the [S05 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s05-gate-record-bounded-cache-and-native-creation-work).

## S06. Budget Initial Resource Materialization

Anchor: [XRRenderPipelineInstance.cs](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs#L1810).

- [x] Identify initial versus replacement builds and per-spec/factory duration;
  confirm the initial `TimeSpan.MaxValue` / `int.MaxValue` bypass is on the trigger.
- [x] Define bounded first-generation preparation or an explicit loading phase,
  with a responsive editor and no partially published resource generation.
- [x] Preserve stale-key rejection, transactional commit, imported-resource
  ownership and active/pending/retired separation. Do not run installed build/view
  contexts or renderer-affine factories wholesale on worker threads.
- [x] If one indivisible factory exceeds budget, split only that factory and
  validate it separately. Inter-spec checks cannot bound its internal work.
- [x] Validate first creation, replacement, repeated resize/pipeline change during
  preparation, cancellation/supersession, failure and recovery. Check both first
  valid output and retirement after superseded builds.

Gate: slice and worst-spec cost meet the chosen budget, no partial generations
escape, and time-to-first-valid-frame is reported alongside frame pacing. Moving
work into loading is not evidence that total preparation cost decreased.

Status: Validated. Initial and replacement generations now share bounded,
owner-thread materialization: ordinary slices stop at 2 ms or four completed
specs, resize catch-up stops at 8 ms or 16 completed specs, and staged factories
split renderer-affine preparation so one stage remains below the 16.67 ms frame
limit. The accepted cold cohort recorded 79 slices, 191.57 ms total work,
15.44 ms worst slice and 15.02 ms worst stage; the accepted replacement recorded
33 slices, 9.78 ms total work, 1.98 ms worst slice and 0.48 ms worst stage.
Repeated resize superseded the intermediate keys and published only the exact
1384x751 generation. A staged one-shot failure at spec 146/198 preserved the
active generation, disposed partial state, observed the one-second retry
backoff, and recovered automatically to the exact 1484x811 generation. Retired
active generations were disposed only after signaled fences.

The cold generation built in 842.75 ms and spent 191.57 ms in owner-thread
materialization; its first active-generation package followed commit by 157 ms.
Request-to-package was 2.335 seconds including startup/world readiness. Stable
log samples reported 15.19-16.86 ms render intervals. A deliberately intrusive
12-frame, stride-three Vulkan readback sequence advanced render IDs 3816-3849
without a failed or dropped capture. The owned session shut down without Vulkan
validation, device-loss, upload, plan, timeout or disposal failures. See the
[S06 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s06-gate-record-bounded-initial-resource-materialization).

## S07. Separate Mesh CPU Data And Wrapper Publication

Anchors: [XRMesh.BufferCollection.cs](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.BufferCollection.cs),
[RenderObjectPublicationScope.cs](../../../../XREngine.Runtime.Rendering/RenderObjects/RenderObjectPublicationScope.cs)
and [GenericRenderObject.cs](../../../../XREngine.Runtime.Rendering/RenderObjects/GenericRenderObject.cs#L95).

- [x] Separate allocation/zero fill, buffer callbacks, vertex population, wrapper
  creation and wrapper-lock waits, recording vertex counts and bytes.
- [x] Reuse importer CPU-preparation/suppression patterns only where measurements
  justify them. Define the owning publication thread and an immutable completion
  boundary; suppression alone must not expose partially initialized objects.
- [x] Preserve all constructor initialization, revision notifications and failure
  cleanup. Do not replace constructors with an apparent fast path that omits
  subscriptions or reintroduce nested `Parallel.For` starvation.
- [x] Validate small helpers and a large imported mesh, material/buffer changes,
  revision during preparation, failure/disposal before publication and multiple
  consumers. For backend-neutral changes, verify both Vulkan and OpenGL.

Gate: correct geometry/bounds/attributes and revision behavior, no partial object
discovery, bounded owner-thread publication and no new allocation/retention leak.

Result: validated. Thread-affine nested publication transactions now keep mesh
CPU data, render objects, backend wrappers and compound renderer resources hidden
until one root commit; rollback restores collection and convenience references,
releases leases and destroys unpublished resources in reverse order. Per-key mesh
replacement, shader-version first use, skin/blend/deformation inputs and compute
outputs publish as coherent generations. Vulkan cold first use queues owner-thread
upload/materialization and retains pending generations instead of synchronously
joining or exposing partial state.

Small-scene and Sponza live gates passed on Vulkan and OpenGL with zero wrapper
failures, zero wrappers created during CPU preparation and zero off-owner wrapper
creates. The final 209,613-vertex Vulkan Sponza run prepared 10,056,976 buffer
bytes and published 24 Vulkan wrappers; the final 210,864-vertex OpenGL run
prepared 10,097,120 bytes and published 134 OpenGL wrappers. Revision races,
replacement, callback failure, nested commit/abort, concurrent first use,
early disposal and multiple-consumer retirement also passed. See the
[S07 Gate Record](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s07-gate-record-separate-mesh-cpu-data-and-wrapper-publication).

## S08. Prepare Indices Before Draw Admission

Anchor: [XRMesh.Geometry.cs](../../../../XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Geometry.cs#L358).

- [x] Establish which cold/revised indexed meshes synchronously join preparation.
- [x] Request work before admission using immutable topology and exact revision
  tickets. Preserve explicit pending/failure outcomes and completion ownership.
- [x] Validate cold indexed geometry, unchanged reuse, topology mutation while
  pending, stale completion, disposal, nonindexed geometry and renderer switching
  where shared code changes. Confirm submitted draw ranges remain correct.

Gate: normal draw admission no longer joins index preparation, stale indices are
never used, and required geometry is not silently skipped. If no join matters,
record a conditional deferral instead of performing a speculative rewrite.

Result: validated by merged PR #75. Index inputs are captured for an exact mesh
revision, Vulkan draw admission requests and polls preparation instead of joining
the worker, and topology changes invalidate obsolete cached buffers. The current
branch also completed warning-free rendering builds and Vulkan/OpenGL live smoke
while exercising indexed scene and fullscreen composition. See the
[S08 gate record](../../investigations/rendering/2026-09-21-s08-index-preparation.md).

## S09. Share Helper Geometry Only When Justified

- [x] Count duplicate fullscreen/debug/light-volume constructions and establish
  their actual cost after S07-S08. Audit existing reuse before adding more.
- [x] If justified, define immutable shared geometry with per-consumer material/
  renderer state, explicit ownership and safe retirement. Apply one geometry
  category at a time and validate it before expanding reuse.
- [x] Validate the selected category across pipelines, owner teardown, custom
  shaders, relevant stereo views, and OpenGL/Vulkan behavior. Confirm window
  ownership remains wrapper-scoped, and inspect debug/light-volume candidates
  before leaving them unchanged.
- [x] Keep procedural fullscreen drawing as a separate deferred design unless
  measured need warrants it; it requires explicit topology/count and shader/view
  compatibility, not simply deleting a dummy mesh.

Gate: sharing reduces measured construction without mutable cross-consumer state,
premature disposal or new retained lifetime. Unchanged visual output is required.

Result: validated for the fullscreen-helper category only. `XRQuadFrameBuffer`
now leases one immutable-by-contract CPU mesh per fullscreen topology while each
consumer retains its own renderer, material, shader callbacks, versions and
multiview state. The final Vulkan Advanced run served 60 triangle acquisitions
with one construction; OpenGL Default served 27 with one. Pipeline-cache teardown,
recreation and renderer replacement preserved output and retired the compatibility
quad at its last lease. Light volumes, debug primitives and procedural fullscreen
drawing remain unchanged pending separate evidence. See the
[S09 gate record](../../investigations/rendering/2026-09-21-s09-shared-helper-geometry.md).

## S10. Remove Cold Toolbar Work From Drawing

Anchor: [EditorImGuiUI.Icons.cs](../../../../XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs#L41).

- [x] Distinguish path lookup, SVG parse, Skia rasterization, texture construction
  and upload. Check whether the first-use delay explains the observed toolbar scope.
- [x] Prepare pixels before use or on bounded workers, then publish/upload on the
  correct owner. Do not move mutable caches/counters or the whole current texture
  factory onto arbitrary workers. One icon per frame is not a time bound.
- [x] Validate first toolbar display, all icons warmed, multiple sizes/windows,
  unavailable/malformed isolated icon inputs, shutdown during preparation and
  repeated requests. Preserve useful pending/failure behavior and bounded retries.
- [x] Inspect actual toolbar images and interaction, pixel-buffer ownership,
  upload budgets and cached resource retirement on both affected backends.

Gate: no synchronous file/parse/raster work remains on the normal draw path,
icons eventually render correctly, and warm allocation/latency stays within budget.

Result: validated. The 12 toolbar SVGs now use one cancellable sequential CPU
preparation worker; the render owner publishes bounded texture/preview work in a
separate profiler scope, and `DrawToolbar` performs only ready-handle lookup.
The matching cold Vulkan toolbar leaf fell from 729.970 ms to a sampled ready
0.023 ms, while OpenGL and Vulkan each reached 12/12 ready icons. The inspected
OpenGL composited capture showed correctly oriented transform, space, snap and
playback icons, and a Vulkan renderer restart reuploaded without rerasterizing.
One indivisible cold Vulkan preview upload had reached 85.753 ms outside the draw
scope. Follow-up diagnosis reproduced a 62.970 ms versus 5.535 ms cold
first-publication spread for the 2.3 KiB icon and found three graphics-queue
submit-and-wait operations in the legacy preview path. That residual is now
remediated: Vulkan honors `UploadIfNeeded`, performs published-descriptor lookup
without `PushData`, admits prepared pixels through the existing generation-owned
`VulkanTextureUploadService`, and returns pending until publication completes.
An isolated Vulkan run completed 12 worker preparations, 12 transfer chunks and
12 final publications with zero upload failures; after initial scheduling,
owner polls were 0.011-0.476 ms. A renderer restart reuploaded 12/12 icons with
0.013-0.091 ms owner polls and no rerasterization. Composited captures before and
after restart showed the toolbar icons present and correctly oriented. See the
[S10 gate record](../../investigations/rendering/2026-09-21-s10-toolbar-icon-preparation.md).

## S11. Bound Inspector Discovery And Metadata Work

Anchor: [CameraComponentEditor.cs](../../../../XREngine.Editor/ComponentEditors/CameraComponentEditor.cs#L343).

- [x] Measure first-use create/replace type discovery separately from warmed
  property drawing, enum arrays, attribute lookup and tooltips.
- [x] Defer measured cold discovery from passive drawing and run it only when
  Create/Replace is requested.
- [x] Validate first panel open, Create/Replace menus, pending discovery, errors
  and script reload before changing warmed metadata handling.
- [x] Then, if material, cache immutable setting descriptors/enum labels and avoid
  tooltip metadata work until hovered. S11b is deferred: warmed camera settings
  measured about 0.12-0.21 ms, below the 1.0 ms entry threshold.
- [x] Key invalidation to script/assembly generations and avoid indefinitely
  retaining collectible assemblies in editor discovery and fallback caches.
- [x] Validate settings mutation notifications, undo behavior, labels, enum
  options, asset creation and failure diagnostics in the live editor.
- [x] Validate different camera/pipeline settings, repeated panel opening,
  changed script types, warmed no-hover drawing and tooltips. Inspect the UI and
  confirm allocations and retained metadata return to their expected baseline.

Gate: no measured cold discovery blocks passive drawing, cached settings remain
current after reload, and visible editing behavior is unchanged.

Status: Validated. The first passive camera-settings draw spent 356.009 ms in
synchronous creatable-type discovery. Discovery now starts from the requested
Create/Replace popup on a serialized worker; passive Vulkan settings draws
measured 0.120-0.213 ms in the changed Release run, with no synchronous discovery
scope. An exact post-review Release editor build and composited Vulkan capture
passed. Generation-owned caches, unloading-context filters, bounded retries and
owner-thread retirement received an independent review with no blocking finding.
The disposable XRENGINE `S11Smoke.xrproj` passed live pending/ready, creation,
notification, undo/redo, constructor-failure preservation, discovery-failure
Retry, search/empty-result and camera enum/tooltip checks. Reload replaced V4
with V6 in the open picker while the old V4 instance was still alive; explicit
unload cleared the discovery entries. That check exposed and fixed the game
loader's weak context ownership, which could finalize a logically loaded
assembly context. The loader now owns its context strongly until explicit
unload; forced GC no longer hides current script types. The final Release build
passed with zero warnings and errors. A passive per-frame selection closure was
also removed. S11b remains deferred; new regression tests await explicit
post-validation clearance. This is Validated, not Closed. See the
[S11 gate record](../../investigations/rendering/2026-09-22-s11-camera-inspector-discovery.md).

## S12. Improve Shared Advanced Preparation Safely

Anchor: [AdvancedSharedPreparationService.cs](../../../../XREngine.Runtime.Rendering/Rendering/Preparation/Advanced/AdvancedSharedPreparationService.cs).

Merge disposition (2026-09-23): local `3893e7e6d` and incoming `4a0d4a2f6`
independently optimized the same planner. Retain the local hash lookup and
remembered payload-range indices once, plus incoming scene/publication identity,
temporal/feedback epochs, view-capacity handling, geometry compaction and bounded
static-deformation generations. The gate record preserves both parents' evidence
with their workload identities; the 393-draw and 465-draw speedups are neither
additive nor measurements of the merged binary. Equivalent timing counters are
consolidated; renderer-owned `framePlanInputCopyDiagnostics` owns the second copy.

Incoming AA lifetime dependency: [AA-B1](advanced-pipeline-antialiasing-todo.md#aa-b1-vulkan-visibility-snapshot-lease-exhaustion--fixed-live-paused-path-verified)
retires receipt-bound work on a terminal/recoverable PresentNow pause; its
reproduced pause survived 37 further rejected frames without the 16-family lease
exhaustion. [AA-B1b](advanced-pipeline-antialiasing-todo.md#aa-b1b-vulkan-sponza-scene-publication-capacity--capacity-fixed-visual-gate-blocked)
shares immutable geometry across frame slots; its 465-draw fixture admitted about
704 MiB of shared geometry without the old frame-storage ceiling. Retain AA-B1c's
fresh-output/streaming/framebuffer/active-mode gate and AA-B2/AA-B3's OpenGL stage
and AA transition issues under that ledger. Later narrow Release S12 captures and
short compaction/reactivation plateaus do not close those Debug AA or long-running
structural/material churn gates. Require fresh accepted mesh output for each
workload comparison; no S12 regression is established by those earlier AA failures.

- [x] Measure cache misses, cold capacity growth, extraction, deformation,
  publication/copy bytes and lock waits separately. Establish actual world/view
  consumers before assuming single-publication thrashing.
- [x] If growth matters, pre-size/reuse suitable storage first and validate it.
  Only then consider moving measured construction out of the shared critical
  section as a separate lifetime-reviewed item. Observed arena/upload growth and
  shared-lock waits did not justify either intervention; the measured range
  planner was optimized with preallocated lookup storage instead.
- [x] Retain coherent immutable generations and consumer leases. Never expose
  mutable extractor spans to remove copies, and never reuse storage while a
  deferred consumer still reads it.
- [x] Validate stationary/moving scenes, changed geometry/materials, multiple
  views, deformation where active, supersession and consumer teardown. Audit
  whether actual same-frame alternating worlds can occur: first-wins world
  publication makes that case unreachable, so it is explicitly dispositioned,
  not claimed as a live test. Introduce per-world caching only if evidence
  warrants it.

Gate: extraction and contention meet budget; every consumer gets the right scene,
view and generation; copied data remains coherent and retired storage is bounded.
Do not mislabel nonblocking deformation polling as a proven GPU wait.

Local parent result: **Validated for its reachable S12 paths**. The [S12 gate record](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md)
contains matched three-window Release Vulkan/Advanced measurements: the
range planner cost 0.152-0.154 ms per rebuild before remediation, then
0.043-0.053 ms; total Build mean fell 30-34%. Both retained Vulkan copy
boundaries measured about 0.010-0.013 ms per successful family, and the
shared lock was inexpensive. The active-deformation typed write and OpenGL
capability queries no longer allocate per warmed extractor Build. Live gates
covered stationary/moving views, scene/material/transform changes, three
emulated visibility views, active deformation, copy/publication retry,
supersession and session teardown/recreation. Arena and upload retention
remained bounded. Forced growth, duplicate published geometry keys and
production XR hardware were not exercised; actual same-frame alternating
worlds are prohibited by upstream first-wins publication. S12 does not claim
to resolve the original severe Vulkan frame-time report or S13's recurring
publication/recording costs.

Current combined status: **Active for the merged lifetime scope**. The incoming
parent additionally passed narrow geometry compaction/live-record copy-forward,
animated reactivation and distinct inspected single-pass stereo-layer checks.
Its bounded static-generation change has not been exercised with two distinct
runtime-world owners. First-wins publication prohibits same-frame alternation;
it does not exclude owner changes across frames with older work in flight.

- [x] Record a merged Release build and live Vulkan/Advanced smoke with fresh
  inspected output, canonical draw/range counts, both copy boundaries and warmed
  allocation/failure deltas. Do not infer a new performance gate from a smoke run.
- [x] Validate the merged view-capacity failure ordering: oversized request,
  identical retry, then a valid set. Both oversized attempts must reject without
  remembering success or admitting stale plans; the valid request must still work.
- [ ] Validate distinct runtime owners across frames, retained GPU consumers,
  deformation/static-generation identity, topology replacement and teardown, or
  explicitly disposition the missing fixture. The same-host snapshot/restore
  exercise is not evidence for this gate. Keep generations bounded and retire
  only after completion; do not add per-world caches without measured need.
- [ ] Revisit the incoming unit-test compile blocker (absent
  `IAdvancedGlobalIlluminationProvider`) when running existing targeted tests;
  record its current result. New integration tests still require explicit
  post-validation clearance. Preserve unexercised forced-failure, duplicate-key,
  hardware XR and long-duration churn limits rather than marking them passed.

Merge validation: the final Release editor built with zero warnings/errors.
Vulkan retained 393 draws/ranges across 1,147 warmed rebuilds with zero Build
allocation bytes or copy-failure increase; both copy diagnostics were live.
The isolated capacity-retry probe passed for initial and incremental requests.
Focused, settled captures showed Sponza geometry after the origin views proved
inconclusive; this is limited smoke coverage, not a new AA/interior-shading or
temporal-quality pass. Details and evidence limits are in the S12 gate record.

## S13. Reduce Recurring Recording And Source Preparation

Status: Pending. The child phases below are future work, not implemented fixes.
S12's local reachable gate passed; its merged lifetime gate remains open.
Preserve the September 23 S13 entry evidence and plan. Resolve or explicitly
disposition that S12 integration gate before dependent S13 implementation work;
this does not erase the already measured and reverted identity-filter experiment.
Each child has its own entry evidence, one implementation change, focused build,
live validation and gate record. If a child needs independent changes, split it
again and validate each increment. Disprove/defer candidates that are already
cheap or eliminated by an earlier phase instead of implementing them anyway.

The intended steady-state contract is retained mesh/material/plan state plus
bounded updates for genuine mutations. Publication must expose a fresh, coherent
snapshot without using its advancing identity as evidence that scene content
changed. Reuse the existing canonical database, dirty queues, frame packages,
generation tracking, resource planners and lifetime leases. A second cache or
publication service is not the default remedy.
Run affected S15 temporal checks after every identity/publication change. Reopen
validated S02/S04/S05 work only with evidence of a regression in its owned mechanism;
the header/ledger now agree with those already-recorded S04/S05 validation results.

Execution order for the measured collect wait: use S13a to close the evidence
and workload-comparability gaps, then implement and validate S13b, including its
affected S15 checks, before continuing with residual S13c-S13h candidates. This
fix belongs in S13b now; it does not wait for S14, completion of all recording
optimizations, or the later integrated S15/S16 closeout. Reuse the recorded
causal result and S12 findings when their identities remain applicable. Refresh
the comparison for changed source/binaries or missing controls, and label why a
rerun was needed rather than restarting the investigation without a reason.

September 23 scope decision: the user explicitly requested wrapping the current
S13a observation and executing S13b now. S13a's existing evidence is handed off
without marking its open composite gate Validated or Closed. S12's distinct-world
lifetime check, final S13a observer/retention checks, attached-debugger condition,
historical backend divergence and exact callback interval attribution remain open.
Proceed with S13b's narrow identity feedback change under its own live correctness
and performance gates; do not treat this decision as evidence that those prior
checks passed or start residual S13c behavior changes on that basis.

Planning estimates for this bounded continuation are 2-4 hours for the permanent
identity fix plus mutation/ordering validation, 1-3 hours for its temporal checks,
and an initial 1-3 hours to explain the OpenGL queue difference. Allow roughly one
focused working day, with shared setup/evidence work overlapping; this is not an
estimate for all of S13. A timing-dependent backend divergence may require another
day, and a newly exposed concurrency/temporal defect needs its own scoped estimate.
Reassess after the first correlated backend trace and mutation run. Estimates are
planning guidance, never a reason to waive or mark a gate passed.

### S13a. Reproduce And Attribute The Current Publication/Recording Path

Owner: Profiler with Runtime Rendering and Vulkan. This phase changes observation
only if existing telemetry cannot answer the questions; validate that change
before drawing conclusions from it.

The September 23 controlled comparison and temporary causal test are recorded in
the [collect-wait investigation](../../investigations/rendering/2026-09-23-vulkan-render-collect-wait.md)
and summarized in the evidence section above.
They isolate the current ~50 ms Release Vulkan `render<-collect` delay to 393
identity-dirtied mesh swap callbacks entering `GPUScene.TryUpdateMeshCommand`;
excluding only the identity notification reduced the wait to 3.0–5.7 ms.
That diagnostic edit was removed. This evidence establishes the S13b entry
candidate but does not close S13a's matched-window, inner-call attribution,
observer-overhead or mutation-validation gates.

The elevated September 23 ETW capture now attributes the current registration CPU
path to `SyncLegacyDynamicAtlasState`: atlas ensuring clears and recopies the
legacy index list and mesh-offset map before the already-resident mesh early
return. The [S13a gate record](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md#elevated-cpu-and-gc-capture)
preserves sampled managed stacks, scheduler totals, GC suspension intervals and
their limits. This identifies an inner owner; it does not close the exact-span
or observer/retention gates, or bypass S13b before remeasuring residual S13c work.

- [ ] Freeze the revision/diff and exact binaries. Record Debug/Release, debugger
  attachment, actual Vulkan validation, CPU profiling, dense/coarse GPU timing,
  diagnostics/logging, effective AA, device/driver/power state, scene, camera,
  internal/output resolution, accepted draw counts and submission path. The HUD
  draw count is not the full Advanced native workload; use canonical/native
  counts too. Verify the requested HUD labels against runtime state.
- [ ] Carry the September 23 reports and representative scope identities into
  the new gate record. Distinguish the unchanged baseline binary, the temporary
  callback-instrumented binary and the identity-exclusion binary. Recover their
  revision/diff/build manifests where available; mark missing identifiers rather
  than assigning the current checkout's identity to historical samples. Preserve
  durable numeric results if ignored evidence has expired. The twenty-sample
  reports are sparse diagnostic observations, not all-frame p95/p99 results or
  completion of the required paired 60-second windows.
- [ ] Reproduce the user's Debug Unit Testing World configuration and establish a
  separate optimized Release baseline. Measure debugger attachment separately
  when available; an unavailable attached run stays unverified. Never combine
  build or observer conditions into one before/after speedup. Reuse S00's minimum
  three matched pairs and at least 60-second warmed windows, with cold and
  controlled-motion cases separately identified.
- [ ] Trace completed, correlated frame/publication/thread spans through dirty
  queue processing, `TryUpdateMeshCommand`, LOD registration, material/state
  resolution, Advanced publication, primary preparation and command encoding.
  Split lock acquisition wait from held-body work, GC pauses and descheduling.
  Apply S02's attribution/observer gate; preserve unexplained time explicitly.
- [ ] Count dirty causes, unique commands/submeshes visited, identity-only changes,
  registration/rebuild/cache-hit counts, allocation bytes, dirty/upload bytes,
  full operation scans, families/stages, scene-preparation calls, readiness
  checks and retained leases. Attribute allocations to the actual owner/thread;
  do not use a single thread counter as a cross-worker total. Keep counters
  bounded and aggregate off the hot path; avoid per-draw strings or logging.
- [ ] Explain the Vulkan/OpenGL dirty-queue difference from the September 23
  investigation. Use the same binary, scene, camera, submission strategy and
  observer settings; match actual collected command identities and accepted
  workload, not just resident draw counts. Correlate publication identity changes,
  property notifications, dirty enqueue/skip reasons, collection authority,
  swap order and callback acknowledgement for the same stationary commands.
  Identify the first differing event and confirm its causal effect with one
  controlled change. Classify the result as extra Vulkan work, missing OpenGL
  updates, different collection membership, or another measured cause; a small
  OpenGL queue alone does not prove correct rendering. Verify a real mutation
  reaches the rendered output on both backends. Preserve any remaining gap
  explicitly rather than labeling shared code as inherently Vulkan-only.
- [ ] In that backend comparison, distinguish a reused publication from a new
  sequence using `AdvancedGpuScenePublisher.TryReuseUnchangedPublication` and
  `HasPlannedPublicationMutation`; record the reason for a new publication.
  Neither advancing sequence nor a small queue alone explains their relationship.
  Track stable source/primitive identity, owning world/view/collection, actual
  notification name, dirty state at collection, any earlier authoritative swap
  and acknowledgement before the measured pass. Include off-camera resident
  commands and multi-view duplicates so different membership cannot masquerade
  as faster processing. Do not assume a FrameGap, changed material or reuse
  failure is the cause until the correlated event trace demonstrates it.
- [ ] Complete the OpenGL comparison with a durable sequence showing the first
  differing event, one controlled confirmation, both backends' accepted output
  after a genuine mutation, and its S13b implication. If a separate OpenGL
  correctness defect is found, give it an explicit owner and gate. Keep shared
  correctness claims pending until resolved; do not expand this into unrelated
  backend optimization or waive S13b's relevant cross-backend validation.
- [ ] Keep residual canonical scene publication distinct from S12 shared
  extraction and S13e Vulkan family preparation. The experiment left about 3 ms
  in `GPUScene.SwapCommandBuffers.AdvancedPublication`; measure its remaining
  owner after S13b under a predeclared entry threshold. If actionable, give the
  measured publisher operation a separate Runtime Rendering child and gate
  before changing it. Do not assume an inexpensive S12 extractor or one Vulkan
  family-preparation reuse change also removes this shared publication cost.
- [ ] Before each later edit, declare the mechanism's entry threshold, target
  p50/p95/p99 and allocation/write budget, paired-run tolerance, accepted workload
  identity and permitted frame latency. Choose timing tolerances from repeated
  baseline variability, not a convenient post-change percentage. A reduced call
  count proves a mechanism change, but does not by itself prove a frame-rate fix.

Gate: the current workload and costly owner are reproducible, new observation
passes its overhead gate, and each proposed fix has a falsifiable entry condition
and predeclared acceptance. Earlier Debug samples alone cannot close this phase.

### S13b. Break Publication-Identity Feedback Without Losing Scene Changes

Owner: Runtime Rendering. Anchors:
[identity publication](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs),
[dirty notifications](../../../../XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand.cs),
and [Advanced publisher](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/Advanced/AdvancedGpuScenePublisher.cs).

The candidate is implemented and under live validation. The earlier diagnostic
filter and current implementation both identify the caller-member name
`nameof(PublishCanonicalDrawIdentities)`, not the two backing-field names or a
blanket suppression of mesh property changes. Retain the fix only after the
checks below; the earlier speed measurement is supporting evidence, not their
substitute.

Earlier September 24 gate note: that Release Vulkan candidate published all 393
fixture draws and held zero identity-only dirty callbacks, other dirty callbacks,
queue additions, swap callbacks, mesh updates, and failures over a 20.1-second
post-mutation stationary interval. Add/remove/re-add, transform settling,
shadow-flag changes on both backends, and Vulkan layer-mask changes reached
accepted rows. A 61.505-second telemetry-off stationary run completed 3,578
frames (58.17/s) with `RenderWaitForCollect` p50/p95 3.280/4.029 ms. The pre-edit
9.64 frames/s capture overlapped a build, and candidate runs varied from 39 to
58 frames/s, so the uplift magnitude is not yet a validated matched comparison.
The final run failed retention (native live resources +1,806; descriptor sets
+1,800). Callback/after-capture races, rejected publication, material/mesh
replacement, multi-view and exact temporal/velocity checks remain open. See
the [S13b investigation](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md)
for exact evidence and binary hashes. Keep S13c pending until this gate is
dispositioned.

Current September 24 disposition: **Blocked**. Subsequent primitive/material,
exact-mesh, sidecar-lifetime, cross-command callback, and publication-lease
repairs passed focused Release builds and live Vulkan checks. The corrected
presentation ledger captured 128 accepted frames, including 64 where the frame
slot differed from the swapchain image, with zero invariant failures. A repeated
180-second-warmup run still failed retention (+1,961 native resources and +1,955
descriptor sets), and the remaining mutation/temporal, multi-view, failure,
matched-performance, dependency, and test-clearance gates below have not passed.
An isolated camera-transition capture localized the retention rise to 372 new
immutable-resource fingerprints on 339 existing mesh descriptor owner groups;
structural owner counts stayed fixed, and repeated identical motion later reused
the retained variants. A follow-up sampled owner showed that `Texture0` and
`Texture1` acquired new native image/view/sampler generations, changing the
published sampler signature and allocation key. That sample does not explain
every variant or prove when old generations can retire; 12 Images, 12
ImageViews, and 12 Samplers were still queued in its final lifetime readback.
The two runs with growing endpoints measured them after camera motion, so they
do not establish stationary-window accumulation. Keep separate stationary and
motion retention gates.
An exact lifetime probe subsequently found that the queued old textures were
pinned by shared `VkMaterial` descriptor sets after GPU completion. The
candidate frame-boundary cleanup now detaches the exact material program state
and retires its pool through normal lifetime tracking. In an isolated live run,
image/view/sampler backlogs cleared and stayed at zero in bounded samples
through 8,400 preparation calls, with completed Vulkan presents and visible
controlled-camera readbacks. Mesh full-key variants and their local sets still
accumulated after a distinct view transition, so native/descriptor retention and the final-binary
observer matrix remain **open**. The probe was removed; neither S13a nor S13b
is promoted to Validated or Closed.
A subsequent probe-free Release build passed with zero warnings and errors.
After roughly 180 seconds of warmup, one stationary 60-second window remained
flat at 15,203 native live resources and 11,651 tracked descriptor sets. A
controlled view change followed by 60 seconds at the second view ended at
16,844 and 13,276 respectively (+1,641/+1,625); image/view/sampler backlogs
remained zero. Returning to the first view added 244 more mesh allocation
variants, while revisiting the second view added none. The motion-phase
retention gate failed on that binary even though stationary retention and old-texture
retirement improved. The exact endpoints and DLL hashes are in the
[S13b gate record](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#probe-free-final-source-retention-check).
The later [local descriptor correction](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#local-descriptor-identity-correction-final-build-retention)
removes unconsumed snapshot resources from eligible local allocation identity.
It eliminated 918 duplicate payload variants in the diagnostic comparison and
kept 801 mesh variants/4,005 mesh sets flat across the probe-free repeated and
unseen camera views. The final stationary interval was flat at 11,410 native
resources and 7,856 tracked sets with zero retirement backlog. These are scoped
retention results; concurrent pipeline changes prevent an isolated performance
claim. Renderer restart then exposed a separate upload-registration failure for
an already published texture generation. Its corrected recovery passed two
successive live restarts, reaching 7,891 and 6,678 presents after A/B/A sequences.
The [restart evidence](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#corrected-renderer-restart-repeated-live-validation)
preserves the exact counts, cumulative validation-layer compatibility errors,
and limits. Broader lifecycle validation remains open.
The [September 24 closeout review](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#september-24-lifetime-reuse-and-closeout-review)
and [descriptor-retention capture](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#september-24-descriptor-retention-owner-capture)
record the exact evidence. Preserve S13c as Pending.

- [ ] Demonstrate the chain on a settled static command: successful publication
  advances its embedded publication identity; `SetField` notifies; generic dirty
  handling enqueues the command; the next swap invokes GPU scene update. Record
  the reason and counts rather than assuming every dirty command is redundant.
- [ ] Separate publication-only notification/invalidation from actual draw-state
  mutation. Keep `XRBase.SetField` and required notifications. Do not bypass them
  with direct field writes, suppress all property changes, freeze publication
  sequences, or treat stable handles as permission to reuse an old snapshot.
- [ ] Keep the exact accepted canonical and render-buffer identity available at
  the existing publication boundary. Review commit/abort/retry ordering so a
  provisional publication cannot become a consumable stale snapshot. Preserve
  real dirty reasons arriving before, during or after publication and the next
  owning update boundary; clearing one reason must not clear another.
- [ ] Acknowledge only the mutation state captured by the accepted swap. Exercise
  mutation during a swap callback and after capture, duplicate notifications and
  disposal: the callback's return must not clear a newer dirty transition. Check
  that command fields, canonical identity and previous/current transforms belong
  to one coherent admitted snapshot, including after abort/retry.
- [ ] Review `RenderCommand.SwapBuffers` clearing `_dirty` after invoking callbacks
  and the owning collection clearing queued membership. State which mutation
  state each acknowledgement accepts; reproduce a real change during the
  callback and after capture using a controlled live scenario. If newer updates
  are lost, make the acknowledgement correction an explicit bounded prerequisite
  or child with its own gate. A broader generation/concurrency redesign requires
  evidence and ownership review; do not silently bundle it into the identity filter.
- [ ] Exercise add/remove/re-add, visibility/pass changes, transform and material
  changes, mesh replacement, world/pipeline switch and failed/superseded
  publication. Check command membership, accepted generations and rendered
  content. Cover previous-transform settling, camera motion and an animated
  object so velocity/history updates survive unchanged-scene optimization.
- [ ] Execute and record the mutation/temporal matrix below in the isolated editor
  using repeatable scene actions. For each row, record the source command/primitive,
  world/view, mutation and publication identity, admitted/consumed frame, expected
  visibility boundary and actual output. Inspect saved images or sequences and
  relevant velocity/history buffers. Use approved runtime diagnostics for cases
  ordinary scene actions cannot reach; an unreached race/failure remains unverified.
  This checklist does not authorize adding regression tests before live validation
  and explicit test clearance.

| Scenario | Required proof before S13b can be validated |
| --- | --- |
| Stationary commands; new publication with unchanged handles; reused publication | Identity snapshots identify the accepted publication while identity-only changes produce zero scene-content dirty callbacks. Genuine handle-set/topology replacement is covered separately; no stale snapshot is reused just because handles are stable. |
| Transform change, sustained motion, then stop | The real mutation reaches the declared admitted frame; previous/current transforms advance and settle correctly after motion. Velocity is meaningful during motion and returns to the correct stationary state without requiring redundant publication callbacks. |
| Material value/resource/override, pass/visibility, mesh or primitive-count change | Required membership, bindings, geometry and identity changes reach every affected consumer; unrelated commands stay unchanged. Compare accepted draw coverage and visible output, including an initially off-camera object moved into view. |
| Add/remove/re-add; shared mesh/material; repeated edits before one swap | The final intended state is visible; removed or recycled identities cannot expose stale resources. Coalescing redundant notifications cannot erase a later real change. |
| Mutation during callback or after capture; secondary view and authoritative collection | Acknowledging the captured state leaves any newer mutation pending for its next owning boundary. Shared views cannot clear each other's required updates; no new deadlock or unbounded retry occurs. |
| Rejected, aborted, retried or superseded publication; disposal | A provisional identity never becomes a consumable failed snapshot. Retry publishes coherent content once accepted, prior accepted state obeys its lifetime, and teardown leaves no stale queued command or leaked lease. |
| Camera motion/cut, disocclusion, moving object, resize and pipeline/world/view switch | Run the affected S15 checks here: correlate exact frame/view/history identity, jitter, previous/current matrices, velocity, depth and reset events. Inspect matched stationary and motion sequences at multiple camera positions for stale content, trails or invalid history. |
| Vulkan/OpenGL and affected multi-view consumption | Repeat representative unchanged, mutation and motion cases with matched accepted work and settings. Record the explained backend difference and any unsupported hardware/scenario; missing evidence limits or blocks the corresponding claim. |

- [ ] Compare the permanent candidate against the unmodified and previous
  validated baselines in S13a's matched windows. Report actual callback counts,
  swap/generation-wait and successful-present distributions, observer overhead,
  allocations, accepted draws and resource/lease retention. The earlier mean
  3.51 ms is a reference result, not a predeclared universal acceptance threshold.
  Check that removed callback work did not move into collection, update, native
  preparation or a later frame. Keep the residual Advanced publication, recording
  and GPU costs separately reported and owned.
- [ ] Remove temporary per-command probes or validate any retained bounded
  telemetry's overhead, then rebuild and recheck the exact final source/binary.
  Retain the implementation once its live correctness and performance gates pass;
  record whether the fix is present, reverted or blocked instead of describing
  the original diagnostic experiment as the final deliverable. Keep later test
  clearance/closure status separate. Restore temporary scene/settings changes
  and stop only owned editor sessions before publishing the gate record.

Gate: after warm-up, publication-only changes generate **zero scene-content dirty
events or update callbacks** for the controlled unchanged commands. Their render
snapshots still track the accepted publication. Every real mutation becomes
visible at the declared boundary with no lost/coalesced-away final state, stale
handle or temporal regression. Compare callback count/time, swap p95/p99 and
successful-present latency; validate this change before optimizing its callees.

### S13c. Retain Logical Mesh/LOD Registration By Real Mutation Identity

Owner: GPUScene. Anchor:
[logical mesh registration](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AtlasManagement.cs).

- [ ] After S13b, measure remaining registration calls, including genuinely moving
  meshes whose geometry/LOD definitions remain unchanged. Attribute the two LOD
  lists, four temporary arrays, referenced-mesh hash sets, residency checks and
  logical-table writes. Do not infer their total cost from the enclosing swap.
- [ ] Define the retained registration's complete dependency set: renderable and
  submesh mapping, authoritative LOD snapshot generation, mesh/topology revisions,
  LOD membership/order/thresholds, mandatory resident mesh, streaming policy/epoch,
  residency and atlas relocation/generation. Identify the owner that advances
  each version. A transform-only update must not reconstruct LOD registration
  when those inputs are unchanged.
- [ ] Reuse existing logical-mesh state on a valid hit. Restrict reconstruction,
  reference-count changes and dirty ranges to real registration mutations. Keep
  scratch bounded and reusable for misses; do not replace fresh arrays with an
  unbounded cache. Preserve existing constant-time meshlet freshness checks.
- [ ] Validate single/multiple LODs, shared meshes/submeshes, threshold edits,
  active LOD changes, streaming completion/eviction, atlas relocation, geometry
  replacement and removal/re-addition. Include a failed registration and retry;
  partial state must not leak residency references or become the accepted cache.
  Preserve the prior accepted registration on failure/supersession. Check reference
  counts and retirement after repeated create/destroy cycles and atlas-slot reuse.

Gate: warmed unchanged registration, including transform-only motion, allocates
**zero temporary LOD lists/arrays/hash sets** and performs zero atlas-ensure calls,
redundant logical-table writes or residency deltas on an exact current hit.
Each dependency mutation produces correct IDs,
LOD selection and dirty ranges for every consuming frame slot. Retention is
bounded and the phase's registration/allocation/tail budgets pass. If S13b removed
this work entirely for the target workload, require a measured remaining trigger
or defer this phase with that evidence.

### S13d. Update Material/Draw Auxiliary State Only When Its Inputs Change

Owner: GPUScene. Anchors:
[command updates](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandConversion.cs),
[material IDs](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.MeshMaterialIds.cs),
and [state classes](../../../../XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.Soa.cs).

- [ ] Measure the residual repeated material ID probes, state-class construction,
  transparency writes, mesh-data writes, transform/bounds computation and value
  comparisons. Select one measured owner per increment; do not batch unrelated
  dictionary, equality and bounds changes under this phase's name.
- [ ] Retain stable IDs/state with explicit source revisions and move the unchanged
  check before unnecessary reconstruction. Define which inputs affect each
  column: material override/content, transparency, pass, layer/flags, instance
  count, transform, bounds and deformation. Keep structural registration separate
  from dynamic data and preserve previous-frame/animation semantics.
- [ ] Include consumed material interface, texture/sampler epochs and layout/pass
  dependencies in the relevant key. Count draw metadata, transform, bounds,
  transparency, state-class, LOD-transition and BVH writes independently; a
  `DrawMetadata` equality result cannot stand in for every auxiliary dependency.
- [ ] Make dirty/write decisions against each destination's accepted generation.
  Include initial population, rotating frame buffers, newly allocated backing and
  retry after failure; an unchanged CPU object does not prove a GPU slot is current.
  Do not hide required writes by merely disabling dirty-byte telemetry.
- [ ] Exercise one mutation per dependency, multiple edits before a swap, shared
  materials, override removal, opacity/pass transitions, changing instance count,
  moving/stopping objects, texture/sampler replacement and skinning/deformation.
  Confirm affected records and
  images update, unrelated records retain stable identities, and removing/reusing
  an ID cannot reuse an old cached binding. Compare actual dirty/upload ranges.

Gate: an already-current destination receives **zero redundant writes or heap
allocations from the selected unchanged-state path**; initialization and real
changes still reach all required consumers. Targeted time/bytes improve within
the predeclared budget without moving cost into uploads or rendering stale data.
Dictionary replacement alone is not an acceptance criterion. Each independently
changed owner has its own completed gate before proceeding.

### S13e. Prepare Shared Advanced Scene State Once Per Compatible Family

Owner: Vulkan resource/command preparation. Anchor:
[Advanced family preparation](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs).

- [ ] Count and time `TryPrepareAdvancedVisibilityScenePublication` invocations
  within a family. Separate real preparation/upload/lifetime work from existing
  cache hits. Compare full input identities before concluding stages duplicate
  work; S12 extractor reuse does not establish Vulkan scene preparation reuse.
- [ ] Establish a family preparation boundary and its exact compatibility key:
  renderer/device, backend package/database/publication, native/planner resource
  generations, frame-plan generation, resource frame slot and GPU frame slot,
  family reservation, authoring views and every consumed binding/layout dependency.
  Document stage-varying inputs. Split incompatible contexts instead
  of asserting that every stage or every eye can share one result.
- [ ] Prepare once per compatible key through the existing resource planner and
  consume the accepted immutable result across that family's stages. Preserve
  per-stage target/attachment validation, ordering/barriers and freshness checks.
  Reuse may skip repeated work, never required validation or an upload dependency.
- [ ] Define lifetime ownership for reuse, successful transfer, partial failure,
  retry, cancellation and frame-slot retirement. Each native resource remains
  retained until all recorded/in-flight consumers complete. Avoid duplicate
  retains, early releases and caching a failed/partial preparation as ready.
  Associate shared state only after complete success. Account for partial lifetime
  transfer/association on retry so each retained dependency is retired exactly once.
- [ ] Exercise multiple families and views, early/late visibility stages, alternating
  frame slots, resize/AA change, material/texture replacement, shader reload,
  supersession and teardown with work in flight. Inspect images and publication,
  descriptor/resource and lease identities; missing XR hardware limits the claim.

Gate: on the unchanged successful path, shared scene preparation executes once
per **distinct compatible key**, with remaining stage work identified separately.
Every incompatible mutation refreshes the required state. Preparation p95/p99
and total recording meet their budgets, lease/resource counts settle after churn,
and there are zero stale-generation uses, lost uploads or validation errors.
Failed attempts/retries are separately counted, not hidden as cache misses.

### S13f. Retain Plan-Derived Operation Metadata

Owner: Vulkan command planning. Uses the same primary-preparation anchor as S13e.

- [ ] Measure residual full-operation traversals and family discovery after S13e.
  Count operations, families, visits and allocations on still and moving views.
  Audit existing sealed-plan, variant-manifest and admitted-frame-data reuse first.
- [ ] If actionable, prepare immutable operation-family indices, stage coverage or
  other selected structural metadata once per genuine sealed-plan generation.
  State the complete invalidation key. Keep dynamic availability, frame-slot
  leases, current output/reservation identity and producer readiness checks live.
  Do not remove duplicate-stage or ordering validation merely to reduce scans.
  Include operation-stream sealing revision, graph/planner generation and changed
  target backing in the applicable dependencies, even if a frame-plan object is reused.
- [ ] Validate changed operation order/count, added/removed passes, multiple output
  families, view/AA/target changes, progressive admission, deferred/retried frames
  and superseded plans. Prove coverage and dependency ordering by identity/count,
  including rejected malformed/stale plans using the existing validation path.

Gate: unchanged structural planning performs zero rebuilds for a retained valid
plan; unavoidable per-frame visits are counted and justified. Each structural
change invalidates precisely the dependent metadata. The selected scan's time
and allocation budget passes without missing operations, incorrect ordering,
retention growth or increased lowering/encoding time. Defer if current plans do
not admit safe reuse or the measured residual cost is below the entry threshold.

### S13g. Bound Warmed Pipeline-Readiness Work

Owner: Vulkan pipeline runtime. Anchor:
[Advanced readiness](../../../../XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Advanced/VulkanAdvancedVisibilityPipelineRuntime.Preparation.cs).

- [ ] Prove the actual call chain and frequency from family/stage preparation to
  readiness checks. Time lock wait/body, shader/source identity evaluation and
  program-currentness checks separately. Cold compilation and warmed identity
  validation have different owners; reuse S03-S05 instead of redoing their work.
- [ ] If costly, reuse readiness for an exact accepted dependency generation or
  propagate its immutable result within a compatible preparation transaction.
  Retain reliable invalidation for generated source, shader edits, layout/device
  recreation and capability changes. Do not bypass checks without proving every
  mutation producer advances the key; file events alone may be insufficient.
- [ ] Validate unchanged reuse, unrelated versus dependent shader edits, rapid
  successive reloads, failed compilation, pending preparation, cancelled/stale
  completion and device-generation replacement through the supported lifecycle.
  Preserve explicit pending/failure behavior and zero foreground compilation joins.
  Pending-to-ready and retryable-failure recovery must remain observable when the
  plan stays unchanged; never freeze a pending result behind a structural cache key.

Gate: the selected unchanged readiness path avoids repeated expensive evaluation,
meets its measured budget and introduces no hot-path allocation. All relevant
mutations invalidate before consumption; stale results never become ready.
S03-S05 nonblocking and resource-retirement guarantees still pass. Record cheap
existing validation as Deferred/Not Applicable rather than removing it speculatively.

### S13h. Change Synchronization Only For A Measured Remaining Bottleneck

Owner: Runtime Rendering/Vulkan with explicit concurrency/lifetime design review.
This conditional phase follows removal of redundant work; it is not a mandate
to delete locks or parallelize recording.

- [ ] Remeasure the GPUScene mutation lock and Vulkan Advanced storage gate
  separately. Capture wait/hold distributions, contender/owner identities,
  serialized publication time and worker utilization. Do not apply S12's shared
  extractor lock result to either gate, or call long held-body work contention.
- [ ] Choose one residual owner. Prefer moving proven immutable computation or
  narrowing a critical section over adding threads to repeated work. Document
  source snapshot ownership, lock order, generation recheck, commit/rollback,
  bounded retry/backpressure and resource retirement before implementing.
  The Advanced storage gate currently protects shared arena lanes, transactional
  rollback and preparation scratch used by parallel eye workers. Preserve or
  explicitly replace that ownership proof; a shorter lock alone is insufficient.
- [ ] If parallel recording is justified, assign worker/frame-slot-owned command
  and descriptor pools and sufficiently large batches using existing facilities.
  Do not share externally synchronized Vulkan pools unsafely, invent a second job
  system, or change publication order/drop required work to make a wait disappear.
- [ ] Exercise actual concurrent mutation/publication, competing views/families,
  delayed completion, cancellation, resize and teardown. Verify complete accepted
  outputs, bounded progress/backlog, no deadlock/data race/use-after-free, and no
  publication of partially prepared state. Include failure paths and repeated churn.

Gate: the measured critical path improves by the declared amount with identical
accepted work and frame-latency semantics. Wait reduction alone fails if hold
time, retries, worker backlog or another stage absorbs the cost. Absent a measured
residual bottleneck or adequate concurrent-lifetime evidence, defer the change.

### S13i. Prove The Cumulative Fix On The Reported Workload

Owner: Rendering with Profiler. This is an additional S13 acceptance gate, not a
substitute for any child gate, S15 temporal validation or S16 integrated closeout.

- [ ] Repeat S13a's matched matrices against both the original current-source
  baseline and the previous validated increment. Keep Debug/debugger observations
  separate from Release claims. Record every child disposition and retained diff.
- [ ] Report dirty causes/callbacks, registration rebuilds, allocations/GC, real
  dirty/upload bytes, preparation calls/scans, lock wait/hold, publication latency,
  Vulkan preparation/encoding, successful-present intervals and queue/lease
  retention. Show that removed work stayed removed during still and moving views
  and genuine mutations. Check all-frame p50/p95/p99/max, not only average FPS.
- [ ] Confirm identical scene content, native/canonical draw coverage, effective
  AA/resolution and feature state, then inspect stationary, motion and disocclusion
  images/sequences. No speedup claim may depend on missing draws, stale output,
  reduced quality, skipped required updates or a silent CPU fallback. Validate
  affected OpenGL/shared paths and multi-view paths with explicit coverage limits.
- [ ] Retain valid coarse GPU query identities/coverage and compare GPU and CPU
  independently. If GPU attribution remains open, obtain dense pass timings in a
  separately validated observer configuration and one-effect-at-a-time evidence;
  distinguish enabled preferences from executed passes. Record a separate GPU
  remediation item if needed instead of bundling unvalidated effect changes here.
- [ ] If attribution instead finds cold canonical PSO admission or required texture
  finalization, create/disposition a separate child under the existing pipeline or
  upload owner. Require pending/failure/stale-completion, transfer-before-binding
  and retirement validation. Do not hide remaining work in this closeout phase.

Gate: each retained change has mechanism and correctness evidence plus its
predeclared performance result; cumulative tails, resources and adjacent stages
pass. Account explicitly for remaining CPU/GPU latency and unexplained intervals.
The original frame-rate/TSR report remains open wherever reproduction, temporal
correctness or user confirmation is missing. Test clearance stays separate under
the existing policy; writing these phases runs or authorizes no new tests.

## S14. Address The Actual Core Update Owner

Anchor: [RuntimeWorldLifecycle.cs](../../../../XREngine.Runtime.Core/World/RuntimeWorldLifecycle.cs#L59).

- [ ] Profile actual Normal/Late callbacks, tick order, pending registration drain
  and callback identity. Do not instrument only the unrelated legacy tick list or
  treat XREvent listener indices as world IDs.
- [ ] If pending registration dominates, retain ordered dispatch and improve
  membership/application cost. Define a coherent batch boundary, snapshot sizing
  under the owning lock and activation/deactivation semantics before adding a cap.
- [ ] Validate duplicate registration, add/remove order, changes made during a
  callback, bulk activation, Play transitions and teardown; compare callback
  sequences and final membership, not only timing.
- [ ] If a callback/wait dominates instead, create one item for that exact owner.
  Verify actual probe/physics consumers, worker dependency and timer debt before
  changing scheduling. Do not drop simulation steps or move app-thread publication
  based only on a broad world-update label.

Gate: update ordering and play semantics remain correct, actual work/pressure is
distinguished, and the measured cause improves. A GC/descheduling explanation or
negligible warm update cost defers unrelated tick optimizations.

## S15. Preserve Temporal Correctness And Resolve The Original Report

This track remains separate from stall-removal claims. Run its relevant checks
after every change that affects frame/view identity, admission or publication;
do not wait until integrated closeout to discover broken TSR history.

- [ ] Baseline and recapture stationary detail, controlled camera motion,
  disocclusion, camera cut, resize and pipeline/view switches with matching settings.
- [ ] Inspect saved images/sequences and correlate exact history/view/frame IDs,
  jitter, previous/current matrices, velocity, depth and reset/publication events.
  Check multiple camera positions so stale output cannot masquerade as success.
- [ ] Confirm deferred TAA/TSR bindings still consume the correct immutable
  pipeline-owned snapshot after admission/lifetime changes. Quiet warnings and
  a `TsrOutputTexture` name are insufficient.
- [ ] If a temporal defect is isolated, create one separately reviewed fix with
  the same build/live/A/B/regression gate. Do not lower history feedback, disable
  picking, hide warnings or change quality to mask it.
- [ ] Record whether the original long-recording trigger now reproduces, what
  exact change explains any improvement, and the user's ghosting/performance
  confirmation. Keep unavailable original evidence explicitly unresolved.

Gate: classify temporal behavior as validated, still failing, or unverified with
evidence. Unverified/failing temporal behavior blocks closing the original report,
even when individual CPU items have passed.

## Validation Operations

These are instructions for future item execution, not commands run while writing
this TODO. Select the narrow owning build, then build/run an isolated editor that
contains that exact change. Typical owning projects are:

```powershell
dotnet build .\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj
dotnet build .\XREngine.Runtime.Rendering.Vulkan\XREngine.Runtime.Rendering.Vulkan.csproj
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Do not run all five for every item. Shared-code changes require the relevant
backend/dependent validation; a Vulkan-only change does not require unrelated
application builds. Use isolated outputs if normal editor outputs are locked.

- [ ] Follow repository scratch retention and reserve one bounded task run before
  creating evidence. Use its `logs/`, `reports/`, `mcp-output/`, `mcp-captures/` and,
  when necessary, `renderdoc/` folders. Never rely on disposable evidence as the
  sole durable record of a gate result.
- [ ] Verify requested backend/scene/settings before launch. Use a unique named
  session per live iteration through the session manager; never manipulate the
  user's editor by process name or assume a default MCP endpoint belongs to it.
- [ ] Use `pwsh` where installed; the recorded Windows environment also supports
  this explicit Windows PowerShell session-manager invocation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Start -Name <unique-item-session>
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Invoke-Mcp.ps1 -Session <unique-item-session> -Method ping
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Manage-McpEditorSession.ps1 Stop -Name <unique-item-session>
```

- [ ] Capture CPU profiles and state through that exact session. Store viewport
  captures under the current task run and view the PNGs. For visually ambiguous
  pass/resource failures, use the repository RenderDoc workflow; do not treat
  capture/replay timings as an unperturbed performance benchmark.
- [ ] Review the owned session's logs, separating steady-state issues from
  shutdown noise. Restore temporary settings, stop only owned sessions, close
  capture tools and record cleanup before completing the gate.

### Per-Item Gate Record

Copy this record into the investigation for each item/child; link it from the
execution ledger when its status changes. Use repository-relative evidence paths.

```text
Item / owner / status:
Prior validated item and any approved reordering:
Hypothesis and disconfirming check:
Baseline source/configuration/cache/scene identity:
Change scope and dependency/lifetime invariants:
Predeclared metrics, budgets, tolerance, repetitions and window:
Changed source diff and validated binary/session identity:
Focused build command and result, including warnings:
Live scenarios and exact evidence paths:
Before/after distributions, sample validity and observer overhead:
Correctness/images, failure cases, retention and adjacent regressions:
Pass/fail decision and reason; does it explain the original symptom?:
Test clearance state, approved focused checks and results:
User confirmation / remaining risks / next permitted item:
Temporary settings and owned session cleanup:
```

## S16. Integrated Acceptance And Closeout

Do this only after individual gates pass; it must not be the first validation of
any fix. Compare both the previous validated increment and the original baseline
so cumulative cost shifts are visible.

- [ ] Complete matched cold-start/cache-warm restart and warmed still/moving-camera
  runs; report all-frame p50/p95/p99/max, successful-present intervals, dropped
  timing samples, preparation, recording, waits, GPU and loading time separately.
- [ ] Complete repeated resize, shader/pipeline reload, mesh/index/texture admission,
  multi-view ownership and teardown cycles, including the affected failure cases.
- [ ] Validate Play entry/exit, probe refresh, repeated redraw/picking, attachment
  metadata and idle BVH diagnostics so prior fixes remain intact.
- [ ] Validate first-use/warmed toolbar and camera settings, plus OpenGL/shared
  backend and XR/stereo paths where changed code affects them. Missing hardware
  evidence remains an explicit blocker for that claim, not a presumed pass.
- [ ] Inspect temporal sequences and record user confirmation; retain any original
  symptom that is not reproduced or explained as a separate unresolved issue.
- [ ] Confirm no new hot-path allocations, unbounded native/managed retention,
  queue starvation, unsafe disposal, silent fallback or missing required draws.
- [ ] After explicit test clearance, run/add only the appropriate focused
  regression coverage and record results. Keep uncleared gates visibly pending.
- [ ] Update the investigation with attempted fixes, successes/failures and exact
  validation evidence. Update related master/child items only for demonstrated
  coverage, not by blanket-closing their broader contracts.
- [ ] Assign every deferred/blocked item an explanation and reopening condition;
  distinguish individual validated improvements from closure of the original
  CPU/TSR regression. Record final settings restoration and owned-session cleanup.

Final acceptance: all applicable per-item gates and integrated checks pass, any
remaining exclusions are explicit, and the original report is closed only with
supporting reproduction/correction evidence and explicit user confirmation.
