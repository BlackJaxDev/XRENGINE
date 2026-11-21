using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates the blended graphics pipeline that draws mesh surface decals into the decal buffer.
/// It shares the packed vertex ABI of the visibility raster. Both targets use premultiplied
/// "over" blending; the alpha channel accumulates coverage, so the cleared value (all zero)
/// means "no decal". The depth test uses the resolved
/// visibility depth without writes, with a slope-scaled bias toward the camera so a decal that
/// lies on its surface passes the test.
/// </summary>
internal static class VulkanDecalBufferPipelineFactory
{
    /// <summary>Constant depth bias in minimum resolvable depth units, toward the camera.</summary>
    internal const float DepthBiasConstant = 2.0f;

    /// <summary>Slope-scaled depth bias, toward the camera.</summary>
    internal const float DepthBiasSlope = 1.0f;

    private static readonly VulkanVisibilityVertexInputSnapshot s_vertexInput =
        VulkanCanonicalVisibilityPipelineFactory.VertexInput;

    internal static bool TryPrepare(
        VkRenderProgram program,
        uint cullMode,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        if (!target.IsValid || target.Kind != EVulkanAdvancedVisibilityTargetKind.DecalBuffer ||
            !program.IsLinked || program.PipelineLayout.Handle == 0UL)
        {
            reason = "decal buffer program or exact decal buffer closure is unavailable";
            return false;
        }
        if (!target.UsesDynamicRendering ||
            target.DynamicRenderingFormats.ColorAttachmentCount != 2u ||
            target.RasterizationSamples != SampleCountFlags.Count1Bit ||
            !target.DepthStencilReadOnly)
        {
            reason = "the decal buffer requires a single-sample dynamic-rendering target with two color attachments and read-only depth";
            return false;
        }
        if (!VulkanCanonicalVisibilityPipelineFactory.ValidateVertexInputs(program, out reason))
            return false;

        bool reversedDepth = target.ClearPolicy.ReversedDepth;
        CompareOp depthCompare = reversedDepth ? CompareOp.GreaterOrEqual : CompareOp.LessOrEqual;
        // A larger depth is nearer with reversed Z, a smaller one otherwise.
        float biasSign = reversedDepth ? 1.0f : -1.0f;
        CullModeFlags cull = cullMode == 0u ? CullModeFlags.None : CullModeFlags.BackBit;
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
            ComputePassMetadataHash(reversedDepth),
            ComputeFeatureProfileHash(program),
            SampleCountFlags.Count1Bit,
            DepthTestEnabled: true,
            DepthWriteEnabled: false,
            depthCompare,
            StencilTestEnabled: false,
            default,
            default,
            0u,
            cull,
            FrontFace.CounterClockwise,
            BlendEnabled: true,
            AlphaToCoverageEnabled: false,
            BlendOp.Add,
            BlendOp.Add,
            BlendFactor.One,
            BlendFactor.OneMinusSrcAlpha,
            BlendFactor.One,
            BlendFactor.OneMinusSrcAlpha,
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
                CullMode = cull,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f,
                DepthBiasEnable = Vk.True,
                DepthBiasConstantFactor = biasSign * DepthBiasConstant,
                DepthBiasSlopeFactor = biasSign * DepthBiasSlope,
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
                DepthWriteEnable = Vk.False,
                DepthCompareOp = depthCompare,
            };
            PipelineColorBlendAttachmentState blend = new()
            {
                ColorWriteMask = ColorComponentFlags.RBit |
                    ColorComponentFlags.GBit | ColorComponentFlags.BBit |
                    ColorComponentFlags.ABit,
                BlendEnable = Vk.True,
                ColorBlendOp = BlendOp.Add,
                AlphaBlendOp = BlendOp.Add,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            };
            PipelineColorBlendAttachmentState[] blends = [blend, blend];
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
                "AdvancedDecalBuffer",
                2u,
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
                blends,
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
            reason = "Vulkan returned a null decal buffer pipeline";
            return false;
        }

        prepared = new(
            program,
            program.LinkGeneration,
            pipeline,
            program.PipelineLayout,
            PrimitiveTopology.TriangleList,
            false,
            s_vertexInput,
            target);
        return true;
    }

    private static ulong ComputePassMetadataHash(bool reversedDepth)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("AdvancedDecalBuffer");
        hash.Add(reversedDepth ? 1UL : 0UL);
        hash.Add((ulong)BitConverter.SingleToUInt32Bits(DepthBiasConstant));
        hash.Add((ulong)BitConverter.SingleToUInt32Bits(DepthBiasSlope));
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
