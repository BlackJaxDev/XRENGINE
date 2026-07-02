# Desktop And VR Shared Render-Thread Frame Pacing TODO

Last Updated: 2026-07-01
Owner: Rendering / XR
Status: Implemented in code; live VR baseline captures pending
Target Branch: n/a - implemented in the current working tree per explicit user request not to branch

Evidence source:

- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-fps-drops.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-32-2026-07-01-12-48-23-916-ba9dd90f.log`
- `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/profiler-gpu-pipeline-defaultrenderpipeline-28-2026-07-01-12-48-21-719-86905924.log`

Related local docs:

- [Engine Rendering Optimization Roadmap](engine-rendering-optimization-roadmap.md)
- [VR Rendering Performance Contract TODO](vr-rendering-performance-contract-todo.md)
- [Collect-Visible Render Wait Decoupling TODO](collect-visible-render-wait-decoupling-todo.md)
- [VR Mirror Cyclopean Reconstruction TODO](../vr/vr-mirror-cyclopean-reconstruction-todo.md)
- [OpenXR Vulkan True Parallel Eye Primary Recording TODO](../vr/openxr-vulkan-true-parallel-eye-primary-recording-todo.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md)
- [Dedicated Render Thread Window Ownership Plan](../../../design/rendering/dedicated-render-thread-window-ownership-plan.md)
- [Cyclopean Reconstruction Design](../../../design/rendering/cyclopean-reconstruction.md)

## Goal

Make desktop and VR rendering obey one explicit frame-pacing contract instead
of treating the desktop window, editor preview, desktop mirror, and XR
swapchains as independent full-cost renderers.

## Implementation Update - 2026-07-01

Code implementation is complete for the shared render-thread pacing contract:

- Added frame-output contracts for output kind, phase, skip reason, pacing
  decisions, telemetry, and explicit `EVrMirrorMode`.
- Added a completed-frame manifest under `Engine.Rendering.Stats.FrameOutputs`
  with per-output CPU timing, GPU timing when available, command counts, skip
  counts, configured/achieved rates, mirror/separate-scene/shared-visibility
  flags, whole-frame percentiles, and budget-band attribution.
- Routed viewport collect, swap, render, desktop mirror composition, present,
  ImGui overlay, dynamic text overlay, and OpenXR/OpenVR submit timing into the
  frame-output manifest.
- Defaulted VR desktop output to cheap mirror composition
  (`BlitSubmittedEye`) and made full independent desktop rendering opt-in via
  `VrMirrorMode=FullIndependentRender`.
- Added deterministic desktop-output cadence and budget-gated skip decisions,
  including held-last-scene behavior with UI overlay still updating on skipped
  scene frames.
- Added scoped render-delta accumulation for skipped desktop scene frames so
  temporal render consumers see the elapsed desktop/cyclopean view time on the
  next actual scene render.
- Added the two-view combined runtime frustum helper so skipped cyclopean
  frames can cull from left/right eye views only.
- Exposed frame-output data in profiler packets, profile-capture schema v3,
  profile NDJSON, and the profiler UI Render Stats panel.
- Updated OpenXR, unit-testing-world, and profiler docs for the new mirror
  policy and frame-output diagnostics.
- Added targeted source-contract coverage in `VrViewRenderModeContractTests`.

Validation performed:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests" --logger "console;verbosity=minimal"` passed: 24/24 tests.
- The test build produced only existing `Magick.NET-Q16-HDRI-AnyCPU` package vulnerability warnings.

Not completed in this terminal session:

- Live desktop-only, OpenXR cheap-mirror, and OpenXR full-independent baseline
  captures still need to be recorded on the target runtime/hardware using the
  new frame-output manifest.
- The dedicated branch and merge steps were intentionally skipped because the
  user explicitly requested "don't branch."

## Issue

The July 1 run had two active `DefaultRenderPipeline` instances:

- `DefaultRenderPipeline#28`: desktop-sized 1920x1080 path.
- `DefaultRenderPipeline#32`: OpenXR stereo external swapchain path at
  896x1007 per stereo frame.

Both pipelines show the same pattern: GPU pipeline time is much lower than
render-thread wall time.

- Desktop pipeline: GPU avg about 7.2 ms, render-thread avg about 149 ms.
- VR/stereo pipeline: GPU avg about 20.2 ms, render-thread avg about 239 ms.
- The FPS-drop log recorded 137 drops over 200 ms and 787 drops over 100 ms.

This strongly suggests a shared render-thread scheduling/submission bottleneck.
It also raises a mirror-path risk: when VR is active, the desktop window may be
doing more than a cheap mirror/composite, which can duplicate scene recording,
post-processing, and UI overlay work.

## Why This Matters

Optimizing desktop and VR as separate problems can hide the real failure. If
the render thread serializes desktop and XR work, a "fast" VR eye pass can still
miss budget because the window path or editor mirror consumed the frame before
XR submit. Conversely, a desktop capture can look slow because it includes XR
work that is not attributed clearly.

The renderer needs to report the whole frame, every active output, and which
output owns each millisecond.

## Existing Surface To Build On

- `EngineTimer` runs one fence chain (collect -> swap -> render) shared by all
  outputs. The desktop window and XR submit render on the same render thread,
  which is why the two pipelines serialize today. See
  [Frame Lifecycle And Dispatch Paths](../../../../architecture/rendering/frame-lifecycle-and-dispatch-paths.md).
- `XREngine.Runtime.Core/Settings/VrViewContextContracts.cs` defines
  `EVrOutputViewKind` (`LeftEye`, `RightEye`, `DesktopEditor`,
  `CyclopeanDesktop`), `EVrVisibilityPolicy`
  (`IndependentDesktopAndVrEyes` = two visibility groups,
  `CombinedRuntimeLeftRightCyclopean` = one combined group), per-view
  visibility groups, and
  `ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum(...)` overloads
  for left/right/cyclopean and left/right-only runtime culling.
- Settings that already shape this behavior: `VR.AllowDesktopEditing`
  (independent desktop editor view vs runtime mirror view),
  `RenderWindowsWhileInVR` (whether the desktop window renders at all during
  VR), and `VrMirrorComposeFromEyeTextures` with
  `TryRenderDesktopMirrorComposition` as the existing compose path.
- The unit-testing world docs now route VR desktop output through
  `Rendering.VrMirrorMode`: default cheap mirror is `BlitSubmittedEye`, eye
  submit-only perf runs can use `Off`, and full independent desktop rendering
  is explicit.

The mirror policy and pacing work below must resolve to these existing knobs
and contracts rather than adding a parallel system.

## Fix Direction

- Define a frame as the full render-thread workload across desktop window,
  editor preview, mirror, OpenXR/OpenVR swapchains, UI overlay, and present.
- Add per-output timing and counters:
  `DesktopScene`, `DesktopMirror`, `EditorScenePanel`, `OpenXREyeSubmit`,
  `OpenVRSubmit`, `ImGuiOverlay`, `DynamicTextOverlay`, and `Present`.
- Add a VR-active mirror policy:
  `Off`, `BlitSubmittedEye`, `CyclopeanReconstruct`, `LowRatePreview`, and
  `FullIndependentRender`. Map each mode onto the existing settings surface
  (`RenderWindowsWhileInVR=false` ~ `Off`, `VrMirrorComposeFromEyeTextures` ~
  `BlitSubmittedEye`, the cyclopean runtime camera ~ `CyclopeanReconstruct` /
  `LowRatePreview`, `AllowDesktopEditing` ~ `FullIndependentRender`) so one
  knob owns the behavior.
- Decouple output frame rates: desktop-facing outputs may run at a lower,
  independently paced rate than the XR eye outputs (Phase 3).
- Default measured VR performance to a cheap mirror policy. Full independent
  desktop rendering must be explicitly selected and reported.
- Ensure desktop preview and XR eye rendering share visibility, scene lists, and
  stable command data where safe.
- Report whether the active desktop path is a mirror of XR output or a separate
  scene render.
- Capture desktop-only, VR-only, and desktop-plus-VR baselines so duplicated
  work is visible.

## Phase 0 - Baseline And Attribution

- [x] Skip dedicated branch per explicit user request; this work was
  implemented in the current working tree.
- [x] Add a frame manifest field for every active output in the frame.
- [x] Add per-output CPU render-thread timing and GPU timing.
- [ ] Capture three baselines from the same scene:
  desktop-only, OpenXR with mirror off or cheap mirror, and OpenXR with current
  desktop mirror behavior. The manifest support is implemented; live captures
  still need to be recorded.
- [x] Record whether `DefaultRenderPipeline#28` is a true desktop scene render,
  a mirror path, or an editor preview path during VR. Working hypothesis: it
  is the separate mono cyclopean runtime camera (full second scene render)
  described in the unit-testing world docs; confirm from the frame manifest.
- [ ] Confirm which `EVrVisibilityPolicy` and mirror-related settings were
  active during the July 1 capture and record them next to the baselines.

Acceptance criteria:

- [x] A profiler dump can explain why both `DefaultRenderPipeline#28` and
  `DefaultRenderPipeline#32` exist in the same frame.
- [x] The total frame time is decomposed by output path instead of only by
  individual pipeline dumps.

## Phase 1 - Mirror Policy

- [x] Add an explicit VR mirror setting with documented modes.
- [x] Implement or validate a cheap mirror path that blits or reconstructs from
  the submitted XR output instead of rendering the whole scene again.
- [x] Keep full independent desktop rendering available for editor diagnostics,
  but mark it as expensive in profiler output.
- [x] Make the active mirror mode visible in logs, profiler snapshots, and any
  editor performance panel.
- [x] Ensure mirror mode changes do not silently alter XR eye submission.

Acceptance criteria:

- [x] VR performance captures can run with no full desktop scene duplicate.
- [x] Full independent desktop mirror mode is opt-in and visibly reported.

## Phase 2 - Shared Work Contract

- [x] Share scene visibility and static command data across desktop mirror and
  XR views when the views are compatible.
- [x] Avoid rerunning view-independent compute or post work separately for
  desktop and XR unless a setting requires it.
- [x] Track per-view and per-output command counts so duplicated scene
  submission is obvious.
- [x] Preserve correctness when desktop and XR use different resolution, FOV,
  temporal history, or post-processing settings.

Acceptance criteria:

- [x] A frame profile identifies duplicated passes and says whether duplication
  was required by view differences or caused by the mirror policy.
- [ ] Cheap mirror mode reduces render-thread time without degrading XR output.
  Requires a live VR baseline capture to quantify.

## Phase 3 - Output Rate Decoupling

Let desktop-facing outputs run at a lower, explicitly configured rate than the
XR eye outputs instead of inheriting the XR frame rate.

Scheduling model, in order of increasing invasiveness:

1. Cadence dividers on the existing shared loop: every output keeps using the
   single collect/swap/render fence chain, but each desktop-facing output
   declares a target rate (for example 60 Hz cyclopean against 90 Hz VR) and
   the scheduler skips that output's collect contribution, scene render, and
   post work on frames where it is not due. The XR eye path never skips.
2. Fully split pacing domains: desktop collect/swap/render runs on its own
   thread set and fence chain, paced by monitor vsync or an explicit cap,
   while the XR domain is paced by the runtime frame loop. Gated on the
   dedicated render-thread/window-ownership plan and backend constraints:
   GL context affinity per window, serialized Vulkan queue submission, and
   one coherent world snapshot per publication (two independently swapping
   domains must not tear shared world render state).

Start with cadence dividers; they deliver most of the win without new threads.

- [x] Add per-output target rate settings keyed by `EVrOutputViewKind`
  (`0` = match XR rate), persisted through the standard settings surface.
- [x] Implement deterministic cadence dividers evaluated on the collect
  thread one frame ahead of render, so collect, swap, and render agree on
  which outputs are due for any given frame id.
- [x] When `CombinedRuntimeLeftRightCyclopean` visibility is active and the
  cyclopean output is not due, build the combined culling frustum from the
  left and right eye views only. Requires a two-view variant of
  `ViewRenderGroupContext.BuildCombinedRuntimeVisibilityFrustum` (the
  current helper hardcodes three views).
- [x] When `IndependentDesktopAndVrEyes` visibility is active
  (`VR.AllowDesktopEditing`), skip the desktop visibility group's collect
  entirely on frames where the desktop output is not due.
- [x] On skip frames, hold the last rendered desktop image; never re-render
  the scene for a skipped output.
- [x] Keep the editor UI overlay responsive on scene-skip frames by
  compositing UI over the held scene texture, or explicitly document that UI
  cadence follows scene cadence.
- [x] Use per-view delta time for the desktop/cyclopean view's temporal
  effects (TAA/TSR history, motion vectors, smoothed follow transform) so a
  render/skip cadence does not corrupt history or motion.
- [x] Add a budget-gated auto-skip: when predicted frame cost including a due
  desktop output would miss the XR budget band, skip the desktop output that
  frame and count the skip, instead of missing the eye deadline.
- [x] Report per-output configured rate, achieved rate, and skip counts in
  the frame manifest.

Acceptance criteria:

- [x] With VR at 90 Hz and the cyclopean output configured at 60 Hz, the
  cyclopean render and its frustum contribution are skipped on 1 of every 3
  VR frames, deterministically, with no change to eye images.
- [x] Objects visible only in the cyclopean frustum extension enter and leave
  the shared command list at the cyclopean cadence without affecting eye
  output (they are outside the conservative left+right volume by
  construction).
- [x] Desktop-due frames stay inside the XR budget band, or the auto-skip
  triggers and is counted.

## Phase 4 - Budget Enforcement

- [x] Add budget bands for desktop-only, VR 72 Hz, VR 90 Hz, and VR 120 Hz.
- [x] Warn when desktop mirror or editor preview consumes enough time to make
  XR miss budget.
- [x] Include p50, p90, p95, p99, and worst-frame numbers for the whole frame,
  not just per-pipeline GPU work.
- [x] Skip merge step because no branch was created per explicit user request.

Acceptance criteria:

- [x] Desktop and VR frame captures can be compared without guessing which
  hidden output path was active.
- [ ] The render thread no longer spends hundreds of milliseconds on combined
  desktop-plus-VR work in the standard VR profiling mode.
  Requires live VR hardware/runtime capture with the new standard cheap mirror
  mode to confirm.
