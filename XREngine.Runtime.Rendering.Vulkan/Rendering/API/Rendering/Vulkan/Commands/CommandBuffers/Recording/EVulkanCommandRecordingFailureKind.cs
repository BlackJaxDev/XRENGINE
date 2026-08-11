namespace XREngine.Rendering.Vulkan;

internal enum EVulkanCommandRecordingFailureKind : byte
{
    None,
    Deferred,
    ReplanRequired,
}
