namespace XREngine.Rendering.Vulkan;

/// <summary>One exact indexed issue within one native proof incarnation.</summary>
internal readonly record struct VulkanWorldUiDrawToken(
    ulong NativeToken, ulong Serial);
