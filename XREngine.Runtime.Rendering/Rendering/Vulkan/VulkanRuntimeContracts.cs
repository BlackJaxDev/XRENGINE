namespace XREngine;

public enum EVulkanAllocatorBackend
{
    Legacy,
    Suballocator,
}

public enum EVulkanSynchronizationBackend
{
    Legacy,
    Sync2,
}

public enum EVulkanDescriptorUpdateBackend
{
    Legacy,
    Template,
}

[Flags]
public enum EVulkanUpscaleBridgeSurfaceSet
{
    None = 0,
    SourceColor = 1 << 0,
    SourceDepth = 1 << 1,
    SourceMotion = 1 << 2,
    OutputColor = 1 << 3,
    Exposure = 1 << 4,
}

public enum EVulkanUpscaleBridgeOwnershipMode
{
    PerViewport,
}

public enum EVulkanUpscaleBridgeInteropMode
{
    CopyResolve,
}

public enum EVulkanUpscaleBridgeQueueModel
{
    Graphics,
}

public sealed record class VulkanUpscaleBridgeCapabilitySnapshot
{
    public bool EnvironmentEnabled { get; init; }
    public bool WindowsOnly { get; init; }
    public bool MonoViewportOnly { get; init; }
    public bool HdrSupported { get; init; }
    public bool DlssFirst { get; init; }
    public EVulkanUpscaleBridgeQueueModel QueueModel { get; init; }
    public EVulkanUpscaleBridgeOwnershipMode OwnershipMode { get; init; }
    public EVulkanUpscaleBridgeInteropMode InteropMode { get; init; }
    public EVulkanUpscaleBridgeSurfaceSet SurfaceSet { get; init; }
    public bool HasOpenGlExternalMemory { get; init; }
    public bool HasOpenGlExternalMemoryWin32 { get; init; }
    public bool HasOpenGlSemaphore { get; init; }
    public bool HasOpenGlSemaphoreWin32 { get; init; }
    public bool VulkanProbeSucceeded { get; init; }
    public bool HasVulkanExternalMemoryImport { get; init; }
    public bool HasVulkanExternalSemaphoreImport { get; init; }
    public string? OpenGlVendor { get; init; }
    public string? OpenGlRenderer { get; init; }
    public string? VulkanDeviceName { get; init; }
    public uint VulkanVendorId { get; init; }
    public uint VulkanDeviceId { get; init; }
    public bool? SamePhysicalGpu { get; init; }
    public string? GpuIdentityReason { get; init; }
    public string? ProbeFailureReason { get; init; }
    public string Fingerprint { get; init; } = string.Empty;

    public bool HasRequiredOpenGlInterop
        => HasOpenGlExternalMemory && HasOpenGlExternalMemoryWin32 && HasOpenGlSemaphore && HasOpenGlSemaphoreWin32;

    public bool HasRequiredVulkanInterop
        => VulkanProbeSucceeded && HasVulkanExternalMemoryImport && HasVulkanExternalSemaphoreImport;
}