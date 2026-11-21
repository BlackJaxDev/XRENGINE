namespace XREngine.Rendering;

/// <summary>
/// A ring of GPU timestamp query pairs that measures the GPU cost of texture streaming items without a stall. The render
/// thread writes a pair around an item and reads it some frames later, when the results are ready. When no pair is free,
/// the item is not timed. A pair that is not ready after <see cref="MaxPendingPolls"/> polls is dropped. It allocates
/// nothing after construction. Use it on the render thread only.
/// </summary>
internal sealed class TextureStreamingGpuTimer
{
    /// <summary>The number of polls after which a pending pair is dropped.</summary>
    internal const int MaxPendingPolls = 120;

    private const int Free = 0;
    private const int Begun = 1;
    private const int Ended = 2;

    private readonly ITextureStreamingTimestampSource _source;
    private readonly int[] _state;
    private readonly int[] _items;
    private readonly int[] _polls;
    private int _next;
    private long _droppedPairs;
    private long _readPairs;

    /// <summary>Creates a timer over the queries of <paramref name="source"/>. Two queries make one pair.</summary>
    public TextureStreamingGpuTimer(ITextureStreamingTimestampSource source)
    {
        _source = source;
        int pairs = Math.Max(0, source.QueryCount / 2);
        _state = new int[pairs];
        _items = new int[pairs];
        _polls = new int[pairs];
    }

    /// <summary>The number of pairs that were dropped because their results did not come or failed.</summary>
    public long DroppedPairs => _droppedPairs;

    /// <summary>The number of pairs that gave a result.</summary>
    public long ReadPairs => _readPairs;

    /// <summary>Writes the start timestamp of an item.</summary>
    /// <returns>The pair index, or -1 when no pair is free or the timestamp could not be written.</returns>
    public int Begin()
    {
        int pairs = _state.Length;
        for (int step = 0; step < pairs; step++)
        {
            int pair = (_next + step) % pairs;
            if (_state[pair] != Free)
                continue;

            if (!_source.TryWriteTimestamp(pair * 2))
                return -1;

            _state[pair] = Begun;
            _next = (pair + 1) % pairs;
            return pair;
        }

        return -1;
    }

    /// <summary>Writes the end timestamp of an item that <see cref="Begin"/> started.</summary>
    /// <param name="pair">The pair index from <see cref="Begin"/>. A negative value does nothing.</param>
    /// <param name="items">The number of items between the two timestamps.</param>
    public void End(int pair, int items)
    {
        if (pair < 0 || pair >= _state.Length || _state[pair] != Begun)
            return;

        if (!_source.TryWriteTimestamp(pair * 2 + 1))
        {
            _state[pair] = Free;
            _droppedPairs++;
            return;
        }

        _state[pair] = Ended;
        _items[pair] = Math.Max(1, items);
        _polls[pair] = 0;
    }

    /// <summary>
    /// Reads one ended pair whose results are ready, without a wait. Call it in a loop until it returns false.
    /// </summary>
    /// <param name="millisecondsPerItem">The GPU time between the two timestamps divided by the item count.</param>
    public bool TryReadCompleted(out double millisecondsPerItem)
    {
        millisecondsPerItem = 0.0;
        for (int pair = 0; pair < _state.Length; pair++)
        {
            if (_state[pair] != Ended)
                continue;

            ERenderQueryReadStatus startStatus = _source.TryReadTimestamp(pair * 2, out ulong start);
            ulong end = 0UL;
            ERenderQueryReadStatus endStatus = startStatus == ERenderQueryReadStatus.Ready
                ? _source.TryReadTimestamp(pair * 2 + 1, out end)
                : startStatus;
            if (startStatus == ERenderQueryReadStatus.Ready && endStatus == ERenderQueryReadStatus.Ready)
            {
                _state[pair] = Free;
                _readPairs++;
                ulong elapsed = end > start ? end - start : 0UL;
                millisecondsPerItem = elapsed / 1_000_000.0 / _items[pair];
                return true;
            }

            bool failed = startStatus != ERenderQueryReadStatus.Ready && startStatus != ERenderQueryReadStatus.NotReady
                || endStatus != ERenderQueryReadStatus.Ready && endStatus != ERenderQueryReadStatus.NotReady;
            if (failed || ++_polls[pair] > MaxPendingPolls)
            {
                _state[pair] = Free;
                _droppedPairs++;
            }
        }

        return false;
    }
}
