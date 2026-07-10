using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly Dictionary<VkMeshRenderer.PipelineKey, Pipeline> _sharedGraphicsPipelines = new();
    private readonly object _sharedGraphicsPipelineLock = new();

    internal bool TryGetSharedGraphicsPipeline(
        in VkMeshRenderer.PipelineKey key,
        out Pipeline pipeline)
    {
        lock (_sharedGraphicsPipelineLock)
            return _sharedGraphicsPipelines.TryGetValue(key, out pipeline) && pipeline.Handle != 0;
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
            return pipeline;
        }
    }

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
        Pipeline[] pipelines;
        lock (_sharedGraphicsPipelineLock)
        {
            if (_sharedGraphicsPipelines.Count == 0)
                return;

            pipelines = [.. _sharedGraphicsPipelines.Values];
            _sharedGraphicsPipelines.Clear();
        }

        if (Api is null || device.Handle == 0)
            return;

        int destroyed = 0;
        foreach (Pipeline pipeline in pipelines)
        {
            if (pipeline.Handle == 0)
                continue;

            Api.DestroyPipeline(device, pipeline, null);
            CompleteVulkanResourceDestruction(ObjectType.Pipeline, pipeline.Handle);
            destroyed++;
        }

        Debug.Vulkan("[Vulkan] Destroyed {0} shared graphics pipeline{1}.", destroyed, destroyed == 1 ? string.Empty : "s");
    }
}
