namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen renderer-supplied encoding input for one worker batch. Worker dispatch
/// itself remains owned by <see cref="VulkanCommandRuntime"/>.
/// </summary>
internal sealed class VulkanPreparedWorkerRecordingContext
{
    public VulkanCommandRuntime? Runtime { get; private set; }
    public VulkanFrameTelemetry? Telemetry { get; private set; }
    public VulkanTrackedCommandEncoder? Encoder { get; private set; }
    public ulong FrameId { get; private set; }

    /// <summary>
    /// Publishes the next frozen worker input. This is called only while the
    /// batch is idle, then remains immutable until the final worker completes.
    /// </summary>
    internal void Prepare(
        VulkanCommandRuntime runtime,
        VulkanFrameTelemetry telemetry,
        VulkanTrackedCommandEncoder encoder,
        ulong frameId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(encoder);
        Runtime = runtime;
        Telemetry = telemetry;
        Encoder = encoder;
        FrameId = frameId;
    }

    internal void Reset()
    {
        Runtime = null;
        Telemetry = null;
        Encoder = null;
        FrameId = 0;
    }
}
