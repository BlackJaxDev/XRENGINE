using System;
using System.Collections.Generic;

namespace XREngine.Rendering.UI;

/// <summary>Per-cohort stable glyph storage. A separate index stream defines painter order.</summary>
internal sealed class RetainedGlyphRanges
{
    internal sealed class Allocation(uint offset, uint capacity)
    {
        public uint Offset = offset;
        public uint Capacity = capacity;
        public object? Layout;
        public int TextIndex = -1;
        internal ulong SeenEpoch;
    }

    private readonly Dictionary<(ulong Handle, int FallbackIndex), Allocation> _allocations = [];
    private readonly List<(uint Offset, uint Length)> _free = [];
    private readonly List<(ulong Handle, int FallbackIndex)> _stale = [];
    private uint _highWater;
    private ulong _epoch;

    public uint HighWater => _highWater;
    public void BeginFrame() => _epoch++;

    public Allocation Reserve(ulong handle, int fallbackIndex, int glyphCount)
    {
        if (glyphCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(glyphCount));
        var key = (handle, handle == 0 ? fallbackIndex : 0);
        uint required = (uint)glyphCount;
        if (_allocations.TryGetValue(key, out Allocation? current))
        {
            if (required <= current.Capacity)
            {
                current.SeenEpoch = _epoch;
                return current;
            }
            _allocations.Remove(key);
            Release(current.Offset, current.Capacity);
        }
        uint capacity = 1;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Allocation allocation = new(Allocate(capacity), capacity) { SeenEpoch = _epoch };
        _allocations.Add(key, allocation);
        return allocation;
    }

    public void EndFrame()
    {
        _stale.Clear();
        foreach (var pair in _allocations)
            if (pair.Value.SeenEpoch != _epoch)
                _stale.Add(pair.Key);
        foreach (var key in _stale)
        {
            Allocation old = _allocations[key];
            _allocations.Remove(key);
            Release(old.Offset, old.Capacity);
        }
    }

    private uint Allocate(uint length)
    {
        for (int index = 0; index < _free.Count; index++)
        {
            var span = _free[index];
            if (span.Length < length)
                continue;
            uint offset = span.Offset;
            if (span.Length == length)
                _free.RemoveAt(index);
            else
                _free[index] = (checked(offset + length), span.Length - length);
            return offset;
        }
        uint result = _highWater;
        _highWater = checked(_highWater + length);
        return result;
    }

    private void Release(uint offset, uint length)
    {
        int index = 0;
        while (index < _free.Count && _free[index].Offset < offset)
            index++;
        _free.Insert(index, (offset, length));
        if (index > 0 && _free[index - 1].Offset + _free[index - 1].Length == offset)
        {
            var previous = _free[index - 1];
            _free[index - 1] = (previous.Offset, checked(previous.Length + length));
            _free.RemoveAt(index);
            index--;
        }
        while (index + 1 < _free.Count &&
            _free[index].Offset + _free[index].Length == _free[index + 1].Offset)
        {
            var current = _free[index];
            _free[index] = (current.Offset, checked(current.Length + _free[index + 1].Length));
            _free.RemoveAt(index + 1);
        }
        while (_free.Count > 0)
        {
            var tail = _free[^1];
            if (tail.Offset + tail.Length != _highWater)
                break;
            _highWater = tail.Offset;
            _free.RemoveAt(_free.Count - 1);
        }
    }
}
