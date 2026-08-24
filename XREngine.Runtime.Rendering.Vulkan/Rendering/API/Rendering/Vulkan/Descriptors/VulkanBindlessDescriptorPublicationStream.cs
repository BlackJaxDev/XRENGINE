using System;
using System.Runtime.CompilerServices;

using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Device-owned, column-oriented publication stream for the bindless material texture table.
/// Slot indices are stable publication identifiers; native descriptor structures are only
/// materialized into the reusable scratch columns immediately before the Vulkan call.
/// </summary>
internal sealed class VulkanBindlessDescriptorPublicationStream
{
    private uint[] _dirtySlotIds = [];
    private uint[] _rangeStarts = [];
    private uint[] _rangeCounts = [];

    internal DescriptorImageInfo[] ImageInfoScratch = [];
    internal WriteDescriptorSet[] WriteScratch = [];

    internal int DirtyCount { get; private set; }
    internal int RangeCount { get; private set; }
    internal ulong SlotsScannedTotal { get; private set; }
    internal ulong SlotsDirtyTotal { get; private set; }
    internal ulong RangesPublishedTotal { get; private set; }
    internal ulong ImageInfoElementsTotal { get; private set; }
    internal ulong WriteElementsTotal { get; private set; }
    internal ulong NativeBytesTotal { get; private set; }
    internal ulong CompatibilityTicksTotal { get; private set; }
    internal int HighWaterMark { get; private set; }

    internal ReadOnlySpan<uint> DirtySlotIds => _dirtySlotIds.AsSpan(0, DirtyCount);
    internal ReadOnlySpan<uint> RangeStarts => _rangeStarts.AsSpan(0, RangeCount);
    internal ReadOnlySpan<uint> RangeCounts => _rangeCounts.AsSpan(0, RangeCount);

    internal void Initialize(uint capacity)
    {
        int length = checked((int)capacity);
        _dirtySlotIds = new uint[length];
        _rangeStarts = new uint[length];
        _rangeCounts = new uint[length];
        ImageInfoScratch = new DescriptorImageInfo[length];
        WriteScratch = new WriteDescriptorSet[length];
        DirtyCount = 0;
        RangeCount = 0;
        HighWaterMark = 0;
    }

    internal void Reset() => DirtyCount = RangeCount = 0;

    internal void AppendDirty(uint slotId)
    {
        if ((uint)DirtyCount >= (uint)_dirtySlotIds.Length)
            throw new InvalidOperationException("Bindless descriptor publication stream exceeded its fixed slot capacity.");

        _dirtySlotIds[DirtyCount++] = slotId;
        if (DirtyCount > HighWaterMark)
            HighWaterMark = DirtyCount;
    }

    /// <summary>Sorts stable slot IDs and produces contiguous publication ranges without allocations.</summary>
    internal void BuildDirtyRanges()
    {
        SlotsScannedTotal += (uint)DirtyCount;
        if (DirtyCount == 0)
        {
            RangeCount = 0;
            return;
        }

        Array.Sort(_dirtySlotIds, 0, DirtyCount);
        int rangeIndex = 0;
        uint start = _dirtySlotIds[0];
        uint previous = start;
        for (int i = 1; i < DirtyCount; i++)
        {
            uint current = _dirtySlotIds[i];
            if (current == previous + 1u)
            {
                previous = current;
                continue;
            }

            _rangeStarts[rangeIndex] = start;
            _rangeCounts[rangeIndex++] = previous - start + 1u;
            start = previous = current;
        }

        _rangeStarts[rangeIndex] = start;
        _rangeCounts[rangeIndex++] = previous - start + 1u;
        RangeCount = rangeIndex;
    }

    internal void RecordPublication(ulong compatibilityTicks)
    {
        SlotsDirtyTotal += (uint)DirtyCount;
        RangesPublishedTotal += (uint)RangeCount;
        ImageInfoElementsTotal += (uint)DirtyCount;
        WriteElementsTotal += (uint)RangeCount;
        NativeBytesTotal += (ulong)DirtyCount * (uint)Unsafe.SizeOf<DescriptorImageInfo>() +
            (ulong)RangeCount * (uint)Unsafe.SizeOf<WriteDescriptorSet>();
        CompatibilityTicksTotal += compatibilityTicks;
    }
}
