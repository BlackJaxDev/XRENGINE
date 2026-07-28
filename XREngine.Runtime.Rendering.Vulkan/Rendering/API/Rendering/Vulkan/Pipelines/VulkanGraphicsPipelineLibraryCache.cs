using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly Dictionary<VkMeshRenderer.GraphicsPipelineLibraryKey, Pipeline> _sharedGraphicsPipelineLibraries = new();
    private readonly HashSet<VkMeshRenderer.GraphicsPipelineLibraryKey> _sharedGraphicsPipelineLibraryCreations = new();
    private readonly object _sharedGraphicsPipelineLibraryLock = new();

    /// <summary>
    /// Returns an existing shared library or reserves its key for exactly one creator.
    /// A caller that does not receive the reservation must defer instead of entering
    /// the Vulkan driver for the same library concurrently.
    /// </summary>
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

    private void DestroySharedGraphicsPipelineLibraries()
    {
        Pipeline[] libraries;
        lock (_sharedGraphicsPipelineLibraryLock)
        {
            _sharedGraphicsPipelineLibraryCreations.Clear();
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
