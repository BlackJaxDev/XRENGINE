using System.Numerics;
using XREngine.Extensions;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public sealed partial class WeightedTransformConstraintComponent
{
    /// <summary>
    /// Solves the constraint once and writes the target. The late tick calls it once per frame after
    /// animation. The weighted source pose plus the constraint-level offsets is the solved pose (for
    /// <see cref="TransformConstraintMode.Aim"/>, the rotation that points the aim axis at the weighted
    /// source position). The target moves from its rest pose (when one was authored) or its current
    /// pose toward the solved pose by <see cref="Weight"/>, on the affected channels only. With a total
    /// source weight of zero the target takes its rest pose. An unlocked constraint does not move the
    /// target.
    /// </summary>
    public void Evaluate()
    {
        if (!Locked)
            return;

        TransformBase targetBase = TargetTransform ?? Transform;
        if (targetBase is not Transform target)
            return;

        if (FreezeToWorld)
        {
            ApplyFrozen(target);
            PublishTargetPose(target);
            return;
        }

        if (_rebakePending)
        {
            _rebakePending = false;
            RebakeOffsetsFromHeldPose(target);
        }

        if (Weight <= 0.0f || !TrySolve(target, out Vector3 position, out Quaternion rotation, out Vector3 scale))
        {
            if (HasRestPose)
            {
                ApplyRest(target);
                PublishTargetPose(target);
            }
            return;
        }

        if (SolveInLocalSpace)
            ApplyLocal(target, position, rotation, scale);
        else
            ApplyWorld(target, position, rotation, scale);
        PublishTargetPose(target);
    }

    /// <summary>
    /// The channels that the current mode can write: <see cref="Channels"/> without scale for
    /// <see cref="TransformConstraintMode.Parent"/>, and only rotation for <see cref="TransformConstraintMode.Aim"/>.
    /// </summary>
    private TransformConstraintChannels EffectiveChannels => Mode switch
    {
        TransformConstraintMode.Parent => Channels & TransformConstraintChannels.Parent,
        TransformConstraintMode.Aim => Channels & TransformConstraintChannels.Rotation,
        _ => Channels,
    };

    /// <summary>
    /// Computes the unweighted solved pose in the solve space (local or world). Returns false when no
    /// source has a weight above zero, or when an aim has no direction (source and target at one point).
    /// </summary>
    private bool TrySolve(Transform target, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        position = SolveInLocalSpace ? target.Translation : target.WorldTranslation;
        rotation = Quaternion.Identity;
        scale = SolveInLocalSpace ? target.Scale : target.LossyWorldScale;

        bool applySourceOffsets = Mode == TransformConstraintMode.Parent;
        if (!TryAccumulateSources(applySourceOffsets, out Vector3 sourcePosition, out Quaternion sourceRotation, out Vector3 sourceScale))
            return false;

        if (Mode == TransformConstraintMode.Aim)
        {
            if (!TrySolveAim(target, sourcePosition, out Quaternion aimRotation))
                return false;

            rotation = Quaternion.Normalize(aimRotation * RotationOffset);
            return true;
        }

        position = sourcePosition + GetPositionOffsetInSolveSpace(target, PositionOffset);
        rotation = Quaternion.Normalize(sourceRotation * RotationOffset);
        scale = sourceScale * ScaleOffset;
        return true;
    }

    /// <summary>
    /// Averages the sources by weight in the solve space. In <see cref="TransformConstraintMode.Parent"/>
    /// each source pose first takes its per-source offset: the position offset is moved by the source's
    /// scale and rotation (as for a child of the source), and the rotation offset follows the source
    /// rotation. Rotations blend by a running slerp in source order.
    /// </summary>
    private bool TryAccumulateSources(
        bool applySourceOffsets,
        out Vector3 position,
        out Quaternion rotation,
        out Vector3 scale)
    {
        float totalWeight = 0.0f;
        position = Vector3.Zero;
        scale = Vector3.Zero;
        rotation = Quaternion.Identity;
        bool hasRotation = false;

        List<TransformConstraintSource> sources = Sources;
        for (int index = 0; index < sources.Count; index++)
        {
            TransformConstraintSource source = sources[index];
            if (source.SourceTransform is not TransformBase sourceTransform || source.Weight <= 0.0f)
                continue;

            float sourceWeight = source.Weight;
            totalWeight += sourceWeight;
            GetSourcePose(sourceTransform, out Vector3 sourcePosition, out Quaternion sourceRotation, out Vector3 sourceScale);
            if (applySourceOffsets)
            {
                sourcePosition += Vector3.Transform(source.PositionOffset * sourceScale, sourceRotation);
                sourceRotation = Quaternion.Normalize(sourceRotation * source.RotationOffset);
            }

            position += sourcePosition * sourceWeight;
            scale += sourceScale * sourceWeight;
            if (!hasRotation)
            {
                rotation = sourceRotation;
                hasRotation = true;
            }
            else
            {
                float blend = sourceWeight / totalWeight;
                rotation = Quaternion.Normalize(Quaternion.Slerp(
                    rotation,
                    EnsureShortestArc(rotation, sourceRotation),
                    blend));
            }
        }

        if (totalWeight <= 0.0f)
            return false;

        position /= totalWeight;
        scale /= totalWeight;
        return true;
    }

    /// <summary>Reads a source pose in the solve space: local values, or world values and lossy scale.</summary>
    private void GetSourcePose(TransformBase source, out Vector3 position, out Quaternion rotation, out Vector3 scale)
    {
        if (SolveInLocalSpace)
        {
            position = GetLocalTranslation(source);
            rotation = GetLocalRotation(source);
            scale = GetLocalScale(source);
            return;
        }

        position = source.WorldTranslation;
        rotation = source.WorldRotation;
        scale = source.LossyWorldScale;
    }

    /// <summary>
    /// Moves a parent-space position offset into the solve space. The authored values satisfy
    /// PositionAtRest = (rest source position in the parent space) + PositionOffset, so a world solve
    /// moves the offset by the parent's world matrix (rotation and scale, no translation).
    /// </summary>
    private Vector3 GetPositionOffsetInSolveSpace(Transform target, Vector3 offset)
        => SolveInLocalSpace || target.Parent is not TransformBase parent
            ? offset
            : Vector3.TransformNormal(offset, parent.WorldMatrix);

    private void ApplyLocal(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 fromPosition = HasRestPose ? PositionAtRest : target.Translation;
        Quaternion fromRotation = HasRestPose ? RotationAtRest : target.Rotation;
        Vector3 fromScale = HasRestPose ? ScaleAtRest : target.Scale;
        Vector3 targetPosition = BlendChannels(fromPosition, target.Translation, position, Weight, PositionMask);
        Quaternion targetRotation = BlendRotation(fromRotation, target.Rotation, rotation, Weight);
        Vector3 targetScale = BlendChannels(fromScale, target.Scale, scale, Weight, ScaleMask);
        target.SetLocalTranslationRotation(targetPosition, targetRotation);
        if (HasScaleChannel)
            target.Scale = targetScale;
    }

    private void ApplyWorld(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 fromPosition = target.WorldTranslation;
        Quaternion fromRotation = target.WorldRotation;
        if (HasRestPose)
            GetRestWorldPose(target, out fromPosition, out fromRotation);
        Vector3 targetPosition = BlendChannels(fromPosition, target.WorldTranslation, position, Weight, PositionMask);
        Quaternion targetRotation = BlendRotation(fromRotation, target.WorldRotation, rotation, Weight);
        target.SetWorldTranslationRotation(targetPosition, targetRotation);

        if (!HasScaleChannel)
            return;

        // Transform has no direct world-scale setter. Convert the solved world scale to a local scale
        // with the parent's lossy scale, then blend in local units so the rest scale (a local value)
        // can be the start of the blend.
        Vector3 desiredLocalScale = WorldToLocalScale(target, scale);
        Vector3 fromScale = HasRestPose ? ScaleAtRest : target.Scale;
        target.Scale = BlendChannels(fromScale, target.Scale, desiredLocalScale, Weight, ScaleMask);
    }

    /// <summary>Puts the affected channels at the authored rest pose.</summary>
    private void ApplyRest(Transform target)
    {
        Vector3 position = BlendChannels(PositionAtRest, target.Translation, PositionAtRest, 1.0f, PositionMask);
        Quaternion rotation = BlendRotation(RotationAtRest, target.Rotation, RotationAtRest, 1.0f);
        target.SetLocalTranslationRotation(position, rotation);
        if (HasScaleChannel)
            target.Scale = BlendChannels(ScaleAtRest, target.Scale, ScaleAtRest, 1.0f, ScaleMask);
    }

    /// <summary>Holds the world pose captured at the first solve after the target was frozen.</summary>
    private void ApplyFrozen(Transform target)
    {
        if (!_frozenPoseCaptured)
        {
            _frozenWorldPosition = target.WorldTranslation;
            _frozenWorldRotation = target.WorldRotation;
            _frozenWorldScale = target.LossyWorldScale;
            _frozenPoseCaptured = true;
        }

        Vector3 position = BlendChannels(_frozenWorldPosition, target.WorldTranslation, _frozenWorldPosition, 1.0f, PositionMask);
        Quaternion rotation = BlendRotation(_frozenWorldRotation, target.WorldRotation, _frozenWorldRotation, 1.0f);
        target.SetWorldTranslationRotation(position, rotation);
        if (HasScaleChannel)
        {
            Vector3 localScale = WorldToLocalScale(target, _frozenWorldScale);
            target.Scale = BlendChannels(localScale, target.Scale, localScale, 1.0f, ScaleMask);
        }
    }

    /// <summary>
    /// Makes the new target pose visible to constraints that solve later in this frame. Transform
    /// setters only mark the matrices dirty, and the world recalculates dirty transforms after the late
    /// tick group, so a dependent constraint would read last frame's world matrix. When another
    /// constraint depends on this one, the target subtree is recalculated now, as the physics chains do
    /// for their bones.
    /// </summary>
    private void PublishTargetPose(Transform target)
    {
        if (HasDependents)
            target.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: false);
    }

    /// <summary>The rest pose (a local pose) moved into world space by the target's parent.</summary>
    private void GetRestWorldPose(Transform target, out Vector3 position, out Quaternion rotation)
    {
        if (target.Parent is not TransformBase parent)
        {
            position = PositionAtRest;
            rotation = RotationAtRest;
            return;
        }

        position = Vector3.Transform(PositionAtRest, parent.WorldMatrix);
        rotation = Quaternion.Normalize(parent.WorldRotation * RotationAtRest);
    }

    private static Vector3 WorldToLocalScale(Transform target, Vector3 worldScale)
    {
        Vector3 parentScale = target.Parent?.LossyWorldScale ?? Vector3.One;
        return new(
            SafeDivide(worldScale.X, parentScale.X),
            SafeDivide(worldScale.Y, parentScale.Y),
            SafeDivide(worldScale.Z, parentScale.Z));
    }

    /// <summary>
    /// Slerps from <paramref name="from"/> toward <paramref name="desired"/> by <paramref name="weight"/>
    /// on the affected rotation channels; unaffected Euler channels keep <paramref name="current"/>.
    /// </summary>
    private Quaternion BlendRotation(Quaternion from, Quaternion current, Quaternion desired, float weight)
    {
        TransformConstraintChannels channels = EffectiveChannels;
        TransformConstraintChannels rotationChannels = channels & TransformConstraintChannels.Rotation;
        if (rotationChannels == TransformConstraintChannels.None)
            return current;

        desired = EnsureShortestArc(from, desired);
        Quaternion blended = Quaternion.Normalize(Quaternion.Slerp(from, desired, weight));
        if (rotationChannels == TransformConstraintChannels.Rotation)
            return blended;

        Vector3 currentEuler = ToEulerDegrees(current);
        Vector3 blendedEuler = ToEulerDegrees(blended);
        Vector3 result = new(
            channels.HasFlag(TransformConstraintChannels.RotationX) ? blendedEuler.X : currentEuler.X,
            channels.HasFlag(TransformConstraintChannels.RotationY) ? blendedEuler.Y : currentEuler.Y,
            channels.HasFlag(TransformConstraintChannels.RotationZ) ? blendedEuler.Z : currentEuler.Z);
        return FromEulerDegrees(result);
    }

    private bool HasScaleChannel
        => (EffectiveChannels & TransformConstraintChannels.Scale) != TransformConstraintChannels.None;

    private Vector3 PositionMask
    {
        get
        {
            TransformConstraintChannels channels = EffectiveChannels;
            return new(
                channels.HasFlag(TransformConstraintChannels.PositionX) ? 1.0f : 0.0f,
                channels.HasFlag(TransformConstraintChannels.PositionY) ? 1.0f : 0.0f,
                channels.HasFlag(TransformConstraintChannels.PositionZ) ? 1.0f : 0.0f);
        }
    }

    private Vector3 ScaleMask
    {
        get
        {
            TransformConstraintChannels channels = EffectiveChannels;
            return new(
                channels.HasFlag(TransformConstraintChannels.ScaleX) ? 1.0f : 0.0f,
                channels.HasFlag(TransformConstraintChannels.ScaleY) ? 1.0f : 0.0f,
                channels.HasFlag(TransformConstraintChannels.ScaleZ) ? 1.0f : 0.0f);
        }
    }

    /// <summary>
    /// Lerps from <paramref name="from"/> toward <paramref name="desired"/> by <paramref name="weight"/>
    /// on the affected axes; unaffected axes keep <paramref name="current"/>.
    /// </summary>
    private static Vector3 BlendChannels(Vector3 from, Vector3 current, Vector3 desired, float weight, Vector3 mask)
    {
        Vector3 blended = Vector3.Lerp(from, desired, weight);
        return new(
            mask.X > 0.0f ? blended.X : current.X,
            mask.Y > 0.0f ? blended.Y : current.Y,
            mask.Z > 0.0f ? blended.Z : current.Z);
    }

    private static Vector3 GetLocalTranslation(TransformBase transform)
        => transform is Transform standard ? standard.Translation : transform.LocalMatrix.Translation;

    private static Vector3 GetLocalScale(TransformBase transform)
        => transform is Transform standard ? standard.Scale : transform.LocalMatrix.ExtractScale();

    private static Quaternion GetLocalRotation(TransformBase transform)
    {
        if (transform is Transform standard)
            return standard.Rotation;

        return Matrix4x4.Decompose(transform.LocalMatrix, out _, out Quaternion rotation, out _)
            ? rotation
            : Quaternion.Identity;
    }

    private static Quaternion EnsureShortestArc(Quaternion from, Quaternion to)
        => Quaternion.Dot(from, to) < 0.0f
            ? new Quaternion(-to.X, -to.Y, -to.Z, -to.W)
            : to;

    private static float SafeDivide(float value, float divisor)
        => MathF.Abs(divisor) > 0.000001f ? value / divisor : value;

    private static Vector3 SafeDivide(Vector3 value, Vector3 divisor)
        => new(SafeDivide(value.X, divisor.X), SafeDivide(value.Y, divisor.Y), SafeDivide(value.Z, divisor.Z));

    private static Quaternion FromEulerDegrees(Vector3 value)
    {
        const float degreesToRadians = MathF.PI / 180.0f;
        return Quaternion.CreateFromYawPitchRoll(
            value.Y * degreesToRadians,
            value.X * degreesToRadians,
            value.Z * degreesToRadians);
    }

    private static Vector3 ToEulerDegrees(Quaternion value)
    {
        value = Quaternion.Normalize(value);
        float sinPitch = 2.0f * ((value.W * value.X) - (value.Z * value.Y));
        float pitch = MathF.Abs(sinPitch) >= 1.0f
            ? MathF.CopySign(MathF.PI * 0.5f, sinPitch)
            : MathF.Asin(sinPitch);
        float yaw = MathF.Atan2(
            2.0f * ((value.W * value.Y) + (value.X * value.Z)),
            1.0f - (2.0f * ((value.X * value.X) + (value.Y * value.Y))));
        float roll = MathF.Atan2(
            2.0f * ((value.W * value.Z) + (value.X * value.Y)),
            1.0f - (2.0f * ((value.X * value.X) + (value.Z * value.Z))));
        const float radiansToDegrees = 180.0f / MathF.PI;
        return new Vector3(pitch, yaw, roll) * radiansToDegrees;
    }
}
