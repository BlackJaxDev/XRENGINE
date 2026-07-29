using System.Numerics;
using XREngine.Extensions;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Weighted parent/position/rotation/scale constraint used by imported Unity avatars.
/// </summary>
[Serializable]
public sealed class UnityTransformConstraintComponent : XRComponent
{
    private TransformBase? _targetTransform;
    private List<UnityTransformConstraintSource> _sources = [];
    private UnityTransformConstraintChannels _channels = UnityTransformConstraintChannels.Parent;
    private float _weight = 1.0f;
    private bool _solveInLocalSpace;
    private bool _locked;

    public TransformBase? TargetTransform
    {
        get => _targetTransform;
        set => SetField(ref _targetTransform, value);
    }

    public List<UnityTransformConstraintSource> Sources
    {
        get => _sources;
        set => SetField(ref _sources, value ?? []);
    }

    public UnityTransformConstraintChannels Channels
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

    public bool Locked
    {
        get => _locked;
        set => SetField(ref _locked, value);
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Late, (int)ETickOrder.Animation + 1, ApplyConstraint);
    }

    protected override void OnComponentDeactivated()
    {
        UnregisterTick(ETickGroup.Late, (int)ETickOrder.Animation + 1, ApplyConstraint);
        base.OnComponentDeactivated();
    }

    private void ApplyConstraint()
    {
        if (Weight <= 0.0f || Sources.Count == 0)
            return;

        TransformBase targetBase = TargetTransform ?? Transform;
        if (targetBase is not Transform target)
            return;

        float totalSourceWeight = 0.0f;
        Vector3 sourcePosition = Vector3.Zero;
        Vector3 sourceScale = Vector3.Zero;
        Quaternion sourceRotation = Quaternion.Identity;
        bool hasRotation = false;

        foreach (UnityTransformConstraintSource source in Sources)
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
            return;

        sourcePosition /= totalSourceWeight;
        sourceScale /= totalSourceWeight;
        if (SolveInLocalSpace)
            ApplyLocal(target, sourcePosition, sourceRotation, sourceScale);
        else
            ApplyWorld(target, sourcePosition, sourceRotation, sourceScale);
    }

    private void ApplyLocal(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 targetPosition = BlendChannels(target.Translation, position, Weight, PositionMask);
        Vector3 targetScale = BlendChannels(target.Scale, scale, Weight, ScaleMask);
        Quaternion targetRotation = BlendRotation(target.Rotation, rotation);
        target.SetLocalTranslationRotation(targetPosition, targetRotation);
        target.Scale = targetScale;
    }

    private void ApplyWorld(Transform target, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Vector3 targetPosition = BlendChannels(target.WorldTranslation, position, Weight, PositionMask);
        Quaternion targetRotation = BlendRotation(target.WorldRotation, rotation);
        target.SetWorldTranslationRotation(targetPosition, targetRotation);

        // Transform has no direct world-scale setter. Convert the authored world result
        // to a local scale using the parent's lossy scale and preserve unaffected axes.
        Vector3 parentScale = target.Parent?.LossyWorldScale ?? Vector3.One;
        Vector3 desiredLocalScale = new(
            SafeDivide(scale.X, parentScale.X),
            SafeDivide(scale.Y, parentScale.Y),
            SafeDivide(scale.Z, parentScale.Z));
        target.Scale = BlendChannels(target.Scale, desiredLocalScale, Weight, ScaleMask);
    }

    private Quaternion BlendRotation(Quaternion current, Quaternion desired)
    {
        UnityTransformConstraintChannels rotationChannels = Channels & UnityTransformConstraintChannels.Rotation;
        if (rotationChannels == UnityTransformConstraintChannels.None)
            return current;

        desired = EnsureShortestArc(current, desired);
        Quaternion blended = Quaternion.Normalize(Quaternion.Slerp(current, desired, Weight));
        if (rotationChannels == UnityTransformConstraintChannels.Rotation)
            return blended;

        Vector3 currentEuler = ToEulerDegrees(current);
        Vector3 blendedEuler = ToEulerDegrees(blended);
        Vector3 result = new(
            Channels.HasFlag(UnityTransformConstraintChannels.RotationX) ? blendedEuler.X : currentEuler.X,
            Channels.HasFlag(UnityTransformConstraintChannels.RotationY) ? blendedEuler.Y : currentEuler.Y,
            Channels.HasFlag(UnityTransformConstraintChannels.RotationZ) ? blendedEuler.Z : currentEuler.Z);
        return FromEulerDegrees(result);
    }

    private Vector3 PositionMask => new(
        Channels.HasFlag(UnityTransformConstraintChannels.PositionX) ? 1.0f : 0.0f,
        Channels.HasFlag(UnityTransformConstraintChannels.PositionY) ? 1.0f : 0.0f,
        Channels.HasFlag(UnityTransformConstraintChannels.PositionZ) ? 1.0f : 0.0f);

    private Vector3 ScaleMask => new(
        Channels.HasFlag(UnityTransformConstraintChannels.ScaleX) ? 1.0f : 0.0f,
        Channels.HasFlag(UnityTransformConstraintChannels.ScaleY) ? 1.0f : 0.0f,
        Channels.HasFlag(UnityTransformConstraintChannels.ScaleZ) ? 1.0f : 0.0f);

    private static Vector3 BlendChannels(Vector3 current, Vector3 desired, float weight, Vector3 mask)
    {
        Vector3 blended = Vector3.Lerp(current, desired, weight);
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
