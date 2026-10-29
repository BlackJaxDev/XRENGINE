using NUnit.Framework;
using XREngine.Data;
using XREngine.Execution;

namespace XREngine.UnitTests.Core;

[TestFixture, NonParallelizable]
public sealed class RuntimeBlockingTaskTests
{
    [OneTimeSetUp]
    public void RegisterNativeBackend() => ThreadedWorkerBackend.EnsureRegistered();

    [Test]
    public void CompletedOperationRunsOnceAndReturnsItsValue()
    {
        int calls = 0;
        int result = RuntimeBlockingTaskServices.Run(() =>
        {
            calls++;
            return Task.FromResult(42);
        });
        Assert.That(result, Is.EqualTo(42));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void NativeBackendCompletesAsynchronousOperation()
    {
        int result = RuntimeBlockingTaskServices.Run(async () =>
        {
            await Task.Yield();
            return 37;
        });
        Assert.That(result, Is.EqualTo(37));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OperationFailurePreservesOriginalException(bool beforeTask)
    {
        InvalidDataException failure = new("expected operation failure");
        Exception? actual = Assert.Throws<InvalidDataException>(() => RuntimeBlockingTaskServices.Run<int>(
            () => beforeTask ? throw failure : Task.FromException<int>(failure)));
        Assert.That(actual, Is.SameAs(failure));
    }

    [Test]
    public void CancellationPreservesToken()
    {
        using CancellationTokenSource lifetime = new();
        lifetime.Cancel();
        OperationCanceledException? failure = Assert.Catch<OperationCanceledException>(() =>
            RuntimeBlockingTaskServices.Run(() => Task.FromCanceled<int>(lifetime.Token)));
        Assert.That(failure!.CancellationToken, Is.EqualTo(lifetime.Token));
    }

    [Test]
    public void CallerAdmissionRejectsBeforeStartingEvenCompletedWork()
    {
        bool previous = RuntimeAssetReadServices.IsCallerThread;
        int calls = 0;
        try
        {
            RuntimeAssetReadServices.IsCallerThread = true;
            Assert.Throws<NotSupportedException>(() => RuntimeBlockingTaskServices.Run(() =>
            {
                calls++;
                return Task.FromResult(42);
            }));
            Assert.That(calls, Is.Zero);
        }
        finally
        {
            RuntimeAssetReadServices.IsCallerThread = previous;
        }
    }

    [Test]
    public void NullOperationIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => RuntimeBlockingTaskServices.Run<int>(null!));
}
