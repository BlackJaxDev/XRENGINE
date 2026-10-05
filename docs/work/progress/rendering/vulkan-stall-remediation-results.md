# Vulkan Stall Remediation Results

Updated: 2026-10-05. Evidence cutoff: 2026-10-05.

This record consolidates completed work and measured deferrals from the
[remaining-work checklist](../../todo/rendering/vulkan-stall-remediation-todo.md).
Each result applies only to its stated scope. The linked investigations retain
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

### Monado Package Consumption

The October 5 strict stereo correction validates the captured XR package's
collection generation instead of the desktop consumed generation. In the
isolated Release fixture, submissions and both eye-preview frame IDs advanced
through camera changes and a Play round trip. The sampled failure log contains
no collection-generation mismatch. Normal exit balanced 432 image acquisitions
and releases per eye and drained the retired generation. The existing package
validation selection passed 13 tests with zero build warnings and errors.

This is an ownership result. Visual defects, cold preparation, reservation
failures, physical headset feedback, and broader performance gates remain open.
The [Monado investigation](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md)
records the exact fixture, control, candidate, captures, and limits.

The two existing YAML round-trip tests also passed for disabled automatic
capture on probes and their grid spawner. Those owners already carry explicit
default-value annotations. This does not establish an engine-wide boolean audit.

### Deferred Stereo Color Attachment

The deferred light-combine descriptor now declares its destination color slot
with the same framebuffer identity as depth and stencil. Previously, Vulkan
pruned that color attachment and recorded a depth-only combine pass. RenderDoc
now shows HDR color Clear/Store and shared depth/stencil Load/Store in the
strict stereo path. Both HDR layers contain scene color.

Normal Monado interior captures show textured geometry in both eyes through
pose changes and Play. Normal exit balanced 479 acquisitions and releases per
eye and drained the retired generation. The Release build had zero warnings
and errors. Cold final output still contains a magenta line and an undefined-
data pattern. Repeated desktop recording exceptions also remain under review.
These are separate open gates in the
[Monado investigation](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md).

### Stable-Bin Manifest Growth

Visibility manifests now use compact record indices. The builder grows each
row before it accesses the manifest. The prior order threw an index exception
at the first valid draw beyond the initial 256 rows and could also fail after
skipped payloads. Capacity limits, payload identities, and freeze ownership
remain unchanged.

The Release Monado fixture completed 1,300 desktop frames in a 60.22-second
window with zero recording failures. Cold startup and a Play round trip also
had zero recording failures. Normal exit balanced 5,554 acquisitions and
releases per eye and drained the retired generation. This fixes the observed
exception and repeated output recreation. It does not establish the desktop
100 FPS target or complete visual acceptance. The
[Monado investigation](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md)
records the failed control, trace limits, candidate, and retained binaries.

### Generation-Qualified Image-View Ownership

`VkImageBackedTexture` retains each native image-view creation generation for
primary, attachment, cached, and imported views. Retirement checks the exact
receipt before admission fencing and checks identity again after dependency
publication. The service keeps native view ownership. Lifecycle locks and image
or sampler ownership did not change.

The scoped gate passed on the frozen Release candidate. The normal Monado
trigger run, graph-patch-parked cold control, both-eye previews, Play/Edit round
trip, warm 60-second liveness window, and normal teardown passed their stated
ownership checks. The final control matched the frozen source and binary
identities. The focused test rerun passed 11/11 with zero skips and no build
warnings or errors; two tests exercise stale-generation behavior and nine
assert source contracts. See the [investigation](../../investigations/rendering/2026-10-05-vulkan-stall-monado.md#scoped-image-view-ownership-closeout).

This closes only the image-view ownership child. It does not establish temporal
history acceptance, TSR visual quality, 100 FPS, headset comfort, lighting
parity, or cumulative report completion. The next scoped item is accepted
strict-stereo temporal history, with the parked TSR ordering patch as a required
dependency. Its pre-edit gate is recorded in the investigation.
### Earlier Scoped Results

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

## Merged Continuation Results

The following records arrived from the other branch. They extend the earlier
history without converting a scoped pass into integrated acceptance. Dates and
fixtures matter: the September desktop comparison does not supersede the
October 1 failure, and the early exterior shadow route does not establish the
later interior performance target. This merge resolution runs no editor checks.

The retained design is also documented in [Frame Loop Design](../../../architecture/rendering/frame-loop-design.md),
[Vulkan Pipeline Compilation](../../../architecture/rendering/vulkan-pipeline-compilation.md),
and [Play Mode Architecture](../../../architecture/editor/play-mode-architecture.md).

### Earlier Branch Measurements

- [September plan metadata](../../investigations/rendering/2026-09-26-s13f-plan-metadata.md)
  deferred cross-generation retention because it needs publication-stable geometry
  identity. The October scan gate remains deferred, not a completed cache design.
- [September readiness](../../investigations/rendering/2026-09-26-s13g-pipeline-readiness.md)
  resolved raster programs/pipelines once per coverage, meshlet and cull
  combination and removed identity-hash allocation. This is distinct from the
  October indexed-traversal correction and deferred broader readiness reuse.
- [September critical-section work](../../investigations/rendering/2026-09-26-s13h-critical-section.md)
  sorted compact stable-bin keys and permuted records once in place. It did not
  change synchronization; measured acquisition did not justify doing so.
- [September cumulative validation](../../investigations/rendering/2026-09-26-s13i-cumulative.md)
  passed its reachable desktop Vulkan scope on September 27. OpenGL and wider
  coverage remained limited. Keep its fixtures and observer conditions separate
  from the October cumulative run that did not pass.

### Play Transition Results

| Item | Retained result | Record |
| --- | --- | --- |
| Core update attribution | Tick optimization deferred. Reopen at 0.10 ms/update tick cost, 1.0 ms pending application within a second, 1.0 ms mean callback cost, or registration churn outside Play transitions. | [Core update](../../investigations/rendering/2026-09-27-s14-core-update-owner.md) |
| Play transitions | Scoped transition corrections validated. | [Transitions](../../investigations/rendering/2026-09-27-s14a-play-transitions.md) |
| Probe spawner restore | Scoped restore correction validated. | [Probe restore](../../investigations/rendering/2026-09-27-s14b-probe-spawner-restore.md) |
| Component tick timing | Scoped timing work validated. | [Tick timing](../../investigations/rendering/2026-09-27-s14c-component-tick-timing.md) |
| Post-exit publication | Scoped publication correction validated. | [Post-exit publication](../../investigations/rendering/2026-09-27-s14d-post-exit-publication.md) |
| Snapshot identity | Scoped identity correction validated. | [Snapshot identity](../../investigations/rendering/2026-09-27-s14f-snapshot-identity.md) |
| Cooked mesh restoration | Restored CPU vertex arrays; three Play round trips and matching views passed. | [Post-Play cost](../../investigations/rendering/2026-10-03-s14g-post-play-cost.md) |
| Planner generation retirement | Superseded generations now retire instead of retaining their complete image/buffer sets. Final October 3 motion reached 98% of pre-Play throughput, and device-local memory stayed flat from first to third trip. Earlier memory failure is historical. Short-lived output metadata remains a conditional follow-up. | [Post-Play cost](../../investigations/rendering/2026-10-03-s14g-post-play-cost.md#why-the-old-set-was-never-retired-fixed) |
| Intermittent exit exception | Live Edit recovery and stack logging retained. Two of seven exits had failed; about 40 later exits did not reproduce it. Cause remains unresolved and waits for recurrence. | [Exit exception](../../investigations/rendering/2026-09-27-s14e-exit-exception.md) |

### Shadow Recording Results

[Packet lowering](../../investigations/rendering/2026-10-03-motion-fps-shadow-recording.md)
now skips scans that cannot reach the packet minimum. October 3 motion rose
33.6 to 40.1 fresh FPS, with identical images and bounded first-motion descriptor
growth. Affected TSR checks passed. Capacity increases, shadow-uniform share
keys, mapped-arena lease changes and a typed uniform writer were rejected or
reverted; they are not retained remedies. Retired-pipeline reload handling was
corrected separately.

The [directional lane](../../investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md)
reuses desktop sealed bins with a distinct depth-only atlas closure. It preserves
CastShadow/per-cascade bounds, tile clears, post-deformation ordering and receipts.
Optional-program readiness no longer changes required-family identity mid-enqueue.
Failed receipts keep atlas keys dirty for generic retry. Unsupported moment
encodings, materials and extra pages use the declared generic path.

Generic depth-only pipelines retain authored fragment stages. This corrected
the floor-view reference: final parity was 0.044/255 mean RGB difference and
0.064% of pixels above channel difference 16, within 0.5/255 and 0.1% limits.
Cold/reload readiness, controlled failure/retry, movement, activation, masked
eligibility, page admission and three Play round trips have scoped evidence.

The early route measured 123.5-128.2 fresh motion FPS. The later viewed interior
repeat measured 92.09/92.46 with the lane versus 33.74/33.65 generically, with zero
rejected/failed deltas and matching images. Both windows failed the unchanged
100 FPS target. Remaining counterbalanced restarts stopped under the failure
rule. SteamVR/Oculus were active, so final idle-host acceptance also remains open.

Dense directional timing adds two queries per refreshed group: refresh frames
used 20 queries, ordinary frames 18. All 1,049 completed disabled frames used
zero. Eighteen exported detailed samples matched completed refresh frame IDs;
146 history samples averaged 2.923 ms. The interval includes dependencies and
barrier/clear/raster work, but excludes later render-scope closure. It is partial
GPU timing, not exclusive shader time or a performance pass. Existing query
tests passed 21/21; the build had zero warnings/errors.

### Temporal Results And User Scope

The [temporal record](../../investigations/rendering/2026-10-03-s15-temporal-checks.md)
contains viewed stationary, cut, scale, pan, lit fixed-speed disocclusion and
actual resize results. Same-camera Default-to-Advanced replacement rendered
settled Sponza, but retained a cold preparation stall and fine-edge comparison
limit. The performance fixture uses FXAA; these history checks used explicit TSR.

Distinct-camera possession with matched TSR settings produced different viewed
positions, valid per-eye history and reset/seed generations 14/14, 2/2 and 17/17.
The fixed mono channel key correctly stayed unchanged. Exact first-frame
temporal-key/snapshot-uniform attribution remains open.

Deferred TAA jitter aliasing was corrected by retaining pre-commit resolve
uniforms and exposure readiness in pipeline-owned TAA/TSR snapshots. Actual
GPU bindings and motion/cut/resize output were inspected. Later admission,
rejection, view/lifecycle correlation and user acceptance remain separate gates.

On October 3 the user confirmed that the original recording was unavailable
and waived its replay. This removes an impossible replay requirement; it is
not evidence that the original symptoms were reproduced or fixed.

### OpenGL Admission And Comparison Results

The [OpenGL record](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md)
contains the exact corrections and rejected runs:

| Owner | Scoped result | Remaining limit |
| --- | --- | --- |
| Progressive upload | Byte/time-budgeted callbacks and exact two-slot ownership drained all 76 textures by 8.906 s versus controls at 26.6-28.7 s. Up to 256 chunks/callback, 16 MB byte cap and measured 2 ms budget were retained. | Strict complete-texture admission remains; no silent placeholder. |
| Query publication order | Publish the current renderer before statistics readback. GPU queries now resolve at about 2.6 KB/frame. Off/on/off medians were 36.89/37.05/37.09 ms; no multi-second gap in those corrected windows. | Earlier large observer penalties are historical. Full matched harness acceptance remains open. |
| Cascade target mask | Publish scoped matrices/mask after material callbacks; all four tiles receive casters. Explicit 1024 shadow dimensions explain the former cross-backend bias difference. | No unsupported shader-bias change or display-tonemapping pass. |
| Reload | Specialized variants resolve authored dependencies before specialization; stale builds release their claim. Mono include/root/in-memory edits and restoration reached Ready with viewed output. | MSAA/stereo, source replacement and driver-parallel coverage remain open. |
| Harness admission | Require actual stages, current reservation, advancing accepted receipts and collected opaque/masked geometry. Both backends reject sky-only views. Vulkan also requires completed progress and no retained terminal failure. | Diagnostic bypass cannot establish acceptance; no live terminal fault was injected for the guard. |
| Harness shadow settings | Named-light selection, requested dimensions and pre/post readback pass 4096/2048/1024 GL and 1024/2048/1024 Vulkan changes. | This sets before-warmup conditions, not bootstrap behavior. |
| MCP collection reads | Owned summary rows captured under the collection read scope prevent recycled-list enumeration. Both backends passed 120 live polls; existing collection tests passed 13/13. | Other fields remain independently sampled; no per-frame copy was added. |
| Pinned control | Unchanged `9fee4b983`, with matching submodules, rebuilt without warnings/errors. | Cold/settled/warm checks pass 0/100 interior samples; texture-source rejection and stale camera-cut output block throughput comparison. |
| Sparse transitions | Respect bindless leases, originating fence ownership, and coherent sparse metadata/content publication. Cold/warm promotion/demotion drained without immutable-parameter errors. | Forced cancellation/device-failure matrix remains open. |
| Exposure and sample coverage | Mean-relative log floor corrected runaway exposure. Cold/warm predictions matched within 0.002%; three non-average modes in two views matched within 0.001%, at the same fetch budget. | Stereo and row-major spatial aliasing remain outside the scoped correction. |
| Abandoned Vulkan lane contexts | Exact active handle/generation ownership and abort/reset/free cleanup keep scene dependencies out of reused upload commands. Three resize trips and 12 cuts advanced 11,278 completed frames without terminal failure. | Thirteen transient rejections remain recorded; guards and frame-slot waits were retained. |
| Raw depth and HDR after resize | Two viewed positions at 1600x900 and 1920x1080 match cascade depth within one occupied texel and 0.00001 depth quantiles. Resident-chain sampler limits and canonical mip rebasing reduce dark HDR mean differences to 0.479%/0.478%; atrium to 0.098%/0.035%, below the unchanged 1% gate. | Failed stale-output and diagnostic-induced captures are excluded. Final display, temporal and performance gates remain open. |

### Shader Root And Family Reload Results

The [reload record](../../investigations/rendering/2026-10-04-shader-root-reload.md)
documents off-thread disk-root refresh with guarded publication. Clean disk-backed
roots update; unsaved/generated text is preserved. Root/include/in-memory edits
and exact restoration were viewed from two positions on Vulkan and mono OpenGL.

Vulkan authors complete required families while executable pipelines are pending.
Physical capability and reservation identity govern intent; sealed preparation
uses existing retries and starts stale binding refresh at visibility preparation.
All twelve Vulkan capture windows advanced fresh output without terminal rejection.
OpenGL specialized variants share current authored text; all twelve HDR captures
were viewed and unsaved edits survived watcher/manual reload.

Release builds had zero warnings/errors. Existing tests reported 32/32 for
dependency/resolver/cache checks, 96/101 for Vulkan focused checks with the same
five prior failures, and 65/66 for OpenGL with its prior source-string failure.
No tests changed. Wider source-object, MSAA/stereo and failure/lifetime gates remain.

### Hardware And Desktop Results

The [hardware record](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md)
establishes available physical SteamVR/OpenXR hardware. Null bootstrap-lease
success and disabled-extension dispatch checks were corrected. Strict Vulkan
SinglePassStereo then produced left/right preview frames 489/491 and 695/697.
One teardown ended at 182 submissions/per-eye publications, zero end-frame
failures and zero sequential fallback attempts. Both eyes acquired/released
436 images and drained one retired generation.

That run also had 254 no-layer frames and needed roughly three minutes for its
first capture. The diagnostic submission ledger was disabled; zero ledger
counters were not proof of acceptance. The user confirms physical presentation
but reports black/flickering Sponza and old-frame jitter. Visual acceptance fails.

Preview-off isolation removes forced reservation waits but exposes frame-data-slot
refusals. Actual CLR contention totaled only 0.1055 ms over 15 seconds; a sampled
monitor-entry stack did not prove a multi-second lock wait. The redundant-lock
candidate gave no overall recording benefit and was reverted.

Supplemental lighting/AO declaration ownership and callback storage are retained
per backend renderer, material and logical draw slot. Captured values still own
content generation. Viewed eye frames 3553/3555 passed; schema fallbacks fell
from about 380 to 2-4 and reservations stayed near 2,757-2,764 instead of exhausting
131,072 entries. Automated 1600x900 and 1920x1080 resize continued submissions.

Desktop ImGui now keeps its explicit viewport canvas independently of the VR
camera. Live File-menu interaction, panel reflow and eye frames without UI passed.
Interactive border dragging did not execute and remains unverified.

Play with XR no longer stops the timer through duplicate GPUScene publication
or torn view IDs. World pre-collect follows the host render session; view-batch
planning uses call-local storage. Four XR and three desktop trips retained loop
liveness and restored Edit panels. XR submission after Play still stalls at
pipeline admission; a 24-second exit stall and magenta third-entry output remain.

### Memory And Overlay Results

The [memory record](../../investigations/rendering/2026-10-04-editor-memory-retention.md)
and [stereo record](../../investigations/rendering/2026-10-04-openxr-stereo-flicker-and-target-duplication.md)
separate desktop retained-reference fixes from XR's remaining budget failure.
Desktop private memory rose 5.98 to 8.79 GB over three trips then plateaued on
the fourth; live heap was 2.22 GB. Stereo target deduplication reduced device-local
usage from 12.4 to 7.4 GB rather than holding the 2.3 GB target set three times.

October 5's [memory plan](../../todo/rendering/optimization/editor-memory-reduction-todo.md)
supersedes the October 4 baseline for the same settings:

| Measure | October 4 | October 5 |
| --- | --- | --- |
| Private bytes | 24.7 GB | 13.5 GB; 13.2 GB after full GC |
| Device-local Vulkan memory | 7.41 GB | 5.52 GB |
| Live managed estimate | 5.9 GB | 1.6 GB |
| Idle allocation | 54 MB/s | 48 MB/s |
| XR submitted/missed per second | 30 / 4.6 | 32.3 / 4.7 |

Private bytes include mapped device-local memory on this driver. Report them
separately; private minus device-local is still about 7.7-8.0 GB. The 8 GB goal
and XR pacing remain unmet. The dedicated plan owns its open decisions and work.

Overlay statistics now show window means with a stable layout. Buffer-binding
revision changes refresh retained descriptors; stale HUD revisits fell from
68/92 to 0/23. The startup stereo-generation black interval, exposure history
loss and physical visual failures remain open.

### Continuation Validation Limits

Selected existing tests included 86/97 passing and a narrower final upload
selection at 31/32. Sparse checks were 52/61, exposure/metering 6/8, hardening
14/18, final resource checks 4/4 and XR Play checks 39/40. The linked records
classify source-contract failures, including one from earlier slot ownership.
These cohorts overlap and are not one combined current failure count. No new
test clearance or fresh execution is implied by this merge.

Temporary upload/shader probes were removed and authored shaders restored.
Owned editor sessions were stopped; user-owned SteamVR remained running.
Ignored capture files are supporting evidence only. Exact durable results and
rejected hypotheses remain in the linked investigations.
