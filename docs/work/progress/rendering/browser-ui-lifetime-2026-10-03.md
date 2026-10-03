# UI render ownership and tree teardown

An isolated screen-canvas plus material-quad lifecycle initially left 15
registered objects per destroyed scene node. Exact object identities separated
three component-owned `RenderInfo.RenderCommands` lists, two UI-transform debug
render-info lists, one quadtree-node item list, and nine material/renderer
containers. This is distinct from the earlier canvas-composed eight-object
GPU-scene/fallback-material observation recorded in the
[unified browser checkpoint](../platform/unified-browser-checkpoint-2026-10-01.md).

`UIRenderableComponent` now disposes its two render infos at destruction, and
`UICanvasComponent` releases its world-quad render info with the canvas render
resources. `UITransform` releases its debug render info after the transform's
scene/parent teardown. Render-info disposal removes actual scene registrations,
detaches command and renderer-mutation callbacks, clears terminal callbacks,
and destroys only its own initial command list. Replacement lists and command
resources remain with their callers. A failed registration or owned-list release
remains retryable.

The 2D visual scene terminates its quadtree and clears its registration queues.
The tree releases only node-owned item-list storage and clears an item's node
back-reference only while it still points to that node. Rebuilt and pruned nodes
are retained during an active object-publication scope and released at the next
out-of-scope swap or terminal teardown. Late scene callbacks are inert after
destruction; direct tree mutations reject terminal use. Tree swaps remain a
single-consumer operation and overlapping swaps fail explicitly.

Constructor-created `XRAsset` metadata, material shader/texture/options, and
mesh-renderer submesh containers are released by their exact asset owner.
Caller-supplied replacement lists, options, materials, referenced shaders and
textures, and submesh entries are not destroyed by those owners. The UI quad's
parameterless default material is captured in its own construction-ownership
scope and released with the component. UI options assigned to a supplied or
replacement material remain alive with that borrowed material; their later
ownership requires an explicit caller contract and is not inferred from the
component's destruction.

The focused UI coordinate/lifecycle probe builds without warnings and passes
91 checks. Three repeated canvas-plus-quad teardown cycles each return to the
exact registered-object baseline, compared with 15 survivors before these
owner fixes. A supplied material and its escaped options survive UI teardown.
Probe source and final logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/ui-canvas-coordinates/`.
These checks cover managed ownership and coordinate behavior; they do not claim
browser GPU pixels, in-flight device completion, or a whole-game memory budget.

## Explicit object reuse

The public `IPoolable` reset path calls `Generate()`, while release requests
deferred destruction. After that destruction has been processed, resetting an
`XRMeshRenderer` previously revived its parent but left the newly owned submesh
container destroyed and the new setter guard permanently active. A focused
runtime probe reproduced that regression, together with the equivalent material
storage/subscription failures under object-cache registration suppression.

Asset, material, and mesh-renderer generation now restore their exact
constructor-owned containers after a successful destroyed-to-live parent
transition. They retain current caller-supplied replacements and restore only
the final list's subscriptions. Borrowed resources are never generated, even
when a caller destroyed them independently. Repeated resets of a live object
do not regenerate children or duplicate callbacks. Constructor-time virtual
generation and failed parent generation do not enter the restoration path.
The texture subscription tracker is cleared during teardown so a retained list
can be attached once on revival.

The focused probe passes 104 checks and builds with zero warnings or errors.
It covers three material and renderer reuse cycles, owned reference identity,
borrowed resources destroyed independently by their callers, failed parent
generation, and destruction/revival during nested list setters. Texture,
parameter, shader, and submesh callbacks attach once and detach at teardown.
After explicitly initializing the process rendering settings, three renderer
pool cycles return to the exact six-object registered baseline; no survivors
are filtered from that comparison. The initial probe had ten failing ownership,
setter, and subscription checks before these restoration hooks.
The existing UI and point-light probes also rebuild without warnings or errors
and retain their 91 and 139 passing checks. Their three UI and six point-light
teardown cycles still report zero registered-object deltas and no survivors.

This is a managed ownership repair. Ordinary destroyed `XRMaterial.Generate()`
still fails the pre-existing render-object publication guard; the material
checks explicitly suppress cache registration. The existing mesh-renderer
resource-publication termination state and reuse before a queued destruction
has drained are outside this repair. No GPU resource/publication revival or
in-flight command replacement behavior is established here.

The focused source and before/after evidence are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/owned-object-revival/`.

## Transform debug handle ownership

The shared Host transform debug handle now disposes the `RenderInfo3D` it creates
when the transform is destroyed. It first hides and detaches the debug render
info, then terminal disposal releases the info's constructor-owned render-command
list and callback registrations. The handle drops its render-info reference after
successful cleanup, so repeated disposal and late visibility, world, or bounds
updates are inert. The transform and world registration target remain borrowed.

An isolated probe creates real handles through `EngineRuntimeTransformServices`
for three transform cycles. Each handle's command list is registered during
construction and destroyed at disposal. The registered-object delta is two
while the borrowed transform and its child list remain alive, then zero after
transform destruction in every cycle. Repeated disposal and late handle updates
leave the render info absent; the transform itself survives handle disposal.
The probe passes 30 checks. The narrow Release Host and probe builds each pass
with zero warnings or errors. Probe source and logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/transform-debug-handle/`.
This validates managed Host ownership and registry cleanup; it does not claim
GPU completion or browser pixels.
