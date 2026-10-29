# Native UI Architecture

The native UI uses retained scene components and the existing `UITextComponent`
renderer.
ImGui remains the default editor while native UI acceptance is open.

| Document | Scope |
|---|---|
| [Retained execution](05-Retained-Execution.md) | Current worker layout, publication, input, batch storage, controls, and virtual data |
| [Change tracking](04-Change-Tracking.md) | Identities, change domains, transaction journal, and work counters |
| [Layout reference](01-Architecture.md) | Value-based measure/arrange, owner and worker boundaries, and extension points |
| [Transform reference](02-Transform-Types.md) | Transform properties and layout behavior |
| [Rendering reference](03-Rendering-Pipeline.md) | Retained canvas collection, resource ownership, clipping, and output receipts |
| [Implementation TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md) | Open implementation items and owner decisions |
| [Validation](../../work/testing/ui/native-ui-validation.md) | Build, runtime, visual, performance, and backend acceptance |
| [Saved window ownership](05-Retained-Execution.md#saved-native-window-ownership) | Conservative legacy-key matching and guarded saves |

The historical layout audit remains available in
[Native UI Layout Known Issues](../../work/audit/COMPLETED/native-ui-layout-known-issues.md).
Its findings are historical evidence, not a statement that both old layout paths
still execute. Production canvas layout now requests the numeric worker kernel
and applies only validated results on the owner thread.
