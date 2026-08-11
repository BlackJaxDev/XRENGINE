namespace XREngine.Rendering.Vulkan;

/// <summary>One named text or binary artifact ready for persistence.</summary>
internal readonly record struct VulkanDeviceFaultArtifact(
    string FileName,
    byte[] Content,
    bool IsBinary);
