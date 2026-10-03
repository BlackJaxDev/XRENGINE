namespace XREngine.RenderBench;

/// <summary>Codec/ring/slab measurements, explicitly separate from socket and authority admission costs.</summary>
public sealed record RuntimeNetworkMeasurement(
    string Channel, int Avatars, int Repeat, int Iterations, int BytesPerTick,
    long SendAllocatedBytes, long ReceiveAllocatedBytes, long RelayAllocatedBytes,
    double SendMilliseconds, double ReceiveMilliseconds, double RelayMilliseconds);
