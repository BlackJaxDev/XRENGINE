using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Components.Scene;
using XREngine.Input;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Animation;

/// <summary>Exercises the production scene collection against copied runtime discovery records.</summary>
[NonParallelizable]
public class VRTrackerCollectionLifecycleTests
{
    [Test]
    public void RoleChangeAndReconnect_KeepOnePhysicalNode()
    {
        WithCollection((collection, service) =>
        {
            RuntimeVrTrackerInfo tracker = Tracker();
            service.Trackers = [tracker];
            Refresh(collection);
            VRTrackerTransform original = collection.OpenXrTrackers[tracker.UserPath];
            service.Trackers = [tracker with { RolePath = "/user/vive_tracker_htcx/role/camera", RoleName = "camera", Connected = false }];
            Refresh(collection);
            collection.Trackers.Count.ShouldBe(1);
            collection.OpenXrTrackers[tracker.UserPath].ShouldBeSameAs(original);
            service.Trackers = [tracker with { Connected = true }];
            Refresh(collection);
            collection.OpenXrTrackers[tracker.UserPath].ShouldBeSameAs(original);
        });
    }

    [Test]
    public void NewSession_ReplacesOwnedNodeWithoutDuplicates()
    {
        WithCollection((collection, service) =>
        {
            RuntimeVrTrackerInfo tracker = Tracker();
            service.Trackers = [tracker];
            Refresh(collection);
            VRTrackerTransform original = collection.OpenXrTrackers[tracker.UserPath];
            service.Trackers = [tracker with { SessionGeneration = 2 }];
            Refresh(collection);
            collection.Trackers.Count.ShouldBe(1);
            collection.OpenXrTrackers.Count.ShouldBe(1);
            collection.OpenXrTrackers[tracker.UserPath].ShouldNotBeSameAs(original);
            collection.SceneNode.Transform.Children.Count.ShouldBe(1);
        });
    }

    [Test]
    public void Deactivation_ReleasesOwnedNodesAndAllowsCleanReactivation()
    {
        WithCollection((collection, service) =>
        {
            service.Trackers = [Tracker()];
            Refresh(collection);
            collection.AddManualTracker();
            collection.SceneNode.Transform.Children.Count.ShouldBe(2);
            typeof(VRTrackerCollectionComponent).GetMethod("OnComponentDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collection, null);
            collection.Trackers.Count.ShouldBe(0);
            collection.OpenXrTrackers.Count.ShouldBe(0);
            collection.SceneNode.Transform.Children.Count.ShouldBe(0);
            Refresh(collection);
            collection.SceneNode.Transform.Children.Count.ShouldBe(1);
        });
    }

    private static RuntimeVrTrackerInfo Tracker()
        => new("/tracker/device-a", "/tracker/device-a", null, null, false, true) { Connected = true, SessionGeneration = 1 };

    private static void Refresh(VRTrackerCollectionComponent collection)
        => typeof(VRTrackerCollectionComponent).GetMethod("ReverifyTrackedDevices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(collection, null);

    private static void WithCollection(Action<VRTrackerCollectionComponent, StubVrTrackingServices> test)
    {
        IRuntimeVrStateServices previous = RuntimeVrStateServices.Current;
        var service = new StubVrTrackingServices();
        var node = new SceneNode("Tracker collection test", new Transform());
        try
        {
            RuntimeVrStateServices.Current = service;
            test(node.AddComponent<VRTrackerCollectionComponent>()!, service);
        }
        finally
        {
            node.Destroy(true);
            RuntimeVrStateServices.Current = previous;
        }
    }
}
