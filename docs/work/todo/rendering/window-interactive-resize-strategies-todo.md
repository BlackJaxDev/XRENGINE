# Window Interactive Resize Strategies TODO

Last Updated: 2026-06-12
Owner: Rendering
Status: Planning. Previous GLFW private/reflection plus Win32 resize workaround has been reverted.
Target Branch: none; user requested no branch for this work.

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

- [ ] Add enum value and setting plumbing.
- [ ] Log the resolved strategy once per window.
- [ ] Ensure `Default` does not install callbacks, native hooks, private
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

- [ ] Add a small `IInteractiveResizeStrategy` abstraction owned by `XRWindow`.
- [ ] Resolve the native GLFW window handle through public Silk.NET native
  window surfaces only.
- [ ] Register `glfwSetWindowRefreshCallback` for GLFW windows.
- [ ] In the callback, request a render through a guarded
  `XRWindow.RenderInteractiveResizeFrame(...)` helper.
- [ ] Avoid rendering if the normal render callback is active or the renderer is
  disposed.
- [ ] Restore the previous callback on `UnlinkWindow()` / dispose.

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

- [ ] Subscribe to `IWindow.Resize` in addition to `FramebufferResize` only for
  this strategy.
- [ ] Queue framebuffer resize through the existing pending-resize fields.
- [ ] Add a guarded interactive render helper that calls the same resize and
  render path as a normal frame.
- [ ] Coalesce duplicate resize sizes to avoid redundant resource invalidation.
- [ ] Rate-limit callback rendering to a target such as 30 or 60 Hz.

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

- [ ] Add a backend selection helper before `Window.Create(...)`.
- [ ] For this resize strategy, prioritize SDL instead of GLFW.
- [ ] Verify OpenGL context creation, input, clipboard, cursor capture, DPI, and
  transparent framebuffer behavior through SDL.
- [ ] Verify Vulkan surface creation through SDL.
- [ ] Keep fallback behavior clear: if SDL cannot create the requested Vulkan
  window, fall back only according to the existing Vulkan-to-OpenGL policy.
- [ ] Add startup diagnostics showing the actual windowing backend and graphics
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

- [ ] Add a Windows-only strategy class, compiled or activated only on Windows.
- [ ] Resolve the `HWND` through public native window data where possible.
- [ ] Subclass the window proc with a lifetime-owned delegate.
- [ ] On `WM_ENTERSIZEMOVE`, start a timer at a configurable interval such as
  16 ms or 33 ms.
- [ ] On `WM_TIMER`, request an interactive render if the renderer is ready.
- [ ] On `WM_SIZING` and `WM_WINDOWPOSCHANGED`, queue the latest client size.
- [ ] On `WM_EXITSIZEMOVE`, stop the timer, queue final resize, and render once.
- [ ] Restore the original window proc during unlink/dispose.
- [ ] Add diagnostics for hook install, hook restore, timer start/stop, and
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

- [ ] Extend the existing `UseNativeTitleBar=false` path with hit-test regions
  for resize edges and corners.
- [ ] Use platform APIs or Silk.NET window position/size setters to apply
  resize during normal engine frames.
- [ ] Keep normal render/update callbacks active while dragging.
- [ ] Add cursor feedback for resize edges.
- [ ] Respect minimum window size and aspect constraints.

Risks:

- Custom chrome is a larger UX surface than just resize rendering.
- Platform snapping, accessibility, shadows, and system menu behavior need
  deliberate follow-up.

Acceptance criteria:

- [ ] No OS modal resize loop is entered for engine-owned resize grips.
- [ ] OpenGL and Vulkan use the same regular render-frame resize path.
- [ ] Native title-bar mode remains available and unaffected.

## Shared Implementation Tasks

- [ ] Add `EInteractiveWindowResizeStrategy`.
- [ ] Add strategy resolution and diagnostics.
- [ ] Add `IInteractiveResizeStrategy` with `Install(XRWindow)` and
  `Uninstall()` lifetime.
- [ ] Add one guarded `XRWindow.RenderInteractiveResizeFrame(string reason)`
  helper shared by callback-based strategies.
- [ ] Prevent nested renders with an interlocked guard.
- [ ] Keep `ProcessPendingFramebufferResize()` as the only code that mutates
  viewport sizes and calls `Renderer.FrameBufferInvalidated()`.
- [ ] Keep scene-panel FBO resizing debounced through
  `XRWindowScenePanelAdapter`; do not destroy scene-panel FBOs on every
  drag-frame unless the selected strategy requires it.
- [ ] Add per-strategy diagnostics counters:
  callback count, interactive render count, suppressed render count, resize
  queue count, and last resize reason.
- [ ] Document the selected strategy in startup logs and diagnostics UI.

## Validation Matrix

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
