namespace XREngine.Rendering;

/// <summary>
/// Shared bounded allocation contract for per-pixel linked-list transparency.
/// </summary>
internal static class PpllCapacityContract
{
    public const int NodeStrideBytes = 32;
    public const uint NodesPerPixel = 2u;
    public const uint MinimumNodeCapacity = 1024u;
    public const uint CounterWordCount = 4u;
    public const int ResolveFragmentLimit = 16;
    public const int ResolveTraversalLimit = 256;

    // PPLL is an optional exact-transparency lane. Keep its single persistent
    // node arena bounded independently of display extent.
    private const ulong MaximumNodeBufferBytes = 128UL * 1024UL * 1024UL;
    private const uint MaximumNodeCapacity =
        (uint)(MaximumNodeBufferBytes / NodeStrideBytes);

    public static uint ComputeNodeCapacity(uint width, uint height)
    {
        ulong pixelCount = Math.Max(checked((ulong)width * height), 1UL);
        ulong desiredNodeCount = Math.Max(
            checked(pixelCount * NodesPerPixel),
            MinimumNodeCapacity);
        return checked((uint)Math.Min(desiredNodeCount, MaximumNodeCapacity));
    }

    public static ulong ComputeNodeBufferBytes(uint nodeCapacity)
        => checked((ulong)nodeCapacity * NodeStrideBytes);

    public static uint ResolveActualNodeCapacity(
        XRDataBuffer? buffer,
        uint desiredCapacity)
        => buffer is null ? 0u : Math.Min(buffer.ElementCount, desiredCapacity);
}
