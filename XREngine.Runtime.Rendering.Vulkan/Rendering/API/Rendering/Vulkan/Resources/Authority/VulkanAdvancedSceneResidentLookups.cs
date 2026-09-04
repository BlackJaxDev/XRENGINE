using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Completed-slot resident logical-handle image.  Each owner keeps a fixed
/// segment until an explicit completed-slot boundary grows it, so a local
/// lookup mutation never moves any other owner's shader-visible offsets.
/// </summary>
internal sealed class VulkanAdvancedSceneResidentLookups
{
    internal const int OwnerCount = 13;

    private readonly uint[] _capacities = new uint[OwnerCount];
    private readonly uint[] _counts = new uint[OwnerCount];
    private readonly ulong[] _lookupGenerations = new ulong[OwnerCount];
    private readonly ulong[] _sequences = new ulong[OwnerCount];

    internal VulkanFrameDataSlice Slice { get; private set; }
    internal ulong DatabaseEpoch { get; private set; }
    internal bool IsAllocated => Slice.IsValid;

    internal uint GetOffset(int owner)
    {
        uint offset = 0u;
        for (int index = 0; index < owner; ++index)
            offset = checked(offset + _capacities[index]);
        return offset;
    }

    internal uint GetCapacity(int owner) => _capacities[owner];

    internal uint GetRequiredCapacity(
        int owner,
        uint sourceCount,
        bool allowBoundaryGrowth)
        => allowBoundaryGrowth
            ? Math.Max(_capacities[owner], Math.Max(sourceCount, 1u))
            : Math.Max(sourceCount, 1u);

    internal uint GetRequiredCapacity(
        ReadOnlySpan<uint> sourceCounts,
        bool allowBoundaryGrowth)
    {
        uint total = 0u;
        for (int index = 0; index < OwnerCount; ++index)
            total = checked(total + GetRequiredCapacity(
                index, sourceCounts[index], allowBoundaryGrowth));
        return total;
    }

    internal bool CanPatch(
        ulong databaseEpoch,
        ReadOnlySpan<uint> sourceCounts)
    {
        if (!IsAllocated || DatabaseEpoch != databaseEpoch)
            return false;

        return MatchesCapacity(sourceCounts);
    }

    internal bool MatchesCapacity(ReadOnlySpan<uint> sourceCounts)
    {
        if (!IsAllocated || sourceCounts.Length != OwnerCount)
            return false;

        for (int index = 0; index < OwnerCount; ++index)
            if (sourceCounts[index] > _capacities[index])
                return false;
        return true;
    }

    internal bool IsOwnerUnchanged(
        int owner,
        ulong lookupGeneration)
        => _lookupGenerations[owner] == lookupGeneration;

    internal void Initialize(
        VulkanFrameDataSlice slice,
        ulong databaseEpoch,
        ReadOnlySpan<uint> capacities,
        ReadOnlySpan<uint> counts,
        ReadOnlySpan<ulong> lookupGenerations,
        ReadOnlySpan<ulong> sequences)
    {
        Slice = slice;
        DatabaseEpoch = databaseEpoch;
        capacities.CopyTo(_capacities);
        counts.CopyTo(_counts);
        lookupGenerations.CopyTo(_lookupGenerations);
        sequences.CopyTo(_sequences);
    }

    internal void StampOwner(
        int owner,
        uint count,
        ulong lookupGeneration,
        ulong sequence)
    {
        _counts[owner] = count;
        _lookupGenerations[owner] = lookupGeneration;
        _sequences[owner] = sequence;
    }

    internal void SetDatabaseEpoch(ulong databaseEpoch) => DatabaseEpoch = databaseEpoch;

    internal void Clear()
    {
        Slice = default;
        DatabaseEpoch = 0u;
        Array.Clear(_capacities);
        Array.Clear(_counts);
        Array.Clear(_lookupGenerations);
        Array.Clear(_sequences);
    }
}
