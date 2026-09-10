namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free recording counters for the native opaque shading root ABI.</summary>
public readonly record struct VulkanNativeShadingRootDiagnosticSnapshot(
    EVulkanNativeShadingRootMode RequestedMode,
    long RecordedImmediateDispatches,
    long RecordedAddressDispatches,
    long ImmediatePushBytes,
    long AddressPushBytes,
    long ParameterUploadBytes,
    long ParameterBlocks);
