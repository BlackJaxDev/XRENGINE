using XREngine.Scene.Transforms;
using XREngine.Scene;

namespace XREngine.Components;

public sealed partial class PhysicsChainWorld
{
    private readonly record struct TransformWitness(
        TransformBase Transform,
        TransformBase? Parent,
        IRuntimeWorldContext? World);

    private readonly record struct ComponentWitness(
        PhysicsChainComponent Component,
        IRuntimeWorldContext? World);

    private readonly record struct WorldObjectWitness(
        XRWorldObjectBase Object,
        IRuntimeWorldContext? World);

    private sealed class MutationSnapshot
    {
        public readonly List<TransformWitness> Transforms = [];
        public readonly List<ComponentWitness> Components = [];
        public readonly List<WorldObjectWitness> Objects = [];
        public readonly HashSet<TransformBase> SeenTransforms =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public readonly HashSet<PhysicsChainComponent> SeenComponents =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        public readonly HashSet<XRWorldObjectBase> SeenObjects =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    }

    internal sealed class WorldMutationLease : IDisposable
    {
        private readonly PhysicsChainWorld? _source;
        private readonly PhysicsChainWorld? _previousContext;
        private readonly List<PhysicsChainComponent> _participants;
        private readonly HashSet<XRWorldObjectBase> _allowedObjects;
        private readonly Dictionary<TransformBase, HashSet<TransformBase>> _allowedChildLists;
        private bool _allowChildDetach;
        private int _depth = 1;

        public IRuntimeWorldContext? SourceWorld { get; }
        public IRuntimeWorldContext? TargetWorld { get; }
        public PhysicsChainWorld? SourceScheduler => _source;

        public WorldMutationLease(
            IRuntimeWorldContext? sourceWorld,
            IRuntimeWorldContext? targetWorld,
            PhysicsChainWorld? source,
            PhysicsChainWorld? previousContext,
            List<PhysicsChainComponent> participants,
            HashSet<XRWorldObjectBase> allowedObjects,
            Dictionary<TransformBase, HashSet<TransformBase>> allowedChildLists,
            bool allowChildDetach)
        {
            SourceWorld = sourceWorld;
            TargetWorld = targetWorld;
            _source = source;
            _previousContext = previousContext;
            _participants = participants;
            _allowedObjects = allowedObjects;
            _allowedChildLists = allowedChildLists;
            _allowChildDetach = allowChildDetach;
        }

        public WorldMutationLease Retain()
        {
            ++_depth;
            return this;
        }

        public bool AllowsWorldChange(
            XRWorldObjectBase target,
            IRuntimeWorldContext? newWorld)
            => _allowedObjects.Contains(target)
                && (ReferenceEquals(newWorld, TargetWorld)
                    || (_allowChildDetach && newWorld is null));

        public bool AllowsChildListChange(TransformBase parent, TransformBase child)
            => _allowedChildLists.TryGetValue(parent, out HashSet<TransformBase>? children)
                && children.Contains(child);

        public bool AllowsLifecycleChange(XRWorldObjectBase target)
            => _allowedObjects.Contains(target);

        public void AllowChildDetachment() => _allowChildDetach = true;

        public void AdmitDetachedObject(XRWorldObjectBase target, bool requireOwnerless = false)
        {
            if (target.World is not null)
                throw new InvalidOperationException(
                    "Complete the current scene mutation before changing another attached object.");
            MutationSnapshot snapshot = CaptureMutationSnapshot(target, null);
            for (int i = 0; i < snapshot.Objects.Count; ++i)
            {
                IRuntimeWorldContext? world = snapshot.Objects[i].World;
                if (world is not null && (requireOwnerless
                    || (!ReferenceEquals(world, SourceWorld)
                        && !ReferenceEquals(world, TargetWorld))))
                    throw new InvalidOperationException(
                        "The detached object references another runtime world.");
            }
            for (int i = 0; i < snapshot.Components.Count; ++i)
            {
                PhysicsChainWorld? owner = snapshot.Components[i].Component.CaptureRuntimeOwner();
                if (owner is not null && (requireOwnerless || !ReferenceEquals(owner, _source)))
                    throw new InvalidOperationException(
                        "The detached object has a physics chain owned by another world.");
            }
            for (int i = 0; i < snapshot.Objects.Count; ++i)
                _allowedObjects.Add(snapshot.Objects[i].Object);
            for (int i = 0; i < snapshot.Components.Count; ++i)
                AddParticipant(snapshot.Components[i].Component);
            for (int i = 0; i < snapshot.Transforms.Count; ++i)
            {
                TransformWitness witness = snapshot.Transforms[i];
                if (witness.Parent is not null)
                    AddAllowedChild(_allowedChildLists, witness.Parent, witness.Transform);
            }
        }

        public void AdmitParentChange(TransformBase child, TransformBase? newParent)
        {
            if (newParent is not null)
                AddAllowedChild(_allowedChildLists, newParent, child);
        }

        public void AddParticipant(PhysicsChainComponent component)
        {
            for (int i = 0; i < _participants.Count; ++i)
                if (ReferenceEquals(_participants[i], component))
                    return;
            PhysicsChainWorld? owner = component.CaptureRuntimeOwner();
            if (owner is not null && !ReferenceEquals(owner, _source))
                throw new InvalidOperationException(
                    "The physics chain belongs to another source world. Complete its transfer before this mutation.");
            component.BeginWorldMutation();
            _participants.Add(component);
            _allowedObjects.Add(component);
        }

        public void Dispose()
        {
            if (--_depth != 0)
                return;

            s_activeWorldMutation = null;
            bool disposeOwner = false;
            if (_source is { } source)
            {
                try { disposeOwner = source.CompleteTick(); }
                finally { source._tickGate.Exit(); }
            }
            s_outerTickWorld = _previousContext;
            for (int i = 0; i < _participants.Count; ++i)
                _participants[i].EndWorldMutation();
            WorldMutationGate.ExitWriteLock();

            // Registration runs after both gates open. A destination tick can now
            // admit the complete graph, including commands queued by callbacks.
            ReplayMutationRegistrations(_participants);
            if (_previousContext is null)
                _source?.DrainDeferredTransfers();
            // A disposal requested by a lifecycle callback runs after every gate opens.
            if (disposeOwner)
                _source!.ScheduleOwnerDisposal();
        }
    }

    private static readonly ReaderWriterLockSlim WorldMutationGate =
        new(LockRecursionPolicy.SupportsRecursion);
    [ThreadStatic] private static WorldMutationLease? s_activeWorldMutation;
    [ThreadStatic] private static int s_hierarchyReadCallbackDepth;

    internal static bool IsWorldMutationActive => s_activeWorldMutation is not null;
    internal static bool RequiresSequentialHierarchyEvaluation
        => s_activeWorldMutation is not null || s_outerTickWorld is not null;

    private readonly ref struct TickAdmissionScope
    {
        public TickAdmissionScope(bool rejectNestedTick)
        {
            if (s_activeWorldMutation is not null ||
                (rejectNestedTick && s_outerTickWorld is not null))
                throw new InvalidOperationException(
                    "Schedule physics ticks after the current physics callback or scene mutation completes.");
            WorldMutationGate.EnterReadLock();
        }

        public void Dispose() => WorldMutationGate.ExitReadLock();
    }

    internal readonly ref struct HierarchyReadCallbackScope
    {
        internal HierarchyReadCallbackScope(bool enter) => ++s_hierarchyReadCallbackDepth;
        public void Dispose() => --s_hierarchyReadCallbackDepth;
    }

    internal static HierarchyReadCallbackScope EnterHierarchyReadCallback()
        => new(true);

    /// <summary>
    /// Holds the source world idle while a World setter updates a scene subtree.
    /// </summary>
    internal static WorldMutationLease? BeginWorldObjectMutation(
        XRWorldObjectBase target,
        IRuntimeWorldContext? oldWorld,
        IRuntimeWorldContext? newWorld)
    {
        if (ReferenceEquals(oldWorld, newWorld))
            return null;
        if (s_activeWorldMutation is { } active)
        {
            if (!active.AllowsLifecycleChange(target) && oldWorld is null
                && ReferenceEquals(newWorld, active.TargetWorld))
                active.AdmitDetachedObject(target);
            if (!active.AllowsWorldChange(target, newWorld))
                throw new InvalidOperationException(
                    "Complete the current world transfer before changing another world.");
            return active.Retain();
        }
        return BeginMutation(target, oldWorld, newWorld, null);
    }

    /// <summary>
    /// Starts before Parent changes any child list or field.
    /// </summary>
    internal static WorldMutationLease? BeginHierarchyMutation(
        TransformBase transform,
        IRuntimeWorldContext? targetWorld,
        TransformBase? newParent = null)
    {
        if (s_activeWorldMutation is { } active)
        {
            bool detachedMutation = targetWorld is null && transform.World is null
                && (newParent is null || newParent.World is null);
            if (detachedMutation)
            {
                // Cold callbacks can construct an ownerless subtree before they
                // attach it to the admitted destination world.
                active.AdmitDetachedObject(transform, requireOwnerless: true);
                if (newParent is not null)
                    active.AdmitDetachedObject(newParent, requireOwnerless: true);
            }
            if (!active.AllowsLifecycleChange(transform) && transform.World is null
                && ReferenceEquals(targetWorld, active.TargetWorld))
                active.AdmitDetachedObject(transform);
            if (!active.AllowsLifecycleChange(transform)
                || (!detachedMutation && !ReferenceEquals(targetWorld, active.TargetWorld)
                    && !(newParent is null && ReferenceEquals(targetWorld, transform.World))))
                throw new InvalidOperationException(
                    "Complete the current world transfer before changing another hierarchy.");
            active.AdmitParentChange(transform, newParent);
            return active.Retain();
        }
        RejectHeldChildList(transform);
        if (newParent is not null)
            RejectHeldChildList(newParent);
        return BeginMutation(transform, transform.World, targetWorld, null,
            newParent: newParent);
    }

    /// <summary>
    /// Starts before Children changes either child list.
    /// </summary>
    internal static WorldMutationLease? BeginChildrenMutation(
        TransformBase transform,
        IEnumerable<TransformBase> incoming)
    {
        if (s_activeWorldMutation is not null)
            throw new InvalidOperationException(
                "Complete the current world transfer before replacing another child list.");
        RejectHeldChildList(transform);
        return BeginMutation(transform, transform.World, transform.World, incoming, allowChildDetach: true);
    }

    internal static WorldMutationLease? BeginNodeActivationMutation(SceneNode node)
    {
        if (s_activeWorldMutation is { } active)
        {
            if (!active.AllowsLifecycleChange(node) && node.World is null)
                active.AdmitDetachedObject(node, requireOwnerless: true);
            if (!active.AllowsLifecycleChange(node))
                throw new InvalidOperationException(
                    "Complete the current scene mutation before changing another node's activation.");
            return active.Retain();
        }
        return BeginMutation(node, node.World, node.World, null);
    }

    internal static WorldMutationLease? BeginTransformDestructionMutation(TransformBase transform)
    {
        if (s_activeWorldMutation is { } active)
        {
            if (!active.AllowsLifecycleChange(transform))
                throw new InvalidOperationException(
                    "Complete the current scene mutation before destroying another transform.");
            active.AllowChildDetachment();
            return active.Retain();
        }
        return BeginMutation(transform, transform.World, transform.World, null, allowChildDetach: true);
    }

    /// <summary>Rejects direct attached child-list edits before the list changes.</summary>
    internal static bool ValidateChildListMutation(TransformBase parent, TransformBase child)
    {
        if (s_activeWorldMutation is { } active && active.AllowsChildListChange(parent, child))
            return true;
        if (s_activeWorldMutation is null && parent.World is null && child.World is null
            && s_outerTickWorld is null && s_hierarchyReadCallbackDepth == 0)
            return true;
        throw new InvalidOperationException(
            "Use the Parent or Children setter outside physics callbacks to change an attached hierarchy.");
    }

    /// <summary>
    /// Keeps a component graph idle during activation or deactivation.
    /// </summary>
    internal static WorldMutationLease? BeginComponentMutation(PhysicsChainComponent component)
    {
        if (s_activeWorldMutation is { } active)
        {
            active.AddParticipant(component);
            return active.Retain();
        }
        return BeginMutation(component, component.World, component.World, null);
    }

    /// <summary>
    /// Removes an exact source slot before deactivation writes rest transforms.
    /// </summary>
    internal static void RetireForComponentMutation(PhysicsChainComponent component)
    {
        WorldMutationLease? lease = s_activeWorldMutation;
        if (lease is null)
            throw new InvalidOperationException("The physics chain mutation has no world lease.");
        component.CaptureRuntimeBinding(out PhysicsChainWorld? owner, out PhysicsChainRuntimeHandle handle);
        if (owner is null)
            return;
        if (!ReferenceEquals(owner, lease.SourceScheduler))
            throw new InvalidOperationException("The physics chain source world changed during deactivation.");
        if (!owner._slotByComponent.TryGetValue(component, out int slotIndex)
            || slotIndex != handle.Slot
            || !ReferenceEquals(owner._slots[slotIndex].Component, component)
            || owner._slots[slotIndex].Generation != handle.Generation)
            throw new InvalidOperationException("The physics chain source slot changed during deactivation.");
        owner.RemoveComponent(component, registerCurrentWorld: false);
    }

    private static WorldMutationLease? BeginMutation(
        XRWorldObjectBase target,
        IRuntimeWorldContext? oldWorld,
        IRuntimeWorldContext? newWorld,
        IEnumerable<TransformBase>? incoming,
        bool allowChildDetach = false,
        TransformBase? newParent = null)
    {
        if (s_outerTickWorld is not null || s_hierarchyReadCallbackDepth != 0)
            throw new InvalidOperationException(
                "Schedule the world or hierarchy mutation after physics callbacks complete.");

        MutationSnapshot before = CaptureMutationSnapshot(target, incoming);
        for (int i = 0; i < before.Transforms.Count; ++i)
            RejectHeldChildList(before.Transforms[i].Transform);
        WorldMutationGate.EnterWriteLock();
        List<PhysicsChainComponent>? marked = null;
        PhysicsChainWorld? source = null;
        bool sourceGateEntered = false;
        try
        {
            if (!ReferenceEquals(target.World, oldWorld)
                || !SnapshotsMatch(before, CaptureMutationSnapshot(target, incoming)))
                throw new InvalidOperationException(
                    "The scene hierarchy changed before physics mutation admission. Retry the mutation.");
            IRuntimeWorldContext? sourceWorld = oldWorld;
            for (int i = 0; i < before.Components.Count; ++i)
            {
                IRuntimeWorldContext? candidate = before.Components[i].World;
                if (candidate is null)
                    continue;
                if (sourceWorld is null)
                    sourceWorld = candidate;
                else if (!ReferenceEquals(sourceWorld, candidate))
                    throw new InvalidOperationException(
                        "A hierarchy mutation cannot move physics chains from more than one source world.");
            }
            if (sourceWorld is not null)
                TryGet(sourceWorld, out source);

            marked = new List<PhysicsChainComponent>(before.Components.Count);
            for (int i = 0; i < before.Components.Count; ++i)
            {
                PhysicsChainComponent component = before.Components[i].Component;
                PhysicsChainWorld? owner = component.CaptureRuntimeOwner();
                if (owner is not null && !ReferenceEquals(owner, source))
                    throw new InvalidOperationException(
                        "The physics chain belongs to another source world. Complete its transfer before this mutation.");
                component.BeginWorldMutation();
                marked.Add(component);
            }

            source?._tickGate.Enter();
            sourceGateEntered = source is not null;
            // Count the lease as a tick, so a disposal request inside it waits until it ends.
            if (sourceGateEntered)
                ++source!._tickDepth;
            if (!ReferenceEquals(target.World, oldWorld)
                || !SnapshotsMatch(before, CaptureMutationSnapshot(target, incoming)))
                throw new InvalidOperationException(
                    "The scene hierarchy changed while the physics chain world became idle. Retry the mutation.");
            for (int i = 0; i < marked.Count; ++i)
            {
                PhysicsChainWorld? owner = marked[i].CaptureRuntimeOwner();
                if (owner is not null && !ReferenceEquals(owner, source))
                    throw new InvalidOperationException("The physics chain source owner changed during the mutation.");
            }

            PhysicsChainWorld? previousContext = s_outerTickWorld;
            var allowed = new HashSet<XRWorldObjectBase>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance) { target };
            for (int i = 0; i < before.Objects.Count; ++i)
                allowed.Add(before.Objects[i].Object);
            var allowedLists = new Dictionary<TransformBase, HashSet<TransformBase>>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
            for (int i = 0; i < before.Transforms.Count; ++i)
            {
                TransformWitness witness = before.Transforms[i];
                if (witness.Parent is not null)
                    AddAllowedChild(allowedLists, witness.Parent, witness.Transform);
            }
            if (newParent is not null && target is TransformBase movedTransform)
                AddAllowedChild(allowedLists, newParent, movedTransform);
            if (incoming is not null && target is TransformBase childListOwner)
                foreach (TransformBase child in incoming)
                    if (child is not null)
                        AddAllowedChild(allowedLists, childListOwner, child);
            var lease = new WorldMutationLease(
                sourceWorld, newWorld, source, previousContext, marked, allowed,
                allowedLists, allowChildDetach);
            s_outerTickWorld = source;
            s_activeWorldMutation = lease;
            return lease;
        }
        catch
        {
            if (sourceGateEntered)
            {
                --source!._tickDepth;
                source._tickGate.Exit();
            }
            if (marked is not null)
                for (int i = 0; i < marked.Count; ++i)
                    marked[i].EndWorldMutation();
            WorldMutationGate.ExitWriteLock();
            if (marked is not null)
                ReplayMutationRegistrations(marked);
            throw;
        }
    }

    private static void RejectHeldChildList(TransformBase transform)
    {
        if (Monitor.IsEntered(transform.Children)
            || (transform.Parent is { } parent && Monitor.IsEntered(parent.Children)))
            throw new InvalidOperationException(
                "Release hierarchy-list locks before changing the scene hierarchy.");
    }

    private static void AddAllowedChild(
        Dictionary<TransformBase, HashSet<TransformBase>> lists,
        TransformBase parent, TransformBase child)
    {
        if (!lists.TryGetValue(parent, out HashSet<TransformBase>? children))
            lists.Add(parent, children = new HashSet<TransformBase>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance));
        children.Add(child);
    }

    private static void ReplayMutationRegistrations(List<PhysicsChainComponent> participants)
    {
        for (int i = 0; i < participants.Count; ++i)
        {
            PhysicsChainComponent component = participants[i];
            if (component.IsDestroyed || !component.IsComponentActivated
                || !component.IsActiveInHierarchy || component.World is null)
                continue;
            try { Register(component); }
            catch (Exception ex)
            {
                Debug.PhysicsWarning($"[PhysicsChain] Scene mutation registration failed: {ex}");
            }
        }
    }

    private static MutationSnapshot CaptureMutationSnapshot(
        XRWorldObjectBase target,
        IEnumerable<TransformBase>? incoming)
    {
        var snapshot = new MutationSnapshot();
        AddWorldObject(snapshot, target);
        if (target is PhysicsChainComponent component)
            AddParticipant(snapshot, component);
        else if (target is SceneNode node)
            AddTransform(snapshot, node.Transform);
        else if (target is TransformBase transform)
            AddTransform(snapshot, transform);
        if (incoming is not null)
            foreach (TransformBase child in incoming)
                if (child is not null)
                    AddTransform(snapshot, child);
        return snapshot;
    }

    private static void AddParticipant(MutationSnapshot snapshot, PhysicsChainComponent component)
    {
        AddWorldObject(snapshot, component);
        if (snapshot.SeenComponents.Add(component))
            snapshot.Components.Add(new ComponentWitness(component, component.World));
    }

    private static void AddWorldObject(MutationSnapshot snapshot, XRWorldObjectBase target)
    {
        if (snapshot.SeenObjects.Add(target))
            snapshot.Objects.Add(new WorldObjectWitness(target, target.World));
    }

    private static void AddTransform(MutationSnapshot snapshot, TransformBase transform)
    {
        if (!snapshot.SeenTransforms.Add(transform))
            return;
        AddWorldObject(snapshot, transform);
        snapshot.Transforms.Add(new TransformWitness(transform, transform.Parent, transform.World));
        if (transform.SceneNode is SceneNode node)
        {
            AddWorldObject(snapshot, node);
            var components = node.Components;
            for (int i = 0; i < components.Count; ++i)
            {
                AddWorldObject(snapshot, components[i]);
                if (components[i] is PhysicsChainComponent component)
                    AddParticipant(snapshot, component);
            }
        }
        var children = transform.Children;
        var childCopy = new List<TransformBase>();
        lock (children)
            for (int i = 0; i < children.Count; ++i)
                if (children[i] is TransformBase child)
                    childCopy.Add(child);
        for (int i = 0; i < childCopy.Count; ++i)
            AddTransform(snapshot, childCopy[i]);
    }

    private static bool SnapshotsMatch(MutationSnapshot before, MutationSnapshot after)
    {
        if (before.Transforms.Count != after.Transforms.Count
            || before.Components.Count != after.Components.Count
            || before.Objects.Count != after.Objects.Count)
            return false;
        for (int i = 0; i < before.Transforms.Count; ++i)
            if (!ReferenceEquals(before.Transforms[i].Transform, after.Transforms[i].Transform)
                || !ReferenceEquals(before.Transforms[i].Parent, after.Transforms[i].Parent)
                || !ReferenceEquals(before.Transforms[i].World, after.Transforms[i].World))
                return false;
        for (int i = 0; i < before.Components.Count; ++i)
            if (!ReferenceEquals(before.Components[i].Component, after.Components[i].Component)
                || !ReferenceEquals(before.Components[i].World, after.Components[i].World))
                return false;
        for (int i = 0; i < before.Objects.Count; ++i)
            if (!ReferenceEquals(before.Objects[i].Object, after.Objects[i].Object)
                || !ReferenceEquals(before.Objects[i].World, after.Objects[i].World))
                return false;
        return true;
    }
}
