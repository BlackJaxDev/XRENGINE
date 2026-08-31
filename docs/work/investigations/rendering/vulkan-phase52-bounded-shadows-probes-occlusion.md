# Phase 5.2: bounded shadows, captures, and occlusion

## Scope and status

Implementation and prerequisite repairs are retained, 2026-08-30. Phase 5.2 is
not fully runtime-accepted or performance-promoted.
Keep explicit disabled, CPU query, CPU software, and GPU Hi-Z modes available.
No CPU fallback is authorized for a requested GPU submission mode.

### Headless RenderBench scenarios, 2026-08-31

The presentationless `XREngine.RenderBench` Phase 5.2 scenario matrix is
implemented. It creates fresh normal/reverse-depth cold repeats for the
three-lane eligibility/disabled/Hi-Z visibility oracle and a separate
no-readback native-buffer stress lane. Its tracked workflow and evidence
contract are documented in
[`renderbench-phase52-scenarios.md`](../../../developer-guides/rendering/renderbench-phase52-scenarios.md).

This work does **not** close Phase 5.2 acceptance. The checker retains first
frames, rejects incomplete cohorts, and reports the remaining failures instead
of substituting a CPU path or treating native submission as visible output.

Implemented headless prerequisites and findings:

- The production world/viewport now has an explicit stopped-timer frame clock,
  synchronous cold resource/program readiness, and thread-scoped renderer and
  wrapper-creation ownership. No hidden window or editor process is created.
- Receipt-bound diagnostics validate the exact command recording's immutable
  sealed resource vector, including descriptor-transitive buffers. Readbacks
  occur after native completion and never feed production visibility decisions.
- RenderDoc proved that the initial black output was a final-copy scissor bug:
  the final draw's viewport was 1024x576 but its scissor was 1x1. Earlier
  G-buffer, lighting and post-process outputs contained the scene. Unbound
  presentationless draws now use the valid scoped output extent, retaining
  OpenXR priority and bound-FBO/subrect behavior. The 12-frame disabled control
  then passed; first-frame and camera-cut images were inspected.
- Submission ownership is published immediately after native queue acceptance,
  before fallible profiling/diagnostics. Accepted arenas cannot be reopened by
  the unsubmitted cancellation path. Premature native reclamation is latched
  as a permanent diagnostic failure, not erased by a later completed snapshot.
- The first Hi-Z matrix localized a second cold-start defect: creation/import
  of `GpuHiZCoarseTilesCapture` changed the descriptor signature after the
  frame context was frozen. A preparation-only pass API now allocates/rebinds
  that texture into the committed generation before collection. It does not
  dispatch Hi-Z, stamp history ready, relax planner validation, or skip a frame.

Initial runtime matrix (before the cold Hi-Z import repair):
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/reports/headless-final-matrix/`.
All 16 fresh child processes ran at 1920x1080, two repeats per depth convention,
with 24 scripted visibility frames and separate no-readback buffer probes.

- All eight eligibility/disabled children passed (192 submitted/captured
  frames). Their 96 repeat-frame comparisons had zero image/candidate mismatch
  with matching input/assembly/shader and adapter/driver identities. The
  eligibility and reversed-depth camera-cut PNGs were inspected.
- All four Hi-Z children failed on frame one before submission: the recorded
  mesh operation's registry/descriptor context had no exact frozen render-graph
  publication. Consequently no Hi-Z parity, false-occlusion count, or complete
  visibility acceptance is claimed by this run.
- All four buffer lanes proved the 7/8/9-command, 8/8/16-capacity boundary,
  actual 64-to-80-byte native growth after recording, exact old-generation
  retention/submission ownership, and subsequent reuse of the recorded slot.
  Two runs observed real GPU overlap; two completed too quickly to prove it.
  All four retained four descriptor references after completion/slot drain, so
  reclamation acceptance failed. No premature reclamation was observed.
- The aggregate correctly exited 1 with `status=failed`; incomplete Hi-Z cold
  repeats cannot become a vacuous pass. This is correctness/debug evidence,
  not timing, profitability, desktop/WSI, or native Advanced shaded-output proof.

Post-repair 1920x1080 matrix:
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/reports/headless-precollect-matrix/`.
The cold-import fix eliminates the first-frame planner exception. All eight
eligibility/disabled controls still pass, and both reversed-depth Hi-Z lanes
complete all 24 frames. Across the five completed lane/depth groups, 120 cold
repeat-frame comparisons have zero image/candidate mismatch.

- Normal depth fails repeatably at step 9 (engine frame 10), after the camera
  cut at step 8. The exact completed GPU descriptor reports seven candidates
  but empty early/late kept streams; the final image is black. The disabled
  control still shows sentinel 1 and disoccluded candidate 2. The saved failed
  PNG was inspected. This is an actual false-occlusion/output failure, not an
  unavailable screenshot or a successful empty submission.
- Reversed depth records 23 two-pass frames per repeat with zero false
  occlusion or missing visible candidates. However, all 144 candidate-frame
  entries remain kept: 112 are hidden in the disabled reference and none is
  culled. The checker deliberately fails the positive-culling gate rather than
  accepting an effectively all-visible result.
- Buffer results remain unaccepted: all four prove native growth/retention and
  recorded-slot reuse, two observe GPU overlap, none proves reclamation. Four
  descriptor references remain; source inspection found active persistent
  descriptor ownership rather than an already-retired slot queue with a known
  frame-based expiry. Extra frames have no proven release deadline, and the
  checker does not force-release ownership to manufacture a pass.

The headless implementation and final clock guards build without warnings or
errors. Runtime validation did not use desktop control or add tests. Subsequent
acceptance work is the normal-depth post-cut visibility failure, a genuine
positive-culling case, and native descriptor-owner retirement; Phase 5.4's
after-seal superseded-packet rejection remains a separate open check.

The final quantitative-analysis run used 1279x719, both depth conventions,
12 scripted frames and two cold repeats:
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/reports/headless-quantitative-odd-matrix/`.
The analyzer preserves the common completed prefix even when a child fails,
without accepting it as a complete cohort. Both normal-depth repeats report
exactly two false occlusions and two missing visible candidates at step 9
(`1,2`); their 10-frame partial cohorts remain failed. Both reversed-depth
12-frame cohorts report zero false/missing candidates but 55 conservative
overdraws and zero demonstrated culls, so also fail acceptance. The five
completed lane/depth groups have 60 matching cold-repeat frame comparisons.
The build preceding this run passed with zero warnings/errors (2.51 seconds).

Final bootstrap smoke checks share an own-if-missing scheduler scope between
the new scenarios and existing RenderBench recipes. This fixes the ordinary
recipe startup failure `The engine execution scheduler has not been installed`
without changing renderer policy. The rebuild passed with zero warnings/errors
and the shared-bootstrap reversed-depth 1279x719 disabled control passed all
12 frames (`reports/headless-shared-bootstrap-control/` under the same run root).
The existing `deterministic-clear` recipe now starts and drains, but still fails
its unchanged `capture_thread_allocations` validity gate: 60,480 bytes across
three captures after 30 warmup and five stability frames, versus the required
zero (`reports/headless-original-clear-warmed/`). This separate measured-path
allocation failure is not waived by the correctness harness and prevents any
blanket no-regression or zero-allocation claim. No tests were added or run.

Latest wrap-up acceptance boundary: shared directional records passed cold
Vulkan two-light contribution/return controls. Repaired OpenGL bindless streaming
passed cold textured mode/return controls in both depth conventions. Fresh
Vulkan open/moderate/foliage cohorts provide 1,080 completed GPU timing samples;
all six calibrated buckets select Disabled / NoMeasuredWin. The scene-growth
timer stop was a canonical tombstone-publication error, not minimization; its
repair passes deactivate/reactivate, primitive mutation and 256-box growth.
Heavy-scene and settled disocclusion mode parity now pass. The final build also
passes 1279x719 mode parity and completed-frame return to full resolution after
repairing auto-exposure manifest ordering. Continuous/first-frame visibility,
quantitative per-candidate false verdicts and exact native-buffer capacity/
supersession acceptance remain unproven. No automatic performance promotion or
whole-engine/native Advanced shaded parity is claimed.

## Findings and changes

- Directional atlas depth invalidation previously included receiver sampling bias/filter state. Separate sampling publication from depth content; only deferred dirty content has a stale-age limit. Unchanged valid static depth remains reusable.
- Progressive captures previously queued a complete six-face batch. Queue one successor face at a time, globally bound work, and publish only after finalization.
- CPU software occlusion had triangle/occluder caps but unbounded candidate scanning, sorting, buffer clearing, and triangle bounding-box raster work. Add explicit work limits and report actual versus reserved work.
- Native Advanced and the opt-in generic path describe a persistent single coarse R32F tile level and a bounded conservative footprint test. Padding contributes far depth; large/invalid footprints remain visible. The generic full mip chain remains available as the control. Native execution/parity acceptance is still missing.
- The initial mutable shared-record migration was withdrawn. The follow-up replaces it with retained immutable CPU publications and completion-owned backend storage; ordinary stream-buffer uploads are not used for these records. The current source is the follow-up implementation, not the historical rollback described below.

## Validation plan and evidence

- Build the narrow editor/runtime path after integration; do not add or modify tests without user clearance.
- Exercise explicit modes, static/moving cameras, odd viewport extents, and capture refresh in isolated named editor sessions. Inspect actual screenshots and validation logs.
- A clear/draw diagnostic in the native Advanced path is not shaded-image parity. Performance promotion requires representative no-occlusion comparisons and false-occlusion checks, not just counters or a successful build.
- Scratch: `Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/`.
- RenderDoc doctor passed (1.44). Evidence-only broker GPU review completed with requested/actual `gpt-5.6-sol`; shader files under Build were refused by broker path policy and were inspected locally, not re-submitted through another path.

## Acceptance limits

No user runtime confirmation yet. Representative open/moderate/occluder-heavy/masked, static/moving-camera cost crossover and image parity are required before automatic/default promotion. Record missing evidence explicitly; do not treat conservative bypass as a positive occlusion result.

## Implementation contract

- Shadow depth-content and receiver-sampling revisions are separate. Receiver bias/filter changes republish metadata without invalidating unchanged depth. Dirty directional content uses the configured stale-frame limit; local content uses four dirty frames. Expired content keeps its atlas placement but samples the explicit conservative fallback until refreshed. Unchanged static content does not expire merely because it is old. Existing exact terminal-output readiness remains authoritative over background refresh budgets.
- Progressive cubemaps enqueue one successor face at the FIFO tail, then finalize after all six faces. Admission is shared across repeated calls in one render frame: four work items and a 2 ms elapsed budget by default. A dequeued item consumes admission even if it throws; failures release deduplication and never finalize partial content. Streaming can defer refresh for at most eight consecutive frames.
- CPU SOC uses a fixed 4,096-entry top-K heap, 16,384 command/submesh inspections, at most 262,144 mask pixels per eye, 1,048,576 reserved raster pixels and 32,768 raster tile visits shared across stereo. Visibility queries reserve work before projection: 8,192 eye queries and 65,536 mask-tile reads. Exhaustion is fail-visible and never accepts partially tested stereo occlusion. Scratch mesh references are cleared after rasterization; selection holds the publication read lock and uses concrete allocation-free traversal.
- Non-forced SOC admission compares measured SOC CPU cost against rejected draws multiplied by measured CPU submission cost. Cold/periodic probes are bounded; explicit forced mode bypasses profitability gating. This estimates CPU savings only, not a measured GPU crossover.
- The unpromoted native Advanced implementation stores one R32F value per 64×64 source tile, including rounded-up edge tiles. Each view records one tile-reduction dispatch and one bounded visibility dispatch, with no per-mip CPU loop. Reverse-Z reduces with MIN; ordinary depth reduces with MAX. Invalid/padded depth contributes far depth. Tests cover every intersected tile; invalid, near-straddling or oversized footprints remain visible. Camera, scene, extent and depth-convention changes invalidate temporal decisions. These are source contracts, not a claim of completed runtime acceptance.
- Telemetry distinguishes actual/reserved raster work, budget bypasses, selection/sort/raster/query CPU cost, and native Advanced Hi-Z dispatch-recording CPU cost. The latter is explicitly **not GPU elapsed time** and its coverage is labelled in MCP/UI. Zero-readback submission does not acquire hidden count readbacks for diagnostics.

## Runtime evidence, 2026-08-30

All sessions were isolated; the user's root world settings and rendering defaults were left unchanged. The controlled configuration imports only the existing deferred Sponza OBJ and a four-probe grid. Graphics-pipeline libraries were disabled in that configuration only.

- Release editor builds passed with zero warnings/errors, including the integrated build at 13:14 (45.33 s), the OpenGL validation build (71.95 s), and the shadow-record rollback build (53.63 s). The final generic rollback build/read-back is recorded separately below.
- Generic Hi-Z build/test and both meshlet task shaders passed offline GLSL/SPIR-V compilation during the attempted rewrite. EXT task compilation used the Vulkan 1.2 target. These shaders were subsequently withdrawn; compilation did not establish image correctness.
- Offline native Advanced validation found `sample` used as a reserved GLSL identifier in the new build shader. The correction also uses 256 shared reduction values, with each invocation reducing 16 source samples first, and explicitly treats nonfinite/out-of-range depth as far depth. Both `BuildDepthPyramid.comp` and `LateVisibility.comp` then compiled for Vulkan 1.3 with the runtime-equivalent preamble. This is compilation evidence only.
- The normal mixed-scene Vulkan run stalled during native graphics-pipeline-library compilation after model import. A five-second trace showed the foreground waiting for the executing native `vkCreateGraphicsPipelines` job, not a CPU SOC loop or a scheduler gate deadlock. The same symptom occurred with occlusion disabled. This is not a SOC regression proof or a completed phase 5.3 fix.
- Controlled Vulkan CPU-direct Sponza advanced for thousands of frames with zero reported validation errors. At camera `(8,6,0)` looking at `(0,2,0)`, forced SOC and disabled occlusion produced byte-identical lighting captures: SHA-256 `F1F0BA60A485123E2B772C64223366D7B76510AAE1E5CFFC4B3581AEBA8FA7E8`, average RGB `0.0063076066`. Geometry changed correctly with the camera. This narrow sample had no rejected bounds and is not a profitability result.
- Disabling directional shadows in the isolated Vulkan scene increased lighting average RGB to `0.043460634`; therefore the dark image is not caused by CPU SOC. This does not yet establish correct shadow parity.
- OpenGL CPU SOC exercised positive raster work: two occluders, 22 closed tiles, 29,268 reserved/executed raster pixels, 3,090 reserved raster tile visits, 22 AABB tests and 6,691 AABB tile reads. No culls in that sample. With the attempted record migration, albedo contained the scene but shadow-enabled lighting was black. After withdrawing that migration, shadow-enabled lighting rendered correctly at `(8,6,0)` and `(-8,6,0)` looking at `(0,2,0)`; both PNGs were inspected. Final forced-SOC captures averaged RGB `0.005696247` and `0.006772005`, respectively. This is a narrow visual check, not representative performance or false-occlusion proof.
- OpenGL probe refresh succeeded: `LightProbe[0,0,0]` had `CaptureVersion=1` and `IblTexturesValid=true` after startup; invoking `QueueCapture` advanced its version to 2 with valid IBL textures. No partial-capture publication or exception was observed in that check.
- Vulkan generic forced GPU Hi-Z executed two builds and two tests in the sampled frame (aggregate across passes/contexts), with zero reported validation errors. Its Sponza albedo was empty while an otherwise equivalent GPU zero-readback/no-occlusion run produced geometry. This is a **failed acceptance check**, not a success based on dispatch counters; correction and rerun are required.
- Native Advanced resources resolved to persistent 30×17 R32F at 1920×1080, but the run submitted no native frame operations. This precedes late-target closure and is consistent with an output-reservation/admission failure. `Required` mode additionally rejected initialization before device readiness. The native path has no final transfer-readable shaded color target here; screenshot failure is not shaded parity evidence.
- RenderDoc 1.44 doctor passed. The named Vulkan process exposed a RenderDoc target, but triggering it produced no capture; no RenderDoc binding or GPU-time proof is claimed.
- A later diagnostic texture capture stalled in `VulkanCommandRuntime.CommandsStop` → `Vk.WaitForFences`, called from the collapsed-window render path. A five-second trace confirmed this differs from the earlier native pipeline compilation stall. The affected named session was stopped. No image or GPU-completion success is claimed for that capture.

### Rollback control and retained-path read-back

- The integrated generic rollback build passed in 63.12 s with zero warnings/errors. Vulkan + `GpuIndirectZeroReadback` + `GpuHiZ` reported `two-phase-current-depth`, zero validation messages, and restored albedo exactly matching the earlier no-occlusion control: SHA-256 `A7F73B766618A0054DE379F96E779D11BEF8ECA0BB5BDC3A0954C38F44F025A3`, average RGB `0.6787017`. The image was inspected. This supports retaining the rollback; it does not identify which removed change caused the failed image.
- That GPU control's `LightingAccumTexture` remained entirely black. Do not equate restored geometry with full shaded parity or call this path regression-free; the lighting failure remains separately unresolved.
- The same rollback binary with Vulkan + `CpuDirect` + forced `CpuSoftwareOcclusion` rendered shadowed Sponza, with two rasterized occluders, 22 tested bounds, no rejected bounds, and zero validation messages. Inspected lighting capture average RGB: `0.006326311`, SHA-256 `C8D439728B82D33C29FCEF1C7A42106329C25AAE9101341978C3E063FA85C92E`.
- The final Release editor build, including the native shader correction and standalone invalidation enum, passed in 51.85 s with zero warnings/errors. A final native `Available` run confirmed `AdvancedRenderPipeline` and zero reported validation messages, but still had no native Hi-Z dispatches or active viewport commands after more than 1,900 frames. Fixing offline shader compilation did not resolve output admission. No shaded native acceptance is claimed. All named editor sessions started for this investigation were stopped.

## Initial review and withdrawn changes (historical)

Retained corrections include native coarse-extent closure, Reverse-Z flag source, framebuffer-Y flag polarity, query-before-projection budgeting, unbudgeted deformation scans, retained candidate references, and invocation-local capture limits. Mutable capture-budget properties use `XRBase.SetField`.

The directional SSBO migration was completely withdrawn after review established that ordinary Vulkan stream-buffer uploads overwrite the same mapped allocation. Descriptor pins protect lifetime, not contents; camera-keyed caches or a modulo-N buffer ring do not establish safety. A future migration needs immutable CPU publication retained by captured operations, followed by serial lowering into storage-capable frame-arena slices after the actual arena reset, with slice-aware descriptors/cache keys and exact completion retirement. The uniform implementation is retained until that bridge is validated.

The ten generic Hi-Z/meshlet files were restored to their pre-task implementation. Source review did not prove an arithmetic/projection cause for the empty image, and no successful R32F write capture was obtained. An attempted early-retain workaround was also rejected: it would make fresh visibility history permanently visible and effectively disable occlusion. It is not present in the retained implementation.

## Remaining acceptance work

### Current follow-up evidence (supersedes the initial rollback state)

- Directional records use a shared 224-byte std430 ABI at binding 39 in forward,
  deferred, enhanced deferred and fog consumers. Captured operations retain
  immutable publications. Vulkan lowers them after the actual retired slot's
  arena reset and keys descriptors by native buffer/offset/range and allocation
  epoch; OpenGL uses completion-fenced persistent storage ranges. Cross-frame
  program artifacts cannot own mutable aliases of frame-owned binding sets.
- Fixed a compute resource-generation mismatch proven in RenderDoc: early
  raster wrote depth image 6665 while coarse build sampled cleared image 1147.
  Prepared compute scopes now select the exact sealed context/allocator/plan.
  The corrected capture `phase52-planner-depth-warm_frame992.rdc` shows raster,
  full-pyramid build and coarse build all using depth image 7495; coarse output
  image 7531 is 30x17 R32F. MCP coarse values span 0.95283157 to 1, instead of
  the previous all-far image. Exported images were inspected.
- Cache review also fixed dynamic-stream context omission and per-operation
  switching-state allocation. Cached planner envelopes retain only the current
  bounded prefix when the context set shrinks.
- Generic test fixes include correct output-buffer capacity, saturated output
  counters, initialized per-draw history addresses even on camera cuts, invalid
  bounds/depth fail-visible handling, and source-pixel (not padded-tile) UV
  addressing. The latter includes a one-pixel raster uncertainty margin.
  Always-on investigation file logging was restored to its documented
  `XRE_HIZ_STAGE_LOGGING=1` gate.
- Real generic GPU timestamps now complete. The root cause was capability
  refresh on a bootstrap-local no-op service, leaving the live query authority
  at Unsupported. Refresh now follows publication to the real authority.
  A live sample had 6,876 ready reads and zero unsupported/invalid/stale reads;
  build was about 0.010 ms and test 0.0037 ms, delayed approximately three frames.
  Eight query pairs remain bounded: saturation is reported, uncertain pairs are
  not recycled. These are per-scope samples, not total frame cost or native
  Advanced coverage.
- The Release build at 15:49 passed with zero warnings/errors. Its coarse-on
  startup advanced normally; earlier intermittent next-slot timeline stalls
  are not thereby considered fixed. Timeout-only diagnostics now expose the
  actual counter/target and accepted slot signal values. Read-only submission
  review found no unsubmitted promise in the shown frame-slot ledger path.
- `reports/vk-single-pose-parity.json` records four Sponza viewpoints (open,
  elevated courtyard, corridor, close foliage), each with disabled/full/coarse
  modes and 20 profiler snapshots. Camera identity/pose was recorded before and
  after each capture. All three modes produced identical raw albedo hashes in
  each view. Resetting the same camera pose for every case had introduced
  one-ULP matrix changes; those earlier comparisons are not culling failures.
  This is static/camera-cut geometry parity, not moving-mesh, masked-pass,
  shadowed-HDR or native Advanced acceptance.
- The first sampled matrix's GPU frame averages (disabled/full/coarse ms) were
  open 0.57/1.14/0.83, courtyard 3.03/3.59/3.41, corridor 2.92/3.45/3.02,
  close foliage 2.34/2.95/2.90. Instrumented CPU frame averages also favored
  disabled occlusion. These short, sequential diagnostic samples establish no
  profitable crossover and do not justify automatic/default promotion.
- Shaded readback remains unresolved: RenderDoc lighting events contain dark
  shaded geometry, while MCP `LightingAccumTexture` reads all zeros. The
  expected-layout fallback experiment did not fix it and was removed. Final
  Vulkan output also has pink/green artifacts; geometry parity must not be
  described as whole-frame visual parity. A one-shot Release-visible stack
  identified the skipped binding-39 draw as a generated material-table indirect
  submission, not deferred-light warmup. The program cache omitted the forward
  pass dimension and could reuse a shader with incompatible storage bindings.
  The shared GL/Vulkan cache now includes that dimension and carries its
  generator-time lighting contract. Acceptance must use a rebuilt binary.
- Fresh OpenGL controls rendered Sponza with CPU-direct submission but produced
  empty albedo with GPU zero-readback, even with occlusion disabled and GPU
  frustum culling bypassed. These controls isolate a GPU submission/binding
  problem; they do not establish a Hi-Z failure. The first source-parser-based
  shadow-binding patch did not fix it and was replaced by the typed cache
  contract above. No automatic CPU fallback was introduced.
- Another coarse-on Vulkan startup lost the device on frame 22; a later full
  Hi-Z startup advanced past 2,500 frames. No timed wait expired in the failed
  run, so the absence of a timeout snapshot is not evidence of GPU completion.
  A bounded source review found unsynchronized CPU resets of live GPU-owned
  counters, including per-view counts and early/late transparency counts.
  Replacing these with queue-ordered clears is required before another startup
  acceptance run. This concrete defect is not yet proven to explain the loss.
- Queue-ordered counter clears built successfully, but the fresh Vulkan Hi-Z
  run produced empty albedo. Switching only occlusion to Disabled restored the
  exact courtyard hash `A7F73B766618A0054DE379F96E779D11BEF8ECA0BB5BDC3A0954C38F44F025A3`.
  Restoring the dedicated late-output clear is an isolation experiment, not a
  proven fix: its buffer set is already covered by the revised full reset.
  The earlier four-view parity must be rerun after this regression is resolved.
- RenderDoc then identified the counter failure, rather than the clear-count
  hypothesis: early scratch/overflow/per-view clears all bind the same 16-byte
  buffer 3445, despite phase one using the intended distinct buffers 3367/3373/
  3359. Candidate count is 24, but the uncleared per-view count reaches 277,155.
  Repeated compute dispatches share the thin-primary ordinal (which excludes
  secondary-owned operations), causing their reusable descriptor and auto-UBO
  keys to collide. A dedicated stable compute occurrence identity is required
  across preparation, refresh, primary/secondary recording and reuse dependencies.
- The typed generated-program cache correction did not eliminate the skipped
  binding-39 draw in a rebuilt process. One-shot publication and pre-snapshot
  diagnostics now distinguish a missing call, cross-program capture rejection,
  and loss after an accepted publication. No ambient-world fallback is present.
- Explicit RenderDoc start/end bracketing captured the actual diagnostic copy
  in `phase52-explicit-readback_capture_3.rdc`. With occlusion disabled and
  healthy geometry, copy event 1841 reads lighting image 5487 into buffer 16928.
  The source image is visibly shaded and the copied buffer starts with nonzero
  bytes (`1feb5862...`), while the live MCP float capture reports all-zero RGB.
  This narrows the discrepancy to live copy/host visibility/readback handling,
  not selection of a different logical lighting target. RenderDoc texture
  exports must set the event and save within the same script; a separate
  `SetFrameEvent` call does not set the next CLI texture export's event.
- A synchronous readback lacked a transfer-write to host-read memory dependency.
  The exact copied range is now published before fence completion and mapped
  reading, following the [Vulkan synchronization example](https://docs.vulkan.org/guide/latest/synchronization_examples.html#_cpu_read_back_of_data_written_by_a_compute_shader).
  Rebuilt live capture acceptance remains required; the barrier is not yet
  claimed to explain the black capture. A bounded mapped-byte diagnostic was
  added only to explicit float texture reads.
- Rebuilt live captures still returned zero mapped lighting bytes with the host
  dependency and with RenderDoc injection disabled. Standard and synchronization
  validation were enabled for a follow-up run and reported no messages. Depth,
  normals, material parameters and AO have nonzero live data. Thus a packed-float
  decoder error is ruled out; actual rendering versus replay/state differences
  remain under investigation.
- Concurrent isolated-session startup exposed a retention race: a Building
  session has no editor PID yet and its disposable build outputs could be
  reclaimed by another launcher. Retention now preserves Preparing/Building/
  Starting sessions whose exact launcher PID/start time is alive. Concurrent
  rebuilds subsequently completed; no normal editor outputs were removed.
- Repeated direct-compute descriptor identity now uses a stable compute
  occurrence ordinal and stream lane, consistently across preparation,
  refresh, primary/secondary recording and reuse dependencies. A fresh
  coarse-on startup restored the exact courtyard albedo hash above with the
  queue-ordered counter clears retained. The subsequent multi-view observer
  had overlapping camera writes and is invalid for parity; its mismatches
  must not be reported as a culling regression. The observer now serializes
  each session and requires matching poses and fresh completed frames.
- A GPU-written guard after the lighting image copy reached the mapped host
  buffer correctly while the copied lighting payload remained zero. This
  rules out a missing copy-submission completion or wrong host mapping for
  that observation. RenderDoc replay's nonzero source/copy does not establish
  what the live image contained. Exact pre-copy image/layout correlation is
  being captured; later background luminance reads can overwrite a global
  last-transition diagnostic and are not valid evidence for this copy.
- The atomic follow-up correlated the same requested/copied lighting image
  with an exact submitted `ShaderReadOnlyOptimal -> TransferSrcOptimal`
  transition and no mutable descriptor source. The guard completed and the
  payload was zero. Copy-time `Undefined` discard and wrapper-image
  replacement are ruled out for this observation; lighting production and
  live/replay preservation remain open.
- The missing shadow-record descriptor was correlated to the exact same
  producer snapshot and program during `ForwardPassFBO` pass 3 preparation.
  `ClearBindingsNoLock` retired the frame-snapshot pool while installing a
  scoped program snapshot, releasing its retained storage before descriptor
  resolution. Immediate binding reset no longer retires frame-owned
  snapshots. Rebuilt zero-failure acceptance is required.
- The next rebuilt failure retained the storage correctly but attempted to
  resolve it during producer authoring, before the frame's prepared storage
  authority existed. Indirect authoring now captures program/link, geometry,
  bindings and draw state; descriptor lowering runs under the existing
  prepared-frame authority before serial/secondary consumption. Material-cache
  initialization also no longer retires an independently initialized capture
  pool. A 30-sample live watch (frames 7,096–8,666) completed every frame with
  zero binding failures, skipped draws or skipped dispatches. Storage remained
  five arena chunks / 160 MiB mapped and descriptors remained 175 sets / 30
  variants. Shaded-image acceptance is still separate and open.
- The OpenGL bindless extension was initially queried before its context was
  current. Initialization now retries advertised extension entry points with
  the context active; the rebuilt material binding rung is bindless. A
  bounded per-pass diagnostic then proved the remaining empty image occurs
  before actual multi-draw submission: opaque passes remain `ProgramPending`
  despite backend-ready log entries. Readiness ownership is under review;
  material-row/candidate accounting alone is not draw evidence.
- OpenGL shared-worker programs reached ready, but `glProgramBinary` failed
  during render-context handoff and permanently poisoned their shader hashes.
  A one-shot source-link recovery on the render context now reaches actual
  indirect dispatch. Explicit GPU diagnostics then showed zero draw counts,
  not a raster failure. The material-key compute ABI also bound culling control
  where binding 5 requires draw metadata; that mismatch is corrected. Remaining
  real scatter candidates are rejected by atlas validation, which is being
  isolated with opt-in GPU reject counters rather than CPU estimates.
- GPU temporal invalidation now covers camera identity/orientation, full
  unjittered projection and near plane, depth convention, sampling matrix,
  depth extent/identity and view/scene revisions in both paths. It resets
  history validity without mapped GPU-history clears. Exact sampling-matrix
  changes (including jitter) currently force conservative visibility; this
  safety policy must be accounted for in performance measurements.
- OpenGL scatter rejection was traced to missing unsigned-vector uniform
  handlers: CPU atlas counts were nonzero, but GPU `uvec3` values were zero.
  Scalar and array unsigned 2/3/4-vector uploads now have matching handlers.
  The live GPU echo matches the CPU atlas counts and opaque submission emits
  15 commands; albedo and lighting are nonzero. The derived 2D texture parameter
  path now honors the bindless immutability guard as well. A fresh Release
  editor run completed sparse uploads with zero immutable-texture GL errors.
- Vulkan indirect recording derived frame-data slot zero from its command
  buffer instead of using the acquired output slot. Rejected write diagnostics
  proved slot zero was already Submitted. Both indirect recording paths now
  receive the explicit acquired slot. A rebuilt warmed run reports zero
  rejected writes, binding failures and skipped draws; safety guards remain.
- Auto-uniform parity exposed an actual template mismatch at `ProbeGridDims`
  in `DeferredLightCombine` (the final combine, not directional lighting).
  The packed plan omitted initialization of constant/default bytes in
  non-Material blocks. New ranges now receive their static template once,
  before dynamic frequency patches. Dependent caches and publication/reuse
  gates now include material identity, revisions and runtime layout. Object
  and Instance ranges use canonical draw occurrence slots so different lights
  sharing one fullscreen mesh cannot overwrite each other. These source fixes
  pass scoped review; they have not established the cause of cold-start black
  Vulkan lighting. Earlier restoration followed other runtime changes too,
  so attributing it to the parity diagnostic was not justified.
- The camera observer now waits for 30 completed Vulkan frames after a
  mutation, not merely CPU pose publication. The resulting nine-case run has
  exact albedo parity: open sky `33C9AFF6...` (empty, not geometry evidence),
  courtyard `A7F73B76...`, and foliage `1CF9E7FA...`. Inspected images show
  distinct correct camera views. Recorded disabled/full/coarse GPU-time averages were
  0.72/1.58/1.10 ms, 4.10/4.31/4.02 ms and 3.54/3.74/3.68 ms respectively;
  CPU cost was higher with occlusion. Concurrent diagnostic workloads and
  these short samples do not establish a crossover. The same settling rule
  invalidated an apparent OpenGL strip-image regression; the settled
  courtyard images match exactly. This observer recorded effective occlusion
  mode but not submission strategy. `occlusion.submission_strategy` is a
  last-recorded-pass value, not the current strategy resolver; it can remain
  stale after runtime overrides. Use `meshlets.requested_strategy` and
  `meshlets.effective_strategy` for the current requested/resolved route.
  Treat this as narrow geometry evidence, not final GPU-route acceptance.
  The revised observer explicitly reloads overrides and asserts/records the
  effective strategy and mode throughout each case.
- Vulkan raw counter evidence was withdrawn: the collection diagnostic could
  fall back to mapped CPU values when no native GPU readback capability existed.
  The opt-in per-pass snapshot now reports unavailable/null instead, clears
  fields on frame changes and never preserves stale values from older frames.
  CPU-side binding/write/skip diagnostics above remain valid for their stated
  purpose, but neither they nor submitted-draw estimates prove GPU execution.
- OpenGL settled courtyard and foliage comparisons match across disabled,
  full Hi-Z and coarse modes. A non-power-of-two 1079x1079 target remained
  nonzero through camera motion and reproduced the original image exactly
  after returning to the original pose. Reverse-Z live acceptance remains open.

### Follow-up implementation and capture investigation

The follow-up request explicitly includes prerequisites from other phases. The
implementation therefore includes immutable read-only storage publications,
capture ownership, serial frame-arena lowering and slice-aware descriptors, plus
completion-fenced OpenGL storage ranges. These are prerequisites for replacing
directional arrays, not claims that all of phases 5.3/5.4 are complete.

RenderDoc capture now succeeds: the initial empty target API/status was not a
terminal failure. Two Vulkan captures were retained at frames 4,960 and 64,000
under this investigation's `renderdoc/` scratch directory. They contain 70 draw
calls and 22 dispatches. In the second capture, lighting event 291 has diagnostic
mode 8 in the object uniform block. Its bound directional atlas (4096x4096) is
cleared to depth 1, and the sampled-depth diagnostic produces white through the
swapchain event 737. Both exported images were inspected. In contrast, live MCP
captures of LightingAccumTexture and the viewport return entirely black. The
capture/readback versus planned-resource mapping is now under investigation;
the earlier all-black MCP result alone does not establish a shading failure.
Disabling contact shadows did not change the live black capture. All toggles
were confined to the named isolated session, not saved root settings.

The generic bounded Hi-Z path now has explicit build-only observability and
an opt-in culling path, with capture-visible coarse R32F tiles and real delayed
GPU timestamp scopes. Runtime parity, actual GPU costs, and crossover evidence
remain required. New storage APIs and shader changes are not accepted merely
because individual projects compile.

1. Validate the implemented immutable shadow-record bridge and generated-program cache correction; resolve shaded readback and retain the existing exact-depth/geometry evidence for bounded Hi-Z.
2. Extend narrow shadow/probe checks to targeted invalidation, capture completion under pressure, moving-camera parity and odd extents with actual images and live counters.
3. Establish representative open/moderate/heavy/masked scene false-occlusion parity and GPU elapsed-time crossover evidence. No automatic mode promotion is authorized by the current measurements.
4. Resolve or separately track the native Advanced output-reservation/startup/final-color limitations; do not conceal them with a CPU fallback.

No automated tests were added, modified or run; user clearance for test work has not been given. Validation here is build, offline shader compilation, live MCP read-back, inspected images and traces. The user's normal editor processes and root world settings were not changed.

### Native execution and shadow-sampling isolation (2026-08-30 follow-up)

The remaining black Vulkan lighting has been narrowed to the shadow factor,
not the PBR light calculation. Temporary shader probes in isolated PID 137180
rendered fixed magenta, valid G-buffer depth (0.922–0.992), light color/intensity
1, and nonzero unshadowed `CalcColor` (average 0.0473, maximum 0.3555). The shadow
factor was zero even with contact shadows forced to one. Four cascades and atlas
sampling were enabled; the selected valid cascade sampled raw atlas depth zero
at the actual receiver UV while receiver depth was about 0.9. Both the unshadowed
image and receiver-depth probe were inspected. All temporary shader edits were
restored. `TextFile.Reload` was necessary: `reload_renderer_shaders` alone did not
reread the entry shader's `XRShader.Source.Text`, so earlier probes without that
reload are not evidence.

The captured artifacts are under this run's `mcp-captures/vk-shader-unshadowed/`
and `mcp-captures/vk-atlas-fragment-sample/`. The earlier RenderDoc replay/live
disagreement remains a limitation of that evidence, not proof that live atlas
contents are correct. Logical `CanSampleRenderedCascade` freshness does not
establish native image contents. Native atlas receipts now cover both dynamic
rendering and legacy render passes, separating image/view identity and emitted
depth clears from published consumer descriptors. The original writer count of
zero covered only dynamic rendering and cannot establish missing atlas work.

A separate synchronization prerequisite now overlays pending image state per
subresource before older recorded/submitted state and preserves known layouts
when a sampled view spans mixed mip/layer states. Scoped review, a zero-warning
build and warmed completed Vulkan frames passed. Lighting stayed black and the
current atlas view covers only mip 0/layer 0 (one each), so this fix is not
attributed as the current lighting root cause.

Native stress capture `renderdoc/stress/predevice-bridge_capture.rdc` exposed an
indirect-consumer disconnect: GPU buffers contained command `[36,1,15,11,3]` at
offset 1024 and count one, but the `IndirectDraw` marker contained no API draw.
The existing indirect command-chain secondary executor had no primary call
site even though pipeline prewarm could skip for that reusable secondary. The
primary path now invokes it before direct recording; native draw acceptance is
still pending. CPU submission estimates and marker presence are not substituted
for actual indirect draw commands.

The old diagnostic UBO GPU-readback `valueMatch` result is withdrawn: its source
buffer lacked transfer-source usage and occurrence/completion matching was too
weak. Corrected diagnostic code builds, but has not supplied replacement live
acceptance. Current route checks use `meshlets.requested_strategy` and
`meshlets.effective_strategy` (both `GpuIndirectZeroReadback` in the warmed run),
not the historical occlusion-pass strategy field. Shared shadow-record acceptance,
representative heavy/moving/masked and Reverse-Z coverage, and measured Hi-Z
crossover remain open; no performance promotion is claimed.

### GPU-only shadow publication defect resolved

Live admission receipts in PID 186648 confirmed repeated `NotPublished`
rejections before the shadow command chain. The GPU-only cascade path skipped
both CPU collection and backend-package preparation/publication, while its
render wrapper returned success after the rejected void call. That falsely
published sampleable shadow metadata for an unwritten atlas.

GPU cascades now prepare the normal full-identity package without CPU scene
traversal and publish it through the normal swap. Every requested refresh
prepares current identity so first-use resource-generation changes can retry.
`XRViewport.TryRender` propagates `XRRenderPipelineInstance.TryRender` admission
failure through directional tile/group/primary wrappers; existing void entry
points remain available. A rejected command chain no longer marks a tile
rendered. The existing ordered submission receipt still handles later Vulkan
submission failure. Sequential recovery also prepares GPU-owned packages when
GPU submission is requested, without hidden CPU visibility collection.

Scoped review passed. The first fixed cold run (PID 194772) admitted the shadow
command chain, recorded native atlas depth clears of 1, and produced nonzero
shadowed `LightingAccumTexture` (average 0.00797, maximum 0.2344) with the original
shader and `GpuIndirectZeroReadback`/`GpuHiZ`. The lighting texture and final
viewport were actually inspected under `mcp-captures/vk-published-gpu-shadow-package/`
and `mcp-captures/vk-restored-shadows/`. The latest complete isolated Release
build, including GPU-only sequential recovery, passed with zero warnings/errors.
Final native atlas writer/consumer correspondence and multi-light validation
are still pending. A startup bindless-generation/capacity rejection recovered
before the warmed completed cohort; this is not a zero-failure cold-start claim.

A fresh stress capture also proves actual Vulkan GPU indirect emission:
`renderdoc/stress/primary-secondary-indirect_frame2075.rdc`, E182
`vkCmdDrawIndexedIndirectCount`, command/count buffers with compute writes then
indirect reads, and E183 with 31,436 triangles. This cohort did not exercise the
new indirect-secondary eligibility path; its unrelated `vkCmdExecuteCommands`
event must not be claimed as validation of that branch.

### Subsequent acceptance controls

The final warmed atlas receipts in PID 103256 match: native depth-clear writer
and published sampled descriptors refer to image generation 9565 (view generation
9566). A refreshed light direction produced nonzero shadowed lighting (average
0.01117, maximum 0.25), and the image was inspected. The retained RenderDoc
attempts did not include that atlas refresh, so this establishes native identity
correspondence, not an exported proof of every atlas texel.

The initial multi-light observer result is **not acceptance evidence**: its
deadline guard allowed an advancing `Deferred/ResourceGenerationBlocked`
sequence to pass. It now requires a `Completed` terminal result. The isolated
window was minimized; restoring/maximizing it did not recover rendering after
the viewport reached 1x1. Bloom declared five mips while its factory correctly
created one legal mip. Resource declarations, FBOs and graph usage now share the
legal extent-dependent mip contract; runtime recovery validation is pending.

Fresh OpenGL controls supersede the earlier narrow parity conclusion. Both normal
and reversed depth render the full textured floor with occlusion disabled, but
the two-pass GPU Hi-Z route loses the textured appearance. The initial keep-all shader control
is withdrawn: it invalidated programs without explicitly reloading the active
`XRShader.Source.Text`. Its unchanged image does not establish that the native
predicate was bypassed. A source-verified repeat must distinguish rejection from
the early/late draw/material handoff. The temporary disk edit was restored;
no diagnostic keep-all bypass is shipped.

An offline-only GPU Hi-Z calibration contract now records workload/GPU identity,
matched completed cohorts, parity proof and equal GPU timestamp scope. It does
not change forced modes or enable automatic selection. No profitable crossover
is claimed. The legacy total-GPU-time scalar lacks source-frame provenance;
adding completed-sample identity is required before using it as matched-cohort
performance evidence.

### Multi-light shadow-record acceptance

Cold Vulkan PID 162768 ran `GpuIndirectZeroReadback` with occlusion explicitly
disabled to isolate the shared lighting records. Two independently oriented
white directional lights each had four resident 1024-pixel cascade pages;
intensities were 1 and 0.25. All four captures followed completed frame cohorts
with zero descriptor binding failures or skipped draws/dispatches:

| Contribution | Mean linear RGB |
| --- | ---: |
| A only | 0.007969650 |
| B only | 0.001450062 |
| A + B | 0.009415386 |
| A + B after isolated controls | 0.009415386 |

The combined mean differs from the sum by 0.046%, consistent with the finite
precision render target; more importantly, both separate contributions are
nonzero and spatially different, and the restored combined raw RGBA hash is
identical (`198193809B2C8CC5B58B8F40768339E90B65F02747F90A056E74908AECDFA161`).
The combined and B-only images were inspected. Evidence is retained in
`mcp-captures/multilight-*` and `reports/multilight-*.json`. The second light was
returned to zero intensity afterward. This closes the shared directional-record
acceptance item, not the remaining occlusion or native Advanced shading work.

### Expanded occlusion acceptance (still open)

The next Vulkan cohort in PID 162768 compared disabled, full Hi-Z and coarse
Hi-Z at three settled camera poses. All nine cases retained the requested
`GpuIndirectZeroReadback` route and completed native frames. Each pose's three
raw albedo hashes matched exactly, including the closer foliage view; the
moderate and foliage coarse images were inspected. The report is
`reports/vulkan-post-shadow-fix-parity.json`. Twenty distinct completed GPU
timestamp identities were collected per case, but concurrent OpenGL work and
the pre-final timestamp implementation make those costs exploratory, not
crossover acceptance. None established a win over disabled occlusion.

OpenGL hot-reload controls were confounded: broad renderer shader reload also
made the disabled control flat. They cannot prove a depth-predicate defect or
exonerate it. Cold-process keep-all/early controls restored textured geometry,
while the normal partially compacted phase-one path remained suspect. Later
image inspection shows populated geometry with flat per-material colors; this
does not by itself prove geometry was rejected. A
separate source-proven defect leaves candidate-indexed exact view masks beside
compacted phase-one draw IDs. Paired GPU mask compaction is being implemented;
its relationship to the single-view artifact still requires live validation.

The first 257-primitive heavy fixture caused a descriptor preparation failure
after a previously healthy Vulkan cohort. The terminal record is latched at
frame 20350 (`sealed-primary-recording`, slot 0, `descriptor writes unresolved`);
later zero descriptor counters are not recovery evidence because native
recording is never reached. This fixture has not supplied heavy/moving parity
or timing acceptance. Failure-specific binding diagnostics are required before
choosing a repair. The source-proven frozen native-buffer barrier generation
repair is independent; it is not yet attributed to this descriptor failure.

The MCP tool table was regenerated after adding depth-mode and offline
crossover evaluation tools. Its legacy generator emitted existing dependency
vulnerability/version-conflict warnings and a reference to the removed
`XREngine/XREngine.csproj`; source-table generation nevertheless completed and
both new tools are present. No dependency was upgraded as part of this work.

### Corrected cold OpenGL controls

The first normal/reverse nine-case reports (`gl-normal-parity.json` and
`gl-reverse-parity.json`) are **not independent disabled baselines**: those
processes started with Hi-Z enabled, then switched to disabled. Matching flat
images cannot close parity. Repeating with initial `Disabled` before any Hi-Z
render produced a textured normal baseline (`CC4D60DD...`) and a textured
reversed-depth baseline (`840DB549...`). In both processes, enabling Hi-Z made
albedo flat; switching back to disabled left it flat. The normal baseline was
inspected, as was the earlier flat moderate capture. This is persistent
texture/material state damage, not evidence for a reverse-depth math defect.

Source review identified an invalid OpenGL bindless sequence: progressive upload
initially restricts BASE_LEVEL/MAX_LEVEL to the smallest resident mip, handle
acquisition freezes that sampler state, and the upload coroutine later attempts
to change those limits. The Phase 5.3 prerequisite now defers handle
publication until ordinary binding has progressed the upload and the final mip
range is stable. Later runtime mip promotion replaces a frozen identity before
changing sampling parameters; same-size texel updates do not replay frozen
parameters. No CPU fallback or keep-visible shader was introduced.

The final isolated GL cold controls launched with `Disabled` before any Hi-Z
work. Their first approximately 84 seconds had a zero albedo target while
material-table entries were explicitly Pending behind budgeted progressive
uploads; this was expected streaming state, not failed output. Once
`pending=0` and `uploading=0`, inspected normal output was textured and each
mode was byte-identical across initial Disabled, full Hi-Z, coarse Hi-Z, and
Disabled-return:

| Depth mode | Raw Albedo hash | Capture directories |
| --- | --- | --- |
| Normal | `7839E74F34608A956505F8D5B30EC7F6C19E779F60B2325AB12BCD5EB7931571` | `mcp-captures/gl-bindlessfix-initial-disabled-normal-settled`, `gl-bindlessfix-full-normal`, `gl-bindlessfix-coarse-normal`, `gl-bindlessfix-disabled-return-normal` |
| Reversed | `DC4FC159A20D70CB7644E1EFA59BC90ABEDF1D288528D6F6D01E017ED56751A6` | `mcp-captures/gl-bindlessfix-initial-disabled-reverse`, `gl-bindlessfix-full-reverse`, `gl-bindlessfix-coarse-reverse`, `gl-bindlessfix-disabled-return-reverse` |

All capture directories are below
`Build/_AgentValidation/20260830-124809-phase52-bounded-rendering/`. This
withdraws the earlier flat-image parity acceptance: it predates the repaired
bindless lifecycle and is not valid evidence about Hi-Z visibility.

### Reviewed lifetime prerequisites

The native-buffer repair now uses a buffer-only release-published epoch,
refreshes/persists only required keyed contexts during the existing preseal
context walk, and pins exact generations in the normal barrier tracking loop.
Generation zero cannot be omitted as an optional resource. Supersession is a
typed outcome (not reason-string matching), retried only within a bound before
the accepted packet is reset for a fresh frame; stable missing bindings remain
explicit failures. Review rejected and replaced earlier main-only and
all-historical-context refresh attempts. Final focused review and the Vulkan
leaf build passed with zero warnings/errors; live growth validation remains.

The OpenGL repair also passed focused lifecycle review and a zero-warning leaf
build plus an isolated Editor build with zero warnings/errors. Handle
acquisition starts ordinary budgeted upload work but stays pending until
registered progressive work and final mip parameters settle. Both normal and
runtime-managed progressive writers are covered. The final cold controls above
close this streaming prerequisite.

### MCP-only wrap-up (2026-08-30)

The user requested no desktop control. Only the named Vulkan session was
restarted, with the final reviewed Release binaries, initial Disabled occlusion
and explicit `GpuIndirectZeroReadback`; no window activation, restore, mouse or
keyboard automation was used. PID 22488 recovered from a cold upload-admission
failure (`Upload actual=4097 configured=4096`, exact texture-generation detail)
to completed native frames. This is not a zero-error cold-start claim.

The synthetic wall and box batches passed fresh completed-frame checkpoints
through box 64. Creation reached box 96, but its 30-frame checkpoint timed out.
The last native result remained Completed at sequence/frame 1368, while the CPU
render counter remained 2291; neither is evidence of subsequent progress.
There was no latched renderer-terminal failure. A five-second sampled trace
showed `PumpCollapsedWindowEvents`, `BlockForCollapsedWindowRendering` and
`IsWindowMinimized`, consistent with minimized rendering suspension, not proof
of a descriptor/lifetime failure. There is no supported headless/minimized
rendering override in the current editor. The session was stopped rather than
restoring its window against the user's constraint.

Evidence: `logs/vk-final-progress-stall.nettrace` and
`reports/vk-final-paused-profiler.json` under this investigation's scratch root.
The incomplete run does not close native-buffer growth, heavy/moving image
parity, Reverse-Z Vulkan acceptance or crossover calibration. No mode was
automatically promoted, no CPU fallback was added, and no tests were added,
modified or run. The completed GL capture was inspected again during wrap-up.
The 5.2 acceptance checkboxes and the 5.4 live-growth prerequisite stay open.

Final isolated Release Editor rebuild succeeded in 5.30 seconds with zero
warnings and zero errors; `git diff --check` passed. Build output is retained
in `logs/wrapup-editor-build.log`. Both investigation-owned GL/Vulkan sessions
are stopped. Normal editor outputs, root world settings and unrelated changes
were preserved; no commit was made.

### Repeated Vulkan acceptance and Phase 5.3 handoff (2026-08-30)

An additional isolated Release run (PID 171420, initial Disabled, explicit
`GpuIndirectZeroReadback`) completed three repeated cohorts in each depth mode.
Each cohort contains open-sky, moderate and foliage views, each captured under
Disabled/full/coarse Hi-Z after 30 fresh completed frames, with 20 distinct
completed primary-command-buffer GPU timestamps per case. All 54 image cases
match their same-depth, same-view Disabled control exactly. This supplies 1,080
fresh timing samples; the textured normal foliage result was visually inspected.

Whole-primary GPU medians in milliseconds (60 samples per cell):

| Depth | View | Disabled | Full | Coarse |
| --- | --- | ---: | ---: | ---: |
| Normal | Open sky | 0.72 | 1.29 | 0.92 |
| Normal | Moderate | 3.76 | 4.40 | 3.85 |
| Normal | Foliage | 3.27 | 3.77 | 3.59 |
| Reversed | Open sky | 0.74 | 1.06 | 0.92 |
| Reversed | Moderate | 3.53 | 4.43 | 4.12 |
| Reversed | Foliage | 3.27 | 3.93 | 3.44 |

Environment: RTX 3090, driver 610.88, 1920x1080, ZeroToOne clip depth (live
`ResolveEffectiveClipDepthRange(Vulkan)` returned 0). Offline calibration uses
input class 0 as this benchmark's class and pass -1 for the full primary, not
a measured native Advanced input classification. Both candidates in every
bucket fail the conservative 60-frame/three-paired-win, 100 microsecond and 5%
savings requirements: `Disabled / NoMeasuredWin`. This is a local measurement,
not an automatic selector policy or a portable performance claim. Normal and
reversed scene hashes differ; only within-mode parity is claimed. Open sky is
an intentionally empty albedo control, not textured-scene coverage.

Per-stage CPU dispatch costs and delayed GPU timings are available. Although the
current query often says Pending/Saturated, its separate last-completed result
is valid: filtering source frames against each case's ready frame and deduplicating
source/sequence gives 60 completed build and test samples per enabled mode/view/
depth bucket, no older than four frames. Last-completed generic build medians
range from 0.008544 to 0.010560 ms; test medians from 0.003360 to 0.008832 ms.
These are delayed per-scope observations, not a sum of all Hi-Z work in a frame.
The full-primary timing above is independent. Unavailable false-verdict/readback
counters are not measured zero false occlusions.

Reports: `reports/phase52-closeout-{normal,reverse}-{a,b,c}-parity.json` and
`reports/phase52-closeout-{normal,reverse}-crossover.json` under the existing
scratch root. An initial scratch evaluator mislabeled clip depth as 1; both
crossover reports were regenerated with the observed value 0 before acceptance.

The subsequent scene-growth attempt hid Sponza and created a synthetic wall,
then failed its 30-fresh-frame checkpoint. MCP remained responsive, but the
render counter held at 21169 and native terminal sequence at 20340 (Completed).
No renderer-terminal disposition or device loss was reported. A five-second
trace again sampled the collapsed render-host event pump; that is also a normal
host mode, so the stack alone does **not** prove a minimized window. Evidence:
`logs/phase52-closeout-growth-stall.nettrace` and
`reports/phase52-closeout-growth-stalled-profiler.json`. The run was stopped
through its named session manager, with no desktop control. Cold startup also
had a recovered upload-admission capacity error; zero-error startup is not
claimed.

Consequently the representative instrumentation and full Hi-Z acceptance gates
remain unchecked, including heavy/moving, final odd-extent and exact buffer-growth
coverage. The completed measurements above are retained rather than repeating
them. The first worker-owned upload slice continues separately in
`docs/work/progress/rendering/vulkan-phase53-worker-texture-preparation.md`.

The first Phase 5.3 worker-only build repeated all nine normal-depth cases with
the same hashes and 180 fresh GPU samples, after all 78 upload workers settled.
Its separate growth attempt reproduced the stop after wall creation. Newly
exposed owner-published window snapshots showed `isMinimized=false`, valid
1920x1080 extents and continuously advancing event sequences while render frame
7355 and native Completed sequence 6486 remained fixed. `get_time_state`
reported `isRunning=false`, not paused. This rules out the earlier minimized
interpretation for the reproduced failure: the engine timer stopped while the
window host continued pumping. Evidence is `reports/phase53-growth-stalled-state.json`.

Release `Debug.LogException` has its implementation behind `DEBUG || EDITOR`,
so the timer's catch-and-Stop paths can discard their exception without category
logs or console entries. A first-fault record and existing `get_time_state`
readback now retain that exception independently of those build symbols. The
next reproduction separates Sponza deactivation, selected box creation, transform
mutation and selection clearing to identify the exact failing operation; box
creation also creates selection gizmo renderables and is not a single geometry
mutation. Neither restarting a stopped timer nor controlling the desktop is an
acceptable substitute for identifying the fault.

The instrumented Release run (PID 39536; build 46.68 seconds, zero warnings and
errors) settled the same 78 worker uploads and passed an initial 30-frame cohort.
Deactivating Sponza alone then stopped the timer, before any primitive or gizmo
creation. The retained fault is `InvalidOperationException: Canonical scene
snapshot capture failed after a successful whole-frame preflight.` It originates
in `AdvancedGpuScenePublisher.Publish` at `Database.TryPreparePublication`, during
`DispatchSwapBuffers` on the collect thread. Requested/completed collect
generation was 2256; published/consumed was 2255. This is a canonical publication
failure, not a Hi-Z shader, minimized-window or imported-upload failure. Full
fault/phase evidence was read through `get_time_state.terminalFault`; the
repeated isolation report is replaced by each subsequent run.

### Canonical tombstone repair

The reverse-dependency manifest iterated physically occupied draw/material rows.
Tombstones deliberately preserve that physical storage until older publication
consumers acknowledge it, but final material release removes the logical layout
association immediately. Snapshot capture therefore mistook a safely retained,
logically dead material for a live dependency and failed its layout lookup.
Capacity preflight could not prevent that membership-contract error.

`AdvancedCanonicalReverseDependencyManifest.TryCapture` now skips exact
generation-noncurrent draw/material owners while still rejecting invalid occupied
handles and malformed live dependencies. Current draw edges also require current
material and geometry owners. ACK, physical retention, reclamation and prior
snapshots are unchanged; no fallback or early resource release was introduced.
Focused review and Runtime.Rendering build passed. The isolated Release Editor
build passed with zero warnings/errors in 36.35 seconds.

PID 60268 then passed eight 30-completed-native-frame checkpoints: initial,
Sponza deactivate, selected box create, box transform, selection clear, box
deactivate, Sponza reactivate and Sponza deactivate again. The timer remained
running with no terminal fault. The exact previously failing operation now
passes. Evidence: `reports/phase52-tombstone-fixed-isolation.json` and
`logs/phase52-tombstone-fix-build-launch.log`. This validates the logical
publication repair, not every physical native-buffer capacity-growth scenario.

The subsequent wall/256-box fixture completed all incremental 30-frame
checkpoints. Eighteen front/edge/overhead albedo cases (Disabled/full/coarse in
normal and reversed depth) matched per-view hashes, with 360 completed GPU
samples. The overhead capture was inspected and shows the wall and exposed box
rows. Sixteen boxes were then moved behind/in front of the wall and back. Depth
captures were used because matching gray albedo would not reveal an incorrectly
hidden foreground box. All 18 state/mode/depth comparisons match their same-state
Disabled reference and the foreground depth differs from the behind-wall depth.
Reversed-depth return is exact; normal-depth return differs identically across
all three modes, so exact normal restoration is not claimed. These are settled
motion/disocclusion checkpoints, not a continuous-animation or first-frame
visibility proof. Reports: `phase52-fixed-heavy-{normal,reverse}-parity.json`
and `phase52-disocclusion-{normal,reverse}-{behind,front,return}-parity.json`.

Changing the camera's internal resolution to 1279x719 through its existing
properties (without changing the desktop window) exposed another prerequisite:
native frames stayed `Deferred / ResourceGenerationBlocked`. The active
1920x1080 generation remained usable, but commit rejected the pending generation
because the `AutoExposureTex` descriptor payload changed before commit.
Restoring FullResolution resumed Completed frames. Evidence:
`reports/phase52-odd-resource-blocked.json`.

Source review identified a prepare/seal ordering violation: the backend captured
its immutable descriptor manifest before preserving auto-exposure history; that
copy initialized a fresh target's tracked layout from Undefined to General and
invalidated the captured snapshot. The narrow repair initializes history before
the final capture; strict commit validation, rollback and retention stay intact.
The rebuilt Release Editor (15.53 seconds, zero warnings/errors; PID 97372)
settled all 78 imported upload workers and committed the odd resource generation
successfully. All 18 open/moderate/foliage × Disabled/full/coarse × normal/reversed
depth cases matched their same-view/depth raw albedo control, with 360 fresh
completed GPU samples. The textured 1279x719 moderate capture was inspected.
Resource generation advanced 1 → 2 → 3 through full → odd → full resolution;
every checkpoint had fresh Completed native frames and no timer terminal fault.
Evidence: `reports/phase52-history-order-odd-extent.json`,
`reports/phase52-fixed-odd-{normal,reverse}-parity.json`, and
`logs/phase52-history-order-build-launch.log`. This validates the prepare/seal
repair and odd-extent mode parity, not exact exposure-value continuity under
animated lighting or asynchronous swapchain retirement.

### Instrumented-route readback clarification

The earlier hot-switch only changed the environment override and reloaded its
cache. The profiler's global resolver changed to Instrumented, but the active
command chains retained ZeroReadback; `occlusion.submission_strategy` and
`two-phase-current-depth` correctly exposed the recorded route. This was not
evidence of a broken count readback. Applying the existing
`XREngine.EngineRenderingSettingsApplication.ApplyGpuRenderDispatchPreference`
after reload rebuilt those chains. After 30 fresh Completed frames, the recorded
route was `GpuIndirectInstrumented`, readback was available and the separate
on-demand counter reads reported 14 candidates, 0 occluded, and 1 pass with
readback. The reads are not an atomic per-frame cohort or a false-verdict oracle.

The instrumented path is single-phase, unlike production two-pass zero-readback;
these observations cannot certify production verdict rates or crossover. Its
capture is retained but not asserted byte-identical to the zero-readback control.
The explicit strategy was reapplied to ZeroReadback afterward and fresh native
frames verified. A final full-resolution reversed-depth foliage cohort matched
Disabled/full/coarse within that cohort, with 60 completed GPU samples. Its raw
hash differs from the earlier process's cohort (camera forward components also
differ at floating-point precision), so cross-run exact restoration is not
claimed. Evidence: `reports/phase52-instrumented-applied.json` and
`reports/phase52-final-full-return-reverse-parity.json`.

### Presentationless acceptance repairs (2026-08-31)

The remaining acceptance work now runs through `XREngine.RenderBench` with no
window, desktop compositor, editor session, or desktop control. The three cold
visibility lanes establish eligible image candidates (E), disabled-occlusion
visibility (V), and the submitted production early/late GPU stream union (K).
Readbacks occur only after their exact submission completes and never affect
renderer visibility. These are correctness measurements, not zero-readback
performance measurements.

RenderDoc identified the normal-depth post-cut false occlusion and reversed-depth
keep-all result as the same resource-identity bug: early rasterization wrote
depth resource 1637 while Hi-Z compute sampled an untouched resource 1867 from a
different physical planner generation. Normal depth interpreted its zeroes as
near; reversed depth treated them as far. The sealed plan now freezes its
per-context physical generations once, before workers run. Graphics preparation,
descriptor preparation, serial recording and compute consume that same map,
with balanced thread scopes and rejection of unprepared or superseded maps.
The corrected capture proves rasterization and compute both use resource 1867.
The initial corrected 1024x576 normal-depth run preserves the visible sentinels
and removes hidden candidates after the cut instead of producing a black frame.

The subsequent 1279x719 matrix completed 288 frames without false occlusion or
missing visible output, but correctly failed positive-cull acceptance: the thin
projected wall covered only three complete 64-pixel coarse tiles. Most candidate
footprints touched background tiles and conservatively remained visible. The
fixture wall height is now eight world units, with unchanged width and exposed
sentinel, to exercise a genuinely covered coarse-tile interior. This is a new
input identity; the earlier failing matrix is not relabeled as passing.

Representative workloads add open, moderate, heavy, moving/cut and genuinely
alpha-tested geometry. The masked fixture uses texture-backed alpha holes and
an identical opaque control; changing an opaque material's cutoff alone was
insufficient and is not accepted as masked coverage. Optional timestamp samples
retain their source frame and availability, and renderer-authored invalidation
flags distinguish real camera-cut bypass from an inferred scripted event.

Native-lifetime acceptance additionally exercises growth after logical seal,
rejection before acquisition, a fresh retry, growth after native recording,
pending queue ownership, and eventual reclamation after slot reuse. Replacing
a buffer also invalidates the exact dependent descriptor owners through normal
retirement; removing native pins or force-destroying old allocations is not an
acceptable shortcut. The pre-acquire validator must inspect the immutable keyed
plans actually used by recording, not an unused root fallback graph.

The warm clear-frame regression was traced to discarding command-buffer tracking
batches on every successful reset, then allocating their pre-sized collections
again on the next begin. Completed resets now clear payloads while preserving
capacity; failed resets preserve ownership and destruction still removes the
batch. Final integrated acceptance and allocation results are recorded below
only after fresh runtime validation.

The expanded unmasked matrix (`reports/phase52-representative-v1`) passes all
16 open/moderate/heavy/moving-cut cohorts at 1279x719, across both depth modes
and two cold repeats: 1,152 completed frames, zero false occlusion or missing
visible candidates, and 576 repeat-frame comparisons with zero mismatches.
The masked lanes fail first-frame texture readiness in that build; therefore
the aggregate matrix remains failed and is not final Phase 5.2 acceptance.

Enabling standard and synchronization validation exposed three additional
defects that pixel parity alone did not find. Presentationless device creation
now omits optional presentation extensions whose required swapchain/instance
extensions are absent. Planned image barriers now reconcile their producer
stage/access scope, not only their old layout, with exact command-recorded
subresource state; this removes write-after-write hazards from intervening
dynamic-rendering stores. Finally, target timestamp polling requires a slot
whose target timestamp reset/write was actually recorded and submitted;
production frames use a different recording path and must not query untouched
component timing pools. The fresh normal-depth 24-frame run
`reports/phase52-validation-normal-v5` has both validation modes and the debug
messenger active, zero native errors, and no diagnostic overflow. Its two
loader warnings are retained separately, not reported as zero warnings.

The masked investigation then proved a generated-shader defect rather than
merely a fixture upload problem. `OpaqueDeferred` was not an explicitly masked
pass, so the zero-readback fragment generator omitted alpha discard even when
the individual material was masked. A named coverage bit in the existing
material flags now selects per-row cutoff in deferred color, depth/normal and
motion paths without changing the record stride or classifying all opaque
materials as masked. The generated table shader samples albedo alpha; it does
not execute the source material's separate `Texture1.r` mask shader. The accepted
fixture therefore uses the canonical RGBA albedo alpha contract, with a real
hole and identical opaque control. First-frame preparation creates its wrappers
through the owning renderer and publishes verified texture references before
collection, without dummy frames.

`reports/masked-coldprep-v6/matrix` passes 12 children across normal/reversed
depth, two cold repeats, and eligibility/disabled/Hi-Z lanes. All 144 frames
complete. The cutout image has 67,817 white border pixels and 39 adjacent green
target pixels; the opaque control hides the target, and later Hi-Z frames remove
hidden candidates without false occlusion or missing output. PNGs were inspected.
This is the static masked slice; integrated moving coverage follows below.

Source review also found that auxiliary color readback resets/reseals the same
primary command buffer. Production receipt authorization now keeps an
independent amortized resource vector, so reuse cannot mutate its proof. The
harness re-reads the production count after color readback to exercise that
invariant. Generic seal invalidation never releases storage for reuse; only a
successful completion-validated native reset does. The remaining warm-frame
allocations were an interpolated tracking-owner string and repeatedly allocated
reverse-dependency sets. A tracker-owned empty-set pool preserves exact reverse
index removal and returns storage only under the lifetime lock. The original
warm deterministic-clear gate reaches zero captured allocations; final-build
rechecks remain required after integration.

### Native buffer-growth closeout (validation-enabled 4096x4096 repeat)

The buffer-only production lane now closes its bounded native-growth proof at
4096x4096 for both normal and reversed depth with
`XRE_VULKAN_VALIDATION=1` and `XRE_VULKAN_SYNC_VALIDATION=1`. Both child
reports pass with zero reported native validation errors:

- `reports/phase52-buffers-4096-normal-validation/scenario-result.json`
- `reports/phase52-buffers-4096-reversed-validation/scenario-result.json`

The earlier retained old `LateDrawIds` allocation came from `VkDataBuffer`
growth retiring through `VulkanBufferResourceService`, bypassing the
runtime-only superseded-descriptor-owner queue. Exact buffer-generation
retirement notification now belongs to `VulkanLifetimeAuthority`, so both
retirement paths feed the same normal descriptor-owner drain without releasing
recorded or queued pins.

Reclamation is proven through ordinary slot rotation. The lane uses the bounded
`2 * FrameSlots + 1` ordinary submissions (seven for the three-slot fixture),
not forced cleanup or a GPU hold. The reports retain exact C-1/C/C+1 capacity
growth, receipt identity, real timeline overlap, sealed-logical rejection/retry,
and old-generation reclamation after completion. Loader duplicate warnings, if
emitted, are loader messages rather than renderer VUID findings; reported native
validation errors are zero.

This closes only the native buffer-growth sub-proof. Master Phase 5.2
acceptance remains open while moving-mask settled-tail work continues.

### Headless Phase 5.2 acceptance closeout — 2026-08-31

The remaining bounded acceptance gates now pass, without an editor window or
desktop control. The retained evidence is deliberately not rewritten to hide
earlier failures:

- `reports/phase52-final-visibility/scenario-result.json`: 72 completed children,
  exact 864/864 cold-repeat frame comparisons and zero false/missing visibility.
  Its aggregate remains failed because the original continuously moving mask
  trajectory never allows history to become valid. The other five workloads
  pass all four cohorts (1,440 completed frames); moderate, heavy/static,
  heavy/moving-cut and masked/static demonstrate real hidden-candidate culls.
- `reports/masked-moving-settle-v1/matrix/scenario-result.json`: the corrected,
  separately identified v5 trajectory passes all twelve children (288 frames),
  with 144/144 exact cold-repeat comparisons. Each cutout/opaque half retains
  motion plus four settled frames. All four cohorts have zero false occlusion
  or missing visible output and four demonstrated hidden-candidate culls each.
  Alpha-zero holes reveal the green target; the opaque control hides it.
  No production invalidation policy or positive-cull assertion was relaxed.
- `reports/phase52-final-default/scenario-result.json`: original moving/cut
  fixture passes twelve children (288 frames), 144/144 exact cold comparisons,
  and five demonstrated culls per cohort with zero false/missing visibility.
- `reports/phase52-final-validation-reversed/scenario-result.json`: masked
  moving production lane, standard and synchronization layers enabled, active
  messenger, zero errors. Together with the normal production validation and
  both validation-enabled 4096² buffer runs above, this verifies the repaired
  native barriers, query lifecycle, and dependency retirement paths. Two local
  loader duplicate-layer warnings are separate from renderer VUID errors.
- The final original deterministic-clear allocation report is
  `reports/headless-original-clear-final-allocation/profiles/` (the
  `deterministic-clear-2c7528d7c199407594595f3639b96261` run): zero captured
  managed allocation bytes after warmup. Isolated RenderBench and Editor
  builds pass with zero warnings/errors. No automated tests were added or run.

The passing representative and original-fixture cohorts total 2,016 frames.
This closes Phase 5.2 and its exact-buffer Phase 5.4 prerequisite, not later
whole-engine performance, full post-processing, Advanced shading, or XR gates.
Current-frame readbacks remain diagnostic-only; the established calibration
still selects `Disabled / NoMeasuredWin`, with no automatic/default promotion.
