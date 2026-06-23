# Window Interactive Resize Strategies TODO

Last Updated: 2026-06-22
Owner: Rendering
Status: Implemented. Targeted editor build passes; manual resize matrix still needs runtime exercise.
Target Branch: none; user requested no branch for this work.

## Implementation Notes

- Added `EInteractiveWindowResizeStrategy`, environment parsing via
  `XRE_INTERACTIVE_RESIZE_STRATEGY`, per-window startup settings, editor
  preference/override plumbing, and the runtime rendering host bridge.
- `XRWindow` now owns an `IInteractiveResizeStrategy` instance, logs the
  resolved strategy and actual windowing backend, queues framebuffer resize
  work through the existing pending-resize path, and uses a guarded
  `RenderInteractiveResizeFrame(...)` helper for callback-driven renders.
- Implemented `Default`, `GlfwRefreshCallback`, `GlfwResizeCallbackRender`,
  `SdlBackend`, `Win32ModalLoopTimer`, and `EngineBorderlessResize` strategy
  classes. SDL is selected before `Window.Create`; runtime switches to or from
  SDL log that the actual backend changes only after window recreation.
- Added counters for callback, interactive render, suppressed render, queued
  resize, and last resize reason; the editor Engine State window lists the
  selected strategy, backend, and resize diagnostics for each window.
- The Win32 modal-loop timer defaults to 16 ms and can be tuned with
  `XRE_WIN32_INTERACTIVE_RESIZE_TIMER_MS` (1-250 ms).
- The Win32 modal-loop strategy now handles live drag as a presentation-only
  resize: `WM_SIZE`/`WM_SIZING` update the effective framebuffer size,
  viewport output region, camera aspect, and screen-space UI dimensions, while
  full internal-resolution/resource invalidation is deferred until
  `WM_EXITSIZEMOVE`.
- Live drag renders are rate-limited to 60 Hz and use actual client dimensions
  from `WM_SIZE` or `GetClientRect`, avoiding the earlier proposed-outer-rect
  conversion that could over-correct ImGui dimensions during border drags.
- Vulkan live drag now bypasses swapchain resize debounce and recreates the
  swapchain immediately when the effective client size diverges from the
  current swapchain extent. While dragging, Vulkan also allows the previous
  scene resource generation to present scaled to the live viewport size; the
  full internal-resolution/resource regeneration still happens on mouse-up.
- Vulkan crash follow-up from the 2026-06-22 13:04 run
  (`Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-22_13-04-18_pid36440`):
  validation reported `vkCmdBeginRendering` with a 1920x1080 render area against
  a 1914x1077 color image view during live drag, then the renderer entered
  device-loss recovery and permanently disabled Vulkan after logical-device
  recreation failed.
- Vulkan framebuffer attachment sources now report their resolved physical
  image/view extent. `VkFrameBuffer` builds dynamic-rendering attachment draw
  extents from that Vulkan-resolved size instead of only the engine-side logical
  texture size, preventing a stale internal-resolution render area from
  exceeding the live swapchain/image-view extent during interactive resize.
- Vulkan crash follow-up from the 2026-06-22 13:19 run
  (`Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-22_13-19-19_pid30788`):
  the previous `vkCmdBeginRendering` extent error was gone, but `vkCmdBlitImage`
  still used 1920-wide blit regions against live swapchain-sized images of
  1915 and then 1654 pixels wide. `BlitImageInfo` now carries the resolved live
  source/destination extent, and command-buffer blit emission clamps source and
  destination offsets to those extents before calling Vulkan.
- `XRWindow` tracks an effective framebuffer size from the latest queued or
  applied resize. Full-window ImGui, viewport-panel bounds, scene-panel restore
  sizing, Vulkan live-surface checks, and render-to-window fallback regions use
  that value so editor UI dimensions update with the in-drag framebuffer rather
  than Silk.NET's delayed cached `FramebufferSize`.
- The Win32 modal-loop hook now guards against duplicate/stale WndProc chains,
  restores only when its own hook is still installed, cancels interactive resize
  on close/destroy messages, and suppresses resize renders after window closing
  starts. This specifically protects the close-after-crash path that showed
  repeated `WindowProc` frames in the stack trace.
- Immediate ImGui layout flushing on close now verifies a current ImGui context
  on the render thread before calling `ImGui.SaveIniSettingsToMemory`; if that
  context is gone, it leaves the layout dirty for the normal async path instead
  of risking a native access violation during renderer teardown.
- The editor/project preference default is now `Win32ModalLoopTimer` so Windows
  editor/runtime windows keep repainting during native border drags by default.
  Select `Default` explicitly when a baseline/no-hook comparison is needed.
- Validation run: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
  completed with 0 warnings and 0 errors on 2026-06-22.
- Follow-up validation after live `WM_SIZING` and effective-framebuffer fixes:
  the normal editor output build was blocked by a running `XREngine.Editor`
  debug session holding output DLLs, then
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:OutDir=Build\_AgentValidation\20260622-124636-interactive-resize-build\temp-build\`
  completed with 0 warnings and 0 errors.
- Follow-up validation after presentation-only live resize split:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:OutDir=Build\_AgentValidation\20260622-125437-interactive-resize-presentation-build\temp-build\`
  completed with 0 warnings and 0 errors.
- Follow-up validation after Vulkan interactive resize fast path:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:OutDir=Build\_AgentValidation\20260622-130125-vulkan-interactive-resize-build\temp-build\`
  completed with 0 warnings and 0 errors.
- Follow-up validation after Vulkan dynamic-rendering extent and close-hook
  fixes:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:OutDir=Build\_AgentValidation\20260622-131855-vulkan-resize-crash-fix\temp-build\`
  completed with 0 warnings and 0 errors.
- Follow-up validation after Vulkan blit-region live-extent clamping:
  `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -p:OutDir=Build\_AgentValidation\20260622-133105-vulkan-blit-resize-fix\temp-build\`
  completed with 0 warnings and 0 errors.

## Goal

Fix the visible freeze/stretch that happens while a user drags a native Silk.NET
window resize border, without baking one platform-specific workaround directly
into `XRWindow`.

The implementation should expose one explicit setting that can toggle between
multiple strategies at runtime/startup, so OpenGL, Vulkan, editor, and game
windows can be tested against the same policy surface.

## Problem Summary

On the current GLFW-backed Silk.NET window path, Windows can enter a native
move/size modal loop while the user drags the window border. During that loop,
normal event processing and render callbacks may not run until the drag ends,
so the compositor stretches the last presented frame.

This is not specific to the engine renderer. GLFW documents that move, resize,
or menu operations can block event processing on some platforms, and Microsoft
documents the Win32 move/size modal loop through `WM_ENTERSIZEMOVE` and
`WM_EXITSIZEMOVE`.

## Research Sources

- GLFW event processing notes:
  https://www.glfw.org/docs/3.0/group__window.html#gab9c0534709fda03ec8959201da3a9a18
- GLFW window resize, framebuffer size, and refresh callbacks:
  https://www.glfw.org/docs/3.3/window_guide.html#window_size
- Silk.NET upstream issue for render-on-drag/resize:
  https://github.com/dotnet/Silk.NET/issues/241
- Silk.NET discussion noting a render-thread/SDL backend approach:
  https://github.com/dotnet/Silk.NET/discussions/2256
- Win32 move/size modal loop entry:
  https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-entersizemove
- Win32 move/size modal loop exit:
  https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-exitsizemove
- Win32 sizing message:
  https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-sizing
- Win32 timers for modal-loop repaint ticks:
  https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-settimer

## Current Local Facts

- `XRWindow` currently calls `Silk.NET.Windowing.Window.PrioritizeGlfw()`.
- Both `Silk.NET.Windowing.Glfw` and `Silk.NET.Windowing.Sdl` packages are
  already referenced at version `2.23.0`.
- The common resize path is already backend-neutral at the engine level:
  `FramebufferResizeCallback(...) -> ProcessPendingFramebufferResize() ->
  ApplyFramebufferResize(...) -> Renderer.FrameBufferInvalidated()`.
- OpenGL needs the window/context current before touching framebuffer-bound
  resources during resize.
- Vulkan should keep swapchain/resource invalidation synchronized with the
  render frame path and avoid reentrant swapchain recreation from native window
  callbacks.

## Proposed Setting

Add a startup/runtime setting such as:

```csharp
public enum EInteractiveWindowResizeStrategy
{
    Default,
    GlfwRefreshCallback,
    GlfwResizeCallbackRender,
    SdlBackend,
    Win32ModalLoopTimer,
    EngineBorderlessResize,
}
```

Suggested locations:

- Per-window persisted startup value:
  `GameWindowStartupSettings.InteractiveResizeStrategy`.
- Optional editor default/override:
  `EditorPreferences.InteractiveResizeStrategy` and
  `EditorPreferencesOverrides`.
- Runtime bridge:
  `IRuntimeRenderingHostServices.InteractiveResizeStrategy`.
- Diagnostic override:
  `XRE_INTERACTIVE_RESIZE_STRATEGY=default|glfw-refresh|glfw-resize|sdl|win32-timer|borderless`.

Resolution order should be:

1. Environment override.
2. Per-window startup setting.
3. Editor/project preference when available.
4. `Default`.

## Strategy A - Default

Keep current Silk.NET/GLFW behavior. This remains useful as a baseline and
fallback.

Implementation tasks:

- [x] Add enum value and setting plumbing.
- [x] Log the resolved strategy once per window.
- [x] Ensure `Default` does not install callbacks, native hooks, private
  reflection hooks, or backend changes.

Acceptance criteria:

- [ ] Current resize behavior is unchanged when the setting is `Default`.
- [ ] OpenGL and Vulkan still initialize and render normally.

## Strategy B - GLFW Refresh Callback

Use the official GLFW window refresh callback to request a lightweight render
when the window content needs repainting during resize/move exposure.

This should avoid private Silk.NET `_onFrame` reflection. If Silk.NET does not
surface the refresh event directly, use the public native GLFW binding and the
native window handle.

Implementation tasks:

- [x] Add a small `IInteractiveResizeStrategy` abstraction owned by `XRWindow`.
- [x] Resolve the native GLFW window handle through public Silk.NET native
  window surfaces only.
- [x] Register `glfwSetWindowRefreshCallback` for GLFW windows.
- [x] In the callback, request a render through a guarded
  `XRWindow.RenderInteractiveResizeFrame(...)` helper.
- [x] Avoid rendering if the normal render callback is active or the renderer is
  disposed.
- [x] Restore the previous callback on `UnlinkWindow()` / dispose.

Risks:

- The refresh callback may only fire when the window is damaged or resized; it
  may not produce steady frames if the user holds the border without moving.
- Callback ownership can conflict with Silk.NET internals if the backend already
  installs its own callback.

Acceptance criteria:

- [ ] No private reflection into Silk.NET types.
- [ ] OpenGL renders a refreshed frame during active drag when the callback
  fires.
- [ ] Vulkan does not recreate swapchain resources reentrantly from the native
  callback.
- [ ] Callback cleanup survives window close and renderer fallback from Vulkan
  to OpenGL.

## Strategy C - GLFW Resize Callback Render

Render from the public Silk.NET `Resize` and `FramebufferResize` callbacks when
those callbacks are delivered during a native drag.

Implementation tasks:

- [x] Subscribe to `IWindow.Resize` in addition to `FramebufferResize` only for
  this strategy.
- [x] Queue framebuffer resize through the existing pending-resize fields.
- [x] Add a guarded interactive render helper that calls the same resize and
  render path as a normal frame.
- [x] Coalesce duplicate resize sizes to avoid redundant resource invalidation.
- [x] Rate-limit callback rendering to a target such as 30 or 60 Hz.

Risks:

- The user's observed behavior says these callbacks may not arrive until drag
  release on the current platform/backend, so this strategy may be insufficient
  alone.

Acceptance criteria:

- [ ] If resize callbacks arrive during drag, the window updates without waiting
  for mouse release.
- [ ] If callbacks do not arrive during drag, the strategy fails visibly in
  diagnostics and does not destabilize rendering.

## Strategy D - SDL Backend

Allow Silk.NET to create the window through the SDL backend instead of forcing
GLFW. This is attractive because the repo already references
`Silk.NET.Windowing.Sdl`, and upstream discussion reports that SDL can avoid
the GLFW drag hang in a render-thread setup.

Implementation tasks:

- [x] Add a backend selection helper before `Window.Create(...)`.
- [x] For this resize strategy, prioritize SDL instead of GLFW.
- [ ] Verify OpenGL context creation, input, clipboard, cursor capture, DPI, and
  transparent framebuffer behavior through SDL.
- [ ] Verify Vulkan surface creation through SDL.
- [ ] Keep fallback behavior clear: if SDL cannot create the requested Vulkan
  window, fall back only according to the existing Vulkan-to-OpenGL policy.
- [x] Add startup diagnostics showing the actual windowing backend and graphics
  API.

Risks:

- SDL behavior may differ for raw input, multi-window ImGui viewport support,
  high-DPI framebuffer sizing, and native title-bar/custom-title-bar behavior.
- Vulkan surface extension negotiation must be checked on every supported OS.

Acceptance criteria:

- [ ] `SdlBackend` works for OpenGL.
- [ ] `SdlBackend` works for Vulkan or fails with a clear unsupported message.
- [ ] Editor scene panel and full-window rendering both survive resize.
- [ ] No GLFW-specific code path is used when SDL is selected.

## Strategy E - Win32 Modal Loop Timer

Install a Windows-only native message strategy that starts a timer on
`WM_ENTERSIZEMOVE`, renders on `WM_TIMER` while the native modal loop is active,
updates pending size from `WM_SIZING` or client rect, and stops on
`WM_EXITSIZEMOVE`.

This is the explicit, toggle-gated version of the native-hack family. It should
be isolated in a Windows strategy class rather than embedded in `XRWindow`.

Implementation tasks:

- [x] Add a Windows-only strategy class, compiled or activated only on Windows.
- [x] Resolve the `HWND` through public native window data where possible.
- [x] Subclass the window proc with a lifetime-owned delegate.
- [x] On `WM_ENTERSIZEMOVE`, start a timer at a configurable interval such as
  16 ms or 33 ms.
- [x] On `WM_TIMER`, request an interactive render if the renderer is ready.
- [x] On `WM_SIZING` and `WM_WINDOWPOSCHANGED`, queue the latest client size.
- [x] On `WM_EXITSIZEMOVE`, stop the timer, queue final resize, and render once.
- [x] Restore the original window proc during unlink/dispose.
- [x] Add diagnostics for hook install, hook restore, timer start/stop, and
  suppressed reentrant renders.

Risks:

- Incorrect subclass lifetime can crash the process.
- Rendering from a native callback can reenter engine state unless carefully
  guarded.
- Windows-only behavior can mask bugs in the backend-neutral resize path.

Acceptance criteria:

- [ ] Strategy is opt-in only.
- [ ] No native hook is installed on non-Windows platforms.
- [ ] Hook restore is verified when windows close normally and during exception
  cleanup.
- [ ] OpenGL makes the context current before rendering.
- [ ] Vulkan only queues resize/invalidation work that is safe for the current
  frame boundary.

## Strategy F - Engine Borderless Resize

Avoid the native move/size modal loop by using an engine-owned borderless window
and implementing resize grips in the title-bar/editor UI layer.

Implementation tasks:

- [x] Extend the existing `UseNativeTitleBar=false` path with hit-test regions
  for resize edges and corners.
- [x] Use platform APIs or Silk.NET window position/size setters to apply
  resize during normal engine frames.
- [x] Keep normal render/update callbacks active while dragging.
- [x] Add cursor feedback for resize edges.
- [x] Respect minimum window size; no per-window aspect constraint setting exists
  yet for this path.

Risks:

- Custom chrome is a larger UX surface than just resize rendering.
- Platform snapping, accessibility, shadows, and system menu behavior need
  deliberate follow-up.

Acceptance criteria:

- [ ] No OS modal resize loop is entered for engine-owned resize grips.
- [ ] OpenGL and Vulkan use the same regular render-frame resize path.
- [ ] Native title-bar mode remains available and unaffected.

## Shared Implementation Tasks

- [x] Add `EInteractiveWindowResizeStrategy`.
- [x] Add strategy resolution and diagnostics.
- [x] Add `IInteractiveResizeStrategy` with `Install(XRWindow)` and
  `Uninstall()` lifetime.
- [x] Add one guarded `XRWindow.RenderInteractiveResizeFrame(string reason)`
  helper shared by callback-based strategies.
- [x] Prevent nested renders with an interlocked guard.
- [x] Keep `ProcessPendingFramebufferResize()` as the only code that mutates
  viewport sizes and calls `Renderer.FrameBufferInvalidated()`.
- [x] Keep scene-panel FBO resizing debounced through
  `XRWindowScenePanelAdapter`; do not destroy scene-panel FBOs on every
  drag-frame unless the selected strategy requires it.
- [x] Add per-strategy diagnostics counters:
  callback count, interactive render count, suppressed render count, resize
  queue count, and last resize reason.
- [x] Document the selected strategy in startup logs and diagnostics UI.

## Validation Matrix

## Vulkan Resize Follow-Up Notes - 2026-06-22

- Latest user run inspected:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-22_13-33-17_pid47684`.
- The previous `vkCmdBlitImage` validation/device-loss crash did not recur in
  the latest logs.
- The `System.InvalidOperationException` spam in that run was traced to VMA
  image allocation fallback: device-local image allocation failed, then the
  renderer tried `HostVisibleBit | HostCoherentBit` for an image allocation and
  VMA returned `ErrorFeatureNotPresent`.
- Vulkan also logged explicit resize frame drops:
  `Skipping frame while resize resources settle`, with active resource
  generations still at the previous size while the viewport had moved to the
  new window size.
- Follow-up patch changed Vulkan live resize behavior so resource-generation
  size mismatches no longer block presentation frames. The renderer keeps using
  the active generation while the pending generation settles.
- Vulkan ImGui sizing now uses `XRWindow.EffectiveFramebufferSize` instead of
  forcing `io.DisplaySize` back to the current swapchain extent.
- Generic ImGui rendering now permits multiple ImGui frames per engine
  timestamp only while the owning window is in interactive resize, preventing
  stale editor draw data during the Win32 modal loop.
- VMA `ErrorFeatureNotPresent` is treated as a non-viable try-allocation result
  instead of throwing `InvalidOperationException`; image allocation no longer
  attempts the invalid host-visible fallback.
- Validation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
  redirected to
  `Build/_AgentValidation/20260622-134526-vulkan-live-resize-imgui-frame-skip/temp-build/`
  completed with 0 warnings and 0 errors.
- Later user run inspected:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-22_13-50-43_pid2516`.
  Vulkan was following swapchain size during drag, but the resource planner was
  still refreshing display extents from the live viewport on every intermediate
  size. Failed image allocations repeated while old physical images and
  descriptor pools piled up in retirement queues, which explains the
  over-time lag and occasional final-size mismatch.
- Vulkan now freezes render-resource planner extents for the duration of an
  interactive resize and unfreezes on mouse-up. Swapchain/presentation size can
  keep following the window, while heavy physical render-resource plans wait for
  the final size.
- Failed physical resource allocation plans are retried only after a short
  delay for the same planner/allocation signature, preventing one failed resize
  plan from hammering VMA every frame while previous resources are still
  retiring.
- `XRWindow` now tracks an effective logical window size alongside the effective
  framebuffer size during Win32 modal resize. The Win32 strategy feeds both from
  the same native client-size measurement, and ImGui display metrics use that
  effective logical size so editor layout can update during drag instead of
  waiting for Silk.NET's final cached size.
- Vulkan ImGui backend no longer overwrites `io.DisplaySize` after the generic
  renderer configures logical display size and framebuffer scale.
- Validation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22.
- Follow-up user run showed the editor UI was being resized but not fully
  re-laid out, Vulkan drag still felt heavy, and the 3D scene could go black
  during the debounced internal-resolution update.
- Win32 modal-loop resize now coalesces Vulkan render-thread resize jobs, drives
  active Vulkan repaint from the timer rather than every native sizing message,
  and uses a lower Vulkan sizing repaint cadence so the native drag loop stays
  lighter.
- Scene-panel internal render-target resizing is now suppressed while an
  interactive resize is in progress. Presentation/camera sizes still update
  live, but expensive internal scene-panel FBO reallocations wait for mouse-up.
- Vulkan swapchain recreates during interactive resize are throttled to a small
  cadence instead of rebuilding on every surface-size mismatch.
- Vulkan render-resource planner extents now stay frozen for the full native
  drag. The previous "promote after 150 ms stable" behavior was removed because
  it could allocate a new internal render-resource generation while the mouse
  was still down, causing the long black pause.
- Vulkan drops stale ImGui overlay snapshots after a swapchain-size race and
  resets the ImGui frame marker so the next resize render rebuilds layout at the
  current display metrics instead of stretching old draw data.
- Validation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -m:1
  -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22. `git diff --check` reported no whitespace errors; only
  line-ending normalization warnings were printed.
- User follow-up reported GPU instability/corrupted output during Vulkan
  resizing. Latest log inspected:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-22_14-31-32_pid2668`.
- Root cause found in `log_rendering.log`: while the window was being dragged,
  render-resource generations were still being requested for transient display
  sizes (`938x685`, `1275x1096`, `653x537`, etc.). After mouse-up queued a final
  `1920x1080` framebuffer resize, the old pending `653x537` generation kept
  building and committed at `14:32:40.375`, replacing the correct active
  `1920x1080` generation and causing the corrupt/stale render output.
- `XRRenderPipelineInstance` now discards pending render-resource generations
  during interactive resize when an active generation already exists. If a
  request arrives for the current active key, any stale pending key is also
  discarded instead of being allowed to commit later.
- `XRRenderPipelineInstance.CommitPendingGeneration` now validates the pending
  key against the current viewport generation key immediately before swapping it
  active. Mismatches are logged and discarded, preventing delayed intermediate
  resize generations from replacing the final-size generation.
- Vulkan present errors now include the actual `Result` in the exception, and
  `QueuePresent` `ErrorSurfaceLostKhr` is treated as an immediate swapchain
  recreate path instead of a generic render exception.
- Validation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -m:1
  -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors after
  the stale-generation guard and present-result handling changes.
- Follow-up hard-crash run inspected:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-22_14-39-59_pid22324`.
  The run still showed an intermediate `1404x859` generation committing after a
  `1920x1080` generation, and then a second drag recorded frames with frozen
  render-resource extents while the swapchain changed to `1517x722` and later
  `1701x906`.
- `log_vulkan.log` reported repeated
  `VUID-vkCmdClearAttachments-pRects-00016` validation errors, meaning clear
  rectangles were being emitted outside the active render-pass area. The same
  run also recreated the swapchain several times while the mouse was still down
  and grew retired descriptor-pool/image backlogs into the hundreds.
- Vulkan interactive resize is now safety-first: during the Win32 size/move
  modal loop, Vulkan records the latest desired surface size but defers
  swapchain recreation until the resize is no longer interactive. Frames are
  skipped and retired resources are drained whenever swapchain dimensions or
  active render-resource generations do not match the live viewport.
- Vulkan explicit clear recording now intersects `vkCmdClearAttachments`
  rectangles with the currently active render area before issuing the command,
  preventing the validation error from being submitted even if an upstream op
  carries stale viewport dimensions.
- Follow-up user run confirmed that fully deferring swapchain recreation made
  Vulkan safe but removed dynamic drag-time resizing. The underlying issue is
  that swapchain/presentation extent and full internal render-resource extent
  were being treated as one resize operation. Vulkan swapchain images must be
  recreated to match the live HWND framebuffer, but the default render pipeline
  render-resource generation is too heavy and hazardous to rebuild for every
  transient drag size.
- Vulkan now uses a coalesced presentation resize queue during interactive
  resize: `_pendingSurfaceWidth/_pendingSurfaceHeight` keep only the latest
  desired surface extent, and the swapchain is recreated at a bounded
  interactive cadence instead of every mouse message. Intermediate sizes are
  intentionally dropped.
- The Vulkan resource blocker now permits display-size mismatch during
  interactive resize only when the active generation's internal resource size
  still matches the viewport's internal size. This allows the old internal scene
  output to be scaled into the latest recreated swapchain while still blocking
  unsafe internal-resource mismatches.
- Final mouse-up resize still uses the full framebuffer resize path, which
  requests the final render-resource generation and blocks rendering until the
  active generation matches that final size.
- Follow-up user run was "almost perfect" but still showed sticky native
  dragging, ImGui scaling without re-layout, and a black/over-bright exposure
  pulse after mouse-up. Latest inspected Vulkan log:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-22_15-40-55_pid28180`.
- The log showed repeated `AutoExposureTex` validation errors after the final
  resize: command buffers expected the planner-backed exposure image in
  `GENERAL` while validation tracked it as `SHADER_READ_ONLY_OPTIMAL`. The
  temporary CPU fallback made auto exposure visibly wrong, so Vulkan GPU auto
  exposure is enabled again. Compute dispatch recording now emits an in-command
  storage-image transition for render-graph-owned image bindings when their
  live tracked layout is not `GENERAL`, and updates the same physical-image
  layout tracker used by later graph barriers.
- Drag-time presentation resizes now trust `XRWindow.EffectiveFramebufferSize`
  as the surface extent and only use logical window size as a fallback. The
  previous `max(framebuffer, window)` sampling could chase mismatched transient
  values from different Win32/Silk.NET timing points.
- Interactive Vulkan swapchain recreation cadence is now 100 ms. This keeps the
  coalesced queue responsive enough to show dynamic resize progress while
  reducing `vkDeviceWaitIdle` pressure from drag-time swapchain rebuilds.
- `XRViewport.ResizeCameraComponentUI` resize diagnostics are throttled to once
  per second. The previous unthrottled interpolated log ran on the modal-loop
  resize hot path and could make native dragging feel heavier.
- Swapchain recreation now resets the ImGui frame marker so the next overlay
  draw rebuilds at the recreated swapchain extent. The editor dockspace node is
  also pushed to the current ImGui main viewport size each frame so saved dock
  layouts resize instead of only being scaled.
- ImGui layout was still able to use stale canvas metrics during Win32 modal
  resize because `UICanvasTransform.ActualSize` is produced by the UI layout
  pass, which can lag the native drag loop. During an active interactive resize,
  `AbstractRenderer.ConfigureImGuiDisplay` now bypasses canvas metrics and uses
  the viewport/window effective size path so ImGui receives the live
  `DisplaySize` and relays out instead of scaling an old logical frame into the
  new framebuffer.
- Validation: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -m:1
  -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22.
- Follow-up auto-exposure regression fix: removed the planner-backed exposure
  texture skip in `VulkanAutoExposure`, so `AutoExposureTex` is written by the
  Vulkan compute path again. Validation: `dotnet build
  .\XREngine.Editor\XREngine.Editor.csproj -m:1
  -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22.
- Follow-up mouse-up exposure reset fix: full framebuffer resize can replace
  the Vulkan physical resource allocator even though `AutoExposureTex` remains
  an absolute 1x1 resource. The old allocator was destroyed without copying
  that one-pixel exposure history, so the first post-resize GPU auto-exposure
  update started from fresh image contents and appeared to reset bright.
  Resource-plan replacement now preserves `AutoExposureTex` by copying the old
  physical image into the new one before retiring the old allocator. Validation:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  -m:1 -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22. The broader editor build was blocked by a live `.NET Host`
  process holding the editor output DLLs.
- Follow-up native window drag lag fix: the Win32 modal-loop strategy still did
  engine presentation resize work from synchronous resize messages
  (`WM_SIZING`, interactive `WM_SIZE`, and Silk framebuffer resize callbacks).
  Those callbacks run in the native modal loop, so viewport/camera/ImGui layout
  work delayed the OS border drag itself. The Win32 strategy now only captures
  the latest client size from those messages and coalesces presentation resize
  plus render requests through the modal-loop timer. While that strategy is
  active, Silk framebuffer callbacks only update the effective framebuffer
  extent and do not walk viewports. Validation:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  -m:1 -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22.
- Follow-up native drag still not smooth: the timer path itself was still doing
  too much. `WM_TIMER` coalesced the size, but then immediately applied
  viewport/camera/ImGui presentation resize and could call `DoRender` inline
  when the modal WndProc was running on the render thread. That still blocked
  Windows' border-drag loop. The Win32 timer now only queues a pending
  presentation resize, and `XRWindow` consumes that pending presentation resize
  on the render path before drawing. A later correction restored timer-driven
  rendering during the modal loop because deferring all timer renders starved
  the render-thread queue until mouse-up. Timer renders now run only when the
  timer coalesces a new client size, avoiding duplicate modal-loop frames while
  keeping visible resize updates active. Validation:
  `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
  -m:1 -p:UseSharedCompilation=false` completed with 0 warnings and 0 errors on
  2026-06-22.

- [ ] Windows + OpenGL + Default.
- [ ] Windows + OpenGL + GLFW refresh callback.
- [ ] Windows + OpenGL + GLFW resize callback render.
- [ ] Windows + OpenGL + SDL backend.
- [ ] Windows + OpenGL + Win32 modal-loop timer.
- [ ] Windows + Vulkan + Default.
- [ ] Windows + Vulkan + GLFW refresh callback.
- [ ] Windows + Vulkan + GLFW resize callback render.
- [ ] Windows + Vulkan + SDL backend.
- [ ] Windows + Vulkan + Win32 modal-loop timer.
- [ ] Editor full-window presentation.
- [ ] Editor scene-panel presentation.
- [ ] Game/runtime startup window.
- [ ] Vulkan-to-OpenGL fallback after failed Vulkan creation.
- [ ] Multiple windows open at once.
- [ ] Window close while dragging or immediately after dragging.
- [ ] High-DPI monitor resize and monitor crossing.

## Completed Baseline Work

- [x] Reverted the previous GLFW resize workaround from `XRWindow`.
- [x] Reverted the companion scene-panel workaround from
  `XRWindowScenePanelAdapter`.
- [x] Captured primary source references for GLFW, Silk.NET, and Win32 resize
  behavior.
- [x] Confirmed both GLFW and SDL Silk.NET windowing packages are already
  referenced by the solution.

## Non-Goals

- Do not reintroduce private Silk.NET field reflection as the default behavior.
- Do not install native Win32 hooks unless the selected strategy explicitly asks
  for them.
- Do not make OpenGL-only assumptions in the shared resize/render helper.
- Do not silently switch windowing backends without logging the resolved
  strategy and actual backend.
