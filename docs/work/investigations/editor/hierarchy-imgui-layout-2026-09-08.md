# ImGui hierarchy layout — 2026-09-08

The hierarchy dock was crowded at approximately 320 px: metadata and large controls consumed the top of the panel, scene actions squeezed scene titles, and the 72 px Active column wasted node-label space.

The layout now keeps world identity and Settings together, with a compact Expand / Collapse / Focus / View toolbar above an independently scrolling tree. View contains Show Editor Scene and game-mode information. The world status tooltip retains the full asset path. Scene visibility remains directly accessible; scene actions (including Unload Scene) are available from the adjacent ellipsis button or the scene header context menu. Dirty scenes mark the menu button in amber. Node tables use a compact, fixed active column without repeated headings or vertical grid lines. Inactive nodes use muted text; truncated names have tooltips. Editor-only content has its own short section label.

The layout helpers live in `XREngine.Editor/IMGUI/EditorImGuiUI.HierarchyPanel.Layout.cs`; hierarchy mutation, undo, selection, rename, drag/drop, and expansion remain in the existing hierarchy partial. The asset-drop window check includes the new scrolling child.

## Iterations and validation

- Both isolated editor builds succeeded with **0 warnings and 0 errors**. Final incremental build: 25.35 seconds. Session: `hierarchy-layout-0908`, Debug/AnyCPU. Its settings copy used OpenGL and left the root settings untouched.
- MCP `ping` and `list_scene_nodes` succeeded against the named session. Full-window captures of both compiled iterations were actually viewed; the final capture shows the compact world header, all four toolbar actions, scene visibility/menu, selection highlighting, and aligned node active controls at approximately 450 px panel width.
- The scene popup was kept inside the same table ID scope as its triggers. Its compact ellipsis is drawn without texture assets.
- Independent review identified clipped Focus/View labels at very narrow widths. Each toolbar label now shortens independently with stable `###` IDs, minimum target widths, and 4/2/1-column wrapping. Re-review accepted the correction with no further findings.
- Computer Use could not initialize because the Windows sandbox failed with `helper_unknown_error: apply deny-read ACLs`, including after reset. Therefore actual mouse/keyboard, drag/drop, and dock-resizing checks remain unverified. The repository capture script ran successfully in Windows PowerShell, with a scratch copy restricted to the named session's exact PID.
- A further launch with `XRE_PROFILE_WINDOW_WIDTH=340` and `XRE_PROFILE_WINDOW_HEIGHT=720` returned a blank client area despite MCP readiness; this does **not** count as narrow-panel visual validation. No unrelated renderer/startup changes were made. Manual narrow-dock interaction validation remains useful.
- No tests added or modified, per repository policy. Diff whitespace checks passed. The named session was stopped after validation.
- User feedback: pending.

Evidence is disposable under `Build/_AgentValidation/20260908-161201-hierarchy-layout/`: `logs/final-build.log`, `logs/scene-nodes.json`, `mcp-captures/hierarchy-final-wide.png`, and the cropped `mcp-captures/hierarchy-panel-preview.png`. Runtime logs are under `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260908-161511-hierarchy-layout-0908/logs/`.

Independent source design review used broker run `8f75dbd832f24d98a7a71f30472a27e7`, requested/actual `gpt-5.6-sol` at low effort. The later native review covered child-window scrolling, widget/popup IDs, input ownership, fast-skip behavior, and responsive toolbar arithmetic.
