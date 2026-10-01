namespace XREngine.Rendering.Vulkan;

/// <summary>Post-capture analysis and fidelity diagnostics.</summary>
public sealed record VulkanCpuSpanAnalysis(
    VulkanCpuStageDistribution[] Stages,
    VulkanCpuWorkerFrameSummary[] Workers,
    VulkanCpuFrameParallelism[] Parallelism,
    VulkanCpuCaptureDiagnostics Diagnostics,
    int InvalidParentage,
    int InvalidIntervals)
{
    public bool Complete => Diagnostics.Complete && InvalidParentage == 0 && InvalidIntervals == 0;

    public static VulkanCpuSpanAnalysis Create(
        VulkanCpuSpanProfiler.VulkanCpuSpanRecord[] records,
        VulkanCpuCaptureDiagnostics diagnostics)
    {
        Dictionary<long, VulkanCpuSpanProfiler.VulkanCpuSpanRecord> byId = new(records.Length);
        foreach (var record in records)
            byId[record.SpanId] = record;

        Dictionary<long, List<(long Start, long End)>> childIntervals = [];
        int invalidParentage = 0;
        int invalidIntervals = 0;
        foreach (var record in records)
        {
            if (record.StartTimestamp > record.EndTimestamp)
            {
                invalidIntervals++;
                continue;
            }
            if (record.ParentSpanId == 0)
                continue;
            if (!byId.TryGetValue(record.ParentSpanId, out var parent) ||
                parent.ThreadId != record.ThreadId || parent.FrameId != record.FrameId ||
                parent.StartTimestamp > record.StartTimestamp || parent.EndTimestamp < record.EndTimestamp)
            {
                invalidParentage++;
                continue;
            }
            if (!childIntervals.TryGetValue(record.ParentSpanId, out var children))
                childIntervals[record.ParentSpanId] = children = [];
            children.Add((record.StartTimestamp, record.EndTimestamp));
        }

        Dictionary<EVulkanCpuStage, List<(long Inclusive, long Exclusive, long Bytes)>> stages = [];
        foreach (var record in records)
        {
            if (record.StartTimestamp > record.EndTimestamp)
                continue;
            long inclusive = record.EndTimestamp - record.StartTimestamp;
            long childUnion = childIntervals.TryGetValue(record.SpanId, out var children)
                ? UnionTicks(children) : 0;
            if (childUnion > inclusive)
                invalidIntervals++;
            long exclusive = Math.Max(0, inclusive - childUnion);
            if (!stages.TryGetValue(record.Stage, out var samples))
                stages[record.Stage] = samples = [];
            samples.Add((inclusive, exclusive, record.AllocatedBytes));
        }
        VulkanCpuStageDistribution[] distributions = new VulkanCpuStageDistribution[stages.Count];
        int stageIndex = 0;
        foreach (var (stage, samples) in stages.OrderBy(static item => item.Key))
        {
            samples.Sort(static (left, right) => left.Inclusive.CompareTo(right.Inclusive));
            distributions[stageIndex++] = new(stage, samples.Count,
                samples.Sum(static item => item.Inclusive), samples.Sum(static item => item.Exclusive),
                samples[0].Inclusive, samples[(samples.Count - 1) / 2].Inclusive,
                samples[(int)Math.Ceiling(samples.Count * 0.95) - 1].Inclusive,
                samples[^1].Inclusive, samples.Sum(static item => item.Bytes));
        }

        List<VulkanCpuWorkerFrameSummary> workers = [];
        Dictionary<(long FrameId, int WorkerId), List<(long Start, long End)>> workByWorker = [];
        var workerGroups = records.Where(static record => record.WorkerId >= 0)
            .GroupBy(static record => (record.FrameId, record.WorkerId)).ToArray();
        Dictionary<long, List<(long Start, long End)>> coordinatorWaitByFrame = [];
        foreach (var record in records)
        {
            if (record.WorkerId >= 0 || record.WaitReason is null || record.StartTimestamp > record.EndTimestamp)
                continue;
            if (!coordinatorWaitByFrame.TryGetValue(record.FrameId, out var intervals))
                coordinatorWaitByFrame[record.FrameId] = intervals = [];
            intervals.Add((record.StartTimestamp, record.EndTimestamp));
        }
        foreach (var group in workerGroups)
        {
            List<(long Start, long End)> work = [];
            List<(long Start, long End)> wait = [];
            long start = long.MaxValue;
            long end = long.MinValue;
            foreach (var record in group)
            {
                if (record.StartTimestamp > record.EndTimestamp)
                    continue;
                start = Math.Min(start, record.StartTimestamp);
                end = Math.Max(end, record.EndTimestamp);
                (record.WaitReason is null ? work : wait).Add((record.StartTimestamp, record.EndTimestamp));
            }
            long waitTicks = UnionTicks(wait);
            work = Subtract(work, wait);
            long workTicks = UnionTicks(work);
            workByWorker[group.Key] = work;
            workers.Add(new(group.Key.FrameId, group.Key.WorkerId, workTicks, waitTicks,
                0, start == long.MaxValue ? 0 : end - start));
        }
        for (int index = 0; index < workers.Count; index++)
        {
            VulkanCpuWorkerFrameSummary worker = workers[index];
            List<(long Start, long End)> otherEffectiveWork = [];
            foreach (var pair in workByWorker)
            {
                if (pair.Key.FrameId == worker.FrameId && pair.Key.WorkerId != worker.WorkerId)
                    otherEffectiveWork.AddRange(pair.Value);
            }
            workers[index] = worker with
            {
                OverlapTicks = IntersectionTicks(workByWorker[(worker.FrameId, worker.WorkerId)], otherEffectiveWork),
            };
        }
        List<VulkanCpuFrameParallelism> parallelism = [];
        foreach (var group in workers.GroupBy(static worker => worker.FrameId))
        {
            List<(long Start, long End)> allWork = [];
            foreach (var worker in group)
                allWork.AddRange(workByWorker[(worker.FrameId, worker.WorkerId)]);
            long sum = group.Sum(static worker => worker.WorkTicks);
            long union = UnionTicks(allWork);
            long min = group.Min(static worker => worker.WorkTicks);
            long max = group.Max(static worker => worker.WorkTicks);
            coordinatorWaitByFrame.TryGetValue(group.Key, out var coordinatorWait);
            parallelism.Add(new(group.Key, group.Count(), union, sum, Math.Max(0, sum - union),
                max - min, coordinatorWait is null ? 0 : UnionTicks(coordinatorWait)));
        }
        return new(distributions, [.. workers], [.. parallelism], diagnostics, invalidParentage, invalidIntervals);
    }

    private static long UnionTicks(List<(long Start, long End)> intervals)
    {
        if (intervals.Count == 0)
            return 0;
        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        long start = intervals[0].Start;
        long end = intervals[0].End;
        long total = 0;
        for (int index = 1; index < intervals.Count; index++)
        {
            var next = intervals[index];
            if (next.Start <= end)
                end = Math.Max(end, next.End);
            else
            {
                total += end - start;
                start = next.Start;
                end = next.End;
            }
        }
        return total + end - start;
    }

    private static long IntersectionTicks(List<(long Start, long End)> left, List<(long Start, long End)> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;
        left = Merge(left);
        right = Merge(right);
        int i = 0, j = 0;
        long total = 0;
        while (i < left.Count && j < right.Count)
        {
            total += Math.Max(0, Math.Min(left[i].End, right[j].End) - Math.Max(left[i].Start, right[j].Start));
            if (left[i].End < right[j].End) i++; else j++;
        }
        return total;
    }

    private static List<(long Start, long End)> Merge(List<(long Start, long End)> intervals)
    {
        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        List<(long Start, long End)> merged = [];
        foreach (var interval in intervals)
        {
            if (merged.Count == 0 || merged[^1].End < interval.Start)
                merged.Add(interval);
            else
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
        }
        return merged;
    }

    private static List<(long Start, long End)> Subtract(
        List<(long Start, long End)> work,
        List<(long Start, long End)> waits)
    {
        List<(long Start, long End)> result = [];
        List<(long Start, long End)> mergedWaits = Merge(waits);
        foreach (var interval in Merge(work))
        {
            long cursor = interval.Start;
            foreach (var wait in mergedWaits)
            {
                if (wait.End <= cursor) continue;
                if (wait.Start >= interval.End) break;
                if (wait.Start > cursor) result.Add((cursor, Math.Min(wait.Start, interval.End)));
                cursor = Math.Max(cursor, wait.End);
                if (cursor >= interval.End) break;
            }
            if (cursor < interval.End) result.Add((cursor, interval.End));
        }
        return result;
    }
}
