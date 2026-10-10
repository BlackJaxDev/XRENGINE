namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <summary>Copies retained first failures from timeline fence rentals.</summary>
    public GpuFenceFailureDiagnostic[] CaptureGpuFenceFailureHistory()
        => _commandRuntime.CaptureGpuFenceFailureHistory();
}
