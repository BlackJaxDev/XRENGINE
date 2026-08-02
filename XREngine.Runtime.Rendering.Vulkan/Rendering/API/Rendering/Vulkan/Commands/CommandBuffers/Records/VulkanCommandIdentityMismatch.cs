namespace XREngine.Rendering.Vulkan;

/// <summary>
/// First mismatched component between two recorded-command identities.
/// </summary>
internal readonly record struct VulkanCommandIdentityMismatch(
    EVulkanCommandIdentityComponent Component,
    ulong Recorded,
    ulong Current)
{
    internal static VulkanCommandIdentityMismatch None { get; } = new(
        EVulkanCommandIdentityComponent.None,
        0,
        0);

    internal bool RequiresRecording =>
        Component != EVulkanCommandIdentityComponent.None;
}
