# Vulkan Stall Validation With Monado

Status: active. No cumulative acceptance is established.

## Scope And Baseline

The user requested completion of the remaining stall work and selected Monado
for all OpenXR validation. Use the
[remaining worklist](../../todo/rendering/vulkan-stall-remediation-todo.md) and
[validation protocol](../../testing/rendering/vulkan-stall-validation.md).
Keep one implementation change active at a time.

The first control uses source `71ccb6a4f`, Release x64, and the named isolated
editor session `stall-monado-baseline`. Its build passed with zero warnings
and errors. Evidence is under
`Build/_AgentValidation/20261005-130013-vulkan-stall-completion/`.

The private settings copy preserves the current authored Sponza fixture:

- Sponza2 OBJ, deferred materials, scale 0.01, translation (-20, 0, 0).
- One directional light with shadows, skybox, and model-grid light probes.
- Vulkan required, Advanced rendering, CpuDirect submission, and TSR.
- Strict SinglePassStereo, independent desktop rendering, eye previews, and
  runtime-recommended eye dimensions at scale 1.

The earlier desktop throughput fixture used Uber materials. This control is
for current OpenXR failure diagnosis. It is not a matched throughput comparison
with that historical fixture. Record the resolved runtime configuration before
accepting measurements.

Monado is selected only in the owned editor process. The system runtime setting
is unchanged. The task first started its own Monado service. The editor's
Monado bootstrap replaced that service to apply its display profile. The
replacement inherited the editor environment and used the default moving pose.
The diagnostic restart therefore sets `SIMULATED_HMD_POSE_MODE=stationary` in
the editor's session environment. Both service launches belong to this task.
Simulated runtime evidence does not establish physical-headset comfort or
vendor-runtime acceptance.

## Admission Investigation

Entry condition: the prior hardware run repeatedly reported
`No compatible Vulkan render program is available yet` after Play entry.
That message comes from mesh program preparation, before graphics-pipeline
manifest admission. Measure both boundaries before selecting a fix.

The source has two candidate progress mechanisms to inspect:

- Mesh materialization uses one resume cursor across output cohorts.
- Graphics-pipeline admission resets its cursor when the exact manifest changes.

Neither mechanism is a confirmed cause yet. Preserve exact compatibility,
bounded preparation, frame deadlines, and complete eye output.

### Current-Source Runtime Results

Monado reports `XR_KHR_vulkan_enable2`, Vulkan, effective SinglePassStereo,
and `TrueSinglePassStereo`. Both swapchains are 896 by 1007 with three images.
No sequential fallback was attempted. These dimensions differ from the earlier
2688-square physical-headset run; this is failure diagnosis, not a matched
memory or throughput acceptance run.

The first run reached 56 submitted eye frames. Submission then stopped while
no-layer frames increased from 124 to 265. Left and right captures retained
preview frame 14208. Moving the playspace from the initial position to
(-20, 2, 4), then (-15, 3, 2), did not refresh that image. Viewed captures show
black geometry, magenta stripes, and incorrect lighting. The desktop capture
shows black scene output and solid-magenta eye-preview panels.

The stationary diagnostic restart used the same binary with
`XRE_VULKAN_RECORDING_DIAG=1`. It reached 74 submitted frames before the interior
camera move. The bounded failure log then recorded 51 empty-operation samples,
12 recording exceptions, and one reservation failure. These are sampled
counts, not per-frame totals. Mesh preparation progressed from request 90 of
304 to request 142 of 304, then reached graphics-pipeline admission. Empty
operation capture explains the later plateau more directly than the initial
mesh-program message. Generated-program supersession stayed at zero in the
saved cumulative counters; eviction is not the selected cause.

Play entry and exit completed. After Play, submission increased slowly to 89
while no-layer frames reached 377. The saved eye capture remains visually
incorrect. The subsequent orderly OpenXR exit completed one normal teardown:
665 image acquisitions and releases per eye, one retired generation queued and
drained, zero pending generations, and no device loss. This is one scoped
teardown pass, not full lifecycle acceptance.

### Diagnostic Increment

The next binary adds evidence without changing admission rules. Existing
graphics-pipeline deferral messages include exact manifest identities, cursor
progress, pending requirements, output identity, and elapsed slice time. The
bounded empty-operation trace includes the actual stereo pipeline's decline
reason and resource generations. Successful warmed paths do not format these
diagnostic strings.

A separate RenderDoc run saved `renderdoc/baseline-eye.rdc`. Loading RenderDoc
caused automatic XR captures and added substantial overhead. Its timing is
excluded from performance claims. The normal MCP exit request timed out in
that run; the named session manager stopped the owned editor. The selected
capture was copied into the task directory, and its disposable automatic
capture files were removed.

The offline capture contains 12 actions and no draws, dispatches, clears, or
render passes. Its only copy uploads a 64-square stone texture. The exported
texture was viewed. This capture does not contain an accepted stereo scene and
does not identify a shader or stereo-layer fault.

The probe build passed with zero warnings and errors. It reached 118 submitted
frames while no-layer frames reached 602. The steady empty-operation samples
report `CollectGenerationMismatch`. The Rvc resource generation is ready and
has no pending replacement. The early graphics-pipeline sample shows cursor
progress from one to two without a manifest reset. Thus, collection-generation
ownership is the next cause to inspect; pipeline cursor starvation is not yet
established.

After the interior camera change, submissions increased only to 121 while
no-layer frames reached 828. Acquisitions and releases remained balanced at
947 per eye. Left and right captures from different camera poses both retained
frame 171603. Viewed images show black geometry, a thin magenta line, and bright
environment content. These remain failed freshness and visual results. The
named probe session was stopped before the next source change.

### Collection Authority Correction

The source review found a generation-domain error. OpenXR collects once for
each pending XR frame. Engine collection and consumption can advance several
times before that XR package renders. The strict stereo path passes no XR
consumption authority, so package validation compares its generation with the
unrelated desktop consumption generation. The existing per-eye path already
captures and revalidates the exact XR package authority.

The selected correction gives strict stereo the same guarded entry. It checks
the command collection, published state, package generation, and collection
generation before it supplies the XR consumption generation. The package
validator and all resource, descriptor, viewport, and command checks remain
active. Do not hold the collection read lock across pipeline execution; a
pipeline transition can acquire the corresponding write lock.

Acceptance requires advancing submissions and both preview frame IDs through
cold startup, camera changes, and Play, with no steady collection mismatch.
Strict stereo must remain active, image acquisitions and releases must balance,
and normal exit must drain retired generations. Visual correctness is a
separate check and remains open if black geometry or magenta lines persist.

The candidate Release x64 build passed with zero warnings and errors. In the
same Monado fixture, submissions advanced from 119 to 175 through the interior
camera change, then to 225 and 274 after Play entry. Play exit returned to Edit,
and submissions reached 323 before normal XR exit. The bounded failure log
contains 48 samples and no `CollectGenerationMismatch` occurrence.

Preview frames advanced from 16910 to 43198 before Play, then to 104559 and
105429 in Play. All saved images were viewed. Black geometry, a magenta vertical
line, and incorrect scene output remain. Thus, package consumption has a
scoped live pass, but visual and overall admission acceptance still fail. Cold
post-Play preparation progressed through a 292-requirement manifest without
reset and reduced pending requirements to nine. Reservation failures also
remain. These costs require separate attribution.

Normal exit completed with 432 acquisitions and releases per eye. One retired
generation was queued and drained; none remained pending or abandoned. There
was no device loss or sequential fallback. The editor session was then stopped.

The existing `BackendReadyFramePackageTests` selection passed all 13 tests in
Release. The isolated test build had zero warnings and errors. No test source
was added or changed.

### Remaining Visual Failure

The source review confirms that located and late eye poses include the playspace
transform. The published view descriptor also includes it. The RVC stereo
oracle graph supports deferred materials through a layered GBuffer and stereo
lighting. Neither a missing root transform nor unsupported deferred materials
is established by the current evidence. Inspect the actual draw uniforms and
both GBuffer layers before changing those paths.

A candidate capture is saved as `renderdoc/authority-eye.rdc`. The installed
Monado source calls RenderDoc start/end capture at every XR frame when the
capture API is present (`oxr_api_session.c`). This explains the automatic
captures and their overhead. The capture run reached only one accepted stereo
submission during the observed interval. Its timing is excluded from all
performance comparisons. The owned editor was stopped. The selected completed
capture was copied, and 80 disposable automatic captures were removed from that
launch's exact output prefix.

The follow-up `renderdoc/interior-eye.rdc` contains 305 draws and 287 indexed
draws. The captured eye matrices include (-20.0315, 2, 4) and (-19.9685, 2, 4),
while the model translation is (-20, 0, 0). Thus, those accepted draws include
the playspace translation. At event 2036, the lighting-combine pass named
`Rendering:ForwardPassFBO` has a depth attachment but no color attachment.
The shader has sampled lighting inputs but cannot write scene color. Trace the
resource declaration and recorded attachment publication before selecting a fix.
The owned editor is stopped. Its 71 automatic captures were removed after the
selected capture was confirmed usable.

The lighting-combine descriptor explicitly declares the destination depth and
stencil slots, but lets the quad command infer color from the underlying HDR
texture. Vulkan selects attachment metadata by framebuffer slot. Once it finds
the depth/stencil slots, it prunes the color slot because that slot is absent
from the declaration. This produces the depth-only pass seen in RenderDoc.

The selected correction explicitly declares `ForwardPassFBO` color in
`DeferredLightCombine()`. Its only caller uses that destination. Preserve
intentional unused-attachment pruning and allocator ownership. Acceptance
requires a color attachment on the combine pass, stored HDR color available to
the following sky pass, and fresh visible scene output in both eyes.

The corrected descriptor built with zero warnings and errors. Normal Monado
captures at (-20, 2, 4) show textured Sponza in both eyes, without the previous
black region or magenta line. A second pose at (-15, 3, 2), yaw 25 degrees,
produces different scene views. Preview IDs advance from 37080 to 64456/67186,
then to 104497 after Play entry. The Play capture retains visible geometry.
Play exit returns to Edit. This establishes a scoped visual improvement;
exposure, motion quality, frame pacing, and physical comfort remain separate.

Submissions reached 347, with 134 no-layer frames. Normal exit balanced 479
acquisitions and releases per eye and drained one retired generation. No
generation remained pending or abandoned, and no device loss was reported.
The first follow-up capture, `renderdoc/color-eye.rdc`, contains desktop output
only. It confirms a color target on that pass, but has no stereo array texture.
Do not use it as eye-layer evidence.

The next capture, `renderdoc/color-stereo-cold.rdc`, contains the stereo array
and `Rendering:OpenXRVulkanStereoFBO`. Its lighting-combine scope at events
251-257 has color Clear/Store and depth/stencil Load/Store. The following forward
scope uses Load/Store for both. This confirms the intended attachment change.
The capture precedes the interior pose change; the normal-run interior images
above remain the scene-content evidence. RenderDoc timings remain excluded.
Both owned capture sessions were stopped. Their exact temporary capture sets
were removed after the saved files were confirmed readable.

The captured HDR target is resource 4711, a two-layer 600 by 674 float texture.
Both layers contain defined color after combine and forward. The shared depth
target is resource 4671. However, the final stereo images still contain an
undefined-data pattern and a vertical magenta line in this cold capture.
The line is already visible after forward rendering. Thus, the attachment
correction has a scoped pass; cold post-processing and source-data correctness
remain failing. A nonzero pixel range is not sufficient visual validation.

Further inspection separates the two residual image faults. Forward event
275 has no line; event 506 first shows it. Final-post target 4679 at event 552
has no undefined-data pattern. TSR history resource 4661 receives a copy from
discarded, unwritten output 4695 at event 488. TSR event 570 samples that history
with `HistoryReady` equal to one and produces the pattern in output 4695. The
final stereo draw at event 586 samples that contaminated output. Inspect the
history publication and validity contract before changing the shader or graph.

The previous authority-only binaries remain under `temp-build/tests/` in the
task run. `reports/authority-control-binaries.json` records their hashes. Keep
that artifact tree frozen for later comparisons.

### Warmed Desktop Recording Failure

A normal restart without RenderDoc exposed repeated desktop
`System.IndexOutOfRangeException` failures during command recording. The final
sample contains 32 completed, 395 failed, 218 deferred, 1,387 skipped, and 196
rejected desktop attempts. Do not interpret skipped or rejected attempts as
fresh presentations. The exception stack is not retained in that terminal
telemetry, and Release category logging does not provide it.

The same run reached 324 submitted XR frames and 102 no-layer frames. Normal XR
exit balanced 424 acquisitions and releases per eye and drained one retired
generation. No device loss was reported. A bounded exception trace is the next
diagnostic step. No performance change is selected from these sparse samples.

A 15-second exception trace captured 42 identical stack events at
`VulkanPreparedStableBinStream.VisibilityAtlasManifest`, called by
`TryBuildVisibilityGeometryStream`. The trace lost 30,725 events, so its count
is incomplete. The decoded stacks identify the failing access. A second normal
run reached 887 failed desktop attempts, including failures after XR shutdown.

The stream initially allocates 256 record rows. The builder reads a manifest
by payload index before `TryAppend` grows the row columns. It therefore fails
on the first valid draw beyond that storage. Skipped payloads can also make the
payload index exceed the compact record index. The selected correction grows
the next compact record row before access and uses that row for its manifest.
Payload, draw, and instance identities remain unchanged. The declared capacity,
freeze boundary, slab rebinding, and failure cleanup remain unchanged.

This reproduced desktop recording failure takes priority over general pacing
work. The preceding color-attachment mechanism has a scoped pass; the remaining
cold source/post-processing defects stay separate and open. Before accepting
the row correction, require a zero-warning Release build, zero index failures
through cold startup, at least 100 fresh desktop presents in a 60-second warmed
window, an interior pose change, a Play round trip, and balanced normal XR
teardown. Compare the same Monado fixture with the frozen prior candidate.
No new test source is authorized.

Starting the new named session reclaimed the stopped color-candidate build.
The session manager retains no stopped build artifacts. Its logs and traces
remain, but those binaries cannot serve as a frozen performance control.
Rebuild that exact source delta for later matched comparisons. The separate
authority-only artifact tree under this task remains frozen. Copy required
candidate binaries into the task run before a different session starts.

The row correction passed its focused Release x64 build with zero warnings and
errors. The first warmed endpoint pair contained 1,206 completed desktop frames
over 71.57 seconds; host tool overhead extended that window. A repeat controlled
by one process contained 1,300 completed frames over 60.22 seconds. Both samples
had zero recording failures and output generation one. During the first pair,
XR submissions increased by 1,121 and no-layer frames remained at 59. These are
endpoint progress measurements, not frame-time distributions or matched speedups.

Interior eye images contain textured Sponza geometry and advance from frames
9306/9425 to 32392/32515 in Play. All images were viewed. The Play views differ
in orientation and exposure; this does not close stereo or temporal correctness.
The desktop screenshot still has black geometry and a magenta line. Play entry
and exit completed. The final Edit sample contains 6,838 completed desktop
frames, zero recording failures, and output generation one. Scene diagnostics
report 393 runtime meshes, beyond the initial 256-row storage boundary.

Normal XR exit balanced 5,554 acquisitions and releases per eye. One retired
generation was queued and drained. None remained pending or abandoned. There
was no device loss or sequential fallback. The row-capacity correction is
validated for this workload; broader throughput and image acceptance remain open.
The complete candidate executable directory is frozen under
`temp-build/rows-control/`; `reports/rows-candidate-binaries.json` contains the
checked assembly hashes. The named session is stopped before the next change.

A review of the earlier normal color-correction run found 56 throttled XR
failure records: 30 submission reservation failures, 19 graphics-pipeline
readiness misses, five mesh materialization misses, and two stereo publication
failures. The reservation records omit the failing tracker branch. They do not
prove capacity exhaustion or a failed wait. Aggregate no-layer counts cannot
be assigned to those sampled reasons without frame-level evidence.

### TSR History Copy Ordering

The active increment adds an explicit dependency from each TSR history copy
to the actual TSR resolve pass. The cold capture above is the entry evidence.
The hypothesis is that graph scheduling moves the copy before its producer
because the resource declarations use different keys and provide no explicit
pass dependency. The Default and Advanced chains must identify the producer
directly; Advanced copies an accumulation attachment with a different FBO name.

Acceptance requires a zero-warning Release build, a live Monado stereo capture
in which every history copy follows the resolve draw, defined output in both
eye layers, at least 100 fresh desktop presents during a 60-second normal run,
zero recording failures, a Play round trip, and balanced normal XR teardown.
RenderDoc time is excluded from performance evidence. Compare pass order and
image data with the frozen row-correction candidate. This increment does not
change history publication, native resource lifetime, or queue completion.
The independent risk that CPU enqueue publishes history before accepted GPU
submission remains open and requires its own transaction review.

The first graph candidate built in 71.10 seconds with zero warnings and errors.
Live acceptance failed: XR submitted one frame, then repeatedly rejected
recording because a prepared texture-blit attachment had no published Vulkan
image. Both preview copies remained at frame 56 after a pose change. Desktop
frames continued with zero recording failures. The ownership failure must be
resolved within this increment before history-publication work begins.
That diagnostic also covers an unready descriptor, so it does not yet prove
that the native image is missing. Normal exit balanced 1,034 acquisitions and
releases per eye and drained the retired generation. The session is stopped.

A failure-only diagnostic now names the exact copy pass, requested aspects,
attachment role, logical texture, wrapper, observed image, and cached view.
It preserves the original readiness check and avoids extra resource-refresh
getters. The fields are not an atomic concurrency snapshot. This diagnostic
built in 33.17 seconds with zero warnings and errors.

The next process did not reproduce the attachment failure during startup,
the interior pose, or one Play round trip. A 60.44-second normal window advanced
completed desktop frames from 733 to 2,077 with zero recording failures and
output generation one. XR reached 1,379 submissions and 56 no-layer frames.
Both viewed interior previews advanced at frames 8,433/8,440, then
17,287/17,297 in Play and 23,263/23,270 after Stop. The private stereo pipeline
remained instance 14, with resource generation four after Play. Play images
have different lighting from Edit; this does not close image parity. The first
process's attachment failure remains unresolved and blocks full acceptance.

The earlier magenta-line investigation identified a separate constant-color
line draw at event 506. It uses 136 indices and color `(0.25, 0, 0.5, 1)`.
This matches the movement debug component's `DarkLavender` wireframe capsule.
The source component is inferred from its exact color, geometry, and pass;
the capture has no component label. It is not evidence of TSR contamination.

The default unit world chooses one of five skybox maps at random in
`BootstrapWorldFactory.CreateUnitTestWorld`. The UberShader world selects a
fixed map. This explains the observed launch-to-launch lighting variation.
Pin an explicit scene texture before later matched performance comparisons;
no cache collision is established by this variation.

#### Latest TSR Order Checks

PID 18808 completed a normal exit. It balanced 4,341 acquisitions and releases
for each eye. It drained generation one, with no pending or abandoned
generation and no device loss.

PID 24100 used a fresh private engine Vulkan cache root. The native pipeline
cache began with zero bytes. After ten seconds, diagnostics showed ten native
entries and 36 shader artifacts. This isolates engine Vulkan caches only. The
run had no attachment-readiness failure. The viewed interior eye previews were
defined at frames 3590 and 3598. Normal exit balanced 826 acquisitions and
releases for each eye and drained generation one. No generation remained
pending or abandoned. No device loss occurred.

PID 41280 was a RenderDoc reproduction. It reached one XR submission, then
stopped advancing. Its final normal exit balanced 102 acquisitions and releases
for each eye and drained generation one. No generation remained pending or
abandoned. No device loss occurred. The failed attachment diagnostic named
`HistoryCaptureFBO` and texture `HistoryDepthStencil` (`VkTexture2DArray`),
requested color and depth aspects, `DepthBit`, mip zero, layer -1, image
`0x2BDC6AE39A0` and view `0x2B51832A7F0`. Readiness was false at generation
one, while the allocator was present. The current evidence does not identify
which readiness check rejected the attachment.

The retained stereo RenderDoc capture records the resolve draw at event 548
before the history copy at event 560. The copy reads texture 4625 and writes
history texture 4591. The history was discarded at event 538 and read at event
548. Both exported eye outputs show the undefined-data pattern. The first UBO
field decodes as float `1.401298464324817e-45`, with bit pattern `uint 1`. A raw
UBO export is unavailable, so the field's meaning remains unverified. Shader
schema source review is pending. These RenderDoc captures are not performance
evidence. TSR order and output acceptance remain open.

### Depth History View Ownership

Status: **Scoped image-view ownership child complete.** The TSR graph patch remains parked; its exact three-file diff is preserved in
`reports/tsr-order-parked.diff`. Its ordering change is a required dependency for the accepted-history item below. This scoped ownership result does not close history publication or image acceptance.

The cached-state diagnostic built in 43.66 seconds with zero warnings and errors.
Process 13200 reproduced the frozen stereo output under Monado and RenderDoc.
The first depth-history failures, at engine frames 50 through 60, had a valid
sampler and a view backed by the correct live image. The view was unavailable
for descriptors. At frames 66 through 84, the view had no registry entry. At
frame 90 and later, the same native view handle belonged to a different live
image. The texture still held that handle and descriptor generation one.
This sequence proves stale view retention across retirement and handle reuse.
It rules out a missing sampler as the cause of this run.

The current hypothesis is that view ownership or preparation leaves a retired
primary view in an active wrapper. The next diagnostic must identify the
retirement owner and view generation. A source review found a possible duplicate
cache owner after an equal-image group switch, but the live run has not proved
that route. Do not weaken the readiness guard or revive a retired view.

Acceptance requires the exact retirement cause, a reviewed ownership repair,
a zero-warning Release build, recovery through the original RenderDoc trigger,
and a normal Monado run with advancing previews in both eyes. Require at least
100 fresh desktop presents over 60 seconds, zero recording failures, one Play
round trip, and balanced normal XR teardown with drained retired generations.
The unchanged TSR ordering defect remains outside this ownership gate. Its
known undefined-history output cannot establish final image acceptance.

The last process-13200 sample had one submission, 67 no-layer frames, and 67
balanced acquisitions and releases per eye. Teardown had not completed when
that sample was taken, so it does not prove the final retirement result. The
owned editor is stopped. Temporary captures from this diagnostic run are removed;
the earlier stereo capture remains available for graph and image inspection.

The control with the graph patch removed built in 63.96 seconds with zero
warnings and errors. Process 31684 advanced from two to 44 XR submissions;
no-layer frames stayed at 19 during the observed steady interval. It did not
reproduce the depth-view failure. Normal teardown balanced 62 acquisitions and
releases per eye and drained one retired generation, with none pending or
abandoned and no device loss. This RenderDoc run is trigger isolation, not a
performance comparison. Random skybox selection remains an unmatched variable.

The graph patch is reapplied only as the controlled diagnostic trigger for the
next retirement-owner capture. Its acceptance remains blocked. Validate the
selected ownership repair with both the control and the graph-trigger recipe;
do not treat the diagnostic recipe as acceptance of a second implementation item.

The retirement-owner diagnostic built in 61.15 seconds with zero warnings and
errors. Process 42492 reproduced the failure after one XR submission. The first
pending record instead named `ForwardPrePassDepthStencil`, at pass 100002.
Its view owner was `VkImageBackedTexture.View:ForwardPrePassDepthStencil`, but
its retirement owner was `VkTextureView`. The image remained submitted and live,
with no retirement owner. No physical-view cache entry held the failing view.
This rules out the proposed duplicate cache owner as the cause of this sample.

Source review found the matching ownership error. `VkTextureView` stores native
view handles without their lifetime generations. Interned-view release permits
retirement when no intern entry exists. Backing-image retirement can remove the
entry and destroy its view before an old texture-view wrapper releases its
handle. If Vulkan reuses that handle, the late wrapper release captures the new
resource's current generation and retires that resource. The repair must retain
the acquired view generation and qualify release and retirement with that exact
identity. A missing or replaced intern entry must not grant retirement ownership.

Normal exit balanced 58 acquisitions and releases per eye. One retired
generation drained; none remained pending or abandoned. There was no device
loss. The owned session is stopped and its temporary RenderDoc captures are
removed. The failure log and build result remain in this task's `logs/` folder.

The first ownership candidate stores generation-qualified interned-view
references and removes native view retirement from `VkTextureView`. The service
remains the native owner, including zero-reference cache entries. A review found
no new lock-order conflict or warm-path heap allocation. The Release build
completed in 41.47 seconds with zero warnings and errors.

Live acceptance did not pass. Process 39968 still reported a stale
`ForwardPrePassDepthStencil` primary view at frames 53, 59, and 87. Its first
sample already mapped that handle to an imported texture view, so the log does
not identify the earlier retirement owner. The base wrapper had descriptor
generation two. After an interior pose change, mesh and graphics-pipeline
admission became the sampled failures. XR had one submitted frame in the last
recorded progress sample. The instrumented process then responded slowly to
MCP calls. The owned session is stopped; no normal-teardown or performance pass
is claimed. Further retirement-owner evidence is required before another repair.

The accepted-retirement transition observer built in Release in 41.17 seconds
with zero warnings and errors. Stationary Monado with RenderDoc, process 25168,
recorded serial 250 for `ForwardPrepass`. The view had generation 4241 and owner
`ForwardPrepass`; `VkImageBackedTexture.DeleteObjectInternal` requested its
retirement while backing image generation 4150 remained submitted and live.
The first fault was frame 52, XR frame 22, for that same view. The wrapper ID
was 26939610, texture ID 48532260, and descriptor generation two. Serial 297
also recorded a `HistoryDepth` view at generation 4463 while backing image
generation 4142 remained live. Which wrapper issued the delete is still
unknown.

The next observer should record wrapper creation and deletion IDs. It must not
change lifecycle locks or behavior. That run alone did not identify the wrapper
that issued the delete. A follow-up identity trace below resolves the owner
route. Final XR progress was one submitted frame and 87 no-layer frames.
Teardown completed with 87 balanced acquisitions and releases per eye. One
generation was queued and drained; none remained pending or abandoned. There
was no device loss. The session is stopped. This evidence does not validate a
fix.

The wrapper-identity observer built in 28.98 seconds with zero warnings and
errors. In process 37668, the `ForwardPrePass` wrapper (ID 21241820) created
native view generation 3872. It later deleted the same handle after the
registry reported generation 4707 for a different backing image. The
`HistoryDepth` wrapper (ID 54568563) created generation 3860 and later deleted
its handle after the registry reported generation 4401 for a different backing
image. Both create and delete events ran on thread two. These two traces prove
the stale base-wrapper ownership route. The active `History` failure at frame
73, XR frame 23, involved wrapper 10975654 and view generation 4114. Its
`DeleteObjectInternal` event was serial 340 while backing image generation
4097 remained submitted and live. The target-name filter did not identify that
deleter.

The acceptance design is to retain each view's creation generation for primary,
attachment, and cached views. Check that exact generation before recording the
fence or ticket and again at the accepted transition. Keep lifecycle locks
unchanged. Do not repair ownership during a read. Apply the existing ownership
acceptance criteria. Final XR progress was one submitted frame and 50 no-layer
frames. Teardown completed with 49 balanced acquisitions and releases per eye.
One generation was queued and drained; none remained pending or abandoned.
There was no device loss. The session is stopped. No fix pass is claimed.

### Generation-Qualified View Ownership Repair

The active repair stores each owned view as a `VulkanOwnedImageView` with its
native handle and creation generation. `VkImageBackedTexture` uses this receipt
for primary, attachment, physical-cache, and imported-upload views. Retirement
checks the exact receipt before admission fencing and checks identity again
after dependency publication. The repair leaves lifecycle locks unchanged.

Static Astra review approved the repair after descriptor-generation publication
was corrected in three alternate-view replacement helpers. The first build
found one conversion from a receipt to a raw handle in cache retirement; that
path was fixed.

An intermediate Release build completed in 42.43 seconds with zero warnings or
errors and started process 10392. The process was stopped for the final
descriptor correction, so it is not acceptance evidence. It had three XR
submissions, 18 no-layer frames, and 20 balanced acquisitions and releases per
eye. Normal teardown drained generation one. The final candidate build and live
validation were pending at that point. This does not close the todo item or
claim test validation.

The final candidate Release build completed in 29.54 seconds with zero warnings
or errors. Source and binary identities are recorded in
`reports/base-view-ownership*.json`. Process 33172 ran Monado with RenderDoc
against the earlier trigger. The original XR failure did not recur: submissions
advanced from 10 to 29, and no-layer frames stayed at 22 after pipeline
preparation. The log contains zero cached-readiness failures and zero
missing-published-image failures. Two stale target-wrapper delete attempts were
observed, but neither retired a foreign view generation.

Both viewed previews at frame 131 show a brick wall and bright exterior sky.
The selected captures are `renderdoc/owned-view-early-stereo.rdc` and
`renderdoc/owned-view-warm-stereo.rdc`; neither has been inspected offline. The
process became slow during capture. Its MCP exit call timed out, and the named
session manager stopped it. This run has no normal-teardown or performance
pass. Sixty-two disposable captures were removed; the two selected captures
remain. The sampled process private memory was 6.83 GB during capture.

The normal Monado run with the same binary has started. The ownership gate
remains active until this run and the required controls complete.

Process 42032 ran the candidate without RenderDoc. It used the same source and
binary. After the bootstrap skybox load completed, the private scene set
`warm_restaurant_4k.exr`; bootstrap had first selected
`klippad_sunrise_2_4k`. This was a private scene change only. At the interior
pose (-20, 2, 4), both viewed previews showed fresh frames 7769 and 7771.

The 60.0097458-second endpoint sample recorded 1,052 more completed frame
outcomes and 1,052 more completed command-record outcomes. Failed outcomes
increased by zero, deferred outcomes by zero, rejected outcomes by 72, and
skipped outcomes by 2,195. These deltas show liveness. They do not establish
100 FPS or a frame-time distribution. The previews at frame 17209 in Play and
21465 after returning to Edit were also viewed. Lighting and exposure differ
across Play and remain open. The saved log has no stale-view or
missing-published-image failure.

Final XR progress was 4,889 submissions and 123 no-layer frames. Teardown
completed with 5,010 balanced acquisitions and releases per eye. One generation
was queued and drained; none remained pending or abandoned. There was no device
loss or end-frame failure. The session is stopping. Before the ownership gate
can advance, run the control with the graph patch parked and the private empty
engine cache. Acceptance remains open.

The graph patch is parked. The exact three-file patch was reversed. The final
control used the candidate binary with a private engine-cache root that did not
exist before the run. Diagnostics report zero initial native pipeline-cache
bytes, then 627 prewarm entries and 82 shader artifacts. The private scene set
`warm_restaurant_4k.exr` after the initial `klippad_sunrise_2_4k` selection.

Process 7924 ran for the cold control. At pose (-15, 3, 2), both viewed previews
at frame 7198 showed current geometry. Foreground arch occlusion differs at
this near-geometry pose; this does not establish projection or comfort parity.
Cold pose preparation caused 834 no-layer frames. The count stayed at 834 as XR
submissions advanced from 1,289 to 3,278. The old stale-view and
missing-published-image failure counts both stayed at zero. The 60.0227884-
second endpoint sample recorded 1,484 more completed outcomes and 1,484 more
completed command-record outcomes, with zero failed or deferred outcomes, 28
rejected outcomes, and 2,678 skipped outcomes. These values show liveness, not
a frame-rate or frame-time distribution.

Final progress was 3,284 submissions and 834 no-layer frames. Teardown
completed with 4,116 balanced acquisitions and releases per eye. One retired
generation drained; none remained pending or abandoned. There was no device
loss. Source and binary identity are recorded in `reports/base-view-ownership-control*.json`.
Astra accepted the declared ownership-only live gates. This does not close the
cumulative todo.

The user cleared focused test edits. Sol is updating the two stale source
assertions and adding deterministic no-GPU stale-generation coverage. Those
tests remain pending. The test changes do not establish cumulative todo
completion.

Offline RenderDoc review confirms defined TSR output in both saved ownership
captures. In `owned-view-early-stereo.rdc`, TSR draw 597 writes output 4576.
Both exported layers were viewed. They show a dark exterior brick surface and
saturated white sky, with a repeated edge pattern. History color image 4542
moves from `Discard` at event 587 to `PS_Resource` at draw 597, then to
`ResolveDst` at event 609. The draw-597 constant buffer at set 0, binding 66,
contains first-field bool value 1. This confirms `HistoryReady` for that draw.

In `owned-view-warm-stereo.rdc`, TSR draw 485 writes the same output image
4576. Both exported layers were viewed. They show defined output with smoother
edges; a thin magenta line remains. History color image 4542 is read as
`PS_Resource` at draw 485 and resolves at event 497. This warm capture has no
history discard. The exported layers and data are in the current task's
`renderdoc/` folder: `owned-view-early-tsr-layer0.png`,
`owned-view-early-tsr-layer1.png`, `owned-view-warm-tsr-layer0.png`,
`owned-view-warm-tsr-layer1.png`, `owned-view-early-temporal-ubo.json`, and
`owned-view-warm-history-usage.json`. Replay sessions are closed.

These captures narrow the observed issue to cold or new physical history
publication. They do not prove a per-frame discard, visual parity, or quality
acceptance. The ownership behavior tests and two updated source assertions
passed. The original focused 11-test run had seven stale source-contract failures. The later 11/11 rerun below supersedes that earlier result. This does not close the todo beyond the scoped ownership child.

### Scoped Image-View Ownership Closeout

The generation-qualified image-view ownership child passed its scoped live gate.
The same-binary Monado trigger run, graph-patch-parked cold control, both-eye
previews, Play/Edit round trip, warm 60-second liveness window, and normal
teardown completed. The final control matched the frozen source and binary
identities. The cold control began with zero native pipeline-cache bytes, then
recorded 627 prewarm entries and 82 shader artifacts. This confirms cache-cold
startup for the engine Vulkan cache only.

The focused Release test rerun passed 11 of 11 tests, with zero skipped tests,
about one second of test time, and no build warnings or errors. It included two
stale-generation behavior tests and nine source-contract checks. The TRX is
`reports/test-results/view-ownership-rerun.trx`. This closes only the
image-view ownership child. It does not close temporal history, TSR ordering,
visual parity, 100 FPS, headset comfort, or other hardware work.

### Next Gate: Accepted Temporal History Publication

Astra completed design review before implementation. The next item stages strict
Vulkan single-pass stereo CPU history. Seal read/write native image handle,
generation, and subresource identities under the exact recording planner before
uniform preparation. Publish frozen matrices and history only through the
existing `NativeSubmissionAccepted` tracker transition, outside tracker and
queue locks. Discard exact tokens for unsubmitted work. Keep desktop and OpenGL
behavior unchanged. Retain the existing asynchronous completion policy.

The parked three-file TSR ordering patch is a required dependency in this one
history-correctness item. The current producer order cannot seed valid history,
so it cannot pass as a separate item. Use the frozen ownership control and graph
diagnostic capture as the baseline. Freeze source and binary hashes before each
run.

Acceptance requires the first cold or new-backing TSR frame to use
`HistoryReady=0`, then a later queue-accepted frame on the same backing to use
`HistoryReady=1`. Verify complete native color, depth, and TSR layer coverage
for both eyes. Pre-queue recording or submit faults must not advance readiness
or matrices. An accepted-publication fault must publish once. Camera cuts, Play,
and session restart must invalidate old tokens and seed new history. Allow at
most one outstanding candidate per state. Add no per-frame heap object, fence,
or wait. Require one 60-second warm liveness window with zero failed recording
outcomes and no stale-view failure. Normal teardown must balance both eyes and
leave zero pending or abandoned generations and no device loss. RenderDoc data
is diagnostic evidence, not performance evidence. Broader 100 FPS, comfort,
and lighting gates remain open.

The source review found that authoring can freeze temporal uniforms in a
`ProgramBindingSnapshot`. A later snapshot update cannot repair that draw.
The strict stereo path must use capacity-only prewarm before the final physical
history check. If that check invalidates an authored ready history, discard the
attempt and let the next frame author unready history. Do not rewrite a captured
binding packet. Permit at most one such history invalidation deferral for each
changed physical history identity. The same identity must then make progress.
Keep other admission failures separate from this counter.

Implementation review found three additional acceptance conditions. Resolve
history roles through the sealed operation registry because frame-plan rebasing
can replace the captured texture and framebuffer objects. Invalidate the exact
pending history owner if its device is lost. Keep diagnostic publication outside
the history lock and release detached uploads on every recording refusal.

The primary recorder can skip a failed mesh draw and continue. A recorded
history copy alone does not prove that its resolve source was written. The
candidate must also attest the exact TAA or TSR resolve draw before its history
copies. Check the bound native color attachments, source generation and range,
full render area, viewport and scissor, and the actual multiview mask for both
eyes. Keep these resolve operations in the primary so their success is explicit.
The producer check is implemented. Astra approved the frozen source for build.
The review also required invalidation of an accepted seed if its backend device
is lost after publication. That check preserves seeds from a replacement backend.
Source identities are in `reports/temporal-candidate-source-identity.json`.
Build and live validation are now in progress; no history acceptance is claimed.

Controlled failure runs will load the existing scene before enabling Monado.
This lets the run enable temporal diagnostics after scene construction, without
adding the diagnostic validation geometry. The smoke timing record links the
submission lifecycle frame to the engine render frame. Use that link to compare
rejected attempts and accepted-publication faults with history commit entries.
Keep the capture window small enough to retain those entries in the bounded
ledger. These are planned checks, not passed results.
The desktop-first launch used `XR_RUNTIME_JSON` without starting a VR session.
Desktop initialization queried Vulkan OpenXR requirements early and the loader
failed. This run gives no temporal fault or history evidence. Use a normal
Monado session that has warmed for 512 frames for controlled faults. Enable
diagnostics after scene construction with
`XREngine.XREnvironment.SetRuntimeOverride`; a call to
`System.Environment.SetEnvironmentVariable` alone does not update the cached
runtime override. No controlled fault gate has passed yet.

The first candidate Release build passed in 100.27 seconds with zero warnings
and errors. The 54 recorded source identities did not change during the build.
The snapshot includes concurrent compiler and editor work outside this repair;
this run is not a controlled performance comparison with the ownership binary.
Binary hashes are in `reports/temporal-candidate-binaries.json`.

Process 53244 started normal Monado strict stereo from an empty private engine
cache. Diagnostics confirmed zero initial native pipeline-cache bytes. The
first query reported 52 submissions, 23 no-layer frames, and ready history for
both eyes in pipeline 14. After the bootstrap skybox loaded
`studio_small_09_4k.exr`, the private scene selected `warm_restaurant_4k.exr`.
The camera cut to (-20, 2, 4) cleared both seeded generations. The existing cold
compatible-program admission gap then held the preview at frame 3503. No-layer
frames reached 852 before rendering resumed. A later query reported 1,308
submissions with the same 852 no-layer frames and both eyes seeded at generation
two. Both previews at frames 7518 and 7662 were viewed and showed current Sponza
geometry. This confirms recovery after the cut. The cold delay remains open.

The normal warm liveness report covers 62.417 seconds. It records 1,150 new
completed outcomes and 1,150 completed command recordings, with zero deferred
or failed outcomes and zero deferred or rejected command recordings. It also
records 22 rejected and 2,949 skipped outcomes. These are endpoint deltas, not
a frame-time distribution. The saved report is
`reports/temporal-warm-liveness.json`.

The Play/Edit round trip resumed stereo output. Both eye previews were viewed
after recovery. The first normal session ended with 3,716 submissions and
1,011 no-layer frames. Each eye had 4,725 acquires and 4,725 releases. Its
teardown completed with one drained retired generation, zero pending or
abandoned generations, and no device loss. A new Monado session reached 552
submissions and two no-layer frames. It ended with 553 acquires and 553
releases per eye, two drained retired generations, zero pending or abandoned
generations, and no device loss. The two normal teardowns are recorded in
`mcp-output/temporal-first-teardown.json` and
`mcp-output/temporal-restart-teardown.json`.

The source review, Release build, cold/cut recovery, warm liveness, Play/Edit,
session restart, and normal teardown child gates pass. The parent history gate
remains open. The first controlled Submit attempt enabled diagnostics after
the injected failure. It cannot prove rejection behavior. The bounded repeats
below replace it for that narrow check. Cold/warm RenderDoc review, the
admission gap after the camera cut, and broader stereo image quality remain open.

### Bounded Rejection Checks And Stop Record

The two repeats used the same candidate binaries, normal Monado startup, 256
warmup frames, and 128 retained frames. The monitor enabled the temporal ring
after 100 successful submissions and disabled it after OpenXR teardown. The
Submit run enabled it at 117 submissions; the Recording run enabled it at 105.
Each ring had zero overflow and zero submission diagnostic failures.

The Submit injection occurred once at completed/XR frame 281 and engine render
frame 972. It was handled as `NotSubmitted`, with no projection layer and no
sequential fallback. The strict stereo temporal ledger has no entry for 972.
Frames 970 and 974 each have one committed entry per eye, with all required
layer masks equal to three and reset/seed generation one. Astra accepted this
narrow proof that the rejected attempt did not publish accepted history. The
run ended with 359 submissions, 25 no-layer frames, and 382 balanced
acquisitions/releases per eye.

The Recording injection occurred once at completed/XR frame 279 and engine
render frame 938. It was handled as `NotSubmitted`, with no projection layer
and no sequential fallback. There is no strict stereo temporal entry for 938.
Frames 930 and 940 each committed both eyes with complete layer masks and
reset/seed generation one. The run ended with 361 submissions, 23 no-layer
frames, zero end-frame failures, and 382 balanced acquisitions/releases per eye.
Both runs completed OpenXR teardown, drained one retired generation, and left
zero pending or abandoned generations and no device loss.

These checks prove absence of accepted-history publication for the rejected
attempts. They do not directly snapshot every temporal field before and after
rejection. The stationary view matrices cannot independently prove matrix
preservation. The reviewed rejection path preserves the accepted seed and
previous matrices, and subsequent accepted frames retain readiness.

The editor remained open after the smoke controller requested shutdown. A
snapshot of its stacks showed the main thread in the native event pump. This
does not identify the cause. An orderly close request did not produce the
automatic summary. A scratch reflection bridge copied the controller's frame
ledger under its lock, then captured the full temporal ring after normal
OpenXR teardown. It did not finish the controller or change its exit result.
The exports are diagnostic evidence, not full smoke-suite passes. Keep the
editor shutdown delay open. The named session manager stopped the owned editor.

Evidence is under `Build/_AgentValidation/20261005-130013-vulkan-stall-completion/`:

- `reports/temporal-submit-bounded-diagnostic.json` and
  `reports/temporal-recording-bounded-diagnostic.json` contain the exact frame joins.
- The matching `mcp-output/temporal-*-bounded-evidence-failures.json` files record
  zero diagnostic failures. Matching log folders preserve the process logs.
- `reports/temporal-control-copy.json` records the frozen candidate executable
  tree in `temp-build/temporal-control/`. All three main assembly hashes match
  `reports/temporal-candidate-binaries.json`.

The user requested a stop after the Recording check. Session
`stall-monado-rows` is stopped. Resume with the accepted-publication failure
check, then cold/warm RenderDoc evidence. Resolve or explicitly scope the
smoke-run shutdown delay before claiming full smoke acceptance. New temporal
test edits still need user clearance after the remaining live checks. The
existing 11/11 focused ownership tests remain the completed test result.

## Independent Evidence Review

A read-only broker review completed with requested and actual model
`gpt-6-luna`. It identified the required configuration, freshness, observer,
runtime-extension, and process-ownership checks. No source or runtime tools were
exposed to that worker. Its result is a checklist, not runtime validation.
