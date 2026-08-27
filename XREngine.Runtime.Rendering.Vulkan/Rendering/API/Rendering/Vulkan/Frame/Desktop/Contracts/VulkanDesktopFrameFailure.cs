using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Typed identity for the condition that ended one desktop frame attempt.
/// Strings are retained only on failure paths; successful frames use
/// <see cref="None"/> and remain allocation-free.
/// </summary>
internal readonly record struct VulkanDesktopFrameFailure(
    EVulkanDesktopFrameFailureKind Kind,
    EVulkanFrameStage Stage,
    Result NativeResult,
    string? ExceptionType,
    string? Detail)
{
    internal static VulkanDesktopFrameFailure None => default;

    internal bool IsFailure => Kind != EVulkanDesktopFrameFailureKind.None;
}
