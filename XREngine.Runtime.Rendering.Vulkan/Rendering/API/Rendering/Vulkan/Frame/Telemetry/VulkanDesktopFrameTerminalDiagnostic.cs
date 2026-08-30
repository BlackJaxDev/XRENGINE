namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stable renderer-facing projection of the latest settled desktop frame.
/// Failure strings are populated only for rejected or failed attempts.
/// </summary>
public readonly record struct VulkanDesktopFrameTerminalDiagnostic(
    long Sequence,
    ulong FrameId,
    int FrameSlot,
    string Outcome,
    string Reason,
    string FailureKind,
    string FailureStage,
    string NativeResult,
    string? ExceptionType,
    string? Detail,
    bool OwnershipSettled)
{
    /// <summary>Whether a settled desktop frame has been captured.</summary>
    public bool IsValid => Sequence != 0;
}
