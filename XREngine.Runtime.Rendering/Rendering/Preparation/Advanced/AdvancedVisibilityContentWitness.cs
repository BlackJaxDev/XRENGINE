using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Gives the visibility content generation of an extractor by output comparison.
/// Each build compares its visibility columns with the columns of the last
/// compared build. The generation stays the same while every column is equal,
/// so a consumer can keep state that it derived from the same content.
/// </summary>
/// <remarks>
/// The comparison is exact: each column is compared row by row. The frame slots
/// of a deformed-arena slice are frame bindings and are not compared; the slot
/// offsets and the other slice fields are compared. Generations come from one
/// process-wide counter, so two extractors never give the same generation.
/// </remarks>
internal sealed class AdvancedVisibilityContentWitness
{
    private static long s_lastGeneration;

    private AdvancedVisibilityPayload[] _payloads = [];
    private EAdvancedGeometryProducer[] _producers = [];
    private AdvancedIndirectRange[] _ranges = [];
    private int[] _payloadIndices = [];
    private AdvancedDeformedArenaSlice[] _slices = [];
    private int _payloadCount;
    private int _rangeCount;
    private bool _hasContent;
    private ulong _generation;
    private long _comparisons;
    private long _unchangedComparisons;
    private long _payloadChanges;
    private long _producerChanges;
    private long _rangeChanges;
    private long _payloadIndexChanges;
    private long _sliceChanges;

    /// <summary>The current content generation. Zero before the first comparison.</summary>
    internal ulong Generation => _generation;

    /// <summary>
    /// Compares the columns of one build with the retained columns. Returns true and
    /// gives a new generation when any column changed.
    /// </summary>
    internal bool Update(
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices)
    {
        if (producers.Length != payloads.Length ||
            payloadIndices.Length != payloads.Length ||
            slices.Length != payloads.Length)
            throw new ArgumentException("Every payload-indexed visibility column must have one row for each payload.");

        _comparisons++;
        bool payloadsChanged = !_hasContent ||
            !payloads.SequenceEqual(_payloads.AsSpan(0, _payloadCount));
        bool producersChanged = !_hasContent ||
            !MemoryMarshal.AsBytes(producers).SequenceEqual(
                MemoryMarshal.AsBytes(_producers.AsSpan(0, _payloadCount)));
        bool rangesChanged = !_hasContent ||
            !ranges.SequenceEqual(_ranges.AsSpan(0, _rangeCount));
        bool payloadIndicesChanged = !_hasContent ||
            !payloadIndices.SequenceEqual(_payloadIndices.AsSpan(0, _payloadCount));
        bool slicesChanged = !_hasContent || !SlicesMatch(slices);
        if (!payloadsChanged && !producersChanged && !rangesChanged &&
            !payloadIndicesChanged && !slicesChanged)
        {
            _unchangedComparisons++;
            return false;
        }

        if (payloadsChanged) _payloadChanges++;
        if (producersChanged) _producerChanges++;
        if (rangesChanged) _rangeChanges++;
        if (payloadIndicesChanged) _payloadIndexChanges++;
        if (slicesChanged) _sliceChanges++;
        Retain(payloads, producers, ranges, payloadIndices, slices);
        _generation = unchecked((ulong)Interlocked.Increment(ref s_lastGeneration));
        return true;
    }

    /// <summary>Captures the cumulative comparison counts and the current generation.</summary>
    internal AdvancedVisibilityContentTelemetry Capture() => new(
        _comparisons,
        _unchangedComparisons,
        _payloadChanges,
        _producerChanges,
        _rangeChanges,
        _payloadIndexChanges,
        _sliceChanges,
        _generation);

    private bool SlicesMatch(ReadOnlySpan<AdvancedDeformedArenaSlice> slices)
    {
        if (slices.Length != _payloadCount)
            return false;
        ReadOnlySpan<AdvancedDeformedArenaSlice> retained = _slices.AsSpan(0, _payloadCount);
        for (int index = 0; index < slices.Length; ++index)
            if (WithoutFrameSlots(in slices[index]) != retained[index])
                return false;
        return true;
    }

    private void Retain(
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices)
    {
        // The columns grow only on a new high-water mark, so steady state does not allocate.
        EnsureCapacity(ref _payloads, payloads.Length);
        EnsureCapacity(ref _producers, payloads.Length);
        EnsureCapacity(ref _payloadIndices, payloads.Length);
        EnsureCapacity(ref _slices, payloads.Length);
        EnsureCapacity(ref _ranges, ranges.Length);
        payloads.CopyTo(_payloads);
        producers.CopyTo(_producers);
        payloadIndices.CopyTo(_payloadIndices);
        ranges.CopyTo(_ranges);
        for (int index = 0; index < slices.Length; ++index)
            _slices[index] = WithoutFrameSlots(in slices[index]);
        _payloadCount = payloads.Length;
        _rangeCount = ranges.Length;
        _hasContent = true;
    }

    private static AdvancedDeformedArenaSlice WithoutFrameSlots(in AdvancedDeformedArenaSlice slice)
        => slice with { CurrentFrameSlot = 0u, PreviousFrameSlot = 0u };

    private static void EnsureCapacity<T>(ref T[] column, int length)
    {
        if (column.Length < length)
            column = new T[length];
    }
}
