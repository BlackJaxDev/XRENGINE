using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class CanonicalTransformUnlinkTests
{
    [Test]
    public void NullWorldTransferKeepsPreviouslyAllowedOldSubtreeUnlink()
    {
        TestWorldContext world = new();
        SceneNode root = new(world, "Root");
        SceneNode child = root.NewChild("Child");
        TransformBase old = root.Transform;
        TransformBase? replacement = null;
        XRPropertyChangedEventHandler replace = (_, change) =>
        {
            if (change.PropertyName != nameof(SceneNode.World) || root.World is not null ||
                replacement is not null)
                return;
            replacement = new Transform();
            root.SetTransform(replacement, SceneNode.ETransformSetFlags.None);
        };
        root.PropertyChanged += replace;
        try
        {
            Assert.DoesNotThrow(() => root.SetWorldContext(null));
            Assert.That(root.Transform, Is.SameAs(replacement));
            Assert.That(old.World, Is.Null);
            Assert.That(old.SceneNode, Is.Null);
            Assert.That(old.Children, Does.Contain(child.Transform));
            Assert.That(child.Parent, Is.Null);
            Assert.That(child.Transform.Parent, Is.SameAs(old));
        }
        finally
        {
            root.PropertyChanged -= replace;
            child.Destroy(true);
            if (!old.IsDestroyed) old.Destroy(true);
            root.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ActivationChildCanReplaceItsTransformWithAnActiveScheduler(
        bool requireTransform, bool schedulerActive)
    {
        TestWorldContext world = new();
        SceneNode? schedulerNode = null;
        if (schedulerActive)
        {
            schedulerNode = new("Chain");
            _ = schedulerNode.AddComponent<PhysicsChainComponent>()!;
            schedulerNode.SetWorldContext(world);
            Assert.That(PhysicsChainWorld.TryGet(world, out _), Is.True);
        }
        SceneNode root = new("Root");
        ActivationChildProbe probe = root.AddComponent(() => new ActivationChildProbe(requireTransform))!;
        try
        {
            Assert.DoesNotThrow(() => root.SetWorldContext(world));
            Assert.That(probe.ActivationCount, Is.EqualTo(1));
            Assert.That(probe.Child, Is.Not.Null);
            Assert.That(probe.Child!.World, Is.SameAs(world));
            Assert.That(probe.Child.Transform, Is.TypeOf<RectTransform>());
            Assert.That(probe.OriginalTransform!.SceneNode, Is.Null);
            Assert.That(probe.OriginalTransform.World, Is.Null);
            Assert.That(probe.OriginalTransform.Parent, Is.Null);
            if (schedulerActive)
                Assert.That(PhysicsChainWorld.TryGet(world, out _), Is.True);
        }
        finally
        {
            root.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (probe.OriginalTransform is { IsDestroyed: false } old) old.Destroy(true);
            if (schedulerNode is not null)
            {
                schedulerNode.SetWorldContext(null);
                schedulerNode.Destroy(true);
            }
            XRObjectBase.ProcessPendingDestructions();
        }
    }

    [Test]
    public void ActivationCallbackCanReplaceTheNewChildDefaultTransform()
    {
        SceneNode root = new("Root");
        TestWorldContext world = new();
        SceneNode? child = null;
        TransformBase? old = null;
        TransformBase? replacement = null;
        bool entered = false;
        XRPropertyChangedEventHandler callback = (_, change) =>
        {
            if (entered || change.PropertyName != nameof(TransformBase.World))
                return;
            entered = true;
            child = root.NewChild("Child");
            old = child.Transform;
            replacement = child.SetTransform<Transform>();
        };
        root.Transform.PropertyChanged += callback;
        try
        {
            Assert.DoesNotThrow(() => root.SetWorldContext(world));
            Assert.That(entered, Is.True);
            Assert.That(child, Is.Not.Null);
            Assert.That(child!.Transform, Is.SameAs(replacement));
            Assert.That(child.World, Is.SameAs(world));
            Assert.That(replacement!.World, Is.SameAs(world));
            Assert.That(old!.SceneNode, Is.Null);
            Assert.That(old.World, Is.Null);
            Assert.That(old.Parent, Is.Null);
        }
        finally
        {
            root.Transform.PropertyChanged -= callback;
            root.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (old is { IsDestroyed: false }) old.Destroy(true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OldTransformCallbackCannotChangeOwnerBeforeWorldWrite(bool regenerate)
    {
        SceneNode root = new("Root");
        SceneNode foreign = new("Foreign");
        TestWorldContext world = new();
        TransformBase? old = null;
        TransformBase? attempted = null;
        SceneNode? child = null;
        Exception? failure = null;
        bool entered = false;
        XRPropertyChangedEventHandler callback = (_, change) =>
        {
            if (entered || change.PropertyName != nameof(TransformBase.World))
                return;
            entered = true;
            child = root.NewChild("Child");
            old = child.Transform;
            XRPropertyChangingEventHandler changeOwner = (_, changing) =>
            {
                if (changing.PropertyName != nameof(TransformBase.World))
                    return;
                if (regenerate) old!.Generate();
                else old!.SceneNode = foreign;
            };
            old.PropertyChanging += changeOwner;
            attempted = new Transform();
            try { child.SetTransform(attempted); }
            catch (Exception exception) { failure = exception; }
            finally { old.PropertyChanging -= changeOwner; }
        };
        root.Transform.PropertyChanged += callback;
        try
        {
            root.SetWorldContext(world);
            Assert.That(entered, Is.True);
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(old!.World, Is.SameAs(world));
            Assert.That(old.Parent, Is.SameAs(root.Transform));
            Assert.That(child!.Transform, Is.SameAs(old));
        }
        finally
        {
            root.Transform.PropertyChanged -= callback;
            if (old is not null && child is not null && !ReferenceEquals(old.SceneNode, child))
                old.SceneNode = child;
            root.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (attempted is { IsDestroyed: false }) attempted.Destroy(true);
        }
    }

    [Test]
    public void PostWriteOwnerChangeStopsBeforeOldParentCleanup()
    {
        SceneNode root = new("Root");
        SceneNode foreign = new("Foreign");
        TestWorldContext world = new();
        SceneNode? child = null;
        TransformBase? old = null;
        TransformBase? attempted = null;
        Exception? failure = null;
        bool entered = false;
        bool reboundAfterWrite = false;
        IRuntimeWorldContext? worldAtFailure = world;
        XRPropertyChangedEventHandler onRootWorld = (_, change) =>
        {
            if (entered || change.PropertyName != nameof(TransformBase.World))
                return;
            entered = true;
            child = root.NewChild("Child");
            old = child.Transform;
            XRPropertyChangedEventHandler rebind = (_, changed) =>
            {
                if (changed.PropertyName == nameof(TransformBase.World) && old!.World is null)
                {
                    reboundAfterWrite = true;
                    old.SceneNode = foreign;
                }
            };
            old.PropertyChanged += rebind;
            attempted = new Transform();
            try { child.SetTransform(attempted); }
            catch (Exception exception)
            {
                failure = exception;
                worldAtFailure = old.World;
            }
            finally { old.PropertyChanged -= rebind; }
        };
        root.Transform.PropertyChanged += onRootWorld;
        try
        {
            root.SetWorldContext(world);
            Assert.That(entered, Is.True);
            Assert.That(reboundAfterWrite, Is.True);
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(worldAtFailure, Is.Null);
            // The outer transfer can publish World again to its retained child link.
            Assert.That(old!.World, Is.SameAs(world));
            Assert.That(old.Parent, Is.SameAs(root.Transform));
            Assert.That(child!.Transform, Is.SameAs(old));
        }
        finally
        {
            root.Transform.PropertyChanged -= onRootWorld;
            if (old is not null && child is not null && !ReferenceEquals(old.SceneNode, child))
                old.SceneNode = child;
            root.Destroy(true);
            foreign.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (attempted is { IsDestroyed: false }) attempted.Destroy(true);
        }
    }

    [Test]
    public void CanonicalUnlinkDoesNotAdmitOtherObjectsOrForeignWorlds()
    {
        TestWorldContext world = new();
        TestWorldContext foreignWorld = new();
        SceneNode unrelated = new(world, "Unrelated");
        SceneNode root = new("Root");
        SceneNode? child = null;
        TransformBase? old = null;
        Exception? unrelatedFailure = null;
        Exception? foreignFailure = null;
        bool entered = false;
        XRPropertyChangedEventHandler onRootWorld = (_, change) =>
        {
            if (entered || change.PropertyName != nameof(TransformBase.World))
                return;
            entered = true;
            child = root.NewChild("Child");
            old = child.Transform;
            XRPropertyChangingEventHandler attempt = (_, changing) =>
            {
                if (changing.PropertyName != nameof(TransformBase.World))
                    return;
                try { unrelated.SetWorldContext(null); }
                catch (Exception failure) { unrelatedFailure = failure; }
                try { old!.SetWorldContext(foreignWorld); }
                catch (Exception failure) { foreignFailure = failure; }
            };
            old.PropertyChanging += attempt;
            try { child.SetTransform<Transform>(); }
            finally { old.PropertyChanging -= attempt; }
        };
        root.Transform.PropertyChanged += onRootWorld;
        try
        {
            root.SetWorldContext(world);
            Assert.That(unrelatedFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(foreignFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(unrelated.World, Is.SameAs(world));
            Assert.That(old!.World, Is.Null);
            Assert.That(old.SceneNode, Is.Null);
            Assert.That(child!.World, Is.SameAs(world));
        }
        finally
        {
            root.Transform.PropertyChanged -= onRootWorld;
            root.Destroy(true);
            unrelated.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (old is { IsDestroyed: false }) old.Destroy(true);
        }
    }

    [Test]
    public void VetoedOldWorldClearStopsBeforeParentCleanupAndClosesPermit()
    {
        TestWorldContext world = new();
        SceneNode root = new("Root");
        SceneNode unrelated = new(world, "Unrelated");
        SceneNode? child = null;
        TransformBase? old = null;
        TransformBase? attempted = null;
        Exception? failure = null;
        bool entered = false;
        XRPropertyChangedEventHandler onRootWorld = (_, change) =>
        {
            if (entered || change.PropertyName != nameof(TransformBase.World))
                return;
            entered = true;
            child = root.NewChild("Child");
            old = child.Transform;
            XRPropertyChangingEventHandler veto = (_, changing) =>
            {
                if (changing.PropertyName == nameof(TransformBase.World))
                    ((XRPropertyChangingEventArgs<IRuntimeWorldContext?>)changing).AllowChange = false;
            };
            old.PropertyChanging += veto;
            attempted = new Transform();
            try { child.SetTransform(attempted); }
            catch (Exception exception) { failure = exception; }
            finally { old.PropertyChanging -= veto; }
        };
        root.Transform.PropertyChanged += onRootWorld;
        try
        {
            root.SetWorldContext(world);
            Assert.That(failure, Is.TypeOf<InvalidOperationException>());
            Assert.That(old!.World, Is.SameAs(world));
            Assert.That(old.Parent, Is.SameAs(root.Transform));
            Assert.That(child!.Transform, Is.SameAs(old));
            Assert.DoesNotThrow(() => unrelated.SetWorldContext(null));
            Assert.That(unrelated.World, Is.Null);
        }
        finally
        {
            root.Transform.PropertyChanged -= onRootWorld;
            if (old is not null && child is not null && !ReferenceEquals(old.SceneNode, child))
                old.SceneNode = child;
            root.Destroy(true);
            unrelated.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            if (attempted is { IsDestroyed: false }) attempted.Destroy(true);
        }
    }

}
