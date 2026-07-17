using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Timers;

namespace XREngine.UnitTests.Core;

public sealed class CollectVisibleGenerationGateTests
{
    [Test]
    public void BootstrapGeneration_IsConsumedBeforeCollectedGenerations()
    {
        var gate = new CollectVisibleGenerationGate();

        gate.TryConsumeFresh(out long bootstrapGeneration).ShouldBeTrue();

        bootstrapGeneration.ShouldBe(0L);
        gate.ConsumedGeneration.ShouldBe(0L);
        gate.RequiredGeneration.ShouldBe(1L);
        gate.IsFreshGenerationAvailable.ShouldBeFalse();
    }

    [Test]
    public void BlockUntilFresh_WaitsForExactCompletedPublication()
    {
        var gate = new CollectVisibleGenerationGate();
        gate.TryConsumeFresh(out _).ShouldBeTrue();
        long generation = gate.RequestNextCollect();
        gate.MarkCollectCompleted(generation);

        using var waiterEntered = new ManualResetEventSlim(false);
        Task<long> waiter = Task.Run(() =>
        {
            waiterEntered.Set();
            if (!gate.WaitForPublication())
                return -1L;

            return gate.TryConsumeFresh(out long consumed) ? consumed : -2L;
        });

        waiterEntered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        waiter.IsCompleted.ShouldBeFalse();

        gate.Publish(generation);

        waiter.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        waiter.Result.ShouldBe(generation);
        gate.PublishedGeneration.ShouldBe(generation);
        gate.ConsumedGeneration.ShouldBe(generation);
    }

    [Test]
    public void ReusePreviousVisibility_IsBoundedOncePerRequiredGeneration()
    {
        var gate = new CollectVisibleGenerationGate();
        gate.TryConsumeFresh(out _).ShouldBeTrue();
        long generation = gate.RequestNextCollect();
        gate.MarkCollectCompleted(generation);

        gate.CanReusePreviousForRequiredGeneration(ECollectVisibleLatePolicy.BlockUntilFresh).ShouldBeFalse();
        gate.CanReusePreviousForRequiredGeneration(ECollectVisibleLatePolicy.ReusePreviousVisibility).ShouldBeTrue();
        gate.TryRecordStaleReuse(generation).ShouldBeTrue();
        gate.CanReusePreviousForRequiredGeneration(ECollectVisibleLatePolicy.ReusePreviousVisibility).ShouldBeFalse();
        gate.TryRecordStaleReuse(generation).ShouldBeFalse();

        gate.Publish(generation);
        gate.TryConsumeFresh(out long consumed).ShouldBeTrue();
        consumed.ShouldBe(generation);

        long nextGeneration = gate.RequestNextCollect();
        gate.MarkCollectCompleted(nextGeneration);
        gate.CanReusePreviousForRequiredGeneration(ECollectVisibleLatePolicy.ReusePreviousVisibility).ShouldBeTrue();
    }

    [Test]
    public void Terminate_ReleasesPublicationWaiterWithTerminalResult()
    {
        var gate = new CollectVisibleGenerationGate();
        gate.TryConsumeFresh(out _).ShouldBeTrue();

        using var waiterEntered = new ManualResetEventSlim(false);
        Task<bool> waiter = Task.Run(() =>
        {
            waiterEntered.Set();
            return gate.WaitForPublication();
        });

        waiterEntered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        waiter.IsCompleted.ShouldBeFalse();

        gate.Terminate();

        waiter.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        waiter.Result.ShouldBeFalse();
    }

    [Test]
    public void MovingCameraVisibility_AppearsWithItsMatchingPublishedGeneration()
    {
        var gate = new CollectVisibleGenerationGate();
        gate.TryConsumeFresh(out _).ShouldBeTrue();
        var bounds = AABB.FromCenterSize(new Vector3(0.0f, 0.0f, -5.0f), new Vector3(1.0f));
        var initialFrustum = new Frustum(
            60.0f,
            1.0f,
            0.1f,
            100.0f,
            Vector3.UnitZ,
            Vector3.UnitY,
            Vector3.Zero);
        var movedCameraFrustum = new Frustum(
            60.0f,
            1.0f,
            0.1f,
            100.0f,
            -Vector3.UnitZ,
            Vector3.UnitY,
            Vector3.Zero);
        bool renderingVisibility = initialFrustum.Intersects(bounds);
        bool updatingVisibility = movedCameraFrustum.Intersects(bounds);
        renderingVisibility.ShouldBeFalse();
        updatingVisibility.ShouldBeTrue();
        long movingCameraGeneration = gate.RequestNextCollect();
        gate.MarkCollectCompleted(movingCameraGeneration);

        using var waiterEntered = new ManualResetEventSlim(false);
        Task<(long Generation, bool Visible)> render = Task.Run(() =>
        {
            waiterEntered.Set();
            if (!gate.WaitForPublication() || !gate.TryConsumeFresh(out long consumed))
                return (-1L, false);

            return (consumed, Volatile.Read(ref renderingVisibility));
        });

        waiterEntered.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        render.IsCompleted.ShouldBeFalse();
        Volatile.Read(ref renderingVisibility).ShouldBeFalse();

        Volatile.Write(ref renderingVisibility, updatingVisibility);
        gate.Publish(movingCameraGeneration);

        render.Wait(TimeSpan.FromSeconds(2)).ShouldBeTrue();
        render.Result.Generation.ShouldBe(movingCameraGeneration);
        render.Result.Visible.ShouldBeTrue();
    }
}
