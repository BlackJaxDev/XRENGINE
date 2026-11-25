using XREngine.Components;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Transforms;

public abstract partial class TransformBase
{
    private long _parentAssignmentRevision;
    private DetachedLeafAttachmentReceipt? _activeLeafAttachment;
    private long _activeParentAssignmentRevision;

    internal long ParentAssignmentRevision => Volatile.Read(ref _parentAssignmentRevision);

    private long AdvanceParentAssignmentRevision()
    {
        while (true)
        {
            long previous = Volatile.Read(ref _parentAssignmentRevision);
            if (previous == long.MaxValue)
                throw new InvalidOperationException("The parent assignment history is exhausted.");
            long next = previous + 1;
            if (Interlocked.CompareExchange(ref _parentAssignmentRevision, next, previous) == previous)
                return next;
        }
    }

    internal void AttachDetachedLeaf(DetachedLeafAttachmentReceipt receipt)
        => SetParentValue(receipt.Parent, attachment: receipt);

    internal void CompensateDetachedLeafParent(DetachedLeafAttachmentReceipt receipt,
        HierarchyStructuralReservation reservation)
    {
        receipt.RequireCompensation(this, reservation);
        EnterReservedHierarchyMutation(reservation);
        try
        {
            receipt.RequireCompensation(this, reservation);
            AdvanceParentAssignmentRevision();
            SetField<TransformBase?>(ref _parent, null, publishNotifications: false,
                propertyName: nameof(Parent));
            receipt.ConsumeCompensation();
        }
        finally { ExitStructuralMutation(); }
    }

    internal void ClearWorldForCanonicalUnlink(Action? validate)
        => SetWorldValue(null, validate);

    private void RequireChangedParent(TransformBase? expected)
    {
        if (!ReferenceEquals(_parent, expected))
            throw new InvalidOperationException("The parent changed during hierarchy callbacks.");
    }

    private void SetInheritedWorld(TransformBase parent, IRuntimeWorldContext world)
    {
        IRuntimeWorldContext? previousWorld = World;
        SetWorldValue(world, () =>
        {
            RequireChangedParent(parent);
            if (!ReferenceEquals(World, previousWorld))
                throw new InvalidOperationException("The world changed during hierarchy callbacks.");
        });
    }

    private void SetParentValue(TransformBase? value, Action? validate = null,
        DetachedLeafAttachmentReceipt? attachment = null)
    {
        long revision = AdvanceParentAssignmentRevision();
        DetachedLeafAttachmentReceipt? previousAttachment = _activeLeafAttachment;
        long previousRevision = _activeParentAssignmentRevision;
        _activeLeafAttachment = attachment;
        _activeParentAssignmentRevision = revision;
        try { SetParentValueCore(value, validate, attachment, revision); }
        finally
        {
            _activeLeafAttachment = previousAttachment;
            _activeParentAssignmentRevision = previousRevision;
        }
    }

    private void SetParentValueCore(TransformBase? value, Action? validate,
        DetachedLeafAttachmentReceipt? attachment, long revision)
    {
        using HierarchyMutationLease structural = new(this, _parent, value);
        attachment?.BeginAssignment(this, value, revision);
        validate?.Invoke();
        HierarchyConstructionScope.RequireLink(this, value);
        if (ReferenceEquals(_parent, value)) return;
        TransformBase? original = _parent;
        using var mutation = PhysicsChainWorld.BeginHierarchyMutation(this, value?.World ?? World, value);
        _parentAssignmentDepth++;
        try
        {
            validate?.Invoke();
            bool notify = !ArePropertyNotificationsSuppressed;
            if (notify && !OnPropertyChanging(nameof(Parent), original, value)) return;
            validate?.Invoke();
            HierarchyConstructionScope.RequireAssignment(this, original, _parent, value);
            if (notify)
            {
                for (TransformBase? ancestor = value; ancestor is not null; ancestor = ancestor.Parent)
                    if (ReferenceEquals(ancestor, this))
                        throw new InvalidOperationException("A transform cannot be parented beneath itself.");
                _parent?._children.Remove(this);
            }
            lock (SceneRootCollection.MutationGate)
            {
                validate?.Invoke();
                HierarchyConstructionScope.RequireAssignment(this, original, _parent, value);
                SetField(ref _parent, value, publishNotifications: false, propertyName: nameof(Parent));
                attachment?.MarkPointerCommitted(this, revision);
            }
            if (notify) OnPropertyChanged(nameof(Parent), original, value);
        }
        finally { --_parentAssignmentDepth; }
    }
}
