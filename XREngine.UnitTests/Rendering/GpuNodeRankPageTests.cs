using System.Collections;
using System.Linq.Expressions;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuNodeRankPageTests
{
    private static readonly Type ProfilerType = typeof(RenderPipelineGpuProfiler);
    private static readonly Type HistoryType = ProfilerType.GetNestedType("PipelineTimingHistory", BindingFlags.NonPublic)!;
    private static readonly Type NodeType = ProfilerType.GetNestedType("PipelineNodeTimingStats", BindingFlags.NonPublic)!;
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Test]
    public void RankingReordersAfterAverageFallsAndRejectsOldRevision()
    {
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        string pipeline = "rank-test-" + Guid.NewGuid().ToString("N");
        object history = NewHistory(pipeline, 101);
        WithHistory(profiler, pipeline, history, () =>
        {
            object a = AddNode(history, "A");
            object b = AddNode(history, "B");
            AddSample(history, a, 100);
            AddSample(history, a, 0);
            AddSample(history, b, 60);
            Publish(history);

            GpuPipelineNodeRankRow[] page = new GpuPipelineNodeRankRow[2];
            profiler.TryCopyRankPage(pipeline, 101, 0, EGpuPipelineNodeRankMetric.WorstFrame,
                0, page, out long firstRevision, out int total, out int copied,
                out bool truncated).ShouldBeTrue();
            (total, copied, truncated).ShouldBe((2, 2, false));
            page[0].Name.ShouldBe("A");
            profiler.TryCopyRankPage(pipeline, 101, firstRevision,
                EGpuPipelineNodeRankMetric.AverageActiveFrame, 0, page,
                out _, out _, out _, out _).ShouldBeTrue();
            page[0].Name.ShouldBe("B");

            AddSample(history, b, 100);
            for (int index = 0; index < 4; index++)
                AddSample(history, b, 0);
            Publish(history);
            profiler.TryCopyRankPage(pipeline, 101, firstRevision,
                EGpuPipelineNodeRankMetric.AverageActiveFrame, 0, page,
                out _, out _, out _, out _).ShouldBeFalse();
            profiler.TryCopyRankPage(pipeline, 101, 0,
                EGpuPipelineNodeRankMetric.AverageActiveFrame, 0, page,
                out long secondRevision, out _, out _, out _).ShouldBeTrue();
            secondRevision.ShouldBeGreaterThan(firstRevision);
            page[0].Name.ShouldBe("A");
            profiler.TryCopyRankPage(pipeline, 102, 0,
                EGpuPipelineNodeRankMetric.TotalCapturedTime, 0, page,
                out _, out _, out _, out _).ShouldBeFalse();
            profiler.TryCopyRankPage(pipeline, 101, 0,
                EGpuPipelineNodeRankMetric.TotalCapturedTime, int.MaxValue, page,
                out _, out _, out _, out _).ShouldBeFalse();
        });
    }

    [Test]
    public void RankingCapsPublishedRowsButReportsEveryIndexedPath()
    {
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        string pipeline = "rank-cap-" + Guid.NewGuid().ToString("N");
        object history = NewHistory(pipeline, 201);
        WithHistory(profiler, pipeline, history, () =>
        {
            for (int index = 0; index < 260; index++)
            {
                object node = AddNode(history, "N" + index);
                AddSample(history, node, index);
            }
            Publish(history);
            GpuPipelineNodeRankRow[] page = new GpuPipelineNodeRankRow[64];
            profiler.TryCopyRankPage(pipeline, 201, 0,
                EGpuPipelineNodeRankMetric.TotalCapturedTime, 192, page,
                out long revision, out int total, out int copied,
                out bool truncated).ShouldBeTrue();
            (total, copied, truncated).ShouldBe((260, 64, true));
            page[0].Name.ShouldBe("N67");
            page[^1].Name.ShouldBe("N4");
            profiler.TryCopyRankPage(pipeline, 201, revision,
                EGpuPipelineNodeRankMetric.TotalCapturedTime, 256, page,
                out _, out _, out copied, out _).ShouldBeTrue();
            copied.ShouldBe(0);
        });
    }

    [Test]
    public void RankingUsesDumpComparatorsAndStableInsertionOrder()
    {
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        string pipeline = "rank-order-" + Guid.NewGuid().ToString("N");
        object history = NewHistory(pipeline, 301);
        WithHistory(profiler, pipeline, history, () =>
        {
            object a = AddNode(history, "A");
            object b = AddNode(history, "B");
            object c = AddNode(history, "C");
            object d = AddNode(history, "D");
            AddSample(history, a, 20); AddSample(history, a, 0);
            AddSample(history, b, 20); AddSample(history, b, 10);
            AddSample(history, c, 18);
            AddSample(history, d, 20); AddSample(history, d, 10);
            Publish(history);
            GpuPipelineNodeRankRow[] page = new GpuPipelineNodeRankRow[4];
            AssertOrder(EGpuPipelineNodeRankMetric.WorstFrame, "B,D,A,C");
            AssertOrder(EGpuPipelineNodeRankMetric.AverageActiveFrame, "C,B,D,A");
            AssertOrder(EGpuPipelineNodeRankMetric.TotalCapturedTime, "B,D,A,C");

            void AssertOrder(EGpuPipelineNodeRankMetric metric, string expected)
            {
                profiler.TryCopyRankPage(pipeline, 301, 0, metric, 0, page,
                    out _, out _, out int copied, out _).ShouldBeTrue();
                string.Join(",", page.Take(copied).Select(static row => row.Name))
                    .ShouldBe(expected);
            }
        });
    }

    [Test]
    public void WarmRankUpdatePublishAndPageCopyDoNotAllocate()
    {
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        string pipeline = "rank-warm-" + Guid.NewGuid().ToString("N");
        object history = NewHistory(pipeline, 401);
        WithHistory(profiler, pipeline, history, () =>
        {
            object node = AddNode(history, "Warm");
            int slot = (int)NodeType.GetProperty("RankSlot")!.GetValue(node)!;
            Action<object, ulong, DateTimeOffset, double, int> sample = CompileSample();
            Action<object, int> update = CompileStaticUpdate();
            Action<object> publish = CompileStaticAction("PublishNodeRanksNoLock");
            GpuPipelineNodeRankRow[] page = new GpuPipelineNodeRankRow[1];
            DateTimeOffset stamp = DateTimeOffset.UtcNow;
            for (int index = 0; index < 32; index++)
            {
                sample(node, (ulong)index, stamp, index, 1);
                update(history, slot);
                publish(history);
                profiler.TryCopyRankPage(pipeline, 401, 0,
                    EGpuPipelineNodeRankMetric.WorstFrame, 0, page,
                    out _, out _, out _, out _);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 128; index++)
            {
                sample(node, (ulong)index, stamp, index, 1);
                update(history, slot);
                publish(history);
                profiler.TryCopyRankPage(pipeline, 401, 0,
                    EGpuPipelineNodeRankMetric.WorstFrame, 0, page,
                    out _, out _, out _, out _);
            }
            (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
        });
    }

    [Test]
    public void ReplacedHistoryRejectsOldGenerationForTheSamePipeline()
    {
        RenderPipelineGpuProfiler profiler = RenderPipelineGpuProfiler.Instance;
        string pipeline = "rank-reset-" + Guid.NewGuid().ToString("N");
        object oldHistory = NewHistory(pipeline, 501);
        WithHistory(profiler, pipeline, oldHistory, () =>
        {
            AddSample(oldHistory, AddNode(oldHistory, "Old"), 2);
            Publish(oldHistory);
        });
        object replacement = NewHistory(pipeline, 502);
        WithHistory(profiler, pipeline, replacement, () =>
        {
            AddSample(replacement, AddNode(replacement, "New"), 3);
            Publish(replacement);
            GpuPipelineNodeRankRow[] page = new GpuPipelineNodeRankRow[1];
            profiler.TryCopyRankPage(pipeline, 501, 0,
                EGpuPipelineNodeRankMetric.WorstFrame, 0, page,
                out _, out _, out _, out _).ShouldBeFalse();
            profiler.TryCopyRankPage(pipeline, 502, 0,
                EGpuPipelineNodeRankMetric.WorstFrame, 0, page,
                out _, out _, out _, out _).ShouldBeTrue();
            page[0].Name.ShouldBe("New");
        });
    }

    private static Action<object, ulong, DateTimeOffset, double, int> CompileSample()
    {
        ParameterExpression node = Expression.Parameter(typeof(object));
        ParameterExpression frame = Expression.Parameter(typeof(ulong));
        ParameterExpression time = Expression.Parameter(typeof(DateTimeOffset));
        ParameterExpression value = Expression.Parameter(typeof(double));
        ParameterExpression count = Expression.Parameter(typeof(int));
        return Expression.Lambda<Action<object, ulong, DateTimeOffset, double, int>>(
            Expression.Call(Expression.Convert(node, NodeType),
                NodeType.GetMethod("AddFrameSample")!, frame, time, value, count),
            node, frame, time, value, count).Compile();
    }

    private static Action<object, int> CompileStaticUpdate()
    {
        ParameterExpression history = Expression.Parameter(typeof(object));
        ParameterExpression value = Expression.Parameter(typeof(int));
        return Expression.Lambda<Action<object, int>>(
            Expression.Call(ProfilerType.GetMethod("UpdateRankedNodeNoLock", PrivateStatic)!,
                Expression.Convert(history, HistoryType), value), history, value).Compile();
    }

    private static Action<object> CompileStaticAction(string name)
    {
        ParameterExpression history = Expression.Parameter(typeof(object));
        return Expression.Lambda<Action<object>>(
            Expression.Call(ProfilerType.GetMethod(name, PrivateStatic)!,
                Expression.Convert(history, HistoryType)), history).Compile();
    }

    private static object NewHistory(string pipeline, long generation)
        => Activator.CreateInstance(HistoryType, BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic, null, [pipeline, generation], null)!;

    private static object AddNode(object history, string name)
    {
        object node = Activator.CreateInstance(NodeType, nonPublic: true)!;
        NodeType.GetProperty("Path")!.SetValue(node, name);
        NodeType.GetProperty("Name")!.SetValue(node, name);
        NodeType.GetProperty("Depth")!.SetValue(node, 1);
        ProfilerType.GetMethod("AddRankedNodeNoLock", PrivateStatic)!.Invoke(null, [history, node]);
        return node;
    }

    private static void AddSample(object history, object node, double milliseconds)
    {
        NodeType.GetMethod("AddFrameSample")!.Invoke(node,
            [1UL, DateTimeOffset.UtcNow, milliseconds, 1]);
        int slot = (int)NodeType.GetProperty("RankSlot")!.GetValue(node)!;
        ProfilerType.GetMethod("UpdateRankedNodeNoLock", PrivateStatic)!.Invoke(null,
            [history, slot]);
    }

    private static void Publish(object history)
        => ProfilerType.GetMethod("PublishNodeRanksNoLock", PrivateStatic)!.Invoke(null, [history]);

    private static void WithHistory(RenderPipelineGpuProfiler profiler, string pipeline,
        object history, Action test)
    {
        object sync = ProfilerType.GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(profiler)!;
        IDictionary histories = (IDictionary)ProfilerType.GetField("_pipelineTimingHistories",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(profiler)!;
        lock (sync)
        {
            histories.Add(pipeline, history);
            try { test(); }
            finally { histories.Remove(pipeline); }
        }
    }
}
