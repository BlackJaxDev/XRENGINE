using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderGraphPlanSnapshotTests
{
    private static IReadOnlyCollection<RenderPassMetadata> BuildConsumerFirstPasses()
    {
        string hdr = RenderGraphResourceNames.MakeTexture("HDRColor");
        RenderPassMetadataCollection passes = new();
        passes.ForPass(0, "Tonemap").DependsOn(1).SampleTexture(hdr);
        passes.ForPass(1, "Scene").WriteTexture(hdr);
        passes.ForPass(2, "Debug").SampleTexture(hdr);
        return passes.Build();
    }

    [Test]
    public void OfflinePlanner_OrdersProducerFirst_AndReportsTheMove()
    {
        RenderGraphPlanSnapshot snapshot = RenderGraphOfflinePlanner.Plan(BuildConsumerFirstPasses());

        snapshot.Origin.ShouldBe(EPlanSnapshotOrigin.OfflineCompile);
        snapshot.TryGetPass(1, out PlanPassSnapshot? scene).ShouldBeTrue();
        snapshot.TryGetPass(0, out PlanPassSnapshot? tonemap).ShouldBeTrue();
        scene!.ExecutionOrder.ShouldBeLessThan(tonemap!.ExecutionOrder);
        snapshot.Reorders.ShouldNotBeEmpty();
        snapshot.OrderViolations.ShouldBeEmpty();
        snapshot.Notes.ShouldContain(RenderGraphOfflinePlanner.OfflineNote);
        snapshot.Barriers.ShouldBeEmpty();
    }

    [Test]
    public void RecordedPlanner_FindsConsumerThatRanBeforeItsProducer()
    {
        RenderGraphPlanSnapshot snapshot = RenderGraphRecordedPlanner.Plan(EPlanSnapshotOrigin.OpenGL, BuildConsumerFirstPasses(), [0, 1], 7);

        snapshot.Revision.ShouldBe(7ul);
        snapshot.Batches.Count.ShouldBe(2);
        snapshot.OrderViolations.ShouldNotBeEmpty();
        snapshot.OrderViolations.ShouldAllBe(violation => violation.ProducerPassIndex == 1 && violation.ConsumerPassIndex == 0);

        snapshot.TryGetPass(2, out PlanPassSnapshot? debug).ShouldBeTrue();
        debug!.Executed.ShouldBeFalse();
        snapshot.Passes[^1].PassIndex.ShouldBe(2);
        snapshot.Reorders.ShouldBeEmpty();
    }

    [Test]
    public void RecordedPlanner_ReportsUnknownPassIndices()
    {
        RenderGraphPlanSnapshot snapshot = RenderGraphRecordedPlanner.Plan(EPlanSnapshotOrigin.OpenGL, BuildConsumerFirstPasses(), [1, 99, 0], 1);

        snapshot.OrderViolations.ShouldBeEmpty();
        snapshot.Notes.ShouldContain(note => note.Contains("#99", StringComparison.Ordinal));
    }

    [Test]
    public void ExecutionTrace_PublishesOnlyWhenTheOrderChanges()
    {
        RenderPipelineExecutionTrace trace = new();
        trace.HasRecords.ShouldBeFalse();

        trace.BeginFrame();
        trace.Record(3);
        trace.Record(1);
        trace.Record(3);
        trace.Publish(10);
        trace.LastExecutedPassOrder.ShouldBe([3, 1]);
        int version = trace.Version;
        IReadOnlyList<int> published = trace.LastExecutedPassOrder;

        trace.BeginFrame();
        trace.Record(3);
        trace.Record(1);
        trace.Publish(11);
        trace.Version.ShouldBe(version);
        trace.LastFrameId.ShouldBe(11ul);
        ReferenceEquals(published, trace.LastExecutedPassOrder).ShouldBeTrue();

        trace.BeginFrame();
        trace.Record(1);
        trace.Publish(12);
        trace.Version.ShouldNotBe(version);
        trace.LastExecutedPassOrder.ShouldBe([1]);
    }
}
