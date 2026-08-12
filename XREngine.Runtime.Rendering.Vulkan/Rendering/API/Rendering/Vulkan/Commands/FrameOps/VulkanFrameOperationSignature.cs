namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Computes the frame-plan identity from immutable operation snapshots without
/// coupling frame-loop preparation to the renderer facade.
/// </summary>
internal static class VulkanFrameOperationSignature
{
    /// <summary>
    /// Computes the resource and descriptor publication versions used by a
    /// sealed frame plan from its canonical lowered streams.
    /// </summary>
    internal static void ComputeVersionSignatures(
        FrameOperationStream operations,
        FrameOperationStream dynamicOverlayOperations,
        out ulong resourceVersionSignature,
        out ulong descriptorVersionSignature)
    {
        resourceVersionSignature = 1469598103934665603UL;
        descriptorVersionSignature = 1099511628211UL;
        AddVersionComponents(
            operations,
            ref resourceVersionSignature,
            ref descriptorVersionSignature);
        AddVersionComponents(
            dynamicOverlayOperations,
            ref resourceVersionSignature,
            ref descriptorVersionSignature);
    }

    private static void AddVersionComponents(
        FrameOperationStream operations,
        ref ulong resourceVersionSignature,
        ref ulong descriptorVersionSignature)
    {
        for (int index = 0; index < operations.Count; index++)
        {
            ref readonly FrameOpContext context =
                ref operations.GetContext(index);
            Add(ref resourceVersionSignature, context.ResourceGeneration);
            Add(ref resourceVersionSignature, context.RecordingFingerprint);
            Add(ref descriptorVersionSignature, context.DescriptorGeneration);
            Add(ref descriptorVersionSignature, context.RecordingFingerprint);
        }
    }

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
