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

    internal static bool TryPrepare(
        VkRenderProgram program,
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        uint cullMode,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
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
        if (colorAttachmentCount != 3u)
        {
            reason = $"visibility raster requires exactly three color attachments, received {colorAttachmentCount}";
            return false;
        }

        VulkanVisibilityVertexInputSnapshot vertexInput = meshlet
            ? default
            : s_vertexInput;
        if (!meshlet && !ValidateVertexInputs(program, out reason))
            return false;

        bool depthWrite = !target.DepthStencilReadOnly;
        CullModeFlags cull = cullMode == 0u
            ? CullModeFlags.None
            : CullModeFlags.BackBit;
        VulkanGraphicsPipelineKey key = new(
            PrimitiveTopology.TriangleList,
            target.UsesDynamicRendering,
            target.UsesDynamicRendering ? 0UL : target.RenderPass.Handle,
            target.DynamicRenderingFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.LinkGeneration,
            meshlet ? 0UL : vertexInput.LayoutHash,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            ComputePassMetadataHash(target),
            ComputeFeatureProfileHash(program),
            target.RasterizationSamples,
            DepthTestEnabled: true,
            DepthWriteEnabled: depthWrite,
            CompareOp.LessOrEqual,
            StencilTestEnabled: false,
            default,
            default,
            0u,
            cull,
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
                CullMode = cull,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f,
            };
            PipelineMultisampleStateCreateInfo samples = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = target.RasterizationSamples,
            };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Vk.True,
                DepthWriteEnable = depthWrite ? Vk.True : Vk.False,
                DepthCompareOp = CompareOp.LessOrEqual,
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
                meshlet ? [] : [.. vertexInput.Bindings],
                meshlet ? [] : [.. vertexInput.Attributes],
                inputAssembly,
                1u,
                RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl,
                raster,
                samples,
                depth,
                blends,
                [DynamicState.Viewport, DynamicState.Scissor],
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

    private static bool ValidateVertexInputs(
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
        in VulkanAdvancedVisibilityTargetClosure target)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("AdvancedVisibilityRaster");
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
        hash.Add(program.MeshTaskBackendContext.Supports(
            EVulkanDeviceCapability.IndexTypeUint8));
        return hash.Value;
    }
}
