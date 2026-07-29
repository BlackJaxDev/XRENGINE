using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns renderer-wide program-link coordination and shared pipeline caches for
/// one Vulkan logical-device lifetime.
/// </summary>
internal sealed class VulkanPipelineManager
{
    private readonly object _pendingDeviceReadyProgramLinksLock = new();
    private readonly HashSet<VulkanRenderer.VkRenderProgram> _pendingDeviceReadyProgramLinks = [];
    private readonly object _sharedGraphicsPipelineLock = new();
    private readonly Dictionary<VulkanRenderer.VkMeshRenderer.PipelineKey, Pipeline> _sharedGraphicsPipelines = [];
    private readonly object _sharedGraphicsPipelineLibraryLock = new();
    private readonly Dictionary<VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey, Pipeline>
        _sharedGraphicsPipelineLibraries = [];
    private readonly HashSet<VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey>
        _sharedGraphicsPipelineLibraryCreations = [];
    private ulong _sharedGraphicsPipelineGeneration;

    internal ulong SharedGraphicsPipelineGeneration
    {
        get
        {
            lock (_sharedGraphicsPipelineLock)
                return _sharedGraphicsPipelineGeneration;
        }
    }

    internal void QueueProgramLinkUntilDeviceReady(VulkanRenderer.VkRenderProgram program)
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
        in VulkanRenderer.VkMeshRenderer.PipelineKey key,
        out Pipeline pipeline)
    {
        lock (_sharedGraphicsPipelineLock)
            return _sharedGraphicsPipelines.TryGetValue(key, out pipeline) &&
                pipeline.Handle != 0;
    }

    internal Pipeline StoreSharedGraphicsPipeline(
        in VulkanRenderer.VkMeshRenderer.PipelineKey key,
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
        in VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey key,
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
        in VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey key,
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
        in VulkanRenderer.VkMeshRenderer.GraphicsPipelineLibraryKey key)
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
