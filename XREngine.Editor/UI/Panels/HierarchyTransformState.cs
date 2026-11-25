using System.Numerics;
using XREngine.Animation;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Captures exact local values for compensation and verifies canonical property replay.</summary>
internal sealed class HierarchyTransformState
{
    /// <summary>Updates one ancestor chain at the command boundary before it captures world space.</summary>
    public static void RefreshAncestors(TransformBase transform, Action requireOwner)
    {
        // A command can allocate this short chain. No frame or input polling path calls it.
        List<TransformBase> ancestors = [];
        for (TransformBase? current = transform; current is not null; current = current.Parent)
            ancestors.Add(current);
        for (int index = ancestors.Count - 1; index >= 0; index--)
        {
            requireOwner();
            ancestors[index].RecalculateMatrices(forceWorldRecalc: true);
            requireOwner();
        }
    }

    private readonly TransformBase _transform;
    private readonly Matrix4x4 _matrix;
    private readonly Vector3 _translation;
    private readonly Vector3 _scale;
    private readonly Quaternion _rotation;
    private readonly ETransformOrder _order;

    public HierarchyTransformState(TransformBase transform)
    {
        _transform = transform;
        _matrix = transform.LocalMatrix;
        if (transform is Transform local)
        {
            _translation = local.Translation;
            _rotation = local.Rotation;
            _scale = local.Scale;
            _order = local.Order;
        }
    }

    public bool Matches()
        => _transform is Transform local
            ? local.Translation == _translation && local.Rotation == _rotation &&
              local.Scale == _scale && local.Order == _order
            : _transform.LocalMatrix == _matrix;

    public void ReconcileProperties(StructuralChangeHandle handle, IUndoPropertyReplayGuard? replayGuard = null)
    {
        if (_transform is not Transform local)
            return;
        handle.ReconcileProperty(local, nameof(Transform.Translation), _translation, local.Translation, replayGuard);
        handle.ReconcileProperty(local, nameof(Transform.Rotation), _rotation, local.Rotation, replayGuard);
        handle.ReconcileProperty(local, nameof(Transform.Scale), _scale, local.Scale, replayGuard);
        handle.ReconcileProperty(local, nameof(Transform.Order), _order, local.Order, replayGuard);
    }

    public void Restore(Action requireOwner)
    {
        Exception? failure = null;
        void Apply(Action setter)
        {
            requireOwner();
            try { setter(); }
            catch (Exception exception) { failure ??= exception; }
            requireOwner();
        }

        if (_transform is Transform local)
        {
            Apply(() => local.Order = _order);
            Apply(() => local.Translation = _translation);
            Apply(() => local.Rotation = _rotation);
            Apply(() => local.Scale = _scale);
        }
        else if (_transform.LocalMatrix != _matrix)
            Apply(() => _transform.DeriveLocalMatrix(_matrix));
        Apply(() => _transform.RecalculateMatrices(forceWorldRecalc: true));
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (!Matches())
            throw new InvalidOperationException("A hierarchy value could not be restored.");
    }
}
