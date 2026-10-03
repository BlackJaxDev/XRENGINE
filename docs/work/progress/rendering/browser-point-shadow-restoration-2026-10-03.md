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

## Validation and remaining acceptance

The focused managed probe passed 121 checks with a warning-free build. It covers
both property orders, the paired API, desktop lifecycle refresh, quality limits,
YAML and nested SceneNode cooked restoration, six distinct face orientations,
translated face positions, the fixture occluder's frustum visibility, and immutable
target/per-face framebuffer replacement. A direct suppressed factory call reproduced
six identity local matrices and zero parent-child registrations.

Before teardown correction, three camera-only cycles each retained 21 registered
objects after light destruction. Afterward, three camera-only and three fully
initialized cycles each retained one object: the separate base-light
`RenderInfo.RenderCommands` list. The 20 point camera-family objects and initialized
viewport, pipeline and framebuffer owners retire. This does not establish the cause
or resolution of the separate eight-object canvas lifecycle observation.

Probe source and logs are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/point-shadow-contract/`.
Physical WebGPU acceptance of point caster draws and visible receiver darkening
remains separate from these managed checks; the preceding hardware run established
directional shadows but recorded zero point caster draws.
