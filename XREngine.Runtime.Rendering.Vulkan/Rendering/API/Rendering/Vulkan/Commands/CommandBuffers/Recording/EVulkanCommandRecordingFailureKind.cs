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
    /// <summary>
    /// The active pipeline or output state cannot produce this frame, but a
    /// validated renderer-state change may admit one bounded recovery probe.
    /// </summary>
    RecoverAfterStateChange,
    /// <summary>
    /// Renderer correctness is no longer known. Pipeline or output changes must
    /// not clear this failure.
    /// </summary>
    RendererTerminal,
}
