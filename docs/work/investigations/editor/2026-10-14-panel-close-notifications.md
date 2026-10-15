# ImGui Panel Close And Asset Notifications

## Problem

The user reported that model import forced Missing Assets open. The panel could
not be closed reliably when it was in a separate window.

## Findings

- `EditorImGuiUI.DrawImGui` consumed the asset diagnostic display flag and set
  `_showMissingAssets` to `true`. A later report could reopen a closed panel.
- Both OpenGL and Vulkan forwarded native close intent in the platform update
  callback. Dear ImGui clears platform request flags after that callback.
  The next panel `Begin` could not consume the close request.
- The panel already passes its persistent visibility field by reference to
  `ImGui.Begin`. The native desktop backend already retains close intent and
  defers native destruction. These paths do not need replacement.
- Per-window focus events can erase detached mouse clicks. The main input
  router reports loss of focus. The detached router then reports mouse press,
  mouse release, and focus gain. ImGui can defer the release and focus gain to
  the next frame. Its end-of-frame focus loss handling clears the mouse press.
  OpenGL and Vulkan both had this event order.
- Vulkan detached windows did not publish input snapshots. The desktop auxiliary
  event pump matched the parent through `SharedContext.OwnerGeneration` only.
  Vulkan has no shared GL context, so its windows never matched the main window.
  Native close intent is set directly by the callback and did not need this
  publication step. This explains why Alt+F4 worked while all mouse controls failed.

## Changes

The desktop focus query also used startup visibility. Detached windows start
hidden and become visible later. The query therefore cleared their focus after
each event pump. It now checks current native visibility.

- Forward native close intent before `ImGui.NewFrame` in both backends.
  Keep native window retirement under the existing ImGui lifecycle.
- Report one focus state for the main window and all live detached windows.
  Send it before mouse and keyboard events. Do not send per-window focus events.
- Set an explicit event pump parent generation for detached windows. Preserve
  both native owner-thread and parent-generation checks in the desktop event loop.
- Remove automatic panel opening from diagnostic reports.
- Show a bell with the distinct asset reference count at the right of the menu
  bar. Group missing and repaired references in its popup. Open and focus the
  panel only when the user selects an event.
- Read counts under the existing diagnostic locks. Cache text until counts
  change. Do not build diagnostic snapshots for the badge.

## Validation

The first isolated Debug editor build passed with zero warnings and errors.
In the Vulkan runtime, a recorded missing reference changed the badge from zero
to one without opening the panel. The dropdown opened the panel. Native Alt+F4
closed the detached panel and kept the main editor open. The ImGui X button
still failed. A repeated click reproduced the failure without new diagnostics.
This ruled out automatic panel opening as the remaining cause and led to the
focus event fix. The main composited screenshot was captured and viewed.

The second Debug build also passed without warnings or errors. Duplicate reports
kept the badge at one and showed two hits. The focus fix alone did not restore
Vulkan clicks. The collapse control also failed, which narrowed the remaining
issue to input delivery. Source inspection found the absent Vulkan event pump
parent. A manual pump call through MCP did not change the result; MCP uses the
app thread and the pump retains its native owner-thread check.

The first build of the event pump parent fix met three temporary compile errors
in `AdvancedGpuScenePublisher.MaterialTransitions`. Concurrent work restored
the missing methods before the retry. This task did not change those files.

An attempted `ImGui.GetIO` MCP inspection failed because its pointer properties
cannot be serialized by the reflection tool. The editor remained responsive.
This diagnostic failure does not establish a UI input result.

The event pump parent build passed without warnings or errors. The detached
close button still failed. Source inspection then found the startup visibility
gate in the native focus query.

The final isolated build passed with zero warnings and zero errors. In Vulkan,
the detached X button closed the panel twice. The notification reopened it.
Title-bar dragging changed its native position, and later clicks still worked.
A new report increased the badge from one to two without reopening the panel.
After a session-only switch to OpenGL, the detached X button, notification
selection, Clear Missing Asset Log, and Alt+F4 also worked. Clearing the log
changed the badge to zero and showed an empty dropdown. The main editor stayed
open after each panel close. Native screenshots were viewed for these checks.
The named session was stopped after validation.

Final logs are under
`Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20261014-220635-imgui-panel-input/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-10-14_22-14-33_pid102780/`.
No ImGui callback errors were found. The backend switch logged a separate
allocator-budget query after Vulkan allocator shutdown. OpenGL also logged a
deferred shader link failure. These rendering issues did not block the panel
checks and were not changed in this task.

No test code was added or changed.
The broker route tool returned a deprecated model. No API worker was started.
A native read-only agent confirmed the close request timing defect.

## User Result

The user has not yet evaluated the change.

## References

- [Editor Notifications](../../../user-guide/editor/notifications.md)
- [Editor workflows](../../../developer-guides/ai/agent-editor-workflows.md)
