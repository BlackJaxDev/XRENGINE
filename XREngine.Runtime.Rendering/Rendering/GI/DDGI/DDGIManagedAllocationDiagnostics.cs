using System;
using System.Collections.Generic;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Development-only rolling managed-allocation samples for DDGI render commands.
/// Callers gate measurement through the host profiling service before entering a scope.
/// </summary>
public static class DDGIManagedAllocationDiagnostics
{
    private const int SampleCapacity = 240;
    private static readonly Dictionary<string, AllocationRing> s_scopes = new(StringComparer.Ordinal);
    private static readonly object s_gate = new();

    /// <summary>Starts one command-local allocation measurement without allocating.</summary>
    public static Scope Begin(string name)
        => new(name, GC.GetAllocatedBytesForCurrentThread());

    /// <summary>Returns a cold-path snapshot of the rolling command samples.</summary>
    public static DDGIManagedAllocationScopeSnapshot[] CaptureSnapshots()
    {
        lock (s_gate)
        {
            var snapshots = new DDGIManagedAllocationScopeSnapshot[s_scopes.Count];
            int index = 0;
            foreach ((string name, AllocationRing ring) in s_scopes)
                snapshots[index++] = ring.Capture(name);
            Array.Sort(snapshots, static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            return snapshots;
        }
    }

    private static void Record(string name, long bytes)
    {
        lock (s_gate)
        {
            if (!s_scopes.TryGetValue(name, out AllocationRing? ring))
            {
                ring = new AllocationRing();
                s_scopes.Add(name, ring);
            }
            ring.Add(Math.Max(bytes, 0L));
        }
    }

    /// <summary>Allocation-free scope value used only while diagnostics are enabled.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _startBytes;

        internal Scope(string name, long startBytes)
        {
            _name = name;
            _startBytes = startBytes;
        }

        public void Dispose()
        {
            if (_name is not null)
                Record(_name, GC.GetAllocatedBytesForCurrentThread() - _startBytes);
        }
    }

    private sealed class AllocationRing
    {
        private readonly long[] _values = new long[SampleCapacity];
        private int _count;
        private int _index;
        private long _sum;
        private long _last;
        private long _max;
        private long _overBudgetCount;

        public void Add(long bytes)
        {
            _last = bytes;
            if (_count < _values.Length)
            {
                _values[_index] = bytes;
                _sum += bytes;
                _count++;
                _max = Math.Max(_max, bytes);
            }
            else
            {
                long replaced = _values[_index];
                _values[_index] = bytes;
                _sum += bytes - replaced;
                if (bytes >= _max)
                    _max = bytes;
                else if (replaced == _max)
                    RecomputeMax();
            }

            if (bytes > 0)
                _overBudgetCount++;

            _index = (_index + 1) % _values.Length;
        }

        public DDGIManagedAllocationScopeSnapshot Capture(string name)
            => new(
                name,
                _last,
                _count == 0 ? 0.0 : (double)_sum / _count,
                _max,
                _count,
                _values.Length,
                _overBudgetCount);

        private void RecomputeMax()
        {
            long max = 0;
            for (int i = 0; i < _count; i++)
                max = Math.Max(max, _values[i]);
            _max = max;
        }
    }
}
