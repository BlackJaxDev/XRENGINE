using NUnit.Framework;
using Shouldly;
using XREngine.Input;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.VR;

[TestFixture, NonParallelizable]
public sealed class DesktopVrServicesLeaseTeleportTests
{
    private sealed class SentinelPublisher : IRuntimeVrTeleportPublisher
    {
        public int Count { get; private set; }

        public void PublishLocalPlayerTeleport() => Count++;
    }

    private static RuntimeApplicationProfile Profile(bool allowsVr)
        => new("TeleportLeaseTest", RuntimeAdapterProfile.None, AllowsWindows: false, AllowsVr: allowsVr, RegisterRendererBackends: false);

    [Test]
    public void VrLease_InstallsTheTeleportPublisherAndRestoresThePreviousOne()
    {
        IRuntimeVrTeleportPublisher? original = RuntimeEngine.VRState.TeleportPublisher;
        SentinelPublisher sentinel = new();
        RuntimeEngine.VRState.TeleportPublisher = sentinel;
        try
        {
            using (new DesktopVrServicesLease(Profile(allowsVr: true)))
            {
                RuntimeEngine.VRState.TeleportPublisher.ShouldNotBeSameAs(sentinel);
                long version = RuntimeVrDiscontinuityServices.Version;
                EVrPoseDiscontinuity? kind = null;
                void OnPublished(VrPoseDiscontinuity discontinuity) => kind = discontinuity.Kind;
                RuntimeVrDiscontinuityServices.Published += OnPublished;
                try
                {
                    RuntimeEngine.VRState.TryPublishLocalPlayerTeleport().ShouldBeTrue();
                }
                finally
                {
                    RuntimeVrDiscontinuityServices.Published -= OnPublished;
                }

                RuntimeVrDiscontinuityServices.Version.ShouldBe(version + 1);
                kind.ShouldBe(EVrPoseDiscontinuity.Teleport);
                sentinel.Count.ShouldBe(0);
            }

            RuntimeEngine.VRState.TeleportPublisher.ShouldBeSameAs(sentinel);
        }
        finally
        {
            RuntimeEngine.VRState.TeleportPublisher = original;
        }
    }

    [Test]
    public void LeaseWithoutVr_KeepsThePreviousTeleportPublisher()
    {
        IRuntimeVrTeleportPublisher? original = RuntimeEngine.VRState.TeleportPublisher;
        SentinelPublisher sentinel = new();
        RuntimeEngine.VRState.TeleportPublisher = sentinel;
        try
        {
            using (new DesktopVrServicesLease(Profile(allowsVr: false)))
                RuntimeEngine.VRState.TeleportPublisher.ShouldBeSameAs(sentinel);

            RuntimeEngine.VRState.TeleportPublisher.ShouldBeSameAs(sentinel);
        }
        finally
        {
            RuntimeEngine.VRState.TeleportPublisher = original;
        }
    }

    [Test]
    public void NoPublisher_TryPublishReturnsFalse()
    {
        IRuntimeVrTeleportPublisher? original = RuntimeEngine.VRState.TeleportPublisher;
        RuntimeEngine.VRState.TeleportPublisher = null;
        try
        {
            RuntimeEngine.VRState.TryPublishLocalPlayerTeleport().ShouldBeFalse();
        }
        finally
        {
            RuntimeEngine.VRState.TeleportPublisher = original;
        }
    }
}
