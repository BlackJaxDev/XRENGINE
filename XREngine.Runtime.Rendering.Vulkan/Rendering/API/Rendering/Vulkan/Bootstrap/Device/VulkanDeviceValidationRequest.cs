namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable diagnostic and validation policy consumed while creating one
/// Vulkan instance. It contains facts only; runtime settings and renderer
/// authorities are resolved by the composition root before this request is made.
/// </summary>
internal sealed class VulkanDeviceValidationRequest
{
    public VulkanDeviceValidationRequest(
        EVulkanDiagnosticPreset preset,
        EVulkanDiagnosticFlags flags,
        bool enableValidationLayers,
        bool enableSynchronizationValidation,
        bool enableGpuAssistedValidation,
        bool enableBestPractices,
        bool enableDebugUtils,
        bool enableCommandBufferLabels,
        bool enableCrashBreadcrumbs,
        string sourceSummary,
        string overheadWarnings)
    {
        Preset = preset;
        Flags = flags;
        EnableValidationLayers = enableValidationLayers;
        EnableSynchronizationValidation = enableSynchronizationValidation;
        EnableGpuAssistedValidation = enableGpuAssistedValidation;
        EnableBestPractices = enableBestPractices;
        EnableDebugUtils = enableDebugUtils;
        EnableCommandBufferLabels = enableCommandBufferLabels;
        EnableCrashBreadcrumbs = enableCrashBreadcrumbs;
        SourceSummary = sourceSummary ?? string.Empty;
        OverheadWarnings = overheadWarnings ?? string.Empty;
    }

    public EVulkanDiagnosticPreset Preset { get; }
    public EVulkanDiagnosticFlags Flags { get; }
    public bool EnableValidationLayers { get; }
    public bool EnableSynchronizationValidation { get; }
    public bool EnableGpuAssistedValidation { get; }
    public bool EnableBestPractices { get; }
    public bool EnableDebugUtils { get; }
    public bool EnableCommandBufferLabels { get; }
    public bool EnableCrashBreadcrumbs { get; }
    public string SourceSummary { get; }
    public string OverheadWarnings { get; }
}
