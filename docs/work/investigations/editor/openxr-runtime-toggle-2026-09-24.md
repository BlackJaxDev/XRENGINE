# Editor OpenXR runtime toggle

Status: Session admission and pawn switching validated with pending scene streaming. Ordinary eye readiness defers without render-error backoff. After viewport-scoping and frozen BRDF-size corrections, two Monado sequential-view cycles restored visible desktop rendering. Hardware and single-pass stereo remain unqualified.

## Request and behavior

The ImGui editor can start with `VR.Mode=Desktop`, prepare either the selected OpenXR runtime or Monado, and enter or leave VR from the toolbar. The local player controls the editor flying camera at startup, the VR pawn while the OpenXR session runs, and the editor flying camera after shutdown. Runtime selection and Vulkan device capabilities remain fixed for the life of the renderer.

The opt-in setting is `VR.EditorToggleRuntime` with `OpenXR` or `MonadoOpenXR`, together with `VR.AllowDesktopEditing=true`. The setting leaves `VR.Mode` as `Desktop`.

Desktop launches now prepare only the renderer for the selected runtime. The
toggle creates an owned VR rig when the session starts, then restores the original
desktop pawn and destroys that rig after rendering stops. Missing desktop pawns
are created on return. Existing authored rigs remain externally owned.

## Findings

- The prior pawn switcher observed OpenXR session changes but did not start or stop the runtime. A `Desktop` launch also omitted the VR pawn and Vulkan OpenXR device preparation.
- The editor flying camera is moved into the editor scene. Searching only the Unit Testing world's root nodes cannot find it; the switcher retains the currently controlled editor pawn.
- `EnqueuePossessionByLocalPlayer` queues behind the already controlled desktop pawn. Runtime switching must use immediate possession to transfer control.
- A requested stop needs state-machine updates until session teardown finishes. A renderer-owned Vulkan OpenXR instance remains paired with its device so another session can start on the same renderer.

## Validation

- Editor build: zero warnings and zero errors. Settings/schema and MCP tool documentation regenerated.
- A named isolated editor session using a minimal `Default` world, Vulkan, `VR.Mode=Desktop`, `VR.EditorToggleRuntime=MonadoOpenXR`, `VR.AllowDesktopEditing=true`, and no imported models started with no OpenXR API and `EditorFlyingCameraPawnComponent` controlling `Editor View`.
- The toolbar's toggle path started a real Monado OpenXR session. Runtime diagnostics reported `SessionRunning`, a Vulkan renderer, two swapchains, and submitted frames. The controlled pawn changed to `CharacterPawnComponent` on `VRPlayspaceNode`.
- Clearing the toggle reached `DesktopOnly` and restored `EditorFlyingCameraPawnComponent` on `Editor View`. A second on/off cycle completed in the same editor process with the same possession changes.
- Desktop screenshots from two camera positions changed with the view. A screenshot of the running editor showed the checked toolbar control and session status. OpenXR eye screenshot capture was unavailable because the minimal validation settings did not publish eye preview copies.

## Remaining observations

- The earlier saved Unit Testing world reached `XrSystemReady` while imported texture streaming reported 53 pending requests. This snapshot did not establish a permanent queue stall or justify calling the scene asset-heavy. Stopping the request returned to `DesktopOnly`.
- In the minimal-world run, the Vulkan logs recorded one direct-eye frame-package authority rejection, desktop `PresentNow` readiness failures, and deferred OpenXR swapchain retirement blockers. These did not prevent two session and pawn cycles, but eye rendering and retirement were not qualified by this validation. Inspect renderer diagnostics before treating this as a production VR path.
- Hardware OpenXR was not exercised; validation used Monado without a headset.

Disposable evidence is under `Build/_AgentValidation/<openxr-toggle-run>/` and the named MCP session logs under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/`.

## Session admission and texture progress follow-up

- Removed the global import/decode/upload drain, minimum accepted desktop frames,
  first-frame observation, command-buffer quiet delay, pending-frame delay, and
  unrelated allocator-pressure heuristics from session admission. Retained valid
  device, startup presentation ordering, and inactive desktop CPU-frame checks.
- Kept the actual synchronization in `ExecuteOpenXrRuntimeGraphicsTransition`:
  exclusive device queue admission, submitted-work completion, and device-idle
  waits around runtime graphics setup. Eye resource readiness is independent.
- The isolated `xr-upload-baseline` run used the saved world with Vulkan and
  sequential OpenXR views. Before the guard change, texture completion progressed
  from 55 to 494 to 1,106 chunks, ending with zero pending transitions, zero failed
  uploads, 132 final publications, and 2,038,699,024 cumulative uploaded bytes.
  Cumulative bytes include repeated mip/residency transitions; they are not a
  measurement of simultaneous GPU residency.
- That baseline eventually created a session after the texture work drained.
  The earlier persistent stall was not reproduced in this fresh build, so no
  speculative upload scheduling change is warranted. A snapshot with no active
  worker can be the interval between bounded batches. Desktop captures from two
  camera positions were viewed and showed the imported avatar from different views.
- No tests were added or modified; live feature validation precedes test changes.
- The changed-guard run `xr-upload-narrow` started Monado during an active import
  and subsequently reported `SessionRunning` with 52 pending texture transitions.
  It possessed the configured flying VR pawn on `FirstPersonViewNode`. Stopping
  restored `EditorFlyingCameraPawnComponent` on `Editor View` and `DesktopOnly`.
- A second start was requested with 21 transitions and 12 upload requests pending.
  It reached `SessionRunning` while uploads continued. Sampled completed chunks
  increased from 29 to 290 to 423 to 471; the final sample had zero pending
  transitions and zero upload failures. Runtime diagnostics reported 406 submitted
  frames and zero end-frame failures. The second stop restored desktop possession.
- Build after removal of the obsolete guard state/aliases: zero warnings/errors.
  Captures were viewed, including two desktop positions after stop. They remained
  black despite recovered desktop frame submission. Logs contain direct-eye
  frame-package authority rejections already observed before this change; the
  relationship between early session startup and the persistent black view needs
  isolation. Session state and possession success do not qualify rendering output.
- The `xr-upload-late` control waited until import scopes, pending transitions,
  active decodes, and queued decodes were all zero before requesting VR. It still
  showed a black desktop after stopping VR. Early startup during import is not
  sufficient to explain the black view; do not reinstate the global asset drain
  to conceal that separate activation/restoration problem.
- A concrete cause of prolonged eye preparation was found: the viewport's normal
  resource-readiness `false` result became an `InvalidOperationException` labeled
  as invalid OpenXR frame-package authority. The strict OpenXR exception path then
  triggered whole-window render backoff from 100 ms to 1,700 ms. In the early run,
  left-eye construction performed 13.54 ms of work over 18.68 seconds and right-eye
  construction performed 14.21 ms over 32.05 seconds. This backoff also delays
  render-thread texture work. The fix propagates ordinary deferral through the
  typed eye emitter, discarding partial operations and releasing capture leases.
  Missing camera/viewport, capacity, and extent invariants retain their errors.
- Final isolated run `xr-upload-fixed`: build succeeded with zero warnings/errors;
  Monado started during import, possessed the flying VR pawn, and continued texture
  progress with no upload failures. Completed chunks advanced from 12 to 37 to 250
  while the session was running. A runtime sample recorded 252 submitted frames,
  62 no-layer frames, and zero end-frame failures. Clearing the toggle restored
  `DesktopOnly` and the editor flying camera.
- No misleading direct-eye authority exceptions or associated whole-window render
  backoff occurred in the final run. Eye resource generations committed in 1,018 ms
  and 1,422 ms wall time (14.12 ms and 12.72 ms work). These are individual debug
  runs, not a controlled performance benchmark. A separate initial BRDF extent
  mismatch still exercised the existing resource-generation retry policy.
- Two final desktop camera captures were viewed and remained black. Rendering
  restoration is not fixed by either the narrowed startup guard or the eye defer
  correction. The next rendering investigation should compare desktop resource
  bindings/output before activation and after teardown, with a GPU capture.
- All four follow-up editor sessions owned by this task were stopped. No hardware
  headset run or automatic-on-launch regression run was performed.

## Desktop restoration investigation

- The `xr-black-capture` run captured and visually inspected a valid desktop GPU
  output before activation. The first stop also restored a visible avatar and
  panorama, but eye resources had not finished committing and no projection frame
  had been submitted. This does not reproduce or disprove the later black output.
- A subsequent RenderDoc capture exceeded its completion timeout and disrupted
  the diagnostic session. That session and its replay session were stopped. The
  capture timing is unsuitable for a normal-performance comparison.
- Source review found no missing pawn/camera restoration: possession switches the
  desktop viewport back, pipeline replacement resets its instance/output state,
  and teardown clears the OpenXR frame-view publication.
- Found a concrete Advanced pipeline resource inconsistency in this run:
  `AdvancedRenderPipeline#10`, `external=Window`, declared a 512-pixel BRDF lookup
  texture but created 256 pixels, then reported a same-key layout change. Its
  runtime restriction predicate used global OpenXR state even for desktop passes.
- Restricted Advanced eye behavior to the current external-swapchain viewport.
  Advanced and Default BRDF factories now capture the declared size from the
  frozen generation profile instead of re-reading mutable rendering context.
  Advanced uses 256 for mono external swapchains and 512 otherwise. Default keeps
  its existing profile policy. Live validation of the changes follows below.
- `xr-scope-fix` built with zero warnings/errors. Two on/off cycles reached
  `SessionRunning` with submitted projection frames (samples: 58 and 38) and zero
  end-frame failures. Both stops restored `DesktopOnly` and the editor flying pawn.
- Captured desktop HDR, post-process, exposure, and FXAA resources at one frame
  boundary after the first stop. All RGB samples were finite; exposure was about
  0.350 and the final output contained the avatar. Viewed the final texture and
  composited screenshots after both cycles, including a changed camera position.
  The persistent all-black viewport did not recur in this run. Material appearance
  and full eye-image quality were not qualified by this check.
- No BRDF extent/layout mismatch or render-error backoff was found in the run.
  Eye resource builds committed in approximately 2.22 and 1.87 seconds wall time,
  with 13.68 and 11.13 ms construction work. This remains a debug run, not a
  performance benchmark. The owned validation session was stopped.

## Dynamic pawn lifetime correction

- User reported the default world's checkbox rejected activation because both
  pawns were required to pre-exist. That requirement belonged to the switcher,
  not OpenXR. It also recognized only an editor flying camera as the desktop pawn.
- Removed the pre-existence requirement. The update-thread transition captures
  any usable controlled desktop pawn, creates a VR-only hierarchy through the
  shared bootstrap factory when necessary, and possesses it. Stopping restores
  that pawn or creates an editor camera fallback, then retires the owned hierarchy.
- Kept renderer preparation independent of startup pawn selection in editor launch
  and render settings. `VR.Mode=Desktop` no longer constructs a dormant VR pawn.
- Rig removal waits for the runtime to stop rendering. Detached, inactive,
  destroyed, or destruction-queued pawns cannot be reused. Authored rigs are never
  destroyed by the switcher. Status diagnostics include the owned rig and controlled
  pawn node IDs to verify lifetime across repeated cycles.
- Live `xr-dynamic-pawns` validation: final editor build had zero warnings/errors.
  The default world began with the desktop pawn and no `VRPlayspaceNode`. Two
  Monado sequential-view cycles created different owned rig IDs, submitted frames
  (samples: 115 and 78, zero end-frame failures), restored the exact original
  desktop node ID, and left no owned rig. Looking up either retired rig ID reported
  the node was not found.
- In a third cycle, deleted the original desktop pawn only in the isolated test
  world while VR was running. Stopping created and possessed a new editor flying
  camera pawn and retired the VR rig without a switcher error. This validates the
  missing-desktop fallback through the live path. No user world was saved or changed.
- Viewed desktop screenshots from two positions after normal stops; scene output
  remained visible. Existing material/noise appearance is outside this pawn-lifetime
  check. No tests were added or modified; character locomotion and hardware input
  were not exercised in this run.

## Runtime chooser and single-pass startup

- User reported that the temporary rig appeared outside the editor scene, activation
  froze the view, and Monado started without asking. The switcher inserted the rig
  directly into world roots and used the saved runtime preference. Temporary roots
  now use the editor scene integration. Enabling the toolbar opens a Monado testing
  or SteamVR headset chooser; cancellation leaves desktop control unchanged.
- Removed the temporary `EditorToggleRuntime` preference. `VR.Mode=Desktop` remains
  the launch mode; runtime preparation occurs after explicit selection. Renderer
  replacement retires the old native instance before changing process manifest
  values, restores configuration on candidate failure, and rejects unsafe cleanup.
  SteamVR service startup waits off the editor/render thread.
- The user's failed run used true single-pass stereo. Prior pawn validation used
  sequential views and did not qualify this path. Mesh materialization incorrectly
  used desktop planner publication inside the scoped eye render. It now uses the
  eye's scoped generation. One bounded nested capture permits required BRDF producer
  work; discarded captures settle unsubmitted marker fences and publication pins.
- Initial `xr-runtime-choice` live build passed with zero warnings/errors. Monado
  diagnostics confirmed `TrueSinglePassStereo`, 244 submitted frames, and zero
  end-frame failures. The owned rig reported editor-scene membership. Stopping
  restored the exact original desktop node and retired the rig (lookup: not found).
- The same process then destroyed its Monado bootstrap instance and selected the
  explicit SteamVR manifest. SteamVR reached `SessionRunning` and submitted 60 frames
  with zero end-frame failures. This verifies runtime selection and submission,
  not headset comfort, tracking, or input quality.
- This first combined run exposed additional validation failures: desktop screenshot
  requests reported no accepted submission, and SteamVR stop remained in
  `SessionStopping`. Its log records a null reference in `CollectVisibleStereo`
  immediately after deferred swapchain retirement. These remain under investigation;
  the runtime chooser is not yet qualified end to end.
- Identified the stop failure: the persistent stereo visibility callback fell
  through to legacy VR after OpenXR became inactive, then dereferenced a missing
  legacy stereo viewport. The timer treats that exception as terminal and stops
  its collect loop. Guarding that optional viewport fixes the observed stop hang.
- The rebuilt run again passed with zero warnings/errors. Monado submitted 80
  single-pass frames and SteamVR 129, with zero end-frame failures in each sampled
  summary. Both stops restored the same original desktop pawn and removed their
  temporary editor-scene rig. SteamVR stop completed through deferred retirement.
- Composited capture now recognizes accepted recovery submissions and their GPU
  timeline boundary. Viewed captures exposed a separate persistent renderer
  recreation issue: desktop presents an initialization clear while exact texture
  descriptors or imported upload generations retry. A later summary had 53 pending
  transitions and 51 active upload requests with zero bytes scheduled that frame.
  Correct possession alone does not qualify desktop visual restoration.
- Added exact descriptor diagnostics. A third run proves the `Normal` source is an
  ordinary non-framebuffer `XRTexture2D` whose Vulkan wrapper has adopted a two-layer
  physical group. Name-only planner lookup can collide with the stereo G-buffer's
  `Normal`. The repair must require the declared texture identity, preserving exact
  descriptor shape checks.
- The imported staging pool also permits starvation when all bounded entries are
  idle but smaller than a later requested chunk. Added replacement of one matching
  undersized idle entry, preserving active leases and the foreground reserve. This
  alone did not resolve the third run: all 56 prepared chunks completed, but further
  preparations repeatedly hit transfer admission. Retirement progress during rejected
  desktop frames is being investigated separately.
- Found the retirement gap: shared-memory deduplication clears a retired buffer's
  memory field, so exact staging-lease lookup missed completed leases. Retirement
  now resolves the authoritative allocation before matching the lease. A bounded
  completed-retirement drain also runs before texture preparation when a desktop
  frame is rejected; it does not wait for unrelated uploads or GPU work.
- The next live build passed with zero warnings/errors. In one process, Monado,
  SteamVR, then Monado again submitted 141, 288, and 195 sampled single-pass frames
  respectively, with zero end-frame failures. Every stop restored the same original
  desktop pawn and removed its temporary rig. A viewed screenshot after the first
  stop showed the scene again, and imported uploads resumed progressing.
- Repeated runtime replacement still produced a black desktop scene with working
  editor UI. Repositioning the camera did not restore it. Fresh diagnostics showed
  the advanced deformation output reuse status `Unsupported`, slot zero, authority
  `NativeConsumers`. The renderer continued presenting, but advanced scene passes
  rejected that deformation publication. This is a remaining renderer lifetime
  issue, distinct from possession, runtime shutdown, and texture admission.
- Traced `Unsupported / NativeConsumers` to an old deformation output buffer with
  no native snapshot in the replacement renderer. The process-wide preparation
  cache retained its old reuse authority and rejected every frame before the
  buffer could be recreated. Successful renderer retirement now replaces this
  device-bound preparation state; the next frame rebuilds it from the unchanged
  canonical world publication. Mixed-backend replacement is rejected before
  teardown until that shared owner can be scoped. Partial or unproven teardown
  never resets the cache or claims verified visual rollback.
- Reset validation exposed a separate ownership error: deformation cleanup
  destroyed the aggregate shader borrowed from the global engine shader cache.
  The next renderer retrieved that destroyed shader, failed program construction,
  and permanently rejected its output slot with `MissingProducerFence`. Cleanup
  now destroys only its owned program and releases its reference to the borrowed
  shader. The existing fence failure checks remain unchanged.
- With shader ownership corrected, the rebuilt process submitted 122 Monado,
  60 SteamVR, and 110 Monado return frames with zero end-frame failures. All three
  stops restored the original desktop pawn. Deformation diagnostics returned
  `Ready / NativeConsumers` with published GPU resources and executed dispatch.
  Screenshots still exposed a canonical texture source mismatch: the publication
  retained 4096-square, 13-mip metadata after the source changed to a 64-square,
  one-mip streamed representation. The texture queue subsequently became fully
  idle, ruling out an ordinary pending transition as the reason this persisted.
- Precise preflight diagnostics then proved the publication stall: shadow
  additions=4, tombstones=4, live count=4, physical high-water=128, capacity=128.
  Preflight required free space at the end of the table although the actual
  contiguous allocator can reuse reclaimed gaps. The frozen canonical publication
  consequently retained old texture metadata. Preflight must use the same free-run
  occupancy rules as allocation; pinned rows remain occupied until reclaimed.
- The subsequent build passed with zero warnings/errors. Monado submitted 287
  single-pass frames and SteamVR 337, both with zero end-frame failures. Both
  stops restored the exact original desktop pawn, and viewed captures showed the
  imported avatar/environment after returning from SteamVR. This qualifies those
  sampled transitions, not headset tracking or input.
- A later shadow preflight rejection revealed unnecessary record churn: changing
  per-frame matrices/timestamps replaced entire shadow groups. Same-layout,
  same-binding groups now update payloads under stable handles. Topology changes
  still allocate new contiguous groups and retire old rows under publication pins.
- Retention diagnostics showed inactive eye viewports holding frame packages after
  session stop. After successful GPU idle and swapchain retirement, teardown now
  cancels eye frame packages. GPU leases still retire through their completion
  owners; cleanup does not force-release them. Repeated runtime validation is
  required to establish that old publication pins no longer stall later updates.
- During overlapping import/replacement, constructor-time API wrapper publication
  could race backend retirement and terminate visibility collection. Optional
  publication now uses the renderer retirement lock and skips a retired renderer;
  direct backend requests still reject retirement.

Import responsiveness is tracked separately in
[Avatar scene publication stalls](../asset-import/avatar-scene-publication-stalls-2026-09-24.md).

## Stereo avatar shading investigation

The user reports an avatar that becomes black or shows background through its
meshes after moving in SteamVR. The editor displays its desktop camera, not a VR
mirror, so the desktop image does not validate either eye's shading.

Process 50428 reproduced a black avatar silhouette in both exported SteamVR eye
preview textures while the environment remained visible. This places the error
before headset composition. A later Monado left-eye capture also showed black and
background-colored avatar regions after several runtime transitions. Runtime name
alone therefore does not explain the current reproduction. This process predates
the final capture-publication lease cleanup; a clean rebuilt run is required.

Both runtimes select the OpenXR-owned RVC pipeline through the same factory and
feature synchronization. Single-pass stereo uses its layered command family;
the independent per-eye viewport instances are inactive for that output. MCP's
`vr_eye=stereo` previously resolved only the legacy OpenVR viewport. It now resolves
the active OpenXR-owned stereo viewport for pipeline resource inspection.

Next evidence: inspect stereo albedo, normals, depth, lighting, and final output
at the silhouette; compare a clean Monado run with SteamVR and repeat after a
runtime switch. Frame submission counts alone do not validate eye image quality.

A RenderDoc capture from process 69328 established a good Monado baseline:
stereo albedo, normals, and depth contain the avatar, and the deferred lighting
result has blue hair and shaded clothing. That capture does not explain the
earlier failing SteamVR output. The physical headset later pointed away from the
avatar, so environment-only eye captures are not shading validation.

Stereo intermediate readback also required a diagnostic correction. Strict
single-pass rendering records its successful submission in a nested Mirror
planner context. Readback previously inspected the stale outer context. It now
requires the exact submitted context receipt in the nested table, retaining all
pipeline, viewport, registry, generation, signature, extent, and allocator
liveness checks. This changes inspection access, not the shading pipeline.

## 2026-09-25 Handoff

Remaining work and acceptance criteria are consolidated in
[Editor OpenXR toggle, rendering, and import responsiveness](../../todo/rendering/vr/editor-openxr-toggle-and-rendering-todo.md).
The user requested wrap-up; the owned `xr-runtime-choice` session was stopped.

Subsequent stereo captures showed valid aligned albedo, normals, and depth.
Raw-albedo debug output matched the avatar silhouette, while normal lighting
remained very dark with emissive details visible. Disabling the skybox did not
restore illumination. Sampled AO was finite and nonzero. No matrix or probe
binding defect has been proven; constants and resources from the current failing
deferred combine draw remain the next shading evidence.

The desktop freeze was reproduced while Monado's engine-submitted frame count
continued advancing. Desktop work retried required texture upload readiness
without recording, submitting, or presenting a new frame. Live streaming state
showed 40 pending transitions, 72 queued preparation packages, no active prep or
pending transfers, and more than 8,000 staging admission deferrals. The pending
transition flag remained true, contradicting a simple canceled-upload diagnosis.
Nonzero loop FPS did not imply successful desktop presentation.

A narrow staging selector fix now filters foreground-reserve eligibility before
best-fit selection; previously an ineligible background buffer could mask an
available reserved buffer. It passed a diff check but has not been built or
runtime-validated. Temporary expanded upload diagnostics were removed.

Separately, the BRDF required-producer change publishes its framebuffer after
deferred draw materialization and uses the validated frozen writer/cohort context
for publication and completion. Source review passed; build and live validation
are still pending. Neither source fix establishes that the headset shading defect
is resolved.
