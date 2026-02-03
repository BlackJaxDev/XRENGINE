using System;
using System.Threading;

namespace XREngine;

public static partial class Engine
{
    public static ThreadAllocationTracker Allocations { get; } = new();

    public sealed class ThreadAllocationTracker
    {
        private readonly AllocationRing _render = new(240);
        private readonly AllocationRing _collectSwap = new(240);
        private readonly AllocationRing _update = new(240);
        private readonly AllocationRing _fixedUpdate = new(240);

        public void RecordRender(long bytes) => _render.Add(bytes);
        public void RecordCollectSwap(long bytes) => _collectSwap.Add(bytes);
        public void RecordUpdateTick(long bytes) => _update.Add(bytes);
        public void RecordFixedUpdateTick(long bytes) => _fixedUpdate.Add(bytes);

        public ThreadAllocationSnapshot GetSnapshot()
            => new(
                _render.GetSnapshot(),
                _collectSwap.GetSnapshot(),
                _update.GetSnapshot(),
                _fixedUpdate.GetSnapshot());

        private sealed class AllocationRing
        {
            private readonly long[] _values;
            private readonly object _lock = new();

            private int _index;
            private int _count;
            private long _sum;
            private long _last;
            private long _max;

            public AllocationRing(int capacity)
            {
                _values = new long[capacity];
            }

            public void Add(long bytes)
            {
                if (bytes < 0)
                    bytes = 0;

                lock (_lock)
                {
                    _last = bytes;
                    _max = Math.Max(_max, bytes);

                    if (_count < _values.Length)
                    {
                        _values[_index] = bytes;
                        _sum += bytes;
                        _count++;
                    }
                    else
                    {
                        var old = _values[_index];
                        _values[_index] = bytes;
                        _sum += bytes - old;
                    }

                    _index++;
                    if (_index >= _values.Length)
                        _index = 0;
                }
            }

            public AllocationRingSnapshot GetSnapshot()
            {
                lock (_lock)
                {
                    double avg = _count == 0 ? 0.0 : (double)_sum / _count;
                    return new AllocationRingSnapshot(_last, avg, _max, _count, _values.Length);
                }
            }
        }
    }

    public readonly record struct AllocationRingSnapshot(long LastBytes, double AverageBytes, long MaxBytes, int Samples, int Capacity)
    {
        public double LastKB => LastBytes / 1024.0;
        public double AverageKB => AverageBytes / 1024.0;
        public double MaxKB => MaxBytes / 1024.0;
    }

    public readonly record struct ThreadAllocationSnapshot(
        AllocationRingSnapshot Render,
        AllocationRingSnapshot CollectSwap,
        AllocationRingSnapshot Update,
        AllocationRingSnapshot FixedUpdate);
}
