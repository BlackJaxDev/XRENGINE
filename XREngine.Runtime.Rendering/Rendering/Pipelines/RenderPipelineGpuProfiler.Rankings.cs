namespace XREngine.Rendering;

internal sealed partial class RenderPipelineGpuProfiler
{
    private const int MaximumRankRows = 256;

    /// <summary>Copies one page from an exact completed history ranking.</summary>
    public bool TryCopyRankPage(string pipelineName, long historyGeneration,
        long expectedRevision, EGpuPipelineNodeRankMetric metric, int offset,
        Span<GpuPipelineNodeRankRow> destination, out long actualRevision,
        out int totalNodes, out int copied, out bool truncated)
    {
        actualRevision = 0;
        totalNodes = 0;
        copied = 0;
        truncated = false;
        if (string.IsNullOrWhiteSpace(pipelineName) || historyGeneration <= 0 ||
            expectedRevision < 0 || offset < 0 || (uint)metric >= 3)
            return false;
        lock (_lock)
        {
            if (!_pipelineTimingHistories.TryGetValue(pipelineName, out PipelineTimingHistory? history) ||
                history.Generation != historyGeneration || history.RankRevision == 0 ||
                expectedRevision != 0 && expectedRevision != history.RankRevision)
                return false;
            actualRevision = history.RankRevision;
            totalNodes = history.RankedNodes.Count;
            truncated = totalNodes > history.PublishedRankCount;
            if (offset > history.PublishedRankCount)
                return false;
            copied = Math.Min(destination.Length, history.PublishedRankCount - offset);
            history.PublishedRanks[(int)metric].AsSpan(offset, copied).CopyTo(destination);
            return true;
        }
    }

    private static void AddRankedNodeNoLock(PipelineTimingHistory history, PipelineNodeTimingStats stats)
    {
        int slot = history.RankedNodes.Count;
        stats.RankSlot = slot;
        history.RankedNodes.Add(stats);
        for (int metric = 0; metric < 3; metric++)
        {
            if (slot == history.RankHeaps[metric].Length)
            {
                int capacity = Math.Max(16, slot <= int.MaxValue / 2 ? slot * 2 : int.MaxValue);
                int[] heap = new int[capacity];
                int[] positions = new int[capacity];
                history.RankHeaps[metric].AsSpan().CopyTo(heap);
                history.RankPositions[metric].AsSpan().CopyTo(positions);
                history.RankHeaps[metric] = heap;
                history.RankPositions[metric] = positions;
            }
            history.RankHeaps[metric][slot] = slot;
            history.RankPositions[metric][slot] = slot;
            SiftRankUp(history, metric, slot);
        }
    }

    private static void UpdateRankedNodeNoLock(PipelineTimingHistory history, int slot)
    {
        for (int metric = 0; metric < 3; metric++)
        {
            int position = history.RankPositions[metric][slot];
            if (position > 0 && CompareRankSlots(history, metric,
                history.RankHeaps[metric][position],
                history.RankHeaps[metric][(position - 1) / 2]) > 0)
                SiftRankUp(history, metric, position);
            else
                SiftRankDown(history, metric, position);
        }
    }

    private static int CompareRankSlots(PipelineTimingHistory history, int metric, int leftSlot, int rightSlot)
    {
        PipelineNodeTimingStats left = history.RankedNodes[leftSlot];
        PipelineNodeTimingStats right = history.RankedNodes[rightSlot];
        int result = metric switch
        {
            0 => left.MaxMilliseconds.CompareTo(right.MaxMilliseconds),
            1 => left.AverageFrameMilliseconds.CompareTo(right.AverageFrameMilliseconds),
            _ => left.TotalMilliseconds.CompareTo(right.TotalMilliseconds),
        };
        if (result != 0)
            return result;
        result = metric switch
        {
            1 => left.MaxMilliseconds.CompareTo(right.MaxMilliseconds),
            _ => metric == 0
                ? left.AverageFrameMilliseconds.CompareTo(right.AverageFrameMilliseconds)
                : left.MaxMilliseconds.CompareTo(right.MaxMilliseconds),
        };
        return result != 0 ? result : rightSlot.CompareTo(leftSlot);
    }

    private static void SwapRankSlots(PipelineTimingHistory history, int metric, int left, int right)
    {
        int[] heap = history.RankHeaps[metric];
        int[] positions = history.RankPositions[metric];
        (heap[left], heap[right]) = (heap[right], heap[left]);
        positions[heap[left]] = left;
        positions[heap[right]] = right;
    }

    private static void SiftRankUp(PipelineTimingHistory history, int metric, int position)
    {
        while (position > 0)
        {
            int parent = (position - 1) / 2;
            int[] heap = history.RankHeaps[metric];
            if (CompareRankSlots(history, metric, heap[position], heap[parent]) <= 0)
                break;
            SwapRankSlots(history, metric, position, parent);
            position = parent;
        }
    }

    private static void SiftRankDown(PipelineTimingHistory history, int metric, int position)
    {
        int[] heap = history.RankHeaps[metric];
        int count = history.RankedNodes.Count;
        while (position < count / 2)
        {
            int left = position * 2 + 1;
            if (left >= count)
                break;
            int best = left;
            int right = left + 1;
            if (right < count && CompareRankSlots(history, metric, heap[right], heap[left]) > 0)
                best = right;
            if (CompareRankSlots(history, metric, heap[best], heap[position]) <= 0)
                break;
            SwapRankSlots(history, metric, best, position);
            position = best;
        }
    }

    private static void PublishNodeRanksNoLock(PipelineTimingHistory history)
    {
        int count = Math.Min(MaximumRankRows, history.RankedNodes.Count);
        history.PublishedRankCount = count;
        for (int metric = 0; metric < 3; metric++)
            ExtractNodeRankNoLock(history, metric, count);
        history.RankRevision++;
    }

    private static void ExtractNodeRankNoLock(PipelineTimingHistory history, int metric, int count)
    {
        if (count == 0)
            return;
        int[] heap = history.RankHeaps[metric];
        int[] frontier = history.RankFrontier;
        int frontierCount = 1;
        frontier[0] = 0;
        for (int rank = 0; rank < count; rank++)
        {
            int position = frontier[0];
            frontier[0] = frontier[--frontierCount];
            SiftFrontierDown(history, metric, frontier, frontierCount);
            PipelineNodeTimingStats stats = history.RankedNodes[heap[position]];
            history.PublishedRanks[metric][rank] = new(stats.Path, stats.Name, stats.Depth,
                stats.IsPotentialShaderOrMaterialNode, stats.ObservedFrameCount,
                stats.TotalSampleCount, stats.TotalMilliseconds,
                stats.AverageFrameMilliseconds, stats.AverageTimerSampleMilliseconds,
                stats.MinMilliseconds == double.MaxValue ? 0.0 : stats.MinMilliseconds,
                stats.MaxMilliseconds, stats.WorstFrameId, stats.LastMilliseconds,
                stats.LastFrameId);
            if (rank + 1 == count)
                break;
            int left = position * 2 + 1;
            if (left < history.RankedNodes.Count)
                PushFrontier(history, metric, frontier, ref frontierCount, left);
            if (left + 1 < history.RankedNodes.Count)
                PushFrontier(history, metric, frontier, ref frontierCount, left + 1);
        }
    }

    private static void PushFrontier(PipelineTimingHistory history, int metric,
        int[] frontier, ref int count, int position)
    {
        int child = count++;
        frontier[child] = position;
        while (child > 0)
        {
            int parent = (child - 1) / 2;
            if (CompareRankSlots(history, metric,
                history.RankHeaps[metric][frontier[child]],
                history.RankHeaps[metric][frontier[parent]]) <= 0)
                break;
            (frontier[child], frontier[parent]) = (frontier[parent], frontier[child]);
            child = parent;
        }
    }

    private static void SiftFrontierDown(PipelineTimingHistory history, int metric,
        int[] frontier, int count)
    {
        int parent = 0;
        while (parent < count)
        {
            int left = parent * 2 + 1;
            if (left >= count)
                break;
            int best = left;
            int right = left + 1;
            if (right < count && CompareRankSlots(history, metric,
                history.RankHeaps[metric][frontier[right]],
                history.RankHeaps[metric][frontier[left]]) > 0)
                best = right;
            if (CompareRankSlots(history, metric,
                history.RankHeaps[metric][frontier[best]],
                history.RankHeaps[metric][frontier[parent]]) <= 0)
                break;
            (frontier[parent], frontier[best]) = (frontier[best], frontier[parent]);
            parent = best;
        }
    }
}
