using NUnit.Framework;
using XREngine;
using XREngine.Components;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class DetachedLeafRetirementTests
{
    [Test]
    public void EmptyDetachedLeaf_RetiresCanonicalNodeAndStorage()
    {
        SceneNode node = new("Leaf");
        var transform = node.Transform;
        var components = (EventList<XRComponent>)node.Components;
        var children = node.Transform.Children;
        DetachedLeafRetirementReceipt receipt = new(node);

        Assert.That(receipt.TryRetire(), Is.True, receipt.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(receipt.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
            Assert.That(node.IsDestroyed, Is.True);
            Assert.That(transform.IsDestroyed, Is.True);
            Assert.That(components.IsDestroyed, Is.False);
            Assert.That(children.IsDestroyed, Is.False);
            Assert.That(components.Count, Is.Zero);
            Assert.That(children.Count, Is.Zero);
            Assert.That(((IHierarchyListAuthority)components.GetMutationAuthority()!).IsCurrent, Is.False);
            Assert.That(((IHierarchyListAuthority)children.GetMutationAuthority()!).IsCurrent, Is.False);
            Assert.That(receipt.TryRetire(), Is.True);
        });
        Assert.Throws<InvalidOperationException>(components.Clear);
        Assert.Throws<InvalidOperationException>(children.Clear);
    }

    [Test]
    public void RegisteredGuardedComponent_RetiresOwnedResource()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        Resource resource = new();
        ResourceComponent component = node.AddComponentGuarded(() => new ResourceComponent(resource), creation)!;

        Assert.That(receipt.TryRetire(), Is.True, receipt.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(component.IsDestroyed, Is.True);
            Assert.That(resource.ReleaseCount, Is.EqualTo(1));
            Assert.That(node.IsDestroyed, Is.True);
        });
    }

    [Test]
    public void ConfirmedGuardedAbort_IsOmittedFromLeafCleanup()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        Resource resource = new();
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<ResourceComponent>(() =>
        {
            _ = new ResourceComponent(resource);
            throw new InvalidOperationException("Factory failed.");
        }, creation));
        Assert.That(creation.TryAbortUnchangedComponent(), Is.True);
        Assert.That(receipt.TryRetire(), Is.True, receipt.Failure?.ToString());
        Assert.That(resource.ReleaseCount, Is.EqualTo(1));
    }

    [Test]
    public void IncompleteGuardedAbort_RejectsLeafCleanupWithoutReplay()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        ThrowingResourceComponent? component = null;
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<ThrowingResourceComponent>(() =>
        {
            component = new ThrowingResourceComponent();
            throw new InvalidOperationException("Factory failed.");
        }, creation));
        try
        {
            Assert.That(creation.TryAbortUnchangedComponent(), Is.False);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(component!.CleanupCalls, Is.EqualTo(1));
        }
        finally
        {
            component?.Destroy(true);
            node.Destroy(true);
        }
    }

    [Test]
    public void RegisterAfterFactoryStarts_RejectsLateOwnership()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        try
        {
            ResourceComponent component = node.AddComponentGuarded(
                () => new ResourceComponent(new Resource()), creation)!;
            Assert.Throws<InvalidOperationException>(() => receipt.Register(creation));
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(component.IsDestroyed, Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void UnusedAttemptCannotStartFactoryDuringRetirement()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt unused = new(node);
        RuntimeComponentCreationReceipt orphanCreation = new(node);
        receipt.Register(orphanCreation);
        receipt.Register(unused);
        bool factoryRan = false;
        Exception? blocked = null;
        ResourceComponent? orphan = null;
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<ResourceComponent>(() =>
        {
            orphan = new ResourceComponent(new Resource());
            throw new InvalidOperationException("Factory failed.");
        }, orphanCreation));
        orphan!.Destroying += _ =>
        {
            try
            {
                node.AddComponentGuarded(() =>
                {
                    factoryRan = true;
                    return new ResourceComponent(new Resource());
                }, unused);
            }
            catch (Exception failure) { blocked = failure; }
            return true;
        };

        Assert.That(receipt.TryRetire(), Is.True, receipt.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(blocked, Is.TypeOf<InvalidOperationException>());
            Assert.That(factoryRan, Is.False);
            Assert.That(unused.Component, Is.Null);
            Assert.That(orphan.IsDestroyed, Is.True);
        });
    }

    [Test]
    public void ParentAttachmentThenDetachment_InvalidatesInitialLeafReceipt()
    {
        SceneNode parent = new("Parent");
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        node.Parent = parent;
        node.Parent = null;
        try
        {
            Assert.That(node.Transform.HasEnteredStructuralList, Is.True);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(node.IsDestroyed, Is.False);
            Assert.Throws<InvalidOperationException>(() => new DetachedLeafRetirementReceipt(node));
        }
        finally
        {
            node.Destroy(true);
            parent.Destroy(true);
        }
    }

    [Test]
    public void SceneRootAdoptionThenRemoval_InvalidatesInitialLeafReceipt()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        XRScene scene = new();
        scene.SceneRoots.Add(node);
        scene.SceneRoots.Remove(node);
        try
        {
            Assert.That(node.HasEnteredRootCollection, Is.True);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(node.IsDestroyed, Is.False);
        }
        finally
        {
            node.Destroy(true);
            scene.Destroy(true);
        }
    }

    [Test]
    public void RuntimeRootAdoptionThenRemoval_InvalidatesInitialLeafReceipt()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RootNodeCollection roots = new(new TestWorldContext());
        roots.Add(node);
        roots.Remove(node);
        try
        {
            Assert.That(node.HasEnteredRootCollection, Is.True);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(node.IsDestroyed, Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ReservedLeafRejectsNestedGuardedAbortBeforePreparation()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt targetCreation = new(node);
        RuntimeComponentCreationReceipt requesterCreation = new(node);
        receipt.Register(requesterCreation);
        receipt.Register(targetCreation);
        AbortSensitiveComponent? target = null;
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<AbortRequesterComponent>(() =>
        {
            _ = new AbortRequesterComponent(targetCreation);
            throw new InvalidOperationException("Factory failed.");
        }, requesterCreation));
        Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded<AbortSensitiveComponent>(() =>
        {
            target = new AbortSensitiveComponent();
            throw new InvalidOperationException("Factory failed.");
        }, targetCreation));

        Assert.That(receipt.TryRetire(), Is.True, receipt.Failure?.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(targetCreation.CleanupFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(targetCreation.CleanupStarted, Is.False);
            Assert.That(target!.CleanupCalls, Is.EqualTo(1));
            Assert.That(target.IsDestroyed, Is.True);
        });
    }

    [Test]
    public void ForeignAndDuplicateMembership_RejectBeforeTeardown()
    {
        SceneNode node = new("Leaf");
        SceneNode foreign = new("Foreign");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        ResourceComponent component = node.AddComponentGuarded(
            () => new ResourceComponent(new Resource()), creation)!;
        foreign.ReattachComponent(component);
        try
        {
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(component.IsDestroyed, Is.False);
            Assert.That(node.IsDestroyed, Is.False);
        }
        finally
        {
            foreign.DetachComponent(component);
            node.DetachComponent(component);
            component.Destroy(true);
            node.Destroy(true);
            foreign.Destroy(true);
        }

        SceneNode duplicateNode = new("Duplicate");
        DetachedLeafRetirementReceipt duplicateReceipt = new(duplicateNode);
        RuntimeComponentCreationReceipt duplicateCreation = new(duplicateNode);
        duplicateReceipt.Register(duplicateCreation);
        ResourceComponent duplicate = duplicateNode.AddComponentGuarded(
            () => new ResourceComponent(new Resource()), duplicateCreation)!;
        duplicateNode.ReattachComponent(duplicate);
        try
        {
            Assert.That(duplicateReceipt.TryRetire(), Is.False);
            Assert.That(duplicate.IsDestroyed, Is.False);
        }
        finally
        {
            duplicateNode.DetachComponent(duplicate);
            duplicateNode.DetachComponent(duplicate);
            duplicate.Destroy(true);
            duplicateNode.Destroy(true);
        }
    }

    [Test]
    public void UnregisteredComponentOrChild_RejectsLeafAdmission()
    {
        SceneNode componentNode = new("Leaf");
        DetachedLeafRetirementReceipt componentReceipt = new(componentNode);
        componentNode.AddComponent(static () => new ResourceComponent(new Resource()));
        try { Assert.That(componentReceipt.TryRetire(), Is.False); }
        finally { componentNode.Destroy(true); }

        SceneNode parent = new("Parent");
        DetachedLeafRetirementReceipt childReceipt = new(parent);
        SceneNode child = new(parent);
        try
        {
            Assert.That(childReceipt.TryRetire(), Is.False);
            Assert.That(child.IsDestroyed, Is.False);
        }
        finally { parent.Destroy(true); }
    }

    [Test]
    public void RegeneratedLeaf_RejectsBeforeDestructionCallback()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        int callbacks = 0;
        node.Destroying += _ => { callbacks++; return true; };
        node.Generate();
        try
        {
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(callbacks, Is.Zero);
            Assert.That(node.IsDestroyed, Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void VetoOrPartialCleanupFailure_DoesNotRetryCallbacks()
    {
        SceneNode vetoNode = new("Veto");
        DetachedLeafRetirementReceipt vetoReceipt = new(vetoNode);
        int vetoCalls = 0;
        Func<XREngine.Data.Core.XRObjectBase, bool> veto = _ => { vetoCalls++; return false; };
        vetoNode.Destroying += veto;
        try
        {
            Assert.That(vetoReceipt.TryRetire(), Is.False);
            Assert.That(vetoReceipt.TryRetire(), Is.False);
            Assert.That(vetoCalls, Is.EqualTo(1));
            Assert.That(vetoNode.IsDestroyed, Is.False);
        }
        finally
        {
            vetoNode.Destroying -= veto;
            vetoNode.Destroy(true);
        }

        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        ThrowingResourceComponent component = node.AddComponentGuarded(
            static () => new ThrowingResourceComponent(), creation)!;
        try
        {
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(component.CleanupCalls, Is.EqualTo(1));
            Assert.That(receipt.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(receipt.RetainedRetirement, Is.Not.Null);
        }
        finally
        {
            component.Destroy(true);
            node.Destroy(true);
        }
    }

    [Test]
    public void DestroyedObserverThrow_RecordsConfirmedCanonicalDestruction()
    {
        SceneNode node = new("Leaf");
        DetachedLeafRetirementReceipt receipt = new(node);
        RuntimeComponentCreationReceipt creation = new(node);
        receipt.Register(creation);
        ResourceComponent component = node.AddComponentGuarded(
            () => new ResourceComponent(new Resource()), creation)!;
        component.Destroyed += _ => throw new InvalidOperationException("Observer failed.");
        try
        {
            Assert.That(receipt.TryRetire(), Is.False);
            Assert.That(receipt.RetainedRetirement!.IsConfirmedDestroyed(component), Is.True);
            Assert.That(component.IsDestroyed, Is.True);
            Assert.That(receipt.TryRetire(), Is.False);
        }
        finally { node.Destroy(true); }
    }

    private sealed class Resource
    {
        public int ReleaseCount { get; private set; }
        public void Release() => ReleaseCount++;
    }

    private sealed class ResourceComponent(Resource resource) : XRComponent
    {
        protected override void OnDestroying()
        {
            resource.Release();
            base.OnDestroying();
        }
    }

    private sealed class ThrowingResourceComponent : XRComponent
    {
        private bool _throwOnce = true;
        public int CleanupCalls { get; private set; }
        protected override void OnDestroying()
        {
            CleanupCalls++;
            if (_throwOnce)
            {
                _throwOnce = false;
                throw new InvalidOperationException("Cleanup failed.");
            }
            base.OnDestroying();
        }
    }

    private sealed class AbortRequesterComponent(RuntimeComponentCreationReceipt target) : XRComponent
    {
        protected override void OnDestroying()
        {
            target.TryAbortUnchangedComponent();
            base.OnDestroying();
        }
    }

    private sealed class AbortSensitiveComponent : XRComponent
    {
        public int CleanupCalls { get; private set; }
        protected override void OnDestroying()
        {
            CleanupCalls++;
            base.OnDestroying();
        }
    }
}
