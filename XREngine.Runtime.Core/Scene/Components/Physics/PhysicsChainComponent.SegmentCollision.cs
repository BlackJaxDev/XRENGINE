using System.Numerics;

namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    /// <summary>
    /// The smallest lever used to turn a segment. A contact near the parent cannot move the child by an unbounded distance.
    /// </summary>
    private const float MinimumSegmentCollisionLever = 0.2f;

    /// <summary>
    /// Runs one collider against a child particle, or against the full segment from its parent.
    /// </summary>
    private bool CollideSnapshot(in PhysicsChainColliderSnapshot collider, Particle child, Particle parent)
    {
        float childRadius = child.Radius * _objectScale;
        if (CollisionMode == EPhysicsChainCollisionMode.Particle)
            return collider.Collide(ref child._position, childRadius);

        float parameter = collider.ClosestSegmentParameter(parent.Position, child.Position);
        return ApplySegmentContact(in collider, null, child, parent, parameter, childRadius);
    }

    /// <summary>
    /// Runs one collider component against a child particle, or against the full segment from its parent.
    /// </summary>
    private bool CollideComponent(PhysicsChainColliderBase collider, Particle child, Particle parent)
    {
        float childRadius = child.Radius * _objectScale;
        if (CollisionMode == EPhysicsChainCollisionMode.Particle)
            return collider.Collide(ref child._position, childRadius);

        float parameter = collider.ClosestSegmentParameter(parent.Position, child.Position);
        return ApplySegmentContact(default, collider, child, parent, parameter, childRadius);
    }

    private bool ApplySegmentContact(
        in PhysicsChainColliderSnapshot snapshot,
        PhysicsChainColliderBase? component,
        Particle child,
        Particle parent,
        float parameter,
        float childRadius)
    {
        Vector3 contact = Vector3.Lerp(parent.Position, child.Position, parameter);
        Vector3 resolved = contact;
        float radius = float.Lerp(parent.Radius * _objectScale, childRadius, parameter);
        bool hit = component is not null
            ? component.Collide(ref resolved, radius)
            : snapshot.Collide(ref resolved, radius);
        if (!hit)
            return false;

        child.Position += (resolved - contact) / MathF.Max(parameter, MinimumSegmentCollisionLever);
        return true;
    }
}
