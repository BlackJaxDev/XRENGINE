using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Returns an existing shared library or reserves its key for exactly one creator.
    /// A caller that does not receive the reservation must defer instead of entering
    /// the Vulkan driver for the same library concurrently.
    /// </summary>
    internal bool TryGetOrReserveSharedGraphicsPipelineLibrary(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
        out Pipeline library,
        out bool creationReserved)
        => _pipelineManager.TryGetOrReserveSharedGraphicsPipelineLibrary(
            key,
            out library,
            out creationReserved);

    internal Pipeline CompleteSharedGraphicsPipelineLibraryCreation(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key,
        Pipeline library)
        => _pipelineManager.CompleteSharedGraphicsPipelineLibraryCreation(key, library);

    internal void CancelSharedGraphicsPipelineLibraryCreation(
        in VkMeshRenderer.GraphicsPipelineLibraryKey key)
        => _pipelineManager.CancelSharedGraphicsPipelineLibraryCreation(key);

    private void DestroySharedGraphicsPipelineLibraries()
    {
        Pipeline[] libraries = _pipelineManager.DrainSharedGraphicsPipelineLibraries();
        if (libraries.Length == 0)
            return;

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
