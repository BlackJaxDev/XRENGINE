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

### Resumed after remote master pull

The user restored shell/filesystem access and requested remote master followed
by all Phase 6 work. The checkout had no tracked local changes. SSH fetch failed
with public-key authentication; HTTPS to the same repository fast-forwarded
master from `51ffc79f8` to `e72ef7ce5`. The saved origin URL is unchanged and
submodules were not recursively updated. The new AGENTS allocation rule was read.

XR-I18's fallback now uses `ActiveBoundDrawFrameBuffer`, respecting the XR
thread-local unbind. The tracker also closes accepted admission before validating
completion data so a corrupt receipt remains quarantined. The next live baseline
is Build36/Start36. Further diagnostics and lifetime work will be built separately.

No Monado service remained at resumption. The existing service helper started
a stationary simulated HMD with this task's marker at
`reports/phase6-owned-monado.json`; startup reported PID10148. Generic OpenXR
settings continue to avoid implicit service management. The user identified
SteamVR as the available physical OpenXR runtime for the final hardware check.

### Resumed Build38 and Start38–40 evidence

Build36/37 encountered incomplete control-plane changes from another active task;
those files were preserved. Build38 passed with zero warnings/errors in 71.57s.
It includes the final target lookup, real frame/view/image provenance on preview
submissions, the ordinary serial-eye route, and an immutable direct-eye emitter.

Start38 submitted 2,267 projection frames. A real `xrRequestExitSession` first
reported pending child retirement and incomplete teardown, then normal epoch 1
completed with one generation queued/drained, no acquired swapchains, no pending
tracker ownership, and 6,801 accepted/completed/retired submissions. Publication
failures were zero. This closes XR-I17; the black output keeps visual validation
open. Evidence: `mcp-output/38-after-exit.json`, `38-after-exit-final.json`,
`logs/build-38.log` and `logs/engine-38` in the task evidence root.

Start39 loaded RenderDoc successfully, but Monado starts/ends a capture around
every XR frame whenever RenderDoc is loaded. The capture overhead disturbed
pacing and produced desktop-only evidence; it does not validate XR output. One
54,208,537-byte capture is retained as `renderdoc/phase6-39-eyes.rdc` with an
inspected desktop export. The 98 task-owned temporary captures (about 5 GiB)
were deleted after copying that capture. The exact-path cleanup succeeded under
the current permissions; no specific cause for the earlier rejection is known.

Start40 used Build38 without RenderDoc. It reached 8,310 projection frames and
24,930 accepted/completed/retired submissions with zero publication failures.
Per-eye visible/draw counts were four. Fresh readbacks of both post-processing
and upstream resources were black: `HDRSceneTex`, FXAA/post/final, visibility
identity/metadata/selection/depth, and shading diagnostics. The visibility PNG
was visually inspected. Crucially, MCP identifies the eye pipeline as
`RvcRenderPipeline`, with 57 resources including the Advanced visibility and
shading resources; the desktop fixture's Default pipeline is not the eye pipeline.
The target-binding correction alone therefore does not close XR-I18. Evidence:
`mcp-output/40-left-resources.json`, `40-left-post-textures.json`,
`40-left-hdr.json`, `40-left-visibility.json`, `40-before-exit.json` and
`logs/engine-40`. The named editor was stopped after these captures.

V06 capacity ownership guards, V13 timing/reuse evidence, V14 allocation counters,
V07 one-shot failure seams, and I16 device-loss parent settlement are separate
source work after Build38. They require a fresh build and runtime evidence; no
tests have been added or modified.

### Build44 allocation evidence and SteamVR Vulkan negotiation

Build42 compiled rendering but hit a concurrent networking API mismatch in the
editor. The stale two-argument `LeaveInstance` call now constructs the existing
request DTO, preserving its two values. Build43 overlapped the new lifecycle
validation partial before its OpenXR namespace import was complete. Build44 then
passed with zero warnings/errors (67.74s). Its Monado Observe run, with verbose
Vulkan tracing disabled, recorded **zero managed bytes** across 5,660 warmed
registration calls, 9,435 polls, and 5,661 retirement calls. All 5,724 accepted
submissions completed and retired; real session exit completed normal epoch 1.
Per-poll native timeline queries still duplicated matching semaphore observations;
the subsequent change shares one actual observation within each poll only.

The Monado service helper had a UTC conversion defect: PowerShell deserialized
the timestamp as `DateTime`, then a string cast lost its UTC kind before a second
parse added the local offset. The helper now preserves `DateTime`'s UTC value.
The original task-owned PID10148 was verified against its exact executable and
start time, then the corrected helper stopped it successfully. A fresh owned
service started as PID43392 with the same task marker.

SteamVR Start41 failed in the engine's version guard before native graphics
creation. The runtime advertises Vulkan 1.0–1.2, but the
[OpenXR graphics requirements specification](https://registry.khronos.org/OpenXR/specs/1.1/man/html/XrGraphicsRequirementsVulkanKHR.html)
defines the maximum as the highest tested instance version and explicitly permits
newer compatible versions. The previous statement that this proves SteamVR cannot
use Vulkan 1.4 was incorrect. The guard now warns above the tested ceiling while
retaining actual Vulkan 1.4 loader/device/features and all native creation checks.

Build45 passed with zero warnings/errors (72.49s). SteamVR Start45 successfully
created a focused session and two 2688×2688, three-image swapchains. Logs report
loader 1.4.350 and physical-device API 1.4.341. This closes XR-I19's initialization
defect. It does not close hardware rendering: the early run has no accepted eye
submissions and reports `The exact canonical scene publication is unavailable
for advanced preparation` for each native eye stage. The version retry therefore
reaches the rendering problem instead of failing a speculative compatibility gate.
Evidence: `logs/41-editor.stderr.log`, `logs/build-44.log`,
`mcp-output/44-after-exit.json`, `mcp-output/45-steamvr-runtime.json`,
`45-steamvr-warm.json`, `45-steamvr-advanced.json` and the PID42884 session logs.

### Planner identity and instrumented eye preparation

The later Start45 diagnostic retry reached accepted native preparation, then
rejected the paired logical plan because independent eye planners had revisions
2 and 1 (21 and 22 operations). Those revisions are local to their owners and
need not agree. The correction retains each eye's normalized logical-view key
and validates its own frozen graph, revision, planner signature and allocation
signature. Desktop/mirror inputs retain their existing global revision check.
This correction still requires a fresh runtime result.

Start46 used the actual single-eye route with SteamVR and GPU counters enabled.
It recorded two projection frames and 16 accepted/completed/retired submissions
before repeated preparation failures; its preview readback timed out. It is not
successful single-eye output evidence. The named editor was stopped.

Build47 passed with zero warnings/errors (71.64s). Its Monado run with
`GpuIndirectInstrumented` exposed a separate frozen-publication mismatch:
the left-eye operations requested pass signature 1714642880, while that exact
left owner had published 1847552347. The former signature appeared on the right
owner's publication. No submission was accepted and no GPU counter receipt was
available. This is a planning failure before GPU execution, not evidence about
the black visibility buffer's GPU cause. The first failure is retained in
`logs/engine-47/log_vulkan_start.log`; runtime snapshots are
`mcp-output/47-runtime.json` and `47-advanced.json`.

The retained Build44 ledger independently supports timing and allocation work:
32 rows have real frame IDs 8–18, populated predicted display times, and strictly
ordered submit-start, submit-end and observed-completion timestamps. Every row
is accepted, shape-matching, ownership-intact and retired, with no cancellation,
abandonment or early settlement violation. First-use image ages are unknown;
subsequent paired-eye image reuse ages are independently three frames while
indices cycle 2, 0, 1. Forced-wait fields are zero because that cohort did not
force a wait. V13 therefore still needs the pressure cohort's actual wait trace;
V14 still needs fresh evidence after the per-poll query deduplication change.

Build48 found a by-reference property argument error in the new validation;
the argument now uses a local value. Build49 passed with zero warnings/errors
(49.30s), but the exact graph-reference check rejected all native eye recording.
Build50 added cold failure details and passed with zero warnings/errors (52.88s).
Its diagnostic showed only the graph object changed: revision, planner signature
and allocation signature were identical. Native resource realization can refreeze
one eye's graph while preparing the other eye, so an earlier captured graph is
not the final sealed graph even when physical ownership is unchanged.

The subsequent correction validates the original stamp/state, allocator object,
allocator ownership ID, revision and both signatures, then carries the exact
`FramePlan`-owned `ResourcePlannerRuntimeGeneration` into the recording worker.
The command stamp uses that generation's immutable graph. The final graph-reference
check remains mandatory. Source review found no added hot-path allocation; Build52
is the first runtime attempt with this correction.

Start51 used Build50's three-command mirror/publish route. It accepted no work
because the frozen graph lacked required Advanced native compute resources.
This is separate from successful output or pressure validation. Starts49–51 were
stopped through the named session manager; their failure evidence is retained in
`mcp-output/49-runtime.json`, `50-runtime.json`, `51-runtime.json`,
`logs/engine-49/log_vulkan_start.log`, and `logs/engine-50/log_vulkan_start.log`.

Build52 passed with zero warnings/errors (51.34s), but continuity validation
still rejected the eye. Build53's expanded diagnostic establishes why:
the coherent producer stamp used live allocator owner 29, while the sealed
publication used owner 20. Both had identical revisions and signatures.
Eye authoring had published the exact keyed owner inside its pipeline resource
scope; later preparation ran on the restored outer eye scope and created a
duplicate allocator. Preparation now re-enters the selected pipeline's exact
resource scope, checks its failure state, realizes resources, reconciles layouts,
and captures its final state before recording. The sealed-generation adoption
still handles later graph refreezes. This is source-corrected but awaits Build54
runtime evidence. The exact owner diagnostic is retained in
`logs/engine-53/log_vulkan_start.log`.

Build54 encountered a concurrent networking compile error: a duplicate recursive
`HandlePlayerLeave` stub. Removing that stub preserves the existing implementation.
Build55 passed but still selected sealed owner 20 instead of prepared owner 28.
The logical-plan builders substituted the global planner switching map for the
map owned by the exact eye. Single-eye planning now retains the eye's captured
map; paired planning needs exact lookup across both captured maps without merging
them or falling back across owners.

Build56 passed with zero warnings/errors (67.69s). The ordinary single-eye run
submitted two projection frames and accepted/completed/retired 12 eye and preview
submissions. Its ledger confirms intact ownership and no early settlement, but
rendering then stopped advancing after foreground pipeline-preparation exceptions.
The retained pending XR frame was retried with no new eye operations; a preview
readback timed out. This is not successful output acceptance. The named session
was stopped. Evidence: `mcp-output/56-runtime-warm.json`, `56-left-preview.json`,
`logs/engine-56-start.log`, and `logs/engine-56-tail.log`.

Build57 passed with zero warnings/errors (77.31s). It includes paired lookup
across the two eye maps, exact graph/allocator ownership, and cache checks for
the map and both signatures. A final provenance guard was initially too broad:
desktop resource publication clones the map but shallow-copies historical
entries, which intentionally keep their historical embedded map. The next
build applies map-container equality only to paired authority; exact graph,
allocator liveness and ownership ID remain required on every path.

The same run confirms the frame-failure cleanup gap and its bounded correction.
After consuming `_framePrepared`, `RenderFrame` now owns finalization through
`try/catch/finally`: it attempts a no-layer end on healthy pre-end failure,
preserves the original exception, discards only pending history, and releases
pacing. One attempt marker prevents a second native end after a post-call
exception. Start57 advances through 47 no-layer frames with zero EndFrame errors
despite the planner rejection; it accepts no GPU work and proves no visible
output. Final review additionally orders all old-frame/loss writes before the
pending-frame admission publication and rechecks health after preparation
admission. Build58 is the first run with those last ordering/provenance changes.
Evidence: `logs/build-57.log`, `logs/rendering-57-start.log`,
`mcp-output/57-runtime-final.json`, and `reports/phase6-57-single-summary.json`.

### Visible paired output and bounded tracker acceptance

Build58 passed with zero warnings/errors (71.74s). Its ordinary single-eye run
exposed XR-I21: the eye's paced published collection is compared against the
independently advancing desktop consumed-collection generation. The later log
example has package collect 187171 versus desktop consumed 198494, with matching
resource/descriptor generations. A separate exact collection/package authority
is needed; relaxing the validator or fabricating an output request is inappropriate.

Start59 uses Build58 with sequential paired submission (`serial=0`, `mirrorFBO=0`).
Both 896×1007 RVC eye previews show the red cube with stereo parallax. The left
and right render/copy IDs agree at 907/911. After moving the cube, the later left
capture at frame 4104 changes pixels and position. The desktop capture also shows
the cube and grid. These PNGs were opened and inspected. Exact planner ownership,
active framebuffer state and graph-generation continuity close XR-I18; paired
receipt/output and preview ownership close XR-V18/V19.

The 32 retained receipt rows contain 11 paired and 21 preview submissions. All
are accepted, shape-matching, pins-transferred, ownership-intact while incomplete,
actually completed and retired, with no early-settlement violation. Each paired
row releases two recorded/prepared owners and resets two frame slots once; each
preview row releases one temporary command once. The final real session exit
retires all 17,829 accepted submissions, drains two queued generations and reaches
normal teardown epoch 1 with zero active/reserved/pending-commit ownership. There
are no Vulkan validation errors or publication failures in this cohort. Native
slot ownership and the reviewed shared runtime queue gates close XR-I13/I20.

XR-V14's warmed observation reports 3,203 registration calls, 5,339 polls and
3,201 retirement calls with zero allocated bytes/high-water in every category.
The 5,338 timeline queries across those polls support shared-semaphore query
deduplication; an empty poll needs no native query. Retained storage limits are
64 uploads, three command buffers, three slots, 64 image records and 32 ledger
rows. The three active entries at the intermediate snapshot are a live tail,
not orphaned work; final teardown settles them.

Start59 also exposes two outstanding lifecycle limits. Repeated active-session
resize attempts safely defer before detachment but starve because each queued
attempt observes another begun XR frame; the admission boundary needs a pending
replacement intent. After normal XR teardown, desktop completion maintenance
attempts to obtain a live XR view set that has already been cleared. This is a
separate remaining cleanup failure despite truthful zero XR ownership.

Evidence: `mcp-output/59-runtime-warm.json`, `59-final-runtime.json`,
`59-left-preview.json`, `59-right-preview.json`, `59-left-shifted-later.json`,
`59-main.json`, `59-after-exit.json`, `logs/general-59-resize-tail.log`, and
`logs/vulkan-59-tail.log`. The sampled desktop Vulkan transaction has acquire
0.0092ms, slot completion wait 0.0191ms and native present 0.0457ms; the coarse
16.35ms “Present” label includes the entire transaction, chiefly CPU preparation
and recording. XR deadline correlation remains XR-V15/V16 work.

### Submission capacity and rejection fault runs

Start60 holds completion observation on real accepted work until the tracker
reaches its capacity of three. It records high-water three, one visible admission
deferral, released hold and continuing projection output. The first retained
paired/preview receipts retain ownership while completion is unobserved and
settle only after the actual completion query; no early-settlement violations
occur. Real exit completes/retires all 5,157 accepted submissions and reaches
normal teardown epoch 1 with zero active/pending ownership. No forced wait was
needed in this scenario. Evidence: `60-held-runtime.json`, `60-before-exit.json`
and `60-after-exit.json` under `mcp-output/`.

Start61 rejects one paired submission before the native call. Its receipt has
no submit/completion timestamp, semaphore or timeline value; it is cancelled
without transferred pins or false GPU completion. Its two recorded/prepared
owners and slots are released once with zero early-settlement violations.
Rendering recovers, and real exit reports 1,272 accepted/completed submissions,
one rejection and 1,273 total retired records, with zero remaining ownership.
The accepted-publication boundary is exercised separately below. Evidence:
`mcp-output/61-rejection-runtime.json` and `61-after-exit.json`.

Start62 injects one publication failure after the paired native submission was
accepted (serial 1, frame 8). Its real submit interval, semaphore and timeline
value remain attached; transferred pins survive the accepted-incomplete
observation. Actual completion releases the two recorded/prepared owners and
slots once, with one retirement callback and no early-settlement violation.
Real exit completes/retires all 3,783 accepted submissions, reports exactly one
publication failure and zero rejection, and reaches normal teardown epoch 1 with
zero active/reserved/pending-commit ownership and zero pending generations.
This and Start61 close XR-V07 without equating pre-submit cancellation with GPU
completion. Evidence: `mcp-output/62-publication-failure-runtime.json` and
`62-after-exit.json`.

### Exact XR package consumption authority and serial validation

The direct eye path now captures an immutable internal authority containing its
effective command collection, published package generation and collect generation.
Capture and revalidation use the existing collection read scope. The pipeline
receives that collect generation independently of desktop collection; exact-output
requests still require matching identity. Missing or changed authority fails
explicitly. No global generation or package validation rule was weakened.

Build63 initially caught an explicit `in` argument applied to a property; omitting
that modifier lets C# supply the readonly parameter's stack copy. The corrected
build passes with zero warnings/errors in 50.45 seconds. Start63 submits ordinary
serial eyes while desktop collection advances. Both eye PNGs were opened and
inspected: red cube, stereo parallax, matching render/copy IDs. The first left
capture preceded application of the asynchronous cube move; the later frame
9690 capture shows the moved cube, as does right frame 8763. The retained ledger
contains 16 single-command eye receipts and 16 preview receipts. Eye ownership
remains intact while accepted-incomplete, then releases its recorded owner and
mapped/data slot once on actual completion. No Vulkan validation errors were
found in the observed run.

Real session exit completes/retires all 5,106 accepted submissions, with zero
active/reserved/pending-commit records, zero publication or EndFrame failures,
and normal teardown epoch 1. This closes XR-I21 and XR-V02. Evidence:
`mcp-output/63-{runtime,warm-runtime,left-later,right,after-exit}.json`,
`logs/build-63.log`; exact engine log session is the named MCP session's
`logs/XREngine.Editor_debug/windows_x64/` directory ending in `pid47912`.

### Parallel recording and combined mirror publication

Start64 uses Build63 with `ParallelCommandBufferRecording`, asynchronous
submission and mirror-FBO mode disabled. Both eye PNGs were viewed: the red cube
has stereo parallax and exact render/copy frame IDs (2272 left, 2276 right).
Eleven retained paired-parallel receipts (shape 3) each contain two commands,
two recorded/prepared owners and two slots. Ownership survives the real
accepted-incomplete observation, then settles once with no early violations.
The fixture has no pending texture uploads. Real exit completes/retires all
2,592 accepted submissions and reports zero active/reserved/pending ownership,
normal teardown epoch 1 and no EndFrame failure. This closes XR-V03. Evidence:
`mcp-output/64-{runtime,warm-runtime,left,right,after-exit}.json`.

Start65 enables mirror-FBO mode for the three-command path. No submission was
accepted: the mirror helper still used desktop collect authority and rejected
the independently paced XR package. Frame failure cleanup keeps no-layer frames
advancing; real exit reaches normal teardown epoch 1. The mirror helper now
uses the same exact package capture/validation API, with its target framebuffer
passed explicitly. Runtime acceptance of that extension remains pending.

### Runtime Vulkan queue synchronization audit (source and runtime evidence)

The session and engine select the same Vulkan graphics family and queue index
zero. Engine submission/presentation acquires device admission for reading, then
the queue gate. The four OpenXR calls that may use that queue bypassed both;
dedicated/CollectVisible frame preparation can call `xrBeginFrame` on another
thread, making this a real synchronization gap (XR-I20).

The [OpenXR Vulkan concurrency and swapchain contract](https://registry.khronos.org/OpenXR/specs/1.1-khr/html/xrspec.html#XR_KHR_vulkan_enable2)
requires host synchronization for begin/end/acquire/release and permits release
while previously submitted commands are incomplete. The runtime does not need
access to the engine's private retirement timeline. The correction will acquire
the same two engine gates in their established order around each relevant native
call, release them in reverse order, and leave `xrWaitFrame`/`xrWaitSwapchainImage`
outside that scope. Failed device admission will remain an explicit graphics
failure, without inventing an OpenXR result. Build56 includes the wrappers and
passes with zero warnings/errors. Source review confirms matching queue identity,
lock order, exception unwind and no new happy-path allocation. A follow-up makes
the failure diagnostic read the current device state after admission and releases
admission in `finally`. Steady-state and lifecycle runtime validation remain pending.

### Retained STOPPING acceptance scope

The Start38 real session-exit cohort closes XR-V12 for pending generation
retirement. `38-after-exit.json` records Visible → Synchronized → Stopping → Idle →
Exiting, one queued/pending generation, zero drained generations, blocker mask 64
and `teardownCompleted=false`. `38-after-exit-final.json` records one drained
generation, zero pending generations/blockers, normal teardown epoch 1 and
`teardownCompleted=true`. Both snapshots report 6,801 accepted/completed/retired
submissions and zero active/reserved/pending-commit submissions. No incomplete
GPU work is claimed at STOPPING; the explicit pending work is generation
retirement. Start44 independently finishes normal teardown with 5,724 completed
and retired submissions and zero remaining generation ownership.

### Build67–70 follow-up evidence

Build67 passed with zero warnings/errors in 68.19 seconds. Within session epoch
1, the eye resize moved from 896×1007 to 960×1080; both new-size PNGs were
viewed with cube parallax. Two generations retired/drained, device-wait-idle
calls were zero, and exit reached 7,326 accepted/completed/retired submissions,
zero ownership and normal teardown epoch 1. The configured runtime-refresh case
remains unrun, so XR-V09 stays open.

Build68 provides bounded real retirement-pressure evidence: capacity and
high-water were 4, five attempts were observed, one deferred before active
detachment while a held 1120×1080 generation remained live, and the hold then
released normally. A later 1152×1080 generation continued running; five
generations queued and drained. Exit reached 3,315 accepted/completed/retired
submissions with zero ownership. No Vulkan VUID appears in the log. This was
real retirement observation, not injected slow-GPU behavior. XR-V08 is closed;
the hold was released normally and was not a terminal-path proof.

Build69 consumed the post-detachment fault at frame 1112 and still completed
3,300 accepted/retired submissions with normal teardown epoch 1. A later
custom 0×0 trigger was correctly rejected during bootstrap. The run exposed
two empty-view observations without an actual native payload, leaving a
resource-lifetime deferral path open; the empty-payload guard is implemented
but not runtime-validated. XR-V10 and XR-I14 remain open.

Build70 exercised the simulated LOSS_PENDING state at frame 7 with one accepted
tracked pending submission, then completed/retired three accepted submissions
with normal teardown epoch 1. Recovery repeated
`GetVulkanGraphicsDevice` twice with `ErrorValidationFailure`; XR-V20 remains
open.

Mirror66 accepted three shape-5 submissions but exposed missing native compute
slot-3 resources. The logical-slot/native-slot correction and exact keyed
sealed-graph review are implemented/reviewed but unbuilt. These follow-ups are
tracked as XR-I22 (mirror logical/native slot and exact planner graph) and
XR-I23 (stale desktop/XR queue-ownership snapshot/revision); XR-I23 source work
is implemented but runtime review remains pending. The bounded direct-eye
XR-I21 result above remains unchanged.

### Build71–72 follow-up evidence

Build71 recorded 0 accepted submissions, 4,735 no-layer frames and 0 EndFrame
failures. The failure exposed the absence of a prematerialized mirror logical
slice and desktop prewarm viewport-extent leakage. The source correction now
records the exact `renderingOperations.Stream` and isolates prewarm thread
render state.

Build72 then passed cleanly with 0 warnings/errors in 42.51 seconds after a
transient concurrent license-copy failure on the first attempt. PID43208
accepted three mirror submissions, then remained pipeline-pending before
`vkBeginCommandBuffer`; final exit nevertheless reported three accepted and
retired, zero active/reserved/pending-commit submissions, zero pending retired
swapchains, 1,892 no-layer frames, 0 EndFrame failures and normal teardown
epoch 1. Source loss/cleanup review is approved. XR-I22 and XR-V04 remain
open because the healthy three-command acceptance was not obtained.

Build72's desktop after-exit screenshot was viewed with the live cube/grid;
the stale immutable-view-set exception was no longer reported. XR-I23 source
review confirms reset and unleased logical-plan release, but it remains open
until healthy cohort exit/restart evidence is obtained from the live73 run.
