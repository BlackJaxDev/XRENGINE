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

## Inspector

For one selected scene node, the inspector shows a stacked view like the ImGui
inspector (`InspectorPanel.BuildStackedNodeLayout`):

- the node summary: name, active state, layer, and the Transform header,
- the members that the node's transform type declares,
- one section per component. Each section starts with the component's active
  toggle, named after the component, and lists the members that the component
  type declares below `XRComponent`.

Each row keeps the object that it edits, so a row can edit the transform or a
component instead of the inspected node. "Open details" on such a row inspects
that object; the Properties button returns to the node. Two components of the
same type share their property objects, so only the first gets a section.
Several selected nodes use the plain list of their shared members.

## Layout Rules

- A stretched axis (`MinAnchor` and `MaxAnchor` differ on that axis) does not move
  by `Translation`. Only the margins move it. Indent a stretched label with its
  left margin.
- With stretched anchors, `Width` and `Height` add to the parent extent.
  `NativeUIElements.CreateButton` uses point anchors on an axis that has a set
  size, so the button has exactly that size.
- A box anchored at the right edge needs `NormalizedPivot.X = 1` to end at the
  anchor instead of starting there.

## Known Limitations

- The Windows symbol font fails to import as a bitmap atlas at the default draw
  size, so icon text uses the bundled UI font. That font has no triangle or check
  glyphs. The hierarchy uses `>` and `v` marks and quad checkboxes.
- On Vulkan, a screen canvas can stop drawing with `UI.Canvas.BatchCapacityPending`
  when small hierarchy glyph texts use the same font instance as the row labels.
  The hierarchy glyph texts use `FontGlyphSet.LoadDefaultUIIconFont()` to avoid
  this. The root cause is open.
