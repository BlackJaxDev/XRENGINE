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

        // ContextId identifies a diagnostic capture, not Vulkan recording state.
        return (first with { ContextId = 0UL }).Equals(second with { ContextId = 0UL });
    }

    public static bool AreQueryScopeCompatible(in FrameOpContext first, in FrameOpContext second)
    {
        if (AreRecordingCompatible(first, second))
            return true;

        // Descriptor-table changes do not alter dynamic-rendering compatibility.
        FrameOpContext normalizedFirst = first with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        };
        FrameOpContext normalizedSecond = second with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        };
        return normalizedFirst.Equals(normalizedSecond);
    }
}
