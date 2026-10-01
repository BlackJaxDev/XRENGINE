# Caller-thread frame stepping and scheduling

Updated: 2026-10-01.

## Implemented boundary

The portable EngineTimer now accepts a host-driven frame clock. A host explicitly installs RuntimeWorkScheduler.ConfigureCallerThread, claims render-thread ownership, initializes engine services, then calls StartCallerThreadLoop followed by StepFrame(elapsedSeconds). It must not call the desktop RunGameLoop or blocking timer dispatch methods. Browser attempts to construct the default worker scheduler or start the threaded timer fail with named diagnostics.

StepFrame uses the same fixed-update, variable-update, collect, world/render swap, and render-dispatch callbacks as desktop. Its ordered execution is:

1. Pump bounded general and remote jobs, then update/app and physics work
2. Accumulate supplied elapsed time and execute at most four fixed ticks
3. Run one variable update (or preserve pause state)
4. Collect visibility, finish swap-affinity work, swap the existing world/render buffers, publish and consume that exact collect generation
5. Begin the production render identity, run render-affinity work and render callbacks, then complete the render identity

The host supplies finite non-negative elapsed seconds. Simulation elapsed time is capped at one second, matching the desktop dispatcher clamp. Fixed catch-up debt is capped at four ticks and excess debt is discarded. A paused single-step request is consumed once for both simulation phases. Zero elapsed is valid for a first render. Exceptions retain the first terminal fault, stop the lifecycle, and propagate to the caller. No new alternate world or render packet model is introduced.

ResetFrameTiming clears simulation debt and phase deltas between frames; the host must also reset its elapsed-time source after suspension. The explicit clock remains readable between frames and after stop. BeginExplicitFrame remains available to existing deterministic production harnesses; the ordinary frame step reuses its clock/ownership primitive without allocating a scope per frame.

Desktop retains its dedicated workers, pacing waits, modal resize behavior, and public dispatch APIs. It calls the extracted shared phase bodies; this change does not switch desktop hosts to caller-thread execution or establish desktop pacing equivalence. CPU dispatch duration uses Stopwatch rather than the externally advanced simulation clock.

## Job execution

JobManager supports explicit WorkerThreads and CallerThread execution modes. Caller-thread mode constructs no general or auxiliary workers and retains separate queues for general, remote, app, render, and collect/swap affinities. ProcessCallerThreadJobs advances bounded general or remote work; existing affinity pumps remain at their original frame boundaries. Queue admission and async-job readiness are checked without waiting. Pending async tasks and terminal callback ownership remain retained until completion. Shutdown is bounded and retryable: a false return means the host must retain dependent state and pump/retry after asynchronous work completes. It does not create shutdown-finalizer threads or substitute another scheduler.

Caller-thread transform flushes require sequential settings and use the existing immediate hierarchy traversal. Browser DEBUG startup no longer creates the optional profiler stats thread; requesting worker-backed frame logging in the browser reports unsupported capability.

## Validation and limits

- Static diff/whitespace checks pass
- Integrated browser build compiled Core and Host successfully with no warnings in those projects; the intermediate build failed later in the in-progress WebGPU renderer implementation
- A standalone process loaded the resulting production Core/Host/Rendering assemblies and drove the actual EngineTimer plus JobManager: 25 ms produced two fixed ticks then update/collect/swap/render; a five-second input was capped to four catch-up ticks; pause/single-step and timing reset behaved as specified
- Ten scheduled jobs drained through a capacity-four caller queue with zero worker count and all slots released. A pending caller-job synchronous wait rejected, and shutdown retained ownership until its async task completed
- After 512 warm-up frames, 2,048 empty production-dispatch frames allocated zero bytes on the caller thread. An injected render callback fault retained terminal evidence, stopped the lifecycle, and a fresh lifecycle reset the generation gate and rendered successfully
- Evidence: `Build/_AgentValidation/20261001-163800-webgpu-baseline/logs/browser-integration-1.log` and `logs/caller-frame-smoke.log` under the same run. The smoke did not compose a GPU backend or physical device; it does not establish visual or desktop pacing parity
- No test files were added or changed before live validation
- Double-buffer publication sequencing is reused, but backend submission/presentation still requires a composed renderer and viewport
- User job steps, cancellation callbacks, and engine event listeners must remain cooperative; a count/time budget cannot preempt a single blocking callback
- Threaded transform, asset, network, physics, animation, and rendering paths remain in the portable closure. The inventory below is a review list, not evidence that every match is browser-reachable or safe

## Blocking and worker inventory

Regenerated from tracked source using the same lexical expressions as the active runtime integration checklist. Counts are files, not call sites. The expressions intentionally retain comments, result properties, and string.Join false positives; multiline calls and Task.Factory.StartNew require separate review. New caller-thread partial files were separately reviewed for waits and worker dispatch.

| Project | Blocking-expression files | new Thread files | Task/parallel/pool files |
| --- | ---: | ---: | ---: |
| XREngine.Runtime.Host | 16 | 2 | 4 |
| XREngine.Runtime.Core | 29 | 4 | 8 |
| XREngine.Runtime.Rendering | 39 | 2 | 9 |
| XREngine.Data | 9 | 0 | 7 |
| XREngine.Extensions | 2 | 0 | 4 |
| XREngine.Animation | 1 | 0 | 0 |
| XREngine.Runtime.AudioIntegration | 3 | 0 | 2 |
| XREngine.Runtime.AnimationIntegration | 3 | 0 | 0 |
| XREngine.Runtime.InputIntegration | 1 | 0 | 1 |
| XREngine.Browser | 1 | 0 | 0 |

### Reachability triage

| Site / family | Status and next check |
| --- | --- |
| EngineTimer desktop loops and waits | Caller loop cannot enter the blocking public timer routes; threaded desktop behavior remains owned by desktop hosts |
| JobManager / JobHandle / RuntimeWorkScheduler | Explicit caller executor uses nonblocking admission and async readiness; pending synchronous job waits reject caller execution |
| RuntimeWorld.Transforms | Caller executor requires sequential traversal and uses immediate matrix recursion; desktop parallel/asynchronous branches remain |
| Engine.CodeProfiler stats loop | Disabled by default on browser; explicit enable rejects unsupported worker-backed logging |
| RuntimeThreadDispatcher.InvokePhysics | Inline on registered caller physics owner; off-owner wait remains for desktop and must not be reached by a browser leaf |
| CollectVisibleGenerationGate | Caller steps use generation transitions and TryConsumeFresh only; blocking publication wait remains desktop-only |
| EngineGeneralWorkDomain / EngineJobAuxiliaryWorkDomain / RenderWorkDomain | Threaded domains are not constructed by ConfigureCallerThread; render backends requiring a domain still need capability validation |
| AssetManager serializers, remote loading, metadata | Async asset-source and browser boot work is separate; synchronous entry points need a reachable-call audit for actual cooked-world content |
| SceneNode / prefab / game-mode transform setup | Several sequential completed-task waits remain; review custom virtual transforms and prefer immediate traversal or true async ownership |
| Physics chains / convex authoring / GPU readback | Capability-specific waits and worker schedulers remain; browser worlds must reject unavailable native/GPU paths until implemented |
| Network transport / replication identity | Transport startup and schema/asset-ID initialization need browser-specific asynchronous/capability review |
| Humanoid / VR calibration / audio conversion | Optional subsystem waits/Task.Run remain; enabling those components requires separate leaf validation |
| Shared events and collection/array parallel helpers | Synchronous ordinary event invocation is used by StepFrame; callers of explicit parallel helpers remain responsible for capability selection |

### Files matched

#### XREngine.Runtime.Host

Blocking expressions:

- `XREngine.Runtime.Host/Core/SnapshotDiagnostics.cs`
- `XREngine.Runtime.Host/Core/Time/EngineTimer.cs`
- `XREngine.Runtime.Host/Core/WorldStateSnapshot.cs`
- `XREngine.Runtime.Host/Engine/Engine.Lifecycle.cs`
- `XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs`
- `XREngine.Runtime.Host/Engine/Engine.Project.cs`
- `XREngine.Runtime.Host/Engine/Engine.State.cs`
- `XREngine.Runtime.Host/Engine/Engine.TickList.cs`
- `XREngine.Runtime.Host/Engine/Engine.WorkSchedulerValidation.cs`
- `XREngine.Runtime.Host/Engine/Subclasses/Engine.CodeProfiler.cs`
- `XREngine.Runtime.Host/Engine/Subclasses/Engine.PlayMode.cs`
- `XREngine.Runtime.Host/RenderingHost/Engine.RuntimeRenderingHostServices.cs`
- `XREngine.Runtime.Host/RenderingHost/Engine.ViewportRebind.cs`
- `XREngine.Runtime.Host/RenderingHost/Engine.Windows.cs`
- `XREngine.Runtime.Host/Settings/UnitTestingWorldSettings.cs`
- `XREngine.Runtime.Host/SubsystemHost/Engine.RuntimeGameModeHostServices.cs`

Explicit thread construction:

- `XREngine.Runtime.Host/Core/Time/EngineTimer.cs`
- `XREngine.Runtime.Host/Engine/Subclasses/Engine.CodeProfiler.cs`

Task / parallel / thread pool:

- `XREngine.Runtime.Host/Engine/Engine.Lifecycle.cs`
- `XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs`
- `XREngine.Runtime.Host/Engine/Engine.TickList.cs`
- `XREngine.Runtime.Host/Settings/EditorPreferences.cs`


#### XREngine.Runtime.Core

Blocking expressions:

- `XREngine.Runtime.Core/Assets/AssetManager.Metadata.cs`
- `XREngine.Runtime.Core/Assets/AssetManager.Serialization.cs`
- `XREngine.Runtime.Core/Assets/AssetManager.cs`
- `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Api.Core.cs`
- `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Remote.Api.cs`
- `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.SerializationAndCache.cs`
- `XREngine.Runtime.Core/Core/RuntimeThreadDispatcher.cs`
- `XREngine.Runtime.Core/Core/Time/CollectVisibleGenerationGate.cs`
- `XREngine.Runtime.Core/Execution/EngineGeneralWorkDomain.cs`
- `XREngine.Runtime.Core/Execution/EngineJobAuxiliaryWorkDomain.cs`
- `XREngine.Runtime.Core/Execution/RenderWorkBatch.cs`
- `XREngine.Runtime.Core/Execution/RenderWorkDomain.DispatchPolicy.cs`
- `XREngine.Runtime.Core/Execution/RenderWorkDomain.cs`
- `XREngine.Runtime.Core/Execution/RuntimeWorkScheduler.cs`
- `XREngine.Runtime.Core/JobHandle.cs`
- `XREngine.Runtime.Core/JobManager.cs`
- `XREngine.Runtime.Core/Networking/BaseNetworkingManager.cs`
- `XREngine.Runtime.Core/Networking/ClientNetworkingManager.TlsTransport.cs`
- `XREngine.Runtime.Core/Networking/Replication/NetworkReplicationSchemaRegistry.cs`
- `XREngine.Runtime.Core/Networking/WorldAssetIdentityProvider.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/CPU/PhysicsChainCpuWorkScheduler.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/ConvexPhysicsActorComponent.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainReadbackService.Transfer.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.cs`
- `XREngine.Runtime.Core/Scene/Prefabs/SceneNodePrefabUtility.cs`
- `XREngine.Runtime.Core/Scene/SceneNode.cs`
- `XREngine.Runtime.Core/Scene/Transforms/Misc/DrivenWorldTransform.cs`
- `XREngine.Runtime.Core/World/RuntimeWorld.Transforms.cs`

Explicit thread construction:

- `XREngine.Runtime.Core/Execution/EngineGeneralWorkDomain.cs`
- `XREngine.Runtime.Core/Execution/EngineJobAuxiliaryWorkDomain.cs`
- `XREngine.Runtime.Core/Execution/RenderWorkDomain.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/CPU/PhysicsChainCpuWorkScheduler.cs`

Task / parallel / thread pool:

- `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.Api.cs`
- `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.SerializationAndCache.cs`
- `XREngine.Runtime.Core/Core/Diagnostics/Debug.cs`
- `XREngine.Runtime.Core/JobManager.cs`
- `XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainWorld.cs`
- `XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs`
- `XREngine.Runtime.Core/World/RuntimeWorld.Transforms.cs`
- `XREngine.Runtime.Core/World/XRWorldObjectBase.cs`


#### XREngine.Runtime.Rendering

Blocking expressions:

- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.Uber.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Geometry.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.StreamingPayload.cs`
- `XREngine.Runtime.Rendering/RenderGraph/RenderGraphSynchronization.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXrSmokePhase524bEvidenceValidator.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs`
- `XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Overflow.cs`
- `XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBvhScheduler.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Materials/MaterialBindingLayout.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/RenderPipelineGpuProfiler.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Scripting/RenderPipelineScript.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.ResourceLogging.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs`
- `XREngine.Runtime.Rendering/Rendering/Profiling/ComponentProfiles/RenderProfileSessionManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Resources/Builder/RenderPipelineResourceLayoutBuilder.cs`
- `XREngine.Runtime.Rendering/Rendering/Resources/RenderPipelineResourceLayout.cs`
- `XREngine.Runtime.Rendering/Rendering/Shaders/Generator/ShaderGraphGenerator.cs`
- `XREngine.Runtime.Rendering/Rendering/SharedRenderHelperGeometry.cs`
- `XREngine.Runtime.Rendering/Rendering/Tools/OctahedralImposterGenerator.cs`
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Resources/Fonts/FontGlyphSet.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderHelper.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/ShaderSourceResolver.cs`
- `XREngine.Runtime.Rendering/Resources/Shaders/UberShaderVariantBuilder.cs`
- `XREngine.Runtime.Rendering/Runtime/RenderForegroundWorkCoordinator.cs`
- `XREngine.Runtime.Rendering/Runtime/RendererReload/RendererReloadFailureInjection.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.SecondaryContext.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.VulkanUpscaleBridge.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeWindowApplicationServices.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Camera/CameraComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Lights/Types/PointLightComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Mesh/OctahedralBillboardComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Skinning.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Core/UIVideoComponent.Pipeline.cs`
- `XREngine.Runtime.Rendering/Scene/Transforms/Misc/BillboardTransform.cs`

Explicit thread construction:

- `XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.SecondaryContext.cs`
- `XREngine.Runtime.Rendering/Runtime/RuntimeRenderThreadHost.cs`

Task / parallel / thread pool:

- `XREngine.Runtime.Rendering/Objects/Materials/XRMaterial.Uber.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Geometry.cs`
- `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.VertexPopulation.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/Mipmap2D.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/XRTexture2D.cs`
- `XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBvhScheduler.cs`
- `XREngine.Runtime.Rendering/Rendering/Profiling/ComponentProfiles/RenderProfileSessionManager.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Particles/ParticleEmitterComponent_Old.cs`
- `XREngine.Runtime.Rendering/Scene/Components/UI/Text/UITextComponent.cs`


#### XREngine.Data

Blocking expressions:

- `XREngine.Data/Core/Assets/XRAsset.cs`
- `XREngine.Data/Core/Events/XRBoolEvent.cs`
- `XREngine.Data/Core/SessionSettingsOverlay.cs`
- `XREngine.Data/MMD/VMD/Dicts/AnimationBase.cs`
- `XREngine.Data/MMD/VMD/Dicts/Bone/BoneFrameKey.cs`
- `XREngine.Data/MMD/VMD/Lists/Camera/CameraKeyFrameKey.cs`
- `XREngine.Data/MMD/VMD/Lists/Lamp/LampKeyFrameKey.cs`
- `XREngine.Data/MMD/VMD/Lists/Property/PropertyFrameKey.cs`
- `XREngine.Data/Runtime/XRRuntimeEnvironment.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- `XREngine.Data/ArchiveExtractor.cs`
- `XREngine.Data/Core/Assets/XRAsset.cs`
- `XREngine.Data/Core/Events/XRBoolEvent.cs`
- `XREngine.Data/Core/Events/XREvent.cs`
- `XREngine.Data/Core/Files/AssetPacker/AssetPacker.cs`
- `XREngine.Data/Core/Memory/Compression.cs`
- `XREngine.Data/TextFile.cs`


#### XREngine.Extensions

Blocking expressions:

- `XREngine.Extensions/Code/Task.cs`
- `XREngine.Extensions/Reflection/Type.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- `XREngine.Extensions/Array.cs`
- `XREngine.Extensions/Enumerable.cs`
- `XREngine.Extensions/List.cs`
- `XREngine.Extensions/String.cs`


#### XREngine.Animation

Blocking expressions:

- `XREngine.Animation/Property/Core/AnimationMember.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- None matched


#### XREngine.Runtime.AudioIntegration

Blocking expressions:

- `XREngine.Runtime.AudioIntegration/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs`
- `XREngine.Runtime.AudioIntegration/Scene/Components/Audio/MicrophoneComponent.cs`
- `XREngine.Runtime.AudioIntegration/Scene/Components/Audio/VoiceMcpBridgeComponent.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- `XREngine.Runtime.AudioIntegration/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs`
- `XREngine.Runtime.AudioIntegration/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs`


#### XREngine.Runtime.AnimationIntegration

Blocking expressions:

- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/Diagnostics/HumanoidPoseAuditOverlayComponent.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.AvatarDefinition.cs`
- `XREngine.Runtime.AnimationIntegration/Scene/Components/Animation/HumanoidComponent.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- None matched


#### XREngine.Runtime.InputIntegration

Blocking expressions:

- `XREngine.Runtime.InputIntegration/Scene/Components/VR/VRPlayerCharacterComponent.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- `XREngine.Runtime.InputIntegration/Scene/Components/Movement/HeightScaleBaseComponent.cs`


#### XREngine.Browser

Blocking expressions:

- `XREngine.Browser/SceneBoot.cs`

Explicit thread construction:

- None matched

Task / parallel / thread pool:

- None matched
