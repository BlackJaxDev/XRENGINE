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
        bool previousEnableOctreeStats = XREngine.Engine.Rendering.Stats.EnableOctreeStats;

        try
        {
            XREngine.Engine.Rendering.Stats.EnableOctreeStats = true;

            XREngine.Engine.Rendering.Stats.RecordOctreeAdd();
            XREngine.Engine.Rendering.Stats.SwapOctreeStats();

            XREngine.Engine.Rendering.Stats.RecordOctreeSwapTiming(new OctreeSwapTimingStats(
                DrainedCommandCount: 3,
                BufferedCommandCount: 2,
                ExecutedCommandCount: 2,
                DrainTicks: EngineTimer.SecondsToStopwatchTicks(0.001),
                ExecuteTicks: EngineTimer.SecondsToStopwatchTicks(0.004),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.003),
                MaxCommandKind: EOctreeCommandKind.Move));

            XREngine.Engine.Rendering.Stats.RecordOctreeSwapTiming(new OctreeSwapTimingStats(
                DrainedCommandCount: 1,
                BufferedCommandCount: 1,
                ExecutedCommandCount: 1,
                DrainTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                ExecuteTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.001),
                MaxCommandKind: EOctreeCommandKind.Add));

            XREngine.Engine.Rendering.Stats.RecordOctreeRaycastTiming(new OctreeRaycastTimingStats(
                ProcessedCommandCount: 1,
                DroppedCommandCount: 2,
                TraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.005),
                CallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxTraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.005),
                MaxCallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0015),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.0065)));

            XREngine.Engine.Rendering.Stats.RecordOctreeRaycastTiming(new OctreeRaycastTimingStats(
                ProcessedCommandCount: 2,
                DroppedCommandCount: 1,
                TraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.0025),
                CallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                MaxTraversalTicks: EngineTimer.SecondsToStopwatchTicks(0.002),
                MaxCallbackTicks: EngineTimer.SecondsToStopwatchTicks(0.0005),
                MaxCommandTicks: EngineTimer.SecondsToStopwatchTicks(0.0025)));

            XREngine.Engine.Rendering.Stats.SwapOctreeStats();

            XREngine.Engine.Rendering.Stats.OctreeSwapDrainedCommandCount.ShouldBe(4);
            XREngine.Engine.Rendering.Stats.OctreeSwapBufferedCommandCount.ShouldBe(3);
            XREngine.Engine.Rendering.Stats.OctreeSwapExecutedCommandCount.ShouldBe(3);
            XREngine.Engine.Rendering.Stats.OctreeSwapDrainMs.ShouldBe(1.5, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeSwapExecuteMs.ShouldBe(5.5, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeSwapMaxCommandMs.ShouldBe(3.0, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeSwapMaxCommandKind.ShouldBe("Move");
            XREngine.Engine.Rendering.Stats.OctreeRaycastProcessedCommandCount.ShouldBe(3);
            XREngine.Engine.Rendering.Stats.OctreeRaycastDroppedCommandCount.ShouldBe(3);
            XREngine.Engine.Rendering.Stats.OctreeRaycastTraversalMs.ShouldBe(7.5, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeRaycastCallbackMs.ShouldBe(2.0, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeRaycastMaxTraversalMs.ShouldBe(5.0, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeRaycastMaxCallbackMs.ShouldBe(1.5, 0.05);
            XREngine.Engine.Rendering.Stats.OctreeRaycastMaxCommandMs.ShouldBe(6.5, 0.05);
        }
        finally
        {
            XREngine.Engine.Rendering.Stats.EnableOctreeStats = previousEnableOctreeStats;
        }
    }
}