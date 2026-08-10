namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen renderer-supplied encoding input for one worker batch. Worker dispatch
/// itself remains owned by <see cref="VulkanCommandRuntime"/>.
/// </summary>
internal sealed class VulkanPreparedWorkerRecordingContext
{
    public ulong FrameId { get; private set; }

    /// <summary>
    /// Publishes the next frozen worker input. This is called only while the
    /// batch is idle, then remains immutable until the final worker completes.
    /// </summary>
    internal void Prepare(
        ulong frameId)
    {
        FrameId = frameId;
    }

    internal void Reset()
    {
        FrameId = 0;
    }
}
