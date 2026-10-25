using System.Reflection;
using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class DetachedLeafLiveWorldRetirementTests
{
    [Test]
    public void RegisteredLiveLeaf_StopsBeforeWorldRemovalAndRetiresOnce()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        var transform = node.Transform;
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        LifecycleComponent component = node.AddComponentGuarded(() => new LifecycleComponent(), creation)!;
        node.OnActivated();
        node.OnBeginPlay();

        Assert.That(retirement.TryRetireFromWorld(world), Is.True, retirement.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(component.DeactivationCount, Is.EqualTo(1));
            Assert.That(component.EndPlayCount, Is.EqualTo(1));
            Assert.That(component.WorldAtDeactivation, Is.SameAs(world));
            Assert.That(component.WorldAtEndPlay, Is.SameAs(world));
            Assert.That(component.IsDestroyed, Is.True);
            Assert.That(node.IsDestroyed, Is.True);
            Assert.That(node.World, Is.Null);
            Assert.That(transform.World, Is.Null);
            Assert.That(retirement.TryRetireFromWorld(world), Is.True);
        });
    }

    [Test]
    public void WorldChangingThrow_RetainsWorldAndDoesNotReplayLifecycle()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        LifecycleComponent component = node.AddComponentGuarded(() => new LifecycleComponent(), creation)!;
        node.OnActivated();
        XRPropertyChangingEventHandler observer = (_, args) =>
        {
            if (args.PropertyName == nameof(SceneNode.World))
                throw new InvalidOperationException("Observer rejected removal.");
        };
        node.PropertyChanging += observer;
        try
        {
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(component.DeactivationCount, Is.EqualTo(1));
                Assert.That(node.World, Is.SameAs(world));
                Assert.That(component.World, Is.SameAs(world));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally
        {
            node.PropertyChanging -= observer;
            node.Destroy(true);
        }
    }

    [Test]
    public void WorldVeto_RetainsSourceAndDoesNotReplayLifecycle()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        LifecycleComponent component = node.AddComponentGuarded(() => new LifecycleComponent(), creation)!;
        node.OnActivated();
        int vetoCalls = 0;
        XRPropertyChangingEventHandler veto = (_, args) =>
        {
            if (args.PropertyName == nameof(SceneNode.World))
            {
                ((XRPropertyChangingEventArgs<IRuntimeWorldContext?>)args).AllowChange = false;
                vetoCalls++;
            }
        };
        node.PropertyChanging += veto;
        try
        {
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(vetoCalls, Is.EqualTo(1));
                Assert.That(component.DeactivationCount, Is.EqualTo(1));
                Assert.That(node.World, Is.SameAs(world));
                Assert.That(node.Transform.World, Is.SameAs(world));
                Assert.That(component.World, Is.SameAs(world));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally
        {
            node.PropertyChanging -= veto;
            node.Destroy(true);
        }
    }

    [Test]
    public void LifecycleStopThrow_DoesNotReplaySideEffects()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        ThrowingStopComponent component = node.AddComponentGuarded(() => new ThrowingStopComponent(), creation)!;
        node.OnActivated();
        try
        {
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(component.StopCalls, Is.EqualTo(1));
                Assert.That(component.WorldAtStop, Is.SameAs(world));
                Assert.That(node.World, Is.SameAs(world));
                Assert.That(component.World, Is.SameAs(world));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void LiveReceiptRejectsNullWorldRetirementAndForeignSource()
    {
        TestWorldContext source = new();
        TestWorldContext foreign = new();
        SceneNode node = new(source, "Leaf");
        try
        {
            DetachedLeafRetirementReceipt receipt = new(node, source);
            Assert.Throws<InvalidOperationException>(() => new DetachedLeafRetirementReceipt(node));
            Assert.Throws<InvalidOperationException>(() => receipt.TryRetire());
            Assert.Throws<InvalidOperationException>(() => receipt.TryRetireFromWorld(foreign));
            Assert.That(node.IsDestroyed, Is.False);
            Assert.That(receipt.TryRetireFromWorld(source), Is.True, receipt.Failure?.ToString());
        }
        finally { if (!node.IsDestroyed) node.Destroy(true); }
    }

    [Test]
    public void RuntimeWorldStore_DetachesExactTransformHandle()
    {
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        SceneNode node = new(world, "Leaf");
        var transform = node.Transform;
        var handle = transform.HierarchyHandle;
        DetachedLeafRetirementReceipt receipt = new(node, world);

        Assert.That(transform.HierarchyStore, Is.SameAs(world.TransformHierarchy));
        Assert.That(world.TransformHierarchy.IsAlive(handle), Is.True);
        Assert.That(receipt.TryRetireFromWorld(world), Is.True, receipt.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(world.TransformHierarchy.IsAlive(handle), Is.False);
            Assert.That(transform.HierarchyStore, Is.Null);
            Assert.That(transform.HierarchyHandle, Is.EqualTo(default(XREngine.Scene.Transforms.TransformHandle)));
        });
    }

    [Test]
    public void WorldChangedThrow_RetainsCommittedRemovalWithoutRetry()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        LifecycleComponent component = node.AddComponentGuarded(() => new LifecycleComponent(), creation)!;
        int observerCalls = 0;
        XRPropertyChangedEventHandler observer = (_, args) =>
        {
            if (args.PropertyName == nameof(SceneNode.World))
            {
                observerCalls++;
                throw new InvalidOperationException("Observer failed after write.");
            }
        };
        node.PropertyChanged += observer;
        try
        {
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.That(retirement.TryRetireFromWorld(world), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(observerCalls, Is.EqualTo(1));
                Assert.That(node.World, Is.Null);
                Assert.That(node.Transform.World, Is.SameAs(world));
                Assert.That(component.World, Is.SameAs(world));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally
        {
            node.PropertyChanged -= observer;
            node.Destroy(true);
        }
    }

    [Test]
    public void FailedUnboundComponent_RemainsInLiveLeafReceipt()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt retirement = new(node, world);
        RuntimeComponentCreationReceipt creation = new(node);
        retirement.Register(creation);
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<FailingComponent>(
            static () => new FailingComponent(), creation));
        Assert.That(creation.Component!.World, Is.Null);

        Assert.That(retirement.TryRetireFromWorld(world), Is.True, retirement.Failure?.ToString());
        Assert.That(creation.Component.IsDestroyed, Is.True);
    }

    [Test]
    public void TransformObserverThrow_RetainsConfirmedStoreDetach()
    {
        using RuntimeWorld world = new(new HierarchyTestPhysicsScene());
        SceneNode node = new(world, "Leaf");
        var transform = node.Transform;
        var handle = transform.HierarchyHandle;
        DetachedLeafRetirementReceipt receipt = new(node, world);
        int observerCalls = 0;
        XRPropertyChangedEventHandler observer = (_, args) =>
        {
            if (args.PropertyName == nameof(transform.World))
            {
                observerCalls++;
                throw new InvalidOperationException("Store observer failed.");
            }
        };
        transform.PropertyChanged += observer;
        try
        {
            Assert.That(receipt.TryRetireFromWorld(world), Is.False);
            Assert.That(receipt.TryRetireFromWorld(world), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(observerCalls, Is.EqualTo(1));
                Assert.That(node.World, Is.Null);
                Assert.That(transform.World, Is.Null);
                Assert.That(transform.HierarchyStore, Is.Null);
                Assert.That(world.TransformHierarchy.IsAlive(handle), Is.False);
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally
        {
            transform.PropertyChanged -= observer;
            node.Destroy(true);
        }
    }

    [Test]
    public void SealedCloneAssignedWorld_RejectsDetachedRetirementBeforeCallbacks()
    {
        var result = XRObjectBase.ConstructOwned(_ => new SceneNode("Clone"));
        SceneNode node = result.Value!;
        TestWorldContext world = new();
        typeof(XRWorldObjectBase).GetProperty(nameof(XRWorldObjectBase.World),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(node, world);
        int destructionCalls = 0;
        node.Destroying += _ => { destructionCalls++; return true; };
        try
        {
            OwnedHierarchyLifetime lifetime = new(result.Ownership);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(destructionCalls, Is.Zero);
                Assert.That(node.World, Is.SameAs(world));
                Assert.That(node.IsDestroyed, Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CaughtNestedWorldWrite_DoesNotConsumeOuterPermit(bool afterCommit)
    {
        TestWorldContext world = new();
        TestWorldContext foreign = new();
        SceneNode node = new(world, "Leaf");
        DetachedLeafRetirementReceipt receipt = new(node, world);
        PropertyInfo property = typeof(XRWorldObjectBase).GetProperty(nameof(XRWorldObjectBase.World),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        int rejected = 0;
        void RejectNested()
        {
            TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(
                () => property.SetValue(node, afterCommit ? foreign : null));
            Assert.That(failure!.InnerException, Is.TypeOf<InvalidOperationException>());
            rejected++;
        }
        XRPropertyChangingEventHandler changing = (_, args) =>
        {
            if (!afterCommit && args.PropertyName == nameof(SceneNode.World)) RejectNested();
        };
        XRPropertyChangedEventHandler changed = (_, args) =>
        {
            if (afterCommit && args.PropertyName == nameof(SceneNode.World)) RejectNested();
        };
        node.PropertyChanging += changing;
        node.PropertyChanged += changed;
        try
        {
            Assert.That(receipt.TryRetireFromWorld(world), Is.True, receipt.Failure?.ToString());
            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(node.IsDestroyed, Is.True);
        }
        finally
        {
            node.PropertyChanging -= changing;
            node.PropertyChanged -= changed;
            if (!node.IsDestroyed) node.Destroy(true);
        }
    }

    private sealed class FailingComponent : XRComponent
    {
        public FailingComponent() => throw new InvalidOperationException("Construction failed.");
    }

    private sealed class LifecycleComponent : XRComponent
    {
        public int DeactivationCount { get; private set; }
        public int EndPlayCount { get; private set; }
        public IRuntimeWorldContext? WorldAtDeactivation { get; private set; }
        public IRuntimeWorldContext? WorldAtEndPlay { get; private set; }

        protected override void OnComponentDeactivated()
        {
            DeactivationCount++;
            WorldAtDeactivation = World;
            base.OnComponentDeactivated();
        }

        protected override void OnEndPlay()
        {
            EndPlayCount++;
            WorldAtEndPlay = World;
            base.OnEndPlay();
        }
    }

    private sealed class ThrowingStopComponent : XRComponent
    {
        private bool _throwOnce = true;
        public int StopCalls { get; private set; }
        public IRuntimeWorldContext? WorldAtStop { get; private set; }

        protected override void OnComponentDeactivated()
        {
            StopCalls++;
            WorldAtStop = World;
            if (_throwOnce)
            {
                _throwOnce = false;
                throw new InvalidOperationException("Stop failed after its side effect.");
            }
            base.OnComponentDeactivated();
        }
    }
}
