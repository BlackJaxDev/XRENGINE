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
}
