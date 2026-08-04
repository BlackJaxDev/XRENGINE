namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit lifecycle state for a reusable recorded Vulkan command artifact.
/// </summary>
internal enum EVulkanRecordedCommandArtifactState : byte
{
    Empty = 0,
    Allocated,
    Recording,
    Executable,
    Invalid,
    PendingRetirement,
    Retired,
    Failed,
}
