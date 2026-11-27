using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Creates the Vulkan graphics pipelines used to carve portal stencil masks and reset
/// depth and visibility targets on carved portal pixels. Both pipelines rely only on
/// Vulkan 1.0 core dynamic stencil states (<see cref="DynamicState.StencilReference"/>
/// and <see cref="DynamicState.StencilWriteMask"/>).
/// </summary>
internal static class VulkanPortalQuadPipelineFactory
{
    private enum EPortalQuadMode : byte
    {
        StencilCarve = 0,
        DepthReset = 1,
        SkyUnwind = 2,
        GlassComposite = 3,
        DepthOnlyReset = 4,
        TransparentDepthRestore = 5,
        TransparentGlassComposite = 6,
    }

    /// <summary>Stencil contract for reflected sky draws: match portal node bits 2..6 without modifying stencil.</summary>
    internal static StencilOpState PortalSkyUnwindStencilState => new()
    {
        FailOp = StencilOp.Keep,
        PassOp = StencilOp.Keep,
        DepthFailOp = StencilOp.Keep,
        CompareOp = CompareOp.Equal,
        CompareMask = AdvancedStencilBitLayout.PortalNodeMask,
        WriteMask = 0u,
        Reference = 0u,
    };

    /// <summary>
    /// Stencil contract for mirror glass composite and unwind: match portal node bits 2..6 and
    /// invert the dynamic XOR transition bits to restore the parent node ID while preserving bits 0..1 and bit 7.
    /// </summary>
    internal static StencilOpState PortalGlassCompositeStencilState => new()
    {
        FailOp = StencilOp.Keep,
        PassOp = StencilOp.Invert,
        DepthFailOp = StencilOp.Keep,
        CompareOp = CompareOp.Equal,
        CompareMask = AdvancedStencilBitLayout.PortalNodeMask,
        WriteMask = AdvancedStencilBitLayout.PortalNodeMask,
        Reference = 0u,
    };

    /// <summary>
    /// Prepares the stencil-only portal quad carve pipeline. It compares stencil bits 2..6
    /// against the parent node ID and uses <see cref="StencilOp.Invert"/> with the dynamic
    /// XOR transition write mask to replace the parent node ID with the child node ID in
    /// one draw call while preserving bits 0..1 and bit 7.
    /// </summary>
    internal static bool TryPrepareStencilCarve(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.StencilCarve,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the depth and visibility reset pipeline for carved portal pixels. It writes
    /// far depth and uncovered background visibility metadata stamped with the portal view
    /// index wherever stencil bits 2..6 match the dynamic child node reference.
    /// </summary>
    internal static bool TryPrepareDepthReset(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.DepthReset,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the depth-only reset pipeline for transparent-background carved portal pixels.
    /// Writes far depth wherever stencil bits 2..6 match the child node reference while keeping
    /// all visibility color attachments untouched so uncovered pixels preserve the live scene.
    /// </summary>
    internal static bool TryPrepareDepthOnlyReset(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.DepthOnlyReset,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the depth-only restore pipeline for uncovered transparent-background mirror pixels.
    /// Reads primary visibility identity and metadata in a zero-color-attachment dynamic rendering
    /// pass and writes reconstructed primary depth wherever stencil bits 2..6 match the portal node.
    /// </summary>
    internal static bool TryPrepareTransparentDepthRestore(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.TransparentDepthRestore,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the reflected sky unwind pipeline on <c>ForwardPassFBO</c>. Draws a fullscreen
    /// triangle at far depth (<see cref="CompareOp.Equal"/>, depth write disabled) masked to
    /// stencil bits 2..6 and writes reflected sky radiance to color attachment 0 only.
    /// </summary>
    internal static bool TryPrepareSkyUnwind(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.SkyUnwind,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the deepest-out mirror glass composite and unwind pipeline on <c>ForwardPassFBO</c>.
    /// Alpha-blends tint, Fresnel, and directional specular into color attachment 0, restores real
    /// mirror plane depth (<see cref="CompareOp.Always"/>, depth write enabled), and unwinds stencil
    /// bits 2..6 to the parent node ID via <see cref="StencilOp.Invert"/>.
    /// </summary>
    internal static bool TryPrepareGlassComposite(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.GlassComposite,
            out prepared,
            out reason);

    /// <summary>
    /// Prepares the transparent-mirror unwind pipeline on <c>ForwardPassFBO</c>.
    /// Alpha-blends optional glass tint and unwinds stencil bits 2..6 to the parent node ID
    /// without overwriting restored primary or reflected hardware depth.
    /// </summary>
    internal static bool TryPrepareTransparentGlassComposite(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
        => TryPrepareCore(
            program,
            in target,
            EPortalQuadMode.TransparentGlassComposite,
            out prepared,
            out reason);

    private static bool TryPrepareCore(
        VkRenderProgram program,
        in VulkanAdvancedVisibilityTargetClosure target,
        EPortalQuadMode mode,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
    {
        prepared = default;
        reason = "Ready";
        if (!target.IsValid || !program.IsLinked || program.PipelineLayout.Handle == 0UL)
        {
            reason = "portal quad program or exact target closure is unavailable";
            return false;
        }

        bool isUnwindPass = mode is EPortalQuadMode.SkyUnwind or EPortalQuadMode.GlassComposite or EPortalQuadMode.TransparentGlassComposite;
        bool isDepthReset = mode == EPortalQuadMode.DepthReset;
        bool isDepthOnlyReset = mode == EPortalQuadMode.DepthOnlyReset;
        bool isTransparentDepthRestore = mode == EPortalQuadMode.TransparentDepthRestore;
        bool isAnyDepthReset = isDepthReset || isDepthOnlyReset;
        bool isSkyUnwind = mode == EPortalQuadMode.SkyUnwind;
        bool isGlassComposite = mode == EPortalQuadMode.GlassComposite;
        bool isTransparentGlassComposite = mode == EPortalQuadMode.TransparentGlassComposite;
        bool isAnyGlassComposite = isGlassComposite || isTransparentGlassComposite;

        DynamicRenderingFormatSignature pipelineFormats = target.DynamicRenderingFormats;
        uint colorAttachmentCount;
        if (isTransparentDepthRestore)
        {
            if (!target.UsesDynamicRendering)
            {
                reason = "transparent portal depth restore requires dynamic rendering";
                return false;
            }
            pipelineFormats = new DynamicRenderingFormatSignature(
                ReadOnlySpan<Format>.Empty,
                target.DynamicRenderingFormats.DepthAttachmentFormat,
                target.DynamicRenderingFormats.StencilAttachmentFormat,
                target.DynamicRenderingFormats.ViewMask,
                target.DynamicRenderingFormats.LayerCount);
            colorAttachmentCount = 0u;
        }
        else
        {
            colorAttachmentCount = target.UsesDynamicRendering
                ? target.DynamicRenderingFormats.ColorAttachmentCount
                : program.MeshTaskProgramServices.GetRenderPassColorAttachmentCount(target.RenderPass);
            if (isUnwindPass)
            {
                if (target.Kind != EVulkanAdvancedVisibilityTargetKind.PortalUnwind || colorAttachmentCount == 0u)
                {
                    reason = $"portal unwind pass requires a PortalUnwind target closure with at least 1 color attachment; received kind={target.Kind}, colors={colorAttachmentCount}";
                    return false;
                }
            }
            else
            {
                uint expectedColorAttachmentCount = target.RasterizationSamples == SampleCountFlags.Count1Bit
                    ? 3u
                    : 4u;
                bool omittedSelection = target.RasterizationSamples == SampleCountFlags.Count1Bit &&
                    colorAttachmentCount == 2u;
                if (colorAttachmentCount != expectedColorAttachmentCount && !omittedSelection)
                {
                    reason = $"portal quad pass requires {expectedColorAttachmentCount} color attachments for {target.RasterizationSamples}, received {colorAttachmentCount}";
                    return false;
                }
            }
        }

        bool usesMultisampleRaster = target.RasterizationSamples != SampleCountFlags.Count1Bit;
        bool needsSampleRateShading = (isDepthReset || isTransparentDepthRestore) && usesMultisampleRaster;
        if (needsSampleRateShading &&
            !program.MeshTaskBackendContext.DeviceContext.SampleRateShadingEnabled)
        {
            reason = "multisample portal depth pass requires the Vulkan sampleRateShading feature; the device did not enable it";
            return false;
        }

        PrimitiveTopology topology = isAnyDepthReset || isTransparentDepthRestore || isSkyUnwind
            ? PrimitiveTopology.TriangleList
            : PrimitiveTopology.TriangleStrip;
        CompareOp depthCompare = mode switch
        {
            EPortalQuadMode.DepthReset or EPortalQuadMode.DepthOnlyReset or EPortalQuadMode.TransparentDepthRestore or EPortalQuadMode.GlassComposite or EPortalQuadMode.TransparentGlassComposite => CompareOp.Always,
            EPortalQuadMode.SkyUnwind => CompareOp.Equal,
            _ => target.ClearPolicy.ReversedDepth ? CompareOp.GreaterOrEqual : CompareOp.LessOrEqual,
        };
        bool depthWrite = (isAnyDepthReset || isTransparentDepthRestore || isGlassComposite) &&
            !target.DepthStencilReadOnly;
        StencilOpState stencilState = mode switch
        {
            EPortalQuadMode.DepthReset or EPortalQuadMode.DepthOnlyReset or EPortalQuadMode.TransparentDepthRestore => new StencilOpState
            {
                FailOp = StencilOp.Keep,
                PassOp = StencilOp.Keep,
                DepthFailOp = StencilOp.Keep,
                CompareOp = CompareOp.Equal,
                CompareMask = AdvancedStencilBitLayout.PortalNodeMask,
                WriteMask = 0u,
                Reference = 0u,
            },
            EPortalQuadMode.SkyUnwind => PortalSkyUnwindStencilState,
            EPortalQuadMode.GlassComposite or EPortalQuadMode.TransparentGlassComposite => PortalGlassCompositeStencilState,
            _ => new StencilOpState
            {
                FailOp = StencilOp.Keep,
                PassOp = StencilOp.Invert,
                DepthFailOp = StencilOp.Keep,
                CompareOp = CompareOp.Equal,
                CompareMask = AdvancedStencilBitLayout.PortalNodeMask,
                WriteMask = AdvancedStencilBitLayout.PortalNodeMask,
                Reference = 0u,
            },
        };
        bool writesDynamicStencilMask = mode is EPortalQuadMode.StencilCarve or EPortalQuadMode.GlassComposite or EPortalQuadMode.TransparentGlassComposite;
        ColorComponentFlags primaryColorWriteMask = mode is EPortalQuadMode.StencilCarve or EPortalQuadMode.DepthOnlyReset or EPortalQuadMode.TransparentDepthRestore
            ? 0
            : ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        bool blendEnabled = isAnyGlassComposite;
        BlendFactor srcColorFactor = blendEnabled ? BlendFactor.SrcAlpha : BlendFactor.One;
        BlendFactor dstColorFactor = blendEnabled ? BlendFactor.OneMinusSrcAlpha : BlendFactor.Zero;
        BlendFactor srcAlphaFactor = blendEnabled ? BlendFactor.Zero : BlendFactor.One;
        BlendFactor dstAlphaFactor = blendEnabled ? BlendFactor.One : BlendFactor.Zero;

        VulkanGraphicsPipelineKey key = new(
            topology,
            target.UsesDynamicRendering,
            target.UsesDynamicRendering ? 0UL : target.RenderPass.Handle,
            pipelineFormats,
            program.ComputeGraphicsPipelineFingerprint(),
            program.InterfaceGeneration,
            0UL,
            program.DescriptorSchemaFingerprint,
            program.PipelineLayout.Handle,
            program.MeshTaskBackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap,
            ComputePassMetadataHash(target, mode),
            ComputeFeatureProfileHash(program),
            target.RasterizationSamples,
            DepthTestEnabled: true,
            DepthWriteEnabled: depthWrite,
            depthCompare,
            StencilTestEnabled: true,
            stencilState,
            stencilState,
            writesDynamicStencilMask ? AdvancedStencilBitLayout.PortalNodeMask : 0u,
            CullModeFlags.None,
            FrontFace.CounterClockwise,
            BlendEnabled: blendEnabled,
            AlphaToCoverageEnabled: false,
            BlendOp.Add,
            BlendOp.Add,
            srcColorFactor,
            dstColorFactor,
            srcAlphaFactor,
            dstAlphaFactor,
            primaryColorWriteMask,
            1u,
            RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);

        VulkanPipelineManager manager = program.MeshTaskBackendContext.Resources.PipelineManager;
        if (!manager.TryGetSharedGraphicsPipeline(key, out Pipeline pipeline))
        {
            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = topology,
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
                RasterizationSamples = target.RasterizationSamples,
                SampleShadingEnable = needsSampleRateShading ? Vk.True : Vk.False,
                MinSampleShading = needsSampleRateShading ? 1.0f : 0.0f,
            };
            PipelineDepthStencilStateCreateInfo depth = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = Vk.True,
                DepthWriteEnable = depthWrite ? Vk.True : Vk.False,
                DepthCompareOp = depthCompare,
                StencilTestEnable = Vk.True,
                Front = stencilState,
                Back = stencilState,
            };
            PipelineColorBlendAttachmentState primaryBlend = new()
            {
                ColorWriteMask = primaryColorWriteMask,
                BlendEnable = blendEnabled ? Vk.True : Vk.False,
                ColorBlendOp = BlendOp.Add,
                AlphaBlendOp = BlendOp.Add,
                SrcColorBlendFactor = srcColorFactor,
                DstColorBlendFactor = dstColorFactor,
                SrcAlphaBlendFactor = srcAlphaFactor,
                DstAlphaBlendFactor = dstAlphaFactor,
            };
            PipelineColorBlendAttachmentState[] blends =
                new PipelineColorBlendAttachmentState[colorAttachmentCount];
            if (isUnwindPass)
            {
                // ForwardPassFBO may carry auxiliary editor highlight attachments at indices 1..N-1.
                // Portal unwind writes HDR color attachment 0 only and leaves auxiliary attachments intact.
                blends[0] = primaryBlend;
                for (int attachment = 1; attachment < blends.Length; ++attachment)
                {
                    blends[attachment] = new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = 0,
                        BlendEnable = Vk.False,
                    };
                }
            }
            else
            {
                blends.AsSpan().Fill(primaryBlend);
            }
            DynamicState[] dynamicStates = writesDynamicStencilMask
                ? [DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference, DynamicState.StencilWriteMask]
                : [DynamicState.Viewport, DynamicState.Scissor, DynamicState.StencilReference];

            string debugLabel = mode switch
            {
                EPortalQuadMode.DepthReset => "PortalDepthReset",
                EPortalQuadMode.DepthOnlyReset => "PortalDepthOnlyReset",
                EPortalQuadMode.TransparentDepthRestore => "PortalTransparentDepthRestore",
                EPortalQuadMode.SkyUnwind => "PortalSkyUnwind",
                EPortalQuadMode.GlassComposite => "PortalGlassComposite",
                EPortalQuadMode.TransparentGlassComposite => "PortalTransparentGlassComposite",
                _ => "PortalStencilCarve",
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
                debugLabel,
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
                target.UsesDynamicRendering ? pipelineFormats : default,
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
            reason = $"Vulkan returned a null portal quad pipeline ({mode})";
            return false;
        }

        prepared = new VulkanVisibilityRasterPipeline(
            program,
            program.LinkGeneration,
            pipeline,
            program.PipelineLayout,
            topology,
            false,
            default,
            target,
            HasNoVertexInput: true);
        return true;
    }

    private static ulong ComputePassMetadataHash(
        in VulkanAdvancedVisibilityTargetClosure target,
        EPortalQuadMode mode)
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add(mode switch
        {
            EPortalQuadMode.DepthReset => "AdvancedPortalDepthReset",
            EPortalQuadMode.DepthOnlyReset => "AdvancedPortalDepthOnlyReset",
            EPortalQuadMode.TransparentDepthRestore => "AdvancedPortalTransparentDepthRestore",
            EPortalQuadMode.SkyUnwind => "AdvancedPortalSkyUnwind",
            EPortalQuadMode.GlassComposite => "AdvancedPortalGlassComposite",
            EPortalQuadMode.TransparentGlassComposite => "AdvancedPortalTransparentGlassComposite",
            _ => "AdvancedPortalStencilCarve",
        });
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
