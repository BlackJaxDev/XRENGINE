# ImGui Panel And Notification Validation

Guide: [Editor Notifications](../../../user-guide/editor/notifications.md)
Investigation: [Panel close and asset notifications](../../investigations/editor/2026-10-14-panel-close-notifications.md)

## Setup

Use a named isolated ImGui editor session. Use the default world without a model
import for the input checks. Record an asset diagnostic through MCP with
`AssetDiagnostics.RecordMissingAsset`. Use a session-only `MainThread` dispatch
mode for method calls. Repeat the close checks with OpenGL and Vulkan.

## Checks

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Asset alert | Record a missing reference while the panel is closed. | Count increases; panel stays closed. | Passed on Vulkan | 2026-10-14 |
| Open from alert | Select the bell, then the missing asset event. | Panel opens and receives focus. | Passed on Vulkan and OpenGL | 2026-10-14 |
| Duplicate report | Record the same path and category twice. | Badge counts one reference; panel shows two hits. | Passed on Vulkan | 2026-10-14 |
| Clear missing records | Select Clear Missing Asset Log in the detached panel. | Badge returns to zero; dropdown is empty. | Passed on OpenGL | 2026-10-14 |
| Repaired records | Record and clear repaired references. | Count and repaired event follow the records. | Pending | None |
| ImGui close | Click X on a detached panel. Repeat after reopening. | Panel closes; main window remains open. | Passed on Vulkan; one close passed on OpenGL | 2026-10-14 |
| Native close | Press Alt+F4 on the detached panel. | Panel closes; main window remains open. | Passed on Vulkan and OpenGL | 2026-10-14 |
| Drag and focus | Drag a detached title bar. Switch focus to the main window and back. | Drag and later clicks work. | Passed on Vulkan | 2026-10-14 |
| Continued reports | Close the panel, then record another reference. | Badge increases; panel stays closed. | Passed on Vulkan | 2026-10-14 |

## Failures

The first runtime passes found lost detached clicks. The final build passed the
checks listed above. See the investigation for the event pump and focus fixes.
Reports were injected through the live diagnostic API. A full model import and
additional panel types were not part of this run.
