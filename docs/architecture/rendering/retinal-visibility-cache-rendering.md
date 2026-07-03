# Retinal Visibility Cache Rendering

Last updated: 2026-07-03

Retinal Visibility Cache (RVC) is XRENGINE's high-end opaque VR rendering path
for OpenXR quad-view and foveated headsets. The design keeps visibility
authoritative per runtime view, then shares expensive work only after identity,
material, lighting, deformation, and view-dependence checks prove that sharing
is safe.

That invariant is the reason RVC is not just "render fewer pixels." VR failure
modes are often eye-local: a stale surface in one eye, a wrong inset edge, or a
view-dependent specular reuse artifact is worse than an ordinary flat-screen
quality loss. RVC therefore starts from exact per-view visibility and makes
sharing opt-in.

Related docs:

- [RVC validation plan](../../work/todo/rendering/vr/retinal-visibility-cache-rendering-todo.md)
- [RVC design source](../../work/design/rendering/retinal-visibility-cache-rendering-design.md)
- [OpenXR VR rendering](openxr-vr-rendering.md)
- [Material binding policy](material-binding-policy.md)
- [Render pipeline resource lifecycle](render-pipeline-resource-lifecycle.md)

## Current Status

The current branch implements the RVC foundation and render-graph/code surfaces.
RVC is selectable as a pipeline mode, the runtime and renderer capability model
exists, OpenXR quad/stereo view plumbing is view-count aware, OpenXR visibility
mask meshes can be queried, and RVC resources/passes are declared in the frame
graph.

The backend shader dispatch for the production RVC GPU stages is not yet linked.
`VPRC_RvcPass` deliberately emits a throttled warning when an active graph stage
does not have a backend kernel. This is a feature, not a placeholder accident:
it prevents the engine from pretending the cache path is doing GPU work when it
is only proving resource and pass topology.

The foundation covers:

- explicit runtime view-set contracts for mono, stereo, quad wide/inset, mirror,
  and debug views
- OpenXR active view-configuration selection and runtime view-count plumbing
- per-view swapchain, framebuffer, image, smoke-counter, and frame-profile
  storage sized by `RenderFrameViewSet.MaxViewCount`
- optional `XR_KHR_visibility_mask` extension enablement, function lookup,
  hidden/visible mesh fetch, status reporting, and invalidation state
- RVC settings, quality controls, pipeline selection, fallback reasons, and
  engine stats hooks
- `RvcRenderPipeline` as a sibling of `DefaultRenderPipeline`
- declared RVC texture arrays, shared buffers, framebuffers, and pass graph
  stages for the planned opaque cache renderer
- renderer capability hooks for descriptor backend, material resource table,
  visibility source paths, visibility-mask stencil, and Vulkan production
  features
- source contracts and tests for view diagnostics, visibility payloads,
  shadelet keys, material binding, reuse, reservoirs, temporal hashing,
  fallback decisions, and OpenXR/RVC wiring

## Design Principles

### Per-View Visibility Is Authoritative

Every runtime view gets its own visibility result. Wide views, inset views, and
stereo eyes may share material or lighting work later, but they do not share the
visibility decision itself. This avoids one-eye holes, wrong disocclusion
choices, and foveal inset artifacts.

### Forward+ Remains The Oracle

Forward+ is kept as both a companion path and the correctness oracle. Transparent
and order-dependent materials remain on Forward+, and every RVC quality claim is
compared against a foveated Forward+ baseline when the runtime supports it.

The point is not to make Forward+ disappear. The point is to let RVC accelerate
opaque work while always having a known-good image to compare against.

### Fallback Must Be Visible

Unsupported runtime, material, renderer, or backend states resolve to explicit
fallback reasons. The engine must not silently switch to a CPU path or ordinary
Forward+ while reporting success. RVC diagnostics are therefore shared across
pipeline resolution, frame profiles, counters, and logs.

### Backend-Neutral Contracts First

The cache keys, material rows, visibility payloads, and frame-graph resource
names do not store backend descriptor handles or API-specific objects. Vulkan can
use descriptor heap or descriptor indexing, and OpenGL can remain a correctness
slice, without changing the meaning of an RVC shadelet or source record.

### Vulkan Is The Production Target

RVC's production path assumes Vulkan features such as explicit synchronization,
dynamic rendering, descriptor heap/indexing, fragment shading rate or density
map, and optional mesh shaders. OpenGL is useful for correctness slices and
regression checks, but it is not expected to reach feature parity.

## Overall Flow

At a high level, one frame follows this shape:

1. Resolve runtime views into a `RenderFrameViewSet`.
2. Resolve RVC settings plus runtime/renderer capabilities into an
   `RvcPipelinePlan`.
3. If the requested mode is unsupported, render with Forward+ and publish the
   fallback reason.
4. If RVC is active, declare RVC graph resources and execute the planned stages:
   visibility-mask stencil, visibility targets, reconstruction, HZB,
   pixel-to-shadelet map, material shadelets, foveated shading rate,
   head-space light clusters, shared lighting, reuse validation, temporal cache,
   transparency, resolve, and diagnostics.
5. Publish per-view frame profiles, counters, and debug data for validation.

The implemented foundation currently performs steps 1 through 3, declares the
resources and stage topology for step 4, and publishes the diagnostics for step
5. Backend kernels still need validation and linking before the stage topology
becomes production rendering.

## Runtime View Model

`RenderFrameViewSet` and `RenderFrameViewDescriptor` make every output view an
explicit data record. Each descriptor carries stable view identity, role,
runtime view index, parent eye, wide/inset relationship, viewport, recommended
image size, projection, previous view state, foveation metadata, and debug name.

This was designed first because quad-view rendering cannot be cleanly bolted
onto code that assumes exactly two eyes. The same abstraction also keeps desktop
mono, editor preview, mirrors, and debug outputs from becoming special cases in
the RVC pipeline.

RVC diagnostics build on the same view model:

- `RvcFrameViewDiagnostics` records role, pixel count, stereo/foveation mode,
  fallback reason, and timing.
- `RvcFrameProfileSnapshot` records projection, viewport, previous
  view-projection, runtime view index, swapchain identity, pixel count,
  foveation mode, stereo mode, GPU timing, and fallback reason.
- `IRuntimeRenderingHostServices.ResolveRvcViewGpuMilliseconds(...)` maps
  resolved backend GPU timing into per-view profile data when timing is
  available.

The GPU timing is intentionally profile data, not a synchronous query. It keeps
the render loop free from readback stalls and lets validation distinguish
"unknown" from "measured."

## OpenXR Integration

OpenXR startup resolves the active view configuration before session begin. If
RVC quad view is enabled and the runtime exposes the Varjo quad-view
configuration, the engine can use
`XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO`. Otherwise it falls back to
primary stereo and records the reason.

The OpenXR path now:

- uses `_activeViewConfigurationType` for session begin and view location
- caches non-foveated stereo and foveated quad `XrViewConfigurationView`
  snapshots
- treats the runtime-reported view count as authoritative up to
  `RenderFrameViewSet.MaxViewCount`
- sizes swapchain arrays, image-count arrays, OpenGL framebuffers, Vulkan
  swapchain image pointers, smoke counters, and RVC profile data by max view
  count
- maps even-numbered OpenXR views to the left-family preview path and
  odd-numbered OpenXR views to the right-family preview path
- reuses the existing eye-family scene rig while applying each runtime view's
  actual pose, FOV, and viewport

The design keeps the OpenXR runtime in charge of view count and dimensions. That
prevents the engine from inventing quad-view assumptions that disagree with the
runtime.

### Visibility Masks

OpenXR visibility masks are requested through `XR_KHR_visibility_mask` when the
runtime advertises the extension. The engine resolves `xrGetVisibilityMaskKHR`,
fetches hidden and visible mesh counts/data per active view, tracks revision and
status, and invalidates the cached mask state when OpenXR reports a visibility
mask change.

The masks are modeled as RVC resources:

- `SharedOpenXrVisibilityMaskVertices`
- `SharedOpenXrVisibilityMaskIndices`
- `OpenXrVisibilityMaskStencil` graph stage

The reason to fetch both hidden and visible masks is diagnostic clarity. Some
runtimes expose only one useful mesh type, and validation needs to prove whether
the runtime, function lookup, mesh data, or backend stencil upload is the
missing piece.

## Pipeline Selection And Fallback

`RvcRenderPipeline` is a sibling of `DefaultRenderPipeline`. It starts from the
same Forward+ infrastructure instead of forking the render path wholesale. This
keeps mono/stereo/editor behavior stable while RVC adds opaque-cache resources
and diagnostics.

`RvcRenderingSettings` controls:

- requested pipeline mode
- quad-view enablement
- stereo, inset/wide, and temporal reuse toggles
- peripheral light aggregation
- diagnostic overlay and debug view mode
- shared-light grid space
- foveal, guard-band, mid-field, peripheral, near-field, derivative, AA, and
  reuse quality controls

Risky reuse defaults off. Stereo reuse is disabled until the A/B validation
harness proves it safe.

`RvcPipelineResolver` takes settings plus `RvcCapabilityMatrix` and returns the
effective mode, descriptor backend, foveation-rate backend, fallback reason, and
diagnostic string. Descriptor backend requirements are mode-aware: early
visibility/debug slices do not require the material table backend, while
material-cache and full modes do.

Full production RVC rejects OpenGL with a visible
`UnsupportedOpenGlProductionPath` diagnostic. That policy keeps OpenGL useful as
a correctness slice without letting it constrain the Vulkan architecture.

## Renderer Capability Surface

RVC asks the active renderer what it can really do through
`IRuntimeRendererHost` and `AbstractRenderer`:

- `RvcDescriptorBackend`
- `SupportsRvcMaterialResourceTable`
- `SupportsRvcVisibilityTargets`
- `SupportsRvcStaticMeshVisibilitySource`
- `SupportsRvcSkinnedComputeVisibilitySource`
- `SupportsRvcZeroReadbackIndirectVisibilitySource`
- `SupportsRvcMeshletVisibilitySource`
- `SupportsRvcOpenXrVisibilityMaskStencil`
- `RvcVulkanProductionFeatures`

The Vulkan renderer maps these to its actual descriptor backend, descriptor
indexing/heap support, dynamic rendering, synchronization2, fragment stores,
fragment shading rate, fragment density map, mesh shader, multiview, and
timeline semaphore support.

This indirection is deliberate. RVC should consume renderer-owned services
rather than create duplicate descriptor pools, duplicate texture tables, or
shadow material rows. The material row a shadelet sees must mean the same thing
whether the backend is descriptor heap or descriptor indexing.

## Frame Graph Resources

`RvcFrameGraphContract` names both semantic per-view resources and aggregate
array resources. RVC declares texture arrays for:

- per-view depth/stencil
- per-view visibility ID
- per-view velocity
- per-view HZB depth
- per-view reconstruction error
- per-view pixel-to-shadelet map
- transparency target
- final resolve
- mirror/debug output

It also declares shared buffers for:

- visibility source records
- material resource rows
- OpenXR visibility mask vertices and indices
- indirect arguments
- material shadelets
- head-space light clusters
- shared lighting
- light reservoirs
- temporal cache
- counters

RVC declares these resources before backend kernels are final because resource
identity is a contract. RenderDoc captures, source tests, frame-graph planning,
and backend implementation can all refer to the same names and lifetimes. That
reduces the risk that the first shader implementation accidentally bakes in a
temporary layout.

Framebuffers are declared for visibility, transparency, resolve, and debug
outputs. Resource contracts also carry aliasing policy and backend barrier
labels so Vulkan synchronization and OpenGL correctness slices can be validated
against the same semantic graph.

## RVC Pass Graph

`VPRC_RvcPass` declares one synthetic render-graph pass per
`ERvcGpuPassStage`. The stage list is:

1. `OpenXrVisibilityMaskStencil`
2. `VisibilityTargets`
3. `AttributeReconstruction`
4. `HzbRejection`
5. `PixelToShadeletMap`
6. `MaterialShadeletShading`
7. `FoveatedShadingRate`
8. `HeadSpaceLightClusters`
9. `SharedLighting`
10. `ReuseValidation`
11. `TemporalCache`
12. `TransparencyForwardPlus`
13. `FoveatedResolve`
14. `DiagnosticOverlay`

Each pass declares the resources it reads, writes, samples, or attaches. The
pass graph is intentionally explicit instead of hidden behind one monolithic
"RVC" command. Explicit stages make RenderDoc inspection, profiler labels,
fallback diagnostics, and partial backend implementation much easier.

The `Execute()` method currently logs that backend shader dispatch is pending.
That prevents a false sense of production readiness while still letting the
resource graph, pipeline resolver, source contracts, and diagnostics compile and
run.

## Visibility Source Design

RVC visibility is per-view and opaque-first. The correctness payload starts as a
64-bit identity format (`RG32_UINT` or equivalent) until packed 32-bit limits
are proven safe. Payloads identify the instance, draw or meshlet, primitive,
flags, material row, transform, deformation version, and editor selection data.

The source-path plan includes:

- static mesh hardware raster
- skinned mesh compute/deformation-aware path
- zero-readback indirect draw/material-table path
- meshlet compute expansion
- mesh shader expansion where supported
- Forward+ oracle fallback

These paths are separate because they fail differently. Static meshes need
simple identity and material rows. Skinned meshes need deformation versions to
reject stale reuse. Zero-readback indirect rendering must preserve the no-CPU
readback contract. Meshlets and mesh shaders depend on backend feature support.

Unsupported material classes route to Forward+ with visible counters. This keeps
RVC focused on opaque/stable work and avoids corrupting correctness for glass,
water, particles, refractive materials, order-dependent transparency, or
strongly view-dependent shaders.

## Attribute Reconstruction And HZB

After visibility, RVC reconstructs attributes from source records rather than
running the full material shader per visible pixel. Reconstruction covers
position, normal, tangent, UV, material row, previous position, and velocity.

The HZB path is conservative:

- previous or early depth can reject work only when reprojection and dynamic
  state prove it is safe
- same-eye wide visibility is generated before inset visibility
- inset HZB can be seeded from wide depth
- uncertain, newly visible, HZB-edge, and cross-view disagreement candidates are
  post-validated in the current frame

The reason for this conservatism is VR comfort. A wrong rejection in the
periphery may be tolerable for a flat scene; a wrong rejection between eyes or
across an inset boundary is not.

## Shadelets And Material Binding

RVC shades material work through shadelets. A shadelet key includes visibility
identity, material class, quantized surface location, roughness bucket, LOD
bucket, deformation version, foveation region, material row, and material
resource generation.

The shadelet map uses:

- 1x1, 2x2, 4x4, and 8x8 density policy
- per-view pixel-to-shadelet maps
- tile-local deduplication before global survivor merge
- material binning before compute-side material evaluation
- overflow counters and fallback reasons
- analytic visibility-gradient derivatives for texture LOD and normal mapping
- edge rejection at depth, normal, material, primitive, and disocclusion
  boundaries

Material binding prefers descriptor heap and falls back to descriptor indexing.
Both backends must expose semantically identical material rows. Shadelet keys do
not include descriptor-set handles because handles would make cache identity
backend-specific and fragile across resource rebinding.

## Foveated Shading Rate

RVC has a foveated shading-rate stage for material classes that have not yet
moved fully to compute-side reconstruction. Vulkan fragment shading rate is the
primary fast path, fragment density map is modeled as an optional alternative,
and compute shadelets carry the larger periphery densities.

The split exists because RVC should not block on every material shader being
ported. The renderer can use fragment shading rate as an incremental bridge
while the shadelet path takes over the stable opaque set.

Near UI and hands can force 1x1 shading. That override keeps interaction-critical
content sharp even when surrounding peripheral content is coarser.

## Shared Head-Space Lighting

RVC shared lighting uses a world-aligned, camera-relative head-space cluster
grid. The grid is camera-relative to keep coordinates numerically stable, but
world-aligned so cluster IDs do not churn under ordinary head rotation.

The lighting plan includes:

- reuse of existing Forward+ light metadata where layout and lifetime are safe
- heap-backed references for shadow maps, cookies, probes, and clustered-light
  buffers when descriptor heap is active
- the old per-view Forward+ tile grid as debug comparison and fallback
- foveation-specific exact-light budgets
- peripheral aggregate lights
- reservoir evaluation for imperfect stereo agreement
- counters for cluster occupancy, exact lights, rejected lights, aggregate
  contribution, reservoir weight, exact-vs-aggregate, and energy error

Shared lighting is conservative for the same reason shadelet reuse is
conservative: eye agreement and foveal correctness matter more than maximum
reuse.

## Reuse Policy

Reuse is split into intra-view, inset/wide, stereo, and temporal domains. Each
domain has separate counters and rejection reasons.

Inset/wide and stereo shadelet reuse require matching primitive identity,
surface location, material identity, material resource generation, normal,
roughness bucket, deformation version, LOD, and disocclusion state. Strongly
view-dependent materials are excluded unless they provide an explicit safe key.
Sharp specular and reflection correction stay per-view, especially in the fovea
and guard band.

Stereo reuse defaults off. The A/B harness must render identical scene state
with reuse enabled and disabled, record side-by-side captures, compute
per-region metrics, and pass human review before stereo reuse becomes a default
quality mode.

## Temporal Cache And Resolve

Temporal cache entries store shadelet key, surface key, material resource
generation, LOD bucket, confidence, age, and invalidation reason. Invalidation
covers material/resource changes, animation/deformation, LOD, shadow casters,
view-set changes, gaze-region changes, and topology instability.

Resolve is foveation-aware:

- visibility-edge AA is the preferred foveal path
- foveated TAA is the fallback
- wide/inset identity must be understood by upsampling and mirror composition
- late-latched foveation constants are modeled as buffer-device-address data
  written at submit time so gaze updates do not rebuild command buffers

Saccade quality policy is represented in the contracts but remains conservative
and disabled until validation proves it useful.

## Diagnostics And Validation Surface

RVC reports through:

- fallback reasons from `RvcPipelineResolution`
- per-view OpenXR frame profiles
- engine stats snapshots for counters and profiles
- render-log diagnostics
- graph stage names and GPU profiling labels
- planned debug overlays for density, source records, reconstruction error,
  HZB, shadelet maps, shared lighting, temporal cache, and final resolve

The validation plan intentionally treats diagnostics as product behavior. A
missing extension, missing backend feature, unsupported material, overflow, or
kernel-pending state must be observable.

## Quality Tolerances

`RvcQualityToleranceSet.Default` defines the current comparison gates against
the Forward+ oracle.

| Region | Max Error | Min SSIM | Max FLIP |
|--------|-----------|----------|----------|
| Fovea | `1/255` | `0.995` | `0.010` |
| Guard band | `2/255` | `0.990` | `0.015` |
| Mid-field | `4/255` | `0.975` | `0.030` |
| Periphery | `8/255` | `0.940` | `0.060` |

Captures must use fixed scene state, fixed camera/gaze, fixed frame timing, and
a documented warm-cache policy.

## Source Map

| Source | Role |
|--------|------|
| `XREngine.Runtime.Core/Settings/RvcRenderingContracts.cs` | Pipeline modes, capabilities, fallback reasons, quality tolerances, frame-graph resources, visibility payloads, shadelet keys, reservoirs, temporal hash keys. |
| `XREngine.Runtime.Core/Settings/RvcRuntimeContracts.cs` | Runtime plans for diagnostics, visibility-mask state, counter readback, resource barriers, visibility source paths, GPU pass execution, Vulkan production features, material binding, shared lighting, reuse, temporal cache, resolve, and pipeline composition. |
| `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/RvcRenderPipeline.cs` | Selectable RVC pipeline, settings snapshot/application, resource declaration, pass-command insertion, capability matrix construction, and visible fallback. |
| `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_RvcPass.cs` | RVC render-graph pass declarations, stage dependencies, resource usage, profiling names, and kernel-pending diagnostics. |
| `XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRendererHost.cs` | Renderer-facing RVC capability surface. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs` | Default renderer capability values for RVC. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs` | Vulkan RVC descriptor backend, material table, visibility target, mask stencil, and production-feature reporting. |
| `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs` | Engine settings that select RVC and configure quality, reuse, foveation, light aggregation, and debug behavior. |
| `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.cs` | Pipeline factory and RVC settings application. |
| `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Rvc.cs` | Engine stats sink for RVC counters and frame profile snapshots. |
| `XREngine.Runtime.Rendering/Runtime/Interfaces/IRuntimeRenderingHostServices.cs` | Host-service surface for RVC settings, counters, frame profiles, and per-view GPU timing. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.ViewConfiguration.cs` | Active OpenXR view configuration selection, quad probing, view setup, stereo/quad snapshots, visibility-mask function lookup, mesh fetch, and mask invalidation. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs` | Per-view RVC frame-profile publication during submitted projection-view setup. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/Extensions.cs` | Optional OpenXR extension list including `XR_KHR_visibility_mask` and runtime mask-support query. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs` | View, swapchain, image, visibility-mask, and diagnostic storage sized by `RenderFrameViewSet.MaxViewCount`. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs` | OpenGL swapchain setup and per-view Forward+ rendering path. |
| `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs` | Vulkan swapchain setup, per-view rendering path, and quad-safe storage/prewarm plumbing. |
| `XREngine.UnitTests/Rendering/RvcRenderingContractTests.cs` | Source-contract tests for resolver behavior, resources, visibility payloads, shadelets, lighting, temporal hashing, OpenXR wiring, renderer hooks, and host-service reporting. |

## Remaining Validation And Backend Work

The architecture is intentionally ahead of runtime proof. The validation plan
tracks the evidence still needed before RVC can be treated as production:

- Forward+ baseline captures and runtime feature inventory
- OpenXR quad-view hardware or simulator validation
- visibility-mask stencil upload and RenderDoc proof
- backend shader dispatch for active RVC graph stages
- RenderDoc inspection of visibility, reconstruction, HZB, shadelets, shared
  lighting, temporal cache, transparency, resolve, and diagnostics
- Vulkan multiview, dynamic rendering, descriptor backend, synchronization2,
  timeline semaphore, fragment shading rate/density map, and mesh shader
  hardening
- delayed GPU counter readback and profiler evidence
- quality comparisons against the foveated Forward+ oracle
- hardware, simulator, MCP screenshot, log, and performance validation
