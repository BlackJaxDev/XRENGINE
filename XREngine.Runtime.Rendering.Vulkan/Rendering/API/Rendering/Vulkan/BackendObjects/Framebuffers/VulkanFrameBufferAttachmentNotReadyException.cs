namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Signals that a framebuffer definition is valid but one of its transient
/// Vulkan attachment backings has not been published for this frame yet.
/// </summary>
internal sealed class VulkanFrameBufferAttachmentNotReadyException : InvalidOperationException
{
    internal const string DiagnosticPrefix = "Vulkan framebuffer attachment not ready:";

    internal VulkanFrameBufferAttachmentNotReadyException(string detail)
        : base(detail.StartsWith(DiagnosticPrefix, StringComparison.Ordinal)
            ? detail
            : $"{DiagnosticPrefix} {detail}") { }

    internal static bool IsTransientReason(string? reason)
        => reason?.StartsWith(DiagnosticPrefix, StringComparison.Ordinal) == true;
}
