# Advanced Render Pipeline

This document describes the frame flow, invariants, capability floor, and output ownership of `AdvancedRenderPipeline`. Selection settings, shared GPU scene tables, and command-authoring rules are in [Default Render Pipeline Notes](default-render-pipeline-notes.md#selecting-advancedrenderpipeline).

`AdvancedRenderPipeline` uses a visibility-buffer opaque renderer. `DefaultRenderPipeline` stays the visual reference and the explicit legacy source. New renderer work goes into `AdvancedRenderPipeline` only. No `DefaultRenderPipeline2` type, alias, or `XRE_USE_PIPELINE_V2` selector exists.

## Code Map
| Area | Location |
|---|---|
| Pipeline partials | `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/` |
| Capability, selection, output binding, frame contract | `Advanced/Contracts/`: `AdvancedRenderPipelineCapabilities`, `AdvancedRenderPipelineCapabilityResolver`, `AdvancedRenderPipelineSelectionResolver`, `AdvancedRenderPipelineOutputBinding`, `AdvancedRenderPipelineFrameContract` |
| Stage identity | `EAdvancedRenderStage`, `AdvancedRenderStageDescriptor` |
| Resource ownership and synchronization | `EAdvancedRenderResourceOwnership`, `AdvancedRenderResourceOwnershipContract`, `EAdvancedSynchronizationBoundary`, `AdvancedSynchronizationContract` |
| Shared scene and material tables | `AdvancedSharedGpuSceneDatabase`, `AdvancedFrameSlotUploadArena` |
| Native opaque shading (Vulkan) | `Build/CommonAssets/Shaders/Advanced/Shading/ShadeNativeOpaque.comp` |

## Frame Flow

`EAdvancedRenderStage` defines the stage order:

```text
FrameBegin
  -> Deformation
  -> VisibilityPreparation
  -> VisibilityRaster
  -> DepthPyramidAndLateVisibility
  -> DirectionalShadowRaster
  -> AmbientOcclusion
  -> WorkClassification
  -> NativeOpaqueShading
  -> LatePasses
  -> TemporalAndPostProcessing
  -> Output
  -> UserInterface
```

- Deformation produces current and previous deformed vertex data one time. Visibility, shadow, velocity, and other geometry consumers reuse it.
- Visibility preparation runs early GPU culling and indirect preparation. Visibility raster writes depth and one visibility payload per pixel. The depth pyramid stage recovers late-visible geometry.
- Work classification builds GPU material and light work proportional to visible coverage.
- Native opaque shading reconstructs surface attributes from visibility identity, evaluates material lighting, and writes HDR scene color.
- Late passes hold transparent, refractive, volumetric, particle, editor-overlay, gizmo, and other special work.

### Scene registration identity reuse

`AdvancedGpuScenePublisher` retains the registration lookup and an ordered
array of source identities. Each row records the source reference, primitive
index and count, support state, source group, registration slot, and complete
draw handle. Reuse requires the same
command count and registration membership generation. Each present row must
still match an active registration and its draw generation. An absent source
must match an absent cached row.

Registration addition, slot reuse, removal, lookup growth, and publication
failure invalidate this cache. A new ordered array becomes valid only after
commit and successful source identity delivery. Successful publication reuse
follows the same delivery rule. Shrink and disposal clear retained source
references.

The publisher also retains source groups and flat primitive-to-draw handle
slices. Reuse requires exact ordered rows and primitive counts, unchanged
registration membership, and no pending identity recipients. A mismatch
rebuilds the whole grouping from active registrations and the current captured
plans. It does not repeat source capture. Registration or lookup storage growth
invalidates the relevant plan before commit. Scene-table journal growth alone
can preserve these identities and groups. Current unsupported sources receive
invalid handles.

Cold delivery preserves registration order, then current command order.
Removed sources receive cleared handles. Primitive shrink first clears the
removed tail; the next stable group uses the smaller handle count. A failed committed delivery retains
all recipients and their required clear lengths for a later caller retry.
Preflight reserves this storage before commit. A rejection cannot discard it.
Full successful delivery clears the pending list. Membership-change and retry
groups cannot become the reusable plan; the next stable publication builds it.
Callback exceptions still propagate to the caller. In the normal editor loop,
they cause a terminal timer fault, so automatic retry is not guaranteed.

These caches replace identity searches and grouping work. Each publication still captures
current source state, command metadata, effective materials, poses, and GPU
bounds sources. Existing geometry, material, temporal, resource, and transaction
checks remain active. Structural transaction plans are rebuilt. Every identity
callback still receives the current accepted publication. No frame resource
or output-page lease is stored in these caches.

The existing `get_render_state` response exposes
`canonicalResidentScene.registrationIdentityReuseCount` and
`registrationLookupRebuildCount`, plus `sourceGroupReuseCount`,
`sourceGroupRebuildCount`, and `pendingIdentityRecipientCount`, for the latest
publication attempt. These live
counters can be read while the publisher is updating them. Use repeated samples
for diagnostics; do not treat one response as an atomic publication snapshot.

`AdvancedGpuSceneIdentityCacheTests` and
`AdvancedGpuSceneIdentityCacheGrowthTests` check real scene publication without
a graphics device. They cover exact source-to-handle mapping, warm-cache
invalidation, capacity growth, missing and unsupported sources, failed delivery,
and cache reference cleanup. `AdvancedGpuSceneIdentityDeliveryBoundaryTests`
checks failures after commit and during reuse of a pinned publication. A
successful caller retry must deliver the accepted publication and clear every
pending recipient before the image becomes consumable.

### Material payload slots

`AdvancedMaterialDatabase` gives each material a fixed constant-word slot and
a fixed texture-binding slot. Adding or replacing a payload writes its active
entries and clears each unused tail. The dirty range covers the whole slot,
including the cleared entries. Adjacent writes merge into one range.

`AdvancedMaterialDatabaseContractTests` checks complete slots for addition,
replacement, and payload shrink. Replacement uses no managed allocation when
capacity is sufficient.

### Selected GPU chain debug

`AppendAdvancedPhysicsDebugCommands` runs after late scene work and before
temporal and post-processing work. It is independent of the transparent mesh
count. The hook binds `ForwardPassFBO` and loads its existing HDR color and
native visibility depth without clearing either attachment. It uses the
internal viewport, depth testing, depth writes, and `LessEqual`.

`VPRC_RenderDebugPhysics` declares the dynamic `Advanced.PhysicsWorldDebug`
pass. The Advanced hook sets `RenderWorldPhysics` to `false`, so selecting a
GPU chain does not also draw the world's general physics debug frame. Other
callers keep the option's default value of `true`.

No selected GPU chain means no framebuffer setup, debug generation, or draw.
The hook also respects `DebugInstanceRenderingAvailable` and the capture
policy's `DebugOverlays` permission. Shadow and light-probe passes skip it.
Minimal depth and visibility output profiles omit the late stage entirely.

The dispatcher generates one bounded batch per world and reuses successful
content across views in the same frame. Deferred or native uses can block
batch reuse, so a later frame can skip debug output. The hook adds no CPU
readback. Buffer ownership, barriers, and indirect draw rules are defined in
the [physics-chain compute contract](../physics/physics-chain-compute-backends.md#global-gpu-chain-debug).

### Synchronization boundaries

OpenGL and Vulkan lower the same four logical boundaries (`EAdvancedSynchronizationBoundary`) through their own encodings:

1. `ComputePreparationToVisibilityRaster`
2. `VisibilityRasterToComputeShading`
3. `ComputeShadingToLateGraphics`
4. `LateGraphicsToPresentation`

### Resource ownership

Each declared resource has one ownership class (`EAdvancedRenderResourceOwnership`): `PipelinePersistent`, `FrameSlotTransient`, `TemporalHistory`, `Imported`, or `External`. Current and previous frame-slot reuse waits on a fence or timeline completion.

## Invariants

- Compatible opaque and masked surfaces write one visibility payload and shade through the visibility path. They do not choose between deferred and ordinary opaque-forward color paths.
- The production Advanced path does not allocate or populate a classic full GBuffer. Diagnostic reconstruction targets exist only behind an explicit capture or debug mode.
- Late and special passes are explicit. They do not make the opaque renderer a deferred and forward composite.
- Geometry identity is backend-neutral across CPU-direct, zero-readback indirect, meshlet, skinned, and future virtual-geometry producers.
- Material work groups by shading-kernel compatibility and visible coverage, not by material object or descriptor-set identity.
- Scene, geometry, material, light, shadow, animation, and texture data are GPU-addressable through stable table records. Warmed production frames have no per-object or per-material binding loops.
- Production GPU-driven modes do no same-frame count, visibility, material-range, or overflow readback.
- Frame execution never creates or resizes declared pipeline resources.
- GPU-written frame data does not invalidate reusable command topology. Recorded command packets change only for topology, capacity, binding, shader, or resource generations.
- A required accelerated mode that is not available fails visibly with a machine-readable reason. It does not route through CPU emulation.
- Per-frame hot paths allocate no managed heap memory in steady state.
- OpenGL 4.6 and Vulkan use the same logical contracts. Backend encodings can differ.
- An unsupported opaque material in a required Advanced mode renders an observable error material or fails selection. It does not enter the classic opaque renderer.

## Capability Floor

`AdvancedRenderPipelineCapabilityResolver` rejects an output with an `EAdvancedRenderPipelineRejectionReason` when a required capability is missing:
| Requirement | Rejection reason |
|---|---|
| A live renderer on a supported backend | `RendererUnavailable`, `UnsupportedBackend` |
| Integer visibility render targets | `MissingIntegerRenderTargets` |
| Compute shaders | `MissingComputeShaders` |
| Storage buffers for geometry and scene tables | `MissingStorageBuffers` |
| Indirect dispatch and draw | `MissingIndirectSubmission` |
| A production texture-indirection mode | `MissingTextureIndirection` |
| Explicit image and buffer synchronization | `MissingSynchronization` |
| Current and previous frame-slot storage | `MissingFrameSlotStorage` |
| Stereo-array resources, when stereo is requested | `MissingStereoArrayResources` |
| OpenGL multiview raster, for OpenGL stereo | `MissingOpenGlMultiviewRaster` |
| The `VisibilityBuffer` shader family | `MissingShaderFamily` |

Optional acceleration flags (`SupportsBufferDeviceAddress`, `SupportsDescriptorIndexing`, `SupportsDescriptorHeap`, `SupportsSubgroupOperations`, `SupportsMeshShaders`, `SupportsAsyncCompute`, `SupportsTimelineSemaphores`) can improve an implementation. They never change the logical visibility, scene, or material contracts.

## Output Ownership

Every pipeline request carries an explicit output purpose. Outputs can share scene, mesh, and material data and compatible GI, temporal, froxel, and post-process feature contracts. They never share output-local pipeline instances, command recordings, frame-slot ownership, or temporal histories.
| Output | Owner |
|---|---|
| Desktop scene | The standard selection policy. `AdvancedRenderPipeline` is the configured source for new desktop cameras under the `Available` or `Required` mode (`XRE_ADVANCED_RENDER_PIPELINE_MODE`, `AdvancedRenderPipelineMode`). The camera pipeline asset is authoritative; capability results and visibility-family reservations live on each `XRRenderPipelineInstance`. |
| OpenXR eyes | An independently owned pipeline of the family that `VrRenderPipeline` selects: `DefaultRenderPipeline`, `AdvancedRenderPipeline` with the OpenXR eye profile, or `RvcRenderPipeline`. The desktop Advanced policy does not affect eyes. An unsupported view mode and family pair submits no projection layer. |
| Offscreen capture | The standard capability policy. Desktop debug and RVC settings do not redirect it. |

Under `Available`, an output that cannot reserve the required family stays explicitly unbound and reports the rejection. Under `Required`, selection throws `AdvancedRenderPipelineNotSupportedException`.

## Known Limits

- The Advanced pipeline still builds the classic GBuffer and `DeferredLightCombine` resources for its opaque path. Removal is open work in the [Vulkan XR And Advanced Rendering TODO](../../work/todo/rendering/vulkan-xr-and-advanced-rendering-todo.md).
- `AdvancedRenderPipeline` is the source default for new desktop cameras, but it is not yet certified production-ready for every backend, material, and output profile. Promotion gates are in the [Vulkan master TODO](../../work/todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md).

## Related Documentation

- [Default Render Pipeline Notes](default-render-pipeline-notes.md)
- [Render Pipeline Resource Lifecycle](render-pipeline-resource-lifecycle.md)
- [Mesh Submission Strategies](mesh-submission-strategies.md)
- [GPU Record Layouts](gpu-record-layouts.md)
- [Default And Advanced Pipeline Validation](../../work/testing/rendering/default-and-advanced-pipeline-validation.md)
