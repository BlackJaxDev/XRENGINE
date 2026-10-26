using System.Reflection;
using NUnit.Framework;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

/// <summary>Checks finalizer queue ordering for exact private structural lists.</summary>
[TestFixture, NonParallelizable]
public sealed class StructuralListFinalizerTests
{
    private static readonly MethodInfo BaseFinalizer = typeof(XRObjectBase).GetMethod(
        "Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingMethodException(nameof(XRObjectBase), "Finalize");

    [SetUp]
    public void SetUp() => XRObjectBase.ProcessPendingDestructions();

    [TestCase(true)]
    [TestCase(false)]
    public void DisplacedUncachedTransformAndListFinalizeInEitherOrder(bool listFirst)
    {
        var (node, displaced) = CreateDisplacedTransform();
        EventList<TransformBase> children = displaced.Children;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(displaced.SceneNode, Is.Null);
                Assert.That(displaced.Parent, Is.Null);
                Assert.That(XRObjectBase.ObjectsCache.Values.Any(value => ReferenceEquals(value, displaced)), Is.False);
                Assert.That(children.GetMutationAuthority(), Is.Not.Null);
            });

            InvokeFinalizer(listFirst ? children : displaced);
            InvokeFinalizer(listFirst ? displaced : children);
            Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);

            Assert.Multiple(() =>
            {
                Assert.That(displaced.IsDestroyed, Is.True);
                Assert.That(children.IsDestroyed, Is.True);
                Assert.That(displaced.IsDestroyQueued, Is.False);
                Assert.That(children.IsDestroyQueued, Is.False);
                Assert.That(node.IsDestroyed, Is.False);
                Assert.That(node.Transform.IsDestroyed, Is.False);
            });
        }
        finally { CleanupDisplacedTransform(node, displaced); }
    }

    [Test]
    public void InstalledComponentListFinalizerLeavesCleanupToItsNode()
    {
        SceneNode node;
        using (XRObjectBase.SuppressObjectCacheRegistration())
            node = new SceneNode("Finalizer component owner");
        SuppressNodeFinalizers(node);
        XRObjectBase components = (XRObjectBase)node.Components;
        try
        {
            InvokeFinalizer(components);
            InvokeFinalizer(node);
            Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
            Assert.That(node.IsDestroyed, Is.True);
            Assert.That(components.IsDestroyed, Is.True);
            Assert.That(node.Transform.IsDestroyed, Is.True);
        }
        finally
        {
            // A baseline failure can leave the node live with an already destroyed list.
            if (!node.IsDestroyed && !components.IsDestroyed)
                node.Destroy(true);
            if (!node.Transform.IsDestroyed && !node.Transform.Children.IsDestroyed)
                node.Transform.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            GC.KeepAlive(node);
            GC.KeepAlive(components);
        }
    }

    [Test]
    public void ReplacedPrivateListKeepsItsOwnFinalizerCleanup()
    {
        Transform owner;
        EventList<TransformBase> supplied;
        using (XRObjectBase.SuppressObjectCacheRegistration())
        {
            owner = new Transform();
            supplied = new EventList<TransformBase>();
        }
        EventList<TransformBase> retired = owner.Children;
        GC.SuppressFinalize(owner);
        GC.SuppressFinalize(retired);
        GC.SuppressFinalize(supplied);
        try
        {
            owner.Children = supplied;
            GC.SuppressFinalize(owner.Children);
            Assert.That(owner.Children, Is.Not.SameAs(retired));
            Assert.That(retired.GetMutationAuthority(), Is.Not.Null);

            InvokeFinalizer(retired);
            Assert.That(retired.IsDestroyQueued, Is.True);
            Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);

            Assert.That(retired.IsDestroyed, Is.True);
            Assert.That(owner.IsDestroyed, Is.False);
            Assert.That(owner.Children.IsDestroyed, Is.False);
            owner.Destroy(true);
            Assert.That(owner.Children.IsDestroyed, Is.True);
        }
        finally
        {
            if (!owner.IsDestroyed) owner.Destroy(true);
            if (!retired.IsDestroyed) retired.Destroy(true);
            if (!supplied.IsDestroyed) supplied.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            GC.KeepAlive(owner);
            GC.KeepAlive(retired);
            GC.KeepAlive(supplied);
        }
    }

    [Test]
    public void OrdinaryListKeepsItsOwnFinalizerCleanup()
    {
        EventList<int> values = new();
        GC.SuppressFinalize(values);
        try
        {
            Assert.That(values.GetMutationAuthority(), Is.Null);
            InvokeFinalizer(values);
            Assert.That(values.IsDestroyQueued, Is.True);
            Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
            Assert.That(values.IsDestroyed, Is.True);
        }
        finally
        {
            if (!values.IsDestroyed) values.Destroy(true);
            XRObjectBase.ProcessPendingDestructions();
            GC.KeepAlive(values);
        }
    }

    [Test]
    public void VetoedOwnerRetainsItsListUntilCanonicalCleanupIsAllowed()
    {
        var (node, displaced) = CreateDisplacedTransform();
        EventList<TransformBase> children = displaced.Children;
        bool allow = false;
        int calls = 0;
        displaced.Destroying += _ => { calls++; return allow; };
        try
        {
            InvokeFinalizer(children);
            InvokeFinalizer(displaced);
            Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(displaced.IsDestroyed, Is.False);
                Assert.That(children.IsDestroyed, Is.False);
                Assert.That(displaced.IsDestroyQueued, Is.False);
                Assert.That(children.IsDestroyQueued, Is.False);
            });
            Assert.DoesNotThrow(() => children.Clear());
            allow = true;
            displaced.Destroy(true);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(displaced.IsDestroyed, Is.True);
            Assert.That(children.IsDestroyed, Is.True);
        }
        finally
        {
            allow = true;
            CleanupDisplacedTransform(node, displaced);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExplicitPrivateListDestructionStillInvalidatesOwnerCleanup(bool now)
    {
        var (node, displaced) = CreateDisplacedTransform();
        EventList<TransformBase> children = displaced.Children;
        try
        {
            children.Destroy(now);
            InvokeFinalizer(displaced);
            InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(
                XRObjectBase.ProcessPendingDestructions);
            Assert.That(failure!.Message, Is.EqualTo(
                "The structural list no longer belongs to its exact owner."));
            Assert.That(children.IsDestroyed, Is.True);
            Assert.That(displaced.IsDestroyed, Is.False);
            Assert.That(displaced.IsDestroyQueued, Is.False);
        }
        finally { CleanupDisplacedTransform(node, displaced); }
    }

    private static (SceneNode Node, TransformBase Displaced) CreateDisplacedTransform()
    {
        SceneNode node;
        TransformBase displaced;
        using (XRObjectBase.SuppressObjectCacheRegistration())
        {
            node = new SceneNode("Finalizer transform owner");
            displaced = node.Transform;
            node.SetTransform(new Transform());
        }
        SuppressNodeFinalizers(node);
        GC.SuppressFinalize(displaced);
        GC.SuppressFinalize(displaced.Children);
        return (node, displaced);
    }

    private static void SuppressNodeFinalizers(SceneNode node)
    {
        // Reflection invokes the real finalizer body. Prevent a later CLR invocation.
        GC.SuppressFinalize(node);
        GC.SuppressFinalize(node.Components);
        GC.SuppressFinalize(node.Transform);
        GC.SuppressFinalize(node.Transform.Children);
    }

    private static void InvokeFinalizer(XRObjectBase value)
        => BaseFinalizer.Invoke(value, BindingFlags.DoNotWrapExceptions, null, null, null);

    private static void CleanupDisplacedTransform(SceneNode node, TransformBase displaced)
    {
        try
        {
            // The negative test deliberately leaves a live owner with a destroyed list.
            // Its request was consumed, its finalizer is suppressed, and it was never cached.
            if (!displaced.IsDestroyed && !displaced.Children.IsDestroyed)
                displaced.Destroy(true);
        }
        finally
        {
            try { if (!node.IsDestroyed) node.Destroy(true); }
            finally
            {
                XRObjectBase.ProcessPendingDestructions();
                GC.KeepAlive(node);
                GC.KeepAlive(displaced);
            }
        }
    }
}
