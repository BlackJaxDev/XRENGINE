# Global Illumination Ownership And Selection

This document describes the modular GI contract that `DefaultRenderPipeline` and `AdvancedRenderPipeline` share. It covers provider selection, host adapters, the output signal, composition, lifetime domains, and submission rules. The authoring workflow is in the [Global Illumination Providers guide](../../developer-guides/gi/global-illumination.md). Open code work: [Modular GI Architecture TODO](../../work/todo/rendering/global-illumination/modular-gi-architecture-todo.md). Checks: [Global Illumination Validation](../../work/testing/rendering/global-illumination-validation.md).

A logical world, component, or texture identity never proves that two GPU allocations can be shared.

## Type Map

All paths are under `XREngine.Runtime.Rendering/Rendering/GI/`.
| Type | File | Responsibility |
|---|---|---|
| `GlobalIlluminationProviderRegistry` | `Contracts/GlobalIlluminationProviderRegistry.cs` | The only mapping from `EGlobalIlluminationMode` to a provider descriptor. Resolves the plan for a host. |
| `GlobalIlluminationProviderDescriptor` | `Contracts/GlobalIlluminationProviderDescriptor.cs` | Identity, required host capabilities, contributions, supported consumers, static support, settings type, feature metadata, module factory. |
| `GlobalIlluminationPlan` | `Contracts/GlobalIlluminationPlan.cs` | Immutable selection for one host. Resource layout, graph construction, bindings, and diagnostics read the same plan. |
| `IGlobalIlluminationHostAdapter` | `Contracts/`, `Integration/DefaultGlobalIlluminationHostAdapter.cs`, `Integration/AdvancedGlobalIlluminationHostAdapter.cs` | Host identity, execution owner, anchors, and neutral capabilities. |
| `GlobalIlluminationHostResources` | `Contracts/GlobalIlluminationHostResources.cs` | Host depth, material, and AO input names, plus the provider output, composition material, and target names. |
| `IGlobalIlluminationModule` | `Contracts/`, `DDGI/DDGIGlobalIlluminationModule.cs`, `RadianceCascades/RadianceCascadesGlobalIlluminationModule.cs` | Provider support evaluation, resource declaration, pass contribution, invalidation, and release. |
| `GlobalIlluminationCompositionState` | `Contracts/GlobalIlluminationCompositionState.cs` | Warm-up and replacement policy for the screen contribution. |
| `GlobalIlluminationDiagnosticPresentation` | `Contracts/GlobalIlluminationDiagnosticPresentation.cs` | Neutral debug presentation state read by post-processing and exposure. |
| `VPRC_GlobalIlluminationCompositePass` | `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_GlobalIlluminationCompositePass.cs` | Neutral composition of the provider output into the host HDR target. |

## Selection

- The registry maps each mode to one descriptor. A host resolves a `GlobalIlluminationPlan` when the selection changes. Advanced also resolves it again when the offscreen or minimal-output profile changes.
- A plan is supported only when the descriptor is supported, the host is not minimal-output, and the host capabilities include every required capability. Otherwise the plan holds an explicit diagnostic.
- An unsupported plan declares no resources and contributes no passes. The registry never substitutes a CPU path or another GI method.
- `GlobalIlluminationPlan.GetConsumerSupport` reports each consumer (deferred opaque, forward opaque, transparent, world-space) as supported or unsupported.
- One diffuse provider is selected at a time. Specular is selected independently. Combining diffuse providers needs a coverage and energy policy that does not exist yet.

Current descriptors:
| Mode | Status | Required capability | Consumers |
|---|---|---|---|
| `LightProbesAndIbl` | Supported | `ProbeSampling` | Deferred opaque, forward opaque, transparent |
| `DDGI` | Supported (experimental) | `ScreenSpaceDiffuseOutput` | Deferred opaque |
| `RadianceCascades` | Experimental descriptor, rejected at runtime | `ScreenSpaceDiffuseOutput` | Deferred opaque (resolve only) |
| `PathTracing` (ReSTIR), `VoxelConeTracing`, `LightVolumes`, `SurfelGI` | Unavailable | None | None |

## Host Adapters
| Capability | Default | Advanced |
|---|---|---|
| `DeferredSurface` | Yes | No |
| `NativeOpaqueSurface` | No | Yes |
| `ScreenSpaceDiffuseOutput`, `ProbeSampling`, `StereoLayers`, `TemporalHistory`, `MaterialSampling`, `DebugPresentation` | Yes | Yes |

- An Advanced minimal-output host advertises no capabilities.
- Execution anchors are `ScenePreparation`, `FieldUpdate`, `SurfaceResolve`, `MaterialSampling`, and `DebugPresentation`. A module uses only the anchors it needs.
- Advanced keeps its ordered stage identities and execution owner. The adapter never creates a nested Default or Advanced pipeline instance.
- Advanced admission reads the plan, not provider classes. `GlobalIlluminationPlan.RequiresNativeMaterialSurfaceExports` sets `AdvancedVisibilityStageBackendRequest.RequiresMaterialSurfaceExports` (OpenGL and Vulkan). `RequiresNativeProbeIblBindings` and `ReplacesProbeDiffuse` drive probe binding and diffuse suppression.

## Module Rules

- Provider code does not reference `DefaultRenderPipeline`, `AdvancedRenderPipeline`, their resource constants, or their framebuffer factories.
- Host code does not switch on provider classes, mode values, pass class names, or algorithm stage flags. The registry is the only selection boundary.
- A module declares every resource it writes and owns its `RenderPipelineResourceVariant` and output names. Each pass declares its own graph accesses.
- No common base class exists for ray, probe, voxel, surfel, cascade, or propagation work. Algorithm stages stay private.
- Inactive providers allocate no resources and add no graph work.

## Signal And Composition Contract

- The common screen output is linear HDR, material-shaded, outgoing indirect diffuse radiance. The provider applies material response and AO in its resolve. The compositor adds the result once and applies neither again.
- Invalid, pending, or incomplete data is unavailable. It is not valid black.
- `VPRC_GlobalIlluminationCompositePass` samples the provider output and writes the host HDR target. The host adapter selects the target, MSAA handling, view layers, and order (before transparency and temporal and post-processing).
- `GlobalIlluminationCompositionState` sets the warm-up rule: keep probe diffuse during warm-up, publish the first valid provider result without compositing it, then replace probe diffuse on later frames.
- Deferred mono and stereo shaders and Advanced native shading use `SuppressProbeDiffuse` from contribution semantics. Probe specular stays bound.
- Debug output replaces the destination immediately. Debug output is a presentation output, not GI radiance. Post-processing reads `GlobalIlluminationDiagnosticPresentation`, not provider state.
- A screen-space output does not serve forward, transparent, world-space, or secondary-ray consumers. World-space sampling needs a separately declared capability.

## Shared Inputs

DDGI is the only working field provider. No second consumer proves a reusable GPU scene-input schema, so these inputs stay provider-local:
| Input | Owner | Reason |
|---|---|---|
| Triangle packing, material atlas, BVH tracing | `DDGI/GpuDdgiGeometryService` through `DDGIGeometryResources` | DDGI needs its own packed triangle and BVH layout. Voxel, LPV, and raster fields can use other representations. |
| Direct-light UBO | `DDGI/DDGILightResources` | The record layout, 255-light limit, binding point, and fail-closed diagnostic are the DDGI hit-shading ABI. |
| Octahedral environment capture | `DDGI/DDGIEnvironmentResources` | Only DDGI hit shading uses its target, skybox variant, scheduling, and fallback. |
| Host surface inputs and composition target | `Contracts/GlobalIlluminationHostResources` | Names of host-owned surfaces only. |

When a second provider needs the same CPU metadata with the same revision rules, add a neutral immutable scene-input snapshot for that need. Keep emission, cutout and transmission, deformation, light-limit, and environment-revision diagnostics. Do not rename a DDGI GPU buffer into a generic service.

## Lifetime And Invalidation Domains
| Domain | Identity | Rule |
|---|---|---|
| Authored field selection | `DDGIVolumeComponent.ID`, render world, `SelectionPriority` | Each render frame snapshots the winning valid component. The highest priority wins. A top-priority tie is rejected with the IDs in a rate-limited diagnostic. Bounds do not select or blend fields. |
| Persistent field and update context | Physical `XRRenderPipelineInstance`, selected component, authored revision, renderer API-wrapper owner | `DDGIFrameContext` owns probe and atlas state, cursors, and GPU receipts. A selection change invalidates history. A renderer-owner change clears state before reuse. Equal component IDs never allow sharing. |
| Algorithm scene inputs | Physical pipeline, scene, renderer API-wrapper owner | `DDGIGeometryResources`, `DDGILightResources`, and `DDGIEnvironmentResources` are conditional-weak-table entries per pipeline. Cache clearing releases them. A scene or owner change recreates them. |
| View-dependent resolve and history | Host plan, resource layout, pipeline view family | The host owns surface targets and view layers. Cameras and viewports get separate contexts. Stereo eyes share one update only inside one pipeline and view-family owner. |

- A resource generation includes provider identity, physical execution and renderer owner, scene identity, selection and layout identity, and dimensions or view layout. Content revisions are separate from layout revisions, so a lighting change does not reallocate.
- The selection snapshot does not change during a frame. On the next frame, `DDGIFrameContext.Synchronize` invalidates the old history before it publishes under the new selection.
- Imported textures and buffers are staged and published at a frame boundary. On Vulkan, an external image (for example `DDGIMaterialTextures`) is a borrowed physical group. The frame package freezes its native generation and checks it again at production seal. The backend-ready package identity includes the imported-resource instance revision.

## Submission And Retirement

- Submitted and GPU-complete are separate states. A pending update can reuse a published generation only while its resources stay valid and protected. Otherwise the provider publishes explicit unavailability.
- Rejected submissions and partial writes do not advance temporal cursors.
- `XRGpuFence` is the shared retirement primitive. DDGI keeps separate completion, dynamic-use, baked-use, and composite-use receipts because each protects a different ordering claim. `VPRC_DDGICompositeCompletionPass` keeps the composite-use receipt after the neutral composition.
- No generic GI retirement manager exists until another provider has the same ownership, submission, and failure semantics.
