namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free reason attached to the latest artifact state transition.
/// </summary>
internal enum EVulkanRecordedCommandArtifactInvalidationReason : byte
{
    None = 0,
    DependencyChanged,
    RecordingStarted,
    InheritanceMismatch,
    RecordingFailed,
    NativeBufferReplaced,
    RetirementRequested,
    Retired,
}
