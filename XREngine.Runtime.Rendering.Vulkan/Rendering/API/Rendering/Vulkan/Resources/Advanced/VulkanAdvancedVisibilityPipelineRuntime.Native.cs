using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
{
    private readonly XRRenderProgram?[] _nativeComputePrograms = new XRRenderProgram?[6];

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetNativeComputePipelines(
        out VulkanAdvancedNativeComputePipelines pipelines, out string reason)
    {
        pipelines = default;
        VulkanAdvancedVisibilityPipelineReadiness readiness = TryGetNativeComputePipeline(0,
            "Advanced/Classification/ClassifyTiles.comp", out VulkanAdvancedComputePipeline classify, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(1, "Advanced/Classification/BuildClassificationIndirect.comp",
            out VulkanAdvancedComputePipeline arguments, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(2, "Advanced/AO/Gtao.comp",
            out VulkanAdvancedComputePipeline ambientOcclusion, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(3, "Advanced/Lighting/BuildFroxels.comp",
            out VulkanAdvancedComputePipeline froxels, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(4, "Advanced/Shading/ShadeBackground.comp",
            out VulkanAdvancedComputePipeline background, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        readiness = TryGetNativeComputePipeline(5, "Advanced/Shading/ShadeNativeOpaque.comp",
            out VulkanAdvancedComputePipeline shade, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready) return readiness;
        pipelines = new(classify, arguments, ambientOcclusion, froxels, background, shade);
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness TryGetNativeComputePipeline(
        int index, string path, out VulkanAdvancedComputePipeline binding, out string reason)
    {
        binding = default;
        try
        {
            XRRenderProgram source = _nativeComputePrograms[index] ??= CreateComputeProgram(path, path);
            if (_resources.WrapperLookup.GetOrCreate(source, generateNow: true) is not VkRenderProgram program ||
                !program.Link(allowAsyncShaderCompile: false) || !program.IsLinked || program.PipelineLayout.Handle == 0)
            {
                reason = DescribeProgramFailure(source, path);
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            VulkanComputePipelineReadiness readiness = program.TryGetOrRequestComputePipeline(
                int.MinValue, null, out Pipeline pipeline, out string detail);
            if (readiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(readiness, path, detail, out reason);
            binding = new(program, pipeline, program.LinkGeneration);
            reason = "Ready";
            return VulkanAdvancedVisibilityPipelineReadiness.Ready;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }
}
