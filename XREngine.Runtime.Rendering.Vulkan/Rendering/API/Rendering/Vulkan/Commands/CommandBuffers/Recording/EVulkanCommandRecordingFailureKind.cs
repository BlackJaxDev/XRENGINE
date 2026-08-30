namespace XREngine.Rendering.Vulkan;

internal enum EVulkanCommandRecordingFailureKind : byte
{
    None,
    Deferred,
    ReplanRequired,
    /// <summary>
    /// The sealed frame became stale after acceptance and must be discarded in
    /// favor of a frame authored from a new producer epoch.
    /// </summary>
    RetryFrame,
}
