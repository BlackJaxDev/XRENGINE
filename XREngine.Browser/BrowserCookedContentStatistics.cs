namespace XREngine.Browser;

/// <summary>Payload accounting; estimates exclude managed object headers and GPU driver allocations.</summary>
public sealed record BrowserCookedContentStatistics(
    int Assets, int Meshes, int Materials, int Textures, int Instances,
    long CpuRetainedPayloadBytes, long EstimatedGpuResourceBytes,
    long PeakManagedBridgePayloadBytes, long PeakDecodeScratchPayloadBytes,
    long PeakManagedResourceUploadCopyBytes,
    long TotalUploadedPayloadBytes, bool Failed);
