# Native UI Validation

## Scope
This document owns native UI runtime, visual, performance, backend, editor workflow, and readiness checks. It covers OpenGL and Vulkan native UI behavior. It does not hold implementation backlog.

Architecture: [UI architecture](../../../architecture/ui/README.md); [UI rendering pipeline](../../../architecture/ui/03-Rendering-Pipeline.md)  Code todos: [Native UI retained performance and editor parity TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)

## Setup
Use the ImGui editor as the default editor until the native UI readiness checks pass. The relevant VS Code tasks are `Build-Editor`, `Build-Editor-Fast`, `Start-Editor-NoDebug`, and `Start-Editor-RendererDevelopment-NoDebug`. The relevant launch profiles are `Editor (Default World)`, `Editor (Renderer Development)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing World, Validation Layers)`. Use `XRE_WORLD_MODE=UnitTesting` for unit-world checks. Use `XRE_GL_DEBUG=1` for OpenGL debug checks and `XRE_VULKAN_VALIDATION=1` for Vulkan validation checks. Use native UI settings or launch flags only after the implementation exposes them.

## Checks

### Layout Invalidation
Architecture: [UI architecture](../../../architecture/ui/01-Architecture.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build editor after layout changes. | `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` | The editor builds without new warnings. | Planned | none |
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
Architecture: [UI architecture](../../../architecture/ui/README.md); [Native hierarchy porting plan](../../design/ui/native-hierarchy-porting-plan.md)

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

## Hardware Matrix

| Backend | Required checks | Status | Last evidence |
|---|---|---|---|
| OpenGL | Layout, input, batching, clipping, text, composition, and native editor workflow checks. | Planned | none |
| Vulkan | Layout, input, batching, clipping, text, composition, and native editor workflow checks with validation enabled. | Planned | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
