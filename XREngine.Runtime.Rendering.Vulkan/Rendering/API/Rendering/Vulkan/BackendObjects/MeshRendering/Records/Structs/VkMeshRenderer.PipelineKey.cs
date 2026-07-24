using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        internal readonly record struct PipelineKey(
            PrimitiveTopology Topology,
            bool UseDynamicRendering,
            ulong RenderPassHandle,
            DynamicRenderingFormatSignature DynamicRenderingFormats,
            ulong ProgramPipelineHash,
            ulong VertexLayoutHash,
            ulong DescriptorLayoutHash,
            ulong PassMetadataHash,
            ulong FeatureProfileHash,
            SampleCountFlags RasterizationSamples,
            bool DepthTestEnabled,
            bool DepthWriteEnabled,
            CompareOp DepthCompareOp,
            bool StencilTestEnabled,
            StencilOpState FrontStencilState,
            StencilOpState BackStencilState,
            uint StencilWriteMask,
            CullModeFlags CullMode,
            FrontFace FrontFace,
            bool BlendEnabled,
            bool AlphaToCoverageEnabled,
            BlendOp ColorBlendOp,
            BlendOp AlphaBlendOp,
            BlendFactor SrcColorBlendFactor,
            BlendFactor DstColorBlendFactor,
            BlendFactor SrcAlphaBlendFactor,
            BlendFactor DstAlphaBlendFactor,
            ColorComponentFlags ColorWriteMask,
            uint ViewportScissorCount,
            bool NativeNegativeOneToOneDepth)
        {
            public bool Equals(PipelineKey other)
                => Topology == other.Topology &&
                   UseDynamicRendering == other.UseDynamicRendering &&
                   RenderPassHandle == other.RenderPassHandle &&
                   DynamicRenderingFormats.Equals(other.DynamicRenderingFormats) &&
                   ProgramPipelineHash == other.ProgramPipelineHash &&
                   VertexLayoutHash == other.VertexLayoutHash &&
                   DescriptorLayoutHash == other.DescriptorLayoutHash &&
                   PassMetadataHash == other.PassMetadataHash &&
                   FeatureProfileHash == other.FeatureProfileHash &&
                   RasterizationSamples == other.RasterizationSamples &&
                   DepthTestEnabled == other.DepthTestEnabled &&
                   DepthWriteEnabled == other.DepthWriteEnabled &&
                   DepthCompareOp == other.DepthCompareOp &&
                   StencilTestEnabled == other.StencilTestEnabled &&
                   StencilEquals(FrontStencilState, other.FrontStencilState) &&
                   StencilEquals(BackStencilState, other.BackStencilState) &&
                   StencilWriteMask == other.StencilWriteMask &&
                   CullMode == other.CullMode &&
                   FrontFace == other.FrontFace &&
                   BlendEnabled == other.BlendEnabled &&
                   AlphaToCoverageEnabled == other.AlphaToCoverageEnabled &&
                   ColorBlendOp == other.ColorBlendOp &&
                   AlphaBlendOp == other.AlphaBlendOp &&
                   SrcColorBlendFactor == other.SrcColorBlendFactor &&
                   DstColorBlendFactor == other.DstColorBlendFactor &&
                   SrcAlphaBlendFactor == other.SrcAlphaBlendFactor &&
                   DstAlphaBlendFactor == other.DstAlphaBlendFactor &&
                   ColorWriteMask == other.ColorWriteMask &&
                   ViewportScissorCount == other.ViewportScissorCount &&
                   NativeNegativeOneToOneDepth == other.NativeNegativeOneToOneDepth;

            public override int GetHashCode()
            {
                HashCode hash = new();
                hash.Add(Topology);
                hash.Add(UseDynamicRendering);
                hash.Add(RenderPassHandle);
                hash.Add(DynamicRenderingFormats);
                hash.Add(ProgramPipelineHash);
                hash.Add(VertexLayoutHash);
                hash.Add(DescriptorLayoutHash);
                hash.Add(PassMetadataHash);
                hash.Add(FeatureProfileHash);
                hash.Add(RasterizationSamples);
                hash.Add(DepthTestEnabled);
                hash.Add(DepthWriteEnabled);
                hash.Add(DepthCompareOp);
                hash.Add(StencilTestEnabled);
                AddStencil(ref hash, FrontStencilState);
                AddStencil(ref hash, BackStencilState);
                hash.Add(StencilWriteMask);
                hash.Add(CullMode);
                hash.Add(FrontFace);
                hash.Add(BlendEnabled);
                hash.Add(AlphaToCoverageEnabled);
                hash.Add(ColorBlendOp);
                hash.Add(AlphaBlendOp);
                hash.Add(SrcColorBlendFactor);
                hash.Add(DstColorBlendFactor);
                hash.Add(SrcAlphaBlendFactor);
                hash.Add(DstAlphaBlendFactor);
                hash.Add(ColorWriteMask);
                hash.Add(ViewportScissorCount);
                hash.Add(NativeNegativeOneToOneDepth);
                return hash.ToHashCode();
            }

            private static bool StencilEquals(in StencilOpState left, in StencilOpState right)
                => left.FailOp == right.FailOp &&
                   left.PassOp == right.PassOp &&
                   left.DepthFailOp == right.DepthFailOp &&
                   left.CompareOp == right.CompareOp &&
                   left.CompareMask == right.CompareMask &&
                   left.WriteMask == right.WriteMask &&
                   left.Reference == right.Reference;

            private static void AddStencil(ref HashCode hash, in StencilOpState state)
            {
                hash.Add(state.FailOp);
                hash.Add(state.PassOp);
                hash.Add(state.DepthFailOp);
                hash.Add(state.CompareOp);
                hash.Add(state.CompareMask);
                hash.Add(state.WriteMask);
                hash.Add(state.Reference);
            }
        }
    }
}
