# UI Rendering Pipeline

The native UI uses `UICanvasComponent`, `UITextComponent`, and the engine's mesh
submission path. `UIBatchCollector` combines compatible retained primitives.
ImGui remains the default editor. See [retained execution](05-Retained-Execution.md)
for ownership details and [validation](../../work/testing/ui/native-ui-validation.md)
for backend acceptance.

## Screen canvas flow

The canvas owns an orthographic `Camera2D`, `VisualScene2D`, and UI render pipeline.
The camera maps the canvas's bottom-left pixel coordinates into its output.

1. The update owner requests worker layout and pumps completed results.
2. A valid accepted layout permits capture of one `UIPublication`: geometry,
   render records, clip regions, hit records, focus scopes, and scroll ancestry.
3. Collection reads that publication's ordered render records. It reuses compatible
   retained batch runs and inserts frozen commands for unbatched primitives.
4. Render submission retains the exact publication and resources used by its draws.
5. An accepted output receipt advances that output's input publication.

An incomplete draw capture leaves the previous complete publication active.
Input does not advance merely because the CPU finished collecting commands.

## Membership and order

`RenderInfo2D.MembershipId` is stable when painter-order fields change.
`VisualScene2D` supplies captured registration order. Mutable quadtree entries are
not the source of displayed hit geometry; input uses the accepted output packet.

Batch compatibility includes primitive semantics, shader/material features,
texture and atlas identity, sampler and raster state, clipping, and render pass.
An incompatible primitive ends a painter run. Custom shaders use owned normal
mesh commands unless they declare supported batch semantics. Missing resources
and unsupported capture callbacks produce named diagnostics.

## Primitive and text storage

Solid, border, image, and text records carry per-instance values. Stable handles
prevent recycled primitive slots from referring to removed elements. GPU cohorts
retain buffers until both native use and reusable command references permit reuse.
Dirty ranges limit uploads. A capacity or preparation failure rejects a candidate
instead of silently replacing a requested accelerated path.

Text layout publishes glyph geometry independently of color, transform, and
outline values. Stable glyph ranges and an index stream let a changed text run
move without repacking unrelated runs. Atlas identity stays in the batch key.
Required texture bindings reject unready descriptors instead of accepting a
placeholder as complete UI output.

Static image and atlas records retain owned `UITextureSnapshot` copies. Unbatched
draws retain owned mesh buffers, shader sources, numeric uniforms, and raster
state. Video, web, and viewport outputs require producer-generation leases.
See retained execution for the explicit pixel mutation and adapter contracts.

## Clipping

`UIClipRegion` resolves ancestor clips into publication records independently of
material identity. Batch runs preserve clip boundaries. Backend scissor mapping
uses output origin and scale. Nested clipping, transformed canvases, and separate
OpenGL and Vulkan visual results remain required acceptance checks.

## Output receipts

`XRWindow` and `VulkanUiPublicationReceiptLedger` track exact frame, output image, and
output generation. The pure receipt state rejects missing draws, unprepared or
unrecorded work, and replay without an accepted source image. Artifact retirement
and GPU completion are separate conditions for resource reuse.

Headless receipt tests verify state transitions. They do not prove native command
recording, presentation, or replay correctness on a GPU.

## Camera and world canvases

Non-screen canvases retain camera/canvas matrices for ray-plane input mapping.
They support offscreen texture composition and direct scene rendering. Offscreen
world canvases use render-command publication claims and separate producer and
consumer receipts. Direct world UI uses the existing component route, unless a
bounded OpenGL or Vulkan profile admits an exact frozen replacement. In these
profiles, canonical solid, image, and text draws can carry a frozen canvas-local
crop. Custom cropped shaders, cropped stereo text, and arbitrary 3D material or
raster overrides stay outside these profiles.

The existing component route also applies the nested crop. When a direct-world
element or a clipping ancestor has a crop, `UIRenderableComponent` sets an owned
clip material as the `RenderCommand3D` material override. The clip material uses
the source shaders, with the canonical fragment shader replaced by its
`UIWorldCropShaderContract` crop variant. Variants are cached for each source
shader and its source revision. The clip material owns no shader parameters. Its
uniform callback writes the source parameters, the component bounds,
`UiWorldCropMinMax`, and `UiWorldModelToCanvas` on each draw, so a program that
two materials share always gets current values. The crop is calculated again
when the element matrix, the element or ancestor region, `ClipToBounds` on the
element or an ancestor, the mesh, the material, or the canvas route changes.
Stereo text vertex shaders are part of the canonical set, so this route also
clips stereo text. An element that has an authored 3D material override, a
non-canonical shader set, a reserved crop uniform, or an out-of-range crop keeps
its unclipped draw. It reports `UI.WorldClip.OverrideUnsupported`,
`UI.WorldClip.ShaderRequired`, `UI.WorldClip.UniformConflict`, or
`UI.WorldClip.Range` through `UIFailureDiagnostics` once for each change of state.
The Advanced pipeline admits a late-pass draw only when its material has
explicit `AdvancedLatePassMetadata`. UI materials keep the opaque transparency
mode that batch lowering requires, so a direct-world element gives its material
the shared `EAdvancedLatePassKind.UserInterface` metadata when the material has
none (`UIAdvancedLatePass`). The late-pass validator admits this kind in
`TransparentForward` and `OnTopForward`, also for an opaque mode. The clip
material copies the metadata of its source.

Direct-world draws without checked
indexed-draw proof increment `UIWorkCounter.WorldUncheckedDraws`. An offscreen
framebuffer completion alone does not prove that a world-space quad reached a
presented window. Do not infer the screen canvas's publication guarantees for
routes outside these profiles.

## Limits and diagnostics

`UICompositionLayerComponent` divides publication output into composition
segments. Vulkan can admit supported screen-space chrome runs to its secondary
command cache. `UIBackdropBlurComponent` shares one canvas-prefix capture on a
supported OpenGL screen canvas; it is disabled by default. `UIImageAtlas` packs
small images into fixed pages. See [retained execution](05-Retained-Execution.md)
for the limits of each path. GPU acceptance of these paths is unverified. Native
editor parity remains in the
[code TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md).
No faster-than-ImGui claim follows from the retained architecture alone.

Set `XRE_UI_BATCH_DIAGNOSTICS=1` only when collecting text and batch diagnostics.
Use equivalent workloads and the validation plan to compare CPU work, allocations,
latency, and GPU upload volume.
