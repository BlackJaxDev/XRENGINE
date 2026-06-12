# XREngine Editor Undo System

The editor ships with a shared undo/redo stack that records every property mutation raised through `XRBase.SetField` (and its variants). This file walks through how the system works, what gets tracked automatically, and how to hook additional functionality into the stack.

## Quick Start

1. **Initialization** – `Undo.Initialize()` is called during editor startup (`Program.Main`). The method registers keyboard shortcuts and prepares the global history stacks.
2. **Automatic tracking** – All objects deriving from `XRBase` are eligible. Call one of the helper methods when an object enters the editor:
   - `Undo.Track(xrBaseInstance)` for a single object.
   - `Undo.TrackSceneNode(SceneNode)` to track a node, its components, and transform hierarchy.
   - `Undo.TrackScene(XRScene)` or `Undo.TrackWorld(XRWorld)` for whole scenes/worlds.
3. **User interaction scopes** – Only edits performed while a user interaction scope is active are recorded. Wrap UI processing with `using var ui = Undo.BeginUserInteraction();` (the provided ImGui helpers and transform tool adapter already do this).
4. **Keyboard shortcuts** – `Ctrl+Z` triggers `Undo.TryUndo()`, `Ctrl+Y` or `Ctrl+Shift+Z` triggers `Undo.TryRedo()`.
5. **Menu access** – The ImGui menu bar now exposes Undo/Redo commands and a history flyout, and the toolbar Edit menu mirrors the same options.

## When a property change is recorded

- The change must flow through `XRBase.SetField`, `SetFieldReturn`, or `SetFieldUnchecked`. These helpers raise `PropertyChanging`/`PropertyChanged` events which the undo system listens to.
- The recorded delta stores the *previous* and *new* value. Undo sets the property back to the previous value by reflection; redo reapplies the new value.
- The history retains a timestamp and a friendly description (`"ObjectName: Property"`).

## Grouping complex edits

Wrap multi-step operations in a change scope to commit them as a single undo entry:

```csharp
using var ui = Undo.BeginUserInteraction();
using var scope = Undo.BeginChange("Duplicate Selection");

// Modify several XRBase instances here

// Optional: cancel the scope if you decide nothing meaningful changed
// scope.Cancel();
```

Calling `Dispose` on the scope (via `using`) submits the grouped change unless `Cancel()` was called or no mutations were detected.

## Manual recording

If you must track a change that does **not** raise `PropertyChanged` (for example, a collection mutation performed outside `EventList`), you can:

1. Convert the code to use an `XRBase` property backed by `SetField`.
2. Or, explicitly enqueue a delta:

```csharp
using var ui = Undo.BeginUserInteraction();
using var scope = Undo.BeginChange("Add waypoint");
// Perform work here
Undo.Track(myNewWaypoint);               // ensure future edits are tracked
// When scope leaves, an undo entry exists even if only manual state changed
```

The system currently exposes only property-based steps; for completely custom actions, consider wrapping the operation in higher-level commands and raising synthetic properties to describe the change.

## Accessing history

- `Undo.PendingUndo` – snapshot of the undo stack (most recent entry first).
- `Undo.PendingRedo` – snapshot of redoable steps.
- `Undo.HistoryChanged` – event raised after the stacks mutate. Use this to refresh UI, as done by the toolbar and ImGui menu.
- `Undo.CanUndo` / `Undo.CanRedo` – convenience flags.

## Best practices

- **Track early**: Call `Undo.Track`/`TrackSceneNode` as soon as an editor feature spawns or loads XR objects. Untracked objects will not produce undo steps until they are registered.
- **Dispose dependencies**: `Undo` automatically unsubscribes from tracked objects when they are destroyed (`XRObjectBase.Destroyed`). If you manage disposable editor-only wrappers, tie their lifetime to tracked objects or call `Undo.Untrack` explicitly.
- **Limit history spam**: Use change scopes in tools that update many properties in tight loops (gizmos, procedural edits) so the stack remains readable.
- **UI updates**: subscribe to `Undo.HistoryChanged` to update inspector widgets or status bars with the latest description or counts.

## Extending the system

The current implementation focuses on property-level deltas. To extend it:

- Add custom step types (e.g., collection mutations) by introducing a new `UndoAction` subtype that knows how to run `Undo`/`Redo`. The existing `UndoAction` class can be refactored to support heterogeneous steps.
- Surface history in other panels by consuming `Undo.PendingUndo` and rendering the list (as demonstrated in the ImGui menu and toolbar integration).
- Persist history between sessions by serializing `UndoEntry` instances when saving projects.

If you build additional tools on top of the undo stack, keep the core invariants intact: undo must be deterministic and run without firing new undo records, and redo must be the exact inverse of undo.
