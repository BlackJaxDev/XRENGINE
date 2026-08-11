namespace XREngine.Rendering.Vulkan;

internal enum EVulkanPrimaryCommandRecordingDisposition : byte
{
    Recorded,
    Reused,
    ReplanRequired,
    Deferred,
}
