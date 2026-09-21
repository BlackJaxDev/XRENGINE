using XREngine.Data.Core;
using System.Diagnostics;

namespace XREngine.Rendering;

/// <summary>
/// Withholds render objects and their API wrappers until synchronous CPU preparation succeeds.
/// </summary>
/// <remarks>
/// This scope is thread-affine and intentionally does not flow through execution contexts.
/// Root completion registers the CPU objects only; the renderer owner creates wrappers on first use.
/// </remarks>
public sealed class RenderObjectPublicationScope : IDisposable
{
    private readonly RenderObjectPublicationScope? _parent;
    private readonly RenderObjectPublicationBatch _batch;
    private readonly ObjectCachePublicationScope _objectCacheScope;
    private readonly int _firstObjectIndex;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private bool _closed;

    internal RenderObjectPublicationScope(RenderObjectPublicationScope? parent)
    {
        _parent = parent;
        _batch = parent?._batch ?? new RenderObjectPublicationBatch();
        _firstObjectIndex = _batch.Objects.Count;
        _objectCacheScope = XRObjectBase.BeginDeferredObjectCachePublication();
        GenericRenderObject.CurrentDeferredPublicationScope = this;
    }

    internal void Enlist(GenericRenderObject value) => _batch.Enlist(value);

    internal object BatchIdentity => _batch;

    /// <summary>
    /// Completes this scope. Only root completion publishes the batch, and it never
    /// invokes a renderer or creates an API wrapper.
    /// </summary>
    public void Complete()
        => Complete(beforeCachePublication: null);

    /// <summary>
    /// Completes this scope after running a synchronous commit callback against ready,
    /// cache-hidden render objects. The callback must return before cache publication.
    /// </summary>
    internal void Complete(Action? beforeCachePublication)
    {
        EnsureCurrentThreadAndScope();

        if (_batch.IsAborted)
            throw new InvalidOperationException("The render-object publication batch was aborted.");

        if (_parent is not null)
        {
            if (beforeCachePublication is not null)
            {
                PrepareObjectsForPublication(_firstObjectIndex);
                beforeCachePublication();
                PrepareObjectsForPublication(_firstObjectIndex);
            }
            _objectCacheScope.Complete();
            _closed = true;
            GenericRenderObject.CurrentDeferredPublicationScope = _parent;
            return;
        }

        try
        {
            PrepareObjectsForPublication(0);
            _batch.ApplyTransactions();
            beforeCachePublication?.Invoke();
            PrepareObjectsForPublication(0);

            long publicationStart = Stopwatch.GetTimestamp();
            // Publish the render cache first while the object-cache scope is still abortable.
            // If either registration fails, disposal destroys the batch and removes any render entries.
            GenericRenderObject.PublishDeferredBatch(_batch.Objects);
            _objectCacheScope.Complete();
            XRMeshCpuPreparationTelemetry.RecordCachePublication(
                _batch.Objects.Count,
                Stopwatch.GetTimestamp() - publicationStart);
        }
        catch
        {
            _batch.IsAborted = true;
            _batch.RollbackTransactions();
            throw;
        }
        _closed = true;
        GenericRenderObject.CurrentDeferredPublicationScope = null;
        _batch.CompleteTransactions();
    }

    /// <summary>
    /// Enlists a reversible state change in the root publication boundary. Nested scopes
    /// defer the change until the enclosing root is ready, so an outer construction failure
    /// cannot leave an existing owner pointing at resources that the batch later destroys.
    /// </summary>
    internal void Complete(
        Action beforeCachePublication,
        Action rollbackOnFailure,
        Action afterPublication)
        => Complete(
            beforeCachePublication,
            rollbackOnFailure,
            afterPublication,
            abortedBeforeApply: null);

    internal void Complete(
        Action beforeCachePublication,
        Action rollbackOnFailure,
        Action afterPublication,
        Action? abortedBeforeApply)
    {
        ArgumentNullException.ThrowIfNull(beforeCachePublication);
        ArgumentNullException.ThrowIfNull(rollbackOnFailure);
        ArgumentNullException.ThrowIfNull(afterPublication);
        EnsureCurrentThreadAndScope();

        if (_batch.IsAborted)
            throw new InvalidOperationException("The render-object publication batch was aborted.");

        if (_parent is not null)
        {
            _objectCacheScope.Complete();
            _batch.EnlistTransaction(
                beforeCachePublication,
                rollbackOnFailure,
                afterPublication,
                abortedBeforeApply);
            _closed = true;
            GenericRenderObject.CurrentDeferredPublicationScope = _parent;
            return;
        }

        _batch.EnlistTransaction(
            beforeCachePublication,
            rollbackOnFailure,
            afterPublication,
            abortedBeforeApply);
        try
        {
            PrepareObjectsForPublication(0);
            _batch.ApplyTransactions();
            PrepareObjectsForPublication(0);

            long publicationStart = Stopwatch.GetTimestamp();
            GenericRenderObject.PublishDeferredBatch(_batch.Objects);
            _objectCacheScope.Complete();
            XRMeshCpuPreparationTelemetry.RecordCachePublication(
                _batch.Objects.Count,
                Stopwatch.GetTimestamp() - publicationStart);
        }
        catch
        {
            _batch.IsAborted = true;
            _batch.RollbackTransactions();
            throw;
        }

        _closed = true;
        GenericRenderObject.CurrentDeferredPublicationScope = null;
        _batch.CompleteTransactions();
    }

    private void PrepareObjectsForPublication(int startIndex)
    {
        for (int index = startIndex; index < _batch.Objects.Count; index++)
            _batch.Objects[index].PrepareDeferredPublication();
        for (int index = startIndex; index < _batch.Objects.Count; index++)
            _batch.Objects[index].FinalizeDeferredPublication();
    }

    public void Dispose()
    {
        if (_closed)
            return;

        EnsureCurrentThreadAndScope();
        _closed = true;
        _batch.IsAborted = true;
        _batch.RollbackTransactions();
        GenericRenderObject.CurrentDeferredPublicationScope = _parent;
        _objectCacheScope.Dispose();
    }

    private void EnsureCurrentThreadAndScope()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Render-object publication scopes must complete on the thread that created them.");
        if (!ReferenceEquals(GenericRenderObject.CurrentDeferredPublicationScope, this))
            throw new InvalidOperationException("Render-object publication scopes must complete in stack order.");
    }
}
