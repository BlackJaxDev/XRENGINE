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
| [Saved window ownership](05-Retained-Execution.md#saved-native-window-ownership) | Conservative legacy-key matching and guarded saves |

Production canvas layout requests the numeric worker kernel and applies only
validated results on the owner thread.
