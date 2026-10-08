using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class AdvancedGpuSceneIdentityDeliveryBoundaryTests
{
    [Test]
    public void ReusedPublicationCallbackFailureHidesIdentityUntilSamePublicationRetry()
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D command);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuSceneDrawIdentitySnapshot accepted = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
        AdvancedGpuScenePublicationReference publication = accepted.Publication;
        fixture.Publisher.Database.TryAcquirePublicationLease(
            in publication, EAdvancedGpuScenePublicationPinKind.Package,
            out AdvancedGpuScenePublicationLease lease).ShouldBeTrue();
        using (lease)
        {
            // Clear the delivered identity so unchanged publication must notify the command.
            foreach (string field in new[] { "_canonicalDrawIdentitySnapshot", "_renderCanonicalDrawIdentitySnapshot" })
                typeof(RenderCommandMesh3D).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(command, default(AdvancedGpuSceneDrawIdentitySnapshot));
            bool fail = true;
            command.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "PublishCanonicalDrawIdentities" || !fail)
                    return;
                fail = false;
                throw new InvalidOperationException("Injected reused-publication callback failure.");
            };

            Should.Throw<InvalidOperationException>(fixture.Publish).Message
                .ShouldBe("Injected reused-publication callback failure.");
            fixture.Publisher.Sequence.ShouldBe(publication.Sequence);
            fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
            fixture.Publisher.PublicationFaulted.ShouldBeFalse();
            fixture.Publisher.PublicationRejected.ShouldBeTrue();
            fixture.Publisher.CurrentPublication.IsValid.ShouldBeFalse();
            fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(1);

            fixture.Publish();

            fixture.Publisher.CurrentPublication.ShouldBe(publication);
            fixture.Publisher.PublicationRejected.ShouldBeFalse();
            fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);
            AdvancedGpuSceneDrawIdentitySnapshot retried = AdvancedGpuSceneIdentityCacheFixture.Snapshot(command);
            retried.IsValid.ShouldBeTrue();
            retried.Publication.ShouldBe(publication);
            retried.Primary.Handle.ShouldBe(accepted.Primary.Handle);
            fixture.Publish();
            fixture.Publish();
            fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        }
    }

    [TestCase("CaptureAndCommit", true, false)]
    [TestCase("IdentityDelivery", false, false)]
    [TestCase("IdentityDelivery", true, true)]
    public void CommittedScopeFailureRetainsEveryRecipientUntilSuccessfulRetry(
        string scope,
        bool throwOnDispose,
        bool expectNotifications)
    {
        using AdvancedGpuSceneIdentityCacheFixture fixture = new();
        fixture.AddSource(out RenderCommandMesh3D active);
        RenderInfo3D removedInfo = fixture.AddSource(out RenderCommandMesh3D removed);
        fixture.Publish();
        fixture.Publish();
        fixture.Publish();
        AdvancedGpuHandle activeHandle = AdvancedGpuSceneIdentityCacheFixture.Snapshot(active).Primary.Handle;
        ulong acceptedSequence = fixture.Publisher.Sequence;
        fixture.Scene.Remove(removedInfo);

        int identityNotifications = 0;
        active.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "PublishCanonicalDrawIdentities")
                ++identityNotifications;
        };
        removed.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "PublishCanonicalDrawIdentities")
                ++identityNotifications;
        };
        bool hiddenDuringFailure = false;
        IRuntimeRenderingHostServices host = AdvancedGpuSceneIdentityFaultHost.Create(
            "GpuIndirect.AdvancedPublication." + scope,
            throwOnDispose,
            () => hiddenDuringFailure = !fixture.Publisher.CurrentPublication.IsValid,
            out AdvancedGpuSceneIdentityFaultHost fault);

        using (RuntimeRenderingHostServices.Install(host))
        {
            InvalidOperationException failure = Should.Throw<InvalidOperationException>(fixture.Publish);
            failure.Message.ShouldBe("Injected identity-delivery boundary failure.");
        }

        fault.FailureCount.ShouldBe(1);
        hiddenDuringFailure.ShouldBeTrue();
        (identityNotifications != 0).ShouldBe(expectNotifications);
        fixture.Publisher.Sequence.ShouldBeGreaterThan(acceptedSequence);
        fixture.Publisher.PublicationFaulted.ShouldBeFalse();
        fixture.Publisher.PublicationRejected.ShouldBeTrue();
        fixture.Publisher.CurrentPublication.IsValid.ShouldBeFalse();
        fixture.Publisher.LegacyMappings.Length.ShouldBe(0);
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(2);

        fixture.Publish();

        fixture.Publisher.PublicationRejected.ShouldBeFalse();
        fixture.Publisher.CurrentPublication.IsValid.ShouldBeTrue();
        fixture.Publisher.PendingIdentityRecipientCount.ShouldBe(0);
        fixture.Publisher.LegacyMappings.Length.ShouldBe(1);
        AdvancedGpuSceneDrawIdentitySnapshot activeSnapshot = AdvancedGpuSceneIdentityCacheFixture.Snapshot(active);
        AdvancedGpuSceneDrawIdentitySnapshot removedSnapshot = AdvancedGpuSceneIdentityCacheFixture.Snapshot(removed);
        activeSnapshot.Primary.Handle.ShouldBe(activeHandle);
        activeSnapshot.Publication.ShouldBe(fixture.Publisher.CurrentPublication);
        removedSnapshot.Primary.Handle.IsValid.ShouldBeFalse();
        removedSnapshot.Publication.ShouldBe(fixture.Publisher.CurrentPublication);

        fixture.Publish();
        fixture.Publish();
        fixture.Publisher.LastSourceGroupReuseCount.ShouldBe(1);
        fixture.Publisher.LastSourceGroupRebuildCount.ShouldBe(0);
    }
}
