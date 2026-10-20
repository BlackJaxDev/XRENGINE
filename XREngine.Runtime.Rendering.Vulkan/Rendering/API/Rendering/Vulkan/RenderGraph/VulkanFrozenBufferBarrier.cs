namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Buffer barrier frozen into a barrier plan. It holds the planned states, the queue-family
/// transfer, and the native buffer range that the barrier covers.
/// </summary>
internal readonly record struct VulkanFrozenBufferBarrier(
    int PassIndex, VulkanResourceId ResourceId, string LogicalResourceName, VulkanBarrierPlanner.PlannedBufferState Previous,
    VulkanBarrierPlanner.PlannedBufferState Next, uint SrcQueueFamilyIndex, uint DstQueueFamilyIndex,
    Silk.NET.Vulkan.Buffer NativeBuffer, ulong NativeOffset, ulong NativeSize, ulong NativeGeneration)
{
    /// <summary>
    /// Compares all fields. The resource name uses an ordinal compare. The native buffer handle
    /// compares by value, so the check does not box it.
    /// </summary>
    public bool Equals(VulkanFrozenBufferBarrier other)
        => PassIndex == other.PassIndex &&
           ResourceId == other.ResourceId &&
           string.Equals(LogicalResourceName, other.LogicalResourceName, StringComparison.Ordinal) &&
           Previous.Equals(other.Previous) &&
           Next.Equals(other.Next) &&
           SrcQueueFamilyIndex == other.SrcQueueFamilyIndex &&
           DstQueueFamilyIndex == other.DstQueueFamilyIndex &&
           NativeBuffer.Handle == other.NativeBuffer.Handle &&
           NativeOffset == other.NativeOffset &&
           NativeSize == other.NativeSize &&
           NativeGeneration == other.NativeGeneration;

    /// <summary>Combines a subset of the compared fields without boxing.</summary>
    public override int GetHashCode()
        => HashCode.Combine(
            PassIndex,
            ResourceId,
            LogicalResourceName,
            NativeBuffer.Handle,
            NativeOffset,
            NativeSize,
            NativeGeneration);
}
