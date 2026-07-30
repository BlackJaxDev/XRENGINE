using System;
using System.Threading;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Stores a bounded set of per-output resize extents without allocating in the render hot path.
/// </summary>
internal sealed class VulkanInteractiveResizePlannerExtentCache
{
    private readonly VulkanInteractiveResizePlannerContextKey[] _keys;
    private readonly VulkanInteractiveResizePlannerExtentSnapshot[] _snapshots;
    private SpinLock _gate = new(enableThreadOwnerTracking: false);
    private int _count;
    private bool _capacityExceededReported;

    /// <summary>
    /// Creates a cache with storage allocated up front.
    /// </summary>
    public VulkanInteractiveResizePlannerExtentCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _keys = new VulkanInteractiveResizePlannerContextKey[capacity];
        _snapshots = new VulkanInteractiveResizePlannerExtentSnapshot[capacity];
    }

    /// <summary>
    /// Gets the number of context snapshots retained for the active resize.
    /// </summary>
    public int Count
    {
        get
        {
            bool lockTaken = false;
            try
            {
                _gate.Enter(ref lockTaken);
                return _count;
            }
            finally
            {
                if (lockTaken)
                    _gate.Exit(useMemoryBarrier: true);
            }
        }
    }

    /// <summary>
    /// Gets the maximum number of planner contexts retained during one resize.
    /// </summary>
    public int Capacity => _keys.Length;

    /// <summary>
    /// Returns the existing snapshot for <paramref name="key"/>, or captures
    /// <paramref name="candidate"/> when the context is first observed. Once capacity is reached,
    /// existing snapshots remain authoritative and additional contexts use their live candidate.
    /// </summary>
    public VulkanInteractiveResizePlannerExtentSnapshot GetOrCapture(
        in VulkanInteractiveResizePlannerContextKey key,
        in VulkanInteractiveResizePlannerExtentSnapshot candidate,
        out bool captured,
        out bool reportCapacityExceeded)
    {
        bool lockTaken = false;
        try
        {
            _gate.Enter(ref lockTaken);

            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] != key)
                    continue;

                captured = false;
                reportCapacityExceeded = false;
                return _snapshots[i];
            }

            if (_count >= _keys.Length)
            {
                captured = false;
                reportCapacityExceeded = !_capacityExceededReported;
                _capacityExceededReported = true;
                return candidate;
            }

            int index = _count++;
            _keys[index] = key;
            _snapshots[index] = candidate;
            captured = true;
            reportCapacityExceeded = false;
            return candidate;
        }
        finally
        {
            if (lockTaken)
                _gate.Exit(useMemoryBarrier: true);
        }
    }

    /// <summary>
    /// Releases all snapshots when an interactive resize ends.
    /// </summary>
    public void Clear()
    {
        if (Volatile.Read(ref _count) == 0)
            return;

        bool lockTaken = false;
        try
        {
            _gate.Enter(ref lockTaken);
            _count = 0;
            _capacityExceededReported = false;
        }
        finally
        {
            if (lockTaken)
                _gate.Exit(useMemoryBarrier: true);
        }
    }
}
