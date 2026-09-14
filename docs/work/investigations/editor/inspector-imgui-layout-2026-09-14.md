# ImGui inspector layout and frame cost — 2026-09-14

## Request and status

Implement the proposed Inspector visual and performance improvements while retaining live editing, undo, multi-selection, and custom inspectors. Implementation and source review are complete. The final build and isolated single-/multi-selection runtime checks passed. The user subsequently reported shrinking transform axis fields after resizing. The follow-up below reproduces that failure and validates the corrected shared table in the installed ImGui runtime. Full-editor visual and interactive validation remains limited by unavailable desktop automation and timed-out screenshot capture.

## Resulting behavior

- A compact node summary keeps Name and Active together, shows a breadcrumb, and moves IDs and hierarchy details into an expandable Details section. Multi-selection uses the same compact structure.
- Component headers combine disclosure, title, activation, and an overflow menu for rename/remove. Transform is initially open and components initially collapsed. Search temporarily opens matching sections and restores their previous expansion state when cleared.
- Transform axes use aligned X/Y/Z controls with axis colors. Significant-digit formatting and NoRoundToFormat preserve precise entry; each axis retains its undo lifecycle.
- Property columns are resizable with initial 38/62 label/value proportions. Truncated labels reveal their full name and description in a tooltip. Mixed values have explicit markers; a mixed checkbox displays a dash and its first click sets the selection to true.
- Type-level layouts cache sorted property/field descriptors and override pairing. Recursion-local row storage is reused while EditorBrowsableIf conditions are still evaluated against current targets.
- Single-target state is retained weakly, and multi-selections are immutable snapshots with selection-scoped reuse. Member-local value buffers avoid fresh arrays each draw, including an array-free single-value path. Buffers remain local to a member because deferred dialogs and nested inspectors can retain them.
- Uniform property rows and primitive collection tables are clipped before values are read. Active controls and popup owners bypass clipping. Variable-height custom editors keep their existing rendering path. All clipped rows reserve a minimum control height, including failure/read-only rows.
- Enum data, captions, and environment-variable attributes are cached. Camera preview metadata refreshes every 125 ms, or immediately when camera/default-target identity changes. Visible GPU handles are resolved every frame; offscreen camera/model previews skip handle work. Failure placeholders retain image/control/label geometry and expose diagnostics.
- A bounded 128-draw timing/allocation window is available through the existing MCP invoke_method action: type_name XREngine.Editor.InspectorDrawMeasurement, method_name GetSnapshot, arguments []. Snapshot serialization runs outside the draw scope. This measures elapsed draw time and current-thread managed allocations, not GPU time or whole-editor FPS.

## Review and fixes

Independent source review covered cached layout and live override visibility, buffer lifetime, mixed controls, transform precision/undo, and preview clipping. Findings were corrected: minimum property-row height, clipping bypass during active edits/popups, immutable deferred-dialog targets, restored search expansion state, preview failure diagnostics, fixed-height failure placeholders, and exact framed-control versus text-row spacing in offscreen preview reservations. No other correctness regressions were found in the reviewed paths. The final two preview-spacing corrections were compiled after the runtime measurement build; those offscreen paths were source-reviewed but not interactively exercised.

## Validation

- Final command: `dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore -m:1 -nr:false -v minimal` — passed with 0 warnings and 0 errors in 22.81 seconds.
- An earlier parallel build failed in MSBuild infrastructure before compilation; the single-worker retry passed. The isolated runtime build also passed with 0 warnings and 0 errors.
- Targeted `git diff --check` passed. Git emitted only LF/CRLF normalization notices for two existing files.
- Named isolated session: `inspector-layout-0914`, Unit Testing World, session-local OpenGL/DefaultRenderPipeline overrides, 1600 by 900 requested window size. Root world settings were not modified.
- MCP selected Mitsuki, appended Root Node, verified a two-node selection, then returned to Mitsuki. Inspector draw counters continued advancing in every scenario. The final session logs and stderr contained no matched exceptions, errors, assertions, or Inspector failure entries.
- Only this named session was stopped through Manage-McpEditorSession.ps1. Read-back returned Stopped, McpReady false, and no process ID.
- No automated tests were added, modified, or run for this UI implementation.

### Observed draw samples

Each row is a separate rolling window of 128 draws in the isolated Debug editor. Component sections were initially collapsed. These are limited smoke measurements without a pre-change baseline; concurrent build activity was present during part of the run, and the later timing spike has not been attributed.

| Selection | Average draw ms | Peak draw ms | Average allocated bytes/draw | Peak allocated bytes/draw |
| --- | ---: | ---: | ---: | ---: |
| Mitsuki | 0.11915 | 1.8453 | 1,144 | 1,144 |
| Mitsuki + Root Node | 0.06471 | 0.1190 | 440 | 440 |
| Mitsuki after returning from multi-selection | 0.71016 | 63.3112 | 1,646.25 | 21,064 |

These figures establish that the measured Inspector paths ran. They do not establish an FPS gain, an allocation reduction against the previous version, or an allocation-free Inspector. An earlier independent CPU frame dump also recorded UI.DrawInspectorPanel at 0.155 ms for one frame.

### Validation limits

Windows Computer Use initialization failed in its sandbox helper (`apply deny-read ACLs`), and a reset/retry could not start its trusted Node process. MCP screenshot capture with screen-space UI timed out on both Vulkan and OpenGL, so no new screenshot was visually verified. Interactive narrow/wide resizing, text entry/undo, mixed-value clicking, search restoration, collection scrolling, and preview clip transitions still need hands-on verification. Their implementations were source-reviewed and compiled; they are not claimed as interactive test passes.

## Evidence

Disposable evidence is under `Build/_AgentValidation/20260914-105054-inspector-layout/`:

- `logs/editor-final-build.log` and `logs/editor-isolated-build.log`.
- `logs/cpu-frame-profile.log`.
- `mcp-output/inspector-single-selection-measurement.json`.
- `mcp-output/inspector-multi-selection-measurement.json`.
- `mcp-output/inspector-restored-single-selection-measurement.json`.

Final runtime logs were inspected at `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260914-110815-inspector-layout-0914/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-09-14_11-30-20_pid2660/`. These ignored paths are disposable; the findings above are the durable record.

## Follow-up: transform columns collapse after resizing

### User report

The first layout failed in use: changing Inspector width caused X and then Y inputs to shrink gradually to effectively zero, leaving Z to fill the row. The user supplied a screenshot and requested shared X/Y/Z table headers. This is a regression introduced by the initial Inspector layout implementation.

### Cause and correction

The original transform implementation used three separate `SizingStretchProp` tables. X/Y/Z columns had implicit stretch weights, and each cell combined an axis label with a negative-width fill input. Content-derived weights depended on the previous frame's column widths. Repeated layout after resizing amplified the imbalance and transferred space toward the final column. `NoSavedSettings` prevented .ini persistence, but did not remove the live table's sizing state.

`StandardTransformEditor` now uses one four-column table for Position, Rotation, and Scale. The label column fits the longest row label; X/Y/Z use `SizingStretchSame` and explicit 1.0 stretch weights. Colored X/Y/Z headers are drawn once. Each numeric input fills its cell with an explicit positive width. Per-row IDs keep all nine axes distinct, and undo tracking remains immediately after each drag. Numeric precision and the queued transform mutations are unchanged.

### Runtime reproduction and validation

A disposable diagnostic replay links the actual before/after `StandardTransformEditor.cs` to the editor's installed ImGui.NET/cimgui 1.91.6 binaries. Only scene mutation/undo dependencies are inert hooks; the table and input layout execute in native ImGui. This diagnoses the active regression without adding or modifying repository tests. It does not validate real engine undo behavior or constitute a screenshot of the full editor.

Both versions ran 2,400 frames: 400 frames each at window widths 380, 620, 240, 460, 300, and 380 pixels. Frame padding was 6 by 6 and cell padding 4 by 3.

| Scenario | Before: X/Y/Z widths | After: X/Y/Z widths |
| --- | --- | --- |
| 240px window after 120 frames | 1 / 1 / 95 px | 48 / 48 / 48 px |
| 460px window after 120 frames | 1 / 1 / 315 px | 121 / 121 / 122 px |
| Return to 380px after 399 frames | 1 / 1 / 235 px | 94 / 95 / 95 px |

The corrected version's maximum axis-width difference was 1px (integer rounding); minimum axis width was 48px across the exercised sizes. Every row shared the same column boundaries, all 10 controls including Order retained distinct stable IDs, and no widths drifted over the 400-frame holds. Independent source review found no issues in the corrected sizing, header rendering, row IDs, or placement of undo tracking.

The full editor build after this correction passed with 0 warnings and 0 errors in 19.85 seconds (`dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore -m:1 -nr:false -v minimal`; `logs/transform-width-editor-build.log`). Targeted whitespace validation also passed.

Evidence: `logs/transform-width-before.log`, `logs/transform-width-after.log`, and the disposable linked-source replay under `temp-build/transform-width-replay/`, all within the existing task evidence root. The user's confirmation of the corrected layout in the full editor is still pending.

## Follow-up: Translation and proportional scale linking

The user requested renaming Position to Translation and a default-on toggle beside Scale that preserves relative axis proportions. The transform table now shows Translation and reserves enough label-column width for both that name and the Scale checkbox. The checkbox is an editor-session setting, starts enabled, and retains the user's choice across selected transforms; it does not serialize into the scene.

While linked, changing one axis multiplies the other axes by the same ratio. For example, changing X from 2 to 3 on scale (2, 4, 6) produces (3, 6, 9). The ratio uses the vector captured at gesture activation, and the latest candidate remains visible while the scene write is queued. This preserves signed proportions through zero and negative values without compounding per-frame changes. Unlinked edits affect only the selected axis. A source axis already at zero has no defined ratio, so it edits independently; an entirely zero vector recovers uniformly. These zero cases are described in the checkbox tooltip. Nonfinite proportional results are rejected before scene mutation.

The existing native ImGui diagnostic replay linked the updated editor source and exercised the actual checkbox and Ctrl-click numeric-entry controls. It passed:

- Default-on proportional input: (2, 4, 6) to (3, 6, 9).
- One active gesture through zero, then negative Y: (2, 4, 6) to (0, 0, 0) to (-4, -8, -12).
- Checkbox off/on, with an unlinked X edit producing (3, 4, 6).
- Independent editing of an initially zero axis and uniform recovery from an all-zero scale.
- Rejection of a linked result that would overflow a float.
- Another 2,400 frames of width changes with the longer label and checkbox: minimum axis width 41px, maximum inter-axis difference 1px, stable distinct input IDs, and aligned rows.

Independent source review accepted the gesture lifecycle, numeric precision, undo hook placement, finite checks, and queued mutation handling. The replay uses inert engine/undo dependencies, so it validates native ImGui interaction and resulting queued values, not real engine undo execution. No repository tests were added or modified. Evidence is in `logs/linked-scale-native-interactions.log` and `logs/linked-scale-width-replay.log` under the existing task evidence root.

The final editor build for Translation and linked scale passed with 0 warnings and 0 errors in 10.94 seconds (`dotnet build XREngine.Editor/XREngine.Editor.csproj --no-restore -m:1 -nr:false -v minimal`). The build log is `logs/linked-scale-editor-build.log` in the task evidence root.
