# Vulkan DLSS, engine AA, and frame generation

## Requested behavior

Default and Advanced pipelines must support native Vulkan DLSS Super Resolution,
DLSS frame generation, and TSR with frame generation. DLSS Super Resolution
provides anti-aliasing and suppresses engine MSAA, TAA, TSR, FXAA, and SMAA while
enabled. Disabling it restores the authored AA selection. Frame generation alone
preserves that selection.

## Measured functional results

Native Vulkan desktop mono, RTX 4070 Laptop GPU (8188 MiB), driver 616.56,
Streamline runtime 2.11.1 with host SDK 2.12.0. The final reviewed build passed
with zero warnings and errors (29.60 seconds). These foreground samples preceded
the last retained-replay correction and confirm actual generated presentation, not just SDK
capability or a successful super-resolution dispatch:

| Pipeline and case | Rendered frames | SDK presentations | Evidence report |
| --- | ---: | ---: | --- |
| Advanced SR + FG | 130 | 260 | `advanced-foreground-verified-sr-fg` |
| Advanced TSR + FG | 127 | 256 | `advanced-foreground-verified-tsr-fg` |
| Advanced TSR, FG off | 135 | 0 | `advanced-foreground-verified-tsr-off` |
| Advanced TSR, FG restored | 129 | 260 | `advanced-foreground-verified-tsr-on` |
| Advanced TSR after resize | 124 | 250 | `advanced-foreground-verified-tsr-resize` |
| Advanced SR after refocusing | 134 | 272 | `advanced-final-sr-on` |
| Default SR after camera change | 127 | 252 | `default-final-verified-sr-switch` |
| Default SR, FG off | 88 | 0 | `default-final-verified-sr-off` |
| Default SR, FG restored | 124 | 248 | `default-final-verified-sr-on` |
| Default SR, settled repeat | 127 | 258 | `default-final-verified-sr-settled` |

All listed samples reported no vendor error and zero retired swapchain
generations pending. Composited captures were viewed for both pipelines and
multiple camera positions/extents; all three thin rods and the full scene area
were present. Engine MSAA remained authored in the UI while the SR frame profile
reported effective AA `None`; disabling SR restored effective TSR and its scale.
The later retained-source transition failure and corrected-build revalidation
are recorded below; the foreground samples alone do not close that failure.

Vulkan validation layers were disabled in this RenderDoc-friendly run, so zero
runtime vendor errors is not a validation-layer cleanliness result. These are
bounded functional checks, not dense consecutive-frame interpolation or latency
acceptance. The generic viewport-sequence capture path does not support this
native vendor final output; composited screenshots were used instead.

The measured frame-generation mode is `OneX` (one generated frame). This does
not certify multi-frame generation, HDR, XR/external swapchains, or the OpenGL
bridge. DLSS features must be provisioned when the Vulkan renderer is created;
live enablement of a feature absent from device bootstrap still requires renderer
recreation and reports that requirement explicitly.

## Findings and implementation

- Both pipelines conflated vendor upscaling with frame generation when suppressing
  AA. Separate reconstruction selection from the final vendor presentation path.
- Advanced selected vendor output before selecting resolved AA output. Route the
  selected TSR/FXAA/SMAA output through frame generation instead.
- Native vendor validation required color, depth, and motion to share one extent.
  TSR supplies display-resolution color with render-resolution depth and motion.
  Keep matching depth/motion extents as the dispatch input extent; require
  matching color only for DLSS Super Resolution and display-size color for FG.
- Vendor constants read the mutable general temporal snapshot after history
  commit. Use the immutable snapshot captured at temporal Begin for the current
  resolve, preserving previous matrices and reset state.
- Suppress engine AA in the effective frame profile, including MSAA resource
  selection, without overwriting camera or global AA settings. Keep temporal
  jitter, velocity, and history preparation active for DLSS.

## Validation scope

The named isolated session is `dlss-aa-framegen`. Disposable evidence belongs to
`Build/_AgentValidation/20260924-153000-dlss-aa-framegen/`; session build/logs live
under the shared MCP-session root. Initial validation targets Vulkan desktop
mono and both pipelines, checking upscaling alone, frame generation alone, TSR
plus frame generation, combined DLSS upscaling/frame generation, camera motion,
and AA restoration after toggles. Generated-frame presentation counters and
visual captures are required; a successful SR dispatch alone does not validate FG.

No tests are added or modified during this live regression investigation.
User confirmation is pending. The separate remaining TSR thin-edge shimmer
quality gate remains open in the [validation tracker](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#motion-history-and-reset-matrix).

The bounded broker inventory completed on the requested `gpt-6-luna` model and
confirmed the input extent checks and temporal accessor/scale assignments.
GPU correctness decisions and local validation remain the coordinator's work.

### Earlier diagnostic attempts

The following chronological notes include failed intermediate builds. Their
pending corrections are resolved by the final results below unless explicitly
retained as an outstanding validation item.

Hardware: NVIDIA GeForce RTX 4070 Laptop GPU, driver 616.56, 8188 MiB VRAM.
The first isolated editor build succeeded with zero warnings/errors.

- Enabling FG at startup failed because the transfer-family selector chose the
  optical-flow family, consuming its only queue before Streamline requested its
  exclusive queue. Exclude optical-flow families from engine queue selection;
  keep selected/published family indices consistent with logical-device creation.
- A separate SR-only launch reached successful native evaluation with
  1267x712 color/depth/motion and 1920x1080 DLSS output. History reset became
  false after seeding. The viewed composited capture exposed a partial-screen
  image: Advanced retained its internal render area for the final vendor draw.
  Give desktop output the display-size viewport scope used by Default.
- FG-only spatial AA did not prepare temporal matrices/history. Both pipelines
  now run the temporal lifecycle for FG without jittering spatial AA modes.
- Native motion input requires current-to-previous texture displacement, while
  engine velocity stores current-minus-previous NDC. Use X scale -0.5 and the
  framebuffer-axis-dependent Y scale (+0.5 for Vulkan Y-up clip policy).
- A fixed subnative input extent incorrectly enabled Streamline dynamic-resolution
  pacing. Leave that option off for the engine's fixed upscale ratios.

These initial results are diagnostic evidence, not final feature acceptance.
The corrected build and full two-pipeline toggle matrix remain pending.

The second build passed with zero warnings/errors. Excluding the optical-flow
family removed the startup failure. FG then failed explicitly because the
presentation-profile resolver ignored the FG feature setting: ordinary Stable
presentation still created a direct swapchain. Promote an active FG request to
the FrameGeneration presentation profile and compare the live request during
preflight so toggles recreate the swapchain. Do not infer this from the already
published profile, which describes the previous swapchain.

With FG temporarily disabled, the second build's viewed Advanced SR capture
showed all three test rods across the full display area. The resource profile
reported `aa=None` with the authored TSR selection retained, 1267x712 internal
and 1920x1080 display. This confirms the partial-screen presentation fix.
An unrelated broad camera-component reflection query timed out; further
validation should use targeted property reads rather than that query.

The third build also passed with zero warnings/errors. Its Advanced FG startup
selected the Streamline Mailbox proxy and reported a nonzero presentation total
(22), but stopped making progress after repeated acquire-unavailable/recreate
cycles. Composited captures timed out and the counter stayed at 22. This is a
failed FG acceptance attempt, not successful sustained generated presentation.
The active window remained 1920x1080, non-minimized, and non-interactively resized.
Investigate WSI completion reservations and proxy acquire/present ownership.

Live retirement telemetry confirmed the cause: generation 8 had three submitted
presents, zero completed presentation fences, and three capacity deferrals; seven
old generations remained pending. Streamline proxy images require their SDK
acquire/present semaphore protocol and intercepted device-drain hook, rather than
direct WSI maintenance fences. A proxy-specific lifecycle correction is in progress.

Default SR-only runs reported effective `aa=None` for authored MSAA, TAA, FXAA,
SMAA, and TSR selections. Turning SR off restored TSR at 1286x723 internal extent;
SR used 1267x712 with 1920x1080 output. Fresh stationary and second-camera-position
captures showed black test rods across the full display. An earlier capture had
bright/brown bands on the rods; the repeat did not reproduce them. The captured
pre-DLSS color was clean, stationary motion was zero, and depth was finite.
Do not treat these spot checks as exhaustive temporal-quality acceptance.

The generic viewport sequence capture could not find a transfer-readable final
color image on the native vendor path. Composited-window captures succeeded.
Record this evidence limitation rather than claiming a completed frame sequence.
RenderDoc tooling and its Vulkan layer passed `rdc doctor`.

RenderDoc-friendly startup deliberately omits optional FG provisioning when FG
is initially off. Its later enable attempt reports that a renderer restart is
required. Validate live FG toggles from an FG-provisioned startup; this diagnostic
preset restriction is separate from the presentation-fence defect.

### Proxy correction and generated-frame evidence

Removing direct WSI fences from proxy admission eliminated the steady-state
freeze. Retirement still requires successful Streamline-intercepted device idle
under exclusive queue admission. Proxy handles must only be destroyed through
the SDK, and destruction validates release proof before changing registration.

Advanced produced 240 SDK-reported presented frames over 120 renderer frames
with TSR plus FG, and the same ratio with DLSS SR plus FG. Both viewed captures
showed the full image. Default initially reported approximately one presented
frame per rendered frame despite a successful SDK status; that is not acceptance
of generated presentation. The overlay incorrectly added one to the SDK count;
the count already includes the real frame. Correct the estimate and labels.

Off/on testing found a second lifecycle defect: Streamline admits only one proxy
swapchain, while deferred retirement left the old proxy alive during successor
creation. Stage proxy-involved replacement across preflights: release current
attempt leases, poll ordinary graphics/resource retirement, destroy the old
proxy, and only then create its successor. Never pass handles between native and
proxy `oldSwapchain` namespaces. Integrated validation of that correction is pending.

Further SDK audit requires Immediate presentation for Vulkan FG and centered
camera pinhole offsets instead of invalid sentinel floats. With those changes,
Default SR plus FG produced 244 SDK-reported presentations over 121 renderer
frames, and Default TSR plus FG also reported two presentations per query.
A later fresh launch reported only one per rendered frame despite successful
SDK status; consistent pacing still needs validation. Keep exact state counters
separate from estimated FPS.

The staged replacement attempt exposed a detached swapchain-image retirement
block after graphics markers, proxy presentation, views, and framebuffers had
passed. Successful intercepted device idle did not publish the engine's tracked
graphics/transfer/other completion frontiers. Publish that proof under the same
exclusive queue-admission gate before marking proxy presentation drained,
matching the ordinary device-idle path. Structural reference counts remain
mandatory; no forced retirement is used. Live transition revalidation follows.

The next attempt proved all queue frontiers complete but retained one recorded
reference. Exact command ownership identified `ScreenshotReadback[0]`, already
CPU-owned after a finished capture, as the owner. Its reusable command buffer
must release recorded image dependencies after successful readback completion;
otherwise taking a validation screenshot blocks subsequent proxy replacement.

After releasing completed screenshot command-buffer dependencies, Default's
first full transition sequence passed: SR plus FG produced 264 presentations
over 130 rendered frames; TSR plus FG produced 250/124; disabling FG left the
counter unchanged while rendering continued; reenabling produced 248/124.
Resizing from 1920x1080 to 2378x1294 produced 246/121 with all three rods visible.
The viewed SR output was also intact from a second camera position, with authored
MSAA still visible in the editor and effective AA reported as `None`.

These results do not close lifecycle acceptance. A later native-to-proxy
transition after SR/resize changes stalled again, and Advanced reproduced that
direction of the transition. The staged diagnostic now covers retiring native
generations as well as proxies. Default also reported persistent one-to-one
presentation after a later camera/SR change. SDK logs demonstrate interpolation
can become disabled while its mode is still `On`; source tracing ruled out a
persistent frame-token offset or stale host-mode cache.

The required `slReflexSleep` call is now placed before desktop scene preparation,
using the upcoming attempt's frame token. The next Advanced run produced
288 presentations over 143 renderer frames with SR, and 264/132 with TSR.
This supports the pacing correction; repeat transitions and longer intervals
are still required. Simulation markers and the full
[NVIDIA Reflex integration checklist](https://github.com/NVIDIA-RTX/Streamline/blob/main/docs/ProgrammingGuideReflex.md)
remain outstanding. Measured generated frames do not establish latency acceptance.

The native-to-proxy retirement diagnostic isolated a different completion
publication problem: an ImGui overlay command buffer retained three recorded
references to an old swapchain view. Its last graphics sequence was 900 while
the completed frontier remained 899, despite the graphics marker fence and WSI
presentation proof both having passed. The marker's lifetime-frontier
publication was the final lifecycle correction; native presentation proof and
structural reference checks remain independent.

## Final lifecycle corrections

Retirement markers previously used a native-only queue submission overload that
did not register their fence in the engine lifetime ledger. The new tracked
marker submission reserves ledger capacity before submitting, serializes with
ordinary queue submissions, and publishes its exact graphics sequence and fence
only after native success. Completing that fence now advances the preceding
same-queue work truthfully. Repeated native/proxy transitions no longer retain
the old ImGui image view in the final matrix; no forced release was introduced.

Completed screenshot readback command buffers now reset after fence completion,
releasing recorded image references immediately. Intercepted SDK device-idle
proof also completes the engine's tracked queue work under exclusive admission
before publishing proxy presentation completion. These are separate requirements
from destroying the proxy only after ordinary resource retirement succeeds.

### Window visibility and generated presentation

The final Advanced build initially reported about one SDK presentation per
rendered frame while the editor was in the background. Restoring/focusing the
same owned editor, with no rendering setting or code change, produced 272 SDK
presentations over 136 rendered frames. Another post-toggle interval recovered
to two presentations per frame after refocusing. Treat window visibility/focus
as a validation condition; the observation does not prove a universal SDK rule
that every unfocused window suppresses generation. Preserve the background
intervals as separate evidence, rather than counting them as generated frames.

The final matrix requests restoration/focus of the owned editor after settings
and swapchain transitions and records native foreground state before/after each
sample. Windows denied some foreground requests; those intervals remain
background or mixed-focus evidence. A value of two in the SDK state includes the real
frame and one generated frame. Counter snapshots are separate calls and can
differ by a few frames from an exact 2:1 ratio.

### Additional SR-to-TSR transition failure

After the Default matrix completed, one additional SR-to-TSR switch at 2378x1294
ended the session. A rejected scene recording tried to replay the last completed
source. Its exact template lease still retained the image, but the ordinary
command dependency validator rejected the image's pending-retirement state
before recording the Vulkan barrier. This is a retained-source recording
admission mismatch, separate from proxy swapchain retirement. The follow-up
must preserve exact generation ownership without admitting arbitrary retired
resources. Earlier successful toggle samples do not close this failure.

The correction adopts the exact still-keyed source image from its active
structural lease into the replay command buffer before the first native
barrier. An independent recorded pin and generation-specific authorization
survive recording and submission, then clear with ordinary command dependencies
on reset/abandon/free. Detached, replaced, destroyed, or otherwise unowned
generations remain rejected. The image-layout journal uses that exact generation
and requires a matching submitted receipt. Both ordinary continuity replay and
rejected-frame replay use this admission path; general retired-resource
recording admission is unchanged.

The corrected Default build completed all 12 cases in
`reports/default-replay-fixed-verified-*.json`: SR plus FG, TSR plus FG, TSR FG
off/on, resize, camera change, SR FG off/on, a settled repeat, then TSR/SR/TSR
switches. All samples had a null vendor error and zero retired swapchains
pending. The saved Vulkan log contains no recording failure or first-chance
exception. Viewed resized TSR and second-camera SR captures contain all three
rods over the full scene area.

These final Default intervals were mostly unfocused, producing approximately
one SDK presentation per rendered frame. Two mixed-focus intervals reported
202/127 (TSR) and 178/127 (SR), showing additional generated presentations after
the correction without claiming a steady 2:1 ratio from those intervals. Earlier
steady foreground samples remain separate above. Final Default logs are
`logs/default-replay-fixed-vulkan.log` and `logs/default-replay-fixed-sl.log`.

The same reviewed build completed all 12 Advanced cases in
`reports/advanced-replay-fixed-verified-*.json`, including the repeated
large-window SR/TSR switches. Every sample reported no vendor error and zero
retired swapchains pending. The saved Vulkan log contains no recording failure
or first-chance exception. Final foreground counts were SR 292/146, TSR 258/129,
TSR after FG off/on 264/132, TSR after resize 262/131, and the final TSR repeat
256/128 (SDK presentations/rendered frames). Background and mixed-focus samples
remain separate in the reports. Resized TSR and second-camera SR/TSR captures
were viewed and showed the full scene with all three rods.

Final Advanced logs are `logs/advanced-replay-fixed-vulkan.log` and
`logs/advanced-replay-fixed-sl.log`. The owned isolated editor session was stopped.
The requested AA routing and bounded Vulkan desktop transition checks are
complete. Remaining acceptance work is localized TSR thin-edge shimmer, dense
consecutive-frame motion/disocclusion quality, full Reflex simulation markers
and latency validation, and validation-layer/GPU-capture evidence. These checks
do not certify HDR, multi-frame generation, XR, or OpenGL bridge behavior.
