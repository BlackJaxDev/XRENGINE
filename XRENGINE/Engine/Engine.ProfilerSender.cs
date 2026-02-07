using XREngine.Data.Profiling;

namespace XREngine;

public static partial class Engine
{
    /// <summary>
    /// Wires up the delegate-based collectors on <see cref="UdpProfilerSender"/>
    /// so it can read engine stats without a direct assembly reference.
    /// Safe to call multiple times — just overwrites the delegates.
    /// </summary>
    internal static void WireProfilerSenderCollectors()
    {
        UdpProfilerSender.CollectProfilerFrame = CollectProfilerFrame;
        UdpProfilerSender.CollectRenderStats = CollectRenderStats;
        UdpProfilerSender.CollectThreadAllocations = CollectThreadAllocations;
        UdpProfilerSender.CollectBvhMetrics = CollectBvhMetrics;
        UdpProfilerSender.CollectJobSystemStats = CollectJobSystemStats;
        UdpProfilerSender.CollectMainThreadInvokes = CollectMainThreadInvokes;
    }

    private static ProfilerFramePacket? CollectProfilerFrame()
    {
        if (!Profiler.TryGetSnapshot(out var snapshot, out var history) || snapshot is null)
            return null;

        var threads = new ProfilerThreadData[snapshot.Threads.Count];
        for (int i = 0; i < snapshot.Threads.Count; i++)
        {
            var t = snapshot.Threads[i];
            threads[i] = new ProfilerThreadData
            {
                ThreadId = t.ThreadId,
                TotalTimeMs = t.TotalTimeMs,
                RootNodes = ConvertNodes(t.RootNodes),
            };
        }

        return new ProfilerFramePacket
        {
            FrameTime = snapshot.FrameTime,
            Threads = threads,
            ThreadHistory = history ?? [],
        };
    }

    private static ProfilerNodeData[] ConvertNodes(IReadOnlyList<CodeProfiler.ProfilerNodeSnapshot> nodes)
    {
        if (nodes.Count == 0)
            return [];

        var result = new ProfilerNodeData[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            result[i] = new ProfilerNodeData
            {
                Name = n.Name,
                ElapsedMs = n.ElapsedMs,
                Children = ConvertNodes(n.Children),
            };
        }
        return result;
    }

    private static RenderStatsPacket? CollectRenderStats()
    {
        var listenerSnapshot = Rendering.Stats.GetRenderMatrixListenerSnapshot();
        var listenerEntries = new RenderMatrixListenerEntry[listenerSnapshot.Length];
        for (int i = 0; i < listenerSnapshot.Length; i++)
        {
            listenerEntries[i] = new RenderMatrixListenerEntry
            {
                Name = listenerSnapshot[i].Key,
                Count = listenerSnapshot[i].Value,
            };
        }

        return new RenderStatsPacket
        {
            DrawCalls = Rendering.Stats.DrawCalls,
            MultiDrawCalls = Rendering.Stats.MultiDrawCalls,
            TrianglesRendered = Rendering.Stats.TrianglesRendered,
            AllocatedVRAMBytes = Rendering.Stats.AllocatedVRAMBytes,
            AllocatedBufferBytes = Rendering.Stats.AllocatedBufferBytes,
            AllocatedTextureBytes = Rendering.Stats.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Rendering.Stats.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Rendering.Stats.FBOBandwidthBytes,
            FBOBindCount = Rendering.Stats.FBOBindCount,
            RenderMatrixStatsReady = Rendering.Stats.RenderMatrixStatsReady,
            RenderMatrixApplied = Rendering.Stats.RenderMatrixApplied,
            RenderMatrixSetCalls = Rendering.Stats.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = Rendering.Stats.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            OctreeStatsReady = Rendering.Stats.OctreeStatsReady,
            OctreeAddCount = Rendering.Stats.OctreeAddCount,
            OctreeMoveCount = Rendering.Stats.OctreeMoveCount,
            OctreeRemoveCount = Rendering.Stats.OctreeRemoveCount,
            OctreeSkippedMoveCount = Rendering.Stats.OctreeSkippedMoveCount,
        };
    }

    private static ThreadAllocationsPacket? CollectThreadAllocations()
    {
        var snap = Allocations.GetSnapshot();
        return new ThreadAllocationsPacket
        {
            Render = ToSlice(snap.Render),
            CollectSwap = ToSlice(snap.CollectSwap),
            Update = ToSlice(snap.Update),
            FixedUpdate = ToSlice(snap.FixedUpdate),
        };
    }

    private static AllocationSlice ToSlice(AllocationRingSnapshot ring)
        => new()
        {
            LastBytes = ring.LastBytes,
            AverageBytes = ring.AverageBytes,
            MaxBytes = ring.MaxBytes,
            Samples = ring.Samples,
            Capacity = ring.Capacity,
        };

    private static BvhMetricsPacket? CollectBvhMetrics()
    {
        var m = Rendering.BvhStats.Latest;
        return new BvhMetricsPacket
        {
            BuildCount = m.BuildCount,
            BuildMilliseconds = m.BuildMilliseconds,
            RefitCount = m.RefitCount,
            RefitMilliseconds = m.RefitMilliseconds,
            CullCount = m.CullCount,
            CullMilliseconds = m.CullMilliseconds,
            RaycastCount = m.RaycastCount,
            RaycastMilliseconds = m.RaycastMilliseconds,
        };
    }

    private static JobSystemStatsPacket? CollectJobSystemStats()
    {
        var jobs = Jobs;
        const int priorityCount = (int)JobPriority.Highest + 1;
        var priorities = new JobPriorityStatsEntry[priorityCount];

        for (int i = 0; i < priorityCount; i++)
        {
            var p = (JobPriority)i;
            priorities[i] = new JobPriorityStatsEntry
            {
                Priority = i,
                PriorityName = p.ToString(),
                QueuedAny = jobs.GetQueuedCount(p, JobAffinity.Any),
                QueuedMain = jobs.GetQueuedCount(p, JobAffinity.MainThread),
                QueuedCollect = jobs.GetQueuedCount(p, JobAffinity.CollectVisibleSwap),
                AvgWaitMs = jobs.GetAverageWait(p).TotalMilliseconds,
            };
        }

        return new JobSystemStatsPacket
        {
            WorkerCount = jobs.WorkerCount,
            IsQueueBounded = jobs.IsQueueBounded,
            QueueCapacity = jobs.QueueCapacity,
            QueueSlotsInUse = jobs.QueueSlotsInUse,
            QueueSlotsAvailable = jobs.QueueSlotsAvailable,
            Priorities = priorities,
        };
    }

    private static MainThreadInvokesPacket? CollectMainThreadInvokes()
    {
        var invokes = GetMainThreadInvokeLogSnapshot();
        if (invokes.Count == 0)
            return null;

        var entries = new MainThreadInvokeEntryData[invokes.Count];
        for (int i = 0; i < invokes.Count; i++)
        {
            var e = invokes[i];
            entries[i] = new MainThreadInvokeEntryData
            {
                Sequence = e.Sequence,
                TimestampTicks = e.Timestamp.Ticks,
                Reason = e.Reason,
                Mode = e.Mode.ToString(),
                CallerThreadId = e.CallerThreadId,
            };
        }

        return new MainThreadInvokesPacket { Entries = entries };
    }
}
