using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed partial class WeightedTransformConstraintComponent
{
    /// <summary>
    /// Recalculates the offsets so that the solved pose equals the target's current pose with the
    /// current source poses. This is what the "Activate" and rebake actions of the source editor do.
    /// <see cref="TransformConstraintMode.Match"/> changes the constraint-level position, rotation, and
    /// scale offsets; <see cref="TransformConstraintMode.Parent"/> changes the offsets of every source that
    /// has a transform; <see cref="TransformConstraintMode.Aim"/> changes the rotation offset.
    /// </summary>
    public void RebakeOffsets()
    {
        if ((TargetTransform ?? Transform) is not Transform target)
            return;

        if (SolveInLocalSpace)
            RebakeOffsets(target, target.Translation, target.Rotation, target.Scale);
        else
            RebakeOffsets(target, target.WorldTranslation, target.WorldRotation, target.LossyWorldScale);
    }

    /// <summary>
    /// Rebakes for <see cref="RebakeOffsetsWhenUnfrozen"/>. The pose to keep is the world pose held while
    /// frozen; animation may have changed the target's local values since then. Without a captured
    /// pose, the current pose is used.
    /// </summary>
    private void RebakeOffsetsFromHeldPose(Transform target)
    {
        if (!_frozenPoseCaptured)
        {
            RebakeOffsets();
            return;
        }

        if (!SolveInLocalSpace)
        {
            RebakeOffsets(target, _frozenWorldPosition, _frozenWorldRotation, _frozenWorldScale);
            return;
        }

        Vector3 localPosition = _frozenWorldPosition;
        Quaternion localRotation = _frozenWorldRotation;
        if (target.Parent is TransformBase parent)
        {
            localPosition = Vector3.Transform(_frozenWorldPosition, parent.InverseWorldMatrix);
            localRotation = Quaternion.Normalize(Quaternion.Inverse(parent.WorldRotation) * _frozenWorldRotation);
        }

        RebakeOffsets(target, localPosition, localRotation, WorldToLocalScale(target, _frozenWorldScale));
    }

    /// <summary>
    /// Sets the offsets so that the solved pose equals the given pose, which is in the solve space
    /// (local values, or world values and lossy scale).
    /// </summary>
    private void RebakeOffsets(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        switch (Mode)
        {
            case TransformConstraintMode.Parent:
                RebakeSourceOffsets(position, rotation);
                break;

            case TransformConstraintMode.Aim:
                if (TryAccumulateSources(false, out Vector3 aimSource, out _, out _) &&
                    TrySolveAim(target, aimSource, out Quaternion aimRotation))
                {
                    RotationOffset = Quaternion.Normalize(Quaternion.Inverse(aimRotation) * rotation);
                }
                break;

            default:
                if (!TryAccumulateSources(false, out Vector3 sourcePosition, out Quaternion sourceRotation, out Vector3 sourceScale))
                    break;

                Vector3 positionOffset = position - sourcePosition;
                if (!SolveInLocalSpace && target.Parent is TransformBase parent)
                    positionOffset = Vector3.TransformNormal(positionOffset, parent.InverseWorldMatrix);
                PositionOffset = positionOffset;
                RotationOffset = Quaternion.Normalize(Quaternion.Inverse(sourceRotation) * rotation);
                ScaleOffset = SafeDivide(scale, sourceScale);
                break;
        }
    }

    /// <summary>
    /// Gives each source the offset that puts the target at the given pose when that source is the only
    /// one: the position in the source's space (rotation and scale removed) and the rotation relative to
    /// the source rotation.
    /// </summary>
    private void RebakeSourceOffsets(Vector3 position, Quaternion rotation)
    {
        List<TransformConstraintSource> sources = Sources;
        for (int index = 0; index < sources.Count; index++)
        {
            TransformConstraintSource source = sources[index];
            if (source.SourceTransform is not TransformBase sourceTransform)
                continue;

            GetSourcePose(sourceTransform, out Vector3 sourcePosition, out Quaternion sourceRotation, out Vector3 sourceScale);
            Quaternion inverseRotation = Quaternion.Inverse(sourceRotation);
            source.PositionOffset = SafeDivide(Vector3.Transform(position - sourcePosition, inverseRotation), sourceScale);
            source.RotationOffset = Quaternion.Normalize(inverseRotation * rotation);
        }
    }
}
