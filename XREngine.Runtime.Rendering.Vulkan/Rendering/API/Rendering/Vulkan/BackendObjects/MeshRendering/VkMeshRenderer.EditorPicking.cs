using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>
    /// Selects the final-color coverage rule from the frozen scene blend state.
    /// Zero RGB writes preserve the identity behind stencil and depth-only draws.
    /// </summary>
    private static uint GetEditorPickingCoverage(in PendingMeshDraw draw)
        => GetEditorPickingCoverageRule(in draw) | (((uint)draw.ColorWriteMask & 7u) << 8);

    private static uint GetEditorPickingCoverageRule(in PendingMeshDraw draw)
    {
        const ColorComponentFlags rgb = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit;
        if (!draw.EditorHighlightOutput.WritesMask || (draw.ColorWriteMask & rgb) == 0)
            return 3;
        if (!draw.BlendEnabled)
            return 0;
        if (draw.ColorBlendOp == BlendOp.Add)
        {
            if (draw.SrcColorBlendFactor == BlendFactor.One && draw.DstColorBlendFactor == BlendFactor.Zero)
                return 0;
            if (draw.SrcColorBlendFactor == BlendFactor.SrcAlpha &&
                draw.DstColorBlendFactor == BlendFactor.OneMinusSrcAlpha)
                return 1;
            if (draw.SrcColorBlendFactor == BlendFactor.One && draw.DstColorBlendFactor == BlendFactor.OneMinusSrcAlpha)
                return 2;
            if (draw.SrcColorBlendFactor == BlendFactor.SrcAlpha && draw.DstColorBlendFactor == BlendFactor.One)
                return 4;
            if (draw.SrcColorBlendFactor == BlendFactor.One && draw.DstColorBlendFactor == BlendFactor.One)
                return 5;
        }
        throw new NotSupportedException(
            $"Editor picking has no coverage rule for scene blending {draw.ColorBlendOp}/{draw.SrcColorBlendFactor}/{draw.DstColorBlendFactor}.");
    }

    private PipelineColorBlendAttachmentState CreateEditorPickingBlend(
        Format format, in EditorHighlightRasterOutput output)
    {
        if (format != Format.R16G16B16A16Unorm)
            throw new NotSupportedException("The editor picking attachment must use RGBA16 UNORM.");
        if (!output.WritesMask)
            return default;
        if (!ProgramCreationPort.SupportsColorAttachmentBlend(format))
            throw new NotSupportedException("The editor picking format does not support blending.");
        return new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = Vk.True,
            ColorBlendOp = BlendOp.Add,
            AlphaBlendOp = BlendOp.Add,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
        };
    }
}
