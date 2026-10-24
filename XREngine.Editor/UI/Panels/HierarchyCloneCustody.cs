using System.Runtime.ExceptionServices;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor;

/// <summary>Retains one exact allocation receipt before any construction callback can run.</summary>
internal sealed class HierarchyCloneCustody
{
    private readonly HierarchyCloneCustodyStore _store;
    private ObjectConstructionResult<SceneNode>? _construction;
    private OwnedHierarchyLifetime? _lifetime;
    private IReadOnlyList<ObjectConstructionIdentity>? _identities;
    private HierarchyLifetimeAdmission? _initialAdmission;
    private bool _historyAttached;
    private HierarchyCloneSelection? _selection;
    private HierarchyDuplicateSource? _operationShape;

    internal HierarchyCloneCustody(HierarchyCloneCustodyStore store, HierarchyCloneDocument document)
    {
        _store = store;
        Document = document;
        Resource = new(ReleaseHistory);
    }

    internal HierarchyCloneDocument Document { get; }
    internal UndoHistoryResource Resource { get; }
    internal EHierarchyCloneCustodyState State { get; private set; }
    internal Exception? Failure { get; private set; }
    internal ObjectConstructionOwnership? RetainedOwnership => _construction?.Ownership;
    internal OwnedHierarchyLifetime? Lifetime => _lifetime;
    internal SceneNode? Node => _construction?.Value;
    internal bool BlocksConstruction => State is EHierarchyCloneCustodyState.Reserved or
        EHierarchyCloneCustodyState.Undoing or EHierarchyCloneCustodyState.Redoing or
        EHierarchyCloneCustodyState.Retiring or EHierarchyCloneCustodyState.RecoveryRequired;

    internal void AttachHistory(StructuralChangeHandle handle)
    {
        _store.RequireOwnerThread();
        handle.RetainResource(Resource);
        _historyAttached = true;
    }

    internal void CaptureSelection() => _selection = new();
    internal void SelectApplied() => _selection?.Select(Node!);

    internal SceneNode Clone(SceneNode source,
        Action<SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSource = null)
    {
        _store.RequireOwnerThread();
        if (State != EHierarchyCloneCustodyState.Reserved || _construction is not null)
            throw new InvalidOperationException("A clone custody entry permits only one construction.");
        // Store the result before allocating or calling another helper. Failed
        // construction keeps the exact ledger and its original exception.
        _construction = SceneNodePrefabUtility.CloneHierarchyOwned(source, configure, requireSource);
        if (!_construction.Ownership.HasSealedIdentities)
            throw new InvalidOperationException("The clone construction could not seal its lifetime identities.");
        _identities = _construction.Ownership.Identities;
        _lifetime = new(_construction.Ownership, _construction.Failure);
        if (_construction.Failure is { } failure)
        {
            RetainFailure(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (_construction.Value is not { } node)
            throw new InvalidOperationException("The clone construction returned no hierarchy.");
        State = EHierarchyCloneCustodyState.Detached;
        return node;
    }

    internal void RequireOperation(HierarchyMutationOperation operation)
    {
        RequireOperationIdentity();
        if (!ReferenceEquals(operation.Node, Node) ||
            !ReferenceEquals(operation.Before.World, Document.World) ||
            operation.Before.Parent is not null || operation.Before.Scenes.Length != 0 ||
            operation.Before.InWorldRoots)
            throw new InvalidOperationException("The clone mutation does not own its detached construction.");
        operation.RequireBefore(includeProperties: true);
    }

    internal void RequireOperationIdentity()
    {
        RequireIdentity();
        try
        {
            if (_operationShape is null)
                throw new InvalidOperationException("The clone has no captured operation graph.");
            _operationShape.RequireCloneShape();
            Document.RequireOperationRoot(Node!);
        }
        catch (Exception failure) { RetainFailure(failure); throw; }
    }

    internal void RequireIdentity()
    {
        _store.RequireOwnerThread();
        if (_identities is null)
            throw new InvalidOperationException("The clone has no completed identity receipt.");
        foreach (ObjectConstructionIdentity identity in _identities)
            if (!identity.IsCurrent)
            {
                InvalidOperationException failure = new("A clone allocation changed identity or lifetime after construction.");
                RetainFailure(failure);
                throw failure;
            }
    }

    internal HierarchyLifetimeAdmission AcquireAdmission()
    {
        _store.RequireOwnerThread();
        RequireUsable();
        return HierarchyLifetimeAdmission.Acquire(_construction!.Ownership);
    }

    internal void BeginInitialAdmission()
    {
        if (_initialAdmission is not null)
            throw new InvalidOperationException("The initial clone edit already holds lifetime admission.");
        _initialAdmission = AcquireAdmission();
        _operationShape = new(Node!);
    }

    internal void EndInitialAdmission()
    {
        HierarchyLifetimeAdmission? admission = _initialAdmission;
        _initialAdmission = null;
        admission?.Dispose();
    }

    internal void MarkApplied()
    {
        _store.RequireOwnerThread();
        RequireIdentity();
        RequireUsable();
        State = EHierarchyCloneCustodyState.Applied;
        SelectApplied();
    }

    internal void MarkDetached()
    {
        _store.RequireOwnerThread();
        RequireIdentity();
        RequireUsable();
        State = EHierarchyCloneCustodyState.Detached;
        _selection?.Restore();
    }

    internal void BeginUndo()
    {
        _store.RequireOwnerThread();
        RequireIdentity();
        if (State != EHierarchyCloneCustodyState.Applied)
            throw new InvalidOperationException("The clone has no complete applied state to undo.", Failure);
        State = EHierarchyCloneCustodyState.Undoing;
    }

    internal void BeginRedo()
    {
        _store.RequireOwnerThread();
        RequireIdentity();
        if (State != EHierarchyCloneCustodyState.Detached)
            throw new InvalidOperationException("The clone has no complete detached state to redo.", Failure);
        State = EHierarchyCloneCustodyState.Redoing;
    }

    internal void RetainFailure(Exception failure)
    {
        Failure ??= failure;
        State = EHierarchyCloneCustodyState.RecoveryRequired;
    }

    internal void ReleaseRestored(StructuralChangeHandle handle)
        => handle.ReleaseRestoredResource(Resource);

    internal void CompleteCommand()
    {
        try
        {
            EndInitialAdmission();
            if (!_historyAttached)
                ReleaseHistory();
        }
        finally { _store.CompleteCommand(this); }
    }

    private void RequireUsable()
    {
        if (_construction is null || State is EHierarchyCloneCustodyState.RecoveryRequired or
            EHierarchyCloneCustodyState.Document or EHierarchyCloneCustodyState.Retiring or
            EHierarchyCloneCustodyState.Released)
            throw new InvalidOperationException("The retained clone cannot accept another history state.", Failure);
    }

    private void ReleaseHistory()
    {
        try
        {
            _store.RequireOwnerThread();
            if (State == EHierarchyCloneCustodyState.Released || State == EHierarchyCloneCustodyState.Document)
                return;
            if (_construction is null)
            {
                State = EHierarchyCloneCustodyState.Released;
                _store.Settle(this);
                return;
            }
            RequireIdentity();
            if (State == EHierarchyCloneCustodyState.Applied)
            {
                Document.RequireCustody(_construction.Ownership);
                // The store remains the strong owner. This does not discard the
                // ledger or give document teardown permission to destroy assets.
                State = EHierarchyCloneCustodyState.Document;
                return;
            }
            if (State != EHierarchyCloneCustodyState.Detached)
                throw new InvalidOperationException("The clone has an incomplete operation and remains retained.", Failure);
            foreach (SceneNode selected in Selection.SceneNodes)
                if (_construction.Ownership.Contains(selected))
                    throw new InvalidOperationException("The detached clone is still selected and remains retained.");
            State = EHierarchyCloneCustodyState.Retiring;
            using (OwnedHierarchyUndoReleaseScope release = new(_construction.Ownership))
                if (_lifetime?.TryRetire() != true)
                    throw new InvalidOperationException("The detached clone could not be retired and remains retained.", _lifetime?.Failure);
            _construction = null;
            State = EHierarchyCloneCustodyState.Released;
            _store.Settle(this);
        }
        catch (Exception failure)
        {
            RetainFailure(failure);
            throw;
        }
    }
}
