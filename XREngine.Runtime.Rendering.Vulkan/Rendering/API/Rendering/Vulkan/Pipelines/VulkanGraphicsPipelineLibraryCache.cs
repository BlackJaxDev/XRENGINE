using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly Dictionary<VkMeshRenderer.GraphicsPipelineLibraryKey, Pipeline> _sharedGraphicsPipelineLibraries = new();
    private readonly object _sharedGraphicsPipelineLibraryLock = new();

    internal bool TryGetSharedGraphicsPipelineLibrary(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
        out Pipeline library)
    {
        lock (_sharedGraphicsPipelineLibraryLock)
            return _sharedGraphicsPipelineLibraries.TryGetValue(key, out library) && library.Handle != 0;
    }

    internal Pipeline StoreSharedGraphicsPipelineLibrary(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
        Pipeline library)
    {
        if (library.Handle == 0)
            return library;

        lock (_sharedGraphicsPipelineLibraryLock)
        {
            if (_sharedGraphicsPipelineLibraries.TryGetValue(key, out Pipeline existing) &&
                existing.Handle != 0)
            {
                return existing;
            }

            _sharedGraphicsPipelineLibraries[key] = library;
            return library;
        }
    }

    private void DestroySharedGraphicsPipelineLibraries()
    {
        Pipeline[] libraries;
        lock (_sharedGraphicsPipelineLibraryLock)
        {
            if (_sharedGraphicsPipelineLibraries.Count == 0)
                return;

            libraries = [.. _sharedGraphicsPipelineLibraries.Values];
            _sharedGraphicsPipelineLibraries.Clear();
        }

        if (Api is null || device.Handle == 0)
            return;

        int destroyed = 0;
        foreach (Pipeline library in libraries)
        {
            if (library.Handle == 0)
                continue;

            Api.DestroyPipeline(device, library, null);
            CompleteVulkanResourceDestruction(ObjectType.Pipeline, library.Handle);
            destroyed++;
        }

        Debug.Vulkan("[Vulkan] Destroyed {0} shared graphics pipeline librar{1}.", destroyed, destroyed == 1 ? "y" : "ies");
    }
}
