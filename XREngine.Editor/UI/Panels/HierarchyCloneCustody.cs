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
    private ObjectConstructionResult<IReadOnlyList<SceneNode>>? _forestConstruction;
    private IReadOnlyList<SceneNode>? _singleNodes;
    private OwnedHierarchyLifetime? _lifetime;
    private OwnedHierarchyLifetime? _documentRetirement;
    private ObjectConstructionOwnership? _documentRetirementOwnership;
    private IReadOnlyList<ObjectConstructionIdentity>? _identities;
    private HierarchyLifetimeAdmission? _initialAdmission;
    private bool _historyAttached;
    private HierarchyCloneSelection? _selection;
    private HierarchyDuplicateSource[]? _operationShapes;

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
    internal ObjectConstructionOwnership? RetainedOwnership =>
        _forestConstruction?.Ownership ?? _construction?.Ownership;
    internal OwnedHierarchyLifetime? Lifetime => _lifetime;
    internal OwnedHierarchyLifetime? DocumentRetirement => _documentRetirement;
    internal ObjectConstructionOwnership? DocumentRetirementOwnership => _documentRetirementOwnership;
    internal SceneNode? Node => _construction?.Value ?? (_singleNodes is { Count: 1 } nodes ? nodes[0] : null);
    internal IReadOnlyList<SceneNode>? Nodes => _forestConstruction?.Value ?? _singleNodes;
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
    internal void SelectApplied()
    {
        if (Nodes is not { } nodes)
            throw new InvalidOperationException("The duplicate has no complete clone roots.");
        _selection?.SelectMany(nodes);
    }

    internal SceneNode Clone(SceneNode source,
        Action<SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSource = null)
    {
        _store.RequireOwnerThread();
        if (State != EHierarchyCloneCustodyState.Reserved || RetainedOwnership is not null)
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
        _singleNodes = [node];
        State = EHierarchyCloneCustodyState.Detached;
        return node;
    }

    internal IReadOnlyList<SceneNode> CloneMany(IReadOnlyList<SceneNode> sources,
        Action<int, SceneNode, ObjectConstructionContext, Action>? configure = null,
        Action? requireSources = null)
    {
        _store.RequireOwnerThread();
        if (State != EHierarchyCloneCustodyState.Reserved || RetainedOwnership is not null ||
            sources.Count < 2)
            throw new InvalidOperationException("A grouped custody entry requires one forest construction.");
        _forestConstruction = SceneNodePrefabUtility.CloneHierarchiesOwned(sources,
            configure, requireSources);
        if (!_forestConstruction.Ownership.HasSealedIdentities)
            throw new InvalidOperationException("The clone forest could not seal its lifetime identities.");
        _identities = _forestConstruction.Ownership.Identities;
        _lifetime = new(_forestConstruction.Ownership, _forestConstruction.Failure);
        if (_forestConstruction.Failure is { } failure)
        {
            RetainFailure(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (_forestConstruction.Value is not { } nodes || nodes.Count != sources.Count)
            throw new InvalidOperationException("The clone forest returned no exact ordered roots.");
        State = EHierarchyCloneCustodyState.Detached;
        return nodes;
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

    internal void RequireOperation(IHierarchyMutationWork operation)
    {
        if (operation is HierarchyMutationOperation single)
        {
            RequireOperation(single);
            return;
        }
        RequireOperationIdentity();
        if (Nodes is not { } nodes || nodes.Count != operation.Nodes.Count)
            throw new InvalidOperationException("The clone forest does not match its batch operation.");
        for (int index = 0; index < nodes.Count; index++)
            if (!ReferenceEquals(nodes[index], operation.Nodes[index]))
                throw new InvalidOperationException("The clone forest changed order before its transaction.");
        if (operation is not HierarchyMutationBatchOperation batch)
            throw new InvalidOperationException("The clone forest requires its exact batch transaction.");
        foreach (HierarchyMutationOperation member in batch.Members)
            if (!ReferenceEquals(member.Before.World, Document.World) ||
                member.Before.Parent is not null || member.Before.Scenes.Length != 0 ||
                member.Before.InWorldRoots)
                throw new InvalidOperationException("A clone member did not begin detached in its document.");
        operation.RequireBefore(includeProperties: true);
    }

    internal void RequireOperationIdentity()
    {
        RequireIdentity();
        try
        {
            if (_operationShapes is null || Nodes is not { } nodes ||
                _operationShapes.Length != nodes.Count)
                throw new InvalidOperationException("The clone forest has no captured operation graph.");
            for (int index = 0; index < nodes.Count; index++)
            {
                _operationShapes[index].RequireCloneShape();
                Document.RequireOperationRoot(nodes[index]);
            }
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
        return HierarchyLifetimeAdmission.Acquire(RetainedOwnership!);
    }

    internal void BeginInitialAdmission()
    {
        if (_initialAdmission is not null)
            throw new InvalidOperationException("The initial clone edit already holds lifetime admission.");
        _initialAdmission = AcquireAdmission();
        _operationShapes = [.. Nodes!.Select(node => new HierarchyDuplicateSource(node))];
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
        if (RetainedOwnership is null || State is EHierarchyCloneCustodyState.RecoveryRequired or
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
            if (RetainedOwnership is not { } ownership)
            {
                State = EHierarchyCloneCustodyState.Released;
                _store.Settle(this);
                return;
            }
            RequireIdentity();
            if (State == EHierarchyCloneCustodyState.Applied)
            {
                State = EHierarchyCloneCustodyState.Retiring;
                using HierarchyCloneDocumentHandoff handoff = Document.PrepareHandoff(ownership);
                _documentRetirementOwnership = handoff.PrivateOwnership;
                _documentRetirement = new(handoff.PrivateOwnership);
                if (!_documentRetirement.TryRetire())
                    throw new InvalidOperationException("Private constructor storage could not be retired before document handoff.", _documentRetirement.Failure);
                handoff.RequireCurrent();
                // Authored scene roots already retain the live hierarchy. Keep only
                // its returned roots; no ledger or later document destruction authority remains.
                _singleNodes = Nodes;
                _construction = null;
                _forestConstruction = null;
                _identities = null;
                _operationShapes = null;
                _selection = null;
                _lifetime = null;
                _documentRetirementOwnership = null;
                _documentRetirement = null;
                State = EHierarchyCloneCustodyState.Document;
                _store.Settle(this);
                return;
            }
            if (State != EHierarchyCloneCustodyState.Detached)
                throw new InvalidOperationException("The clone has an incomplete operation and remains retained.", Failure);
            foreach (SceneNode selected in Selection.SceneNodes)
                if (ownership.Contains(selected))
                    throw new InvalidOperationException("The detached clone is still selected and remains retained.");
            State = EHierarchyCloneCustodyState.Retiring;
            using (OwnedHierarchyUndoReleaseScope release = new(ownership))
                if (_lifetime?.TryRetire() != true)
                    throw new InvalidOperationException("The detached clone could not be retired and remains retained.", _lifetime?.Failure);
            _construction = null;
            _forestConstruction = null;
            _singleNodes = null;
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
