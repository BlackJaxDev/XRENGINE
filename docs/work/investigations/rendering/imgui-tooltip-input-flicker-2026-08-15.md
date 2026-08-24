# ImGui Tooltip Input And Header Flicker

## Problem

Hovering an inspector input that emits an ImGui tooltip can make the editor header flicker and prevent reliable interaction with the hovered field.

## Root Cause

Dear ImGui marks tooltip platform viewports with `ImGuiViewportFlags.NoInputs`. The Vulkan and OpenGL multi-viewport backends still included every detached native window in hovered-viewport resolution. A tooltip overlapping the cursor could therefore replace the inspector viewport as `MouseHoveredViewport`; the inspector stopped emitting the tooltip on the next frame, then emitted it again after hover returned, producing a frame-to-frame feedback loop.

The platform backends also used Silk.NET's `IWindow.Handle` for Win32 calls and `PlatformHandleRaw`. With the GLFW backend that value is a `GLFWwindow*`, not an HWND, so native hit testing and screen-rectangle queries could silently target an invalid handle.

After the input interception loop was fixed, live Win32 inspection found a second, independent cause for the remaining header flicker: GLFW created the tooltip native window with `WS_EX_APPWINDOW`, while the ImGui viewport requested `NoTaskBarIcon` and the integration added `WS_EX_TOOLWINDOW` without removing `WS_EX_APPWINDOW`. The tooltip was therefore classified as both an application window and a tool window while it appeared and disappeared.

The exact style mapping and hide-before-restore teardown greatly reduced native-window flicker, but user validation found that the ImGui menu row could still disappear occasionally. High-cadence captures separated that symptom from the Windows title bar: the OS title bar, toolbar, dockspace, and panels remained present while only the ImGui `File / Edit / Settings / View / Tools` row vanished. A temporary diagnostic also confirmed that `BeginMainMenuBar()` continued succeeding, so the menu draw data was being generated every frame.

The remaining fault was in the Vulkan upload lifetime. The desktop overlay and every detached platform viewport shared one `VulkanImGuiDrawBufferResources` instance. The desktop selected slots with its swapchain image index, while a detached viewport selected a slot with its own local frame index. Those independent index domains both start at zero. Because platform viewports render after the desktop, a tooltip upload could overwrite the beginning of the desktop's host-visible vertex and index buffers while the already-recorded desktop command buffer still referenced them. The menu row is at the beginning of the desktop ImGui draw stream, so it was the first visible data to be corrupted.

Whether the indices collided and whether the GPU consumed the desktop upload before the tooltip overwrote it depended on swapchain acquisition and CPU/GPU scheduling. That explains why the residual flicker appeared random, became less frequent after the native-window fixes changed timing, and disappeared under RenderDoc's timing perturbation.

## Implemented Solution

- Resolve the actual Silk.NET Win32 HWND for `PlatformHandleRaw` and Win32 APIs while retaining the GLFW pointer in `PlatformHandle`.
- Exclude current `NoInputs` viewports from hovered-viewport resolution in both renderer backends.
- Subclass detached Win32 viewport windows and return `HTTRANSPARENT` from `WM_NCHITTEST` while `NoInputs` is active.
- Return `MA_NOACTIVATE` from `WM_MOUSEACTIVATE` for `NoFocusOnClick` viewports.
- Show `NoFocusOnAppearing` windows with `SW_SHOWNA`.
- Map `NoTaskBarIcon` to `WS_EX_TOOLWINDOW` while explicitly removing `WS_EX_APPWINDOW`, notify Win32 of the non-client style change, and restore the original taskbar-style bits during teardown.
- Synchronously hide a retiring viewport before removing its subclass or restoring its taskbar-style bits, so no visible tooltip can temporarily regain `WS_EX_APPWINDOW` during deferred disposal.
- Update native behavior when ImGui changes viewport flags, and remove the subclass during platform-window teardown.
- Give the desktop output and each detached Vulkan platform window its own `VulkanImGuiDrawBufferResources` instance. Platform recording now receives its owning output's buffers explicitly, and each platform window retires its buffers only after its queues are idle during final teardown.
- Keep `VulkanImGuiResources` limited to genuinely shared Vulkan handles; output-local vertex and index buffers are no longer stored there.

## Validation

- `XREngine.Runtime.Rendering`, `XREngine.Runtime.Rendering.OpenGL`, and `XREngine.Runtime.Rendering.Vulkan` targeted builds pass with zero warnings and zero errors.
- The full isolated editor builds for sessions `imgui-tooltip-fix2` and `imgui-tooltip-fix3` passed with zero errors. The nine warnings are pre-existing nullable/unused-member warnings in the OscCore submodule.
- Live Vulkan input validation passed in `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260815-203300-imgui-tooltip-fix2`:
  - The `Near Plane` inspector tooltip remained continuously visible across repeated captures separated by more than one second.
  - The top editor header and dockspace remained visible and stable.
  - Dragging the hovered `Near Plane` input changed its value while the tooltip remained visible, confirming the tooltip native window no longer intercepts the pointer.
  - The editor shut down through the named session manager without a subclass/disposal fault. The session logs contain no Vulkan validation error, access violation, or ImGui platform-window exception.
- Live Vulkan header validation passed in `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260815-204952-imgui-tooltip-fix3`:
  - While the inspector tooltip was visible, its native extended style was `0x98` (`WS_EX_TOOLWINDOW` present and `WS_EX_APPWINDOW` absent).
  - The editor remained the foreground window for 100 consecutive 10 ms samples while the tooltip was visible.
  - Twelve consecutive 40 ms captures contained both the editor header and the tooltip without the header disappearing.
  - The editor process exited after the named session requested shutdown. Its logs contain no access violation, native subclass failure, or Vulkan validation error associated with this change. Shutdown did report the existing Vulkan command-pool retirement warning (`39 recorded artifact(s)`), which occurs after renderer teardown begins and is unrelated to the tooltip behavior.
- The isolated `imgui-tooltip-fix5` editor build passed with zero errors. Its nine warnings are the same pre-existing OscCore nullable/unused-member warnings.
- Live Vulkan buffer-isolation validation passed in `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260815-213805-imgui-tooltip-fix5`:
  - The `Distance` inspector tooltip remained visible across twelve consecutive high-cadence captures.
  - The Windows title bar and the complete ImGui menu/header row remained visible in every capture.
  - Clicking the still-hovered `Distance` field entered its active input state while the tooltip remained visible.
  - The successful PID 44008 run contains no Vulkan validation error, device-loss report, or ImGui platform-window exception.

## User Confirmation

The first fix restored input interaction, as confirmed by the user. The native-window corrections made the header flicker substantially rarer, but did not eliminate it. The per-output Vulkan draw-buffer isolation passes local live validation; user confirmation of that final correction is pending.
