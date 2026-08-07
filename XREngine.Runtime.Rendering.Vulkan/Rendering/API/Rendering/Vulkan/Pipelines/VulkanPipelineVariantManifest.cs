using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MaxCachedPipelineVariantManifests = 64;

    private VulkanPipelineVariantManifest GetOrBuildPipelineVariantManifest(
        VulkanCompiledRenderGraphPlan plan,
        FrameOperationSequence ops,
        EMeshSubmissionStrategy submissionStrategy,
        bool dynamicRendering,
        ulong recordingStructuralSignature)
    {
        var key = new VulkanPipelineManifestCacheKey(
            plan.CompatibilityIdentity,
            recordingStructuralSignature,
            submissionStrategy,
            dynamicRendering);
        lock (ResourceRuntime.PipelineManager._pipelineVariantManifestCacheLock)
        {
            if (ResourceRuntime.PipelineManager._pipelineVariantManifestCache.TryGetValue(key, out VulkanPipelineVariantManifest? manifest))
                return manifest;

            manifest = VulkanPipelineVariantManifest.Build(
                plan,
                ops,
                submissionStrategy,
                dynamicRendering,
                recordingStructuralSignature);
            while (ResourceRuntime.PipelineManager._pipelineVariantManifestCache.Count >= MaxCachedPipelineVariantManifests &&
                   ResourceRuntime.PipelineManager._pipelineVariantManifestInsertionOrder.TryDequeue(out VulkanPipelineManifestCacheKey evictedKey))
            {
                ResourceRuntime.PipelineManager._pipelineVariantManifestCache.Remove(evictedKey);
            }

            ResourceRuntime.PipelineManager._pipelineVariantManifestCache.Add(key, manifest);
            ResourceRuntime.PipelineManager._pipelineVariantManifestInsertionOrder.Enqueue(key);
            return manifest;
        }
    }

}
