# Point shadow sizing and cooked restoration

Point shadow configuration now separates authored dimensions from physical cube
storage. Assigning `ShadowMapResolutionWidth` and `ShadowMapResolutionHeight`
preserves each serialized value independently. The explicit paired
`PointLightComponent.SetShadowMapResolution(width, height)` API continues to
normalize both authored values to their maximum. `GetShadowMapStorageResolution`
resolves square cube faces without rewriting those values; browser quality limits
apply afterward. Target recreation and lifecycle refresh retain the authored pair.

This corrects a restoration failure where sequential assignments of 256 and 256
were repeatedly combined with the untouched 1024 default, leaving a 1024 cube.
The native cold resource descriptor uses the same storage-shape query.

Nested cooked readers suppress property callbacks while restoring their containing
graph. Previously the point post-deserialization hook could create six camera
transforms during that suppression: rotation changes did not mark matrices dirty,
and parent assignments did not register children. All six cameras could retain
identity matrices at the origin while their render targets still cleared normally.
Point camera, viewport and target preparation now defers until callbacks resume.
Lazy access preserves the six-camera inspection contract afterward.

The first standalone target is prepared before caster collection. Browser point
collection, swapping and rendering use the same complete six-face requirement,
because filtering samples across cube seams. Each camera retains the explicitly
created shadow pipeline instead of querying a potentially pending viewport source.
Reused viewports refresh their world binding.

Point teardown now stops its viewports and releases retained private pipeline,
framebuffer, camera-transform, camera-list and synthetic-parent owners. Viewport
creation publishes the six-member family together or cleans up partial candidates.
Late processing calls cannot recreate or index a disposed family.

The base light also disposes its `RenderInfo` when the component is destroyed.
`RenderInfo` unregisters from its world or canvas, detaches command callbacks
and renderer-mutation tracking, and destroys the command list it created.
Replacing that list detaches the previous commands and retains the original
owned list until terminal disposal, including when replacement occurs inside a
deferred construction scope. A caller-supplied replacement remains caller-owned
and survives render-info disposal; its command connections are detached without
destroying its contents.

## Validation and remaining acceptance

The focused managed probe passed 139 checks with a warning-free build. It covers
both property orders, the paired API, desktop lifecycle refresh, quality limits,
YAML and nested SceneNode cooked restoration, six distinct face orientations,
translated face positions, the fixture occluder's frustum visibility, and immutable
target/per-face framebuffer replacement. A direct suppressed factory call reproduced
six identity local matrices and zero parent-child registrations.

Before teardown correction, three camera-only cycles each retained 21 registered
objects after light destruction. The camera-family correction retired 20 point
helpers per cycle and exposed the separate base-light `RenderInfo.RenderCommands`
list. With render-info disposal, three camera-only and three fully initialized
cycles now retain zero registered objects and restore the exact cache baseline.
The probe also confirms that component teardown destroys the original command
list and leaves a caller-supplied replacement alive. The earlier eight-object
canvas-composed observation had separate GPU-scene and fallback-material owners,
as recorded in the unified browser checkpoint; this point probe does not
attribute that historical observation to render-info lists.

The final probe also covers a nested command-list replacement and confirms only
the final borrowed list receives later commands. A world-registration callback
that throws after the property changes still records the actual target; terminal
cleanup removes that registration even when a later property change is vetoed.
Both borrowed command lists remain alive, and their command callbacks detach
when the light is destroyed.

Probe source and logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/point-shadow-contract/`.
Physical WebGPU acceptance of point caster draws and visible receiver darkening
remains separate from these managed checks; the preceding hardware run established
directional shadows but recorded zero point caster draws.
