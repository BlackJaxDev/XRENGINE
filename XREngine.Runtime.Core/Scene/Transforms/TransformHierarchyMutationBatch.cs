using System.Numerics;

namespace XREngine.Scene.Transforms;

/// <summary>
/// Applies a group of local hierarchy mutations while deferring world dirty
/// registration to a single tree-root enqueue.
/// </summary>
/// <remarks>
/// Property notifications and local/world dirty flags are still produced for
/// every changed transform. Callers must only mutate descendants of the root
/// used to create the batch. The scope is stack-only and allocation-free.
/// </remarks>
public ref struct TransformHierarchyMutationBatch
{
    private TransformBase? _root;
    private bool _hasMutations;

    internal TransformHierarchyMutationBatch(TransformBase root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _hasMutations = false;
        TransformBase.EnterHierarchyMutationBatch();
    }

    /// <summary>Applies a world-space rotation delta without enqueuing the individual transform.</summary>
    public void AddWorldRotationDelta(Transform transform, Quaternion worldDelta)
    {
        ArgumentNullException.ThrowIfNull(transform);
        EnsureActive();
        _hasMutations = true;
        TransformBase.EnterHierarchyMutation(transform);
        try
        {
            transform.AddWorldRotationDelta(worldDelta);
        }
        finally
        {
            TransformBase.ExitHierarchyMutation(transform);
        }
    }

    /// <summary>Sets a world-space translation without enqueuing the individual transform.</summary>
    public void SetWorldTranslation(Transform transform, Vector3 worldTranslation)
    {
        ArgumentNullException.ThrowIfNull(transform);
        EnsureActive();
        _hasMutations = true;
        TransformBase.EnterHierarchyMutation(transform);
        try
        {
            transform.SetWorldTranslation(worldTranslation);
        }
        finally
        {
            TransformBase.ExitHierarchyMutation(transform);
        }
    }

    /// <summary>
    /// Refreshes a transform's cached matrices inside the batch. This is useful
    /// when a later world-space mutation depends on the updated parent inverse.
    /// </summary>
    public void RecalculateMatrices(TransformBase transform, bool forceWorldRecalc = false, bool setRenderMatrixNow = false)
    {
        ArgumentNullException.ThrowIfNull(transform);
        EnsureActive();
        transform.RecalculateMatrices(forceWorldRecalc, setRenderMatrixNow);
    }

    /// <summary>
    /// Ends the batch and enqueues its root exactly once when any mutation was applied.
    /// </summary>
    public void Dispose()
    {
        TransformBase? root = _root;
        if (root is null)
            return;

        bool hasMutations = _hasMutations;
        _root = null;
        _hasMutations = false;
        TransformBase.ExitHierarchyMutationBatch();
        if (hasMutations)
            root.EnqueueHierarchyRecalculation();
    }

    private readonly void EnsureActive()
    {
        if (_root is null)
            throw new ObjectDisposedException(nameof(TransformHierarchyMutationBatch));
    }
}
