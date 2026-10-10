using System.Runtime.CompilerServices;

namespace XREngine.Components;

/// <summary>Owns pinned, cache-line-aligned counters for CPU range workers.</summary>
internal sealed unsafe class PhysicsChainCpuWorkerCounters
{
    private const int CacheLineBytes = 64;
    private const int SlotBytes = 128;
    private readonly byte[] _storage;
    private readonly int _alignedOffset;
    private readonly int _slotCount;

    internal PhysicsChainCpuWorkerCounters(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        _slotCount = slotCount;
        int usedBytes = checked(slotCount * SlotBytes);
        _storage = GC.AllocateUninitializedArray<byte>(
            checked(usedBytes + CacheLineBytes - 1), pinned: true);

        nuint storageAddress = (nuint)Unsafe.AsPointer(ref _storage[0]);
        _alignedOffset = (int)((CacheLineBytes -
            (storageAddress & (CacheLineBytes - 1))) & (CacheLineBytes - 1));
        if (((storageAddress + (nuint)_alignedOffset) & (CacheLineBytes - 1)) != 0)
            throw new InvalidOperationException("CPU worker counter storage is not cache-line aligned.");

        Clear();
    }

    internal int SlotCount => _slotCount;

    /// <summary>Clears all claim, completion, and failure counters.</summary>
    internal void Clear()
        => _storage.AsSpan(_alignedOffset, checked(_slotCount * SlotBytes)).Clear();

    /// <summary>Gets the claim or completion counter at the start of a slot.</summary>
    internal ref int CompletedRangeCount(int slot)
        => ref Unsafe.As<byte, int>(ref _storage[_alignedOffset + slot * SlotBytes]);

    /// <summary>Gets the failure counter in a separate cache line.</summary>
    internal ref int FailedRangeCount(int slot)
        => ref Unsafe.As<byte, int>(ref _storage[_alignedOffset + slot * SlotBytes + CacheLineBytes]);
}
