namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Computes the frame-plan identity from immutable operation snapshots without
/// coupling frame-loop preparation to the renderer facade.
/// </summary>
internal static class VulkanFrameOperationSignature
{
    internal static ulong Compute(FrameOp[] operations)
        // FrameOp records contain frame-plan-owned snapshot objects. Their
        // generated GetHashCode() includes those managed object identities, so
        // hashing the record itself made an unchanged command stream appear new
        // every frame. The canonical semantic hasher captures native command
        // structure and binding layouts while deliberately excluding mutable
        // frame payload identity.
        => VulkanFrameOperationSemantics.ComputeFrameOpsSignature(operations);

    /// <summary>
    /// Computes the resource and descriptor publication versions used by a
    /// sealed frame plan. Keeping this calculation producer-array based lets an
    /// exact cache hit be proven before the relatively expensive lowering step.
    /// </summary>
    internal static void ComputeVersionSignatures(
        FrameOp[] operations,
        FrameOp[] dynamicOverlayOperations,
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
        FrameOp[] operations,
        ref ulong resourceVersionSignature,
        ref ulong descriptorVersionSignature)
    {
        for (int index = 0; index < operations.Length; index++)
        {
            ref readonly FrameOpContext context =
                ref operations[index].ContextReference;
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
