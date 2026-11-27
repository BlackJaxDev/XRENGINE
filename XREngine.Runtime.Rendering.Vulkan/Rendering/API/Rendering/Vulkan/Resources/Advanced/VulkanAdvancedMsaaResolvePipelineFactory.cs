using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Creates the fullscreen graphics pipelines that resolve raw visibility samples.</summary>
internal static class VulkanAdvancedMsaaResolvePipelineFactory
{
    /// <summary>
    /// Stencil-only replacement state used after MSAA visibility resolve to repopulate single-sample
    /// portal node bits (<c>0x7C</c>) via Vulkan 1.0 dynamic <see cref="DynamicState.StencilReference"/>.
    /// </summary>
    internal static StencilOpState PortalStencilRepopulateState => new()
    {
        FailOp = StencilOp.Keep,
        PassOp = StencilOp.Replace,
        DepthFailOp = StencilOp.Keep,
        CompareOp = CompareOp.Always,
        CompareMask = 0xFFu,
        WriteMask = AdvancedStencilBitLayout.PortalNodeMask,
        Reference = 0u,
    };

    /// <summary>Dynamic states required by the portal MSAA stencil repopulation pipeline.</summary>
    internal static ReadOnlySpan<DynamicState> PortalStencilRepopulateDynamicStates
        => [DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference];

    internal static bool TryPrepare(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanAdvancedMsaaResolvePipeline prepared,
        out string reason)
        => TryPrepareCore(program, in target, portalStencilRepopulate: false, out prepared, out reason);

    /// <summary>
    /// Prepares the stencil-only fullscreen pipeline that repopulates single-sample canonical
    /// portal stencil node bits inside the <c>MultisampleResolve</c> render pass.
    /// </summary>
    internal static bool TryPreparePortalStencilRepopulate(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanAdvancedMsaaResolvePipeline prepared,
        out string reason)
        => TryPrepareCore(program, in target, portalStencilRepopulate: true, out prepared, out reason);

    private static bool TryPrepareCore(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        bool portalStencilRepopulate,
        out VulkanAdvancedMsaaResolvePipeline prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        if (!target.IsValid || !program.IsLinked || program.PipelineLayout.Handle == 0UL)
        {
            reason = "MSAA resolve program or exact canonical target closure is unavailable";
            return false;
        }
        if (target.RasterizationSamples != SampleCountFlags.Count1Bit)
        {
            reason = $"MSAA visibility resolve requires a single-sample canonical target, received {target.RasterizationSamples}";
            return false;
        }

        uint colorAttachmentCount = target.UsesDynamicRendering
            ? target.DynamicRenderingFormats.ColorAttachmentCount
            : program.MeshTaskProgramServices.GetRenderPassColorAttachmentCount(target.RenderPass);
        if (colorAttachmentCount != 3u)
        {
            reason = $"MSAA visibility resolve requires exactly three canonical color attachments, received {colorAttachmentCount}";
            return false;
        }

        string label = portalStencilRepopulate
            ? "AdvancedMsaaPortalStencilRepopulate"
            : "AdvancedMsaaVisibilityResolve";
        StencilOpState stencilState = portalStencilRepopulate ? PortalStencilRepopulateState : default;
        ColorComponentFlags colorWriteMask = portalStencilRepopulate
            ? 0
            : ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit;

        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList,
            target.UsesDynamicRendering,
            target.UsesDynamicRendering ? 0UL : target.RenderPass.Handle,
            target.DynamicRenderingFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.InterfaceGeneration,
            0UL,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
            ComputePassMetadataHash(in target, label),
            ComputeFeatureProfileHash(program),
            SampleCountFlags.Count1Bit,
            DepthTestEnabled: !portalStencilRepopulate,
            DepthWriteEnabled: !portalStencilRepopulate,
            CompareOp.Always,
            StencilTestEnabled: portalStencilRepopulate,
            stencilState,
            stencilState,
            0u,
            CullModeFlags.None,
            FrontFace.CounterClockwise,
            BlendEnabled: false,
            AlphaToCoverageEnabled: false,
            BlendOp.Add,
            BlendOp.Add,
            BlendFactor.One,
            BlendFactor.Zero,
            BlendFactor.One,
            BlendFactor.Zero,
            colorWriteMask,
            1u,
            RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);

        VulkanPipelineManager manager = program.MeshTaskBackendContext.Resources.PipelineManager;
        if (!manager.TryGetSharedGraphicsPipeline(key, out Pipeline pipeline))
        {
            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            PipelineRasterizationStateCreateInfo raster = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f,
            };
            PipelineMultisampleStateCreateInfo samples = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = portalStencilRepopulate ? Vk.False : Vk.True,
                DepthWriteEnable = portalStencilRepopulate ? Vk.False : Vk.True,
                DepthCompareOp = CompareOp.Always,
                StencilTestEnable = portalStencilRepopulate ? Vk.True : Vk.False,
                Front = stencilState,
                Back = stencilState,
            };
            PipelineColorBlendAttachmentState blend = new()
            {
                ColorWriteMask = colorWriteMask,
                BlendEnable = Vk.False,
                ColorBlendOp = BlendOp.Add,
                AlphaBlendOp = BlendOp.Add,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.Zero,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
            };
            PipelineColorBlendAttachmentState[] blends = new PipelineColorBlendAttachmentState[3];
            blends.AsSpan().Fill(blend);
            DynamicState[] dynamicStates = portalStencilRepopulate
                ? [DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference]
                : [DynamicState.Viewport, DynamicState.Scissor];
            using VulkanPipelineCompilationDependencyLease lease =
                manager.AcquireCompilationDependencyLease();
            PipelineShaderStageCreateInfo[] graphicsStages =
                [.. program.GetShaderStages(VulkanProgramUtilities.GraphicsStageMask)];
            PipelineShaderStageCreateInfo[] vertexStages =
                [.. program.GetShaderStages(EProgramStageMask.VertexShaderBit)];
            PipelineShaderStageCreateInfo[] fragmentStages =
                [.. program.GetShaderStages(EProgramStageMask.FragmentShaderBit)];
            VulkanGraphicsPipelineBuildRequest request = new(
                program.BindingId,
                program,
                program.MeshTaskProgramServices,
                useGraphicsPipelineLibraries:
                    RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
                    program.MeshTaskBackendContext.Supports(EVulkanDeviceCapability.GraphicsPipelineLibrary),
                lease.Generation,
                key,
                label,
                colorAttachmentCount,
                program.PipelineLayout,
                program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
                program.DescriptorHeapLayout is { Mappings.Length: > 0 } descriptorHeapLayout
                    ? [.. descriptorHeapLayout.Mappings]
                    : [],
                [],
                [],
                inputAssembly,
                1u,
                RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl,
                raster,
                samples,
                depth,
                blends,
                dynamicStates,
                target.UsesDynamicRendering ? default : target.RenderPass,
                target.UsesDynamicRendering ? target.DynamicRenderingFormats : default,
                graphicsStages,
                vertexStages,
                fragmentStages,
                false);
            try
            {
                pipeline = manager.StoreOrRetireSharedGraphicsPipeline(
                    key,
                    manager.CreateGraphicsPipelineFromRequest(
                        request,
                        manager.ActivePipelineCache,
                        backgroundCompile: false));
            }
            catch (Exception exception)
            {
                reason = exception.Message;
                return false;
            }
        }

        if (pipeline.Handle == 0UL)
        {
            reason = $"Vulkan returned a null {label} pipeline";
            return false;
        }

        prepared = new(program, program.LinkGeneration, pipeline,
            program.PipelineLayout, target);
        return true;
    }

    private static ulong ComputePassMetadataHash(
        in VulkanAdvancedVisibilityTargetClosure target,
        string label)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add(label);
        hash.Add(target.DepthStencilReadOnly ? 1UL : 0UL);
        return hash.Value;
    }

    private static ulong ComputeFeatureProfileHash(VkRenderProgram program)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add(RuntimeEngine.Rendering.Settings.ShaderConfigVersion);
        hash.Add(RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap);
        hash.Add(RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);
        hash.Add((int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
        hash.Add((int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
        hash.Add(program.MeshTaskBackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8));
        return hash.Value;
    }
}
