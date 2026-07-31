namespace XREngine.Rendering.Vulkan;

/// <summary>Reports whether the optional headless WSI instance extension is available.</summary>
internal readonly record struct VulkanHeadlessWsiProbeResult(bool Supported, string Message);
