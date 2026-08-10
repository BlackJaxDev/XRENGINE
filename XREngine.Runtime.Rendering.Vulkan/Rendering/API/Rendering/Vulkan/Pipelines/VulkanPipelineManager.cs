using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns renderer-wide program-link coordination and shared pipeline caches for
/// one Vulkan logical-device lifetime.
/// </summary>
internal sealed unsafe partial class VulkanPipelineManager
{
    private const int MaxCachedPipelineVariantManifests = 64;
    internal const uint CommonPushConstantByteSize = 16;
    internal const ShaderStageFlags CommonPushConstantStages =
        ShaderStageFlags.VertexBit |
        ShaderStageFlags.TessellationControlBit |
        ShaderStageFlags.TessellationEvaluationBit |
        ShaderStageFlags.GeometryBit |
        ShaderStageFlags.FragmentBit |
        ShaderStageFlags.ComputeBit;
    private Vk? _api;
    private VulkanDeviceContext? _deviceContext;
    private VulkanProgramCreationPort? _programServices;
    internal readonly ConcurrentDictionary<VulkanGraphicsPipelineCompileKey, VulkanGraphicsPipelineCompileJob> _vulkanGraphicsPipelineCompileJobs = new();
    internal readonly Dictionary<ulong, VulkanGraphicsPipelineCompileKey> _vulkanGraphicsPipelineProgramCompileJobs = new();
    internal readonly Lock _vulkanGraphicsPipelineCompileJobsLock = new();
    internal readonly object _vulkanPipelineCompileDependencyMutationLock = new();
    internal readonly Lock _vulkanPipelineCompileGateLock = new();
    internal SemaphoreSlim? _vulkanPipelineCompileGate;
    internal int _vulkanPipelineCompileWorkerCount;
    internal int _vulkanPipelineCompileQueueAnnounced;
    internal int _vulkanPipelineCompileShutdownStarted;
    internal int _vulkanPipelineCompileDependencyMutationActive;
    internal int _vulkanPipelineCompileDependencyMutationDepth;
    internal long _vulkanPipelineCompileDependencyGeneration;
    internal long _vulkanPipelineCompileActivityGeneration;
    internal PipelineCache _pipelineCache;
    internal PipelineCache _backgroundPipelineCache;
    internal string? _pipelineCacheFilePath;
    internal int _pipelineCacheCreatesSinceSave;
    internal int _pipelineCacheInitialDataBytes;
    internal bool _supportsPipelineCreationCacheControl;
    internal long _pipelineCacheLastAutoSaveAttemptTick;
    internal int _pipelineCacheAutoSaveInFlight;
    internal long _pipelineCacheSaveGeneration;
    internal readonly object _pipelineCacheFileWriteLock = new();
    internal readonly Lock _pipelineCacheHostAccessLock = new();
    internal readonly Lock _backgroundPipelineCacheHostAccessLock = new();
    internal readonly Dictionary<VulkanPipelineManifestCacheKey, VulkanPipelineVariantManifest> _pipelineVariantManifestCache = new();
    internal readonly Queue<VulkanPipelineManifestCacheKey> _pipelineVariantManifestInsertionOrder = new();
    internal readonly Lock _pipelineVariantManifestCacheLock = new();
    private readonly object _pendingDeviceReadyProgramLinksLock = new();
    private readonly HashSet<VkRenderProgram> _pendingDeviceReadyProgramLinks = [];
    private readonly object _sharedGraphicsPipelineLock = new();
    private readonly Dictionary<VulkanGraphicsPipelineKey, Pipeline> _sharedGraphicsPipelines = [];
    private readonly ConcurrentQueue<Pipeline> _supersededSharedGraphicsPipelines = new();
    private readonly object _sharedGraphicsPipelineLibraryLock = new();
    private readonly Dictionary<VulkanGraphicsPipelineLibraryKey, Pipeline>
        _sharedGraphicsPipelineLibraries = [];
    private readonly HashSet<VulkanGraphicsPipelineLibraryKey>
        _sharedGraphicsPipelineLibraryCreations = [];
    private ulong _sharedGraphicsPipelineGeneration;
    private VulkanPipelinePrewarmDatabase? _prewarmDatabase;
    private string? _prewarmDatabaseFilePath;
    private bool _prewarmCaptureEnabled;
    private int _prewarmNewEntriesSinceSave;
    private int _prewarmAutoSaveInFlight;
    private const int PipelinePrewarmAutoSaveEntryThreshold = 16;
    private const string PipelinePrewarmCaptureEnvVar = XREngineEnvironmentVariables.VulkanPipelinePrewarmCapture;

    internal VulkanPipelinePrewarmDatabase? PrewarmDatabase => _prewarmDatabase;

    /// <summary>
    /// Publishes generation-local native pipeline services without retaining the
    /// renderer facade. The device context instance is completed in place during
    /// logical-device bootstrap.
    /// </summary>
    internal void PublishDeviceContext(Vk api, VulkanDeviceContext? deviceContext)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (_api is null)
            _api = api;
        else if (!ReferenceEquals(_api, api))
            throw new InvalidOperationException("The Vulkan pipeline manager already owns a different Vk API instance.");

        if (deviceContext is not null)
        {
            if (_deviceContext is null)
                _deviceContext = deviceContext;
            else if (!ReferenceEquals(_deviceContext, deviceContext))
                throw new InvalidOperationException("The Vulkan pipeline manager already owns a different device context.");
        }
    }

    internal void PublishProgramServices(VulkanProgramCreationPort programServices)
    {
        ArgumentNullException.ThrowIfNull(programServices);
        VulkanProgramCreationPort? current = Interlocked.CompareExchange(
            ref _programServices,
            programServices,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, programServices))
            throw new InvalidOperationException("The Vulkan pipeline manager already owns different program services.");
    }

    private Vk RequireApi()
        => _api ?? throw new InvalidOperationException("The Vulkan pipeline manager has no published Vk API.");

    private VulkanDeviceContext RequireDeviceContext()
        => _deviceContext ?? throw new InvalidOperationException("The Vulkan pipeline manager has no published device context.");

    private VulkanProgramCreationPort RequireProgramServices()
        => _programServices ?? throw new InvalidOperationException(
            "The Vulkan pipeline manager has no published program services.");

    internal string? PrewarmDatabaseFilePath => _prewarmDatabaseFilePath;

    internal bool PrewarmCaptureEnabled => _prewarmCaptureEnabled;

    /// <summary>
    /// Resolves a recording-specific pipeline manifest from the cache owned by
    /// this pipeline authority. The renderer facade may request a manifest, but
    /// cache mutation remains generation-local resource state.
    /// </summary>
    internal VulkanPipelineVariantManifest GetOrBuildVariantManifest(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence operations,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature,
        FramePlan? framePlan = null)
    {
        ulong renderGraphPlanSignature = framePlan?.RenderGraphPlanSignature ??
            plan.CompatibilityIdentity;
        VulkanPipelineManifestCacheKey key = new(
            renderGraphPlanSignature,
            recordingStructuralSignature,
            submissionStrategy,
            dynamicRendering);
        lock (_pipelineVariantManifestCacheLock)
        {
            if (_pipelineVariantManifestCache.TryGetValue(key, out VulkanPipelineVariantManifest? manifest))
                return manifest;

            manifest = VulkanPipelineVariantManifest.Build(
                plan,
                operations,
                submissionStrategy,
                dynamicRendering,
                recordingStructuralSignature,
                renderGraphPlanSignature,
                framePlan);
            while (_pipelineVariantManifestCache.Count >= MaxCachedPipelineVariantManifests &&
                   _pipelineVariantManifestInsertionOrder.TryDequeue(out VulkanPipelineManifestCacheKey evictedKey))
            {
                _pipelineVariantManifestCache.Remove(evictedKey);
            }

            _pipelineVariantManifestCache.Add(key, manifest);
            _pipelineVariantManifestInsertionOrder.Enqueue(key);
            return manifest;
        }
    }

    internal void ConfigurePrewarmDatabase(
        VulkanPipelinePrewarmDatabase database,
        string filePath,
        bool captureEnabled)
    {
        _prewarmDatabase = database;
        _prewarmDatabaseFilePath = filePath;
        _prewarmCaptureEnabled = captureEnabled;
        Interlocked.Exchange(ref _prewarmNewEntriesSinceSave, 0);
        Interlocked.Exchange(ref _prewarmAutoSaveInFlight, 0);
    }

    internal bool RecordPrewarmEntry(
        VulkanPipelinePrewarmEntry entry,
        bool countForAutoSave,
        out bool knownAtStartup)
    {
        VulkanPipelinePrewarmDatabase? database = _prewarmDatabase;
        knownAtStartup = database?.WasKnownAtStartup(entry.Key) == true;
        if (database?.Record(entry) != true || !countForAutoSave)
            return false;

        Interlocked.Increment(ref _prewarmNewEntriesSinceSave);
        return true;
    }

    internal bool TryBeginPrewarmAutoSave(
        int entryThreshold,
        out VulkanPipelinePrewarmDatabase database,
        out string filePath)
    {
        database = _prewarmDatabase!;
        filePath = _prewarmDatabaseFilePath ?? string.Empty;
        if (!_prewarmCaptureEnabled ||
            database is null ||
            string.IsNullOrWhiteSpace(filePath) ||
            Volatile.Read(ref _prewarmNewEntriesSinceSave) < entryThreshold ||
            Interlocked.CompareExchange(ref _prewarmAutoSaveInFlight, 1, 0) != 0)
        {
            return false;
        }

        Interlocked.Exchange(ref _prewarmNewEntriesSinceSave, 0);
        return true;
    }

    internal bool CompletePrewarmAutoSave(int entryThreshold)
    {
        Interlocked.Exchange(ref _prewarmAutoSaveInFlight, 0);
        return Volatile.Read(ref _prewarmNewEntriesSinceSave) >= entryThreshold;
    }

    internal void InitializePipelinePrewarmDatabase(PhysicalDeviceProperties properties)
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XREngine",
            "Vulkan",
            "PipelinePrewarm");
        string deviceProfile =
            $"v{VulkanPipelinePrewarmDatabase.CurrentVersion}_{properties.VendorID:X8}_{properties.DeviceID:X8}_{properties.DriverVersion:X8}_{properties.ApiVersion:X8}_{VulkanFeatureProfile.ActiveProfile}";
        string filePath = Path.Combine(cacheDir, $"prewarm_{deviceProfile}.json");
        bool captureEnabled = !string.Equals(
            Environment.GetEnvironmentVariable(PipelinePrewarmCaptureEnvVar),
            "0",
            StringComparison.OrdinalIgnoreCase);
        VulkanPipelinePrewarmDatabase database =
            VulkanPipelinePrewarmDatabase.LoadOrCreate(filePath, deviceProfile);
        ConfigurePrewarmDatabase(database, filePath, captureEnabled);
        Debug.Vulkan(
            "[Vulkan] Pipeline prewarm database loaded (path={0}, entries={1}, capture={2}).",
            filePath,
            database.EntryCount,
            captureEnabled);
    }

    internal void SavePipelinePrewarmDatabase()
    {
        if (!_prewarmCaptureEnabled ||
            _prewarmDatabase is null ||
            !_prewarmDatabase.Dirty ||
            string.IsNullOrWhiteSpace(_prewarmDatabaseFilePath))
        {
            return;
        }

        try
        {
            _prewarmDatabase.Save(_prewarmDatabaseFilePath);
            Debug.Vulkan(
                "[Vulkan] Pipeline prewarm database saved ({0} entries).",
                _prewarmDatabase.EntryCount);
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Failed to save pipeline prewarm database '{0}': {1}",
                _prewarmDatabaseFilePath,
                exception.Message);
        }
    }

    internal bool RecordGraphicsPipelineCacheMiss(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        string pipelineName,
        string? meshName,
        XRMaterial material,
        string? programName,
        PrimitiveTopology topology,
        bool useDynamicRendering,
        RenderPass renderPass,
        DynamicRenderingFormatSignature dynamicRenderingFormats,
        ulong programPipelineHash,
        ulong vertexLayoutHash,
        ulong descriptorLayoutHash,
        ulong passMetadataHash,
        ulong featureProfileHash,
        ulong fixedFunctionStateHash,
        SampleCountFlags rasterizationSamples,
        bool depthTestEnabled,
        bool blendEnabled,
        bool alphaToCoverageEnabled,
        ColorComponentFlags colorWriteMask)
    {
        string passName = ResolveRenderPassName(passIndex, passMetadata);
        string resolvedProgramName = string.IsNullOrWhiteSpace(programName)
            ? "UnnamedProgram"
            : programName;
        string resolvedMeshName = string.IsNullOrWhiteSpace(meshName)
            ? "UnnamedMesh"
            : meshName;
        string materialName = string.IsNullOrWhiteSpace(material.Name)
            ? "UnnamedMaterial"
            : material.Name;
        string effectName = ResolveMaterialEffectName(material);
        string renderPassSignature = useDynamicRendering
            ? BuildDynamicRenderingSignature(dynamicRenderingFormats)
            : RequireProgramServices().GetRenderPassSemanticSignature(renderPass);

        VulkanPipelinePrewarmEntry entry = VulkanPipelinePrewarmDatabase.CreateGraphicsEntry(
            passIndex,
            passName,
            pipelineName,
            resolvedMeshName,
            materialName,
            resolvedProgramName,
            effectName,
            topology,
            useDynamicRendering,
            renderPassSignature,
            useDynamicRendering ? dynamicRenderingFormats.DescribeColorFormats() : Format.Undefined.ToString(),
            useDynamicRendering ? dynamicRenderingFormats.DepthAttachmentFormat.ToString() : Format.Undefined.ToString(),
            programPipelineHash,
            vertexLayoutHash,
            descriptorLayoutHash,
            passMetadataHash,
            featureProfileHash,
            fixedFunctionStateHash,
            rasterizationSamples,
            depthTestEnabled,
            blendEnabled,
            alphaToCoverageEnabled,
            colorWriteMask,
            VulkanFeatureProfile.ActiveProfile.ToString());

        bool shouldAutoSave = RecordPrewarmEntry(
            entry,
            countForAutoSave: true,
            out bool knownAtStartup);
        if (shouldAutoSave)
            QueuePrewarmAutoSave();
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheMiss(
            entry.ToProfilerSummary(knownAtStartup));
        return knownAtStartup;
    }

    private void QueuePrewarmAutoSave()
    {
        if (!TryBeginPrewarmAutoSave(
                PipelinePrewarmAutoSaveEntryThreshold,
                out VulkanPipelinePrewarmDatabase database,
                out string path))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                database.Save(path);
            }
            catch (Exception exception)
            {
                Debug.VulkanWarning(
                    "[Vulkan] Failed to auto-save pipeline prewarm database '{0}': {1}",
                    path,
                    exception.Message);
            }
            finally
            {
                if (CompletePrewarmAutoSave(PipelinePrewarmAutoSaveEntryThreshold))
                    QueuePrewarmAutoSave();
            }
        });
    }

    private static string ResolveRenderPassName(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is not null)
            foreach (RenderPassMetadata metadata in passMetadata)
                if (metadata.PassIndex == passIndex)
                    return metadata.Name;

        return passIndex == VulkanBarrierPlanner.SwapchainPassIndex
            ? "Swapchain"
            : "UnknownPass";
    }

    private static string ResolveMaterialEffectName(XRMaterial material)
    {
        if (material.Shaders.Count == 0)
            return "<no shaders>";

        return string.Join("+", material.Shaders.Select(static shader =>
            shader.Name ?? shader.Source?.Name ?? shader.Type.ToString()));
    }

    private static string BuildDynamicRenderingSignature(
        DynamicRenderingFormatSignature formats)
        => $"Dynamic:Colors={formats.DescribeColorFormats()};Depth={formats.DepthAttachmentFormat};Stencil={formats.StencilAttachmentFormat};ViewMask=0x{formats.ViewMask:X8};Layers={formats.LayerCount}";

    internal ulong SharedGraphicsPipelineGeneration
    {
        get
        {
            lock (_sharedGraphicsPipelineLock)
                return _sharedGraphicsPipelineGeneration;
        }
    }

    internal void QueueProgramLinkUntilDeviceReady(VkRenderProgram program)
    {
        lock (_pendingDeviceReadyProgramLinksLock)
            _pendingDeviceReadyProgramLinks.Add(program);
    }

    internal int FlushPendingDeviceReadyProgramLinks()
    {
        lock (_pendingDeviceReadyProgramLinksLock)
        {
            int deferredCount = _pendingDeviceReadyProgramLinks.Count;
            _pendingDeviceReadyProgramLinks.Clear();
            return deferredCount;
        }
    }

    internal void ClearPendingDeviceReadyProgramLinks()
    {
        lock (_pendingDeviceReadyProgramLinksLock)
            _pendingDeviceReadyProgramLinks.Clear();
    }

    internal bool TryGetSharedGraphicsPipeline(
        in VulkanGraphicsPipelineKey key,
        out Pipeline pipeline)
    {
        lock (_sharedGraphicsPipelineLock)
            return _sharedGraphicsPipelines.TryGetValue(key, out pipeline) &&
                pipeline.Handle != 0;
    }

    internal Pipeline StoreSharedGraphicsPipeline(
        in VulkanGraphicsPipelineKey key,
        Pipeline pipeline)
    {
        if (pipeline.Handle == 0)
            return pipeline;

        lock (_sharedGraphicsPipelineLock)
        {
            if (_sharedGraphicsPipelines.TryGetValue(key, out Pipeline existing) &&
                existing.Handle != 0)
            {
                return existing;
            }

            _sharedGraphicsPipelines[key] = pipeline;
            _sharedGraphicsPipelineGeneration++;
            return pipeline;
        }
    }

    internal Pipeline StoreOrRetireSharedGraphicsPipeline(
        in VulkanGraphicsPipelineKey key,
        Pipeline pipeline)
    {
        Pipeline cachedOrCreated = StoreSharedGraphicsPipeline(key, pipeline);
        if (pipeline.Handle != 0 && cachedOrCreated.Handle != pipeline.Handle)
            RequireProgramServices().RetirePipeline(pipeline);

        return cachedOrCreated;
    }

    private void DrainSupersededSharedGraphicsPipelines()
    {
        while (_supersededSharedGraphicsPipelines.TryDequeue(out Pipeline pipeline))
            RequireProgramServices().RetirePipeline(pipeline);
    }

    /// <summary>
    /// Publishes a completed worker result without retaining a renderer. Any duplicate native
    /// handle is queued for retirement by normal renderer-frame orchestration.
    /// </summary>
    internal void PublishCompletedGraphicsPipelineCompile(VulkanGraphicsPipelineCompileJob completedJob)
    {
        lock (_vulkanGraphicsPipelineCompileJobsLock)
        {
            if (!_vulkanGraphicsPipelineCompileJobs.TryGetValue(
                    completedJob.Request.CompileKey,
                    out VulkanGraphicsPipelineCompileJob? registeredJob) ||
                !ReferenceEquals(registeredJob, completedJob))
            {
                return;
            }

            if (completedJob.Task.IsCompletedSuccessfully)
            {
                VulkanGraphicsPipelineCompileResult result =
                    completedJob.Task.GetAwaiter().GetResult();
                if (result.Success && result.Pipeline.Handle != 0)
                {
                    Pipeline published = StoreSharedGraphicsPipeline(
                        completedJob.Request.Key,
                        result.Pipeline);
                    if (published.Handle != result.Pipeline.Handle)
                        _supersededSharedGraphicsPipelines.Enqueue(result.Pipeline);
                }
            }

            _vulkanGraphicsPipelineCompileJobs.TryRemove(
                completedJob.Request.CompileKey,
                out _);
            ReleaseProgramCompileReservation(completedJob.Request);
            Interlocked.Increment(ref _vulkanPipelineCompileActivityGeneration);
        }
    }

    internal bool TryTakeSupersededSharedGraphicsPipeline(out Pipeline pipeline)
        => _supersededSharedGraphicsPipelines.TryDequeue(out pipeline);

    /// <summary>
    /// Runs native worker creation from the pipeline authority. The request retains the
    /// shader-module/layout generation captured by its producer until this call returns.
    /// </summary>
    internal VulkanGraphicsPipelineCompileResult CreateGraphicsPipelineOnWorker(
        VulkanGraphicsPipelineBuildRequest request,
        PipelineCache backgroundPipelineCache)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            Pipeline pipeline = CreateGraphicsPipelineFromRequest(
                request,
                backgroundPipelineCache,
                backgroundCompile: true);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            PublishBackgroundPipelineCache(elapsedMs);
            uint keyHash = unchecked((uint)request.Key.GetHashCode());
            Debug.Vulkan(
                "[Vulkan] Async graphics pipeline compiled in {0:F2} ms: pipeline='{1}' program='{2}' key=0x{3:X8} programHash=0x{4:X16} vertexLayout=0x{5:X16} descriptorLayout=0x{6:X16} depthTest={7} depthWrite={8} depthCompare={9} blend={10} atc={11} cull={12} handle=0x{13:X}.",
                elapsedMs,
                request.PipelineName,
                request.Program.Data.Name ?? "<unnamed program>",
                keyHash,
                request.Key.ProgramPipelineHash,
                request.Key.VertexLayoutHash,
                request.Key.DescriptorLayoutHash,
                request.Key.DepthTestEnabled,
                request.Key.DepthWriteEnabled,
                request.Key.DepthCompareOp,
                request.Key.BlendEnabled,
                request.Key.AlphaToCoverageEnabled,
                request.Key.CullMode,
                pipeline.Handle);
            return new VulkanGraphicsPipelineCompileResult(true, pipeline, null, elapsedMs);
        }
        catch (VulkanPipelineCompilationDeferredException ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new VulkanGraphicsPipelineCompileResult(
                false,
                default,
                ex.Message,
                elapsedMs,
                Retryable: true);
        }
        catch (Exception ex)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return new VulkanGraphicsPipelineCompileResult(false, default, ex.Message, elapsedMs);
        }
    }

    internal Pipeline CreateGraphicsPipelineFromRequest(
        VulkanGraphicsPipelineBuildRequest request,
        PipelineCache pipelineCache,
        bool backgroundCompile)
        => VulkanGraphicsPipelineFactory.Create(
            this,
            request,
            pipelineCache,
            backgroundCompile);

    private void ReleaseProgramCompileReservation(
        VulkanGraphicsPipelineBuildRequest request)
    {
        if (_vulkanGraphicsPipelineProgramCompileJobs.TryGetValue(
                request.Key.ProgramPipelineHash,
                out VulkanGraphicsPipelineCompileKey compileKey) &&
            compileKey.Equals(request.CompileKey))
        {
            _vulkanGraphicsPipelineProgramCompileJobs.Remove(
                request.Key.ProgramPipelineHash);
        }
    }

    internal Pipeline[] DrainSharedGraphicsPipelines()
    {
        lock (_sharedGraphicsPipelineLock)
        {
            if (_sharedGraphicsPipelines.Count == 0)
                return [];

            Pipeline[] pipelines = [.. _sharedGraphicsPipelines.Values];
            _sharedGraphicsPipelines.Clear();
            return pipelines;
        }
    }

    internal int DestroySharedGraphicsPipelines()
    {
        Pipeline[] pipelines = DrainSharedGraphicsPipelines();
        VulkanProgramCreationPort services = RequireProgramServices();
        int destroyed = 0;
        for (int index = 0; index < pipelines.Length; index++)
        {
            Pipeline pipeline = pipelines[index];
            if (pipeline.Handle == 0)
                continue;

            services.DestroyPipelineImmediate(pipeline);
            destroyed++;
        }

        return destroyed;
    }

    internal bool TryGetOrReserveSharedGraphicsPipelineLibrary(
        in VulkanGraphicsPipelineLibraryKey key,
        out Pipeline library,
        out bool creationReserved)
    {
        lock (_sharedGraphicsPipelineLibraryLock)
        {
            if (_sharedGraphicsPipelineLibraries.TryGetValue(key, out library) &&
                library.Handle != 0)
            {
                creationReserved = false;
                return true;
            }

            creationReserved = _sharedGraphicsPipelineLibraryCreations.Add(key);
            return false;
        }
    }

    internal Pipeline CompleteSharedGraphicsPipelineLibraryCreation(
        in VulkanGraphicsPipelineLibraryKey key,
        Pipeline library)
    {
        if (library.Handle == 0)
        {
            CancelSharedGraphicsPipelineLibraryCreation(key);
            return library;
        }

        lock (_sharedGraphicsPipelineLibraryLock)
        {
            _sharedGraphicsPipelineLibraryCreations.Remove(key);
            if (_sharedGraphicsPipelineLibraries.TryGetValue(key, out Pipeline existing) &&
                existing.Handle != 0)
            {
                return existing;
            }

            _sharedGraphicsPipelineLibraries[key] = library;
            return library;
        }
    }

    internal void CancelSharedGraphicsPipelineLibraryCreation(
        in VulkanGraphicsPipelineLibraryKey key)
    {
        lock (_sharedGraphicsPipelineLibraryLock)
            _sharedGraphicsPipelineLibraryCreations.Remove(key);
    }

    internal Pipeline[] DrainSharedGraphicsPipelineLibraries()
    {
        lock (_sharedGraphicsPipelineLibraryLock)
        {
            _sharedGraphicsPipelineLibraryCreations.Clear();
            if (_sharedGraphicsPipelineLibraries.Count == 0)
                return [];

            Pipeline[] libraries = [.. _sharedGraphicsPipelineLibraries.Values];
            _sharedGraphicsPipelineLibraries.Clear();
            return libraries;
        }
    }

    internal int DestroySharedGraphicsPipelineLibraries()
    {
        Pipeline[] libraries = DrainSharedGraphicsPipelineLibraries();
        VulkanProgramCreationPort services = RequireProgramServices();
        int destroyed = 0;
        for (int index = 0; index < libraries.Length; index++)
        {
            Pipeline library = libraries[index];
            if (library.Handle == 0)
                continue;

            services.DestroyPipelineImmediate(library);
            destroyed++;
        }

        return destroyed;
    }
}
