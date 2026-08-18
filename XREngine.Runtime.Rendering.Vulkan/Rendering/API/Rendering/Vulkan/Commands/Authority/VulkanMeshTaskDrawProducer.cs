using Silk.NET.Vulkan;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the pre-recording pipeline admission for deferred task/mesh draws.
/// The recorded operation receives a concrete native pipeline; it never relies
/// on whatever graphics pipeline happened to be bound by an earlier operation.
/// </summary>
internal static class VulkanMeshTaskDrawProducer
{
    /// <summary>
    /// Resolves the pipeline that a sealed mesh-task operation will bind during
    /// primary recording.  This is deliberately callable only from the
    /// pre-recording admission phase: the target's render-pass or dynamic
    /// rendering signature is final there, whereas enqueue can still observe a
    /// legacy/default target while the frame graph is being assembled.
    /// </summary>
    internal static bool TryAdmitPrimaryPipeline(
        VulkanCommandRuntime runtime,
        VkRenderProgram program,
        in VulkanMeshProducerSnapshot producer,
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        RenderPass renderPass,
        bool useDynamicRendering,
        in DynamicRenderingFormatSignature dynamicFormats,
        SampleCountFlags rasterizationSamples,
        bool depthStencilReadOnly,
        out Pipeline pipeline,
        out string reason)
        => TryPrepare(
            runtime,
            program,
            producer,
            passIndex,
            passMetadata,
            renderPass,
            useDynamicRendering,
            dynamicFormats,
            rasterizationSamples,
            depthStencilReadOnly,
            out pipeline,
            out reason);

    internal static bool TryPrepare(
        VulkanCommandRuntime runtime,
        VkRenderProgram program,
        in VulkanMeshProducerSnapshot producer,
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        RenderPass renderPass,
        bool useDynamicRendering,
        in DynamicRenderingFormatSignature dynamicFormats,
        SampleCountFlags rasterizationSamples,
        bool depthStencilReadOnly,
        out Pipeline pipeline,
        out string reason)
    {
        pipeline = default;
        reason = string.Empty;
        if (!program.IsLinked || program.PipelineLayout.Handle == 0)
        {
            reason = "task/mesh program is not linked with a pipeline layout";
            return false;
        }

        VulkanFixedFunctionStateSnapshot state = producer.FixedFunctionState;
        uint colorAttachmentCount = useDynamicRendering
            ? dynamicFormats.ColorAttachmentCount
            : program.MeshTaskProgramServices.GetRenderPassColorAttachmentCount(renderPass);
        if (depthStencilReadOnly)
        {
            StencilOpState frontStencil = state.FrontStencilState;
            StencilOpState backStencil = state.BackStencilState;
            frontStencil.WriteMask = 0;
            backStencil.WriteMask = 0;
            state = state with
            {
                DepthWriteEnabled = false,
                FrontStencilState = frontStencil,
                BackStencilState = backStencil,
                StencilWriteMask = 0,
            };
        }

        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList, useDynamicRendering,
            useDynamicRendering ? 0UL : renderPass.Handle, dynamicFormats,
            program.ComputeGraphicsPipelineFingerprint(), program.LinkGeneration,
            VertexLayoutHash: 0UL, program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle, PassMetadataHash: 0UL, FeatureProfileHash: 0UL,
            rasterizationSamples, state.DepthTestEnabled, state.DepthWriteEnabled,
            state.DepthCompareOp, state.StencilTestEnabled, state.FrontStencilState,
            state.BackStencilState, state.StencilWriteMask, state.CullMode, state.FrontFace,
            state.BlendEnabled, state.AlphaToCoverageEnabled, state.ColorBlendOp,
            state.AlphaBlendOp, state.SrcColorBlendFactor, state.DstColorBlendFactor,
            state.SrcAlphaBlendFactor, state.DstAlphaBlendFactor, state.ColorWriteMask,
            Math.Max(producer.IndexedViewportScissors.Count, 1u),
            RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);
        VulkanPipelineManager manager = program.MeshTaskBackendContext.Resources.PipelineManager;
        if (manager.TryGetSharedGraphicsPipeline(key, out pipeline) && pipeline.Handle != 0)
            return true;

        PipelineColorBlendAttachmentState blend = new()
        {
            ColorWriteMask = state.ColorWriteMask,
            BlendEnable = state.BlendEnabled ? Vk.True : Vk.False,
            ColorBlendOp = state.ColorBlendOp, AlphaBlendOp = state.AlphaBlendOp,
            SrcColorBlendFactor = state.SrcColorBlendFactor, DstColorBlendFactor = state.DstColorBlendFactor,
            SrcAlphaBlendFactor = state.SrcAlphaBlendFactor, DstAlphaBlendFactor = state.DstAlphaBlendFactor,
        };
        PipelineColorBlendAttachmentState[] blends = new PipelineColorBlendAttachmentState[colorAttachmentCount];
        for (int i = 0; i < blends.Length; i++) blends[i] = blend;
        PipelineRasterizationStateCreateInfo raster = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
            CullMode = state.CullMode, FrontFace = state.FrontFace, LineWidth = 1.0f,
        };
        PipelineMultisampleStateCreateInfo samples = new() { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = rasterizationSamples, AlphaToCoverageEnable = state.AlphaToCoverageEnabled ? Vk.True : Vk.False };
        PipelineDepthStencilStateCreateInfo depth = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = state.DepthTestEnabled ? Vk.True : Vk.False,
            DepthWriteEnable = state.DepthWriteEnabled ? Vk.True : Vk.False, DepthCompareOp = state.DepthCompareOp,
            StencilTestEnable = state.StencilTestEnabled ? Vk.True : Vk.False, Front = state.FrontStencilState, Back = state.BackStencilState,
        };
        using VulkanPipelineCompilationDependencyLease lease = manager.AcquireCompilationDependencyLease();
        VulkanGraphicsPipelineBuildRequest request = new(
            program.BindingId, program, program.MeshTaskProgramServices, useGraphicsPipelineLibraries: false,
            lease.Generation, key, "MeshTaskDeferred", colorAttachmentCount, program.PipelineLayout,
            [], [], default, Math.Max(producer.IndexedViewportScissors.Count, 1u),
            RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl, raster, samples, depth, blends,
            [DynamicState.Viewport, DynamicState.Scissor], renderPass, dynamicFormats,
            [.. program.GetShaderStages(VulkanProgramUtilities.GraphicsStageMask)],
            [.. program.GetShaderStages(EProgramStageMask.TaskShaderBit | EProgramStageMask.MeshShaderBit)],
            [.. program.GetShaderStages(EProgramStageMask.FragmentShaderBit)], isMeshShaderPipeline: true);
        try
        {
            pipeline = manager.StoreOrRetireSharedGraphicsPipeline(key, manager.CreateGraphicsPipelineFromRequest(request, manager.ActivePipelineCache, backgroundCompile: false));
            return pipeline.Handle != 0;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
