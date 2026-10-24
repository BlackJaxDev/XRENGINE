# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-24
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

The code reconciliation is **46 of 60 implementation items complete**; 14 items and four original owner decisions remain. Worker layout, retained screen publication, common controls, virtual collections, and native panels are implemented. Guarded scene-root and structural-list ownership, profiler views, Vulkan image/text cache contracts, and an explicit canvas-only OpenGL world route are implemented. Sparse publication capture is implemented for local edits; count-changing edits still copy clean records. Headless tests and managed editor/test compilation pass. Broader world output, complete text behavior, safe editor mutations, and editor parity remain open. Three additional proposals await approval. See the [checkpoint record](../../progress/ui/retained-ui-checkpoint.md), [retained execution](../../../architecture/ui/05-Retained-Execution.md), and [validation plan](../../testing/ui/native-ui-validation.md) for evidence and limits. ImGui remains the default editor.

## Open Code Items

### Phase 3: Coherent Publication And Input

- [ ] Publish layout, render records, clip data, and hit data as one complete UI generation. `UICanvasComponent`, `UICanvasInputComponent`, and UI publication code. Sparse capture visits only changed subtrees, and count-preserving edits replace only changed record pages. Window input uses the presented generation. Remaining: an edit that changes record counts (visibility, caret, activation) still copies all clean records into new stores, and the collector still traverses changed storage. Make the record stores splice variable-length ranges without copying clean leaves. Done when visited-record tests bound local work for count-changing edits too, and no consumer sees partial state.

### Phase 4: Persistent Render Data And Clipping

- [ ] Complete hierarchical clipping for direct-world render commands. `UIClipRegion`, `UIBatchCollector`, `UIRenderableComponent`, and render backends. Screen and offscreen 2D records already carry nested crop independently of material identity. The bounded combined-program OpenGL route now uses frozen canvas-local fragment crop. Vulkan world publication and the other unsupported world output profiles remain open. Done when the remaining world route clips nested regions through a defined OpenGL and Vulkan contract, and publication tests preserve mixed batch/fallback painter order. Native acceptance remains in the validation plan.
- [ ] Cache shaping/layout results by all glyph-placement inputs. Done when unchanged text with matching font/fallback, locale/direction, features, wrap, line policy, and DPI reuses layout.

### Phase 5: Retained Controls And Virtual Collections

- [ ] Complete text selection, grapheme-aware navigation, caret hit testing, undo/redo, and platform IME composition. `UITextInputComponent`, text layout, and input integration. Done when edits preserve text identity during layout changes and IME candidate bounds use the presented generation.

### Phase 7: Editor Adoption

- [ ] Reuse hierarchy row visuals and bindings; connect real scene sections and replace debug-only asset drops. Done when sections, roots, visibility, dirty state, multi-selection, menus, rename, drag/drop, and prefab/model spawn use the shared data model.
- [ ] Port inspector property rows to the virtual collection. Done when reusable editors, refresh, multi-object edits, and undo/redo use shared controls.
- [ ] Complete native scene view, asset browser, console, profiler, animation, and other panels through shared controls. Console categories/actions and cached log formatting are implemented. Profiler snapshot indexing is bounded and row formatting is lazy. Component, spike, GPU summary, engine diagnostic, and setting views are implemented; detailed diagnostics, remaining animation authoring, broader clone profiles and document-end cleanup, and the pending asset recovery decision remain open. Done when primary panels are functional and avoid rebuilding unbounded strings or row trees. Resource event and shadow-atlas details are implemented. Detailed Vulkan stage, causal-wait, and descriptor diagnostics use bounded rows; frame-output pages retain access to every output. CPU/GPU root history plots use virtual cells. The rolling worst CPU hierarchy, stale-root persistence, and guarded multi-child loop edits are implemented. Presented plot selection, key insertion, pan, bounded member lanes, method/attached-animation detail, and source subscription refresh are implemented. Channel editing and built-in baked/frame-cadence length adapters are implemented. Wheel zoom is anchored to the presented pointer. Remaining animation parity includes frame-axis pointer behavior. Length and stretch edits use guarded batch receipts.
- [ ] Complete single-line and multiline text input, reusable scroll/popup/menu/context-menu/tooltip/combo/splitter/tab/docking/table/tree/property-grid controls. Context menu rows have arrow, Home, and End navigation and reject stale shows; submenus remain open. Done when editor panels use shared controls for navigation, selection, commit/cancel, and state.
- [ ] Complete native docking. Rebuilds are serialized and stale dock callbacks are rejected. Cross-window pointer drag and native multi-window checks remain. Done when layouts persist, restore, resize, move, close, reopen, and support multiple windows.
- [ ] Preserve selection, scroll, expansion, focus, and docking state across rebuilds and play-mode transitions. Done when applicable lifecycle tests pass.
- [ ] Complete native drag/drop for scene nodes, assets, files, components, and editor payloads. Integrate undo/redo and dirty tracking for all mutating editor actions. Initial hierarchy rollback keeps newer callback edits; grouped multi-root duplication remains unavailable. Done when actions use canonical editor mutation paths.
- [ ] Update UI architecture and guide documents as behavior lands. Remove stale links and claims. Done when docs describe current behavior and do not claim unimplemented contracts.
- [ ] Make native UI failures explicit and actionable. Done when unsupported accelerated paths report failure rather than silently using a fallback.
- [ ] Switch the default editor to native only after the validation plan passes and owner review accepts the stabilization result. Keep ImGui available as a compatibility/debug option.

## Decisions Needed

The paragraph analyzer choice, [native asset recovery proposal](../../design/ui/native-asset-creation-recovery-design.md), and [primary window layout key proposal](../../design/ui/native-window-layout-key-compatibility-design.md) await owner approval. No analyzer replacement, recovery journal, asset-creation UI, or primary-key migration is implemented. The recovery proposal keeps existing asset formats unchanged. The key proposal keeps old layout records and rejects ambiguous matches. These choices do not close the remaining text, panel, or docking items.

The four decisions below remain the other known owner dependencies. Review these with the three pending proposals as one decision set. Independent implementation can continue; elapsed time does not resolve a decision.

- [ ] Decide when variable-height virtualization is required. Owner: UI owner.
- [ ] Decide where bindless image handles are allowed in UI batching. Owner: rendering owner.
- [ ] Decide whether world-space direct UI may cast shadows. Owner: rendering owner.
- [ ] Decide the stabilization period before native UI becomes the default editor. Owner: editor owner.

## Out Of Scope

- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
