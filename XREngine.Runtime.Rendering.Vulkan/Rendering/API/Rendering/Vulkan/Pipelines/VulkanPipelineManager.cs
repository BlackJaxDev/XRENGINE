using System.Collections.Concurrent;
using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns renderer-wide program-link coordination and shared pipeline caches for
/// one Vulkan logical-device lifetime.
/// </summary>
internal sealed class VulkanPipelineManager
{
    internal readonly ConcurrentDictionary<VkMeshRenderer.GraphicsPipelineCompileKey, VulkanRenderer.VulkanGraphicsPipelineCompileJob> _vulkanGraphicsPipelineCompileJobs = new();
    internal readonly Dictionary<ulong, VkMeshRenderer.GraphicsPipelineCompileKey> _vulkanGraphicsPipelineProgramCompileJobs = new();
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
    internal readonly Dictionary<VulkanRenderer.VulkanPipelineManifestCacheKey, VulkanRenderer.VulkanPipelineVariantManifest> _pipelineVariantManifestCache = new();
    internal readonly Queue<VulkanRenderer.VulkanPipelineManifestCacheKey> _pipelineVariantManifestInsertionOrder = new();
    internal readonly Lock _pipelineVariantManifestCacheLock = new();
    private readonly object _pendingDeviceReadyProgramLinksLock = new();
    private readonly HashSet<VkRenderProgram> _pendingDeviceReadyProgramLinks = [];
    private readonly object _sharedGraphicsPipelineLock = new();
    private readonly Dictionary<VkMeshRenderer.PipelineKey, Pipeline> _sharedGraphicsPipelines = [];
    private readonly object _sharedGraphicsPipelineLibraryLock = new();
    private readonly Dictionary<VkMeshRenderer.GraphicsPipelineLibraryKey, Pipeline>
        _sharedGraphicsPipelineLibraries = [];
    private readonly HashSet<VkMeshRenderer.GraphicsPipelineLibraryKey>
        _sharedGraphicsPipelineLibraryCreations = [];
    private ulong _sharedGraphicsPipelineGeneration;
    private VulkanPipelinePrewarmDatabase? _prewarmDatabase;
    private string? _prewarmDatabaseFilePath;
    private bool _prewarmCaptureEnabled;
    private int _prewarmNewEntriesSinceSave;
    private int _prewarmAutoSaveInFlight;

    internal VulkanPipelinePrewarmDatabase? PrewarmDatabase => _prewarmDatabase;

    internal string? PrewarmDatabaseFilePath => _prewarmDatabaseFilePath;

    internal bool PrewarmCaptureEnabled => _prewarmCaptureEnabled;

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
        in VkMeshRenderer.PipelineKey key,
        out Pipeline pipeline)
    {
        lock (_sharedGraphicsPipelineLock)
            return _sharedGraphicsPipelines.TryGetValue(key, out pipeline) &&
                pipeline.Handle != 0;
    }

    internal Pipeline StoreSharedGraphicsPipeline(
        in VkMeshRenderer.PipelineKey key,
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

    internal bool TryGetOrReserveSharedGraphicsPipelineLibrary(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
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
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
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
        in VkMeshRenderer.GraphicsPipelineLibraryKey key)
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
}
