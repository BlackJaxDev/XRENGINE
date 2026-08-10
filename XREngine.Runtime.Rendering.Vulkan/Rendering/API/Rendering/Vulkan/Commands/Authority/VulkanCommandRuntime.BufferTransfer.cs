using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    /// <summary>
    /// Records and synchronously submits one tracked buffer copy on the graphics
    /// queue. A dedicated transfer queue requires a semaphore-backed asynchronous
    /// ownership plan, so this compatibility operation deliberately stays on the
    /// graphics queue.
    /// </summary>
    internal bool ExecuteSynchronousBufferUpload(
        Buffer source,
        Buffer destination,
        ulong size,
        ulong sourceOffset,
        ulong destinationOffset)
    {
        if (!DeviceContext.IsOperational)
            return false;

        QueueFamilyIndices queueFamilies = DeviceContext.QueueFamilies;
        uint graphicsFamily = queueFamilies.GraphicsFamilyIndex ?? 0u;
        uint transferFamily = queueFamilies.TransferFamilyIndex ?? graphicsFamily;
        RecordTransferQueuePolicyDiagnostics(
            source,
            destination,
            size,
            graphicsFamily,
            transferFamily,
            transferFamily != graphicsFamily);

        using CommandScope uploadScope = NewCommandScope();
        BufferCopy copyRegion = new()
        {
            SrcOffset = sourceOffset,
            DstOffset = destinationOffset,
            Size = size,
        };
        CmdCopyBufferTracked(
            uploadScope.CommandBuffer,
            source,
            destination,
            1,
            &copyRegion);
        return DeviceContext.IsOperational;
    }

    private static void RecordTransferQueuePolicyDiagnostics(
        Buffer source,
        Buffer destination,
        ulong size,
        uint graphicsFamily,
        uint transferFamily,
        bool dedicatedTransferFamily)
    {
        if (!RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            return;

        Debug.Vulkan(
            "[VkUploadQueuePolicy] source=0x{0:X} destination=0x{1:X} bytes={2} graphicsFamily={3} transferFamily={4} dedicatedTransferFamily={5} selectedQueue=graphics reason=synchronous-upload-ordering.",
            source.Handle,
            destination.Handle,
            size,
            graphicsFamily,
            transferFamily,
            dedicatedTransferFamily);
    }
}
