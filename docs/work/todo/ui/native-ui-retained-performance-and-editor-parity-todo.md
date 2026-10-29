# Native UI Retained Performance And Editor Parity TODO

Last Updated: 2026-10-29
Status: Active
Architecture: [UI architecture](../../../architecture/ui/README.md)  Design: [Native UI retained rendering design](../../design/ui/native-ui-retained-rendering-design.md)
Validation: [Native UI validation](../../testing/ui/native-ui-validation.md)

## Current State

The code reconciliation is **59 of 60 implementation items complete**; only the default-editor switch remains, and it waits for the validation plan and owner review. Worker layout, retained screen publication, common controls, virtual collections, and native panels are implemented. Guarded scene-root and structural-list ownership, profiler views, Vulkan image/text cache contracts, and an explicit canvas-only OpenGL world route are implemented. Text layout results are cached by all glyph-placement inputs. Native UI failures are reported by code through `UIFailureDiagnostics`. Publication is complete: sparse capture visits only changed subtrees, count-changing edits splice only touched record pages, and the batch collector visits only new record pages. Headless tests and managed editor/test compilation pass. Text fields place the caret from the presented layout. Direct-world clipping is implemented on the frozen and live routes, and the Advanced pipeline admits direct-world UI draws to its late passes; native panel parity is implemented, and live Windows acceptance is tracked in the validation plan. The owner approved the remaining policy proposals on 2026-10-26. See the [checkpoint record](../../progress/ui/retained-ui-checkpoint.md), [retained execution](../../../architecture/ui/05-Retained-Execution.md), and [validation plan](../../testing/ui/native-ui-validation.md) for evidence and limits. ImGui remains the default editor.

## Open Code Items

### Phase 7: Editor Adoption

- [ ] Switch the default editor to native only after the validation plan passes and owner review accepts the stabilization result. Keep ImGui available as a compatibility/debug option.

## Approved Policies And Remaining Gate

The owner approved the paragraph analyzer choice, [native asset recovery proposal](../../design/ui/native-asset-creation-recovery-design.md), and [primary window layout key proposal](../../design/ui/native-window-layout-key-compatibility-design.md) on 2026-10-26. Use Windows DirectWrite paragraph analysis without a new package. The recovery journal must keep existing asset formats unchanged. Primary-window compatibility must preserve old layout records and reject ambiguous matches. Approval does not complete the remaining implementation or validation.

The same approval sets these implementation policies:

- Use variable-height rows for multiline content and an explicit measured-height contract for custom editors.
- Use bindless UI images only on backends that support that capability.
- Direct-world UI does not cast shadows.
- Clone history for runtime-only roots, unsupported assets, custom private transforms, and foreign owners stays retained. It is released with a warning when its document ends (owner decision, 2026-10-28).
- Keep ImGui as the default until Windows, graphics, and input acceptance passes and final owner review approves the switch. That final review remains pending.

## Out Of Scope

- ImGui optimization.
- Baseline recording archives, benchmark dashboards, and performance-history snapshots.
- Silent CPU fallbacks for explicitly accelerated UI paths.
- Preserving legacy native UI contracts solely for compatibility before v1.
