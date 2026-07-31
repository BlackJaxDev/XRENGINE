using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal readonly ref struct DynamicRenderingScopePlan
{
    public DynamicRenderingScopePlan(
        Rect2D renderArea,
        uint layerCount,
        uint viewMask,
        ReadOnlySpan<DynamicRenderingAttachmentPlan> colorAttachments,
        DynamicRenderingAttachmentPlan depthAttachment,
        bool hasDepthAttachment,
        DynamicRenderingAttachmentPlan stencilAttachment,
        bool hasStencilAttachment,
        bool depthStencilReadOnly,
        DynamicRenderingFormatSignature formatSignature,
        SampleCountFlags sampleCount)
        : this(
            renderArea,
            layerCount,
            viewMask,
            colorAttachments,
            depthAttachment,
            hasDepthAttachment,
            stencilAttachment,
            hasStencilAttachment,
            depthStencilReadOnly,
            formatSignature,
            sampleCount,
            default)
    {
    }

    public DynamicRenderingScopePlan(
        Rect2D renderArea,
        uint layerCount,
        uint viewMask,
        ReadOnlySpan<DynamicRenderingAttachmentPlan> colorAttachments,
        DynamicRenderingAttachmentPlan depthAttachment,
        bool hasDepthAttachment,
        DynamicRenderingAttachmentPlan stencilAttachment,
        bool hasStencilAttachment,
        bool depthStencilReadOnly,
        DynamicRenderingFormatSignature formatSignature,
        SampleCountFlags sampleCount,
        DynamicRenderingLocalReadPlan localRead)
    {
        RenderArea = renderArea;
        LayerCount = VulkanDynamicRenderingUtilities.ResolveLayerCount(layerCount, viewMask);
        ViewMask = viewMask;
        ColorAttachments = colorAttachments;
        DepthAttachment = depthAttachment;
        HasDepthAttachment = hasDepthAttachment;
        StencilAttachment = stencilAttachment;
        HasStencilAttachment = hasStencilAttachment;
        DepthStencilReadOnly = depthStencilReadOnly;
        FormatSignature = formatSignature;
        SampleCount = sampleCount;
        LocalRead = localRead;
        LocalReadSignature =
            DynamicRenderingLocalReadSignature.Create(in localRead);
        InheritanceRenderingFlags = 0;
    }

    public Rect2D RenderArea { get; }
    public uint LayerCount { get; }
    public uint ViewMask { get; }
    public ReadOnlySpan<DynamicRenderingAttachmentPlan> ColorAttachments { get; }
    public DynamicRenderingAttachmentPlan DepthAttachment { get; }
    public bool HasDepthAttachment { get; }
    public DynamicRenderingAttachmentPlan StencilAttachment { get; }
    public bool HasStencilAttachment { get; }
    public bool DepthStencilReadOnly { get; }
    public DynamicRenderingFormatSignature FormatSignature { get; }
    public DynamicRenderingFormatSignature SemanticSignature => FormatSignature;
    public SampleCountFlags SampleCount { get; }
    public DynamicRenderingLocalReadPlan LocalRead { get; }
    public DynamicRenderingLocalReadSignature LocalReadSignature { get; }
    public RenderingFlags InheritanceRenderingFlags { get; }
}
