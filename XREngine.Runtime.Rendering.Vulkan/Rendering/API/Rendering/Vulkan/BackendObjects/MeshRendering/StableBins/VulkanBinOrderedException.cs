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
/// Fixed-capacity ordered exception stream. It retains source order and reports
/// saturation explicitly, never silently changing a submission strategy.
/// </summary>
internal sealed class VulkanBinOrderedExceptionStream
{
    private readonly VulkanBinOrderedException[] _entries;
    private int _count;

    internal VulkanBinOrderedExceptionStream(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _entries = new VulkanBinOrderedException[capacity];
    }

    internal int Count => _count;
    internal int Capacity => _entries.Length;
    internal ReadOnlySpan<VulkanBinOrderedException> Entries => _entries.AsSpan(0, _count);

    internal bool TryAppend(
        in AdvancedGpuSceneDrawIdentitySnapshot draw,
        VulkanBinOrderedExceptionReason reason,
        ulong sequence)
    {
        if (reason == 0 || _count == _entries.Length)
            return false;
        _entries[_count++] = new(draw, reason, sequence);
        return true;
    }

    internal void Clear() => _count = 0;
}
