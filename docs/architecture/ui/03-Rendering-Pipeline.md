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

`XRWindow` and the Vulkan receipt ledger track exact frame, output image, and
output generation. The pure receipt state rejects missing draws, unprepared or
unrecorded work, and replay without an accepted source image. Artifact retirement
and GPU completion are separate conditions for resource reuse.

Headless receipt tests verify state transitions. They do not prove native command
recording, presentation, or replay correctness on a GPU.

## Camera and world canvases

Non-screen canvases retain camera/canvas matrices for ray-plane input mapping.
They support offscreen texture composition and direct scene rendering. Exact
producer-to-consumer receipts and complete frozen direct-world submission remain
open implementation work. An offscreen framebuffer completion alone does not
prove that a world-space quad reached a presented window. Do not infer the screen
canvas's publication guarantees for these unfinished paths.

## Limits and diagnostics

Independent composition layers, reusable static backend commands, shared backdrop
captures, small-image atlasing, and full native editor parity remain in the
[code TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md).
No faster-than-ImGui claim follows from the retained architecture alone.

Set `XRE_UI_BATCH_DIAGNOSTICS=1` only when collecting text and batch diagnostics.
Use equivalent workloads and the validation plan to compare CPU work, allocations,
latency, and GPU upload volume.
