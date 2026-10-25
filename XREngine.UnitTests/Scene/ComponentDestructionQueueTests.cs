using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class ComponentDestructionQueueTests
{
    [SetUp]
    public void SetUp() => XRObjectBase.ProcessPendingDestructions();

    [Test]
    public void GenerateCancelsAnOlderQueuedDestruction()
    {
        QueueObjectProbe value = new();
        try
        {
            value.Destroy();
            Assert.That(value.IsDestroyQueued, Is.True);
            value.Generate();

            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(value.IsDestroyQueued, Is.False);
                Assert.That(value.DestroyNowCalls, Is.Zero);
                Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            });
        }
        finally
        {
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void RegeneratedObjectRunsOnlyItsNewQueuedDestruction()
    {
        QueueObjectProbe value = new();
        try
        {
            value.Destroy();
            value.Generate();
            value.Destroy();

            Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(1));
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(value.IsDestroyed, Is.True);
                Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
                Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            });
        }
        finally
        {
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void DuplicateQueueRequestHasOnePendingDispatch()
    {
        QueueObjectProbe value = new();
        value.Destroy();
        value.Destroy();

        Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(1));
        XRObjectBase.ProcessPendingDestructions();

        Assert.Multiple(() =>
        {
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VirtualOverrideCanLeaveItsExactRequestPending(bool callBaseToDefer)
    {
        QueueObjectProbe value = new()
        {
            ReturnBeforeBase = !callBaseToDefer,
            DeferFromOverride = callBaseToDefer,
        };
        try
        {
            value.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(value.IsDestroyQueued, Is.True);
                Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
                Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(1));
            });

            value.ReturnBeforeBase = false;
            value.DeferFromOverride = false;
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.DestroyNowCalls, Is.EqualTo(2));
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        }
        finally
        {
            value.ReturnBeforeBase = false;
            value.DeferFromOverride = false;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ThrowingVirtualOverrideDoesNotRetryItsPartialCleanup()
    {
        QueueObjectProbe value = new() { ThrowBeforeBase = true };
        try
        {
            value.Destroy();
            Assert.Throws<InvalidOperationException>(XRObjectBase.ProcessPendingDestructions);
            Assert.That(value.IsDestroyed, Is.False);
            Assert.That(value.IsDestroyQueued, Is.False);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);

            value.ThrowBeforeBase = false;
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
        }
        finally
        {
            value.ThrowBeforeBase = false;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void DispatchReleaseFailureKeepsAnUnconsumedRequest()
    {
        QueueObjectProbe value = new()
        {
            ReturnBeforeBase = true,
            ThrowOnDispatchRelease = true,
        };
        try
        {
            value.Destroy();
            Assert.Throws<InvalidOperationException>(XRObjectBase.ProcessPendingDestructions);
            Assert.That(value.IsDestroyQueued, Is.True);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(1));

            value.ThrowOnDispatchRelease = false;
            value.ReturnBeforeBase = false;
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.DestroyNowCalls, Is.EqualTo(2));
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        }
        finally
        {
            value.ThrowOnDispatchRelease = false;
            value.ReturnBeforeBase = false;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void VirtualFailureWinsOverDispatchReleaseFailure()
    {
        QueueObjectProbe value = new()
        {
            ThrowBeforeBase = true,
            ThrowOnDispatchRelease = true,
        };
        try
        {
            value.Destroy();
            InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(
                XRObjectBase.ProcessPendingDestructions);
            Assert.That(failure!.Message, Is.EqualTo("Expected pre-base destruction failure."));
            Assert.That(value.IsDestroyQueued, Is.False);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);

            value.ThrowBeforeBase = false;
            value.ThrowOnDispatchRelease = false;
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
        }
        finally
        {
            value.ThrowBeforeBase = false;
            value.ThrowOnDispatchRelease = false;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void RunningGenerateDefersQueuedDispatchUntilItsWriterExits()
    {
        QueueObjectProbe value = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        value.GenerateEntered = entered;
        value.ReleaseGenerate = release;
        Task? generation = null;
        try
        {
            value.Destroy();
            generation = Task.Run(value.Generate);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.DestroyNowCalls, Is.Zero);
            Assert.That(value.IsDestroyQueued, Is.True);

            release.Set();
            generation.GetAwaiter().GetResult();
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.IsDestroyed, Is.False);
            Assert.That(value.IsDestroyQueued, Is.False);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        }
        finally
        {
            release.Set();
            generation?.GetAwaiter().GetResult();
            value.GenerateEntered = null;
            value.ReleaseGenerate = null;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ClaimedDispatchRejectsGenerateBeforeItsVirtualOverrideReturns()
    {
        QueueObjectProbe value = new();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        value.DestroyEntered = entered;
        value.ReleaseDestroy = release;
        Task? dispatch = null;
        try
        {
            value.Destroy();
            Guid queuedId = value.ID;
            dispatch = Task.Run(XRObjectBase.ProcessPendingDestructions);
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);

            Assert.Throws<InvalidOperationException>(value.Generate);
            Assert.That(value.ID, Is.EqualTo(queuedId));
            Assert.That(value.IsDestroyQueued, Is.True);

            release.Set();
            dispatch.GetAwaiter().GetResult();
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        }
        finally
        {
            release.Set();
            dispatch?.GetAwaiter().GetResult();
            value.DestroyEntered = null;
            value.ReleaseDestroy = null;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void RecursiveDrainReturnsAndNewChildRequestRunsInTheOuterDrain()
    {
        QueueObjectProbe owner = new() { DrainInsideDestroy = true };
        QueueObjectProbe child = new();
        owner.DestroyingAction = () => child.Destroy();
        try
        {
            owner.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(owner.IsDestroyed, Is.True);
                Assert.That(child.IsDestroyed, Is.True);
                Assert.That(child.DestroyNowCalls, Is.EqualTo(1));
                Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            });
        }
        finally
        {
            if (!owner.IsDestroyed) owner.Destroy(true);
            if (!child.IsDestroyed) child.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CallbackFailureAfterCanonicalEntryHasNoAutomaticRetry(bool afterCommit)
    {
        QueueObjectProbe value = new();
        Action<XRObjectBase> destroyed = _ => throw new InvalidOperationException("Expected Destroyed failure.");
        if (afterCommit) value.Destroyed += destroyed;
        else value.DestroyingAction = () => throw new InvalidOperationException("Expected OnDestroying failure.");
        try
        {
            value.Destroy();
            Assert.Throws<InvalidOperationException>(XRObjectBase.ProcessPendingDestructions);
            Assert.That(value.IsDestroyed, Is.EqualTo(afterCommit));
            Assert.That(value.IsDestroyQueued, Is.False);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
        }
        finally
        {
            value.Destroyed -= destroyed;
            value.DestroyingAction = null;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void DirectDestroyAndRegenerateCannotReuseAnOlderRequest()
    {
        QueueObjectProbe value = new();
        try
        {
            value.Destroy();
            value.Destroy(true);
            value.Generate();
            value.Destroy();

            XRObjectBase.ProcessPendingDestructions();
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.DestroyNowCalls, Is.EqualTo(2));
            Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
        }
        finally
        {
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void DirectDestroyCallbackCannotLeaveARequestForAnAlreadyDestroyedObject()
    {
        QueueObjectProbe value = new();
        value.DestroyingAction = () => value.Destroy();
        try
        {
            value.Destroy(true);
            Assert.That(value.IsDestroyed, Is.True);
            Assert.That(value.IsDestroyQueued, Is.True);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(1));

            XRObjectBase.ProcessPendingDestructions();
            Assert.Multiple(() =>
            {
                Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
                Assert.That(value.IsDestroyQueued, Is.False);
                Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            });
        }
        finally
        {
            value.DestroyingAction = null;
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void VetoConsumesTheQueuedRequestWithoutDestroyingTheObject()
    {
        QueueObjectProbe value = new();
        Func<XRObjectBase, bool> veto = _ => false;
        value.Destroying += veto;
        try
        {
            value.Destroy();
            XRObjectBase.ProcessPendingDestructions();

            Assert.Multiple(() =>
            {
                Assert.That(value.IsDestroyed, Is.False);
                Assert.That(value.IsDestroyQueued, Is.False);
                Assert.That(value.DestroyNowCalls, Is.EqualTo(1));
                Assert.That(XRObjectBase.PendingDestructionCount, Is.Zero);
            });
        }
        finally
        {
            value.Destroying -= veto;
            if (!value.IsDestroyed) value.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void QueuedComponentCannotEnterAnotherComponentList()
    {
        SceneNode source = new("Source");
        SceneNode foreign = new("Foreign");
        QueueComponentProbe component = source.AddComponent(static () => new QueueComponentProbe())!;
        try
        {
            Assert.That(source.DetachComponent(component), Is.True);
            component.Destroy();

            Assert.Throws<InvalidOperationException>(() => foreign.ReattachComponent(component));
            Assert.Multiple(() =>
            {
                Assert.That(source.Components, Does.Not.Contain(component));
                Assert.That(foreign.Components, Does.Not.Contain(component));
                Assert.That(component.IsDestroyQueued, Is.True);
            });
        }
        finally
        {
            XRObjectBase.ProcessPendingDestructions();
            source.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ComponentCannotReattachFromItsDestroyingCallback()
    {
        SceneNode source = new("Source");
        SceneNode foreign = new("Foreign");
        QueueComponentProbe component = source.AddComponent(static () => new QueueComponentProbe())!;
        Exception? attemptFailure = null;
        Func<XRObjectBase, bool> callback = _ =>
        {
            try { foreign.ReattachComponent(component); }
            catch (Exception exception) { attemptFailure = exception; }
            return true;
        };
        try
        {
            Assert.That(source.DetachComponent(component), Is.True);
            component.Destroying += callback;
            component.Destroy(true);

            Assert.Multiple(() =>
            {
                Assert.That(attemptFailure, Is.TypeOf<InvalidOperationException>());
                Assert.That(component.IsDestroyed, Is.True);
                Assert.That(foreign.Components, Does.Not.Contain(component));
            });
        }
        finally
        {
            component.Destroying -= callback;
            source.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void DestroyedComponentCannotReattach()
    {
        SceneNode source = new("Source");
        SceneNode foreign = new("Foreign");
        QueueComponentProbe component = source.AddComponent(static () => new QueueComponentProbe())!;
        try
        {
            Assert.That(source.DetachComponent(component), Is.True);
            component.Destroy(true);

            Assert.Throws<InvalidOperationException>(() => foreign.ReattachComponent(component));
            Assert.That(foreign.Components, Does.Not.Contain(component));
        }
        finally
        {
            source.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PreAddCallbackCannotCommitAComponentThatStartsDestruction(bool destroyNow)
    {
        SceneNode node = new("Owner");
        QueueComponentProbe component = node.AddComponent(static () => new QueueComponentProbe())!;
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        EventList<XRComponent>.SingleCancelableHandler callback = _ =>
        {
            component.Destroy(destroyNow);
            return true;
        };
        try
        {
            Assert.That(node.DetachComponent(component), Is.True);
            list.PreAdded += callback;

            Assert.Throws<InvalidOperationException>(() => node.ReattachComponent(component));
            Assert.That(list, Does.Not.Contain(component));
        }
        finally
        {
            list.PreAdded -= callback;
            XRObjectBase.ProcessPendingDestructions();
            node.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void VetoedComponentDestructionAllowsLaterReattachment()
    {
        SceneNode node = new("Owner");
        QueueComponentProbe component = node.AddComponent(static () => new QueueComponentProbe())!;
        Func<XRObjectBase, bool> veto = _ => false;
        try
        {
            Assert.That(node.DetachComponent(component), Is.True);
            component.Destroying += veto;
            component.Destroy();
            XRObjectBase.ProcessPendingDestructions();
            Assert.That(component.IsDestroyQueued, Is.False);

            node.ReattachComponent(component);
            Assert.That(node.Components, Does.Contain(component));
            Assert.That(component.IsDestroyed, Is.False);
        }
        finally
        {
            component.Destroying -= veto;
            node.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void PreAddRangeCallbackCannotCommitAQueuedComponent()
    {
        SceneNode node = new("Owner");
        QueueComponentProbe component = node.AddComponent(static () => new QueueComponentProbe())!;
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        EventList<XRComponent>.MultiCancelableHandler callback = _ =>
        {
            component.Destroy();
            return true;
        };
        try
        {
            Assert.That(node.DetachComponent(component), Is.True);
            list.PreAddedRange += callback;

            Assert.Throws<InvalidOperationException>(() => list.AddRange([component]));
            Assert.That(list, Does.Not.Contain(component));
        }
        finally
        {
            list.PreAddedRange -= callback;
            XRObjectBase.ProcessPendingDestructions();
            node.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ComponentCanDetachAndReattachBeforeItIsQueued()
    {
        SceneNode node = new("Owner");
        QueueComponentProbe component = node.AddComponent(static () => new QueueComponentProbe())!;
        try
        {
            Assert.That(node.DetachComponent(component), Is.True);
            node.ReattachComponent(component);

            Assert.Multiple(() =>
            {
                Assert.That(node.Components, Does.Contain(component));
                Assert.That(component.IsDestroyed, Is.False);
                Assert.That(component.IsDestroyQueued, Is.False);
            });
        }
        finally
        {
            node.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void NodeRemovalCallbackCannotMoveItsQueuedComponent()
    {
        SceneNode source = new("Source");
        SceneNode foreign = new("Foreign");
        QueueComponentProbe component = source.AddComponent(static () => new QueueComponentProbe())!;
        EventList<XRComponent> list = (EventList<XRComponent>)source.Components;
        Exception? attemptFailure = null;
        EventList<XRComponent>.MultiHandler callback = _ =>
        {
            try { foreign.ReattachComponent(component); }
            catch (Exception exception) { attemptFailure = exception; }
        };
        list.PostRemovedRange += callback;
        try
        {
            source.Destroy(true);

            Assert.Multiple(() =>
            {
                Assert.That(attemptFailure, Is.TypeOf<InvalidOperationException>());
                Assert.That(foreign.Components, Does.Not.Contain(component));
                Assert.That(component.IsDestroyQueued, Is.True);
            });
        }
        finally
        {
            list.PostRemovedRange -= callback;
            XRObjectBase.ProcessPendingDestructions();
            if (!source.IsDestroyed) source.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

}
