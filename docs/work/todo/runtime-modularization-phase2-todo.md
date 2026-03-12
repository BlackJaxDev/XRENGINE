# Runtime Modularization Phase 2 - Remaining Work

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)

## Completed Work (Audit Snapshot 2026-03-12)

The original 2026-03-11 audit remains valid for the previously moved slices. This pass additionally re-validated the solution with:

- `dotnet build XRENGINE.slnx` -> 0 warnings, 0 errors
- `dotnet test XREngine.UnitTests --filter AssetPackerTests` -> 22/22 passing
- `dotnet test XREngine.UnitTests --filter SceneNodeLifecycleTests` -> 4/4 passing
- `dotnet test XREngine.UnitTests --filter StreamVariantInfoTests` -> 3/3 passing
- `dotnet test XREngine.UnitTests --filter VulkanTodoP2ValidationTests` -> 6/6 passing
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\Generate-Dependencies.ps1` -> refreshed `docs/DEPENDENCIES.md`

### Project Skeletons

All six runtime layer projects exist in the solution with correct allowed references:

| Project | References | Has Code |
|---------|-----------|----------|
| `Runtime.Core` | Data, Extensions | Yes |
| `Runtime.Rendering` | Runtime.Core, Data, Extensions | Yes |
| `Runtime.AnimationIntegration` | Animation, Data, Runtime.Core, Runtime.Rendering | Empty |
| `Runtime.AudioIntegration` | Audio, Data, Runtime.Core, Runtime.Rendering | Empty |
| `Runtime.InputIntegration` | Data, Extensions, Input, Runtime.Core, Runtime.Rendering | Empty |
| `Runtime.ModelingBridge` | Data, Modeling, Runtime.Rendering | Yes |

### Types Moved To Runtime.Core

- Job runtime package - `ActionJob`, `CoroutineJob`, `EnumeratorJob`, `Job`, `JobHandle`, `JobManager`, `JobProgress`, `JobStepResult`, `LabeledActionJob`, `RemoteJobRequest`, `RemoteJobResponse`, `RemoteJobTransferMode`, and `IRemoteJobTransport`
- Networking contracts/codec package - `PlayerJoinRequest`, `PlayerAssignment`, `PlayerInputSnapshot`, `PlayerTransformUpdate`, `PlayerLeaveNotice`, `PlayerHeartbeat`, `ServerErrorMessage`, `WorldLocator`, `WorldSyncDescriptor`, `HumanoidPoseFrame`, `HumanoidPoseCodec`, and `StateChangePayloadSerializer`
- Startup/discovery contracts - `ENetworkingType`, `EVRRuntime`, `IVRGameStartupSettings`, and `DiscoveryAnnouncement`
- AOT/runtime metadata package - `AotRuntimeMetadata`, `AotRuntimeMetadataStore`, and `XRRuntimeEnvironment`
- Tick/runtime world contract package - `WorldTick`, `ETickGroup`, `ETickOrder`, `ReplicateOnTickAttribute`, `ReplicateOnChangeAttribute`, `IRuntimeWorldContext`, `IRuntimeWorldObjectServices`, `RuntimeWorldObjectServices`, and `RuntimeWorldObjectBase`
- Scene-graph service seam - `EParentAssignmentMode`, `IRuntimeTransformServices`, and `RuntimeTransformServices`
- `PhysicsGpuMemorySettings` - physics GPU memory config asset
- `PhysicsVisualizeSettings` - physics debug visualization config asset
- `RuntimeOnlyAttribute` - marks properties excluded from cooked serialization
- `ProjectionMatrixCombiner` - combines stereo projection matrices for VR culling

Related lower-layer move completed to unblock Runtime.Core:

- `AssetArchiveReader` - read-only archive lookup moved into `XREngine.Data` so `Runtime.Core` can load published AOT metadata without depending on `XRENGINE/Core/Files/AssetPacker`

### Types Moved To Runtime.Rendering

- `DrawElementsIndirectCommand` - OpenGL indirect draw command struct
- `GPUIndirectRenderCommand` - engine-level indirect render command wrapper
- `GpuSortPolicy` - GPU sort dispatch policy enum
- Render-graph metadata package - `ERenderGraphAccess`, `ERenderGraphPassStage`, `ERenderPassLoadOp`, `ERenderPassResourceType`, `ERenderPassStoreOp`, `RenderGraphDescribeContext`, `RenderGraphDescriptorSchemaCatalog`, `RenderGraphResourceNames`, `RenderGraphSynchronizationPlanner`, `RenderPassBuilder`, `RenderPassMetadata`, and related render-graph support types
- Render-resource descriptor package - `RenderResourceLifetime`, `RenderResourceSizeClass`, `RenderResourceSizePolicy`, `RenderResourceDescriptor`, `TextureResourceDescriptor`, `FrameBufferAttachmentDescriptor`, `FrameBufferResourceDescriptor`, and `BufferResourceDescriptor`
- Post-process schema metadata package - `PostProcessParameterKind`, `PostProcessEnumOption`, `PostProcessParameterDescriptor`, `PostProcessStageDescriptor`, `PostProcessCategoryDescriptor`, `RenderPipelinePostProcessSchema`, `PostProcessParameterNames`, and `ETonemappingType`
- Video-streaming package - `StreamOpenOptions`, `StreamVariantInfo`, `ResolvedStream`, `DecodedVideoFrame`, `DecodedAudioFrame`, `IMediaStreamSession`, `IHlsStreamResolver`, `IVideoFrameGpuActions`, `IRuntimeVideoStreamingServices`, `RuntimeVideoStreamingServices`, `VideoStreamingSubsystem`, `TwitchHlsStreamResolver`, `FFmpegStreamDecoder`, `HlsMediaStreamSession`, and `HlsReferenceRuntime`

### Types Moved To Runtime.ModelingBridge

- `XRMeshModelingImportOptions` - mesh import option flags
- `XRMeshModelingExportOptions` - mesh export option flags
- `XRMeshModelingExportOrderingPolicy` - export ordering policy enum
- `XRMeshModelingSkinningBlendshapeFallbackPolicy` - skinning fallback policy enum

### Bootstrap / Editor Integration

- `IBootstrapEditorBridge` interface in Runtime.Bootstrap replaces the old delegate bag
- `EditorBootstrapBridge` implementation registered in Editor
- `XREngine.Server` does not reference `XREngine.Editor` (phase 1 goal achieved)
- Bootstrap files: `BootstrapWorldFactory`, `BootstrapRenderSettings`, `BootstrapPawnFactory`, `BootstrapModelBuilder`, `BootstrapLightingBuilder`, `BootstrapAudioBuilder`, `BootstrapPhysicsBuilder`, `BootstrapNetworkingWorldProfiles`, `BootstrapEditorHooks`, `RuntimeBootstrapState`, `UnitTestingWorldSettings`, and `UnitTestingWorldSettingsStore`

### Current Dependency Graph (Simplified)

```text
Editor -> XRENGINE -> Runtime.Core
  |          |-----> Runtime.Rendering
  |          |-----> Runtime.ModelingBridge
  |          |-----> Animation, Audio, Input, Modeling, Data, Extensions
  |-> Runtime.Bootstrap -> XRENGINE (transitional)
  |-> Data, Extensions, Audio, Modeling, Profiler.UI

Server -> XRENGINE -> (same as above)
  |-> Runtime.Bootstrap -> XRENGINE (transitional)
  |-> Animation, Audio, Data, Extensions, Input
```

---

## Remaining Work

### 1. Runtime Core - Bulk Extraction

The plan targets moving the engine's core runtime surface out of `XRENGINE` into `Runtime.Core`. A meaningful leaf slice has moved, but the bulk remains.

Target ownership per the modularization plan:

- Engine lifecycle, threading, timing, job scheduling, shutdown coordination
- World/scene ownership models that do not require rendering types
- Base scene graph / runtime object base types that are not subsystem adapters
- Networking core and host-independent multiplayer orchestration
- Runtime settings application
- Physics runtime (kept here to avoid a premature physics project split)

Candidate move areas in `XRENGINE`:

- [ ] `Engine/` lifecycle files that do not depend on rendering/input/audio/animation concrete types
- [x] `Jobs/` - timing, job scheduling, threading primitives
- [ ] `Core/` - base scene graph types and remaining runtime objects without subsystem dependencies
- [ ] `Settings/` - remaining runtime settings that only depend on Data/Extensions
- [ ] `Scene/` base types - world, scene ownership, game mode infrastructure
- [ ] Networking core under `Engine/` - host-independent orchestration and transport abstractions

Completed networking-owned Runtime.Core slice:

- [x] top-level networking message contracts, payload serializer, and humanoid pose codec moved to `XREngine.Runtime.Core`
- [x] `EStateChangeType`, `StateChangeInfo`, `BaseNetworkingManager`, `ClientNetworkingManager`, `ServerNetworkingManager`, and `PeerToPeerNetworkingManager` de-nested out of the `Engine` partial type inside `XRENGINE`, removing the API-shape blocker for future cross-assembly moves
- [x] startup/discovery-owned contracts (`ENetworkingType`, `EVRRuntime`, `IVRGameStartupSettings`, and `DiscoveryAnnouncement`) moved to `XREngine.Runtime.Core`
- [ ] networking orchestration and engine-hosted managers still remain in `XRENGINE` because `BaseNetworkingManager` still depends on `XRComponent`, `TransformBase`, `Time`, and `Debug`

Completed core-owned runtime slice:

- [x] `AotRuntimeMetadata`, `AotRuntimeMetadataStore`, and `XRRuntimeEnvironment` moved to `XREngine.Runtime.Core`
- [x] world tick contracts (`WorldTick`, `ETickGroup`, and `ETickOrder`) moved to `XREngine.Runtime.Core`
- [x] runtime world object ownership/replication logic moved to `RuntimeWorldObjectBase` in `XREngine.Runtime.Core`, with `XRWorldObjectBase` reduced to an `XRENGINE` wrapper
- [x] read-only archive lookup moved down to `XREngine.Data` as `AssetArchiveReader`, removing the last `Runtime.Core -> XRENGINE/Core/Files/AssetPacker` dependency
- [x] `EParentAssignmentMode` and the transform-host service seam (`IRuntimeTransformServices` / `RuntimeTransformServices`) moved to `XREngine.Runtime.Core`
- [x] `IRuntimeWorldContext` now carries dirty-object and world-matrix publication hooks, letting `TransformBase` stop calling `XRWorldInstance` directly for those paths
- [x] `IRuntimeWorldContext` now exposes play-session state, and `IRuntimeWorldObjectServices` now owns runtime-object activation/world-change hooks used by `XRComponent`
- [x] late `SceneNode` attachment flow now replays component begin/end-play and parent/root attachment world-play lifecycle transitions instead of silently missing them

Strategy:

- Work outward from leaf types.
- Each slice must compile with only Data and Extensions as dependencies.
- Types that pull rendering, audio, animation, input, or modeling stay in `XRENGINE` until their subsystem adapter project is ready.

Important note for the next pass:

- `XRBase` and `XRObjectBase` are not remaining Phase 2 move candidates; they already live in `XREngine.Data/Core/Objects/`.
- `XRWorldObjectBase` is no longer the blocker; it now lives in `XREngine.Runtime.Core` as `RuntimeWorldObjectBase`, with the old `XRENGINE` type reduced to a host wrapper.
- `TransformBase` no longer depends directly on `XRWorldInstance` for dirty-transform / world-matrix publication, and no longer reaches directly into `Engine` for transform debug settings and render-loop policy.
- The remaining Runtime.Core blockers are now narrower: `XRComponent` renderable world-binding and late-attachment lifecycle flow no longer reach directly into rendering/world host code, but `SceneNode`, `XRComponent`, and `TransformBase` themselves still live in `XRENGINE`, and `TransformBase` debug rendering still anchors that side to `RenderInfo`.
- Do not spend time trying to "move XRBase first" again; it does not unblock Runtime.Core ownership and would only re-open analysis already completed.

### 2. Runtime Rendering - Bulk Extraction

The plan targets moving the rendering subsystem out of `XRENGINE` into `Runtime.Rendering`. The first command/policy slice, render-graph metadata package, render-resource descriptor package, and post-process schema metadata now live in `Runtime.Rendering`. The larger rendering runtime still remains.

Target ownership per the modularization plan:

- `XRWindow`, viewports, render context, render thread coordination
- Render pipelines, render passes, materials, shaders, programs
- Mesh runtime types, texture runtime types, GPU resource abstractions
- Render synchronization, world rendering publication
- Rendering-facing scene components and renderable runtime objects

Candidate move areas in `XRENGINE`:

- [ ] `Rendering/` - the entire rendering pipeline (massive scope)
- [ ] Rendering-facing scene components under `Scene/Components/`
- [ ] Window/viewport management code

Strategy:

- Start with types that depend only on Runtime.Core, Data, and Extensions.
- Resource metadata and post-process schema metadata are now done.
- Materials, shaders, mesh types, and render-state ownership remain attractive next candidates only if their `XRWindow`/API-object coupling is peeled back first.

Completed rendering-owned slice:

- [x] `Rendering/RenderGraph/` metadata and synchronization package moved to `XREngine.Runtime.Rendering`
- [x] original `XRENGINE/Rendering/RenderGraph/` files deleted after the move
- [x] full solution build and Vulkan render-graph validation tests passed after the move
- [x] render-resource descriptor metadata moved to `XREngine.Runtime.Rendering`, with `XRENGINE` retaining only descriptor factories and physical-resource registry bindings
- [x] post-process schema metadata (`RenderPipelinePostProcessSchema`, parameter descriptors, tonemapping metadata) moved to `XREngine.Runtime.Rendering`
- [x] video-streaming contracts, resolver/orchestrator layer, FFmpeg runtime bootstrap, decode/session runtime, and GPU-upload service contract (`StreamOpenOptions`, `StreamVariantInfo`, `ResolvedStream`, `IMediaStreamSession`, `IHlsStreamResolver`, `IVideoFrameGpuActions`, `IRuntimeVideoStreamingServices`, `RuntimeVideoStreamingServices`, `VideoStreamingSubsystem`, `TwitchHlsStreamResolver`, `FFmpegStreamDecoder`, `HlsMediaStreamSession`, and `HlsReferenceRuntime`) moved to `XREngine.Runtime.Rendering`

Important note for the next pass:

- The major rendering ownership blocker is now the `GenericRenderObject -> AbstractRenderAPIObject(XRWindow)` chain.
- That coupling still prevents a clean bulk move of materials, textures, shaders, mesh runtime types, and the concrete render API object hierarchy.
- In the video-streaming path, the contract/factory seam now lives in `Runtime.Rendering`; the remaining blockers are the concrete backend upload adapters (`OpenGLVideoFrameGpuActions`, `VulkanVideoFrameGpuActions`) and the broader `XRWindow`/render-API ownership chain they still depend on inside `XRENGINE`.
- The next rendering move should target either another dependency-clean metadata/contract package or a deliberate break in the `XRWindow` ownership chain.

### 3. AnimationIntegration - First Anchors + Bulk

The project is empty. The plan says it should own:

- `Scene/Components/Animation/**` (animation scene components)
- Humanoid runtime integration and animation-driven transform/component application
- Motion capture receivers that drive runtime scene objects
- Animation diagnostics requiring runtime scene bindings

Candidate move areas in `XRENGINE`:

- [ ] Find dependency-clean animation option/contract types (similar to how modeling contracts moved)
- [ ] `Scene/Components/Animation/` - animation scene components (depend on Runtime.Core + Animation)
- [ ] Humanoid animation bridge code

Blocker:

- Most animation scene components depend on scene graph base types. Those base types need to move to Runtime.Core first, or AnimationIntegration needs to temporarily reference `XRENGINE`.

### 4. AudioIntegration - First Anchors + Bulk

The project is empty. The plan says it should own:

- `Scene/Components/Audio/**` (audio scene components)
- World/listener integration
- Steam Audio scene bridges (attach runtime geometry/components to audio APIs)
- Video/audio bridge code

Candidate move areas in `XRENGINE`:

- [ ] Find dependency-clean audio option/config types
- [ ] `Scene/Components/Audio/` - audio scene components (depend on Runtime.Core + Audio)
- [ ] Steam Audio scene integration code

Blocker:

- Same as animation: scene components depend on scene graph base types in `XRENGINE`.

### 5. InputIntegration - First Anchors + Bulk

The project is empty. The plan says it should own:

- Local/remote player controllers
- Pawn input assemblies
- Runtime camera/pawn input bridges
- VR action transforms and scene bindings
- Runtime window/viewport input routing

Candidate move areas in `XRENGINE`:

- [ ] Find dependency-clean input option/config types
- [ ] `Input/` - runtime controller code
- [ ] VR scene transforms wrapping input action sources
- [ ] Pawn input sets and camera/pawn control bridges

Blocker:

- Input controllers and VR transforms depend heavily on scene graph and rendering types.

### 6. ModelingBridge - Remaining Heavy Implementations

Contract/option types have moved successfully. The heavy implementations remain in `XRENGINE`:

- [ ] `XRMeshModelingExporter` - depends on `XRMesh`
- [ ] `XRMeshModelingImporter` - depends on `XRMesh`
- [ ] `XRMeshBooleanOperations` - depends on `XRMesh`

Blocker:

- `XRMesh` and related rendering mesh types need to move to Runtime.Rendering first, then the exporter/importer/boolean code can move to ModelingBridge without a reverse reference to `XRENGINE`.

### 7. Bootstrap - Remove Transitional XRENGINE Reference

Runtime.Bootstrap currently references `XRENGINE` directly. The target state is for Bootstrap to reference only the runtime layer projects:

- [ ] Replace Bootstrap's `XRENGINE` project reference with references to Runtime.Core, Runtime.Rendering, and the adapter projects
- [ ] Migrate any Bootstrap code that currently uses XRENGINE types to use runtime-layer equivalents
- [ ] Verify Server and Editor still build without Bootstrap pulling in XRENGINE transitively

Blocker:

- This can only complete after enough types have moved from `XRENGINE` into the runtime layer projects that Bootstrap's code can compile against them.

### 8. Editor-Owned Behavior Migration

The `IBootstrapEditorBridge` interface exists but the behavior behind its methods still lives in Editor-owned code. The remaining bridge methods that dispatch to editor implementations:

- [ ] `CreateSpecializedWorld` - editor-only specialized world creation
- [ ] `ImportModels` - model import flow
- [ ] `CreateEditorUi` - editor UI creation
- [ ] `EnableTransformToolForNode` - transform tool activation
- [ ] `CreateFlyableCameraPawn` - flyable editor pawn creation

For each:

- Determine whether the behavior is truly editor-only or should be runtime-owned.
- Runtime-owned behavior should migrate into the appropriate runtime layer project.
- Editor-only behavior stays behind the bridge as designed.

### 9. Adapter Project Population

The adapter project reference graph now matches the modularization plan. The remaining problem is that three adapter projects still have no owned code:

- [ ] `Runtime.AnimationIntegration` - add the first real animation scene/integration slice
- [ ] `Runtime.AudioIntegration` - add the first real audio scene/integration slice
- [ ] `Runtime.InputIntegration` - add the first real input controller/bridge slice

These are blocked more by missing Runtime.Core / Runtime.Rendering ownership than by project-file setup.

---

## Recommended Execution Order

The blockers above create a natural dependency chain:

1. **Runtime Core bulk moves** - move scene graph base types, engine lifecycle, and runtime model. This unblocks everything else.
2. **Runtime Rendering bulk moves** - move rendering types (`XRMesh`, materials, shaders, pipelines). This unblocks ModelingBridge heavy implementations and rendering-dependent adapter moves.
3. **Adapter bulk moves** - once core and rendering are substantially in their target projects, the adapter projects (Animation, Audio, Input, Modeling) can receive their scene components and bridge code.
4. **Bootstrap XRENGINE reference removal** - once the runtime layers have enough surface, Bootstrap stops referencing XRENGINE directly.
5. **Editor behavior migration** - final cleanup of which bridge behaviors are truly editor-only vs runtime-owned.

Each step should be done in small, coherent slices that keep the build green. The goal is not to finish every move in one sweep - it is to establish enough of the Runtime.Core and Runtime.Rendering surface that downstream moves become mechanical.

## Acceptance Criteria

Phase 2 is complete when:

- [ ] `Runtime.Core` owns engine lifecycle, scene graph base types, and runtime model (not just leaf utilities and metadata packages)
- [ ] `Runtime.Rendering` owns the rendering pipeline surface beyond command structs, metadata packages, and resource descriptors
- [ ] Each adapter project has at least one coherent subsystem slice (not just contracts/options)
- [ ] `Runtime.Bootstrap` no longer references `XRENGINE` directly
- [ ] Project references follow the intended one-way graph with no cycles
- [ ] `Runtime.Core` does not reference Animation, Audio, Input, or Modeling
- [ ] Editor, Server, VRClient, and all targeted tests build and pass

## Validation Protocol

After each slice:

1. Build the changed projects directly
2. Build full solution (`dotnet build XRENGINE.slnx`)
3. Run the nearest targeted tests for moved code
4. Inspect the project graph for accidental reverse references
5. Verify no new compiler warnings introduced
