namespace XREngine.Browser;

/// <summary>Payload accounting; estimates exclude managed object headers and GPU driver allocations.</summary>
public sealed record BrowserCookedContentStatistics(
    int Assets, int Meshes, int Materials, int Textures, int Instances,
    long CpuRetainedPayloadBytes, long EstimatedGpuResourceBytes,
    long PeakManagedBridgePayloadBytes, long PeakDecodeScratchPayloadBytes,
    long PeakManagedResourceUploadCopyBytes,
    long TotalUploadedPayloadBytes, bool Failed)
{
    public int AnimationAssets { get; init; }
    public int AnimatedInstances { get; init; }
    public int CollisionBoxes { get; init; }
}
