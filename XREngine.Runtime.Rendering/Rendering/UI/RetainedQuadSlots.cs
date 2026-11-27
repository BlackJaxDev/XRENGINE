using System;
using System.Collections.Generic;

namespace XREngine.Rendering.UI;

/// <summary>Stable per-cohort quad storage; a separate draw stream preserves painter order.</summary>
internal sealed class RetainedQuadSlots
{
    private readonly Dictionary<(ulong Handle, int FallbackIndex), Slot> _slots = [];
    private readonly Stack<uint> _free = [];
    private readonly List<(ulong Handle, int FallbackIndex)> _stale = [];
    private uint _highWater;
    private ulong _epoch;

    internal sealed class Slot(uint index)
    {
        public uint Index { get; } = index;
        internal ulong SeenEpoch;
    }

    public uint HighWater => _highWater;
    public void BeginFrame() => _epoch++;

    public Slot Reserve(ulong handle, int fallbackIndex)
    {
        var key = (handle, handle == 0 ? fallbackIndex : 0);
        if (!_slots.TryGetValue(key, out Slot? slot))
        {
            uint index = _free.Count != 0 ? _free.Pop() : _highWater++;
            slot = new Slot(index);
            _slots.Add(key, slot);
        }
        slot.SeenEpoch = _epoch;
        return slot;
    }

    public void EndFrame()
    {
        _stale.Clear();
        foreach (var pair in _slots)
            if (pair.Value.SeenEpoch != _epoch)
                _stale.Add(pair.Key);
        foreach (var key in _stale)
        {
            uint index = _slots[key].Index;
            _slots.Remove(key);
            _free.Push(index);
        }
        // Reclaim only unused tail slots; interior holes remain reusable without moving live payloads.
        while (_free.Count != 0 && _free.Peek() == _highWater - 1)
        {
            _highWater--;
            _free.Pop();
        }
    }
}
