# Advanced Pipeline Antialiasing TODO

Last updated: 2026-09-22  
Owner: Rendering  
Status: TSR mesh output observed; material-publication stall blocks mode-image checks and quality sign-off

## Goal

Make TAA, TSR, MSAA, and DLAA visibly and correctly affect meshes rendered by
the Advanced pipeline on the supported OpenGL and Vulkan paths. Finish the
in-progress work in the current worktree; do not treat a selected AA profile or
a successful build as proof of a correct frame.

The [investigation](../../investigations/rendering/2026-09-22-advanced-pipeline-antialiasing.md)
contains the original diagnosis, implementation notes, runtime observations,
and evidence paths. This document tracks only the remaining work and its exit
criteria.

## Implemented baseline

- [x] TAA/TSR: publish temporally jittered native authoring views after temporal
  Begin, correct perspective jitter direction, and account for current/previous
  jitter in history lookup.
- [x] TAA: declare the accumulation framebuffer with its actual HDR and
  exposure-variance outputs.
- [x] DLAA: connect the Advanced output chain to the existing vendor command
  with final color, depth, motion, and exposure inputs.
- [x] MSAA: add raw multisample visibility, metadata, selection, depth, and
  sample-position resources; per-sample native shading; a coherent visibility
  and depth resolve; coverage-aware HDR composition; an initial background
  blend; and OpenGL and Vulkan execution plumbing.
- [x] Targeted OpenGL and Vulkan project builds, native shader compilation,
  and the final isolated editor builds passed with zero warnings and errors.

## Remaining work

### 0. Restore native execution before AA quality validation

Priority: diagnose and repair the execution blockers below one at a time, then
complete section 0b's temporal fixes and the controlled AA comparisons. A
stage-rejected or stale frame cannot establish whether antialiasing works on meshes.
Read-only temporal analysis can continue independently; do not combine backend
lifetime recovery and temporal shader changes in one unvalidated patch.

This extends the open checklist included in `236250e86` (camera/editor,
framerate, and AA work). That commit also retains **S12 Active** in the
[stall-remediation TODO](vulkan-stall-remediation-todo.md#s12-improve-shared-advanced-preparation-safely).
Use its existing lifetime/retirement contracts and one-change validation gates;
these observations do not prove that S12 or the latest commit caused a regression.

#### AA-B1. Vulkan visibility snapshot lease exhaustion — Fixed; live paused-path verified

Observed in the follow-up Debug Vulkan/Advanced/Sponza run, including repeated
messages from 19:14:19 through 19:15:26 on 2026-09-22:

> The bounded advanced visibility authoring lease arena exhausted its 16 concurrent families.

The first decisive failure was a terminal PresentNow pause at frame 3157:
the compact advanced-scene image requested 645,072,032 bytes per frame slot
against the explicit 134,217,728-byte limit. Before this point, transient
canonical texture `SourceMismatch` rejections occurred, but those return before
lease acquisition. While PresentNow was paused, subsequent CPU render frames
continued authoring receipt-bound scene operations. The normal skipped-frame
drain deliberately excludes operations with an output-completion receipt, so
each new render-frame identity retained another immutable input lease without
a consumer. Sixteen such families exhausted the arena. This was a missing
paused-path retirement, not evidence that the arena needed more slots or that
the historical static-deformation capture bug returned.

- [x] Add an atomic paused-scene discard in `VulkanFrameOperationQueue`: settle
  scene operation snapshots, submission markers, output-completion fences, and
  frame-view candidates while retaining independent texture uploads.
- [x] Invoke that discard on first terminal/recoverable pause and on each
  subsequent tick rejected before a recovery probe. Discard stale queued mesh
  requests at the same boundary so a later probe authors a fresh cohort.
- [x] Validate with the isolated Debug Vulkan/Advanced/Sponza session
  `aa-b1-0922`. The same scene-image limit paused PresentNow at frame 3565;
  37 later rejected frames each discarded 7–19 stale operations, and no lease
  exhaustion appeared. The Vulkan project and isolated editor builds completed
  with zero warnings/errors. Evidence:
  `Build/_AgentValidation/20260922-200000-aa-b1/`.

The lease failure is resolved for the reproduced paused path. The viewport
remained background-only from two camera positions because the independent
scene-storage capacity failure prevents Sponza submission. Fresh Sponza color
and AA mode-transition verification therefore remain open under AA-B1b.

#### AA-B1b. Vulkan Sponza scene-publication capacity — Capacity fixed; TSR mesh output observed

The measured 645,072,880-byte compact image contained 644,408,192 bytes of
immutable geometry and only 664,688 bytes of frame-owned records. Seven
geometry streams now use `VulkanAdvancedGeometryResidentCache`, shared across
frame slots and keyed by database epoch plus arena handle/generation. The
128 MiB frame-scene ceiling and 1 GiB frame-arena guard remain unchanged.

- [x] Measure exact used bytes and distinguish geometry from reserved capacity.
- [x] Move immutable geometry out of per-slot packing. Preserve append-only
  prefixes, slot pins, failure rollback and tracked retirement. Bound each
  image to 1 GiB and live-plus-retiring cache allocations to 2 GiB; include
  alignment/growth capacity and device descriptor-range limits in admission.
- [x] Build and exercise the original private Vulkan/Advanced/Sponza scene.
  The final isolated build had zero warnings/errors. Sponza's 543,568,256-byte
  vertex image uploaded once into a 612,368,384-byte bank; the five nonempty
  geometry banks plus retained zero sentinels peaked at 738,197,616 bytes.
  There were no additional geometry allocations through the observed later
  frames. Native scene publication admitted 465 draw records without the
  previous frame-storage terminal failure or visibility lease exhaustion.
- [x] Recheck mesh output from a useful camera distance. The second named
  Vulkan/Sponza run produced nonzero visibility, depth, HDR and a full-size TSR
  output; close viewport captures visibly contained a large Sponza facade.
  The earlier distant camera made the model occupy only a tiny area.
- [ ] Complete TAA/TSR/MSAA visual transitions. The active camera override
  committed TSR-to-TAA, but material-publication capacity then froze the
  canonical scene at sequence 207, and the TAA readback was black. The editor
  closed before MSAA. See AA-B1c and the investigation for frame evidence.

Evidence and full byte breakdown: [investigation](../../investigations/rendering/2026-09-22-advanced-pipeline-antialiasing.md#aa-b1b-shared-geometry-residency-2026-09-22).
No tests were added or modified while feature validation remains open.

#### AA-B1c. Vulkan scene-publication retention and resource transitions — Pending

AA-B1b removed the frame-scene capacity failure. The first distant captures did
not clearly establish mesh color, but a second close run exposed visible Sponza
mesh output and nonzero TSR inputs/output.
At sampled frame 3732, `VisibilityPreparation` rejected `SourceMismatch` when
`stone_01_tile_BaseColor` changed from the retained 8x8 image to 64x64 during
streaming. Frame 3763 later accepted every native stage, but both inspected
images were still background-only. Backend enqueue acceptance is insufficient
GPU/output proof. Frame 3732 also reported an `XRFrameBuffer` wrapper requested
before CPU construction was published; later frames resumed. The TAA setting
read back correctly while the active generation still described TSR. Do not
label requested-mode captures as proof that the mode became active.

- [ ] Diagnose the newly observed material-table publication limit. At frame
  3622, publication sequence 207 was frozen with `The canonical material tables
  cannot accept the complete ownership transition.` Retention showed an old
  sequence-133 GPU pin and minimum reclaimable sequence 132. Determine which
  table's row, journal, remap or tombstone capacity failed and why the pin
  survived; preserve old-output safety while allowing new material/texture
  publications. The resulting stale 8x8 source caused repeated `SourceMismatch`
  rejections as Sponza textures streamed to 4096x4096.
- [ ] Correlate exact canonical texture publications, native source stability,
  stage acceptance, submitted/completed output receipts and exported geometry,
  visibility/depth, HDR and final targets. Use RenderDoc if logs and MCP target
  captures cannot establish current-frame output and per-sample contents.
- [ ] Diagnose the framebuffer construction/publication race independently of
  texture streaming; verify recovery without using incomplete resources.
- [x] Identify the effective AA setting owner and verify generation commit. The
  Unit Testing World camera override masked `set_game_setting`; changing the
  active camera override committed `aa:Tsr->Taa` at 21:12:56.
- [ ] After scene publication resumes, rerun two-viewpoint TAA/TSR/MSAA
  captures and moving/cut comparisons. A committed TAA resource generation alone
  does not validate a black, stage-rejected TAA image.
- [ ] Establish why the isolated editor exited before further mode validation.

This is an execution/output gate before AA motion-quality signoff; do not
replace it with a larger scene-storage limit or a silent fallback.
#### AA-B2. OpenGL native stage rejection — Pending

Observed repeatedly during the same follow-up's OpenGL run, including native
shading rejection at sampled frame 15113:

> The OpenGL Advanced stage does not match its sealed family or required per-view order.

`OpenGLRenderer.AdvancedVisibilityExecution.TryEnqueueAdvancedVisibilityStage`
checks active family identity and the expected per-view operation before
executing a stage. Its generic message can be a downstream consequence of an
earlier preparation failure that aborted the family. It is not enough evidence
to conclude the command chain itself is misordered. Cold shader compilation and
readback timeouts were also present in this run.

- [ ] Capture the first rejected/aborted operation, actual versus expected
  stage/phase/view index, active-family state, reservation, and resource/source
  generation. Correlate later rejections with that first cause.
- [ ] Separate pending shader/resource admission during startup from failure
  after required programs are ready. Record readiness, completed-frame progress,
  and abort/retry behavior rather than treating elapsed time as warm-up proof.
- [ ] Exercise None/TAA/TSR/MSAA transitions. Check conditional multisample
  resolve ordering and active-generation pass metadata; compare against the
  earlier repaired MSAA rejection without assuming it is the same defect.
- [ ] Fix the earliest proven failure and verify that a rejected family can
  recover on a subsequent frame. Preserve operation-order and identity checks.

Exit: a completed native stage sequence produces fresh mesh color after startup
and each transition, with matching generation/view identity and no repeated
stage rejection after readiness. Verify screenshots and backend completion
evidence, not only the profile's Admitted/Bound status.

#### AA-B3. OpenGL AA resource publication progress — Pending

At closeout frame 15137, the requested MSAA generation was still pending while
the active generation remained TSR at 1286x723. An earlier materialization
snapshot showed active 199/199 resources and pending 38/205. The capture labeled
`gl-msaa-capture` therefore contains the old TSR resources, not a validated MSAA
frame. This proves an incomplete transition during the observation window;
it does not establish a permanent deadlock or a separate root cause from AA-B2.

- [ ] After AA-B2's readiness/abort diagnosis, follow one requested generation
  through materialization, activation, and old-generation retirement. Record
  spec progress, queued work, owner-thread service, compilation state, pending
  reason, and the first failed prerequisite across successive frames.
- [ ] Determine whether publication is merely delayed by cold work or cannot
  progress after readiness. Reuse the existing resource manager and the
  stall-remediation S03/S06 machinery; add no parallel publication system.
- [ ] Repair the demonstrated progress dependency if needed. Confirm that
  superseded requests are cancelled/retired correctly and failed generations
  expose a specific reason rather than silently appearing active.

Exit: each requested AA profile becomes the active physical generation with its
correct extents and sample count, or reports an explicit terminal unsupported/
failure result. Rapid mode changes and resize leave no unbounded pending work.
Successful modes render fresh meshes before their AA quality is assessed.

Evidence and reproduction settings are indexed in the
[follow-up investigation](../../investigations/rendering/2026-09-22-advanced-pipeline-antialiasing.md#follow-up-quality-verification-2026-09-22).
Keep these three items open until their individual live gates pass. No tests or
renderer changes are authorized merely by writing this checklist; apply the
repository's feature-validation and explicit test-clearance rules.

### 0b. Correct temporal inputs after execution recovery

- [ ] Convert native NDC velocity and temporal jitter displacements into the
  framebuffer texture coordinate convention before TAA/TSR history lookup.
  The default Vulkan Y-up clip / Y-down texture combination currently uses
  the wrong Y sign. Validate horizontal and vertical motion independently.
- [ ] Keep previous-frame depth until TSR resolves. The non-TAA temporal
  passthrough currently copies current depth into history before TSR reads it.
  Commit depth with the corresponding TSR color history after resolve.
- [ ] Correct DLAA/DLSS vendor motion direction. The native buffer encodes
  current-minus-previous NDC, while DLSS needs current-to-previous displacement;
  both native and bridge dispatch currently supply a positive scale.
- [ ] Remove the duplicated X jitter stratum and verify zero-mean, full-cycle
  sample coverage. Assess TSR's small jitter range with static subpixel edges
  at multiple render scales before changing reconstruction tuning.
- [ ] Propagate frame-view history invalidation into TAA/TSR readiness for
  projection/FOV changes and explicit camera history resets.
- [ ] Make closest-depth velocity selection respect normal/reversed depth in
  both mono and stereo temporal shaders.

See the follow-up section of the investigation for source traces and runtime
limitations. Earlier checkmarks below describe the earlier execution evidence,
not clearance of these newly identified defects.

### 1. Correct the render-graph contract

- [x] In `VPRC_AdvancedRenderStage.DescribeRenderPass`, select pass declarations
  and resource uses from the active resource layout/generation. The prior graph
  declared raw accesses and a synthetic resolve even for non-MSAA profiles;
  raw multisample resources are now described only for MSAA.
- [x] For non-MSAA, declare canonical early/late visibility writes, omit the
  raw resolve and raw native-shading reads, and depend AO/classification on
  late raster directly. For MSAA, declare raw early/late writes, resolve into
  canonical targets, then depend downstream work on the resolve. Do not claim
  canonical writes in an MSAA raster pass that only writes raw attachments.
- [x] Check graph descriptions before and after an AA mode change without
  assuming the command chain is rebuilt. Resource invalidation currently does
  not rebuild the command chain. The active generation now supplies profile-
  specific pass metadata; live MSAA, None, and TAA switches showed the expected
  raw/canonical resources and temporal passes.

Exit criterion: each effective profile describes only passes and resources it
executes; Vulkan planning has no non-MSAA phantom resolve or false producer.

### 2. Make MSAA execution and background composition correct

- [x] Rebuild and rerun OpenGL MSAA. Diagnose the last live rejection:
  `MultisampleResolve` did not match the sealed stage family or required
  per-view order despite an admitted `aa=Msaa msaa=4` profile. Separate shader
  startup/admission timing from a persistent command-order defect.
- [x] Verify four-sample raw color/depth attachment descriptors, the active
  graph's raw-raster-to-resolve-to-AO dependencies, and changed mesh-edge
  coverage in both backends against AA-off captures. The canonical HDR target
  has intermediate coverage values at edges that are black with AA off.
- [ ] Capture raw per-sample visibility and depth plus the canonical resolve in
  RenderDoc to independently confirm sample contents and nearest-sample
  sidecars. MCP cannot read multisample textures; the manual RenderDoc trigger
  disconnected before writing a capture.
- [ ] Reproduce or rule out the `without metadata: 100065` warning seen in a
  direct RenderDoc-launched MSAA run. The later isolated Vulkan session showed
  no repeat through MSAA/None/TAA/MSAA, so its cause is not established.
- [x] Define and enforce background source alpha. Inspected skybox shaders
  previously output `vec3`, while destination-alpha blending requires alpha
  one to close uncovered pixels. Admitted sky shaders now output opaque alpha,
  and the background path has an explicit multiple-draw policy.
- [x] Align background material validation with the actual subdraw material.
  The earlier validation examined submaterials while cached render state came
  from the primary material; the selected policy now prevents that mismatch.
- [x] Track sample count and fixed-sample-location mode in OpenGL multisample
  texture allocation identity so a surviving wrapper cannot retain storage
  from a different MSAA profile.
- [x] Rebuild and validate the final Vulkan implementation. The corrected
  frame completed with four-sample targets and visible edge coverage; the
  earlier binding-55 validation failure did not recur.

Exit criterion: OpenGL and Vulkan each render Sponza with four-sample mesh
coverage, no resolve-order or admission errors, coherent visibility/depth
sidecars, and correct background pixels at partial coverage.

### 3. Prove TAA, TSR, and DLAA visually

- [x] On OpenGL and Vulkan, capture Sponza still views and camera motion with
  TAA and TSR. The native temporal view receives current jitter, history is
  ready after settling, and scene color remains fresh across tested mode
  transitions. Compare antialiasing quality and fast-motion ghosting separately
  before claiming visual parity or production quality.
- [x] Recheck and fix the TSR-to-TAA black/stale-color anomaly. Temporal pass
  declaration now uses the active profile, and pass-index lookup uses active-
  generation metadata. Live Vulkan TSR-to-TAA produced nonblack matching color
  input, HDR output, and history; OpenGL did likewise.
- [x] Confirm DLAA invokes the vendor path and produces a valid final image on
  supported hardware. OpenGL on the available NVIDIA setup evaluated NGX DLAA,
  survived two off/on transitions, and presented a nonblack Sponza image.
  Preserve explicit diagnostics when the vendor path cannot run.
- [ ] Establish Vulkan DLAA availability on a configured vendor-capable setup,
  or document the backend's explicit unsupported result. The live Vulkan
  session did not exercise a vendor resolve.
- [x] Check mono behavior in the isolated desktop editor on both backends.
- [ ] Check ordinary stereo behavior on an XR runtime. Quad-view temporal post
  state still has only left/right eye slots; establish its supported behavior
  before claiming quad-view parity.
- [ ] Run a controlled still/moving/cut comparison of final TAA, TSR, MSAA,
  and DLAA mesh-edge quality, including ghosting and background partial
  coverage. The current captures prove execution, fresh output, and MSAA edge
  coverage but are not a full quality assessment.

  The 2026-09-24 Advanced Vulkan TSR changes improve mean stationary edge
  stability, but localized thin-edge shimmer remains at 0.67 scale; temporal
  variance is essentially unchanged and occasional edge spikes remain. Quality
  signoff is still outstanding. User-scene confirmation, dense consecutive-frame
  motion/disocclusion captures, live OpenGL/stereo validation of these changes,
  and dense-geometry GPU timing remain open under
  [ARP-V17](vulkan-xr-and-advanced-rendering-todo.md#motion-history-and-reset-matrix).
  See the [measured results and limitations](../../investigations/rendering/2026-09-24-advanced-vulkan-tsr-jitter.md#final-coverage-validation).

Exit criterion: before/after captures show that each mode affects mesh edges as
intended, with stable scene color, camera motion, and mode transitions. Record
the actual backend, profile, frame state, captures, and observed limitations in
the investigation.

### 4. Final verification and closeout

- [x] Run the narrowest integrated editor build and live editor path after the
  fixes above. Review rendering/OpenGL/Vulkan logs for persistent validation
  errors and warnings; distinguish startup shader compilation from steady-state
  frame failures. Named live sessions settled and rendered; see the
  investigation for the one Vulkan startup framebuffer error and the separate
  unsuccessful RenderDoc launch.
- [x] Update the investigation with the current result for each AA type and
  backend, including any unsupported hardware or view configuration.
- [ ] Only after live feature validation and the user's explicit clearance,
  add or update regression tests. Review the existing test expectations that
  assume TAA native jitter is absent.

Use a named isolated editor session for the live checks and stop only that
session when done. Keep disposable captures and logs under
`Build/_AgentValidation/`; the 2026-09-22 evidence root is
`Build/_AgentValidation/20260922-111614-advanced-aa/`.
