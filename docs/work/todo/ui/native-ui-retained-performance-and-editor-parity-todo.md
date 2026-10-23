# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-23
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

Phase 1 contracts and instrumentation are implemented in the runtime UI change-tracking types and their integration points. They provide stable element IDs and incarnations, eight change domains, a bounded coalescing journal with removal tombstones, local ancestor invalidation, and work counters. The final targeted run passed 23 tests; the full headless run passed 167 tests. NuGet restore and pinned submodule initialization passed. Full editor compilation remains blocked by `NETSDK1073` for the Windows Forms framework reference in the Linux environment, and live acceptance remains open in the validation document. No worker layout, coherent asynchronous publication, persistent UI rendering, virtual controls, or editor panel implementations are complete. ImGui remains the default editor.

## Open Code Items

### Phase 2: Immutable Layout And Bounded Scheduling

- [ ] Capture immutable, versioned layout inputs from `UILayoutSystem`, `UICanvasTransform`, and UI transforms. Done when workers receive only values, topology, constraints, font metrics, and dependency revisions, with no live scene objects or callbacks.
- [ ] Extract one pure measure/arrange kernel with dependency-keyed caches. Done when worker and serial-oracle runs use the same algorithm and match on deterministic inputs.
- [ ] Add bounded per-canvas scheduling and dirty-root coalescing. Done when each canvas has at most one running task and one coalesced pending input, with global worker and retained-page limits.
- [ ] Validate results against canvas lifetime, element incarnation, topology, and layout dependencies. Done when stale or overlapping results cannot change live UI, while unrelated visual edits do not discard valid layout.
- [ ] Retain immutable input pages until tasks finish, including cancelled tasks. Done when cancellation and continuous edits cannot cause unbounded queued work or reuse pages still read by workers.
- [ ] Complete incremental invalidation in `UILayoutSystem` and layout transforms. Done when measure changes visit required ancestors, arrange changes visit affected branches and dependent siblings, and global bounds/DPI changes use the same kernel.
- [ ] Remove forced synchronous panel layout calls and the live-tree coroutine after the kernel owns all layout behavior. `XREngine.Editor/UI`, `UICanvasTransform`, and `UILayoutSystem`. Done when no active layout path mutates live transforms from a worker or bypasses validated results.
- [ ] Remove active `OnResizeChildComponents` paths only after equivalent behavior belongs to measure and arrange. UI transform classes. Done when normal layout has one authoritative kernel.
- [ ] Replace LINQ in `UIBoundableTransform.GetMaxChildWidth` and `GetMaxChildHeight`; audit layout hot paths for allocations. Done when steady-state visits use allocation-free iteration and measured steady-state layout allocates no managed memory.

### Phase 3: Coherent Publication And Input

- [ ] Publish layout, render records, clip data, and hit data as one complete UI generation. `UICanvasComponent`, `UICanvasInputComponent`, and UI publication code. Done when no consumer sees partial state and window input uses the most recently presented generation.
- [ ] Retire snapshots and UI records only after workers and render/input readers release them. UI lifecycle and renderer integration. Done when cancellation, reparenting, removal, canvas teardown, and in-flight frames cannot reuse referenced storage.
- [ ] Replace hit transition set rebuilding with an allocation-free identity diff. `UICanvasInputComponent` and render records. Done when warmed pointer movement allocates no managed memory and enter/leave behavior remains correct.
- [ ] Define stable identity and ordering semantics for `RenderInfo2D` membership. Done when mutable painter-order fields cannot lose hit membership.
- [ ] Bound reusable blocker-filter capacity and cache idle hit queries by pointer state, capture state, output transform, and hit generation. Done when warmed hit queries avoid allocations and unchanged input reuses the prior result.
- [ ] Advance hit generations only for visibility, eligibility, clip, painter order, transform, or bounds changes. Done when visual-only changes leave hit state valid.
- [ ] Exclude decorative primitives from hit testing unless they block input. Done when only interactive or blocking elements enter hit queries.
- [ ] Preserve pointer capture and drag behavior across publication changes. Keep `TopMostInteractable` as the camera-input blocking contract. Done when capture targets remain valid and overlapping UI/camera input tests pass.

### Phase 4: Persistent Render Data And Clipping

- [ ] Define explicit solid-quad, border, image, and text primitive contracts and a complete batch key. `UIRenderableComponent`, `UIMaterialComponent`, and `UIBatchCollector`. Done when type, shader variant, texture/atlas, sampler, blend/depth state, clip mode, render pass, and material features determine compatibility.
- [ ] Route custom shaders through normal render commands unless they declare compatible batch semantics. Done when custom textureless shaders cannot become generic solid quads and missing `MatColor` produces an explicit diagnostic.
- [ ] Preserve painter-order runs across compatible and incompatible primitives. Done when mixed translucent primitive tests retain authored order.
- [ ] Define hierarchical clipping in render records, independent of material identity. `UIClipRegion`, `UIBatchCollector`, and render backends. Done when nested regions clip consistently through batched rectangles or a documented scissor fallback on OpenGL and Vulkan.
- [ ] Give batchable primitives stable generation-checked handles and register/unregister them on activation changes. `UIRenderableComponent` and related components. Done when normal frames do not rediscover membership and stale handles cannot draw removed elements.
- [ ] Store primitive records in persistent pools and replace per-frame clear/re-add collection. `UIBatchCollector`. Done when unchanged UI performs no logical batch rebuild.
- [ ] Keep stable instance slots and coalesce dirty upload ranges in OpenGL and Vulkan UI backends. Done when only changed ranges upload and buffer growth does not republish unused capacity during ordinary frames.
- [ ] Preserve reusable backend command buffers for compatible static UI layers. Done when dynamic overlays do not invalidate static chrome.
- [ ] Publish glyph payloads by text-layout revision and use owned or pooled contiguous storage. `UITextComponent`, `UIText`, and `UIBatchCollector`. Done when unchanged text performs no glyph-list copy and color/transform/outline changes do not rebuild glyph geometry.
- [ ] Add stable text slots and reusable glyph ranges. Done when editing one text run does not repack unrelated text; atlas identity remains part of batch compatibility.
- [ ] Cache shaping/layout results by all glyph-placement inputs. Done when unchanged text with matching font/fallback, locale/direction, features, wrap, line policy, and DPI reuses layout.
- [ ] Remove or gate `FpsTextDiag` work from normal execution. Done when collection, locks, string building, and periodic diagnostic logs run only when diagnostics are enabled.
- [ ] Avoid adding 3D render records for UI items that no 3D camera can draw. `UIRenderableComponent` and canvas integration. Preserve the decision whether world-space UI may cast shadows for owner review. Done when screen-space, inactive, and offscreen canvases do not pollute 3D collection, draw-space changes restore valid visibility, and a test covers transitions.
- [ ] Use shared solid, border, image, and text shaders with immutable material prototypes. Done when common controls do not create one shader/material per control and per-control values stay in instance records.
- [ ] Atlas compatible editor icons and small UI images; evaluate bindless handles only on supported backends. Done when unsupported paths report their limits.
- [ ] Make backdrop blur opt-in and share captures by canvas/composition layer when enabled. Done when compatible panels reuse one capture.
- [ ] Group static chrome, scrolling content, and high-frequency overlays into independently invalidated composition layers. Done when FPS, caret, hover, and selection updates leave static chrome valid.

### Phase 5: Retained Controls And Virtual Collections

- [ ] Define a provider contract and fixed-height virtual list. Done when extent uses item count and row height without scanning all source items or creating one scene subtree per item.
- [ ] Recycle rows within viewport capacity plus overscan. Done when scrolling rebinds stable row instances without creating/destroying subtrees.
- [ ] Define identity-safe focus, selection, capture, rename, and drag behavior for recycled rows. Done when interaction cannot transfer to a different item after scrolling or removal.
- [ ] Add a virtual tree on the list using cached visible ranges. Done when expansion, insert, remove, move, rename, activation, selection, and section headers do not rebuild the full data set. Add sticky headers only if required by a concrete control.
- [ ] Add typed retained bindings and shared control state. Runtime controls and `NativeUIElements`. Done when callback lifetime is explicit and reflection is limited to cached editor adapters.
- [ ] Complete first-milestone buttons, checkboxes, radio groups, dropdowns, range sliders, color selectors, numeric inputs, and scroll regions. Done when keyboard, focus, state, commit/cancel, clipping, and binding contracts work.
- [ ] Complete text selection, grapheme-aware navigation, caret hit testing, undo/redo, and platform IME composition. `UITextInputComponent`, text layout, and input integration. Done when edits preserve text identity during layout changes and IME candidate bounds use the presented generation.
- [ ] Define accessibility semantics and platform adapter boundaries. Done when roles, labels, values, actions, focus order, and virtual realization have contracts and unsupported adapters report limits.
- [ ] Replace `UIGridChildPlacementInfo` `NotImplementedException` with a complete placement contract. Done when grid placement properties work at runtime.
- [ ] Make focus traversal, gamepad navigation, clipping, visibility, disabled state, and input blocking consistent. Done when nested-control navigation and shared-state behavior pass tests.
- [ ] Deactivate child subtrees and unregister callbacks during teardown. Done when render, input, watcher, audio, and streaming listeners do not outlive their owners.

### Phase 6: First-Milestone Fixture

- [ ] Add an opt-in FPS overlay, common-control gallery, and large-list fixture with UI work counters. Unit-world UI construction and runtime UI. Done when the fixture uses the production canvas/text/batch path and does not change the default editor.
- [ ] Validate 100,000 and 1,000,000 logical list items with bounded realization. Done when realized rows stay within viewport capacity plus overscan and focus stays on the correct item.
- [ ] Keep runtime tests separate from validation procedures. Add deterministic invariants under `XREngine.UnitTests/`; record live, visual, performance, OpenGL, and separate Vulkan results in the [validation plan](../../testing/ui/native-ui-validation.md).

### Phase 7: Editor Adoption

- [ ] Replace `HierarchyPanel` full-tree construction with the virtual tree. Done when the 2,000-row cap, `Transform.Clear`, and whole-tree `RemakeChildren` do not run for routine edits, selection, or refresh.
- [ ] Reuse hierarchy row visuals and bindings; connect real scene sections and replace debug-only asset drops. Done when sections, roots, visibility, dirty state, multi-selection, menus, rename, drag/drop, and prefab/model spawn use the shared data model.
- [ ] Split `HierarchyPanel` into focused data, row-binding, command, drag/drop, and menu files. Done when each concern has a clear owner.
- [ ] Port inspector property rows to the virtual collection. Done when reusable editors, refresh, multi-object edits, and undo/redo use shared controls.
- [ ] Complete native scene view, asset browser, console, profiler, animation, and other panels through shared controls. Done when primary panels are functional and avoid rebuilding unbounded strings or row trees.
- [ ] Complete single-line and multiline text input, reusable scroll/popup/menu/context-menu/tooltip/combo/splitter/tab/docking/table/tree/property-grid controls. Done when editor panels use shared controls for navigation, selection, commit/cancel, and state.
- [ ] Complete native docking. Done when layouts persist, restore, resize, move, close, reopen, and support multiple windows.
- [ ] Preserve selection, scroll, expansion, focus, and docking state across rebuilds and play-mode transitions. Done when applicable lifecycle tests pass.
- [ ] Complete native drag/drop for scene nodes, assets, files, components, and editor payloads. Integrate undo/redo and dirty tracking for all mutating editor actions. Done when actions use canonical editor mutation paths.
- [ ] Update UI architecture and guide documents as behavior lands. Remove stale links and claims. Done when docs describe current behavior and do not claim unimplemented contracts.
- [ ] Make native UI failures explicit and actionable. Done when unsupported accelerated paths report failure rather than silently using a fallback.
- [ ] Switch the default editor to native only after the validation plan passes and owner review accepts the stabilization result. Keep ImGui available as a compatibility/debug option.

## Decisions Needed

- [ ] Decide when variable-height virtualization is required. Owner: UI owner.
- [ ] Decide where bindless image handles are allowed in UI batching. Owner: rendering owner.
- [ ] Decide whether world-space direct UI may cast shadows. Owner: rendering owner.
- [ ] Decide the stabilization period before native UI becomes the default editor. Owner: editor owner.

## Out Of Scope

- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
