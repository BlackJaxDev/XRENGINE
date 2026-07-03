using System;
using System.Diagnostics;
using System.Threading;
using XREngine.Data.Runtime.Memory;

namespace XREngine;

public static partial class Engine
{
#if !XRE_PUBLISHED
    public static ThreadAllocationTracker Allocations { get; } = new();

    public sealed class ThreadAllocationTracker
    {
        private readonly AllocationRing _render = new(240);
        private readonly AllocationRing _collectSwap = new(240);
        private readonly AllocationRing _update = new(240);
        private readonly AllocationRing _fixedUpdate = new(240);
        private readonly Dictionary<string, AllocationScopeRing> _scopes = new(StringComparer.Ordinal);
        private readonly object _scopesLock = new();

        public void RecordRender(long bytes) => _render.Add(bytes);
        public void RecordCollectSwap(long bytes) => _collectSwap.Add(bytes);
        public void RecordUpdateTick(long bytes) => _update.Add(bytes);
        public void RecordFixedUpdateTick(long bytes) => _fixedUpdate.Add(bytes);

        public AllocationScope BeginScope(
            string name,
            AllocationScopeCategory category,
            long budgetBytes = -1)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = "<unnamed>";

            return new AllocationScope(
                this,
                name,
                category,
                budgetBytes >= 0 ? budgetBytes : GetDefaultBudget(category),
                GC.GetAllocatedBytesForCurrentThread());
        }

        public void RecordScope(
            string name,
            AllocationScopeCategory category,
            long budgetBytes,
            long bytes)
        {
            if (bytes < 0)
                bytes = 0;

            AllocationScopeRing ring;
            lock (_scopesLock)
            {
                if (!_scopes.TryGetValue(name, out ring!))
                {
                    ring = new AllocationScopeRing(name, category, 240);
                    _scopes.Add(name, ring);
                }
            }

            bool overBudget = ring.Add(bytes, budgetBytes);
            if (overBudget && ring.ShouldLogOverBudget())
            {
                Debug.LogWarning(
                    ELogCategory.General,
                    $"[AllocBudget] Scope '{name}' allocated {bytes} bytes with budget {budgetBytes} bytes.");
            }
        }

        public ThreadAllocationSnapshot GetSnapshot()
            => new(
                _render.GetSnapshot(),
                _collectSwap.GetSnapshot(),
                _update.GetSnapshot(),
                _fixedUpdate.GetSnapshot(),
                GetScopeSnapshots());

        private AllocationScopeSnapshot[] GetScopeSnapshots()
        {
            lock (_scopesLock)
            {
                if (_scopes.Count == 0)
                    return [];

                AllocationScopeSnapshot[] snapshots = new AllocationScopeSnapshot[_scopes.Count];
                int index = 0;
                foreach (AllocationScopeRing ring in _scopes.Values)
                    snapshots[index++] = ring.GetSnapshot();

                Array.Sort(snapshots, static (left, right) => right.MaxBytes.CompareTo(left.MaxBytes));
                return snapshots;
            }
        }

        private static long GetDefaultBudget(AllocationScopeCategory category)
            => category switch
            {
                AllocationScopeCategory.RenderPass => 0L,
                AllocationScopeCategory.RenderSubmission => 0L,
                AllocationScopeCategory.EcsSystem => 0L,
                AllocationScopeCategory.NetworkCodec => 0L,
                AllocationScopeCategory.VrInput => 0L,
                AllocationScopeCategory.AnimationIk => 0L,
                AllocationScopeCategory.GpuUploadPreparation => 0L,
                AllocationScopeCategory.EditorUi => 32 * 1024L,
                AllocationScopeCategory.Diagnostics => 16 * 1024L,
                _ => 0L,
            };

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

        private sealed class AllocationScopeRing
        {
            private static readonly long LogThrottleTicks = Stopwatch.Frequency;
            private readonly AllocationRing _ring;
            private readonly object _lock = new();
            private long _sampleCount;
            private long _overBudgetCount;
            private long _lastBudgetBytes;
            private long _lastOverBudgetLogTicks;

            public AllocationScopeRing(string name, AllocationScopeCategory category, int capacity)
            {
                Name = name;
                Category = category;
                _ring = new AllocationRing(capacity);
            }

            public string Name { get; }
            public AllocationScopeCategory Category { get; }

            public bool Add(long bytes, long budgetBytes)
            {
                _ring.Add(bytes);
                lock (_lock)
                {
                    _sampleCount++;
                    _lastBudgetBytes = budgetBytes;
                    if (budgetBytes >= 0 && bytes > budgetBytes)
                    {
                        _overBudgetCount++;
                        return true;
                    }
                }

                return false;
            }

            public bool ShouldLogOverBudget()
            {
                long now = Stopwatch.GetTimestamp();
                long previous = Interlocked.Read(ref _lastOverBudgetLogTicks);
                if (previous != 0 && now - previous < LogThrottleTicks)
                    return false;

                Interlocked.Exchange(ref _lastOverBudgetLogTicks, now);
                return true;
            }

            public AllocationScopeSnapshot GetSnapshot()
            {
                AllocationRingSnapshot ring = _ring.GetSnapshot();
                lock (_lock)
                {
                    return new AllocationScopeSnapshot(
                        Name,
                        Category.ToString(),
                        _lastBudgetBytes,
                        ring.LastBytes,
                        ring.AverageBytes,
                        ring.MaxBytes,
                        ring.Samples,
                        ring.Capacity,
                        _overBudgetCount);
                }
            }
        }
    }

    public readonly struct AllocationScope : IDisposable
    {
        private readonly ThreadAllocationTracker? _tracker;
        private readonly string? _name;
        private readonly AllocationScopeCategory _category;
        private readonly long _budgetBytes;
        private readonly long _startBytes;

        internal AllocationScope(
            ThreadAllocationTracker tracker,
            string name,
            AllocationScopeCategory category,
            long budgetBytes,
            long startBytes)
        {
            _tracker = tracker;
            _name = name;
            _category = category;
            _budgetBytes = budgetBytes;
            _startBytes = startBytes;
        }

        public void Dispose()
        {
            ThreadAllocationTracker? tracker = _tracker;
            string? name = _name;
            if (tracker is null || name is null)
                return;

            tracker.RecordScope(
                name,
                _category,
                _budgetBytes,
                GC.GetAllocatedBytesForCurrentThread() - _startBytes);
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
        AllocationRingSnapshot FixedUpdate,
        AllocationScopeSnapshot[] Scopes);

    public readonly record struct AllocationScopeSnapshot(
        string Name,
        string Category,
        long BudgetBytes,
        long LastBytes,
        double AverageBytes,
        long MaxBytes,
        int Samples,
        int Capacity,
        long OverBudgetCount)
    {
        public double LastKB => LastBytes / 1024.0;
        public double AverageKB => AverageBytes / 1024.0;
        public double MaxKB => MaxBytes / 1024.0;
    }
#else
    public static ThreadAllocationTracker Allocations { get; } = new();

    public sealed class ThreadAllocationTracker
    {
        public void RecordRender(long bytes) { }
        public void RecordCollectSwap(long bytes) { }
        public void RecordUpdateTick(long bytes) { }
        public void RecordFixedUpdateTick(long bytes) { }
        public AllocationScope BeginScope(string name, AllocationScopeCategory category, long budgetBytes = -1) => default;
        public void RecordScope(string name, AllocationScopeCategory category, long budgetBytes, long bytes) { }

        public ThreadAllocationSnapshot GetSnapshot()
            => new(default, default, default, default, []);
    }

    public readonly struct AllocationScope : IDisposable
    {
        public void Dispose() { }
    }

    public readonly record struct AllocationRingSnapshot(long LastBytes, double AverageBytes, long MaxBytes, int Samples, int Capacity)
    {
        public double LastKB => 0.0;
        public double AverageKB => 0.0;
        public double MaxKB => 0.0;
    }

    public readonly record struct ThreadAllocationSnapshot(
        AllocationRingSnapshot Render,
        AllocationRingSnapshot CollectSwap,
        AllocationRingSnapshot Update,
        AllocationRingSnapshot FixedUpdate,
        AllocationScopeSnapshot[] Scopes);

    public readonly record struct AllocationScopeSnapshot(
        string Name,
        string Category,
        long BudgetBytes,
        long LastBytes,
        double AverageBytes,
        long MaxBytes,
        int Samples,
        int Capacity,
        long OverBudgetCount)
    {
        public double LastKB => 0.0;
        public double AverageKB => 0.0;
        public double MaxKB => 0.0;
    }
#endif
}
