using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Creates the fullscreen graphics pipeline that resolves raw visibility samples.</summary>
internal static class VulkanAdvancedMsaaResolvePipelineFactory
{
    internal static bool TryPrepare(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
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

        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList,
            target.UsesDynamicRendering,
            target.UsesDynamicRendering ? 0UL : target.RenderPass.Handle,
            target.DynamicRenderingFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.LinkGeneration,
            0UL,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
            ComputePassMetadataHash(in target),
            ComputeFeatureProfileHash(program),
            SampleCountFlags.Count1Bit,
            DepthTestEnabled: true,
            DepthWriteEnabled: true,
            CompareOp.Always,
            StencilTestEnabled: false,
            default,
            default,
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
            ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
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
                DepthTestEnable = Vk.True,
                DepthWriteEnable = Vk.True,
                DepthCompareOp = CompareOp.Always,
            };
            PipelineColorBlendAttachmentState blend = new()
            {
                ColorWriteMask = ColorComponentFlags.RBit |
                    ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                    ColorComponentFlags.ABit,
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
                "AdvancedMsaaVisibilityResolve",
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
                [DynamicState.Viewport, DynamicState.Scissor],
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
            reason = "Vulkan returned a null MSAA visibility resolve pipeline";
            return false;
        }

        prepared = new(program, program.LinkGeneration, pipeline,
            program.PipelineLayout, target);
        return true;
    }

    private static ulong ComputePassMetadataHash(
        in VulkanAdvancedVisibilityTargetClosure target)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("AdvancedMsaaVisibilityResolve");
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
