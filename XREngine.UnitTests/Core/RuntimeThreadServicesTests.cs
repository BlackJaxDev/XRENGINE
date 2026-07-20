using NUnit.Framework;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class RuntimeThreadServicesTests
{
    private static readonly object ServiceLock = new();

    [Test]
    public void DirectService_ExecutesFallbackAndImmediateActionsExactlyOnce()
    {
        lock (ServiceLock)
        {
            IRuntimeThreadServices previous = RuntimeThreadServices.Current;
            try
            {
                RuntimeThreadServices.Current = null!;

                int fallbackCalls = 0;
                bool fallbackEnqueued = RuntimeThreadServices.Current.InvokeOnAppThread(() => fallbackCalls++);
                if (!fallbackEnqueued)
                    fallbackCalls++;

                Assert.That(fallbackEnqueued, Is.False);
                Assert.That(fallbackCalls, Is.EqualTo(1));

                int immediateCalls = 0;
                bool immediateEnqueued = RuntimeThreadServices.Current.InvokeOnAppThread(
                    () => immediateCalls++,
                    executeNowIfAlreadyAppThread: true);

                Assert.That(immediateEnqueued, Is.False);
                Assert.That(immediateCalls, Is.EqualTo(1));

                int updateCalls = 0;
                RuntimeThreadServices.Current.EnqueueUpdateThread(() => updateCalls++);
                Assert.That(updateCalls, Is.EqualTo(1));

                int physicsCalls = 0;
                RuntimeThreadServices.Current.EnqueuePhysicsThread(() => physicsCalls++);
                Assert.That(physicsCalls, Is.EqualTo(1));
            }
            finally
            {
                RuntimeThreadServices.Current = previous;
            }
        }
    }
}