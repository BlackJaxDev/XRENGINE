namespace XREngine.Rendering.Vulkan;

/// <summary>Ownership phase of a <see cref="VulkanPendingDesktopRecord"/>.</summary>
internal enum EVulkanPendingDesktopRecordPhase : byte
{
    /// <summary>The record owns nothing.</summary>
    Idle = 0,

    /// <summary>The render thread prepares inputs; the lane arena holds a pending use.</summary>
    Preparing = 1,

    /// <summary>The CPU batch is published and may be queued or recording.</summary>
    Submitted = 2,

    /// <summary>The batch joined; recorded output waits for queue submission or discard.</summary>
    Recorded = 3,
}
