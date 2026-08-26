namespace XREngine.Rendering.Vulkan;

/// <summary>One bounded foreground-readiness driver observation.</summary>
internal readonly record struct VulkanFrameDependencyDriveResult(
    int DeclaredCount,
    int ReadyCount,
    int SubmittedCount,
    int FailedCount,
    EVulkanFrameDependencyKind? ActiveKind,
    ulong ActiveResourceKey,
    ulong ActiveGeneration,
    ulong ActiveTimelineValue)
{
    internal bool IsReady => DeclaredCount == ReadyCount && FailedCount == 0;
    internal bool HasTerminalFailure => FailedCount != 0;
}
