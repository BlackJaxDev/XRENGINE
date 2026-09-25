using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Listener mutations are deferred to the next invocation. A removal that arrives while its
/// addition is still pending must cancel that addition so rarely invoked events cannot retain
/// listener targets indefinitely.
/// </summary>
[TestFixture]
public sealed class XREventPendingListenerTests
{
    [Test]
    public void RemoveBeforeInvoke_CancelsPendingAdd()
    {
        XREvent<int> xrEvent = new();
        int calls = 0;
        Action<int> listener = _ => calls++;

        xrEvent.AddListener(listener);
        xrEvent.RemoveListener(listener);

        xrEvent.HasPendingAdds.ShouldBeFalse();
        xrEvent.HasPendingRemoves.ShouldBeFalse();
        xrEvent.Invoke(1);
        calls.ShouldBe(0);
        xrEvent.Count.ShouldBe(0);
    }

    [Test]
    public void SubtractOperator_CollapsesEventWhenOnlyAPendingAddIsCanceled()
    {
        XREvent<int>? xrEvent = null;
        Action<int> listener = _ => { };

        xrEvent += listener;
        xrEvent -= listener;

        xrEvent.ShouldBeNull();
    }

    [Test]
    public void RemoveBeforeInvoke_ReleasesListenerTarget()
    {
        XREvent<int> xrEvent = new();
        WeakReference target = AddAndRemoveCapturingListener(xrEvent);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        target.IsAlive.ShouldBeFalse();
    }

    [Test]
    public void DuplicatePendingAdds_SingleRemove_KeepsOneListener()
    {
        XREvent<int> xrEvent = new();
        int calls = 0;
        Action<int> listener = _ => calls++;

        xrEvent.AddListener(listener);
        xrEvent.AddListener(listener);
        xrEvent.RemoveListener(listener);
        xrEvent.Invoke(1);

        calls.ShouldBe(1);
        xrEvent.Count.ShouldBe(1);
    }

    [Test]
    public void RemoveAfterAppliedAdd_TakesEffectOnNextInvoke()
    {
        XREvent<int> xrEvent = new();
        int calls = 0;
        Action<int> listener = _ => calls++;

        xrEvent.AddListener(listener);
        xrEvent.Invoke(1);
        xrEvent.RemoveListener(listener);
        xrEvent.HasPendingRemoves.ShouldBeTrue();
        xrEvent.Invoke(2);

        calls.ShouldBe(1);
        xrEvent.Count.ShouldBe(0);
    }

    [Test]
    public void ListenerRemovingItselfDuringInvoke_RunsOnceThenStops()
    {
        XREvent<int> xrEvent = new();
        int calls = 0;
        Action<int>? listener = null;
        listener = _ =>
        {
            calls++;
            xrEvent.RemoveListener(listener!);
        };

        xrEvent.AddListener(listener);
        xrEvent.Invoke(1);
        xrEvent.Invoke(2);

        calls.ShouldBe(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemoveCapturingListener(XREvent<int> xrEvent)
    {
        ListenerTarget target = new();
        Action<int> listener = target.OnValue;
        xrEvent.AddListener(listener);
        xrEvent.RemoveListener(listener);
        return new WeakReference(target);
    }

    private sealed class ListenerTarget
    {
        public int Last;

        public void OnValue(int value)
            => Last = value;
    }
}
