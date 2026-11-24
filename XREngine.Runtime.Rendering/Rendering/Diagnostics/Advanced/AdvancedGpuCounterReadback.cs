using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Completed, bounded GPU counter receipt. Values enter this store only after
/// the producer submission has completed; the identity is sealed when the
/// copy is attached to that producer's primary command buffer.
/// </summary>
public sealed record AdvancedGpuCounterReadback(
    string Source,
    ulong FrameId,
    ulong OutputId,
    int ResourceGeneration,
    uint ViewId,
    uint[] Values)
{
    /// <summary>
    /// Gets the meshlets and fragments that the visibility raster dropped because their
    /// payload did not fit: a meshlet index outside the draw's meshlet range, meshlet
    /// vertex or triangle ranges past the end of their buffers, or a fragment whose draw,
    /// primitive, producer, origin, or view did not encode into the visibility word.
    /// The counter copy runs between the early and the late raster, so a receipt holds
    /// only the events of the stages before that point.
    /// </summary>
    public uint PayloadOverflow
        => IsVisibilityReceipt && Values.Length > 6 ? Values[6] : 0u;

    /// <summary>
    /// Gets the reads that the visibility stages found out of bounds and skipped: a
    /// payload index past the payload buffer, a draw, geometry, material, or transform
    /// handle that did not resolve, a skinned meshlet without prepared deformation, or a
    /// vertex index past its vertex buffer. The counter copy runs between the early and
    /// the late raster, so a receipt holds only the events of the stages before that point.
    /// </summary>
    public uint DecodeOutOfBounds
        => IsVisibilityReceipt && Values.Length > 8 ? Values[8] : 0u;

    /// <summary>Gets the unsupported displacement count from a completed visibility receipt.</summary>
    public uint UnsupportedDisplacement
        => IsVisibilityReceipt && Values.Length > 10 ? Values[10] : 0u;

    /// <summary>Gets rejected chain-bound routes from a completed visibility receipt.</summary>
    public uint PhysicsChainBoundsRouteRejected
        => IsVisibilityReceipt && Values.Length > 11 ? Values[11] : 0u;

    /// <summary>Gets invalid chain bounds from a completed visibility receipt.</summary>
    public uint PhysicsChainBoundsNumericRejected
        => IsVisibilityReceipt && Values.Length > 12 ? Values[12] : 0u;

    /// <summary>Gets the meshlets that the early-raster task stage launched for this view.</summary>
    public uint MeshletEarlyVisible
        => IsVisibilityReceipt && Values.Length > 13 ? Values[13] : 0u;

    /// <summary>Gets the meshlets that the early-raster task stage culled against the view frustum.</summary>
    public uint MeshletEarlyFrustumCulled
        => IsVisibilityReceipt && Values.Length > 14 ? Values[14] : 0u;

    /// <summary>Gets the meshlets that the early-raster task stage culled with their normal cone.</summary>
    public uint MeshletEarlyConeCulled
        => IsVisibilityReceipt && Values.Length > 15 ? Values[15] : 0u;

    /// <summary>Gets the meshlets that the early-raster task stage culled with the previous-frame visibility history.</summary>
    public uint MeshletEarlyHistoryCulled
        => IsVisibilityReceipt && Values.Length > 38 ? Values[38] : 0u;

    /// <summary>Gets the triangles of the meshlets that the early-raster task stage launched for this view.</summary>
    public uint MeshletEarlyTriangles
        => IsVisibilityReceipt && Values.Length > 39 ? Values[39] : 0u;

    private bool IsVisibilityReceipt
        => Source.StartsWith("AdvancedVisibilityCounters", StringComparison.Ordinal);
}

/// <summary>Immutable producer identity sealed with an Advanced counter copy.</summary>
public readonly record struct AdvancedGpuCounterReadbackIdentity(
    ulong FrameId,
    ulong OutputId,
    int ResourceGeneration,
    uint ViewId);

/// <summary>Thread-safe fixed-capacity publication for completed counter receipts.</summary>
public static class AdvancedGpuCounterReadbacks
{
    private const int Capacity = 64;
    private static readonly object Sync = new();
    private static readonly AdvancedGpuCounterReadback?[] Receipts = new AdvancedGpuCounterReadback?[Capacity];
    private static int _next;

    public static void Publish(AdvancedGpuCounterReadback receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        lock (Sync)
        {
            Receipts[_next] = receipt;
            _next = (_next + 1) % Capacity;
        }
    }

    /// <summary>Returns detached completed receipts in newest-first order.</summary>
    public static AdvancedGpuCounterReadback[] CaptureSnapshot()
    {
        lock (Sync)
        {
            var result = new List<AdvancedGpuCounterReadback>(Capacity);
            for (int offset = 1; offset <= Capacity; offset++)
            {
                int index = (_next - offset + Capacity) % Capacity;
                if (Receipts[index] is { } receipt)
                    result.Add(receipt);
            }

            return [.. result];
        }
    }
}
