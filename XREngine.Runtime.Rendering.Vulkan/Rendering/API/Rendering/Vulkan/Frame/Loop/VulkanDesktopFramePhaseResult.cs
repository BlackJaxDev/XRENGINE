namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed result of one desktop orchestration stage. It prevents a terminal helper
/// result from being accidentally treated as permission to start later work.
/// </summary>
internal readonly record struct VulkanDesktopFramePhaseResult(
    EVulkanFrameStage Stage,
    EDesktopFrameFlow Flow)
{
    /// <summary>Whether the next orchestration stage may begin.</summary>
    public bool ShouldContinue => Flow == EDesktopFrameFlow.Continue;
}
