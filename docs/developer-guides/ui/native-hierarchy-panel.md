# Native Hierarchy Panel

The native hierarchy edits worlds, scenes, and scene nodes through retained UI.
ImGui remains the default editor. Native workflow and graphics acceptance remain
open in the [validation record](../../work/testing/ui/native-ui-validation.md).
See the [implementation TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)
for remaining editor parity work.

## Capabilities

The panel supports:

- virtualized flat-list rendering with depth indentation,
- expand and collapse state for branch nodes,
- single selection and modifier-based multi-selection,
- double-click camera focus,
- drag-and-drop reparenting with ancestor checks,
- active/enabled toggles per node,
- right-click context menus,
- inline rename with submit/cancel behavior,
- scene-grouped sections and world header information,
- optional editor-scene visibility,
- dirty tracking for hierarchy edits,
- and asset-path drops from the asset browser.

## Interaction Model

The hierarchy uses `UIVirtualTreeProvider` and the fixed-height virtual list. The
list realizes viewport rows plus overscan and rebinds stable row instances.
Collapsed node IDs and scene sections remain in the data model. Child-list
notifications update subtree ranges. Plain scene/root lists use a shallow
membership check every 250 ms. Routine refresh does not construct the full row
tree or apply a 2,000-row cap.

Selection uses the editor-wide `Selection` state. Plain click selects one node; modifier clicks update the selected set. The row highlight model follows the active selection so native UI and editor state stay synchronized.

Right-click on a node opens a native `UIContextMenuComponent` with Rename, Add
Child, Focus Camera, Duplicate, Deep Duplicate, and Delete. Deep Duplicate asks for
confirmation first. Right-click on a scene section shows or hides the scene, or
unloads it. These actions match the ImGui hierarchy menus. Inline rename uses
single-line text input events, so Enter commits and Escape cancels.

Create, delete, reparent, and move-to-root edits go through `HierarchyMutation`
and record one undo action. Each edit keeps the exact scene memberships of the
root, including hidden and editor scenes. When the first editor-only child moves
to the root, the editor creates the editor scene.

## Drag And Drop

Scene-node payloads can be dropped onto hierarchy rows to reparent nodes. The panel rejects ancestor cycles before enqueueing the edit.

Asset-path payloads allow prefab or model assets to be dropped into the hierarchy.
Hierarchy and viewport drops use the same spawn path,
`EditorImGuiUI.TryHandleDroppedSpawnableAsset`. A drop target can be the world
root, a node in the world, or a node in a hidden scene. A prefab spawned under a
hidden-scene parent stays detached from the world, like its parent. The native
asset explorer and hierarchy share the payload helpers in `EditorDragDropUtility`.

## Native UI Dependencies

The hierarchy uses reusable native UI support that is now available outside the panel:

- right-click and modifier-key tracking in `UICanvasInputComponent`,
- `UIContextMenuComponent` for popup menus,
- single-line submit/cancel support in `UITextInputComponent`,
- hierarchy-specific styling in `EditorStyles`.

## Implementation References

- `XREngine.Editor/UI/Panels/HierarchyPanel.cs` and its `HierarchyPanel.*.cs` partial files
- `XREngine.Editor/UI/EditorDragDropUtility.cs`
- `XREngine.Editor/UI/EditorStyles.cs`
- `XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Interactable/UIContextMenuComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Core/Interactable/UITextInputComponent.cs`
