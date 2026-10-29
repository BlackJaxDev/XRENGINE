using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the render-thread state of <see cref="GaussianCacheComponent"/> that needs no GPU: the gate that keeps the
/// compute passes and the draw off during a cache switch, and the overflow read state.
/// </summary>
[TestFixture]
public sealed class GaussianCachePlaybackStateTests
{
    [Test]
    public void UploadGate_CacheSwitch_BlocksDispatchUntilNewLevelTable()
    {
        object cacheA = new();
        object cacheB = new();
        GaussianCacheUploadGate gate = new();
        gate.CanDispatch.ShouldBeFalse();

        gate.Begin(cacheA);
        gate.CanDispatch.ShouldBeFalse("The first level table of cache A is not on the GPU yet.");
        gate.AcceptLevelTable(cacheA, 1).ShouldBeTrue();
        gate.CanDispatch.ShouldBeTrue();

        // The switch happens before the first upload of cache B is collected.
        gate.Begin(cacheB);
        gate.CanDispatch.ShouldBeFalse("The buffers still hold the records and frames of cache A.");
        gate.Accepts(cacheA).ShouldBeFalse();
        gate.AcceptLevelTable(cacheA, 7).ShouldBeFalse("A late upload of cache A must not enable the passes.");
        gate.CanDispatch.ShouldBeFalse();

        gate.Accepts(cacheB).ShouldBeTrue();
        gate.AcceptLevelTable(cacheB, 1).ShouldBeTrue();
        gate.CanDispatch.ShouldBeTrue();
        gate.AcceptLevelTable(cacheB, 1).ShouldBeFalse("An upload that is not newer keeps the table on the GPU.");
        gate.AcceptLevelTable(cacheB, 2).ShouldBeTrue();

        gate.Begin(null);
        gate.CanDispatch.ShouldBeFalse();
        gate.Accepts(null).ShouldBeFalse();
    }

    [Test]
    public void OverflowCheck_CleanRead_StopsPolling()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(loadedNodeCount: 5_000, capacity: 1_000).ShouldBeTrue();
        check.ReadStarted();
        check.IsPending.ShouldBeTrue();
        check.ShouldStartRead(5_000, 1_000).ShouldBeFalse("Only one read is in flight.");

        check.ReadCompleted(0u).ShouldBeFalse();
        check.IsFinished.ShouldBeTrue();
        for (int frame = 0; frame < 100; frame++)
            check.ShouldStartRead(5_000, 1_000).ShouldBeFalse("A clean read ends the checks.");
    }

    [Test]
    public void OverflowCheck_CapacityChange_RestartsAndAllowsANewLog()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(5_000, 1_000).ShouldBeTrue();
        check.ReadStarted();
        check.ReadCompleted(4_000u).ShouldBeTrue("The first overflow is logged.");
        check.HasLogged.ShouldBeTrue();
        check.ShouldStartRead(5_000, 1_000).ShouldBeFalse();

        // New nodes do not restart the checks after a log.
        check.RearmForNewNodes();
        check.ShouldStartRead(6_000, 1_000).ShouldBeFalse();

        // A new capacity (or a new MaxRenderedSplats, or a new cache) restarts them and allows one more log.
        check.Rearm();
        check.HasLogged.ShouldBeFalse();
        check.ShouldStartRead(6_000, 2_000).ShouldBeTrue();
        check.ReadStarted();
        check.ReadCompleted(4_000u).ShouldBeTrue();
        check.ReadCompleted(4_000u).ShouldBeFalse("The log occurs one time for each rearm.");
    }

    [Test]
    public void OverflowCheck_NewNodesAfterCleanRead_Restarts()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(1_500, 1_000).ShouldBeTrue();
        check.ReadStarted();
        check.ReadCompleted(0u).ShouldBeFalse();

        check.RearmForNewNodes();
        check.ShouldStartRead(3_000, 1_000).ShouldBeTrue();
    }

    [Test]
    public void OverflowCheck_NodesWithinCapacity_NeverReads()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(1_000, 1_000).ShouldBeFalse("The cut cannot choose more nodes than are loaded.");
        check.ShouldStartRead(10, 1_000).ShouldBeFalse();
    }

    [Test]
    public void OverflowCheck_NoFenceSupport_StopsReads()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(5_000, 1_000).ShouldBeTrue();
        check.ReadUnavailable();
        for (int frame = 0; frame < 100; frame++)
            check.ShouldStartRead(5_000, 1_000).ShouldBeFalse("A backend without fences never reads the counter.");
    }

    [Test]
    public void OverflowCheck_FailedFence_RetriesOnce()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(5_000, 1_000).ShouldBeTrue();
        check.ReadStarted();
        check.ReadFailed();
        check.IsPending.ShouldBeFalse();
        check.ShouldStartRead(5_000, 1_000).ShouldBeTrue();
    }

    [Test]
    public void OverflowCheck_RearmWhilePending_AbandonsTheRead()
    {
        GaussianCacheOverflowCheck check = new();
        check.ShouldStartRead(5_000, 1_000).ShouldBeTrue();
        check.ReadStarted();
        check.Rearm();
        check.IsPending.ShouldBeFalse();
        check.ShouldStartRead(5_000, 2_000).ShouldBeTrue();
    }
}
