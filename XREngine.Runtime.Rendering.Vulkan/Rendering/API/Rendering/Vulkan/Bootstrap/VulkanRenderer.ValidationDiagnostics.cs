namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <summary>
    /// Copies validation totals and bounded message samples for the current device.
    /// Unlike frame counters, these totals retain errors between diagnostic reads.
    /// </summary>
    public VulkanValidationDiagnosticSnapshot CaptureValidationDiagnostics()
        => _deviceContext.ValidationDiagnostics.CaptureSnapshot(
            _deviceContext.ValidationLayersEnabled,
            _deviceContext.SynchronizationValidationEnabled,
            _deviceContext.HasDebugMessenger);
}
