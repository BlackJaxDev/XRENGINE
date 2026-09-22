# Global Illumination Ownership And Selection

This document records the ownership boundary used by the modular GI host
contract. It is intentionally conservative: a logical world, component, or
texture identity is never sufficient proof that two GPU allocations may be
shared.

## Shared inputs: current decision

The only working field provider is DDGI. Radiance Cascades is registered as
unsupported until it gains injection, propagation, and live update production.
There is therefore no second working consumer that proves a reusable GPU scene
input schema. Phase 5 intentionally does **not** create a generic geometry,
material, light, or environment allocation service merely because the names
look similar.

| Input | Current owner | Why it remains local |
|---|---|---|
| Triangle packing, material atlas, and BVH tracing | `GI/DDGI/GpuDdgiGeometryService` through `DDGIGeometryResources` | DDGI requires its own packed triangle/BVH layout. Voxelization, LPV, and a future raster-only field must be free to use a different scene representation. |
| Direct-light UBO | `GI/DDGI/DDGILightResources` | Its record layout, 255-light limit, binding point, and fail-closed diagnostic are the DDGI hit-shading ABI. |
| Octahedral environment radiance capture | `GI/DDGI/DDGIEnvironmentResources` | Its target, skybox variant, capture scheduling, and fallback are consumed only by DDGI hit shading. |
| Host surface inputs and composition target | `GI/Contracts/GlobalIlluminationHostResources` | These names identify host-owned surfaces only; they neither allocate a field nor prescribe geometry/tracing. |

If a later provider consumes the same *CPU* metadata with matching revision
semantics, add a neutral, immutable scene-input snapshot for that exact shared
need. It must preserve material emission, cutout/transmission, deformation,
light limits, and environment-revision diagnostics. Do not rename a DDGI GPU
buffer into a generic service as a substitute for proving that contract.

## Lifetime and invalidation domains

| Domain | Identity | Owner and invalidation rule |
|---|---|---|
| Authored field selection | `DDGIVolumeComponent.ID`, render world, `SelectionPriority` | Each render frame snapshots the winning valid component. Highest priority wins; a top-priority tie is rejected with the stable IDs in a rate-limited diagnostic. Volume bounds do not select or blend fields yet. |
| Persistent field/update context | Physical `XRRenderPipelineInstance`, selected component reference, authored revision, renderer API-wrapper owner | `DDGIFrameContext` owns probe/atlas state, update cursors, and GPU receipts. A selected-component change invalidates that state; a renderer-owner change clears it before reuse. Equal component IDs never permit sharing. |
| Algorithm scene inputs | Physical pipeline, scene reference, renderer API-wrapper owner | `DDGIGeometryResources`, `DDGILightResources`, and `DDGIEnvironmentResources` are conditional-weak-table entries per pipeline and release on cache clearing. A scene or wrapper-owner change recreates the affected local resource. |
| View-dependent resolve/history | Host plan/resource layout and pipeline view family | The host owns surface targets and view layers. DDGI field updates are scoped to one pipeline context; cameras or viewports receive separate contexts. Stereo may share an update only inside that one verified pipeline/view-family owner. |

The selection snapshot deliberately does not change during a frame. This prevents
the update, resolve, and completion commands from targeting different authored
fields when editor data changes mid-frame. On the following frame a changed
selection reaches `DDGIFrameContext.Synchronize`, which invalidates the prior
field history before it can publish under the new selection.

## Submission and retirement

`XRGpuFence` remains the reusable submission/retirement primitive. DDGI retains
its update-stage logic and the separate completion, dynamic-use, baked-use, and
composite-use receipts because each protects a different read/write ordering
claim. A wrapper that merges those receipts would weaken the failure diagnostics
and has no second algorithm consumer today.

No generic GI retirement manager is introduced until another provider proves the
same ownership, submission, and failure semantics.

## Current validation boundary

The targeted DDGI dynamic, interruption-recovery, baked round-trip, and baked
load validation covers both Default and Advanced hosts. The ownership rules
above are enforced by their pipeline-instance and renderer-owner keys; the
broader multi-viewport and stereo matrix remains final-runtime coverage rather
than an implied sharing guarantee. See the modular GI execution record and the
Radiance Cascades completion TODO for the open representation and matrix work.
