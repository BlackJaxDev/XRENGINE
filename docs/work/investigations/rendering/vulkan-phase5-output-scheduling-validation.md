# Vulkan Phase 5 Output Scheduling And Camera-Motion Investigation

## Status

Current master Phase 5.0/5.1 results are recorded in the
[2026-08-30 closeout](#2026-08-30-phase-5051-closeout). Phase 5.2 has not started.
The earlier measurements below are historical investigation evidence, not a
claim of current shaded-output parity or Phase 8 performance promotion.

### Historical status: 2026-08-13

Implementation complete as of 2026-08-13; extended validation remains open.
The native FPS-overlay continuity defect is fixed in the sampled recovery path,
and camera-motion stalls are materially shorter, but a fresh exact-source A/B
found a reproducible 15.9-16.2% steady-state render-throughput regression. The
strict unlit-Sponza no-regression and long-duration Win32 resize gates are not
met. This document remains the durable handoff boundary for measurements and
validation.

The restored Phase 5 implementation checklist is complete in
`../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md`. Hardware,
stress, and performance acceptance remain in the companion testing tracker.

## Problem

Moving the editor camera through the unlit Sponza scene can freeze presentation,
reduce frame rate, and make the native FPS text disappear intermittently. Before
the Phase 5 work, the same scene was reported to be fast. The acceptance target
is therefore not merely a stationary warmed frame: camera traversal must remain
responsive while new visibility, pipeline, descriptor, and command-chain state
appears.

Phase 5 additionally requires one deadline-aware executable output DAG,
nonblocking XR/secondary outputs, bounded optional-output deferral, narrow queue
ownership, bounded modal resize, and safe persistent recording workers.

## Reproduction And Findings

### Original camera-motion failure

- The isolated Vulkan session `vulkan-phase5-camera-instability` reproduced the
  freeze after entering an uncached Sponza view. Stable-camera recording was
  approximately 7-17 ms.
- Two rejected frames spent 326.2 and 346.9 ms in recording. The next completed
  frames spent 812.5 and 742.2 ms. Queue submission remained 0.4-8.0 ms, which
  localized the freeze to render-thread CPU preparation/recording rather than a
  queue wait or GPU stall.
- A detailed 419.1 ms frame spent 42.2 ms lowering a new command-chain schedule,
  126.0 ms in primary prewarm, and 189.0 ms encoding the primary command buffer.
- Camera motion exposed cold mesh/pipeline variants. Thirty pipelines compiled
  in one four-second interval, but their reported native compile time totaled
  only 9.24 ms (0.61 ms maximum). Pipeline discovery triggered the cold path;
  native compilation was not the dominant stall.
- The scheduled mesh-secondary executor existed but was not called by the
  authoritative mesh payload recording path. Cold scheduled mesh runs therefore
  fell through to expensive inline-primary encoding.
- Recovery recorded ImGui over the last complete scene but omitted the native
  dynamic-text command buffer. Alternating recovered and completed swapchain
  images caused the FPS text to disappear and reappear.

### Remaining cost after the main fix

Wiring scheduled mesh runs into the authoritative payload path removed the
largest 300-800 ms behavior, but did not restore a strict no-regression result:

| Measurement | Current camera-motion sample |
| --- | ---: |
| Unique frames | 41 |
| Deferred frames | 0 |
| Frames missing dynamic overlay | 0 |
| Vulkan validation errors | 0 |
| Average total Vulkan CPU stage | 86.26 ms |
| p95 total Vulkan CPU stage | 207.19 ms |
| Maximum total Vulkan CPU stage | 218.28 ms |
| Maximum preparation | 23.71 ms |
| Maximum primary handling | 190.97 ms |
| Maximum encoding | 104.52 ms |
| Maximum secondary merge | 6.85 ms |

Later frames returned to roughly 18.7-22.4 ms. A detail-instrumented close-wall
frame completed in 28.5745 ms: 16.2219 ms preparation, 4.3753 ms primary
handling, and 3.9341 ms packet construction, with 673 scheduled/reused chains
and no newly recorded chains. Detailed diagnostics add overhead and are not a
clean performance capture.

### 2026-08-13 render-thread/worker localization

The isolated `vulkan-structural-ledger` session measured the same explicit
camera path from `(60, 15, 60)` to `(-20, 5, -20)` over ten seconds. A warmed,
stationary frame was 4.554 ms whole-frame time, with 2.297 ms command recording
and 0.478 ms in the primary-recording CPU stage. During 60 sampled moving-camera
frames, the distribution was:

| Stage | p50 | p95 | Maximum |
| --- | ---: | ---: | ---: |
| Whole frame | 32.192 ms | 77.728 ms | 98.417 ms |
| Frame-op preparation | 8.153 ms | 17.362 ms | 48.123 ms |
| Primary prewarm | 2.011 ms | 14.225 ms | 21.934 ms |
| Primary command encoding | 0 ms | 31.520 ms | 40.922 ms |
| Primary operation loop | 0 ms | 30.710 ms | 39.899 ms |
| Primary mesh operations | 0 ms | 29.066 ms | 37.915 ms |
| Secondary recording | 0.001 ms | 4.558 ms | 19.812 ms |
| Worker command recording | 0 ms | 4.546 ms | 19.800 ms |
| Render-thread worker wait | 0 ms | 1.163 ms | 3.343 ms |

The final sampled frame was 16.188 ms with 373 reused chains and no newly
recorded chains. Vulkan validation remained at zero errors. This narrows the
remaining regression: worker utilization is sometimes substantial, but the
render thread usually waits very little. The large cost is the serial producer
work performed before workers receive an immutable recording packet, followed
by mesh-heavy primary operation dispatch when the command topology changes.
Increasing worker count alone cannot remove either cost.

The latest progressive structural-preparation ledger reduced this observed
path's maximum from the prior 200+ ms range to 98.417 ms by spreading cold
renderer/descriptor preparation across rejected replacement frames while
continuing to present the last complete scene. It is not a completion result:
the p95 remains far above budget, and the user has not yet confirmed that the
interactive freeze/frame-rate regression is resolved in their normal editor
workflow.

After the new per-operation telemetry scopes were gated behind explicit detail
profiling, the same 60-sample path measured 20.952 ms p50, 74.275 ms p95, and
98.111 ms maximum whole-frame time. Primary encoding still reached 48.364 ms,
while worker active span and render-thread wait peaked at 8.203 and 8.241 ms.
The probes contributed some cost, but removing them did not remove the
regression; the measured primary/preparation work is real.

A following immutable-cohort reuse change removed a duplicated O(operation
count) refresh-plan build on primary-cache misses. The targeted CPU frame dump
changed from two `Vulkan.FrameDataManifest.BuildRefreshCohort` calls totaling
6.223 ms to one 2.642 ms call, and the profiled render-thread slice fell from
11.535 ms to 7.360 ms. A stationary frame in the new session was 3.469 ms.
The next full traversal was noisy (22.344 ms p50, 79.388 ms p95, 103.978 ms
maximum), so the exact local saving is proven but an end-to-end Sponza
improvement is not yet claimed from a single pass.

Twelve targeted CPU-frame captures during that traversal separated the
remaining cost into three different producers:

- One 32.935 ms frame spent 11.465 ms of render-thread self time in
  `Vulkan.PrepareFrameOps.MaterializeQueuedMeshes`, followed by 3.102 ms sealing
  the frame plan and 2.472 ms building the now-single refresh cohort. This is
  serial mutable mesh preparation performed before an immutable worker packet
  exists.
- One 20.241 ms frame spent 16.543 ms in
  `Vulkan.RecordPrimary.MainOpLoop`, including 13.878 ms of un-nested primary
  work. Its six scheduled-secondary runs totaled only about 2.56 ms. This cost
  therefore cannot be removed merely by adding more secondary workers; the
  primary is still assembling and validating too much changing topology.
- A separate 52.693 ms CPU dump was initially misread as showing 26.020 ms of
  `VisibleCollection` work. That scope actually covered the whole
  collect/swap loop and was mostly `WaitForRender`; an adjacent complete dump
  measured actual collection at about 2.8 ms. The root issue was orphaned
  profiler-child attribution, not a 26 ms visible-collection producer.

In the same clean session, recording-worker active span peaked at 0.943 ms and
render-thread worker wait at 1.280 ms. Aggregate worker CPU time is not a wall
clock stall and must not be interpreted as one. The first useful offload target
is mesh materialization after it is split into immutable structural preparation
and narrow current-frame publication. The primary-side remedy is coarser stable
cohorts plus indirect/count buffers, not moving the complete primary to a worker
and waiting for it elsewhere.

### Pre-Phase-5 hardening acceptance record

All comparable captures used the explicit camera traversal `(60, 15, 60)` to
`(-20, 5, -20)`, a ten-second Release `CleanProfile` interval, and no
screenshots in the measured interval. Percentiles below are p50/p95/p99/max in
milliseconds. This is a pre-Phase-5 hardening record, not evidence that the
Phase 5 architecture is complete.

| Capture | Samples and outcome | Whole | Collection | Vulkan | Record | Preparation | Primary |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Exact Phase-4 baseline `09cac87d4133e0fdcfb0838c3c329ffd52780036` | 1,058; zero deferrals/errors | 4.213/20.635/107.351/176.798 | .234/1.497/2.05/6.815 | 1.93/16.155/102.036/172.243 | 1.472/14.371/100.564/170.931 | .824/5.094/6.359/10.57 | .301/12.335/85.269/139.672 |
| Pre-hardening current | 794; 9 deferred, zero errors | 7.159/38.738/79.738/590.507 | .327/1.479/2.065/11.89 | 2.721/28.786/55.146/82.156 | 2.023/28.069/53.567/80.849 | 1.186/6.138/7.519/9.608 | .435/19.293/43.431/70.99 |
| Integrated hardened, before auto threshold | 1,397; 44 deferred, 1,353 complete, zero errors | 3.558/23.404/39.485/70.452 | .155/1.139/1.309/5.53 | 1.359/17.974/34.562/65.572 | 1.013/17.774/33.861/64.623 | .593/5.203/5.871/8.188 | .208/13.502/27.2/53.742 |
| Fresh final default, session `20260813-162936-renderloop-hardening-final`, 23:33:30.904Z–23:33:40.904Z | 1,209; 1,162 complete, 47 deferred, zero validation errors | 4.174/27.128/42.008/72.628 | .195/1.346/1.825/6.573 | 1.671/23.19/36.456/63.925 | 1.247/23.06/35.371/62.605 | .683/5.768/6.491/12.721 | .231/17.569/29.372/51.797 |

The pre-hardening 590.507 ms whole-frame outlier was 557 ms of render-thread
jobs outside Vulkan, not Vulkan recording. In the final default capture,
outside-Vulkan time was 1.849/4.86/9.22/21.369 ms. Worker active/wait was zero,
dynamic-text recording was nonzero on every frame, and distinct coherent final
auto captures confirm that the sampled endpoints were not stale repeats.

The hardening result has p50 parity with the exact Phase-4 baseline and greatly
improves p99/max, but p95 remains 6.493 ms worse. Strict no-regression and
perceived-smoothness acceptance therefore remain open.

The remaining primary tail is now localized more precisely. The worst completed
replacement frame in the final-default trace contained 831 mesh draws but 719
reused secondary command buffers. It issued those buffers in seven
`vkCmdExecuteCommands` batches. Primary command encoding took 29.582 ms and the
primary operation loop took 28.895 ms while persistent-worker activity was zero.
The cost is therefore render-thread classification, refresh/validation, and
assembly of hundreds of tiny reusable artifacts, not slow worker recording.

Outcome correlation also corrected an important interpretation error. The 47
`Deferred` samples are terminal Vulkan-frame outcomes, not output-budget
deferrals: acquire completed, CPU/GPU budget-deferral counters were zero, and
the command-record stage itself returned. Their p95 primary time was 34.227 ms
versus 8.661 ms for completed samples. Current capture data does not include the
terminal `EDesktopFrameReason` or primary-recording disposition, so it cannot
yet distinguish primary publication rejection from the later submission-source
validation recovery path.

#### Worker admission matrix

| Explicit worker override | Samples | Whole p50/p95/p99/max (ms) | Result |
| --- | ---: | ---: | --- |
| 0 | 1,930 | 2.681/16.797/30.823/50.816 | Best result; no workers. |
| 1 | — | 3.844/25.542/45.729/75.8 | Worse than serial. |
| 4 | — | 3.543/21.01/33.394/101.621 | Better than one, but not serial. |
| 8, repeat | — | 3.639/21.33/35.573/75.642 | No advantage over the bounded policy. |

The default therefore caps workers at four and dispatches graphics batches only
when they contain at least 32 eligible operations. Explicit overrides from zero
through eight remain available for diagnosis and hardware-specific comparison.

Current evidence points to cold command-chain artifact materialization in each
swapchain/frame slot plus expensive compatibility/resource validation. Several
small post-process chains repeatedly report `ResourcePlan` invalidation despite
an unchanged structural packet signature, and some image entries report
`MissingCommandBufferState`. Published uniform slot bases are stable per
renderer/family, so visibility order or uniform-base churn does not explain most
of the invalidation.

The automated viewport-sequence capture under
`Build/_AgentValidation/20260812-122100-vulkan-phase5/mcp-captures/ViewportSequence_20260812_235718_174_12a2b9137693458db1684c679fdaf948/`
is excluded from performance conclusions. Framebuffer readback added periodic
166-183 ms CPU work and the captured frames did not prove that the camera moved.

### 2026-08-13 post-Phase-5 framerate regression verification

A fresh matched comparison used the repository's `desktop-deferred-moving`
cohort with `deferred-large-scene.jsonc`: Release Vulkan dynamic rendering,
1920x1080, warm `GpuIndirectZeroReadback`, VSync off, a 200 Hz target, and the
deterministic moving camera. The baseline was built in a clean detached worktree
at the exact pre-Phase-5 source state
`75e042e986266f58ae0ca13cb799b6f15a0ea13c`; the current managed assemblies have
different hashes and include every Phase 5 edit. The matching apphost executable
hash is not a source-equivalence signal because that generated host stub is
unchanged.

Three 20-second profiled captures per build produced these medians:

| Metric | Baseline | Phase 5 | Delta |
| --- | ---: | ---: | ---: |
| Profiler samples/s | 133.905 | 117.631 | **-12.16%** |
| Whole-frame mean | 6.202 ms | 6.454 ms | +4.06% |
| Whole-frame p50 | 5.823 ms | 5.963 ms | +2.40% |
| Whole-frame p95 | 7.355 ms | 7.256 ms | -1.35% |
| Whole-frame p99 | 10.215 ms | 10.123 ms | -0.90% |
| Render wait for collection p50 | 1.137 ms | 2.024 ms | **+78.01%** |
| Render wait for collection p95 | 1.569 ms | 2.285 ms | **+45.63%** |
| Vulkan frame p50/p95 | 4.394/5.144 ms | 4.487/5.065 ms | +2.12%/-1.54% |
| Command record p50/p95 | 4.086/4.786 ms | 4.176/4.725 ms | +2.20%/-1.27% |

The ordinary p50/p95/p99 regression check would therefore pass while missing a
real cadence loss. An independent profiler-disabled wall-clock measurement used
a 30-second warmup and three consecutive 20-second `frame_id` intervals for each
binary. Baseline-first measured 159.757 versus 133.821 FPS median (-16.24%).
Reversing execution order measured 155.952 versus 130.784 FPS (-16.14%). Across
all six intervals the medians were 157.300 and 132.303 FPS (-15.89%). Both
binaries retained 62 renderables/active viewport commands and 60 GPU commands.

All six profiled runs had the same workload identity
`8881944379212414834`, 540 requested/consumed draws at p50, and zero VUIDs,
planner prunes, global in-flight waits, force flushes, submission rejections, or
unapproved output-policy events. Submit and present p95 were effectively flat.
The observed movement is instead the render thread waiting for fresh visibility
publication: final profiler-disabled samples were 0.722/0.962 ms for baseline
and 2.038/2.152 ms for Phase 5. Normal successful-submit publication remains at
the same source lifecycle point, so the result localizes the regression but does
not yet prove its causal Phase 5 change. A controlled output-manifest/admission
ablation is the next required diagnostic.

The canonical automatic stability gate was disabled because its growing-file
rescan extended the bounded run. This is decisive matched diagnostic evidence,
including a reversed-order control, but not promotion-grade gate evidence. The
full disposable report is
`Build/_AgentValidation/20260813-220550-phase5-framerate/reports/phase5-framerate-comparison.md`.

## Attempted Fixes

### Retained fixes

- Normalize scheduling-only `FrameOpContext` fields out of command-recording
  compatibility so per-frame output requests do not split otherwise reusable
  recording runs.
- Make graphics-pipeline prewarm resumable within a 2 ms admission slice and
  use nonblocking entry to the mesh pipeline preparation gate.
- Replace per-pipeline OS-thread creation with the persistent pipeline compiler
  and suppress routine sub-2 ms compile logging.
- Replace the order-sensitive mesh warm-preparation ledger with a bounded,
  pre-sized signature set keyed by stable pipeline/resource preparation state.
- Call `TryExecuteScheduledMeshCommandChainSecondaryRun` from the authoritative
  mesh payload path. This is the change that removed the worst inline-primary
  camera-motion stalls.
- Record and submit the current native dynamic-text overlay when presenting the
  last complete scene. A 196-frame recovery sample and the later 41-frame
  camera-motion sample both reported zero missing dynamic overlays.
- Keep queue-drain cohorts and materialization scratch storage preallocated and
  bound cold materialization work rather than allocating it on every frame.
- Bound progressive command-chain publication by both chain count and actual
  operation count. The earlier chain-only limit admitted large chains containing
  hundreds of draw preparations and therefore did not bound CPU work.
- Remove the duplicate frame-data prewarm performed again inside scheduled
  secondary preparation after the authoritative pre-record pass has already
  published and validated the exact draw slot.
- Prewarm conservative, invariant descriptor allocations during already-bounded
  cold materialization; do not perform this extra work for warmed materialized
  draws.
- Add a bounded structural-preparation ledger keyed by preparation/resource/
  arena/frame-slot/draw-slot identity. Successful structural work survives a
  rejected replacement frame, but every frame that is actually recorded still
  performs its complete current-frame dynamic-data refresh atomically.
- Split mesh preparation into retained structural work and mandatory narrow
  current-frame publication. Structural work may carry across rejected
  replacement attempts; current-frame data is never published partially.
- Unify secondary-artifact publication so an artifact is eligible only while a
  live command-buffer lifetime exists and its complete image journal matches
  the artifact's bind-state generation. Lifetime and bind-state generations
  are independent domains and are never compared numerically.
- Classify the new per-operation telemetry scopes as opt-in fine-grained probes
  so default aggregate profiling does not add a stopwatch/aggregation pair to
  every mesh operation.
- Retain profiler children until their parent completes, subject to a bounded
  cap, so delayed/overlapping scopes cannot be orphaned into a misleading
  parent or aggregate bucket.
- Reuse the immutable frame-data refresh cohort already published for the
  primary-reuse probe when fresh recording consumes the same sealed frame-plan
  generation, render-frame ID, and image slot. An intervening registration
  invalidates the cohort and retains the original rebuild path.
- Reject partial persistent-worker batches after the first timeout, quarantine
  artifacts while abandoned workers remain active, and guard primary recording
  before reuse, serial fallback, or artifact migration.
- Start the exact admitted worker count at startup and apply cost-aware graphics
  admission rather than paying worker setup/synchronization for small batches.

### Implemented Phase 5 items

- Win32 modal resize now freezes the active pipeline/planner/swapchain resource
  generation, suppresses interactive swapchain recreation, uses validated WSI
  presentation scaling where supported, and performs the catch-up after the
  modal loop exits.
- Eligible independent non-graphics secondary packets use persistent workers
  with worker-owned command pools/arenas. Small or ineligible batches retain the
  serial recording path.

### Ruled out or incomplete attempts

- Native pipeline compile duration is too small to explain the largest stalls.
- A stationary warmed-view microbenchmark is insufficient; it hides cold
  per-slot command-chain publication.
- The current detailed frame-data/reuse diagnostics identify broad
  `ResourcePlan` invalidation but do not yet expose the exact resource identity
  responsible for every false invalidation.
- A simple non-resumable two-millisecond cap on primary frame-data prewarm was
  reverted immediately because it could reject every replacement attempt and
  replay a stale scene forever. Any deadline mechanism must retain completed
  structural work across attempts and must never submit partially refreshed
  current-frame data.
- Moving vertex-input construction earlier did not improve the measured cold
  path and was reverted.
- Enforcing `MinMeshDrawsPerRenderPacket` by retaining every sub-ten-draw mesh
  island inline was implemented with explicit fresh-primary and binding-owner
  accounting, built successfully, and then reverted after live A/B validation.
  It reduced the active Sponza schedule to two secondaries, but the 657-sample
  camera interval reported primary p95 51.933 ms, record p95 55.558 ms, and 82
  deferred frames. That capture accidentally retained ImGui and is unsuitable
  for whole-frame promotion, but the directly measured primary regression is
  sufficient to reject wholesale inline fallback. The reusable granularity
  must be improved without encoding hundreds of draws into every fresh primary.
- The local agent broker is outside the renderer runtime path, but its default
  Sol budget was independently defective. Route-aware defaults now provide
  16,384 output tokens/300 seconds for ordinary Sol and 32,768/600 seconds for
  Sol xhigh/max. A fresh Codex task or app restart is required to load the new
  broker process; an explicit 32,768-token/600-second Sol run completed where
  the old 4,096-token default failed.

## Command Recording Architecture Decision

Do not move the complete primary command buffer to another thread. The render
thread currently needs its result immediately, so that merely relocates the
same critical-path wait while complicating layout, lifetime, and submission
authority. Keep barriers, dynamic-rendering scopes, primary assembly, final
layout publication, and queue submission render-owned.

The intended improvement is a producer/consumer architecture:

1. Collection publishes immutable visibility, material, transform, and output
   snapshots; it does not mutate `VkMeshRenderer` recording state.
2. Planning workers partition visible work into stable camera-independent
   pipeline/material/pass cohorts and prepare immutable frame execution packets.
3. Command workers own their Vulkan command pools and publish fully ended,
   immutable per-frame-slot secondary artifacts through a bounded
   `Free -> Writing -> Ready -> InUse -> Retiring` ring.
4. The render thread patches frame-dynamic buffers, resolves required image
   layouts, executes ready cohort secondaries from a small stable primary, and
   submits. A missing optional artifact yields/reuses by explicit deadline
   policy; it never causes an unbounded worker wait.
5. Visibility/LOD/count changes should primarily update indirect/count/draw-data
   buffers rather than rebuilding command topology. The existing Vulkan
   `CmdDrawIndexedIndirectCount` path is the bridge: first feed it from current
   CPU culling, then optionally move culling/compaction to the GPU. The current
   unit-testing configuration explicitly has `GPURenderDispatch` disabled, so
   enabling that existing path blindly is not a valid regression fix.

Before `TryPrewarmFrameDataForRecording` can safely run on workers, it must be
split. It currently mixes immutable resource preparation with current-frame
uniform callbacks, active-program mutation, descriptor allocation/publication,
frame-source descriptor refresh, and vertex-input state. Blind parallelization
would serialize on `_recordDrawSync` or introduce renderer/descriptor races.
The next implementation boundary is therefore an immutable structural packet
plus a narrow frame-dynamic publication step, not another layer of general
thread-pool dispatch.

## Current Wrap-Up Boundary

The retained code is the final-default hardening state measured above; the
sub-threshold-inline experiment is not retained. The coherent completed work is:

- exact Phase-4/current Sponza baselines and corrected profiler attribution;
- structural versus current-frame mesh preparation, with current-frame refresh
  remaining atomic on every submitted replacement;
- unified secondary-artifact lifetime/journal publication and timeout
  quarantine, including rejection of partial worker batches;
- bounded startup worker capacity and cost-aware admission; the ordinary
  Sponza path remains serial because 1/4/8-worker A/B runs all lost to serial;
- profiler descendant retention so overlapping child scopes are not published
  as false roots; and
- current dynamic-text overlay recording during last-complete-scene recovery.

Four of the six pre-Phase-5 hardening gates are complete. Camera-motion p95 and
normal-UI final-present overlay continuity remain open. Phase 5 stays paused at
this boundary; only modal generation freeze and persistent safe-packet workers
are checked in the active Phase 5 checklist and copied to completed history.

## Phase 5 Wrap-Up State

| Criterion | State at wrap-up |
| --- | --- |
| One deadline-aware executable DAG for every output and publication | Implemented: each sealed manifest is reset independently, consumes the host admission decision, prunes non-executable branches, orders retained operations, and gates recording/submission. |
| Acquired OpenXR eye critical path and nonblocking secondary work | Implemented: actual image acquisition reserves the XR prerequisite closure; busy XR frame-data slots defer without waiting, retirement is capped, and desktop/ImGui acquisition is nonblocking during XR-owned frames. |
| Bounded cadence, deferral, budgets, and stale reuse | Implemented: inferred outputs are registered with the host ledger, optional defaults carry nonzero cadence/CPU/GPU budgets, content-age reuse is bounded, and deadline risk is counted. |
| Narrow queue lock and timeline/frame-slot ownership | Implemented: timeline values are reserved inside serialized submission admission; OpenXR and sidecar ownership use timeline/frame-slot completion; ImGui destruction uses queue-ordered marker fences with waits outside the queue lease. |
| Frozen modal-resize generations with WSI scaling and one catch-up | Complete; copied to the completed-work sibling. Live long-duration Win32 soak remains a validation task. |
| Bounded, nonblocking modal dispatch with typed terminal result | Implemented: the callback no longer enters render dispatch and accepts stale reuse only from the current window's valid, WSI-scalable Vulkan presentation package; unavailable, incompatible, busy, and surface-loss states defer explicitly. |
| Persistent safe-packet workers with serial fallback | Complete; copied to the completed-work sibling. Deterministic timeout fault injection remains a validation task. |

### 2026-08-13 Phase 5 implementation closeout

- Vulkan output lowering now carries the canonical host request and admission
  decision into a fresh sealed manifest. The executable prerequisite closure is
  the source of operation retention/order and desktop submit admission.
- OpenXR requests carry the runtime predicted-display deadline. Actual acquired
  images reserve the critical path, pending frame-data slots produce a bounded
  defer, and exceptional accepted submissions retire against the graphics
  timeline rather than a synchronous fence owner.
- Graphics timeline values are allocated while holding the same serialized
  submission admission that fixes native dispatch order, preventing a later
  producer from signaling a larger value before an earlier reservation.
- Detached ImGui lifecycle work is frame-scoped: create/destroy and rendering
  defer during XR/resize critical frames. Destruction waits on queue-ordered
  marker fences after the native queue lease is released; no `QueueWaitIdle`
  remains in that path.
- The modal Win32 callback consumes a per-window Vulkan presentation package
  and cannot enter ordinary render dispatch or mutate visibility publication.

## Pre-Phase-5 Hardening Gates

| Gate | State | Evidence / remaining condition |
| --- | --- | --- |
| Exact Phase-4 baseline | Complete | Exact baseline commit and matched ten-second traversal are recorded above. |
| Timing attribution | Complete | The apparent 26.020 ms visible-collection result was corrected to orphaned profiler attribution; adjacent collection was about 2.8 ms. |
| Worker policy | Complete | Default cap is four, graphics admission requires at least 32 eligible operations, and explicit 0-8 overrides were compared. |
| Artifact lifetime safety | Complete | Secondary publication requires a live command-buffer lifetime plus a complete image journal matching the artifact bind-state generation; timeout cancellation quarantines unsafe artifacts. |
| Camera-motion live gate | Partial | p50 parity and p99/max improvement are established, but p95 remains 6.493 ms worse than baseline. |
| Native overlay gate | Partial | Telemetry confirms dynamic text records every sampled frame and coherent captures are distinct; normal final-present visual continuity still requires user/normal-UI validation. |

## Validation Evidence

- Final Phase 5 implementation builds passed with zero warnings and errors:
  `XREngine.Runtime.Rendering.Vulkan`, `XREngine.Runtime.Rendering.OpenGL`, and
  `XREngine.Editor` (`--no-restore --disable-build-servers`).
- Final live validation used isolated session `phase5-output-dag-final` at
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260813-214643-phase5-output-dag-final/`.
  Vulkan readback completed from two camera states (queue slots 0 and 1); the
  moved-camera capture showed a coherent Sponza view. The final logs contained
  no device-loss, VUID, validation-error, exception, or output-DAG admission
  failure. The first startup capture retained the known transient colored
  attachment visualization and was not treated as the visual acceptance frame.
- `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --disable-build-servers`
  passed with zero warnings and zero errors during implementation.
- The latest isolated session was `vulkan-phase5-request-scope`; logs are under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260812-155638-vulkan-phase5-request-scope/logs/`.
- The newest isolated measurement session is `vulkan-structural-ledger`; logs
  and artifacts are under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260813-145328-vulkan-structural-ledger/`.
- Post-instrumentation and single-cohort runs are under the named sessions
  `vulkan-offload-gated` (`20260813-150414`) and `vulkan-cohort-once`
  (`20260813-151333`). The latter contains the CPU dump proving that the refresh
  cohort is built once.
- Two fresh readbacks from opposite ends of the measured camera path are under
  that session's `artifacts/mcp-captures/` directory. They are retained only as
  disposable evidence that the requested camera positions publish distinct
  scene views.
- The final default fresh run is
  `20260813-162936-renderloop-hardening-final`; its exact measured interval was
  `2026-08-13T23:33:30.904Z` through `2026-08-13T23:33:40.904Z`.
- A coherent close-wall readback is
  `Build/_AgentValidation/20260812-122100-vulkan-phase5/mcp-captures/Screenshot_20260812_165816_121_905d99af43ee4e40a6e66e82bc98c498.png`.
- The final default interval had zero validation errors, 47 bounded deferrals,
  and nonzero dynamic-text recording on every frame; its whole-frame maximum
  was 72.628 ms rather than the earlier 200+ ms CPU-stage spikes.
- The rejected singleton-inline experiment is preserved only as disposable
  evidence under named session `20260813-165204-renderloop-packet-threshold`.
  Its Vulkan build passed with zero warnings/errors and the session stopped
  cleanly; because ImGui remained enabled, it is not a clean whole-frame
  comparison. Its direct primary/recording measurements are recorded above.
- RenderDoc tooling passed `rdc doctor`. A GPU capture was not required to
  localize the observed freeze because queue/GPU-facing time stayed small while
  CPU-stage telemetry isolated preparation and recording.

## Next Steps

1. Use the existing `MeshSecondaryFallbackEndIndex` (currently reset but not
   consumed) to suppress repeated `CountContiguousMeshCommandChainRun` and
   secondary-preflight retries after the first failed attempt for one contiguous
   island. Verify render-scope re-entry before the inline fallback draw. This is
   the smallest next fix for the suspected O(N-squared) primary loop.
2. Add allocation-free numeric capture fields for terminal
   `EDesktopFrameReason`, primary-recording disposition, and the exact deferred
   recovery site. Re-run the same traversal and group primary p95 by terminal
   reason before changing deadline policy.
3. Add fixed-bin packet-size telemetry (1, 2-4, 5-9, 10-16, 17+) and use it to
   design coarser reusable cohorts. Do not repeat the rejected all-inline policy;
   evaluate safely mixing programs/descriptors within bounded exact-identity
   capacity, then move visibility/LOD/count churn toward the existing indirect-
   count topology so it updates buffers instead of command topology.
4. Extend command-chain invalidation diagnostics with the exact changed
   resource identity/signature, then eliminate false/broad `ResourcePlan`
   invalidation on stable post-process/cohort artifacts.
5. Validate normal-UI final-present native-overlay continuity with the user;
   sampled recovery telemetry alone is not the final visual acceptance gate.
6. Run the Win32 drag-duration and guard-liveness soak against the completed
   per-window modal presentation-package path.
7. Exercise OpenXR on hardware and verify acquired-eye deadline telemetry,
   pending-slot deferral, mirror/ImGui cadence, and runtime image release.
8. Re-run a deterministic Sponza traversal after each p95/topology change:
   capture
   at least ten warmed samples plus cold-view transitions, verify visual camera
   movement, native-overlay continuity, profiler stages, and Vulkan logs.

## Validation Boundary

No tests were added or modified while the integration and regression remain
under live validation, per repository policy. After the live Sponza path is
stable and the user clears test work, add focused output-DAG, modal-resize,
worker-timeout, and OpenXR scheduling coverage and run the companion hardware
and stress matrix.

## 2026-08-30 Phase 5.0/5.1 Closeout

Phase 5.0 and 5.1 are now complete. This closeout does not enter Phase 5.2.

| Gate | Result |
| --- | --- |
| Warm desktop performance | `desktop-steady-fixed/summary.json`: 1,232 samples; render p50/p95/p99 4.294/5.942/8.065 ms and Vulkan p50/p95 3.650/4.905 ms. After the four-second stabilization boundary: zero policy violation, rejection, in-flight wait, forced flush, or VUID. |
| Interactive resize | Isolated session `20260830-112835-phase51-resize-release-0830`: the editor remained MCP-responsive throughout a modal Win32 resize, recreated the swapchain, resumed live FPS output, and logged zero `DesktopFrameFailure`, `VulkanPlanPreconditionException`, lease exhaustion, VUID, synchronization hazard, or frame rejection. |
| Allocation policy non-activation | Baseline and Analyze remained inactive. The earlier ProofGated cohort had zero candidates and reached imported-model compute dispatches with zero VUID after the common push-constant mask fix. Final review tightened this further: every mode now keeps dedicated device-local images, and ProofGated reports `block='native dependency/initialization and positive-path validation pending'`. This validates safe non-activation, not a positive alias/lazy A/B or performance gain. |
| Multi-output OpenXR | `openxr-phase51-final/reports/openxr-smoke-summary.json`: 240 total frames (163 submitted, 77 no-layer), 80 warmup and 160 retained frames, true strict SPS for all 163 submissions, zero sequential fallback/end-frame failure, mirror composed, six of six scripted desktop resizes, zero global in-flight wait/forced flush/final pending retirement, and empty failure/warning arrays with Vulkan and synchronization validation enabled. The 37 counted deadline misses were observable under synchronization validation and caused no output or submission failure. |

The implementation closes three render-graph issues:

- `VulkanRenderGraphCompiler` keeps an O(1) clean-revision hit and immutable
  connected-subgraph cache. A dirty component rebuild reuses untouched pass
  objects and restores global order; mutation during compilation is rejected.
- `VulkanBarrierPlan` coalesces only matching stage/access/layout/queue scopes
  with adjacent layers or mips, and the recorder emits one Synchronization2
  dependency batch per pass. A missing frozen graph pass fails closed instead
  of substituting a broad `AllCommands` transition.
- `XRE_VULKAN_TRANSIENT_ATTACHMENT_MODE` defaults to Baseline and exposes
  evidence-only Analyze/ProofGated modes. Candidate analysis identifies
  graphics-queue, non-imported, non-overlapping declared intervals without
  mislabeling them native lifetime proof. All image sharing and lazy activation
  remain disabled pending the native dependency/initialization contract and
  positive-path validation, including outside OpenXR/VR.

Live validation exposed and fixed two additional lifetime/ABI regressions:

- modal swapchain recreation delayed lowering until the shared advanced
  extractor had advanced. Advanced visibility columns are now captured under
  the preparation-service lock into a bounded, reference-counted authoring
  lease; lowering reads only the immutable lease. Resize abandonment, OpenXR
  prewarm/mirror/paired-eye early returns, plan lowering, and queue disposal all
  release that ownership explicitly.
- compute dispatch used the legacy common push-constant stage mask even when
  the pipeline layout included Task/Mesh stages. Direct and indirect compute
  recorders now use the same device-aware full mask as layout creation,
eliminating `VUID-vkCmdPushConstants-offset-01796` in the imported-model run.

Final read-only GPU review also prevented an unsafe opt-in path from shipping.
The original candidate gate accepted disjoint graphics storage-image writes,
but equal physical access states can suppress a native barrier; pass order does
not prove a WAW handoff. Lazy eligibility also needed to reject
`RequiresStorageUsage`, rather than stripping `StorageBit` later. The retained
implementation fixes that eligibility check, preserves all native usage bits,
removes candidate-driven allocation activation, and reports the missing proof
explicitly. Future positive A/B work needs native handoff dependencies even for
equal layouts, complete physical-use lifetime coverage, and initialization
authority. The present conditional fail-closed gate is complete; those future
optimizations are not claimed as implemented or validated.

The final isolated Debug run `20260830-121139-phase51-native-proof-gate-0830`
confirmed the explicit ProofGated block with zero active alias/lazy groups.
Observed frame IDs advanced from 2,744 to 5,097 with 25 resident draws and no
publication rejection. Logs contained zero VUID, synchronization hazard,
desktop-frame failure, plan precondition exception, or lease exhaustion. One
startup texture-readiness retry remained at one after import settled. The MCP
viewport screenshot request could not resolve a transfer-readable color image,
so it was not treated as visual acceptance and no fallback capture path was
silently substituted. The earlier live modal-resize/FPS observation remains the
visual continuity evidence; full shaded-output parity is still a Phase 8 gate.

No tests were added or modified. The existing targeted unit-test invocation is
currently blocked at compile time by pre-existing stale calls to the removed
`AdvancedRenderPipeline(visibilityFamilyReservation: ...)` overload and the old
`CaptureAdvancedResourceProfile` signature in three test files. Runtime Vulkan,
Release editor, strict SPS, resize, and synchronization-validation paths all
built and completed successfully; the stale test-source migration remains
separate work requiring explicit test clearance.

Final read-back: `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release
--no-restore --disable-build-servers` passed with zero warnings and zero errors;
`git diff --check` passed. Every isolated editor started for this validation was
stopped. The final smoke editor's auto-started Monado child outlived the wrapper
marker and was stopped only after verifying its exact PID, parent smoke-editor
PID, executable path, and UTC creation time.
