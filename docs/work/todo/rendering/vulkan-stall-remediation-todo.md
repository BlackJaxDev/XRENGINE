# Vulkan Stall Remediation TODO

Last Updated: 2026-10-04
Owner: Rendering, with Profiler, Runtime Core, serialization and ImGui Editor owners per item
Status: active on October 4; mono OpenGL shader reload, shadow-depth attribution,
harness admission, explicit shadow fixture settings and guarded disk-root source
refresh have scoped validation on Vulkan and OpenGL. Vulkan reload now preserves complete-family
admission and recovers fresh submitted output after in-memory edits. Matched performance and
integrated acceptance remain open. Physical Vulkan/OpenXR strict stereo now starts
and delivers both eyes. Desktop ImGui ownership now passes live menu interaction
and programmatic resize; Sponza flicker/black frames, stale-frame jitter, frame
pacing and XR lifecycle recovery remain failed or unverified acceptance gates. Completed
items are documented as design in
[Frame Loop Design](../../../architecture/rendering/frame-loop-design.md),
[Vulkan Pipeline Compilation](../../../architecture/rendering/vulkan-pipeline-compilation.md)
and [Play Mode Architecture](../../../architecture/editor/play-mode-architecture.md);
their measurements and gate records stay in the investigation records listed in
the [evidence index](#evidence-index).

## Target And Current State

The user target is above 100 Hz while the camera moves on Vulkan with the
Advanced pipeline and CpuDirect submission, rendering Sponza with one
directional light, without removing features. Earlier camera-route measurements
recorded **123.5-128.2 fresh FPS in motion**. The October 4 viewed interior route
measures **92.09-92.46 FPS with the lane versus 33.65-33.74 FPS generically**;
the 100 FPS target remains unmet on that route, and acceptance remains open:

- The generic path runs at about 159 fresh stationary frames per second and
  about 40 during camera motion. Ordinary motion frames take about 7 ms;
  about 45% of motion frames refresh the directional shadow cascades, and each
  refresh records about 400 casters through the per-renderer draw path at about
  100 us each (about 51 ms per refresh frame).
- The lane removes the long refresh-frame tail in the recorded fixture.
  Cold-start readiness, shadow-write retries, floor-view parity, material
  eligibility, multiple-page admission, and three play round trips have live
  evidence. Matched idle-host performance acceptance remains before
  [S15b](#s15b-record-directional-cascade-casters-on-the-advanced-canonical-lane)
  can be validated.

The original performance/ghosting acceptance remains open. The user confirmed
that no original recording is available and waived replay of it. Retained
TAA/TSR corrections have live and GPU-binding evidence for the documented
scenarios; distinct-view/lifecycle correlation and user confirmation remain.

## Current Completion And Remaining Work

Work resumed on October 4. Checked boxes below mean the stated scope has
evidence; they do not imply the entire remediation is complete.

Completed and retained in this continuation:

- Vulkan directional-shadow correctness/admission fixes: generic fragment-stage
  parity, optional lane readiness, failed-receipt recovery, masked-material
  eligibility, single-page reservation, and three play round trips.
- Immutable temporal resolve snapshots and history invalidation for gaps, with
  retained TAA/TSR GPU jitter/matrix checks and viewed motion/cut/resize captures.
- Distinct-camera TSR switching and restoration now have viewed settled output,
  matching settings and camera-scoped reset/seed evidence. Exact first-frame
  temporal-key/uniform attribution remains open.
- OpenGL upload budgeting and exact two-slot ownership: all 76 fixture textures
  finish by the 8.906-second sample versus 26.6-28.7 seconds in control runs.
- OpenGL directional cascade target-mask publication: all four atlas tiles now
  receive casters. Dark-shadow attribution now matches raw depth and authored
  resolution across backends; display tonemapping remains unverified.
- OpenGL GPU query readback ordering: timings now resolve; the settled same-build
  profiler off/on/off median is 36.89/37.05/37.09 ms, with no multi-second gap in
  the measured windows.
- Completed boxes reconciled with evidence; original-recording replay explicitly
  waived rather than reported as a successful reproduction.
- OpenGL specialized source invalidation/replacement; harness admission rejects
  sky-only views on both backends; requested directional shadow dimensions are
  set, read back and reported before warmup.
- Shared disk-backed shader roots refresh off-thread with guarded publication.
  Live Vulkan root/include edits render and restore, and unsaved text survives
  disk notification and manual reload.
- Vulkan complete-family admission during asynchronous shader compilation:
  root/include/in-memory edits and restoration recover fresh completed frames,
  with viewed output from two camera positions and no terminal rejection.
- OpenGL specialized variants share current authored text: unsaved root edits
  now render, survive disk notification/manual reload and restore, with all
  root/include/in-memory output variants viewed from two camera positions.
- Vulkan harness readiness now also requires completed-frame progress and
  rejects retained terminal faults. Both backends admit stable geometry and
  reject sky-only views in the live repeat.
- MCP pass diagnostics now capture owned summary rows under the collection's
  read scope, preventing buffer swaps from recycling lists during enumeration.
  Both backends pass 120 live polls and readiness; 13 existing collection tests pass.
- The pinned OpenGL control (`9fee4b983`) is reconstructed from unchanged tracked
  source and matching submodule revisions; its isolated Release build passes
  with zero warnings/errors. Its live admission fails; comparison remains blocked.
- OpenGL sparse transitions now respect bindless leases and exact native upload
  ownership. Cold/warm promotion and demotion drain, source revisions advance
  together, and immutable-parameter errors disappear. HDR readback succeeds;
  final display metering now uses the established mean-relative log floor.
  Cold/warm exposure follows the captured scene samples instead of saturating
  at 100, with viewed material detail and unchanged exposure/bloom settings.
- OpenGL bounded metering samples span the whole selected mip. All three
  non-average modes match captured-pixel predictions in two live views within
  0.001%, with unchanged fetch budget and restored settings.
- Vulkan directional-shadow GPU timing now covers the group's barriers and
  clear/raster commands through the existing dense-only query mechanism.
  Live refresh frames add exactly two queries, disabled frames add none, output
  remains visually stable, and 21 existing query tests pass. This measures a
  partial GPU interval; it does not meet the outstanding motion FPS target.
- Vulkan lane-context ownership now survives abandoned recordings and native
  handle reuse without importing primary scene dependencies into texture uploads.
  Repeated resize/camera cuts and fresh capture windows pass without a terminal
  failure. Raw cascade depth matches OpenGL after resize.
- Sparse sampler limits now use the exposed resident chain, and native storage
  retirement rebases retained mips in one canonical publication. Fresh OpenGL
  and Vulkan processes pass the dark-view HDR gate at both window sizes:
  0.479%/0.478% mean difference, down from about 12.3%. Atrium differences are
  0.098%/0.035%; all eight captures were viewed. Final display tonemapping and
  performance acceptance remain separate.
- Play entry with OpenXR active no longer kills the engine loop. World
  pre-collect publication now follows the host's render session, and OpenXR
  view-batch planning no longer shares descriptor storage across threads. Four
  OpenXR and three desktop-only round trips pass loop liveness and Edit
  restoration; XR layer submission after Play still stalls on pipeline admission.

Remaining work, in suggested resumption order:

The active hardware regression takes priority over the comparison backlog below.
Use the [OpenXR stereo worklist](#openxr-stereo-and-desktop-regression-worklist)
for the next bounded slice; retain the authored Advanced/CpuDirect Sponza scene
and strict SinglePassStereo contract during isolation.

1. Resolve the pinned OpenGL control's failed admission before a matched
   throughput claim. Rebuilt `9fee4b983` collects the scene but passes 0/100
   interior samples across cold/settled/warm checks and retains stale output
   after a camera cut; canonical texture-source rejection is recorded. The
   current build admits under the same observer settings.
2. Rerun valid matched comparisons with the corrected harness and explicit
   shadow settings. Sparse ownership and runaway log-average exposure have
   scoped cold/warm validation; full-mip sample coverage passes its live gate.
   The Vulkan resize descriptor fault is corrected and raw cascade depth matches
   after resize; the dark-view HDR gate now passes below 0.5% mean difference;
   existing scoped checks do not certify the full comparison matrix.
3. Attribute the remaining interior-motion GPU cost, then run final idle-host
   Vulkan stationary/motion A/B on the retained lane, and
   exact camera-switch and post-admission/lifecycle temporal binding checks.
   Resolve fine-edge TSR concerns and obtain user performance/ghosting feedback.
4. Complete the allocation/retention, sealed snapshot ownership, scheduling/GC
   jitter attribution, profiler metadata, and baseline/prior-increment work,
   plus the linked Advanced GPU attribution checklist.
5. Exercise the missing multi-LOD/streaming/eviction/relocation/retry/reuse
   fixture and the remaining integrated mutation, multiview, teardown, picking,
   toolbar/camera-settings and physical XR matrix. The isolated SteamVR OpenXR
   startup blocker is resolved after the user restarted SteamVR without
   elevation and two engine startup checks were corrected. October 4 live
   Vulkan SinglePassStereo produced fresh left/right captures in two windows,
   182 submitted frames, zero sequential fallback attempts, and one clean
   runtime teardown. Hardware performance, head-motion/orientation feedback,
   repeated lifecycle/failure coverage and OpenGL/OpenVR remain open; this run
   also recorded 254 frames without layers. See the
   [hardware investigation](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md).
6. Obtain explicit clearance before adding/modifying tests. Existing selected
   tests ran: 86/97 passed with 11 source-contract assertion failures; a narrower
   final upload selection passed 31/32 with the previously classified mapped-
   bounds assertion failure. No tests were changed. Conditional exit-failure
   and bounded planner-state follow-ups retain their reopening conditions.

Validation/cleanup: the latest isolated Release build (Play-transition
publication gate and call-local OpenXR view planning) succeeded with zero
warnings/errors. Temporary
upload and shader probes are removed; the authored shaders were restored
byte-for-byte. User-owned SteamVR was left running. The work is uncommitted.
Detailed measurements and rejected hypotheses are in the linked investigations;
ignored evidence for this continuation is under
`Build/_AgentValidation/20261003-142654-vk-todo/`.

## Working Rules

- **One implementation item at a time.** Do not start the next fix until the
  current one passes its focused build, live behaviour, performance and
  regression checks with evidence recorded. Split items with independent
  changes into children. Read-only evidence gathering may run in parallel;
  edits, runtime mutations and A/B measurements stay serialized.
- **Predeclare acceptance.** Before editing, record the owning path, one
  falsifiable hypothesis, the check that could reject it, correctness
  invariants, the metric, its budget and tolerance (from measured variability),
  sample count, window and observer overhead.
- **Validate live.** Build the owning project without new warnings, run the
  original trigger in a named isolated editor session, view saved images when
  rendering is involved, and compare matched baseline and candidate binaries.
  Confirm the removed cost did not move into another stage. A successful build
  or quiet log does not close a runtime item.
- **On failure, stop.** Repair the same item or record the falsified hypothesis.
  A flaky result is not a pass. Disproved entry conditions are Deferred/Not
  Applicable with a reopening condition, never Fixed.
- **Statuses:** Pending, Active, Blocked, Validated, Closed, Deferred/Not
  Applicable. Validated means the live gates passed; Closed also requires
  follow-ups and any cleared test work.
- **Tests:** do not add or modify regression tests until live validation passes
  and the user explicitly clears test work. Existing tests may support
  diagnosis but never replace live validation.
- **Measurement:** count fresh frames from
  `frame_lifecycle.outcome_counts.completed`, not render-frame numbers (rejected
  frames replay the previous scene). Attribute leaf costs with engine stage
  timers (`XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`) and `dump_cpu_frame_profile`;
  EventPipe sample traces are biased toward GC safe points. See
  [measuring the frame loop](../../../architecture/rendering/frame-loop-design.md#measuring-the-frame-loop).
- **Scratch and sessions:** evidence goes under a reserved run in
  `Build/_AgentValidation/`; copy conclusions into the investigation record.
  Use named isolated sessions and stop only sessions you started.

Each item's gate record (hypothesis, baseline identity, predeclared budget,
changed binary, live scenarios, before/after distributions, correctness,
retention, pass/fail, test clearance, cleanup) lives in its investigation record.

## Completed Work

These checked milestones passed for the scopes in their linked records in the
[evidence index](#evidence-index). They do not close the original report or the
remaining temporal and integrated acceptance gates.

- [x] S00-S07: baseline and profiler attribution, nonblocking readiness,
  compilation invalidation, bounded cache/native creation, initial resource
  materialization, and mesh data/publication gates.
- [x] S08-S11: index preparation, fullscreen helper geometry, toolbar icon
  preparation, and camera-inspector discovery for their recorded scopes.
- [x] S12: reachable shared-preparation paths and the explicit distinct-world
  gate disposition; retained limits and reopening conditions remain below.
- [x] S13a-S13e: publication/collect-wait attribution, identity feedback,
  registration retention, auxiliary-state updates, and family preparation for
  reachable scope. Unavailable fixture and hardware coverage remain open.
- [x] S14a-S14d and S14f: play transitions, probe-spawner restore, component tick
  timing, post-exit publication, and snapshot identity. The intermittent exit
  exception remains open.
- [x] S14h: restore CPU vertex arrays for cooked meshes; validate three play
  round trips and matching scene views.
- [x] S14g: retire superseded planner generations; validate post-play motion
  performance and device-local memory stability. The short-lived-output
  planner-state follow-up remains below.
- [x] S15a: bound packet-lowering scans during shadow updates; validate motion
  performance, identical images, bounded descriptor growth, and affected TSR
  checks.

## Ledger

| ID | Item | Status | Next step |
| --- | --- | --- | --- |
| S15b | [Directional cascade casters on the Advanced canonical lane](#s15b-record-directional-cascade-casters-on-the-advanced-canonical-lane) | Active | Correctness and retained lifecycle checks recorded; matched idle-host stationary/motion performance remains |
| S15 | [Temporal correctness and the original report](#s15-preserve-temporal-correctness-and-resolve-the-original-report) | Active | Lit disocclusion, resize and view switches; user confirmation |
| S15a | [Shadow-update recording during camera motion](#s15a-bound-shadow-update-recording-during-camera-motion) | Validated | None; 100 Hz continues in S15b |
| S14g | [Steady state after play round trips](#s14g-close-the-steady-state-gap-after-play-round-trips) | Validated | One small follow-up (shadow-viewport planner states) |
| S14e | [Intermittent play-exit exception](#s14e-capture-the-intermittent-play-exit-exception) | Waiting for recurrence | Fix the cause `playmode-transitions.log` names |
| S16a | [Black OpenGL scene on the measurement host](#s16a-black-opengl-scene-on-the-measurement-host) | Active | Upload/query, sparse ownership, log-average exposure and bounded sample coverage validated; valid performance control remains |
| S13i | [Residual allocation, jitter and cumulative comparison](#s13i-prove-the-cumulative-fix-on-the-reported-workload) | Open | One residual owner at a time |
| S12 | [Distinct runtime owners for shared Advanced preparation](#s12-improve-shared-advanced-preparation-safely) | Reachable paths validated; distinct-owner gate Not Applicable | Reopen under the recorded conditions; retained limitations remain |
| S13c | [Registration cases the fixture cannot reach](#s13c-retain-logical-meshlod-registration-by-real-mutation-identity) | Validated for reachable scope | Needs a multi-LOD/streaming fixture |
| S13f, S13g, S13h, S14 | [Deferred items](#deferred-items) | Deferred | Reopen only on their recorded conditions |
| S16 | [Integrated acceptance and closeout](#s16-integrated-acceptance-and-closeout) | Pending | After the items above |
| - | [Follow-ups found during this work](#follow-ups-found-during-this-work) | Open | Each needs its own owner |

### S15b. Record Directional Cascade Casters On The Advanced Canonical Lane

Owner: Rendering (Advanced pipeline with shadows). See the
[shadow-recording record](../../investigations/rendering/2026-10-03-motion-fps-shadow-recording.md#why-100-hz-is-not-reached).
The [implementation and resumed validation record](../../investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md)
supersedes the proposed separate-family design: the implementation reuses
the desktop family's sealed bins with a distinct depth-only atlas target closure.
Moment encodings and unsupported consumers remain explicitly on the generic path.
The floor-view parity failure was traced to the generic reference dropping
fragment shaders from depth-only pipelines. Preserving authored fragment work
passes the floor and atrium image thresholds; broader material eligibility and
lane acceptance remain open.
The implementation filters canonical casters by CastShadow and per-cascade
bounds, uses per-tile clears and viewports after deformation, and preserves
atlas scheduling and submission receipts. Completed corrections and remaining
acceptance work:

- [x] Stabilize required-family identity across optional shadow-program
  readiness; validate live startup and shader-reload recovery. Transient
  rejected frames remain; this does not close lane acceptance.
- [x] Preserve atlas dirty keys when lane preparation fails; verify failed
  receipts and generic retry/recovery with a controlled failure.
- [x] Validate scene movement, deactivation/reactivation, and the explicit
  generic-path override on the corrected Release binary (October 3).
- [x] Resolve the floor-view masked-shadow parity failure with identical
  cascade matrices and raw atlas coverage/sampler evidence. The final matched
  floor comparison is 0.044/255 mean RGB difference and 0.064% pixels above
  channel difference 16, within the 0.5/255 and 0.1% limits.
- [x] Restore authored fragment coverage in generic depth-only pipelines;
  validate fragment shaders, sampled textures, atlas output, and live reload
  recovery on the corrected reference.
- [x] Enforce material eligibility consistently; nonstandard masked materials
  must not silently write opaque depth in the shadow shader.
- [x] Decline unsupported additional atlas pages before enqueue or retain
  pipeline closures per group; validate distinct-page directional groups
  without recurring rejected frames.
- [x] Complete three play-mode entry/exit round trips on the retained
  implementation without transition errors. Memory plateaus after first-use
  retention; the linked lane record contains before/after motion measurements.
- [x] Repeat retained TAA/TSR motion, resize, and camera-cut checks. Both modes
  recover ready history, and viewed lit motion shows no trail. Exact binding,
  view-switch, and user-confirmation gates remain separately tracked in S15.
- [ ] Repeat matched stationary/motion performance after correctness gates,
  with no other editor workload competing for the host.
  October 4's corrected interior route measures 33.65-33.74 generic versus
  92.09-92.46 lane fresh motion FPS on the same binaries, with zero rejected/
  failed frames and matching viewed output. This misses the 100 FPS gate.
  SteamVR/Oculus background work remains active; idle-host acceptance and the
  rest of the counterbalanced sequence remain open. The old exterior camera
  trial is excluded from acceptance; see the investigation for both fixtures.
- [x] Expose directional-shadow raster GPU time through the existing dense
  diagnostic query budget. The cached `Advanced/Shadows/DirectionalCascades`
  scope adds exactly two queries per recorded group: 20 on refresh frames
  versus 18 otherwise; 1,049 completed disabled frames use zero queries.
  Both viewed endpoints retain parity, 18 exported detailed samples join the
  correct completed refresh source frames, and all 21 existing query tests pass.
  Build: zero warnings/errors; stderr empty. The interval includes dependency
  stalls and excludes later render-scope closure; it is not exclusive shader cost.

Risks: atlas rendering moves out of `GlobalPreRender` (desktop and HMD
consumers), GPU cost without per-view culling, masked-material parity for custom
shadow variants, Vulkan/Advanced-only scope.

Gate: identical shadow output at stationary, camera-motion, light-motion and
object-motion views; refresh-frame CPU cost and fresh motion FPS reported for all
frames; the generic fallback still exercised when the lane declines.

## S15. Preserve Temporal Correctness And Resolve The Original Report

Run the relevant checks after every change that affects frame/view identity,
admission or publication. The frame-rate fixture uses the engine default AA
(FXAA) and has no temporal history; use a TSR fixture
(`CameraAntiAliasingModeOverride: "Tsr"`) for these checks. First pass, October 3
([record](../../investigations/rendering/2026-10-03-s15-temporal-checks.md)):
stationary detail stable, camera cut and render-scale changes reset history
correctly, and no ghost trail was seen in a pan past a column. Disocclusion was
inconclusive (unlit view).

- [x] Capture and inspect stationary detail, camera cuts, render-scale changes,
  and a pan past a column with TSR (October 3; linked temporal record).
- [x] Capture and inspect disocclusion on a lit view with a fixed-speed path
  and actual window resize on the retained implementation (October 3).
- [x] Capture and inspect same-camera, same-TSR-scale Default-to-Advanced
  pipeline replacement. Both settled outputs render Sponza; the temporal record
  retains the cold preparation stall and unresolved fine-edge comparison.
- [x] Switch to a distinct camera/view with matching TSR settings and verify
  settled output and history readiness, then restore the original camera.
  October 4 uses actual immediate possession on the main thread; camera IDs,
  positions and viewed images differ, post-process settings match exactly, and
  authoring-resolved per-eye history is valid. Camera-scoped reset/seed generations
  are 14/14, 2/2 and 17/17. The fixed mono channel key correctly stays unchanged.
  Exact first-frame temporal-key/uniform attribution remains in the next item.
- [ ] Inspect saved images/sequences and correlate exact history/view/frame IDs,
  jitter, previous/current matrices, velocity, depth and reset/publication
  events, at several camera positions so stale output cannot pass as success.
  Existing public temporal diagnostics omit the actual temporal key and its
  snapshot frame; separate asynchronous polls cannot certify the first frame
  after a camera switch.
- [ ] Confirm deferred TAA/TSR bindings consume the correct immutable
  pipeline-owned snapshot after admission/lifetime changes. Quiet warnings and a
  `TsrOutputTexture` name are insufficient.
- [x] Fix the isolated deferred TAA jitter aliasing without changing quality:
  retain pre-commit resolve uniforms and exposure readiness; use pipeline-owned
  TAA/TSR snapshots. Actual before/after GPU bindings, motion, cut, and resize
  evidence are in the linked temporal record. Rejected-frame publication remains
  part of the separate correlation gate above.
- [x] Resolve the original recording replay requirement: on October 3 the user
  confirmed no recording is available and directed that it be ignored. Replay
  of that unavailable artifact is waived; this is not evidence of a fix.
- [ ] Attribute improvements using the reproducible live fixture and record the
  user's ghosting/performance confirmation.

Gate: temporal behaviour is classified validated, failing or unverified with
evidence. Failing or unverified behaviour blocks closing the original report.

### S15a. Bound Shadow-Update Recording During Camera Motion

Validated for its owner on October 3 (packet lowering no longer scans runs that
cannot reach the packet minimum; motion 33.6 to 40.1 fresh frames per second with
identical images; the first-movement descriptor step is explained and bounded).
See the [record](../../investigations/rendering/2026-10-03-motion-fps-shadow-recording.md).

- [x] Rerun the affected S15 temporal checks. The correction leaves the accepted
  packet set and every recorded command unchanged, so no change is expected.
  (October 3: with TSR active, stationary, cut, render-scale and pan checks
  pass on the build containing the correction; see the
  [temporal record](../../investigations/rendering/2026-10-03-s15-temporal-checks.md).)

## S14g. Close The Steady-State Gap After Play Round Trips

Validated October 3 for both criteria (motion 98% of before play; device-local
memory flat from the first to the third round trip). See the
[record](../../investigations/rendering/2026-10-03-s14g-post-play-cost.md#why-the-old-set-was-never-retired-fixed).
The memory growth was the resource planner keeping every superseded resource
generation's full image/buffer set alive; publishing a generation now removes
older generations' states for the same output so their allocators retire. The
same defect leaked a full set per render-scale change or resize.

- [ ] Planner states of short-lived outputs (each world copy's shadow viewports)
  still accumulate under new keys until the 12-state cap evicts them. They hold
  no textures; retire them when their output is destroyed if they ever grow.

## S14e. Capture The Intermittent Play-Exit Exception

Owner: Runtime Core with the play-mode owner. See the
[S14e record](../../investigations/rendering/2026-09-27-s14e-exit-exception.md).
2 of 7 exits once threw between node deactivation and persistent-root
reactivation. A failure now recovers into a live edit-mode world and writes the
exception with its stack to `playmode-transitions.log`. It has not recurred in
about 40 exits since (13 on September 27, about 27 on October 3).

- [ ] When `playmode-transitions.log` records an exit failure, fix the cause it
  names.

Gate: the cause is fixed and repeated full probe runs show no exit failure.

## S16a. Black OpenGL Scene On The Measurement Host

Owner: Rendering (OpenGL). See the
[S16a record](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md).
Admission now polls every stage program and names failed ones (see
[Frame Loop Design](../../../architecture/rendering/frame-loop-design.md#the-render-thread-never-waits-for-compilation-linking-or-uploads)).
The measured loading cause was one-chunk-per-tick progressive upload, with all
materials waiting behind the 4096×2048 float environment map. The retained
budgeted transfer correction drains the fixture by the 10-second sample instead
of 27–29 seconds. OpenGL Advanced still waits for complete texture publication.
The dark-shadow discrepancy is attributed to mismatched fixture resolution;
matching the requested resolution establishes parity in two views. The earlier
harness profile capture also incurred a large GPU-profiling penalty.
Query-readback ordering is now corrected and the measured observer penalty is
small; accepted harness geometry and matched performance comparisons remain open.
The October 4 DevelopmentProfile repeat admits current geometry. Its sparse
immutable-texture mutation and runaway log-average exposure are corrected and
cold/warm validated; full-mip bounded sampling also passes its live gate.
The pinned control fails stage admission and retains
stale scene output. Those limitations are tracked below; earlier scoped
upload/query results remain valid.

- [x] Make progressive upload progress time-budgeted (bounded by the existing
  per-frame texture budget) instead of one 16 KB chunk per tick, so load time
  is no longer limited to one chunk per render tick. Live evidence records up
  to 256 chunks per callback, byte/time yields, a 16 MB byte maximum, and no
  measured 2 ms upload-budget overrun; see the linked OpenGL investigation.
- [x] Let a lower-priority progressive upload use a free slot instead of
  yielding to every higher-priority registration
  (`TextureUploadScheduler.HasHigherPriorityUpload`). Exact owners are excluded
  from waiting priority; the live constrained-budget run observes two owners
  and drains to zero, with no limit increase or transfer-budget bypass.
- [x] Decide whether OpenGL Advanced should publish materials whose textures are
  still uploading (for example with a placeholder handle) rather than reject
  the whole family. Retain strict admission: native bindless handles freeze the
  mip range, and silently substituting a placeholder changes required material
  content. Revisit an explicitly authored placeholder policy only if measured
  loading latency remains unacceptable after upload and profiler corrections.
- [x] Restore OpenGL GPU timing readback. Window statistics resolved queries
  before publishing the current renderer, so reads returned unsupported and
  pending queries expired. Publish the renderer before statistics; live enabled
  samples now read about 2.6 KB/frame and report ready GPU timings.
- [x] Quantify GPU pipeline profiling overhead and Advanced diagnostics latency
  after the readback correction. Settled off/on/off medians are
  36.89/37.05/37.09 ms; the enabled window has p99 39.59 ms, maximum 40.54 ms,
  and diagnostics latency 0.734 s. No multi-second gap appears in those windows.
  Full matched harness acceptance remains below.
- [x] Publish the OpenGL directional target mask and scoped matrices after
  material callbacks; fresh atlas captures now contain all four cascades.
- [x] Attribute the remaining dark-shadow output using raw depth and matched
  cross-backend captures. Both backends are dark in this nonprocedural fixture;
  bootstrap requests 4096 shadow resolution on GL versus 1024 on Vulkan, yielding
  fourfold different bias. Matching GL's request to 1024 matches all cascade bias
  settings and puts HDR means within 0.6%/0.2% of Vulkan in two viewed positions.
  No shader bias adjustment is justified. Preserve this match in the harness;
  this does not certify display tonemapping or full performance acceptance.
- [x] Regenerate specialized OpenGL Advanced shader source on reload. Authored
  dependencies are resolved before specialization; discarded source builds
  release their compilation claim and old shader state. Live mono include edits,
  repeated reload and restoration reach Ready with the expected fingerprint and
  viewed output. Release build: zero warnings/errors. Existing selected tests:
  65/66 pass; one unchanged source-string assertion also mismatches HEAD.
  MSAA/stereo and driver-parallel overlap coverage remain in integrated acceptance.
- [x] Require actual Advanced admission, a current output reservation, advancing
  accepted preparation/raster/shading receipts, and collected opaque/masked
  mesh commands in the harness. Live GL and Vulkan camera sequences reject
  every sky-only sample and admit 265-command interior views after 5.7/5.6
  stable seconds. Resident candidate counts alone remain insufficient.
- [x] Add explicit requested directional shadow resolution and named-light
  selection to the measurement harness, with pre/post readback and reporting.
  Live GL 4096→2048→1024 and Vulkan 1024→2048→1024 round trips read back exactly
  and re-admit; missing lights and incomplete arguments fail. This matches
  before-warmup settings, not bootstrap/cold-start behavior or full image parity.
- [x] Require advancing Vulkan completed-frame counts throughout harness
  readiness, rejecting missing telemetry, retained PresentNow terminal faults
  and latest rejected/failed frames. Vulkan/GL live interior windows admit after
  5.4/6.2 stable seconds; both reject all six sky-only samples. GL adds no profiler
  query. Recorded terminal-fault evidence matches the rejecting conditions;
  no new live terminal fault was injected. Diagnostic `NoStabilityGate` still
  bypasses readiness and cannot establish performance acceptance.
- [x] Capture MCP render-command pass summaries within their collection's
  rendering-buffer read scope and return owned scalar rows before formatting.
  No per-frame snapshot copies were added. Vulkan and OpenGL each pass 120 live
  requests, retain 265 meshes in settled interior views and zero in sky views,
  and pass readiness. Release build: zero warnings/errors; existing collection
  tests: 13/13. Other response fields remain independently sampled; diagnostic
  latency and full matched performance acceptance remain open.
- [x] Reconstruct the pinned OpenGL control and rerun correctness admission.
  Unchanged `9fee4b983` builds without warnings/errors; isolated path-length
  setup failures are resolved using identical supplies. Cold/settled/warm
  interior windows pass 0/100 samples, with canonical texture-source rejection
  and stale output after a camera cut. This establishes the control limitation,
  not a successful performance comparison.
- [x] Preserve sparse texture transition ownership after bindless publication.
  New handles wait for residency publication; existing leases retire before
  storage changes. Cancellation retains its originating fence owner; sparse
  metadata/content revisions publish together. Cold/warm promotion/demotion
  drains with no immutable-parameter errors; readiness admits 17/20 and 18/20
  interior samples after the stability window and rejects all 12 sky samples.
  Build: zero warnings/errors. Existing tests: 52/61, with eight source-string
  failures already present in HEAD and one from the earlier slot-ownership
  change; none points to this sparse diff. Forced cancellation/device-failure
  coverage remains open in integrated validation.
- [x] Correct runaway OpenGL log-average exposure with requested features enabled.
  Mono/array shaders use the established Vulkan mip-backed mean-relative floor.
  Cold/warm exposure settles at 1.38536 and 0.91619 in two views, within 0.002%
  of predictions from captured mip samples; viewed final output recovers detail.
  Automatic exposure, bloom and authored bounds stay enabled/unchanged. Build:
  zero warnings/errors. Existing tests: 6/8, with two unchanged Vulkan source-path
  assertions failing. Full stereo, post-resize and matched comparison remain open.
- [x] Spread bounded OpenGL metering samples across the complete selected mip.
  Centered linear strata span indices 0-479 of the recorded 480-texel mip,
  retaining at most 256 samples/fetches. All three non-average modes in two
  views match captured-pixel predictions within 0.001%; all six final images
  were viewed and original settings restored. Readiness admits 9/12 interior
  samples after 5.2 seconds and rejects all six sky samples. Build: zero
  warnings/errors; existing tests: 6/8 with the same two Vulkan source-path
  failures. Stereo execution and row-major spatial aliasing remain outside
  this scoped correction.
- [x] Capture descriptor completion evidence when Vulkan rejects a scene-set write.
  Failure-only diagnostics preserve owner/generation, reference pins, queue
  completion sequences and exact/pending submissions, plus scene slot context.
  Live reproduction identifies an unobserved fence-backed descriptor use after
  slot reuse; guards are unchanged. Build: zero warnings/errors. Existing
  hardening tests: 14/18, including the descriptor mutation guard; four other
  source-contract assertions fail. This diagnoses the fault, not its correction.
- [x] Prevent abandoned lane contexts from leaking scene dependencies into reused
  Vulkan upload command buffers. Exact handle/generation ownership and cleanup
  on abort/reset/destruction keep upload receipts independent of old primary
  recordings. Three resize round trips and twelve camera cuts advance 11,278
  completed frames with no failed or terminal frame; 13 transient streaming/resize
  rejections remain recorded. Four settled capture windows have zero rejected/
  failed deltas and fresh output. Build: zero warnings/errors; source review passes.
  Existing hardening checks pass 14/18 with the same four source-contract
  failures observed before this correction; no tests were modified.
- [x] Verify mono raw cascade depth after actual window resize on both backends.
  The same nonprocedural Sponza/Advanced/CpuDirect fixture and 1024-pixel shadows
  render both views at 1600x900 and restored 1920x1080. All captures were viewed;
  admitted stages advance, and Vulkan completion advances without capture-window
  rejection. Corresponding published inner tiles differ by at most one occupied
  texel, with depth quantile differences below 0.00001. Unused atlas texels are
  excluded and framebuffer Y orientation is accounted for.
- [x] Resolve the declared cross-backend HDR shading gate in the dark camera
  view. Sparse samplers no longer apply the resident mip offset twice; native
  texture retirement rebases retained CPU mips before dense recreation, preserving
  authored clamps and publishing metadata/content generation together. Fresh
  processes without native inspection or material-readback prerequisites pass
  both views at 1600x900 and 1920x1080. Dark-view mean differences are
  0.479%/0.478% (previously about 12.3%); atrium differences are 0.098%/0.035%,
  all below the unchanged 1% threshold. Cascade occupancy still differs by at
  most one texel. Vulkan capture windows advance 49/163/39/60 completed frames
  with zero rejected/failed deltas; all eight captures were viewed. Final build:
  zero warnings/errors; four existing resource-contract tests pass, none changed.
  The investigation preserves the unsuccessful sampler-only repeat and the
  diagnostic-induced failure. Final display tonemapping, temporal sequences and
  performance acceptance remain open.
- [ ] Rerun the S13i OpenGL comparison once GPU pipeline profiling no longer
  stalls OpenGL. With `ProceduralSky: true` both binaries now draw Sponza under
  the harness, and the current tree is faster than B in the same window
  (whole-frame p50 27.0 vs 43.6 ms), but B's capture aborted on a render gap and
  neither run is a valid measurement. October 4's rebuilt control also fails
  correctness admission; do not patch its renderer or bypass the guard to
  present it as the original baseline. Reopen when a reproducible, correctly
  rendering control can pass the same gates, with any source delta disclosed.

Gate: OpenGL renders the fixture under the harness on this host, or the
limitation is recorded with its cause. OpenGL acceptance in S16 stays open until
then.

## S13i. Prove The Cumulative Fix On The Reported Workload

Owner: Rendering with Profiler. The cumulative comparison was validated for
reachable scope on desktop Vulkan (September 27) but the October 1 run on the
reported workload is NOT PASSED: stationary dispatch reached 1,901 ms with
1,888 ms outside recorded Vulkan work, and motion encoding/dispatch tails remain.
See the [cumulative record](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md)
and the [CPU investigation](../../investigations/rendering/2026-10-01-cpu-stall-attribution.md).
Sampled allocation is 1.646 MB per completed present after the retained
corrections.

- [ ] **Pool retention and excluded lifetimes.** Validate scene-unload retention:
  pooled objects keep their last resource/context references up to historical
  per-frame high-water demand. Do not broaden reuse to receipt-owned, captured,
  ordered-batch or OpenXR work without proving every borrow ends before reuse.
- [ ] **Submission-contract sealing** (about 69 KB per present). Classify any
  remaining operation resource-use array growth first; preserve dependency
  closure and ownership.
- [ ] **Sealed binding snapshots** (about 1.077 MB per present, the largest
  owner). Measure required contents and copy frequency, then establish explicit
  program-borrow retirement and content generations before reusing storage.
  `ApplyBindingSnapshot` retains snapshots beyond recording and frame-data
  signatures use snapshot identity, so frame-slot retirement alone is
  insufficient.
- [ ] **Jitter attribution.** Correlate GC suspension, successful-present
  intervals and the ~1.9 s outer-dispatch gap with aligned scheduling and file
  I/O evidence. Use a constant-speed camera path or account for easing; obtain
  displayed-motion evidence and user confirmation.
- [ ] **Profiler series.** Repair the unavailable camera-state and default-zero
  lifetime/lease series, then repeat observer overhead/retention admission on a
  frozen binary.
- [ ] **Comparison evidence.** Establish replacement source-row and
  previous-increment comparisons; repeat S13a's matched matrices against the
  original baseline and the previous validated increment, keeping Debug and
  Release separate.
- [ ] Complete the separate
  [Advanced GPU attribution item](optimization/advanced-pipeline-gpu-attribution-todo.md).

Each retained increment needs its own frozen control and candidate, stationary
and uninterrupted-motion comparisons with matched observers, bytes per completed
present and GC tails, and validation of real mutations, relinks, snapshot
ownership and retention.

## S12. Improve Shared Advanced Preparation Safely

Validated for its reachable paths; the distinct-runtime-owner gate was
dispositioned Not Applicable by the user on September 25. See the
[S12 record](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md).

- [x] Disposition the distinct-runtime-owner gate for current product paths.
  Reopen when a supported distinct-world Advanced path exists, under the
  conditions in the record. Same-host snapshot/restore is not evidence for
  distinct-owner lifetime safety; other recorded limitations remain.
- [x] Revisit the incoming unit-test compile blocker: the current Release test
  project compiles without the absent `IAdvancedGlobalIlluminationProvider`
  failure. The selected existing texture/temporal suites ran 97 tests (86 pass,
  11 fail); those failures require separate classification and do not constitute
  a passing regression gate.

## S13c. Retain Logical Mesh/LOD Registration By Real Mutation Identity

Validated for reachable scope. See the
[S13c record](../../investigations/rendering/2026-09-26-s13c-registration-retention.md).

- [ ] With a fixture that has them, validate multiple LODs, threshold edits,
  active-LOD changes, streaming completion/eviction, atlas relocation, a failed
  registration and retry, and repeated create/destroy with atlas-slot reuse.

## Deferred Items

These were measured and deferred. Reopen only on the stated condition.

### S13f. Retain Plan-Derived Operation Metadata

Deferred October 1: structural scans are allocation-free and below 0.05 ms per
presentation ([record](../../investigations/rendering/2026-10-01-advanced-operation-metadata.md)).
Reopen on material operation or family growth, scan allocation, above-budget
repeated measurements, or separately attributed structural demand cost. A
retained cache needs a publication-stable geometry identity; the invalidation
key is in the record.

### S13g. Bound Warmed Pipeline-Readiness Work

The allocation-only scope is validated; readiness reuse is deferred
([record](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md)).
Reopen with a measured readiness cost. Reuse for an exact accepted dependency
generation must keep reliable invalidation for generated source, shader edits,
layout/device recreation and capability changes, and must never freeze a pending
result behind a structural key.

### S13h. Change Synchronization Only For A Measured Remaining Bottleneck

Deferred October 1: lock acquisition stays below 0.10 ms per present for both
owners ([record](../../investigations/rendering/2026-10-01-remaining-synchronization-gates.md)).
Reopen with measured contention. Before any change: wait/hold distributions,
contender and owner identities, worker utilization, and a documented lifetime
proof for the Advanced storage gate (shared arena lanes, transactional rollback,
parallel-eye scratch).

### S14. Address The Actual Core Update Owner

Tick optimization deferred September 27
([record](../../investigations/rendering/2026-09-27-s14-core-update-owner.md)).
Reopen when a workload shows the tick path at 0.10 ms per update, pending
application at 1.0 ms within a second, a callback at 1.0 ms mean, or
registration churn outside play transitions.

## S16. Integrated Acceptance And Closeout

Only after the individual gates pass; compare both the previous validated
increment and the original baseline.

- [ ] Matched cold-start, cache-warm restart and warmed still/moving-camera runs
  with all-frame p50/p95/p99/max, successful-present intervals, dropped timing
  samples, and preparation, recording, waits, GPU and loading time separately.
- [ ] Repeated resize, shader/pipeline reload, mesh/index/texture admission,
  multi-view ownership and teardown cycles, including failure cases.
  Root source refresh and Vulkan complete-family reload recovery have scoped
  evidence below. Cross-backend, multiview, failure and lifecycle coverage remain
  open. Track evidence in the
  [root reload investigation](../../investigations/rendering/2026-10-04-shader-root-reload.md).
- [x] Refresh clean disk-backed shader roots without overwriting unsaved or
  generated sources. Original/generated hashes, fresh ready link generations,
  viewed root/include edits and exact restoration validate Vulkan source flow;
  unsaved text survives watcher and manual reload. Build passes without warnings.
  Existing dependency/resolver/asset-cache tests pass 32/32 without modifications.
  OpenGL mono root/include/in-memory edits and restoration also render with
  current authored text shared by specialized variants. All twelve two-position
  HDR captures were viewed; unsaved text survives watcher/manual reload. Its
  Release build has zero warnings/errors; existing selected tests pass 65/66
  with the same previously classified source-string assertion failure. No tests
  were changed. Wider MSAA/stereo, source-object
  replacement and the broader reload gate remain open.
- [x] Preserve complete Vulkan stage families during asynchronous reload.
  Physical capability and reservation identity govern intent authoring; sealed
  preparation handles pending executable pipelines through existing retries.
  Initial/stale binding refresh starts at visibility preparation. The full
  root/include/in-memory sequence renders expected probes and restoration from
  two camera positions, with fresh completed frames in all twelve capture windows
  and no terminal rejection. Release build has zero warnings/errors. Existing
  focused tests report 96/101 passing; all five failures match the prior independent
  HEAD-worktree run and predate this fix. No tests were changed.
- [ ] Play entry/exit, probe refresh, repeated redraw/picking, attachment
  metadata and idle BVH diagnostics.
- [ ] First-use and warmed toolbar and camera settings, OpenGL/shared backend and
  XR/stereo paths where changed code affects them; missing hardware stays an
  explicit blocker.
- [x] Correct Vulkan/OpenXR startup lease detection and enabled-extension
  dispatch. A null bootstrap lease no longer reports success; session setup
  resolves only enabled Vulkan extension entrypoints. The live SteamVR session
  reaches strict SinglePassStereo after both observed startup errors are fixed.
  The isolated Release build has zero warnings/errors; no tests were changed.
- [x] Capture and view fresh left/right Vulkan SinglePassStereo output in two
  windows and complete one normal runtime teardown. Preview IDs advance
  489/491 to 695/697; final submitted/per-eye publish counts are 182, with zero
  end-frame failures and zero sequential fallback attempts. Both eyes acquire
  and release 436 images; one retired swapchain generation drains, leaving no
  acquired images or pending generations. This bounded smoke pass does not
  establish acceptable performance or the broader lifecycle/failure matrix.
- [ ] Resolve hardware startup/warm-up cost and continuing no-layer frames;
  validate head-motion/orientation, ghosting and comfort with user feedback,
  repeated lifecycle/failure cases, and the separate OpenGL/OpenVR path. The
  user now confirms physical headset presentation during the reopened live
  session, but reports mostly black output, flicker and old-frame jitter when
  Sponza is visible; sky-only views mostly work. Visual acceptance fails.
  The reported desktop UI ownership failure now has a scoped correction below;
  drag-resize and Play-mode recovery remain open. Earlier captured stacks show
  changing CPU recording work, not a proven deadlock.
  Bounded Release diagnostics identify warmed submission-reservation failures.
  Disabling eye previews for an isolation run removes forced reservation waits
  but reveals continuing frame-data-slot rejection. A sampled thread-time trace
  attributes most recording delay to monitor entry; distinguish contention from
  aggregate synchronization cost before changing it. Neither issue has a
  validated correction.
  Actual CLR contention totals only 0.1055 ms in a 15-second capture; this
  does not support a multi-second monitor wait. A redundant-lock candidate
  showed no overall recording benefit and was reverted.
  Preview captures alone do not establish continuous headset presentation. The
  first successful capture was roughly three minutes after launch; the run
  ends with 254 no-layer frames. Submission diagnostic ledger capture was not
  enabled, so its zero accepted/rejected counters are not acceptance evidence.
- [x] Correct supplemental lighting and ambient-occlusion declaration ownership
  and retain callback storage by backend mesh renderer, material and logical
  draw slot. Captured values still determine content generation. The isolated
  Release build passes without warnings/errors; fresh eye images at frames
  3553/3555 are captured and viewed. Warmed schema fallbacks fall from roughly
  380 to 2–4, and reservations remain bounded at 2,757–2,764 in the final
  60-second sample instead of exhausting the 131,072-entry limit.
- [x] Exercise desktop resize with XR active at 1600×900 and 1920×1080.
  Both requested dimensions become active, engine frames and XR submissions
  continue, and end-frame failures remain zero. This validates the automated
  resize path only; the separate UI ownership correction is validated below.
- [ ] Complete the [OpenXR stereo worklist](#openxr-stereo-and-desktop-regression-worklist)
  below. The scoped uniform/storage fix and automated resize check above do not
  clear the reported physical rendering or remaining desktop lifecycle failures.
- [ ] Temporal sequences inspected and user confirmation recorded.
- [ ] No new hot-path allocations, unbounded retention, queue starvation, unsafe
  disposal, silent fallback or missing required draws.
- [ ] After explicit test clearance, focused regression coverage.
- [ ] Every deferred/blocked item has an explanation and reopening condition.

Final acceptance: all applicable gates pass, exclusions are explicit, and the
original report closes only with reproduction/correction evidence and explicit
user confirmation.

## OpenXR Stereo And Desktop Regression Worklist

Physical SteamVR/OpenXR Vulkan SinglePassStereo is available. Hardware absence is
not the blocker. The user reports mostly black output, flicker and old-frame
jitter with Sponza visible, while sky-only views mostly work. The desktop was
reported uninteractive and apparently frozen after resizing. The ownership fix
below restores live desktop UI; keep the remaining failures open until their
corresponding live results and physical feedback establish a fix.

- [x] Restore desktop ImGui composition and interaction while XR is active in
  Edit mode. Switching to the VR camera removed the editor canvas producer;
  Vulkan kept an old UI snapshot until resize discarded it. An explicit desktop
  viewport UI override now retains the editor canvas independently of the scene
  camera. The isolated Release build passes with zero warnings/errors. Real
  File-menu clicks open and close the menu, panels survive and reflow at
  1600×900, and fresh left/right eye frames 2168/2170 contain no editor UI.
  This closes the reproduced ownership failure only.
- [ ] Validate interactive border drag-resize and recovery; the native drag
  attempt was rejected before execution. Programmatic resize and subsequent
  menu interaction pass, but do not establish behavior inside the modal drag loop.
- [x] Diagnose Play-mode progress loss with OpenXR active. The engine timer was
  stopping on a collect-thread terminal fault, leaving the main thread spinning in
  the native event pump. Two defects are corrected: OpenXR drove the world's
  pre-collect publication while Play transitions had torn down its visual scene
  (duplicate GPU-scene index after re-initialization), and OpenXR view-batch
  planning shared one descriptor array across the collect, render and diagnostic
  threads (torn view IDs). Publication now follows the world host's render
  session and planning uses call-local storage. Four OpenXR and three
  desktop-only round trips keep the timer running, restore Edit with live editor
  panels, and re-register Sponza in the Play copy. Release build: zero
  warnings/errors; existing tests 39/40 with one unrelated source-string failure;
  no tests changed. XR layer submission after Play is tracked in the next item.
- [ ] Eliminate prolonged cold/warm-up gaps (**next slice**). The last run retained
  eye frame 143 through a long pipeline-admission plateau, then recovered. Every
  Play entry restores a fresh scene copy and restarts this plateau: submitted
  frames stayed at 80 for a full capture-free round trip while sampled omissions
  reported `No compatible Vulkan render program is available yet`. Measure
  manifest changes and cursor resets before changing the thread-local admission
  progress model. Preserve bounded work, exact pipeline compatibility and XR
  deadlines.
- [ ] Attribute the post-Play XR observations: a 24-second render stall inside
  `Renderer.RenderWindow` on one exit, and a fresh third-entry eye preview that
  renders the Sponza wall solid magenta where earlier frames were textured.
- [x] Bound editor memory across Play round trips. Destroyed Play copies stayed
  reachable through shared program binding caches, event lists in the global
  object cache, embedded-asset back-references, skybox resources and a stale
  pick result; descriptor scratch was allocated per renderer; exit left garbage
  uncompacted. Desktop now plateaus (5.98 → 8.79 GB private over three trips,
  live heap 2.22 GB, unchanged on a fourth trip) where it grew about 1.5 GB per
  trip. See the
  [memory record](../../investigations/rendering/2026-10-04-editor-memory-retention.md).
- [ ] Reduce OpenXR memory to the 8 GB ceiling. With user settings the editor
  settles at about 24.7 GB private and a 17.4 GB working set. That is 7.4 GB of
  device-local Vulkan memory (mirrored into the process as write-combined
  memory), an 8.5–9.4 GB managed heap, about 1.2 GB of CPU-resident texture data,
  and the rest native. The stereo pipeline no longer holds its 2.3 GB target set
  three times (12.4 → 7.4 GB device-local). Generated uber variants duplicated
  by the Play snapshot and ~54 MB/s of per-frame binding-snapshot garbage remain.
  The ordered plan is the
  [editor memory reduction TODO](optimization/editor-memory-reduction-todo.md).
  Evidence is in the
  [memory record](../../investigations/rendering/2026-10-04-editor-memory-retention.md)
  and the
  [stereo flicker and duplication record](../../investigations/rendering/2026-10-04-openxr-stereo-flicker-and-target-duplication.md).
- [x] Keep the stats overlay consistent. It refreshed four times a second from
  single-frame samples and alternated between desktop-only and VR-inclusive
  counters depending on whether the last frame carried VR work, so it jumped
  between sets of values. It now shows window means with a stable layout that
  fits the viewport. A second cause also made it alternate: when a native buffer
  grew, the existing descriptor sets kept binding the retired buffer.
  Renderers now refresh descriptors when the native buffer binding revision
  changes (68 of 92 HUD changes revisited a stale state before, 0 of 23 after).
- [ ] Remove the black period at the startup stereo generation transition
  (about 2.5 s of no-layer frames). A commit made inside the OpenXR planner scope
  retires the live eye-planner allocator, forcing a fresh set without
  auto-exposure history. See the stereo flicker and duplication record.
- [ ] Resolve warmed frame pacing and black/no-layer output with Sponza. Current
  mixed-workload CPU dispatch is about 237 ms median, with approximately 39 ms
  in snapshot copy; these overlapping values are not GPU time. Attribute CPU,
  GPU completion, submission reservation and frame-data-slot refusals separately.
  Preview-off isolation removes forced reservation waits but not frame-data-slot
  refusal. Preserve completion ownership; do not hide failure with stale output,
  sequential fallback, disabled features or increased capacity alone.
- [ ] Correct the remaining 2–4 uniform-schema fallbacks, including observed
  skybox intensity/rotation ownership. Preserve publication generation and
  per-view ownership, and verify fresh output and bounded storage.
- [ ] Validate both eyes through motion and Sponza/sky transitions: freshness,
  eye assignment, orientation, stereo projection, temporal histories, exposure,
  flicker, ghosting and old-frame reuse. Eye-preview screenshots alone cannot
  establish continuous physical headset presentation or comfort.
- [ ] Repeat enable/disable, resize, session visibility/focus changes and normal
  teardown; verify balanced image ownership, drained retired generations,
  bounded retention, no device loss and no silent sequential fallback. One
  earlier clean teardown is scoped evidence, not the complete lifecycle matrix.
- [ ] Exercise the separate OpenGL/OpenVR hardware path and record explicit
  backend-specific outcomes before closing the shared XR acceptance gate.

Evidence and rejected hypotheses belong in the
[hardware investigation](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md).
The original recording is unavailable and replay is explicitly waived; user
confirmation of the reproduced hardware symptoms remains required.

## Follow-ups Found During This Work

Not gates of this TODO; each needs its own owner.

- **Silent canonical rejection.** The Advanced canonical publisher drops
  commands it cannot support (for example a mesh whose CPU vertex array does not
  match its vertex count) and commits an empty publication without a diagnostic;
  `GPUScene.TryGetCanonicalCompatibilityReason` has no callers. Surface the
  rejection reason and count.
- **Mesh descriptor variants keyed by arena view identity.** Masked cascade
  casters hold two descriptor allocation variants each because the allocation
  key includes the identity of per-frame auto-uniform arena views (320 extra sets
  on the Sponza fixture, bounded).
- **Phase-named diagnostics.** `S13aPublicationTelemetry` and related types,
  `get_s13a_publication_trace`, `XRE_S13A_PUBLICATION_TELEMETRY` and
  `XRE_S13A_PUBLICATION_TRACE` carry a completed phase's name. Rename them by
  responsibility, update their documentation and regenerate the MCP docs.
- **Restore time.** Later play-mode restores take 0.8-1.0 s against 0.25-0.33 s
  when snapshot sharing landed (September 27); the cause is not identified.
- **Shadow edge aliasing.** Directional shadow boundaries are strongly
  stair-stepped at rest and in motion on the Sponza fixture (shadow-map
  resolution or filtering).
- **Capture camera attribution.** Viewport sequence captures record the live
  camera transform, which leads the displayed frame by two frames; attribute
  frames to the rendered snapshot's camera.
- **Package history flag.** Collect-side frame-package views never carry
  `TemporalHistoryValid` while the render-side admitted views do; make the
  diagnostics agree.
- **Exposure after history resets.** Auto-exposure takes several seconds to
  settle after repeated history resets (render-scale changes), visible as a
  uniform brightness shift.
- **Black upper sky with some environment maps.** With `klippad_sunrise_2_4k`
  the desktop sky above the horizon renders black while the ground half shows
  the map; other maps render their sky. Present before the memory work. The
  default world picks one of five maps per launch, so compare captures only
  with the same map or `ProceduralSky: true`.
- **Environment map per launch.** The default world's environment lighting
  differs between launches although the skybox texture is fixed, which changes
  image comparisons between sessions.
- **Carried from September 25:** world snapshot/restore MCP calls issued after
  long settles do not return; YAML `OmitDefaults` drops `false` on
  true-initialized booleans unless the member has `[DefaultValue(true)]`;
  `duplicate_scene_node` clones share material GUIDs; the editor hover highlight
  toggles about every 7.5 s and adds dirty traffic to measurements;
  `VPRC_RenderToWindow`'s 1,024-entry deferred presentation ring can keep the
  previous renderer generation alive until overwritten; the replacement GI
  contracts have no unit coverage; 15 `RenderPipelineResourceLifecycleTests`
  failures are pre-existing drift.

## Evidence Index

| Item | Record |
| --- | --- |
| S00-S07, original report | [Last-run diagnostics and S00-S07 gate records](../../investigations/rendering/2026-09-16-vulkan-last-run-diagnostics.md) |
| Debug frame-rate reproduction | [Frame-rate CPU/GPU attribution](../../investigations/rendering/2026-09-22-framerate-cpu-gpu-attribution.md) |
| Collect wait | [Vulkan render<-collect wait](../../investigations/rendering/2026-09-23-vulkan-render-collect-wait.md) |
| S08 | [Index preparation](../../investigations/rendering/2026-09-21-s08-index-preparation.md) |
| S09 | [Shared helper geometry](../../investigations/rendering/2026-09-21-s09-shared-helper-geometry.md) |
| S10 | [Toolbar icon preparation](../../investigations/rendering/2026-09-21-s10-toolbar-icon-preparation.md) |
| S11 | [Camera inspector discovery](../../investigations/rendering/2026-09-22-s11-camera-inspector-discovery.md) |
| S12 | [Shared Advanced preparation](../../investigations/rendering/2026-09-22-s12-shared-advanced-preparation.md) |
| S13a | [Publication attribution](../../investigations/rendering/2026-09-23-s13a-publication-attribution.md) |
| S13b | [Identity feedback](../../investigations/rendering/2026-09-23-s13b-identity-feedback.md) |
| S13c | [Registration retention](../../investigations/rendering/2026-09-26-s13c-registration-retention.md) |
| S13d | [Auxiliary state](../../investigations/rendering/2026-09-26-s13d-auxiliary-state.md) |
| S13e | [Family preparation](../../investigations/rendering/2026-09-26-s13e-family-preparation.md) |
| S13f | [Plan metadata](../../investigations/rendering/2026-09-26-s13f-plan-metadata.md), [operation metadata](../../investigations/rendering/2026-10-01-advanced-operation-metadata.md) |
| S13g | [Pipeline readiness](../../investigations/rendering/2026-09-26-s13g-pipeline-readiness.md), [warmed readiness](../../investigations/rendering/2026-10-01-warmed-pipeline-readiness.md) |
| S13h | [Critical section](../../investigations/rendering/2026-09-26-s13h-critical-section.md), [synchronization gates](../../investigations/rendering/2026-10-01-remaining-synchronization-gates.md) |
| S13i | [Cumulative (September)](../../investigations/rendering/2026-09-26-s13i-cumulative.md), [cumulative (October)](../../investigations/rendering/2026-10-01-cumulative-publication-validation.md), [CPU stall attribution](../../investigations/rendering/2026-10-01-cpu-stall-attribution.md) |
| S14 | [Core update owner](../../investigations/rendering/2026-09-27-s14-core-update-owner.md) |
| S14a | [Play transitions](../../investigations/rendering/2026-09-27-s14a-play-transitions.md) |
| S14b | [Probe spawner restore](../../investigations/rendering/2026-09-27-s14b-probe-spawner-restore.md) |
| S14c | [Component tick timing](../../investigations/rendering/2026-09-27-s14c-component-tick-timing.md) |
| S14d | [Post-exit publication](../../investigations/rendering/2026-09-27-s14d-post-exit-publication.md) |
| S14e | [Exit exception](../../investigations/rendering/2026-09-27-s14e-exit-exception.md) |
| S14f | [Snapshot identity](../../investigations/rendering/2026-09-27-s14f-snapshot-identity.md) |
| S14g, S14h | [Post-play cost](../../investigations/rendering/2026-10-03-s14g-post-play-cost.md) |
| S15a | [Motion FPS and shadow recording](../../investigations/rendering/2026-10-03-motion-fps-shadow-recording.md) |
| S15b | [Directional shadow lane and resumed validation](../../investigations/rendering/2026-10-03-s15b-directional-shadow-lane.md) |
| S16a | [OpenGL admission and texture uploads](../../investigations/rendering/2026-10-03-s16a-opengl-admission.md) |
| Hardware | [Retained rendering hardware checks](../../investigations/rendering/2026-10-03-retained-rendering-hardware.md) |
