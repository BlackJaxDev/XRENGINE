# Vulkan Resize Auto-Exposure Plan Lifetime

## Problem

Resizing the Vulkan editor window from `1920x1080` to `2560x1369` replaced the
main viewport render-resource generation. Shortly afterward, desktop frame-plan
sealing repeatedly failed because the `AutoExposureTex` image barrier referenced
a physical image group whose native image handle had been cleared. Resizing back
to the original dimensions briefly recovered one frame, then reproduced the same
failure.

## Evidence

- Source run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-21_19-59-20_pid36900/`.
- The resize committed a new Advanced render-resource generation.
- Auto-exposure history was copied into the new allocator, then retained while a
  planner context without `AutoExposureTex` was prepared.
- The retained history was copied forward and immediately destroyed.
- `VulkanPhysicalImageGroup.Destroy` cleared the group's image handle while a
  keyed render-graph publication and a queued `SubmissionMarker` still referred
  to that group.
- `TryFreezeResourcePlannerRenderGraphPlan` rejected the zero native image handle
  before command recording, producing `VulkanPresentNowReadinessException` and
  later `VulkanPlanPreconditionException` records.
- The reverse resize committed a replacement generation and logged one recovered
  frame, but the same retain/copy/destroy sequence repeated and frame sealing
  failed again.

## Root Cause

Auto-exposure preservation treated preparation of an unrelated render-resource
generation without an `AutoExposureTex` target as a history gap. It retained the
currently tracked main-viewport physical group even though that group remained
owned by an existing keyed planner publication. Restoring history into the next
main-viewport generation then destroyed the retained group without invalidating
the publication and queued operation that still referenced it.

Resizing back did not repair the failure because allocator state is shallow-copied
into keyed publications. The old publication therefore continued to expose the
retired allocator and its cleared image handle. A queued `SubmissionMarker` then
kept requesting that stale publication even though markers only carry a fence
receipt and perform no resource or barrier work.

## Correction

- Preserve auto-exposure history during prepared-generation construction only
  when the pending allocator actually owns an `AutoExposureTex` destination.
- Treat allocator retirement as an authoritative shared tombstone when freezing,
  refreezing, or recording a copied planner state. Ownership identity must still
  match the allocator as well.
- Classify `SubmissionMarker` as not requiring a primary recording context, and
  honor that classification while collecting planner keys, compiling the primary
  command plan, resolving operation actions, and choosing the recorder's initial
  render-graph context.
- Keep the existing active-history copy path for genuine main-viewport generation
  replacement. Unrelated UI, shadow, probe, and capture generations no longer
  retain main-viewport exposure history.

## Validation

- `rdc doctor` passed the Windows and Vulkan capture checks. A RenderDoc capture
  was not needed because the failure was fully identified in CPU-side publication
  and lifetime diagnostics before command recording.
- The targeted Vulkan project built with zero warnings and zero errors.
- Isolated editor session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260921-210935-resize-exposure-verified-0921/`.
- The resize workload completed nine alternating `1600x900` / `1920x1080`
  framebuffer resize requests, including committed Advanced and UI resource
  generations in both directions.
- A Vulkan viewport readback completed after the resize sequence:
  `Build/_AgentValidation/20260921-202515-vulkan-resize-exposure/mcp-captures/Screenshot_20260921_211225_178_5c1a6a23b49d47539ac42d6b51a6e538.png`.
- Final logs contained zero occurrences of the reported `AutoExposureTex` freeze
  failure, `VulkanPresentNowReadinessException`, `native-barrier-bindings`,
  `VulkanPlanPreconditionException`, `VK_ERROR_DEVICE_LOST`, or Vulkan validation
  VUIDs.
- One unrelated, self-recovering `XRFrameBuffer` CPU-construction publication
  warning occurred during completion maintenance. The next frame completed, and
  it did not involve auto exposure, plan freezing, PresentNow readiness, device
  loss, or Vulkan validation.
- No unit tests were added or run while validating this active rendering defect,
  per repository policy.

## Status

The original `AutoExposureTex` lifetime crash was resolved and live-validated on
2026-09-21. Follow-up user testing found a separate final-presentation defect.

## Follow-up: interactive resize presentation corruption

### Reported behavior

- During a held Win32 border drag, the window intermittently presents dense
  vertical stripes beneath the editor overlays.
- After mouse-up, the striped base can remain visible until the editor loses and
  regains focus.
- The native FPS overlay flickers while the striped base is active. This points
  to shared desktop presentation/recovery, rather than exposure alone; it does
  not exclude independent scene-rendering failures.

### Evidence and root cause

The user run at
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-09-21_21-23-03_pid50800/`
shows two related authority violations:

- Interactive recording mapped final window regions from the mutable
  `WindowResizeExtents.PresentationExtent` even though the render dispatch had
  already latched an immutable framebuffer snapshot. A modal Win32 resize could
  therefore change final viewport/scissor mapping while the same frame was being
  recorded. The resulting deferred frame entered stale-source replay, matching
  the intermittent striped base beneath otherwise legible overlays.
- Mouse-up recreated the successor swapchain before all contributors were
  current. The handoff then alternated
  `SceneCommandChainIncomplete` / `AuthoredTerminalProducerMissing` recovery for
  about 13 seconds. Those recovery presentations prevented the normal successor
  transaction from immediately converging; a focus/state change eventually
  published an authored successor and released the handoff.

Swapchain extent selection also contained a regression to
`max(framebuffer, logicalWindow)`. Those values are published from independent
Win32/Silk.NET timing points and must not be combined dimension-by-dimension.

### Correction

- Final present-scaling maps now consume `XRWindow.RenderFramebufferSize` through
  the desktop driver's render-frame latch. Mutable resize-controller extents are
  fallback-only.
- Swapchain creation trusts that same framebuffer latch and uses logical window
  size only when the complete framebuffer extent is unavailable.
- Presentation source dimensions now come from the native image allocation;
  replay validates them against that allocation instead of trusting a resized
  managed texture's dimensions.
- The successor swapchain may be created before its scene is ready. Admission
  checks the artifacts recorded for that dispatch, not mutable producer-side
  completion flags. This allows resource convergence to make forward progress.
- A terminal-empty successor submits its useful work while copying the retained,
  lease-backed authored image. It neither clears the window nor attests fresh
  scene history. The old source remains retained until a genuinely authored
  successor has been submitted and presented.
- Primary recording carries an allocation-free `HasAuthoredSwapchainWrite`
  receipt, captured before synthetic clears/replays and late overlays. Both
  retaining a drag source and completing the handoff require this receipt.
  Total swapchain writes cannot serve as proof: the continuity replay increments
  that count too. The receipt is reset before recording and retained with cached
  artifacts for reuse.

### Follow-up validation (2026-09-21)

- Isolated editor build succeeded with zero warnings/errors. Session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260921-230957-resize-authored-receipt-0922/`.
- The ordinary imported-model run revealed a separate blocker: canonical texture
  source metadata no longer matched streamed texture extents/mips. Advanced
  visibility and opaque shading then rejected every frame. `HDRSceneTex` was
  exactly zero after resize while `AutoExposureTex` stayed approximately 0.08885.
  The first rejection at 23:13:42.489 preceded interactive resize at 23:13:48.288:
  live imported textures advanced beyond the retained 8x8/one-mip canonical
  record. This is an imported-texture publication advancement defect, not
  evidence of a remaining presentation-only defect.
- A session-local settings copy disabled model imports and probes to isolate
  resize; normal repository settings were unchanged. The Advanced/Vulkan path,
  skybox, ImGui, and native FPS overlay remained enabled.
- Window grow (1400x850 to 1800x1010) and shrink (1800x1010 to 1200x720)
  exercised Win32 enter-size-move, repeated size changes, held resize, and
  exit-size-move. Captures during movement, while held, immediately after exit,
  and later showed a valid base and legible FPS overlay without stripes or a
  focus-toggle recovery. These are sampled captures, not proof that every
  presented frame is flicker-free.
- After growth, the resized 1784x971 HDR target remained finite and nonzero
  (minimum 0.0542, maximum 1.8984, mean 1.0574). A subsequent camera change
  produced a visibly different skybox image, ruling out indefinite old-frame
  replay.
- The stable run's log distinguishes synthetic replay from completion: after a
  successor at 23:21:42.775, continuity replay occurred at 23:21:42.780 and an
  authored successor completed at 23:21:42.839. The retained source was not
  released by the replay itself.
- That run logged no Vulkan exceptions, errors, validation VUIDs, device loss,
  barrier-freeze failures, or PresentNow/plan-precondition exceptions. Expected
  `RecordDeferred` recovery entries still occur when the surface changes during
  recording. The final narrow Vulkan build also passed with zero warnings/errors,
  and `git diff --check` passed.
- Evidence is under
  `Build/_AgentValidation/20260921-202515-vulkan-resize-exposure/mcp-captures/`
  (`stable-before`, `stable-after`, `stable-shrink`, `stable-camera-changed`,
  and `continuity-*.png`).
- RenderDoc tooling was healthy, but triggering a capture with its Vulkan layer
  active terminated that isolated run with RenderDoc image-subresource assertions
  before a usable capture was written. Direct GPU texture readbacks and window
  captures supplied the visual evidence instead.
- No tests were added or modified. User confirmation on the original
  scene/physical drag remains outstanding; the imported-texture publication
  mismatch is a separately identified unresolved issue.

### Rapid-drag follow-up: invalid recovery image range (2026-09-21)

The user confirmed the preceding changes were nearly successful, but stripes
still appeared when dragging outran rendering and the compositor stretched the
old surface. A faster reproducer was necessary: 240 asynchronous window size
changes at approximately 5 ms intervals, inside enter/exit-size-move, with direct
screen captures every ten changes. Unlike the previous PrintWindow-based capture,
this does not force extra paints that mask the deferred-frame path.

Findings and attempted corrections:

- The baseline reproduced the exact dense stripes beneath readable ImGui panels
  (`mcp-captures/rapid-before/`). Recovery was taking the `RecordDeferred` /
  `PresentLastCompletedContent` path while the immutable output DAG deferred all
  outputs.
- Accepted draw selection deliberately creates a logical-only presentation
  tuple. Descriptor binding filled its native handles and dimensions but never
  populated **format, aspect, or sample count**. Consequently recovery used an
  empty image aspect in both its barrier and blit, despite treating the tuple as
  complete. This is the direct invalid-copy defect.
- A submitted-layout guard alone removed stripes but produced the initialization
  clear (`rapid-submitted-layout/`). The first committed-source attempt also
  cleared (`rapid-committed-source/`). Instrumentation then proved the steady-state
  source aspect was `ImageAspectNone`; the layout lookup had no aspect to query.
  These attempts were not accepted as visual fixes.
- Recovery also selected `CaptureAnyCompleteBinding`, which proves descriptor
  preparation, not submitted content. A resize/rejected frame could replace that
  speculative publication independently of the last accepted scene.

Implemented correction:

- Native allocation metadata remains typed. Descriptor publication copies native
  format/sample count and resolves its aspect from that format, alongside the
  immutable native extent. Tuple completeness now rejects missing metadata.
- `VulkanSubmittedPresentationSource` retains the exact last submitted source
  image/view/sampler generations. Its lease is reused without steady-state heap
  allocation and replaced only when native dependencies change.
- Publication requires a successfully recorded matching final-source draw,
  authored swapchain work, accepted graphics submission, transferred lifetime
  pins, and successful post-submit image-state publication. Single-draw and
  secondary-run paths retain the source marker on the command artifact; descriptor
  preparation or unrelated output writes cannot advance the recovery receipt.
- Recovery consumes the retained receipt (or explicit resize handoff source),
  validates its lease, and transitions from the submitted layout ledger. Its
  conservative recovery-only barriers cover prior image reads/writes; the copy
  restores the source layout and independently pins its command-buffer uses.
- Submitted layout state now survives retirement enqueue and wrapper rebinding.
  It is cleared at generation-checked native image destruction, because a retained
  image may legally remain replayable while pending retirement. Shutdown releases
  the submitted-source lease.

Validation:

- Final isolated editor build: zero warnings and errors. No tests added/modified.
- Same Advanced/Vulkan session-local scene isolation as above. Three corrected
  rapid-drag runs produced valid stretched scene images without stripes or purple
  clears in inspected movement/held/release captures. The final run includes all
  receipt and retirement safeguards (`rapid-final/`, PID 52880); earlier corrected
  repeats are `rapid-native-metadata/` and `rapid-native-metadata-repeat/`.
- Final run: replacement swapchain ready at 23:53:39.004; continuity work at
  23:53:39.013; authored successor presented at 23:53:39.097. No focus toggle was
  used. A subsequent camera change produced a visibly different scene, captured
  through MCP in `rapid-final-camera/`, ruling out indefinite stale replay.
- Final steady/runtime log had zero exceptions, device-loss reports, missing
  replay-source reports, or VUID messages. Diagnostic preset was Off; this is not
  a claim of validation-layer coverage. Relevant log session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260921-230957-resize-authored-receipt-0922/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-21_23-53-18_pid52880/`.
- Native FPS text is absent in sampled all-output-deferred recovery frames and
  returns on the authored successor. This copy fix does not synthesize missing
  native overlay commands or promise fresh layout for every drag position.
- Screenshots sample presented output, not every frame. User confirmation of the
  original physical rapid-drag case remains the final external check. All named
  isolated editor processes started for this investigation were stopped.

### Overlay, live layout, and delayed release flash follow-up (2026-09-22)

User reports: the stripe fix is nearly successful, but native FPS text disappears,
layout stretches/jumps during fast dragging, and a black/incorrect frame appears
about half a second after release. These are not considered resolved by the
preceding sampled captures.

Evidence and current corrections:

- The user's `xrengine_2026-09-22_00-00-54_pid47820/log_vulkan.log` contains
  seven authored-successor presentations immediately followed by an explicit
  `fresh full-surface clear for empty exact-output` on the next frame. Missing
  producer work was being interpreted as an intentionally empty scene. Viewport
  pacing can skip scene production while continuing UI; it supplies no authority
  to clear a previously completed scene.
- Desktop primary recording now classifies a missing terminal as
  `NoAuthoredOutput` only after recording real operations and observing zero
  authored swapchain writes, before synthetic finalization. An earlier attempt
  to use the plan's missing-terminal flag before recording incorrectly rejected
  valid scene frames and was removed. It uses the recovery transaction and leased submitted source,
  not a manufactured exact-output clear. Recovery cannot complete scene/history
  receipts. Explicit authored clears remain authored work; external exact targets
  retain their separate behavior.
- Deferred primary recording returned before generating the dynamic FPS secondary.
  Additionally, mesh preparation replaced results carrying fresh UI artifacts
  with an empty generic failure. The recovery path now receives the current
  image's freshly recorded color-only text secondary and any upload batch.
- Extent-only swapchain recreation unconditionally invalidated the compatible
  dynamic ImGui pipeline, synchronously recompiling both shaders. Compatibility
  comparison now retains the pipeline for unchanged formats/layout/backend;
  legacy render-pass changes remain conservative. The first isolated live run
  measured 17.5 and 24.9 ms recreates, versus approximately 160–180 ms in the user
  log. That first run still lost FPS text in some moving captures, prompting the
  additional artifact-forwarding correction above.
- Release no longer waits an additional 250 ms after the native interactive
  resize transaction has already ended. The latest coalesced extent is applied
  after the existing short debounce.
- Screen-space ImGui metrics used the asynchronously laid-out native canvas size,
  then scaled it to the current viewport: even fresh ImGui could therefore stretch
  an old layout. Metrics now use the viewport and latched client/framebuffer DPI.
  Interactive viewport presentation layout is reconciled with that same latch
  before authoring, without resizing the heavy internal scene allocation.

Validation in progress. The rapid-resize capture helper now buffers screenshots
in memory before PNG encoding, allowing dense post-release sampling without
forcing window paints. RenderDoc doctor passed; the previously recorded capture
layer crash remains a tooling limitation. No tests added or modified.

Dense live sampling (`rapid-recorded-writes/` and `rapid-independent-overlay/`)
found no black/incorrect scene frames over 1.5 seconds after release, but still
captured missing FPS text for approximately 100 ms. Retaining independent UI
operations through output admission was therefore insufficient: scene-history
candidate rejection can return before the UI producer collects any operations.
This remaining producer issue is still being corrected, not accepted as solved.
Later extent-only recreates measured approximately 13–22 ms. A camera change
and inspected MCP screenshot confirmed continued scene rendering after recovery.
The native review also identified and removed an obsolete positive `ActualSize`
guard around screen-space metrics; screen-space sizing no longer depends on an
asynchronous canvas layout having completed at all.

The independent UI producer must also run at the earlier `XRViewport` history
authoring rejection, before entering the scene pipeline. Frame-plan admission
classifies that nested UI as `UiPreview`, not `DesktopScene`; the retained lane
is restricted to targetless `UserInterfaceRenderPipeline` on-top mesh draws with
no consumer dependencies. It does not mark the scene output executable.
The `rapid-viewport-overlay/` iteration (PID 56044) kept FPS text visible in all
24 inspected moving captures and all 68 post-release captures, with no black or
corrupted scene frame. The release recreate measured 14.0 ms.

The final cleanup removes three unused UI-owned depth resources. They caused an
unnecessary generation replacement and invalidated the collected UI package on
release, despite neither UI rendering path consuming them. Normal package
generation/descriptor/command/collect validation stays intact. The existing
`RetainedAuxiliaryPipelines_DeclareTheirOwnedResources` test asserts those old
resources and needs an expectation update once the user clears test work; no
test changes have been made during feature validation.

Final validation (PID 39848):

- Isolated editor build succeeded with zero warnings/errors. `git diff --check`
  passed. Tests remain unchanged, pending explicit clearance requested in chat.
- `rapid-layoutless-final/`: all 24 inspected movement captures and 71 dense
  post-release captures retained FPS text and a valid scene. No focus toggle was
  used. `rapid-layoutless-immediate-release/` repeated the test without the
  100 ms pre-release hold; all 70 inspected release captures retained both.
- Final recreates measured 12.2–27.9 ms (first 20.9 ms), compared with roughly
  160–180 ms before retaining the compatible ImGui pipeline. The extra 250 ms
  release quiet period is gone.
- The runtime log contains no exceptions, device-loss reports, VUID messages,
  or manufactured fresh-empty full-surface clears. The rendering log contains
  no UI resource-generation replacement/mismatch. Diagnostic preset remained
  Off; these observations do not claim validation-layer coverage.
- A camera cut from yaw 135 to 45 produced a visibly different scene in the
  inspected GPU-readback screenshot under `layoutless-final-camera/`, proving
  rendering resumed rather than preserving the same image indefinitely.
- Log session:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260921-230957-resize-authored-receipt-0922/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-22_01-03-10_pid39848/`.
- Testing used the previously documented isolated Advanced/Vulkan skybox scene,
  not the original imported-model workload. The named editor session was stopped.

Residual limits: the compositor can still stretch a previously presented frame
when window events outrun rendering. Fresh ImGui uses the latched live extent;
this change does not promise a new scene render for every native resize event.
Recovery's independent UI admission covers resident on-top text/mesh draws, not
new texture-upload work when all scene terminals are deferred. The RenderDoc
capture-layer assertion described above remains an unresolved tooling limit.
User confirmation on physical dragging is still outstanding.

Independent API reasoning run `d36252bb791a4d4486b9fb853a5bf841` completed with
requested/actual `gpt-5.6-sol`. Its one attempted broader repository lookup was
denied by the narrow root policy; its conclusion used the supplied snapshots.
Local source tracing and live validation remain the implementation authority.
