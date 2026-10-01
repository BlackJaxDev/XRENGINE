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

    internal ObjectCachePublicationScope(ObjectCachePublicationScope? parent, bool independentBatch = false)
    {
        _parent = parent;
        _ownsBatch = parent is null || independentBatch;
        _batch = _ownsBatch ? new ObjectCachePublicationBatch() : parent!._batch;
        XRObjectBase.CurrentObjectCachePublicationScope = this;
    }

    internal void Enlist(XRObjectBase value) => _batch.Enlist(value);

    internal void RecordDestructionFailure(XRObjectBase value)
    {
        for (ObjectCachePublicationScope? scope = this; scope is not null; scope = scope._parent)
            if (scope._batch.Contains(value))
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
            XRObjectBase.AbortDeferredObjectCachePublication(_batch.Objects);
            throw new InvalidOperationException("The object-cache publication batch was aborted.");
        }

        XRObjectBase.PublishDeferredObjectCacheBatch(_batch.Objects, allowDestroyedMembers);
        _closed = true;
        XRObjectBase.CurrentObjectCachePublicationScope = _parent;
    }

    /// <summary>
    /// Publishes an independently owned batch and transfers its exact allocation ledger
    /// to a lifetime owner. Borrowed objects that existed before this batch are not included.
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

        if (_ownsBatch)
            XRObjectBase.AbortDeferredObjectCachePublication(_batch.Objects);
    }

    private void EnsureCurrentThreadAndScope()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Object-cache publication scopes must complete on the thread that created them.");
        if (!ReferenceEquals(XRObjectBase.CurrentObjectCachePublicationScope, this))
            throw new InvalidOperationException("Object-cache publication scopes must complete in stack order.");
    }
}
