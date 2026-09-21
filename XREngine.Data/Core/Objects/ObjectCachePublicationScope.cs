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
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _closed;

    internal ObjectCachePublicationScope(ObjectCachePublicationScope? parent)
    {
        _parent = parent;
        _batch = parent?._batch ?? new ObjectCachePublicationBatch();
        XRObjectBase.CurrentObjectCachePublicationScope = this;
    }

    internal void Enlist(XRObjectBase value) => _batch.Enlist(value);

    /// <summary>
    /// Completes this scope. The root scope publishes the entire batch only after every
    /// nested scope has completed successfully.
    /// </summary>
    public void Complete()
    {
        EnsureCurrentThreadAndScope();

        if (_parent is not null)
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

        XRObjectBase.PublishDeferredObjectCacheBatch(_batch.Objects);
        _closed = true;
        XRObjectBase.CurrentObjectCachePublicationScope = null;
    }

    public void Dispose()
    {
        if (_closed)
            return;

        EnsureCurrentThreadAndScope();
        _closed = true;
        _batch.IsAborted = true;
        XRObjectBase.CurrentObjectCachePublicationScope = _parent;

        if (_parent is null)
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
