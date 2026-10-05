namespace XREngine.Rendering.Vulkan;

/// <summary>Counts records lost or invalidated during a targeted CPU capture.</summary>
public readonly record struct VulkanCpuCaptureDiagnostics(
    long OverwrittenSpans,
    long UnwarmedThreadSpans,
    long InvalidNesting)
{
    public bool Complete => OverwrittenSpans == 0 && UnwarmedThreadSpans == 0 && InvalidNesting == 0;
}
