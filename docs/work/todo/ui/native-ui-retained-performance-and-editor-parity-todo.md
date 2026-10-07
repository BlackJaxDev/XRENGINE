# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native hierarchy porting plan](../../design/ui/native-hierarchy-porting-plan.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State
The native UI uses `UILayoutSystem`, `UICanvasInputComponent`, `UIBatchCollector`, `UITextComponent`, and editor panels under `XREngine.Editor/UI`. Code verification shows that `InvalidateCanvasLayout` still invalidates descendants, `UIBoundableTransform.GetMaxChildWidth` and `GetMaxChildHeight` still use LINQ, `UICanvasInputComponent` still lacks input and hit-region generations, `UIBatchCollector` still rebuilds batch data and logs `FpsTextDiag` paths, `HierarchyPanel` still uses `RemakeChildren`, `Transform.Clear`, a 2,000 row limit, and debug-log-only asset drops, and `UIGridChildPlacementInfo` still throws `NotImplementedException`.

## Open Code Items

### Layout Invalidation
- [ ] Introduce explicit dirtiness for measure, arrange, transform, and canvas-bounds changes. `UILayoutSystem`, `UICanvasTransform`, and `UITransform`. Done when ordinary child property changes do not invalidate the full canvas.
- [ ] Track dirty layout roots per canvas. `UILayoutSystem`. Done when layout visits only required measure ancestors and affected arrange branches.
- [ ] Limit full descendant invalidation to canvas-bounds, DPI, scale, or equivalent global changes. `UILayoutSystem.InvalidateCanvasLayout`. Done when scrolling and visual-only changes do not schedule full descendant invalidation.
- [ ] Add a scoped layout mutation transaction. `UICanvasTransform` and `UILayoutSystem`. Done when nested transactions coalesce panel, menu, hierarchy, inspector, and docking construction into one layout publication.
- [ ] Remove direct forced layout calls from panel rebuild paths. `XREngine.Editor/UI`. Done when transaction completion guarantees valid layout before publication.
- [ ] Replace LINQ in `UIBoundableTransform.GetMaxChildWidth` and `GetMaxChildHeight`. `UIBoundableTransform`. Done when these methods use allocation-free loops.
- [ ] Audit layout hot paths for captured delegates, boxing, temporary arrays, string formatting, and non-struct enumerators. UI transform classes. Done when steady-state layout paths allocate zero managed bytes.
- [ ] Remove active `OnResizeChildComponents` layout paths after measure and arrange own all behavior. UI transform classes. Done when no active override drives normal layout.
- [ ] Reassess or remove the async layout coroutine. UI layout classes. Done when synchronous incremental layout is the canonical path and no competing algorithm remains.

### Input And Hit Testing
- [ ] Replace intersection-set transition work with an allocation-free identity diff. `UICanvasInputComponent`. Done when pointer movement performs no managed allocation and enter/leave transitions stay correct.
- [ ] Confirm `RenderInfo2D` identity equality and ordering semantics. `UICanvasInputComponent` and render records. Done when membership checks cannot lose items because mutable render-order fields changed.
- [ ] Replace blocker filtering temporary lists with reusable or in-place filtering. `UICanvasInputComponent`. Done when input blocking allocates no managed memory in steady state.
- [ ] Track pointer and UI hit-region generations. `UICanvasInputComponent` and `RenderInfo2D`. Done when idle pointer frames reuse the previous hit result.
- [ ] Increment UI hit-region generation only for visibility, eligibility, clip, painter order, transform, or bounds changes. UI render and input integration. Done when decorative or visual-only updates do not rebuild hit state.
- [ ] Avoid hit testing non-interactive decorative primitives unless they block input. UI components. Done when hit tests skip decoration by default.
- [ ] Preserve pointer capture and drag movement without rebuilding unrelated hit state. `UICanvasInputComponent`. Done when capture and drag tests pass.
- [ ] Keep `TopMostInteractable` as the camera-input blocking contract. `UICanvasInputComponent` and camera input code. Done when overlapping UI and camera input coexist correctly.

### Batch Primitive And Clipping Contracts
- [ ] Define explicit native UI primitive kinds for solid quad, border, image, and text. UI render components and `UIBatchCollector`. Done when batch participation is opt-in through primitive contracts.
- [ ] Define a complete UI batch key. `UIBatchCollector`. Done when primitive type, shader variant, texture or atlas, sampler, blend/depth state, clip mode, render pass, and required material features affect compatibility.
- [ ] Route custom shader materials through normal render commands unless they declare compatible batch semantics. `UIMaterialComponent` and `UIBatchCollector`. Done when custom textureless shaders do not become generic solid quads.
- [ ] Convert outline controls to an explicit border primitive or keep them unbatched. `NativeUIElements`. Done when outline rendering is correct with batching enabled.
- [ ] Prevent missing `MatColor` from becoming a magenta generic batch instance. `UIBatchCollector`. Done when diagnostics are explicit and rendering is not silently wrong.
- [ ] Preserve stable painter-order runs across compatible and incompatible primitives. `UIBatchCollector`. Done when mixed primitive tests keep visual order.
- [ ] Define hierarchical clipping independent of material identity. UI render records. Done when resolved clip rectangles or handles flow with each record.
- [ ] Support batched rectangular clipping and scissor fallback. `UIBatchCollector` and render backends. Done when nested lists and text clip correctly on OpenGL and Vulkan.

### Persistent Render Records
- [ ] Give batchable primitives stable render handles with generations. UI render components. Done when stale handles cannot draw detached or recycled elements.
- [ ] Register and unregister handles on activation and deactivation. `UIRenderableComponent` and related components. Done when normal frames do not rediscover all batch membership.
- [ ] Store primitive records in persistent pools with free-list reuse. `UIBatchCollector`. Done when record addition, removal, and reuse are generation-safe.
- [ ] Separate topology, transform, visual, clip, and visibility dirty domains. UI render records. Done when color-only and transform-only changes update only instance data.
- [ ] Replace per-frame `UIBatchCollector` clear and re-add behavior. `UIBatchCollector`. Done when unchanged visual trees perform no logical batch rebuild.
- [ ] Add a generation-aware change queue for renderable operations. UI render pipeline. Done when unchanged retained UI avoids a flat all-renderables screen-space walk.
- [ ] Keep persistent instance buffers with stable slot ownership. OpenGL and Vulkan UI backends. Done when only dirty slot ranges upload.
- [ ] Coalesce dirty slots into bounded upload ranges. OpenGL and Vulkan UI backends. Done when buffer growth does not republish unused capacity on normal frames.
- [ ] Preserve reusable backend command buffers for compatible static UI layers. Rendering backends. Done when dynamic overlays do not invalidate static editor chrome.

### Text And Glyph Publication
- [ ] Replace per-frame glyph-list array copies with versioned published glyph payloads. `UITextComponent`, `UIText`, and `UIBatchCollector`. Done when unchanged text performs no glyph copy.
- [ ] Use pooled or owned contiguous glyph storage with safe lifetime. Text components. Done when render snapshots can consume glyph data across update, collect, and render threads.
- [ ] Publish glyph payloads only when text layout inputs change. Text components. Done when color, transform, and outline instance changes do not rebuild glyph geometry.
- [ ] Add stable text instance slots and reusable glyph ranges. `UITextComponent` and `UIBatchCollector`. Done when editing one text control does not repack unrelated text.
- [ ] Preserve atlas identity in batch keys and move only affected text when an atlas changes. Text batching. Done when atlas updates touch only affected text.
- [ ] Cache shaping and layout results by actual glyph placement inputs. Text layout code. Done when unchanged text with the same inputs reuses layout.
- [ ] Remove or gate `FpsTextDiag` collection, locking, string building, and periodic logging from normal execution. `UIBatchCollector` and text diagnostics. Done when normal FPS text updates only their own records.

### Virtual Lists And Trees
- [ ] Add a reusable data-provider contract for virtual collections. UI controls. Done when item count, identity, hierarchy, expansion, and row binding do not create one subtree per source item.
- [ ] Implement fixed-height `VirtualList`. UI controls. Done when extent calculation uses item count and row height without iterating all items.
- [ ] Pool virtual rows by viewport capacity plus overscan. UI controls. Done when scrolling rebinds stable row instances without creating or destroying scene subtrees.
- [ ] Define focus, selection, pointer capture, rename, and drag behavior for recycled rows. UI controls. Done when scrolled-out rows keep correct source identity.
- [ ] Add variable-height support only after fixed-height behavior needs it. UI controls. Done when a concrete control justifies the added complexity.
- [ ] Build `VirtualTree` on the virtual list. UI controls. Done when expand, collapse, insert, remove, move, rename, activation, selection, section headers, and optional sticky headers use cached visible ranges.
- [ ] Replace `HierarchyPanel` full-tree construction with `VirtualTree`. `HierarchyPanel`. Done when the 2,000-row cap, `Transform.Clear`, and whole-tree `RemakeChildren` no longer run for collapse, rename, selection, scene edits, or refresh.
- [ ] Pool and reuse hierarchy row visuals and bindings. `HierarchyPanel`. Done when rows, buttons, toggles, text, drag/drop bindings, context menu data, and rename state reuse row instances.
- [ ] Wire hierarchy scene sections into the real hierarchy data source. `HierarchyPanel`. Done when scene sections, unassigned roots, editor-scene visibility, dirty state, and multi-selection work through the virtual data model.
- [ ] Replace debug-log-only hierarchy asset drops with the real prefab or model spawn path. `HierarchyPanel`. Done when asset drops create the correct scene data or report explicit failure.
- [ ] Split `HierarchyPanel` into focused partial files. `HierarchyPanel`. Done when data model, row binding, commands, drag/drop, and context menus have separate files.

### GPU-Efficient Composition
- [ ] Use shared solid, border, image, and text shaders with per-instance parameters. UI render code. Done when common controls no longer construct a custom `XRShader` or `XRMaterial` per control.
- [ ] Cache immutable shader and material prototypes. UI render code. Done when per-control state lives in instance records.
- [ ] Atlas compatible editor icons and small UI images. Editor UI assets and image controls. Done when common images batch through shared atlas records.
- [ ] Evaluate bindless image handles only where the renderer contract supports them. Rendering backends. Done when unsupported backends get explicit diagnostics.
- [ ] Preserve deterministic painter order for translucent UI. `UIBatchCollector`. Done when batching does not globally reorder translucent UI.
- [ ] Replace default per-panel grab-pass blur with batched flat or gradient primitives. Editor panel styling. Done when backdrop blur is opt-in.
- [ ] Share blur captures by canvas or composition layer when blur is enabled. UI composition. Done when compatible panels reuse one capture and blur result.
- [ ] Group static chrome, scrolling content, and high-frequency overlays into independently invalidated composition layers. Editor UI composition. Done when FPS counters, carets, hover, and selection do not invalidate static chrome.

### Editor Controls And Panels
- [ ] Complete single-line and multiline text input. `UITextInputComponent`. Done when selection, clipboard, navigation, submit, cancel, and IME-safe composition work.
- [ ] Complete reusable controls for scroll views, popups, context menus, tooltips, combo boxes, menus, splitters, tabs, docking, tables, trees, and property grids. UI controls. Done when panels use shared controls instead of panel-specific paths.
- [ ] Replace `UIGridChildPlacementInfo` `NotImplementedException` with a complete placement contract. `UIGridTransform.UIGridChildPlacementInfo`. Done when grid placement properties are valid at runtime.
- [ ] Make keyboard focus traversal and gamepad navigation explicit. UI input and controls. Done when navigation tests cover nested controls.
- [ ] Make clipping, visibility, disabled state, and input blocking consistent. UI controls. Done when common controls share one state model.
- [ ] Port the inspector to virtualized property rows. `InspectorPanel`. Done when reusable editors, incremental refresh, multi-object editing, and undo/redo work.
- [ ] Complete the native scene view. `SceneViewPanel`. Done when camera output, viewport interaction, selection, gizmos, drag/drop, play-state interaction, and overlays work.
- [ ] Complete the native asset browser. `AssetsPanel` and browser UI. Done when directories, filtering, thumbnails, watching, rename, delete, create, selection, and asset drag payloads work through virtual list or grid controls.
- [ ] Complete the native console. `ConsolePanel`. Done when bounded log data, virtual rows, filtering, grouping, selection, copy, and source navigation avoid one growing string.
- [ ] Complete the native profiler. Profiler panel code. Done when immutable snapshots and virtual tables or graphs avoid rebuilding strings or rows each frame.
- [ ] Port animation and remaining panels onto shared controls. Editor UI. Done when no primary native panel remains an empty stub or commented-out bootstrap branch.
- [ ] Complete native docking. Editor UI docking. Done when layout persist, restore, resize, move, close, reopen, and multi-window ownership work.

### Lifecycle, Documentation, And Readiness
- [ ] Deactivate child subtrees and unregister callbacks during panel teardown. Editor panels and UI components. Done when tests cover render, input, watcher, audio, and streaming callback cleanup.
- [ ] Preserve selection, scroll, expansion, focus, and docking state across rebuilds and play-mode transitions. Editor UI. Done when state survives relevant editor lifecycle changes.
- [ ] Complete native drag/drop for scene nodes, assets, files, components, and editor payloads. Editor UI. Done when all payload types use canonical editor mutation paths.
- [ ] Integrate undo/redo and dirty tracking for every mutating editor action. Editor UI. Done when no mutating native editor action bypasses canonical scene/editor mutation.
- [ ] Update UI architecture and guide docs. `docs/architecture/ui` and `docs/developer-guides/ui`. Done when documents describe input generations, dirty roots, transactions, persistent records, virtual controls, batch primitives, clipping, panel guidance, and validation workflows.
- [ ] Fix stale native UI documentation links and claims. UI docs. Done when the layout audit link, completed audit claims, hierarchy porting plan, and feature guide match current behavior.
- [ ] Add invariant tests. `XREngine.UnitTests`. Done when tests cover dirty-root propagation, transaction coalescing, zero allocation scopes, unchanged frames, persistent handles, batch compatibility, painter order, glyph payloads, virtual rows, panel workflows, and lifecycle cleanup.
- [ ] Remove prototype-only diagnostic logging from normal native UI execution. UI runtime and editor code. Done when diagnostics are opt-in and normal execution does not build log strings.
- [ ] Make native UI failures explicit and actionable. UI runtime and editor code. Done when unsupported accelerated paths fail diagnostically instead of using silent CPU or generic visual fallbacks.
- [ ] Switch the default editor type to native only after validation passes. Editor bootstrap and settings. Done when all validation checks pass and ImGui remains an explicit compatibility or debug option.

## Decisions Needed
- [ ] Decide when variable-height virtualization is required. Owner: UI owner.
- [ ] Decide where bindless image handles are allowed in UI batching. Owner: rendering owner.
- [ ] Decide the stabilization period before native UI becomes the default editor. Owner: editor owner.

## Out Of Scope
- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
