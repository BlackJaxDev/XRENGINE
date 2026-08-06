using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal ulong SharedGraphicsPipelineGeneration
        => ResourceRuntime.PipelineManager.SharedGraphicsPipelineGeneration;

    internal bool TryGetSharedGraphicsPipeline(
        in VkMeshRenderer.PipelineKey key,
        out Pipeline pipeline)
        => ResourceRuntime.PipelineManager.TryGetSharedGraphicsPipeline(key, out pipeline);

    internal Pipeline StoreSharedGraphicsPipeline(
        in VkMeshRenderer.PipelineKey key,
        Pipeline pipeline)
        => ResourceRuntime.PipelineManager.StoreSharedGraphicsPipeline(key, pipeline);

    internal Pipeline StoreOrRetireSharedGraphicsPipeline(
        in VkMeshRenderer.PipelineKey key,
        Pipeline pipeline)
    {
        Pipeline cachedOrCreated = StoreSharedGraphicsPipeline(key, pipeline);
        if (pipeline.Handle != 0 && cachedOrCreated.Handle != pipeline.Handle)
            RetirePipeline(pipeline);

        return cachedOrCreated;
    }

    private void DestroySharedGraphicsPipelines()
    {
        Pipeline[] pipelines = ResourceRuntime.PipelineManager.DrainSharedGraphicsPipelines();
        if (pipelines.Length == 0)
            return;

        if (Api is null || _deviceContext.Device.Handle == 0)
            return;

        int destroyed = 0;
        foreach (Pipeline pipeline in pipelines)
        {
            if (pipeline.Handle == 0)
                continue;

            Api.DestroyPipeline(_deviceContext.Device, pipeline, null);
            CompleteVulkanResourceDestruction(ObjectType.Pipeline, pipeline.Handle);
            destroyed++;
        }

        Debug.Vulkan("[Vulkan] Destroyed {0} shared graphics pipeline{1}.", destroyed, destroyed == 1 ? string.Empty : "s");
    }
}
