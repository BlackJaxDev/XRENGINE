# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-07-17
Owner: UI / Editor / Rendering
Status: Open; architecture-first execution

## Goal

Turn the native retained UI into the production editor UI by making unchanged
frames perform essentially no UI preparation work, making changed frames scale
with the affected subtree or dirty render ranges, and completing editor feature
parity on top of those efficient foundations.

The intended advantage over immediate-mode UI is architectural:

- retained layout is recomputed only for dirty roots,
- input queries run only when pointer or UI state changes,
- render records and GPU slots persist across frames,
- unchanged glyphs and primitives are not copied or uploaded,
- large lists and trees realize only visible rows plus bounded overscan,
- and static UI command topology remains reusable until it actually changes.

This is a prototype-stage cleanup. Prefer coherent v1 contracts and breaking
internal API improvements over preserving the current transitional behavior.

## Execution Policy

- Execute the phases in order unless a phase explicitly identifies independent
  work.
- Fix root contracts before porting more panels onto them.
- Do not add or maintain baseline recordings, benchmark archives, comparison
  matrices, or historical performance snapshots as part of this effort.
- Do not spend time proving the current prototype is slow before removing the
  source-visible whole-tree work, allocations, rebuilds, and full uploads listed
  here.
- Add narrow correctness and invariant tests after each implementation slice is
  functionally sound.
- Validate both OpenGL and Vulkan native UI paths where rendering behavior is
  affected.
- Keep ImGui as the default editor until the native path satisfies the feature
  and stability completion gates in this document.

## Scope

- Native UI transforms, layout invalidation, arrangement, and matrix updates.
- Native UI input collection, hit testing, focus, pointer transitions, and
  clipping-aware interaction.
- VisualScene2D collection and screen-space UI render publication.
- UIBatchCollector, material primitives, text batching, persistent GPU data,
  clipping, and painter-order preservation.
- Reusable virtual list/tree controls.
- Native hierarchy, inspector, scene view, asset browser, console, profiler,
  docking, toolbar, menus, and editor interaction parity.
- Native UI architecture, audit, design, feature, and workflow documentation.

## Non-Goals

- Do not record a before/after baseline for this work.
- Do not build a benchmark dashboard or performance-history archive.
- Do not optimize ImGui as part of this tracker.
- Do not preserve legacy native UI contracts solely for compatibility; v1 has
  not shipped.
- Do not use asynchronous layout as a substitute for eliminating unnecessary
  layout work.
- Do not hide unsupported batch primitives behind a visually incorrect generic
  quad fallback.
- Do not add silent CPU rendering fallbacks for explicitly accelerated UI
  paths.

## Core Invariants

- An unchanged canvas performs no measure or arrange traversal.
- An unchanged pointer and unchanged input tree perform no hit-test query or
  intersection-set rebuild.
- An unchanged visual tree performs no logical batch reconstruction and no
  CPU-to-GPU UI data upload.
- UI hot paths allocate zero managed bytes in steady state.
- A local layout change invalidates only the required measure ancestors and
  arrange descendants, not every descendant of the canvas.
- Canvas-bounds changes may invalidate the full canvas; ordinary child
  property, scroll, selection, and text changes may not.
- Virtualized controls keep realized UI row count bounded by viewport capacity
  plus overscan, independent of source item count.
- Painter order, clipping, shader behavior, and blend/depth state remain
  correct when batching is enabled.
- Custom materials are never routed through the generic solid-quad batch path
  unless they explicitly declare compatible semantics.
- A text-content change republishes only the affected text and glyph ranges.
- UI tree mutation and render publication use stable handles/generations so
  stale slots cannot render detached or rebound elements.
- High-frequency overlays update their own dynamic records without invalidating
  static editor chrome.

## Related Documents

- [Architecture overview](../../../architecture/ui/README.md)
- [Native hierarchy porting plan](../../design/ui/native-hierarchy-porting-plan.md)
- [Native hierarchy feature guide](../../../developer-guides/ui/native-hierarchy-panel.md)
- [Historical layout audit](../../audit/COMPLETED/native-ui-layout-known-issues.md)
- [Vulkan native FPS text investigation](../../investigations/native-ui-vulkan-fps-text.md)

## Primary Implementation Surfaces

- XRENGINE/Scene/Components/UI/Core/Transforms/UILayoutSystem.cs
- XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs
- XRENGINE/Scene/Components/UI/Core/Transforms/UITransform.cs
- XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs
- XRENGINE/Scene/Components/UI/Core/Arrangements/UIListTransform.cs
- XRENGINE/Scene/Components/Pawns/UICanvasComponent.cs
- XRENGINE/Scene/Components/Pawns/UICanvasInputComponent.cs
- XRENGINE/Scene/Components/UI/Core/UIRenderableComponent.cs
- XRENGINE/Scene/Components/UI/Core/UIMaterialComponent.cs
- XRENGINE/Scene/Components/UI/Text/UITextComponent.cs
- XREngine.Runtime.Rendering/Rendering/VisualScene2D.cs
- XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs
- XREngine.Editor/UI/UIEditorComponent.cs
- XREngine.Editor/UI/Panels/HierarchyPanel.cs
- XREngine.Editor/UI/Panels/Inspector/
- XREngine.Editor/UI/NativeUIElements.cs

## Phase 1 - Layout Invalidation And Construction Transactions

### 1.1 Replace canvas-wide invalidation

- [ ] Introduce explicit layout dirtiness for measure, arrange, transform, and
  canvas-bounds changes.
- [ ] Track dirty layout roots per canvas rather than recursively incrementing
  every descendant version in UILayoutSystem.InvalidateCanvasLayout.
- [ ] Reserve full descendant invalidation for canvas-bounds, DPI/scale, or
  other changes that genuinely alter every descendant constraint.
- [ ] Propagate measure invalidation upward only through ancestors whose desired
  size depends on children.
- [ ] Propagate arrange invalidation downward only from the nearest ancestor
  whose child regions changed.
- [ ] Ensure changing selection colors, hover state, or non-layout visual data
  does not invalidate layout.
- [ ] Ensure scrolling a list updates the list/realized-row arrangement without
  invalidating unrelated canvas branches.
- [ ] Prevent Visibility changes made by virtualization from scheduling a
  redundant follow-up full layout pass.

### 1.2 Add bulk mutation transactions

- [ ] Add a scoped canvas/layout mutation transaction that coalesces repeated
  invalidations.
- [ ] Use the transaction while constructing editor panels, menus, hierarchy
  rows, inspector controls, and docking layouts.
- [ ] Defer measure/arrange until the outermost transaction completes.
- [ ] Define nested transaction behavior and make disposal safe when
  construction throws.
- [ ] Remove direct forced layout calls from panel rebuild paths once normal
  transaction completion guarantees a valid layout before publication.

### 1.3 Remove known layout hot-path overhead and dead paths

- [ ] Replace LINQ in UIBoundableTransform.GetMaxChildWidth and
  GetMaxChildHeight with allocation-free indexed or direct loops.
- [ ] Audit layout loops for captured delegates, boxing, temporary arrays,
  string formatting, and non-struct enumerators.
- [ ] Remove the dead OnResizeChildComponents layout path and its overrides
  after confirming all active behavior exists in measure/arrange.
- [ ] Remove stale compatibility comments that still describe the old path as
  active.
- [ ] Keep synchronous incremental layout as the canonical implementation.
- [ ] Reassess or remove the async layout coroutine after the dirty-root path is
  complete; do not maintain two competing layout algorithms.

### Phase 1 completion gates

- [ ] A leaf visual-property change causes no layout work.
- [ ] A leaf size change visits only its required measure ancestors and affected
  arrange branch.
- [ ] List scrolling does not traverse unrelated canvas branches.
- [ ] Bulk construction performs one coalesced layout publication.
- [ ] No active OnResizeChildComponents implementation remains.
- [ ] Targeted layout tests cover nested auto-size, anchors, splitters, canvas
  resize, visibility, scrolling, and transaction nesting.

## Phase 2 - Allocation-Free Incremental Input

### 2.1 Replace intersection array and union work

- [ ] Remove LastUIElementIntersections.Union(...).ForEach(...).
- [ ] Remove per-element ToArray calls from ValidateIntersection.
- [ ] Replace them with an allocation-free ordered-set diff or stable-handle
  membership transition pass.
- [ ] Confirm the RenderInfo2D comparer implements identity equality consistently
  with ordering so SortedSet.Contains is trustworthy.
- [ ] Replace blocker filtering temporary lists with reusable storage or
  in-place stable filtering.
- [ ] Preserve correct enter, leave, directly-over, move, focus, click, drag,
  and context-menu transitions.

### 2.2 Add input generations

- [ ] Track pointer position/button/wheel generation separately from UI
  hit-region generation.
- [ ] Increment the UI hit-region generation only when visibility, input
  eligibility, clip, painter order, transform, or bounds changes.
- [ ] Reuse the previous hit result when neither generation changes.
- [ ] Avoid hit testing non-interactive decorative primitives unless they are
  explicitly needed for input blocking.
- [ ] Ensure pointer capture and active drag operations can request movement
  events without rebuilding unrelated hit state.
- [ ] Keep TopMostInteractable as the camera-input blocking contract.

### Phase 2 completion gates

- [ ] An idle pointer performs no quadtree query or intersection-set diff.
- [ ] Pointer movement performs no managed allocation.
- [ ] Hovering nested controls produces one correct enter/leave transition per
  affected interactable.
- [ ] Clipped, hidden, collapsed, detached, and input-transparent controls cannot
  become topmost.
- [ ] Targeted tests cover overlapping elements, blocker ancestry, capture,
  drag/drop, focus, and camera-input coexistence.

## Phase 3 - Explicit UI Primitive And Batch Compatibility Contract

### 3.1 Replace heuristic material batching

- [ ] Define explicit native UI primitive kinds, initially including solid quad,
  border/outline quad, image, and text.
- [ ] Define a UI batch key containing every state that affects compatibility:
  primitive/shader variant, texture or atlas, sampler, blend/depth state, clip
  mode, render pass, and required material features.
- [ ] Make batch participation opt-in through a primitive contract rather than
  inferring compatibility from the absence of textures.
- [ ] Route custom shader materials through their normal render command unless
  they explicitly implement a compatible batch primitive.
- [ ] Convert NativeUIElements outline controls to an explicit border/outline
  primitive or deliberately keep them unbatched until that primitive exists.
- [ ] Prevent missing MatColor from silently becoming a magenta generic batch
  instance.
- [ ] Preserve stable painter-order runs when compatible and incompatible
  primitives alternate.

### 3.2 Establish clipping semantics

- [ ] Define hierarchical clip inheritance independently of material identity.
- [ ] Carry resolved clip rectangles or clip handles in UI render records.
- [ ] Support batched rectangular clipping without forcing each clipped element
  into a standalone draw.
- [ ] Make text overflow and list viewport clipping use the same clip contract.
- [ ] Keep scissor-state fallback available for primitives that cannot use the
  batched clip representation.

### Phase 3 completion gates

- [ ] Custom textureless shaders render with their own semantics when batching
  is enabled globally.
- [ ] Solid, border, image, and text primitives produce deterministic batch keys.
- [ ] Painter order remains correct across batch-key transitions.
- [ ] Nested list/text clipping is visually and interactively consistent on
  OpenGL and Vulkan.
- [ ] Contract tests reject incompatible material batching.

## Phase 4 - Persistent Render Records And Dirty-Range GPU Publication

### 4.1 Introduce stable render records

- [ ] Give every batchable UI primitive a stable render handle with a generation.
- [ ] Register/unregister handles on component activation/deactivation instead
  of rediscovering all batch membership every frame.
- [ ] Store stable primitive records in persistent pools with free-list reuse.
- [ ] Separate record fields into topology, transform/bounds, visual parameters,
  clip, and visibility dirty domains.
- [ ] Publish detach/rebind generations so stale handles cannot draw recycled
  records.
- [ ] Keep render records independent of transient scene traversal order while
  retaining explicit painter-order keys.

### 4.2 Stop rebuilding unchanged batches

- [ ] Replace per-frame UIBatchCollector Clear/re-add behavior with persistent
  batch groups keyed by the explicit compatibility contract.
- [ ] Rebuild batch topology only when handles are added, removed, reordered, or
  change batch key.
- [ ] Update only dirty instance records when transforms, colors, bounds, clips,
  or visibility change.
- [ ] Avoid the flat all-renderables screen-space collection walk for unchanged
  retained UI.
- [ ] Process pending renderable operations through a generation-aware change
  queue.
- [ ] Separate static editor chrome from dynamic overlay records.

### 4.3 Publish only dirty GPU ranges

- [ ] Keep persistent instance buffers with stable slot ownership.
- [ ] Coalesce adjacent dirty slots into bounded upload ranges.
- [ ] Grow buffers geometrically without republishing unused capacity on normal
  frames.
- [ ] Preserve reusable command buffers while topology, pipeline state, and
  buffer bindings remain compatible.
- [ ] Make record removal safe for in-flight frames through the renderer's
  existing lifetime/fence model.
- [ ] Ensure OpenGL and Vulkan use the same logical dirty-record contract even
  when their upload implementations differ.

### Phase 4 completion gates

- [ ] An unchanged visual tree performs no collector rebuild and no UI buffer
  commit.
- [ ] A color-only change updates one persistent instance slot.
- [ ] A transform-only change does not rebuild batch topology.
- [ ] Adding/removing/reordering one primitive updates only affected topology
  runs.
- [ ] Detached and recycled handles cannot render stale data.
- [ ] Static command topology remains reusable while a dynamic FPS/text overlay
  changes.

## Phase 5 - Persistent Text Layout And Glyph Publication

### 5.1 Remove per-frame glyph copies

- [ ] Replace glyph-list array copies in batched and non-batched text paths with
  a versioned published glyph payload.
- [ ] Use pooled or owned contiguous storage that remains stable while the render
  snapshot consumes it.
- [ ] Publish a new glyph payload only when text, font, wrapping, alignment,
  outline spacing, or layout bounds change.
- [ ] Keep text transform/color/outline instance changes separate from glyph
  geometry changes.
- [ ] Make glyph payload ownership and retirement safe across update, collect,
  and render threads.

### 5.2 Add stable text and glyph slots

- [ ] Give each UITextComponent a stable text-instance slot.
- [ ] Allocate/reuse glyph ranges without repacking every unrelated text entry
  when one string changes.
- [ ] Upload only changed glyph transforms, UVs, glyph-to-text mappings, and text
  instance records.
- [ ] Preserve atlas identity in the batch key.
- [ ] Cache shaping/layout results by the actual inputs that affect glyph
  placement.
- [ ] Ensure the FPS overlay updates only its own text/glyph records.
- [ ] Remove or gate FpsTextDiag collection, locking, string building, and
  periodic logging from normal execution.

### Phase 5 completion gates

- [ ] Unchanged text performs no glyph copy, layout, or upload.
- [ ] Color/transform-only text changes do not rebuild glyph geometry.
- [ ] Editing one text control does not repack unrelated text.
- [ ] Atlas changes move only the affected text to the correct batch group.
- [ ] Text outline, wrapping, alignment, clipping, and non-batched parity remain
  correct on OpenGL and Vulkan.

## Phase 6 - Real Virtual List And Virtual Tree Controls

### 6.1 Build reusable virtualization infrastructure

- [ ] Introduce a data-provider contract that exposes item count, identity,
  hierarchy/expansion state, and row binding without creating one UI subtree per
  source item.
- [ ] Implement a fixed-height VirtualList first.
- [ ] Compute content extent from item count and row height without iterating all
  source items.
- [ ] Maintain a row pool sized to viewport capacity plus configurable overscan.
- [ ] Rebind stable row instances as the viewport range changes.
- [ ] Keep scroll offset changes independent of total source item count.
- [ ] Define focus, selection, pointer capture, rename, and drag behavior when a
  bound row scrolls out of view.
- [ ] Add variable-height support only after the fixed-height contract is
  complete and needed by a concrete control.

### 6.2 Build VirtualTree on the list

- [ ] Maintain a flattened visible-node index with cached subtree spans.
- [ ] Apply expand/collapse as an incremental visible-range insertion/removal.
- [ ] Preserve stable source identity independently of recycled row identity.
- [ ] Support incremental node insert, remove, move, rename, activation, and
  selection changes.
- [ ] Keep scene/world section headers in the same virtual data model.
- [ ] Allow sticky or non-scrolling headers without realizing the entire tree.

### 6.3 Migrate HierarchyPanel

- [ ] Replace the current 2,000-row construction/truncation model with
  VirtualTree.
- [ ] Remove Transform.Clear and whole-tree RemakeChildren from collapse,
  rename, selection, scene edits, and ordinary refresh.
- [ ] Pool/reuse row visuals, buttons, toggles, text, and drag/drop bindings.
- [ ] Wire the existing scene-section model into the actual hierarchy data
  source instead of rendering RootNodes directly.
- [ ] Complete asset-path drop by invoking the real prefab/model spawn path;
  remove the debug-log-only placeholder.
- [ ] Split the large HierarchyPanel into focused partial files for data model,
  row binding, commands, drag/drop, and context menu behavior.

### Phase 6 completion gates

- [ ] Realized hierarchy row count is bounded by viewport rows plus overscan for
  any source item count.
- [ ] Scrolling, selection, and hover do not create/destroy row scene subtrees.
- [ ] Expand/collapse and rename do not clear or reconstruct the hierarchy.
- [ ] Scene sections, unassigned roots, editor-scene visibility, dirty state,
  drag/drop, context menus, rename, and multi-selection remain functional.
- [ ] Virtualization tests cover empty, small, deeply nested, wide, and very
  large logical trees.

## Phase 7 - GPU-Efficient Native UI Composition

### 7.1 Consolidate primitive rendering

- [ ] Use shared solid/border/image/text shaders and per-instance parameters
  instead of constructing a custom XRShader/XRMaterial for each control.
- [ ] Cache immutable shader/material prototypes; keep per-control state in
  instance records.
- [ ] Atlas compatible editor icons and small UI images.
- [ ] Evaluate bindless image handles only where the existing renderer contract
  supports them cleanly.
- [ ] Keep painter-order batching deterministic; do not globally reorder
  translucent UI to reduce draw count.

### 7.2 Remove per-panel grab-pass blur as the default

- [ ] Replace default editor panel backgrounds with a batched flat or gradient
  primitive.
- [ ] Make backdrop blur an explicit optional style/effect.
- [ ] When blur is enabled, capture/blur once per canvas or composition layer
  and reuse the result across compatible panels.
- [ ] Do not create a separate grab-pass texture and 15-sample material for
  every panel/menu/field background.

### 7.3 Preserve static command topology

- [ ] Group static chrome, scrolling content, and high-frequency overlays into
  independently invalidated composition layers.
- [ ] Reuse backend command buffers for layers whose topology and bindings did
  not change.
- [ ] Keep dynamic instance updates from invalidating unrelated static
  command-buffer segments.
- [ ] Preserve renderer diagnostics for invalid/stale generations without
  formatting logs in the normal hot path.

### Phase 7 completion gates

- [ ] Common editor controls use shared primitive pipelines and persistent
  instance data.
- [ ] Default editor chrome requires no per-panel grab pass.
- [ ] Updating an FPS counter, caret, hover, or selection does not invalidate
  static editor chrome.
- [ ] UI composition remains visually correct under transparency, nested clips,
  resizing, and multiple canvases on OpenGL and Vulkan.

## Phase 8 - Native Editor Feature Parity On Efficient Controls

### 8.1 Complete core reusable controls

- [ ] Finish single-line and multiline text input, selection, clipboard,
  navigation, submit/cancel, and IME-safe composition behavior.
- [ ] Complete scroll view, popup, context menu, tooltip, combo box, menu bar,
  splitter, tab, docking, table, tree, and property-grid controls.
- [ ] Replace UIGridTransform.UIGridChildPlacementInfo's
  NotImplementedException with a complete placement contract.
- [ ] Make keyboard focus traversal and gamepad navigation explicit.
- [ ] Make clipping, visibility, disabled state, and input blocking consistent
  across all controls.

### 8.2 Complete editor panels

- [ ] Inspector: virtualized property rows, reusable editors, incremental object
  refresh, multi-object editing, and undo/redo integration.
- [ ] Scene view: camera output, viewport interaction, selection, gizmos,
  drag/drop, play-state interaction, and overlays.
- [ ] Asset browser: virtualized list/grid, directories, filtering, thumbnails,
  file watching, rename/delete/create, selection, and asset drag payloads.
- [ ] Console: bounded log model, virtualized rows, filtering, grouping,
  selection, copy, and source navigation; do not concatenate the entire log into
  one growing string.
- [ ] Profiler: immutable data snapshots and virtualized tables/graphs without
  rebuilding strings or rows every frame.
- [ ] Animation and remaining editor panels: port onto the shared controls
  rather than introducing panel-specific layout/render paths.
- [ ] Docking: persist/restore editor layout and support resize, move, close,
  reopen, and multi-window ownership correctly.

### 8.3 Lifecycle and workflow parity

- [ ] Ensure panel teardown deactivates child subtrees and unregisters input,
  render, watcher, audio, and streaming callbacks.
- [ ] Preserve selection, scroll, expansion, focus, and docking state across
  relevant editor rebuilds and play-mode transitions.
- [ ] Complete native drag/drop for scene nodes, assets, files, components, and
  editor-specific payloads.
- [ ] Integrate undo/redo and dirty tracking for every mutating editor action.
- [ ] Keep editor mutations queued through the canonical scene/editor mutation
  path.

### Phase 8 completion gates

- [ ] Native hierarchy, inspector, scene view, asset browser, console, profiler,
  toolbar/menus, and docking support normal editor workflows.
- [ ] No primary native panel remains an empty stub or commented-out bootstrap
  branch.
- [ ] Large data panels use virtualization and incremental updates.
- [ ] Activation/deactivation tests cover every panel with external callbacks or
  resources.
- [ ] Native UI can complete the standard edit, play, inspect, import, drag/drop,
  undo, and save workflow on OpenGL and Vulkan.

## Phase 9 - Documentation, Tests, And Default-Path Readiness

### 9.1 Reconcile documentation with implementation

- [ ] Update docs/architecture/ui to describe the single active measure/arrange
  path, dirty roots, transactions, persistent render records, and virtual
  controls.
- [ ] Fix the broken native layout audit link.
- [ ] Correct or retire stale claims in the completed layout audit.
- [ ] Update the hierarchy porting plan and feature guide to distinguish
  visibility culling from row recycling and to describe actual asset spawning.
- [ ] Document the explicit batch primitive and clipping contracts.
- [ ] Document how new editor panels must use virtualized collections and
  persistent primitives.
- [ ] Document native UI debugging and validation workflows without introducing
  a baseline-recording requirement.

### 9.2 Add invariant-focused validation

- [ ] Add layout tests for dirty-root propagation and transaction coalescing.
- [ ] Add allocation tests or zero-budget native UI scopes around layout, input,
  collection, batch publication, and text publication.
- [ ] Add tests proving unchanged frames perform no layout, hit-test rebuild,
  logical batch rebuild, or UI buffer commit.
- [ ] Add persistent-handle generation and retirement tests.
- [ ] Add explicit batch-compatibility and painter-order tests.
- [ ] Add glyph-payload ownership, reuse, partial-update, and atlas-key tests.
- [ ] Add virtual list/tree row-bound and incremental mutation tests.
- [ ] Add functional panel workflow and lifecycle tests.
- [ ] Run the narrowest relevant build/tests after each phase and resolve new
  warnings in touched files.
- [ ] Perform native editor functional validation on OpenGL and Vulkan after
  rendering, clipping, text, or panel integration phases.

### 9.3 Default-path readiness

- [ ] Remove prototype-only diagnostic logging from normal native UI execution.
- [ ] Confirm native UI failures are explicit and actionable rather than hidden
  by silent fallback behavior.
- [ ] Confirm no essential editor workflow requires an ImGui-only implementation.
- [ ] Switch the default editor type from ImGui to Native only after all
  completion gates below are satisfied.
- [ ] Keep an explicit ImGui compatibility/debug option until the native default
  has completed a stabilization period.

## Final Completion Gates

- [ ] Unchanged native UI frames do no layout, input-query, logical batch
  rebuild, glyph copy, or UI GPU upload work.
- [ ] Native UI steady-state layout, input, collection, and publication paths
  allocate zero managed bytes.
- [ ] Local changes update only affected layout branches and persistent render
  ranges.
- [ ] Virtual collections realize bounded rows independent of source item count.
- [ ] Custom materials, clipping, painter order, transparency, and text are
  correct with batching enabled.
- [ ] Native editor panels provide the normal workflows currently expected from
  the ImGui editor.
- [ ] Native UI behaves correctly on both OpenGL and Vulkan.
- [ ] Targeted tests pass, touched projects build without new warnings, and no
  primary native panel remains a stub.
- [ ] Architecture and feature documentation match the implemented contracts.
- [ ] Native UI is the default editor path; ImGui is retained only as an
  explicit compatibility/debug option.

## Recommended Immediate Execution Order

1. Phase 1: dirty-root layout and construction transactions.
2. Phase 2: allocation-free incremental input.
3. Phase 3: explicit primitive/batch compatibility and clipping.
4. Phase 4: persistent render handles, batches, and dirty GPU ranges.
5. Phase 5: persistent text/glyph payloads.
6. Phase 6: reusable virtualization and hierarchy migration.
7. Phase 7: shared GPU primitives, optional shared blur, and static layers.
8. Phase 8: editor feature parity using the completed foundations.
9. Phase 9: documentation reconciliation, invariant tests, and default-path
   readiness.
