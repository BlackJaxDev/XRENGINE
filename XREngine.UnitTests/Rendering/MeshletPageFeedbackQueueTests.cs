using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// The feedback queue moves the page requests and touch bits of completed GPU frames from the
/// render thread to the publisher. These tests check that it copies the data, gives the frames to
/// the streaming manager oldest first, replaces the oldest frame when it is full, and clears.
/// </summary>
[TestFixture]
public sealed class MeshletPageFeedbackQueueTests
{
    [Test]
    public void DrainTo_GivesEveryQueuedFrameToTheManager()
    {
        MeshletPageStreamingManagerTests.Fixture fixture = new(slotCount: 4);
        MeshletPageFeedbackQueue queue = new();
        queue.Enqueue([fixture.MakeRequest(3, EMeshletPageRequestBand.Prefetch)], 1, [], 10);
        queue.Enqueue([], 2, [], 11);

        queue.DrainTo(fixture.Manager).ShouldBe(2);
        fixture.Update(12);

        queue.Count.ShouldBe(0);
        fixture.Manager.Telemetry.TotalRequestOverflows.ShouldBe(3L);
        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.Resident);
    }

    [Test]
    public void Enqueue_CopiesTheRequestsBeforeTheCallerReusesTheBuffer()
    {
        MeshletPageStreamingManagerTests.Fixture fixture = new(slotCount: 4);
        MeshletPageFeedbackQueue queue = new();
        MeshletPageRequest[] buffer = [fixture.MakeRequest(3, EMeshletPageRequestBand.Prefetch)];
        queue.Enqueue(buffer, 0, [], 10);
        buffer[0] = fixture.MakeRequest(1, EMeshletPageRequestBand.Prefetch);

        queue.DrainTo(fixture.Manager);
        fixture.Update(11);

        fixture.Page(3).State.ShouldBe(EMeshletPageResidencyState.Resident);
        fixture.Page(1).State.ShouldBe(EMeshletPageResidencyState.NotResident);
    }

    [Test]
    public void Enqueue_ReplacesTheOldestFrameWhenTheQueueIsFull()
    {
        MeshletPageStreamingManagerTests.Fixture fixture = new(slotCount: 4);
        MeshletPageFeedbackQueue queue = new(frameCapacity: 2);
        queue.Enqueue([], 1, [], 10);
        queue.Enqueue([], 2, [], 11);
        queue.Enqueue([], 4, [], 12);

        queue.DroppedFrames.ShouldBe(1L);
        queue.Count.ShouldBe(2);
        queue.DrainTo(fixture.Manager).ShouldBe(2);
        fixture.Manager.Telemetry.TotalRequestOverflows.ShouldBe(6L);
    }

    [Test]
    public void Enqueue_PassesTheTouchBitsWithTheFeedbackFrame()
    {
        MeshletPageStreamingManagerTests.Fixture fixture = new(slotCount: 4);
        MeshletPageFeedbackQueue queue = new();
        uint rootSlot = fixture.Pool.GetSlot(fixture.Global(0));
        queue.Enqueue([], 0, [1u << (int)rootSlot], 500);

        queue.DrainTo(fixture.Manager);

        fixture.Page(0).LastTouchFrame.ShouldBe(500L);
    }

    [Test]
    public void Clear_DropsTheQueuedFrames()
    {
        MeshletPageStreamingManagerTests.Fixture fixture = new(slotCount: 4);
        MeshletPageFeedbackQueue queue = new();
        queue.Enqueue([fixture.MakeRequest(3, EMeshletPageRequestBand.Prefetch)], 0, [], 10);

        queue.Clear();

        queue.DrainTo(fixture.Manager).ShouldBe(0);
        queue.EnqueuedFrames.ShouldBe(1L);
    }
}
