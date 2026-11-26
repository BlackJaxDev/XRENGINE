# Native Editor Layout And Theme

The native editor builds its window from retained UI: a menu bar, a dock tree of
panel stacks, and the panels. This guide describes how the scene is presented,
how the glass theme is applied, and the layout rules that the editor code must
follow. ImGui remains the default editor.

## Scene Presentation

The editor preference `ViewportPresentationMode` selects how the scene shows.
The ImGui editor reads the same preference.

- `FullViewportBehindImGuiUI` (default): the window viewport renders the scene
  across the whole window. The panels float over the scene as translucent cards.
  The Scene panel is a clear region. Its `NativeSceneViewportInput` converts the
  pointer position from the panel to the window viewport, so picking, gizmos, and
  camera input use the window viewport.
- `UseViewportPanel`: the Scene panel creates a `UIViewportComponent` and renders
  the scene inside the panel. The panels sit on a gradient window backdrop.

The mode applies when the editor UI is built. `SceneViewPanel.IsSceneDockedInPanel`
reports it.

### Docked Scene Viewport

A docked Scene panel sets `UIViewportComponent.IsPrimarySceneView`. This setting:

- requests a `DesktopScene` pipeline. The Advanced pipeline does not reserve an
  output for an `OffscreenCapture` request that has no offscreen intent.
- turns on the viewport's own frame-paced collect and swap callbacks. The desktop
  view history accepts a frame only when the collect and the render share one
  frame output request.
- renders into the published surface in place. A new surface for each frame
  gives the UI draw a new captured material each frame, and the Vulkan backend
  compiles a program for each new material.
- flips the quad UVs on Vulkan, because Vulkan framebuffer textures have row 0 at
  the top of the image.

In this mode the window viewport still renders the same scene under the UI.

## Glass Theme

`XREngine.Editor/UI/EditorGlassTheme.cs` holds the colors, sizes, and styles of
the native editor. `EditorUI.Styles` reads its defaults from the theme, so
controls that set colors directly match the styled controls.

- Each dock stack sits on a rounded card (`PanelCard`). A stack that shows the
  scene behind the UI uses `ClearCard`.
- `SceneBehindPanels` turns on the backdrop blur of the cards and the menu bar.
  The pipeline backdrop blur samples the scene, so the cards blur only when the
  scene is behind them.
- Colors are sRGB and the styles decode them to linear. Alpha is linear coverage,
  and blending is linear, so white overlay fills use small alpha values.

## Vector Icons

The UI quad shader can draw a vector icon instead of a box. Set
`UIStyleProperties.Icon` (or `UIQuadStyle.Icon`) to an `EUIQuadIcon` value:
`ChevronRight`, `ChevronDown`, `Check`, `Minus`, `Cross`, `Search`, or `Plus`. The icon value uses
bits 5 to 7 of the style flags (`EUIQuadStyleFlags.IconMask`).

- The quad fill color is the icon color.
- A border width above zero sets the stroke width in pixels. Otherwise the stroke
  width follows the quad size.
- The shader computes the shape from line segments, so the icon is sharp at every
  size and needs no font glyph.

The hierarchy rows and the inspector section rows use chevron icons for their
disclosure marks.

## Inspector

For one selected scene node, the inspector shows a stacked view like the ImGui
inspector (`InspectorPanel.BuildStackedNodeLayout`):

- the node summary: name, active state, layer, and the Transform header,
- the members that the node's transform type declares,
- one section per component. Each section starts with the component's active
  toggle, named after the component, and lists the members that the component
  type declares below `XRComponent`.

Each row keeps the objects that it edits, so a row can edit the transform or a
component instead of the inspected node.

Several selected nodes (up to 64) also get the stacked view, like the ImGui
multi-node inspector (`InspectorPanel.BuildStackedMultiNodeLayout`). Each row
edits the matching object of every node: the shared node members, the members
of the common transform type, and one section for each component type that every
node has exactly once. A field shows a dash (`EditorTextPropertyBinding.MixedValueText`)
where the objects have different values, and writing the dash back changes
nothing. A vector shows dashes in all its fields when the whole vectors differ.

- Primitives, enums, strings, vectors, rotations, colors, and layer masks have
  inline editors in their row (`InspectorPanel.HasInlineEditor`). A vector or
  rotation editor binds its fields to a transformer component. A row rebind
  changes only the transformer target.
- A writable component, scene node, transform, or asset reference gets an
  object field (`InspectorObjectField`): the object name in a field well, a
  picker, and an arrow button that opens the object in the inspector. The picker
  lists "None" and the matching objects (`InspectorObjectCandidateSource`):
  components, nodes, or transforms in the edited objects' world, or the asset
  files of the type in the game and engine asset folders. The list is built when
  the picker opens, not when the row binds. A picked asset file loads through the
  same path as a dropped asset. A drop on the row also assigns a component or an
  asset.
- An event member (`XREvent` or `XREvent<T>`, property or public field) shows
  its callback count and a plus button that adds a persistent callback. Each
  callback gets its own row (`InspectorEventCallbackRow`), like a row of the
  ImGui callbacks table: a node picker in the name column, a method picker in the
  value column, and a remove button. The pickers and the undo records use the
  same helpers as ImGui (`EditorImGuiUI.EventCallbackAccess`). Event fields are
  wrapped as properties (`InspectorFieldPropertyInfo`) to get rows.
- A type with a custom ImGui editor can have a member profile
  (`InspectorMemberProfiles`): the members that the ImGui editor draws, in the
  same order and with the same labels. The standard transform shows
  Translation, Rotation (the `Rotator` in degrees), Scale, and Order. The rigid
  body transform shows its interpolation mode, offsets, and physics pose. A type
  derived from a profiled type also lists the members that it declares. Custom
  ImGui component editors that draw more than members, such as camera previews,
  have no native form yet.
- Other members show their value as a link row: the object name, the item count
  of a collection, the type name, `None`, or a dash when the selected objects
  have different values. A chevron at the right shows that a click opens the
  value. A link row of the transform or a component inspects that object; the
  Properties button returns to the node.
- The search field at the top of the inspector filters the rows while the user
  types. A member matches when its property name, display name, or category
  contains the text, as in the ImGui inspector. A section stays when its title
  or one of its members matches; a title match keeps all its members. A search
  opens every section. Escape clears the field.
- Click a section header row to collapse or open the section. A chevron in the
  gutter at the left of the name column shows the state. The inspector keeps the
  collapse state by section key: `Transform`, or the component type name.
- A component header row shows the active checkbox after the chevron and before
  the title, like the ImGui component header. The title can run across the value
  column, because a header row has no value editor.
- The name column is a container cell (`UIVirtualTableColumn<T>.Text` with a
  leading inset). The label clips to its own bounds, and a clip applies to all
  children of the clipped node, so the chevron and the checkbox are siblings of
  the label, not its children.
- Vector and rotation fields share the row width equally. Text fields, enum
  select fields, and link rows use the glass field style
  (`NativeUIElements.ApplyFieldStyle`).
- Rows are keyed by `PropertyInfo`, and two components of the same type share
  their property objects. The later components use `InspectorScopedPropertyInfo`
  copies, which forward to the real property but have their own identity.
  `EditorPropertyDelegates` unwraps them before it builds accessors.

## Layout Rules

- A stretched axis (`MinAnchor` and `MaxAnchor` differ on that axis) does not move
  by `Translation`. Only the margins move it. Indent a stretched label with its
  left margin.
- With stretched anchors, `Width` and `Height` add to the parent extent.
  `NativeUIElements.CreateButton` uses point anchors on an axis that has a set
  size, so the button has exactly that size.
- A box anchored at the right edge needs `NormalizedPivot.X = 1` to end at the
  anchor instead of starting there.

## Draw Count

Each UI run is one Vulkan mesh request in the UI lane. Text and material quads
alternate in painter order, so a dense panel needs several hundred runs. The UI
lane of `VulkanMeshOperationRequestQueue` matches the UI capacity of the
accepted frame plan (`VulkanAcceptedFramePlan.UiCapacity`). If a frame needs more
UI requests, the renderer stops with `FramePlanCapacityExceeded lane=Ui`. To keep
the run count low, create an icon node before the label node of the same
control, so the icon quad follows the background quad in one run.

## Known Limitations

- The Windows symbol font fails to import as a bitmap atlas at the default draw
  size, and the bundled UI font has no triangle or check glyphs. Use vector icons
  for these marks.
- On Vulkan, a screen canvas can stop drawing with `UI.Canvas.BatchCapacityPending`
  when small hierarchy glyph texts use the same font instance as the row labels.
  The hierarchy glyph texts use `FontGlyphSet.LoadDefaultUIIconFont()` to avoid
  this. The root cause is open.
