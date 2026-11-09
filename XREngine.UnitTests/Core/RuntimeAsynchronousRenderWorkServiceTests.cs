using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeAsynchronousRenderWorkServiceTests
{
    [Test]
    public void Register_HoldsTwoSlotsAndStopsAfterAdmissionCloses()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var first = new TestConsumer();
        var second = new TestConsumer();
        var third = new TestConsumer();

        service.TryRegister(first).ShouldBeTrue();
        service.TryRegister(first).ShouldBeTrue();
        service.TryRegister(second).ShouldBeTrue();
        service.TryRegister(third).ShouldBeFalse();

        service.Unregister(first);
        service.CloseAdmission();
        service.TryRegister(third).ShouldBeFalse();
    }

    [Test]
    public void Unregister_RejectsAConsumerWithUnresolvedWork()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        service.TryRegister(consumer).ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => service.Unregister(consumer));
        consumer.Unresolved = false;
        service.Unregister(consumer);
    }

    [Test]
    public void Service_RunsOnlyOnTheOwnerThreadAndOnlyForUnresolvedConsumers()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var busy = new TestConsumer { Unresolved = true };
        var idle = new TestConsumer();
        service.TryRegister(busy).ShouldBeTrue();
        service.TryRegister(idle).ShouldBeTrue();

        service.Service();
        busy.ServiceCount.ShouldBe(0);

        service.BindOwnerThread(Environment.CurrentManagedThreadId);
        service.Service();
        busy.ServiceCount.ShouldBe(1);
        idle.ServiceCount.ShouldBe(0);
        service.HasUnresolvedWork.ShouldBeTrue();
    }

    [Test]
    public void DrainRequest_FromAnotherThreadIsAcknowledgedByTheOwnerWithItsGeneration()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long first = Task.Run(() => service.RequestDrain(closeAdmission: false)).GetAwaiter().GetResult();
        service.TryGetDrainAcknowledgement(first, out _).ShouldBeFalse();
        service.HasUnresolvedWork.ShouldBeTrue();
        service.IsAdmissionOpen.ShouldBeTrue();

        service.Service();
        service.TryGetDrainAcknowledgement(first, out bool succeeded).ShouldBeTrue();
        succeeded.ShouldBeTrue();
        consumer.DrainCount.ShouldBe(1);

        // An older acknowledgement does not satisfy a newer request.
        long second = Task.Run(() => service.RequestDrain()).GetAwaiter().GetResult();
        second.ShouldBeGreaterThan(first);
        service.TryGetDrainAcknowledgement(second, out _).ShouldBeFalse();
        service.IsAdmissionOpen.ShouldBeFalse();
        service.Service();
        service.TryGetDrainAcknowledgement(second, out _).ShouldBeTrue();
    }

    [Test]
    public void DrainRequest_OnTheOwnerThreadDrainsAtOnce()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long generation = service.RequestDrain();
        service.WaitForDrainAcknowledgement(generation, TimeSpan.FromMilliseconds(10)).ShouldBeTrue();
        consumer.DrainCount.ShouldBe(1);
    }

    [Test]
    public void FailedDrain_IsReportedAndOwnerExitAcknowledgesPendingRequests()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true, DrainResult = false };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long generation = Task.Run(() => service.RequestDrain()).GetAwaiter().GetResult();
        service.DrainOnOwnerExit().ShouldBeFalse();
        service.TryGetDrainAcknowledgement(generation, out bool succeeded).ShouldBeTrue();
        succeeded.ShouldBeFalse();
        service.WaitForDrainAcknowledgement(generation, TimeSpan.FromMilliseconds(10)).ShouldBeFalse();
    }

    [Test]
    public void ScopedDrain_DrainsOnlyTheNamedRendererAndClosesOnlyItsAdmission()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        AbstractRenderer closing = CreateRenderer();
        AbstractRenderer surviving = CreateRenderer();
        var closingConsumer = new TestConsumer(closing) { Unresolved = true };
        var survivingConsumer = new TestConsumer(surviving) { Unresolved = true };
        service.TryRegister(closingConsumer).ShouldBeTrue();
        service.TryRegister(survivingConsumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long generation = Task.Run(() => service.RequestDrain(closing)).GetAwaiter().GetResult();
        service.Service();
        service.TryGetDrainAcknowledgement(generation, out bool succeeded).ShouldBeTrue();
        succeeded.ShouldBeTrue();

        closingConsumer.DrainCount.ShouldBe(1);
        survivingConsumer.DrainCount.ShouldBe(0);
        service.HasUnresolvedWorkFor(closing).ShouldBeFalse();
        service.HasUnresolvedWorkFor(surviving).ShouldBeTrue();
        service.IsAdmissionOpen.ShouldBeTrue();
        service.IsAdmissionOpenFor(closing).ShouldBeFalse();
        service.IsAdmissionOpenFor(surviving).ShouldBeTrue();

        service.Unregister(closingConsumer);
        service.TryRegister(new TestConsumer(closing)).ShouldBeFalse();
        service.TryRegister(new TestConsumer(surviving)).ShouldBeTrue();
    }

    [Test]
    public void ScopedDrains_ForDifferentRenderersMergeIntoOneFullDrain()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        AbstractRenderer first = CreateRenderer();
        AbstractRenderer second = CreateRenderer();
        var firstConsumer = new TestConsumer(first) { Unresolved = true };
        var secondConsumer = new TestConsumer(second) { Unresolved = true };
        service.TryRegister(firstConsumer).ShouldBeTrue();
        service.TryRegister(secondConsumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long firstGeneration = Task.Run(() => service.RequestDrain(first)).GetAwaiter().GetResult();
        long secondGeneration = Task.Run(() => service.RequestDrain(second)).GetAwaiter().GetResult();
        service.Service();

        service.TryGetDrainAcknowledgement(firstGeneration, out _).ShouldBeTrue();
        service.TryGetDrainAcknowledgement(secondGeneration, out _).ShouldBeTrue();
        firstConsumer.DrainCount.ShouldBe(1);
        secondConsumer.DrainCount.ShouldBe(1);
    }

    [Test]
    public void DrainRequestedInsideAConsumerCallback_RunsAfterTheCallbackReturns()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        long generation = 0;
        consumer.OnService = () =>
        {
            generation = service.RequestDrain();
            consumer.DrainCount.ShouldBe(0);
            service.WaitForDrainAcknowledgement(generation, TimeSpan.FromSeconds(1)).ShouldBeFalse();
        };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        service.Service();

        consumer.ServiceCount.ShouldBe(1);
        consumer.DrainCount.ShouldBe(1);
        service.TryGetDrainAcknowledgement(generation, out bool succeeded).ShouldBeTrue();
        succeeded.ShouldBeTrue();
    }

    [Test]
    public void DrainRequestedInsideAnOwnerOperation_RunsWhenTheOutermostOperationEnds()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        long generation;
        using (service.EnterOwnerOperation())
        {
            using (service.EnterOwnerOperation())
            {
                generation = service.RequestDrain();
                service.IsInsideOwnerOperation.ShouldBeTrue();
            }

            service.Service();
            consumer.DrainCount.ShouldBe(0);
            service.TryGetDrainAcknowledgement(generation, out _).ShouldBeFalse();
        }

        service.IsInsideOwnerOperation.ShouldBeFalse();
        consumer.DrainCount.ShouldBe(1);
        service.TryGetDrainAcknowledgement(generation, out bool succeeded).ShouldBeTrue();
        succeeded.ShouldBeTrue();
    }

    [Test]
    public void OwnerOperation_OnAnotherThreadDoesNothing()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);

        Task.Run(() =>
        {
            using (service.EnterOwnerOperation())
                service.IsInsideOwnerOperation.ShouldBeFalse();
        }).GetAwaiter().GetResult();
        service.IsInsideOwnerOperation.ShouldBeFalse();
    }

    [Test]
    public void Service_AllocatesNoManagedMemoryWhenWarm()
    {
        var service = new RuntimeAsynchronousRenderWorkService();
        var consumer = new TestConsumer { Unresolved = true };
        service.TryRegister(consumer).ShouldBeTrue();
        service.BindOwnerThread(Environment.CurrentManagedThreadId);
        service.Service();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 64; pass++)
        {
            service.Service();
            _ = service.HasUnresolvedWork;
            _ = service.NextDeadlineTicks;
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
    }

    private static readonly AbstractRenderer s_defaultRenderer = CreateRenderer();

    // The service uses the renderer only as an identity, so an uninitialized
    // instance is enough and creates no graphics context.
    private static AbstractRenderer CreateRenderer()
        => (AbstractRenderer)RuntimeHelpers.GetUninitializedObject(typeof(OpenGLRenderer));

    private sealed class TestConsumer(AbstractRenderer? renderer = null) : IRuntimeAsynchronousRenderWorkConsumer
    {
        public AbstractRenderer Renderer { get; } = renderer ?? s_defaultRenderer;
        public bool Unresolved { get; set; }
        public bool DrainResult { get; set; } = true;
        public int ServiceCount { get; private set; }
        public int DrainCount { get; private set; }

        public bool HasUnresolvedWork => Unresolved;
        public long NextDeadlineTicks => long.MaxValue;

        public Action? OnService { get; set; }

        public void ServiceAsynchronousWork()
        {
            ServiceCount++;
            OnService?.Invoke();
        }

        public bool DrainAsynchronousWork()
        {
            DrainCount++;
            if (DrainResult)
                Unresolved = false;
            return DrainResult;
        }
    }
}
