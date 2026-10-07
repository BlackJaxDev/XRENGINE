# Window And Render Thread Validation

Scope: runtime checks for window creation, renderer backend startup and fallback, render-settings ownership, and render-thread ownership.

Architecture: [Window Creation And Renderer Initialization](../../../architecture/rendering/window-creation-and-renderer-init.md), [Rendering Code Map](../../../architecture/rendering/code-map.md)

Code todos: [Vulkan Core Frame Loop Master TODO](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md)

## Imported Checks

### From render-settings-api-separation-refactor-todo.md

- [ ] Run `dotnet test XREngine.UnitTests/XREngine.UnitTests.csproj --filter Settings`. Report unrelated failures separately.
- [ ] Start the editor with OpenGL. Expected: normal startup.
- [ ] Start the editor with Vulkan dynamic rendering. Expected: normal startup.
- [ ] Start the editor with the Vulkan legacy render-pass target mode. Expected: normal startup.
- [ ] Make Vulkan initialization fail, or run where Vulkan is unavailable, with `RenderBackendFallbackPolicy.RequireRequested`. Expected: startup fails visibly with the requested backend, the policy, and the exception summary.
- [ ] Repeat with a fallback policy that permits fallback. Expected: the log records the fallback reason.
- [ ] Open the profiler, diagnostics, and settings panels. Expected: grouped settings (`OpenGL`, `Vulkan`, editor diagnostics groups) are shown clearly with their source.

### From dedicated-render-thread-window-ownership-todo.md

Run each check in the collapsed mode and with `XRE_WINDOW_PUMP_HOST=sdl-prototype` (Vulkan, `SdlBackend` resize strategy) unless the check names one mode.

- [ ] Start the editor in the default world and with `--unit-testing`. Expected: normal render and input.
- [ ] Start the editor with OpenGL. Expected: render, input, resize, minimize, restore, and close work.
- [ ] Start the editor with Vulkan. Expected: render, input, resize, minimize, restore, and close work.
- [ ] Split mode: confirm ImGui overlay submission, scene-panel mode, full-window mode, and profiler overlays and render diagnostics.
- [ ] Drag a native resize border continuously. Expected: the native rectangle stays responsive.
- [ ] Vulkan live resize. Expected: presents refreshed lagging output content, or skips ticks with clear diagnostics.
- [ ] Release the mouse after a drag. Expected: the exact-size full internal generation catches up.
- [ ] Change VSync and HDR preferences. Expected: the change applies without errors.
- [ ] Run a screenshot or readback flow. Expected: correct image.
- [ ] Open multiple startup windows. Expected: each renders, resizes, minimizes, restores, and closes cleanly.
- [ ] OpenXR: confirm context-affine startup on the correct render owner and the desktop mirror path.
- [ ] OpenVR: confirm the stereo and mirror swap paths keep their behavior.
- [ ] Confirm VR-disabled desktop rendering stays the baseline path.
- [ ] Run the existing Vulkan P1 and swapchain-resize regression tests (`VulkanP1ValidationTests`).

### From window-interactive-resize-strategies-todo.md

For each strategy, record whether frames update during the drag, the final size after mouse-up, and any validation or device-loss messages. Select a strategy with `XRE_INTERACTIVE_RESIZE_STRATEGY`.

- [ ] Windows + OpenGL with `Default`, `GlfwRefreshCallback`, `GlfwResizeCallbackRender`, `SdlBackend`, and `Win32ModalLoopTimer`.
- [ ] Windows + Vulkan with `Default`, `GlfwRefreshCallback`, `GlfwResizeCallbackRender`, `SdlBackend`, and `Win32ModalLoopTimer`.
- [ ] `Default`: resize behavior has no callbacks or hooks, and OpenGL and Vulkan initialize and render normally.
- [ ] `GlfwRefreshCallback`: OpenGL renders a refreshed frame when the callback fires; Vulkan does not recreate the swapchain from the native callback; cleanup survives window close and Vulkan-to-OpenGL fallback.
- [ ] `GlfwResizeCallbackRender`: when callbacks arrive during a drag, the window updates before mouse-up; when they do not, diagnostics show it and rendering stays stable.
- [ ] `SdlBackend`: OpenGL context creation, input, clipboard, cursor capture, DPI, and transparent framebuffer work; Vulkan surface creation works or fails with a clear message; no GLFW-specific path runs; editor scene panel and full-window rendering survive resize.
- [ ] `Win32ModalLoopTimer`: no hook on non-Windows platforms; the hook is restored on normal close and on exception cleanup; OpenGL makes its context current before rendering; Vulkan queues only frame-boundary-safe resize work.
- [ ] `EngineBorderlessResize`: native title-bar mode stays available and unaffected.
- [ ] Editor full-window presentation and editor scene-panel presentation during resize.
- [ ] Game or runtime startup window during resize.
- [ ] Vulkan-to-OpenGL fallback after a failed Vulkan window creation.
- [ ] Multiple windows open at once during resize.
- [ ] Close the window while dragging or just after a drag. Expected: no crash and no stale window-procedure hook.
- [ ] Resize on a high-DPI monitor and move the window across monitors.

### From runtime-modularization-phase4-todo.md

- [ ] Run the extended performance and resize soak on the modular runtime. Record the result.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/COMPLETED/window-interactive-resize-strategies-todo.md`

- [ ] High-DPI monitor resize and monitor crossing.
