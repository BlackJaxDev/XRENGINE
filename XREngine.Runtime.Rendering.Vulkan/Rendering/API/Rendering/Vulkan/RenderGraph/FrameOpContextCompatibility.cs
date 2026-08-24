namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Defines recording and inline-query compatibility for immutable frame-operation contexts.
/// </summary>
internal static class FrameOpContextCompatibility
{
    public static bool AreRecordingCompatible(in FrameOpContext first, in FrameOpContext second)
    {
        if (first.Equals(second))
            return true;
        if (first.RecordingFingerprint != second.RecordingFingerprint)
            return false;

        // Context and output-scheduling metadata select diagnostics/admission,
        // not Vulkan commands. In particular, RenderOutputRequest.FrameId changes
        // every frame and must never split an otherwise reusable recording batch.
        return NormalizeSchedulingMetadata(first with { ContextId = 0UL }).Equals(
            NormalizeSchedulingMetadata(second with { ContextId = 0UL }));
    }

    /// <summary>
    /// Determines whether independently recorded command chains can share one
    /// worker-dispatch batch and Vulkan rendering scope.
    /// </summary>
    public static bool AreCommandChainBatchCompatible(in FrameOpContext first, in FrameOpContext second)
    {
        if (AreRecordingCompatible(first, second))
            return true;

        // Resource and descriptor generations select immutable snapshots for
        // each chain; they do not alter render-pass inheritance. Normalize only
        // those captured planning fields. Target, dimensions, queue family,
        // stereo/multiview, pipeline, registry identity, and ordering policy
        // remain exact so incompatible rendering scopes never share a batch.
        FrameOpContext normalizedFirst = NormalizeSchedulingMetadata(first with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            ResourceGeneration = 0UL,
            DescriptorGeneration = 0UL,
            ResourceRegistrySignatureSnapshot = null,
        });
        FrameOpContext normalizedSecond = NormalizeSchedulingMetadata(second with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            ResourceGeneration = 0UL,
            DescriptorGeneration = 0UL,
            ResourceRegistrySignatureSnapshot = null,
        });
        return normalizedFirst.Equals(normalizedSecond);
    }

    public static bool AreQueryScopeCompatible(in FrameOpContext first, in FrameOpContext second)
    {
        if (AreRecordingCompatible(first, second))
            return true;

        // Descriptor-table changes do not alter dynamic-rendering compatibility.
        FrameOpContext normalizedFirst = NormalizeSchedulingMetadata(first with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        });
        FrameOpContext normalizedSecond = NormalizeSchedulingMetadata(second with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        });
        return normalizedFirst.Equals(normalizedSecond);
    }

    /// <summary>
    /// Removes policy-only fields from comparisons that answer whether two
    /// contexts encode the same native Vulkan work. The output DAG consumes
    /// these values separately through <see cref="OutputRequest"/>.
    /// </summary>
    private static FrameOpContext NormalizeSchedulingMetadata(in FrameOpContext context)
        => context with
        {
            OutputProducerDependencySetId = 0UL,
            OutputConsumerDependencySetId = 0UL,
            OutputSchedulingInstanceIdentity = 0UL,
            OutputSchedulingRequest = default,
        };
}
