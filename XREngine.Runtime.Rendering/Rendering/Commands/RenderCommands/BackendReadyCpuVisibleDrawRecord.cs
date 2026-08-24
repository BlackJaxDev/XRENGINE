namespace XREngine.Rendering.Commands;

/// <summary>
/// CPU-visible canonical draw retained for direct submission or parity checks.
/// </summary>
public readonly record struct BackendReadyCpuVisibleDrawRecord(
    AdvancedGpuHandle DrawHandle,
    uint ViewId,
    int PassIndex,
    uint InstanceCount,
    ulong OrderKey);
