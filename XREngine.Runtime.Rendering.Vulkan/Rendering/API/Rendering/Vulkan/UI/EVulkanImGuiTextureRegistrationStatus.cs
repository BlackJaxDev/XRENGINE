namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Classifies a nonblocking ImGui texture registration attempt so callers only
/// schedule uploads for resources that genuinely lack published image data.
/// </summary>
internal enum EVulkanImGuiTextureRegistrationStatus
{
    Ready,
    NeedsUpload,
    DescriptorUnavailable,
    Unsupported,
}
