# Console Vulkan routing and detached-window focus

## Reported behavior

With the ImGui Console open under Vulkan, Vulkan messages appeared as `[Unknown] [Vulkan] ...` in the All tab. The user also reported that an open Console took text focus from another application.

## Findings

- The diagnostics core already stores Vulkan entries with `ELogCategory.Vulkan` and writes them to the Vulkan log. `EditorImGuiUI.ConsolePanel` omitted Vulkan from its tab, color, and display-prefix switches. The `[Unknown]` label came from that UI default branch; the `[Vulkan]` text was already part of many message bodies.
- Vulkan's detached ImGui viewport renderer calls `ShowPlatformWindow` on every ready viewport in `RenderPendingViewports`, every frame. The show helper previously set `IWindow.IsVisible = true` on each call. Repeating a native show request on a visible window can reactivate the editor after the user has moved focus to another application. The Console itself does not call `SetKeyboardFocusHere` or another explicit focus API.
- `ImGuiPlatformWindowBehavior.TryShowWithoutActivation` handles the first show when ImGui requests `NoFocusOnAppearing`; explicit platform focus requests remain handled by `PlatformSetWindowFocus`.
- The frequent `[Vulkan][FrameTree]` and `[Vulkan][PresentNow] readiness=ready` entries are separate from the console routing bug. `VulkanFrameLoop.PublishDesktopFrameTelemetry` and `DriveDesktopPresentNowReadiness` called `Debug.VulkanEvery` unconditionally in DEBUG/EDITOR builds. Their keys were rate-limited to 500 ms and 1 second respectively, then emitted at normal Vulkan verbosity. No diagnostic flag was required, so routine frame activity filled the bounded global Console history.

## Change

- Added a Vulkan Console tab, category prefix, and category color. When the message body already starts with `[Vulkan]`, the Console does not add a second copy of that prefix.
- The Vulkan show helper now returns when its native viewport is already visible. Its first reveal and ImGui's explicit focus callback retain their prior behavior.
- Gated the two periodic informational frame messages on the existing `VulkanFrameDiagnosticsTraceEnabled` setting. It is enabled by `XRE_VULKAN_RECORDING_DIAG`, `XRE_VK_TRACE_DRAW`, or `XRE_VK_TRACE_SWAPDRAW`. Telemetry publication and failure warnings/errors remain active without those flags.

## Validation

The final integrated editor build passed with zero warnings and zero errors in 88.85 seconds. The Vulkan tab filtered entries correctly. With the Console detached and rendering, a text box in Notepad retained the caret and accepted typing over a 36-second focus probe. The final Vulkan run produced zero periodic `FrameTree` or `PresentNow readiness=ready` messages with tracing disabled. Other Vulkan diagnostics remained visible, including missing vertex-attribute warnings. The owned validation session was stopped and its logs inspected. Docked Console focus behavior was not separately probed.

Evidence is under `Build/_AgentValidation/00000000-000000-shared/avatar-transforms-20260924/`: `logs/mapping-final-build.log`, `logs/mapping-final-log_vulkan.log`, and `reports/console-focus-check.txt`. See the [avatar mapping/import investigation](../asset-import/avatar-mapping-and-import-diagnostics-2026-09-24.md) for the accompanying model checks.
