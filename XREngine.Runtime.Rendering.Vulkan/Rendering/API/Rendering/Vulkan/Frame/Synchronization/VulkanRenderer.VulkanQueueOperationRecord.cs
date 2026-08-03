using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Describes one recent queue operation for device-loss and synchronization
    /// diagnostics.
    /// </summary>
    /// <param name="Serial">The monotonic diagnostic operation serial.</param>
    /// <param name="Operation">The queue operation name.</param>
    /// <param name="QueueHandle">The native queue handle.</param>
    /// <param name="Result">The Vulkan result returned by the operation.</param>
    /// <param name="DeviceState">The renderer device state at record time.</param>
    /// <param name="SubmissionSerial">The associated submission serial, if any.</param>
    /// <param name="ThreadId">The managed thread that performed the operation.</param>
    /// <param name="Caller">The originating member name.</param>
    internal readonly record struct VulkanQueueOperationRecord(
        ulong Serial,
        string Operation,
        ulong QueueHandle,
        Result Result,
        EVulkanDeviceState DeviceState,
        ulong SubmissionSerial,
        int ThreadId,
        string? Caller);
}
