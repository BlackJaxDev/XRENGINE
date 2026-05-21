using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Host-owned services and settings consumed by the runtime rendering assembly.
/// </summary>
/// <remarks>
/// This is the narrow bridge from runtime rendering code back to the concrete engine/editor host. Prefer
/// capability-focused members here over direct <c>Engine</c> access in runtime rendering types.
/// </remarks>
public interface IRuntimeRenderingHostServices
{
    #region Profiling

    /// <summary>
    /// Starts a host profiler scope. Returning <see langword="null"/> is the expected no-op fast path.
    /// </summary>
    IDisposable? StartProfileScope([CallerMemberName] string? scopeName = null);

    #endregion

    #region Import and shader settings

    /// <summary>
    /// Gets whether shader pipeline objects may be used instead of monolithic shader programs.
    /// </summary>
    bool AllowShaderPipelines { get; }

    /// <summary>
    /// Gets whether exact transparency techniques such as depth peeling and per-pixel fragment storage are enabled.
    /// </summary>
    bool EnableExactTransparencyTechniques { get; }

    /// <summary>
    /// Gets whether mesh vertex attributes should be packed into an interleaved GPU buffer layout by default.
    /// </summary>
    bool UseInterleavedMeshBuffer { get; }

    /// <summary>
    /// Gets whether shader uniforms that represent integer data should use integer uniform APIs.
    /// </summary>
    bool UseIntegerUniformsInShaders { get; }

    /// <summary>
    /// Gets whether imported blendshape deltas should be remapped before runtime mesh data is populated.
    /// </summary>
    bool RemapBlendshapeDeltas { get; }

    /// <summary>
    /// Gets whether imported and runtime meshes should retain blendshape data.
    /// </summary>
    bool AllowBlendshapes { get; }

    /// <summary>
    /// Gets whether mesh vertex data population may use parallel CPU work.
    /// </summary>
    bool PopulateVertexDataInParallel { get; }

    /// <summary>
    /// Gets whether mesh import processing may be scheduled asynchronously.
    /// </summary>
    bool ProcessMeshImportsAsynchronously { get; }

    /// <summary>
    /// Gets whether imported and runtime meshes should retain skinning data.
    /// </summary>
    bool AllowSkinning { get; }

    /// <summary>
    /// Gets whether skinning data should always be reduced to four weights where possible.
    /// </summary>
    bool OptimizeSkinningTo4Weights { get; }

    /// <summary>
    /// Gets whether skinning data may be reduced to four weights when the mesh already fits that limit.
    /// </summary>
    bool OptimizeSkinningWeightsIfPossible { get; }

    /// <summary>
    /// Gets whether skeletal skinning should be evaluated by the compute pre-pass.
    /// </summary>
    bool CalculateSkinningInComputeShader { get; }

    /// <summary>
    /// Gets whether blendshapes should be evaluated by the compute pre-pass.
    /// </summary>
    bool CalculateBlendshapesInComputeShader { get; }

    /// <summary>
    /// Gets the host shader configuration revision used to invalidate runtime render programs.
    /// </summary>
    int ShaderConfigVersion { get; }

    /// <summary>
    /// Subscribes a callback to host rendering setting changes.
    /// </summary>
    void SubscribeRenderingSettingsChanged(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host rendering setting changes.
    /// </summary>
    void UnsubscribeRenderingSettingsChanged(Action callback);

    /// <summary>
    /// Subscribes a callback to host anti-aliasing setting changes.
    /// </summary>
    void SubscribeAntiAliasingSettingsChanged(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host anti-aliasing setting changes.
    /// </summary>
    void UnsubscribeAntiAliasingSettingsChanged(Action callback);

    #endregion

    #region Frame and render state

    /// <summary>
    /// Gets whether the current caller is executing on the host render thread.
    /// </summary>
    bool IsRenderThread { get; }

    /// <summary>
    /// Gets whether a backend renderer is currently active and able to accept GPU work.
    /// </summary>
    bool IsRendererActive { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside a shadow-map pass.
    /// </summary>
    bool IsShadowPass { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside a stereo render pass.
    /// </summary>
    bool IsStereoPass { get; }

    /// <summary>
    /// Gets whether runtime rendering is currently inside an off-screen scene capture pass.
    /// </summary>
    bool IsSceneCapturePass { get; }

    /// <summary>
    /// Gets whether debug culling volumes should be rendered for visible render-info objects.
    /// </summary>
    bool RenderCullingVolumesEnabled { get; }

    /// <summary>
    /// Gets whether the active renderer or GPU state is known to be NVIDIA-specific.
    /// </summary>
    bool IsNvidia { get; }

    /// <summary>
    /// Gets the luminance weighting vector used by post-processing and texture luminance calculations.
    /// </summary>
    Vector3 DefaultLuminance { get; }

    /// <summary>
    /// Gets host elapsed time in stopwatch ticks.
    /// </summary>
    long ElapsedTicks { get; }

    /// <summary>
    /// Gets host elapsed time in seconds.
    /// </summary>
    float ElapsedTime { get; }

    /// <summary>
    /// Gets the most recent update-frame delta in seconds.
    /// </summary>
    double UpdateDeltaSeconds { get; }

    /// <summary>
    /// Gets the host timestamp for the most recent update frame.
    /// </summary>
    long LastUpdateTimestampTicks { get; }

    /// <summary>
    /// Gets the most recent render-frame delta in seconds.
    /// </summary>
    double RenderDeltaSeconds { get; }

    /// <summary>
    /// Gets the host timestamp for the most recent render frame.
    /// </summary>
    long LastRenderTimestampTicks { get; }

    /// <summary>
    /// Gets the renderer's currently tracked VRAM allocation total in bytes.
    /// </summary>
    long TrackedVramBytes { get; }

    /// <summary>
    /// Gets the renderer's current VRAM allocation budget in bytes.
    /// </summary>
    long TrackedVramBudgetBytes { get; }

    /// <summary>
    /// Gets whether GPU indirect rendering debug logging should be emitted.
    /// </summary>
    bool EnableGpuIndirectDebugLogging { get; }

    /// <summary>
    /// Gets the active occlusion culling mode resolved by the host.
    /// </summary>
    EOcclusionCullingMode GpuOcclusionCullingMode { get; }

    /// <summary>
    /// Gets the frame period for periodically retesting meshes culled by CPU hardware queries.
    /// </summary>
    int CpuQueryOcclusionRetestPeriodFrames { get; }

    /// <summary>
    /// Gets whether the legacy CPU software occlusion culling side toggle is enabled.
    /// </summary>
    bool EnableCpuSoftwareOcclusionCulling { get; }

    /// <summary>
    /// Gets the CPU software occlusion buffer width in pixels.
    /// </summary>
    int CpuSocBufferWidth { get; }

    /// <summary>
    /// Gets the CPU software occlusion buffer height in pixels.
    /// </summary>
    int CpuSocBufferHeight { get; }

    /// <summary>
    /// Gets the triangle budget for CPU software occlusion rasterization.
    /// </summary>
    int CpuSocOccluderTriangleBudget { get; }

    /// <summary>
    /// Gets the maximum number of CPU software occlusion occluders.
    /// </summary>
    int CpuSocMaxOccluders { get; }

    /// <summary>
    /// Gets the minimum screen-area fraction for CPU software occlusion occluders.
    /// </summary>
    float CpuSocMinOccluderScreenArea { get; }

    /// <summary>
    /// Gets whether AVX2 acceleration is allowed for CPU software occlusion.
    /// </summary>
    bool CpuSocUseAvx2 { get; }

    /// <summary>
    /// Gets whether CPU software occlusion debug visualization is enabled.
    /// </summary>
    bool CpuSocDebugVisualization { get; }

    /// <summary>
    /// Gets whether CPU software occlusion should force every test visible for diagnostics.
    /// </summary>
    bool CpuSocDebugForceVisible { get; }

    /// <summary>
    /// Gets the CPU spatial structure used for render visibility when GPU dispatch is disabled.
    /// </summary>
    ECpuSceneCullingStructure CpuSceneCullingStructure { get; }

    /// <summary>
    /// Gets the host preference for splitting a window between two local players.
    /// </summary>
    ETwoPlayerPreference TwoPlayerViewportPreference { get; }

    /// <summary>
    /// Gets the host preference for splitting a window between three local players.
    /// </summary>
    EThreePlayerPreference ThreePlayerViewportPreference { get; }

    /// <summary>
    /// Gets the graphics API used by the current or primary renderer.
    /// </summary>
    RuntimeGraphicsApiKind CurrentRenderBackend { get; }

    /// <summary>
    /// Gets the render-command state currently active on the host, when any command pass is executing.
    /// </summary>
    IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState { get; }

    /// <summary>
    /// Gets the render pipeline context currently active on the host, when any pipeline frame is executing.
    /// </summary>
    IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext { get; }

    /// <summary>
    /// Gets whether the host is entering or leaving play mode and rendering should avoid transient state changes.
    /// </summary>
    bool IsPlayModeTransitioning { get; }

    /// <summary>
    /// Gets the host play-mode state name for diagnostics and frame labels.
    /// </summary>
    string PlayModeStateName { get; }

    /// <summary>
    /// Gets the default anti-aliasing mode used when no camera or pipeline override is active.
    /// </summary>
    EAntiAliasingMode DefaultAntiAliasingMode { get; }

    /// <summary>
    /// Gets the default MSAA sample count used when no camera or pipeline override is active.
    /// </summary>
    uint DefaultMsaaSampleCount { get; }

    /// <summary>
    /// Gets whether render outputs should default to HDR when no camera or pipeline override is active.
    /// </summary>
    bool DefaultOutputHDR { get; }

    /// <summary>
    /// Gets the default temporal super-resolution render scale used when no camera override is active.
    /// </summary>
    float DefaultTsrRenderScale { get; }

    /// <summary>
    /// Gets whether the forward renderer should emit a depth pre-pass by default.
    /// </summary>
    bool ForwardDepthPrePassEnabled { get; }

    /// <summary>
    /// Gets whether the forward pre-pass should share G-buffer targets with later passes.
    /// </summary>
    bool ForwardPrePassSharesGBufferTargets { get; }

    /// <summary>
    /// Gets whether the host has per-frame render statistics tracking enabled.
    /// When false, render-stats sampling and the GPU pipeline profiler are skipped.
    /// </summary>
    bool EnableRenderStatisticsTracking { get; }

    /// <summary>
    /// Gets whether the host has GPU render-pipeline command timing enabled.
    /// When false, the render-pipeline GPU profiler short-circuits scope creation.
    /// </summary>
    bool EnableGpuRenderPipelineProfiling { get; }

    /// <summary>
    /// Gets the host's current render frame id. Used by runtime rendering code that
    /// needs to correlate work to the same frame counter the host increments per frame.
    /// </summary>
    ulong CurrentRenderFrameId { get; }

    #endregion

    #region Shadow settings

    /// <summary>
    /// Gets whether the host supplies authoritative shadow atlas settings.
    /// </summary>
    bool ProvidesShadowAtlasSettings { get; }

    /// <summary>
    /// Gets whether spot lights should render into the shared shadow atlas when possible.
    /// </summary>
    bool UseSpotShadowAtlas { get; }

    /// <summary>
    /// Gets whether directional lights should render into the shared shadow atlas when possible.
    /// </summary>
    bool UseDirectionalShadowAtlas { get; }

    /// <summary>
    /// Gets whether point lights should render face tiles into the shared shadow atlas when possible.
    /// </summary>
    bool UsePointShadowAtlas { get; }

    /// <summary>
    /// Gets the square page size in pixels for shadow atlas pages.
    /// </summary>
    uint ShadowAtlasPageSize { get; }

    /// <summary>
    /// Gets the maximum number of shadow atlas pages that may be allocated.
    /// </summary>
    int MaxShadowAtlasPages { get; }

    /// <summary>
    /// Gets the maximum shadow atlas memory budget in bytes.
    /// </summary>
    long MaxShadowAtlasMemoryBytes { get; }

    /// <summary>
    /// Gets the maximum number of shadow atlas tiles that may render in a single frame.
    /// </summary>
    int MaxShadowTilesRenderedPerFrame { get; }

    /// <summary>
    /// Gets the maximum target CPU-side shadow rendering budget in milliseconds.
    /// </summary>
    float MaxShadowRenderMilliseconds { get; }

    /// <summary>
    /// Gets the minimum tile resolution allowed in the shadow atlas.
    /// </summary>
    uint MinShadowAtlasTileResolution { get; }

    /// <summary>
    /// Gets the maximum tile resolution allowed in the shadow atlas.
    /// </summary>
    uint MaxShadowAtlasTileResolution { get; }

    #endregion

    #region Viewports, cameras, and players

    /// <summary>
    /// Resolves the graphics API backend for a specific host window.
    /// </summary>
    RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window);

    /// <summary>
    /// Enumerates viewports that are currently active in host windows or XR presentation.
    /// </summary>
    IEnumerable<IRuntimeViewportHost> EnumerateActiveViewports();

    /// <summary>
    /// Enumerates local player controllers known to the host.
    /// </summary>
    IEnumerable<IPawnController> EnumerateLocalPlayers();

    /// <summary>
    /// Resolves the default depth mode used when a scene camera is initialized by runtime rendering code.
    /// </summary>
    XRCamera.EDepthMode ResolveSceneCameraDepthModePreference();

    /// <summary>
    /// Ensures a controllable pawn exists for the supplied camera and local player.
    /// </summary>
    IRuntimeInputControllablePawn? EnsurePawnForCamera(SceneNode sceneNode, CameraComponent camera, ELocalPlayerIndex playerIndex, Type? pawnType = null);

    /// <summary>
    /// Schedules a viewport-space physics pick and invokes the callback with sorted hit results.
    /// </summary>
    void PickViewportPhysicsAsync(
        XRViewport viewport,
        CameraComponent camera,
        Vector2 normalizedViewportPosition,
        LayerMask layerMask,
        object? filter,
        SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
        Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
        bool useUnjitteredProjection);

    #endregion

    #region Pipeline context and diagnostics

    /// <summary>
    /// Pushes a pipeline context as current on the host and returns a scope that restores the previous context.
    /// </summary>
    IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline);

    /// <summary>
    /// Writes an informational message through the host output system.
    /// </summary>
    void LogOutput(string message);

    /// <summary>
    /// Writes a warning message through the host output system.
    /// </summary>
    void LogWarning(string message);

    /// <summary>
    /// Writes an exception through the host diagnostic system with optional contextual text.
    /// </summary>
    void LogException(Exception ex, string? context = null);

    /// <summary>
    /// Records a missing asset diagnostic for tooling, logs, or editor reports.
    /// </summary>
    void RecordMissingAsset(string assetPath, string category, string? context = null);

    #endregion

    #region Asset and texture IO

    /// <summary>
    /// Gets the host asset-file extension without a leading period.
    /// </summary>
    string AssetFileExtension { get; }

    /// <summary>
    /// Gets the texture path used when a requested texture cannot be loaded.
    /// </summary>
    string? TextureFallbackPath { get; }

    /// <summary>
    /// Gets the material used when a requested material is invalid or unavailable.
    /// </summary>
    XRMaterial? InvalidMaterial { get; }

    /// <summary>
    /// Gets the texture diagnostics logging mode.
    /// </summary>
    TextureRuntimeLogMode TextureLogMode { get; }

    /// <summary>
    /// Gets the CPU decode or resize duration threshold, in milliseconds, that should be logged as slow.
    /// </summary>
    double TextureSlowCpuDecodeResizeMilliseconds { get; }

    /// <summary>
    /// Gets the mip-build duration threshold, in milliseconds, that should be logged as slow.
    /// </summary>
    double TextureSlowMipBuildMilliseconds { get; }

    /// <summary>
    /// Gets the texture upload chunk duration threshold, in milliseconds, that should be logged as slow.
    /// </summary>
    double TextureSlowUploadChunkMilliseconds { get; }

    /// <summary>
    /// Gets the texture lifecycle transition duration threshold, in milliseconds, that should be logged as slow.
    /// </summary>
    double TextureSlowTransitionMilliseconds { get; }

    /// <summary>
    /// Gets the render-thread queue wait threshold, in milliseconds, that should be logged as slow.
    /// </summary>
    double TextureSlowQueueWaitMilliseconds { get; }

    /// <summary>
    /// Gets the per-frame texture upload work budget in milliseconds.
    /// </summary>
    double TextureUploadFrameBudgetMilliseconds { get; }

    /// <summary>
    /// Reads all bytes for a file path using the host file IO path, including DirectStorage where available.
    /// </summary>
    byte[] ReadAllBytes(string filePath);

    /// <summary>
    /// Resolves the authoritative path used to key texture streaming state and cooked caches.
    /// </summary>
    string ResolveTextureStreamingAuthorityPath(string filePath);

    /// <summary>
    /// Reports sparse texture streaming support for the supplied internal texture format.
    /// </summary>
    SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format);

    /// <summary>
    /// Attempts to schedule an asynchronous sparse texture streaming transition on the render backend.
    /// </summary>
    bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null);

    /// <summary>
    /// Finalizes a sparse texture streaming transition after backend work completes.
    /// </summary>
    SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult);

    /// <summary>
    /// Schedules an enumerator-based background job through the host job system.
    /// </summary>
    EnumeratorJob ScheduleEnumeratorJob(
        Func<IEnumerable> routineFactory,
        JobPriority priority = JobPriority.Normal,
        Action? completed = null,
        Action<Exception>? error = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Scheduling and frame callbacks

    /// <summary>
    /// Subscribes a callback to the host viewport swap-buffers frame event.
    /// </summary>
    void SubscribeViewportSwapBuffers(Action swapBuffers);

    /// <summary>
    /// Unsubscribes a callback from the host viewport swap-buffers frame event.
    /// </summary>
    void UnsubscribeViewportSwapBuffers(Action swapBuffers);

    /// <summary>
    /// Subscribes a callback to the host viewport collect-visible frame event.
    /// </summary>
    void SubscribeViewportCollectVisible(Action collectVisible);

    /// <summary>
    /// Unsubscribes a callback from the host viewport collect-visible frame event.
    /// </summary>
    void UnsubscribeViewportCollectVisible(Action collectVisible);

    /// <summary>
    /// Subscribes paired window swap and render callbacks to the host frame timer.
    /// </summary>
    void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);

    /// <summary>
    /// Unsubscribes paired window swap and render callbacks from the host frame timer.
    /// </summary>
    void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);

    /// <summary>
    /// Subscribes a callback to host play-mode transition notifications that affect rendering.
    /// </summary>
    void SubscribePlayModeTransitions(Action callback);

    /// <summary>
    /// Unsubscribes a callback from host play-mode transition notifications.
    /// </summary>
    void UnsubscribePlayModeTransitions(Action callback);

    /// <summary>
    /// Queues work for execution on the host render thread.
    /// </summary>
    void EnqueueRenderThreadTask(Action task);

    /// <summary>
    /// Queues named work for execution on the host render thread.
    /// </summary>
    void EnqueueRenderThreadTask(Action task, string reason);

    /// <summary>
    /// Queues work for execution on the host application/update thread. Use this for
    /// non-GPU work (scene/editor/networking) so it does not stall the render thread.
    /// </summary>
    void EnqueueAppThreadTask(Action task);

    /// <summary>
    /// Queues named work for execution on the host application/update thread.
    /// </summary>
    void EnqueueAppThreadTask(Action task, string reason);

    /// <summary>
    /// Queues a render-thread coroutine that returns <see langword="true"/> when it should continue.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task);

    /// <summary>
    /// Queues a named render-thread coroutine that returns <see langword="true"/> when it should continue.
    /// </summary>
    void EnqueueRenderThreadCoroutine(Func<bool> task, string reason);

    /// <summary>
    /// Processes pending render-thread tasks through the host task pump.
    /// </summary>
    void ProcessRenderThreadTasks();

    #endregion

    #region Debug drawing and scene maintenance

    /// <summary>
    /// Pushes the currently rendered transform identifier for diagnostics and debug visualization.
    /// </summary>
    IDisposable? PushTransformId(uint transformId);

    /// <summary>
    /// Records that an octree move was skipped during runtime visibility maintenance.
    /// </summary>
    void RecordOctreeSkippedMove();

    /// <summary>
    /// Processes pending GPU physics chain dispatch requests before scene rendering.
    /// </summary>
    void ProcessGpuPhysicsChainDispatches();

    /// <summary>
    /// Processes completed GPU physics chain readbacks or completion callbacks after scene work.
    /// </summary>
    void ProcessGpuPhysicsChainCompletions();

    /// <summary>
    /// Queues or renders a two-dimensional debug rectangle.
    /// </summary>
    void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug line.
    /// </summary>
    void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug sphere.
    /// </summary>
    void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug cone.
    /// </summary>
    void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an axis-aligned debug bounding box.
    /// </summary>
    void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an oriented debug bounding box.
    /// </summary>
    void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an oriented debug quad.
    /// </summary>
    void RenderDebugQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug point.
    /// </summary>
    void RenderDebugPoint(Vector3 position, ColorF4 color);

    /// <summary>
    /// Queues or renders world-space debug text.
    /// </summary>
    void RenderDebugText(Vector3 position, string text, ColorF4 color);

    /// <summary>
    /// Flushes any queued debug shapes through the host debug renderer.
    /// </summary>
    void RenderDebugShapes();

    #endregion

    #region Factories and window presentation

    /// <summary>
    /// Loads an engine asset through the host asset manager.
    /// </summary>
    TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new();

    /// <summary>
    /// Creates the host's default render pipeline implementation.
    /// </summary>
    IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline();

    /// <summary>
    /// Creates a backend renderer for the supplied host window and graphics API.
    /// </summary>
    IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind);

    /// <summary>
    /// Creates the adapter used to present a window render target inside an editor scene panel.
    /// </summary>
    IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter();

    /// <summary>
    /// Gets the scene panel subregion that should receive window rendering, when panel mode is active.
    /// </summary>
    BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Returns whether the host allows the supplied render window to close.
    /// </summary>
    bool AllowWindowClose(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Removes the supplied render window from the host window collection.
    /// </summary>
    void RemoveWindow(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Replicates a window target-world change through the host networking layer, when appropriate.
    /// </summary>
    void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window);

    /// <summary>
    /// Begins per-frame render statistics tracking for the host.
    /// </summary>
    void BeginRenderStatsFrame();

    /// <summary>
    /// Adds submitted draw calls to the host render-stat counters.
    /// </summary>
    void IncrementRenderDrawCalls(int count);

    /// <summary>
    /// Adds submitted multi-draw calls to the host render-stat counters.
    /// </summary>
    void IncrementRenderMultiDrawCalls(int count);

    /// <summary>
    /// Adds rendered triangles to the host render-stat counters.
    /// </summary>
    void AddRenderTrianglesRendered(int count);

    void AddRenderGpuBufferAllocation(long bytes);
    void RemoveRenderGpuBufferAllocation(long bytes);
    void AddRenderGpuTextureAllocation(long bytes);
    void RemoveRenderGpuTextureAllocation(long bytes);
    void AddRenderGpuRenderBufferAllocation(long bytes);
    void RemoveRenderGpuRenderBufferAllocation(long bytes);
    bool CanAllocateRenderVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes);
    void RecordRenderGpuBufferMapped(int count = 1);
    void RecordRenderGpuReadbackBytes(long bytes);
    void RecordRenderGpuCpuFallback(int eventCount, int recoveredCommands);
    void RecordRenderForbiddenGpuFallback(int eventCount = 1);
    void RecordRenderGpuTransparencyDomainCounts(uint opaqueOrOtherVisible, uint maskedVisible, uint approximateVisible, uint exactVisible);
    void RecordRenderGpuMeshletStrategyRequested(int eventCount = 1);
    void RecordRenderGpuMeshletProductionFrame(int eventCount = 1);
    void RecordRenderGpuMeshletFallback(int eventCount = 1);
    void RecordRenderGpuMeshletDispatchSkipped(int eventCount = 1);
    void RecordRenderGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled);
    void RecordRenderGpuMeshletExpansionOverflow(uint overflowCount);
    void RecordRenderGpuMeshletBufferBytesResident(long bytes);
    void RecordRenderGpuMeshletInstrumentation(uint visibleMeshletCount, uint dispatchedMeshletCount, uint taskRecordOverflowCount, TimeSpan dispatchTime, uint readbackBytes);
    void RecordRenderGpuMeshletCacheHit(int eventCount = 1);
    void RecordRenderGpuMeshletCacheMiss(int eventCount = 1);
    void RecordRenderGpuMeshletCacheStale(int eventCount = 1);
    void RecordRenderOctreeCollect(int visibleRenderables, int emittedCommands);
    void RecordRenderCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks);
    void RecordRenderRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime);
    void RecordRenderRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime);
    void RecordRenderSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded);
    void RecordRenderSkinnedBoundsRefreshDeferredScheduled();
    void RecordRenderSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks);
    void RecordRenderVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime);
    void RecordRenderVrPerViewVisibleCounts(uint leftVisible, uint rightVisible);
    void RecordRenderVrRenderSubmitTime(TimeSpan submitTime);
    void RecordRenderVrXrWaitFrameBlockTime(TimeSpan waitTime);
    void RecordRenderVrXrEndFrameSubmitTime(TimeSpan submitTime);
    void RecordRenderVrXrPredictedToLatePoseDelta(double millimeters, double degrees);
    void RecordRenderVrXrPredictedDisplayLeadTime(double leadTimeMs);
    void RecordRenderVrXrMissedDeadlineFrame();
    void RecordRenderVrXrTrackingLossFrame();
    void RecordRenderVrXrRelocatePredictedTime(TimeSpan elapsed);
    void RecordRenderVrXrCollectFrustumExpansionDegrees(double degrees);
    void RecordRenderVrXrPacingThreadIdleTime(TimeSpan elapsed);
    void RecordRenderVrXrPacingHandoffStall();
    void RecordRenderVulkanAdhocBarrier(int emittedCount, int redundantCount);
    void RecordRenderVulkanAllocation(int allocationClass, long bytes);
    void RecordRenderVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes);
    void RecordRenderVulkanBindChurn(
        int pipelineBinds = 0,
        int descriptorBinds = 0,
        int pushConstantWrites = 0,
        int vertexBufferBinds = 0,
        int indexBufferBinds = 0,
        int pipelineBindSkips = 0,
        int descriptorBindSkips = 0,
        int vertexBufferBindSkips = 0,
        int indexBufferBindSkips = 0);
    void RecordRenderVulkanDescriptorBindingFailure(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        bool skippedDraw,
        bool skippedDispatch,
        string? message);
    void RecordRenderVulkanDescriptorFallback(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        int count = 1);
    void RecordRenderVulkanDescriptorPoolCreate();
    void RecordRenderVulkanDescriptorPoolDestroy();
    void RecordRenderVulkanDescriptorPoolReset();
    void RecordRenderVulkanDynamicUniformAllocation(long bytes);
    void RecordRenderVulkanDynamicUniformExhaustion();
    void RecordRenderVulkanFrameDiagnostics(
        int droppedFrameOps,
        int droppedDrawOps,
        int droppedComputeOps,
        int sceneSwapchainWriters,
        int overlaySwapchainWriters,
        int forcedDiagnosticSwapchainWriters,
        int fboOnlyDrawOps,
        int fboOnlyBlitOps,
        bool missingSceneSwapchainWriters,
        string? firstFailedOpType,
        int firstFailedPassIndex,
        int firstFailedPipelineIdentity,
        int firstFailedViewportIdentity,
        string? firstFailedTargetName,
        string? firstFailedMaterialName,
        string? firstFailedShaderName,
        string? firstFailedMessage,
        string? diagnosticSummary);
    void RecordRenderVulkanFrameGpuCommandBufferTime(TimeSpan elapsed);
    void RecordRenderVulkanFrameLifecycleTiming(
        TimeSpan waitFence,
        TimeSpan acquireImage,
        TimeSpan recordCommandBuffer,
        TimeSpan submit,
        TimeSpan trim,
        TimeSpan present,
        TimeSpan total);
    void RecordRenderVulkanGpuDrivenStageTiming(int stage, TimeSpan elapsed);
    void RecordRenderVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount);
    void RecordRenderVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u);
    void RecordRenderVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount);
    void RecordRenderVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws);
    void RecordRenderVulkanOomFallback();
    void RecordRenderVulkanPipelineCacheLookup(bool cacheHit);
    void RecordRenderVulkanPipelineCacheMiss(string? summary);
    void RecordRenderVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode);
    void RecordRenderVulkanQueueSubmit();
    void RecordRenderVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount);
    void RecordRenderVulkanValidationMessage(bool isError, string? message);

    /// <summary>
    /// Gets whether editor scene panel presentation is enabled for render windows.
    /// </summary>
    bool IsWindowScenePanelPresentationEnabled { get; }

    /// <summary>
    /// Gets the scene panel resize debounce interval in milliseconds.
    /// </summary>
    int ScenePanelResizeDebounceMs { get; }

    /// <summary>
    /// Gets whether windows should ignore panel regions and render to the full viewport.
    /// </summary>
    bool ForceFullViewport { get; }

    #endregion

    #region VR and desktop mirror

    /// <summary>
    /// Gets whether desktop render windows should continue rendering while XR presentation is active.
    /// </summary>
    bool RenderWindowsWhileInVR { get; }

    /// <summary>
    /// Gets whether VR rendering should configure a foveated multi-view view set.
    /// </summary>
    bool EnableVrFoveatedViewSet { get; }

    /// <summary>
    /// Gets whether the host is currently in VR mode.
    /// </summary>
    bool IsInVR { get; }

    /// <summary>
    /// Gets whether OpenXR is the active XR runtime path.
    /// </summary>
    bool IsOpenXRActive { get; }

    /// <summary>
    /// Gets whether the desktop mirror should compose from eye textures instead of rendering desktop viewports directly.
    /// </summary>
    bool VrMirrorComposeFromEyeTextures { get; }

    /// <summary>
    /// Gets the normalized UV center of the VR foveation region.
    /// </summary>
    Vector2 VrFoveationCenterUv { get; }

    /// <summary>
    /// Gets the normalized inner radius of the VR foveation full-rate region.
    /// </summary>
    float VrFoveationInnerRadius { get; }

    /// <summary>
    /// Gets the normalized outer radius of the VR foveation falloff region.
    /// </summary>
    float VrFoveationOuterRadius { get; }

    /// <summary>
    /// Gets the shading rates used for inner, middle, and outer VR foveation regions.
    /// </summary>
    Vector3 VrFoveationShadingRates { get; }

    /// <summary>
    /// Gets the visibility margin applied around the VR foveation region.
    /// </summary>
    float VrFoveationVisibilityMargin { get; }

    /// <summary>
    /// Gets whether UI and near-field geometry should force full-resolution foveated rendering.
    /// </summary>
    bool VrFoveationForceFullResForUiAndNearField { get; }

    /// <summary>
    /// Gets the near-field distance threshold, in meters, used for full-resolution foveated rendering.
    /// </summary>
    float VrFoveationFullResNearDistanceMeters { get; }

    bool OpenXrCullWithFrustum { get; }
    bool OpenXrDebugGl { get; }
    bool OpenXrDebugClearOnly { get; }
    bool OpenXrDebugLifecycle { get; }
    bool OpenXrDebugRenderRightThenLeft { get; }
    bool OpenXrPrepareFrameAfterDesktopRender { get; }
    float OpenXrDeadlineSafetyMarginMs { get; }
    OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy { get; }
    float OpenXrCollectVisibleFrustumPaddingDegrees { get; }
    OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy { get; }
    OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy { get; }
    OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode { get; }

    /// <summary>
    /// Attempts to render the host desktop mirror composition into the current target size.
    /// </summary>
    void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight);

    /// <summary>
    /// Records left-eye and right-eye draw counts for VR diagnostics.
    /// </summary>
    void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws);

    #endregion

    #region Backend utilities

    /// <summary>
    /// Destroys host-owned API render objects associated with a renderer.
    /// </summary>
    void DestroyObjectsForRenderer(IRuntimeRendererHost renderer);

    /// <summary>
    /// Gets whether the supplied viewport is currently present on the host rendering viewport stack.
    /// </summary>
    bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport);

    /// <summary>
    /// Gets whether the host requests a debug opaque pipeline override.
    /// </summary>
    bool ShouldForceDebugOpaquePipeline { get; }

    /// <summary>
    /// Creates a debug opaque pipeline override for diagnosing transparency or material binding issues.
    /// </summary>
    IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride();

    /// <summary>
    /// Prepares host-owned upscaling or interop resources for the supplied viewport and pipeline frame.
    /// </summary>
    void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline);

    /// <summary>
    /// Applies host-level material shader binding configuration before a material program is used.
    /// </summary>
    void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program);

    /// <summary>
    /// Gets the byte size of one pixel for a sized internal texture format.
    /// </summary>
    int GetBytesPerPixel(ESizedInternalFormat format);

    /// <summary>
    /// Gets the byte size of one pixel for a renderbuffer storage format.
    /// </summary>
    int GetBytesPerPixel(ERenderBufferStorage storage);

    /// <summary>
    /// Adds framebuffer bandwidth usage, in bytes, to host render statistics.
    /// </summary>
    void AddFrameBufferBandwidth(long totalBytes);

    /// <summary>
    /// Dispatches a compute program through the active host renderer.
    /// </summary>
    void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ);

    /// <summary>
    /// Attempts to blit between two framebuffers through the active host renderer.
    /// </summary>
    bool TryBlitFrameBufferToFrameBuffer(
        XRFrameBuffer sourceFrameBuffer,
        XRFrameBuffer destinationFrameBuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);

    /// <summary>
    /// Attempts to blit from a viewport grab source to a framebuffer through the active host renderer.
    /// </summary>
    bool TryBlitViewportToFrameBuffer(
        IRuntimeViewportGrabSource viewport,
        XRFrameBuffer framebuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);

    #endregion
}
