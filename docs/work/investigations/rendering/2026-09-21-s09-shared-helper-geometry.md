# S09: Shared Immutable Helper Geometry

Date: 2026-09-21
Base revision: `ab02793f9`
Scope: [S09 in the Vulkan stall remediation TODO](../../todo/rendering/vulkan-stall-remediation-todo.md#s09-share-helper-geometry-only-when-justified)
Status: **Validated for fullscreen helper geometry.**
Test clearance: not granted; no regression tests were added or run.

## Entry evidence and bounded decision

`XRQuadFrameBuffer` constructed a new fullscreen triangle or two-triangle quad
for every consumer even though the positions never change. A source inventory
found 93 `XRQuadFrameBuffer` construction/definition matches and separate
fullscreen-shaped meshes in skybox, grid, atmosphere and Advanced-pipeline
passes. Those other helpers do not share the same topology, culling, instancing,
shader or lifetime contract, so this item deliberately changes only
`XRQuadFrameBuffer` geometry.

The falsifiable hypothesis was that one CPU mesh per fullscreen topology could
serve all simultaneously live framebuffer consumers while renderer, material,
shader callback, version and stereo state remained consumer-local. The change
would be rejected if wrappers or mutable render state crossed consumers, if the
last lease could destroy geometry still referenced by a renderer, if output
changed, or if recreation after teardown failed.

## Ownership and lifetime design

`SharedRenderHelperGeometry` owns two cache slots: the oversized fullscreen
triangle and the compatibility two-triangle quad. Geometry is immutable by
contract, not frozen by the `XRMesh` type; callers receive an exact reference
lease and must neither mutate nor destroy its mesh.

- Construction is serialized per slot but runs outside the cache lock. Waiting
  callers either acquire the published mesh or observe the construction failure.
- Every `XRQuadFrameBuffer` still creates its own `XRMeshRenderer` and keeps its
  material, uniforms, shader versions, wrapper generations and `ForceOvrMultiview`
  setting independent.
- Framebuffer teardown first blocks new preparation/enqueue work and destroys its
  renderer. The lease is released only after that renderer reaches terminal
  destruction, including a destruction request that has to drain queued work.
- The last lease tombstones the cache entry under the lock, then destroys the CPU
  mesh outside the lock. A later acquisition builds a new generation.
- Construction failures release the acquired lease and destroy any partial
  consumer renderer. There is no silent private-mesh fallback.

This ordering preserves the S07 owner-scoped backend wrapper and frozen-work
retirement model: only logical CPU geometry is shared, while every backend/native
resource remains owned by its consumer and renderer context.

## Validation evidence

The final `XREngine.Runtime.Rendering` build completed with zero warnings and
zero errors. An isolated editor build from the exact final source also completed
with zero warnings and zero errors.

### Vulkan Advanced

- Cold startup peaked at 29 simultaneous fullscreen-triangle leases and one
  compatibility-quad lease. Each topology constructed once.
- After clearing the render-pipeline cache, 58 textures and 58 framebuffers were
  retired. Triangle consumers released and reacquired the still-live shared mesh;
  the final counter reached 60 acquisitions, one construction and 59 avoided
  constructions.
- The compatibility quad reached zero leases, retired, and constructed a second
  generation when requested again. This exercised the last-release and recreate
  path rather than retaining the mesh forever.
- A Vulkan renderer restart completed without rollback or shared-geometry lease
  failures. Screenshots before and after cache recreation remained rendered and
  changed with the editor camera.

### OpenGL and pipeline coverage

OpenGL Default produced visible, camera-dependent output with 27 fullscreen-
triangle consumers served by one construction and 26 avoided constructions.
OpenGL Advanced also booted and exercised the same helpers, although its captured
scene output remained black with editor UI; that pre-existing visual behavior is
not attributed to S09. Vulkan Advanced and OpenGL Default therefore provide the
accepted cross-backend visual smoke, while the code keeps no backend state in the
shared cache.

The live cache-clear and renderer-replacement paths exercise context/owner
turnover and many concurrent framebuffer consumers. A separate second OS window
was not available through the isolated editor control surface; multi-window
safety therefore rests on the already-validated S07 owner-scoped wrapper model
plus S09's deliberate refusal to share renderers or wrappers.

Emulated VR was requested for a stereo check, but the editor remained in
`EnteringPlay` with no active VR runtime or eye viewports. No eye-buffer image is
claimed. The stereo invariant is source-enforced: each framebuffer retains its
own multiview flag and renderer, and the shared mesh contains positions only.

## Diagnostics and deferred work

No shared-geometry error, double release, object-disposed failure or retained
lease appeared during startup, pipeline-cache teardown/recreation, renderer
replacement or shutdown. A capture immediately after cache clear did expose an
existing `XRFrameBuffer` CPU-publication exception and one Vulkan desktop-frame
failure before a later capture succeeded. Model import from `<desktop>/...` also
failed independently. Neither diagnostic originated in the shared mesh cache;
both remain separate investigation candidates.

Debug primitives, light volumes, skybox/grid/atmosphere helpers and their
specialized meshes remain unchanged because their contracts and measured benefit
were not established for this category. Procedural fullscreen drawing is also
deferred: adopting it would require an explicit topology/count and shader/view
compatibility design, not merely deleting the helper mesh.

## Gate outcome

S09 is Validated for fullscreen framebuffer helpers. Sharing reduced the final
Vulkan triangle construction ratio from 60 logical consumers to one live mesh
generation, preserved independent mutable consumer state, survived last-release
retirement and recreation, and retained visible Vulkan/OpenGL output. Expansion
to another geometry category requires its own evidence and validation gate.
