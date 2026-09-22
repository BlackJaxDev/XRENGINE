namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
{
    private XRRenderProgram? _multisampleResolveProgram;
    private XRRenderProgram? _multiviewMultisampleResolveProgram;
    private XRRenderProgram? _lateVisibilityNoHzbProgram;

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetMultisampleResolveProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        XRRenderProgram? source = multiview
            ? _multiviewMultisampleResolveProgram
            : _multisampleResolveProgram;
        if (source is null ||
            _resources.WrapperLookup.GetOrCreate(source, generateNow: false) is not VkRenderProgram resolve)
        {
            reason = "Prepared MSAA visibility resolve wrapper is unavailable.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        program = resolve;
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetLateVisibilityComputePipelines(
        bool disableHzbOcclusion,
        out VkRenderProgram buildDepthPyramid,
        out VkRenderProgram lateVisibility,
        out string reason)
    {
        if (!disableHzbOcclusion)
            return TryGetLateVisibilityComputePipelines(
                out buildDepthPyramid, out lateVisibility, out reason);

        buildDepthPyramid = null!;
        lateVisibility = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        if (_resources.WrapperLookup.GetOrCreate(_buildDepthPyramidProgram!, generateNow: false) is not VkRenderProgram depth ||
            _resources.WrapperLookup.GetOrCreate(_lateVisibilityNoHzbProgram!, generateNow: false) is not VkRenderProgram late)
        {
            reason = "Prepared MSAA late-visibility compute wrappers are unavailable.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        buildDepthPyramid = depth;
        lateVisibility = late;
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareMultisamplePrograms(
        out string reason)
    {
        _lateVisibilityNoHzbProgram ??= CreateComputeProgram(
            AdvancedVisibilityShaderLibrary.LateVisibilityCompute,
            "VulkanAdvancedLateVisibilityNoHzb",
            "#define XR_ADV_DISABLE_HZB_OCCLUSION 1\n");
        VulkanAdvancedVisibilityPipelineReadiness readiness = TryPrepareProgram(
            _lateVisibilityNoHzbProgram,
            out VkRenderProgram lateNoHzb,
            out reason,
            "MSAA late-visibility compute program did not link a Vulkan pipeline layout");
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        VulkanComputePipelineReadiness lateReadiness = lateNoHzb.TryGetOrRequestComputePipeline(
            int.MinValue, null, out _, out string lateReason);
        if (lateReadiness != VulkanComputePipelineReadiness.Ready)
            return DescribeComputePipelineReadiness(
                lateReadiness, "MSAA late visibility", lateReason, out reason);

        readiness = PrepareMultisampleResolveProgram(multiview: false, out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        bool supportsMultiview =
            _resources.BackendObjectContext?.DeviceContext.AdvancedMultiviewEnabled == true;
        return supportsMultiview
            ? PrepareMultisampleResolveProgram(multiview: true, out reason)
            : VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareMultisampleResolveProgram(
        bool multiview,
        out string reason)
    {
        try
        {
            ref XRRenderProgram? retained = ref multiview
                ? ref _multiviewMultisampleResolveProgram
                : ref _multisampleResolveProgram;
            if (retained is null)
            {
                XRShader vertexAsset = XRShader.EngineShader(
                    "Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert",
                    EShaderType.Vertex);
                XRShader fragmentAsset = XRShader.EngineShader(
                    "Advanced/Visibility/ResolveAdvancedMsaaVisibility.frag",
                    EShaderType.Fragment);
                string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
                    _resources.AdvancedSceneResources);
                if (multiview)
                    preamble = "#define XR_ADV_MULTIVIEW_RESOLVE 1\n" + preamble;
                string name = multiview
                    ? "VulkanAdvancedMsaaVisibilityResolveMultiview"
                    : "VulkanAdvancedMsaaVisibilityResolve";
                retained = new XRRenderProgram(
                    linkNow: false,
                    separable: false,
                    CreateShaderWithPreamble(vertexAsset, preamble, name + ".vert"),
                    CreateShaderWithPreamble(fragmentAsset, preamble, name + ".frag"))
                {
                    Name = name,
                    ExternallyOwnedDescriptorSetMask =
                        VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
                };
            }
            return TryPrepareProgram(retained, out _, out reason,
                "MSAA visibility resolve program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }
}
