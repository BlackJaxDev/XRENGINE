# Advanced mirror and Vulkan placement closeout

This investigation implements ARP-I91, ARP-I93, ARP-I94, ARP-I81 and ARP-I90
from [the Vulkan XR/Advanced tracker](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md).
The September 8 failed mirror images and Vulkan placement captures remain
historical evidence; a successful build alone does not close their runtime gates.

## Starting evidence and plan

- The oblique camera uses a zero-to-one projection but replaces its depth column
  using a negative-one-to-one formula. Compute a validated candidate before
  committing it, and configure the reflected camera before the clipping plane.
- Legacy mirror readers have only a PostRender fence. Reserve collected readers
  until command rejection/reset and retain executed readers until GPU completion.
- Cold mirror targets are exposed before a successful writer. Keep scheduling
  active while admitting only completed output generations for display.
- The standalone Advanced capture owner already owns writer completion and
  canonical texture publication. Reuse it for persistent reflected-camera slots,
  with matching projective material constants and explicit camera capacity.
- Vulkan primitive/gizmo placement needs fresh scene and transform evidence;
  do not repeat the already completed stereo shader-entry-point split.

## Validation ledger

Task-owned session: `arp-mirror-placement`. Scratch evidence root:
`Build/_AgentValidation/20260913-234432-arp-mirror-placement/`.
Settings are copied into that root and passed through
`XRE_UNIT_TEST_WORLD_SETTINGS_PATH`; the user's settings and editor sessions are
preserved. RenderDoc 1.44 passes `rdc doctor`.

The five requested implementation tasks are complete. The extended large-scene
mirror cohort exposed a separate Vulkan storage obligation, tracked as ARP-I95
under ARP-V36; it does not reopen the demonstrated primitive placement behavior.
No user report of a new solution working or failing has been received. No tests
have been added or modified; the repository requires explicit clearance after
feature validation.

## September 14 implementation and runtime checkpoints

The oblique replacement now uses the row-vector, zero-to-one convention: retain
the projection's far corner and replace its depth column with `plane / dot(plane,q)`.
Plane normalization, finite/invertible input and a relative singularity check
precede the state commit. Reflected owners install their transform and lens
before applying the world plane.

The mirror integration uses three admitted camera identities and two persistent
slots per camera. A completed texture generation and its saved reflected
view-projection enter the same native material snapshot. The planned material
transaction retains each source until a canonical publication takes ownership;
legacy display collection and executed GPU readers have separate retains.
Cold/replaced outputs are withheld from display while capture scheduling remains
active. Capture owners request HDR and late transparency without temporal history,
main-view post-processing, bloom or depth of field.

Vulkan placement investigation found two coupled source-publication defects:

1. An imported texture changed from a 64×64, one-mip preview to a 4096×4096,
   13-mip resident image while canonical sequence 10 retained its preview metadata.
   Native stages correctly reported `SourceMismatch`.
2. The publication ring was full with sequences 3–10. All consumers acknowledged
   sequence 10, but the previous Vulkan picking source retained sequence 3 while
   its unaccepted candidate retained sequence 10. FIFO-only reclamation kept the
   unpinned middle sequences, so the corrected scene could not publish sequence 11.

The ring now compacts acknowledged, unpinned entries while preserving every pinned
snapshot and its leases. Tombstones still respect the oldest retained sequence.
Imported metadata has a separate serialized odd/even epoch, with ordered reader
validation; ordinary GPU capture-generation values do not acquire parity rules.

Start04, Release, built with **zero warnings and zero errors**. A texture-free
Vulkan fixture admitted native shading and showed one cube and one centered gizmo
at `(0,2,0)`, `(2,4,-1)` with 35° yaw, and `(-2,1,2)` with −20° yaw. All three
screenshots were inspected. Canonical sequence 18 had one retained publication,
one resident draw and no rejection. These are placement observations, not the
broader ARP-V37 editor-consumer acceptance.

Adding the mirror exposed a separate offscreen rejection. Start05, Debug, also
built with **zero warnings and zero errors**. Its logs established:

- Mirror capture scope restoration left `IsSceneCapturePass` set on the render
  thread, suppressing subsequent main-view work. Restore the mirror scope before
  restoring the prior scene-capture flag.
- Exact output binding rejected the reflected writer with `reason=producer output
  id`, `matched=0`, `authored=8`, despite matching 256×256 extents and generation 2.
  CaptureVersion stayed zero and the cold display gate stayed closed. This output
  identity mismatch remains under investigation at this checkpoint.

Start04 evidence: `mcp-output/state-04.json`, `vk-placement-04/05/06.json` and the
referenced PNGs in the task scratch root. Start05 log session:
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260913-234641-arp-mirror-placement/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-14_00-25-36_pid33620/`.
The reusable live setup script is disposable evidence; required behavior does not
depend on it. The optional Sol API broker run failed because its project had no
credits; no broker result was accepted. Native agent review continued locally.

Start06, Debug, built with **zero warnings and zero errors**. The Vulkan output
request now preserves `InWorldMirror`/`VrPickupMirror` identity for scene-capture
contexts. Together with the scope restoration fix, this produced 327 completed
reflections across the same two persistent 256×256 slots. The exported HDR image
contains the red cube in front of the mirror and excludes the green cube behind
its plane; the PNG was inspected. No slot was quarantined. Main-view publication
still rejected the mirror material, so this checkpoint does not establish native
display acceptance. A missing projective-material capacity entry and an exposed
publication-failure diagnostic are included in the next iteration.

Source review also found that true single-pass stereo collection supplied only
the left camera to mirror admission. Each eye has a distinct canonical identity;
both must be requested while collecting only one display command.

Start07, Debug, built with **zero warnings and zero errors**. Including the fourth
material layout in the capacity profile admits all three native fixture draws.
Its first completed mirror then exposed a Vulkan receipt-domain mismatch: eight
captured FrameOps plus fourteen prepared static mesh entries lower to twenty-two
operations, but completion binding compared twenty-two against eight. The fix
counts both authored sources while retaining exact count and terminal checks.

Resizing from 256×256 to 1×256 and back retired and replaced both output textures,
recovered capture scheduling, and advanced to 1,804 completed generations without
quarantine. This also revealed that private viewport resizing mutated the shared
source lens. Dedicated offscreen viewports now disable camera-aspect updates.
The native resident scene still included the mirror itself during reflection
visibility, occluding the subject with a black quad; explicit per-view exclusion
is the next isolation step. Native display acceptance remains open here.

Live static oblique-method probes accepted perspective, off-center perspective,
off-center orthographic and equivalent planes scaled by 1e-12/1e12; a zero normal
threw `ArgumentException`. The derived zero-to-one equations retain the intended
halfspace and put the plane at zero depth. MCP's opaque Matrix4x4 return prevents
direct numeric read-back, so the derived numbers are not claimed as measured GPU
depth values. Evidence: `reports/oblique-live-math.json`.

Before stopping Start07, 6,212 completed reflections had advanced the canonical
scene to sequence 5,949 with one retained publication and no publication
rejection. Deactivation initially retained the last canonical reader, then
drained all banks: no output, no queued retirement and all three bank entries
null. No resource was destroyed ahead of that reader's release.

Build08 passed with **zero warnings/errors**. Start09 used its binaries after a
shader-only restart to avoid observing a partially edited include/caller pair.
Exact Vulkan writer completion and capture scheduling now succeed from cold
activation. Initial reflection exclusion still failed because both Vulkan and
OpenGL regenerate authoring views after collection and discarded the package's
policy flag. The next iteration reapplies the immutable collection policy to
those newer authoring matrices and to their diagnostics. This keeps fresh XR
eye poses while preserving reflection exclusion.

Start10, Debug, built with **zero warnings/errors**. Its reflected texture now
shows the red front cube with the green behind-plane cube and reflective surface
excluded; capture scheduling and native publication both advance. The main mirror
remained black because Vulkan used a duplicate view converter that omitted the
new source-camera identity. Vulkan now uses the shared `AdvancedViewRecordFactory`
also used by OpenGL, removing that divergence. The next run verifies native display.

Start11, Debug, built with **zero warnings/errors** and established native Vulkan
projective display. Inspected front and angled views show the red front cube's
reflection moving with the camera; the green behind-plane cube is absent from the
reflection. Resizing from 256×256 to 128×192 replaced the private textures without
changing the main camera lens. CaptureVersion advanced from 5 through 886 with
three resident draws and no publication rejection. A framed off-center
orthographic view also produced the expected reflected red geometry.

The reversed-depth check exposed a general native Vulkan visibility bug:
`VulkanCanonicalVisibilityPipelineFactory` always selected `LessOrEqual` while
reversed targets clear depth to zero. Its pipeline key and depth state now derive
the comparison from the sealed target clear policy (`GreaterOrEqual` for reverse).
Start12 built with **zero warnings/errors** in 12.73 seconds. Inspected perspective
and off-center orthographic images retain geometry in both depth modes. Changing
the mirror's translation and yaw updates the reflected projection. Three immediate
deactivate/reactivate cycles recovered completed reflections; final deactivation
drained all banks at CaptureVersion 960, with no output, no queued retirement and
no capture failure. Evidence summaries are `reports/11-front.json`,
`11-angle.json`, `11-resize.json`, `12-reversed.json`, `12-ortho-forward.json`,
`12-ortho-reversed.json`, `12-mirror-pose.json`, `12-reactivated.json` and
`mcp-output/12-retired-diag.json` in the task scratch root. These observations do
not certify hardware stereo or the complete ARP-V36 fault-injection matrix.

Native review found that resident scene instances do not use the legacy
`RenderCommand.Enabled` gate. Early/late visibility now additionally reject a
projective mirror without a valid entry matching the selected camera's complete
64-bit identity. This removes cold or withdrawn surfaces before they can write
depth, while preserving the independent reflection-recursion policy. Malformed
projective constant ranges or incomplete binding rows are also excluded.

Start13 selected OpenGL but its intended Default fixture still used the obsolete
`UseAdvancedRenderPipeline` property. Its actual main pipeline was Advanced, so
that run is excluded from legacy display acceptance. The mirror's private Default
capture did complete and exported clipped red geometry, while the mismatched main
view contained only blurred editor overlays; this is not an Advanced OpenGL
acceptance result. Start14 uses the supported
`Rendering.RenderPipeline: DefaultRenderPipeline` selector. For readable display
evidence its main camera has bloom and automatic exposure disabled; those changes
are confined to the disposable session.

Start14 reused Build12 binaries and inspected real legacy OpenGL display and
reflected output from front and angled cameras. Forward and reversed depth both
retain the red front cube and clip the green behind-plane cube. Resizing to
128×192 and three immediate deactivate/reactivate cycles recover reflections with
no capture failure or quarantined slot. Collected-reader diagnostics show exact
generation references while commands are queued; final deactivation drains every
bank at CaptureVersion 6,875 with no output or queued retirement. Evidence:
`reports/14-angle.json`, `14-reversed.json`, `14-resized.json`,
`14-reactivated.json` and `mcp-output/14-retired-diag.json`.

Start15 reintroduced the streamed animated avatar with 78 resident draws. The
publication fix now advances past the original pinned sequence: sequence 37 has
two retained publications and no source mismatch or publication rejection. A
separate deformation owner stall still rejects native stages with
`The advanced deformation GPU output slot is not reusable.` Its first relevant
Vulkan message is a deferred `AdvancedDeformation.Aggregate` program at
01:21:34.718; frame 1523 is then rejected during recording retry. The failure
predates mirror creation and remains after mirror deactivation. The mirror's
first slot completes one capture, while the second is cleanly rejected and its
unwritten output stays unpublished. This streamed cohort remains under
investigation and does not yet establish I90 runtime recovery.

Log review narrows the streamed texture result: source-metadata rejections occur
from 01:21:36.939 through 01:21:37.525 during intermediate texture promotion, then
stop; streaming telemetry later reaches `pending=0`, `promoted=11`, `failed=0`.
The deformation stall persists independently with `outputReuseStatus` set to
`AwaitingSubmission`, slot 2, authority `ProducerFence`, thousands of frames
after authoring. Thus the ring no longer blocks corrected publication, but an
orphaned producer receipt still blocks native execution.

The Start13 OpenGL failure has an explicit cause: shader lowering expected the
old 928-byte camera record while the new shared record is 944 bytes. The OpenGL
lowerer now uses `AdvancedShaderRecordLayout.ViewSize`; a focused scan finds no
other stale view strides (928 remains the correct identity-field byte offset).
Native OpenGL parity is being rerun with that correction. Important run logs
were copied into the task scratch root before the session manager pruned older
engine log directories; the Start12 build and captured diagnostics remain, but
its original engine logs are no longer retained.

Build16 includes the shared OpenGL view-stride correction and passes with zero
warnings/errors in 7.46 seconds. Start16 Advanced OpenGL admits all native stages
and exports correct reflected geometry, but the main native mirror is still
black; its sampling path remains under investigation. Controlled output
withdrawal through the ordinary retirement request leaves the node active and
all three resident draws present. With no valid camera entry, the mirror writes
neither visibility nor depth: the inspected image shows the green behind-plane
cube and grid unobstructed. All capture banks drain. This directly validates the
native generation gate independently of component visibility. Evidence:
`reports/16-native.json` and `mcp-output/16-withdrawn-{diag,state,main}.json`.

Start17 used Build16 binaries and a temporary projective shading diagnostic.
The complete mirror quad was magenta, proving the published entry matched the
selected camera and produced finite, in-range projective coordinates. Start18
then used a temporary red return for failed texture-reference resolution; its
quad stayed black. The resolver succeeded, leaving either valid coordinates
pointing at black texels or the actual OpenGL bindless sample as the next split.
Both diagnostic edits were restored immediately after observation; these are
isolation evidence, not successful mirror display acceptance.

Start19 sampled the known red capture region at a constant `(0.495, 0.49)`.
The entire mirror became red, confirming the OpenGL handle and captured content
are valid. The previous black result is a coordinate/reconstruction mismatch,
not a failed resource lookup. The constant-coordinate diagnostic was restored.

Start20 uses the corrected shared depth reconstruction: convert sampled depth
according to the sealed view's `DepthZeroToOne` flag, not the backend name.
OpenGL's zero-to-one clip control and the stored inverse projection require the
sampled value unchanged. The actual native mirror now displays the red reflected
cube from an angled view and clips the green behind-plane cube. Reverse depth,
128×192 resizing and three rapid deactivation/reactivation cycles preserve the
reflection; completed captures advance from 49 through 2,694. Final deactivation
drains all banks at 5,025 with no output, queued retirement or failure. These
PNGs were inspected. The ordinary legacy grid overlays native surfaces in reverse
depth; that separate forward/grid depth-consumer issue is not closed by this
mirror result. Negative-one-to-one culling support and hardware stereo remain
outside this cohort. Evidence: `reports/20-native.json`, `20-reversed.json`,
`20-resized.json`, `20-reactivated.json` and `mcp-output/20-retired-diag.json`.

The Vulkan output-authoring rejection path now discards only Pending operations
bearing the failed output's exact receipt. It fails their producer markers and
releases authoring snapshots/visibility leases before deleting the receipt;
unrelated and later queue work remains in order. The earlier artifacts did not
prove a desktop receipt-zero orphan: the first measured AwaitingSubmission state
occurred after mirror creation. Start21 validates this narrow fix with temporary,
bounded ordered-producer fence tracing; no broad desktop queue reset is added.

## Final closeout and remaining boundary

Build21 passed with zero warnings/errors in 23.69 seconds. Its large streamed
main/mirror cohort reached a different failure: at 01:55:13.893, frame 2176,
Vulkan terminal-paused during pipeline preparation because the occupied scene
storage slot could not accommodate a 73,234,848-byte compact image. The last
render diagnostics then froze at frame 2179 and the second mirror writer stayed
AwaitingSubmission. That state follows from the renderer pause and is not proof
of another orphaned producer fence. All traced pre-mirror producer markers had
reached Bind or Fail; the bounded trace was exhausted before mirror creation,
so no post-mirror marker conclusion is drawn from it. Temporary fence tracing
and all diagnostic shader overrides were removed before the final build.

Start22 isolated selected-primitive placement with the streamed scene still
resident. The initial image was occluded by the imported avatar's scale; its
root was scaled to 0.01 and offset to `(3,0,-4)` in this disposable session to
expose the cube. Three inspected images then show one cube and one centered
gizmo at `(0,2,0)`, `(2,4,-1)` with 35° yaw, and `(-2,1,2)` with −20° yaw.
There are 79 resident draws, frames advance 3469–3542, scene publication advances
56–60 with one retained publication, native stages are accepted, and GPU
deformation remains Ready. This closes ARP-I90's creation/repeated-transform
scope; it does not certify imported avatar deformation or all ARP-V37 consumers.
Evidence: `reports/placement-22-framed.json` and its referenced PNGs.

Final Build23 passes with **zero warnings and zero errors** in 24.07 seconds,
without temporary instrumentation. Its Vulkan mirror displays the red front
cube's reflection from the angled camera and excludes the green behind-plane
cube. Captures advance from 10 to 432. Withdrawing the completed output while
leaving all three native scene draws resident removes the mirror's color and
depth: the green cube and grid become unobstructed. Every bank drains, with no
output, queued retirement or capture failure. Both main images were inspected.
Evidence: `reports/23-final-native.json` and
`mcp-output/23-withdrawn-{diag,state,main}.json`.

The requested task results are:

| Task | Implemented behavior and bounded evidence |
|---|---|
| ARP-I91 | Validated transactional zero-to-one oblique clipping, reflected-camera ordering, shared-lens isolation; forward/reverse perspective and off-center orthographic Vulkan images, visible OpenGL reflections. |
| ARP-I93 | Separate collected/package/GPU reader ownership, exact generation retention, safe rejection/retirement; Vulkan and both OpenGL paths survive resize/reactivation and drain healthy banks. |
| ARP-I94 | Completed-generation publication plus per-camera native visibility gate; controlled withdrawal in native GL Start16 and Vulkan Start23 leaves no mirror color/depth. |
| ARP-I81 | Persistent three-camera/two-slot Advanced owner, matching projective texture/projection publication and native opaque shading; bounded Vulkan and GL output cohorts pass. |
| ARP-I90 | Stable imported metadata and reclamation around retained snapshots; selected cube creation/repeated poses pass in texture-free and 79-draw streamed Vulkan scenes. |

ARP-I95 owns the separate large-scene multi-output storage fix required by
ARP-V36. Exact database/publication payloads should be shared within the same
frame-slot generation, with independent Views, FrameMetadata, global descriptor
sets and entry/use receipts. Preserve the scene payload's NativeGeneration and
shared resource descriptor ranges. Texture/sampler cursors must remain monotonic;
a globals-only rollback must not clear resident scene mirrors it did not mutate.
Different publications retain independent immutable payloads. Do not increase
caps or switch buffers beneath readers to mask the failure. Validate both the
same-publication main/mirror case and changing-publication transitions.

The reverse-depth legacy grid overlay remains an ARP-V37 observation, and full
negative-one-to-one culling/producer support is outside this zero-to-one cohort.
Hardware XR, arbitrary multi-owner pressure and fault injection remain under
their existing validation rows. Phase 6 XR validation is the next requested
workstream. No commit or push was created.

The task-owned editor `arp-mirror-placement` was stopped after Start23; its
manifest reports Stopped with no process ID. Final source whitespace checks pass,
temporary shader/fence tracing is absent, and the tracker has unique task IDs and
anchors: I106 completed/1 remaining (new I95), A9/0 and V22/67. The older inactive
scratch-directory removal was rejected by automatic approval review as blocked
by policy; that directory remains in place.

## ARP-I95 follow-up and Phase 6 continuation

The follow-up request explicitly includes ARP-I95 and then Phase 6. Vulkan now
shares the exact database/publication scene payload within a slot generation.
Each output still receives independent Views, FrameMetadata, global descriptors,
and a native-use receipt. The frame-slot lifetime collector deduplicates only
the complete native publication state, so shared scene identity cannot discard
another output's globals ownership. Shared reuse revalidates mutable sources,
preserves NativeGeneration and resource descriptor ranges, advances descriptor
cursors monotonically, and rolls back only its unpublished globals tail.

Occupied-slot pressure records bounded capacity demand for the next completed
slot boundary. Only that empty boundary may select a larger reserved group;
the existing 128 MiB per-slot ceiling and aggregate arena budget remain in
force. The exact aggregate must fit the ceiling before pressure is retryable,
preventing a permanently oversized multi-publication frame from retrying forever.
Build29 includes this final boundary-growth addition and passes with zero warnings/errors (39.21 seconds). Start29 repeats the 81-draw streamed mirror: capture 1,012 advances during read-back, both exported images were inspected, and retirement at capture 1,140 leaves all three banks null with no queued release or failure.

Build24 and Build25 passed with zero warnings/errors (25.68 and 25.39 seconds).
Build25 includes the independent native-use lifetime correction. Its Start25
created a mirror before the animated model import completed: the native scene
grew from 3 to 81 draws while captures advanced from 13 to 374. Adding a second
mirror and two diagnostic cubes produced 84 resident draws. Both outputs kept
advancing through eight object/camera/resolution changes; the first output read
back 192x256 at capture 551 and the second remained 256x256 at capture 120.
Final retirement drained both banks completely at captures 686 and 249, with
all view slots null, no queued release, no output and no capture failure.

Start26 repeated the original failing order using Build25: stabilize all 78
imported draws, then create the mirror. The diagnostic reports **1,056 bytes**
of additional output globals, **36,621,472 bytes** total retained in the
**67,108,864-byte** slot. The 81-draw mirror advances from capture 2 to 516 and
then 1,603 before complete retirement. The exported reflections were inspected.
No occupied-pressure rejection occurred in these corrected cohorts, so their
changing-publication continuity is not evidence of an injected rejection.
The imported avatar's deformation/scale appearance remains outside this mirror
storage acceptance; the large geometry stays resident to reproduce its memory
load. Reports are `24-streamed`, `25-after-import`, `25-first-with-second`,
`25-resize-mutation`, `25-second-after-mutation`, and `26-stable-import-sustained`;
their JSON files reference the inspected PNGs. Per-session logs and retirement
read-backs are retained in this task's ignored evidence root.

Phase 6 still has 20 unchecked XR validation rows. Passive submission diagnostics
are being added to expose actual receipt shape, eye/image ownership, timeline
completion and retirement in the smoke summary. Existing service PID43684 came
from the stopped `vulkan14-h-code-xr` editor session and is not owned by this task.
Source inspection found that `MonadoOpenXR` settings normalization may stop a
matching existing service even without the smoke runner's `-StartService` flag.
Observational runs therefore use the named editor with generic `VR.Mode=OpenXR`
and an explicit Monado manifest; they do not enter Monado service management.
Service restart approval and real headset availability were requested separately.

The earlier scratch deletion returned only `blocked by policy` from automatic
tool review. No specific rule was disclosed; the repository itself authorizes
bounded inactive scratch cleanup. This continuation reuses the existing run root.

### Phase 6 first live baseline (Start30)

Build29 was launched through the same isolated session with generic OpenXR,
explicit Monado manifest, Vulkan DefaultRenderPipeline, sequential views,
asynchronous submit and passive `XRE_OPENXR_SUBMISSION_VALIDATION=Observe`.
The existing Monado PID43684 remained unchanged. Start30 completed 3,720 runtime
frames but submitted **zero projection layers**; both preview capture tools
correctly rejected the missing fresh output. The submission ledger recorded
zero accepted submissions, capacity 3 and admission high-water 1. Teardown
remained pending behind retired child image views. This run is a failed baseline,
not submission or lifecycle acceptance.

The first two empty-operation frames were startup. Subsequent eye plans had
21 operations but native scene preparation used logical plan slot 0 while the
XR command recorder owned dedicated resource slots 3/4. Slot 0 was already
submitted by desktop rendering. Follow-up inspection also found fixed desktop
capacity in the scene-publication, visibility and native-use lifetime services,
even though mapped arenas had reserved five slots. The correction separates
logical plan identity from native resource ownership, aligns all slot-indexed
capacities, and requires retirement/rejection to release the exact eye owner.

Independent lifecycle review found safe pre-detachment retirement deferrals
were reported as post-detachment failures, unnecessarily stopping a usable
session. The API now distinguishes those outcomes and resumes pacing on a safe
deferral. Cold XR retirement diagnostics are separate from desktop counters;
session epochs prevent an earlier teardown from certifying a later session.
Those changes still await the next combined build and live rerun.

The passive observer is selected by `XRE_OPENXR_SUBMISSION_VALIDATION=Observe`
for a bounded OpenXR smoke run. `Disabled` is the default; other reserved
scenario names reject configuration until their runtime seams are implemented.
Schema 11 adds exact submission ownership and separate XR retirement snapshots.
MCP `get_openxr_runtime_diagnostics` reads these without waiting for completion;
`request_openxr_session_exit` calls the real runtime exit request and leaves
completion observable asynchronously. These are diagnostic/validation surfaces,
not a runtime or headset substitute.

Device-loss tracker ownership now has a separate abandonment count and ledger
flag, driven only by an observed device fault. It does not certify completion,
retirement or arena reuse. The complete swapchain/input/session-parent abandonment
path is still unresolved at the runtime validation boundary; XR-V21 stays open,
and binding teardown retains those parents rather than reporting a normal drain.

### Phase 6 second baseline (Build32 / Start32)

The combined editor build passed with zero warnings/errors (65.05 seconds).
Start32 reached a running/focused Monado session but still submitted zero layers.
The resource-slot correction removed the reserved-cursor failure; the next
failure showed that the frozen graph's native-compute resource names use the
authored logical slot (0–2), while scene publications and arena lifetimes use
the dedicated native XR slots (3/4). Only graph-closure lookup returns to the
logical slot. Native scene storage, descriptor addresses and lifetime receipts
retain the actual resource slot.

The new MCP read-back and real `xrRequestExitSession` request worked. Shutdown
reported one queued, zero drained swapchain generations, with `ChildResources`
as the last observed blocker. Completion tickets and dependency checks were
ready, but image-view scanning had exhausted the final production frame's
allowance. With desktop production stopped, no new accounting boundary arrived.
Terminal teardown now starts one fresh ordinary bounded interval after the XR
submission drain succeeds. It runs on the owning render thread, uses a separate
negative serial domain and preserves all native lifetime proofs. Ordinary
polling and live resolution replacement do not reset the budget this way.

Recording cancellation also marks unsubmitted cached primaries dirty before
releasing their native publication owner. Registration must succeed before any
native submit or upload-list transfer; the single-eye path's reservation now
stays inside the cleanup scope when recording throws. These follow-up changes
and the device-loss settlement serialization await Build33/runtime validation.

### Build33 / Start33: accepted submissions and terminal drain

Build33 passed with zero warnings/errors (49.63 seconds). The run submitted
5,592 projection frames. Its retained receipt sample contains paired-eye
submissions with slots 3/4, both prepared inputs and accepted-incomplete ownership,
followed by two preview-copy submissions. Those sampled rows reach real timeline
completion and exactly one retirement with no early-settlement violation.
The 32-row observer's count fields incorrectly stop at retained sample capacity;
the follow-up separates run totals from the bounded detailed ledger. Overflow
is omitted diagnostic rows, not missing native submission ownership.

Both preview images are fresh 896x1007 captures but contain only zero RGBA.
Moving the unit box out of the origin and repositioning the desktop camera
produces an inspected desktop cube/grid image; the eye preview remains black.
Visual acceptance therefore remains open. The direct preview path does not
allocate the separate desktop-mirror texture under this configuration, and its
capture tool correctly reports that absence.

A repeated accepted-publication warning comes from an independent eligibility
error: sealed submission admits an `OpenXrRuntimeReleasePending` image exit,
while its publisher supports only `EngineOwned` exits. Eligibility now checks
external ownership at both entries and exits so XR transitions use the full
image-state publisher. This change still needs a fresh build/run.

After a live resolution request and real `xrRequestExitSession`, read-back
reports `TeardownCompleted=true`, current/normal epoch 1, one normal teardown,
zero active/reserved/pending submission entries and no pending runtime-acquired
images or XR generations. Two generations queued and drained, with a high-water
of one. The final terminal meter serial is -94; device-wide idle calls remain
zero. This closes XR-I15's starvation defect, while the broader resolution and
STOPPING fault/timing validations retain their full acceptance criteria.

### User-requested pause / final Build35

The user asked to wrap up before the next GPU capture. Start34 never launched:
its build found CS0136 from two pattern variables named `acceptedTicket`. The
async/synchronous finalizers now have distinct local names. Build35 rebuilt the
editor into the same isolated artifacts directory **without starting it** and
passed with zero warnings/errors (49.75 seconds). Its log is
`logs/build-35-wrap.log` under this task's evidence root. The named editor has
no process; its manager manifest still records the failed Start34 build, while
Build35's separate successful build log is the final compiler evidence.

Build35 includes these changes after the last live Start33:

- Sealed image-state admission accepts only engine-owned entries/exits. Runtime
  image ownership transitions use the complete publisher.
- Submission totals count events independently of the retained 32-row ledger.
  Acceptance, rejection, publication failure, completion, retirement and
  abandonment are counted once per selected submission.
- Accepted entries remain `PendingCommit` until gateway publication and receipt
  observation finish. Async ownership opens to polling after its
  accepted-incomplete observation; synchronous ownership opens before waiting.
  A gateway escape retains the accepted owner for explicit recovery.
- Normal settlement and device-loss abandonment share an outer settlement
  monitor, with deferred reentrant abandonment and loss guards between cleanup
  stages. Existing terminal trackers remain retrievable for caller cleanup;
  new tracker creation is prohibited after loss. Invalid/terminal registration
  returns false before native submission.

Those changes pass independent source review and Build35 but have **not** been
run live. No test files were added or modified by this task; unrelated existing
checkout changes were preserved. No commit or push was requested or performed.

Resume with these bounded steps:

1. Start the same named editor through `Manage-McpEditorSession.ps1` and repeat
   the generic OpenXR sequential fixture. Verify zero accepted-publication
   warnings, truthful totals above 32, exact paired/preview settlement and fresh
   images. The output helper is `scratch/Capture-XrEvidence.ps1`; the profiler
   tool is `get_render_profiler_stats`.
2. Resolve black eye output. Desktop cube/grid output becomes visible after
   moving `UnitBox` to `(0,1.5,-5)` and pointing the editor camera from `(3,2,2)`
   toward it; fresh eye previews remain all-zero RGBA. Inspect the actual eye
   render targets and final output path. `rdc doctor` passes with RenderDoc
   1.44; no capture was produced before this pause. The session-only capture
   environment prepared for Start34 was `ENABLE_VULKAN_RENDERDOC_CAPTURE=1`,
   `VK_IMPLICIT_LAYER_PATH=C:/Users/dnedd/AppData/Local/rdc/renderdoc` and
   `XRE_VULKAN_RENDERDOC_FRIENDLY=1`.
   Final read-only review located a concrete routing defect: the diagnostic
   reports `sw=None`, `scene=0`, `fboD=12`, `ops=21`. The final
   `RenderToWindow_FxaaOutputTexture` draw survives view slicing, but
   `VulkanCommandRuntime.MeshMaterialOperations.cs` falls back to global
   `CommandBuffers.BoundDrawFrameBuffer` instead of `ActiveBoundDrawFrameBuffer`.
   The latter honors the OpenXR thread-local unbind. This correction is recorded
   as XR-I18 and was not applied after the user's pause. Verify FXAA content,
   the final draw's actual runtime-image target and nonzero fresh preview pixels.
3. Do not enable completion-hold injection yet. Eye recording currently reuses
   fixed native slots 3/4 and can independently reopen them on real completion.
   Withholding tracker retirement could let an older owner reset a newer use.
   Slot rotation tied to admission identity, or a per-use epoch plus a sole
   reset authority, is required before the capacity-hold validation is safe.
4. Complete device-loss parent abandonment explicitly. Tracker abandonment is
   separate from normal completion, but swapchain/input/session parents still
   retain their handles behind a failed teardown. Do not certify XR-V21 from
   the tracker-only path or discard parent handles without an authoritative
   abandonment result.
   A final review also identified an invalid-receipt edge in
   `CommitAcceptedSubmission`: close the admission slot immediately when native
   acceptance is marked, before completion semaphore/value validation. If that
   validation fails, keep the entry pending/quarantined so caller cleanup cannot
   cancel arena preparation after native acceptance. This remains unmodified at
   the pause and belongs to XR-I16's terminal ownership work.
5. Current production routing always submits both eyes; the single-eye method
   has no ordinary setting selector. A real one-view branch or a narrowly
   scoped validation route is required for XR-V02. Paired eyes use separate
   physical planner allocators despite sharing authored graph slot names.

The existing Monado PID43684 was not stopped or adopted. Permission for service
fault/restart validation and physical headset availability remain unanswered.
The scratch deletion's automatic reviewer disclosed only `blocked by policy`;
the precise blocking rule remains unknown. No retry, bypass, or policy change
was made. All current evidence reused this task's existing scratch root.
