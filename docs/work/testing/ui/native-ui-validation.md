# Native UI Validation

## Scope
This document owns native UI runtime, visual, performance, backend, editor workflow, and readiness checks. It covers OpenGL and Vulkan native UI behavior. It does not hold implementation backlog.

Design: [Native UI retained rendering](../../design/ui/native-ui-retained-rendering-design.md)  Architecture: [UI architecture](../../../architecture/ui/README.md); [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)  Code todos: [Native UI retained performance and editor parity TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)

## Change-Tracking Validation (2026-10-23)

The implemented scope is contracts and instrumentation: runtime identities,
change domains, a bounded transaction journal, ancestor cache invalidation, and
work counters. Worker layout, publication, persistent rendering, and controls
remain open. See [the current contract](../../../architecture/ui/04-Change-Tracking.md).

| Check | Result | Limit |
|---|---|---|
| Runtime rendering and input integration builds | Passed, zero warnings and errors, through `Tools/Invoke-AgentBuild.ps1`, `build`, `--no-restore`. | Uses the installed environment dependencies. |
| Headless test build | Passed, zero warnings and errors, through the build wrapper with `--no-restore`. | Final source state compiled on Linux/.NET 10. |
| Journal and transform contract tests | Passed: 23/23, zero warnings, failures, or skipped tests. | Deterministic headless runtime checks, including child-index replacement. |
| Full headless regression suite | Passed: 167/167 with one test worker, zero warnings, failures, or skipped tests. Repeated after editor restore against the same final test binary. | Covers the tests linked by the existing headless project; not the full Windows test project. |
| Declared editor NuGet restore | Passed through the build wrapper. | No tracked package declarations or lockfiles changed. No separate native dependency or Windows workload installation was performed. |
| Full editor build after restore | Blocked: missing OpenVR.NET and OscCore submodule projects; `MSB9008` and downstream `CS0246`. The build ended with 4 warnings and 207 errors. | Restore resolved the earlier `NETSDK1004`. The editor did not finish compilation. The small `NativeUIElements` notification edits have static review only. |
| Windows editor, OpenGL/Vulkan, visual input, GPU uploads, performance comparison | Not run. | This environment provides headless Linux validation. No live visual or speed result is claimed. |

Final verification ran on 2026-10-23. The editor retry uses `restore XREngine.Editor/XREngine.Editor.csproj` and then
`build XREngine.Editor/XREngine.Editor.csproj --no-restore`, both through
`Tools/Invoke-AgentBuild.ps1`. `Build/Submodules/OpenVR.NET` and
`Build/Submodules/OscCore-NET9` are uninitialized. Their missing projects prevent
`XREngine.Runtime.XR.OpenVR` and `XREngine.Runtime.Net.Osc` from compiling.
No submodule changes were made. This is an environment/source-checkout blocker,
not a successful editor validation.

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
result. Apply the repository's live-first validation rule before adding further
tests during feature-regression debugging or integration awaiting validation.

## Setup
Use the ImGui editor as the default editor until the native UI readiness checks pass. The relevant VS Code tasks are `Build-Editor`, `Build-Editor-Fast`, `Start-Editor-NoDebug`, and `Start-Editor-RendererDevelopment-NoDebug`. The relevant launch profiles are `Editor (Default World)`, `Editor (Renderer Development)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing World, Validation Layers)`. Use `XRE_WORLD_MODE=UnitTesting` for unit-world checks. Use `XRE_GL_DEBUG=1` for OpenGL debug checks and `XRE_VULKAN_VALIDATION=1` for Vulkan validation checks. Use native UI settings or launch flags only after the implementation exposes them.

## Checks

### Layout Invalidation
Architecture: [UI architecture](../../../architecture/ui/01-Architecture.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build editor after layout changes. | Use the [agent build workflow](../../../developer-guides/ai/agent-editor-workflows.md#build-run-debug) for `XREngine.Editor`. | The editor builds without new warnings. | Planned | none |
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
| Check failure diagnostics. | Force unsupported native UI batch, accelerated, and backend cases. | Failures are explicit and actionable. Silent CPU or generic visual fallback does not hide errors. | Planned | none |
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

| Backend | Required checks | Status | Last evidence |
|---|---|---|---|
| OpenGL | Layout, input, batching, clipping, text, composition, and native editor workflow checks. | Planned | none |
| Vulkan | Layout, input, batching, clipping, text, composition, and native editor workflow checks with validation enabled. | Planned | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
