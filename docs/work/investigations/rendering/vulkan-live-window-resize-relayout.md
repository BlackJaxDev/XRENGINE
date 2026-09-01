# Vulkan live window resize relayout

Status: held-drag relayout works, but live acceptance is reopened. User testing
found a post-release compositor freeze, Advanced-pipeline overlay accumulation,
and a terminal renderer pause after an Advanced-to-Debug-Opaque asset swap.
Root causes were identified from the 2026-08-31 logs; remediation is pending.

The user reports that holding and dragging the editor window border stretches
the old render instead of laying out at the current drag resolution. Earlier
Phase 5.4 acceptance used discrete native window resizes, minimize/restore, and
detached ImGui resizing; it did not exercise a held Win32 sizing modal loop.

Live scene projection and screen-space UI layout now use the current drag
extent. Internal image allocation remains deferred until release; a fresh scene
and UI are recorded against the retained images with the matching presentation
mapping. The default pipeline's warmed graphics admission survives per-frame
buffer-binding publication instead of repeatedly restarting preparation.

## Findings and intended repair

- The Win32 modal strategy publishes current client/presentation extents on
  sizing/paint callbacks. Full internal-resource resizing waits for drag end.
- Before the repair, `XRWindow.RenderCallback` suppressed `RenderWindowViewports`
  throughout an interactive dispatch. This also suppressed the fullscreen editor's normal
  screen-space UI producer, although the viewport-panel callback is separate.
- Vulkan selected an overlay-only interactive frame path and retained the old
  swapchain. Reusing a baked scene/UI image cannot satisfy current-size layout
  merely by stretching that image at presentation.
- Preserve exact queue and WSI completion, render-owner/non-reentrant dispatch,
  and bounded generation retirement. Validate actual held-drag images and
  fresh layout/projection, not only convergence after releasing the border.

## Validation tracker

- Baseline isolated editor build: zero warnings/errors.
- Named editor: `live-resize-regression`; only this task's session is controlled.
- Scratch/evidence: `Build/_AgentValidation/20260831-123635-vulkan-live-resize/`.
- Independent evidence review uses the repository-authorized broker. Its first
  request was rejected before dispatch because duplicate path ranges are not
  accepted; the corrected request supplies each selected file once.
- No tests have been added or modified. User confirmation of a repair is pending.

## Reproduced baseline

PID 29232 reproduced the report with a real mouse-held bottom-right border
drag, verified by native hit testing and the left-button state. The outer window
changed from 1733x971 to 1293x731 while the button remained held. The viewed
`before-held.png` shows smaller, stretched menu text and dock panels; the viewed
`before-released.png` immediately restores normal-size text and relayout.
The Vulkan swapchain stayed 1711x915 while live client size reached 1271x675.
Profiler evidence reports zero scene recording time and an overlay-only record
during the held interval, with zero device-idle calls. Logs confirm the Win32
sizing path and final `win32-exit-size-move` resize publication.

Independent review completed with requested/actual `gpt-5.6-sol` matching,
broker run `7aaa5d2ca852490ca6c3963c6ff90bbb`. It confirms both suppression gates
must change together. The repair must retain the active internal generation
and use nonblocking exact admission, not run ordinary blocking PresentNow
readiness from a modal callback.

## First repair

Restore viewport rendering during interactive callbacks so fullscreen ImGui
and scene camera constants are produced at the live presentation extent. Keep
global scene pre/post hooks and upload/readback pumps out of the modal path.
Vulkan now uses normal scene record/submit/present ownership
with one nonblocking attempt instead of discarding scene operations and only
overlaying the retained image.

The frame plan's desktop outputs explicitly use `AllowDeferral`/`Background`
during this attempt. Only these two policy fields change; output/view/target
identity and unrelated XR/offscreen contracts are preserved. Existing active
allocation dimensions stay frozen. Missing native target snapshots or changed
native barrier bindings defer the frame; no synchronous image generation or
resource-binding repair is performed in the callback.

The first live repair did **not** pass the held-drag check. PID 32020 still
displayed scaled text while the mouse was held. `log_rendering.log` reports
`CollectGenerationMismatch`; the profiler shows zero authored frame ops and
one rejected backend-ready package. The downstream "DAG deferred every output"
message describes an empty plan, not a CPU/GPU budget rejection. Its recorded
output budgets were zero, ruling out the initial budget hypothesis.

## Collection handoff and recording safety

The collapsed window event pump runs before `WaitToRender`. Consequently the
native modal callback had bypassed the normal `TryConsumeFresh` handoff: a
fully published package could be one collect generation ahead of the consumer.
The timer now nonblockingly consumes the exact published generation after its
final cadence check, then signals the existing collect thread only after the
entire synchronous callback has finished. It does not run collect work inline
or wait for it. The ordinary package validator remains unchanged, including
all command/resource/descriptor/render-graph generation checks.

During the drag, a newly collected package may describe the previous transient
presentation size. Display-only lag is permitted while its internal extent
matches both the active allocation generation and the current viewport.
Camera/UI command production uses the current presentation extent.

Independent native review also identified and the repair addresses:

- Cold compute linking, buffers, textures and texel descriptors must honor the
  recording's no-synchronous-upload policy.
- Fresh camera/UI recording must not imply foreground pipeline compilation
  waits. Those waits require the normal `PresentNow`/`BlockForExact` policy.
- Framebuffer snapshots must read the published view's exact backing-image
  identity, without invoking refresh/allocation getters. Modal target
  preparation and render scopes must use those existing tuples.
- All abandoned authoring snapshots and advanced-visibility input leases must
  be released, including exceptions before operation splitting/lowering.
- A busy-slot or unavailable-acquire skip discards its queued mesh requests
  together with only that published cohort's pins. It must not append a new
  camera/UI cohort to the discarded frame's old draws.

## Second runtime pass and remaining corrections

The second isolated build completed with zero warnings/errors. PID 44660
produced fresh scene and UI commands during the interactive interval: frame
20604 recorded 17 operations (15 mesh, two clear), consumed both published
backend-ready packages without rejection, and completed submit/present. Scene
recording took 9.49 ms; no collect wait or device-idle call occurred. These are
diagnostic observations, not a frame-time promotion claim.

Windows had switched to the screen-saver input desktop by this pass. Both
attempted physical mouse drags returned `MouseHeld=false` and unchanged window
sizes, so their images are **not** held-drag acceptance. The native helper now
rejects an unavailable input desktop. The subsequent `WM_ENTERSIZEMOVE`, size
updates, and `WM_EXITSIZEMOVE` target only the owned editor window and exercise
the application's interactive state, but do not reproduce the real modal
message loop. A real held-drag check remains required when the interactive
desktop is available.

Viewed `second-message-width-shrink-interactive.png` shows correctly sized menu
text and dock panels at 1271x915 client size over a retained 1711x915 backbuffer.
However, the scene was still compressed horizontally and offset. The generic
presentation-area commands call `MapWindowPresentationRegionToBackbuffer`, but
the Vulkan renderer facade had lost its forwarding override. Restore that
override so those existing commands map the live presentation area into the
retained backbuffer exactly once.

The release frame also exposed one native
`VUID-VkImageMemoryBarrier-oldLayout-01197` at 13:17:53.111 in
`TransitionSwapchainToPresent`. Its preceding fallback presentation-source blit
restored the acquired image's **initial** `PresentSrcKhr` layout, although both
callers had transitioned it to `ColorAttachmentOptimal` and the recorder still
tracked that layout. Prepared swapchain blits now restore
`ColorAttachmentOptimal`, matching their caller contract and subsequent
overlay/presentation barriers. Native logs, rather than the current-frame
profiler's zero error count, are the cumulative validation authority.

The second pass logs are copied to `logs/second-repair-editor/` under the task
evidence root. The latest scaling, blit, and cache-only recording changes still
require a rebuilt runtime pass. No new tests are being added under the
repository's feature-validation policy.

## Third runtime pass

PID 8140 used the rebuilt Vulkan backend with the presentation mapping and
blit-layout corrections (zero compiler warnings/errors). The owned-window
message sequence passed these viewed checkpoints in `DebugOpaqueRenderPipeline`:

| Interactive client extent | Change | Frame | Consumed/rejected packages | Scene record | Device idle calls |
| --- | --- | --- | --- | --- | --- |
| 1271x915 | Width shrink | 1282 | 2 / 0 | 9.52 ms | 0 |
| 1711x915 | Width grow | 3666 | 2 / 0 | 8.28 ms | 0 |
| 1711x675 | Height shrink | 4142 | 2 / 0 | 7.94 ms | 0 |

All three snapshots recorded 17 operations and completed with exact ownership
settled. Menu/dock text retains its size, panels relocate to the live edges,
and the scene has the same proportions and position before and after release.
Full `log_vulkan.log` review found no native VUID/validation/synchronization
errors, including the release transitions that failed the second pass.
The settled frame 7813 reached desktop generation five, no pending retired
generations, no quarantined failures, no device-idle calls, and cumulative
retirement p99 of 0.037 ms. These timings apply only to this diagnostic cohort.

Screenshots are `third-width-shrink-*`, `third-width-grow-*`, and
`third-height-shrink-*`; compact frame reports are in `reports/`, and the full
stopped-session logs are copied to `logs/third-repair-editor/`.

This simple pipeline has no compute dispatches. A normal-path sampled-image
transition correction landed after its Vulkan assembly was compiled; a fresh
build with `DefaultRenderPipeline` is therefore required before accepting the
compute changes. The physical mouse-held check remains blocked by the
screen-saver input desktop; message-driven evidence is not labeled as that check.

## Default pipeline admission follow-up

The subsequent build also completed with zero warnings/errors. The warmed
`DefaultRenderPipeline` cube scene (TSR at 0.67 internal scale) completed frame
2978 with 42 operations and one compute dispatch. Its interactive width shrink
did **not** pass: frame 3871 deferred recording with zero emitted operations,
although both backend-ready packages were consumed and ownership was settled.
The log identifies the immediate cause as the new modal hard rejection when
native buffer-barrier bindings are incomplete or behind the publication revision.
The normal path already supports a lookup-only native-binding freeze under
`allowSynchronousResourceUploads=false`; it must be allowed to refresh that
metadata once without creating images/buffers or changing structural resources.

This pass also reports `UserInterfaceRenderPipeline` `ViewportMismatch` while
scene internal resolution differs from display resolution. Its screen-space
resource dimensions need distinct validation from the scaled scene dimensions.
Both findings remain part of the live-resize repair; the successful simpler
pipeline cohort is not used to claim Default pipeline acceptance.

The next Default build (PID 38976, zero compiler warnings/errors) fixes both
the lookup-only buffer-binding refresh and the display-resolution UI package
dimensions. Normal frame 1527 completed 42 operations with one compute dispatch;
interactive frame 1853 still deferred recording, with both packages consumed
and none rejected. Its native log identifies a different remaining cause:
pipeline admission repeatedly scans 23–27 of 31 already-ready requirements before
its two-millisecond budget expires. Pending compilation count is zero.

`FramePlan.RenderGraphPlanSignature` includes exact native barrier epochs, and
the general operation signature includes viewport rectangles and frame-local
descriptor/buffer state. Both were also keys for the pipeline manifest and its
successful-preparation ledger. Each lookup-only buffer-binding publication or
resize rectangle therefore reset progress on otherwise unchanged pipelines.
The repair gives pipeline admission a separate compatibility identity derived
from frozen compiled contexts, logical allocation/attachment compatibility,
program/link generations, renderer preparation, fixed-function state, viewport
count, and exact operation indices. Native handles, barrier publication epochs,
and dynamic viewport rectangles remain in recording validation, not warmup keys.
The existing cold-preparation budget and exact lifetime checks are unchanged.
Pre-acquire and recording admission now both use the frame's frozen context plans.

The stopped second Default logs are copied to `logs/default-second-editor/`.
No cumulative native VUID or synchronization error occurred in that run. The
new pipeline compatibility changes require the next rebuilt runtime cohort.

## Real held-drag acceptance

The third Default build completed with zero warnings/errors (38.24 seconds).
The input desktop became available, allowing a real border drag in PID 62944.
Native hit testing and `MouseHeld=true` confirm the owned window shrank from
2165x1487 to 1725x1487 while the button remained down. Held frame 1182 completed
42 operations including one compute dispatch, consumed both backend packages
with zero rejection, settled ownership, and made no device-idle call. Viewed
`default-third-physical-width-shrink-held.png` and `-released.png` have the same
cube proportions/position and correctly sized menu text and dock panels. This
is actual held Win32 modal-loop evidence, unlike the earlier message probes.

The following grow/height probes did not establish their requested extent
changes: the owned window's bounds changed between probes, and their recorded
held dimensions were unchanged or changed by only three pixels. They are not
used as controlled grow/height acceptance. The settled frame 4830 completed
42 operations with one compute dispatch at desktop generation 15, no pending
retired generations, no quarantine, and no device-idle calls. Its cumulative
retirement p99 was 0.714 ms; this uncontrolled resize/debug cohort is not a
performance promotion and does not establish the earlier cohort's 0.5 ms target.

The stopped logs in `logs/default-third-editor/` contain no native VUID or
synchronization errors. Cold pipeline requests can still defer an individual
frame while compilation completes; the previous endless 23–27/31 warmup scan
is gone. A final review tightened the compatibility key's legacy-render-pass
fallback when a dynamic-rendering target is invalid. That one-line guard landed
after this assembly compiled and is included in the final rebuild.

## Final rebuilt validation

PID 32736 includes all source corrections, including the reviewed legacy-target
guard. Its isolated editor build passed with zero warnings/errors in 21.65
seconds. Vulkan core and synchronization validation were enabled; the scene
used `DefaultRenderPipeline`, TSR internal scale 0.67, and the cube marker.
Each checkpoint below proves both `MouseHeld=true` and changed native window
dimensions. The probe also verifies that the cursor actually reaches the owned
window's sizing border before pressing the button.

| Actual held drag (outer window) | Held frame | Operations / compute | Packages consumed / rejected | Scene record | Device idle calls |
| --- | --- | --- | --- | --- | --- |
| Height shrink: 1440x1000 → 1440x760 | 2853 | 42 / 1 | 2 / 0 | 22.06 ms | 0 |
| Width grow: 1440x760 → 1680x760 | 3808 | 42 / 1 | 2 / 0 | 19.93 ms | 0 |
| Height grow: 1680x760 → 1680x1000 | 3953 | 42 / 1 | 2 / 0 | 20.82 ms | 0 |

All three held frames completed recording/submission/presentation with exact
ownership settled. All six held/released screenshots were viewed: the cube
retains its proportions and position for each extent, menu text keeps its size,
and dock panels/HUD layout follow the live window dimensions. Native logs also
confirm the corresponding Win32 modal-loop exit publications, with client
extents 1418x704, 1658x704, and 1658x944. These checks supplement the preceding
real width-shrink pass; they are not synthetic message-loop substitutes.

Settled frame 5281 completed at desktop generation eight with zero pending
retired generations, zero quarantine, zero device-idle calls, and cumulative
retirement p99 of 0.087 ms. The complete stopped `log_vulkan.log` (657 lines)
contains zero native VUID/validation/synchronization errors, including shutdown.
Three first-chance compute-readiness exceptions occurred during initial startup,
before the scene and resize probes; readiness subsequently converged. No endless
graphics-admission scan remains. Timings describe this validation cohort only.

Final evidence is under the task root: `logs/default-final-editor/`,
`logs/default-final-build.log`, `reports/default-final-*-summary.json`,
`reports/default-final-native-validation.json`, and
`mcp-captures/default-final-physical-*-held.png` / `*-released.png`.
Only the named editor session was stopped. No new resize tests were added or
modified under the repository's feature-validation policy. User confirmation
of the repair remains unreported; the acceptance above is local runtime evidence.

## Resize-release continuity follow-up

After held-drag relayout was restored, mouse release exposed a second regression:
ImGui disappeared and returned, then the 3D scene briefly presented black before
returning. The baseline log identified both writers. Release could occur after
preflight but before recording; recording re-read the live mouse state and
discarded the already accepted interactive ImGui mapping. After swapchain
recreation, a semantic-empty successor plan then published the fresh full-surface
initialization clear. These were two consecutive frame-authority races, rather
than a dock-layout or shader fault.

Recording now consumes the frame attempt's latched interactive-resize state.
Resize release also has an explicit generation handoff:

- The last successful held presentation records its contributing scene
  viewports, their current camera-bound screen-space UI, and whether ImGui was
  present. Fixed-capacity storage keeps this render-path bookkeeping allocation
  free.
- `AwaitingReadyToRecreate` keeps that completed image visible until every still
  attached contributor has a current-frame command-chain or draw-data receipt.
- `AwaitingSuccessorPresent` retains the compositor image after recreation and
  rejects semantic-empty, clear-only, overlay-only, stale-generation, and
  superseded successor work. A second recreation rebases the unpublished
  successor generation instead of completing against the wrong swapchain.
- The handoff completes only after an authored scene successor, plus required
  ImGui, is submitted and presented. Detached viewports or replaced camera UI
  are re-resolved without recursive UI discovery from backend preflight.

The final isolated source build passed with zero warnings and zero errors. Two
real bottom-right border drags then passed with `MouseHeld=true` and changed
native extents. The primary acceptance changed the outer window from 1733x971
to 1413x751; the warm follow-up changed it from 1733x971 to 1473x791. The held
and 600-ms post-release screenshots were viewed for both runs: the cube remains
visible, ImGui remains complete, and panels remain laid out at the dragged
extent. The first cold successor took longer to converge, while the retained
compositor image preserved visual continuity; the warm successor completed in
about 2.4 seconds.

The accepted log contains the handoff arm, swapchain transition, deferred empty
successor decisions, and final authored successor completion. It contains no
`Published a fresh full-surface clear`, first-chance exception, native VUID,
validation error, or device-idle call in the acceptance interval. Evidence is
under `mcp-captures/release-handoff-acceptance-*`,
`mcp-captures/live-resize-regression-*`,
`reports/release-handoff-acceptance-held-window.json`,
`reports/live-resize-regression-held-window.json`, and
`logs/release-handoff-acceptance-editor/` in the task evidence root. The named
editor session was stopped after its logs were copied. No tests were added or
modified under the repository's feature-validation policy.

## User acceptance correction and last-run root cause

The preceding release-continuity acceptance was incorrect. A screenshot taken
600 ms after release could show the retained compositor image and therefore
prove that black/UI disappearance did not occur at that instant, but it could
not prove that new frames, ImGui, or dynamic FPS text were being presented.
User testing exposed three independent failures.

### Default pipeline freezes after mouse release

The newest user run contains `AdvancedRenderPipeline` followed by
`DebugOpaquePipeline`; it contains no `DefaultRenderPipeline` entry. The Default
symptom is instead confirmed by the earlier release-handoff log under
`logs/release-handoff-acceptance-editor/log_vulkan.log`.

After swapchain recreation, `TryDeferIncompleteResizeReleaseSuccessorBeforeAcquire`
stops every frame while the successor plan reports
`AuthoredTerminalProducerMissing`. Because this gate runs before image acquire,
it suppresses all presentation, including ImGui and dynamic FPS text, rather
than merely preventing an incomplete scene from replacing the retained image.
One handoff deferred from 15:32:02.417 until 15:32:19.475 (about 17.1 seconds).
A later handoff produced a 53.1-second desktop-frame gap before completing at
15:36:34.064. The render loop is not GPU-hung; it deliberately leaves the
compositor displaying its last image. Unfocus, maximize, or another drag
publishes new WSI/resource state and can make the handoff converge or rebase,
which explains the apparent recovery.

### Advanced pipeline has no reliable authored scene beneath the overlays

The latest user run is
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/` followed by
`xrengine_2026-08-31_15-47-05_pid60248/`. Advanced starts with zero registered
passes while backend admission is failed/pending, reports missing passes 10, 3,
and 1, and then throws `Visibility indirect range capacity was exhausted` from
`AdvancedIndirectRangePlanner.Build` on every attempted scene frame. The fixed
default permits 65,536 draws but only 64 indirect ranges; Sponza exceeds that
range-key capacity. The exception aborts the Advanced terminal producer, so the
Vulkan log repeatedly reports no scene swapchain writer and publishes
semantic-empty full-surface clears outside the held drag.

During interactive resize, the stale/recovery path still presents through WSI
scaling and records ImGui with `clearSwapchain: false`. With no valid authored
scene/base write, the overlay pass loads previously presented swapchain pixels
and blends the current ImGui/dynamic text over them. That undefined stale base
is the visible accumulation/ghosting. The resize machinery exposes the problem;
the Advanced range-capacity failure is what removes the fresh scene writer, and
the recovery overlay policy is what turns it into repeated overdraw instead of
a defined clear or retained authored image.

### Debug Opaque swap succeeds, then required upload readiness pauses rendering

The asset swap itself succeeds: Debug Opaque installs nine passes, commits its
three-resource generation, compiles the required graphics pipelines, and frame
1124 presents an authored `MeshDrawOp`. Frame 1127 then encounters required
texture ticket `texture-upload:146:4`, which had already failed with `No current
renderer owns Vulkan wrapper creation for imported texture upload.` PresentNow
classifies that required-upload failure as `RendererTerminal` and stores it in
`_presentNowTerminalFailure`, so later desktop frames stop before acquire. This
looks like a renderer crash, but the process remains alive and there is no
Vulkan device-loss or validation-VUID failure in the run.

The ownership failure is an ordering bug around `XRWindow.RenderCallback`.
That callback publishes `AbstractRenderer.Current` only while it renders and
clears it in `finally`. `XRWindow.RenderFrame` then calls
`RuntimeEngine.ProcessMainThreadTasks()` after `Window.DoRender()` returns, so
queued `VulkanTextureUploadService.DrainUploadPrepQueue` coroutines execute after
the owner has been cleared. `EnsureJobPreparation` requires the current renderer
to create the texture's Vulkan wrapper and permanently fails the job when it is
null. Debug Opaque makes many of those textures immediately required, converting
the pre-existing streaming fault into the intentional terminal pause.

### Reopened acceptance

- Keep presenting live ImGui/dynamic text after resize release while an authored
  scene successor converges; do not stop the entire output before acquire.
- Give every overlay-only/recovery frame a defined, non-recursive background so
  Advanced or otherwise missing scene writers cannot accumulate UI history.
- Move render-owner publication ahead of pending upload processing, or pass the
  owning renderer explicitly through upload preparation; required texture
  failure must not permanently pause a healthy device after a pipeline swap.
- Repeat actual held-drag/release acceptance with Default, Advanced, and Debug
  Opaque, including an Advanced-to-Debug-Opaque live asset replacement.

## Deadline handoff: unresolved required-upload progress

Work stopped at the user's 2026-08-31 17:05 local cutoff. The named
`live-resize-regression` editor session is stopped. The Vulkan project builds
with zero warnings and zero errors, but the three user-visible scenarios have
not completed their final live acceptance and Phase 5 remains open.

The current implementation includes the release handoff with exact held-scene
replay and current overlays, a defined base for recovery overlays, 65,536
Advanced indirect draw/range capacity, exact renderer-owner/backend-generation
upload scheduling, and a bounded direct pre-frame upload drain. The final source
change makes accepted visible-material closures capture the last published
texture descriptor generation instead of an unpublished full-resolution
promotion. This should allow a valid 64-pixel preview generation to remain
drawable while a 1024-pixel promotion proceeds, but it has not yet survived the
full prior failure window in a live run.

Every Default run before that final change reproduced the same sequence around
frame 92-103: a visible Sponza texture promotion decoded and applied, PresentNow
waited approximately 30 seconds on `RequiredUploadCompletion` with
`prepQueued=1, prepActive=0`, and the queued
`VulkanTextureUploadService.DrainUploadPrepQueue` coroutine ran only after the
watchdog stored a `RendererPaused` terminal transition. The clearest sample is
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-123719-live-resize-regression/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-31_17-01-36_pid10200/`.
There was no Vulkan device loss, validation error, or normal-frame
`vkDeviceWaitIdle`; this is a CPU scheduling/readiness failure that presents as
a frozen last compositor image.

The final post-change run was observed for only 25 seconds. Frame 93 remained
`Completed/Success` and no terminal/drain-wait signature appeared during that
short interval, but earlier failures occurred after roughly 30 seconds. Treat
`Build/_AgentValidation/20260831-123635-vulkan-live-resize/reports/default-published-generation-25s-summary.json`
as a stopped smoke sample, not a pass.

Continue with the ordered acceptance and instrumentation checklist in Phase 5.4
of the master TODO. In particular, first run Default beyond 45 seconds after the
promotion. If it still stalls, log the exact manifest ticket beside its prep-job
state (`sequence`, streaming generation, state, retry timestamp, worker task,
pending upload, and in-flight count) and explain the ownership/progress mismatch
before making another readiness-policy change. Only after that path stays live
should held resize, Advanced, and the Advanced-to-Debug-Opaque asset replacement
be rechecked.

## Resize and editor-camera pipeline transition implementation

Follow-up work on 2026-08-31 separated the viewport/pipeline lifecycle defect
from the unresolved required-upload watchdog. The implementation now has these
invariants:

- `XRViewport.Resize` batches display extent, camera Full/Scale/Manual policy,
  and pipeline AA/upscale policy before publishing one coherent resource profile.
  Presentation-only interactive extents do not allocate intermediate render
  graphs; the release/full-internal commit requests the settled generation.
- `XRRenderPipelineInstance` owns the generic resize generation request for
  every declared pipeline. Default and Advanced hooks only invalidate their
  ancillary AA/history state.
- Camera asset changes are latest-wins render-thread transitions. A real asset
  reference change increments an instance-local pipeline revision, force-resets
  command/pass publications, clears pipeline-scoped runtime state, and prevents
  an old generation from validating the successor command chain.
- Active, pending, and retired generations retain their creating pipeline asset,
  so delayed destruction callbacks cannot target a newer replacement asset.
  Layoutless successors explicitly retire an old managed generation while
  retaining legacy resources under the new owner.
- `XRWindow` considers a full-internal resize committed only when controller
  presentation/output extents agree and each viewport's active generation
  matches its actual display and actual internal extent. This supports scaled
  internal resolution and non-full-window viewports.

The first final Default replay found a real Vulkan blocker after a successful
`1920x1080 -> 1599x1080` generation. A subsequent `2560x1369` request failed in
`VkDataBuffer.PushSubData`: the buffer's logical length was below 64 KiB, so
memory policy was recorded as host-visible, while resizable capacity rounded to
64 KiB and the backing allocation was created device-local. Reuse then tried to
map the device-local allocation. `VkDataBuffer.PushData` now computes planned
capacity first and uses that same byte count for memory-policy selection,
recreate comparison, and allocation. The corrected run committed
`1920x1080 -> 2560x1369` in 208.87 ms with no pending generation, generation
failure, or `not host-visible` exception. Evidence is in:

`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-190734-pipeline-resize-swap/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-31_19-41-01_pid49808/`.

The resize/swap acceptance observations were:

- Advanced: actual held width and height border drags changed
  `1920x1080 -> 1499x1080 -> 1499x819`; each release produced a matching active
  generation with no pending/failure state. The solid-red surface is the
  Advanced pipeline's current explicit diagnostic clear, not a resize artifact.
- Default: the corrected build visibly rendered the scene after resize and its
  active resource key matched `2560x1369` for both display and internal extent.
- Cross-type presentation: a fresh Advanced-to-Debug run and a fresh
  Default-to-Debug run both changed the window surface to Debug's black output.
  The latter reported `DebugOpaquePipeline pipelineRev=3`, a complete
  `1920x1080` active generation, no pending/failure state, and only Debug-owned
  enabled command passes.
- Any-to-any state: Advanced-to-Debug-to-Default, Default-to-Advanced, and two
  distinct Advanced assets advanced pipeline revisions and resource generations.
  Reassigning the exact same Advanced asset left the camera assignment revision
  and resource generation unchanged.

The camera API and the ImGui picker now share
`XRCamera.ReplaceRenderPipelineAsset`, and MCP exposes
`set_editor_camera_render_pipeline_asset` for bounded live diagnostics.

This cohort does not override the deadline handoff above. The unrelated
`RequiredUploadCompletion` watchdog still reaches `RendererPaused` after about
30 seconds in some fresh runs, including after otherwise successful resize
commits. Post-terminal state changes were excluded from presentation evidence.
Phase 5 therefore remains open for the long-duration upload-progress gate even
though the resize and asset-transition lifecycle is implemented and passing its
targeted live checks. No tests were added or changed because repository policy
still requires explicit user clearance after live integration validation.

## Final lifecycle hardening and exact-build replay

A final ownership review found six transition-edge cases and the implementation
was hardened before the final replay:

- stale shared-asset instance snapshots can no longer mutate a successor;
  command rebuild and settings invalidation verify asset owner, pipeline
  revision, and command generation under the transition lock;
- a `SetField` veto cannot leave partial ownership or advance the revision;
- layoutless authority retains its last successful key until cleanup succeeds;
- notification callbacks and terminal generation/resource teardown are
  exception-isolated and best effort;
- resize readiness rejects every outstanding pipeline request; and
- output binding reads one coherent request/applying/applied target instead of
  racing the camera-facing request with the render-thread transition.

The exact isolated build is under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-202840-pipeline-final-vk/`.
Three Vulkan processes exercised the same artifacts. The state cohort performed
Advanced -> Default -> Debug Opaque -> Advanced, verified distinct asset
revisions and generations, and verified that reassigning the exact Advanced
asset was a strict no-op. Advanced then committed a native maximize to
`2560x1369` with six textures and one FBO. A fresh Default process committed the
same native extent with 31 textures and 30 FBOs.

The last Default replay (PID 27264) visibly presented the scene and editor UI
after the maximize, with the runtime overlay reporting
`Vulkan | DefaultRenderPipeline | CpuDirect`. Its managed generation committed
`1920x1080 -> 2560x1369` in 42.42 ms, the swapchain converged to that extent in
216.423 ms, and the full-internal resize committed only after the generation was
active. The run continued for more than 75 seconds after the pipeline change and
advanced beyond frame 2560. Across that run there were zero pipeline-transition,
cleanup, description, host-visibility, device-loss, `RendererPaused`,
`RequiredUploadCompletion`, or validation-VUID records. Two transient
`VulkanPresentNowReadinessException` records were retry-frame waits for
asynchronous compute compilation; presentation recovered normally. Later asset
loading also retained the pre-existing vertex-input and auto-uniform warnings,
which did not stop presentation and are outside this lifecycle slice.

This longer replay strengthens the Default upload-progress evidence, but it did
not explicitly identify the checklist's Sponza 64-to-1024 promotion event.
Phase 5 remains open until that named promotion cohort and the remaining held
Advanced/Debug cross-pipeline acceptance rows are completed. No tests were
added, modified, or run; repository policy requires explicit user clearance
after live feature validation.

## Three-piece follow-up: storage, upload progress, and recoverable admission

The 2026-08-31 follow-up implements the three blockers left at the end of the
Phase 5 handoff.

### Advanced scene storage is a declared generation budget

The Advanced scene lane now reserves 32 MiB per frame slot (64 MiB for the
two-slot desktop renderer) during resource-runtime initialization. Startup
validates that reservation against the frame arena's shared 1 GiB aggregate
mapped-memory guard before native allocation. The frame-preparation hot path
does not grow native storage. If both retained and compact publication layouts
exceed the declaration, the failure reports required, consumed, compact, and
declared byte counts and remains a hard non-retryable capacity failure.

The first exact run reported `storageBytesPerSlot=33554432`,
`storageReservationBytes=67108864`, and
`frameArenaAllocatedBytes=167772160`; it lowered the retained canonical
publication without `FrameStorageCapacity` exhaustion.

### Default upload preparation cannot lose its drain edge

The preparation scheduler bit now has an explicit owner contract. A synchronous
scheduler rejection releases it, an exceptional coroutine completion releases
and rearms it, retirement/device-unavailable paths clear it, and completion of
either worker preparation kind independently rechecks the render-thread drain.
Normal coroutine completion still uses the queue's atomic clear/recheck path so
a successor producer cannot have its ownership erased by a second clear.

The final isolated run remained live from startup through repeated pipeline
replacement and more than 21,000 render frames. Default was active for well
beyond the old 30-second and 45-second failure windows. Startup produced a few
bounded `RetryFrame` upload/pipeline deferrals while Sponza became resident, but
there was no `RendererPaused`, terminal transition, upload watchdog, or delayed
preparation-drain failure.

### PresentNow distinguishes retry, recoverable state, and hard terminal state

Foreground admission now has three explicit dispositions:

- `RetryFrame` rejects only the current producer epoch.
- `RecoverAfterStateChange` permits one bounded automatic probe, then requires
  an explicit window/pipeline state-change request before another bounded probe.
- `RendererTerminal` retains the permanent diagnostic latch for fixed-capacity,
  invariant, memory-integrity, and device-correctness failures.

A pipeline transition requests recovery only after its new asset is fully
applied; an explicit window circuit-breaker reset also publishes a recovery
edge. A probe is considered successful only when a fresh `PresentNow` primary
was readiness-complete, recorded this frame, submitted with a nonzero serial,
and accepted by presentation. Hard terminal telemetry is never cleared by a
pipeline replacement.

The classification remains typed through the late recording boundary: fixed
Advanced set-1/set-2/set-3 capacities, publication/transaction invariants,
native faults, and required-upload ledger terminal failures all reach the hard
latch unchanged. Only explicitly identified pipeline/target incompatibilities
are recoverable. A state-change request published while a bounded probe is
running retains its sequence and can admit the next probe if the active one
fails; it is not consumed by that failure.

### Exact-build replay

The final editor build exercised Advanced -> Default -> Advanced -> Default.
Pipeline revisions advanced `1 -> 2 -> 3 -> 4`, active resource generations
advanced through `2 -> 4 -> 6 -> 8`, and exact same-reference assignments were
strict no-ops. Before the native resize interaction, Default was exact at
1920x1080 with no pending generation or generation failure, the latest desktop
frame was `Completed/Success`, presentation was dispatched and accepted, the
device was operational, the terminal latch was null, and validation reported
zero messages/errors.

The same process then recorded two real Win32 modal sizing cycles:
`1920x1080 -> 1436x699 -> 1243x688`. Each release committed the matching Default
generation, converged the swapchain, and resumed fresh accepted `PresentNow`
frames; the log reached frame 21,167 after the second resize. The rebuilt run
contains zero in-flight descriptor-update failures, desktop-frame failures,
terminal/recovery transitions, validation errors, or VUIDs. This did not
reproduce the earlier discarded build's binding-112 descriptor-lifetime fault.

The two recorded held intervals were shorter than the one-second readiness-log
sampling cadence, so the files prove native modal-loop entry/exit and healthy
post-release presentation, not every visual frame while the mouse remained
held. Formal start/held/release screenshots and the remaining Debug Opaque row
therefore stay open. RenderDoc was not needed for this slice because the
identified failures and recovery gates were CPU scheduling/admission issues and
the final run had no unexplained GPU pass or validation fault.

`dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore`
completed with zero warnings and zero errors after the final scheduling cleanup.
No tests were added, modified, or run; repository policy still requires explicit
user clearance after live feature validation.
