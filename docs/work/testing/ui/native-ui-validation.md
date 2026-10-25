# Native UI Validation

## Scope
This document owns native UI runtime, visual, performance, backend, editor workflow, and readiness checks. It covers OpenGL and Vulkan native UI behavior. It does not hold implementation backlog.

Design: [Native UI retained rendering](../../design/ui/native-ui-retained-rendering-design.md)  Architecture: [UI architecture](../../../architecture/ui/README.md); [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)  Code todos: [Native UI retained performance and editor parity TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)

## Retained UI Integration (2026-10-23)

Latest continuation check (2026-10-25): headless build169 passed with zero
warnings/errors, and run112 passed **1,112/1,112** tests with no failures or skips.
The output-swap wheel regression failed before the repair and passed afterward.
The dock detach regression also failed before repair; six cases now cover
reparenting, transform replacement, lost/destroyed slots, and normal detachment.
Failed rebuilds also retire their partial controls; both new gate cases failed
before the repair. Menu lifetime and scroll-navigation regressions also failed
before repair. Initial submenu-focus regressions also failed before the shared
request replaced its old retry. Desktop test build41 compiled the editor and test assembly with zero
warnings/errors. The new `NativeToolPanelStateTests` compiled but did not execute
on this Linux host. Check console category/auto-scroll, profiler view, and settled
scroll saves through the active dock-tree host on Windows. See the
[continuation comparison](../../investigations/ui/native-ui-continuation-comparison.md)
for the preserved local snapshot, source differences, and exact validation limits.

The expanded implementation is under integration. The user explicitly approved
new headless regression tests before Windows and graphics validation on
2026-10-23. This approval applies to this task. It does not establish live
acceptance or change the default editor.

The earlier headless snapshot passed compilation with zero warnings/errors through
`Tools/Invoke-AgentBuild.ps1`. Headless build141 took 38.98s. Full run90 passes
**979 of 979 tests**, with no warnings, failures, or skips, in 15.220s. Coverage
includes paged composition claims, throwing-observer invalidation, bounded
profiler output pages, queued object identities, channel value helpers, configured
owned-clone admission, and the earlier retained layout/input/render contracts.

The frozen checked source snapshot contains 10,763 files and stayed unchanged
through the checks. Matching editor build61 passed with zero warnings/errors in
53.89s. Desktop test build34 passed with zero warnings/errors in 58.93s.
Desktop inspector request, animation input/Undo, native Duplicate/menu, and clone
replay fixtures remain compile-only in this environment; no Windows execution
is claimed. Their runtime checks must include callback reentry, history pruning,
source changes, and selection restoration on the supported desktop host.

The supported Vulkan static-cache contract is implemented, but native cache hits,
fence retirement, image/font changes, and output/clip changes still require GPU
acceptance. The explicit OpenGL world route also requires cold-start, resize,
context replacement, failed-output, and accepted-input tests on a native window.
Editor animation/Undo and profiler-specific fixtures remain compile-only here.
See the [investigation record](../../investigations/ui/retained-ui-integration.md)
for intermediate failures and repairs. Windows, OpenGL/Vulkan, visual, IME, native
resource, benchmark, and equal-workload ImGui acceptance remain open.

## Scrollable Menu Keyboard Checks (2026-10-25)

Headless tests use accepted layout geometry and a presentation probe. They cover
End/Home scrolling, separator and disabled-row spacing, repeated pending arrows,
newer focus intent, lifetime cancellation, scroll-callback reentry, and the
120-tick retry limit. The probe permits focus only when the test allows it; this
does not establish real window presentation or keyboard-device behavior.

On Windows, open a menu taller than its viewport. Include disabled rows and a
separator. Use arrows, Home, and End. Confirm that the intended row scrolls into
view and gets focus only after it is presented. Repeat keys while layout is
pending. Move focus to another control, hide/reopen the menu, and deactivate its
owner while focus is pending. Confirm that the old request does not steal focus.
Check focus outlines and both mouse and keyboard opening. Repeat focus-intent
cancellation checks after Right opens a submenu, before its first row is
presented. Mouse opening must cancel pending keyboard focus. Right/Left/Escape
behavior still requires native-window acceptance.

## Hierarchy And Panel State Validation (2026-10-25)

This run checks that moving the first editor-only child to the root allocates the
editor scene, and that asset drops accept a parent in a hidden scene. These editor
changes have no headless test path.

| Check | Result | Limit |
|---|---|---|
| `TheFirstPromotedEditorChildAllocatesTheEditorScene` | Compiled with 0 warnings and 0 errors. Not executed. | On Linux, VSTest discovers no tests in the Windows desktop test assembly. Run the test on Windows. |
| `NativeHierarchyAssetSpawnTests` | Compiled with 0 warnings and 0 errors. Not executed. The tests check that a prefab spawns under a hidden-scene parent, stays out of the world, and supports undo and redo. They also check that destroyed and foreign parents are rejected. | Same VSTest discovery limit. Run the tests on Windows. Model drops use the same target check but have no test. |
| Profiler and console view persistence | Headless passed: `NativeDockLayoutTests` checks that an unknown profiler view falls back to CPU Timings, an unknown console category shows all categories, and the view, category, and auto-scroll values survive JSON and sanitize. Full headless suite passed 1050/1050. The panel restore path compiled with 0 warnings and 0 errors. | The panel restore and save callbacks did not run. Check them in the editor on Windows. |
| Scroll save coalescing | Headless passed: `NativeSaveCoalescerTests` checks that repeated requests save once after the last request settles, and that an immediate take clears the request once. Full headless suite passed 1052/1052. | The editor tick and teardown flush did not run. Check on Windows that wheel scrolling writes the preference file once after scrolling stops. |
| Shared scene/tools splitter and debounced split saves | Editor and desktop test compilation passed with 0 warnings and 0 errors. | No runtime check. On Windows, drag the scene/tools splitter and a dock split, use the arrow keys on the focused splitter, and confirm that the size persists after a restart. |
| Editor text field focus outline | Editor compilation passed with 0 warnings and 0 errors. The outline now stays while the field has keyboard focus, after the pointer leaves. | No runtime check. On Windows, click a property field, move the pointer away, and confirm that the outline stays until focus moves. |
| Hierarchy menu parity | Source review: the native node menu has Rename, Add Child, Focus Camera, Duplicate, Deep Duplicate, and Delete. The scene menu has the visibility toggle and Unload Scene. These match the ImGui menus. | No native interaction was run. |

## Text Pointer Caret Validation (2026-10-25)

This run checks pointer caret placement and drag selection in text fields. It used
the same Linux SDK setup and sequential `Tools/Invoke-AgentBuild.ps1` builds as the
runs below. The changed source files were hashed before the builds and were
unchanged after them.

| Check | Result | Limit |
|---|---|---|
| Text pointer caret tests | Passed. `UITextPointerCaretTests` checks the nearest line and nearest grapheme boundary, press and drag selection from the press packet, and rejection after an edit or for another field. `TextCaretSelectionAndButtonHoverPreserveChromeSegments` checks that a real capture freezes the typed text's layout in the field hit. | Headless; synthetic pointer calls, not native mouse input. |
| Full headless regression suite | Passed: 1045/1045, no warnings, failures, or skips. | The headless project only. |
| Editor and desktop unit test compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only; no Windows test executed. |
| Native mouse, IME, bidirectional and fallback-font caret checks | Not run. | The text and IME row of the acceptance matrix stays open. |

## Collector Page Reuse Validation (2026-10-24)

This run checks page reuse in `UIBatchCollector`. It used the same Linux SDK setup
as the sparse capture run below. All builds used `Tools/Invoke-AgentBuild.ps1` with
`--no-restore`, in sequence. The changed source files were hashed before the builds
and were unchanged after them.

| Check | Result | Limit |
|---|---|---|
| Headless test build | Passed: 0 warnings, 0 errors. | Linux, SDK 10.0.113 with Roslyn 5.9. |
| Collector and sparse publication tests | Passed. `BatchCollectorVisitsOnlyChangedRecordPages` collects a 2000-row canvas, then checks that a color edit and a hidden row each visit one record page (at most 256 records), that the hidden row's primitive slot is free, and that the runs equal a fresh collection. `DiscardedCollectionKeepsCommittedRunsUntilSwap` checks the committed runs after a discard. A deliberate fault that kept dropped pages made two tests fail; the source was then restored and rebuilt. | Counter-based work bounds, not time measurements. |
| Full headless regression suite | Passed: 1043/1043, no warnings, failures, or skips. | The headless project only. |
| Editor Windows-target compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only. |
| Desktop unit test project compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only; no Windows test executed. |
| Windows editor, OpenGL/Vulkan, visual, performance comparison | Not run. | No live, visual, or speed result is claimed. Run-boundary changes at page edges need a visual check on the supported host. |

## Sparse Publication Splice Validation (2026-10-24)

This run checks the count-changing splice of record stores. It used the same Linux
SDK setup as the sparse capture run below. All builds used
`Tools/Invoke-AgentBuild.ps1` with `--no-restore`, in sequence. The changed source
files were hashed before the builds and were unchanged after them.

| Check | Result | Limit |
|---|---|---|
| Headless test build | Passed: 0 warnings, 0 errors. | Linux, SDK 10.0.113 with Roslyn 5.9. |
| Store, segment, and sparse publication tests | Passed. `CountChangingEditsCopyOnlyTouchedRecordPages` bounds copied record cells to fewer than two pages and checks that all other pages are shared. A deliberate fault in the render splice merge order made `RecapturedPanelKeepsSurvivingRecordsInPlace` fail; the source was then restored and rebuilt. | Counter-based work bounds, not time measurements. |
| Full headless regression suite | Passed: 1041/1041, no warnings, failures, or skips. | The headless project only. |
| Editor Windows-target compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only. |
| Desktop unit test project compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only; no Windows test executed. |
| Windows editor, OpenGL/Vulkan, visual, performance comparison | Not run. | No live, visual, or speed result is claimed. |

## Sparse Publication Capture Validation (2026-10-24)

This run checks the sparse publication capture increment. It ran on Linux with
the installed .NET SDK 10.0.113, not the SDK pinned in `global.json`. The run used
a scratch `global.json`, `MSBuildEnableWorkloadResolver=false`, and the Roslyn 5.9
`Microsoft.Net.Compilers.Toolset` props through `CustomBeforeMicrosoftCommonProps`.
The SDK compiler reports `CS0110` for `OpenXrRenderPacingMode`; Roslyn 5.9 does not.
All builds used `Tools/Invoke-AgentBuild.ps1` with `--no-restore`, in sequence.
The changed source files were hashed before the builds and were unchanged after them.

| Check | Result | Limit |
|---|---|---|
| Headless test build | Passed: 0 warnings, 0 errors. | Linux, SDK 10.0.113 with Roslyn 5.9. |
| Sparse publication tests | Passed: 9/9. A deliberate fault in the splice start shift made one test fail; the source was then restored and rebuilt. | Counter-based work bounds, not time measurements. |
| Full headless regression suite | Passed: 988/988, no warnings, failures, or skips, in 17.5s. | The headless project only. |
| Editor Windows-target compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. OpenVR.NET and OscCore were initialized at their tracked commits. | Compilation only. |
| Desktop unit test project compilation | Passed: 0 warnings, 0 errors, with `EnableWindowsTargeting=true`. | Compilation only; no Windows test executed. |
| Windows editor, OpenGL/Vulkan, visual, IME, performance comparison | Not run. | No live, visual, or speed result is claimed. |

## Published Change-Tracking Validation (2026-10-23)

The earlier published scope was contracts and instrumentation: runtime identities,
change domains, a bounded transaction journal, ancestor cache invalidation, and
work counters. The table below records that source state. It does not describe
the later integration work. See [the change-tracking contract](../../../architecture/ui/04-Change-Tracking.md).

| Check | Result | Limit |
|---|---|---|
| Runtime rendering and input integration builds | Passed, zero warnings and errors, through `Tools/Invoke-AgentBuild.ps1`, `build`, `--no-restore`. | Uses the installed environment dependencies. |
| Headless test build | Passed, zero warnings and errors, through the build wrapper with `--no-restore`. | Final source state compiled on Linux/.NET 10. |
| Journal and transform contract tests | Passed: 23/23, zero warnings, failures, or skipped tests. | Deterministic headless runtime checks, including child-index replacement. |
| Full headless regression suite | Passed: 167/167 with one test worker, zero warnings, failures, or skipped tests. Repeated after the audio component cleanup and a fresh wrapper build. | Covers the tests linked by the existing headless project; not the full Windows test project. |
| Declared editor NuGet restore | Passed through the build wrapper. | The approved audio cleanup replaces only the NAudio umbrella reference with same-version component references. No native dependency or Windows workload installation was performed. |
| Pinned submodule initialization | Passed for OpenVR.NET and OscCore. Both submodules and their engine adapters compiled. | Exact tracked gitlink commits were used. No submodule bumps or declarations changed. |
| Bootstrap Windows cross-compilation on Linux | Passed after removing the temporary `EnableWindowsTargeting` setting: 0 warnings, 0 errors. | The complete project graph built through the wrapper with `--no-restore`. Target remains `net10.0-windows7.0`. |
| Editor managed Windows-target compilation on Linux | Passed through the wrapper with `--no-restore`: 0 warnings, 0 errors. | Includes the `NativeUIElements` changes. No source exclusions. Does not establish Linux execution or Windows runtime behavior. |
| Windows target and dependency checks | Both projects retain `net10.0-windows7.0`. Audio, Bootstrap, and editor graphs retain the required audio components at their prior versions and contain no WinForms or Windows Desktop framework requirement. | Editor runtime configuration now requires only `Microsoft.NETCore.App`. Windows native audio APIs remain; this is a compile check, not a deployment package. |
| Audio component cleanup | Audio adapter build passed with zero warnings and errors. All 11 restored consumer graphs retain the required five components and have no Forms/Desktop framework requirement. | Versions remain 2.3.0, with SDL2 at 2.2.6. Server restore needs its separate Windows SDK targeting override. |
| Existing desktop audio tests | Not run: compilation succeeded, but NUnit rejected the 49 discovered tests because the assembly targets Windows7.0. Zero tests executed. | The command's zero exit code is not counted as a test pass. No test changes or platform-check bypass. |
| Windows editor, OpenGL/Vulkan, visual input, GPU uploads, performance comparison | Not run. | This environment provides headless Linux validation. No live visual or speed result is claimed. |

Final verification ran on 2026-10-23. Normal editor restore resolved the initial
`NETSDK1004`. The next editor build exposed two uninitialized submodules. The
repository README supports `git submodule sync` and `git submodule update --init`.
Those commands were scoped to these two existing paths:

| Submodule | Verified tracked commit |
|---|---|
| `Build/Submodules/OpenVR.NET` | `660933f65beb34384ede7b61b9f21e77614ed6a6` |
| `Build/Submodules/OscCore-NET9` | `2be09497ffa8560b700d4793c3e4d344c49caf49` |

After initialization, `restore XREngine.Editor/XREngine.Editor.csproj` and
`build XREngine.Editor/XREngine.Editor.csproj --no-restore` ran through
`Tools/Invoke-AgentBuild.ps1`. OpenVR.NET, OscCore, `XREngine.Runtime.XR.OpenVR`,
and `XREngine.Runtime.Net.Osc` compiled. That editor attempt then stopped at the
Windows Forms framework reference in `XREngine.Runtime.Bootstrap`. The source was
the Windows dependency group of NAudio 2.3.0, not editor Forms controls.
A temporary `EnableWindowsTargeting` setting fixed that reference-pack failure.
The approved audio cleanup then replaced the NAudio umbrella package with its
required Core, WinMM, Wasapi, and Asio components at 2.3.0; SDL2 remains at 2.2.6.
Both MP3 callers use the same ACM decoder through `Mp3FileReaderBase`. The
component split removes WinForms and the need for the temporary setting in
Bootstrap and the editor. Both final builds pass with zero warnings and errors.
See the [audio dependency investigation](../../investigations/platform/linux-windows-desktop-targeting.md)
for the full consumer audit and audio test results. No Windows workload, native
library, credential change, or dependency version change was needed.

The first contract run found a lazy-construction error: an optional constructor
argument did not provide the parameterless constructor required by the default
`LazyInitializer` overload. An explicit static factory fixed it. Review also found
preflight and identity-retirement gaps for direct child-list edits and nested
canvas moves. Targeted runtime checks cover those paths.

Use the repository build wrapper to build
`XREngine.UnitTests/Headless/XREngine.HeadlessTests.csproj --no-restore`.
Then run the built test executable without a build:

```text
dotnet XREngine.UnitTests/Headless/bin/Debug/net10.0/XREngine.HeadlessTests.dll --test=XREngine.UnitTests.Rendering.UIChangeJournalTests,XREngine.UnitTests.Rendering.UIChangeTrackingTests --result=<validation-run>/logs/ui-contracts.xml
```

Create `<validation-run>` under `Build/_AgentValidation/` with the repository
retention workflow. The targeted tests check coalescing, nested scopes, overflow,
short drains, tombstones, foreign-thread rejection, local cache invalidation,
canvas moves, child order and replacement, and warmed journal/counter allocations.
The zero-allocation assertions apply only to journal record/drain and counter
add/copy. They do not cover layout, rendering, transactions, or tree construction.

Live acceptance remains open: use the existing FPS text path, change one size,
color, transform, visibility state, and child order, then compare output and input
with the prior behavior. Check counter deltas with recording enabled. Confirm
that disabled counters do not alter behavior. Check nested canvases, destruction,
and component construction. Record backend, build, scene, and workload with each
result. The user has approved headless test work during this integration before
live validation, as recorded above.

## Setup
Use the ImGui editor as the default editor until the native UI readiness checks pass. The relevant VS Code tasks are `Build-Editor`, `Build-Editor-Fast`, `Start-Editor-NoDebug`, and `Start-Editor-RendererDevelopment-NoDebug`. The relevant launch profiles are `Editor (Default World)`, `Editor (Renderer Development)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing World, Validation Layers)`. Use `XRE_WORLD_MODE=UnitTesting` for unit-world checks. Use `XRE_GL_DEBUG=1` for OpenGL debug checks and `XRE_VULKAN_VALIDATION=1` for Vulkan validation checks. Use native UI settings or launch flags only after the implementation exposes them.

## Checks

### Layout Invalidation
Architecture: [UI architecture](../../../architecture/ui/01-Architecture.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build editor after layout changes. | Use the [agent build workflow](../../../developer-guides/ai/agent-editor-workflows.md#build-run-debug) for `XREngine.Editor`. | The editor builds without new warnings. | Passed: managed Windows-target compilation on Linux | 2026-10-23; zero warnings/errors. Windows execution remains open. |
| Check unchanged layout frames. | Run a native UI scene with no input or visual changes and inspect layout counters or profiler scopes. | No measure or arrange traversal runs for unchanged canvases. | Planned | none |
| Check local layout invalidation. | Change one leaf size, one visual-only property, and one scrolling offset in focused scenarios. | Size changes visit only required branches. Visual-only changes do not run layout. Scrolling does not traverse unrelated branches. | Planned | none |
| Check construction transactions. | Build hierarchy, inspector, menus, and docking layouts while a transaction is active. | Construction publishes one coalesced layout update. | Planned | none |

### Input And Hit Testing
Architecture: [UI architecture](../../../architecture/ui/01-Architecture.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check idle pointer work. | Run a native UI scene with a still pointer and inspect hit-test counters or profiler scopes. | Idle frames do not run quadtree queries or intersection-set diffs. | Planned | none |
| Check pointer transitions. | Move the pointer across nested, overlapping, clipped, hidden, collapsed, detached, and input-transparent controls. | Enter, leave, directly-over, focus, click, drag, context-menu, and camera-blocking transitions are correct. | Planned | none |
| Check pointer allocation. | Run allocation instrumentation around pointer movement. | Pointer movement allocates zero managed bytes. | Planned | none |

### Batch Primitive And Clipping Contracts
Architecture: [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check primitive compatibility. | Render solid, border, image, text, and custom-material controls with batching enabled. | Compatible primitives batch. Incompatible custom materials keep their own semantics. | Planned | none |
| Check painter order. | Render alternating compatible and incompatible translucent controls. | Painter order remains deterministic and visually correct. | Planned | none |
| Check clipping. | Render nested lists, scroll views, and clipped text on OpenGL and Vulkan. | Visual clipping and interactive hit regions match. | Planned | none |

### Persistent Render Records
Architecture: [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check unchanged visual trees. | Run a static native UI scene and inspect batch and upload counters. | No logical batch rebuild and no UI buffer commit occurs. | Planned | none |
| Check dirty records. | Change one color, one transform, one visibility state, and one primitive order. | Only affected record fields, slots, or topology runs update. | Planned | none |
| Check stale handle safety. | Activate, deactivate, remove, and recycle UI primitives while frames are in flight. | Detached or recycled handles cannot draw stale data. | Planned | none |

### Text And Glyph Publication
Architecture: [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check unchanged text. | Run static text and inspect glyph layout, glyph copy, and upload counters. | Unchanged text performs no glyph copy, layout, or upload. | Planned | none |
| Check text instance changes. | Change text color, transform, outline, atlas, and one text string. | Instance-only changes do not rebuild glyph geometry. One edited string does not repack unrelated text. Atlas changes move only affected text. | Planned | none |
| Check text visual parity. | Render text outline, wrapping, alignment, clipping, and non-batched text on OpenGL and Vulkan. | Batched and non-batched text are visually correct. | Planned | none |

### Virtual Lists And Trees
Architecture: [UI architecture](../../../architecture/ui/01-Architecture.md); [Native hierarchy feature guide](../../../developer-guides/ui/native-hierarchy-panel.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check row bounds. | Render empty, small, deeply nested, wide, and very large logical lists and trees. | Realized row count is bounded by viewport rows plus overscan. | Planned | none |
| Check scroll and mutation. | Scroll, select, hover, expand, collapse, rename, insert, remove, and move items. | Row scene subtrees are not destroyed for ordinary scroll and selection. Source identity and UI state stay correct. | Planned | none |
| Check hierarchy workflow. | Use scene sections, unassigned roots, editor-scene visibility, dirty state, drag/drop, context menus, rename, and multi-selection. | The native hierarchy behavior matches the supported editor workflow. | Planned | none |

### GPU-Efficient Composition
Architecture: [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check shared primitives. | Render common editor controls with native UI batching. | Controls use shared primitive pipelines and persistent instance data. | Planned | none |
| Check panel backgrounds. | Open panels, menus, and fields with default styling. | Default chrome uses no per-panel grab pass. Blur is opt-in and shared by compatible layers. | Planned | none |
| Check static and dynamic layers. | Update an FPS counter, caret, hover, and selection over static editor chrome. | Dynamic records update without invalidating static command topology. | Planned | none |
| Check bounded Vulkan chrome reuse. | Mark a solid screen-space subtree as `StaticChrome`; edit a separate overlay, then change the chrome, clip, backend, and mesh. Repeat with resource/debug/output-sensitive cache policies. | Compatible chrome reuses secondary commands while each current receipt proves actual execution. Changed owners and restricted policies reject reuse. No stale buffers or skipped required output occur. | Planned | none |

### Editor Feature Parity
Architecture: [UI architecture](../../../architecture/ui/README.md); [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check native editor workflows. | Use hierarchy, inspector, scene view, asset browser, console, profiler, toolbar, menus, docking, edit, play, import, drag/drop, undo, and save workflows. | Native panels support the normal workflows expected from the ImGui editor. | Planned | none |
| Check panel lifecycle. | Open, close, rebuild, and transition through play mode for panels with external callbacks or resources. | Teardown deactivates child subtrees and unregisters input, render, watcher, audio, and streaming callbacks. State that must persist stays stable. | Planned | none |
| Check backend parity. | Run the same native editor workflow on OpenGL and Vulkan. | Native UI behavior and visuals are correct on both backends. | Planned | none |

### Default-Path Readiness
Architecture: [UI architecture](../../../architecture/ui/README.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Check default switch readiness. | Review code, validation results, and editor settings before changing the default editor type. | Native UI is the default only after all readiness checks pass. ImGui remains an explicit compatibility or debug option. | Planned | none |
| Check failure diagnostics. | Force unsupported native UI batch, accelerated, and backend cases. | Failures are explicit and actionable. Each forced case logs its `UIFailureDiagnostics` code (see the code table in retained execution) and increments `FailuresReported`. Silent CPU or generic visual fallback does not hide errors. | Planned | none |
| Check documentation readiness. | Review UI architecture, feature, workflow, debugging, and validation docs. | Documentation matches implemented contracts and avoids baseline-recording requirements. | Planned | none |

## First Milestone: Async Retained UI

Scope: opt-in FPS text, a common-control gallery, and a fixed-height virtual
list. Full editor parity and a default-editor switch remain later checks.
All rows below are **Planned; last evidence: none**. The design review ran no
build, runtime, visual, or performance checks.

### Workloads and acceptance

| Check | Procedure | Required result |
|---|---|---|
| FPS path | Use `AddFPSText` with native batching over a static gallery; update only FPS text. | Only that text run and dependent auto-size roots change. Unrelated glyph ranges, layout roots, and batch topology stay cached. |
| Idle retained UI | Freeze data and pointer after warm-up; hold the gallery and list visible for 60 seconds. | Zero UI measure/arrange visits, glyph copies, logical batch rebuilds, or UI buffer upload bytes. Zero steady-state managed allocations in the native UI subsystem. Report unavoidable host/driver work separately. |
| Dirty domains | Change one size, color, transform, clip, enabled state, and painter order separately. | Visits and uploads match the design's dependency domains. Color does not run layout or rebuild hit data; clipping matches hit eligibility. |
| Worker correctness | Run identical immutable input through workers and the serial diagnostic kernel. Include nested auto-size, grid/list placement, constraints, DPI, and text wrapping. | Equal output within declared float tolerances. No worker touches mutable transforms or invokes authored callbacks. |
| Stale results | Delay/reorder task completion while resizing, editing text, reparenting, deleting, and replacing fonts. Keep FPS visual changes active. | Invalid dependencies reject results; independent valid roots can progress. No mixed generation or starvation from visual-only updates. |
| Bounded tasks | Burst thousands of setters; resize continuously; throttle workers and render consumption. | At most one running and one coalesced pending input per canvas. Declared global worker/page limits hold. Queued age, cancellation, and stale rejection are reported. |
| Coherent input | Click, drag, scroll, and change clips while workers and presentation are delayed. Record rendered, hit, clip, and event generations. | Input targets the displayed generation. Removed/disabled incarnations cannot receive commands. Camera blocking still follows `TopMostInteractable`. |
| Retained draws | Alternate solid, border, image, text, and custom-material primitives with overlap and nested clips. Change one instance or text run. | Painter order and custom semantics match. Dirty byte/range counts exclude unrelated records; retained glyph and atlas leases remain valid. |
| List scale | Use 100,000 then 1,000,000 logical fixed-height items; same viewport, row height, and overscan. Scroll slowly, fling, jump to end, insert/remove, select, edit, and recycle. | Realized rows <= `ceil(H/h)+1+2*o`, plus at most one documented pinned edit row. No all-item scroll scan or subtree recreation. Focus, selection, capture, and edits keep stable IDs. |
| Common controls | Exercise every design-table behavior for buttons, checkboxes, radio groups, scroll regions, dropdowns, sliders, color selectors, numeric fields, and single/multiline text. | Correct keyboard/pointer/disabled behavior; one committed event per action; invalid intermediate numeric text does not corrupt the value. |
| Text and IME | Selection/clipboard/undo, combining marks, surrogate pairs, bidirectional text, fallback fonts, IME preedit/commit/cancel, DPI changes, and recycled editing rows. | Grapheme and composition state remain correct; caret hit position and IME candidate placement match the presented text. List each tested platform/IME. |
| Accessibility | Test keyboard focus order, visible focus, scaling/high contrast, semantic roles/labels/values/actions, screen reader, and virtual-item realization. | Correct behavior for each claimed platform adapter; unsupported adapters are explicitly listed and block a parity claim. |
| Lifetime | Repeatedly create/destroy canvases, recycle rows, change atlases, and exit with tasks/frames pending. Run a 30-minute stress session. | No stale handle use, callbacks after disposal, unsafe buffer reuse, or unbounded retained memory. Include renderer teardown diagnostics. |
| Backend | Repeat the same fixture on OpenGL, then Vulkan as a separate qualification. | Correct image/input per backend; a pass on OpenGL does not mark Vulkan passed. Software-driver results are separate from hardware results. |

### Benchmark protocol

Compare the native fixture with an equivalent ImGui fixture. Use the same data,
visible controls and rows, text/font coverage, clipping, update cadence, input
replay, output size/DPI, backend, quality, and release configuration. Enable
normal ImGui list clipping and normal caching; do not compare against an
intentionally inefficient baseline. Document any visual or feature difference.
A comparison with missing controls or disabled effects is a different workload.

Record fixed fixture and executable revisions, CPU/core count, RAM, GPU/driver,
OS/power mode, worker limits, and validation/profiler settings. Separate cold
construction/font/shader startup from warm frames. Start with 10 seconds warm-up
and 60 seconds collection, at least five fresh processes per variant in alternating
A/B order. Use lightweight counters for timing and a separate allocation trace
so instrumentation cost is visible. Do not treat this document as evidence that
a benchmark executable or CLI option already exists.

Record these counters for idle, 1% and 10% dirty controls, full resize, FPS-only,
typing/IME, pointer movement, list scrolling, and simultaneous scene load:

- Main/UI owner, layout worker, render submission, and total process CPU time;
  keep total CPU in core-milliseconds per frame, not elapsed wall time.
- Frame time p50/p95/p99 and event-to-visible-output p50/p95/p99 latency, including
  snapshot capture, queue wait, layout, publication, and present delay.
- Managed allocations, peak and steady retained bytes, snapshot pages, running
  and pending tasks, cancellation count, stale rejects, and oldest queued work.
- Cache requests/hits for measure, arrange, text, and hit queries; report both
  counts and hit rate so an idle 0/0 sample is not shown as 100%.
- Elements visited, logical and realized rows, rows rebound, draw runs, glyph
  copies/repacked ranges, logical dirty bytes, actual GPU upload bytes/ranges,
  and buffer capacity. Measure GPU/raster cost separately where available.

Provisional first-milestone targets at 1280x720, 60 Hz, on a named agreed desktop
CPU: p95 native UI owner work <=1 ms for a warm 1,000-control gallery; p95
input-to-visible change <=2 displayed frames for async layout edits. Visual-only
changes should appear in the next available frame. These are targets, not
measurements or claims for every CPU. Use a 40-row viewport with overscan 8 per
side for the list comparison; include partial-row cases in the capacity bound.
Increasing logical item count from 100,000 to 1,000,000 must not increase realized
row count or cause per-scroll O(N) work. Report timing variance and provider costs.

Acceptance requires the correctness rows first. Report native-minus-ImGui main
and total CPU, allocations, upload bytes, and latency per workload. A lower main
thread time with more worker CPU or extra input delay is a tradeoff, not an
unqualified speed win. Claim a measured improvement only for an equal workload
with repeated results outside observed run variance. No fixed speedup is promised.
If a provisional budget fails, record the failure and decide whether to improve
the code or narrow the supported workload. Do not silently relax the budget.

## Hardware Matrix

For opt-in backdrop blur, use at least two panels with different strengths in
one screen canvas. Verify one shared prefix capture, correct viewport origin,
clamped edges, and unchanged panel order. Check resize, multiple windows,
context replacement, hidden consumers, and delayed fences. Force a missing or
duplicate consumer, copy failure, and output-target replacement. Each required
failure must suppress presentation and input publication. Verify that pending
or uncertain surfaces stay within the four-slot/128 MiB pool without early
reuse. Check disabled blur adds no full render-record scan. Unsupported
backends, world/offscreen canvases, and visible `PreRender` draws must report
the rejected contract. These native checks remain pending.

| Backend | Required checks | Status | Last evidence |
|---|---|---|---|
| OpenGL | Layout, input, batching, clipping, text, composition, and native editor workflow checks. | Planned | none |
| Vulkan | Layout, input, batching, clipping, text, composition, and native editor workflow checks with validation enabled. | Planned | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
