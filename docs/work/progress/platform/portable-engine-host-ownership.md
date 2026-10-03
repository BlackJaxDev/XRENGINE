# Portable Engine Host Ownership

[Work index](../../README.md) · [Active runtime checklist](../../todo/platform/unified-desktop-browser-runtime-todo.md) · [Project organization](../../../architecture/runtime/project-organization.md)

Status: ownership inventory, source extraction and selected desktop host qualification are complete. D1 approves `XREngine.Runtime.Host`; D6 approves the shared PowerShell factory generator. The final full solution, WebGPU closure, 15-project Release browser compile lane, fresh browser publish and MonkeyBall Development Debug build pass with zero warnings/errors. OpenGL/Vulkan Editor restore, Server and VRClient startup evidence and their limits are recorded in the [host validation record](../../investigations/platform/portable-engine-host-validation.md). Browser-world execution and broader native integration acceptance remain separate work.

## Scope and classification

The source table covers all 165 tracked Bootstrap C# files before extraction. The installed-service table covers 37 rows: the checklist's shared host slots plus app/leaf registrations needed to understand composition. There are 112 portable sources, nine sources needing a dependency cut, and 44 desktop sources. Categories describe source ownership and compilation eligibility, not browser execution or trimming qualification.

- **P — portable as-is:** managed implementation or metadata using the shared contracts. Moving its `Engine` dependency together is an ownership change, not a second runtime. Synchronous I/O, reflection and worker/wait reachability still require their separate audits.
- **R — portable after contract replacement:** shared behavior currently coupled to a desktop implementation or factory. Consume the existing renderer, physics, window and VR contracts. Desktop startup policy and display extent require narrow typed seams; there is no existing shared primary-display service.
- **D — desktop only:** native implementation, desktop application composition, development import, or launch/world-fixture workflow. Some metadata could compile on a neutral framework; its responsibility remains with the desktop workflow.

Managed editor preferences move with the facade that consumes them; editor UI and secret-provider implementations remain outside Host. Static game-mode registration belongs in Host because the registered game modes already live in portable InputIntegration and registration does not activate their platform features. These choices follow the approved host boundary and need no new dependency or owner decision.

## Dependency cuts and lifetime constraints

All `Engine` and `EngineTimer` partials move together, preserving `XREngine.Engine` and `XREngine.Timers.EngineTimer`. Native backend registration moves out of the facade's static constructor. Its managed default settings, owner-thread assignment and settings-cascade suppression retain their ordering; startup must not create workers or invoke platform callbacks while holding a type-initializer lock.

Desktop installs leaves and a stable startup-policy instance before the first asset access. Editor, Server and VRClient currently access assets before application installation, so their composition order changes explicitly. The policy supplies sandbox/default settings, display extent and managed-client/environment ingress. It is required at explicit startup boundaries, never by the facade's static constructor. Protocol, verified-world and transport validation remain shared and execute before networking starts. Caller-provided default-settings factories do not require an unused platform default factory.

Rendering host services consume caller-supplied catalogs and a default-pipeline factory. Desktop composition owns module/catalog/asset leases. The headless world host receives an `AbstractPhysicsScene` factory rather than constructing `JoltScene`; desktop server composition retains its current Jolt selection. Native-window permission must not determine whether a future canvas world receives rendering.

VR input/state/lifecycle providers remain desktop-owned. Capture prior lifecycle before constructing the state provider, because its constructor changes that slot. Preserve the existing gates: input/state/lifecycle are installed for the Input adapter; VR rendering is installed when the profile permits VR. Keep these providers installed through all shared adapter disposal, including world, pawn and controller teardown. Restore their service setters and dispose owned providers on rollback and teardown. Capability and startup-policy leases restore last.

The pre-move compiled Bootstrap assembly exposes 152 public identities, recorded through PE metadata without loading native libraries. The compiled post-move comparison preserves every name: 117 moved to Host and 35 remain in Bootstrap, with no overlap or omissions. Host adds two startup-policy contract identities. The [identity audit](native-subsystem-type-identities.md#shared-engine-host-identities) records all moved names and snapshot resolution across old assembly qualifiers. Persisted asset and live startup validation remain required.

## Implemented ownership and qualification

The 121 reviewed source files move unchanged initially, followed by the dependency cuts. Host targets `net10.0`, is registered in the portable project/package manifests and solution, and uses the same source set for desktop and `browser-wasm`. Core and Rendering grant Host the internal access previously needed by Bootstrap. No desktop leaf or model-authoring implementation enters the Host reference graph.

Startup contracts are explicit: `IRuntimeEngineStartupPolicy` supplies host defaults, display extent, settings preparation, and launch ingress; missing policy fails before initialization mutations. Desktop's stable policy keeps one-shot handoff state and the existing managed-world preflight. Shared networking still validates the verified world, protocol, and transport before starting it. Shared snapshot references resolve saved type names through the metadata resolver.

Desktop provider preparation precedes the first asset access in Editor, Server, VRClient, and generated launchers. Application/world leases unwind before model-import leases. Rendered-world choice is independent of native-window permission, preserving presentationless RenderBench worlds. Previous adapters retire while their providers are still installed. VR provider leases remain active through all world, pawn, and controller disposal; constructor failure restores prior slots and removes owned hooks. Shared adapter installation rolls back partial installation and attempts every cleanup step after a failure.

The generator uses explicit portable and desktop input sets. Isolated comparison preserves all 67 prior portable factory lines, while Rendering exclusively owns its 124 built-in commands. Browser bridge generation preserves its manifest schema and output API, and normal builds replace the former checked-in generated file and Python generator. Generated files and their source inputs are validated by the portability checks; reflection admissions are narrow and qualify only the untrimmed interpreter.

Disposable evidence is under `Build/_AgentValidation/20260930-105523-unified-browser-runtime/`: final gate logs are `logs/portable-host-qualified-*.log`; runtime calls and measurements are in `reports/portable-host/`. Required counts and findings remain in tracked records. Fresh OpenGL/Vulkan Before→Play→Edit→After captures preserve the active camera and editor UI, Server runs the headless unit world, and VRClient's local unit world measures 89.808 Hz variable updates over 30 ready samples. All three timer source files retain their pre-move contents. VRClient's inherited shutdown queue stall, physical HMD operation and broader test-suite acceptance remain unqualified; these smokes do not establish browser gameplay.

## Source ownership table

Paths identify the pre-move source. Host keeps the same suffix for moved sources. The type/symbol column identifies each file's responsibility; replacement rows have additional constraints above.

| Source | Types / responsibility | Category | Intended owner |
| --- | --- | --- | --- |
| `XREngine.Runtime.Bootstrap/BootstrapNetworkingWorldProfiles.cs` | BootstrapNetworkingWorldProfiles: BootstrapNetworkingWorldProfiles | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/BootstrapNetworkingWorldProfiles.ENetworkingPoseRole.cs` | BootstrapNetworkingWorldProfiles; ENetworkingPoseRole: BootstrapNetworkingWorldProfiles ENetworkingPoseRole | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/BootstrapPawnFactory.cs` | BootstrapPawnFactory: Creates a VR pawn and its complete scene hierarchy for runtime possession. The caller owns the returned root and chooses when to possess the pawn. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/BootstrapRenderSettings.cs` | BootstrapRenderSettings: Creates the explicitly configured scene pipeline, or delegates to runtime automatic selection when no selection was configured. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/BootstrapStartupWork.cs` | BootstrapStartupWork; DeferredStartupWorkItem: BootstrapStartupWork | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/BootstrapWorldFactory.cs` | BootstrapWorldFactory: Creates the simulation-only world used by a dedicated server. The server must not create a local pawn, camera, audio listener, or VR/input component because it has no local device ownership and… | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/BootstrapEditorBridge.cs` | BootstrapEditorBridge: BootstrapEditorBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/BootstrapModelImportBridge.cs` | BootstrapModelImportBridge: BootstrapModelImportBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/BootstrapWorldBridge.cs` | BootstrapWorldBridge: BootstrapWorldBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/IBootstrapEditorBridge.cs` | IBootstrapEditorBridge: IBootstrapEditorBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/IBootstrapModelImportBridge.cs` | IBootstrapModelImportBridge: IBootstrapModelImportBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Bridges/IBootstrapWorldBridge.cs` | IBootstrapWorldBridge: IBootstrapWorldBridge | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapLightingBuilder.cs` | BootstrapLightingBuilder; DynamicDebugLightState: BootstrapLightingBuilder | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapLightingBuilder.DynamicDebugLightRigComponent.cs` | BootstrapLightingBuilder; DynamicDebugLightRigComponent: BootstrapLightingBuilder DynamicDebugLightRigComponent | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapModelBuilder.cs` | BootstrapModelBuilder: BootstrapModelBuilder | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapPhase524bValidationBuilder.cs` | BootstrapPhase524bValidationBuilder: Builds the deterministic, query-eligible scene used by the Vulkan/OpenXR Phase 5.2.4b acceptance validator. The workload is never present unless the dedicated validation environment switch is enabled. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapPhysicsBuilder.cs` | BootstrapPhysicsBuilder: BootstrapPhysicsBuilder | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapPhysicsTestWorldBuilder.cs` | BootstrapPhysicsTestWorldBuilder: Builds a deterministic, backend-neutral playground for visual and interactive physics validation. Stable zone and fixture names are intentional so MCP and automated tests can locate them. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapWaterBuilder.cs` | BootstrapWaterBuilder: BootstrapWaterBuilder | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Builders/BootstrapWaterBuilder.DynamicWaterPreviewControllerComponent.cs` | BootstrapWaterBuilder; DynamicWaterPreviewControllerComponent: BootstrapWaterBuilder DynamicWaterPreviewControllerComponent | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Composition/RuntimeAssetBootstrap.cs` | RuntimeAssetBootstrap; SharedInstallationLease: RuntimeAssetBootstrap | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Converters/MeshSubmissionStrategyJsonConverter.cs` | MeshSubmissionStrategyJsonConverter: MeshSubmissionStrategyJsonConverter | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Converters/ModelPostImportFlagsJsonConverter.cs` | ModelPostImportFlagsJsonConverter: Tolerant converter for . Accepts strings (including empty/whitespace as ), integers, and null. Writes the value as a comma-separated flag-name string so generated JSON stays human-readable. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/GameState.cs` | GameState: GameState | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/SnapshotAssetReference.cs` | SnapshotAssetReference: Lightweight handle written into world snapshots whenever a referenced asset should be preserved by pointer instead of duplicating its full serialized payload. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/SnapshotDiagnostics.cs` | SnapshotAssetSerializationMode; SnapshotDiagnostics; Scope; Session; BufferStats; WorldAssetSummary: SnapshotDiagnostics | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/SnapshotSceneReferenceResolver.cs` | SnapshotSceneReferenceResolver: Rebinds scene-owned references that cooked snapshot encoding restores as detached objects with the same serialized identity. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/Time/EngineTimer.cs` | EngineTimer: This is the delta used for physics and other fixed-timestep calculations. Fixed-timestep is consistent and does not vary based on rendering speed. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/Time/EngineTimer.DeltaManager.cs` | EngineTimer; DeltaManager: Gets a float representing the frequency of frame events, in hertz (updates per second). Max value is clamped to 10k. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/Time/EngineTimer.ExplicitFrames.cs` | EngineTimer; ExplicitFrameScope: Opens one deterministic, single-threaded frame clock while the normal timer is stopped. The caller must drive the real collect-generation gate in submission order. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/Time/EngineTimerTerminalFault.cs` | : Immutable evidence retained for the first fault that terminates an engine timer run. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Core/WorldStateSnapshot.cs` | WorldStateSnapshot: Captures the state of a world for later restoration when exiting play mode. Uses the cooked binary serializer, augmented with snapshot-specific filtering to keep payloads minimal. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.ConvexHullInputProvider.cs` | EngineConvexHullInputProvider: Adapts facade-owned model and render meshes to Runtime.Core collision input. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.cs` | Engine; PooledExternalProfilerScope: The root static class for the XREngine runtime. This class serves as the central hub for all engine operations, managing: Engine lifecycle (initialization, game loop, shutdown) Window and viewport… | R | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Lifecycle.cs` | Engine: Engine lifecycle management - initialization, game loop, and shutdown. | R | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.MainThreadInvokeLog.cs` | Engine; MainThreadInvokeMode; MainThreadInvokeEntry: Engine MainThreadInvokeLog | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Memory.cs` | Engine: Engine Memory | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Networking.cs` | Engine: Networking, VR initialization, and remote job handling for the engine. | R | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.NetworkSimulation.cs` | Engine: Runs a bounded world operation between simulation steps. The asynchronous physics fence avoids blocking the update thread while an in-flight physics callback may itself require that thread. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.ProfileCapture.cs` | Engine; ProfileCapture: Applies the non-intrusive observer policy before renderer creation. The overrides are process-local and only activate for an explicitly selected clean or release benchmark profile. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.ProfilerSender.cs` | Engine: Wires up the delegate-based collectors on so it can read engine stats without a direct assembly reference. Safe to call multiple times — just overwrites the delegates. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Project.cs` | Engine: The currently loaded project, if any. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeMaintenanceServices.cs` | EngineRuntimeMaintenanceServices: Engine RuntimeMaintenanceServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeNetworkDiscoveryHostServices.cs` | EngineRuntimeNetworkDiscoveryHostServices: Engine RuntimeNetworkDiscoveryHostServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimePhysicsServices.cs` | EngineRuntimePhysicsServices: Engine RuntimePhysicsServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeSceneNodeServices.cs` | EngineRuntimeSceneNodeServices: Engine RuntimeSceneNodeServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeSceneStreamingHostServices.cs` | EngineRuntimeSceneStreamingHostServices; SceneHandle: Engine RuntimeSceneStreamingHostServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeStaticColliderAuthoringServices.cs` | EngineRuntimeStaticColliderAuthoringServices; State: Engine RuntimeStaticColliderAuthoringServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeThreadServices.cs` | EngineRuntimeThreadServices: Engine RuntimeThreadServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeTimingServices.cs` | EngineRuntimeTimingServices: Publishes the application-owned engine timer through the lower runtime timing contract. The renderer/window loop remains outside Runtime.Core. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeTransformServices.cs` | EngineRuntimeTransformServices; TransformDebugHandle; TransformDebugRenderable: Engine RuntimeTransformServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.RuntimeWorldObjectServices.cs` | EngineRuntimeWorldObjectServices: Engine RuntimeWorldObjectServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Settings.cs` | Engine; SettingsCascadeSuppressionScope: Settings properties, change handlers, and settings application for the engine. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.State.cs` | Engine; WindowCloseRequestResult; State: Owns application-level engine state and player composition. public static partial class Engine { Whether the engine is running in editor mode (as opposed to standalone game). This is set at startup… | R | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Threading.cs` | Engine: Threading properties and task scheduling functionality for the engine. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.TickList.cs` | Engine; TickList; TickEntry: Ticks all items in this list. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.WorkSchedulerValidation.cs` | Engine: Proves the installed runtime work capability, including post-warmup allocation closure, and decodes one already-completed telemetry payload. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Engine.Worlds.cs` | Engine: Resolves the Bootstrap-owned host for a serialized world and returns its canonical Core runtime context. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Execution/EngineSchedulerSmokeExecutor.cs` | EngineSchedulerSmokeExecutor: Renderer-neutral startup proof for disjoint preparation ranges. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/IGameLaunchBootstrap.cs` | IGameLaunchBootstrap: Provides a statically rooted composition entry point for a published game. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/IGameLaunchRuntimeSmokeBootstrap.cs` | IGameLaunchRuntimeSmokeBootstrap: Extends a published game's bootstrap with an automated runtime validation that runs after the ordinary archive checks performed by --aot-smoke. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Networking/Engine.NetworkingDiscovery.cs` | Engine: Public helper to (re)configure networking at runtime using the existing initialization path. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Networking/Engine.ServerSessionResolver.cs` | Engine: Transitional application-facing realtime session hooks. Bootstrap adapts these delegates to the Runtime.Core networking host boundary. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Networking/RealtimeJoinHandoff.cs` | RealtimeJoinHandoff: RealtimeJoinHandoff | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/PerformanceProfileDebugHostServices.cs` | PerformanceProfileDebugHostServices: Applies a process-local verbosity ceiling while preserving the configured debug host's file routing and recency policy. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.CodeProfiler.cs` | Engine; CodeProfiler; CompletedScopeEvent; ThreadProducerState; LinkedThreadProducerState; LinkedScopeContext; ThreadProducerBuffer; AsyncPendingTimer; ProfilerScope; BuiltTimer; ThreadBuildState; ComponentTimingFrameState; ComponentTimingAccumulator; CodeProfilerTimer; ProfilerFrameSnapshot; ProfilerThreadSnapshot; ProfilerNodeSnapshot; ProfilerComponentFrameSnapshot; ProfilerComponentTimingSnapshot: Event-based code profiler with near-zero overhead on the calling thread. Start() only captures a timestamp and pushes a lightweight event to a queue. All tree reconstruction and processing happens on… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.EffectiveSettings.cs` | Engine; EffectiveSettings; SettingSource: Provides resolved effective settings values using the cascading override system. Resolution order: User Settings > Game Settings > Project Engine Defaults > Global Engine Defaults. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.Physics.cs` | Engine; Physics: Engine Physics | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.PlayMode.cs` | Engine; PlayMode: Manages play mode state and transitions for the engine. Handles entering/exiting play mode, physics control, and state management. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.ThreadAllocationTracker.cs` | Engine; ThreadAllocationTracker; AllocationRing; AllocationScopeRing; AllocationScope; AllocationRingSnapshot; ThreadAllocationSnapshot; AllocationScopeSnapshot: Engine ThreadAllocationTracker | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Engine.Time.cs` | Engine; Time: This delta is the time that has passed since the last update, in seconds. Not affected by time dilation. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.cs` | EngineRenderingSettingsApplication: Forces initialization of the application-owned settings side-effect boundary. Runtime.Rendering owns settings data and notification; XRENGINE applies world, window, shader, and renderer consequences. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Engine/Subclasses/Rendering/EngineRenderingSettingsApplication.Preferences.cs` | EngineRenderingSettingsApplication: Binds a full Advanced family to one RVC-owned OpenXR eye only after the renderer has reserved that exact mono output. An unavailable Available-mode eye retains the complete Default oracle chain; it… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/EngineRuntimeWindowApplicationServices.cs` | EngineRuntimeWindowApplicationServices: EngineRuntimeWindowApplicationServices | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientJoinCoordinator.cs` | ManagedClientJoinCoordinator: ManagedClientJoinCoordinator | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientJoinFailureKind.cs` | ManagedClientJoinFailureKind: ManagedClientJoinFailureKind | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientJoinState.cs` | ManagedClientJoinState: ManagedClientJoinState | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientJoinStatus.cs` | ManagedClientJoinStatus: ManagedClientJoinStatus | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientLaunchLease.cs` | ManagedClientLaunchLease: ManagedClientLaunchLease | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedClientWorldLoader.cs` | ManagedClientWorldLoader: Loads the exact verified managed-client world before networking opens a socket. public static class ManagedClientWorldLoader { public const string ConfigurationEnvironmentVariable =… | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedInstanceServiceClient.cs` | ManagedInstanceServiceClient: Credential-safe client for the authenticated local managed-instance service. The bearer credential is sent only in an HTTP authorization header and is never included in route, query, diagnostics, or… | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedInstanceServiceClientJsonContext.cs` | ManagedInstanceServiceClientJsonContext: ManagedInstanceServiceClientJsonContext | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedServiceCreateInstanceRequest.cs` | ManagedServiceCreateInstanceRequest: ManagedServiceCreateInstanceRequest | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/ManagedServiceReservationRequest.cs` | ManagedServiceReservationRequest: ManagedServiceReservationRequest | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Phase524bScenarioComponent.cs` | BootstrapPhase524bValidationBuilder; Phase524bScenarioComponent: Phase524bScenarioComponent | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RenderingHost/BuiltInPhysicsBackendModules.cs` | BuiltInPhysicsBackendModules: BuiltInPhysicsBackendModules | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RenderingHost/BuiltInRendererBackendModules.cs` | BuiltInRendererBackendModules: Statically composes the built-in renderer leaf assemblies without reflection. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs` | EngineRuntimeRenderingHostServices: Engine RuntimeRenderingHostServices | R | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderObjectServices.cs` | EngineRuntimeRenderObjectServices: Engine RuntimeRenderObjectServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeShaderServices.cs` | EngineRuntimeShaderServices: Engine RuntimeShaderServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeVideoStreamingServices.cs` | EngineRuntimeVideoStreamingServices: Engine RuntimeVideoStreamingServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeVrRenderingServices.cs` | EngineRuntimeVrRenderingServices; EngineRuntimeVrEyeCamera; EngineRuntimeVrRenderModelProvider; EngineRuntimeVrRenderModelHandle: Engine RuntimeVrRenderingServices | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.ViewportRebind.cs` | Engine: Bootstrap-owned play mode diagnostics and viewport rebinding. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/Engine.Windows.cs` | Engine: Bootstrap-owned window and viewport composition. | R | Portable Host |
| `XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs` | RuntimeRenderingBootstrap; InstallationLease: Installs concrete rendering and adapter services at an application composition root. public static class RuntimeRenderingBootstrap { Compatibility entry point for callers that have not selected an… | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RuntimeApplicationBootstrap.cs` | RuntimeApplicationBootstrap; ApplicationInstallation: RuntimeApplicationBootstrap | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RuntimeApplicationProfile.cs` | : Explicit application composition profile. It determines which optional adapters and local-device services are installed; it does not describe renderer or input implementation details. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/RuntimeBootstrapState.cs` | RuntimeBootstrapState: RuntimeBootstrapState | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/RuntimeStartupPolicy.cs` | RuntimeStartupPolicy: RuntimeStartupPolicy | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/Serialization/BootstrapAssetSerializationRegistration.cs` | BootstrapAssetSerializationRegistration: BootstrapAssetSerializationRegistration | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Serialization/BootstrapPublishedCookedAssetRegistration.cs` | BootstrapPublishedCookedAssetRegistration: BootstrapPublishedCookedAssetRegistration | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Serialization/SnapshotBinarySerializer.cs` | SnapshotBinarySerializer: Wraps the cooked binary serializer with snapshot-specific filtering so play-mode captures stay compact and never duplicate heavyweight asset data like meshes or textures. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Serialization/SnapshotYamlSerializer.cs` | SnapshotYamlSerializer: Historical snapshot YAML hook retained only as an explicit published-runtime guard. Editor/dev snapshot serialization should go through the normal YAML asset pipeline. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferenceGroups.cs` | EditorViewportPreferences; EditorSelectionPreferences; EditorDiagnosticsPreferences; EditorGeneralDiagnosticsPreferences; EditorVisualizationDiagnosticsPreferences; EditorRenderPipelineDiagnosticsPreferences; EditorCullingDiagnosticsPreferences; EditorExceptionDiagnosticsPreferences; EditorOpenGLDiagnosticsPreferences; EditorVulkanDiagnosticsPreferences; EditorProfilerPreferences: EditorPreferenceGroups | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferenceOverrideGroups.cs` | EditorPreferencesOverrides; EditorViewportPreferenceOverrides; EditorSelectionPreferenceOverrides; EditorDiagnosticsPreferenceOverrides; EditorGeneralDiagnosticsPreferenceOverrides; EditorVisualizationDiagnosticsPreferenceOverrides; EditorRenderPipelineDiagnosticsPreferenceOverrides; EditorCullingDiagnosticsPreferenceOverrides; EditorExceptionDiagnosticsPreferenceOverrides; EditorOpenGLDiagnosticsPreferenceOverrides; EditorVulkanDiagnosticsPreferenceOverrides; EditorProfilerPreferenceOverrides: EditorPreferenceOverrideGroups | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferences.cs` | EditorPreferences; ESceneDepthModePreference; EViewportPresentationMode; EditorThemeSettings; EDebugPrimitiveBufferFormat; LegacyDebugVisualizerPopulationMode; EDebugShapePopulationMode; EditorDebugOptions: Editor-only preferences stored per project (e.g., UI theme and editor viewport behavior). | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferences.Persistence.cs` | EditorPreferences: EditorPreferences Persistence | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferences.Secrets.cs` | EditorPreferences: Secret persistence partial for . Each secret-bearing preference is owned in three pieces: A public, plaintext UI-facing string property (declared in EditorPreferences.cs) that the editor surfaces and… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorPreferencesOverrides.cs` | EditorPreferencesOverrides; EditorThemeOverrides; EditorDebugOverrides: Project/sandbox-local overrides for editor preferences. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/EditorRuntimeEnvironmentPreferences.cs` | EditorRuntimeEnvironmentPreferences: Session-scoped editor view over every environment variable declared by XREngine. Values are not serialized: launch values remain authoritative until the user explicitly creates a temporary runtime… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/CameraUIDrawMode.cs` | CameraUIDrawMode: CameraUIDrawMode | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/LightProbeCaptureMode.cs` | LightProbeCaptureMode: LightProbeCaptureMode | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/LightProbeMode.cs` | LightProbeMode: LightProbeMode | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/ModelImportBackendPreference.cs` | ModelImportBackendPreference: ModelImportBackendPreference | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/ModelImportMaterialMode.cs` | ModelImportMaterialMode: ModelImportMaterialMode | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/ModelPostImportFlags.cs` | ModelPostImportFlags: ModelPostImportFlags | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestEditorType.cs` | UnitTestEditorType: UnitTestEditorType | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestFbxLogVerbosity.cs` | UnitTestFbxLogVerbosity: UnitTestFbxLogVerbosity | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestingVrLaunchMode.cs` | UnitTestingVrLaunchMode: UnitTestingVrLaunchMode | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestModelImportKind.cs` | UnitTestModelImportKind: UnitTestModelImportKind | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Enums/UnitTestWorldKind.cs` | UnitTestWorldKind: UnitTestWorldKind | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/GameStartupSettings.Clone.cs` | GameStartupSettings: Creates a detached runtime-session projection of this settings root. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/GameStartupSettings.cs` | GameStartupSettings; EMaxMirrorRecursionCount: Project-authored game configuration settings composed by the runtime bootstrap. Contains startup, networking, and build configuration. Also includes optional overrides for engine-level settings… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/GameStartupSettings.Persistence.cs` | GameStartupSettings: GameStartupSettings Persistence | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/GameWindowStartupSettings.cs` | GameWindowStartupSettings: Project-authored configuration for one runtime window. [MemoryPackable] public partial class GameWindowStartupSettings : XRBase { private EWindowState _windowState = EWindowState.Windowed; private… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.AtmosphericScatteringInitSettings.cs` | UnitTestingWorldSettings; AtmosphericScatteringInitSettings: UnitTestingWorldSettings AtmosphericScatteringInitSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.ColorRgb.cs` | UnitTestingWorldSettings; ColorRgb: UnitTestingWorldSettings ColorRgb | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.ModelImportSettings.cs` | UnitTestingWorldSettings; ModelImportSettings: Selects the generic model material factory. Unity prefabs use their source-aware material converter instead; recognized Poiyomi materials are always converted to XRENGINE's forward-plus Uber shader. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.ProbeGridCounts.cs` | UnitTestingWorldSettings; ProbeGridCounts: UnitTestingWorldSettings ProbeGridCounts | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.TranslationXYZ.cs` | UnitTestingWorldSettings; TranslationXYZ: UnitTestingWorldSettings TranslationXYZ | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.VolumetricFogVolumeInitSettings.cs` | UnitTestingWorldSettings; VolumetricFogVolumeInitSettings: UnitTestingWorldSettings VolumetricFogVolumeInitSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/Subclasses/UnitTestingWorldSettings.YawPitchRollDegrees.cs` | UnitTestingWorldSettings; YawPitchRollDegrees: UnitTestingWorldSettings YawPitchRollDegrees | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingOpenGLRenderSettings.cs` | UnitTestingOpenGLRenderSettings: UnitTestingOpenGLRenderSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingOpenGLShaderLinkingSettings.cs` | UnitTestingOpenGLShaderLinkingSettings: UnitTestingOpenGLShaderLinkingSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingOpenXrEyeResolutionSettings.cs` | UnitTestingOpenXrEyeResolutionSettings: UnitTestingOpenXrEyeResolutionSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingRenderPipeline.cs` | UnitTestingRenderPipeline: Selects the scene render pipeline created by the normal and unit-testing bootstrap cameras. The values are the supported RenderPipeline-derived class names serialized in the world settings. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingRenderSettings.cs` | UnitTestingRenderSettings: Scene pipeline created by bootstrap cameras. Explicit selections take precedence over automatic pipeline policy. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingVrFoveationSettings.cs` | UnitTestingVrFoveationSettings: UnitTestingVrFoveationSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingVrSettings.cs` | UnitTestingVrSettings: Requested VR eye rendering mode. OpenXR Vulkan SinglePassStereo strictly requires true layered multiview rendering; unavailable capabilities are logged and the XR output is not rendered. It never… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingVulkanRenderSettings.cs` | UnitTestingVulkanRenderSettings: UnitTestingVulkanRenderSettings | P | Portable Host |
| `XREngine.Runtime.Bootstrap/Settings/UnitTestingWorldSettings.cs` | UnitTestingWorldSettings: Startup model imports processed when the Unit Testing World boots. Each array item is a ModelImportSettings object with Enabled, Kind, MaterialMode, ImporterBackend, Path, optional UnityProjectRoot,… | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.Input.cs` | Engine; Input: Application-level input access composed by Bootstrap. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeAnimationHostServices.cs` | EngineRuntimeAnimationHostServices: Engine RuntimeAnimationHostServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeAudioIntegrationServices.cs` | EngineRuntimeAudioIntegrationServices: Engine RuntimeAudioIntegrationServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeGameModeHostServices.cs` | EngineRuntimeGameModeHostServices: Binds Runtime.Core game-mode policy to the Bootstrap-composed Core world, scene, and camera model. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeInputServices.cs` | EngineRuntimeInputServices: Engine RuntimeInputServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeNetworkingHostServices.cs` | EngineRuntimeNetworkingHostServices; EngineRuntimeNetworkWorldContext: Adapts Bootstrap-owned world hosts and controller composition to the lower Runtime.Core networking contract. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimePawnHostServices.cs` | EngineRuntimePawnHostServices; OptionalInputRegistration: Engine RuntimePawnHostServices | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimePlayerControllerServices.cs` | EngineRuntimePlayerControllerServices: Bootstrap-installed controller registry. Concrete local and remote controller ownership remains in Runtime.InputIntegration rather than the facade state. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrInputServices.cs` | EngineRuntimeVrInputServices; CallbackRegistration; BoolRegistration; FloatRegistration; Vector2Registration; Vector3Registration; PoseRegistration; SkeletonSummaryRegistration: Engine RuntimeVrInputServices | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrLifecycleServices.cs` | EngineRuntimeVrLifecycleServices: Bridges application-owned VR startup and transport behavior into Runtime.Rendering. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrStateServices.cs` | EngineRuntimeVrStateServices: Engine RuntimeVrStateServices | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/SubsystemHost/EngineVrLifecycle.cs` | EngineVrLifecycle; VRRuntime; VRMode: Application-owned VR lifecycle, transport, and render-callback orchestration. Process-wide VR state is owned by . | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/SubsystemHost/GameModeCompositionBootstrap.cs` | GameModeCompositionBootstrap: Registers scene-composition game modes without introducing higher-layer dependencies into Runtime.Core. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs` | RuntimeAdapterBootstrap; IRuntimeAdapterHostLease; HeadlessRuntimeAdapterHostLease; RuntimeAdapterHostLease: Installs the facade-backed capabilities used by optional runtime subsystem adapters. | R | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterProfile.cs` | RuntimeAdapterProfile: Selects the runtime-facing subsystem adapters installed by an application composition root. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/VRGameStartupSettings.cs` | VRGameStartupSettings: The name of the process to search for when running in client mode. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/SubsystemHost/XREngineVrRuntimeJsonContext.cs` | XREngineVrRuntimeJsonContext: XREngineVrRuntimeJsonContext | P | Portable Host |
| `XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs` | UnitTestingWorldSettingsStore; MonadoSimulatedDisplayProfile: Resolves the JSONC file used to configure the Unit Testing World. | D | Desktop Bootstrap |
| `XREngine.Runtime.Bootstrap/WorldHost/EngineRuntimeWorldHostServices.cs` | EngineRuntimeWorldHostServices: Installs Bootstrap's explicit world-host registry for Engine-facing Core requests. Registry lifetime is owned by the adapter lease and can be reset deterministically. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/WorldHost/HeadlessRuntimeWorldHost.cs` | HeadlessRuntimeWorldHost: HeadlessRuntimeWorldHost | R | Portable Host |
| `XREngine.Runtime.Bootstrap/WorldHost/HeadlessRuntimeWorldHostServices.cs` | HeadlessRuntimeWorldHostServices: HeadlessRuntimeWorldHostServices | R | Portable Host |
| `XREngine.Runtime.Bootstrap/WorldHost/IRuntimeWorldHostCompositionServices.cs` | IRuntimeWorldHostCompositionServices: Optional application-owned composition invoked after a Core world and its renderer exist, but before the target asset's scenes are loaded. | P | Portable Host |
| `XREngine.Runtime.Bootstrap/WorldHost/RuntimeWorldHost.cs` | RuntimeWorldHost: Bootstrap-owned composition root for one live world. This is deliberately a coordinator, not another world interface: nodes retain and windows retain . | P | Portable Host |
| `XREngine.Runtime.Bootstrap/WorldHost/RuntimeWorldHostCompositionServices.cs` | RuntimeWorldHostCompositionServices; InstallationLease: Explicit installation point for optional world-host composition such as editor-only scene policy. Runtime applications may leave it uninstalled. | P | Portable Host |

## Installed service table

The category applies to the implementing provider; a desktop provider can implement a shared interface. Installation locations are the pre-move baseline. Lower contracts retain their current assemblies.

| Slot | Implementing type | Category | Installation / owner |
| --- | --- | --- | --- |
| `RuntimeApplicationCapabilityServices.Current` | RuntimeApplicationProfile.ToCapabilities() -> RuntimeApplicationCapabilities | P | XREngine.Runtime.Bootstrap/RuntimeApplicationBootstrap.cs:64 |
| `RuntimeRenderingHostServices.Current` | EngineRuntimeRenderingHostServices | R | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:30-62 |
| `RuntimeRenderObjectServices.Current` | EngineRuntimeRenderObjectServices | P | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:50 |
| `RuntimeShaderServices.Current` | EngineRuntimeShaderServices | P | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:51 |
| `RuntimeCharacterMovementVisualizationServices.Current` | RenderingCharacterMovementVisualizationServices (XREngine.Runtime.Rendering/Runtime/RenderingCharacterMovementVisualizationServices.cs) | P | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:54 |
| `RuntimeWindowApplicationServices.Current` | EngineRuntimeWindowApplicationServices (Bootstrap) -> RuntimeWindowPumpHost (Runtime.Platform.Desktop) | D | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:55 |
| `RuntimeVrRenderingServices.Current` | EngineRuntimeVrRenderingServices | D | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:58 (profile AllowsVr) |
| `RuntimeVideoStreamingServices.Current` | EngineRuntimeVideoStreamingServices | P | XREngine.Runtime.Bootstrap/RenderingHost/RuntimeRenderingBootstrap.cs:60 (profile AllowsWindows) |
| `RuntimeAnimationHostServices.Current` | EngineRuntimeAnimationHostServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:76,169 |
| `RuntimeAudioIntegrationServices.Current` | EngineRuntimeAudioIntegrationServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:171 |
| `RuntimeInputServices.Current` | EngineRuntimeInputServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:174 |
| `RuntimeInputCaptureServices.Current` | RuntimeInputCaptureState | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:175 |
| `RuntimeVrInputServices.Current` | EngineRuntimeVrInputServices | D | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:176 (Input profile) |
| `RuntimeVrStateServices.Current` | EngineRuntimeVrStateServices | D | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:177 (Input profile) |
| `RuntimeEngine.VRState.LifecycleServices` | EngineRuntimeVrLifecycleServices | D | XREngine.Runtime.Bootstrap/SubsystemHost/Engine.RuntimeVrStateServices.cs:12 (constructor) |
| `RuntimeGameModeHostServices.Current` | EngineRuntimeGameModeHostServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:178 (Input profile) |
| `RuntimePawnHostServices.Current` | EngineRuntimePawnHostServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:179 (Input profile) |
| `RuntimePlayerControllerServices.Current` | EngineRuntimePlayerControllerServices or RemoteOnlyPlayerControllerServices (Runtime.InputIntegration) | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:77,182,187 |
| `RuntimeWorldHostServices.Current` | EngineRuntimeWorldHostServices or HeadlessRuntimeWorldHostServices | R | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:75,166 |
| `RuntimeWorldRegistryServices.Current` | CoreWorldRegistry exposed by EngineRuntimeWorldHostServices/HeadlessRuntimeWorldHostServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:74,157,164 |
| `RuntimeNetworkingHostServices.Current` | EngineRuntimeNetworkingHostServices | P | XREngine.Runtime.Bootstrap/SubsystemHost/RuntimeAdapterBootstrap.cs:79,190 |
| `RuntimeWorldObjectServices.Current` | EngineRuntimeWorldObjectServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:334 |
| `RuntimeThreadServices.Current` | EngineRuntimeThreadServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:335 |
| `RuntimeMaintenanceServices.Current` | EngineRuntimeMaintenanceServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:338 |
| `RuntimeNetworkDiscoveryHostServices.Current` | EngineRuntimeNetworkDiscoveryHostServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:339 |
| `RuntimeSceneNodeServices.Current` | EngineRuntimeSceneNodeServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:340 |
| `RuntimeSceneStreamingHostServices.Current` | EngineRuntimeSceneStreamingHostServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:341 |
| `RuntimeTransformServices.Current` | EngineRuntimeTransformServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:342 |
| `RuntimeTimingServices.Current` | EngineRuntimeTimingServices(EngineTimer) | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:370 via InstallRuntimeTimingServices |
| `RuntimePhysicsServices.Current` | EngineRuntimePhysicsServices + EngineConvexHullInputProvider | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:386 via InstallRuntimePhysicsServices |
| `RuntimeStaticColliderAuthoringServices.Current` | EngineRuntimeStaticColliderAuthoringServices | P | XREngine.Runtime.Bootstrap/Engine/Engine.cs:389 via InstallRuntimePhysicsServices |
| `RuntimeDebugHostServices.Current` | DefaultRuntimeDebugHostServices, wrapped by PerformanceProfileDebugHostServices | P | Default in Runtime.Core; temporary override in XREngine.Runtime.Bootstrap/Engine/Engine.ProfileCapture.cs:227-240 |
| `RuntimeModelImportServices / RuntimeModelSceneLoadingServices / RuntimeThirdPartyAssetLoadingServices` | ModelAssetPipelineRuntimeServices + ModelPrefabAssetLoadingServices + registered third-party handlers | D | XREngine.Runtime.ModelAssetPipeline/Importing/Caching/ModelAssetPipelineRegistration.cs:19-23; called by Editor, Server, VRClient app roots |
| `RuntimeWorldHostCompositionServices.Current` | EditorRuntimeWorldHostCompositionServices | D | XREngine.Editor/Program.cs:101 |
| `RuntimeSceneImportServices.Current` | EditorRuntimeSceneImportServices | D | XREngine.Editor/Program.cs:109 |
| `RuntimeOpenVrStateServices.Current and RuntimeOpenVrCompositorServices.Current` | OpenVrStateProvider.Instance + OpenVrCompositorBackend.Instance | D | XREngine.Runtime.XR.OpenVR/OpenVrRuntimeBackend.cs:91-92 (registered by Engine static composition) |
| `RuntimeClipboardServices.Current` | DesktopClipboardServices | D | XREngine.Runtime.Platform.Desktop/DesktopPlatformBackend.cs:29 (registered by Engine static composition) |

## Qualification and open decisions

The reference-harness build checks and live results are recorded in the [build stabilization record](unified-runtime-build-stabilization.md) and [harness investigation](../../investigations/rendering/desktop-browser-reference-harness.md). The extraction must pass the complete six-command build gate, whole-project browser compilation, and the desktop startup/world checks before its source items are marked complete. No regression tests were added during inventory.

D13 already approves application-root ModelAssetPipeline references and the desktop factory scan. Its provider remains outside the portable closure. The separate ModelingIntegration factory-input reply remains pending; the inventory preserves its current state. Browser frame stepping, nonblocking asset access, backend execution, physical-device qualification and performance measurements remain separate work.
