namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Monotonic readiness states for a generation-specific frame dependency.
/// </summary>
internal enum EVulkanFrameDependencyState
{
    Declared,
    CpuPrepared,
    GpuSubmitted,
    Ready,
    TerminalFailed,
}
