using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates the visibility-only graphics pipeline from sealed canonical state.
/// This intentionally bypasses <see cref="VkMeshRenderer"/>: resident scene
/// records, rather than a mutable renderer wrapper, own the visibility ABI.
/// </summary>
internal static class VulkanCanonicalVisibilityPipelineFactory
{
    private static readonly VulkanVisibilityVertexInputSnapshot s_vertexInput =
        CreateVertexInput();

    /// <summary>Packed canonical vertex ABI shared by every lane that draws the geometry atlas.</summary>
    internal static VulkanVisibilityVertexInputSnapshot VertexInput => s_vertexInput;

    internal static bool TryPrepare(
        VkRenderProgram program,
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        uint cullMode,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason,
        EVulkanVisibilityPortalRasterMode portalMode = EVulkanVisibilityPortalRasterMode.None)
    {
        prepared = default;
        reason = "Ready";
        if (!target.IsValid || !program.IsLinked ||
            program.PipelineLayout.Handle == 0UL)
        {
            reason = "visibility program or exact render-target closure is unavailable";
            return false;
        }

        uint colorAttachmentCount = target.UsesDynamicRendering
            ? target.DynamicRenderingFormats.ColorAttachmentCount
            : program.MeshTaskProgramServices.GetRenderPassColorAttachmentCount(
                target.RenderPass);
        // Single-sample targets carry identity, metadata and an optional selection image
        // (omitted for OpenXR eyes); multisample targets add sample positions.
        uint expectedColorAttachmentCount = target.RasterizationSamples == SampleCountFlags.Count1Bit
            ? 3u
            : 4u;
        bool omittedSelection = target.RasterizationSamples == SampleCountFlags.Count1Bit &&
            colorAttachmentCount == 2u;
        if (colorAttachmentCount != expectedColorAttachmentCount && !omittedSelection)
        {
            reason = $"visibility raster requires exactly {expectedColorAttachmentCount} color attachments for {target.RasterizationSamples}, received {colorAttachmentCount}";
            return false;
        }
        bool usesMultisampleRaster = target.RasterizationSamples != SampleCountFlags.Count1Bit;
        if (usesMultisampleRaster && !program.MeshTaskBackendContext.DeviceContext.SampleRateShadingEnabled)
        {
            reason = "multisample visibility requires the Vulkan sampleRateShading feature; the device did not enable it";
            return false;
        }

        VulkanVisibilityVertexInputSnapshot vertexInput = meshlet
            ? default
            : s_vertexInput;
        if (!meshlet && !ValidateVertexInputs(program, out reason))
            return false;

        bool depthWrite = !target.DepthStencilReadOnly;
        CompareOp depthCompare = target.ClearPolicy.ReversedDepth
            ? CompareOp.GreaterOrEqual
            : CompareOp.LessOrEqual;
        CullModeFlags cull = (ECullMode)cullMode switch
        {
            ECullMode.None => CullModeFlags.None,
            ECullMode.Front => CullModeFlags.FrontBit,
            ECullMode.Both => CullModeFlags.FrontAndBack,
            _ => CullModeFlags.BackBit,
        };
        bool isPortal = portalMode != EVulkanVisibilityPortalRasterMode.None;
        FrontFace frontFace = portalMode == EVulkanVisibilityPortalRasterMode.InvertedWinding
            ? FrontFace.Clockwise
            : FrontFace.CounterClockwise;
        StencilOpState portalStencil = isPortal
            ? new StencilOpState
            {
                FailOp = StencilOp.Keep,
                PassOp = StencilOp.Keep,
                DepthFailOp = StencilOp.Keep,
                CompareOp = CompareOp.Equal,
                CompareMask = AdvancedStencilBitLayout.PortalNodeMask,
                WriteMask = 0u,
                Reference = 0u,
            }
            : default;
        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList,
            target.UsesDynamicRendering,
            target.UsesDynamicRendering ? 0UL : target.RenderPass.Handle,
            target.DynamicRenderingFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.InterfaceGeneration,
            meshlet ? 0UL : vertexInput.LayoutHash,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
            ComputePassMetadataHash(target, portalMode),
            ComputeFeatureProfileHash(program),
            target.RasterizationSamples,
            DepthTestEnabled: true,
            DepthWriteEnabled: depthWrite,
            depthCompare,
            StencilTestEnabled: isPortal,
            portalStencil,
            portalStencil,
            0u,
            cull,
            frontFace,
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
                CullMode = cull,
                FrontFace = frontFace,
                LineWidth = 1.0f,
            };
            PipelineMultisampleStateCreateInfo samples = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = target.RasterizationSamples,
                SampleShadingEnable = usesMultisampleRaster ? Vk.True : Vk.False,
                MinSampleShading = usesMultisampleRaster ? 1.0f : 0.0f,
            };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Vk.True,
                DepthWriteEnable = depthWrite ? Vk.True : Vk.False,
                DepthCompareOp = depthCompare,
                StencilTestEnable = isPortal ? Vk.True : Vk.False,
                Front = portalStencil,
                Back = portalStencil,
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
            PipelineColorBlendAttachmentState[] blends =
                new PipelineColorBlendAttachmentState[colorAttachmentCount];
            blends.AsSpan().Fill(blend);
            using VulkanPipelineCompilationDependencyLease lease =
                manager.AcquireCompilationDependencyLease();
            PipelineShaderStageCreateInfo[] graphicsStages =
                [.. program.GetShaderStages(VulkanProgramUtilities.GraphicsStageMask)];
            PipelineShaderStageCreateInfo[] preRasterStages = meshlet
                ? [.. program.GetShaderStages(EProgramStageMask.TaskShaderBit |
                    EProgramStageMask.MeshShaderBit)]
                : [.. program.GetShaderStages(EProgramStageMask.VertexShaderBit)];
            PipelineShaderStageCreateInfo[] fragmentStages =
                [.. program.GetShaderStages(EProgramStageMask.FragmentShaderBit)];
            VulkanGraphicsPipelineBuildRequest request = new(
                program.BindingId,
                program,
                program.MeshTaskProgramServices,
                useGraphicsPipelineLibraries: !meshlet &&
                    RuntimeEngine.Rendering.Settings.AllowShaderPipelines &&
                    program.MeshTaskBackendContext.Supports(
                        EVulkanDeviceCapability.GraphicsPipelineLibrary),
                lease.Generation,
                key,
                meshlet ? "CanonicalVisibilityMesh" : "CanonicalVisibilityRaster",
                colorAttachmentCount,
                program.PipelineLayout,
                program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
                program.DescriptorHeapLayout is { Mappings.Length: > 0 } descriptorHeapLayout
                    ? [.. descriptorHeapLayout.Mappings]
                    : [],
                meshlet ? [] : [.. vertexInput.Bindings],
                meshlet ? [] : [.. vertexInput.Attributes],
                inputAssembly,
                1u,
                RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl,
                raster,
                samples,
                depth,
                blends,
                isPortal
                    ? [DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference]
                    : [DynamicState.Viewport, DynamicState.Scissor],
                target.UsesDynamicRendering ? default : target.RenderPass,
                target.UsesDynamicRendering
                    ? target.DynamicRenderingFormats
                    : default,
                graphicsStages,
                preRasterStages,
                fragmentStages,
                meshlet);
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
            reason = "Vulkan returned a null canonical visibility pipeline";
            return false;
        }

        prepared = new(
            program,
            program.LinkGeneration,
            pipeline,
            program.PipelineLayout,
            PrimitiveTopology.TriangleList,
            meshlet,
            vertexInput,
            target);
        return true;
    }

    internal static bool ValidateVertexInputs(
        VkRenderProgram program,
        out string reason)
    {
        if (program.TryGetVertexStageInputCount(out int count) && count == 2 &&
            program.TryGetVertexInputLocation("Position", out uint position) &&
            program.TryGetVertexInputLocation("TexCoord0", out uint uv) &&
            position == 0u && uv == 1u)
        {
            reason = "Ready";
            return true;
        }

        reason = "canonical packed visibility requires Position@0 and TexCoord0@1";
        return false;
    }

    private static VulkanVisibilityVertexInputSnapshot CreateVertexInput()
        => new(
            [new VertexInputBindingDescription
            {
                Binding = 0u, Stride = 64u, InputRate = VertexInputRate.Vertex,
            }],
            [new VertexInputAttributeDescription
            {
                Location = 0u, Binding = 0u, Format = Format.R32G32B32Sfloat, Offset = 0u,
            }, new VertexInputAttributeDescription
            {
                Location = 1u, Binding = 0u, Format = Format.R16G16Sfloat, Offset = 20u,
            }],
            ComputeVertexLayoutHash());

    private static ulong ComputeVertexLayoutHash()
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("CanonicalAdvancedGeometry.PackedVertex64");
        hash.Add(0u);
        return hash.Value;
    }

    private static ulong ComputePassMetadataHash(
        in VulkanAdvancedVisibilityTargetClosure target,
        EVulkanVisibilityPortalRasterMode portalMode)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("AdvancedVisibilityRaster");
        hash.Add(target.DepthStencilReadOnly ? 1UL : 0UL);
        if (portalMode != EVulkanVisibilityPortalRasterMode.None)
            hash.Add((ulong)portalMode);
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
