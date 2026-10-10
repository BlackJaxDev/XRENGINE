using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class AdvancedGpuSceneIdentityCacheGrowthTests
{
    [Test]
    public void ChangedTransformCommitsFreshPublicationWithReusedIdentityGroup()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot before =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);

        command.WorldMatrixIsModelMatrix = true;
        command.WorldMatrix = Matrix4x4.CreateTranslation(3f, 0f, 0f);
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot after =
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);

        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        after.Publication.Sequence.ShouldBeGreaterThan(before.Publication.Sequence);
        after.Publication.ShouldBe(fixture.Publisher.CurrentPublication);
        after.Primary.Handle.ShouldBe(before.Primary.Handle);
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        fixture.Publisher.LastRegistrationIdentityReuseCount.ShouldBe(1);
    }

    [Test]
    public void PinnedUnchangedPublicationRefreshesCommandIdentityWithoutNewSequence()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuScenePublicationReference publication = fixture.Publisher.CurrentPublication;
        fixture.Publisher.Database.TryAcquirePublicationLease(
            in publication, EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease).ShouldBeTrue();
        using (lease)
        {
            SetCommandIdentitySnapshot(command, default);
            fixture.Publish();
            fixture.Publisher.CurrentPublication.ShouldBe(publication);
            AdvancedGpuSceneDrawIdentitySnapshot delivered =
                AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
            delivered.Publication.ShouldBe(publication);
            delivered.IsValid.ShouldBeTrue();
            fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        }
    }

    [Test]
    public void RegistrationLookupGrowthPreservesAllLiveSourceIdentities()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        RenderCommandMesh3D[] commands = new RenderCommandMesh3D[65];
        fixture.AddSource(out commands[0]);
        fixture.Publish();
        AdvancedGpuHandle firstDraw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(commands[0]).Primary.Handle;
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        for (int index = 1; index < commands.Length; ++index)
            fixture.AddSource(out commands[index]);

        fixture.Publish();
        fixture.Publisher.PublicationRejected.ShouldBeFalse(
            fixture.Publisher.LastPublicationFailure);
        fixture.Publisher.LastRegistrationLookupRebuildCount.ShouldBeGreaterThan(0);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBeGreaterThan(0);
        HashSet<AdvancedGpuHandle> uniqueDraws = [];
        for (int index = 0; index < commands.Length; ++index)
        {
            AdvancedGpuSceneDrawIdentitySnapshot snapshot =
                AdvancedGpuSceneIdentityCacheFixture.Snapshot(commands[index]);
            snapshot.IsValid.ShouldBeTrue();
            snapshot.Publication.ShouldBe(fixture.Publisher.CurrentPublication);
            fixture.Publisher.TryGetCanonicalDraw(commands[index], out AdvancedGpuHandle indexedDraw)
                .ShouldBeTrue();
            snapshot.Primary.Handle.ShouldBe(indexedDraw);
            uniqueDraws.Add(snapshot.Primary.Handle).ShouldBeTrue();
        }
        uniqueDraws.Count.ShouldBe(commands.Length);
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(commands[0]).Primary.Handle
            .ShouldBe(firstDraw);

        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastRegistrationIdentityReuseCount.ShouldBe(commands.Length);
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(commands.Length);
    }

    [Test]
    public void PinnedOldPublicationAllowsSceneJournalGrowthWithStableDrawIdentity()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        command.WorldMatrixIsModelMatrix = true;
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuHandle draw = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(command).Primary.Handle;
        AdvancedGpuScenePublicationReference oldPublication =
            fixture.Publisher.CurrentPublication;
        fixture.Publisher.Database.TryAcquirePublicationLease(
            in oldPublication, EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease).ShouldBeTrue();
        using (lease)
        {
            uint initialCapacity = fixture.Publisher.Database.Capacities.Scene.TransformRecords;
            bool grew = false;
            for (int index = 1; index <= 300; ++index)
            {
                command.WorldMatrix = Matrix4x4.CreateTranslation(index, 0f, 0f);
                fixture.Publish();
                fixture.Publisher.PublicationRejected.ShouldBeFalse(
                    fixture.Publisher.LastPublicationFailure);
                AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Primary.Handle
                    .ShouldBe(draw);
                if (fixture.Publisher.Database.Capacities.Scene.TransformRecords <=
                    initialCapacity)
                    continue;
                fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
                fixture.Publisher.LastSourceGroupRebuildCount.ShouldBe(0);
                grew = true;
                break;
            }

            grew.ShouldBeTrue("The pinned publication must reach a scene journal boundary.");
            fixture.Publisher.CurrentPublication.Sequence
                .ShouldBeGreaterThan(oldPublication.Sequence);
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Publication
                .ShouldBe(fixture.Publisher.CurrentPublication);
        }
    }

    [Test]
    public void UnsupportedPassReceivesInvalidIdentityThenRecovers()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        var info = fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        AdvancedGpuHandle first = AdvancedGpuSceneIdentityCacheFixture
            .Snapshot(command).Primary.Handle;
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);

        fixture.Scene.Remove(info);
        command.RenderPass = (int)EDefaultRenderPass.Background;
        fixture.Scene.Add(info);
        fixture.Publish();
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Primary.Handle.IsValid
            .ShouldBeFalse();
        fixture.Publisher.TryGetCanonicalCompatibilityReason(command, 0,
            out EAdvancedCanonicalCompatibilityReason reason).ShouldBeTrue();
        reason.ShouldBe(EAdvancedCanonicalCompatibilityReason.UnsupportedRenderPass);
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);

        fixture.Scene.Remove(info);
        command.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
        fixture.Scene.Add(info);
        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).IsValid.ShouldBeTrue();
        fixture.Publisher.TryGetCanonicalDraw(command, out AdvancedGpuHandle current)
            .ShouldBeTrue();
        current.ShouldNotBe(first);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingOrNullSourceClearsIdentityAndRecovers(bool nullEntry)
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).IsValid.ShouldBeTrue();
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);

        fixture.PublishWithMissingSource(nullEntry);
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Primary.Handle.IsValid
            .ShouldBeFalse();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);

        fixture.Publish();
        AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).IsValid.ShouldBeTrue();
    }

    [Test]
    public void InjectedPreflightRejectionLeavesPriorIdentityCurrent()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        AdvancedGpuScenePublicationReference prior = fixture.Publisher.CurrentPublication;
        command.WorldMatrixIsModelMatrix = true;
        command.WorldMatrix = Matrix4x4.CreateTranslation(4f, 0f, 0f);
        int priorArmed = AdvancedPublicationFaultInjection.ArmedPreflightRejections;
        try
        {
            AdvancedPublicationFaultInjection.ArmPreflightRejections(1);
            fixture.Publish();
            fixture.Publisher.PublicationRejected.ShouldBeTrue();
            fixture.Publisher.LastPublicationFailure!.ShouldContain("Injected");
            fixture.Publisher.CurrentPublication.ShouldBe(prior);
            AdvancedGpuSceneIdentityCacheFixture.Snapshot(command).Publication.ShouldBe(prior);
        }
        finally
        {
            AdvancedPublicationFaultInjection.ArmPreflightRejections(priorArmed);
        }

        fixture.Publish();
        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        fixture.Publisher.CurrentPublication.Sequence.ShouldBeGreaterThan(prior.Sequence);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBeGreaterThan(0);
    }

    private static void SetCommandIdentitySnapshot(
        RenderCommandMesh3D command,
        AdvancedGpuSceneDrawIdentitySnapshot snapshot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RenderCommandMesh3D).GetField("_canonicalDrawIdentitySnapshot", flags)!
            .SetValue(command, snapshot);
        typeof(RenderCommandMesh3D).GetField("_renderCanonicalDrawIdentitySnapshot", flags)!
            .SetValue(command, snapshot);
    }
}
