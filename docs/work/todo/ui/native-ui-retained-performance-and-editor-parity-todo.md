# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-28
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

The code reconciliation is **57 of 60 implementation items complete**; three implementation items remain; policy choices are approved and final default-switch review is pending. Worker layout, retained screen publication, common controls, virtual collections, and native panels are implemented. Guarded scene-root and structural-list ownership, profiler views, Vulkan image/text cache contracts, and an explicit canvas-only OpenGL world route are implemented. Text layout results are cached by all glyph-placement inputs. Native UI failures are reported by code through `UIFailureDiagnostics`. Publication is complete: sparse capture visits only changed subtrees, count-changing edits splice only touched record pages, and the batch collector visits only new record pages. Headless tests and managed editor/test compilation pass. Text fields place the caret from the presented layout. Broader world output, safe editor mutations, and editor parity remain open. The owner approved the remaining policy proposals on 2026-10-26. See the [checkpoint record](../../progress/ui/retained-ui-checkpoint.md), [retained execution](../../../architecture/ui/05-Retained-Execution.md), and [validation plan](../../testing/ui/native-ui-validation.md) for evidence and limits. ImGui remains the default editor.

## Open Code Items

### Phase 4: Persistent Render Data And Clipping

- [ ] Complete hierarchical clipping for direct-world render commands. `UIClipRegion`, `UIBatchCollector`, `UIRenderableComponent`, and render backends. Screen and offscreen 2D records already carry nested crop independently of material identity. The bounded combined-program OpenGL route now uses frozen canvas-local fragment crop. The canonical Vulkan route now checks compiled stages, reflected members, exact descriptor offsets and mapped crop/model bytes before indexed issue. Cropped custom/stereo and arbitrary 3D override profiles remain open. Done when the remaining world route clips nested regions through a defined OpenGL and Vulkan contract, and publication tests preserve mixed batch/fallback painter order. Native acceptance remains in the validation plan.

### Phase 7: Editor Adoption

- [ ] Complete native scene view, asset browser, console, profiler, animation, and other panels through shared controls. Native asset row menus route Open Folder/Inspect, Rename, and Reimport through existing panel paths, with row and rename-draft admission. Open/Show in Explorer uses the shared file-browser helper after request and selection checks. Presented table selection updates the asset path and file inspector; folder opening remains an activation action. Console categories/actions and cached log formatting are implemented. Profiler snapshot indexing is bounded and row formatting is lazy. Component, spike, GPU summary, engine diagnostic, and setting views are implemented. The Settings view also starts and stops the shared speed profile capture with an editable duration. Detailed profiler diagnostics and the animation authoring commands are implemented; broader clone profiles remain open. Applied clone history of an ended document is released without destruction and no longer blocks Duplicate. The approved asset creation recovery journal is implemented; the native asset browser creates assets through it and has explicit recovery accept and cleanup commands. Applied structural-only clone history now hands live adopted objects to exact authored scene owners, retires only admitted private built-in constructor storage, and releases its construction ledger. Unsupported assets, custom private transforms, and runtime-only roots remain open. Shared scene actions preserve ordered membership, independent loaded state, selection intent, and source-document dirty state through Undo/Redo. Done when primary panels are functional and avoid rebuilding unbounded strings or row trees. Resource event and shadow-atlas details are implemented. Detailed Vulkan stage, causal-wait, and descriptor diagnostics use bounded rows; frame-output pages retain access to every output. CPU/GPU root history plots use virtual cells. Render Stats also shows per-frame history rows and plots for Vulkan churn, Vulkan frame timing, and OpenXR timing. The FPS spike rows sort by any column in either order. The footer trend draws frame-budget guide lines. The footer trend shows the hovered sample, its age, and the window statistics with a marker line. The rolling worst CPU hierarchy, stale-root persistence, and guarded multi-child loop edits are implemented. Presented plot selection, key insertion, pan, bounded member lanes, method/attached-animation detail, and source subscription refresh are implemented. Channel editing and built-in baked/frame-cadence length adapters are implemented. Wheel zoom is anchored to the presented pointer, Ctrl+wheel zooms the curve graph value axis, and the time axis scrubs to whole frames. The panel follows the inspected clip, so it needs no separate "Use inspector clip" command. Exact length, sample rate, root normalization, and clip path copy commands are implemented. A hover row shows the member, time, frame, and value of the key under the pointer. Length and stretch edits use guarded batch receipts.
- [ ] Switch the default editor to native only after the validation plan passes and owner review accepts the stabilization result. Keep ImGui available as a compatibility/debug option.

## Approved Policies And Remaining Gate

The owner approved the paragraph analyzer choice, [native asset recovery proposal](../../design/ui/native-asset-creation-recovery-design.md), and [primary window layout key proposal](../../design/ui/native-window-layout-key-compatibility-design.md) on 2026-10-26. Use Windows DirectWrite paragraph analysis without a new package. The recovery journal must keep existing asset formats unchanged. Primary-window compatibility must preserve old layout records and reject ambiguous matches. Approval does not complete the remaining implementation or validation.

The same approval sets these implementation policies:

- Use variable-height rows for multiline content and an explicit measured-height contract for custom editors.
- Use bindless UI images only on backends that support that capability.
- Direct-world UI does not cast shadows.
- Keep ImGui as the default until Windows, graphics, and input acceptance passes and final owner review approves the switch. That final review remains pending.

## Out Of Scope

- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
