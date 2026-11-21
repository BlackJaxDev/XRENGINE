using System.Numerics;
using XREngine.Extensions;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Weighted parent/position/rotation/scale constraint used by imported Unity avatars.
/// </summary>
[Serializable]
public sealed class WeightedTransformConstraintComponent : XRComponent
{
    private TransformBase? _targetTransform;
    private List<TransformConstraintSource> _sources = [];
    private TransformConstraintChannels _channels = TransformConstraintChannels.Parent;
    private float _weight = 1.0f;
    private bool _solveInLocalSpace;
    private bool _locked;
    private Vector3 _positionOffset;
    private Quaternion _rotationOffset = Quaternion.Identity;
    private bool _hasRestPose;
    private Vector3 _positionAtRest;
    private Quaternion _rotationAtRest = Quaternion.Identity;
    private bool _freezeToWorld;
    private bool _frozenPoseCaptured;
    private Vector3 _frozenWorldPosition;
    private Quaternion _frozenWorldRotation = Quaternion.Identity;

    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set => SetField(ref _targetTransform, value);
    }

    public List<TransformConstraintSource> Sources
    {
        get => _sources;
        set => SetField(ref _sources, value ?? []);
    }

    public TransformConstraintChannels Channels
    {
        get => _channels;
        set => SetField(ref _channels, value);
    }

    public float Weight
    {
        get => _weight;
        set => SetField(ref _weight, Math.Clamp(value, 0.0f, 1.0f));
    }

    public bool SolveInLocalSpace
    {
        get => _solveInLocalSpace;
        set => SetField(ref _solveInLocalSpace, value);
    }

    /// <summary>
    /// True when the authored at-rest and offset values are fixed, so the constraint drives the
    /// target. A VRChat constraint that is not locked recalculates its at-rest and offset values
    /// from the current pose instead of moving the target, so this component does not drive it.
    /// </summary>
    public bool Locked
    {
        get => _locked;
        set => SetField(ref _locked, value);
    }

    /// <summary>
    /// Constraint-level position offset that is added to the weighted source position. It is in the
    /// target's parent space: a world-space solve moves it by the parent's world matrix (rotation and
    /// scale). VRChat bakes it so that the target holds <see cref="PositionAtRest"/> when the sources
    /// are at rest (rest = source position in the parent space + offset).
    /// </summary>
    public Vector3 PositionOffset
    {
        get => _positionOffset;
        set => SetField(ref _positionOffset, value);
    }

    /// <summary>
    /// Constraint-level rotation offset, applied after the weighted source rotation
    /// (source rotation * offset).
    /// </summary>
    public Quaternion RotationOffset
    {
        get => _rotationOffset;
        set => SetField(ref _rotationOffset, value);
    }

    /// <summary>
    /// True when <see cref="PositionAtRest"/> and <see cref="RotationAtRest"/> hold authored values.
    /// The constraint then blends from the rest pose by <see cref="Weight"/>, and uses the rest pose
    /// when the total source weight is zero.
    /// </summary>
    public bool HasRestPose
    {
        get => _hasRestPose;
        set => SetField(ref _hasRestPose, value);
    }

    /// <summary>The target's local position when the constraint weight or the total source weight is zero.</summary>
    public Vector3 PositionAtRest
    {
        get => _positionAtRest;
        set => SetField(ref _positionAtRest, value);
    }

    /// <summary>The target's local rotation when the constraint weight or the total source weight is zero.</summary>
    public Quaternion RotationAtRest
    {
        get => _rotationAtRest;
        set => SetField(ref _rotationAtRest, value);
    }

    /// <summary>
    /// VRChat Freeze To World: the constraint ignores its sources and holds the target's world
    /// position and rotation, on the affected channels, from the first solve after activation.
    /// </summary>
    public bool FreezeToWorld
    {
        get => _freezeToWorld;
        set
        {
            if (SetField(ref _freezeToWorld, value))
                _frozenPoseCaptured = false;
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _frozenPoseCaptured = false;
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Animation + 1, ApplyConstraint);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Animation + 1, ApplyConstraint);
        base.OnComponentDeactivated();
    }

    /// <summary>
    /// Solves the constraint once per frame after animation. The weighted source pose plus the
    /// constraint-level offset is the solved pose. The target moves from its rest pose (when one
    /// was authored) or its current pose toward the solved pose by <see cref="Weight"/>, on the
    /// affected channels only. With a total source weight of zero the target takes its rest pose.
    /// An unlocked constraint does not move the target.
    /// </summary>
    private void ApplyConstraint()
    {
        if (!Locked)
            return;

        TransformBase targetBase = TargetTransform ?? Transform;
        if (targetBase is not Transform target)
            return;

        if (FreezeToWorld)
        {
            ApplyFrozen(target);
            return;
        }

        if (Weight <= 0.0f)
        {
            if (HasRestPose)
                ApplyRest(target);
            return;
        }

        float totalSourceWeight = 0.0f;
        Vector3 sourcePosition = Vector3.Zero;
        Vector3 sourceScale = Vector3.Zero;
        Quaternion sourceRotation = Quaternion.Identity;
        bool hasRotation = false;

        foreach (TransformConstraintSource source in Sources)
        {
            if (source.SourceTransform is not TransformBase sourceTransform || source.Weight <= 0.0f)
                continue;

            float sourceWeight = source.Weight;
            totalSourceWeight += sourceWeight;
            Vector3 position = SolveInLocalSpace
                ? GetLocalTranslation(sourceTransform)
                : sourceTransform.WorldTranslation;
            Vector3 scale = SolveInLocalSpace
                ? GetLocalScale(sourceTransform)
                : sourceTransform.LossyWorldScale;
            Quaternion rotation = SolveInLocalSpace
                ? GetLocalRotation(sourceTransform)
                : sourceTransform.WorldRotation;

            sourcePosition += (position + source.PositionOffset) * sourceWeight;
            sourceScale += (scale + source.ScaleOffset) * sourceWeight;
            Quaternion offsetRotation = Quaternion.Normalize(rotation * source.RotationOffset);
            if (!hasRotation)
            {
                sourceRotation = offsetRotation;
                hasRotation = true;
            }
            else
            {
                float blend = sourceWeight / totalSourceWeight;
                sourceRotation = Quaternion.Normalize(Quaternion.Slerp(
                    sourceRotation,
                    EnsureShortestArc(sourceRotation, offsetRotation),
                    blend));
            }
        }

        if (totalSourceWeight <= 0.0f)
        {
            if (HasRestPose)
                ApplyRest(target);
            return;
        }

        // The offset is in the target's parent space: the authored values satisfy
        // PositionAtRest = (rest source position in the parent space) + PositionOffset.
        Vector3 offset = SolveInLocalSpace || target.Parent is not TransformBase offsetParent
            ? PositionOffset
            : Vector3.TransformNormal(PositionOffset, offsetParent.WorldMatrix);
        sourcePosition = sourcePosition / totalSourceWeight + offset;
        sourceScale /= totalSourceWeight;
        sourceRotation = Quaternion.Normalize(sourceRotation * RotationOffset);
        if (SolveInLocalSpace)
            ApplyLocal(target, sourcePosition, sourceRotation, sourceScale);
        else
            ApplyWorld(target, sourcePosition, sourceRotation, sourceScale);
    }

    private void ApplyLocal(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 fromPosition = HasRestPose ? PositionAtRest : target.Translation;
        Quaternion fromRotation = HasRestPose ? RotationAtRest : target.Rotation;
        Vector3 targetPosition = BlendChannels(fromPosition, target.Translation, position, Weight, PositionMask);
        Vector3 targetScale = BlendChannels(target.Scale, target.Scale, scale, Weight, ScaleMask);
        Quaternion targetRotation = BlendRotation(fromRotation, target.Rotation, rotation, Weight);
        target.SetLocalTranslationRotation(targetPosition, targetRotation);
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

        // Transform has no direct world-scale setter. Convert the authored world result
        // to a local scale using the parent's lossy scale and preserve unaffected axes.
        Vector3 parentScale = target.Parent?.LossyWorldScale ?? Vector3.One;
        Vector3 desiredLocalScale = new(
            SafeDivide(scale.X, parentScale.X),
            SafeDivide(scale.Y, parentScale.Y),
            SafeDivide(scale.Z, parentScale.Z));
        target.Scale = BlendChannels(target.Scale, target.Scale, desiredLocalScale, Weight, ScaleMask);
    }

    /// <summary>Puts the affected position and rotation channels at the authored rest pose.</summary>
    private void ApplyRest(Transform target)
    {
        Vector3 position = BlendChannels(PositionAtRest, target.Translation, PositionAtRest, 1.0f, PositionMask);
        Quaternion rotation = BlendRotation(RotationAtRest, target.Rotation, RotationAtRest, 1.0f);
        target.SetLocalTranslationRotation(position, rotation);
    }

    /// <summary>Holds the world pose captured at the first solve after activation.</summary>
    private void ApplyFrozen(Transform target)
    {
        if (!_frozenPoseCaptured)
        {
            _frozenWorldPosition = target.WorldTranslation;
            _frozenWorldRotation = target.WorldRotation;
            _frozenPoseCaptured = true;
        }

        Vector3 position = BlendChannels(_frozenWorldPosition, target.WorldTranslation, _frozenWorldPosition, 1.0f, PositionMask);
        Quaternion rotation = BlendRotation(_frozenWorldRotation, target.WorldRotation, _frozenWorldRotation, 1.0f);
        target.SetWorldTranslationRotation(position, rotation);
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

    /// <summary>
    /// Slerps from <paramref name="from"/> toward <paramref name="desired"/> by <paramref name="weight"/>
    /// on the affected rotation channels; unaffected Euler channels keep <paramref name="current"/>.
    /// </summary>
    private Quaternion BlendRotation(Quaternion from, Quaternion current, Quaternion desired, float weight)
    {
        TransformConstraintChannels rotationChannels = Channels & TransformConstraintChannels.Rotation;
        if (rotationChannels == TransformConstraintChannels.None)
            return current;

        desired = EnsureShortestArc(from, desired);
        Quaternion blended = Quaternion.Normalize(Quaternion.Slerp(from, desired, weight));
        if (rotationChannels == TransformConstraintChannels.Rotation)
            return blended;

        Vector3 currentEuler = ToEulerDegrees(current);
        Vector3 blendedEuler = ToEulerDegrees(blended);
        Vector3 result = new(
            Channels.HasFlag(TransformConstraintChannels.RotationX) ? blendedEuler.X : currentEuler.X,
            Channels.HasFlag(TransformConstraintChannels.RotationY) ? blendedEuler.Y : currentEuler.Y,
            Channels.HasFlag(TransformConstraintChannels.RotationZ) ? blendedEuler.Z : currentEuler.Z);
        return FromEulerDegrees(result);
    }

    private Vector3 PositionMask => new(
        Channels.HasFlag(TransformConstraintChannels.PositionX) ? 1.0f : 0.0f,
        Channels.HasFlag(TransformConstraintChannels.PositionY) ? 1.0f : 0.0f,
        Channels.HasFlag(TransformConstraintChannels.PositionZ) ? 1.0f : 0.0f);

    private Vector3 ScaleMask => new(
        Channels.HasFlag(TransformConstraintChannels.ScaleX) ? 1.0f : 0.0f,
        Channels.HasFlag(TransformConstraintChannels.ScaleY) ? 1.0f : 0.0f,
        Channels.HasFlag(TransformConstraintChannels.ScaleZ) ? 1.0f : 0.0f);

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
