using System.Diagnostics;

namespace XREngine.Execution;

/// <summary>
/// Fixed-capacity allocation-free queue used by one logical render lane.
/// A short lock keeps the initial scheduler slice correct for multiple
/// dependency producers and work-stealing consumers.
/// </summary>
internal sealed class BoundedRenderWorkQueue
{
    private readonly object _sync = new();
    private readonly RenderWorkClaim[] _items;
    private int _head;
    private int _tail;
    private int _count;

    internal BoundedRenderWorkQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _items = new RenderWorkClaim[capacity];
    }

    internal int Capacity => _items.Length;

    internal int Count
    {
        get
        {
            lock (_sync)
                return _count;
        }
    }

    internal bool TryEnqueue(
        in RenderWorkClaim claim,
        out bool transitionedFromEmpty,
        out int queueDepth,
        out long lockWaitTicks)
    {
        EnterMeasured(_sync, out lockWaitTicks);
        try
        {
            if (_count == _items.Length)
            {
                transitionedFromEmpty = false;
                queueDepth = _count;
                return false;
            }

            transitionedFromEmpty = _count == 0;
            _items[_tail] = claim;
            _tail = (_tail + 1) % _items.Length;
            _count++;
            queueDepth = _count;
            return true;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    internal bool TryDequeue(
        out RenderWorkClaim claim,
        out long lockWaitTicks)
    {
        EnterMeasured(_sync, out lockWaitTicks);
        try
        {
            if (_count == 0)
            {
                claim = default;
                return false;
            }

            claim = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) % _items.Length;
            _count--;
            return true;
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    private static void EnterMeasured(object gate, out long waitTicks)
    {
        if (Monitor.TryEnter(gate))
        {
            waitTicks = 0L;
            return;
        }

        long waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        waitTicks = Math.Max(
            1L,
            Stopwatch.GetTimestamp() - waitStarted);
    }
}
