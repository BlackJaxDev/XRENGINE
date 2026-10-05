# Vulkan Stall Remediation Results

Updated: 2026-10-05. Evidence cutoff: 2026-10-02.

This record consolidates completed work and measured deferrals from the
[remaining-work checklist](../../todo/rendering/vulkan-stall-remediation-todo.md).
It does not report new runtime validation. The linked investigations retain
exact binaries, captures, measurements and case-level decisions.

The cumulative gate remains **NOT PASSED**. The original long-recording and
TSR report remains open. Individual validated changes do not close that report.

Architecture now lives in
[Vulkan scene preparation and publication](../../../architecture/rendering/vulkan-scene-preparation-and-publication.md)
and [editor background preparation](../../../architecture/editor/background-preparation.md).
The [validation protocol](../../testing/rendering/vulkan-stall-validation.md)
owns the repeatable procedure and gate template.

## Evidence Limits

- The original report described repeated 153-165 ms CPU recording and TSR
  ghosting. Its original logs were unavailable locally. The resumed probe's
  sparse 27.1-42.6 ms recording, 17.81-24.85 ms GPU and 15-20 Hz output samples
  did not reproduce or explain that report.
- Seven 500-ms watchdog detections crossed visibility, UI, upload and recording
  scopes. An active leaf did not explain the complete stalled interval.
- Historical `GetHottestPath` paired a descendant path with root duration.
  The 1,285.837 ms mesh and 92.077 ms update labels were not exclusive method
  costs. The [profiler guide](../../../developer-guides/diagnostics/profiler.md)
  describes the corrected attribution contract.
- `TsrOutputTexture` participation proved neither correct history nor absence
  of ghosting. Controlled motion had ready history and finite nonzero velocity,
  but the viewed path did not resolve the original visual report.
- Debug and Release runs, different observers, and different scene fixtures
  remain separate comparisons. Sparse samples are not all-frame percentiles.
  Summed parallel work and overlapping waits are not elapsed critical-path cost.

## Completed Work And Scoped Dispositions

IDs below preserve links to the active checklist and original investigations.
Validated means the recorded scoped live gate passed. It does not mean Closed.

| Item | Retained result and limit | Evidence |
| --- | --- | --- |
| S00/S00a | Comparable baseline and clean profiler toggles validated. Three current-source off/on pairs at `a1f9408e5` passed observer, retention, backlog and loss gates. Motion GPU coverage was 99.625-99.643%. Motion p99 deltas were -1.00%, +3.94%, +6.43%, below the 13.67% disabled-run spread. | [Baseline](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s00-gate-record-comparable-baseline-and-evidence-manifest) |
| S01 | Scope, parent, thread, epoch, publication and frame identities validated. A 1,395-node dump had no hierarchy/interval failures. Linked async/parallel listener checks passed; automatic event conversion was not authorized. | [Profiler attribution](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s01-gate-record-correct-profiler-duration-and-identity-reporting) |
| S02 | Explicit scalar/handle equality removed boxed stable-bin validation/lowering allocations. Median Advanced operation allocation fell 337,176 to 58,376 bytes. Recording median fell 16.601 to 7.097 ms; p95 fell 58.656 to 36.934 ms in the matched profile capture. Rare tails and GPU cost remain separate. | [Warmed allocation](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s02-gate-record-warmed-recording-allocation-attribution) |
| S03 | Generation-owned nonblocking readiness validated through cold/warm, reload, pending/failure recovery and shutdown. Cold admission took 3,904.05 ms; reload took 1,664.39 ms, with zero attributed foreground joins. The validation-only `XRE_VK_ADVANCED_FORCE_UNAVAILABLE=1` restart override rejected admission without downgrade. Canonical texture `SourceMismatch` prevented a visual-quality claim. | [Readiness](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s03-gate-record-nonblocking-advanced-readiness) |
| S04 | Dependency-scoped invalidation and retirement validated. Cold run: 38 additive links, 62 scoped mutations, zero global invalidations, drains or publication waits. Reload queues and retirement backlogs settled to zero. | [Invalidation](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s04-gate-record-dependency-scoped-compile-invalidation) |
| S05 | Cache-header rejection and empty-cache recovery validated. Measured creation/cache costs did not justify new queue, publication or concurrency policy. Five foreground creates totaled 3.85 ms; maximum host wait was 0.001 ms. A 927,965-byte autosave captured in 0.393 ms and wrote in 0.5293 ms. | [Cache and native creation](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s05-gate-record-bounded-cache-and-native-creation-work) |
| S06 | Bounded first-generation materialization, failure/backoff, supersession and fence-gated retirement validated. Cold build took 842.75 ms, including 191.57 ms owner materialization; request-to-package took 2.335 seconds including startup/world readiness. | [Resource materialization](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s06-gate-record-bounded-initial-resource-materialization) |
| S07 | Nested CPU preparation and owner-thread publication transactions validated on Vulkan/OpenGL, including rollback and multiple-consumer retirement. No wrappers were created during CPU preparation or off owner. | [Mesh publication](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md#s07-gate-record-separate-mesh-cpu-data-and-wrapper-publication) |
| S08 | Exact-revision index request/poll admission validated; PR #75 merged. | [Index preparation](../../investigations/rendering/2026-09-21-s08-index-preparation.md) |
| S09 | Shared leased fullscreen CPU geometry validated. Vulkan served 60 triangle acquisitions with one construction; OpenGL served 27 with one. Debug/light-volume and procedural fullscreen changes were not made. | [Helper geometry](../../investigations/rendering/2026-09-21-s09-shared-helper-geometry.md) |
| S10 | Worker SVG preparation and asynchronous Vulkan preview upload validated. Both backends reached 12/12 icons. Initial Vulkan owner polls after scheduling were 0.011-0.476 ms; restart polls were 0.013-0.091 ms without rerasterization. | [Toolbar icons](../../investigations/rendering/2026-09-21-s10-toolbar-icon-preparation.md) |
| S11 | Requested-popup discovery, cache lifetime, picker mutation and script reload/unload validated. Passive draws fell from a 356.009 ms cold discovery to 0.120-0.213 ms. Warmed metadata caching remains deferred below its 1.0 ms entry threshold. New regression-test clearance remains separate. | [Camera discovery](../../investigations/rendering/2026-09-22-s11-camera-inspector-discovery.md) |
| S12 | Local reachable scope validated; distinct-world coverage is Not Applicable by the September 25 decision, with reopening conditions. Local planner mean fell 0.152-0.154 to 0.043-0.053 ms; copies cost about 0.010-0.013 ms per family. Merged smoke and view-capacity retry passed. Parent speedups are not additive or merged-binary measurements. | [Shared preparation](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md) |
| S13a/S13b | Final-binary observer, mutation, retention, debugger and elevated capture gates validated on September 26. Publication-only notifications no longer generate scene-content callbacks. Historical backend divergence is Not Reproducible, with a reopening condition. | [Attribution](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md), [identity feedback](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md) |
| S13c | Registration signature retained on `LogicalMeshState`; transform-only hits allocate no registration scratch. Registration time fell about 120 times for the cube and 550 times for the merged node. Multi-LOD/streaming coverage remains limited. Shape-replacement unregister behavior was also repaired. | [Registration](../../investigations/rendering/2026-09-26-s13c-registration-retention.md) |
| S13d | Typed equality, selective metadata/bounds writes, transparency dirty range and content-gated state-class writes validated as four separate owners. Per-submesh update allocation reached zero. Transform-only motion published zero unchanged cull-control/classification/visibility elements. Fixture limits remain open. | [Auxiliary state](../../investigations/rendering/2026-09-26-s13d-auxiliary-state.md) |
| S13e | Compatible-family preparation fell from 7.0 to 1.0 calls per family, with 6.0 reuses, on one and three families per frame. Reload, scale change, rejection retry, restart and emulated stereo passed. Resize/MSAA and hardware XR remain untested. | [Family preparation](../../investigations/rendering/2026-09-26-s13e-family-preparation.md) |
| S13f | Deferred/Not Applicable. Twelve warmed windows found zero scan allocation and means below 0.05 ms/presentation. No metadata cache was retained. | [Operation metadata](../../investigations/rendering/2026-10-01-advanced-operation-metadata.md) |
| S13g | Allocation-only scope validated: indexed traversal removed 880 bytes/poll at about 431 polls/presentation. Body time fell 27.9% stationary and 42.1% moving. Broader readiness reuse remains deferred. | [Warmed readiness](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md) |
| S13h | Deferred. Twelve windows, 9,653 presents and 91,962 transform writes did not establish material contention. Both approximate acquisition costs stayed below 0.10 ms/present. No synchronization change was retained. | [Synchronization gates](../../investigations/rendering/2026-10-01-remaining-synchronization-gates.md) |

## Publication And Recording Evidence

The [September 22 frame-rate probe](../../investigations/rendering/2026-09-22-framerate-cpu-gpu-attribution.md)
was a separate Debug reproduction. Its stationary profiler-off medians were
121.594 ms present interval, 40.074 ms Vulkan CPU, 28.503 ms primary recording,
8.666 ms nested encoding and 15.423 ms coarse GPU. The two 20-second observer
windows were diagnostic evidence, not a Release or effect-level GPU comparison.

The [September 23 collect-wait experiment](../../investigations/rendering/2026-09-23-vulkan-render-collect-wait.md)
used Advanced/CpuDirect/TSR, 1920x1080 output, 1286x723 internal resolution and
393 canonical draws. Release Vulkan wait samples were about 49-59 ms. One swap
contained 393 callbacks totaling about 48-51 ms. The temporary identity-only
notification exclusion reduced 20 wait samples to 3.0-5.7 ms, mean 3.51 ms.
The temporary filter and callback scopes were removed. The later permanent
change received separate mutation and lifetime validation.

Release OpenGL had about 2.6-3.3 ms wait samples and 4-6 callbacks across
multiple frames. Those were not single-frame callback totals. Equal resident
counts did not prove equal collection, accepted work or correct images.
The measured chain ran from `PublishSourceDrawIdentities` through `SetField`,
dirty enqueue and swap to `GPUScene.TryUpdateMeshCommand`. Later elevated
sampling identified legacy atlas synchronization inside registration.

### September 24 handoff: remaining closeout work

This historical handoff was superseded by the September 26 result below.
It is retained here as the destination for older investigation links.

At the handoff, observation and mutation gates were Blocked. One post-motion
run gained 1,961 native resources and 1,955 descriptor sets. A later run was
flat while stationary, then gained 1,641 resources and 1,625 sets after a view
change. These endpoints did not prove stationary accumulation.

Local descriptor identity removed 918 duplicate payload variants. Shared
material retirement cleared old image/view/sampler backlogs. Corrected texture
rehydration passed two restarts, reaching 7,891 and 6,678 presents with zero
retirement backlog. These focused results did not yet pass the full matrix.
The 21-image automated packet required image/log review before acceptance.
See the [handoff evidence](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#automated-evidence-collection-awaiting-review)
and [lifecycle harness](../../testing/rendering/vulkan-lifecycle-evidence-harness.md).

September 25 resolved packet review, snapshot diagnostics, supported-world
disposition, validation-layer compatibility and focused mutation/failure checks.
The final observer, attribution, OpenGL and test runs were still pending.
The approved layer was 1.4.357.0; the earlier unknown descriptor-heap structures
were compatibility errors, not a zero-error validation run.

### September 26 final closeout status

The final binary was `7ab827983` plus the recorded lifetime fixes, in owned
session `s13-final-0925i`. Exact hashes remain in the
[attribution record](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md#september-25-final-binary-closeout-evening-session)
and [mutation record](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md#september-25-final-binary-closeout-evening-session).

- Fifteen Vulkan mutation rows and an OpenGL subset passed. Idle descriptor
  variant retirement and superseded generated-program eviction closed the
  material-edit retention gap.
- Four observer pairs had flat native/descriptor endpoints, returned backlogs
  and 99.96% GPU coverage. Three pairs met the render allowance in both windows;
  the first pair's motion excess did not recur.
- The attached-debugger Release window measured about 0.5 ms/frame additional
  cost and sub-millisecond collect wait. The operator-run elevated capture had
  zero lost events, identity feedback and mesh updates. It had zero samples in
  the former callback owners. GC suspension was 3.99%, maximum 3.6 ms.
- The OpenGL harness wait p50 was 0.081 ms versus 92.7 ms in the September 23
  harness comparison. Mutations propagated; historical divergence was
  dispositioned Not Reproducible.
- Two-pass VR now collects, swaps and renders through eye-owned viewports,
  pipeline instances and collections. Corrected Vulkan readback uses the eye's
  collection. Both eyes showed Sponza parallax in Release and Debug validation.
- Vulkan 1.4 device bootstrap now queries/enables `pushDescriptor` with the
  extension. Later desktop and emulated-stereo Debug sessions had zero VUID,
  hazard or leaked-object lines.

Continuously animated velocity remained sampled at MCP cadence. Eye exposure
needed time to converge after playspace movement. Managed-heap endpoints varied
with GC phase and were not used as retention evidence.

## Residual Allocation Results

The [October 1 cumulative run](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md)
executed eighteen 60-second diagnostic windows and 27,821 attempts. It remained
**NOT PASSED**. A repaired Host compile guard restored 31 frame/outcome/diagnostic
fields; the incomplete observer cohort was excluded. Stationary dispatch reached
1,901 ms, with 1,888 ms outside recorded Vulkan work. A 242 ms GC-pause delta did
not explain that interval. Fixture, observer, previous-binary and image limits
prevented cumulative acceptance.

The [CPU attribution record](../../investigations/rendering/2026-10-01-cpu-stall-attribution.md)
contains the later focused changes:

| Change | Recorded result | Limit |
| --- | --- | --- |
| Subscription-refresh scratch reuse and unboxed traversal | Zero samples in all three refresh methods; about 28% less sampled allocation per present in its comparison. | Separate baseline from the comparisons below. |
| Program invalidation only for linked-interface changes | 4.269 to 2.872 MB/present; layout-signature and vertex-input construction had zero samples. | Sampled diagnostic windows. |
| Compact uniforms | `ProgramUniformValue` 200 to 80 bytes; dictionary entries 216 to 96 bytes. Combined result 2.093 MB/present, sealed-copy share 1.271 MB. | No snapshot lifetime change; combined reduction 51% against the 4.269 MB control. |
| Scalar prepared-cohort comparison, October 2 | Equality samples fell 1,178 / 125.5 MB to zero. Fresh motion windows fell 2.098 to 2.019 MB/present (3.79%). | Allocation result, not motion acceptance. |
| Worker workspace for immediately drained desktop operations, October 2 | All 16,384 probed rentals had null workspaces before the fix. `MeshDrawOp` samples fell 1,583 to zero; resource-use arrays fell 460 to 9. Total fell 2.019 to 1.646 MB/present (18.49%); sealed copies fell 1.268 to 1.077 MB. | Receipt-owned, captured, ordered-batch and OpenXR paths excluded. Scene-unload retention remains unvalidated. |

The program/uniform traces lost zero events. GC suspension maxima were 459.31 ms
with allocation stacks and 23.96 ms with the lighter observer. Later desktop
pooling maxima were 60.50 ms with stacks and 567.52 ms with GC-only observation,
also with zero event loss. Do not add percentages across controls or attribute
the whole sampled reduction to one nested owner.

Focused Release builds had zero warnings/errors. Twenty root-transform edits,
deactivation/reactivation and reload completed; resident draws returned to 393
and final retirement queues were empty. Viewed images still had black regions.
The uninterrupted camera focus path still eased at its endpoints. None of these
checks proved normal displayed cadence or visual correctness.

Source-reference sealed-copy sharing was measured and **reverted**:
2.898 versus 2.872 MB/present gave no meaningful benefit. Its first incomplete
capture raced live command enumeration and was excluded. Reopen only with new
evidence of eligible source reuse.

## Test And Historical Blocker Dispositions

The September 25 user clearance applied to the focused closeout tests. The
September 26 record reports that five repaired test files allowed a warning-free
test build and all five new classes passed. This supersedes the older generic
test-project compile blocker; it is not fresh validation of the current tree.

The later 306-test source-contract run retained 33 pre-existing failures:
15 lifecycle, 16 `OpenXrTimingPipelineContractTests`, and 2
`VrViewRenderModeContractTests`. Expected literals were also absent at that
recorded HEAD. That HEAD could not compile without the five repairs, so it had
no executable test baseline. Readiness validation later recorded 15 passing
tests and five pre-existing source-text failures. Counts describe their own
cohorts and must not be added as a current failure total.

New work still follows the repository's live-validation and explicit-clearance
rules. These historical approvals do not authorize unrelated new tests.
Separate open issues remain in the remaining-work checklist. The runtime MCP
documentation generator succeeded on September 26 with one added row and none
lost; its earlier failed source-parser output was not retained.
