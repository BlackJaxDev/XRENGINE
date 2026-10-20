using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates the depth-only graphics pipeline the directional shadow lane uses to
/// draw the family's canonical bins into an atlas page. It shares the packed
/// vertex ABI of the visibility raster and, like the generic layered shadow
/// pass, never culls faces so thin casters shadow from both sides.
/// </summary>
internal static class VulkanDirectionalShadowPipelineFactory
{
    private static readonly VulkanVisibilityVertexInputSnapshot s_vertexInput =
        VulkanCanonicalVisibilityPipelineFactory.VertexInput;

    internal static bool TryPrepare(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        if (!target.IsValid || target.Kind != EVulkanAdvancedVisibilityTargetKind.DirectionalShadow ||
            !program.IsLinked || program.PipelineLayout.Handle == 0UL)
        {
            reason = "directional shadow program or exact atlas-page closure is unavailable";
            return false;
        }
        if (!target.UsesDynamicRendering)
        {
            reason = "the directional shadow lane requires a dynamic-rendering atlas page";
            return false;
        }
        if (target.DynamicRenderingFormats.ColorAttachmentCount != 0u ||
            target.DynamicRenderingFormats.DepthAttachmentFormat == Format.Undefined)
        {
            reason = "the directional shadow lane requires a depth-only atlas page";
            return false;
        }
        if (target.RasterizationSamples != SampleCountFlags.Count1Bit)
        {
            reason = "the directional shadow lane does not rasterize multisampled atlas pages";
            return false;
        }
        if (!VulkanCanonicalVisibilityPipelineFactory.ValidateVertexInputs(program, out reason))
            return false;

        CompareOp depthCompare = target.ClearPolicy.ReversedDepth
            ? CompareOp.GreaterOrEqual
            : CompareOp.LessOrEqual;
        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList,
            true,
            0UL,
            target.DynamicRenderingFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.InterfaceGeneration,
            s_vertexInput.LayoutHash,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
            ComputePassMetadataHash(target),
            ComputeFeatureProfileHash(program),
            SampleCountFlags.Count1Bit,
            DepthTestEnabled: true,
            DepthWriteEnabled: true,
            depthCompare,
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
            ColorComponentFlags.None,
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
                DepthCompareOp = depthCompare,
            };
            using VulkanPipelineCompilationDependencyLease lease =
                manager.AcquireCompilationDependencyLease();
            PipelineShaderStageCreateInfo[] graphicsStages =
                [.. program.GetShaderStages(VulkanProgramUtilities.GraphicsStageMask)];
            PipelineShaderStageCreateInfo[] preRasterStages =
                [.. program.GetShaderStages(EProgramStageMask.VertexShaderBit)];
            PipelineShaderStageCreateInfo[] fragmentStages =
                [.. program.GetShaderStages(EProgramStageMask.FragmentShaderBit)];
            VulkanGraphicsPipelineBuildRequest request = new(
                program.BindingId,
                program,
                program.MeshTaskProgramServices,
                useGraphicsPipelineLibraries:
                    RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
                    program.MeshTaskBackendContext.Supports(
                        EVulkanDeviceCapability.GraphicsPipelineLibrary),
                lease.Generation,
                key,
                "AdvancedDirectionalShadowRaster",
                0u,
                program.PipelineLayout,
                program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
                program.DescriptorHeapLayout is { Mappings.Length: > 0 } descriptorHeapLayout
                    ? [.. descriptorHeapLayout.Mappings]
                    : [],
                [.. s_vertexInput.Bindings],
                [.. s_vertexInput.Attributes],
                inputAssembly,
                1u,
                RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl,
                raster,
                samples,
                depth,
                [],
                [DynamicState.Viewport, DynamicState.Scissor],
                default,
                target.DynamicRenderingFormats,
                graphicsStages,
                preRasterStages,
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
            reason = "Vulkan returned a null directional shadow pipeline";
            return false;
        }

        prepared = new(
            program,
            program.LinkGeneration,
            pipeline,
            program.PipelineLayout,
            PrimitiveTopology.TriangleList,
            IsMeshShaderPipeline: false,
            s_vertexInput,
            target);
        return true;
    }

    private static ulong ComputePassMetadataHash(
        in VulkanAdvancedVisibilityTargetClosure target)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("AdvancedDirectionalShadowRaster");
        hash.Add(target.ClearPolicy.ReversedDepth ? 1UL : 0UL);
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
        hash.Add(program.MeshTaskBackendContext.Supports(
            EVulkanDeviceCapability.IndexTypeUint8));
        return hash.Value;
    }
}
