using System.Numerics;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Rigid joint transform (position and unit quaternion) matching the right-handed
/// Hamilton composition convention used by NVIDIA MotionBricks SOMA kernels.
/// </summary>
public readonly record struct SomaPoseTransform(Vector3 Position, Quaternion Rotation)
{
    public static SomaPoseTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>
    /// Composes two rigid transforms so that <paramref name="child"/> applies first,
    /// followed by <paramref name="parent"/>.
    /// </summary>
    public static SomaPoseTransform Multiply(in SomaPoseTransform parent, in SomaPoseTransform child)
    {
        Vector3 position = parent.Position + Vector3.Transform(child.Position, parent.Rotation);
        Quaternion rotation = NormalizeOrIdentity(parent.Rotation * child.Rotation);
        return new SomaPoseTransform(position, rotation);
    }

    /// <summary>
    /// Computes the inverse rigid transform so that <c>Multiply(Inverse(t), t)</c> is identity.
    /// </summary>
    public static SomaPoseTransform Inverse(in SomaPoseTransform transform)
    {
        Quaternion invRotation = Quaternion.Inverse(NormalizeOrIdentity(transform.Rotation));
        Vector3 invPosition = Vector3.Transform(-transform.Position, invRotation);
        return new SomaPoseTransform(invPosition, invRotation);
    }

    /// <summary>
    /// Applies the transform to a 3D point.
    /// </summary>
    public Vector3 TransformPoint(Vector3 point)
        => Position + Vector3.Transform(point, Rotation);

    /// <summary>
    /// Linearly interpolates position and spherically interpolates rotation.
    /// </summary>
    public static SomaPoseTransform Lerp(in SomaPoseTransform a, in SomaPoseTransform b, float weight)
    {
        Vector3 position = Vector3.Lerp(a.Position, b.Position, weight);
        Quaternion rotation = Quaternion.Slerp(
            NormalizeOrIdentity(a.Rotation),
            NormalizeOrIdentity(b.Rotation),
            weight);
        return new SomaPoseTransform(position, NormalizeOrIdentity(rotation));
    }

    /// <summary>
    /// Computes the local delta transform that maps <paramref name="source"/> onto <paramref name="destination"/>.
    /// </summary>
    public static SomaPoseTransform ComputeDelta(in SomaPoseTransform source, in SomaPoseTransform destination)
        => Multiply(Inverse(source), destination);

    /// <summary>
    /// Applies a weighted local delta transform in place.
    /// </summary>
    public static SomaPoseTransform ApplyDelta(in SomaPoseTransform transform, in SomaPoseTransform delta, float weight)
    {
        SomaPoseTransform weightedDelta = Lerp(Identity, delta, weight);
        return Multiply(transform, weightedDelta);
    }

    /// <summary>
    /// Computes the world-space root endpoint correction that moves <paramref name="generatedRoot"/>
    /// onto <paramref name="guideRoot"/> without swinging the root along a curved arc.
    /// </summary>
    public static SomaPoseTransform ComputeRootEndpointDelta(in SomaPoseTransform generatedRoot, in SomaPoseTransform guideRoot)
    {
        Quaternion rotationDelta = NormalizeOrIdentity(
            Quaternion.Inverse(NormalizeOrIdentity(generatedRoot.Rotation))
            * NormalizeOrIdentity(guideRoot.Rotation));
        return new SomaPoseTransform(guideRoot.Position - generatedRoot.Position, rotationDelta);
    }

    /// <summary>
    /// Applies both start and end root endpoint corrections across a generated sequence.
    /// <paramref name="endWeight"/> advances from 0 at frame 0 to 1 at the final frame.
    /// </summary>
    public static SomaPoseTransform ApplyRootEndpointCorrection(
        in SomaPoseTransform root,
        in SomaPoseTransform startDelta,
        in SomaPoseTransform endDelta,
        float endWeight)
    {
        float startWeight = 1.0f - endWeight;
        Quaternion startStep = Quaternion.Slerp(Quaternion.Identity, NormalizeOrIdentity(startDelta.Rotation), startWeight);
        Quaternion endStep = Quaternion.Slerp(Quaternion.Identity, NormalizeOrIdentity(endDelta.Rotation), endWeight);
        Quaternion rotation = NormalizeOrIdentity(NormalizeOrIdentity(root.Rotation) * startStep * endStep);
        Vector3 position = root.Position + startDelta.Position * startWeight + endDelta.Position * endWeight;
        return new SomaPoseTransform(position, rotation);
    }

    internal static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
            return Quaternion.Identity;

        return Quaternion.Normalize(value);
    }
}
