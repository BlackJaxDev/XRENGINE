# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-24
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

The branch has bounded worker layout, opt-in single-run shaping, shared immutable pages, frozen screen-canvas input, persistent quad/glyph storage, common controls, virtual collections, and native editor panels. Code completion is 43 of the original 60 implementation items; 17 code items and four original owner decisions remain. Screen, hidden, inactive, and offscreen UI no longer enters 3D collection; nested canvas and transform transitions have regression coverage. Explicit composition owners preserve unchanged painter spans across real text, caret, selection, hover, and virtual-row updates. Topology-size changes recompute layout outputs before reuse. Bounded Vulkan solid and border chrome cache paths are implemented; broader native command reuse remains open. The dock host uses a shared splitter. Hierarchy source refresh, live/editor roots, rename drafts, and accepted keyboard selection retain exact owners. Streaming attachment and deferred watcher commits also carry exact owners. All primary panels can use detached windows. Confirmed transfers and approved-close geometry preserve exact window owners; stable primary-key migration remains pending. Inspector list rows retain controls and use guarded BCL edits. Exact BCL linked lists and supported string/int dictionaries now have grouped Undo receipts. Multiline drafts and tab focus reject replaced owners. All collection detail builders reject retired callbacks; broader collection mutation receipts remain open. String details use accepted text height. Hidden and dual scene memberships have guarded structural commands. Full cross-window workflows remain open. Opt-in OpenGL screen-canvas blur shares one bounded prefix capture; unsupported output paths fail explicitly. Direct world publication, complete text behavior, and editor parity remain open. See the [checkpoint record](../../progress/ui/retained-ui-checkpoint.md) and [retained execution](../../../architecture/ui/05-Retained-Execution.md) for evidence and limits. Platform acceptance is separate. ImGui remains the default editor.

## Open Code Items

### Phase 3: Coherent Publication And Input

- [ ] Publish layout, render records, clip data, and hit data as one complete UI generation. `UICanvasComponent`, `UICanvasInputComponent`, and UI publication code. Done when no consumer sees partial state and window input uses the most recently presented generation.
- [ ] Retire snapshots and UI records only after workers and render/input readers release them. UI lifecycle and renderer integration. Done when cancellation, reparenting, removal, canvas teardown, and in-flight frames cannot reuse referenced storage, and deterministic holder/reader retirement prevents bounded producer pools from waiting for garbage collection.
- [ ] Preserve pointer capture and drag behavior across publication changes. Keep `TopMostInteractable` as the camera-input blocking contract. Done when capture targets remain valid and overlapping UI/camera input tests pass.

### Phase 4: Persistent Render Data And Clipping

- [ ] Complete hierarchical clipping for direct-world render commands. `UIClipRegion`, `UIBatchCollector`, `UIRenderableComponent`, and render backends. Screen and offscreen 2D records already carry nested crop independently of material identity. Direct-world `RenderCommandMesh3D` still omits that crop. Done when the remaining world route clips nested regions through a defined OpenGL and Vulkan contract, and publication tests preserve mixed batch/fallback painter order. Native acceptance remains in the validation plan.
- [ ] Complete reusable backend command buffers for compatible static UI layers. Vulkan currently caches bounded solid and border `StaticChrome` draws; broader compatible content remains open. Document capability limits for backends that have no reusable command-buffer primitive. Done when the supported backend contracts retain commands across dynamic overlays without losing resource, output, clip, or ordering ownership.
- [ ] Cache shaping/layout results by all glyph-placement inputs. Done when unchanged text with matching font/fallback, locale/direction, features, wrap, line policy, and DPI reuses layout.

### Phase 5: Retained Controls And Virtual Collections

- [ ] Complete text selection, grapheme-aware navigation, caret hit testing, undo/redo, and platform IME composition. `UITextInputComponent`, text layout, and input integration. Done when edits preserve text identity during layout changes and IME candidate bounds use the presented generation.

### Phase 7: Editor Adoption

- [ ] Reuse hierarchy row visuals and bindings; connect real scene sections and replace debug-only asset drops. Done when sections, roots, visibility, dirty state, multi-selection, menus, rename, drag/drop, and prefab/model spawn use the shared data model.
- [ ] Port inspector property rows to the virtual collection. Done when reusable editors, refresh, multi-object edits, and undo/redo use shared controls.
- [ ] Complete native scene view, asset browser, console, profiler, animation, and other panels through shared controls. Console categories/actions and cached log formatting are implemented. Profiler snapshot indexing is bounded and row formatting is lazy; the remaining diagnostic views and the pending asset recovery decision remain open. Done when primary panels are functional and avoid rebuilding unbounded strings or row trees.
- [ ] Complete single-line and multiline text input, reusable scroll/popup/menu/context-menu/tooltip/combo/splitter/tab/docking/table/tree/property-grid controls. Done when editor panels use shared controls for navigation, selection, commit/cancel, and state.
- [ ] Complete native docking. Done when layouts persist, restore, resize, move, close, reopen, and support multiple windows.
- [ ] Preserve selection, scroll, expansion, focus, and docking state across rebuilds and play-mode transitions. Done when applicable lifecycle tests pass.
- [ ] Complete native drag/drop for scene nodes, assets, files, components, and editor payloads. Integrate undo/redo and dirty tracking for all mutating editor actions. Done when actions use canonical editor mutation paths.
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
