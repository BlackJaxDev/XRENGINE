namespace XREngine.Rendering.Vulkan;

/// <summary>Explains whether a measured interval advanced work or waited for another authority.</summary>
public enum EVulkanFrameIntervalClass
{
    Work,
    Wait,
    Driver,
    External,
    Diagnostic,
}
