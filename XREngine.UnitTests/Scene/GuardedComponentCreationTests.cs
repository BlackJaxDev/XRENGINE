using NUnit.Framework;
using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Scene;

[TestFixture]
[NonParallelizable]
public sealed class GuardedComponentCreationTests
{
    [Test]
    public void UnchangedOwner_AddsClaimedComponent()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Probe component = node.AddComponentGuarded(static () => new Probe(), receipt, "Probe")!;

            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component, Is.SameAs(component));
                Assert.That(receipt.Failure, Is.Null);
                Assert.That(component.World, Is.SameAs(world));
                Assert.That(component.Name, Is.EqualTo("Probe"));
                Assert.That(node.GetComponent<Probe>(), Is.SameAs(component));
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ReceiptCannotBeReusedOrClaimedByNestedFactory()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Probe component = node.AddComponentGuarded(static () => new Probe(), receipt)!;
            bool secondFactoryRan = false;
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                secondFactoryRan = true;
                return new Probe();
            }, receipt));
            Assert.Multiple(() =>
            {
                Assert.That(secondFactoryRan, Is.False);
                Assert.That(receipt.Component, Is.SameAs(component));
                Assert.That(receipt.Failure, Is.Null);
            });

            RuntimeComponentCreationReceipt nestedReceipt = new(node);
            Probe outer = node.AddComponentGuarded(() =>
            {
                Assert.Throws<InvalidOperationException>(
                    () => node.AddComponentGuarded(static () => new Probe(), nestedReceipt));
                return new Probe();
            }, nestedReceipt)!;
            Assert.That(nestedReceipt.Component, Is.SameAs(outer));
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ConstructorThrow_RetainsClaimBeforeFactoryReturns()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new ThrowingProbe(), receipt))!;

            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component, Is.TypeOf<ThrowingProbe>());
                Assert.That(receipt.Failure, Is.SameAs(failure));
                Assert.That(receipt.Component!.IsDestroyed, Is.False);
                Assert.That(node.GetComponent<ThrowingProbe>(), Is.Null);
            });
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.True);
            Assert.That(receipt.Component!.IsDestroyed, Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CleanupThrow_AllowsOnlyOneAbortAttempt()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new ThrowingCleanupProbe(), receipt))!;
            ThrowingCleanupProbe component = (ThrowingCleanupProbe)receipt.Component!;
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Failure, Is.SameAs(failure));
                Assert.That(receipt.CleanupFailure, Is.TypeOf<ApplicationException>());
                Assert.That(component.IsDestroyed, Is.False);
                Assert.That(component.CleanupCount, Is.EqualTo(1));
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
            Assert.That(component.CleanupCount, Is.EqualTo(1));
            component.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CleanupRegeneratesComponent_DoesNotCommitDestruction()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new RegeneratingCleanupProbe(), receipt));
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.CleanupFailure, Is.TypeOf<InvalidOperationException>());
                Assert.That(receipt.Component!.IsDestroyed, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
            receipt.Component!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void NestedDestruction_DoesNotCountAsReceiptAbort()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new NestedDestroyCleanupProbe(), receipt));
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component!.IsDestroyed, Is.True);
                Assert.That(receipt.CleanupFailure, Is.TypeOf<InvalidOperationException>());
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void CleanupRegeneratesNode_DoesNotCommitDestruction()
    {
        SceneNode node = new("Owner");
        RegeneratingOwnerCleanupProbe.Owner = node;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new RegeneratingOwnerCleanupProbe(), receipt));
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.CleanupFailure, Is.TypeOf<InvalidOperationException>());
                Assert.That(receipt.Component!.IsDestroyed, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
            receipt.Component!.Destroy(true);
        }
        finally
        {
            RegeneratingOwnerCleanupProbe.Owner = null;
            node.Destroy(true);
        }
    }

    [Test]
    public void CallerValidator_RejectsChangedCanvasOwnership()
    {
        SceneNode node = new("Owner");
        bool canvasIsCurrent = true;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node, () => canvasIsCurrent);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                Probe component = new();
                canvasIsCurrent = false;
                return component;
            }, receipt));

            Assert.That(receipt.Component, Is.TypeOf<Probe>());
            Assert.That(receipt.Failure, Is.Not.Null);
            Assert.That(node.GetComponent<Probe>(), Is.Null);
            Assert.That(receipt.CanAbortClaimed, Is.True);
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ComponentCreated_ReparentRejectsBeforeListInsertion()
    {
        SceneNode first = new("First");
        SceneNode second = new("Second");
        SceneNode node = new(first, "Owner");
        void ChangeOwner(XRComponent component)
        {
            if (component is Probe)
                node.Parent = second;
        }
        XRComponent.ComponentCreated += ChangeOwner;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component, Is.TypeOf<Probe>());
                Assert.That(receipt.Failure, Is.Not.Null);
                Assert.That(node.GetComponent<Probe>(), Is.Null);
            });
            Assert.That(receipt.CanAbortClaimed, Is.False);
            receipt.Component!.Destroy(true);
        }
        finally
        {
            XRComponent.ComponentCreated -= ChangeOwner;
            first.Destroy(true);
            second.Destroy(true);
        }
    }

    [Test]
    public void ComponentCreated_SameIdNewLifetimeRejectsCreation()
    {
        SceneNode node = new("Owner");
        void Regenerate(XRComponent component)
        {
            if (component is not Probe)
                return;
            Guid id = node.ID;
            node.Generate();
            node.AdoptPersistentID(id);
        }
        XRComponent.ComponentCreated += Regenerate;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Assert.That(node.GetComponent<Probe>(), Is.Null);
            Assert.That(receipt.CanAbortClaimed, Is.False);
            Assert.That(receipt.Failure, Is.Not.Null);
            Assert.That(receipt.Component!.IsDestroyed, Is.False);
            receipt.Component.Destroy(true);
        }
        finally
        {
            XRComponent.ComponentCreated -= Regenerate;
            node.Destroy(true);
        }
    }

    [Test]
    public void ComponentCreated_RegeneratesAndThrows_RetainsCallbackFailure()
    {
        SceneNode node = new("Owner");
        InvalidOperationException failure = new("Callback failed.");
        void ChangeThenThrow(XRComponent component)
        {
            if (component is not Probe)
                return;
            node.Generate();
            throw failure;
        }
        XRComponent.ComponentCreated += ChangeThenThrow;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.That(Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt)), Is.SameAs(failure));
            Assert.That(receipt.Failure, Is.SameAs(failure));
            Assert.That(receipt.Component, Is.TypeOf<Probe>());
            Assert.That(receipt.CanAbortClaimed, Is.False);
            Assert.That(receipt.Component!.IsDestroyed, Is.False);
            receipt.Component.Destroy(true);
        }
        finally
        {
            XRComponent.ComponentCreated -= ChangeThenThrow;
            node.Destroy(true);
        }
    }

    [Test]
    public void ComponentAdded_RegeneratesOwnerAndStopsContinuation()
    {
        SceneNode node = new("Owner");
        node.ComponentAdded += _ => node.Generate();
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Assert.That(receipt.Failure, Is.Not.Null);
            Assert.That(receipt.Component!.IsDestroyed, Is.False);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ComponentAdded_ThrowRetainsOriginalFailure()
    {
        SceneNode node = new(new TestWorldContext(), "Owner");
        InvalidOperationException failure = new("Callback failed.");
        node.ComponentAdded += _ => throw failure;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.That(Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt)), Is.SameAs(failure));
            Assert.That(receipt.Failure, Is.SameAs(failure));
            Assert.That(receipt.Component, Is.TypeOf<Probe>());
            Assert.That(receipt.IsInserted, Is.True);
            Assert.That(((Probe)receipt.Component!).ActivationCount, Is.Zero);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void VetoedInsertion_RemainsAbortableWithoutListMembership()
    {
        SceneNode node = new("Owner");
        node.Components.PreAdded += _ => false;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.IsInserted, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.True);
                Assert.That(receipt.Component!.IsDestroyed, Is.False);
            });
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.True);
            Assert.That(receipt.Component!.IsDestroyed, Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void FailedUninsertedComponent_ForeignMembershipBlocksAbort()
    {
        SceneNode node = new("Owner");
        SceneNode foreign = new("Foreign");
        node.Components.PreAdded += _ => false;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Probe component = (Probe)receipt.Component!;
            Assert.That(receipt.CanAbortClaimed, Is.True);

            foreign.ReattachComponent(component);
            Assert.Multiple(() =>
            {
                Assert.That(component.CaptureHierarchyListMemberships(), Has.Length.EqualTo(1));
                Assert.That(receipt.CanAbortClaimed, Is.False);
                Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
                Assert.That(component.IsDestroyed, Is.False);
            });
            Assert.That(foreign.DetachComponent(component), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(component.CaptureHierarchyListMemberships(), Is.Empty);
                Assert.That(receipt.CanAbortClaimed, Is.False);
                Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
                Assert.That(component.IsDestroyed, Is.False);
            });
            component.Destroy(true);
        }
        finally
        {
            node.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void WorldChangingOverrideAfterBase_RejectsBeforeWorldWrite()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new AfterBaseWorldProbe(), receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component!.World, Is.Null);
                Assert.That(receipt.IsInserted, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
            receipt.Component!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void SuppressedNotifications_ForeignWorldRejectsBeforeWrite()
    {
        TestWorldContext world = new();
        TestWorldContext foreignWorld = new();
        SceneNode node = new(world, "Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                Probe component = new();
                using (XRBase.SuppressPropertyNotifications())
                    component.AssignWorld(foreignWorld);
                return component;
            }, receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component!.World, Is.Null);
                Assert.That(receipt.CanAbortClaimed, Is.True);
            });
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void PreAddedOwnerChange_RejectsBeforeListWrite()
    {
        SceneNode node = new("Owner");
        node.Components.PreAdded += _ =>
        {
            node.Generate();
            return true;
        };
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.IsInserted, Is.False);
                Assert.That(receipt.Component!.CaptureHierarchyListMemberships(), Is.Empty);
                Assert.That(receipt.CanAbortClaimed, Is.False);
            });
            receipt.Component!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void PostAddedOwnerChange_RejectsBeforeComponentAdded()
    {
        SceneNode node = new("Owner");
        node.Components.PostAdded += _ => node.Generate();
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Probe component = (Probe)receipt.Component!;
            Assert.Multiple(() =>
            {
                Assert.That(receipt.IsInserted, Is.True);
                Assert.That(receipt.CanAbortClaimed, Is.False);
                Assert.That(component.AddedCount, Is.Zero);
                Assert.That(component.ActivationCount, Is.Zero);
            });
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void SuccessfulThenDetachedComponent_CannotBeAborted()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Probe component = node.AddComponentGuarded(static () => new Probe(), receipt)!;
            Assert.That(node.DetachComponent(component), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.IsInserted, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.False);
                Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
                Assert.That(component.IsDestroyed, Is.False);
            });
            component.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void FailedCommittedInsertionThenDetached_CannotBeAborted()
    {
        SceneNode node = new("Owner");
        node.ComponentAdded += _ => throw new InvalidOperationException("Add failed.");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new Probe(), receipt));
            Probe component = (Probe)receipt.Component!;
            Assert.That(receipt.IsInserted, Is.True);
            Assert.That(node.DetachComponent(component), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(receipt.IsInserted, Is.False);
                Assert.That(receipt.CanAbortClaimed, Is.False);
                Assert.That(receipt.TryAbortUnchangedComponent(), Is.False);
                Assert.That(component.IsDestroyed, Is.False);
            });
            component.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void ForeignReattachment_CannotClaimGuardedComponent()
    {
        SceneNode node = new("Owner");
        SceneNode foreign = new("Foreign");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                Probe component = new();
                foreign.ReattachComponent(component);
                return component;
            }, receipt));
            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component, Is.TypeOf<Probe>());
                Assert.That(receipt.Component!.CaptureHierarchyListMemberships(), Is.Empty);
                Assert.That(foreign.Components.Count, Is.Zero);
            });
            Assert.That(receipt.TryAbortUnchangedComponent(), Is.True);
        }
        finally
        {
            node.Destroy(true);
            foreign.Destroy(true);
        }
    }

    [Test]
    public void RequiredDependencies_MustAlreadyExist()
    {
        SceneNode node = new("Owner");
        try
        {
            RuntimeComponentCreationReceipt missingComponent = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new RequiredProbe(), missingComponent));
            Assert.That(node.GetComponent<Probe>(), Is.Null);
            Assert.That(missingComponent.TryAbortUnchangedComponent(), Is.True);

            Probe dependency = node.AddComponent(static () => new Probe())!;
            RuntimeComponentCreationReceipt presentComponent = new(node);
            RequiredProbe created = node.AddComponentGuarded(static () => new RequiredProbe(), presentComponent)!;
            Assert.That(node.GetComponent<RequiredProbe>(), Is.SameAs(created));
            Assert.That(node.GetComponent<Probe>(), Is.SameAs(dependency));

            RuntimeComponentCreationReceipt missingTransform = new(node);
            Assert.Throws<InvalidOperationException>(
                () => node.AddComponentGuarded(static () => new RectTransformProbe(), missingTransform));
            Assert.That(node.Transform, Is.TypeOf<Transform>());
            Assert.That(missingTransform.TryAbortUnchangedComponent(), Is.True);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void RequiredTransform_WhenPresent_AddsComponent()
    {
        SceneNode node = new("Owner", new RectTransform());
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            RectTransformProbe component = node.AddComponentGuarded(
                static () => new RectTransformProbe(), receipt)!;
            Assert.That(node.GetComponent<RectTransformProbe>(), Is.SameAs(component));
            Assert.That(node.Transform, Is.TypeOf<RectTransform>());
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void NameChanging_RegeneratedOwnerDoesNotWriteName()
    {
        SceneNode node = new("Owner");
        Probe? claimed = null;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                claimed = new Probe();
                claimed.PropertyChanging += (_, args) =>
                {
                    if (args.PropertyName == nameof(Probe.Name))
                        node.Generate();
                };
                return claimed;
            }, receipt, "Rejected"));

            Assert.Multiple(() =>
            {
                Assert.That(claimed!.Name, Is.Null);
                Assert.That(receipt.Component, Is.SameAs(claimed));
                Assert.That(receipt.Failure, Is.Not.Null);
            });
            claimed!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void WorldChanging_RegeneratedOwnerDoesNotWriteWorld()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Owner");
        Probe? claimed = null;
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                claimed = new Probe();
                claimed.PropertyChanging += (_, args) =>
                {
                    if (args.PropertyName == nameof(Probe.World))
                        node.Generate();
                };
                return claimed;
            }, receipt));

            Assert.Multiple(() =>
            {
                Assert.That(claimed!.World, Is.Null);
                Assert.That(receipt.Component, Is.SameAs(claimed));
                Assert.That(receipt.Failure, Is.Not.Null);
            });
            claimed!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void WorldChanged_RegeneratedOwnerStopsCreation()
    {
        TestWorldContext world = new();
        SceneNode node = new(world, "Owner");
        try
        {
            RuntimeComponentCreationReceipt receipt = new(node);
            Assert.Throws<InvalidOperationException>(() => node.AddComponentGuarded(() =>
            {
                Probe component = new();
                component.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(Probe.World))
                        node.Generate();
                };
                return component;
            }, receipt));

            Assert.Multiple(() =>
            {
                Assert.That(receipt.Component, Is.TypeOf<Probe>());
                Assert.That(receipt.Failure, Is.Not.Null);
                Assert.That(node.GetComponent<Probe>(), Is.Null);
            });
            receipt.Component!.Destroy(true);
        }
        finally { node.Destroy(true); }
    }

    [Test]
    public void DefaultFactory_DoesNotUseGuardedAdmission()
    {
        SceneNode node = new("Owner");
        void Regenerate(XRComponent component)
        {
            if (component is Probe)
                node.Generate();
        }
        XRComponent.ComponentCreated += Regenerate;
        try
        {
            Probe component = node.AddComponent(static () => new Probe())!;
            Assert.That(node.GetComponent<Probe>(), Is.SameAs(component));
        }
        finally
        {
            XRComponent.ComponentCreated -= Regenerate;
            node.Destroy(true);
        }
    }

    private sealed class Probe : XRComponent
    {
        public void AssignWorld(IRuntimeWorldContext world) => World = world;
        public int ActivationCount { get; private set; }
        public int AddedCount { get; private set; }
        protected override void AddedToSceneNode(SceneNode sceneNode)
        {
            AddedCount++;
            base.AddedToSceneNode(sceneNode);
        }
        protected override void OnComponentActivated()
        {
            ActivationCount++;
            base.OnComponentActivated();
        }
    }

    private sealed class AfterBaseWorldProbe : XRComponent
    {
        protected override bool OnPropertyChanging<T>(string? propName, T field, T next)
        {
            bool accepted = base.OnPropertyChanging(propName, field, next);
            if (propName == nameof(World))
                SceneNode.Generate();
            return accepted;
        }
    }

    [RequireComponents(typeof(Probe))]
    private sealed class RequiredProbe : XRComponent;

    [RequiresTransform(typeof(RectTransform))]
    private sealed class RectTransformProbe : XRComponent;

    private sealed class ThrowingProbe : XRComponent
    {
        public ThrowingProbe() => throw new InvalidOperationException("Constructor failed.");
    }

    private sealed class ThrowingCleanupProbe : XRComponent
    {
        private bool _throwOnce = true;
        public int CleanupCount { get; private set; }
        public ThrowingCleanupProbe() => throw new InvalidOperationException("Constructor failed.");
        protected override void OnDestroying()
        {
            CleanupCount++;
            if (_throwOnce)
            {
                _throwOnce = false;
                throw new ApplicationException("Cleanup failed.");
            }
            base.OnDestroying();
        }
    }

    private sealed class RegeneratingCleanupProbe : XRComponent
    {
        private bool _regenerateOnce = true;
        public RegeneratingCleanupProbe() => throw new InvalidOperationException("Constructor failed.");
        protected override void OnDestroying()
        {
            if (_regenerateOnce)
            {
                _regenerateOnce = false;
                Generate();
            }
            base.OnDestroying();
        }
    }

    private sealed class NestedDestroyCleanupProbe : XRComponent
    {
        private bool _destroyOnce = true;
        public NestedDestroyCleanupProbe() => throw new InvalidOperationException("Constructor failed.");
        protected override void OnDestroying()
        {
            if (_destroyOnce)
            {
                _destroyOnce = false;
                CreationReceipt = null;
                Destroy(true);
                return;
            }
            base.OnDestroying();
        }
    }

    private sealed class RegeneratingOwnerCleanupProbe : XRComponent
    {
        internal static SceneNode? Owner;
        private bool _regenerateOnce = true;
        public RegeneratingOwnerCleanupProbe() => throw new InvalidOperationException("Constructor failed.");
        protected override void OnDestroying()
        {
            if (_regenerateOnce)
            {
                _regenerateOnce = false;
                Owner!.Generate();
            }
            base.OnDestroying();
        }
    }
}
