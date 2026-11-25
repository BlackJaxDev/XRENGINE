using NUnit.Framework;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Prefabs;

[TestFixture, NonParallelizable]
public sealed class OwnedHierarchyRetirementTests
{
    [Test]
    public void CanonicalCloneRetiresItsFullForestWithoutQueueingDestruction()
    {
        SceneNode source = new("Source");
        _ = new SceneNode(source, "Child");
        ObjectConstructionResult<SceneNode> result = SceneNodePrefabUtility.CloneHierarchyOwned(source);
        OwnedHierarchyLifetime lifetime = new(result.Ownership, result.Failure);
        long pending = XRObjectBase.PendingDestructionCount;
        try
        {
            Assert.That(result.Succeeded, Is.True, result.Failure?.ToString());
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.Retired));
            Assert.That(lifetime.RetainedOwnership, Is.Null);
            Assert.That(XRObjectBase.PendingDestructionCount, Is.EqualTo(pending));
            foreach (XRObjectBase allocation in result.Ownership.Objects)
                if (allocation is SceneNode or TransformBase or XRComponent)
                {
                    Assert.That(allocation.IsDestroyed, Is.True);
                    Assert.That(XRObjectBase.ObjectsCache.TryGetValue(allocation.ID, out XRObjectBase? cached) &&
                        ReferenceEquals(cached, allocation), Is.False);
                }
            Assert.That(source.IsDestroyed, Is.False);
            Assert.That(source.GetChild(0)!.IsDestroyed, Is.False);
        }
        finally { CleanupKnownGraph(result.Ownership); source.Destroy(true); XRObjectBase.ProcessPendingDestructions(); }
    }

    [Test]
    public void OwnedDetachedComponentsAndTransformsCompleteCanonicalCleanup()
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(context =>
        {
            SceneNode root = new("Owned");
            root.AddComponent(static () => new OwnedRetirementComponentProbe());
            _ = new Transform();
            _ = new OwnedRetirementComponentProbe();
            _ = new EventList<SceneNode>();
            return root;
        });
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            foreach (XRObjectBase allocation in result.Ownership.Objects)
                if (allocation is OwnedRetirementComponentProbe component)
                    Assert.That(component.DestructionCount, Is.EqualTo(1));
        }
        finally { CleanupKnownGraph(result.Ownership); }
    }

    [TestCase("Scene")]
    [TestCase("SilentChild")]
    public void ForeignOwnersRejectRetirementBeforeAnyDestruction(string ownerKind)
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ => new SceneNode("Owned"));
        SceneNode foreign = new("Foreign");
        XRScene scene = new();
        int attempts = 0;
        Func<XRObjectBase, bool> observer = _ => { attempts++; return true; };
        result.Value!.Destroying += observer;
        if (ownerKind == "Scene") scene.SceneRoots.Add(result.Value);
        else foreign.Transform.Children.Add(result.Value.Transform, false, false);
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
            Assert.That(attempts, Is.Zero);
            Assert.That(result.Value.IsDestroyed, Is.False);
            Assert.That(foreign.IsDestroyed, Is.False);
        }
        finally
        {
            result.Value.Destroying -= observer;
            scene.SceneRoots.Remove(result.Value);
            foreign.Transform.Children.Remove(result.Value.Transform, false, false);
            CleanupKnownGraph(result.Ownership);
            foreign.Destroy(true);
            scene.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void UnknownNestedAssetRejectsBeforeStructuralEffects()
    {
        TextFile? asset = null;
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ =>
        {
            SceneNode node = new("Owned");
            asset = new TextFile();
            return node;
        });
        int attempts = 0;
        Func<XRObjectBase, bool> observer = _ => { attempts++; return true; };
        result.Value!.Destroying += observer;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.Failure!.Message, Does.Contain(typeof(TextFile).FullName));
            Assert.That(attempts, Is.Zero);
            Assert.That(asset!.IsDestroyed, Is.False);
            Assert.That(result.Value.IsDestroyed, Is.False);
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
        }
        finally
        {
            result.Value.Destroying -= observer;
            CleanupKnownGraph(result.Ownership);
            asset!.Destroy(true);
        }
    }

    [Test]
    public void PublicDestructionOverrideRejectsBeforeItsCodeRuns()
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(_ =>
            new SceneNode("Owned", new OwnedRetirementOverrideTransform()));
        OwnedRetirementOverrideTransform transform = (OwnedRetirementOverrideTransform)result.Value!.Transform;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(transform.DestructionCalls, Is.Zero);
            Assert.That(result.Value.IsDestroyed, Is.False);
        }
        finally { CleanupKnownGraph(result.Ownership); }
    }

    [Test]
    public void RemovalCallbacksCannotReuseRetirementAdmissionForPublicMutations()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        SceneNode node = result.Value!;
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)node.Components[0];
        SceneNode foreign = new("Foreign");
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        bool notified = false;
        EventList<XRComponent>.SingleHandler observer = removed =>
        {
            notified = true;
            Assert.That(removed, Is.SameAs(component));
            Assert.Throws<InvalidOperationException>(() => list.Add(component, false, false));
            Assert.Throws<InvalidOperationException>(() => foreign.Transform.Parent = node.Transform);
            Assert.Throws<InvalidOperationException>(() => component.Destroy(true));
            Assert.Throws<InvalidOperationException>(() => node.Destroy(false));
            Assert.Throws<InvalidOperationException>(() => node.Generate());
            Assert.Throws<InvalidOperationException>(() => component.AdoptPersistentID(Guid.NewGuid()));
        };
        list.PostRemoved += observer;
        EventList<XRComponent>.SingleCancelableHandler veto = _ => false;
        list.PreRemoved += veto;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(notified, Is.True);
            Assert.That(foreign.Parent, Is.Null);
            Assert.That(foreign.IsDestroyed, Is.False);
            Assert.That(component.DestructionCount, Is.EqualTo(1));
        }
        finally
        {
            list.PostRemoved -= observer;
            list.PreRemoved -= veto;
            CleanupKnownGraph(result.Ownership);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ThrowingRemovalObserverRetainsPublishedProgressAndStillNotifiesOtherObservers()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        SceneNode node = result.Value!;
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)node.Components[0];
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        InvalidOperationException failure = new("Expected removal failure.");
        EventList<XRComponent>.SingleHandler throwing = _ => throw failure;
        int notified = 0;
        EventList<XRComponent>.SingleHandler later = _ => notified++;
        list.PostRemoved += throwing;
        list.PostAnythingRemoved += later;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.Failure, Is.SameAs(failure));
            Assert.That(node.Components, Is.Empty);
            Assert.That(component.CaptureHierarchyListMemberships(), Is.Empty);
            Assert.That(component.IsDestroyed, Is.False);
            Assert.That(notified, Is.EqualTo(1));
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(notified, Is.EqualTo(1));
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
        }
        finally
        {
            list.PostRemoved -= throwing;
            list.PostAnythingRemoved -= later;
            CleanupKnownGraph(result.Ownership);
        }
    }

    [Test]
    public void DestructionVetoRetainsDetachedComponentAndDoesNotRetry()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)result.Value!.Components[0];
        int attempts = 0;
        Func<XRObjectBase, bool> veto = _ => { attempts++; return false; };
        component.Destroying += veto;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(component.IsDestroyed, Is.False);
            Assert.That(result.Value.Components, Is.Empty);
            component.Destroying -= veto;
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(component.DestructionCount, Is.Zero);
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
        }
        finally { component.Destroying -= veto; CleanupKnownGraph(result.Ownership); }
    }

    [Test]
    public void DestroyedFlagCannotReplaceCanonicalCleanupCompletion()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)result.Value!.Components[0];
        component.SkipBaseCleanup = true;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(component.IsDestroyed, Is.True);
            Assert.That(result.Value.IsDestroyed, Is.False);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(component.DestructionCount, Is.EqualTo(1));
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
        }
        finally { component.SkipBaseCleanup = false; CleanupKnownGraph(result.Ownership); }
    }

    [Test]
    public void EarlierUnprovedDestructionRejectsBeforeAnyNewCleanup()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)result.Value!.Components[0];
        component.SkipBaseCleanup = true;
        component.Destroy(true);
        int attempts = 0;
        Func<XRObjectBase, bool> observer = _ => { attempts++; return true; };
        result.Value.Destroying += observer;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(component.IsDestroyed, Is.True);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(attempts, Is.Zero);
            Assert.That(result.Value.IsDestroyed, Is.False);
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(component.DestructionCount, Is.EqualTo(1));
        }
        finally
        {
            result.Value.Destroying -= observer;
            component.SkipBaseCleanup = false;
            CleanupKnownGraph(result.Ownership);
        }
    }

    [Test]
    public void ActivatedOrphanRejectsBeforeAnyCleanup()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)result.Value!.Components[0];
        result.Value.OnActivated();
        Assert.That(result.Value.DetachComponent(component), Is.True);
        int attempts = 0;
        Func<XRObjectBase, bool> observer = _ => { attempts++; return true; };
        result.Value.Destroying += observer;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(lifetime.Failure!.Message, Does.Contain("active lifecycle"));
            Assert.That(attempts, Is.Zero);
            Assert.That(component.DestructionCount, Is.Zero);
            Assert.That(lifetime.RetainedOwnership, Is.SameAs(result.Ownership));
        }
        finally
        {
            result.Value.Destroying -= observer;
            component.IsActive = false;
            CleanupKnownGraph(result.Ownership);
        }
    }

    [Test]
    public void ParentPrechangeVetoKeepsExactRemainingGraphAndNeverRetries()
    {
        ObjectConstructionResult<SceneNode> result = XRObjectBase.ConstructOwned(context =>
        {
            SceneNode root = new("Root");
            _ = new SceneNode(root, "Child");
            return root;
        });
        SceneNode child = result.Value!.GetChild(0)!;
        int attempts = 0;
        XRPropertyChangingEventHandler veto = (_, change) =>
        {
            if (change.PropertyName != nameof(TransformBase.Parent)) return;
            attempts++;
            ((XRPropertyChangingEventArgs<TransformBase?>)change).AllowChange = false;
        };
        child.Transform.PropertyChanging += veto;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(child.Parent, Is.SameAs(result.Value));
            Assert.That(child.IsDestroyed, Is.False);
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(attempts, Is.EqualTo(1));
        }
        finally { child.Transform.PropertyChanging -= veto; CleanupKnownGraph(result.Ownership); }
    }

    [TestCase("Activate")]
    [TestCase("Deactivate")]
    [TestCase("BeginPlay")]
    [TestCase("EndPlay")]
    public void OrdinaryLifecycleCallbackPreventsRetirementUntilItReturns(string callbackKind)
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        SceneNode node = result.Value!;
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)node.Components[0];
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        bool callbackReturned = false;
        Action attempt = () =>
        {
            Assert.That(lifetime.TryRetire(), Is.False);
            Assert.That(node.IsDestroyed, Is.False);
            Assert.That(component.IsDestroyed, Is.False);
            callbackReturned = true;
        };
        try
        {
            switch (callbackKind)
            {
                case "Activate":
                    component.ActivationCallback = attempt;
                    node.OnActivated();
                    break;
                case "Deactivate":
                    node.OnActivated();
                    component.DeactivationCallback = attempt;
                    component.IsActive = false;
                    break;
                case "BeginPlay":
                    component.BeginPlayCallback = attempt;
                    node.OnBeginPlay();
                    break;
                case "EndPlay":
                    node.OnBeginPlay();
                    component.EndPlayCallback = attempt;
                    node.OnEndPlay();
                    break;
            }
            Assert.That(callbackReturned, Is.True);
            Assert.That(component.DestructionCount, Is.Zero);
            Assert.That(lifetime.State, Is.EqualTo(EOwnedHierarchyLifetimeState.RecoveryRequired));
            Assert.That(lifetime.TryRetire(), Is.False);
        }
        finally
        {
            component.ActivationCallback = null;
            component.DeactivationCallback = null;
            component.BeginPlayCallback = null;
            component.EndPlayCallback = null;
            node.OnDeactivated();
            node.OnEndPlay();
            CleanupKnownGraph(result.Ownership);
        }
    }

    [Test]
    public void ReservedStopCallbacksCannotRestartPublicLifecycle()
    {
        ObjectConstructionResult<SceneNode> result = WithComponent();
        SceneNode node = result.Value!;
        OwnedRetirementComponentProbe component = (OwnedRetirementComponentProbe)node.Components[0];
        node.OnActivated();
        node.OnBeginPlay();
        int stopped = 0;
        Action rejectRestart = () =>
        {
            stopped++;
            Assert.Throws<InvalidOperationException>(node.OnActivated);
            Assert.Throws<InvalidOperationException>(node.OnBeginPlay);
            Assert.Throws<InvalidOperationException>(node.OnDeactivated);
            Assert.Throws<InvalidOperationException>(node.OnEndPlay);
            Assert.Throws<InvalidOperationException>(() => component.IsActive = false);
        };
        component.DeactivationCallback = rejectRestart;
        component.EndPlayCallback = rejectRestart;
        OwnedHierarchyLifetime lifetime = new(result.Ownership);
        try
        {
            Assert.That(lifetime.TryRetire(), Is.True, lifetime.Failure?.ToString());
            Assert.That(stopped, Is.EqualTo(2));
            Assert.That(node.HasBegunPlay, Is.False);
            Assert.That(component.DestructionCount, Is.EqualTo(1));
        }
        finally
        {
            component.DeactivationCallback = null;
            component.EndPlayCallback = null;
            CleanupKnownGraph(result.Ownership);
        }
    }

    private static ObjectConstructionResult<SceneNode> WithComponent()
        => XRObjectBase.ConstructOwned(_ =>
        {
            SceneNode node = new("Owned");
            node.AddComponent(static () => new OwnedRetirementComponentProbe());
            return node;
        });

    private static void CleanupKnownGraph(ObjectConstructionOwnership ownership)
    {
        // These fixtures construct only the explicit probes above. This is test cleanup,
        // not the production ownership or retirement algorithm.
        foreach (XRObjectBase value in ownership.Objects)
            if (value is XRComponent && !value.IsDestroyed) value.Destroy(true);
        foreach (XRObjectBase value in ownership.Objects)
            if (value is SceneNode && !value.IsDestroyed) value.Destroy(true);
        foreach (XRObjectBase value in ownership.Objects)
            if (value is TransformBase && !value.IsDestroyed) value.Destroy(true);
        XRObjectBase.ProcessPendingDestructions();
    }
}
