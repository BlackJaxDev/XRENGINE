using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.Pipelines;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Represents the context of a frame operation in the Vulkan renderer.
/// Contains information about the current frame operation, including pipeline, viewport, and rendering targets.
/// </summary>
/// <param name="PipelineIdentity">The identity of the rendering pipeline.</param>
/// <param name="ViewportIdentity">The identity of the viewport.</param>
/// <param name="PipelineInstance">The instance of the rendering pipeline.</param>
/// <param name="ResourceRegistry">The resource registry for the current frame operation.</param>
/// <param name="PassMetadata">The metadata for the render passes.</param>
/// <param name="DisplayWidth">The width of the display.</param>
/// <param name="DisplayHeight">The height of the display.</param>
/// <param name="InternalWidth">The internal width used for rendering.</param>
/// <param name="InternalHeight">The internal height used for rendering.</param>
/// <param name="OutputFrameBufferName">The name of the output frame buffer.</param>
/// <param name="PreserveSubmissionOrderBlock">Indicates whether to preserve the submission order block.</param>
/// <param name="OutputTargetIdentity">The identity of the output target.</param>
/// <param name="OutputTargetName">The name of the output target.</param>
/// <param name="OutputFrameBufferIdentity">The identity of the output frame buffer.</param>
/// <param name="ContextKind">The kind of the frame operation context.</param>
/// <param name="ContextId">The unique identifier for the context.</param>
/// <param name="LogicalViewId">Stable logical view/history identity, independent of acquired target slots.</param>
/// <param name="RecordingFingerprint">The recording fingerprint for the frame operation.</param>
/// <param name="SubmissionQueueFamily">The submission queue family index.</param>
/// <param name="StereoEnabled">Indicates whether stereo rendering is enabled.</param>
/// <param name="MultiviewEnabled">Indicates whether multiview rendering is enabled.</param>
/// <param name="ResourceGeneration">The resource generation number.</param>
/// <param name="DescriptorGeneration">The descriptor generation number.</param>
/// <param name="OutputFrameBuffer">The output frame buffer.</param>
/// <param name="ResourceRegistrySignatureSnapshot">The immutable registry descriptor signature captured for this operation.</param>
/// <param name="OutputProducerDependencySetId">Optional semantic output-resource set produced by this context.</param>
/// <param name="OutputConsumerDependencySetId">Optional semantic output-resource set required before this context may execute.</param>
/// <param name="OutputSchedulingInstanceIdentity">Stable engine output instance used to correlate backend work with pacing admission.</param>
/// <param name="OutputSchedulingRequest">Canonical engine output request frozen for this backend context.</param>
internal readonly record struct FrameOpContext(
    int PipelineIdentity,
    int ViewportIdentity,
    XRRenderPipelineInstance? PipelineInstance,
    RenderResourceRegistry? ResourceRegistry,
    IReadOnlyCollection<RenderPassMetadata>? PassMetadata,
    uint DisplayWidth = 1u,
    uint DisplayHeight = 1u,
    uint InternalWidth = 1u,
    uint InternalHeight = 1u,
    string? OutputFrameBufferName = null,
    bool PreserveSubmissionOrderBlock = false,
    int OutputTargetIdentity = 0,
    string? OutputTargetName = null,
    int OutputFrameBufferIdentity = 0,
    EVulkanFrameOpContextKind ContextKind = EVulkanFrameOpContextKind.Unknown,
    ulong ContextId = 0,
    ulong LogicalViewId = 0,
    ulong RecordingFingerprint = ulong.MaxValue,
    uint SubmissionQueueFamily = 0,
    bool StereoEnabled = false,
    bool MultiviewEnabled = false,
    ulong ResourceGeneration = 0,
    ulong DescriptorGeneration = 0,
    XRFrameBuffer? OutputFrameBuffer = null,
    int? ResourceRegistrySignatureSnapshot = null,
    ulong OutputProducerDependencySetId = 0,
    ulong OutputConsumerDependencySetId = 0,
    ulong OutputSchedulingInstanceIdentity = 0,
    RenderOutputRequest OutputSchedulingRequest = default,
    VulkanFrameOpWorkspace? OperationWorkspace = null)
{
    public int SchedulingIdentity => OutputTargetIdentity == 0
        ? HashCode.Combine(PipelineIdentity, ViewportIdentity)
        : HashCode.Combine(PipelineIdentity, ViewportIdentity, OutputTargetIdentity);
}
