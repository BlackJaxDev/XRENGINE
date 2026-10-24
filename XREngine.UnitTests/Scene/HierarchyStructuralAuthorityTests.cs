using System.Collections;
using System.Reflection;
using NUnit.Framework;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class HierarchyStructuralAuthorityTests
{
    private static readonly MethodInfo AcquireReservation = typeof(SceneNode).Assembly
        .GetType("XREngine.Scene.Prefabs.HierarchyStructuralReservation", throwOnError: true)!
        .GetMethod("TryAcquire", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly List<SceneNode> _nodes = [];
    private readonly List<Action> _detachObservers = [];
    private IDisposable? _cacheSuppression;

    [SetUp]
    public void SetUp() => _cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();

    [TearDown]
    public void TearDown()
    {
        foreach (Action detach in _detachObservers) detach();
        _detachObservers.Clear();
        foreach (SceneNode node in _nodes)
            if (!node.Transform.IsDestroyed)
            {
                node.Transform.Children.Clear(false, false);
                node.Transform.Parent = null;
            }
        foreach (SceneNode node in _nodes)
        {
            node.Destroy(true);
            node.Transform.Destroy(true);
        }
        XRObjectBase.ProcessPendingDestructions();
        _nodes.Clear();
        _cacheSuppression?.Dispose();
    }

    [Test]
    public void SilentAndInterfaceMutationsKeepExactOccurrenceCounts()
    {
        EventList<TransformBase> list = Node().Transform.Children;
        TransformBase first = Node().Transform;
        TransformBase second = Node().Transform;
        second.AdoptPersistentID(first.ID);
        list.Add(first, false, false);
        list.Insert(0, second, false, false);
        list.AddRange([first, second], false, false);
        Assert.That(Count(first, list), Is.EqualTo(2));
        Assert.That(Count(second, list), Is.EqualTo(2));
        Assert.That(list.IndexOf(second), Is.Zero);

        list._updating = true;
        ((IList)list)[0] = first;
        ((ICollection<TransformBase>)list).Add(second);
        ((IList)list).Remove(first);
        list._updating = false;
        Assert.That(Count(first, list), Is.EqualTo(2));
        Assert.That(Count(second, list), Is.EqualTo(2));

        list.RemoveRange(0, 2, false, false);
        Assert.That(Count(first, list), Is.Zero);
        Assert.That(Count(second, list), Is.EqualTo(2));
        list.RemoveAll(item => ReferenceEquals(item, second), false, false);
        Assert.That(second.CaptureHierarchyListMemberships(), Is.Empty);
    }

    [Test]
    public void ComponentDetachReattachAndSilentCastUseTheSameAuthority()
    {
        SceneNode node = Node();
        HierarchyAuthorityComponentProbe component = node.AddComponent(static () => new HierarchyAuthorityComponentProbe())!;
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        Assert.That(Count(component, list), Is.EqualTo(1));
        Assert.That(node.DetachComponent(component), Is.True);
        Assert.That(Count(component, list), Is.Zero);
        node.ReattachComponent(component);
        list.Add(component, false, false);
        Assert.That(Count(component, list), Is.EqualTo(2));
        list.RemoveAt(1, false, false);
        Assert.That(Count(component, list), Is.EqualTo(1));
    }

    [Test]
    public void PrechangeReentryRejectsOnlyTheStaleInsertion()
    {
        EventList<TransformBase> list = Node().Transform.Children;
        TransformBase pending = Node().Transform;
        TransformBase newer = Node().Transform;
        EventList<TransformBase>.SingleCancelableHandler handler = _ =>
        {
            list.Add(newer, false, false);
            return true;
        };
        list.PreAdded += handler;
        _detachObservers.Add(() => list.PreAdded -= handler);
        Assert.Throws<InvalidOperationException>(() => list.Add(pending));
        Assert.That(list.Single(), Is.SameAs(newer));
        Assert.That(Count(pending, list), Is.Zero);
        Assert.That(Count(newer, list), Is.EqualTo(1));
    }

    [Test]
    public void FailedPostAddKeepsTheStorageOwnerBeforeReciprocalParenting()
    {
        TransformBase parent = Node().Transform;
        TransformBase child = Node().Transform;
        EventList<TransformBase> list = parent.Children;
        EventList<TransformBase>.SingleCancelableHandler before = _ =>
        {
            using IDisposable? reservation = TryReserve(parent, child);
            Assert.That(reservation, Is.Null, "Prechange callbacks must keep both endpoints admitted.");
            return true;
        };
        EventList<TransformBase>.SingleHandler handler = _ =>
        {
            using IDisposable? reservation = TryReserve(parent, child);
            Assert.That(reservation, Is.Null, "The callback must keep both endpoints admitted.");
            throw new InvalidOperationException("Expected observer failure.");
        };
        list.PreAdded += before;
        list.PostAdded += handler;
        _detachObservers.Add(() => { list.PreAdded -= before; list.PostAdded -= handler; });
        Assert.Throws<InvalidOperationException>(() => list.Add(child));
        Assert.That(child.Parent, Is.Null);
        Assert.That(Count(child, list), Is.EqualTo(1));
        using IDisposable? settled = TryReserve(parent, child);
        Assert.That(settled, Is.Not.Null);
    }

    [Test]
    public void SetRejectsNewerContentsInstalledByRemovalCallback()
    {
        EventList<TransformBase> list = Node().Transform.Children;
        TransformBase old = Node().Transform;
        TransformBase pending = Node().Transform;
        TransformBase newer = Node().Transform;
        list.Add(old, false, false);
        EventList<TransformBase>.MultiHandler handler = _ => list.Add(newer, false, false);
        list.PostRemovedRange += handler;
        _detachObservers.Add(() => list.PostRemovedRange -= handler);
        Assert.Throws<InvalidOperationException>(() => list.Set([pending]));
        Assert.That(list.Single(), Is.SameAs(newer));
        Assert.That(Count(old, list), Is.Zero);
        Assert.That(Count(pending, list), Is.Zero);
        Assert.That(Count(newer, list), Is.EqualTo(1));
    }

    [Test]
    public void FindAllPredicateRunsWithoutAStorageReadLock()
    {
        EventList<TransformBase> list = Node().Transform.Children;
        TransformBase original = Node().Transform;
        TransformBase added = Node().Transform;
        list.Add(original, false, false);
        Task? mutation = null;
        try
        {
            TransformBase[] result = list.FindAll(item =>
            {
                mutation = Task.Run(() => list.Add(added, false, false));
                Assert.That(mutation.Wait(TimeSpan.FromSeconds(5)), Is.True,
                    "A predicate must not block a writer while it waits for that writer.");
                return true;
            }).ToArray();
            Assert.That(result, Is.EqualTo(new[] { original }));
            Assert.That(Count(added, list), Is.EqualTo(1));
        }
        finally
        {
            if (mutation is not null)
                Assert.That(mutation.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }
    }

    [Test]
    public void ExclusiveReservationRejectsForeignSilentAndRootWrites()
    {
        TransformBase parent = Node().Transform;
        SceneNode child = Node();
        XRScene scene = new();
        try
        {
            using IDisposable? reservation = TryReserve(child, child.Transform);
            Assert.That(reservation, Is.Not.Null);
            Task<Exception?> writer = Task.Run(() =>
            {
                try { parent.Children.Add(child.Transform, false, false); return null; }
                catch (Exception error) { return error; }
            });
            Assert.That(writer.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(writer.Result, Is.TypeOf<InvalidOperationException>());
            Assert.Throws<InvalidOperationException>(() => scene.SceneRoots.Add(child));
            Assert.That(parent.Children.Count, Is.Zero);
            Assert.That(child.CaptureSceneRootMemberships(), Is.Empty);
            Assert.That(child.Transform.CaptureHierarchyListMemberships(), Is.Empty);
        }
        finally { scene.Destroy(true); }
    }

    [Test]
    public void RangeNotificationsCannotChangeTheReservedStorageSnapshot()
    {
        EventList<TransformBase> list = Node().Transform.Children;
        TransformBase first = Node().Transform;
        TransformBase second = Node().Transform;
        EventList<TransformBase>.MultiCancelableHandler before = items =>
        {
            Assert.Throws<NotSupportedException>(() => ((IList<TransformBase>)items).Clear());
            return true;
        };
        list.PreAddedRange += before;
        XREngine.Core.TCollectionChangedEventHandler<TransformBase> changed = (_, change) =>
            Assert.Throws<NotSupportedException>(() => change.NewItems.Clear());
        list.CollectionChanged += changed;
        _detachObservers.Add(() => { list.PreAddedRange -= before; list.CollectionChanged -= changed; });
        list.AddRange([first, second]);
        Assert.That(Count(first, list), Is.EqualTo(1));
        Assert.That(Count(second, list), Is.EqualTo(1));
        using IDisposable? settled = TryReserve(first, second);
        Assert.That(settled, Is.Not.Null);
    }

    [Test]
    public void ChildReplacementCopiesTheAliasAndRetiresOnlyThePreviousList()
    {
        TransformBase parent = Node().Transform;
        TransformBase child = Node().Transform;
        EventList<TransformBase> previous = parent.Children;
        EventList<TransformBase> supplied = new([child, child]);
        parent.Children = supplied;
        Assert.That(parent.Children, Is.Not.SameAs(supplied));
        Assert.That(supplied.ThreadSafe, Is.False);
        supplied.Clear();
        Assert.That(parent.Children.Count, Is.EqualTo(2));
        Assert.That(Count(child, parent.Children), Is.EqualTo(2));
        Assert.That(child.Parent, Is.SameAs(parent));
        Assert.Throws<InvalidOperationException>(() => previous.Add(child, false, false));
        parent.Children.Remove(child);
        Assert.That(child.Parent, Is.SameAs(parent));
        Assert.That(Count(child, parent.Children), Is.EqualTo(1));
        parent.Children.Remove(child);
        Assert.That(child.Parent, Is.Null);
    }

    [Test]
    public void QueuedTransformCannotReplaceItsChildListBeforeAVetoSettles()
    {
        TransformBase parent = Node().Transform;
        TransformBase original = Node().Transform;
        TransformBase pending = Node().Transform;
        parent.Children.Add(original);
        EventList<TransformBase> previous = parent.Children;
        Func<XRObjectBase, bool> vetoDestroy = _ => false;
        parent.Destroying += vetoDestroy;
        _detachObservers.Add(() => parent.Destroying -= vetoDestroy);
        parent.Destroy(false);
        Assert.That(original.CaptureHierarchyListMemberships().Single().IsCurrent, Is.True);
        Assert.Throws<InvalidOperationException>(() => parent.Children = new([pending]));
        Assert.That(parent.Children, Is.SameAs(previous));
        Assert.That(Count(original, previous), Is.EqualTo(1));
        Assert.That(pending.CaptureHierarchyListMemberships(), Is.Empty);
        XRObjectBase.ProcessPendingDestructions();
        Assert.That(parent.IsDestroyed, Is.False);
        Assert.That(parent.IsDestroyQueued, Is.False);
        Assert.That(original.CaptureHierarchyListMemberships().Single().IsCurrent, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedPropertyReplacementKeepsTheAlreadyCommittedAuthority(bool afterFieldWrite)
    {
        TransformBase parent = Node().Transform;
        TransformBase pending = Node().Transform;
        TransformBase newer = Node().Transform;
        bool replacing = false;
        void Replace()
        {
            if (replacing) return;
            replacing = true;
            parent.Children = new([newer]);
        }
        XRPropertyChangingEventHandler before = (_, change) =>
        {
            if (change.PropertyName == nameof(TransformBase.Children)) Replace();
        };
        XRPropertyChangedEventHandler after = (_, change) =>
        {
            if (change.PropertyName == nameof(TransformBase.Children)) Replace();
        };
        if (afterFieldWrite) parent.PropertyChanged += after;
        else parent.PropertyChanging += before;
        _detachObservers.Add(() => { parent.PropertyChanging -= before; parent.PropertyChanged -= after; });
        Assert.Throws<InvalidOperationException>(() => parent.Children = new([pending]));
        Assert.That(replacing, Is.True);
        Assert.That(parent.Children.Count, Is.EqualTo(afterFieldWrite ? 1 : 0));
        if (afterFieldWrite)
            Assert.That(parent.Children.Single(), Is.SameAs(pending));
        Assert.That(Count(pending, parent.Children), Is.EqualTo(afterFieldWrite ? 1 : 0));
        Assert.That(newer.CaptureHierarchyListMemberships(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StaleListCallbacksCannotChangeTheReplacementOwner(bool remove)
    {
        TransformBase parent = Node().Transform;
        TransformBase child = Node().Transform;
        EventList<TransformBase> previous = parent.Children;
        if (remove) previous.Add(child);
        EventList<TransformBase>.SingleHandler handler = _ => parent.Children = new(remove ? [child] : []);
        if (remove) previous.PostAnythingRemoved += handler;
        else previous.PostAnythingAdded += handler;
        _detachObservers.Add(() => { previous.PostAnythingRemoved -= handler; previous.PostAnythingAdded -= handler; });
        // Cooked restore reattaches canonical handlers after observers already on the list.
        typeof(TransformBase).GetMethod("AttachChildListHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(parent, null);
        if (remove) previous.Remove(child);
        else previous.Add(child);
        Assert.That(ReferenceEquals(child.Parent, parent), Is.EqualTo(remove));
        Assert.That(Count(child, parent.Children), Is.EqualTo(remove ? 1 : 0));
        Assert.That(Count(child, previous), Is.Zero);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ChildPrechangeReentryPreservesTheForeignParentInTheAdmittedWorld(bool attach, bool duringWorld)
    {
        SceneNode owner = Node();
        SceneNode foreign = Node();
        SceneNode child = Node();
        HierarchyAuthorityWorldContext world = new();
        owner.SetWorldContext(world);
        foreign.SetWorldContext(world);
        if (!attach) child.Parent = owner;
        bool moved = false;
        XRPropertyChangingEventHandler handler = (_, change) =>
        {
            if (moved || change.PropertyName != (duringWorld ? nameof(TransformBase.World) : nameof(TransformBase.Parent)))
                return;
            moved = true;
            child.Transform.Parent = foreign.Transform;
        };
        child.Transform.PropertyChanging += handler;
        _detachObservers.Add(() => child.Transform.PropertyChanging -= handler);

        Assert.Throws<InvalidOperationException>(() => owner.Transform.Children = new(attach ? [child.Transform] : []));
        Assert.That(moved, Is.True);
        Assert.That(child.Transform.Parent, Is.SameAs(foreign.Transform));
        Assert.That(child.World, Is.SameAs(world));
        Assert.That(child.Transform.World, Is.SameAs(world));
        Assert.That(foreign.Transform.Children.Single(), Is.SameAs(child.Transform));
        Assert.That(Count(child.Transform, foreign.Transform.Children), Is.EqualTo(1));
    }

    [Test]
    public void DifferentWorldMoveStaysRejectedDuringChildReplacement()
    {
        SceneNode owner = Node();
        SceneNode foreign = Node();
        SceneNode child = Node();
        HierarchyAuthorityWorldContext originalWorld = new();
        HierarchyAuthorityWorldContext otherWorld = new();
        owner.SetWorldContext(originalWorld);
        foreign.SetWorldContext(otherWorld);
        child.Parent = owner;
        bool attempted = false;
        XRPropertyChangingEventHandler handler = (_, change) =>
        {
            if (attempted || change.PropertyName != nameof(TransformBase.Parent)) return;
            attempted = true;
            Assert.Throws<InvalidOperationException>(() => child.Transform.Parent = foreign.Transform);
        };
        child.Transform.PropertyChanging += handler;
        _detachObservers.Add(() => child.Transform.PropertyChanging -= handler);
        owner.Transform.Children = new();
        Assert.That(attempted, Is.True);
        Assert.That(child.Parent, Is.Null);
        Assert.That(child.World, Is.Null);
        Assert.That(foreign.World, Is.SameAs(otherWorld));
        Assert.That(foreign.Transform.Children.Count, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ComponentDestructionSettlesWhenTheOwnerIsQueuedOrAlreadyDestroyed(bool queueOwner)
    {
        SceneNode node = Node();
        HierarchyAuthorityComponentProbe component = node.AddComponent(static () => new HierarchyAuthorityComponentProbe())!;
        if (queueOwner)
        {
            node.Destroy(false);
            Assert.DoesNotThrow(() => component.Destroy(true));
        }
        else
            node.Destroy(true);
        Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
        Assert.That(node.IsDestroyed, Is.True);
        Assert.That(component.IsDestroyed, Is.True);
        Assert.That(component.CaptureHierarchyListMemberships(), Is.Empty);
    }

    [Test]
    public void ComponentRemovalSettlesBeforeQueuedOwnerDestructionIsVetoed()
    {
        SceneNode node = Node();
        HierarchyAuthorityComponentProbe component = node.AddComponent(static () => new HierarchyAuthorityComponentProbe())!;
        EventList<XRComponent> list = (EventList<XRComponent>)node.Components;
        EventList<XRComponent>.SingleCancelableHandler vetoRemoval = _ => false;
        list.PreRemoved += vetoRemoval;
        _detachObservers.Add(() => list.PreRemoved -= vetoRemoval);
        Func<XRObjectBase, bool> vetoDestroy = _ => false;
        node.Destroying += vetoDestroy;
        _detachObservers.Add(() => node.Destroying -= vetoDestroy);
        node.Destroy(false);
        Assert.Throws<InvalidOperationException>(() => list.Add(component, false, false));
        Assert.DoesNotThrow(() => component.Destroy(true));
        Assert.That(list.Count, Is.Zero);
        Assert.That(component.CaptureHierarchyListMemberships(), Is.Empty);
        Assert.DoesNotThrow(XRObjectBase.ProcessPendingDestructions);
        Assert.That(node.IsDestroyed, Is.False);
        Assert.That(node.IsDestroyQueued, Is.False);
        Assert.That(list.Count, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedRuntimeWorldBindingKeepsOnlyCommittedRootOwnership(bool failRollback)
    {
        SceneNode node = Node();
        RootNodeCollection roots = new(new HierarchyAuthorityWorldContext());
        XRPropertyChangingEventHandler before = (_, change) =>
        {
            if (change.PropertyName == nameof(SceneNode.World) && !failRollback)
                throw new InvalidOperationException("Expected pre-write bind failure.");
        };
        XRPropertyChangedEventHandler after = (_, change) =>
        {
            if (change.PropertyName == nameof(SceneNode.World) && failRollback)
                throw new InvalidOperationException("Expected post-write bind and rollback failure.");
        };
        node.PropertyChanging += before;
        node.PropertyChanged += after;
        try
        {
            Assert.Catch<Exception>(() => roots.Add(node));
            Assert.That(roots.Count, Is.EqualTo(failRollback ? 1 : 0));
            Assert.That(node.CaptureRuntimeRootMemberships().Length, Is.EqualTo(failRollback ? 1 : 0));
            Assert.That(node.World, Is.Null);
        }
        finally
        {
            node.PropertyChanging -= before;
            node.PropertyChanged -= after;
            roots.Remove(node);
        }
        Assert.That(node.CaptureRuntimeRootMemberships(), Is.Empty);
    }

    private SceneNode Node()
    {
        SceneNode node = new("Authority node");
        _nodes.Add(node);
        return node;
    }

    private static int Count(XRWorldObjectBase value, object list)
        => value.CaptureHierarchyListMemberships()
            .Where(owner => ReferenceEquals(owner.Collection, list)).Sum(owner => owner.Count);

    private static IDisposable? TryReserve(params XRWorldObjectBase[] values)
        => (IDisposable?)AcquireReservation.Invoke(null, [values]);
}
