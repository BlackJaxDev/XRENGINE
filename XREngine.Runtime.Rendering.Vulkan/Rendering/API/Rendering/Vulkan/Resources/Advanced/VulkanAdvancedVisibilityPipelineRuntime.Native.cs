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
            XRRenderProgram source = _nativeComputePrograms[index] ??= CreateComputeProgram(
                path, path, index == 0 ? ResolveClassificationPreamble() : string.Empty);
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

    /// <summary>Chooses a device-local shader variant once, during cold program creation.</summary>
    private unsafe string ResolveClassificationPreamble()
    {
        VulkanBackendObjectContext context = _resources.BackendObjectContext ??
            throw new InvalidOperationException("Classification requires a published Vulkan device context.");
        PhysicalDeviceSubgroupProperties subgroup = new()
        {
            SType = StructureType.PhysicalDeviceSubgroupProperties,
        };
        PhysicalDeviceProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &subgroup,
        };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
        const SubgroupFeatureFlags required = SubgroupFeatureFlags.BasicBit | SubgroupFeatureFlags.BallotBit;
        bool supported = subgroup.SubgroupSize > 1 &&
            (subgroup.SupportedStages & ShaderStageFlags.ComputeBit) != 0 &&
            (subgroup.SupportedOperations & required) == required;
        Debug.Out($"[VulkanAdvancedClassification] mode={(supported ? "SubgroupBallotScan" : "SharedHistogram")} subgroupSize={subgroup.SubgroupSize} operations={subgroup.SupportedOperations}");
        return supported
            ? "#extension GL_KHR_shader_subgroup_basic : require\n" +
              "#extension GL_KHR_shader_subgroup_ballot : require\n" +
              "#define XR_ADV_CLASSIFICATION_SUBGROUP_BALLOT 1\n"
            : string.Empty;
    }
}
