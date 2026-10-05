# Retained rendering hardware validation

Status: active, hardware visual acceptance failed. The user confirms the engine
scene reaches the headset, but Sponza causes predominantly black output,
flickering, and jitter back to old frames; looking at sky works mostly normally.
Non-elevated SteamVR runs a Vulkan SinglePassStereo session; fresh eye output
and one clean runtime teardown are verified. Startup latency,
continuing no-layer frames, motion/orientation feedback, repeated lifecycle and
failure coverage, and OpenGL/OpenVR remain open. The unavailable original stall
recording was waived, not treated as reproduced or fixed.

October 4: the user confirms the headset is now connected and ready in SteamVR,
and explicitly requests the hardware checks. Retry the same strict Vulkan
OpenXR fixture using the retained, validated sampler/storage corrections.
Require effective SinglePassStereo, zero sequential-fallback attempts, advancing
submitted/per-eye/preview frame counts, and valid acquired-image ownership.
Capture and view both eyes in two windows, then stop only the owned editor.
OpenVR's OpenGL compositor path is a separate backend check and cannot substitute
for Vulkan OpenXR acceptance.

## OpenXR through SteamVR

Owner: Vulkan OpenXR graphics binding and Advanced stereo publication. Run the
retained single-directional-light Sponza fixture with Vulkan required,
Advanced/CpuDirect, and the existing strict single-pass stereo setting. Use a
named isolated editor session and per-session VR overrides; do not mutate the
user's runtime or shared unit-world settings.

Hypothesis: retained desktop corrections preserve hardware stereo ownership
and fresh left/right output. Reject on capability rejection, missing eye draws,
stale frame identity, invalid acquired-image ownership, or failed teardown.
Do not silently switch to sequential rendering or another backend. Capture and
view both eye previews and record session/frame diagnostics before and after a
settled interval. Budget: one startup, two diagnostic windows, both eye images,
then owned-session shutdown and log inspection. This does not establish head
motion comfort or user-visible ghosting; those require user feedback.

Evidence root: `Build/_AgentValidation/20261003-142654-vk-todo/`.

The first startup reached MCP but not renderer initialization. The requested
OpenXR transition remains pending, with no API/session or eye submission.
Two managed stack samples place the main thread inside native
`XR.CreateInstance`: first under `QueryVulkanRuntimeRequirements`, then under
`TryGetRequestedVulkanPhysicalDevice`. The different bootstrap callers show
progress between long native calls, not a demonstrated managed deadlock.
Windows' selected OpenXR manifest points to SteamVR.
The running SteamVR processes alone do not establish a ready tracked headset;
the user has been asked for its current status. Stop only the owned editor.
Do not treat this as a stereo rendering pass or silently select another runtime.

Reopen after the headset/runtime reports ready, then retry the same fixture and
collect eye output. Evidence: `reports/openxr-hardware-before.json`,
`reports/openxr-hardware-views-before.json`,
`reports/openxr-hardware-start-stacks*.txt`, and `logs/openxr-hardware-start.log`.

## Ready-headset retry and process privileges

October 4 PID 13960 remains inside native `XR.CreateInstance` under
`QueryVulkanRuntimeRequirements` for more than three minutes. MCP remains
reachable, but reports no OpenXR API/session or submitted eyes. SteamVR's client
log identifies the connection failure: opening the existing server process
returns Windows access denied (5), before instance creation can finish.
The headset is present in the server log; lack of headset readiness is no
longer the supported explanation.

Read-only token queries show the running SteamVR server and compositor are
elevated, while a process launched by the agent tool has a limited, non-elevated
token. These processes are not AppContainers according to the Windows token
query; the runtime's `app container state: 1` message alone must not be treated
as proof of AppContainer isolation. Source review finds no invalid bootstrap
flags, temporary argument lifetime, or engine debug callback explaining this
native connection failure. No renderer/bootstrap changes or fallback were made.

The owned editor is stopped; user-owned SteamVR remains running. The user has
been asked to restart SteamVR normally, without administrator elevation, then
report headset readiness. Retry the same strict fixture after that external
prerequisite changes. Hardware acceptance and both eye captures remain open.

Evidence: `reports/openxr-ready-{toggle,before,views,token-state}.json`,
`reports/openxr-ready-stack.txt`, `logs/openxr-hardware-ready-start.log`, and
`logs/openxr-ready-steamvr-{client,server}.txt` under the existing task run.

## Non-elevated runtime retry

October 4: replacement SteamVR server PID 20932 and compositor PID 20972 both
have limited, non-elevated Windows tokens. Owned editor PID 12480 now advances
past native bootstrap but repeatedly rejects session creation with "bootstrap
lease from an unknown runtime owner". It submitted no eyes and was stopped.

Hypothesis: `TryGetOpenXrBootstrapInstance` returns true for a null lease because
the lifted comparison `lease?.InstanceHandle != 0` is true for null. The caller
then rejects that null as an unknown owner. Require a non-null lease and a
nonzero handle; preserve the existing normal instance-creation path when no
renderer-owned instance exists. Acceptance: clean isolated build, no recurrence
of this ownership rejection, then evaluate strict stereo startup and eye output
through the same live fixture. This is not yet a hardware acceptance pass.

Evidence: `reports/openxr-restarted-{token-state,toggle,runtime,views}.json` and
`logs/openxr-hardware-restarted-start.log` under the existing task run.

The null-lease correction built with zero warnings/errors (19.83 seconds).
Live PID 40000 creates an OpenXR instance and no longer reports unknown runtime
ownership. It then fails resolving `xrGetVulkanGraphicsRequirements2KHR` with
`ErrorFunctionUnsupported`. Session setup unconditionally resolves both Vulkan
extension variants even though native dispatch throws for unsupported functions.
The editor was stopped. Next correction: query the current instance's enabled
extensions through the graphics host and resolve only their entrypoints, retaining
hard failure for an enabled extension whose required function is unavailable.
Acceptance remains the same live strict stereo fixture. Evidence:
`reports/openxr-null-lease-runtime.json` and
`logs/openxr-null-lease-build-start.log`.

## Successful instance/session startup, eye output, and normal teardown

The enabled-extension correction built with zero warnings/errors (77.55 seconds).
Independent read-only review found no further adjustment to either startup fix.
Owned PID 48040 started at 17:45:21 UTC and reached `SessionRunning`/`Visible`,
Vulkan, requested/effective `SinglePassStereo`, implementation
`TrueSinglePassStereo`. Both prior startup failures disappeared. No runtime,
backend, extension-negotiation, or sequential-rendering fallback was forced.

The initial left preview request reported no current copy; the initial right
request timed out after 20 seconds. Do not count those attempts as captures.
Later submitted frames and both per-eye publish counts advanced. Two successive
windows produced 2688x2688 PNGs, all four actually viewed:

| Window | Left preview/frame ID | Right preview/frame ID | Submitted frames at following snapshot |
| --- | --- | --- | --- |
| B | 489 | 491 | 79 |
| C | 695 | 697 | 124 |

Each preview ID equals its reported latest-rendered frame ID; image hashes
change between windows and all RGB samples are finite. Images show textured
scene geometry with distinct eye views. The captured pose appears inverted;
physical headset orientation was not independently established. The user was
asked to move the headset and confirm scene/orientation. The later reply reports
only SteamVR Home and no engine scene in the headset. The owned editor had
already been stopped when that reply arrived; the observation's timing relative
to shutdown is unknown. Do not interpret the preview captures as confirmation
of headset presentation. Reopen the same fixture and keep it running during
user verification, correlating current OpenXR state with SteamVR compositor logs.
This verifies fresh output, not motion response, orientation correctness,
stereo comfort, or temporal ghosting. XR uses its stereo pipeline projection;
these captures are not the matched desktop Advanced HDR comparison.

The run is not a performance pass. First successful captures arrive roughly
three minutes after launch, and no-layer counts continue rising afterward.
At teardown there are 182 submitted frames and 254 no-layer frames. Both eyes
publish 182 times. The submission diagnostic request/ledger was disabled;
zero accepted/rejected/retired ledger counts must not be interpreted as zero
work or proof of submission validation. Forced-wait telemetry is nonzero.
A managed stack sampled mesh auto-uniform/descriptor recording under true
stereo render/publish, showing engine rendering work rather than the earlier
native instance-creation stall. Attribute missing-layer reasons and cold versus
warmed costs before changing rendering behavior or claiming acceptable speed.

The normal editor toggle-off reaches `DesktopOnly`, `sessionRunning=false`,
`teardownCompleted=true`, normal teardown count 1. Each eye has 436 acquires and
436 releases. One retired swapchain generation is queued and drained, with zero
pending generations, zero acquired runtime images, no abandonment, and no
device loss. End-frame failure count and strict sequential-fallback attempts
remain zero; runtime diagnostics report no failures. Only the owned editor is
then stopped. User-owned SteamVR is left running.

Evidence under the existing task run: `reports/openxr-extension-*.json`,
`reports/openxr-extension-stack.txt`, `reports/openxr-live-window-{b,c}.json`,
`reports/openxr-eye-{left,right}-{a,b,c}.json`,
`reports/openxr-live-teardown.json`, `mcp-captures/openxr-live-{b,c}/`,
`logs/openxr-extension-build.log`, and `logs/openxr-live/`.
No tests were added or modified. These bounded completed checks are marked in
the active TODO; wider hardware acceptance remains unchecked.

## Headset presentation verification after the user report

Reopened the same built fixture as owned PID 34588 at 17:51:54 UTC and kept it
running for user verification. SteamVR logs explicitly transition this PID to
`VRApplication_OpenXRScene`, capture scene focus, and receive left/right
2688x2688 projection layers at 17:52:01 UTC. At 17:52:37 UTC, engine diagnostics
report `SessionRunning`/`Visible`, 158 submitted frames, 158 no-layer frames,
141 missed deadlines, zero end-frame failures, and no reported runtime failures.
These establish the runtime connection and layer delivery; user confirmation
of physical display is still required. Do not infer headset success merely
from the OpenXR state name or preview textures.

Evidence: `reports/openxr-headset-visible-{before,waiting}.json`,
`logs/openxr-headset-visible-start.log`, and
`logs/openxr-headset-visible-{client,compositor}.txt`. The test editor remains
running while awaiting the contemporaneous user observation; stop only this
owned session once that observation and any required captures are complete.

The user subsequently confirms headset presentation but reports severe black
flicker and old-frame jitter when Sponza is visible, while sky-only views work
mostly normally. This is a failed visual/performance result, not acceptance.
At 17:55:26 UTC the same live session reports 422 submitted frames, 278 no-layer
frames, 394 missed deadlines, zero end-frame failures, and zero sequential
fallback attempts. Initial hypothesis: geometry-dependent recording/readiness
or deadline pressure is causing omitted layers and stale compositor output.
Correlate actual no-layer exit reasons and recorded CPU/GPU work before choosing
a correction; neither successful xrEndFrame nor fresh preview images proves
continuous headset output. Keep strict SPS and the authored scene; do not hide
the failure by changing backend or silently dropping geometry.

The user also reports the desktop editor is unresponsive and resizing appears
to freeze it. Two managed stack samples show changing CPU recording work
(`TryGetAutoUniformMaterialWritePlan`, then `TrySealSubmissionContract`) under
strict stereo mirror recording; the process still reported responsive during
the first sample. This supports severe starvation, not a demonstrated permanent
deadlock. Profiler logs repeatedly show roughly 400–500 ms render recording
intervals and collect-visible waiting on render. Stop owned PID 34588 after
these samples; no successful resize recovery is claimed.

The missed-deadline counter counts submitted layers only, so it does not explain
no-layer frames. Read-only review identifies two candidates: reuse of a fixed
stereo frame-data slot while its asynchronous submission remains in flight,
and mesh-readiness deadline rejection. Release builds compile ordinary debug
logging out, so enable bounded auxiliary failure diagnostics for one repeat:
at most 64 records, at most one per second, gated by the existing Vulkan
recording diagnostic switch. Record rejection stage and frame/image identities
without changing admission, resource ownership, scene contents, or scheduling.
Success for this diagnostic iteration is attribution of omitted-layer causes;
it is not a behavior fix or performance comparison.

Diagnostic PID 43992 builds cleanly (26.91 seconds). Early omitted layers report
`MeshMaterialization` with pending compatible Vulkan programs. Warmed failure
samples repeatedly report `submission-reservation` before stereo recording;
this is distinct from the earlier fixed-slot hypothesis. At 18:06:20 UTC there
are 732 submitted, 492 no-layer and 729 missed-deadline frames. The bounded log
is sample evidence, not an exact census of failure reasons. Candidate pressure:
the main stereo submission and left/right eye-preview copies share the same
three-slot tracker. Run one diagnostic comparison with only the unit-world eye
preview option disabled, retaining Vulkan, strict SPS, Sponza, desktop rendering
and the diagnostic switch. A reduction in reservation failures would implicate
preview pressure; disabling previews is not an accepted production correction.

The preview-disabled comparison (owned PID 43928, same binary and scene) removes
the observed reservation waits: forced-wait count is zero, compared with 369 in
the preview-enabled run. Omitted layers persist, with warmed samples now reporting
`frame-data-slot`. At 18:09:40 UTC, the comparison has 275 submitted, 245 no-layer,
and 273 missed-deadline frames. This implicates preview admission pressure while
also establishing a separate completion-owned stereo frame-data bottleneck.
Neither disabling previews nor bypassing that ownership guard is a correction.

A 15-second `dotnet-sampled-thread-time` trace of the comparison places about
11 seconds of sampled renderer-thread time under primary recording. About
8.6 seconds are attributed to `Monitor.Enter_Slowpath`; this changes the next
investigation from generic recording optimization to attributing monitor entry
and checking actual contention events. These are sampled thread-time weights, not precise method
durations, measured GPU time, or proof that all recording time is CPU execution.
Distinguish contention from aggregate synchronization cost before changing it.
The owned editor is stopped;
user-owned SteamVR remains running. No successful desktop interaction or resize
recovery has been demonstrated.

Evidence: `logs/openxr-render-failures-preview-{on,off}.log`,
`reports/openxr-no-preview-{before,after,disable}.json`, and
`reports/openxr-recording-cpu.{nettrace,speedscope.json}` under the existing run.

Source review finds descriptor and layout getters already holding their texture's
image-state monitor before calling a helper that enters the same monitor again.
The next bounded implementation removes only those redundant recursive entries,
calling the existing no-lock helper inside the unchanged outer critical section.
Refresh, publication, readiness, and completion ownership remain unchanged.
This is a measured-path optimization candidate, not an established correction
for the headset/resize failures. Compare a fresh built live run against the same
preview-disabled diagnostic fixture before claiming a performance benefit.

The dedicated 15-second CLR contention capture (owned PID 46212) records only
nine contention start/stop pairs, totaling 0.1055 ms with a maximum of 0.0267 ms.
It does not support multi-second lock blocking as the explanation for sampled
monitor time. The locks belong to separate buffer/material/texture/view state;
there is no demonstrated single shared lock causing the freeze. Continue with
aggregate recording/synchronization cost, while keeping resize deadlock unproven.
Evidence: `reports/openxr-contention.{nettrace,speedscope.json}` and
`reports/openxr-contention-runtime.json`.

The redundant-entry candidate builds with zero warnings/errors (17.09 seconds)
and runs as owned PID 37692. The paired 15-second sampled traces do not establish
an overall win: renderer `RecordPrimary` inclusive weight is 10,891.7 ms before
and 11,234.1 ms after, while monitor entry weight is 5,602.7 ms before and
5,364.4 ms after. A single pair is noise-sensitive and does not establish a
regression either. Omitted layers and missed deadlines continue (198 submitted,
233 no-layer, 194 missed at the candidate snapshot). Revert the 15-call candidate;
do not retain it as a purported fix. Both owned runs are stopped. The reusable
session binary still contains the reverted candidate and must be rebuilt before
the next baseline run. Evidence: `reports/openxr-getter-lock.*`,
`reports/openxr-getter-lock-{runtime,disable}.json`, and
`logs/openxr-getter-lock-build.log`.

Rebuilt the reverted baseline cleanly (zero warnings/errors, 16.11 seconds) and
enabled the existing per-frame profile capture for owned PID 48856. This restores
useful counters that were zero in incidental MCP snapshots. Active warmed rows
show 378–380 uniform-plan misses and the same number of schema-mismatch legacy
writes; some rows additionally contain 818 binding-snapshot-ineligible writes.
Do not confuse zero rows between rendered work with proof that this cost is absent.

A follow-up bounded MCP sample captures the exact schema mismatch:
`ShadowMapEncoding`, in the UberShader material fragment block (5,248 bytes), is
declared at Material frequency but supplied at RuntimeCallback frequency. Repeated
plan attempts allocate the temporary static byte buffer and operation list before
rejecting the frequency mismatch and using the legacy writer. Earlier sky-only
samples show `SkyboxIntensity` and `SkyboxRotation` declared Material but supplied
at View frequency. Correct the producer/declaration contract rather than allowing
incompatible frequencies through the material cache. This is a concrete remaining
work item; its contribution to total headset stalls is not yet isolated.

The descriptor fingerprint fast path also explicitly rejects read-only storage
bindings because publication identity alone does not prove physical buffer/offset.
Preparation, prewarming, and draw binding each revisit full reflected descriptors;
retain that correctness guard while attributing the slow-path count. Existing
descriptor owner-miss counters do not count this early rejection. Evidence:
`reports/openxr-work-profile-{runtime,stats}.json`,
`reports/openxr-schema-mismatch.json`, and the PID 48856 session's
`profiler-render-stats.ndjson`. Both owned sessions are stopped; the session binary
again matches the retained source. No hardware acceptance or resize recovery is
claimed.

The observed lighting mismatch originates at two existing boundaries: forward
lighting captures raw program values without a typed publication scope, so its
supplemental names retain per-callback ownership; shader declaration classification
defaults those same names to Material. The next candidate classifies the existing
canonical supplemental Lights names as RuntimeCallback before block construction.
Explicit shader frequency annotations and core engine camera/time classifications
retain precedence. This separates callback data physically from material data
without sharing it across lights/views or relaxing frequency validation. Acceptance
requires the observed mismatch/failed-plan repetition to disappear in the live
fixture, fresh rendered output, and no ownership/submission regression; overall
VR performance and resize recovery are still independent gates.

The classification candidate builds cleanly (16.77 seconds) and runs as owned
PID 2076. Warmed schema mismatch counts drop from roughly 380 to 2 (the skybox
pair), establishing that the declaration correction reaches the measured failure.
It is not safe to accept alone: frame-data reservations grow from 108,089
(10,878,672 reserved bytes) to the 131,072-entry limit (13,194,960 bytes). Runtime
callback storage currently keys ownership by captured snapshot object, and fresh
snapshots create persistent frequency reservations. Stop this run. Audit stable
logical draw ownership before retaining the candidate; do not increase capacity.
The candidate's attempted eye capture is rejected because previews are disabled,
so there is no visual-validation claim for this change yet. Evidence:
`reports/openxr-lighting-frequency-stats{,-late}.json`,
`reports/openxr-lighting-frequency-runtime.json`, and
`reports/openxr-lighting-left.json`.

The storage candidate replaces callback snapshot-object ownership with the
backend mesh-renderer instance, material, and assigned frame-wide draw slot.
The manifest separates view/pass families for that backend instance; separate
physical frame slots retain independent chunks/publication state and completion
gates. Use the backend instance, not the parent engine mesh renderer, because
different render versions can share that parent. Captured uniform names/values
still determine content generation. Acceptance adds stable warm reservation
counts and bytes over multiple windows before enabling previews for visual checks.

The stable-owner candidate (PID 49584) holds at 1,933 reservations / 482,144 bytes
through the measured 100–110 second active window, without exhaustion. Later
high-draw rows still report 380 schema failures: the next preview-enabled run
(PID 46356) identifies `AmbientOcclusionMultiBounce`, now the first failing member
of the smaller 4,784-byte material block. Apply the same existing canonical
classification to AmbientOcclusion names; these too are captured per callback.
Do not describe the intermediate count of two as the final warmed result.
Both eye previews are captured and viewed at fresh frames 460 and 848: lit stone
wall geometry and bright sky are visible from distinct eye positions. This proves
delivery, not continuous output or correct exposure. Stop PID 46356 before the
next build. Evidence: `reports/openxr-stable-schema-samples.txt` and
`reports/openxr-callback-{left,right}-a.json`.

The combined lighting/AO declaration and stable-owner build passes with zero
warnings/errors (16.95 seconds). PID 41824 initially stalls at 56 submitted
frames: both early eye previews remain at frame 143 while engine frames advance.
The bounded failure log repeatedly reports pipeline admission still inside its
CPU budget. Those captures were viewed but are stale-output evidence, not a
fresh-output pass. The run eventually recovers: at 19:01:40 UTC it reports 702
submitted, 727 no-layer and 701 missed-deadline frames. Later captured and viewed
left/right images advance to 3553/3555, showing stone geometry and bright sky.
Image orientation still requires physical-headset confirmation.

The final measured active window, elapsed 413.9–473.7 seconds, contains 180
draw-bearing rows. The repeated 380 schema failures are absent: 409-draw rows
typically have four schema fallbacks; 802-draw rows have four schema fallbacks
plus 818 snapshot-ineligible fallbacks. Reservations range from 2,757 to 2,764
(575,632–576,080 bytes), with five chunks and 167,772,160 mapped bytes unchanged;
exhaustion/overflow counters remain zero. Retain the scoped declaration/storage
correction. This is not a full performance pass: mixed-workload median dispatch
is 236.513 ms, mesh preparation 40.747 ms, and snapshot copy 38.573 ms. These
overlapping CPU measurements must not be summed or presented as GPU time.

Automated desktop resize succeeds from 1920×1080 to 1600×900 and back. The first
request advances engine frames 3284→3328 and XR submissions 843→856; the second
advances frames 3335→3376 and submissions 858→870. Both dimensions are confirmed
active and end-frame failures stay zero. This rules out persistent resize
deadlock in this warmed run, not poor interaction or a drag-resize-specific
failure. Viewed viewport and composited-window screenshots show a mostly black
upper region with landscape below and no visible editor panels, despite IMGUI,
screen-space UI and desktop editing being enabled in the fixture. Desktop UI
composition/input remains open. Evidence: `reports/openxr-desktop-resize.json`,
`reports/openxr-desktop-{ui-after-resize,after-resize}.json`,
`reports/openxr-warmed-{left,right,stats}.json`, and PID 41824's profile log.

Source review identifies a possible cold-admission delay, not a demonstrated
root cause: one thread-local recording scratch object retains one pipeline
manifest cursor/pending set. A manifest switch resets that scan; XR's default
MeetDeadlineWithGpuFallback policy retains the two-millisecond budget. Successfully
admitted signatures survive resets, so repeated prefixes can eventually become
cheap enough to finish. Measure manifest transitions, cursor progress, ledger
hits/misses and readiness policy before changing progress storage. Do not remove
compatibility validation or force synchronous XR compilation from this evidence.

The owned `vk-shadow-resume` session is stopped after evidence collection;
SteamVR is left running. The disable request returned `Stopping VR` before
session shutdown, so this final run adds no completed-teardown acceptance claim.
No new physical interaction/flicker confirmation arrived for this candidate.
No tests were added or modified. The scoped source/doc diff check passes.

## Desktop UI Ownership During OpenXR Possession

The next isolated run (PID 51864) reproduces an unresponsive File menu while
OpenXR is running in Edit mode. The selected desktop camera is FirstPersonViewNode
with no UserInterface; AllowUIRender is true and presentation is
FullViewportBehindImGuiUI. An initial composited capture shows panels, which at
first contradicts missing UI production. Source review resolves that ambiguity:
Vulkan retains its last non-empty ImGui snapshot. Clicking File produces no menu
even after a delayed observation, and resizing to 1600×900 removes all panels
from the viewed composited capture. The old snapshot no longer matches the
window dimensions. This is stale UI presentation, not evidence of live input.
The attempted native border drag did not establish changed dimensions; the
successful reproduction uses the explicit window resize request.

The candidate gives the desktop viewport explicit UI ownership while the
OpenXR pawn switcher changes its camera. UI collection, swap, rendering, backend
preflight and resize must resolve the same override. Never attach the hidden
editor canvas to a shared gameplay/eye camera or relax editor/gameplay discovery
rules. Preserve camera selection and VR input possession; release only the
override installed by the switcher on teardown or owner changes. Validate real
menu/selection changes and resize, plus continued fresh eye submission, before
marking the desktop fix complete. The baseline session is stopped before edits.
Evidence: `reports/openxr-desktop-{slice-state,preferences,ui-diagnostics}.json`,
`reports/openxr-desktop-ui-before{,-resized}.json`, and viewed native window
observations during the File-menu click. The broker still recommends a deprecated
GPT-5.6 model; no broker run was started, and native review supplied the source
analysis.

The viewport override candidate builds in Release in 44.20 seconds with zero
warnings/errors. In PID 38340, diagnostics identify the active desktop UI source
as `viewport_override`; the desktop camera remains FirstPersonViewNode. Native
File-menu clicks visibly open and then close the menu. A programmatic resize to
1600×900 preserves panels, the next frame reflows their layout, and the subsequent
menu click still works. The composited resized PNG is captured and viewed.
Left/right previews at frames 2168/2170 are also captured and viewed: both show
scene geometry and sky without editor UI. Active eye viewport diagnostics report
no UI override. Keep this scoped correction; it adds no per-frame allocations or
input rebinding. Physical flicker, pacing and headset comfort remain unresolved.

Evidence under the existing validation run: `logs/openxr-desktop-ui-owner-build.log`,
`reports/openxr-desktop-ui-owner-diagnostics.json`,
`reports/openxr-ui-fixed-{menu,resize,left,right}.json`,
`reports/openxr-ui-resize-state.json`, and the corresponding
`mcp-captures/desktop-ui-fixed-*` PNGs. The attempted native border drag was
rejected before execution because its endpoint was outside the selected window;
do not count this as drag-resize validation. Computer Use was reset after the
bounded input checks and was not restarted.

The subsequent Play-mode check fails to establish lifecycle recovery. Play is
entered and the desktop UI override is released (`screenSpaceUi: null`), but
submitted frames remain at 733 and screenshot readback times out after 20 seconds.
Exit Play is accepted without an observed return to Edit. The sampled render
thread in `reports/openxr-ui-play-stack.txt` is in
`RuntimeRenderThreadHost.PumpCollapsedWindowEvents`; here “collapsed” denotes the
combined window/render thread, not a minimized window. Other sampled workers are
waiting, including OpenXR pacing. A later render-state query does not return
before cleanup. These observations do not identify a lock owner or establish a
deadlock; inspect timer dispatch and native event-pump state in the next bounded
reproduction. Also verify retained ImGui snapshot invalidation on leaving Edit:
removing the producer alone does not prove old draw data is no longer composited.
Evidence: `reports/openxr-ui-play-diagnostics.json`, `reports/openxr-ui-play.json`,
and `reports/openxr-ui-edit-restored-diagnostics.json`.

The named session manager confirms `vk-shadow-resume` is stopped with no owned
editor PID. This cleanup is not a normal XR teardown pass. SteamVR is left
untouched. No tests were added or modified during this feature-regression work.

## Play-Mode Progress Loss With OpenXR Active

The earlier Play stack sample contains no `XRE-Update`, `XRE-CollectVisible` or
`XRE-FixedUpdate` frames: the engine loop threads had exited, and the main thread
was spinning between the native event pump and `WaitToRender`, which returns
immediately once the timer stops. That run's logs all end at the
`ViewportRebind.PostEnterPlay.AfterWindowRebind` snapshot record.

Reproduction (owned PID 56216, same binaries and fixture, previews and failure
diagnostics on, profile capture off): OpenXR reaches `Focused` with advancing
submissions; `enter_play_mode` is accepted; about five seconds later
`get_time_state` reports `isRunning=false` with a captured terminal fault in
`CollectVisibleThread`/`DispatchCollectVisible`:
`ArgumentException: An item with the same key has already been added. Key: 0`
at `GPUScene.Add` (`_commandIndexLookup.Add`) <- `VisualScene3D.ProcessPendingRenderableOperations`
<- `VisualScene3D.GlobalCollectVisible` <- `RuntimeWorldRenderer.GlobalPreCollectVisible`
<- `OpenXRAPI.OpenXrCollectVisibleCore` <- `EngineVrLifecycle.CollectVisibleStereo`.
XR submissions stop at 73. Evidence: `reports/openxr-play-timer-a.json` and
`scratch/xr-play-timer-probe.py`.

Root cause: the desktop path reaches `RuntimeWorldRenderer.GlobalPreCollectVisible`
only through the timer subscription that `RuntimeWorldHost` links after
`VisualScene.Initialize()` and root activation, and unlinks before root
deactivation and `VisualScene.Destroy()`. OpenXR calls the same method directly
every collect iteration to republish late transforms before stereo collection,
regardless of that session window. Snapshot restore during Play entry ends the
edit session (`Destroy()` clears the GPU scene), then reactivates persistent
editor roots, queueing renderable adds. OpenXR processes those adds into the
torn-down scene. `BeginPlay` then calls `GPUScene.Initialize()`, which zeroes the
command count but keeps the lookup keys, so the next `Add` reuses index 0 and
throws. The collect loop's terminal-fault handler stops the timer. Desktop-only
Play does not reach this because its pre-collect subscription is unlinked for the
whole window. The same unsynchronized window also lets any in-flight pre-collect
overlap `Destroy()`.

Predeclared correction: `RuntimeWorldRenderer` gates collect-time publication on
the host's render session. The host opens it after linking its timer callbacks
and closes it before unlinking them; closing takes the same lock as
`GlobalPreCollectVisible`, so teardown waits for any in-flight publication.
While closed, renderable and matrix changes stay queued for the next session,
matching the desktop path. The OpenXR call and its transform republication
inside an open session are unchanged. Per-frame cost is one uncontended lock
entry on the collect thread, with no allocation.

Hypothesis rejection: any timer terminal fault, a stopped timer, or non-advancing
engine/XR frames after Play entry in the same fixture. Acceptance: three Play
entry/exit round trips with OpenXR active, the timer running throughout, XR
submissions advancing in Play and after exit, `get_time_state.terminalFault`
null, desktop viewport and both eye previews captured and viewed in Play, and
Edit restored with live editor UI after exit. The desktop-only Play path and
the retained ImGui snapshot invalidation remain separate checks.

The gated build (zero warnings/errors, 42.96 seconds; PID 63736) removes the
duplicate-key fault: the transition reaches `Play` with the timer running. About
eight seconds later a different terminal fault stops the collect loop:
`ArgumentException: Frame view IDs must be dense and match their view-set slot`
in `RenderFrameViewSet.Create` <- `VulkanXrGraphicsBinding.TryPlanVulkanOpenXrViewBatches`
<- `OpenXRAPI.TryResolveOpenXrViewRenderModeForCurrentBackend` <- `OpenXrCollectVisibleCore`.
The planner writes dense IDs by construction, but builds them in one instance
array shared by every caller: the collect thread (mode resolution), the render
thread (true-stereo recording and batch planning), and the public
`CanUseTrueSinglePassStereo` property used by temporal passes and statistics.
Concurrent calls tear the descriptor structs, so validation reads a foreign
view ID; a torn set that passes validation could also plan against mixed
target metadata. Correction: plan into a call-local inline array instead of
the shared field; planning output is unchanged and allocation-free. Acceptance is
unchanged from above. Evidence: `reports/openxr-play-fix-a.json`.

### Result of both corrections

The combined build passes with zero warnings/errors (29.99 seconds; PID 59536).
Three OpenXR Play entry/exit round trips and a fourth capture-free round trip
complete with no terminal fault: `get_time_state` reports the timer running in
every sample, each entry reaches `Play`, each exit restores `Edit`, and the
composited desktop captures show live editor panels with the frame HUD after exit.
Process private memory oscillates between 26.7 and 29.8 GB and settles lower
after the fourth round trip (28.77 to 26.66 GB), so these samples show no per-trip
growth; the absolute footprint is large and has no fresh-session baseline here.

Desktop-only regression check on the same binaries (PID 43388, OpenXR off):
two scripted round trips plus a third with the editor camera placed on Sponza
keep the timer running with no fault. The same camera node captured in Edit and
in Play renders the same Sponza geometry, so renderables re-register once the
host reopens publication. Existing tests covering the changed paths ran 40:
39 pass; `GpuBvhSceneTreeImprovementTests.LateBvhActivation_BackfillsEveryCommandFromAuthoritativeBoundsSnapshot`
fails a source-string assertion on GPU scene source text that this diff does
not touch. No tests were added or modified.

Acceptance is only partly met. The engine-loop loss is corrected, but XR layer
submission does not recover: submitted frames stay at 72 through the first two
Play windows and at 80 for the entire capture-free round trip (Edit before, Play
and Edit after), while no-layer frames rise at about one per second. Every
sampled omission in this session's bounded failure log (64-record cap) is
`MeshMaterialization` on `openxr-eye-visible-meshes`:
`No compatible Vulkan render program is available yet` for Sponza meshes, plus
two `PipelineCompilation` mirror-primary rejections. The same session warmed
slowly before any Play entry (0, 42, 53, 71 submitted over 25 seconds), so this
is the open cold/warm pipeline-admission problem, which every Play entry restarts
by restoring a fresh scene copy. It is not caused by the publication gate.

Other observations, not yet attributed:

- Recurring 4-5 second render stalls inside `PostRenderViewportsCallback`
  coincide with the probe's 2688x2688 eye-preview readbacks; captures in that
  run are observer load and depressed its Play-window submissions.
- The second exit took about 21 seconds, with one 24.2-second render stall inside
  `XRWindow.Renderer.RenderWindow`; other exits took about 2 seconds.
- The third Play entry's fresh left-eye preview renders the Sponza wall as solid
  magenta, where earlier frames showed textured stone. Trip 1 and 2 previews are
  byte-identical stale images from before Play.

Evidence: `reports/openxr-play-fix-b.json`, `reports/openxr-play-memory-a.json`,
`reports/desktop-play-gate-a.json`, `mcp-captures/openxr-play-fix-b/`,
`mcp-captures/desktop-play-gate-{a,b}/`, `logs/play-gate-tests.log`, and the
PID 59536 session's `openxr-render-failures.log` and `profiler-render-stalls.log`.
All owned sessions are stopped; SteamVR is untouched.
