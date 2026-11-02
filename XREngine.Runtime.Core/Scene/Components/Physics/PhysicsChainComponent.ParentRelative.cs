using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private const float MinimumSegmentGravity = 0.0015f;

    /// <summary>
    /// Gets if a setting reads the turn of each segment from its rest direction.
    /// </summary>
    private bool TracksSegmentTurn
        => RestFollowsParent || GravityMode == EPhysicsChainGravityMode.SegmentRatio;

    /// <summary>
    /// Gets the rest offset of a child from its parent, without the simulated turn of the parents.
    /// </summary>
    private static Vector3 ComputeRestOffset(Particle child, Particle parent)
    {
        Vector3 local = child.Transform is not null
            ? child.TransformLocalPosition
            : child.EndOffset;
        return Vector3.TransformNormal(local, parent.TransformLocalToWorldMatrix);
    }

    /// <summary>
    /// Gets the rest offset that the keep-shape step aims at.
    /// </summary>
    private Vector3 ComputeShapeRestOffset(Particle child, Particle parent)
    {
        Vector3 offset = ComputeRestOffset(child, parent);
        return RestFollowsParent ? Vector3.Transform(offset, parent.SegmentTurn) : offset;
    }

    /// <summary>
    /// Pulls a child toward its rest position, applies segment gravity, then applies stiffness.
    /// </summary>
    private void ApplyKeepShape(ParticleTree tree, Particle child, Particle parent, float stiffness, float restLength, float timeVar)
    {
        Vector3 restPosition = parent.Position + ComputeShapeRestOffset(child, parent);
        child.Position += (restPosition - child.Position) * (child.Elasticity * timeVar);

        if (GravityMode == EPhysicsChainGravityMode.SegmentRatio)
            child.Position += ComputeSegmentGravity(tree, child) * (child.SegmentLength * timeVar);

        if (stiffness <= 0.0f)
            return;

        if (StiffnessMode == EPhysicsChainStiffnessMode.HoldParentOffset)
        {
            Vector3 heldPosition = child.PrevPosition + (parent.Position - parent.PrevPosition);
            child.Position = Vector3.Lerp(heldPosition, child.Position, 1.0f / (1.0f + stiffness));
        }
        else
            ApplyStiffnessDistanceLimit(child, restPosition, stiffness, restLength);
    }

    private static void ApplyStiffnessDistanceLimit(Particle child, Vector3 restPosition, float stiffness, float restLength)
    {
        Vector3 offset = restPosition - child.Position;
        float length = offset.Length();
        float maximumLength = restLength * (1.0f - stiffness) * 2.0f;
        if (length > maximumLength)
            child.Position += offset * ((length - maximumLength) / length);
    }

    /// <summary>
    /// Gets the gravity of one segment. Falloff removes gravity in proportion to how closely the turned rest gravity
    /// still points along gravity, so it has its full effect at rest and less effect as the segment turns away.
    /// </summary>
    private Vector3 ComputeSegmentGravity(ParticleTree tree, Particle child)
    {
        Vector3 gravity = Gravity;
        float falloff = GravityFalloff;
        float gravityLengthSquared = gravity.LengthSquared();
        float restLengthSquared = tree.RestGravity.LengthSquared();
        float scale = 1.0f;
        if (falloff > 0.0f && gravityLengthSquared > 1e-12f && restLengthSquared > 1e-12f)
        {
            Vector3 direction = gravity / MathF.Sqrt(gravityLengthSquared);
            Vector3 restDirection = Vector3.Transform(tree.RestGravity / MathF.Sqrt(restLengthSquared), child.SegmentTurn);
            scale = 1.0f - falloff * MathF.Max(Vector3.Dot(direction, restDirection), 0.0f);
        }

        // A PhysBone ignores a very small gravity: a value of 0.001 has no effect, and 0.002 has its full effect.
        return MathF.Sqrt(gravityLengthSquared) * scale <= MinimumSegmentGravity
            ? Vector3.Zero
            : gravity * scale;
    }

    /// <summary>
    /// Records how far the segment from parent to child has turned from its rest direction, including the turn of the parents.
    /// </summary>
    private static void UpdateSegmentTurn(Particle child, Particle parent)
    {
        Vector3 rest = Vector3.Transform(ComputeRestOffset(child, parent), parent.SegmentTurn);
        Vector3 simulated = child.Position - parent.Position;
        child.SegmentTurn = rest.LengthSquared() > MinimumSegmentDirectionLengthSquared &&
            simulated.LengthSquared() > MinimumSegmentDirectionLengthSquared
            ? Quaternion.Normalize(XRMath.RotationBetweenVectors(rest, simulated) * parent.SegmentTurn)
            : parent.SegmentTurn;
    }
}
