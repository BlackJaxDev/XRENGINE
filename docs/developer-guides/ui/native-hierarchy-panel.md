# Native Hierarchy Panel

The native hierarchy panel is the native UI tree editor for worlds, scenes, and scene nodes. The ImGui hierarchy remains the default day-to-day editor path, but the native hierarchy now has the tree-editing features needed for production UI work.

This feature doc promotes the completed porting plan from `docs/work/design/UI/native-hierarchy-porting-plan.md`.

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

Hierarchy rows are generated from the current world and scene graph, then flattened for scrolling. Collapsed node IDs and collapsed scene sections are tracked separately so the tree can rebuild quickly while preserving user state.

Selection uses the editor-wide `Selection` state. Plain click selects one node; modifier clicks update the selected set. The row highlight model follows the active selection so native UI and editor state stay synchronized.

Right-click opens a native `UIContextMenuComponent` with common hierarchy actions such as rename, delete, create child node, and focus camera. Inline rename uses single-line text input events so Enter commits and Escape cancels.

## Drag And Drop

Scene-node payloads can be dropped onto hierarchy rows to reparent nodes. The panel rejects ancestor cycles before enqueueing the edit.

Asset-path payloads allow prefab or model assets to be dropped into the hierarchy. The native asset explorer and hierarchy share the payload helpers in `EditorDragDropUtility`.

## Native UI Dependencies

The hierarchy uses reusable native UI support that is now available outside the panel:

- right-click and modifier-key tracking in `UICanvasInputComponent`,
- `UIContextMenuComponent` for popup menus,
- single-line submit/cancel support in `UITextInputComponent`,
- hierarchy-specific styling in `EditorStyles`.

## Implementation References

- `XREngine.Editor/UI/Panels/HierarchyPanel.cs`
- `XREngine.Editor/UI/EditorDragDropUtility.cs`
- `XREngine.Editor/UI/EditorStyles.cs`
- `XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Interactable/UIContextMenuComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Core/Interactable/UITextInputComponent.cs`
