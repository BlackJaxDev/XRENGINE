namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies the output targeted by a logical Vulkan frame.</summary>
public readonly record struct VulkanFrameOutputIdentity(int OutputIndex, ulong OutputGeneration);
