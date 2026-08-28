using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private static readonly VulkanVisibilityVertexInputSnapshot
        s_canonicalGeometryVisibilityVertexInput =
            CreateCanonicalGeometryVisibilityVertexInput();

    /// <summary>
    /// Prewarms a graphics pipeline for the dedicated visibility program while
    /// preserving this renderer's ordinary material-program and vertex-input
    /// cache. Only triangle-list source primitives are admitted because the
    /// visibility fragment ABI encodes triangle primitive IDs.
    /// </summary>
    internal bool TryPrepareVisibilityRasterPipeline(
        VkRenderProgram visibilityProgram,
        PrimitiveTopology topology,
        in PendingMeshDraw sourceDraw,
        bool useCanonicalGeometryVertexInput,
        bool meshlet,
        in VulkanAdvancedVisibilityTargetClosure targetClosure,
        out VulkanVisibilityRasterPipeline prepared,
        out string reason)
    {
        prepared = default;
        if (!_recordDrawSync.TryEnter())
        {
            reason = "renderer recording state is busy";
            return false;
        }

        try
        {
            if (!targetClosure.IsValid ||
                visibilityProgram is not { IsLinked: true } ||
                visibilityProgram.PipelineLayout.Handle == 0UL)
            {
                reason = "visibility program or exact render-target closure is unavailable";
                return false;
            }
            if (topology != PrimitiveTopology.TriangleList)
            {
                reason = $"visibility raster does not support topology '{topology}'";
                return false;
            }
            if (sourceDraw.BlendEnabled || sourceDraw.AlphaToCoverageEnabled ||
                targetClosure.DepthStencilReadOnly && sourceDraw.DepthWriteEnabled)
            {
                reason = "visibility raster requires an opaque writable-depth source draw";
                return false;
            }
            VulkanVisibilityVertexInputSnapshot vertexInput = default;
            if (!meshlet && !(useCanonicalGeometryVertexInput
                    ? TryCaptureCanonicalGeometryVisibilityVertexInput(
                        visibilityProgram,
                        out vertexInput,
                        out reason)
                    : TryCaptureVisibilityVertexInput(
                        visibilityProgram,
                        out vertexInput,
                        out reason)))
                return false;

            PendingMeshDraw draw = sourceDraw with
            {
                RasterizationSamples = targetClosure.RasterizationSamples,
                BlendEnabled = false,
                AlphaToCoverageEnabled = false,
                ColorBlendOp = BlendOp.Add,
                AlphaBlendOp = BlendOp.Add,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.Zero,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                ColorWriteMask = ColorComponentFlags.RBit |
                    ColorComponentFlags.GBit |
                    ColorComponentFlags.BBit |
                    ColorComponentFlags.ABit,
            };
            if (targetClosure.DepthStencilReadOnly)
                draw = draw with { DepthWriteEnabled = false };

            bool useDynamicRendering = targetClosure.UsesDynamicRendering;
            uint colorAttachmentCount = useDynamicRendering
                ? targetClosure.DynamicRenderingFormats.ColorAttachmentCount
                : ProgramCreationPort.GetRenderPassColorAttachmentCount(targetClosure.RenderPass);
            if (colorAttachmentCount != 3u)
            {
                reason = $"visibility raster requires exactly three color attachments, received {colorAttachmentCount}";
                return false;
            }

            ulong programPipelineHash = visibilityProgram.ComputeGraphicsPipelineFingerprint();
            ulong descriptorLayoutHash = visibilityProgram.DescriptorSchemaFingerprint;
            ulong featureProfileHash = ComputeVisibilityFeatureProfileHash();
            VulkanStableHash64 passMetadata = new(schemaVersion: 1);
            passMetadata.Add("AdvancedVisibilityRaster");
            passMetadata.Add(targetClosure.DepthStencilReadOnly ? 1UL : 0UL);
            VulkanGraphicsPipelineKey key = new(
                topology,
                useDynamicRendering,
                useDynamicRendering ? 0UL : targetClosure.RenderPass.Handle,
                useDynamicRendering ? targetClosure.DynamicRenderingFormats : default,
                programPipelineHash,
                visibilityProgram.LinkGeneration,
                meshlet ? 0UL : vertexInput.LayoutHash,
                descriptorLayoutHash,
                visibilityProgram.PipelineLayout.Handle,
                passMetadata.Value,
                featureProfileHash,
                draw.RasterizationSamples,
                draw.DepthTestEnabled,
                draw.DepthWriteEnabled,
                draw.DepthCompareOp,
                draw.StencilTestEnabled,
                draw.FrontStencilState,
                draw.BackStencilState,
                draw.StencilWriteMask,
                draw.CullMode,
                draw.FrontFace,
                false,
                false,
                draw.ColorBlendOp,
                draw.AlphaBlendOp,
                draw.SrcColorBlendFactor,
                draw.DstColorBlendFactor,
                draw.SrcAlphaBlendFactor,
                draw.DstAlphaBlendFactor,
                draw.ColorWriteMask,
                1u,
                RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);

            Pipeline pipeline;
            if (!BackendContext.Resources.PipelineManager.TryGetSharedGraphicsPipeline(key, out pipeline))
            {
                PipelineInputAssemblyStateCreateInfo inputAssembly = new()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = topology,
                    PrimitiveRestartEnable = Vk.False,
                };
                PipelineRasterizationStateCreateInfo rasterizer = new()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = draw.CullMode,
                    FrontFace = draw.FrontFace,
                    LineWidth = 1.0f,
                };
                PipelineMultisampleStateCreateInfo multisampling = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = draw.RasterizationSamples,
                };
                PipelineDepthStencilStateCreateInfo depthStencil = new()
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = draw.DepthTestEnabled ? Vk.True : Vk.False,
                    DepthWriteEnable = draw.DepthWriteEnabled ? Vk.True : Vk.False,
                    DepthCompareOp = draw.DepthCompareOp,
                    StencilTestEnable = draw.StencilTestEnabled ? Vk.True : Vk.False,
                    Front = draw.FrontStencilState,
                    Back = draw.BackStencilState,
                };
                PipelineColorBlendAttachmentState[] blendAttachments = new PipelineColorBlendAttachmentState[colorAttachmentCount];
                for (int index = 0; index < blendAttachments.Length; index++)
                    blendAttachments[index] = new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = draw.ColorWriteMask,
                        BlendEnable = Vk.False,
                        ColorBlendOp = BlendOp.Add,
                        AlphaBlendOp = BlendOp.Add,
                        SrcColorBlendFactor = BlendFactor.One,
                        DstColorBlendFactor = BlendFactor.Zero,
                        SrcAlphaBlendFactor = BlendFactor.One,
                        DstAlphaBlendFactor = BlendFactor.Zero,
                    };

                VulkanGraphicsPipelineBuildRequest request = CreateGraphicsPipelineBuildRequest(
                    visibilityProgram, key, meshlet ? "VulkanAdvancedVisibilityMeshRaster" : "VulkanAdvancedVisibilityRaster", colorAttachmentCount,
                    inputAssembly, 1u, RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl,
                    rasterizer, multisampling, depthStencil, blendAttachments,
                    [DynamicState.Viewport, DynamicState.Scissor], targetClosure.RenderPass,
                    useDynamicRendering, targetClosure.DynamicRenderingFormats, in vertexInput,
                    meshlet);
                pipeline = BackendContext.Resources.PipelineManager.StoreOrRetireSharedGraphicsPipeline(
                    key,
                    BackendContext.Resources.PipelineManager.CreateGraphicsPipelineFromRequest(
                        request,
                        BackendContext.Resources.PipelineManager.ActivePipelineCache,
                        backgroundCompile: false));
            }
            if (pipeline.Handle == 0UL)
            {
                reason = "Vulkan returned a null visibility raster pipeline";
                return false;
            }

            prepared = new(
                visibilityProgram,
                visibilityProgram.LinkGeneration,
                pipeline,
                visibilityProgram.PipelineLayout,
                topology,
                meshlet,
                vertexInput,
                targetClosure);
            reason = "Ready";
            return true;
        }
        catch (VulkanPipelineCompilationDeferredException exception)
        {
            reason = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            reason = exception.Message;
            return false;
        }
        finally
        {
            _recordDrawSync.Exit();
        }
    }

    private static bool TryCaptureCanonicalGeometryVisibilityVertexInput(
        VkRenderProgram visibilityProgram,
        out VulkanVisibilityVertexInputSnapshot snapshot,
        out string reason)
    {
        snapshot = default;
        if (!visibilityProgram.TryGetVertexStageInputCount(out int inputCount) ||
            inputCount != 2 ||
            !visibilityProgram.TryGetVertexInputLocation(
                "Position",
                out uint positionLocation) ||
            !visibilityProgram.TryGetVertexInputLocation(
                "TexCoord0",
                out uint uvLocation) ||
            positionLocation != 0u || uvLocation != 1u)
        {
            reason =
                "canonical packed visibility requires Position and TexCoord0 vertex inputs";
            return false;
        }

        snapshot = s_canonicalGeometryVisibilityVertexInput;
        reason = "Ready";
        return true;
    }

    private static VulkanVisibilityVertexInputSnapshot
        CreateCanonicalGeometryVisibilityVertexInput()
    {
        VertexInputBindingDescription[] bindings =
        [
            new()
            {
                Binding = 0u,
                Stride = 64u,
                InputRate = VertexInputRate.Vertex,
            },
        ];
        VertexInputAttributeDescription[] attributes =
        [
            new()
            {
                Location = 0u,
                Binding = 0u,
                Format = Format.R32G32B32Sfloat,
                Offset = 0u,
            },
            new()
            {
                Location = 1u,
                Binding = 0u,
                Format = Format.R16G16Sfloat,
                Offset = 20u,
            },
        ];
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add("CanonicalAdvancedGeometry.PackedVertex64");
        hash.Add(0u);
        return new(bindings, attributes, hash.Value);
    }

    private bool TryCaptureVisibilityVertexInput(
        VkRenderProgram visibilityProgram,
        out VulkanVisibilityVertexInputSnapshot snapshot,
        out string reason)
    {
        snapshot = default;
        reason = "Ready";
        lock (_bufferStateSync)
        {
            if (!visibilityProgram.TryGetVertexStageInputCount(out int inputCount) || inputCount != 2)
            {
                reason = "visibility vertex program must expose exactly Position and TexCoord0 inputs";
                return false;
            }

            List<KeyValuePair<string, VkDataBuffer>> vertexBuffers = [];
            foreach ((string name, VkDataBuffer buffer) in _bufferCache)
                if (buffer.Data.Target == EBufferTarget.ArrayBuffer)
                    vertexBuffers.Add(new(name, buffer));
            vertexBuffers.Sort(static (left, right) =>
            {
                uint leftBinding = left.Value.Data.BindingIndexOverride ?? uint.MaxValue;
                uint rightBinding = right.Value.Data.BindingIndexOverride ?? uint.MaxValue;
                int comparison = leftBinding.CompareTo(rightBinding);
                return comparison != 0 ? comparison : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            List<VertexInputBindingDescription> bindings = [];
            List<VertexInputAttributeDescription> attributes = [];
            HashSet<uint> bindingsUsed = [];
            HashSet<uint> locationsUsed = [];
            uint nextBinding = 0u;
            foreach ((string name, VkDataBuffer buffer) in vertexBuffers)
            {
                uint binding = buffer.Data.BindingIndexOverride ?? AllocateNextVertexBinding(bindingsUsed, ref nextBinding);
                if (!bindingsUsed.Add(binding))
                {
                    reason = $"visibility vertex input has duplicate binding {binding}";
                    return false;
                }
                bool interleaved = buffer.Data.InterleavedAttributes is { Length: > 0 };
                uint stride = interleaved && Mesh is not null ? Mesh.InterleavedStride : buffer.Data.ElementSize;
                bindings.Add(new VertexInputBindingDescription
                {
                    Binding = binding,
                    Stride = stride,
                    InputRate = buffer.Data.InstanceDivisor > 0 ? VertexInputRate.Instance : VertexInputRate.Vertex,
                });
                if (interleaved)
                {
                    foreach (var attribute in buffer.Data.InterleavedAttributes)
                    {
                        if (!visibilityProgram.TryGetVertexInputLocation(attribute.AttributeName, out uint location))
                            continue;
                        if (!locationsUsed.Add(location))
                        {
                            reason = $"visibility vertex input has duplicate location {location}";
                            return false;
                        }
                        attributes.Add(new VertexInputAttributeDescription
                        {
                            Location = location,
                            Binding = binding,
                            Format = ToFormat(attribute.Type, attribute.Count, attribute.Integral, buffer.Data.Normalize),
                            Offset = attribute.Offset,
                        });
                    }
                }
                else if (visibilityProgram.TryGetVertexInputLocation(name, out uint location))
                {
                    if (!locationsUsed.Add(location))
                    {
                        reason = $"visibility vertex input has duplicate location {location}";
                        return false;
                    }
                    attributes.Add(new VertexInputAttributeDescription
                    {
                        Location = location,
                        Binding = binding,
                        Format = ToFormat(buffer.Data.ComponentType, buffer.Data.ComponentCount, buffer.Data.Integral, buffer.Data.Normalize),
                    });
                }
            }
            if (!locationsUsed.SetEquals([0u, 1u]))
            {
                reason = "visibility vertex input requires mesh attributes Position@0 and TexCoord0@1";
                return false;
            }

            VulkanStableHash64 hash = new(schemaVersion: 1);
            hash.Add(bindings.Count);
            foreach (VertexInputBindingDescription binding in bindings)
            {
                hash.Add(binding.Binding);
                hash.Add(binding.Stride);
                hash.Add((int)binding.InputRate);
            }
            hash.Add(attributes.Count);
            foreach (VertexInputAttributeDescription attribute in attributes)
            {
                hash.Add(attribute.Location);
                hash.Add(attribute.Binding);
                hash.Add((int)attribute.Format);
                hash.Add(attribute.Offset);
            }
            snapshot = new(bindings.ToArray(), attributes.ToArray(), hash.Value);
            return true;
        }
    }

    private ulong ComputeVisibilityFeatureProfileHash()
    {
        VulkanStableHash64 hash = new(schemaVersion: 1);
        hash.Add(RuntimeEngine.Rendering.Settings.ShaderConfigVersion);
        hash.Add(RuntimeEngine.Rendering.ShouldUseVulkanShaderClipDepthRemap);
        hash.Add(RuntimeEngine.Rendering.ShouldUseNativeVulkanDepthClipControl);
        hash.Add((int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
        hash.Add((int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
        hash.Add(BackendContext.Supports(EVulkanDeviceCapability.IndexTypeUint8));
        return hash.Value;
    }
}
