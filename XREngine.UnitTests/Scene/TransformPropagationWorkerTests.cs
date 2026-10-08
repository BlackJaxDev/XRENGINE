using NUnit.Framework;
using Shouldly;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class TransformPropagationWorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkerBarrierTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public void RepeatedBatches_ExecuteEachRangeExactlyOnce()
    {
        RunWithDeadline(() =>
        {
            using var store = new TransformHierarchyStore();
            int[] visits = [];
            int total = 0;
            using ITransformPropagationWorkerPool pool = TransformPropagationWorkerServices
                .GetRequiredFactory().Create(store, index =>
                {
                    Interlocked.Increment(ref visits[index]);
                    Interlocked.Increment(ref total);
                });

            foreach (int count in new[] { 0, 1, 17, 64, 17, 0, 3 })
            {
                visits = new int[count];
                total = 0;

                pool.Run(count);

                total.ShouldBe(count);
                foreach (int visitCount in visits)
                    visitCount.ShouldBe(1);
            }
        });
    }

    [Test]
    public void CallbackFailure_AllowsNextBatchOnSamePool()
    {
        RunWithDeadline(() =>
        {
            using var store = new TransformHierarchyStore();
            int failOnce = 1;
            int[] visits = new int[32];
            using ITransformPropagationWorkerPool pool = TransformPropagationWorkerServices
                .GetRequiredFactory().Create(store, index =>
                {
                    if (index == 0 && Interlocked.Exchange(ref failOnce, 0) == 1)
                        throw new InvalidOperationException("Transform range failure.");
                    Interlocked.Increment(ref visits[index]);
                });

            Should.Throw<InvalidOperationException>(() => pool.Run(visits.Length))
                .Message.ShouldBe("Transform range failure.");

            visits = new int[47];
            pool.Run(visits.Length);

            foreach (int visitCount in visits)
                visitCount.ShouldBe(1);
        });
    }

    [Test]
    public void Dispose_JoinsEveryParticipatingWorker()
    {
        RunWithDeadline(() =>
        {
            int workerCount = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
            using var store = new TransformHierarchyStore();
            using var entered = new CountdownEvent(workerCount);
            (Thread? Thread, bool IsBackground)[] participants = new (Thread?, bool)[workerCount];
            ITransformPropagationWorkerPool pool = TransformPropagationWorkerServices
                .GetRequiredFactory().Create(store, index =>
                {
                    Thread participant = Thread.CurrentThread;
                    participants[index] = (participant, participant.IsBackground);
                    entered.Signal();
                    if (!entered.Wait(WorkerBarrierTimeout))
                        throw new TimeoutException("Transform workers did not enter the batch.");
                });

            try
            {
                pool.Run(workerCount);
            }
            finally
            {
                pool.Dispose();
            }

            entered.CurrentCount.ShouldBe(0);
            var uniqueParticipants = new HashSet<Thread>();
            foreach (var captured in participants)
            {
                Thread participant = captured.Thread ?? throw new AssertionException(
                    "A transform worker did not enter the batch.");
                uniqueParticipants.Add(participant);
                captured.IsBackground.ShouldBeTrue();
                participant.IsAlive.ShouldBeFalse();
            }
            uniqueParticipants.Count.ShouldBe(workerCount);
        });
    }

    private static void RunWithDeadline(Action test)
        => Task.Run(test).WaitAsync(TestTimeout).GetAwaiter().GetResult();
}
