using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>One ordered draw retained outside stable bins with its exact cause.</summary>
internal readonly record struct VulkanBinOrderedException(
    AdvancedGpuSceneDrawIdentitySnapshot Draw,
    VulkanBinOrderedExceptionReason Reason,
    ulong Sequence);

/// <summary>Reasons that intentionally prevent binning; none imply fallback.</summary>
internal enum VulkanBinOrderedExceptionReason : byte
{
    Transparency = 1,
    Ui = 2,
    Callback = 3,
    PreserveSubmissionOrder = 4,
    ExternalTarget = 5,
    UnsupportedCustomWork = 6,
    MissingCanonicalIdentity = 7,
    MissingResidentTemplate = 8,
    TopologyRejected = 9,
}

/// <summary>
/// Bounded ordered exception stream. It retains source order and reports
/// saturation explicitly, never silently changing a submission strategy.
/// Storage starts small and doubles on a new high-water mark up to
/// <see cref="Capacity"/>; the owning stable-bin stream appends only while it
/// is mutable.
/// </summary>
internal sealed class VulkanBinOrderedExceptionStream
{
    private const int InitialCapacity = 16;

    private readonly int _maximumCapacity;
    private VulkanBinOrderedException[] _entries;
    private int _count;

    internal VulkanBinOrderedExceptionStream(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _maximumCapacity = capacity;
        _entries = new VulkanBinOrderedException[Math.Min(capacity, InitialCapacity)];
    }

    internal int Count => _count;
    /// <summary>Admission limit; the backing storage may currently be smaller.</summary>
    internal int Capacity => _maximumCapacity;
    internal ReadOnlySpan<VulkanBinOrderedException> Entries => _entries.AsSpan(0, _count);

    internal bool TryAppend(
        in AdvancedGpuSceneDrawIdentitySnapshot draw,
        VulkanBinOrderedExceptionReason reason,
        ulong sequence)
    {
        if (reason == 0 || _count == _maximumCapacity)
            return false;
        if (_count == _entries.Length)
            Array.Resize(ref _entries, Math.Min(_maximumCapacity, Math.Max(InitialCapacity, _entries.Length * 2)));
        _entries[_count++] = new(draw, reason, sequence);
        return true;
    }

    internal void Clear() => _count = 0;
}
