namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable diagnostic policy and queried feature facts needed to report the
/// optional Vulkan device-diagnostics paths.
/// </summary>
internal readonly record struct VulkanDiagnosticCapabilitySnapshot(
    bool RequestDeviceFault,
    bool RequestDeviceAddressBindingReport,
    bool RequestNvDiagnosticCheckpoints,
    bool RequestNvDiagnosticsConfig,
    bool KhrDeviceFaultExtensionAvailable,
    bool KhrDeviceFaultExtensionEnabled,
    bool KhrDeviceFaultFeatureSupported,
    bool KhrDeviceFaultVendorBinaryFeatureSupported,
    bool KhrDeviceFaultReportMaskedFeatureSupported,
    bool KhrDeviceFaultDeviceLostOnMaskedFeatureSupported,
    uint KhrDeviceFaultMaxReportCount,
    bool ExtDeviceFaultExtensionAvailable,
    bool ExtDeviceFaultExtensionEnabled,
    bool ExtDeviceFaultFeatureSupported,
    bool ExtDeviceFaultVendorBinaryFeatureSupported,
    bool DeviceAddressBindingReportExtensionAvailable,
    bool DeviceAddressBindingReportExtensionEnabled,
    bool DeviceAddressBindingReportFeatureSupported,
    bool NvDiagnosticCheckpointsExtensionAvailable,
    bool NvDiagnosticCheckpointsExtensionEnabled,
    bool NvDiagnosticsConfigExtensionAvailable,
    bool NvDiagnosticsConfigExtensionEnabled,
    bool NvDiagnosticsConfigFeatureSupported);
