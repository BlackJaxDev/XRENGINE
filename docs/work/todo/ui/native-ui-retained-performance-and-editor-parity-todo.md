# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-24
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

The branch has bounded worker layout, opt-in single-run shaping, shared immutable pages, frozen screen-canvas input, persistent quad/glyph storage, common controls, virtual collections, and native editor panels. Code completion is 41 of the original 60 implementation items; 19 code items and four original owner decisions remain. Screen, hidden, inactive, and offscreen UI no longer enters 3D collection; nested canvas and transform transitions have regression coverage. Explicit composition owners preserve unchanged painter spans. A bounded Vulkan solid-chrome cache path is implemented; broader native command reuse remains open. The dock host uses a shared splitter. Hierarchy edits, streaming attachment, and deferred watcher commits carry exact owners. All primary panels can use detached windows; full cross-window workflows remain open. Opt-in OpenGL screen-canvas blur shares one bounded prefix capture; unsupported output paths fail explicitly. Direct world publication, complete text behavior, remaining teardown, and editor parity remain open. See the [checkpoint record](../../progress/ui/retained-ui-checkpoint.md) and [retained execution](../../../architecture/ui/05-Retained-Execution.md) for evidence and limits. Platform acceptance is separate. ImGui remains the default editor.

## Open Code Items

### Phase 3: Coherent Publication And Input

- [ ] Publish layout, render records, clip data, and hit data as one complete UI generation. `UICanvasComponent`, `UICanvasInputComponent`, and UI publication code. Done when no consumer sees partial state and window input uses the most recently presented generation.
- [ ] Retire snapshots and UI records only after workers and render/input readers release them. UI lifecycle and renderer integration. Done when cancellation, reparenting, removal, canvas teardown, and in-flight frames cannot reuse referenced storage, and deterministic holder/reader retirement prevents bounded producer pools from waiting for garbage collection.
- [ ] Preserve pointer capture and drag behavior across publication changes. Keep `TopMostInteractable` as the camera-input blocking contract. Done when capture targets remain valid and overlapping UI/camera input tests pass.

### Phase 4: Persistent Render Data And Clipping

- [ ] Define hierarchical clipping in render records, independent of material identity. `UIClipRegion`, `UIBatchCollector`, and render backends. Done when nested regions clip consistently through batched rectangles or a documented scissor fallback on OpenGL and Vulkan.
- [ ] Preserve reusable backend command buffers for compatible static UI layers. Done when dynamic overlays do not invalidate static chrome.
- [ ] Cache shaping/layout results by all glyph-placement inputs. Done when unchanged text with matching font/fallback, locale/direction, features, wrap, line policy, and DPI reuses layout.
- [ ] Group static chrome, scrolling content, and high-frequency overlays into independently invalidated composition layers. Done when FPS, caret, hover, and selection updates leave static chrome valid.

### Phase 5: Retained Controls And Virtual Collections

- [ ] Complete text selection, grapheme-aware navigation, caret hit testing, undo/redo, and platform IME composition. `UITextInputComponent`, text layout, and input integration. Done when edits preserve text identity during layout changes and IME candidate bounds use the presented generation.
- [ ] Deactivate child subtrees and unregister callbacks during teardown. Done when render, input, watcher, audio, and streaming listeners do not outlive their owners.

### Phase 7: Editor Adoption

- [ ] Reuse hierarchy row visuals and bindings; connect real scene sections and replace debug-only asset drops. Done when sections, roots, visibility, dirty state, multi-selection, menus, rename, drag/drop, and prefab/model spawn use the shared data model.
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

The paragraph analyzer choice remains pending. The separate [native asset recovery proposal](../../design/ui/native-asset-creation-recovery-design.md) also awaits approval; no journal implementation or asset-creation UI is exposed. These choices do not close the remaining text or panel items.

- [ ] Decide when variable-height virtualization is required. Owner: UI owner.
- [ ] Decide where bindless image handles are allowed in UI batching. Owner: rendering owner.
- [ ] Decide whether world-space direct UI may cast shadows. Owner: rendering owner.
- [ ] Decide the stabilization period before native UI becomes the default editor. Owner: editor owner.

## Out Of Scope

- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
