namespace XREngine.Input;

/// <summary>Copied identity and connection state for a tracked VR device.</summary>
public readonly record struct RuntimeVrDeviceInfo(
    RuntimeVrRuntimeKind Runtime,
    uint DeviceIndex,
    RuntimeVrDeviceClass DeviceClass,
    bool IsConnected,
    string? PersistentIdentity = null);
