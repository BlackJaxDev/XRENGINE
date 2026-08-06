using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable output target request lowered from a frame-operation context.
/// It deliberately carries both display and internal extents so an output never
/// inherits allocation dimensions from an unrelated context.
/// </summary>
internal readonly record struct OutputRequest(
    EFrameOutputKind OutputKind,
    EVrOutputViewKind ViewKind,
    ulong StableOutputId,
    ulong StableViewFamilyId,
    int OutputTargetIdentity,
    int OutputFrameBufferIdentity,
    EVulkanFrameOpContextKind ContextKind,
    int PipelineIdentity,
    int ViewportIdentity,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    ulong ContextFingerprint,
    ulong ProducerDependencySetId,
    ulong ConsumerDependencySetId)
{
    internal static OutputRequest FromContext(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind = null)
        => new(
            ResolveOutputKind(context.ContextKind),
            ResolveViewKind(context.ContextKind, openXrViewKind),
            ComputeStableOutputId(context, openXrViewKind),
            ComputeStableViewFamilyId(context, openXrViewKind),
            context.OutputTargetIdentity,
            context.OutputFrameBufferIdentity,
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.RecordingFingerprint,
            context.OutputProducerDependencySetId == 0UL
                ? ComputeStableOutputId(context, openXrViewKind)
                : context.OutputProducerDependencySetId,
            context.OutputConsumerDependencySetId);

    internal RenderOutputRequest ToGraphRequest(ulong frameId)
    {
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            ViewKind,
            OutputKind,
            frameId);
        RenderOutputTargetDescriptor target = request.Target with
        {
            StableTargetId = StableOutputId,
            TargetGeneration = ResourceGeneration,
            DisplayWidth = DisplayWidth,
            DisplayHeight = DisplayHeight,
            InternalWidth = InternalWidth,
            InternalHeight = InternalHeight,
            FormatCompatibilityKey = ContextFingerprint,
        };
        return request with
        {
            OutputId = StableOutputId,
            ViewFamilyId = StableViewFamilyId,
            Target = target,
            ProducerDependencySetId = ProducerDependencySetId,
            ConsumerDependencySetId = ConsumerDependencySetId,
        };
    }

    internal static int CompareDeterministically(in OutputRequest left, in OutputRequest right)
    {
        int result = left.StableOutputId.CompareTo(right.StableOutputId);
        if (result != 0)
            return result;
        result = left.OutputKind.CompareTo(right.OutputKind);
        if (result != 0)
            return result;
        result = left.StableViewFamilyId.CompareTo(right.StableViewFamilyId);
        if (result != 0)
            return result;
        result = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
        return result != 0
            ? result
            : left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
    }

    /// <summary>
    /// Compares the output terminal identity without treating a particular
    /// operation input as a separate output publication.
    /// </summary>
    internal bool MatchesOutput(in OutputRequest other)
        => OutputKind == other.OutputKind &&
           ViewKind == other.ViewKind &&
           StableOutputId == other.StableOutputId &&
           StableViewFamilyId == other.StableViewFamilyId &&
           OutputTargetIdentity == other.OutputTargetIdentity &&
           OutputFrameBufferIdentity == other.OutputFrameBufferIdentity &&
           ContextKind == other.ContextKind &&
           PipelineIdentity == other.PipelineIdentity &&
           ViewportIdentity == other.ViewportIdentity;

    private static EFrameOutputKind ResolveOutputKind(EVulkanFrameOpContextKind contextKind)
        => contextKind switch
        {
            EVulkanFrameOpContextKind.OpenXrEye => EFrameOutputKind.OpenXREyeSubmit,
            EVulkanFrameOpContextKind.OpenXrMirror => EFrameOutputKind.DesktopMirror,
            EVulkanFrameOpContextKind.SceneCapture => EFrameOutputKind.SceneCapture,
            EVulkanFrameOpContextKind.LightProbeCapture => EFrameOutputKind.LightProbeCapture,
            EVulkanFrameOpContextKind.Shadow => EFrameOutputKind.Shadow,
            EVulkanFrameOpContextKind.UiPreview => EFrameOutputKind.UiPreview,
            EVulkanFrameOpContextKind.DiagnosticCapture => EFrameOutputKind.Diagnostic,
            _ => EFrameOutputKind.DesktopScene,
        };

    private static EVrOutputViewKind ResolveViewKind(
        EVulkanFrameOpContextKind contextKind,
        EVrOutputViewKind? openXrViewKind)
        => contextKind == EVulkanFrameOpContextKind.OpenXrEye
            ? openXrViewKind ?? EVrOutputViewKind.LeftEye
            : contextKind == EVulkanFrameOpContextKind.OpenXrMirror && openXrViewKind.HasValue
                ? openXrViewKind.Value
            : contextKind == EVulkanFrameOpContextKind.DiagnosticCapture
                ? EVrOutputViewKind.Debug
                : EVrOutputViewKind.Secondary;

    private static ulong ComputeStableOutputId(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind)
    {
        ulong hash = 1469598103934665603UL;
        Add(ref hash, (ulong)(uint)ResolveOutputKind(context.ContextKind));
        Add(ref hash, unchecked((ulong)(uint)context.OutputTargetIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputFrameBufferIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.PipelineIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.ViewportIdentity));
        Add(ref hash, (ulong)(uint)ResolveViewKind(context.ContextKind, openXrViewKind));
        // Paired OpenXR plans are target-neutral: logical view identity, not
        // an acquired image identity, keeps the two eye terminals distinct.
        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye)
            Add(ref hash, context.LogicalViewId);
        return hash == 0UL ? 1UL : hash;
    }

    private static ulong ComputeStableViewFamilyId(
        in FrameOpContext context,
        EVrOutputViewKind? openXrViewKind)
    {
        if (context.ContextKind == EVulkanFrameOpContextKind.OpenXrEye)
            return 0x58525F0000000001UL;

        ulong hash = 1099511628211UL;
        Add(ref hash, (ulong)(uint)ResolveOutputKind(context.ContextKind));
        Add(ref hash, unchecked((ulong)(uint)context.PipelineIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.ViewportIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputTargetIdentity));
        Add(ref hash, unchecked((ulong)(uint)context.OutputFrameBufferIdentity));
        return hash == 0UL ? 1UL : hash;
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
