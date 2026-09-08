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
    uint[] Values);

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
