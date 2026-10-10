using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class AdvancedGpuSceneIdentityCacheTests
{
    [Test]
    public void StableSceneReusesIdentityGroupsAndDeliversFreshPublication()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);

        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot first =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        fixture.Publisher.TryGetCanonicalCompatibilityReason(command, 0,
            out EAdvancedCanonicalCompatibilityReason compatibilityReason);
        Assert.That(first.IsValid, Is.True,
            $"commands={fixture.Scene.TotalCommandCount}, rejected={fixture.Publisher.PublicationRejected}, " +
            $"failure={fixture.Publisher.LastPublicationFailure}, compatibility={compatibilityReason}, " +
            $"publication={fixture.Publisher.CurrentPublication.IsValid}");
        first.Handles!.Count.ShouldBe(1);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBe(1);

        fixture.Publish();
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBe(1);
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot second =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        second.IsValid.ShouldBeTrue();
        second.Primary.Handle.ShouldBe(first.Primary.Handle);
        second.Publication.ShouldBe(fixture.Publisher.CurrentPublication);
        fixture.Publisher.LastRegistrationIdentityReuseCount.ShouldBe(1);
        fixture.Publisher.LastRegistrationLookupRebuildCount.ShouldBe(0);
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBe(0);
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);
    }

    [Test]
    public void RemovedSourceReceivesInvalidIdentityAndReusedSlotHasNewGeneration()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderInfo3D removedInfo = fixture.AddSource(out RenderCommandMesh3D removed);
        fixture.AddSource(out RenderCommandMesh3D survivor);
        fixture.Publish();
        AdvancedGpuHandle removedDraw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(removed).Primary.Handle;
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(2);

        fixture.Scene.Remove(removedInfo);
        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed).Primary.Handle.IsValid
            .ShouldBeFalse();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(survivor).IsValid
            .ShouldBeTrue();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);

        fixture.AddSource(out RenderCommandMesh3D replacement);
        fixture.Publish();
        AdvancedGpuHandle replacementDraw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(replacement).Primary.Handle;
        replacementDraw.IsValid.ShouldBeTrue();
        replacementDraw.Index.ShouldBe(removedDraw.Index);
        replacementDraw.Generation.ShouldNotBe(removedDraw.Generation);
        fixture.Publisher.TryGetCanonicalDraw(removed, out _).ShouldBeFalse();
    }

    [Test]
    public void PrimitiveGrowthAndShrinkDeliverExactHandleSlices()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderInfo3D info = fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        AdvancedGpuHandle first = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(command).Primary.Handle;
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);

        fixture.SetPrimitiveCount(info, command, 2);
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot grown =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        grown.Handles!.Count.ShouldBe(2);
        grown.Handles.Handles[0].ShouldBe(first);
        grown.Handles.Handles[1].IsValid.ShouldBeTrue();
        grown.Handles.Handles[1].ShouldNotBe(first);
        fixture.Publisher.TryGetCanonicalDraw(command, 1, out AdvancedGpuHandle second)
            .ShouldBeTrue();
        grown.Handles.Handles[1].ShouldBe(second);
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);

        fixture.SetPrimitiveCount(info, command, 1);
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot shrunk =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        shrunk.Handles!.Count.ShouldBe(2);
        shrunk.Primary.Handle.ShouldBe(first);
        shrunk.Handles.Handles[1].IsValid.ShouldBeFalse();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Handles!.Count.ShouldBe(1);
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
    }

    [Test]
    public void ReorderedCommandsRebuildThenReuseExactSources()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderInfo3D firstInfo = fixture.AddSource(out RenderCommandMesh3D first);
        RenderInfo3D secondInfo = fixture.AddSource(out RenderCommandMesh3D second);
        fixture.Publish();
        AdvancedGpuHandle firstDraw = AdvancedGpuSceneIdentityCacheFixture.Snapshot(first).Primary.Handle;
        AdvancedGpuHandle secondDraw = AdvancedGpuSceneIdentityCacheFixture.Snapshot(second).Primary.Handle;
        fixture.Publish();
        fixture.Publish();

        fixture.Scene.Remove(firstInfo);
        fixture.Scene.Remove(secondInfo);
        fixture.Scene.Add(secondInfo);
        fixture.Scene.Add(firstInfo);
        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(first).Primary.Handle.ShouldBe(firstDraw);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(second).Primary.Handle.ShouldBe(secondDraw);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBeGreaterThan(0);
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(2);
    }

    [Test]
    public void FailedIdentityCallbackRetainsAllRecipientsUntilRetry()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D first);
        fixture.AddSource(out RenderCommandMesh3D second);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuHandle firstDraw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(first).Primary.Handle;
        AdvancedGpuHandle secondDraw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(second).Primary.Handle;
        int firstCallbacks = 0;
        first.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "PublishCanonicalDrawIdentities")
                ++firstCallbacks;
        };
        int failures = 1;
        second.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "PublishCanonicalDrawIdentities" && failures-- > 0)
                throw new InvalidOperationException("Injected identity callback failure.");
        };
        second.WorldMatrixIsModelMatrix = true;
        second.WorldMatrix = Matrix4x4.CreateTranslation(1f, 0f, 0f);

        Should.Throw<InvalidOperationException>(fixture.Publish);
        firstCallbacks.ShouldBeGreaterThan(0);
        fixture.Publisher.PublicationRejected.ShouldBeTrue();
        fixture.Publisher.CurrentPublication.IsValid.ShouldBeFalse();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(2);

        second.WorldMatrix = Matrix4x4.CreateTranslation(2f, 0f, 0f);
        int priorArmed = AdvancedPublicationFaultInjection.ArmedPreflightRejections;
        try
        {
            AdvancedPublicationFaultInjection.ArmPreflightRejections(1);
            fixture.Publish();
            fixture.Publisher.PublicationRejected.ShouldBeTrue();
            fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(2);
        }
        finally
        {
            AdvancedPublicationFaultInjection.ArmPreflightRejections(priorArmed);
        }

        fixture.Publish();
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(first).Publication
            .ShouldBe(fixture.Publisher.CurrentPublication);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(second).Publication
            .ShouldBe(fixture.Publisher.CurrentPublication);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(first).Primary.Handle
            .ShouldBe(firstDraw);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(second).Primary.Handle
            .ShouldBe(secondDraw);
    }

    [Test]
    public void RepeatedFailedDeliveryKeepsRemovedRecipientUntilCompleteRetry()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D active);
        RenderInfo3D removedInfo = fixture.AddSource(out RenderCommandMesh3D removed);
        fixture.Publish();
        fixture.Publish();
        fixture.Scene.Remove(removedInfo);
        int failures = 2;
        active.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "PublishCanonicalDrawIdentities" && failures-- > 0)
                throw new InvalidOperationException("Injected retry failure.");
        };

        Should.Throw<InvalidOperationException>(fixture.Publish);
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(2);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed).Primary.Handle.IsValid
            .ShouldBeTrue();
        Should.Throw<InvalidOperationException>(fixture.Publish);
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(2);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed).Primary.Handle.IsValid
            .ShouldBeTrue();

        fixture.Publish();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed).Primary.Handle.IsValid
            .ShouldBeFalse();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(active).Publication
            .ShouldBe(fixture.Publisher.CurrentPublication);
        GetSourceArray(fixture.Publisher, "_pendingIdentitySources")
            .ShouldAllBe(source => source == null);
        fixture.Publish();
        fixture.Publish();
        GetSourceArray(fixture.Publisher, "_retainedIdentitySources")
            .ShouldNotContain(removed);
    }

    [Test]
    public void RetainedSourceTailClearsAfterMembershipShrinkAndDisposal()
    {
        AdvancedGpuSceneIdentityCacheFixture fixture = new();
        try
        {
            RenderInfo3D removedInfo = fixture.AddSource(out RenderCommandMesh3D removed);
            fixture.AddSource(out _);
            fixture.Publish();
            fixture.Publish();
            fixture.Publish();
            fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(2);
            fixture.Scene.Remove(removedInfo);
            fixture.Publish();
            fixture.Publish();
            fixture.Publish();
            IRenderCommandMesh?[] retained = GetSourceArray(fixture.Publisher,
                "_retainedIdentitySources");
            retained.ShouldNotContain(removed);
            OrderedRowsContainSource(fixture.Publisher, removed).ShouldBeFalse();
        }
        finally
        {
            fixture.Dispose();
        }

        GetSourceArray(fixture.Publisher, "_plannedIdentitySources")
            .ShouldAllBe(source => source == null);
        GetSourceArray(fixture.Publisher, "_retainedIdentitySources")
            .ShouldAllBe(source => source == null);
        GetSourceArray(fixture.Publisher, "_pendingIdentitySources")
            .ShouldAllBe(source => source == null);
        OrderedRowsContainAnySource(fixture.Publisher).ShouldBeFalse();
    }

    [Test]
    public void DisposalClearsPendingRecipientsAfterCallbackFailure()
    {
        AdvancedGpuSceneIdentityCacheFixture fixture = new();
        try
        {
            fixture.AddSource(out RenderCommandMesh3D command);
            fixture.Publish();
            fixture.Publish();
            fixture.Publish();
            fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
            command.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "PublishCanonicalDrawIdentities")
                    throw new InvalidOperationException("Injected callback failure.");
            };
            command.WorldMatrixIsModelMatrix = true;
            command.WorldMatrix = Matrix4x4.CreateTranslation(1f, 0f, 0f);
            Should.Throw<InvalidOperationException>(fixture.Publish);
            fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(1);
        }
        finally
        {
            fixture.Dispose();
        }

        GetSourceArray(fixture.Publisher, "_pendingIdentitySources")
            .ShouldAllBe(source => source == null);
        OrderedRowsContainAnySource(fixture.Publisher).ShouldBeFalse();
    }

    private static IRenderCommandMesh?[] GetSourceArray(
        AdvancedGpuScenePublisher publisher, string fieldName)
        => (IRenderCommandMesh?[])typeof(AdvancedGpuScenePublisher)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(publisher)!;

    private static bool OrderedRowsContainSource(
        AdvancedGpuScenePublisher publisher, IRenderCommandMesh source)
    {
        Array rows = (Array)typeof(AdvancedGpuScenePublisher)
            .GetField("_orderedRegistrationIdentities",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(publisher)!;
        foreach (object row in rows)
            if (ReferenceEquals(row.GetType().GetProperty("Source")!.GetValue(row), source))
                return true;
        return false;
    }

    private static bool OrderedRowsContainAnySource(AdvancedGpuScenePublisher publisher)
    {
        Array rows = (Array)typeof(AdvancedGpuScenePublisher)
            .GetField("_orderedRegistrationIdentities",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(publisher)!;
        foreach (object row in rows)
            if (row.GetType().GetProperty("Source")!.GetValue(row) is not null)
                return true;
        return false;
    }
}
