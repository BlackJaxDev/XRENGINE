using NUnit.Framework;
using Shouldly;
using XREngine.Data.Trees;
using XREngine.Timers;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class OctreeStatsTimingTests
{
    [Test]
    public void OctreeTimingStats_AggregateIntoDisplaySnapshot()
    {
        bool previousEnableOctreeStats = XREngine.RuntimeEngine.Rendering.Stats.Octree.EnableOctreeStats;

        try
        {
            XREngine.RuntimeEngine.Rendering.Stats.Octree.EnableOctreeStats = true;

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordOctreeAdd();
            XREngine.RuntimeEngine.Rendering.Stats.Octree.SwapOctreeStats();

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordOctreeSwapTiming(new OctreeSwapTimingStats(
                DrainedCommandCount: 3,
                BufferedCommandCount: 2,
                ExecutedCommandCount: 2,
                DrainTicks: EngineTimer.SecondsToStopwatchTicks(0.001),
                ExecuteTicks: EngineTimer.SecondsToStopwatchTicks(0.004),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.003),
                MaxCommandKind: EOctreeCommandKind.Move));

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordOctreeSwapTiming(new OctreeSwapTimingStats(
                DrainedCommandCount: 1,
                BufferedCommandCount: 1,
                ExecutedCommandCount: 1,
                DrainTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                ExecuteTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.001),
                MaxCommandKind: EOctreeCommandKind.Add));

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordOctreeRaycastTiming(new OctreeRaycastTimingStats(
                ProcessedCommandCount: 1,
                DroppedCommandCount: 2,
                TraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.005),
                CallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxTraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.005),
                MaxCallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.0065)));

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordOctreeRaycastTiming(new OctreeRaycastTimingStats(
                ProcessedCommandCount: 2,
                DroppedCommandCount: 1,
                TraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.0025),
                CallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                MaxTraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.002),
                MaxCallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.0025)));

            XREngine.RuntimeEngine.Rendering.Stats.Octree.RecordCpuSpatialTreeStats(
                "Bvh",
                new SpatialTreeOccupancyStats(
                    NodeCount: 31,
                    ItemCount: 128,
                    RootItemCount: 4,
                    MaxNodeItemCount: 8,
                    MaxDepth: 5,
                    UnboundedItemCount: 4),
                EngineTimer.SecondsToStopwatchTicks(0.00125));

            XREngine.RuntimeEngine.Rendering.Stats.Octree.SwapOctreeStats();

            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainedCommandCount.ShouldBe(4);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapBufferedCommandCount.ShouldBe(3);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecutedCommandCount.ShouldBe(3);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainMs.ShouldBe(1.5, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecuteMs.ShouldBe(5.5, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandMs.ShouldBe(3.0, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandKind.ShouldBe("Move");
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastProcessedCommandCount.ShouldBe(3);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastDroppedCommandCount.ShouldBe(3);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastTraversalMs.ShouldBe(7.5, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastCallbackMs.ShouldBe(2.0, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxTraversalMs.ShouldBe(5.0, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCallbackMs.ShouldBe(1.5, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCommandMs.ShouldBe(6.5, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMode.ShouldBe("Bvh");
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeNodeCount.ShouldBe(31);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeItemCount.ShouldBe(128);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeRootItemCount.ShouldBe(4);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxNodeItemCount.ShouldBe(8);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxDepth.ShouldBe(5);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeUnboundedItemCount.ShouldBe(4);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeCollectMs.ShouldBe(1.25, 0.05);
            XREngine.RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxCollectMs.ShouldBe(1.25, 0.05);
        }
        finally
        {
            XREngine.RuntimeEngine.Rendering.Stats.Octree.EnableOctreeStats = previousEnableOctreeStats;
        }
    }
}
