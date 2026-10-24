namespace XREngine.Data.Core;

/// <summary>
/// Defers object-cache discovery until a synchronous construction batch is complete.
/// </summary>
/// <remarks>
/// The scope is thread-affine by design. It must not cross an asynchronous boundary.
/// Nested scopes share one batch; an incomplete nested scope aborts the root batch.
/// </remarks>
public sealed class ObjectCachePublicationScope : IDisposable
{
    private readonly ObjectCachePublicationScope? _parent;
    private readonly ObjectCachePublicationBatch _batch;
    private readonly bool _ownsBatch;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _closed;

    internal ObjectCachePublicationScope(ObjectCachePublicationScope? parent, bool independentBatch = false,
        bool captureConstructions = false)
    {
        _parent = parent;
        _ownsBatch = parent is null || independentBatch;
        _batch = _ownsBatch ? new ObjectCachePublicationBatch(captureConstructions) : parent!._batch;
        XRObjectBase.CurrentObjectCachePublicationScope = this;
    }

    internal ObjectConstructionOwnership? ConstructionOwnership => _batch.ConstructionOwnership;
    internal ObjectConstructionIdentityGuard? ConstructionIdentityGuard => ConstructionOwnership?.IdentityGuard;

    internal void RecordConstruction(XRObjectBase value) => ConstructionOwnership?.Record(value);

    internal void RequireGeneratedOrigin(XRObjectBase value)
    {
        ObjectConstructionIdentityGuard.RequireOwningBatch(value, ConstructionIdentityGuard);
        if (ConstructionOwnership is { } ownership && !ownership.Contains(value))
            throw new InvalidOperationException("An owned construction cannot regenerate a borrowed object.");
        RequireConstructionIdentityChange(value);
    }

    internal void RequireConstructionIdentityChange(XRObjectBase value)
    {
        for (ObjectCachePublicationScope? scope = this; scope is not null; scope = scope._parent)
            if (scope._batch.IsPublishing && scope.ConstructionOwnership?.Contains(value) == true)
                throw new InvalidOperationException("An owned object's identity cannot change during cache publication.");
    }

    internal void Enlist(XRObjectBase value)
    {
        RequireGeneratedOrigin(value);
        _batch.Enlist(value);
    }

    internal bool TryDeferOwnedPublication(XRObjectBase value)
    {
        if (ConstructionOwnership is null)
            return false;
        Enlist(value);
        return true;
    }

    internal void RecordDestructionFailure(XRObjectBase value)
    {
        for (ObjectCachePublicationScope? scope = this; scope is not null; scope = scope._parent)
            if (scope._batch.Contains(value) || scope.ConstructionOwnership?.Contains(value) == true)
                scope._batch.IsAborted = true;
    }

    /// <summary>
    /// Completes this scope. The root scope publishes the entire batch only after every
    /// nested scope has completed successfully.
    /// </summary>
    public void Complete() => Complete(allowDestroyedMembers: false);

    private void Complete(bool allowDestroyedMembers)
    {
        EnsureCurrentThreadAndScope();

        if (!_ownsBatch)
        {
            if (_batch.IsAborted)
                throw new InvalidOperationException("A nested object-cache publication scope was aborted.");
            _closed = true;
            XRObjectBase.CurrentObjectCachePublicationScope = _parent;
            return;
        }

        if (_batch.IsAborted)
        {
            if (ConstructionOwnership is null)
                XRObjectBase.AbortDeferredObjectCachePublication(_batch.Objects);
            throw new InvalidOperationException("The object-cache publication batch was aborted.");
        }

        if (_batch.PublishedEntries is { } published)
        {
            _batch.IsPublishing = true;
            ConstructionOwnership!.BeginPublication();
            try
            {
                XRObjectBase.PublishOwnedObjectCacheBatch(_batch.Objects, published);
                EnsureCurrentThreadAndScope();
                if (_batch.IsAborted)
                    throw new InvalidOperationException("The owned construction failed during cache publication.");
            }
            finally { _batch.IsPublishing = false; }
        }
        else
            XRObjectBase.PublishDeferredObjectCacheBatch(_batch.Objects, allowDestroyedMembers);
        _closed = true;
        XRObjectBase.CurrentObjectCachePublicationScope = _parent;
    }

    /// <summary>
    /// Publishes an independent batch and transfers its enlisted objects to a lifetime owner.
    /// The caller must not regenerate borrowed objects in this batch. Use ConstructOwned
    /// when the caller needs constructor-origin proof and retained failure ownership.
    /// Fully destroyed temporary allocations are omitted from publication; a failed
    /// destruction aborts the batch rather than publishing a partially cleaned object.
    /// </summary>
    public ObjectCacheOwnership CompleteWithOwnership()
    {
        EnsureCurrentThreadAndScope();
        if (!_ownsBatch)
            throw new InvalidOperationException("Only an independent publication batch can transfer object ownership.");
        Complete(allowDestroyedMembers: true);
        return new ObjectCacheOwnership(_batch.Objects);
    }

    public void Dispose()
    {
        if (_closed)
            return;

        EnsureCurrentThreadAndScope();
        _closed = true;
        _batch.IsAborted = true;
        XRObjectBase.CurrentObjectCachePublicationScope = _parent;

        if (_ownsBatch && ConstructionOwnership is null)
            XRObjectBase.AbortDeferredObjectCachePublication(_batch.Objects);
    }

    internal void RetainFailedConstruction()
    {
        if (!_ownsBatch || ConstructionOwnership is null)
            throw new InvalidOperationException("Only an owned construction can retain a failed allocation ledger.");
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Owned construction must finish on its original thread.");
        bool inOrder = ReferenceEquals(XRObjectBase.CurrentObjectCachePublicationScope, this);
        _closed = true;
        _batch.IsAborted = true;
        XRObjectBase.CurrentObjectCachePublicationScope = _parent;
        XRObjectBase.RollBackOwnedObjectCacheBatch(_batch.PublishedEntries!);
        if (!inOrder)
            throw new InvalidOperationException("The owned factory did not close its nested publication scopes.");
    }

    private void EnsureCurrentThreadAndScope()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Object-cache publication scopes must complete on the thread that created them.");
        if (!ReferenceEquals(XRObjectBase.CurrentObjectCachePublicationScope, this))
            throw new InvalidOperationException("Object-cache publication scopes must complete in stack order.");
    }
}
